using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }

using (var fixture = await Fixture.Create())
{
    await fixture.Call("OWNER", 1, -20);
    await fixture.Call("OLD", 50, -10);
    await fixture.Call("NEW", 50, -5);
    await fixture.Call("HIGH", 90, 0);
    await fixture.Observe("OWNER", 1);
    Check((await fixture.Acquire("OWNER")).Acquired, "initial holder acquires resource");
    await fixture.Observe("OLD", 50, true);
    await fixture.Observe("NEW", 50, true);
    Check(!(await fixture.Acquire("OLD")).Acquired, "waiting never replaces an existing holder");
    await fixture.Manager.ReleaseTaskAsync("OWNER", default);
    var blocked = await fixture.Acquire("NEW");
    Check(!blocked.Acquired && blocked.BlockingTaskId == "OLD", "release preserves oldest equal-priority call ahead of a later retry");
    fixture.Restart();
    Check(!(await fixture.Acquire("NEW")).Acquired, "queue order survives manager restart");
    await fixture.Observe("HIGH", 90);
    Check((await fixture.Acquire("HIGH")).Acquired, "higher effective priority is evaluated before an older lower-priority call");
    await fixture.Manager.ReleaseTaskAsync("HIGH", default);
    Check((await fixture.Acquire("OLD")).Acquired, "oldest call acquires after higher priority completes");
    Check(!(await fixture.Acquire("NEW")).Acquired, "new call cannot revoke granted right");
    Check((await fixture.Acquire("OLD")).Acquired, "existing holder renews regardless of queued calls");
    fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);
    Check((await fixture.Manager.GetHeldTaskIdsAsync(default)).SetEquals(["OLD"]), "expired heartbeat remains a fixed held task");
    Check((await fixture.Manager.GetHeldVehicleIdsAsync(default)).SetEquals(["V-OLD"]), "physical lease fixes its vehicle even without task vehicle assignment");
    await fixture.Db.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.Faulted)
        .Where(t => t.TaskId == "OLD").ExecuteCommandAsync();
    Check((await fixture.Manager.GetHeldVehicleIdsAsync(default)).Contains("V-OLD"), "faulted task cannot make its retained lease vehicle available");
    await fixture.Db.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.WaitingForVehicle)
        .Where(t => t.TaskId == "OLD").ExecuteCommandAsync();
    Check(!(await fixture.Acquire("NEW")).Acquired, "expired heartbeat does not release physical traffic ownership");
    await fixture.Manager.ReleaseTaskAsync("OLD", default);
    Check((await fixture.Acquire("NEW")).Acquired, "waiting call proceeds only after actual release");
}

using (var fixture = await Fixture.Create())
{
    await fixture.Call("OLD", 50, -10); await fixture.Call("NEW", 50, -5);
    await fixture.Observe("OLD", 50, true);
    fixture.Clock.Now = fixture.Clock.Now.AddSeconds(11);
    await fixture.Observe("NEW", 50);
    Check((await fixture.Acquire("NEW")).Acquired, "unrefreshed optimizer recommendation stops blocking after 10 seconds");
    await fixture.Manager.ReleaseTaskAsync("NEW", default);
    await fixture.Observe("OLD", 50, true, "M-OTHER");
    Check((await fixture.Acquire("NEW")).Acquired, "queue recommendation from another map does not block current map");
    await fixture.Manager.ReleaseTaskAsync("NEW", default);
    await fixture.Observe("OLD", 50, true);
    await fixture.Db.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.Cancelled)
        .Where(t => t.TaskId == "OLD").ExecuteCommandAsync();
    Check((await fixture.Acquire("NEW")).Acquired, "cancelled call cannot retain an ungranted queue position");
}

using (var fixture = await Fixture.Create())
{
    await fixture.Call("OLD", 50, -10); await fixture.Call("NEW", 50, -5); await fixture.Call("OWNER", 1, -20);
    await fixture.Observe("OWNER", 1, resources: ["S"]);
    await fixture.Acquire("OWNER", ["S"]);
    await fixture.Observe("OLD", 50, true, resources: ["R", "S"]);
    await fixture.Observe("NEW", 50);
    Check((await fixture.Acquire("NEW")).Acquired, "predecessor blocked by another resource does not waste free capacity");
}

