using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuotaShare.Core;

public static class RelayProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 16 * 1024;
    public static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)
    ];
}

public sealed record RelayIdentity(
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("deviceSecret")] string DeviceSecret);

public sealed record RelayActivityReport(
    [property: JsonPropertyName("windowStart")] long WindowStart,
    [property: JsonPropertyName("windowEnd")] long WindowEnd,
    [property: JsonPropertyName("tokenDelta")] long TokenDelta,
    [property: JsonPropertyName("activeSessionCount")] int ActiveSessionCount);

public sealed record RelayDevice(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("joinedAt")] long JoinedAt,
    [property: JsonPropertyName("lastSeen")] long? LastSeen,
    [property: JsonPropertyName("lastActivity")] RelayActivityReport? LastActivity)
{
    [JsonPropertyName("limitPercent")] public decimal? LimitPercent { get; init; }
}

public sealed record RelayOfficialQuota(
    [property: JsonPropertyName("weeklyUsedPercent")] decimal WeeklyUsedPercent,
    [property: JsonPropertyName("weeklyRemainingPercent")] decimal WeeklyRemainingPercent,
    [property: JsonPropertyName("weeklyResetAt")] long WeeklyResetAt,
    [property: JsonPropertyName("planType")] string? PlanType,
    [property: JsonPropertyName("observedAt")] long ObservedAt);

public sealed record RelayWeeklyEpoch(
    [property: JsonPropertyName("epochId")] string EpochId,
    [property: JsonPropertyName("startedAt")] long StartedAt,
    [property: JsonPropertyName("resetAt")] long ResetAt,
    [property: JsonPropertyName("lastQuota")] decimal LastQuota);

public sealed record RelayUsageLedger(
    [property: JsonPropertyName("deviceUsage")] IReadOnlyDictionary<string, decimal> DeviceUsage,
    [property: JsonPropertyName("unattributedUsage")] decimal UnattributedUsage,
    [property: JsonPropertyName("confidence")] IReadOnlyDictionary<string, string> Confidence);

public sealed record RelayNotification(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("deviceId")] string? DeviceId = null);

public sealed record RelaySnapshot(
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("ownerDeviceId")] string OwnerDeviceId,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt,
    [property: JsonPropertyName("devices")] IReadOnlyList<RelayDevice> Devices)
{
    [JsonPropertyName("officialQuota")] public RelayOfficialQuota? OfficialQuota { get; init; }
    [JsonPropertyName("weeklyEpoch")] public RelayWeeklyEpoch? WeeklyEpoch { get; init; }
    [JsonPropertyName("usageLedger")] public RelayUsageLedger? UsageLedger { get; init; }
    [JsonPropertyName("lastNotification")] public RelayNotification? LastNotification { get; init; }
    [JsonPropertyName("notifications")] public IReadOnlyList<RelayNotification> Notifications { get; init; } = [];
}

public sealed record RelayPairingResult(
    RelayIdentity Identity,
    RelaySnapshot Snapshot,
    string? JoinCode,
    long? JoinCodeExpiresAt);

public enum RelayConnectionState
{
    Offline,
    Connecting,
    Online,
    Stopped
}

public sealed record RelaySnapshotEnvelope(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("snapshot")] RelaySnapshot Snapshot);

public static class RelaySnapshotCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RelaySnapshot? Load(string path)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<RelaySnapshotEnvelope>(File.ReadAllText(path), JsonOptions);
            return envelope is { ProtocolVersion: RelayProtocol.Version, Type: "GROUP_SNAPSHOT" } ? envelope.Snapshot : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(string path, RelaySnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A directory is required.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        var envelope = new RelaySnapshotEnvelope(RelayProtocol.Version, "GROUP_SNAPSHOT", snapshot);
        File.WriteAllText(temporary, JsonSerializer.Serialize(envelope, JsonOptions));
        File.Move(temporary, path, true);
    }
}
