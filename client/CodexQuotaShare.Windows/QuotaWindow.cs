using System.Globalization;
using CodexQuotaShare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using global::Windows.Graphics;
using global::Windows.Storage.Pickers;

namespace CodexQuotaShare.Windows;

internal sealed class QuotaWindow : Window
{
    private ActivitySnapshot? _lastActivity;
    private readonly bool _demo;
    private readonly TextBlock _used = new() { Text = "—", FontSize = 44, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _remaining = new() { Text = "等待额度数据", FontSize = 15 };
    private readonly TextBlock _reset = new() { Text = "—", FontSize = 15, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _observed = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _todayActivity = new() { Text = "—", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _currentSession = new() { Text = "当前会话：—", FontSize = 14 };
    private readonly TextBlock _tokenDelta = new() { Text = "本次新增：—", FontSize = 14 };
    private readonly TextBlock _activeSessions = new() { Text = "活跃会话：—", FontSize = 14 };
    private readonly TextBlock _relayStatus = new() { Text = "未配对", FontSize = 14 };
    private readonly TextBlock _relayDevices = new() { Text = "暂无群组快照", FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _launchStatus = new() { Text = "受控启动入口：未配对时不执行 Relay 限制。", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _plan = new() { Text = "OpenAI Account", FontSize = 15 };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 8 };
    private readonly InfoBar _status = new() { IsOpen = true, IsClosable = false, Title = "正在读取 OpenAI 额度", Severity = InfoBarSeverity.Informational };
    private readonly TextBox _executable;
    private readonly TextBox _relayUrl;
    private readonly TextBox _joinCode;
    private readonly ComboBox _limitDeviceId;
    private readonly StackPanel _pairingControls = new() { Spacing = 8 };
    private readonly Expander _ownerPanel = new() { Header = "管理设备", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _ownerControls = new() { Spacing = 8 };
    private readonly CheckBox _startup, _notifications, _enforcement;
    private readonly TextBox _deviceName;
    private readonly InfoBar _limitWarning = new() { IsClosable = false, Severity = InfoBarSeverity.Error };
    private readonly TextBox _limitValue;
    private readonly CheckBox _logging;
    private readonly Button _refresh = new() { Content = "立即刷新" };
    public bool HasDemoValues => _used.Text == "42.5%" && _remaining.Text.Contains("57.5%") && _progress.Value == 42.5;
    internal bool HasRelayDemoValues =>
        _relayDevices.Text.Contains("estimated 30%", StringComparison.Ordinal)
        && _relayDevices.Text.Contains("unattributed 7.5%", StringComparison.Ordinal)
        && _relayDevices.Text.Contains("LIMIT_REACHED", StringComparison.Ordinal)
        && _launchStatus.Text.Contains("已阻止", StringComparison.Ordinal)
        && _relayStatus.Text.Contains("Relay 通知", StringComparison.Ordinal);
    internal bool HasActivityValues(ActivitySnapshot activity) =>
        _todayActivity.Text == $"今日 token：{activity.TodayTokenCount:N0}"
        && _currentSession.Text == $"当前会话 token：{activity.CurrentSessionTokenCount:N0}"
        && _tokenDelta.Text.StartsWith($"本次新增：{activity.TokenDelta:N0}", StringComparison.Ordinal)
        && _activeSessions.Text == $"活跃会话：{activity.ActiveSessionCount}";

    public QuotaWindow(LocalSettings settings, bool demo, Func<Task> refresh, Func<LocalSettings, Task<string?>> save,
        Func<string, Task<string?>>? createGroup = null, Func<string, string, Task<string?>>? joinGroup = null,
        Func<string, string, Task<string?>>? setLimit = null, Func<Task<string?>>? launchCodex = null,
        Func<string, string?, string?, Task<string?>>? manage = null)
    {
        _demo = demo;
        Title = "CodexQuotaShare";
        AppWindow.Resize(new SizeInt32(560, 760));
        AppWindow.Closing += (_, e) => { e.Cancel = true; AppWindow.Hide(); };
        var stack = new StackPanel { Padding = new Thickness(28), Spacing = 16, RequestedTheme = ElementTheme.Light };
        stack.Children.Add(new TextBlock { Text = "CodexQuotaShare", FontSize = 25, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = demo ? "演示模式 · 合成数据" : "账号额度与设备周限额", Opacity = 0.65 });
        stack.Children.Add(_status);
        stack.Children.Add(_limitWarning);
        var card = new StackPanel { Spacing = 12 };
        card.Children.Add(_plan);
        card.Children.Add(new TextBlock { Text = "官方账号周额度 / Weekly used", FontSize = 14 });
        card.Children.Add(_used); card.Children.Add(_progress); card.Children.Add(_remaining);
        card.Children.Add(new TextBlock { Text = "重置时间", FontSize = 13, Opacity = 0.65 });
        card.Children.Add(_reset); card.Children.Add(_observed);
        stack.Children.Add(new Border { Child = card, Padding = new Thickness(20), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 245, 247, 250)) });
        var activityCard = new StackPanel { Spacing = 8 };
        activityCard.Children.Add(new TextBlock { Text = "本机 Activity（额度归因信号）", FontSize = 14 });
        activityCard.Children.Add(_todayActivity);
        activityCard.Children.Add(_currentSession);
        activityCard.Children.Add(_tokenDelta);
        activityCard.Children.Add(_activeSessions);
        activityCard.Children.Add(new TextBlock { Text = "仅用于估算设备活动，不代表 OpenAI 官方额度。首次扫描历史文件只建立 baseline。", FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        var activityBorder = new Border { Child = activityCard, Padding = new Thickness(20), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 248, 246, 240)) };
        var relayCard = new StackPanel { Spacing = 8 };
        relayCard.Children.Add(new TextBlock { Text = "我的设备", FontSize = 14 });
        relayCard.Children.Add(new TextBlock { Text = "设备 Activity 仅是归因信号，不是 OpenAI 官方额度。", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        relayCard.Children.Add(_relayStatus);
        relayCard.Children.Add(_relayDevices);
        _relayUrl = new TextBox { Text = settings.RelayUrl ?? ProductConfiguration.DefaultRelayUrl ?? "", Header = "自定义同步服务地址（高级）", PlaceholderText = "此候选版尚未配置公共服务" };
        _pairingControls.Children.Add(new Expander { Header = "高级连接设置", Content = _relayUrl });
        _joinCode = new TextBox { PlaceholderText = "粘贴 Owner 邀请码", Header = "加入邀请码" };
        _pairingControls.Children.Add(_joinCode);
        var pairingButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var create = new Button { Content = "创建群组" };
        var join = new Button { Content = "加入群组" };
        create.Click += async (_, _) =>
        {
            if (createGroup is null) { ShowMessage("当前不可创建 Relay 群组。", InfoBarSeverity.Warning); return; }
            create.IsEnabled = false;
            try { SetRelayMessage(await createGroup(_relayUrl.Text.Trim())); }
            finally { create.IsEnabled = true; }
        };
        join.Click += async (_, _) =>
        {
            if (joinGroup is null) { ShowMessage("当前不可加入 Relay 群组。", InfoBarSeverity.Warning); return; }
            join.IsEnabled = false;
            try { SetRelayMessage(await joinGroup(_relayUrl.Text.Trim(), _joinCode.Text.Trim())); }
            finally { join.IsEnabled = true; }
        };
        pairingButtons.Children.Add(create); pairingButtons.Children.Add(join);
        _pairingControls.Children.Add(pairingButtons);
        relayCard.Children.Add(_pairingControls);
        _limitDeviceId = new ComboBox { Header = "选择设备", DisplayMemberPath = "DisplayName", SelectedValuePath = "DeviceId", HorizontalAlignment = HorizontalAlignment.Stretch };
        _limitValue = new TextBox { Text = "Unlimited", PlaceholderText = "1–100 或 Unlimited", Header = "每周限额百分比" };
        _ownerControls.Children.Add(_limitDeviceId); _ownerControls.Children.Add(_limitValue);
        var applyLimit = new Button { Content = "保存设备限额" };
        applyLimit.Click += async (_, _) =>
        {
            if (setLimit is null) { ShowMessage("当前不可修改 Relay 限额。", InfoBarSeverity.Warning); return; }
            applyLimit.IsEnabled = false;
            try { SetRelayMessage(await setLimit(_limitDeviceId.SelectedValue as string ?? "", _limitValue.Text.Trim())); }
            finally { applyLimit.IsEnabled = true; }
        };
        _ownerControls.Children.Add(applyLimit);
        var rename = new TextBox { Header = "设备新名称" };
        _ownerControls.Children.Add(rename);
        Button ActionButton(string label, string action, Func<string?>? value = null)
        {
            var button = new Button { Content = label };
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try
                {
                    if (action is "remove" or "transfer" or "leave")
                    {
                        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = label, Content = "确认执行此设备管理操作？", PrimaryButtonText = "确认", CloseButtonText = "取消" };
                        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                    }
                    SetRelayMessage(manage is null ? "当前不可用。" : await manage(action, _limitDeviceId.SelectedValue as string, value?.Invoke()));
                }
                finally { button.IsEnabled = true; }
            };
            return button;
        }
        _ownerControls.Children.Add(ActionButton("重命名设备", "rename", () => rename.Text.Trim()));
        _ownerControls.Children.Add(ActionButton("移除设备", "remove"));
        _ownerControls.Children.Add(ActionButton("转让 Owner", "transfer"));
        _ownerControls.Children.Add(ActionButton("生成新邀请码", "invite"));
        _ownerControls.Visibility = Visibility.Collapsed;
        _ownerPanel.Content = _ownerControls;
        relayCard.Children.Add(_ownerPanel);
        relayCard.Children.Add(ActionButton("离开群组", "leave"));
        relayCard.Children.Add(ActionButton("重载显示缓存", "cache"));
        var launch = new Button { Content = "启动 Codex" };
        launch.Click += async (_, _) =>
        {
            launch.IsEnabled = false;
            try
            {
                var message = launchCodex is null ? "当前不可使用受控启动入口。" : await launchCodex();
                if (!string.IsNullOrWhiteSpace(message)) { _launchStatus.Text = message; ShowMessage(message, InfoBarSeverity.Warning); }
                else _launchStatus.Text = "已请求启动本机 Codex。";
            }
            finally { launch.IsEnabled = true; }
        };
        relayCard.Children.Add(launch);
        relayCard.Children.Add(_launchStatus);
        stack.Children.Add(new Border { Child = relayCard, Padding = new Thickness(20), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 244, 248, 245)) });
        stack.Children.Add(new Expander { Header = "本机活动详情", Content = activityBorder, HorizontalAlignment = HorizontalAlignment.Stretch });
        _refresh.Click += async (_, _) => { _refresh.IsEnabled = false; try { await refresh(); } finally { _refresh.IsEnabled = true; } };
        stack.Children.Add(_refresh);
        var options = new StackPanel { Spacing = 10 };
        options.Children.Add(new TextBlock { Text = "优先自动查找已安装的 Codex。需要时可选择本机 codex.exe。", TextWrapping = TextWrapping.Wrap });
        _executable = new TextBox { Text = settings.CodexExecutable ?? "", PlaceholderText = "自动查找", Header = "Codex 程序位置" };
        options.Children.Add(_executable);
        var browse = new Button { Content = "选择程序…" };
        browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync(); if (file is not null) _executable.Text = file.Path;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            { ShowMessage("无法打开程序选择器，请输入程序路径。", InfoBarSeverity.Warning); }
        };
        options.Children.Add(browse);
        _deviceName = new TextBox { Header = "本机设备名称（用于配对）", Text = settings.DeviceName ?? Environment.MachineName };
        _startup = new CheckBox { Content = "登录 Windows 时启动", IsChecked = settings.LaunchAtStartup };
        _notifications = new CheckBox { Content = "桌面通知", IsChecked = settings.DesktopNotifications };
        _enforcement = new CheckBox { Content = "Soft enforcement：达到限额时关闭本机 Codex", IsChecked = settings.SoftEnforcement };
        options.Children.Add(_deviceName); options.Children.Add(_startup); options.Children.Add(_notifications); options.Children.Add(_enforcement);
        options.Children.Add(new TextBlock { Text = "启用限制后会关闭已识别的 Codex Desktop / CLI，正在运行的任务可能中断。额度读取不受影响。增强防火墙限制未启用。", TextWrapping = TextWrapping.Wrap });
        var updates = new Button { Content = "检查 GitHub 更新" };
        updates.Click += async (_, _) => ShowMessage(await ProductConfiguration.CheckReleaseAsync() ?? "", InfoBarSeverity.Informational);
        options.Children.Add(updates);
        var releases = new Button { Content = "打开下载页面", IsEnabled = ProductConfiguration.Repository is not null };
        releases.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://github.com/{ProductConfiguration.Repository}/releases") { UseShellExecute = true });
        options.Children.Add(releases);
        _logging = new CheckBox { Content = "诊断日志（仅状态码，不含账号内容）", IsChecked = settings.DebugLogging };
        options.Children.Add(_logging);
        var apply = new Button { Content = "保存并重新连接" };
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try
            {
                var result = await save(new LocalSettings(string.IsNullOrWhiteSpace(_executable.Text) ? null : _executable.Text.Trim(), _logging.IsChecked == true,
                    string.IsNullOrWhiteSpace(_relayUrl.Text) ? null : _relayUrl.Text.Trim(),
                    _startup.IsChecked == true, _notifications.IsChecked == true, _enforcement.IsChecked == true, _deviceName.Text.Trim()));
                if (result is not null) ShowMessage(result, InfoBarSeverity.Error);
            }
            finally { apply.IsEnabled = true; }
        };
        options.Children.Add(apply);
        stack.Children.Add(new Expander { Header = "设置", Content = options, HorizontalAlignment = HorizontalAlignment.Stretch });
        stack.Children.Add(new Expander { Header = "隐私", HorizontalAlignment = HorizontalAlignment.Stretch, Content = new TextBlock { Text = "通过本机 Codex 读取官方额度，并扫描本机会话文件中的 token 计数和时间戳。仅保存统计值、会话文件路径、完整行位置及前缀哈希，不保存提示词、响应或会话原文。\n\n配对后上传设备标识、名称、官方额度百分比、重置时间、本机活动汇总、时间戳与序列号。永不上传 OpenAI 凭据、Cookie、提示词、响应、源代码、会话原文或文件内容。", TextWrapping = TextWrapping.Wrap, MaxWidth = 430 } });
        stack.Children.Add(new TextBlock { Text = "关闭窗口后仍在托盘运行。可从托盘菜单退出。", FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        Content = new ScrollViewer { Content = stack, Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255)) };
    }

    public void RenderRelay(RelayConnectionState state, RelaySnapshot? snapshot, RelayLaunchGateState? gate = null, string? localDeviceId = null)
    {
        _pairingControls.Visibility = snapshot is null ? Visibility.Visible : Visibility.Collapsed;
        _ownerPanel.Visibility = snapshot is not null && snapshot.OwnerDeviceId == localDeviceId ? Visibility.Visible : Visibility.Collapsed;
        _ownerControls.Visibility = Visibility.Visible;
        var selected = _limitDeviceId.SelectedValue as string;
        _limitDeviceId.ItemsSource = snapshot?.Devices;
        _limitDeviceId.SelectedValue = selected;
        _limitWarning.IsOpen = gate?.IsLimited == true;
        _limitWarning.Title = "本机已达到周限额 / LIMIT_REACHED";
        _limitWarning.Message = $"设备估算 {gate?.EstimatedUsagePercent:0.##}% / 限额 {gate?.LimitPercent:0.##}%。新周期或 Owner 提高限额后恢复。";
        _relayStatus.Text = state switch
        {
            RelayConnectionState.Online => "Relay 已连接",
            RelayConnectionState.Connecting => "Relay 正在连接…",
            RelayConnectionState.Offline => "Relay 离线（保留上次快照，不自动解除限制）",
            RelayConnectionState.Stopped => "Relay 未启动",
            _ => "Relay 状态未知"
        };
        _relayDevices.Text = snapshot is null
            ? "暂无群组快照"
            : "群组 " + snapshot.GroupId + " · 同步 " + DateTimeOffset.FromUnixTimeMilliseconds(snapshot.UpdatedAt).ToLocalTime().ToString("HH:mm:ss") + Environment.NewLine +
              string.Join(Environment.NewLine, snapshot.Devices.Select(device =>
              {
                  var activity = device.LastActivity is null ? "activity —" : $"activity {device.LastActivity.TokenDelta:N0} tokens";
                  var estimate = snapshot.UsageLedger?.DeviceUsage.TryGetValue(device.DeviceId, out var value) == true
                      ? $"estimated {value:0.##}%" : "estimated —";
                  return $"{device.DisplayName} · {device.Role} · {device.Status} · {estimate} / {(device.LimitPercent is null ? "Unlimited" : device.LimitPercent + "%")} · 最后在线 {(device.LastSeen is { } seen ? DateTimeOffset.FromUnixTimeMilliseconds(seen).ToLocalTime().ToString("MM-dd HH:mm") : "—")}";
              })) + Environment.NewLine +
              $"Other / Unknown · unattributed {snapshot.UsageLedger?.UnattributedUsage:0.##}%";
        if (snapshot?.OfficialQuota is { } official)
        {
            var display = new QuotaDisplayState();
            display.Accept(new QuotaSnapshot(official.PlanType, official.WeeklyUsedPercent, DateTimeOffset.FromUnixTimeMilliseconds(official.WeeklyResetAt), DateTimeOffset.FromUnixTimeMilliseconds(official.ObservedAt)));
            Render(display, _lastActivity, _demo);
            _observed.Text = "群组官方额度观察：" + DateTimeOffset.FromUnixTimeMilliseconds(official.ObservedAt).ToLocalTime().ToString("MM-dd HH:mm:ss");
        }
        _launchStatus.Text = gate is null
            ? "受控启动入口：未配对时不执行 Relay 限制。"
            : gate.IsLimited
                ? "已阻止受控启动：LIMIT_REACHED（断网期间保持限制）。"
                : $"受控启动入口：允许（Relay {gate.Status}）。";
    }

    public async Task SaveScreenshotAsync(string path)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(Content);
        var pixels = await bitmap.GetPixelsAsync();
        var bytes = new byte[pixels.Length];
        using (var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
        var folder = await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), global::Windows.Storage.CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(global::Windows.Storage.FileAccessMode.ReadWrite);
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
    }

    public void ShowRelayNotification(RelayNotification notification)
    {
        _relayStatus.Text = notification.Type == "WEEKLY_RESET"
            ? "已收到通知：OpenAI 周额度新周期已确认。"
            : "已收到 Relay 通知：" + notification.Type;
    }

    private void SetRelayMessage(string? message)
    { if (!string.IsNullOrWhiteSpace(message)) _relayStatus.Text = message; }

    public void Render(QuotaDisplayState state, ActivitySnapshot? activity, bool demo)
    {
        if (state.LastGood is { } q)
        {
            _plan.Text = q.PlanType is null ? "OpenAI Account" : "ChatGPT " + q.PlanType;
            _used.Text = q.WeeklyUsedPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%";
            _remaining.Text = "剩余 " + q.WeeklyRemainingPercent.ToString("0.##", CultureInfo.InvariantCulture) + "%";
            _progress.Value = (double)q.WeeklyUsedPercent;
            var remaining = q.WeeklyResetAt - DateTimeOffset.UtcNow;
            var countdown = remaining > TimeSpan.Zero ? $"（约 {remaining.Days} 天 {remaining.Hours} 小时）" : "（等待新周期数据）";
            _reset.Text = q.WeeklyResetAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz") + " " + countdown;
            _observed.Text = (state.IsStale ? "上次有效数据：" : "数据时间：") + q.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
        }
        _lastActivity = activity;
        RenderActivity(activity);
        if (state.ErrorCode is { } error)
        {
            _status.Title = "无法读取 OpenAI 额度 / Unable to read OpenAI quota";
            _status.Message = ErrorText(error) + (state.LastGood is null ? "" : " 当前显示上次有效数据，可能已过时。");
            _status.Severity = InfoBarSeverity.Warning; _status.IsOpen = true;
        }
        else if (state.LastGood is not null)
        {
            _status.Title = demo ? "演示模式" : "额度已更新";
            _status.Message = demo ? "当前为合成数据，未连接真实账号。" : "来自本机已登录 Codex；每 60 秒补充刷新。";
            _status.Severity = InfoBarSeverity.Informational; _status.IsOpen = true;
        }
    }

    private void RenderActivity(ActivitySnapshot? activity)
    {
        if (activity is null)
        {
            _todayActivity.Text = "暂无本机 Activity 数据";
            _currentSession.Text = "当前会话：—";
            _tokenDelta.Text = "本次新增：—";
            _activeSessions.Text = "活跃会话：—";
            return;
        }

        if (activity.ErrorCode is not null)
        {
            _todayActivity.Text = "本机 Activity 暂不可用";
            _currentSession.Text = "错误：" + activity.ErrorCode;
            _tokenDelta.Text = "本次新增：—";
            _activeSessions.Text = "活跃会话：—";
            return;
        }

        _todayActivity.Text = $"今日 token：{activity.TodayTokenCount:N0}";
        _currentSession.Text = $"当前会话 token：{activity.CurrentSessionTokenCount:N0}";
        _tokenDelta.Text = $"本次新增：{activity.TokenDelta:N0}" + (activity.BaselineEstablished ? "（历史 baseline）" : "");
        _activeSessions.Text = $"活跃会话：{activity.ActiveSessionCount}";
    }
    private void ShowMessage(string text, InfoBarSeverity severity)
    { _status.Title = text; _status.Message = ""; _status.Severity = severity; _status.IsOpen = true; }
    private static string ErrorText(string code) => code switch
    {
        "CODEX_NOT_FOUND" => "未找到可用 Codex 程序。请确认已安装 Codex，或在设置中选择本机 codex.exe。",
        "CODEX_REQUEST_REJECTED" => "Codex 无法提供额度，请确认已登录支持的 ChatGPT 账号。",
        "WEEKLY_QUOTA_UNAVAILABLE" or "ACCOUNT_QUOTA_UNAVAILABLE" => "当前账号没有可识别的周额度数据。",
        "CODEX_TIMEOUT" => "读取超时，将自动重试，也可立即刷新。",
        "CODEX_EXITED" or "CODEX_DISCONNECTED" => "Codex 连接已断开，将自动重新连接。",
        _ => "额度数据暂不可用。请稍后刷新或更新 Codex。"
    };
}
