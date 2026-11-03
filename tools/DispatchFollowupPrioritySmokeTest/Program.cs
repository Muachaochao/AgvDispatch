using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

var checks = 0;
void Check(bool condition, string message)
{ if (!condition) throw new InvalidOperationException(message); checks++; Console.WriteLine("PASS " + message); }

foreach (var type in new[] { DispatchTaskStepType.DeliverFullBoxToDumper, DispatchTaskStepType.EnterDumpingPosition,
    DispatchTaskStepType.ReturnEmptyBox, DispatchTaskStepType.WaitForDumperReady })
{
    using var fixture = await Fixture.Create(type);
    var scores = await fixture.Layer.PrioritiesAsync(default);
    Check(scores["LOW"].EffectivePriority == 60 && scores["HIGH"].EffectivePriority == 82,
        type + ": same original priority and call-age score used after assignment");
    var low = fixture.Observation("LOW"); var high = fixture.Observation("HIGH");
    await fixture.Layer.ObserveWaitingAsync([low with { QueuePriority = 9999 }, high with { QueuePriority = -1 }], default);
    var recorded = await fixture.Db.Queryable<TrafficRequestEntity>().Where(r => r.StepId == "LOW-STEP").SingleAsync();
    Check(recorded.QueuePriority == 60 && recorded.QueueObservedAtUtc == fixture.Clock.Now.UtcDateTime,
        type + ": real task score replaces stale or caller-supplied queue priority");
    var rejected = await fixture.Layer.AcquireAsync(low with { Waiting = false }, default);
    Check(!rejected.Acquired && rejected.BlockingTaskId == "HIGH" && await fixture.Db.Queryable<TrafficResourceLeaseEntity>().CountAsync() == 0,
        type + ": low-priority first poll cannot take a free conflict point from the observed higher-priority step");
    Check((await fixture.Layer.AcquireAsync(high with { Waiting = false }, default)).Acquired,
        type + ": high-priority subsequent step acquires real transactional resource");
    await fixture.Db.Updateable<DispatchTaskEntity>().SetColumns(t => t.Priority == 999).Where(t => t.TaskId == "LOW").ExecuteCommandAsync();
    Check(!(await fixture.Layer.AcquireAsync(low with { Waiting = false }, default)).Acquired &&
        (await fixture.Db.Queryable<TrafficResourceLeaseEntity>().SingleAsync()).TaskId == "HIGH",
        type + ": increasing priority never preempts existing valid ownership");
    await fixture.Manager.ReleaseStepAsync("HIGH-STEP", default);
    Check((await fixture.Layer.AcquireAsync(low with { Waiting = false }, default)).Acquired &&
        await fixture.Db.Queryable<AgvCommandOutboxEntity>().CountAsync() == 0,
        type + ": normal verified release permits next request without creating a vehicle command");
}

using (var fixture = await Fixture.Create(DispatchTaskStepType.DeliverFullBoxToDumper))
{
    var map = new ActiveMapRuntimeSnapshot("M", "V", new("wait-bays", "synthetic", "Cartesian", "m",
        [new("P", 0, 0, MapNodeType.Station, true, []),
         new("W1", 1, 0, MapNodeType.WaitingPoint, true, ["WAIT-1"]),
         new("W2", 1, 1, MapNodeType.WaitingPoint, true, ["WAIT-2"]),
         new("D", 2, 0, MapNodeType.Station, true, [])], [],
        [new("WAIT-1", TrafficResourceType.Station, 1), new("WAIT-2", TrafficResourceType.Station, 1)]), fixture.Clock.Now);
    var options = new DispatchOrchestratorOptions { DumperWaitingSite = "W1", DumperWaitingSites = ["W1", "W2"], DumperDumpingSite = "D" };
    var planner = new Planner();
    var low = fixture.Step("LOW"); var high = fixture.Step("HIGH");
    await TaskWaitingBayRouter.ObserveWaitingAsync(low, "P", options, map, planner, fixture.Manager, fixture.Manager, fixture.Layer, default);
    await TaskWaitingBayRouter.ObserveWaitingAsync(high, "P", options, map, planner, fixture.Manager, fixture.Manager, fixture.Layer, default);
    var denied = await TaskWaitingBayRouter.AcquireAsync(low, "P", options, map, planner, fixture.Manager, fixture.Manager, default, fixture.Layer);
    Check(denied is null && await fixture.Manager.GetAsync("LOW", default) is null,
        "waiting-bay transaction cannot bind a low-priority contender ahead of a ready high-priority shared-route contender");
    var granted = await TaskWaitingBayRouter.AcquireAsync(high, "P", options, map, planner, fixture.Manager, fixture.Manager, default, fixture.Layer);
    Check(granted is not null && (await fixture.Manager.GetAsync("HIGH", default)) is { WaitingSite: "W1" },
        "higher-priority waiting bay and its entire route are bound atomically to the original task");
    Check(await fixture.Db.Queryable<AgvCommandOutboxEntity>().CountAsync() == 0,
        "waiting candidate publication and arbitration create no physical vehicle or PLC command");
}

