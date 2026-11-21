using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Service;

var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS " + label);
}

var now = DateTimeOffset.UtcNow;
var root = Path.Combine(Path.GetTempPath(), "AGV-progress-reader-" + Guid.NewGuid().ToString("N"));
var db = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = Path.Combine(root, "test.db") });
await new DatabaseInitializer(db).InitializeAsync(default);
var map = new MapDefinition("M", "test", "LocalCartesian", "m",
    [
        new("A", 0, 0, MapNodeType.Waypoint, true, []) { AgvSiteId = "P1" },
        new("B", 2, 0, MapNodeType.Waypoint, true, []) { AgvSiteId = "P2" },
        new("C", 4, 0, MapNodeType.Waypoint, true, []) { AgvSiteId = "P3" },
        new("D", 6, 0, MapNodeType.Station, true, []) { AgvSiteId = "P4" }
    ],
    [
        new("AB", "A", "B", 2, 1, true, true, ["PASSED"]),
        new("BC", "B", "C", 2, 1, true, true, []),
        new("CD", "C", "D", 2, 1, true, true, [])
    ],
    [new("PASSED", TrafficResourceType.NarrowAisle, 1,
        [new(0, -1), new(1, -1), new(1, 1), new(0, 1)])]);
var runtime = new ActiveMapRuntime();
runtime.Replace(new("M", "V1", map, now));
var payload = JsonSerializer.Serialize(new AgvTransportCommandPayload(
    "STEP", "AGV", "P1", "P4", ["P1", "P2", "P3", "P4"], null),
    new JsonSerializerOptions(JsonSerializerDefaults.Web));
var request = new TrafficRequestEntity
{
    StepId = "STEP", TaskId = "TASK", VehicleId = "AGV", Source = "A", Destination = "D",
    EdgeIds = "[\"AB\",\"BC\",\"CD\"]", ResourceIds = "[]", MapId = "M", MapVersionId = "V1",
    State = "Held"
};
var route = TransportRouteSnapshots.Capture("TASK", "STEP", "CMD", "AGV", payload,
    new("M", "V1", map, now), request);
var routeJson = TransportRouteSnapshots.Serialize(route);
var routeHash = TransportRouteSnapshots.Hash(routeJson);
db.Insertable(new DispatchTaskEntity
{
    TaskId = "TASK", StationId = "CNC-001", VehicleId = "AGV", VendorTaskId = "VENDOR",
    Stage = (int)DispatchTaskStage.GoingToPickup
}).ExecuteCommand();
db.Insertable(new DispatchTaskStepEntity
{
    StepId = "STEP", TaskId = "TASK", StepType = (int)DispatchTaskStepType.GoToPickup,
    Status = (int)DispatchTaskStepStatus.Executing, VehicleId = "AGV", VendorTaskId = "VENDOR",
    SourceSite = "P1", DestinationSite = "P4"
}).ExecuteCommand();
db.Insertable(new AgvCommandOutboxEntity
{
    CommandId = "CMD", IdempotencyKey = "TRANSPORT", TaskId = "TASK", StepId = "STEP",
    VehicleId = "AGV", VendorCommandId = "VENDOR", CommandType = (int)AgvCommandType.Transport,
    Status = (int)AgvCommandStatus.Sent, PayloadJson = payload,
    ExecutionRouteSnapshotJson = routeJson, ExecutionRouteSnapshotHash = routeHash,
    ExecutionMapId = "M", ExecutionMapVersionId = "V1", SentAt = now.AddSeconds(-3).LocalDateTime
}).ExecuteCommand();

var clock = new FixedClock(now);
var cache = new AgvStateCache(new AgvOptions
{
    Enabled = true,
    StaleAfterMs = 5000,
    Robots =
    [
        new() { Enabled = true, VehicleId = "AGV", DisplayName = "AGV", IpAddress = "127.0.0.1" },
        new() { Enabled = true, VehicleId = "AGV-WINDOW", DisplayName = "AGV-WINDOW", IpAddress = "127.0.0.1" }
    ]
}, clock);
var handshake = new HandshakeExecutionOptions
{
    DumperExit = new()
    {
        BodyProfiles =
        [
            new("AGV-FULL-LOAD", "AGV", 0.49, 0.1, true, true),
            new("AGV-WINDOW-FULL-LOAD", "AGV-WINDOW", 0.49, 0.1, true, true)
        ]
    }
};
var options = new TrafficSegmentReleaseWorkerOptions { Enabled = true, FrameWaitSeconds = 2 };
var reader = new AgvTrafficProgressReader(db, cache, handshake, options, clock);
ApplyTelemetry(now.AddMilliseconds(-400), 2.5, 0.4);
var pending = reader.ReadAsync("AGV", default);
await Task.Delay(100);
clock.UtcNow = now;
ApplyTelemetry(now.AddMilliseconds(-100), 2.8, 0.5);
var pair = await pending.WaitAsync(TimeSpan.FromSeconds(3));
Check(pair is not null, "取得两帧权威进度");
Check(pair!.First.LastFullyTraversedEdgeIndex == 0 && pair.Second.LastFullyTraversedEdgeIndex == 0,
    "当前执行第二段时只把第一段声明为完全通过");