using (var fixture = await Fixture.Create(2))
{
    await fixture.Call("OLD", 50, -10); await fixture.Call("NEW", 50, -5);
    await fixture.Observe("OLD", 50, true); await fixture.Observe("NEW", 50);
    Check((await fixture.Acquire("NEW")).Acquired && (await fixture.Acquire("OLD")).Acquired,
        "capacity two serves both calls without treating queue head as an exclusive lock");
    var events = await fixture.Db.Queryable<TrafficLifecycleEventEntity>().CountAsync();
    await fixture.Observe("NEW", 50);
    var renewed = await fixture.Db.Queryable<TrafficLifecycleEventEntity>().CountAsync();
    await fixture.Observe("NEW", 50);
    Check(await fixture.Db.Queryable<TrafficLifecycleEventEntity>().CountAsync() == renewed,
        "refreshing queue recommendation does not append duplicate lifecycle events");
}

using (var fixture = await Fixture.Create())
{
    await fixture.Call("BUSINESS", 50, -5);
    await fixture.Observe("BUSINESS", 55, true);
    await fixture.Ordinary("CHARGING");
    var charging = await fixture.Acquire("CHARGING");
    Check(!charging.Acquired && charging.BlockingTaskId == "BUSINESS",
        "ordinary charging without a DispatchTask cannot bypass a scored business queue");
    Check((await fixture.Acquire("BUSINESS")).Acquired,
        "business request acquires before a lower-priority charging movement");
    await fixture.Manager.ReleaseTaskAsync("BUSINESS", default);
    Check((await fixture.Acquire("CHARGING")).Acquired,
        "charging ordinary queue can acquire after the real business owner releases");
    await fixture.Observe("BUSINESS", 95, true);
    Check((await fixture.Acquire("CHARGING")).Acquired,
        "higher business priority does not revoke an existing charging lease");
}

using (var fixture = await Fixture.Create())
{
    await fixture.Ordinary("FIRST", true);
    fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
    await fixture.Ordinary("SECOND");
    var second = await fixture.Acquire("SECOND");
    Check(!second.Acquired && second.BlockingTaskId == "FIRST",
        "two ordinary system movements use persisted FIFO instead of polling order");
    await fixture.Ordinary("FIRST", true);
    fixture.Restart();
    Check(!(await fixture.Acquire("SECOND")).Acquired,
        "ordinary FIFO start survives recommendation refresh and manager restart");
    Check((await fixture.Acquire("FIRST")).Acquired, "oldest ordinary movement can acquire without a DispatchTask");
    await fixture.Manager.ReleaseTaskAsync("FIRST", default);
    Check((await fixture.Acquire("SECOND")).Acquired, "next ordinary movement proceeds after actual release");
}

using (var fixture = await Fixture.Create())
{
    await fixture.Call("EQUIPMENT", 50, -5);
    await fixture.Manager.RecordRouteAsync(new("EQUIPMENT", "EQUIPMENT", "V-EQUIPMENT", "A", "B", [], ["R"], true,
        "dumper permit") { MapId = "M", MapVersionId = "V", WaitingCategory = "Equipment", QueuePriority = 55 }, default);
    await fixture.Call("LOW", 10, -5);
    await fixture.Observe("LOW", 15);
    Check(!(await fixture.Acquire("LOW")).Acquired,
        "dumper equipment wait and route wait participate in the same effective-priority queue");
    Check((await fixture.Acquire("EQUIPMENT")).Acquired, "equipment wait acquires its actual traffic capacity");
}

using (var fixture = await Fixture.Create())
{
    await fixture.Call("BUSINESS", 50, -5);
    await fixture.Observe("BUSINESS", 55, true);
    var ordinary = await fixture.Acquire("UNVERSIONED-LEGACY");
    Check(!ordinary.Acquired && ordinary.BlockingTaskId == "BUSINESS",
        "legacy direct acquisition without route metadata cannot jump a valid business queue");
    await fixture.Db.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.Completed)
        .Where(t => t.TaskId == "BUSINESS").ExecuteCommandAsync();
    Check((await fixture.Acquire("UNVERSIONED-LEGACY")).Acquired,
        "legacy direct acquisition remains possible when no valid predecessor exists");
}

using (var fixture = await Fixture.Create())
{
    await fixture.Call("CANCELLED", 90, -5);
    await fixture.Observe("CANCELLED", 95, true);
    await fixture.Db.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.Cancelled)
        .Where(t => t.TaskId == "CANCELLED").ExecuteCommandAsync();
    await fixture.Ordinary("SYSTEM");
    Check((await fixture.Acquire("SYSTEM")).Acquired, "cancelled business call cannot block an ordinary system movement");
}

