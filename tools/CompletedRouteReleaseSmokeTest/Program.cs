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
    if (!value) throw new Exception(description);
    checks++;
    Console.WriteLine("PASS " + description);
}

var now = DateTimeOffset.UtcNow;
var root = Path.Combine(Path.GetTempPath(),
    "AGV-completed-route-release-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var database = DatabaseFactory.Create(new()
{
    DatabasePath = Path.Combine(root, "test.db")
});
await new DatabaseInitializer(database).InitializeAsync(default);

var resources = new[]
{
    new TrafficResourceDefinition("CORRIDOR", TrafficResourceType.NarrowAisle, 1,
        [new(-3, -1), new(-2, -1), new(-2, 1), new(-3, 1)]),
    new TrafficResourceDefinition("INTERSECTION", TrafficResourceType.Intersection, 1,
        [new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)]),
    new TrafficResourceDefinition("FINAL", TrafficResourceType.Station, 1,
        [new(9, -1), new(11, -1), new(11, 1), new(9, 1)])
};
var map = new MapDefinition("completed-route-test", "", "LocalCartesian", "m",
    [
        new("A", -5, 0, MapNodeType.Waypoint, true, []),
        new("B", 0, 0, MapNodeType.Station, true, ["INTERSECTION"]),
        new("C", 10, 0, MapNodeType.Station, true, ["FINAL"])
    ],
    [
        new("AB", "A", "B", 5, 1, false, true, ["CORRIDOR"]),
        new("BC", "B", "C", 10, 1, false, true, ["INTERSECTION"])
    ], resources);
var runtime = new ActiveMapRuntime();
runtime.Replace(new("MAP", "V1", map, now));

var payload = JsonSerializer.Serialize(new AgvTransportCommandPayload(
    "STEP", "AGV", "A", "C", ["A", "B", "C"], null),
    new JsonSerializerOptions(JsonSerializerDefaults.Web));
var request = new TrafficRequestEntity
{
    StepId = "STEP",
    TaskId = "TASK",
    VehicleId = "AGV",
    Source = "A",
    Destination = "C",
    EdgeIds = "[\"AB\",\"BC\"]",
    ResourceIds = "[\"CORRIDOR\",\"FINAL\",\"INTERSECTION\"]",
    State = "Held",
    MapId = "MAP",
    MapVersionId = "V1"
};
var snapshotJson = TransportRouteSnapshots.Serialize(TransportRouteSnapshots.Capture(
    "TASK", "STEP", "CMD", "AGV", payload, runtime.Current!, request));
var revision = TransportRouteSnapshots.Hash(snapshotJson);

database.Insertable(new DispatchTaskEntity
{
    TaskId = "TASK",
    StationId = "CNC-001",
    VehicleId = "AGV",
    Stage = (int)DispatchTaskStage.PickingUp,
    CreatedAt = DateTime.Now,
    UpdatedAt = DateTime.Now
}).ExecuteCommand();
database.Insertable(new DispatchTaskStepEntity
{
    StepId = "STEP",
    TaskId = "TASK",
    Sequence = 1,
    StepType = (int)DispatchTaskStepType.GoToPickup,
    Status = (int)DispatchTaskStepStatus.Completed,
    SourceSite = "A",
    DestinationSite = "C",
    VehicleId = "AGV",
    VendorTaskId = "STEP",
    CreatedAt = DateTime.Now,
    UpdatedAt = DateTime.Now,
    CompletedAt = DateTime.Now
}).ExecuteCommand();
database.Insertable(new AgvCommandOutboxEntity
{
    CommandId = "CMD",
    IdempotencyKey = "STEP:STEP:TRANSPORT",
    TaskId = "TASK",
    StepId = "STEP",
    VehicleId = "AGV",
    VendorCommandId = "STEP",
    CommandType = (int)AgvCommandType.Transport,
    Status = (int)AgvCommandStatus.Sent,
    PayloadJson = payload,
    ExecutionRouteSnapshotJson = snapshotJson,
    ExecutionRouteSnapshotHash = revision,
    ExecutionMapId = "MAP",
    ExecutionMapVersionId = "V1",
    SentAt = now.AddSeconds(-10).LocalDateTime,
    CreatedAt = now.AddSeconds(-10).LocalDateTime,
    UpdatedAt = now.AddSeconds(-10).LocalDateTime
}).ExecuteCommand();
database.Insertable(request).ExecuteCommand();
foreach (var (leaseId, resourceId) in new[]
{
    ("LEASE-CORRIDOR", "CORRIDOR"),
    ("LEASE-INTERSECTION", "INTERSECTION"),
    ("LEASE-FINAL", "FINAL")
})
{
    database.Insertable(new TrafficResourceLeaseEntity
    {
        LeaseId = leaseId,
        ResourceId = resourceId,
        OwnerStepId = "STEP",
        TaskId = "TASK",
        VehicleId = "AGV",
        MapId = "MAP",
        MapVersionId = "V1",
        AcquiredAt = now.AddSeconds(-10).LocalDateTime,
        RenewedAt = now.AddSeconds(-1).LocalDateTime,
        ExpiresAt = now.AddMinutes(2).LocalDateTime
    }).ExecuteCommand();
}