Check(pair.Second.Sequence > pair.First.Sequence && pair.Second.ObservedAt > pair.First.ObservedAt,
    "两帧序号和采样时间严格递增");
Check(pair.First.TaskId == "TASK" && pair.First.StepId == "STEP" && pair.First.VendorTaskId == "VENDOR" &&
      pair.First.CommandId == "CMD" && pair.First.RouteRevision == routeHash,
    "进度帧绑定原任务、步骤、厂家任务、命令与路线快照");
Check(Math.Abs(pair.First.PositionUncertaintyMeters - 0.1) < 1e-9,
    "定位误差采用已核定最大载荷包络中的保守误差界");

clock.UtcNow = now.AddSeconds(1);
ApplyTelemetry(now, 4.4, 0.2, "P3", "P4", "VENDOR");
var nextPending = reader.ReadAsync("AGV", default);
await Task.Delay(100);
clock.UtcNow = now.AddSeconds(1.3);
ApplyTelemetry(now.AddMilliseconds(300), 4.7, 0.35, "P3", "P4", "VENDOR");
var next = await nextPending.WaitAsync(TimeSpan.FromSeconds(3));
Check(next is not null && next.First.LastFullyTraversedEdgeIndex == 1 &&
      next.Second.LastFullyTraversedEdgeIndex == 1,
    "车辆进入第三段后只新增第二段为完全通过，无需等待整单完成");

