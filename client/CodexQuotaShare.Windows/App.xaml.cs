// Explicit tray lifecycle informed by QuotaScope, Copyright (c) 2026 HexX, MIT. See THIRD_PARTY_NOTICES.md.
using System.Diagnostics;
using System.Text.Json;
using CodexQuotaShare.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace CodexQuotaShare.Windows;

public partial class App : Application
{
    private readonly EventWaitHandle _toggle;
    private readonly bool _demo;
    private readonly string? _smokePath;
    private readonly QuotaDisplayState _state = new();
    private readonly RelayDashboardState _dashboard = new();
    private ActivitySnapshot? _activity;
    private LocalSettings _settings;
    private TrayIconHost? _tray;
    private QuotaWindow? _window;
    private QuotaMonitor? _monitor;
    private CodexActivityReader? _activityReader;
    private RelayClient? _relayClient;
    private RelayLaunchGate? _launchGate;
    private RelaySnapshot? _relaySnapshot;
    private RelayConnectionState _relayState = RelayConnectionState.Stopped;
    private RegisteredWaitHandle? _toggleRegistration;
    private DispatcherQueue? _dispatcher;
    private bool _exiting;
    private bool _pauseNotifications;
    private bool _toastRegistered;
    private SoftEnforcementMonitor? _enforcement;

