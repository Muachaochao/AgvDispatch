using System.Reflection;
using System.Text.Json;
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
using SqlSugar;

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    checks++; Console.WriteLine("PASS " + name);
}

// Settings use isolated files and real service authorization/revision handling.
{
    var directory = Path.Combine(Path.GetTempPath(), "agv-optimization-settings-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "options.json");
    var service = new DispatchOptimizationService(path, new(), NullLogger<DispatchOptimizationService>.Instance);
    var first = await service.GetAsync(default);
    var admin = TrafficUserContext.LocalOperation("TEST\\ADMIN");
    var viewer = new TrafficUserContext("TEST\\VIEW", "Test viewer", TrafficPermission.View);
    var denied = false;
    try { await service.SaveOptionsAsync(viewer, new(new() { Mode = "Global" }, first.Revision), default); }
    catch (UnauthorizedAccessException) { denied = true; }
    Check(denied && !File.Exists(path) && service.Options.Mode == "Shadow", "non-admin save rejected before disk or dispatch policy changes");
    var requested = new DispatchAssignmentOptions { Mode = "Global", WindowSize = 60,
        SiteZones = new() { ["HOME1"] = "A" }, StationDeadlineMinutes = new() { ["CNC1"] = 10 } };
    var saved = await service.SaveOptionsAsync(admin, new(requested, first.Revision), default);
    Check(File.Exists(path) && saved.Options.Mode == "Global" && saved.Revision != first.Revision,
        "admin save atomically persists selected mode and new revision");
    requested.SiteZones["HOME1"] = "MUTATED";
    Check(service.Options.SiteZones["HOME1"] == "A", "saved policy does not retain caller-owned mutable dictionary");
    var conflict = false;
    try { await service.SaveOptionsAsync(admin, new(new() { Mode = "Disabled" }, first.Revision), default); }
    catch (InvalidOperationException) { conflict = true; }
    Check(conflict && (await service.GetAsync(default)).Revision == saved.Revision && service.Options.Mode == "Global",
        "stale revision rejected without replacing a newer saved policy");
    var restarted = new DispatchOptimizationService(path, new(), NullLogger<DispatchOptimizationService>.Instance);
    Check(restarted.Options.Mode == "Global" && restarted.Options.WindowSize == 60 &&
          (await restarted.GetAsync(default)).Revision == saved.Revision && restarted.Options.StationDeadlineMinutes["CNC1"] == 10,
        "restart restores exact persisted mode, limits, deadlines and revision");
    var diskBefore = File.ReadAllText(path);
    foreach (var invalid in new[] { saved.Options with { IntervalMs = 1999 }, saved.Options with { WindowSize = 0 },
        saved.Options with { MinimumBatteryPercent = 101 }, saved.Options with { NominalSpeedMetersPerSecond = double.NaN },
        saved.Options with { StandbyBonusSeconds = double.PositiveInfinity }, saved.Options with { SiteZones = null! } })
    {
        var rejected = false;
        try { await service.SaveOptionsAsync(admin, new(invalid, saved.Revision), default); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected && File.ReadAllText(path) == diskBefore && (await service.GetAsync(default)).Revision == saved.Revision,
            "invalid/nonfinite policy rejected without persistent partial save");
    }
    service.Publish(new() { Message = "isolated cycle" }, shadow: true, improved: true);
    var published = await service.GetAsync(default);
    Check(published.Options.Mode == "Global" && published.Revision == saved.Revision && published.ShadowCycleCount == 1 && published.ImprovedCycleCount == 1,
        "cycle publication retains current saved policy identity and comparison counters");
    File.WriteAllText(path, "{broken-json");
    var damaged = new DispatchOptimizationService(path, new() { Mode = "Global" }, NullLogger<DispatchOptimizationService>.Instance);
    Check(damaged.Options.Mode == "Disabled" && (await damaged.GetAsync(default)).Message.Contains("读取失败"),
        "corrupt persisted file disables optimization instead of silently enabling a default global mode");
    Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "atomic saves leave no temporary configuration fragments");
}