var windows = new[]
{
    new AgvTransportRouteWindow(0, "P1", "P2", ["P1", "P2"], ["AB"], [], false),
    new AgvTransportRouteWindow(1, "P2", "P4", ["P2", "P3", "P4"], ["BC", "CD"], [], true)
};
var earlierWindowPayload = JsonSerializer.Serialize(new AgvTransportCommandPayload(
    "WINDOW-V-01", "AGV-WINDOW", "P1", "P2", ["P1", "P2"], null)
{
    BusinessStepId = "WINDOW-STEP", WindowIndex = 0, WindowCount = 2, Windows = windows
}, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var currentWindowPayload = JsonSerializer.Serialize(new AgvTransportCommandPayload(
    "WINDOW-V-02", "AGV-WINDOW", "P2", "P4", ["P2", "P3", "P4"], null)
{
    BusinessStepId = "WINDOW-STEP", WindowIndex = 1, WindowCount = 2, Windows = windows
}, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var currentWindowRequest = new TrafficRequestEntity
{
    StepId = "WINDOW-STEP", TaskId = "WINDOW-TASK", VehicleId = "AGV-WINDOW",
    Source = "P2", Destination = "P4", EdgeIds = "[\"BC\",\"CD\"]", ResourceIds = "[\"PASSED\"]",
    MapId = "M", MapVersionId = "V1", State = "Held"
};
var currentWindowRoute = TransportRouteSnapshots.Capture("WINDOW-TASK", "WINDOW-STEP",
    "WINDOW-CMD", "AGV-WINDOW", currentWindowPayload, new("M", "V1", map, now),
    currentWindowRequest);
var currentWindowJson = TransportRouteSnapshots.Serialize(currentWindowRoute);
db.Insertable(new DispatchTaskEntity
{
    TaskId = "WINDOW-TASK", StationId = "CNC-002", VehicleId = "AGV-WINDOW",
    VendorTaskId = "WINDOW-V-02", Stage = (int)DispatchTaskStage.GoingToPickup
}).ExecuteCommand();
db.Insertable(new DispatchTaskStepEntity
{
    StepId = "WINDOW-STEP", TaskId = "WINDOW-TASK", StepType = (int)DispatchTaskStepType.GoToPickup,
    Status = (int)DispatchTaskStepStatus.Executing, VehicleId = "AGV-WINDOW",
    VendorTaskId = "WINDOW-V-02", SourceSite = "P1", DestinationSite = "P4"
}).ExecuteCommand();
db.Insertable(new[]
{
    new AgvCommandOutboxEntity
    {
        CommandId = "WINDOW-CMD-OLD", IdempotencyKey = "WINDOW-OLD", TaskId = "WINDOW-TASK",
        StepId = "WINDOW-STEP", VehicleId = "AGV-WINDOW", VendorCommandId = "WINDOW-V-01",
        CommandType = (int)AgvCommandType.Transport, Status = (int)AgvCommandStatus.Sent,
        PayloadJson = earlierWindowPayload, ExecutionMapId = "M", ExecutionMapVersionId = "V1",
        SentAt = now.AddSeconds(-6).LocalDateTime
    },
    new AgvCommandOutboxEntity
    {
        CommandId = "WINDOW-CMD", IdempotencyKey = "WINDOW-NEW", TaskId = "WINDOW-TASK",
        StepId = "WINDOW-STEP", VehicleId = "AGV-WINDOW", VendorCommandId = "WINDOW-V-02",
        CommandType = (int)AgvCommandType.Transport, Status = (int)AgvCommandStatus.Sent,
        PayloadJson = currentWindowPayload, ExecutionRouteSnapshotJson = currentWindowJson,
        ExecutionRouteSnapshotHash = TransportRouteSnapshots.Hash(currentWindowJson),
        ExecutionMapId = "M", ExecutionMapVersionId = "V1", SentAt = now.AddSeconds(-3).LocalDateTime
    }
}).ExecuteCommand();
db.Insertable(currentWindowRequest).ExecuteCommand();
db.Insertable(new TrafficResourceLeaseEntity
{
    LeaseId = "PASSED-LEASE", ResourceId = "PASSED", OwnerStepId = "WINDOW-STEP",
    TaskId = "WINDOW-TASK", VehicleId = "AGV-WINDOW", MapId = "M", MapVersionId = "V1",
    AcquiredAt = now.AddSeconds(-6).LocalDateTime
}).ExecuteCommand();
clock.UtcNow = now.AddSeconds(2);
ApplyWindowTelemetry(now.AddMilliseconds(900), 2.3);
var windowPending = reader.ReadAsync("AGV-WINDOW", default);
await Task.Delay(100);
clock.UtcNow = now.AddSeconds(2.3);
ApplyWindowTelemetry(now.AddMilliseconds(1200), 2.7);
var windowPair = await windowPending.WaitAsync(TimeSpan.FromSeconds(3));
Check(windowPair is not null && windowPair.First.LastFullyTraversedEdgeIndex == 0 &&
      windowPair.Second.LastFullyTraversedEdgeIndex == 0,
    "第二窗口命令只按当前厂家任务累计整单路段进度");

var envelopeProvider = new ConfiguredTrafficBodyEnvelopeProvider(handshake, runtime);
var envelope = await envelopeProvider.ReadAsync("AGV", default);
Check(envelope is { CalibrationVerified: true, IncludesMaximumPermittedLoad: true } &&
      envelope.VehicleId == "AGV" && Math.Abs(envelope.RadiusFromReportedReferenceMeters - 0.49) < 1e-9,
    "整车包络唯一绑定车辆并包含额定最大载荷");

var executionGate = new MapExecutionGate(new OpenRecoveryBarrier());
executionGate.SetMode(MapExecutionMode.Running);
var inspector = new TrafficSegmentReleaseInspector(db, runtime, executionGate,
    new TrafficSegmentReleasePolicy { Enabled = true },
    new RecordedProgress(windowPair), envelopeProvider, clock);
var assessment = await inspector.InspectAsync("WINDOW-STEP", default);
Check(assessment.Available && assessment.Candidates.Any(candidate =>
        candidate.ResourceId == "PASSED" && candidate.EvidenceVerified),
    "当前窗口仍在行驶时，核验通过的旧路段资源可以释放：" + assessment.Reason);

db.Updateable<DispatchTaskStepEntity>().Where(step => step.StepId == "STEP")
    .SetColumns(step => step.VendorTaskId == "OTHER").ExecuteCommand();
options.FrameWaitSeconds = 0;
clock.UtcNow = now.AddSeconds(3);
Check(await reader.ReadAsync("AGV", default) is null,
    "原步骤厂家编号不一致时不制造进度证据");

File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "progress-reader-result.json"),
    JsonSerializer.Serialize(new { Checks = checks, Scope = "synthetic SQLite, cache telemetry and commissioned body profile" },
        new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS {checks} production progress-reader checks.");

void ApplyTelemetry(DateTimeOffset observedAt, double x, double percentage,
    string source = "P2", string target = "P3", string runningTask = "VENDOR-02")
{
    cache.ApplyTelemetry("AGV", new AgvRobotTelemetry(
        x, 0, 0, 0.98, null, source, 80, false, false, "2", source, target, percentage,
        [
            new("VENDOR-01", 4, null),
            new("VENDOR-02", runningTask == "VENDOR-02" ? 2 : 4, null),
            new("VENDOR", runningTask == "VENDOR" ? 2 : 1, null)
        ], [], observedAt), TimeSpan.FromMilliseconds(20));
}

void ApplyWindowTelemetry(DateTimeOffset observedAt, double x)
{
    cache.ApplyTelemetry("AGV-WINDOW", new AgvRobotTelemetry(
        x, 0, 0, 0.98, null, "P2", 80, false, false, "2", "P2", "P3", 0.4,
        [new("WINDOW-V-02-01", 2, null)], [], observedAt), TimeSpan.FromMilliseconds(20));
}

sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

sealed class OpenRecoveryBarrier : IRecoveryBarrier
{
    public bool IsOpen => true;
}

sealed class RecordedProgress(TrafficProgressPair? pair) : IAuthoritativeTrafficProgressReader
{
    public Task<TrafficProgressPair?> ReadAsync(string vehicleId, CancellationToken ct)
        => Task.FromResult(pair);
}
