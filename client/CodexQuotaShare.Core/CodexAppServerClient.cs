using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexQuotaShare.Core;

/// <summary>One owned stdio connection. Reconnect by creating a fresh instance.</summary>
public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly TimeSpan _requestTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<RpcReply>> _pending = new();
    private readonly Task _readLoop;
    private readonly Task _errorLoop;
    private sealed record RpcReply(JsonElement Data, DateTimeOffset ReceivedAt);
    private long _lastObservedTicks;
    private long _nextId;
    private int _disposed;
    private string? _failure;
    public event Action<QuotaSnapshot>? QuotaUpdated;
    public event Action<string>? Unavailable;
    public int ProcessId => _process.Id;
    public bool IsConnected => Volatile.Read(ref _failure) is null && Volatile.Read(ref _disposed) == 0;

    private CodexAppServerClient(Process process, TimeSpan timeout)
    {
        _process = process;
        _requestTimeout = timeout;
        _readLoop = ReadLoopAsync();
        _errorLoop = DrainStderrAsync();
    }

    public static async Task<CodexAppServerClient> StartAsync(ProcessStartInfo command,
        CancellationToken cancellationToken = default, TimeSpan? requestTimeout = null)
    {
        command.UseShellExecute = false;
        command.CreateNoWindow = true;
        command.RedirectStandardInput = true;
        command.RedirectStandardOutput = true;
        command.RedirectStandardError = true;
        command.StandardInputEncoding = new UTF8Encoding(false);
        command.StandardOutputEncoding = Encoding.UTF8;
        Process process;
        try { process = Process.Start(command) ?? throw new InvalidOperationException(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { throw new QuotaUnavailableException("CODEX_NOT_FOUND"); }
        var client = new CodexAppServerClient(process, requestTimeout ?? TimeSpan.FromSeconds(15));
        try
        {
            await client.RequestAsync("initialize", new
            {
                clientInfo = new { name = "codex-quota-share", title = "CodexQuotaShare", version = "0.1.0" },
                capabilities = new { experimentalApi = false }
            }, cancellationToken).ConfigureAwait(false);
            await client.WriteAsync(JsonSerializer.Serialize(new { method = "initialized" }), cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<QuotaSnapshot> ReadQuotaAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("account/rateLimits/read", null, cancellationToken).ConfigureAwait(false);
        return QuotaParser.Parse(result.Data, result.ReceivedAt);
    }

    private async Task<RpcReply> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<RpcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            ThrowIfUnavailable();
            await WriteAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }), deadline.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var code = Volatile.Read(ref _failure) ?? "CODEX_TIMEOUT";
            Fail(code); // A timed-out connection is not reused; late replies cannot leak into a new connection.
            throw new QuotaUnavailableException(code);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task WriteAsync(string json, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        { Fail("CODEX_DISCONNECTED"); throw new QuotaUnavailableException("CODEX_DISCONNECTED"); }
        finally { _writer.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
                if (line is null) { Fail("CODEX_EXITED"); return; }
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > 1_048_576) { Fail("CODEX_PROTOCOL_ERROR"); return; }
                using var doc = JsonDocument.Parse(line);
                var message = doc.RootElement;
                if (message.ValueKind != JsonValueKind.Object) { Fail("CODEX_PROTOCOL_ERROR"); return; }
                if (message.TryGetProperty("id", out var idValue))
                {
                    // Ignore unrecognized IDs (including unsolicited server requests).
                    if (idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetInt64(out var id)
                        || !_pending.TryRemove(id, out var completion)) continue;
                    if (message.TryGetProperty("error", out _))
                        completion.TrySetException(new QuotaUnavailableException("CODEX_REQUEST_REJECTED"));
                    else if (message.TryGetProperty("result", out var result)) completion.TrySetResult(new RpcReply(result.Clone(), NextObservedAt()));
                    else completion.TrySetException(new QuotaUnavailableException("CODEX_PROTOCOL_ERROR"));
                    continue;
                }
                if (message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String
                    && method.GetString() == "account/rateLimits/updated" && message.TryGetProperty("params", out var data))
                {
                    try
                    {
                        var snapshot = QuotaParser.Parse(data, NextObservedAt());
                        QuotaUpdated?.Invoke(snapshot);
                    }
                    catch (QuotaUnavailableException ex) { Unavailable?.Invoke(ex.Code); }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or JsonException)
        { Fail("CODEX_PROTOCOL_ERROR"); }
    }

    // Called exclusively by the stdout reader. Preserve wire order even within one clock tick.
    private DateTimeOffset NextObservedAt()
    {
        _lastObservedTicks = Math.Max(DateTimeOffset.UtcNow.Ticks, _lastObservedTicks + 1);
        return new DateTimeOffset(_lastObservedTicks, TimeSpan.Zero);
    }

    private async Task DrainStderrAsync()
    {
        // Drain to prevent deadlock; never retain or log raw stderr, which may contain credentials.
        var buffer = new char[2048];
        try { while (await _process.StandardError.ReadAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false) != 0) { } }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new QuotaUnavailableException("CODEX_DISPOSED");
        if (Volatile.Read(ref _failure) is { } code) throw new QuotaUnavailableException(code);
    }

    private void Fail(string code)
    {
        if (Interlocked.CompareExchange(ref _failure, code, null) is not null) return;
        foreach (var item in _pending) item.Value.TrySetException(new QuotaUnavailableException(code));
        Unavailable?.Invoke(code);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Fail("CODEX_DISPOSED");
        _lifetime.Cancel();
        try { _process.StandardInput.Close(); } catch (IOException) { }
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        await Task.WhenAll(_readLoop, _errorLoop).ConfigureAwait(false);
        _process.Dispose();
        // A concurrent request may still be releasing the writer / observing cancellation.
        // Leave these small managed synchronization objects for GC rather than racing Dispose.
    }
}
