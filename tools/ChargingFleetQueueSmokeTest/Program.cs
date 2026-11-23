using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Service;

// Queue priority of charging against the production traffic manager (SQLite) and the fleet policy of the charging service.
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
    Console.WriteLine("PASS " + description);
}
var evidence = Path.Combine(Path.GetTempPath(), "agv-charging-fleet-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(evidence);

// C5: a critical vehicle keeps its place at the head of the bay queue; a higher-battery request cannot take the bay first.
using (var database = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(evidence, "traffic.db") }))
{
    var catalog = new TrafficResourceCatalog([new("BAY", TrafficResourceType.ChargingBay, 1)]);
    await new DatabaseInitializer(database, catalog).InitializeAsync(default);
    var traffic = new TrafficResourceManager(database, catalog, new(TimeSpan.FromSeconds(1)));
    async Task<bool> Reserve(string session, string vehicle, double priority)
    {
        await traffic.RecordRouteAsync(new(session + "-LOCK", session, vehicle, "STATION", "CHARGER", [], ["BAY"], false, "synthetic charging")
        { MapId = "MAP", MapVersionId = "V1", QueuePriority = priority }, default);
        return (await traffic.TryAcquireAsync(session + "-LOCK", session, vehicle, ["BAY"], default)).Acquired;
    }
    const double critical = ChargingRuntimePort.CriticalChargingQueueTier;
    Check(await Reserve("CHARGE-HOLDER", "H", 0), "the bay is granted to the first charging request");
    Check(!await Reserve("CHARGE-HIGHER", "B", 0), "an ordinary charging request waits behind the holder");
    Check(!await Reserve("CHARGE-CRITICAL", "C", critical + 90), "a critical charging request queues behind the holder");
    await traffic.ReleaseTaskAsync("CHARGE-HOLDER", default);
    Check(!await Reserve("CHARGE-HIGHER", "B", 0), "after release the higher-battery request cannot take the bay ahead of the critical vehicle");
    Check(await Reserve("CHARGE-CRITICAL", "C", critical + 90), "the critical vehicle takes the released bay");
}

Console.WriteLine($"PASS: {checks} charging queue and fleet checks.");

