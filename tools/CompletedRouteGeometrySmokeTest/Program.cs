using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Service;

var checks = 0;
void Check(bool value, string description)
{
    if (!value) throw new Exception("FAILED: " + description);
    checks++;
    Console.WriteLine("PASS " + description);
}

var now = DateTimeOffset.UtcNow;
var root = Path.Combine(Path.GetTempPath(), "AGV-completed-route-geometry-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

// A -> B (one-way corridor), B <-> C (bidirectional, no explicit resource) so that C's
// approach gets a runtime AUTO-CORRIDOR guard. The vehicle parks at station C.
var resources = new[]
{
    new TrafficResourceDefinition("CORRIDOR", TrafficResourceType.NarrowAisle, 1,
        [new(-3, -1), new(-2, -1), new(-2, 1), new(-3, 1)]),
    new TrafficResourceDefinition("INTERSECTION", TrafficResourceType.Intersection, 1,
        [new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)]),
    new TrafficResourceDefinition("FINAL", TrafficResourceType.Station, 1,
        [new(9, -1), new(11, -1), new(11, 1), new(9, 1)]),
    new TrafficResourceDefinition("OLD-ONLY", TrafficResourceType.NarrowAisle, 1,
        [new(-4.5, 3), new(-4, 3), new(-4, 4), new(-4.5, 4)])
};
var map = new MapDefinition("geometry-test", "", "LocalCartesian", "m",
    [
        new("A", -5, 0, MapNodeType.Waypoint, true, []),
        new("B", 0, 0, MapNodeType.Waypoint, true, ["INTERSECTION"]),
        new("C", 10, 0, MapNodeType.Station, true, ["FINAL"])
    ],
    [
        new("AB", "A", "B", 5, 1, false, true, ["CORRIDOR"]),
        new("BC", "B", "C", 10, 1, true, true, [])
    ], resources);
var runtime = new ActiveMapRuntime();
runtime.Replace(new("MAP", "V1", map, now));
// BC carries its own corridor guard and the node guard of B; AB carries the node guard of B.
var topology = MapCorridorResources.Enrich(map);
var autoBc = topology.Edges.Single(e => e.Id == "BC").ResourceIds.Where(MapCorridorResources.IsAutomatic).ToArray();
var autoNodeB = topology.Edges.Single(e => e.Id == "AB").ResourceIds.Where(MapCorridorResources.IsAutomatic).ToArray();

var parked = new AgvRobotSnapshot("AGV", "AGV", "127.0.0.1", 19204,
    ConnectionState.Online, SignalQuality.Good, 10, 0, 0, .95, "C", "C",
    80, false, false, "Idle", null, null, null, [], [], now,
    now, 0, null, null, 1);
var body = new TrafficBodyEnvelope("CALIBRATED", "AGV", "LocalCartesian", .49, true, true);
var handshake = new HandshakeExecutionOptions
{
    Enabled = true,
    DumperExit = new() { BodyProfiles = [new DumperExitBodyProfile("CALIBRATED", "AGV", .49, .1, true, true)] }
};