using (var fixture = await Fixture.Create(DispatchTaskStepType.ReturnEmptyBox))
{
    var task = (await fixture.Repository.GetAssignedTasksAsync(default)).Single(t => t.TaskId == "LOW");
    var policy = new DispatchAssignmentOptions { StationDeadlineMinutes = new() { ["LOW"] = 12 } };
    var score = DispatchTaskPriority.Score(task, policy, fixture.Clock.Now);
    var direct = new DispatchAssignmentOptimizer().Score(DispatchTaskPriority.Input(task, policy), policy, fixture.Clock.Now);
    Check(score == direct && score.DeadlinePriority > 0 && score.CalledAt == task.CreatedAt,
        "assignment and later transport share deadline urgency and the same immutable original call time");
    await fixture.Layer.ObserveAsync(fixture.Observation("HIGH"), default);
    await fixture.Layer.SuspendRecommendationAsync(fixture.Observation("HIGH") with { Reason = "PLC not ready" }, default);
    Check((await fixture.Db.Queryable<TrafficRequestEntity>().Where(r => r.TaskId == "HIGH").SingleAsync()).QueuePriority is null &&
        (await fixture.Layer.AcquireAsync(fixture.Observation("LOW") with { Waiting = false }, default)).Acquired,
        "withdrawing an ineligible ungranted recommendation prevents a false priority queue head");
}
Console.WriteLine($"PASS {checks} follow-up priority checks; isolated SQLite, synthetic routes, no real vehicle/PLC calls.");

sealed class Fixture : IDisposable
{
    public SqlSugar.ISqlSugarClient Db = null!;
    public TrafficResourceManager Manager = null!;
    public DispatchOrchestrationRepository Repository = null!;
    public DispatchTrafficReservationService Layer = null!;
    public Clock Clock = new();
    private DispatchTaskStepType _type;
    public static async Task<Fixture> Create(DispatchTaskStepType type)
    {
        var fixture = new Fixture { _type = type };
        var directory = Path.Combine(Path.GetTempPath(), "agv-followup-priority-" + Guid.NewGuid().ToString("N"));
        fixture.Db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(directory, "test.db") });
        var catalog = new TrafficResourceCatalog([new("R", TrafficResourceType.NarrowAisle),
            new("WAIT-1", TrafficResourceType.Station), new("WAIT-2", TrafficResourceType.Station)]);
        await new DatabaseInitializer(fixture.Db, catalog).InitializeAsync(default);
        fixture.Manager = new(fixture.Db, catalog, new(TimeSpan.FromMinutes(1)), fixture.Clock);
        fixture.Repository = new(fixture.Db);
        var optimizer = new DispatchOptimizationService(Path.Combine(directory, "policy.json"), new() { Mode = "Global" }, NullLogger<DispatchOptimizationService>.Instance);
        fixture.Layer = new(fixture.Manager, optimizer, fixture.Repository, optimizer, fixture.Clock);
        foreach (var (id, priority, age) in new[] { ("LOW", 50, 10), ("HIGH", 80, 2) })
        {
            await fixture.Db.Insertable(new DispatchTaskEntity { TaskId = id, StationId = id, VehicleId = "V-" + id,
                Priority = priority, Stage = (int)fixture.Stage, CreatedAt = fixture.Clock.Now.AddMinutes(-age).LocalDateTime,
                UpdatedAt = fixture.Clock.Now.LocalDateTime }).ExecuteCommandAsync();
            await fixture.Db.Insertable(new DispatchTaskStepEntity { StepId = id + "-STEP", TaskId = id, VehicleId = "V-" + id,
                Sequence = 3, StepType = (int)type, Status = (int)DispatchTaskStepStatus.Ready,
                CreatedAt = fixture.Clock.Now.LocalDateTime, UpdatedAt = fixture.Clock.Now.LocalDateTime }).ExecuteCommandAsync();
        }
        return fixture;
    }
    private DispatchTaskStage Stage => _type switch
    {
        DispatchTaskStepType.DeliverFullBoxToDumper => DispatchTaskStage.PickingUp,
        DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStage.Dumping,
        _ => DispatchTaskStage.WaitingForDumper
    };
    public TrafficRouteObservation Observation(string taskId) => new(taskId + "-STEP", taskId, "V-" + taskId, "A", "B", ["AB"], ["R"], true, "follow-up")
        { MapId = "M", MapVersionId = "V", WaitingCategory = _type == DispatchTaskStepType.WaitForDumperReady ? "Equipment" : "Route" };
    public DispatchTaskStepWorkItem Step(string taskId) => new(taskId, taskId, "V-" + taskId, Stage, taskId + "-STEP", 3, _type,
        DispatchTaskStepStatus.Ready, null, null, Clock.Now);
    public void Dispose() => Db.Dispose();
}
sealed class Clock : TimeProvider
{
    public DateTimeOffset Now = new(2026, 11, 3, 4, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class Planner : IRoutePlanner
{
    public RoutePlan? Plan(string source, string destination, IReadOnlySet<string> unavailable)
    {
        var resources = new[] { "R", destination == "W1" ? "WAIT-1" : "WAIT-2" };
        return resources.Any(unavailable.Contains) ? null : new([source, destination], [source + destination], resources, destination == "W1" ? 1 : 2);
    }
}