using (var fixture = await Fixture.Create())
{
    await fixture.Call("BUSINESS", 90, -5);
    await fixture.Observe("BUSINESS", 95, true);
    await fixture.Manager.RecordRouteAsync(new("BUSINESS", "BUSINESS", "V-BUSINESS", "A", "B", [], ["R"], true,
        "readiness no longer holds") { MapId = "M", MapVersionId = "V", QueuePriority = 95, QueueEligible = false }, default);
    await fixture.Ordinary("SYSTEM");
    Check((await fixture.Acquire("SYSTEM")).Acquired,
        "withdrawn business recommendation cannot block a ready ordinary system movement");
    await fixture.Manager.ReleaseTaskAsync("SYSTEM", default);
    await fixture.Observe("BUSINESS", 95);
    Check((await fixture.Acquire("BUSINESS")).Acquired, "a revalidated original request can rejoin the priority queue");
    await fixture.Manager.RecordRouteAsync(new("BUSINESS", "BUSINESS", "V-BUSINESS", "A", "B", [], ["R"], true,
        "withdraw recommendation only") { MapId = "M", MapVersionId = "V", QueuePriority = 95, QueueEligible = false }, default);
    Check(!(await fixture.Acquire("SYSTEM")).Acquired,
        "withdrawn recommendation never releases an already granted physical lease");
}

using (var fixture = await Fixture.Create())
{
    await fixture.Call("SEND", 50, -1);
    await fixture.Observe("SEND", 50, resources: ["R", "S"]);
    await fixture.Acquire("SEND", ["R", "S"]);
    Task<string?> Verify(string step = "SEND", string task = "SEND", string vehicle = "V-SEND", string mapId = "M",
        string version = "V", string source = "A", string destination = "B") =>
        fixture.Manager.CheckStepReservationAsync(step, task, vehicle, mapId, version, source, destination, default);
    Check(await Verify() == null, "send guard accepts the exact persisted route with every original-step lease");
    Check(await Verify(step: "MISSING") != null, "send guard rejects missing route request");
    Check(await Verify(task: "OTHER") != null && await Verify(vehicle: "OTHER") != null,
        "send guard rejects task or vehicle identity mismatch");
    Check(await Verify(mapId: "OTHER") != null && await Verify(version: "OTHER") != null,
        "send guard rejects map or map-version mismatch");
    Check(await Verify(source: "OTHER") != null && await Verify(destination: "OTHER") != null,
        "send guard rejects route endpoint mismatch");
    var expired = DateTime.Now.AddDays(-1);
    await fixture.Db.Updateable<TrafficResourceLeaseEntity>().SetColumns(l => l.ExpiresAt == expired)
        .Where(l => l.TaskId == "SEND").ExecuteCommandAsync();
    Check(await Verify() == null, "send guard accepts unreleased physical rights despite expired heartbeat");
    await fixture.Db.Deleteable<TrafficResourceLeaseEntity>().Where(l => l.OwnerStepId == "SEND" && l.ResourceId == "S")
        .ExecuteCommandAsync();
    Check(await Verify() != null && (await fixture.Manager.GetHeldTaskIdsAsync(default)).Contains("SEND"),
        "send guard rejects missing required lease while retaining other physical rights");
    await fixture.Db.Updateable<TrafficRequestEntity>().SetColumns(r => r.State == "Waiting")
        .Where(r => r.StepId == "SEND").ExecuteCommandAsync();
    Check(await Verify() != null, "send guard rejects waiting state even with partial physical rights");
    await fixture.Manager.ReleaseStepAsync("SEND", default);
    Check(await Verify() != null, "send guard rejects a released route before the outbox can claim its command");
    await fixture.Call("EMPTY", 50, -1);
    await fixture.Observe("EMPTY", 50, resources: []);
    await fixture.Acquire("EMPTY", []);
    Check(await Verify(step: "EMPTY", task: "EMPTY", vehicle: "V-EMPTY") == null,
        "send guard accepts explicit NotRequired state for a resource-free route");
    await fixture.Db.Updateable<TrafficRequestEntity>().SetColumns(r => r.ResourceIds == "[\"R\"]")
        .Where(r => r.StepId == "EMPTY").ExecuteCommandAsync();
    Check(await Verify(step: "EMPTY", task: "EMPTY", vehicle: "V-EMPTY") != null,
        "send guard rejects nonempty resources falsely marked NotRequired");
    await fixture.Observe("SEND", 50, resources: ["R"]);
    await fixture.Acquire("SEND", ["R"]);
    await fixture.Db.Updateable<TrafficResourceLeaseEntity>().SetColumns(l => l.VehicleId == "OTHER")
        .Where(l => l.OwnerStepId == "SEND").ExecuteCommandAsync();
    Check(await Verify() != null, "send guard rejects lease owned by another vehicle");
    await fixture.Db.Updateable<TrafficResourceLeaseEntity>().SetColumns(l => new TrafficResourceLeaseEntity { VehicleId = "V-SEND", MapVersionId = "OLD" })
        .Where(l => l.OwnerStepId == "SEND").ExecuteCommandAsync();
    Check(await Verify() != null, "send guard rejects a lease from another map version");
    await fixture.Db.Updateable<TrafficRequestEntity>().SetColumns(r => r.ResourceIds == "invalid-json")
        .Where(r => r.StepId == "SEND").ExecuteCommandAsync();
    Check(await Verify() != null, "send guard rejects damaged resource metadata");
}

