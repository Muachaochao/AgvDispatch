using System.Reflection;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;

// D4: a held step timeout is recorded once, not every 500 ms cycle, and the selection
// wait starts from a stable creation time rather than the task's last status write.
var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
var root = Path.Combine(Path.GetTempPath(), "agv-timeout-hold-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "isolated.db") });
var catalog = new TrafficResourceCatalog([new("R", TrafficResourceType.NarrowAisle)]);
await new DatabaseInitializer(db, catalog).InitializeAsync(default);
var tasks = new DispatchTaskRepository(db);
var orchestration = new DispatchOrchestrationRepository(db);

var created = await tasks.TryCreateAsync("ST", "DUMP", 50, default);
await tasks.MarkAcknowledgedAsync(created.Task.TaskId, default);
var step = (await tasks.GetStepsAsync(created.Task.TaskId, default)).First();

// Selection start is the creation time and does not move when the task row is rewritten.
var candidate = (await orchestration.GetDispatchCandidatesAsync(100, default)).Single(t => t.TaskId == created.Task.TaskId);
await db.Updateable<DispatchTaskEntity>().SetColumns(t => new DispatchTaskEntity { UpdatedAt = DateTime.Now.AddHours(1) })
    .Where(t => t.TaskId == created.Task.TaskId).ExecuteCommandAsync();
var rewritten = (await orchestration.GetDispatchCandidatesAsync(100, default)).Single(t => t.TaskId == created.Task.TaskId);
Check(rewritten.PolicyStartedAt == candidate.PolicyStartedAt && candidate.PolicyStartedAt == candidate.CreatedAt,
    "the vehicle-selection wait starts at task creation and ignores later status writes");

var clock = new ManualClock(DateTimeOffset.UtcNow);
var journal = new Journal();
var coordinator = new DispatchTaskStepCoordinator(NullLogger<DispatchTaskStepCoordinator>.Instance,
    new DispatchTaskStepOptions(), new DispatchOrchestratorOptions(), new DispatchTaskCreationOptions { DumperStationId = "DUMP" },
    orchestration, null!, null!, null!, null!, null!, null!, journal: journal, clock: clock);
var apply = typeof(DispatchTaskStepCoordinator).GetMethod("ApplyTimeoutPolicyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
async Task<bool> Apply(DispatchTaskStepWorkItem item) => (bool)await (Task<bool>)apply.Invoke(coordinator, [item, CancellationToken.None])!;

var started = clock.Now;
var queued = (await orchestration.GetReadyStepsAsync(100, default)).Single(row => row.StepId == step.StepId);
clock.Now = queued.StartedAt!.Value.AddSeconds(901);
Check(!await Apply(queued) && journal.Stopped == 0 && journal.Holds == 0 &&
      (await orchestration.GetDispatchCandidatesAsync(100, default)).Any(row => row.TaskId == queued.TaskId),
    "a GoToPickup step waiting for selection remains queued past the execution timeout");
Check(!await Apply(queued with { TemplateNodeId = "pickup", TimeoutAction = TaskTemplateExecutionPolicy.Terminate,
    TimeoutSeconds = 1 }) && journal.Stopped == 0,
    "a GoToPickup template timeout cannot stop a task still waiting for selection");

await db.Updateable<DispatchTaskEntity>().SetColumns(task => new DispatchTaskEntity
    { Stage = (int)DispatchTaskStage.PickingUp, VehicleId = "AGV-01" })
    .Where(task => task.TaskId == created.Task.TaskId).ExecuteCommandAsync();
var pickup = (await tasks.GetStepsAsync(created.Task.TaskId, default)).Single(row => row.StepType == DispatchTaskStepType.PickupFullBox);
await db.Updateable<DispatchTaskStepEntity>().SetColumns(row => new DispatchTaskStepEntity
    { Status = (int)DispatchTaskStepStatus.Ready, VehicleId = "AGV-01", StartedAt = started.LocalDateTime })
    .Where(row => row.StepId == pickup.StepId).ExecuteCommandAsync();
var item = (await orchestration.GetReadyStepsAsync(100, default)).Single(row => row.StepId == pickup.StepId)
    with { TemplateNodeId = "pickup-box", TimeoutAction = TaskTemplateExecutionPolicy.NotifyAndHold,
        TimeoutSeconds = 10, StartedAt = started };
clock.Now = started.AddSeconds(11);
Check(!await Apply(item), "a timed-out notify-and-hold step is not stopped automatically");
Check(journal.Holds == 1, "the first timeout hold writes one evidence record");
for (var i = 0; i < 20; i++)
{
    clock.Now = clock.Now.AddMilliseconds(500);
    await Apply(item);
}
Check(journal.Holds == 1, "repeated 500 ms cycles do not rewrite the same timeout hold, even as elapsed seconds grow");
clock.Now = clock.Now.AddSeconds(60);
Check(!await Apply(item) && journal.Holds == 1, "the hold stays recorded once while the step remains held");
Check((await orchestration.GetReadyStepsAsync(100, default)).Any(row => row.StepId == pickup.StepId),
    "the held work step remains ready");

var defaultItem = item with
{
    TemplateNodeId = "",
    TimeoutAction = TaskTemplateExecutionPolicy.NotifyAndHold,
    TimeoutSeconds = 0,
    StartedAt = clock.Now
};
clock.Now = clock.Now.AddSeconds(901);
Check(await Apply(defaultItem) && journal.Stopped == 1,
    "a task without a template still receives the visible per-step watchdog and stops automation");
Check((await tasks.GetStepsAsync(created.Task.TaskId, default)).Single(row => row.StepId == pickup.StepId).Status ==
      DispatchTaskStepStatus.ManualIntervention,
    "the implicit watchdog leaves the original step and responsibility visible for manual recovery");

var stepOptions = new DispatchTaskStepOptions();
var noConfiguration = TaskWatchdogPolicyResolver.ResolveStep(DispatchTaskStepType.GoToPickup, "", 0,
    null, stepOptions, null);
Check(noConfiguration is { TimeoutSeconds: 900, Action: TaskTemplateExecutionPolicy.ManualRecovery, Source: "Default" },
    "the Implicit watchdog defaults are configurable, bounded, and use manual recovery instead of indefinite hold");

var parameterRuntime = new TaskParameterRuntime(new DispatchOrchestratorOptions(),
    new DispatchTaskCreationOptions { DumperStationId = "DUMP" });
var defaultTimeouts = Enum.GetValues<DispatchTaskStepType>()
    .Select(type => new TaskStepTimeoutParameter(type, type == DispatchTaskStepType.PickupFullBox ? 7 : 0)).ToArray();
var parameterConfiguration = new TaskParameterConfiguration("DUMP", "WAIT", "DMP",
    [new TaskPickupParameter("ST", "PICK")],
    [
        new(DispatchTaskStepType.GoToPickup, "Wait", null, null, null, null),
        new(DispatchTaskStepType.DeliverFullBoxToDumper, "Wait", null, null, null, null),
        new(DispatchTaskStepType.EnterDumpingPosition, "Wait", null, null, null, null),
        new(DispatchTaskStepType.ReturnEmptyBox, "Wait", null, null, null, null)
    ])
{
    WaitingSites = ["WAIT"],
    StepTimeouts = defaultTimeouts
};
parameterRuntime.Apply(new(1, "r1", "request", "MAP", "VERSION", parameterConfiguration, []));
var savedPolicy = TaskWatchdogPolicyResolver.ResolveStep(DispatchTaskStepType.PickupFullBox, "", 0,
    null, stepOptions, parameterRuntime);
Check(savedPolicy is { TimeoutSeconds: 7, Source: "Parameters" },
    "a persisted task-parameter threshold overrides the fail-safe default without changing its zero-disables semantics");

Console.WriteLine($"PASS: {checks} timeout hold checks; isolated SQLite and a manual clock only.");

sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class Journal : IExecutionEvidenceJournal
{
    public int Holds;
    public int Stopped;
    public IDisposable BeginScope(string taskId, string stepId) => new Scope();
    public Task RecordAsync(string taskId, string stepId, string kind, object evidence, CancellationToken ct)
    {
        if (kind == "NodeTimeoutHold") Holds++;
        if (kind == "NodeTimeoutStopped") Stopped++;
        return Task.CompletedTask;
    }
    public Task RecordCurrentAsync(string kind, object evidence, CancellationToken ct) => Task.CompletedTask;
    private sealed class Scope : IDisposable { public void Dispose() { } }
}