// Graph costs already contain seconds; preserve that unit while separating live traffic penalty.
{
    var map = new ActiveMapRuntimeSnapshot("ESTIMATE", "V1", new("route-estimate", "", "LocalCartesian", "m",
        [new("A", 0, 0, MapNodeType.Station, true, []), new("B", 10, 0, MapNodeType.Station, true, [])],
        [new("AB", "A", "B", 10, 2, false, true, [])], []), DateTimeOffset.UtcNow);
    var estimate = typeof(DispatchTaskOrchestrator).GetMethod("EstimateRoute", BindingFlags.Static | BindingFlags.NonPublic)!;
    (double Travel, double TrafficWait) Estimate(RoutePlan route, ActiveMapRuntimeSnapshot? current, double nominal) =>
        ((double, double))estimate.Invoke(null, [route, current, new DispatchAssignmentOptions { NominalSpeedMetersPerSecond = nominal }])!;
    var route = new RoutePlan(["A", "B"], ["AB"], [], 12); // 10m / 2m/s + 7s congestion.
    Check(Estimate(route, map, 2) == (5, 7), "route cost seconds are not divided by speed a second time");
    Check(Estimate(route, map, 1) == (10, 7), "nominal speed cap adjusts travel but preserves traffic delay seconds");
    Check(Estimate(route, map, 3) == (5, 7), "nominal speed cannot exceed road speed limit");
    Check(Estimate(route, null, 2) == (12, 0) && Estimate(route with { EdgeIds = [] }, map, 2) == (12, 0),
        "unknown edge geometry preserves configured cost units without fabricating traffic delay");
    Check(Estimate(route with { Cost = 4 }, map, 2) == (5, 0), "traffic estimate never reports negative wait");
}

foreach (var mode in new[] { "Disabled", "Shadow" })
{
    using var f = await Fixture.Create(mode);
    f.Dispatch.RequireGlobalMatching = true;
    var task = await f.Call("CNC1", 80);
    await f.Cycle();
    Check((await f.Due()).Count == 0 && f.Traffic.Acquisitions.Count == 0 &&
          (await f.Tasks.GetStepsAsync(task, default))[0].Status == DispatchTaskStepStatus.Ready,
        "production requiring Global preserves waiting task without greedy dispatch in " + mode);
}

using (var f = await Fixture.Create("Shadow"))
{
    var a = await f.Call("CNC1", 80); var b = await f.Call("CNC2", 50);
    await f.Cycle();
    var view = await f.Optimization.GetAsync(default);
    Check(view.Tasks.Single(t => t.TaskId == a).RecommendedVehicleId == "V2" &&
          view.Tasks.Single(t => t.TaskId == b).RecommendedVehicleId == "V1", "shadow detects global combination over greedy trap");
    Check(view.Tasks.Single(t => t.TaskId == a).GreedyVehicleId == "V1" &&
          view.Tasks.Single(t => t.TaskId == b).GreedyVehicleId == "V2", "shadow reports comparable greedy baseline");
    Check((await f.Due()).Count == 0 && f.Traffic.Acquisitions.Count == 0 && f.Traffic.RecordCount == 0,
        "shadow calculation does not create commands, leases, or traffic-queue mutations");
}

