using System.Diagnostics;
using CodexQuotaShare.Core;
namespace CodexQuotaShare.Windows;

internal sealed class SoftEnforcementMonitor(Func<bool> limited, Func<int> observerId, Func<string?> executable,
    Action<string> report) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private DateTimeOffset _pathsChecked;
    private HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    public void Start() => _loop ??= RunAsync();
    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (!limited()) continue;
                if (_known.Count == 0 || DateTimeOffset.UtcNow - _pathsChecked > TimeSpan.FromMinutes(1))
                {
                string cli;
                try { cli = CodexCommandResolver.Resolve(executable()).FileName; }
                catch (QuotaUnavailableException) { continue; }
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { cli };
                // Installed desktop package paths are discovered through Windows' package API.
                try
                {
                    foreach (var package in new global::Windows.Management.Deployment.PackageManager().FindPackagesForUser(""))
                        if (package.Id.Name.Equals("OpenAI.Codex", StringComparison.OrdinalIgnoreCase))
                            known.Add(Path.Combine(package.InstalledLocation.Path, "app", "Codex.exe"));
                }
                catch (Exception e) when (e is UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
                _known = known; _pathsChecked = DateTimeOffset.UtcNow;
                }
                using var self = Process.GetCurrentProcess();
                foreach (var process in Process.GetProcessesByName("codex"))
                {
                    using (process)
                    try
                    {
                        if (process.SessionId != self.SessionId) continue;
                        if (!SoftEnforcementPolicy.ShouldStop(true, limited(), process.Id, observerId(), process.MainModule?.FileName, _known)) continue;
                        if (!process.CloseMainWindow()) process.Kill();
                        else
                        {
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                            deadline.CancelAfter(1500);
                            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { if (!process.HasExited && limited()) process.Kill(); }
                        }
                        report("CODEX_LIMIT_ENFORCED");
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or NotSupportedException) { report("ENFORCEMENT_UNAVAILABLE"); }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); if (_loop is not null) await _loop.ConfigureAwait(false); _stop.Dispose(); }
}