using (var fixture = await Fixture.Create())
{
    await fixture.Call("DONE", 1, -10); await fixture.Call("FAULT", 1, -5);
    await fixture.Observe("DONE", 1, resources: ["R"]); await fixture.Acquire("DONE", ["R"]);
    await fixture.Observe("FAULT", 1, resources: ["S"]); await fixture.Acquire("FAULT", ["S"]);
    await fixture.Db.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.Completed)
        .Where(t => t.TaskId == "DONE").ExecuteCommandAsync();
    await fixture.Db.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.Faulted)
        .Where(t => t.TaskId == "FAULT").ExecuteCommandAsync();
    var events = await fixture.Db.Queryable<TrafficLifecycleEventEntity>().CountAsync();
    var observed = await fixture.Manager.GetUnavailableResourceIdsAsync("READER", default);
    Check(observed.SetEquals(["R", "S"]) && await fixture.Db.Queryable<TrafficResourceLeaseEntity>().CountAsync() == 2 &&
        await fixture.Db.Queryable<TrafficLifecycleEventEntity>().CountAsync() == events,
        "candidate unavailable-resource read never prunes leases or writes lifecycle history");
    await fixture.Manager.PrepareDispatchCycleAsync(default);
    Check(!(await fixture.Manager.GetUnavailableResourceIdsAsync("READER", default)).Contains("R") &&
        await fixture.Db.Queryable<TrafficLifecycleEventEntity>().AnyAsync(e => e.TaskId == "DONE" && e.Kind == "Released"),
        "explicit traffic lifecycle preparation releases actually completed ownership with audit");
    Check((await fixture.Manager.GetHeldTaskIdsAsync(default)).SetEquals(["FAULT"]),
        "explicit lifecycle cleanup retains faulted task ownership");
}

var map = new ActiveMapRuntime();
var definition = new MapDefinition("fixture", "synthetic", "Cartesian", "m",
    [new("A", 0, 0, MapNodeType.Station, true, []), new("B", 1, 0, MapNodeType.Station, true, [])],
    [new("AB", "A", "B", 1, 1, true, true, [])], []);
map.Replace(new("M", "V", definition, DateTimeOffset.UtcNow));
var planner = new Planner(); var flow = new Flow(); var roads = new Roads(); var options = new DispatchOrchestratorOptions();
var cache = new DispatchRouteCache(planner, map, flow, roads, dispatch: options, capacity: 2);
var route = cache.Plan("A", "B", new HashSet<string>(["X", "Y"]));
cache.Plan("A", "B", new HashSet<string>(["Y", "X"]));
Check(planner.Calls == 1 && cache.Hits == 1, "same map and resource set reuses route independently of set order");
flow.Penalty = 3; cache.Plan("A", "B", new HashSet<string>(["X", "Y"]));
Check(planner.Calls == 2, "dynamic traffic penalty change invalidates route");
roads.Snapshot = roads.Snapshot with { Revision = 2 }; cache.Plan("A", "B", new HashSet<string>(["X", "Y"]));
Check(planner.Calls == 3, "road closure revision invalidates route");
map.Replace(new("M", "V", definition, DateTimeOffset.UtcNow)); cache.Plan("A", "B", new HashSet<string>(["X", "Y"]));
Check(planner.Calls == 4, "reactivation of same version gets a new map runtime identity");
options.StandbySites = ["B"]; cache.Plan("A", "B", new HashSet<string>(["X", "Y"]));
Check(planner.Calls == 5 && cache.EntryCount == 2, "protected standby configuration invalidates and LRU stays bounded");
planner.Result = null; cache.Plan("C", "D", new HashSet<string>()); cache.Plan("C", "D", new HashSet<string>());
Check(planner.Calls == 6, "unreachable route is cached under the complete context");
planner.Result = route; planner.BeforeReturn = () => roads.Snapshot = roads.Snapshot with { Revision = roads.Snapshot.Revision + 1 };
Check(cache.Plan("E", "F", new HashSet<string>()) == null, "context changing during route calculation discards result");
Console.WriteLine($"PASS {checks} rolling traffic fairness/cache checks; isolated database and synthetic maps only.");