using (var f = await Fixture.Create("Global", shared: true))
{
    var original = f.Map.Current!.Definition;
    var approach = new MapNode("APPROACH", 25, 0, MapNodeType.Station, true, []);
    var safe = new MapNode("SAFE", 50, 0, MapNodeType.Intersection, true, []);
    var nodes = original.Nodes.Append(approach).Append(safe).ToArray();
    var targetEdges = new[]
    {
        new MapEdge("HOME1-APPROACH", "HOME1", "APPROACH", 1, 1, false, true, []),
        new MapEdge("APPROACH-SAFE", "APPROACH", "SAFE", 1, 1, false, true, ["R"]),
        new MapEdge("SAFE-PICK1", "SAFE", "PICK1", 1, 1, false, true, ["DOWNSTREAM"])
    };
    f.Planner.Routes[("HOME1", "PICK1")] =
        new RoutePlan(["HOME1", "APPROACH", "SAFE", "PICK1"],
            ["HOME1-APPROACH", "APPROACH-SAFE", "SAFE-PICK1"], ["R", "DOWNSTREAM"], 3);
    f.Map.Replace(new("M", "V2",
        new("optimization-window-test", "", "LocalCartesian", "m", nodes, targetEdges,
            [new TrafficResourceDefinition("R", TrafficResourceType.NarrowAisle),
             new TrafficResourceDefinition("DOWNSTREAM", TrafficResourceType.NarrowAisle)]),
        DateTimeOffset.UtcNow));
    f.Dispatch.StationVehiclePools["CNC1"] = ["V1"];
    var task = await f.Call("CNC1", 80);
    await f.Cycle();
    var command = (await f.Due()).Single();
    var payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(
        command.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    Check(payload.WindowCount == 2 && payload.Windows.Count == 2 &&
          payload.Windows[0].Destination == "SAFE" &&
          payload.Windows[0].EdgeIds.SequenceEqual(["HOME1-APPROACH", "APPROACH-SAFE"]) &&
          !payload.Windows[0].ResourceIds.Contains("DOWNSTREAM"),
        "global first-leg dispatch reserves only its nearest safe window; downstream exit remains free");
    Check(f.Db.Queryable<TrafficResourceLeaseEntity>().Where(x => x.TaskId == task).ToList()
            .Select(x => x.ResourceId).SequenceEqual(["R"]),
        "global first-leg dispatch does not pre-lease a later shared corridor");
}

using (var f = await Fixture.Create("Global"))
{
    var a = await f.Call("CNC1", 80); var b = await f.Call("CNC2", 50);
    await f.Cycle();
    var commands = await f.Due();
    Check(commands.Count == 2 && commands.Select(c => c.VehicleId).Distinct().Count() == 2,
        "global prepares exactly one command per task and per vehicle");
    Check(commands.Single(c => c.TaskId == a).VehicleId == "V2" && commands.Single(c => c.TaskId == b).VehicleId == "V1",
        "global applies joint minimum arrival allocation");
    Check(f.Traffic.Acquisitions[0].Task == a, "road-right acquisition follows effective task priority");
    await f.Cycle();
    Check((await f.Due()).Count == 2, "subsequent cycle preserves original pending commands without duplicates");
    Check(f.Gateway.Writes == 0, "optimization registers outbox only and never bypasses command sender");
}

var parallelPreflight = new ConcurrentMapPreflight();
using (var f = await Fixture.Create("Global", preflight: parallelPreflight))
{
    await f.Call("CNC1", 80);
    await f.Call("CNC2", 50);
    var occupied = (await f.Repository.GetSubmittingTasksAsync(default))
        .Concat(await f.Repository.GetAssignedTasksAsync(default)).ToArray();
    var cycle = (Task)typeof(DispatchTaskOrchestrator).GetMethod(
        "RunOptimizationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(f.Orchestrator, [f.Optimization.Options, 2, occupied, CancellationToken.None])!;
    var bothStarted = true;
    try
    {
        await parallelPreflight.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
    catch (TimeoutException)
    {
        bothStarted = false;
    }
    finally
    {
        parallelPreflight.Release.TrySetResult();
    }
    await cycle.WaitAsync(TimeSpan.FromSeconds(5));
    Check(bothStarted && parallelPreflight.MaximumConcurrentReads == 2 && (await f.Due()).Count == 2,
        "independent vehicle map checks start in parallel before one-reservation-at-a-time commit");
}

foreach (var change in new[] { "none", "gate", "map", "policy", "slots" })
{
    var preflight = new PausableMapPreflight();
    using var f = await Fixture.Create("Global", preflight: preflight);
    var first = await f.Call("CNC1", 80);
    await f.Cycle();
    var second = await f.Call("CNC2", 70);
    preflight.Paused = true;
    var cycle = (Task)typeof(DispatchTaskOrchestrator).GetMethod("RunCycleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(f.Orchestrator, [CancellationToken.None])!;
    try
    {
        var reachedPreflight = await Task.WhenAny(preflight.Started.Task, cycle).WaitAsync(TimeSpan.FromSeconds(5));
        if (reachedPreflight == cycle)
        {
            await cycle;
            var snapshot = await f.Optimization.GetAsync(default);
            throw new InvalidOperationException("Dispatch cycle ended before the paused preflight: " + snapshot.Message
                + " " + string.Join(" ", snapshot.Tasks.Select(task => task.Reason)));
        }
        f.Gateway.AllowWrites = true;
        await f.SendCycle().WaitAsync(TimeSpan.FromSeconds(3));
        Check(f.Gateway.Writes == 1, "earlier command is sent during pending vehicle map check: " + change);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var control = await f.Gate.EnterControlAsync(budget.Token);
        if (change == "gate") f.Gate.SetMode(MapExecutionMode.Draining);
        if (change == "map")
        {
            var map = f.Map.Current!;
            f.Map.Replace(new(map.MapId, "V2", map.Definition, DateTimeOffset.UtcNow));
        }
        if (change == "policy")
        {
            var policy = await f.Optimization.GetAsync(default);
            await f.Optimization.SaveOptionsAsync(TrafficUserContext.LocalOperation("TEST\\ADMIN"),
                new(policy.Options with { WindowSize = policy.Options.WindowSize + 1 }, policy.Revision), default);
        }
        if (change == "slots") f.Dispatch.MaxConcurrentTasks = 1;
    }
    finally
    {
        preflight.Resume.TrySetResult();
    }
    await cycle.WaitAsync(TimeSpan.FromSeconds(5));
    Check((await f.Due()).Any(c => c.TaskId == second) == (change == "none"),
        "vehicle map evidence is consumed only under the current dispatch context: " + change);
    Check((await f.Tasks.GetStepsAsync(first, default))[0].Status != DispatchTaskStepStatus.Ready,
        "the earlier command remains attached to its original task: " + change);
}

using (var f = await Fixture.Create("Global"))
{
    var a = await f.Call("CNC1", 80); var b = await f.Call("CNC2", 50);
    f.Traffic.FailOnce = (a, "V2");
    await f.Cycle();
    var commands = await f.Due();
    Check(f.Traffic.Acquisitions.First().Task == a && f.Traffic.Acquisitions.First().Vehicle == "V2",
        "reservation race starts with globally recommended pair");
    Check(commands.Count == 2 && commands.Single(c => c.TaskId == a).VehicleId == "V1" && commands.Single(c => c.TaskId == b).VehicleId == "V2",
        "failed road-right request triggers joint rematch instead of stealing a reserved vehicle");
    Check(f.Traffic.Acquisitions.Count == 3, "failed pair is excluded from current cycle without retry loop");
}

using (var f = await Fixture.Create("Global", shared: true))
{
    var a = await f.Call("CNC1", 80); var b = await f.Call("CNC2", 50);
    await f.Cycle();
    Check((await f.Due()).Count == 1 && (await f.Due()).Single().TaskId == a,
        "capacity-one public resource permits only high-priority transport");
    Check((await f.Tasks.GetStepsAsync(b, default))[0].Status == DispatchTaskStepStatus.Ready,
        "unleased task remains persisted and ready for next cycle");
    Check(f.Db.Queryable<TrafficResourceLeaseEntity>().Count() == 1, "successful existing road-right lease retained");
    await f.Traffic.ReleaseTaskAsync(a, default);
    await f.Cycle();
    Check((await f.Due()).Count == 2 && (await f.Due()).Single(c => c.TaskId == b).VehicleId == "V1",
        "waiting task receives remaining vehicle after resource release without replacing first assignment");
}

foreach (var scenario in new[] { "low-battery", "blocked", "emergency", "unknown-emergency", "alarm", "stale", "vehicle-task" })
{
    using var f = await Fixture.Create("Global");
    var task = await f.Call("CNC1", 80);
    f.Dispatch.StationVehiclePools["CNC1"] = ["V1"];
    f.Apply("V1", battery: scenario == "low-battery" ? 10 : 90,
        blocked: scenario == "blocked", emergency: scenario == "unknown-emergency" ? null : scenario == "emergency",
        alarm: scenario == "alarm", stale: scenario == "stale", hasTask: scenario == "vehicle-task");
    await f.Cycle();
    var view = await f.Optimization.GetAsync(default);
    Check((await f.Due()).Count == 0 && view.Tasks.Single(t => t.TaskId == task).Candidates.All(c => !c.Eligible),
        scenario + " excludes vehicle as a hard constraint");
}

using (var f = await Fixture.Create("Global"))
{
    var owner = await f.Call("OWNER", 90);
    var step = (await f.Tasks.GetStepsAsync(owner, default))[0];
    await f.Outbox.TryPrepareTransportStepAsync(owner, step.StepId, "V1", ["HOME1", "PICK1"], null, default);
    var waiting = await f.Call("CNC1", 80);
    await f.Cycle();
    Check((await f.Due()).Single(c => c.TaskId == waiting).VehicleId == "V2" &&
          (await f.Due()).Single(c => c.TaskId == owner).VehicleId == "V1", "occupied vehicle remains fixed and cannot be reassigned");
}

using (var f = await Fixture.Create("Global", shared: true))
{
    var owner = await f.Call("CNC1", 90);
    var step = (await f.Tasks.GetStepsAsync(owner, default))[0];
    await f.Traffic.TryAcquireAsync(step.StepId, owner, "V1", ["R"], default);
    await f.Cycle();
    Check((await f.Due()).Count == 0 && f.Db.Queryable<TrafficResourceLeaseEntity>().Count() == 1,
        "waiting task with previously acquired road-right remains fixed instead of being reallocated");
}

using (var f = await Fixture.Create("Global", shared: true))
{
    var owner = await f.Call("CNC1", 90);
    var step = (await f.Tasks.GetStepsAsync(owner, default))[0];
    await f.Traffic.TryAcquireAsync(step.StepId, owner, "V1", ["R"], default);
    var waiting = await f.Call("CNC2", 80);
    f.Dispatch.StationVehiclePools["CNC2"] = ["V1"];
    f.Planner.Routes[("HOME1", "PICK2")] = f.Planner.Routes[("HOME1", "PICK2")] with { ResourceIds = [] };
    await f.Cycle();
    var view = await f.Optimization.GetAsync(default);
    Check((await f.Due()).Count == 0 && view.Tasks.Single(t => t.TaskId == waiting).Candidates.All(c => !c.Eligible),
        "vehicle with orphaned prior lease cannot be reused even for another resource-free route");
}

using (var f = await Fixture.Create("Global", shared: true))
{
    await f.Call("CNC1", 80);
    f.Traffic.AfterAcquire = () => f.Map.Replace(new("M", "V2", f.Map.Current!.Definition, DateTimeOffset.UtcNow));
    await f.Cycle();
    Check((await f.Due()).Count == 0 && f.Db.Queryable<TrafficResourceLeaseEntity>().Count() == 0,
        "map change after reservation blocks command and releases newly acquired step lease");
}

using (var f = await Fixture.Create("Global", shared: true))
{
    await f.Call("CNC1", 80);
    f.Dispatch.StationVehiclePools["CNC1"] = ["V1"];
    f.Traffic.AfterAcquire = () => f.Apply("V1", emergency: true);
    await f.Cycle();
    Check((await f.Due()).Count == 0 && f.Db.Queryable<TrafficResourceLeaseEntity>().Count() == 0,
        "emergency appearing after road-right acquisition prevents command and releases only new lease");
}

using (var f = await Fixture.Create("Global", shared: true))
{
    var task = await f.Call("CNC1", 80);
    f.Traffic.ThrowOnReleaseExcept = true;
    await f.Cycle();
    var commands = await f.Due();
    var leases = f.Db.Queryable<TrafficResourceLeaseEntity>().ToList();
    Check(commands.Count == 1 && commands.Single().TaskId == task && leases.Count == 1 &&
          leases.Single().VehicleId == commands.Single().VehicleId && f.Traffic.Acquisitions.Count == 1,
        "exception after outbox commit freezes both task and vehicle and preserves original valid road-right lease");
}

// Exercise the real sender boundary, after an optimizer has prepared a persisted command.
foreach (var interruption in new[] { "released", "missing-lease", "missing-route", "map-version", "wrong-lease-vehicle", "bad-resource-json" })
{
    using var f = await Fixture.Create("Global", shared: true);
    var task = await f.Call("CNC1", 80);
    await f.Cycle();
    var original = (await f.Due()).Single();
    var originalMap = f.Map.Current!;
    var request = f.Db.Queryable<TrafficRequestEntity>().Single(r => r.StepId == original.StepId);
    var route = new TrafficRouteObservation(original.StepId, task, original.VehicleId, request.Source, request.Destination,
        JsonSerializer.Deserialize<string[]>(request.EdgeIds)!, ["R"], false, "Restore original dispatch grant for isolated test")
        { MapId = originalMap.MapId, MapVersionId = originalMap.VersionId };
    f.Gateway.AllowWrites = true;
    switch (interruption)
    {
        case "released": await f.Traffic.ReleaseStepAsync(original.StepId, default); break;
        case "missing-lease": f.Db.Deleteable<TrafficResourceLeaseEntity>().ExecuteCommand(); break;
        case "missing-route": f.Db.Deleteable<TrafficRequestEntity>().ExecuteCommand(); break;
        case "map-version": f.Map.Replace(new("M", "OTHER-VERSION", originalMap.Definition, DateTimeOffset.UtcNow)); break;
        case "wrong-lease-vehicle": f.Db.Updateable<TrafficResourceLeaseEntity>().SetColumns(l => new TrafficResourceLeaseEntity { VehicleId = "OTHER-VEHICLE" }).Where(l => l.OwnerStepId == original.StepId).ExecuteCommand(); break;
        case "bad-resource-json": f.Db.Updateable<TrafficRequestEntity>().SetColumns(r => new TrafficRequestEntity { ResourceIds = "not-json" }).Where(r => r.StepId == original.StepId).ExecuteCommand(); break;
    }
    await f.SendCycle();
    var pending = (await f.Due()).Single();
    Check(pending.CommandId == original.CommandId && pending.AttemptCount == 0 && pending.ClaimedAt is null &&
          pending.Status == AgvCommandStatus.Pending && f.Gateway.Writes == 0,
        interruption + " blocks actual sender before claim, attempt consumption, or vehicle write");

    f.Map.Replace(originalMap);
    if (interruption == "wrong-lease-vehicle")
        f.Db.Updateable<TrafficResourceLeaseEntity>().SetColumns(l => new TrafficResourceLeaseEntity { VehicleId = original.VehicleId }).Where(l => l.OwnerStepId == original.StepId).ExecuteCommand();
    if (interruption == "bad-resource-json")
        f.Db.Updateable<TrafficRequestEntity>().SetColumns(r => new TrafficRequestEntity { ResourceIds = "[\"R\"]" }).Where(r => r.StepId == original.StepId).ExecuteCommand();
    await f.Traffic.RecordRouteAsync(route, default);
    var reservation = await f.Traffic.TryAcquireAsync(original.StepId, task, original.VehicleId, ["R"], default);
    Check(reservation.Acquired, interruption + " restores grant for original step and vehicle");
    await f.SendCycle();
    var sent = f.Db.Queryable<AgvCommandOutboxEntity>().Single(c => c.CommandId == original.CommandId);
    Check((await f.Due()).Count == 0 && sent.Status == (int)AgvCommandStatus.Sent && sent.AttemptCount == 1 &&
          sent.VehicleId == original.VehicleId && f.Gateway.Writes == 1 &&
          (await f.Repository.GetExecutingTasksAsync(default)).Single().TaskId == task,
        interruption + " resumes same persisted outbox with exactly first send attempt after original grant restored");
    await f.SendCycle();
    Check(f.Gateway.Writes == 1 && f.Db.Queryable<AgvCommandOutboxEntity>().Count() == 1,
        interruption + " never duplicates accepted original command on a later sender cycle");
}

using (var f = await Fixture.Create("Global"))
{
    await f.Call("CNC1", 80);
    await f.Cycle();
    f.Gateway.AllowWrites = true;
    await f.SendCycle();
    Check(f.Gateway.Writes == 1 && f.Db.Queryable<AgvCommandOutboxEntity>().Single().AttemptCount == 1 &&
          f.Db.Queryable<TrafficResourceLeaseEntity>().Count() == 0,
        "real sender accepts validated NotRequired route without inventing a public-resource lease");
}

Console.WriteLine($"Dispatch optimization integration: {checks} checks passed; temporary SQLite, synthetic telemetry and stub gateway only. No network or physical AGV/PLC access.");

sealed class Fixture : IDisposable
{
    public ISqlSugarClient Db { get; private set; } = null!;
    public DispatchTaskRepository Tasks { get; private set; } = null!;
    public DispatchOrchestrationRepository Repository { get; private set; } = null!;
    public AgvCommandOutboxRepository Outbox { get; private set; } = null!;
    public DispatchOrchestratorOptions Dispatch { get; private set; } = null!;
    public DispatchOptimizationService Optimization { get; private set; } = null!;
    public DispatchTaskOrchestrator Orchestrator { get; private set; } = null!;
    public AgvCommandOutboxDispatcher Sender { get; private set; } = null!;
    public ActiveMapRuntime Map { get; } = new();
    public FakeGateway Gateway { get; } = new();
    public ProbeTraffic Traffic { get; private set; } = null!;
    public FixturePlanner Planner { get; private set; } = null!;
    public AgvStateCache States { get; private set; } = null!;

    public MapExecutionGate Gate { get; } = new();

    public static async Task<Fixture> Create(string mode, bool shared = false, IRobokitTaskMapPreflight? preflight = null)
    {
        var f = new Fixture();
        var root = Path.Combine(Path.GetTempPath(), "agv-optimization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        f.Db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "test.db") });
        var resources = new List<TrafficResourceDefinition>();
        if (shared) resources.Add(new("R", TrafficResourceType.NarrowAisle));
        if (preflight is not null) resources.Add(new("WAIT-RESOURCE", TrafficResourceType.Station));
        var catalog = new TrafficResourceCatalog(resources);
        await new DatabaseInitializer(f.Db, catalog).InitializeAsync(default);
        f.Tasks = new(f.Db); f.Repository = new(f.Db); f.Outbox = new(f.Db, f.Map);
        f.Traffic = new(new TrafficResourceManager(f.Db, catalog, new(TimeSpan.FromMinutes(1))));
        f.Dispatch = new() { Enabled = true, MaxConcurrentTasks = 2, DumperWaitingSite = "WAIT", DumperDumpingSite = "DUMP",
            StationPickupSites = new() { ["CNC1"] = "PICK1", ["CNC2"] = "PICK2", ["OWNER"] = "PICK1" } };
        f.Optimization = new(Path.Combine(root, "optimizer.json"), new() { Mode = mode }, NullLogger<DispatchOptimizationService>.Instance);
        f.States = new(new AgvOptions { Enabled = true, Robots = [new() { VehicleId = "V1" }, new() { VehicleId = "V2" }] });
        var nodes = new[] { "HOME1", "HOME2", "PICK1", "PICK2", "WAIT", "DUMP" }
            .Select((id, i) => new MapNode(id, i, 0, id == "WAIT" ? MapNodeType.WaitingPoint : MapNodeType.Station, true,
                preflight is not null && id == "WAIT" ? ["WAIT-RESOURCE"] : [], id)).ToArray();
        var planner = new FixturePlanner(shared);
        f.Planner = planner;
        var edges = planner.Routes.Select(p => new MapEdge(p.Value.EdgeIds[0], p.Key.Item1, p.Key.Item2, p.Value.Cost, 1, false, true, p.Value.ResourceIds)).ToArray();
        f.Map.Replace(new("M", "V1", new("optimization-test", "", "LocalCartesian", "m", nodes, edges, resources), DateTimeOffset.UtcNow));
        f.Apply("V1"); f.Apply("V2");
        var gate = f.Gate; gate.SetMode(MapExecutionMode.Running);
        f.Orchestrator = new(NullLogger<DispatchTaskOrchestrator>.Instance, f.Dispatch, f.Repository, f.Outbox,
            f.Gateway, f.States, f.Map, new() { PositionStaleSeconds = 60 }, planner, f.Traffic, gate,
            robokitMap: preflight, optimization: f.Optimization,
            handshakeOptions: preflight is null ? null : new() { Enabled = true, PlcContractVersion = HandshakeExecutionOptions.Version });
        f.Sender = new(NullLogger<AgvCommandOutboxDispatcher>.Instance, new(), f.Outbox, f.Gateway, gate,
            trafficResources: f.Traffic, activeMap: f.Map);
        return f;
    }

    public async Task<string> Call(string station, int priority)
    {
        var created = await Tasks.TryCreateAsync(station, "DUMP", priority, default);
        await Tasks.MarkAcknowledgedAsync(created.Task.TaskId, default);
        return created.Task.TaskId;
    }
    public void Apply(string vehicle, decimal battery = 90, bool blocked = false, bool? emergency = false,
        bool alarm = false, bool stale = false, bool hasTask = false)
    {
        States.ApplyTelemetry(vehicle, new(vehicle == "V1" ? 0 : 1, 0, 0, .99, vehicle == "V1" ? "HOME1" : "HOME2", null,
            battery, blocked, emergency, "Idle", null, null, null,
            hasTask ? [new("EXISTING", 2, null)] : [], alarm ? [new("51001", AgvAlarmSeverity.Error, "test alarm")] : [],
            DateTimeOffset.UtcNow.AddMinutes(stale ? -5 : 0)), TimeSpan.Zero);
    }
    public async Task Cycle()
    {
        var occupied = (await Repository.GetSubmittingTasksAsync(default)).Concat(await Repository.GetAssignedTasksAsync(default)).ToArray();
        var method = typeof(DispatchTaskOrchestrator).GetMethod("RunOptimizationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(Orchestrator, [Optimization.Options, Math.Max(0, 2 - occupied.Length), occupied, CancellationToken.None])!;
    }
    public Task<IReadOnlyList<AgvCommandOutboxItem>> Due() => Outbox.GetDueCommandsAsync(DateTimeOffset.UtcNow.AddSeconds(1), 100, default);
    public async Task SendCycle() => await (Task)typeof(AgvCommandOutboxDispatcher)
        .GetMethod("DispatchDueCommandsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Sender, [CancellationToken.None])!;
    public void Dispose() => Db.Dispose();
}

sealed class PausableMapPreflight : IRobokitTaskMapPreflight
{
    public bool Paused;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<RobokitTaskMapCheck> CheckAsync(string vehicleId, string currentSite, string pickup,
        string waiting, string dumping, string mapId, string versionId, CancellationToken ct)
    {
        if (Paused)
        {
            Started.TrySetResult();
            await Resume.Task.WaitAsync(ct);
        }
        return new(true, "test map and paths verified");
    }
}

sealed class ConcurrentMapPreflight : IRobokitTaskMapPreflight
{
    private int _active;
    private int _maximumConcurrentReads;
    public TaskCompletionSource AllStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int MaximumConcurrentReads => Volatile.Read(ref _maximumConcurrentReads);

    public async Task<RobokitTaskMapCheck> CheckAsync(string vehicleId, string currentSite, string pickup,
        string waiting, string dumping, string mapId, string versionId, CancellationToken ct)
    {
        var active = Interlocked.Increment(ref _active);
        var maximum = Volatile.Read(ref _maximumConcurrentReads);
        while (active > maximum)
        {
            var observed = Interlocked.CompareExchange(
                ref _maximumConcurrentReads, active, maximum);
            if (observed == maximum) break;
            maximum = observed;
        }
        if (active >= 2) AllStarted.TrySetResult();
        try
        {
            await Release.Task.WaitAsync(ct);
            return new(true, "parallel test map and paths verified");
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

sealed class FixturePlanner
    : IRoutePlanner
{
    public Dictionary<(string, string), RoutePlan> Routes { get; } = [];
    public FixturePlanner(bool shared)
    {
        var data = new[] { ("HOME1", "PICK1", 1d), ("HOME2", "PICK1", 3d), ("HOME1", "PICK2", 2d), ("HOME2", "PICK2", 100d) };
        foreach (var (from, to, cost) in data) Routes[(from, to)] = new([from, to], [from + "-" + to], shared ? ["R"] : [], cost);
    }
    public RoutePlan? Plan(string source, string destination, IReadOnlySet<string> unavailableResourceIds)
    {
        var route = Routes.GetValueOrDefault((source, destination));
        return route is not null && !route.ResourceIds.Any(unavailableResourceIds.Contains) ? route : null;
    }
}

sealed class ProbeTraffic(ITrafficResourceManager inner) : ITrafficResourceManager
{
    public (string Task, string Vehicle)? FailOnce;
    public Action? AfterAcquire;
    public int RecordCount;
    public bool ThrowOnReleaseExcept;
    public List<(string Task, string Vehicle)> Acquisitions { get; } = [];
    public async Task<TrafficReservationResult> TryAcquireAsync(string step, string task, string vehicle, IReadOnlyCollection<string> resources, CancellationToken ct)
    {
        Acquisitions.Add((task, vehicle));
        if (FailOnce is { } fail && fail == (task, vehicle)) { FailOnce = null; return new(false, "R", "SIMULATED-RACE"); }
        var result = await inner.TryAcquireAsync(step, task, vehicle, resources, ct);
        if (result.Acquired) AfterAcquire?.Invoke();
        return result;
    }
    public Task RecordRouteAsync(TrafficRouteObservation route, CancellationToken ct) { RecordCount++; return inner.RecordRouteAsync(route, ct); }
    public Task<IReadOnlySet<string>> GetHeldTaskIdsAsync(CancellationToken ct) => inner.GetHeldTaskIdsAsync(ct);
    public Task<IReadOnlySet<string>> GetHeldVehicleIdsAsync(CancellationToken ct) => inner.GetHeldVehicleIdsAsync(ct);
    public Task<string?> CheckStepReservationAsync(string step, string task, string vehicle, string? mapId, string? mapVersionId,
        string source, string destination, CancellationToken ct) => inner.CheckStepReservationAsync(step, task, vehicle, mapId, mapVersionId, source, destination, ct);
    public Task<IReadOnlySet<string>> GetUnavailableResourceIdsAsync(string task, CancellationToken ct) => inner.GetUnavailableResourceIdsAsync(task, ct);
    public Task ReleaseStepAsync(string step, CancellationToken ct) => inner.ReleaseStepAsync(step, ct);
    public Task ReleaseTaskExceptStepAsync(string task, string step, CancellationToken ct)
    {
        if (ThrowOnReleaseExcept) { ThrowOnReleaseExcept = false; throw new IOException("Injected bookkeeping failure after successful outbox commit"); }
        return inner.ReleaseTaskExceptStepAsync(task, step, ct);
    }
    public Task ReleaseTaskAsync(string task, CancellationToken ct) => inner.ReleaseTaskAsync(task, ct);
    public Task RenewTaskAsync(string task, CancellationToken ct) => inner.RenewTaskAsync(task, ct);
}

sealed class FakeGateway : IAgvGateway
{
    public int Writes;
    public bool AllowWrites;
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => Task.FromResult(new DeviceConnectionStatus("FAKE", "FAKE", ConnectionState.Online, DateTimeOffset.UtcNow));
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgvVehicleStatus>>([
        new("V1", ConnectionState.Online, "Idle", 90, "HOME1", DateTimeOffset.UtcNow), new("V2", ConnectionState.Online, "Idle", 90, "HOME2", DateTimeOffset.UtcNow)]);
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string vendorTaskId, CancellationToken ct) => Task.FromResult<AgvTransportTaskStatus?>(null);
    public Task<string> CreateTransportTaskAsync(string task, string? vehicle, IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken ct)
    {
        Writes++;
        if (!AllowWrites) throw new InvalidOperationException("Unexpected direct vehicle command in optimization test");
        return Task.FromResult("VENDOR-" + task);
    }
    public Task CancelTaskAsync(string task, CancellationToken ct) { Writes++; throw new InvalidOperationException("Unexpected cancel in optimization test"); }
}
