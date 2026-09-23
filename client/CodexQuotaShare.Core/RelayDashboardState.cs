namespace CodexQuotaShare.Core;

public enum ActivityConfidence
{
    None,
    Low,
    Medium,
    High
}

/// <summary>Official account quota and device activity are deliberately separate models.</summary>
public sealed record DeviceActivityView(
    string DeviceId,
    string DisplayName,
    string Role,
    string Status,
    long? LastSeen,
    long ActivityTokenDelta,
    int ActiveSessionCount,
    ActivityConfidence Confidence,
    string Source,
    decimal? EstimatedUsagePercent);

public sealed class RelayDashboardState
{
    private RelaySnapshot? _snapshot;
    private bool _authoritative;
    private readonly Queue<RelayNotification> _notifications = new();
    private QuotaSnapshot? _officialAccountUsage;
    private readonly HashSet<string> _seenNotificationIds = new(StringComparer.Ordinal);

    public RelaySnapshot? Snapshot => _snapshot;
    public QuotaSnapshot? OfficialAccountUsage => _officialAccountUsage;
    public RelayNotification? NewNotification { get; private set; }
    public IReadOnlyList<DeviceActivityView> DeviceActivity => _snapshot is null ? [] :
        _snapshot.Devices.Select(device =>
        {
            var activity = device.LastActivity;
            var confidence = activity is null ? ActivityConfidence.None : activity.ActiveSessionCount switch
            {
                1 => ActivityConfidence.High,
                > 1 => ActivityConfidence.Medium,
                _ => ActivityConfidence.Low
            };
            decimal? estimatedUsage = null;
            var source = "local-activity-signal";
            if (_snapshot.UsageLedger is { } ledger && ledger.DeviceUsage.TryGetValue(device.DeviceId, out var estimate))
            {
                estimatedUsage = estimate;
                source = "server-attribution";
            }
            return new DeviceActivityView(device.DeviceId, device.DisplayName, device.Role, device.Status,
                device.LastSeen, activity?.TokenDelta ?? 0, activity?.ActiveSessionCount ?? 0, confidence, source, estimatedUsage);
        }).ToArray();

    /// <summary>Cache is only a display baseline; equal versions are replaced by a server snapshot later.</summary>
    public bool AcceptCachedSnapshot(RelaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_snapshot is not null && snapshot.Version < _snapshot.Version) return false;
        _snapshot = snapshot;
        foreach (var notification in snapshot.Notifications) _seenNotificationIds.Add(notification.EventId);
        if (snapshot.LastNotification is { } notificationLast) _seenNotificationIds.Add(notificationLast.EventId);
        return true;
    }

    /// <summary>Full authoritative snapshots may repair a tampered cache at the same version.</summary>
    public bool AcceptAuthoritativeSnapshot(RelaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_authoritative && _snapshot is not null && snapshot.Version <= _snapshot.Version) return false;
        _snapshot = snapshot; _authoritative = true;
        foreach (var notification in snapshot.Notifications.Concat(snapshot.LastNotification is { } last ? [last] : []))
            if (_seenNotificationIds.Add(notification.EventId)) _notifications.Enqueue(notification);
        NewNotification = _notifications.TryPeek(out var next) ? next : null;
        return true;
    }

    public bool AcceptOfficialAccountUsage(QuotaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_officialAccountUsage is not null && snapshot.ObservedAt < _officialAccountUsage.ObservedAt) return false;
        _officialAccountUsage = snapshot;
        return true;
    }

    public void ClearSnapshot() { _snapshot = null; _authoritative = false; _notifications.Clear(); NewNotification = null; }
    public void BeginReconnect() => _authoritative = false;

    public RelayNotification? ConsumeNotification()
    {
        var notification = _notifications.TryDequeue(out var value) ? value : null;
        NewNotification = _notifications.TryPeek(out var next) ? next : null;
        return notification;
    }
}
