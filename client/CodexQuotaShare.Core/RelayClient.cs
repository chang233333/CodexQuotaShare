using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace CodexQuotaShare.Core;

public sealed class RelayClientException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Authenticated Relay transport. It owns one WebSocket writer and keeps outbound activity
/// queued while the Relay is unavailable. No credential or message content is logged.
/// </summary>
public sealed class RelayClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri _baseUri;
    private readonly RelayIdentity _identity;
    private readonly string _stateDirectory;
    private readonly string _counterPath;
    private readonly string _snapshotPath;
    private readonly Channel<OutboundMessage> _queue = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false
    });
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _counterGate = new();
    private CancellationTokenSource? _stop;
    private Task? _run;
    private long _authSequence;
    private long _activitySequence;
    private long _quotaSequence;
    private long _activityWindowEnd;
    private RelaySnapshot? _snapshot;
    private RelayConnectionState _connectionState = RelayConnectionState.Offline;
    private bool _disposed;
    private bool _receivedAuthoritativeSnapshot;
    private readonly List<OutboundMessage> _pendingActivity = [];
    private OutboundMessage? _latestQuota;
    private TaskCompletionSource<bool>? _reportAck;
    private OutboundMessage? _inFlight;
    private readonly SemaphoreSlim _wire = new(1, 1);

    public RelayClient(string relayUrl, RelayIdentity identity, string stateDirectory)
    {
        _baseUri = NormalizeBaseUri(relayUrl);
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (string.IsNullOrWhiteSpace(identity.GroupId) || string.IsNullOrWhiteSpace(identity.DeviceId) || string.IsNullOrWhiteSpace(identity.DeviceSecret))
            throw new ArgumentException("A complete Relay identity is required.", nameof(identity));
        _stateDirectory = stateDirectory ?? throw new ArgumentNullException(nameof(stateDirectory));
        if (string.IsNullOrWhiteSpace(_stateDirectory)) throw new ArgumentException("A state directory is required.", nameof(stateDirectory));
        _counterPath = Path.Combine(_stateDirectory, "relay-counters.json");
        _snapshotPath = Path.Combine(_stateDirectory, "relay-snapshot.json");
        LoadLocalState();
        if (_snapshot is not null && !string.Equals(_snapshot.GroupId, identity.GroupId, StringComparison.Ordinal)) _snapshot = null;
    }

    public RelaySnapshot? Snapshot => _snapshot;
    public RelayConnectionState ConnectionState => _connectionState;
    public bool IsOffline => _connectionState is RelayConnectionState.Offline or RelayConnectionState.Stopped;
    public event Action<RelaySnapshot>? SnapshotUpdated;
    public event Action<RelayConnectionState>? ConnectionStateChanged;
    public event Action<string>? Unavailable;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_run is not null) return;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            SetConnectionState(RelayConnectionState.Offline);
            _run = RunAsync(_stop.Token);
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask QueueActivityAsync(ActivitySnapshot activity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (activity.ErrorCode is not null) return;
        if (activity.TokenDelta <= 0) return;
        var windowEnd = activity.ObservedAt.ToUnixTimeMilliseconds();
        long windowStart;
        long sequence;
        lock (_counterGate)
        {
            if (windowEnd <= _activityWindowEnd) return;
            windowStart = Math.Max(windowEnd - 30_000, _activityWindowEnd);
            if (windowStart >= windowEnd) return;
            sequence = ++_activitySequence;
            _activityWindowEnd = windowEnd;
            SaveCounters();
        }
        var report = new RelayActivityReport(
            windowStart, windowEnd,
            Math.Max(0, activity.TokenDelta), Math.Max(0, activity.ActiveSessionCount));
        lock (_counterGate)
        {
            _pendingActivity.RemoveAll(item => item.Activity!.WindowEnd < windowEnd - 300_000);
            _pendingActivity.Add(OutboundMessage.ActivityUpdate(sequence, report));
            if (_pendingActivity.Count > 32) _pendingActivity.RemoveAt(0);
            SaveCounters();
        }
        await _queue.Writer.WriteAsync(OutboundMessage.Wake(), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask RequestSnapshotAsync(CancellationToken cancellationToken = default) =>
        _queue.Writer.WriteAsync(OutboundMessage.RequestSnapshot(), cancellationToken);

    public async ValueTask QueueQuotaObservationAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var observation = new RelayQuotaObservation(snapshot.WeeklyUsedPercent, snapshot.WeeklyResetAt.ToUnixTimeMilliseconds(), snapshot.PlanType, snapshot.ObservedAt.ToUnixTimeMilliseconds());
        long sequence;
        lock (_counterGate)
        {
            sequence = ++_quotaSequence;
            SaveCounters();
        }
        lock (_counterGate) { _latestQuota = OutboundMessage.Quota(sequence, observation); SaveCounters(); }
        await _queue.Writer.WriteAsync(OutboundMessage.Wake(), cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stop is null) return;
            _stop.Cancel();
            var run = _run;
            _run = null;
            if (run is not null)
            {
                try { await run.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            }
            _stop.Dispose(); _stop = null;
            SetConnectionState(RelayConnectionState.Stopped);
        }
        finally { _lifecycle.Release(); }
    }

    public static Task<RelayPairingResult> CreateGroupAsync(string relayUrl, string displayName, CancellationToken cancellationToken = default) =>
        PairAsync(relayUrl, "/v1/groups", new { protocolVersion = RelayProtocol.Version, displayName }, cancellationToken);

    public static Task<RelayPairingResult> JoinGroupAsync(string relayUrl, string joinCode, string displayName, CancellationToken cancellationToken = default) =>
        PairAsync(relayUrl, "/v1/groups/join", new { protocolVersion = RelayProtocol.Version, joinCode, displayName }, cancellationToken);

    public async Task<RelaySnapshot> SetDeviceLimitAsync(string deviceId, decimal? limitPercent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (limitPercent is not null && (limitPercent < 1 || limitPercent > 100 || decimal.Truncate(limitPercent.Value) != limitPercent))
            throw new ArgumentOutOfRangeException(nameof(limitPercent));
        return (await MutateAsync(HttpMethod.Patch, $"devices/{Uri.EscapeDataString(deviceId)}", new { protocolVersion = 1, limitPercent }, cancellationToken)).Snapshot!;
    }

    public string DeviceId => _identity.DeviceId;
    public Task RenameDeviceAsync(string id, string name, CancellationToken ct = default) => MutateAsync(HttpMethod.Patch, $"devices/{Uri.EscapeDataString(id)}", new { protocolVersion = 1, displayName = name }, ct);
    public Task RemoveDeviceAsync(string id, CancellationToken ct = default) => MutateAsync(HttpMethod.Delete, $"devices/{Uri.EscapeDataString(id)}", null, ct);
    public Task TransferOwnerAsync(string id, CancellationToken ct = default) => MutateAsync(HttpMethod.Post, "owner", new { protocolVersion = 1, deviceId = id }, ct);
    public async Task<string> RotateInviteAsync(CancellationToken ct = default) => (await MutateAsync(HttpMethod.Post, "invite/rotate", new { protocolVersion = 1 }, ct)).JoinCode ?? throw new RelayClientException("INVALID_INVITE");

    private void AcceptServerSnapshot(RelaySnapshot snapshot, bool reconnect = false)
    {
        if (snapshot.GroupId != _identity.GroupId || snapshot.Devices is null || snapshot.Version < 1) throw new RelayClientException("INVALID_SNAPSHOT");
        lock (_counterGate)
        {
            if (!reconnect && _receivedAuthoritativeSnapshot && _snapshot is not null && snapshot.Version <= _snapshot.Version) return;
            _snapshot = snapshot;
            var acceptedEnd = snapshot.Devices.FirstOrDefault(d => d.DeviceId == _identity.DeviceId)?.LastActivity?.WindowEnd;
            if (acceptedEnd is { } end) _pendingActivity.RemoveAll(item => item.Activity!.WindowEnd <= end);
            RelaySnapshotCache.Save(_snapshotPath, snapshot);
        }
        SnapshotUpdated?.Invoke(snapshot);
    }

    private async Task<SnapshotResponse> MutateAsync(HttpMethod method, string suffix, object? body, CancellationToken ct)
    {
        await _wire.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = $"/v1/groups/{_identity.GroupId}/{suffix}";
            using var client = new HttpClient { BaseAddress = _baseUri, Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
            AddLocalTestHeader(request, _baseUri);
            AddAuthHeaders(request, CreateAuth(method.Method, path, body));
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new RelayClientException(await ReadErrorCodeAsync(response).ConfigureAwait(false));
            var result = await response.Content.ReadFromJsonAsync<SnapshotResponse>(JsonOptions, ct).ConfigureAwait(false);
            if (result is not { ProtocolVersion: 1, Snapshot: not null }) throw new RelayClientException("INVALID_SNAPSHOT");
            AcceptServerSnapshot(result.Snapshot);
            return result;
        }
        catch (HttpRequestException) { throw new RelayClientException("RELAY_OFFLINE"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new RelayClientException("RELAY_TIMEOUT"); }
        catch (JsonException) { throw new RelayClientException("INVALID_SNAPSHOT"); }
        finally { _wire.Release(); }
    }

    private static async Task<RelayPairingResult> PairAsync(string relayUrl, string path, object body, CancellationToken cancellationToken)
    {
        var baseUri = NormalizeBaseUri(relayUrl);
        using var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
        AddLocalTestHeader(request, baseUri);
        HttpResponseMessage received;
        try { received = await client.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new RelayClientException("RELAY_OFFLINE"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new RelayClientException("RELAY_TIMEOUT"); }
        using var response = received;
        if (!response.IsSuccessStatusCode) throw new RelayClientException(await ReadErrorCodeAsync(response).ConfigureAwait(false));
        try
        {
            var result = await response.Content.ReadFromJsonAsync<PairingResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new RelayClientException("INVALID_PAIRING_RESPONSE");
            if (result.ProtocolVersion != RelayProtocol.Version || string.IsNullOrWhiteSpace(result.GroupId) ||
                string.IsNullOrWhiteSpace(result.DeviceId) || string.IsNullOrWhiteSpace(result.DeviceSecret))
                throw new RelayClientException("INVALID_PAIRING_RESPONSE");
            var snapshot = result.Snapshot ?? throw new RelayClientException("INVALID_PAIRING_RESPONSE");
            return new RelayPairingResult(new RelayIdentity(result.GroupId, result.DeviceId, result.DeviceSecret), snapshot,
                result.JoinCode, result.JoinCodeExpiresAt);
        }
        catch (JsonException) { throw new RelayClientException("INVALID_PAIRING_RESPONSE"); }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var delayIndex = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            SetConnectionState(RelayConnectionState.Connecting);
            try
            {
                await ConnectOnceAsync(cancellationToken).ConfigureAwait(false);
                delayIndex = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (RelayClientException error)
            {
                Unavailable?.Invoke(error.Code);
            }
            catch (Exception) { Unavailable?.Invoke("RELAY_OFFLINE"); }
            SetConnectionState(RelayConnectionState.Offline);
            try { await Task.Delay(RelayProtocol.ReconnectDelays[Math.Min(delayIndex++, RelayProtocol.ReconnectDelays.Length - 1)], cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        var path = $"/v1/groups/{_identity.GroupId}/ws?deviceId={Uri.EscapeDataString(_identity.DeviceId)}";
        var auth = CreateAuth("GET", path, null);
        AddAuthHeaders(socket.Options, auth);
        AddLocalTestHeader(socket.Options, _baseUri);
        var wsUri = new UriBuilder(new Uri(_baseUri, path)) { Scheme = _baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" }.Uri;
        _receivedAuthoritativeSnapshot = false;
        using (var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(wsUri, connectDeadline.Token).ConfigureAwait(false);
        }
        // Server sends a full snapshot on upgrade. Start sending only after it is accepted.
        using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _queue.Writer.TryWrite(OutboundMessage.Wake());
        var sender = SendLoopAsync(socket, path, connectionStop.Token);
        var receiver = ReceiveLoopAsync(socket, connectionStop.Token);
        var heartbeat = HeartbeatLoopAsync(connectionStop.Token);
        try { await Task.WhenAny(sender, receiver, heartbeat).ConfigureAwait(false); }
        finally
        {
            connectionStop.Cancel();
            try { socket.Abort(); } catch { }
            try { await Task.WhenAll(sender, receiver, heartbeat).ConfigureAwait(false); }
            catch (OperationCanceledException) when (connectionStop.IsCancellationRequested) { }
        }
        if (cancellationToken.IsCancellationRequested) return;
        throw new RelayClientException("RELAY_DISCONNECTED");
    }

    private async Task SendLoopAsync(ClientWebSocket socket, string path, CancellationToken cancellationToken)
    {
        await foreach (var signal in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!_receivedAuthoritativeSnapshot)
            {
                if (DateTime.UtcNow > deadline) throw new RelayClientException("SNAPSHOT_TIMEOUT");
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            var control = signal.Kind is OutboundKind.RequestSnapshot or OutboundKind.Heartbeat ? signal : null;
            for (;;)
            {
                OutboundMessage? message;
                lock (_counterGate)
                {
                    var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 300_000;
                    _pendingActivity.RemoveAll(item => item.Activity!.WindowEnd < cutoff);
                    if (_latestQuota?.QuotaObservation?.ObservedAt < cutoff) _latestQuota = null;
                    message = _pendingActivity.FirstOrDefault() ?? _latestQuota ?? control;
                }
                if (message is null) break;
                if (ReferenceEquals(message, control)) control = null;
                await _wire.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var report = message.Kind is OutboundKind.Activity or OutboundKind.QuotaObservation;
                    _inFlight = message;
                    _reportAck = report ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
                    var bytes = Encoding.UTF8.GetBytes(CreateMessage(message, path));
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                    if (report)
                    {
                        await _reportAck!.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                        lock (_counterGate)
                        {
                            _pendingActivity.Remove(message);
                            if (ReferenceEquals(_latestQuota, message)) _latestQuota = null;
                            SaveCounters();
                        }
                    }
                }
                finally { _inFlight = null; _reportAck = null; _wire.Release(); }
                if (message.Kind == OutboundKind.Activity) await Task.Delay(TimeSpan.FromSeconds(5.1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await _queue.Writer.WriteAsync(OutboundMessage.Heartbeat(), cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(RelayProtocol.MaxMessageBytes);
        try
        {
            using var message = new MemoryStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                using var receiveDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                receiveDeadline.CancelAfter(TimeSpan.FromSeconds(90));
                var result = await socket.ReceiveAsync(rented.AsMemory(0, RelayProtocol.MaxMessageBytes), receiveDeadline.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Text) throw new RelayClientException("RELAY_BINARY_MESSAGE");
                message.Write(rented, 0, result.Count);
                if (message.Length > RelayProtocol.MaxMessageBytes) throw new RelayClientException("MESSAGE_TOO_LARGE");
                if (!result.EndOfMessage) continue;
                HandleMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length)));
                message.SetLength(0);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private void HandleMessage(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type == "GROUP_SNAPSHOT")
            {
                var envelope = JsonSerializer.Deserialize<RelaySnapshotEnvelope>(text, JsonOptions)
                    ?? throw new RelayClientException("INVALID_SNAPSHOT");
                if (envelope.ProtocolVersion != RelayProtocol.Version || envelope.Snapshot is null) throw new RelayClientException("INVALID_SNAPSHOT");
                if (!_receivedAuthoritativeSnapshot || _snapshot is null || envelope.Snapshot.Version > _snapshot.Version)
                {
                    AcceptServerSnapshot(envelope.Snapshot, !_receivedAuthoritativeSnapshot);
                    _receivedAuthoritativeSnapshot = true;
                    SetConnectionState(RelayConnectionState.Online);
                }
                return;
            }
            if (type == "ERROR")
            {
                var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
                if (code == "REPLAYED_QUOTA_SEQUENCE" && _inFlight?.Kind == OutboundKind.QuotaObservation)
                { _reportAck?.TrySetResult(true); return; }
                throw new RelayClientException(string.IsNullOrWhiteSpace(code) ? "RELAY_ERROR" : code);
            }
            if (type == "REPORT_ACK")
            {
                var expected = _inFlight;
                if (expected is not null && root.TryGetProperty("sequence", out var seq) &&
                    seq.GetInt64() == (expected.Kind == OutboundKind.Activity ? expected.ActivitySequence : expected.QuotaSequence) &&
                    root.GetProperty("kind").GetString() == (expected.Kind == OutboundKind.Activity ? "ACTIVITY_UPDATE" : "QUOTA_OBSERVATION"))
                    _reportAck?.TrySetResult(true);
                return;
            }
            if (type is "HEARTBEAT_ACK") return;
            throw new RelayClientException("UNKNOWN_RELAY_MESSAGE");
        }
        catch (JsonException) { throw new RelayClientException("INVALID_RELAY_MESSAGE"); }
    }

    private string CreateMessage(OutboundMessage message, string path)
    {
        object payload = message.Kind switch
        {
            OutboundKind.RequestSnapshot => new { protocolVersion = RelayProtocol.Version, type = "REQUEST_SNAPSHOT" },
            OutboundKind.Heartbeat => new { protocolVersion = RelayProtocol.Version, type = "HEARTBEAT" },
            OutboundKind.Activity => new { protocolVersion = RelayProtocol.Version, type = "ACTIVITY_UPDATE", sequence = message.ActivitySequence,
                activity = message.Activity },
            OutboundKind.QuotaObservation => new { protocolVersion = RelayProtocol.Version, type = "QUOTA_OBSERVATION", sequence = message.QuotaSequence,
                observation = message.QuotaObservation },
            _ => throw new InvalidOperationException("Unknown outbound message.")
        };
        var payloadNode = JsonNode.Parse(JsonSerializer.Serialize(payload, JsonOptions))!.AsObject();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nonce = RelayAuth.CreateNonce();
        long sequence;
        lock (_counterGate) { sequence = ++_authSequence; SaveCounters(); }
        var unsigned = new AuthEnvelope(_identity.DeviceId, timestamp, nonce, sequence, string.Empty);
        var canonical = RelayAuth.CanonicalJson(payloadNode);
        var signed = unsigned with { Signature = RelayAuth.Sign(_identity.DeviceSecret, "WS", path, timestamp, nonce, sequence, canonical) };
        payloadNode["auth"] = JsonSerializer.SerializeToNode(signed, JsonOptions);
        return payloadNode.ToJsonString(JsonOptions);
    }

    private AuthEnvelope CreateAuth(string method, string path, object? payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nonce = RelayAuth.CreateNonce();
        long sequence;
        lock (_counterGate) { sequence = ++_authSequence; SaveCounters(); }
        var unsigned = new AuthEnvelope(_identity.DeviceId, timestamp, nonce, sequence, string.Empty);
        var canonical = RelayAuth.CanonicalJson(payload);
        return unsigned with { Signature = RelayAuth.Sign(_identity.DeviceSecret, method, path, timestamp, nonce, sequence, canonical) };
    }

    private void LoadLocalState()
    {
        try
        {
            _snapshot = RelaySnapshotCache.Load(_snapshotPath);
            if (!File.Exists(_counterPath)) return;
            var state = JsonSerializer.Deserialize<CounterState>(File.ReadAllText(_counterPath), JsonOptions)
                ?? throw new RelayClientException("RELAY_STATE_INVALID");
            if (state.AuthSequence < 0 || state.ActivitySequence < 0 || state.QuotaSequence < 0 || state.ActivityWindowEnd < 0) throw new RelayClientException("RELAY_STATE_INVALID");
            _authSequence = state.AuthSequence; _activitySequence = state.ActivitySequence; _quotaSequence = state.QuotaSequence;
            _activityWindowEnd = state.ActivityWindowEnd;
            _pendingActivity.AddRange((state.PendingActivity ?? []).Where(item => item.Activity is not null));
            _latestQuota = state.LatestQuota;
            if (_activityWindowEnd == 0 && _snapshot is not null)
            {
                _activityWindowEnd = _snapshot.Devices.FirstOrDefault(d => d.DeviceId == _identity.DeviceId)?.LastActivity?.WindowEnd ?? 0;
            }
        }
        catch (RelayClientException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { throw new RelayClientException("RELAY_STATE_INVALID"); }
    }

    private void SaveCounters()
    {
        Directory.CreateDirectory(_stateDirectory);
        var temporary = _counterPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new CounterState(_authSequence, _activitySequence, _quotaSequence, _activityWindowEnd, _pendingActivity.ToArray(), _latestQuota), JsonOptions));
        File.Move(temporary, _counterPath, true);
    }

    private void SetConnectionState(RelayConnectionState state)
    {
        if (_connectionState == state) return;
        _connectionState = state; ConnectionStateChanged?.Invoke(state);
    }

    private static Uri NormalizeBaseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || (uri.Scheme == Uri.UriSchemeHttp && !IsLocal(uri)))
            throw new ArgumentException("Relay URL must be an absolute HTTP(S) URL without query or fragment.", nameof(value));
        return new Uri(uri.ToString().TrimEnd('/') + "/");
    }

    private static void AddLocalTestHeader(HttpRequestMessage request, Uri uri)
    { if (IsLocal(uri)) request.Headers.Add("X-CQS-Local-Test", "1"); }
    private static void AddLocalTestHeader(ClientWebSocketOptions options, Uri uri)
    { if (IsLocal(uri)) options.SetRequestHeader("X-CQS-Local-Test", "1"); }
    private static bool IsLocal(Uri uri) => uri.Host is "localhost" or "127.0.0.1" or "[::1]";
    private static void AddAuthHeaders(ClientWebSocketOptions options, AuthEnvelope auth)
    {
        options.SetRequestHeader("X-CQS-Device-Id", auth.DeviceId); options.SetRequestHeader("X-CQS-Timestamp", auth.Timestamp.ToString(CultureInfo.InvariantCulture));
        options.SetRequestHeader("X-CQS-Nonce", auth.Nonce); options.SetRequestHeader("X-CQS-Sequence", auth.Sequence.ToString(CultureInfo.InvariantCulture));
        options.SetRequestHeader("X-CQS-Signature", auth.Signature);
    }
    private static void AddAuthHeaders(HttpRequestMessage request, AuthEnvelope auth)
    {
        request.Headers.Add("X-CQS-Device-Id", auth.DeviceId); request.Headers.Add("X-CQS-Timestamp", auth.Timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-CQS-Nonce", auth.Nonce); request.Headers.Add("X-CQS-Sequence", auth.Sequence.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-CQS-Signature", auth.Signature);
    }

    private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(JsonOptions).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(error?.Code) ? "RELAY_HTTP_" + (int)response.StatusCode : error.Code;
        }
        catch { return "RELAY_HTTP_" + (int)response.StatusCode; }
    }

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(RelayClient)); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private sealed record CounterState(
        [property: JsonPropertyName("authSequence")] long AuthSequence,
        [property: JsonPropertyName("activitySequence")] long ActivitySequence,
        [property: JsonPropertyName("quotaSequence")] long QuotaSequence,
        [property: JsonPropertyName("activityWindowEnd")] long ActivityWindowEnd = 0,
        OutboundMessage[]? PendingActivity = null, OutboundMessage? LatestQuota = null);
    private sealed record ErrorEnvelope([property: JsonPropertyName("code")] string? Code);
    private sealed record SnapshotResponse([property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
        [property: JsonPropertyName("snapshot")] RelaySnapshot? Snapshot,
        [property: JsonPropertyName("joinCode")] string? JoinCode = null);
    private sealed record PairingResponse(
        [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
        [property: JsonPropertyName("groupId")] string? GroupId,
        [property: JsonPropertyName("deviceId")] string? DeviceId,
        [property: JsonPropertyName("deviceSecret")] string? DeviceSecret,
        [property: JsonPropertyName("joinCode")] string? JoinCode,
        [property: JsonPropertyName("joinCodeExpiresAt")] long? JoinCodeExpiresAt,
        [property: JsonPropertyName("snapshot")] RelaySnapshot? Snapshot);
    private sealed record AuthEnvelope(
        [property: JsonPropertyName("deviceId")] string DeviceId,
        [property: JsonPropertyName("timestamp")] long Timestamp,
        [property: JsonPropertyName("nonce")] string Nonce,
        [property: JsonPropertyName("sequence")] long Sequence,
        [property: JsonPropertyName("signature")] string Signature);
    private enum OutboundKind { RequestSnapshot, Heartbeat, Activity, QuotaObservation, Wake }
    private sealed record RelayQuotaObservation(
        [property: JsonPropertyName("weeklyUsedPercent")] decimal WeeklyUsedPercent,
        [property: JsonPropertyName("weeklyResetAt")] long WeeklyResetAt,
        [property: JsonPropertyName("planType")] string? PlanType,
        [property: JsonPropertyName("observedAt")] long ObservedAt);
    private sealed record OutboundMessage(OutboundKind Kind, long ActivitySequence, RelayActivityReport? Activity,
        long QuotaSequence = 0, RelayQuotaObservation? QuotaObservation = null)
    {
        public static OutboundMessage Wake() => new(OutboundKind.Wake, 0, null);
        public static OutboundMessage RequestSnapshot() => new(OutboundKind.RequestSnapshot, 0, null);
        public static OutboundMessage Heartbeat() => new(OutboundKind.Heartbeat, 0, null);
        public static OutboundMessage ActivityUpdate(long sequence, RelayActivityReport activity) => new(OutboundKind.Activity, sequence, activity);
        public static OutboundMessage Quota(long sequence, RelayQuotaObservation observation) => new(OutboundKind.QuotaObservation, 0, null, sequence, observation);
    }
}