var vehicle = new AgvRobotSnapshot("AGV", "AGV", "127.0.0.1", 19204,
    ConnectionState.Online, SignalQuality.Good, 10, 0, 0, .95, "C", "C",
    80, false, false, "Idle", null, null, null, [], [], now,
    now, 0, null, null, 1);
var vehicles = new Vehicles(vehicle);
var provider = new BodyProvider(new("CALIBRATED", "AGV", "LocalCartesian",
    .49, true, true));
var options = new HandshakeExecutionOptions
{
    Enabled = true,
    DumperExit = new()
    {
        BodyProfiles =
        [
            new DumperExitBodyProfile("CALIBRATED", "AGV", .49, .1, true, true)
        ]
    }
};
var manager = new TrafficResourceManager(database,
    new TrafficResourceCatalog(resources), new(TimeSpan.FromMinutes(2)));
var service = new CompletedTransportRouteReleaseService(database, runtime, vehicles,
    provider, options, new MapOptions { MinimumLocalizationConfidence = .5 },
    new TrafficSegmentReleasePolicy { Enabled = true }, manager);

var released = await service.ReleaseAsync("TASK", "STEP", default);
Check(released.ReleasedCount == 2, "完成到站后释放两个已走过资源");
Check(!database.Queryable<TrafficResourceLeaseEntity>().Any(item =>
    item.LeaseId == "LEASE-CORRIDOR" || item.LeaseId == "LEASE-INTERSECTION"),
    "前序路段物理租约已删除");
Check(database.Queryable<TrafficResourceLeaseEntity>().Any(item =>
    item.LeaseId == "LEASE-FINAL"), "终点资源继续保留");
request = database.Queryable<TrafficRequestEntity>().Single();
Check(request.ResourceIds == "[\"FINAL\"]" && request.State == "Held",
    "路权查询只显示剩余终点资源");
var replay = await service.ReleaseAsync("TASK", "STEP", default);
Check(replay.ReleasedCount == 0 &&
    database.Queryable<TrafficResourceLeaseEntity>().Count() == 1,
    "重复扫描幂等且不删除终点");

Console.WriteLine($"PASS {checks} completed-route release checks. Database: {root}");

sealed class Vehicles(AgvRobotSnapshot snapshot) : IAgvStateCache
{
    public event Action<AgvRobotStateChanged>? StateChanged;
    public long Version => snapshot.Version;
    public IReadOnlyList<AgvRobotSnapshot> GetAll() => [snapshot];
    public bool TryGet(string vehicleId, out AgvRobotSnapshot value)
    {
        value = snapshot;
        return vehicleId == snapshot.VehicleId;
    }
}

sealed class BodyProvider(TrafficBodyEnvelope body) : ITrafficBodyEnvelopeProvider
{
    public Task<TrafficBodyEnvelope?> ReadAsync(string vehicleId, CancellationToken ct) =>
        Task.FromResult<TrafficBodyEnvelope?>(body.VehicleId == vehicleId ? body : null);
}