    internal App(EventWaitHandle toggle, bool demo, string? smokePath)
    {
        _toggle = toggle; _demo = demo; _smokePath = smokePath;
        // Smoke/demo mode never reads settings that could select a real executable.
        _settings = demo ? new() : SettingsStore.Load();
        UnhandledException += (_, e) =>
        {
            SettingsStore.Log(_settings.DebugLogging, "UI_FAILURE");
            if (_smokePath is not null)
                File.WriteAllText(_smokePath + ".error.json", JsonSerializer.Serialize(new { type = e.Exception.GetType().Name, hresult = e.Exception.HResult, message = e.Exception.Message, stack = e.Exception.StackTrace }));
            e.Handled = true;
            _ = ExitAsync();
        };
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Trace("OnLaunched");
        try
        {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Trace("Creating tray");
        _tray = new TrayIconHost();
        Trace("Tray created");
        _tray.SetIcon((System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone());
        _tray.SetTooltip("CodexQuotaShare — 正在读取额度");
        _tray.LeftClicked += ShowWindow;
        _tray.SetMenu([
            new("打开", ShowWindow),
            new("立即刷新", () => _ = RefreshAsync()),
            new("暂停 / 恢复通知", () => _pauseNotifications = !_pauseNotifications),
            new("设置", ShowWindow),
            new("管理设备", ShowWindow),
            TrayMenuItem.Separator,
            new("退出", () => _ = ExitAsync())
        ]);
        _tray.Show();
        _toggleRegistration = ThreadPool.RegisterWaitForSingleObject(_toggle, (_, _) => _dispatcher.TryEnqueue(ShowWindow), null, Timeout.Infinite, false);
        if (_demo) AcceptDemo();
        else
        {
            if (SettingsStore.LoadQuota() is { } cachedQuota) { _state.Accept(cachedQuota); _state.Fail("CACHED_QUOTA"); }
            _monitor = new QuotaMonitor(() => _settings.CodexExecutable);
            _monitor.Updated += q => _dispatcher.TryEnqueue(() => Accept(q));
            _monitor.Unavailable += code => _dispatcher.TryEnqueue(() => Fail(code));
            _monitor.Start();
            try { AppNotificationManager.Default.Register(); _toastRegistered = true; }
            catch (Exception) { SettingsStore.Log(_settings.DebugLogging, "TOAST_UNAVAILABLE"); }
            _enforcement = new SoftEnforcementMonitor(() => _settings.SoftEnforcement && _launchGate?.State.IsLimited == true,
                () => _monitor?.ObserverProcessId ?? -1, () => _settings.CodexExecutable,
                code => SettingsStore.Log(_settings.DebugLogging, code));
            _enforcement.Start();

            _activityReader = new CodexActivityReader();
            _activityReader.Updated += activity => _dispatcher.TryEnqueue(() => AcceptActivity(activity));
            _activityReader.Start();
            _ = StartSavedRelayAsync();
        }
        Trace("Tray ready");
        if (_smokePath is not null) _ = SmokeTestAsync();
        }
        catch (Exception error)
        {
            if (_smokePath is not null) File.WriteAllText(_smokePath + ".error.json", JsonSerializer.Serialize(new { type = error.GetType().Name, message = error.Message, stack = error.StackTrace }));
            _ = ExitAsync();
        }
    }

    private void Trace(string stage) { if (_smokePath is not null) File.AppendAllText(_smokePath + ".trace.txt", stage + Environment.NewLine); }

    private void AcceptDemo()
    {
        var now = DateTimeOffset.UtcNow;
        Accept(new QuotaSnapshot("Plus", 42.5m, now.AddDays(3), now));
        AcceptActivity(new ActivitySnapshot(now, 125_000, 80_000, 5_000, 1, now, false));
    }
    private void Accept(QuotaSnapshot q)
    {
        if (_exiting) return;
        _state.Accept(q);
        _dashboard.AcceptOfficialAccountUsage(q);
        _tray?.SetTooltip($"CodexQuotaShare — 周已用 {q.WeeklyUsedPercent:0.##}%" + (_demo ? "（演示）" : ""));
        _window?.Render(_state, _activity, _demo);
        SettingsStore.Log(_settings.DebugLogging, "QUOTA_UPDATED");
        if (!_demo) { SettingsStore.SaveQuota(q); _ = QueueRelayQuotaAsync(q); }
    }

    private void AcceptActivity(ActivitySnapshot activity)
    {
        if (_exiting) return;
        _activity = activity;
        _window?.Render(_state, _activity, _demo);
        SettingsStore.Log(_settings.DebugLogging, activity.ErrorCode ?? "ACTIVITY_UPDATED");
        if (!_demo) _ = QueueRelayActivityAsync(activity);
    }
    private void Fail(string code)
    {
        if (_exiting) return;
        _state.Fail(code);
        _tray?.SetTooltip("CodexQuotaShare — 额度暂不可用");
        _window?.Render(_state, _activity, _demo);
        SettingsStore.Log(_settings.DebugLogging, code);
    }
    private void ShowWindow()
    {
        if (_exiting) return;
        Trace("Creating window");
        _window ??= new QuotaWindow(_settings, _demo, () => RefreshAsync(), SaveAsync, CreateGroupAsync, JoinGroupAsync, SetDeviceLimitAsync, LaunchCodexAsync, ManageAsync);
        Trace("Window constructed");
        _window.Render(_state, _activity, _demo);
        _window.RenderRelay(_relayState, _relaySnapshot, _launchGate?.State, _relayClient?.DeviceId);
        Trace("Window rendered");
        _window.Activate(); _window.AppWindow.Show();
        Trace("Window shown");
    }
    private async Task RefreshAsync(bool reconnect = false)
    {
        if (_exiting) return;
        if (_demo) { AcceptDemo(); return; }
        if (_monitor is null) return;
        try { await _monitor.RefreshAsync(reconnect); }
        catch (OperationCanceledException) when (_exiting) { }
    }
    private async Task<string?> SaveAsync(LocalSettings settings)
    {
        if (_demo) return "演示模式不修改本机设置。";
        if (_relayClient is not null && !string.Equals(_settings.RelayUrl?.TrimEnd('/'), settings.RelayUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return "请先离开当前群组，再更换同步服务。";
        if (string.IsNullOrWhiteSpace(settings.DeviceName) || settings.DeviceName.Length > 64) return "设备名称应为 1–64 个字符。";
        try { if (settings.LaunchAtStartup != _settings.LaunchAtStartup) ProductConfiguration.SetStartup(settings.LaunchAtStartup); SettingsStore.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return "无法保存设置，请将程序解压到可写目录。"; }
        var relayChanged = !string.Equals(_settings.RelayUrl, settings.RelayUrl, StringComparison.OrdinalIgnoreCase);
        _settings = settings;
        if (relayChanged && RelayIdentityStore.Load(IdentityDirectory) is { } identity) await StartRelayAsync(identity);
        await RefreshAsync(true);
        return null;
    }

    private string IdentityDirectory => Path.Combine(AppContext.BaseDirectory, "data", "identity");
    private string RelayStateDirectory => Path.Combine(AppContext.BaseDirectory, "data", "relay");

    private async Task StartSavedRelayAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.RelayUrl)) return;
        var identity = RelayIdentityStore.Load(IdentityDirectory);
        if (identity is not null) await StartRelayAsync(identity);
    }

