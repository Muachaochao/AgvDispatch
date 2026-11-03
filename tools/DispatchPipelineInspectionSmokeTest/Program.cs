using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

var checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
var now = DateTimeOffset.UtcNow;
using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(Path.GetTempPath(), "agv-pipeline-inspection-" + Guid.NewGuid().ToString("N"), "isolated.db") });
var catalog = new TrafficResourceCatalog([new("R", TrafficResourceType.NarrowAisle)]);
await new DatabaseInitializer(db, catalog).InitializeAsync(default);
var monitor = new DispatchOptimizationService(Path.Combine(Path.GetTempPath(), "agv-inspection-policy-" + Guid.NewGuid().ToString("N"), "policy.json"), new(), NullLogger<DispatchOptimizationService>.Instance);
var robots = new Robots(); var stations = new Stations(); var plc = new Signals(); var gate = new Gate();
var dispatch = new DispatchOrchestratorOptions { Enabled = false }; var creation = new DispatchTaskCreationOptions { Enabled = false, AutomaticCreationEnabled = false };
var sender = new AgvCommandOutboxOptions { Enabled = false }; var map = new RuntimeMap();
var service = new DispatchPipelineInspectionService(db, monitor, robots, stations, plc, new TrafficResourceControlService(db, catalog), gate, map, new(), dispatch, creation, sender, new());

monitor.Publish(new() { Message = "任务编排未启用", DurationMs = 0 });
var empty = await service.GetAsync(default);
Check(empty.Layers.Select(l => l.Key).SequenceEqual(new[] { "queue", "candidates", "matching", "traffic", "safety", "execution" }), "inspection returns all six ordered layers even while execution is disabled");
Check(empty.CycleNumber == 1 && empty.OptimizationCapturedAt is null, "gate-only cycle is not mislabeled as an optimization computation");
Check(empty.Gates.Single(g => g.Key == "dispatch").Enabled == false && empty.Gates.Single(g => g.Key == "outbox").Enabled == false && empty.Layers.Where(l => l.Key != "traffic").All(l => l.State == "Disabled"), "explicit disabled configuration is reported without enabling actions");
Check(empty.Layers.Single(l => l.Key == "safety").Checks.Single(c => c.Key == "last-safety").State == "NotChecked", "no prior safety execution remains explicitly unreviewed");
Check(empty.Layers.Single(l => l.Key == "safety").Checks.Single(c => c.Key == "pre-send").ObservedAt is null && gate.Entries == 0, "monitoring never enters actionful pre-send checks or execution operation leases");
Check(empty.Layers.Single(l => l.Key == "candidates").Checks.Single(c => c.Key == "vehicle-fresh").State == "Unknown", "empty vehicle cache is not treated as valid telemetry");
Check(empty.Layers.Single(l => l.Key == "traffic").Checks.Single(c => c.Key == "segment-release-observation").State == "Disabled" &&
      empty.Layers.Single(l => l.Key == "traffic").Checks.Single(c => c.Key == "segment-release-commit").State == "Disabled",
    "pass-through observation and commit switches are independently visible");

var taskRows = Enumerable.Range(0, 125).Select(i => new DispatchTaskEntity { TaskId = "T" + i, StationId = "S" + i, Stage = 10, Priority = i, VehicleId = i == 0 ? "V-OWNED" : i == 124 ? "V-OUTSIDE" : null,
    CreatedAt = now.AddMinutes(-20 - i).LocalDateTime, UpdatedAt = now.AddMinutes(-i).LocalDateTime }).ToList();
db.Insertable(taskRows).ExecuteCommand();
db.Insertable(Enumerable.Range(0, 125).Select(i => new DispatchTaskStepEntity { StepId = "STEP" + i, TaskId = "T" + i, Sequence = 1, StepType = 10, Status = 10, CreatedAt = now.LocalDateTime, UpdatedAt = now.LocalDateTime }).ToList()).ExecuteCommand();
db.Insertable(new AgvCommandOutboxEntity { CommandId = "FUTURE", IdempotencyKey = "STEP0", TaskId = "T0", StepId = "STEP0", VehicleId = "V-OWNED", Status = 20, CommandType = 10, PayloadJson = "{}", AttemptCount = 3,
    NextAttemptAt = now.AddHours(1).LocalDateTime, CreatedAt = now.AddMinutes(-10).LocalDateTime, UpdatedAt = now.LocalDateTime, LastError = "等待原编号核对", ExecutionMapId = "M", ExecutionMapVersionId = "V" }).ExecuteCommand();
