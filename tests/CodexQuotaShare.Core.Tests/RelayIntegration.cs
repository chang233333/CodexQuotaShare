using CodexQuotaShare.Core;
namespace CodexQuotaShare.Core.Tests;

internal static class RelayIntegration
{
    public static async Task<int> RunAsync(string url, string root)
    {
        var clients = new List<RelayClient>();
        async Task Wait(Func<bool> condition, string name)
        {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (!condition()) { if (DateTime.UtcNow > deadline) throw new Exception("Timed out: " + name); await Task.Delay(50); }
            Console.WriteLine("PASS " + name);
        }
        try
        {
            var owner = await RelayClient.CreateGroupAsync(url, "MAIN-PC");
            var members = new List<RelayPairingResult> { owner };
            foreach (var name in new[] { "LAB-PC", "LAPTOP", "OFFICE-PC" }) members.Add(await RelayClient.JoinGroupAsync(url, owner.JoinCode!, name));
            foreach (var m in members) clients.Add(new RelayClient(url, m.Identity, Path.Combine(root, m.Identity.DeviceId)));
            for (var i = 1; i < 4; i++) await clients[0].SetDeviceLimitAsync(members[i].Identity.DeviceId, i == 3 ? 20 : 30);
            foreach (var c in clients) await c.StartAsync();
            await Wait(() => clients.All(c => c.ConnectionState == RelayConnectionState.Online && c.Snapshot?.Devices.Count == 4), "four real C# WebSocket clients connect");
            var reset = DateTimeOffset.UtcNow.AddDays(3);
            await clients[1].QueueQuotaObservationAsync(new("Plus", 40, reset, DateTimeOffset.UtcNow));
            await Wait(() => clients.All(c => c.Snapshot?.OfficialQuota?.WeeklyUsedPercent == 40), "official quota visible on all clients");
            await clients[0].StopAsync();
            await clients[1].QueueActivityAsync(new(DateTimeOffset.UtcNow, 100, 100, 100, 1, DateTimeOffset.UtcNow, false));
            await Wait(() => clients[2].Snapshot?.Devices.First(d => d.DeviceId == members[1].Identity.DeviceId).LastActivity?.TokenDelta == 100, "activity sync with owner offline");
            await clients[1].QueueQuotaObservationAsync(new("Plus", 70, reset, DateTimeOffset.UtcNow));
            await Wait(() => clients.Skip(1).All(c => c.Snapshot?.UsageLedger?.DeviceUsage[members[1].Identity.DeviceId] == 30), "LAB-PC reaches 30 percent");
            await Wait(() => clients.Skip(1).All(c => c.Snapshot?.LastNotification?.Type == "DEVICE_LIMIT_REACHED"), "limit notification reaches every online client");
            var gate = new RelayLaunchGate(members[1].Identity.DeviceId); gate.AcceptSnapshot(clients[1].Snapshot!);
            if (gate.State.CanStartNewCodex) throw new Exception("Limit gate did not block");
            await clients[1].DisposeAsync();
            var cache = Path.Combine(root, members[1].Identity.DeviceId, "relay-snapshot.json");
            var tampered = RelaySnapshotCache.Load(cache)! with { Version = 999999, UsageLedger = new(new Dictionary<string, decimal> { [members[1].Identity.DeviceId] = 0 }, 0, new Dictionary<string, string>()) };
            RelaySnapshotCache.Save(cache, tampered);
            clients[1] = new RelayClient(url, members[1].Identity, Path.Combine(root, members[1].Identity.DeviceId));
            await clients[1].StartAsync();
            await Wait(() => clients[1].Snapshot?.UsageLedger?.DeviceUsage[members[1].Identity.DeviceId] == 30, "restart repairs tampered cache and preserves limit");
            try { await clients[1].SetDeviceLimitAsync(members[1].Identity.DeviceId, null); throw new Exception("Member policy accepted"); }
            catch (RelayClientException e) when (e.Code == "OWNER_REQUIRED") { Console.WriteLine("PASS member cannot change limit"); }
            await clients[2].QueueQuotaObservationAsync(new("Plus", 1, reset.AddDays(7), DateTimeOffset.UtcNow));
            await Task.Delay(300);
            if (clients[1].Snapshot!.WeeklyEpoch!.ResetAt != reset.ToUnixTimeMilliseconds()) throw new Exception("Single observer reset accepted");
            await clients[3].QueueQuotaObservationAsync(new("Plus", 1, reset.AddDays(7), DateTimeOffset.UtcNow));
            await Wait(() => clients.Skip(1).All(c => c.Snapshot?.WeeklyEpoch?.ResetAt == reset.AddDays(7).ToUnixTimeMilliseconds()), "members confirm weekly reset with owner offline");
            gate.AcceptSnapshot(clients[1].Snapshot!);
            if (!gate.State.CanStartNewCodex || clients[1].Snapshot!.UsageLedger!.DeviceUsage.Values.Any(v => v != 0)) throw new Exception("Reset did not clear limits");
            await clients[1].StopAsync(); await clients[1].StartAsync();
            await Wait(() => clients[1].ConnectionState == RelayConnectionState.Online, "network reconnect gets complete snapshot");
            Console.WriteLine("PASS C# to Worker acceptance"); return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e); return 1; }
        finally { foreach (var c in clients) await c.DisposeAsync(); }
    }
}