    private async Task StartRelayAsync(RelayIdentity identity)
    {
        if (_relayClient is not null) await _relayClient.DisposeAsync();
        _relayClient = null; _relaySnapshot = null; _launchGate = new RelayLaunchGate(identity.DeviceId); _dashboard.ClearSnapshot();
        if (string.IsNullOrWhiteSpace(_settings.RelayUrl)) return;
        try
        {
            var deviceState = Path.Combine(RelayStateDirectory, identity.DeviceId);
            if (!Directory.Exists(deviceState) && RelaySnapshotCache.Load(Path.Combine(RelayStateDirectory, "relay-snapshot.json")) is { } legacy && legacy.GroupId == identity.GroupId)
            {
                Directory.CreateDirectory(deviceState);
                foreach (var name in new[] { "relay-snapshot.json", "relay-counters.json" })
                    if (File.Exists(Path.Combine(RelayStateDirectory, name))) File.Copy(Path.Combine(RelayStateDirectory, name), Path.Combine(deviceState, name), false);
            }
            var client = new RelayClient(_settings.RelayUrl, identity, deviceState);
            _relayClient = client;
            _relaySnapshot = client.Snapshot;
            if (_relaySnapshot is not null)
            {
                _dashboard.AcceptCachedSnapshot(_relaySnapshot);
                _launchGate?.AcceptSnapshot(_relaySnapshot, authoritative: false);
            }
            client.SnapshotUpdated += snapshot => _dispatcher?.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_relayClient, client)) return;
                if (!_dashboard.AcceptAuthoritativeSnapshot(snapshot)) return;
                _launchGate?.AcceptSnapshot(snapshot);
                _relaySnapshot = _dashboard.Snapshot; _window?.RenderRelay(_relayState, _relaySnapshot, _launchGate?.State, _relayClient?.DeviceId);
                while (_dashboard.ConsumeNotification() is { } notification)
                {
                    Notify(notification, snapshot);
                    _window?.ShowRelayNotification(notification);
                }
            });
            client.ConnectionStateChanged += state => _dispatcher?.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_relayClient, client)) return;
                if (state == RelayConnectionState.Connecting) _dashboard.BeginReconnect();
                _relayState = state; _launchGate?.MarkConnectionState(state);
                _window?.RenderRelay(_relayState, _relaySnapshot, _launchGate?.State);
            });
            client.Unavailable += code => SettingsStore.Log(_settings.DebugLogging, code);
            await client.StartAsync();
            if (_state.LastGood is { } quota) await client.QueueQuotaObservationAsync(quota);
        }
        catch (RelayClientException error)
        {
            _relayState = RelayConnectionState.Offline;
            _launchGate?.MarkConnectionState(_relayState);
            SettingsStore.Log(_settings.DebugLogging, error.Code);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _relayState = RelayConnectionState.Offline;
            _launchGate?.MarkConnectionState(_relayState);
            SettingsStore.Log(_settings.DebugLogging, "RELAY_URL_INVALID");
        }
    }

    private async Task QueueRelayActivityAsync(ActivitySnapshot activity)
    {
        var client = _relayClient;
        if (client is null) return;
        try { await client.QueueActivityAsync(activity); }
        catch (OperationCanceledException) when (_exiting) { }
        catch (RelayClientException error) { SettingsStore.Log(_settings.DebugLogging, error.Code); }
    }

    private async Task QueueRelayQuotaAsync(QuotaSnapshot snapshot)
    {
        var client = _relayClient;
        if (client is null) return;
        try { await client.QueueQuotaObservationAsync(snapshot); }
        catch (OperationCanceledException) when (_exiting) { }
        catch (RelayClientException error) { SettingsStore.Log(_settings.DebugLogging, error.Code); }
    }

    private async Task<string?> CreateGroupAsync(string relayUrl)
    {
        if (_demo) return "演示模式不连接 Relay。";
        if (RelayIdentityStore.Load(IdentityDirectory) is not null) return "请先离开当前群组。";
        try
        {
            var result = await RelayClient.CreateGroupAsync(relayUrl, _settings.DeviceName ?? Environment.MachineName);
            SettingsStore.Save(_settings = _settings with { RelayUrl = relayUrl });
            RelayIdentityStore.Save(IdentityDirectory, result.Identity);
            RelaySnapshotCache.Save(Path.Combine(RelayStateDirectory, result.Identity.DeviceId, "relay-snapshot.json"), result.Snapshot);
            await StartRelayAsync(result.Identity);
            return $"已创建群组，邀请码：{result.JoinCode}（15 分钟有效）";
        }
        catch (RelayClientException error) { return "创建失败：" + error.Code; }
        catch (ArgumentException) { return "创建失败：Relay 地址无效。"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "创建失败：无法保存本机配对信息。"; }
    }

    private async Task<string?> JoinGroupAsync(string relayUrl, string joinCode)
    {
        if (_demo) return "演示模式不连接 Relay。";
        if (string.IsNullOrWhiteSpace(joinCode)) return "请输入邀请码。";
        try
        {
            var result = await RelayClient.JoinGroupAsync(relayUrl, joinCode, _settings.DeviceName ?? Environment.MachineName);
            SettingsStore.Save(_settings = _settings with { RelayUrl = relayUrl });
            RelayIdentityStore.Save(IdentityDirectory, result.Identity);
            RelaySnapshotCache.Save(Path.Combine(RelayStateDirectory, "relay-snapshot.json"), result.Snapshot);
            await StartRelayAsync(result.Identity);
            return "已加入群组，正在同步快照。";
        }
        catch (RelayClientException error) { return "加入失败：" + error.Code; }
        catch (ArgumentException) { return "加入失败：Relay 地址无效。"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "加入失败：无法保存本机配对信息。"; }
    }

    private async Task<string?> SetDeviceLimitAsync(string deviceId, string value)
    {
        if (_demo) return "演示模式不修改 Relay 限额。";
        var client = _relayClient;
        if (client is null) return "Relay 尚未连接。";
        if (string.IsNullOrWhiteSpace(deviceId)) return "请先选择设备。";
        decimal? limit = null;
        if (!string.IsNullOrWhiteSpace(value) && !value.Equals("unlimited", StringComparison.OrdinalIgnoreCase) && !value.Equals("无限", StringComparison.Ordinal))
        {
            if (!decimal.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
                parsed < 1 || parsed > 100 || decimal.Truncate(parsed) != parsed) return "限额必须是 1–100 的整数，或 Unlimited。";
            limit = parsed;
        }
        try
        {
            await client.SetDeviceLimitAsync(deviceId, limit);
            return limit is null ? "已设为 Unlimited。" : $"已设置 {limit:0}% 限额。";
        }
        catch (RelayClientException error) { return "保存限额失败：" + error.Code; }
        catch (ArgumentOutOfRangeException) { return "限额必须在 1–100 之间。"; }
    }

    private void Notify(RelayNotification notification, RelaySnapshot snapshot)
    {
        if (!_settings.DesktopNotifications || _pauseNotifications) return;
        var device = snapshot.Devices.FirstOrDefault(d => d.DeviceId == notification.DeviceId);
        var title = notification.Type == "WEEKLY_RESET" ? "OpenAI 周额度已重置" : $"{device?.DisplayName ?? "设备"} 达到周限额";
        var usage = device is not null && snapshot.UsageLedger?.DeviceUsage.TryGetValue(device.DeviceId, out var u) == true ? u : 0;
        var message = notification.Type == "WEEKLY_RESET" ? "新的统计周期已开始，设备限额已恢复。" :
            $"{usage:0.##} / {device?.LimitPercent:0.##}% · 重置：{(snapshot.OfficialQuota is { } q ? DateTimeOffset.FromUnixTimeMilliseconds(q.WeeklyResetAt).ToLocalTime().ToString("MM-dd HH:mm") : "—")}";
        try
        {
            if (_toastRegistered) AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText(title).AddText(message).BuildNotification());
            else _tray?.ShowNotification(title, message);
        }
        catch (Exception) { _tray?.ShowNotification(title, message); }
    }

    private async Task<string?> ManageAsync(string action, string? deviceId, string? value)
    {
        if (_demo) return "演示模式不修改群组。";
        var client = _relayClient;
        if (client is null) return "请先连接群组。";
        try
        {
            switch (action)
            {
                case "invite": return "邀请码（15 分钟有效）：" + await client.RotateInviteAsync();
                case "cache": _dashboard.BeginReconnect(); await client.RequestSnapshotAsync(); return "已请求最新群组状态。";
                case "leave":
                    await client.RemoveDeviceAsync(client.DeviceId);
                    await client.DisposeAsync(); _relayClient = null;
                    foreach (var name in new[] { "device-identity.json", "relay-group.json" }) File.Delete(Path.Combine(IdentityDirectory, name));
                    _relaySnapshot = null; _launchGate = null; _dashboard.ClearSnapshot(); _relayState = RelayConnectionState.Stopped;
                    _window?.RenderRelay(_relayState, null); return "已离开群组。Owner 请先转让所有权。";
            }
            if (string.IsNullOrWhiteSpace(deviceId)) return "请先选择设备。";
            switch (action)
            {
                case "rename": if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return "设备名称应为 1–64 个字符。"; await client.RenameDeviceAsync(deviceId, value); break;
                case "remove": await client.RemoveDeviceAsync(deviceId); break;
                case "transfer": await client.TransferOwnerAsync(deviceId); break;
                default: return "未知操作。";
            }
            return "已更新群组。";
        }
        catch (RelayClientException e) { return "操作失败：" + e.Code; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "无法保存本机群组状态。"; }
    }

    private Task<string?> LaunchCodexAsync()
    {
        if (_demo) return Task.FromResult<string?>("演示模式不启动本机 Codex。");
        if (_settings.SoftEnforcement && _launchGate is { State.CanStartNewCodex: false })
            return Task.FromResult<string?>("已阻止受控启动：本机 Relay 限额已达到；断网期间不会自动解除。");
        try
        {
            var command = CodexCommandResolver.Resolve(_settings.CodexExecutable);
            command.ArgumentList.Clear();
            command.UseShellExecute = true;
            Process.Start(command);
            return Task.FromResult<string?>(null);
        }
        catch (QuotaUnavailableException error) { return Task.FromResult<string?>("启动失败：" + error.Code); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return Task.FromResult<string?>("启动失败：无法启动本机 Codex。"); }
    }
    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _toggleRegistration?.Unregister(null);
        if (_enforcement is not null) await _enforcement.DisposeAsync();
        if (_toastRegistered) AppNotificationManager.Default.Unregister();
        if (_activityReader is not null) await _activityReader.DisposeAsync();
        if (_monitor is not null) await _monitor.DisposeAsync();
        if (_relayClient is not null) await _relayClient.DisposeAsync();
        _tray?.Dispose();
        Exit();
    }
    private async Task SmokeTestAsync()
    {
        ShowWindow();
        await Task.Delay(1500);
        var visible = _tray?.IsVisible == true;
        var rendered = _window?.HasDemoValues == true;
        // Exercise the production reader and App exit path with synthetic files only.
        // The explicit smoke output directory owns all fixtures and state.
        var activityReaderPassed = false;
        var activityRendered = false;
        var relayRendered = false;
        if (_smokePath is not null)
        {
            var fixtureDirectory = _smokePath + ".activity";
            var sessions = Path.Combine(fixtureDirectory, "sessions");
            Directory.CreateDirectory(sessions);
            var session = Path.Combine(sessions, "synthetic.jsonl");
            var state = Path.Combine(fixtureDirectory, "activity-state.json");
            // Each smoke execution gets an independent cutoff and count.
            if (File.Exists(state)) File.Delete(state);
            static string TokenLine(long total, DateTimeOffset at) => JsonSerializer.Serialize(new
            {
                timestamp = at, type = "event_msg",
                payload = new { type = "token_count", info = new { total_token_usage = new { total_tokens = total } } }
            }) + "\n";
            await File.WriteAllTextAsync(session, TokenLine(100, DateTimeOffset.UtcNow.AddMinutes(-1)));
            _activityReader = new CodexActivityReader(sessions, state);
            ActivitySnapshot? observed = null;
            _activityReader.Updated += snapshot => observed = snapshot;
            await _activityReader.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await File.AppendAllTextAsync(session, TokenLine(120, DateTimeOffset.UtcNow));
            await _activityReader.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (observed is { ErrorCode: null, TokenDelta: 20, TodayTokenCount: 20, CurrentSessionTokenCount: 120 })
            {
                activityReaderPassed = true;
                AcceptActivity(observed);
                activityRendered = _window?.HasActivityValues(observed) == true;
            }
        }
        // Exercise the dashboard's authoritative Relay rendering with synthetic state only.
        var demoDeviceId = "11111111-1111-4111-8111-111111111111";
        var demoDevice = new RelayDevice(demoDeviceId, "LAB-PC", "OWNER", "LIMIT_REACHED", 1, 2,
            new RelayActivityReport(1, 2, 8_000, 1)) { LimitPercent = 30 };
        var demoRelaySnapshot = new RelaySnapshot(demoDeviceId, demoDeviceId, 9, 1, 2, [demoDevice])
        {
            OfficialQuota = new RelayOfficialQuota(42.5m, 57.5m, DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeMilliseconds(), "Plus", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            WeeklyEpoch = new RelayWeeklyEpoch("demo-epoch", 1, 2, 42.5m),
            UsageLedger = new RelayUsageLedger(new Dictionary<string, decimal> { [demoDeviceId] = 30m }, 7.5m,
                new Dictionary<string, string> { [demoDeviceId] = "HIGH" })
        };
        var demoGate = new RelayLaunchGate(demoDeviceId);
        demoGate.AcceptSnapshot(demoRelaySnapshot);
        demoGate.MarkConnectionState(RelayConnectionState.Online);
        _dashboard.AcceptAuthoritativeSnapshot(demoRelaySnapshot);
        _window?.RenderRelay(RelayConnectionState.Online, demoRelaySnapshot, demoGate.State);
        _window?.ShowRelayNotification(new RelayNotification("demo-limit", "DEVICE_LIMIT_REACHED", 3, demoDeviceId));
        relayRendered = _window?.HasRelayDemoValues == true;
        if (_smokePath is not null && _window is not null) await _window.SaveScreenshotAsync(_smokePath + ".png");
        _window?.AppWindow.Hide();
        await Task.Delay(200);
        ShowWindow();
        await Task.Delay(300);
        var reopened = _window?.AppWindow.IsVisible == true;
        if (_smokePath is not null)
            await File.WriteAllTextAsync(_smokePath, JsonSerializer.Serialize(new
            {
                demo = true, tray = visible, quotaRendered = rendered, activityReader = activityReaderPassed,
                activityRendered, relayRendered, reopen = reopened,
                passed = visible && rendered && activityReaderPassed && activityRendered && relayRendered && reopened
            }));
        await ExitAsync();
    }
}
