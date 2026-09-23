namespace CodexQuotaShare.Core;

/// <summary>
/// Decides whether local Codex use is limited; process monitoring consumes the same state.
/// Quota observation is exempt from enforcement.
/// </summary>
public sealed record RelayLaunchGateState(
    bool IsLimited,
    bool IsRelayOnline,
    string Status,
    decimal? LimitPercent,
    decimal? EstimatedUsagePercent,
    string? EpochId)
{
    public bool CanStartNewCodex => !IsLimited;
}

public sealed class RelayLaunchGate
{
    private readonly string _deviceId;
    private bool _isLimited;
    private bool _isRelayOnline;
    private string _status = "UNPAIRED";
    private decimal? _limitPercent;
    private decimal? _estimatedUsagePercent;
    private string? _epochId;

    public RelayLaunchGate(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("A device id is required.", nameof(deviceId));
        _deviceId = deviceId;
    }

    public RelayLaunchGateState State => new(
        _isLimited, _isRelayOnline, _status, _limitPercent, _estimatedUsagePercent, _epochId);

    /// <summary>
    /// A cached snapshot may establish a limit immediately. A non-limited cached snapshot
    /// never clears a previously observed limit unless the server explicitly proves reset or
    /// Unlimited, so reconnects and process restarts remain fail-safe.
    /// </summary>
    public void AcceptSnapshot(RelaySnapshot snapshot, bool authoritative = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var device = snapshot.Devices.FirstOrDefault(item => item.DeviceId == _deviceId);
        if (device is null)
        {
            _status = "DEVICE_NOT_IN_GROUP";
            return;
        }

        var nextEpoch = snapshot.WeeklyEpoch?.EpochId;
        var newEpoch = _epochId is not null && nextEpoch is not null && !string.Equals(_epochId, nextEpoch, StringComparison.Ordinal);
        _epochId = nextEpoch ?? _epochId;
        _limitPercent = device.LimitPercent;
        _estimatedUsagePercent = snapshot.UsageLedger?.DeviceUsage.TryGetValue(_deviceId, out var estimate) == true ? estimate : null;

        if (device.Status is "LIMIT_REACHED" or "BLOCKED" || (_limitPercent is not null && _estimatedUsagePercent >= _limitPercent))
        {
            _isLimited = true;
            _status = "LIMIT_REACHED";
            return;
        }

        // Unlimited and a server-confirmed new epoch are the two explicit clear paths.
        if (authoritative && (device.LimitPercent is null || newEpoch || _estimatedUsagePercent < device.LimitPercent)) _isLimited = false;
        _status = _isLimited ? "LIMIT_REACHED" : device.Status;
    }

    public void MarkConnectionState(RelayConnectionState state)
    {
        _isRelayOnline = state == RelayConnectionState.Online;
        if (state == RelayConnectionState.Offline || state == RelayConnectionState.Stopped)
        {
            // Never clear _isLimited on transport loss. Existing quota reads continue separately.
            if (_isLimited) _status = "LIMIT_REACHED_OFFLINE";
        }
    }
}