db.Insertable(Enumerable.Range(0, 25).Select(i => new AgvCommandOutboxEntity { CommandId = "SENT" + i, IdempotencyKey = "HISTORY" + i, TaskId = "OLD" + i, StepId = "OLDSTEP" + i, VehicleId = "OLD-CAR", Status = 30, CommandType = 10,
    PayloadJson = "{}", AttemptCount = 1, NextAttemptAt = now.LocalDateTime, CreatedAt = now.AddDays(-2).LocalDateTime, UpdatedAt = now.AddMinutes(-i).LocalDateTime, SentAt = now.AddMinutes(-i).LocalDateTime, VendorCommandId = "VENDOR" + i }).ToList()).ExecuteCommand();
db.Insertable(new TrafficResourceLeaseEntity { LeaseId = "LEASE", ResourceId = "R", TaskId = "T0", OwnerStepId = "STEP0", VehicleId = "V-OWNED", AcquiredAt = now.AddMinutes(-10).LocalDateTime, RenewedAt = now.AddMinutes(-5).LocalDateTime, ExpiresAt = now.AddMinutes(-4).LocalDateTime }).ExecuteCommand();
robots.Items.Add(Robot("V-OWNED", now));
robots.Items.Add(Robot("V-OUTSIDE", now));
robots.Items.Add(Robot("V-STALE", now.AddMinutes(-1)));
robots.Items.Add(Robot("V-UNKNOWN", null));
stations.Items.Add(new("S0", StationOperationalState.FullBoxWaitingForPickup, true, null, true, true, false, true, SignalQuality.Good, "T0", 1, now.AddHours(-5), now.AddHours(-5), 1));
plc.Items.Add(new("S0", "FullBoxRequested", "满箱呼叫", "SYNTHETIC-D0", "bool", "Input", "Read", true, true, SignalQuality.Good, now, now.AddHours(-5), 1));
plc.Items.Add(new("S0", "Faulted", "工位故障", "SYNTHETIC-D1", "bool", "Input", "Read", true, null, SignalQuality.Unknown, now, now, 2));
plc.Items.Add(new("S0", "BoxPresent", "箱体到位", "SYNTHETIC-D2", "bool", "Input", "Read", true, true, SignalQuality.Good, now.AddMinutes(-5), now.AddMinutes(-5), 3));
string Stored() => JsonSerializer.Serialize(new
{
    Tasks = db.Queryable<DispatchTaskEntity>().OrderBy(t => t.TaskId).ToList(),
    Steps = db.Queryable<DispatchTaskStepEntity>().OrderBy(t => t.StepId).ToList(),
    Commands = db.Queryable<AgvCommandOutboxEntity>().OrderBy(t => t.CommandId).ToList(),
    Leases = db.Queryable<TrafficResourceLeaseEntity>().OrderBy(t => t.LeaseId).ToList(),
    Requests = db.Queryable<TrafficRequestEntity>().OrderBy(t => t.StepId).ToList(),
    Events = db.Queryable<TrafficLifecycleEventEntity>().OrderBy(t => t.EventId).ToList()
});
var before = Stored();
var observed = await service.GetAsync(default);
Check(Stored() == before && gate.Entries == 0, "all database rows, attempts, leases and traffic events remain byte-equivalent after inspection");
var queue = observed.Layers.Single(l => l.Key == "queue");
Check(queue.TotalCount == 126 && queue.Records.Count == 100 && queue.Truncated, "queue combines exact persistent call count with station samples and explicitly bounds returned records");
Check(queue.Records[0].Facts.Any(f => f.Label == "基础优先级" && f.Value == "124") && queue.Records[0].TaskId == "T124", "persistent queue ordering retains real priority and original task identity");
var candidate = observed.Layers.Single(l => l.Key == "candidates");
Check(candidate.Records.Single(r => r.VehicleId == "V-OUTSIDE").State == "Occupied", "all distinct persistent vehicle owners are retained independently of the bounded task sample");
Check(candidate.Records.Single(r => r.VehicleId == "V-STALE").State == "Stale" && candidate.Records.Single(r => r.VehicleId == "V-UNKNOWN").State == "Stale", "old and never-observed telemetry are not presented as successful current candidates");
Check(candidate.Checks.Single(c => c.Key == "vehicle-fresh").Actual == "2 / 4 台" && candidate.Checks.Single(c => c.Key == "vehicle-fresh").State != "Pass", "cache presence is distinguished from complete fresh successful sampling");
var execution = observed.Layers.Single(l => l.Key == "execution");
var future = execution.Records.Single(r => r.Id == "command:FUTURE");
Check(future.State == "RetryScheduled" && future.Facts.Any(f => f.Label == "发送尝试" && f.Value == "3") && future.Facts.Any(f => f.Label == "下次尝试" && f.Value != "尚无记录"), "future retry commands stay visible with original attempt and retry time even when not due");
Check(execution.Records.Count(r => r.State == "Sent") == 20 && execution.Records.Where(r => r.State == "Sent").All(r => r.Title.Contains("历史")), "recent sent commands are bounded and clearly labeled historical rather than current execution");
Check(execution.TotalCount == 146 && execution.Truncated && execution.Records.Count == 100, "execution exact total includes all 125 unfinished-task steps, not just a 100-task sample");
var traffic = observed.Layers.Single(l => l.Key == "traffic");
Check(traffic.Records.Single(r => r.Id == "lease:LEASE") is { State: "Retained" } lease && lease.Facts.Any(f => f.Label == "占用步骤" && f.Value == "STEP0"), "expired heartbeat remains visible with original owner step and is never released by monitoring");
var segmentMonitor = new TrafficSegmentReleaseMonitor();
segmentMonitor.Update(new("T0", "STEP0", "V-OWNED", "Held", "车辆完整包络仍覆盖 LM11，继续占用。", now,
    [new("LEASE", "LM11", false, "完整车体/载荷与定位误差/安全裕量仍覆盖资源。")],
    new(2, 2, 3, 0, 3.2, 0, 0.1, 0.1, "AGV-FULL-LOAD", 0.49, now, now)));
