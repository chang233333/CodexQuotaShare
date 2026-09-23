using CodexQuotaShare.Core;

namespace CodexQuotaShare.Windows;

internal sealed class QuotaMonitor(Func<string?> executable) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private CodexAppServerClient? _client;
    private Task? _loop;
    public int ObserverProcessId => _client?.ProcessId ?? -1;
    public event Action<QuotaSnapshot>? Updated;
    public event Action<string>? Unavailable;
    public void Start() => _loop ??= RunAsync();
    private async Task RunAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false)) await RefreshAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async Task RefreshAsync(bool reconnect = false)
    {
        await _refresh.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            if (reconnect || _client is { IsConnected: false })
            {
                if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
            if (_client is null)
            {
                _client = await CodexAppServerClient.StartAsync(CodexCommandResolver.Resolve(executable()), _stop.Token).ConfigureAwait(false);
                _client.QuotaUpdated += q => Updated?.Invoke(q);
                _client.Unavailable += code => { if (!_stop.IsCancellationRequested) Unavailable?.Invoke(code); };
            }
            Updated?.Invoke(await _client.ReadQuotaAsync(_stop.Token).ConfigureAwait(false));
        }
        catch (QuotaUnavailableException ex) { Unavailable?.Invoke(ex.Code); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Unavailable?.Invoke("CODEX_UNAVAILABLE"); }
        finally { _refresh.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        await _refresh.WaitAsync().ConfigureAwait(false);
        try { if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false); }
        finally { _refresh.Release(); }
    }
}