// Builds one completed transport step with the given leases, request resources and command identity.
async Task<(int Released, HashSet<string> Remaining)> Scenario(string name, string requestState, string businessId,
    string[] requestResources, string[] leaseResources)
{
    var root2 = Path.Combine(root, name);
    Directory.CreateDirectory(root2);
    using var database = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root2, "test.db") });
    await new DatabaseInitializer(database).InitializeAsync(default);
    var payload = JsonSerializer.Serialize(new AgvTransportCommandPayload(
        businessId, "AGV", "A", "C", ["A", "B", "C"], null)
    {
        BusinessStepId = businessId.StartsWith("OBSTACLERECOVERY-", StringComparison.Ordinal) ? "" : businessId,
        WindowIndex = 0,
        WindowCount = 1,
        Windows = [new AgvTransportRouteWindow(0, "A", "C", ["A", "B", "C"], ["AB", "BC"], [], true)]
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var request = new TrafficRequestEntity
    {
        StepId = "STEP", TaskId = "TASK", VehicleId = "AGV", Source = "A", Destination = "C",
        EdgeIds = "[\"AB\",\"BC\"]", ResourceIds = JsonSerializer.Serialize(requestResources),
        State = requestState, MapId = "MAP", MapVersionId = "V1"
    };
    var snapshotJson = TransportRouteSnapshots.Serialize(TransportRouteSnapshots.Capture(
        "TASK", "STEP", "CMD", "AGV", payload, runtime.Current!, new TrafficRequestEntity
        {
            StepId = "STEP", TaskId = "TASK", VehicleId = "AGV", Source = "A", Destination = "C",
            EdgeIds = "[\"AB\",\"BC\"]", ResourceIds = request.ResourceIds, State = "Held", MapId = "MAP", MapVersionId = "V1"
        }));
    database.Insertable(new DispatchTaskEntity
    {
        TaskId = "TASK", StationId = "CNC-001", VehicleId = "AGV",
        Stage = (int)DispatchTaskStage.PickingUp, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
    }).ExecuteCommand();
    database.Insertable(new DispatchTaskStepEntity
    {
        StepId = "STEP", TaskId = "TASK", Sequence = 1, StepType = (int)DispatchTaskStepType.GoToPickup,
        Status = (int)DispatchTaskStepStatus.Completed, SourceSite = "A", DestinationSite = "C",
        VehicleId = "AGV", VendorTaskId = "STEP", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now,
        CompletedAt = DateTime.Now
    }).ExecuteCommand();
    database.Insertable(new AgvCommandOutboxEntity
    {
        CommandId = "CMD", IdempotencyKey = "STEP:STEP:TRANSPORT", TaskId = "TASK", StepId = "STEP",
        VehicleId = "AGV", VendorCommandId = "STEP", CommandType = (int)AgvCommandType.Transport,
        Status = (int)AgvCommandStatus.Sent, PayloadJson = payload, ExecutionRouteSnapshotJson = snapshotJson,
        ExecutionRouteSnapshotHash = TransportRouteSnapshots.Hash(snapshotJson), ExecutionMapId = "MAP",
        ExecutionMapVersionId = "V1", SentAt = now.AddSeconds(-10).LocalDateTime,
        CreatedAt = now.AddSeconds(-10).LocalDateTime, UpdatedAt = now.AddSeconds(-10).LocalDateTime
    }).ExecuteCommand();
    database.Insertable(request).ExecuteCommand();
    foreach (var resourceId in leaseResources)
    {
        database.Insertable(new TrafficResourceLeaseEntity
        {
            LeaseId = "LEASE-" + resourceId, ResourceId = resourceId, OwnerStepId = "STEP", TaskId = "TASK",
            VehicleId = "AGV", MapId = "MAP", MapVersionId = "V1",
            AcquiredAt = now.AddSeconds(-10).LocalDateTime, RenewedAt = now.AddSeconds(-1).LocalDateTime,
            ExpiresAt = now.AddMinutes(2).LocalDateTime
        }).ExecuteCommand();
    }
    var manager = new TrafficResourceManager(database, new TrafficResourceCatalog(resources), new(TimeSpan.FromMinutes(2)));
    var service = new CompletedTransportRouteReleaseService(database, runtime, new Vehicles(parked),
        new BodyProvider(body), handshake, new MapOptions { MinimumLocalizationConfidence = .5 },
        new TrafficSegmentReleasePolicy { Enabled = true }, manager);
    var result = await service.ReleaseAsync("TASK", "STEP", default);
    Console.WriteLine($"  [{name}] released={result.ReleasedCount} reason={result.Reason}");
    var remaining = database.Queryable<TrafficResourceLeaseEntity>().Select(l => l.ResourceId).ToList();
    return (result.ReleasedCount, remaining.ToHashSet(StringComparer.OrdinalIgnoreCase));
}

// R3: the vehicle is parked at station C with its body still covering the last edge (BC).
// The automatic guard of BC and the node guard of B must survive; the clear CORRIDOR and
// INTERSECTION leases are released.
var all = new[] { "CORRIDOR", "INTERSECTION", "FINAL" }.Concat(autoBc).Concat(autoNodeB).Distinct().ToArray();
var (releasedMain, remainingMain) = await Scenario("main", "Held", "STEP", all, all);
Check(!remainingMain.Contains("CORRIDOR") && !remainingMain.Contains("INTERSECTION"),
    "停在站点时已经驶离的路段与路口资源被释放");
Check(autoBc.Length == 2 && autoBc.All(remainingMain.Contains), "车体仍压在最后一段边上时，自动走廊资源不得释放（R3）");
Check(autoNodeB.Length == 1 && autoNodeB.All(remainingMain.Contains), "自动路口节点守卫仍覆盖车体时保留");
Check(remainingMain.Contains("FINAL"), "终点资源保留");
Check(releasedMain == 2, "到站释放数量只包括几何上已经离开的两项资源");

// R10: a request already moved to Waiting for the next window must not disable the release.
var (releasedWaiting, remainingWaiting) = await Scenario("waiting", "Waiting",
    "STEP", ["FINAL", "STOP-NEXT"], ["CORRIDOR", "INTERSECTION", "FINAL"]);
Check(releasedWaiting == 2 && !remainingWaiting.Contains("CORRIDOR") && !remainingWaiting.Contains("INTERSECTION"),
    "请求已转为等待下一窗口时，几何释放仍按租约判定（R10）");

// O9: a replacement command from obstacle recovery has its own business identity. Its old-route-only
// lease (OLD-ONLY is no longer in the request) is still released on arrival.
var (releasedRecovery, remainingRecovery) = await Scenario("recovery", "Held",
    "OBSTACLERECOVERY-20260101-STEP", ["FINAL", "INTERSECTION"], ["CORRIDOR", "INTERSECTION", "FINAL", "OLD-ONLY"]);
Check(!remainingRecovery.Contains("CORRIDOR") && !remainingRecovery.Contains("OLD-ONLY"),
    "恢复替代命令到站后释放旧路线独有的租约（O9）");
Check(remainingRecovery.Contains("FINAL"), "恢复替代命令的终点资源保留");

Console.WriteLine($"Completed route geometry: {checks} checks passed.");

sealed class Vehicles(AgvRobotSnapshot snapshot) : IAgvStateCache
{
    public AgvRobotSnapshot Snapshot = snapshot;
    public event Action<AgvRobotStateChanged>? StateChanged;
    public long Version => Snapshot.Version;
    public IReadOnlyList<AgvRobotSnapshot> GetAll() => [Snapshot];
    public bool TryGet(string vehicleId, out AgvRobotSnapshot value)
    {
        value = Snapshot;
        return vehicleId == Snapshot.VehicleId;
    }
}

sealed class BodyProvider(TrafficBodyEnvelope body) : ITrafficBodyEnvelopeProvider
{
    public Task<TrafficBodyEnvelope?> ReadAsync(string vehicleId, CancellationToken ct) =>
        Task.FromResult<TrafficBodyEnvelope?>(body.VehicleId == vehicleId ? body : null);
}
