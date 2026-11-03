using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

var checks = 0;
void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; Console.WriteLine("PASS " + label); }
var now = DateTimeOffset.UtcNow;
var policy = new DispatchAssignmentOptions { WindowSize = 1 };

// Existing PLC ingress persists first, retries acknowledgement and deduplicates active stations.
{
    var directory = Path.Combine(Path.GetTempPath(), "agv-planning-layer-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "calls.db");
    using var db = DatabaseFactory.Create(new() { DatabasePath = path });
    var catalog = new TrafficResourceCatalog([new("R", TrafficResourceType.NarrowAisle)]);
    await new DatabaseInitializer(db, catalog).InitializeAsync(default);
    var tasks = new DispatchTaskRepository(db);
    var repository = new DispatchOrchestrationRepository(db);
    var traffic = new TrafficResourceManager(db, catalog, new(TimeSpan.FromMinutes(1)));
    var queue = new DispatchQueueWindowService(repository, traffic);
    var first = await tasks.TryCreateAsync("PLC-A", "DUMP", 10, default);
    var original = db.Queryable<DispatchTaskEntity>().Single(t => t.TaskId == first.Task.TaskId);
    var duplicate = await tasks.TryCreateAsync("PLC-A", "DUMP", 99, default);
    Check(!duplicate.Created && duplicate.Task.TaskId == first.Task.TaskId && db.Queryable<DispatchTaskEntity>().Count() == 1 &&
        (await tasks.GetStepsAsync(first.Task.TaskId, default)).Count == 9, "persistent ingress deduplicates repeated PLC call without duplicate task or steps");
    await tasks.RecordAcknowledgementFailureAsync(first.Task.TaskId, "PLC ACK temporarily unavailable", default);
    Check((await tasks.GetPendingAcknowledgementsAsync(default)).Single().TaskId == first.Task.TaskId &&
        (await queue.LoadAsync(policy, [], now, default)).Window.Count == 0,
        "unacknowledged call stays durable for retry and cannot enter dispatch window");
    await tasks.MarkAcknowledgedAsync(first.Task.TaskId, default);
    var afterAck = db.Queryable<DispatchTaskEntity>().Single(t => t.TaskId == first.Task.TaskId);
    var acknowledged = await queue.LoadAsync(policy, [], now, default);
    Check(afterAck.CreatedAt == original.CreatedAt && afterAck.Priority == 10 && acknowledged.Ready.Count == 1 &&
        acknowledged.Input.Single().CalledAt == first.Task.CreatedAt, "acknowledgement preserves original call time and base priority and readies first step");
    var uniqueRejected = false;
    try
    {
        db.Insertable(new DispatchTaskEntity { TaskId = "RACING-INSERT", StationId = "PLC-A", Priority = 99,
            Stage = (int)DispatchTaskStage.WaitingForVehicle, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommand();
    }
    catch { uniqueRejected = true; }
    Check(uniqueRejected && db.Queryable<DispatchTaskEntity>().Count() == 1, "database active-station unique index prevents duplicate racing insert");
    var b = await tasks.TryCreateAsync("PLC-B", "DUMP", 30, default);
    await tasks.MarkAcknowledgedAsync(b.Task.TaskId, default);
    db.Updateable<DispatchTaskEntity>().SetColumns(t => new DispatchTaskEntity { CreatedAt = now.AddMinutes(-25).LocalDateTime })
        .Where(t => t.TaskId == first.Task.TaskId).ExecuteCommand();
    var aged = await queue.LoadAsync(policy, [], now, default);
    Check(aged.Window.Single().TaskId == first.Task.TaskId && aged.Waiting.Length == 2 && aged.Work.Length == 2 &&
        aged.Ready.Count == 1 && db.Queryable<DispatchTaskEntity>().Count() == 2,
        "dynamic waiting score selects bounded window while preserving deferred persistent calls");
    var firstStep = (await tasks.GetStepsAsync(first.Task.TaskId, default))[0];
    await traffic.TryAcquireAsync(firstStep.StepId, first.Task.TaskId, "LEASED-VEHICLE", ["R"], default);
    var withLease = await queue.LoadAsync(policy, [], now, default);
    Check(withLease.Work.Single().TaskId == b.Task.TaskId && withLease.OccupiedVehicles.Contains("LEASED-VEHICLE") &&
        db.Queryable<TrafficResourceLeaseEntity>().Count() == 1, "queue layer freezes existing leased task and vehicle without releasing road rights");
    using var reopened = DatabaseFactory.Create(new() { DatabasePath = path });
    var restarted = await new DispatchQueueWindowService(new DispatchOrchestrationRepository(reopened),
        new TrafficResourceManager(reopened, catalog, new(TimeSpan.FromMinutes(1)))).LoadAsync(policy, [], now, default);
    Check(restarted.Waiting.Length == 2 && restarted.Work.Single().TaskId == b.Task.TaskId && restarted.OccupiedVehicles.Contains("LEASED-VEHICLE"),
        "new repository and queue service restore the same persisted waiting and fixed lease after restart");

    var pending = await tasks.TryCreateAsync("PLC-PENDING", "DUMP", 50, default);
    var monitor = new DispatchOptimizationService(Path.Combine(directory, "optimizer.json"), new(), NullLogger<DispatchOptimizationService>.Instance);
    var unobserved = await monitor.GetAsync(default);
    var keys = new[] { "queue", "candidates", "matching", "traffic", "safety", "execution" };
    Check(unobserved.Layers.Select(l => l.Key).SequenceEqual(keys) &&
        unobserved.Layers.Select(l => l.Number).SequenceEqual(Enumerable.Range(1, 6)), "runtime snapshot exposes the actual six layers in execution order");
    Check(unobserved.Layers.All(l => l.UpdatedAt == DateTimeOffset.MinValue && l.ItemCount is null),
        "unobserved layers retain explicit zero-time evidence instead of appearing recently executed");
    var outbox = new AgvCommandOutboxRepository(db);
    var gate = new MapExecutionGate(); gate.SetMode(MapExecutionMode.Running);
    var snapshots = new DispatchPipelineSnapshotService(monitor, repository, tasks, traffic, outbox,
        new() { Enabled = false }, new() { Enabled = false }, new() { Enabled = false }, gate);
    var visible = await snapshots.GetAsync(default);
    Check(visible.WaitingTaskCount == 2 && visible.Layers.Single(l => l.Key == "queue").ItemCount == 3 &&
        visible.Layers.Single(l => l.Key == "queue").Summary.Contains("待 PLC 应答 1"),
        "read-only dashboard counts real waiting and unacknowledged durable calls even while dispatch is disabled");
    Check(visible.Layers.Single(l => l.Key == "candidates").State == "Disabled" &&
        visible.Layers.Single(l => l.Key == "matching").State == "Disabled" &&
        visible.Layers.Single(l => l.Key == "execution").State == "Disabled",
        "disabled pipeline states are reported from actual service settings");
    Check(db.Queryable<DispatchTaskEntity>().Count() == 3 && db.Queryable<DispatchTaskStepEntity>().Count() == 27 &&
        db.Queryable<TrafficResourceLeaseEntity>().Count() == 1 && db.Queryable<AgvCommandOutboxEntity>().Count() == 0,
        "reading the six-layer dashboard creates no calls, leases, transport commands or duplicate steps");
    Check((await monitor.GetAsync(default)).Layers.All(l => l.UpdatedAt == DateTimeOffset.MinValue),
        "live dashboard observations do not fabricate stored pipeline execution reports");
    monitor.ReportLayer("safety", "Blocked", "PLC permission not confirmed", 1);
    monitor.Publish(new() { Message = "next optimizer cycle" });
    Check((await monitor.GetAsync(default)).Layers.Single(l => l.Key == "safety") is { State: "Blocked", Summary: "PLC permission not confirmed" },
        "publishing an optimizer cycle preserves previously blocked safety-layer evidence");

    await traffic.ReleaseTaskAsync(first.Task.TaskId, default);
    var reservations = new DispatchTrafficReservationService(traffic, monitor);
    var bStep = (await tasks.GetStepsAsync(b.Task.TaskId, default))[0];
    var grant = await reservations.AcquireAsync(new(bStep.StepId, b.Task.TaskId, "B-VEHICLE", "HOME", "PICK-B", ["E-B"], ["R"], false, "layer-four grant test")
        { MapId = "M", MapVersionId = "V1" }, default);
    Check(grant.Acquired && db.Queryable<TrafficResourceLeaseEntity>().Count() == 1 && db.Queryable<AgvCommandOutboxEntity>().Count() == 0 &&
        (await tasks.GetStepsAsync(b.Task.TaskId, default))[0].Status == DispatchTaskStepStatus.Ready,
        "real traffic layer grants a resource without preparing an Outbox command or advancing the business step");
    Check((await monitor.GetAsync(default)).Layers.Single(l => l.Key == "traffic").State == "Ready",
        "traffic layer reports an actual successful resource grant");
    Check(await traffic.CheckStepReservationAsync(bStep.StepId, b.Task.TaskId, "B-VEHICLE", "M", "V1", "HOME", "PICK-B", default) is null,
        "same-step task vehicle map and endpoints validate an actual matching grant");
    db.Updateable<TrafficResourceLeaseEntity>().SetColumns(l => new TrafficResourceLeaseEntity { OwnerStepId = "OTHER-TRANSPORT-STEP" })
        .Where(l => l.TaskId == b.Task.TaskId).ExecuteCommand();
    Check(await traffic.CheckStepReservationAsync(bStep.StepId, b.Task.TaskId, "B-VEHICLE", "M", "V1", "HOME", "PICK-B", default) is not null &&
        db.Queryable<TrafficResourceLeaseEntity>().Single().OwnerStepId == "OTHER-TRANSPORT-STEP",
        "another step's lease cannot prove current command rights even for identical task vehicle resource and map");
    db.Updateable<TrafficResourceLeaseEntity>().SetColumns(l => new TrafficResourceLeaseEntity { OwnerStepId = bStep.StepId })
        .Where(l => l.TaskId == b.Task.TaskId).ExecuteCommand();
    Check(await traffic.CheckStepReservationAsync(bStep.StepId, b.Task.TaskId, "B-VEHICLE", "M", "V1", "HOME", "PICK-B", default) is null &&
        db.Queryable<AgvCommandOutboxEntity>().Count() == 0,
        "restoring exact original step lease permits validation without generating a transport command");
    await tasks.MarkAcknowledgedAsync(pending.Task.TaskId, default);
    var pendingStep = (await tasks.GetStepsAsync(pending.Task.TaskId, default))[0];
    var waitObservation = new TrafficRouteObservation(pendingStep.StepId, pending.Task.TaskId, "WAIT-VEHICLE", "HOME", "PICK-PENDING", ["E-P"], ["R"], true, "layer-four capacity wait test")
        { MapId = "M", MapVersionId = "V1" };
    await reservations.ObserveWaitingAsync([waitObservation], default);
    var waitGrant = await reservations.AcquireAsync(waitObservation, default);
    Check(!waitGrant.Acquired && waitGrant.BlockingResourceId == "R" && db.Queryable<AgvCommandOutboxEntity>().Count() == 0 &&
        db.Queryable<TrafficResourceLeaseEntity>().Single().TaskId == b.Task.TaskId,
        "traffic-layer capacity conflict preserves earlier grant and returns waiting without generating transport commands");
    Check((await monitor.GetAsync(default)).Layers.Single(l => l.Key == "traffic").State == "Waiting" &&
        (await snapshots.GetAsync(default)).Layers.Single(l => l.Key == "traffic").State == "Waiting",
        "actual traffic wait remains visible through the read-only dashboard");
    var snapshotField = typeof(DispatchOptimizationService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var previousSnapshot = await monitor.GetAsync(default);
    var expiredTraffic = previousSnapshot with { Layers = previousSnapshot.Layers.Select(l => l.Key == "traffic"
        ? l with { State = "Idle", UpdatedAt = now.AddSeconds(-30) } : l).ToArray() };
    snapshotField.SetValue(monitor, expiredTraffic);
    var persistedTraffic = new TrafficResourceControlService(db, catalog);
    var observedSnapshots = new DispatchPipelineSnapshotService(monitor, repository, tasks, traffic, outbox,
        new() { Enabled = false }, new() { Enabled = false }, new() { Enabled = false }, gate, persistedTraffic);
    var historyCount = db.Queryable<TrafficLifecycleEventEntity>().Count();
    var observedTraffic = (await observedSnapshots.GetAsync(default)).Layers.Single(l => l.Key == "traffic");
    Check(observedTraffic.State == "Waiting" && observedTraffic.ItemCount == 1 && observedTraffic.Summary.Contains("等待 1 单") &&
        observedTraffic.Summary.Contains("1 个任务持有实际路权"),
        "expired traffic observation refreshes persisted waiting count and occupied-task count instead of displaying Idle");
    Check(db.Queryable<TrafficLifecycleEventEntity>().Count() == historyCount && db.Queryable<TrafficResourceLeaseEntity>().Count() == 1 &&
        db.Queryable<AgvCommandOutboxEntity>().Count() == 0 && (await monitor.GetAsync(default)).Layers.Single(l => l.Key == "traffic").State == "Idle",
        "expired traffic fallback is a read-only projection without mutating grants, history, commands or stored execution observations");
    await reservations.RollbackUncommittedAsync(bStep.StepId, default);
    var unownedWait = (await observedSnapshots.GetAsync(default)).Layers.Single(l => l.Key == "traffic");
    Check(unownedWait.State == "Waiting" && unownedWait.ItemCount == 1 && unownedWait.Summary.Contains("0 个任务持有实际路权"),
        "persisted ungranted request still displays Waiting after previous owner releases all actual leases");
}

// Layer two has read-only inputs and no command/lease mutation dependencies in its output path.
{
    var map = new ActiveMapRuntime();
    var nodes = new[] { ("HOME1", 0d), ("HOME2", 1d), ("HOME3", 2d), ("HOME4", 3d), ("PICK", 10d) }
        .Select(p => new MapNode(p.Item1, p.Item2, 0, MapNodeType.Station, true, [], p.Item1)).ToArray();
    var definition = new MapDefinition("candidate-layer", "", "LocalCartesian", "m", nodes,
        [new("TO-PICK", "HOME1", "PICK", 10, 2, false, true, ["R"]), new("RETURN", "PICK", "HOME1", 4, 1, false, true, [])],
        [new("R", TrafficResourceType.NarrowAisle)]);
    map.Replace(new("M", "V1", definition, now));
    var states = new AgvStateCache(new AgvOptions { Enabled = true,
        Robots = Enumerable.Range(1, 4).Select(i => new AgvRobotOptions { VehicleId = "V" + i }).ToList() });
    void Telemetry(string id, decimal battery = 75, bool emergency = false)
    {
        var index = int.Parse(id[1..]);
        states.ApplyTelemetry(id, new(index - 1, 0, 0, .99, "HOME" + index, null, battery, false, emergency, "Idle", null, null, null, [], [], DateTimeOffset.UtcNow), TimeSpan.Zero);
    }
    Telemetry("V1"); Telemetry("V2", 10); Telemetry("V3", emergency: true); Telemetry("V4");
    var gateway = new PlanningGateway(); var traffic = new ReadOnlyTraffic(); var planner = new PlanningRoutes();
    var options = new DispatchOrchestratorOptions { StationPickupSites = new() { ["PLC"] = "PICK" }, StandbySites = ["HOME1"],
        StationVehiclePools = new() { ["PLC"] = ["V1", "V2", "V3"] } };
    var task = new DispatchTaskWorkItem("TASK", "PLC", null, null, DispatchTaskStage.WaitingForVehicle, 50, now, now);
    var input = new DispatchAssignmentTask("TASK", "PLC", 50, now);
    var queue = new DispatchQueueWindow { Waiting = [task], Work = [task], Input = [input],
        Window = [new DispatchAssignmentOptimizer().Score(input, policy, now)], Ready = new(StringComparer.OrdinalIgnoreCase)
        { ["TASK"] = new("TASK", "PLC", null, DispatchTaskStage.WaitingForVehicle, "STEP", 1,
            DispatchTaskStepType.GoToPickup, DispatchTaskStepStatus.Ready, null, "PICK", now) } };
    var costs = new DispatchAssignmentOptions { ReturnCostWeight = .5, SiteZones = new() { ["HOME1"] = "A", ["PICK"] = "B" }, CrossZoneSeconds = 10 };
    var filter = new DispatchCandidateFilter(options, gateway, states, map, new() { PositionStaleSeconds = 60 }, planner, traffic);
    var graph = await filter.BuildAsync(queue, costs, map.Current, default);
    Check(graph.Edges.Count == 1 && graph.Edges.Single().VehicleId == "V1" && graph.GeneralReasons["V2"] is not null &&
        graph.GeneralReasons["V3"] is not null && graph.Diagnostics["TASK"].Single(c => c.VehicleId == "V4").Reason.Contains("车辆池"),
        "candidate layer enforces battery, emergency and station vehicle pool before matching");
    var candidate = graph.Edges.Single();
    Check(candidate.TravelSeconds == 10 && candidate.TrafficWaitSeconds == 3 && candidate.ReturnSeconds == 2 &&
        candidate.CrossZoneSeconds == 10 && candidate.BatteryRiskSeconds == 2.5 && candidate.StandbyBonusSeconds == 1,
        "candidate layer exposes travel, traffic, return, zone, battery and bounded standby costs separately");
    Check(graph.WaitingRoutes.Count == 0 && traffic.Writes == 0 && gateway.Writes == 0,
        "candidate layer never persists traffic observations, acquires leases or sends vehicle commands");
    var result = new DispatchAssignmentOptimizer().Optimize(queue.Input, graph.Edges, costs, now);
    Check(result.Assignments.Single().VehicleId == "V1" && result.Assignments.Single().TotalCostSeconds == 26.5,
        "pure layer-three optimizer consumes only hard-feasible layer-two edges");
    traffic.Unavailable.Add("R");
    var blocked = await filter.BuildAsync(queue, costs with { Mode = "Global" }, map.Current, default);
    Check(blocked.Edges.Count == 0 && blocked.WaitingRoutes.Single().TaskId == "TASK" && traffic.Writes == 0 && gateway.Writes == 0,
        "blocked candidate returns waiting observation proposal for layer four while keeping computation read-only");
    traffic.Unavailable.Clear();
    var switching = new DispatchCandidateFilter(options, gateway, states, map, new() { PositionStaleSeconds = 60 }, planner, traffic,
        taskSwitchGuard: new RejectSwitch());
    var switchGraph = await switching.BuildAsync(queue, costs, map.Current, default);
    Check(switchGraph.Edges.Count == 0 && switchGraph.Diagnostics["TASK"].Single(c => c.VehicleId == "V1").Reason.Contains("换单"),
        "controlled task-switch ownership cannot be overridden by candidate cost");
    Telemetry("V1", 101);
    Check(filter.CheckVehicle((await gateway.GetVehiclesAsync(default))[0], new HashSet<string>(), costs) is not null,
        "out-of-range battery telemetry is rejected as invalid rather than granting a negative risk bonus");
}

{
    var service = new DispatchOptimizationService(Path.Combine(Path.GetTempPath(), "agv-layer-cas-" + Guid.NewGuid().ToString("N"), "options.json"),
        new(), NullLogger<DispatchOptimizationService>.Instance);
    var keys = new[] { "queue", "candidates", "matching", "traffic", "safety", "execution" };
    var publishers = keys.Select(key => Task.Run(() =>
    {
        for (var n = 0; n < 200; n++) service.ReportLayer(key, "Blocked", key + ":" + n, n);
    })).Append(Task.Run(() =>
    {
        for (var n = 0; n < 500; n++) service.Publish(new() { Message = "parallel optimizer " + n }, shadow: true);
    })).ToArray();
    await Task.WhenAll(publishers);
    var after = await service.GetAsync(default);
    Check(after.Layers.Count == 6 && after.Layers.All(l => l.Summary == l.Key + ":199" && l.ItemCount == 199 && l.State == "Blocked" && l.UpdatedAt > DateTimeOffset.MinValue),
        "concurrent independent layer reports and optimizer publication retain every final layer observation");
    Check(after.CycleNumber == 500 && after.ShadowCycleCount == 500 && after.Options.Mode == "Shadow" && after.Revision == "initial",
        "concurrent observation updates preserve exact cycle counters and saved policy identity");
    var rejected = false;
    try { service.ReportLayer("unknown-layer", "Ready", "should fail"); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected && (await service.GetAsync(default)).Layers.Select(l => l.Key).SequenceEqual(keys),
        "unknown runtime layer cannot overwrite or add a misleading seventh execution layer");
}

Console.WriteLine($"Dispatch planning layers: {checks} checks passed; isolated persistent queue, synthetic telemetry and read-only candidate planning only.");

sealed class PlanningGateway : IAgvGateway
{
    public int Writes;
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgvVehicleStatus>>(
        Enumerable.Range(1, 4).Select(i => new AgvVehicleStatus("V" + i, ConnectionState.Online, "Idle", 75, "HOME" + i, DateTimeOffset.UtcNow)).ToArray());
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => Task.FromResult(new DeviceConnectionStatus("FAKE", "FAKE", ConnectionState.Online, DateTimeOffset.UtcNow));
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    public Task<string> CreateTransportTaskAsync(string id, string? vehicle, IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken ct)
    { Writes++; throw new InvalidOperationException("Planning layer must not send"); }
    public Task CancelTaskAsync(string id, CancellationToken ct) { Writes++; throw new InvalidOperationException("Planning layer must not cancel"); }
}
sealed class PlanningRoutes : IRoutePlanner
{
    public RoutePlan? Plan(string source, string destination, IReadOnlySet<string> unavailable) =>
        (source, destination) switch
        {
            ("HOME1", "PICK") when !unavailable.Contains("R") => new([source, destination], ["TO-PICK"], ["R"], 8),
            ("PICK", "HOME1") => new([source, destination], ["RETURN"], [], 4),
            _ => null
        };
}
sealed class ReadOnlyTraffic : ITrafficResourceManager
{
    public HashSet<string> Unavailable { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Writes;
    public Task<IReadOnlySet<string>> GetUnavailableResourceIdsAsync(string task, CancellationToken ct) => Task.FromResult<IReadOnlySet<string>>(Unavailable);
    public Task RecordRouteAsync(TrafficRouteObservation observation, CancellationToken ct) { Writes++; throw new InvalidOperationException("Candidate layer must not persist observations"); }
    public Task<TrafficReservationResult> TryAcquireAsync(string step, string task, string vehicle, IReadOnlyCollection<string> resources, CancellationToken ct)
    { Writes++; throw new InvalidOperationException("Candidate layer must not acquire"); }
    public Task ReleaseStepAsync(string step, CancellationToken ct) { Writes++; throw new InvalidOperationException("Candidate layer must not release"); }
    public Task ReleaseTaskExceptStepAsync(string task, string step, CancellationToken ct) { Writes++; throw new InvalidOperationException("Candidate layer must not release"); }
    public Task ReleaseTaskAsync(string task, CancellationToken ct) { Writes++; throw new InvalidOperationException("Candidate layer must not release"); }
    public Task RenewTaskAsync(string task, CancellationToken ct) { Writes++; throw new InvalidOperationException("Candidate layer must not renew"); }
}
sealed class RejectSwitch : ITaskSwitchGuard
{
    public Task<bool> AllowsVehicleAsync(string vehicle, string? task, string? request, CancellationToken ct) => Task.FromResult(false);
}
