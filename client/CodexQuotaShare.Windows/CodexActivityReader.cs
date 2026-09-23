using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexQuotaShare.Core;

namespace CodexQuotaShare.Windows;

/// <summary>
/// Reads append-only Codex session JSONL. Only complete-line offsets and numeric
/// activity state are persisted; incomplete source lines are reread next time.
/// Activity is an attribution signal, not the official account quota.
/// </summary>
internal sealed class CodexActivityReader : IAsyncDisposable
{
    private sealed class FileCursor
    {
        public long Offset { get; set; }
        public int PrefixLength { get; set; }
        public string? PrefixHash { get; set; }
        public bool Initialized { get; set; }
    }

    private sealed class PersistedState
    {
        public int Version { get; set; }
        public DateTimeOffset? TrackingSince { get; set; }
        public ActivityAccumulatorState? Accumulator { get; set; }
        public Dictionary<string, FileCursor> Cursors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly string _sessionsDirectory;
    private readonly string _statePath;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _refreshInterval;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly ActivityAccumulator _accumulator = new();
    private readonly Dictionary<string, FileCursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _trackingSince;
    private Task? _loop;
    private int _disposed;

    public CodexActivityReader(string? sessionsDirectory = null, string? statePath = null,
        TimeProvider? timeProvider = null, TimeSpan? refreshInterval = null)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _sessionsDirectory = sessionsDirectory ?? Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(profile, ".codex"), "sessions");
        _statePath = Path.GetFullPath(statePath ?? Path.Combine(AppContext.BaseDirectory, "data", "activity-state.json"));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(20);
        _trackingSince = _timeProvider.GetUtcNow();
        _stopToken = _stop.Token;
        LoadState();
    }

    public event Action<ActivitySnapshot>? Updated;
    public event Action<string>? Unavailable;

    public void Start()
    {
        if (Volatile.Read(ref _disposed) == 0) _loop ??= RunAsync();
    }

    public async Task RefreshAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var acquired = false;
        try
        {
            await _refresh.WaitAsync(_stopToken).ConfigureAwait(false);
            acquired = true;
            var now = _timeProvider.GetUtcNow();
            if (!Directory.Exists(_sessionsDirectory))
            {
                Publish(_accumulator.Snapshot(now) with { ErrorCode = "ACTIVITY_DIRECTORY_NOT_FOUND" });
                return;
            }

            var paths = Directory.EnumerateFiles(_sessionsDirectory, "*.jsonl", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            long delta = 0;
            var baseline = false;
            foreach (var path in paths)
            {
                var result = await ReadFileAsync(path, now, _stopToken).ConfigureAwait(false);
                delta = checked(delta + result.TokenDelta);
                baseline |= result.Baseline;
            }

            await SaveStateAsync(_stopToken).ConfigureAwait(false);
            Publish(_accumulator.Snapshot(now, delta, baseline));
        }
        catch (OperationCanceledException) when (_stopToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stopToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Publish(_accumulator.Snapshot(_timeProvider.GetUtcNow()) with { ErrorCode = "ACTIVITY_UNAVAILABLE" });
        }
        finally
        {
            if (acquired) _refresh.Release();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(false);
            using var timer = new PeriodicTimer(_refreshInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(_stopToken).ConfigureAwait(false))
                await RefreshAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopToken.IsCancellationRequested) { }
    }

    private async Task<(long TokenDelta, bool Baseline)> ReadFileAsync(string path, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var key = Path.GetFullPath(path);
        var cursor = _cursors.TryGetValue(key, out var existing) ? existing : new FileCursor();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        // Use the same handle for identity checks and reading, and bound each poll to
        // the length observed on open even if the writer continues appending.
        var end = stream.Length;
        var reset = cursor.Initialized && (end < cursor.Offset
            || (cursor.PrefixLength > 0 && !string.Equals(cursor.PrefixHash,
                await ReadPrefixHashAsync(stream, cursor.PrefixLength, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal)));
        if (reset) cursor = new FileCursor();

        var lineBuffer = new Utf8LineBuffer();
        var records = new List<ActivityRecord>();
        stream.Position = cursor.Offset;
        var completeOffset = cursor.Offset;
        var buffer = new byte[64 * 1024];
        while (stream.Position < end)
        {
            var start = stream.Position;
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - start)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            foreach (var line in lineBuffer.Append(buffer.AsSpan(0, read)))
            {
                if (CodexActivityParser.TryParse(line, key, now, out var record, requireTimestamp: true)) records.Add(record);
            }
            var newline = buffer.AsSpan(0, read).LastIndexOf((byte)'\n');
            if (newline >= 0) completeOffset = start + newline + 1;
        }

        var prefixLength = (int)Math.Min(4096, completeOffset);
        var prefixHash = await ReadPrefixHashAsync(stream, prefixLength, cancellationToken).ConfigureAwait(false);
        if (reset) _accumulator.ForgetSession(key);
        // Persist the monitoring cutoff so sessions started while the app was
        // closed are counted on restart; imported older history only seeds totals.
        // Apply historical records first, including a historical partial line that
        // only acquired its newline on this poll.
        var history = records.Where(record => reset || record.Timestamp < _trackingSince).ToArray();
        _accumulator.Apply(history, now, establishBaseline: true);
        var delta = reset ? 0 : _accumulator.Apply(records.Where(record => record.Timestamp >= _trackingSince),
            now, establishBaseline: false).TokenDelta;
        var baseline = reset || history.Length > 0;
        _cursors[key] = new FileCursor
        {
            Offset = completeOffset, PrefixLength = prefixLength, PrefixHash = prefixHash, Initialized = true
        };
        return (delta, baseline);
    }

    private static async Task<string> ReadPrefixHashAsync(FileStream stream, int length, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(SHA256.HashData(buffer));
    }

    private string SerializeState() => JsonSerializer.Serialize(new PersistedState
    {
        Version = 2, TrackingSince = _trackingSince, Accumulator = _accumulator.ExportState(), Cursors = _cursors
    });

    private async Task SaveStateAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporary = _statePath + ".tmp";
        await File.WriteAllTextAsync(temporary, SerializeState(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _statePath, true);
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_statePath));
            if (state?.Accumulator is not null) _accumulator.Restore(state.Accumulator);
            if (state?.Version == 2)
            {
                if (state.TrackingSince is { } since && since <= _trackingSince) _trackingSince = since;
                foreach (var pair in state.Cursors ?? [])
                {
                    var cursor = pair.Value;
                    if (!string.IsNullOrWhiteSpace(pair.Key) && cursor is not null && cursor.Offset >= 0
                        && cursor.PrefixLength >= 0 && cursor.PrefixLength <= Math.Min(4096, cursor.Offset))
                        _cursors[pair.Key] = cursor;
                }
            }
            else
            {
                // Version 1 stored raw partial lines in PendingBase64. Drop its unsafe
                // cursors, preserve numeric totals, and rebuild historical baselines.
                // Rewrite immediately, including a stale .tmp, even if sessions are missing.
                var temporary = _statePath + ".tmp";
                File.WriteAllText(temporary, SerializeState(), Encoding.UTF8);
                File.Move(temporary, _statePath, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _cursors.Clear();
        }
    }

    private void Publish(ActivitySnapshot snapshot)
    {
        if (_stopToken.IsCancellationRequested) return;
        Updated?.Invoke(snapshot);
        if (snapshot.ErrorCode is { } code) Unavailable?.Invoke(code);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try
        {
            if (_loop is not null) await _loop.ConfigureAwait(false);
        }
        finally
        {
            await _refresh.WaitAsync().ConfigureAwait(false);
            _refresh.Release();
            _refresh.Dispose();
            _stop.Dispose();
        }
    }
}