var releaseService = new DispatchPipelineInspectionService(db, monitor, robots, stations, plc,
    new TrafficResourceControlService(db, catalog), gate, map, new(), dispatch, creation, sender, new(),
    new TrafficSegmentReleasePolicy { Enabled = true }, new TrafficSegmentCommitOptions { Enabled = true },
    segmentMonitor);
var releaseView = await releaseService.GetAsync(default);
var releaseTraffic = releaseView.Layers.Single(l => l.Key == "traffic");
Check(releaseTraffic.Records.Single(r => r.Id == "segment-release:STEP0") is { State: "Held" } observation &&
      observation.Facts.Any(f => f.Label == "继续占用" && f.Value.Contains("LM11")) &&
      observation.Facts.Any(f => f.Label == "两帧路段进度" && f.Value == "2 → 2"),
    "traffic layer shows held resource, two-frame route progress, and envelope conclusion without releasing it");
var safety = observed.Layers.Single(l => l.Key == "safety");
Check(safety.Records.Single(r => r.Id == "plc:S0:FullBoxRequested").State == "Observed" && safety.Records.Single(r => r.Id == "plc:S0:Faulted").State == "Unknown" && safety.Records.Single(r => r.Id == "plc:S0:BoxPresent").State == "Stale", "PLC unknown quality and old samples remain distinct from fresh observed values");
Check(safety.Checks.Single(c => c.Key == "pre-send").State == "NotChecked" && safety.Records.All(r => r.State != "Pass"), "observed PLC values do not fabricate task-stage send permission");

dispatch.Enabled = true; sender.Enabled = true;
gate.CurrentMode = MapExecutionMode.Held;
var held = await service.GetAsync(default);
Check(held.Layers.Single(l => l.Key == "safety").State == "Blocked" && held.Layers.Single(l => l.Key == "safety").ObservedAt > now &&
    held.Layers.Single(l => l.Key == "safety").Checks.Single(c => c.Key == "last-safety").State == "NotChecked", "current held execution gate has a fresh blocked layer while missing historical safety review remains unreviewed");