sealed class Fixture : IDisposable
{
    public SqlSugar.ISqlSugarClient Db = null!;
    public TrafficResourceManager Manager = null!;
    public Clock Clock = new();
    private TrafficResourceCatalog _catalog = null!;
    public static async Task<Fixture> Create(int capacity = 1)
    {
        var fixture = new Fixture();
        fixture.Db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(Path.GetTempPath(), "agv-queue-" + Guid.NewGuid().ToString("N"), "fixture.db") });
        fixture._catalog = new([new("R", TrafficResourceType.NarrowAisle, capacity), new("S", TrafficResourceType.NarrowAisle)]);
        await new DatabaseInitializer(fixture.Db, fixture._catalog).InitializeAsync(default);
        fixture.Restart(); return fixture;
    }
    public void Restart() => Manager = new(Db, _catalog, new(TimeSpan.FromSeconds(1)), Clock);
    public async Task Call(string id, int priority, int minutes)
    {
        await Db.Insertable(new DispatchTaskEntity { TaskId = id, StationId = id, Priority = priority, Stage = 10,
            CreatedAt = Clock.Now.AddMinutes(minutes).LocalDateTime, UpdatedAt = Clock.Now.LocalDateTime }).ExecuteCommandAsync();
        await Db.Insertable(new DispatchTaskStepEntity { StepId = id, TaskId = id, Sequence = 1, StepType = 0, Status = 10,
            CreatedAt = Clock.Now.LocalDateTime, UpdatedAt = Clock.Now.LocalDateTime }).ExecuteCommandAsync();
    }
    public Task Observe(string id, double priority, bool waiting = false, string map = "M", string[]? resources = null) =>
        Manager.RecordRouteAsync(new(id, id, "V-" + id, "A", "B", [], resources ?? ["R"], waiting, "fixture")
            { MapId = map, MapVersionId = "V", QueuePriority = priority }, default);
    public Task Ordinary(string id, bool waiting = false) =>
        Manager.RecordRouteAsync(new(id, id, "V-" + id, "A", "B", [], ["R"], waiting, "ordinary system fixture")
            { MapId = "M", MapVersionId = "V" }, default);
    public Task<TrafficReservationResult> Acquire(string id, string[]? resources = null) =>
        Manager.TryAcquireAsync(id, id, "V-" + id, resources ?? ["R"], default);
    public void Dispose() => Db.Dispose();
}
sealed class Clock : TimeProvider
{
    public DateTimeOffset Now = new(2026, 11, 3, 4, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class Planner : IRoutePlanner
{
    public int Calls;
    public RoutePlan? Result = new(["A", "B"], ["AB"], [], 1);
    public Action? BeforeReturn;
    public RoutePlan? Plan(string source, string destination, IReadOnlySet<string> unavailableResourceIds)
    { Calls++; BeforeReturn?.Invoke(); return Result; }
}
sealed class Flow : ITrafficFlowMonitor
{
    public double Penalty;
    public IReadOnlyDictionary<string, double> GetEdgePenalties() => new Dictionary<string, double> { ["AB"] = Penalty };
    public TrafficFlowSnapshot GetSnapshot() => new(DateTimeOffset.UtcNow, "M", "V", [], []);
}
sealed class Roads : IRoadClosureService
{
    public RoadClosureSnapshot Snapshot = new(true, 1, "M", "V", "HASH", []);
    public RoadClosureSnapshot GetSnapshot() => Snapshot;
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<RoadClosureSnapshot> ListAsync(CancellationToken ct) => Task.FromResult(Snapshot);
    public Task<RoadClosureResult> CreateAsync(TrafficUserContext u, RoadClosureCreateRequest r, CancellationToken ct) => throw new NotSupportedException();
    public Task<RoadClosureResult> ReleaseAsync(TrafficUserContext u, RoadClosureReleaseRequest r, CancellationToken ct) => throw new NotSupportedException();
    public Task<RoadClosurePreview> PreviewAsync(RoadClosurePreviewRequest r, CancellationToken ct) => throw new NotSupportedException();
    public Task<IDisposable> EnterRouteSendAsync(IReadOnlyList<string> path, CancellationToken ct) => throw new NotSupportedException();
}
