using System.Text.Json;
using CodexQuotaShare.Core;

namespace CodexQuotaShare.Windows;

/// <summary>Stores only the group id beside the DPAPI-protected device secret.</summary>
internal static class RelayIdentityStore
{
    private sealed record GroupEnvelope(int Version, string GroupId);

    public static void Save(string directory, RelayIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(identity);
        Directory.CreateDirectory(directory);
        DeviceSecretStore.Save(Path.Combine(directory, "device-identity.json"), identity.DeviceId, identity.DeviceSecret);
        var path = Path.Combine(directory, "relay-group.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new GroupEnvelope(1, identity.GroupId)));
        File.Move(temporary, path, true);
    }

    public static RelayIdentity? Load(string directory)
    {
        try
        {
            var group = JsonSerializer.Deserialize<GroupEnvelope>(File.ReadAllText(Path.Combine(directory, "relay-group.json")));
            var (deviceId, deviceSecret) = DeviceSecretStore.Load(Path.Combine(directory, "device-identity.json"));
            if (group is not { Version: 1 } || string.IsNullOrWhiteSpace(group.GroupId)) return null;
            return new RelayIdentity(group.GroupId, deviceId, deviceSecret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidDataException)
        { return null; }
    }
}