gate.CurrentMode = MapExecutionMode.Running;
monitor.ReportLayer("safety", "Blocked", "实际安全复核：测试阻止");
monitor.Publish(new() { CapturedAt = now.AddMinutes(-1), DurationMs = 4.2, Tasks = [new("T0", "S0", now.AddMinutes(-20), 50, 70, 20, "V-OWNED", null, null, "最近周期推荐，尚未登记", [])] });
var computed = await service.GetAsync(default);
Check(computed.OptimizationCapturedAt == now.AddMinutes(-1), "real computation retains its own original capture time instead of the new inspection time");
Check(computed.Layers.Single(l => l.Key == "matching").Records.Single().UpdatedAt == now.AddMinutes(-1), "matching row time is the original computation time");
Check(computed.Layers.Single(l => l.Key == "safety").State == "Blocked" && computed.Layers.Single(l => l.Key == "safety").Checks.Single(c => c.Key == "last-safety").State == "Fail", "current actual blocked safety observation remains blocked in the inspection");
Check(computed.Layers.Single(l => l.Key == "safety").Checks.Single(c => c.Key == "execution-map").State == "Fail", "missing runtime map is a failed prerequisite rather than assumed readiness");
Check(Stored() == before && gate.Entries == 0, "inspection after mode changes still preserves all durable work and consumes no attempts");
Check(queue.Records.Any(r => r.Id == "station:S0" && r.UpdatedAt == now.AddHours(-5)) && observed.Layers.Single(l => l.Key == "safety").Records.Single(r => r.Id == "plc:S0:FullBoxRequested").UpdatedAt == now,
    "large call queues still show station samples, while old business evaluations and fresh PLC sampling retain distinct times");
Console.WriteLine($"Dispatch pipeline inspection: {checks} checks passed; synthetic caches and isolated SQLite, no AGV/PLC/network actions.");

static AgvRobotSnapshot Robot(string id, DateTimeOffset? poll) => new(id, id, "127.0.0.1", 0, ConnectionState.Online, SignalQuality.Good, 1, 2, 0, 1, "A", "A", 80, false, false, "Idle", null, null, null, [], [], poll, DateTimeOffset.UtcNow, 0, 0, null, 1);
sealed class Robots : IAgvStateCache
{
    public List<AgvRobotSnapshot> Items = [];
    public event Action<AgvRobotStateChanged>? StateChanged { add { } remove { } }
    public long Version => 1;
    public IReadOnlyList<AgvRobotSnapshot> GetAll() => Items;
    public bool TryGet(string id, out AgvRobotSnapshot state) { state = Items.FirstOrDefault(r => r.VehicleId == id)!; return state is not null; }
}
sealed class Stations : IStationStateService
{
    public List<StationStateSnapshot> Items = [];
    public event Action<StationStateChanged>? StationStateChanged { add { } remove { } }
    public long Version => 1;
    public IReadOnlyList<StationStateSnapshot> GetAll() => Items;
    public bool TryGet(string id, out StationStateSnapshot state) { state = Items.FirstOrDefault(r => r.StationId == id)!; return state is not null; }
}
sealed class Signals : IPlcSignalCache
{
    public List<PlcSignalSnapshot> Items = [];
    public event Action<PlcSignalChanged>? SignalChanged { add { } remove { } }
    public long Version => 1;
    public IReadOnlyList<PlcSignalSnapshot> GetAll() => Items;
    public IReadOnlyList<PlcSignalSnapshot> GetStation(string id) => Items.Where(s => s.StationId == id).ToArray();
    public bool TryGet(string id, string variable, out PlcSignalSnapshot state) { state = Items.FirstOrDefault(r => r.StationId == id && r.VariableName == variable)!; return state is not null; }
}
sealed class Gate : IMapExecutionGate
{
    public int Entries;
    public MapExecutionMode CurrentMode = MapExecutionMode.Running;
    public MapExecutionMode Mode => CurrentMode;
    public bool CanStartNewWork => CurrentMode == MapExecutionMode.Running;
    public ValueTask<IDisposable?> EnterAsync(MapExecutionOperation operation, CancellationToken ct) { Entries++; throw new InvalidOperationException("Inspection must not take an operation lease"); }
    public ValueTask<IDisposable> EnterControlAsync(CancellationToken ct) { Entries++; throw new InvalidOperationException("Inspection must not take a control lease"); }
    public void SetMode(MapExecutionMode mode) { Entries++; throw new InvalidOperationException("Inspection must not change execution permission"); }
}
sealed class RuntimeMap : IActiveMapRuntime
{
    public ActiveMapRuntimeSnapshot? Current => null;
    public MapGraph? CurrentGraph => null;
    public void Replace(ActiveMapRuntimeSnapshot? map) => throw new InvalidOperationException("Inspection must not replace map");
}
