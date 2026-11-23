using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using SqlSugar;

// D1: global-mode selection (DispatchQueueWindowService) applies the same predecessor and SelectAgv rules as the
// legacy loop: a dependent task waits for its predecessor, and an expired stop policy stops it before the window.
var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
var root = Path.Combine(Path.GetTempPath(), "agv-global-selection-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "isolated.db") });
var catalog = new TrafficResourceCatalog([new("R", TrafficResourceType.NarrowAisle)]);
await new DatabaseInitializer(db, catalog).InitializeAsync(default);
var tasks = new DispatchTaskRepository(db);
var orchestration = new DispatchOrchestrationRepository(db);
var traffic = new TrafficResourceManager(db, catalog, new(TimeSpan.FromSeconds(30)));
var queue = new DispatchQueueWindowService(orchestration, traffic);
var policy = new DispatchAssignmentOptions();

var predecessor = (await tasks.TryCreateAsync("ST-A", "DUMP", 50, default)).Task.TaskId;
await tasks.MarkAcknowledgedAsync(predecessor, default);
var dependent = (await tasks.TryCreateAsync("ST-B", "DUMP", 90, default)).Task.TaskId;
await tasks.MarkAcknowledgedAsync(dependent, default);
await db.Updateable<DispatchTaskEntity>().SetColumns(t => new DispatchTaskEntity
{
    DependsOnTaskIdsJson = JsonSerializer.Serialize(new[] { predecessor }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
}).Where(t => t.TaskId == dependent).ExecuteCommandAsync();

var now = DateTimeOffset.UtcNow;
var first = await queue.LoadAsync(policy, [], now, default);
Check(first.Work.Any(t => t.TaskId == predecessor) && first.Work.All(t => t.TaskId != dependent),
    "global selection keeps a dependent task out of the window until its predecessor completes");
Check(first.Window.All(t => t.TaskId != dependent), "the optimiser window never contains the dependent task while it waits");

await db.Updateable<DispatchTaskEntity>().SetColumns(t => new DispatchTaskEntity { Stage = (int)DispatchTaskStage.Completed })
    .Where(t => t.TaskId == predecessor).ExecuteCommandAsync();
var second = await queue.LoadAsync(policy, [], now, default);
Check(second.Work.Any(t => t.TaskId == dependent), "the dependent task is selectable once its predecessor is completed");

// SelectAgv stop policy: a ManualRecovery timeout of 30 s stops the ready GoToPickup before the optimiser sees it.
var template = new TaskTemplateBinding("snapshot-select-timeout", 7, new string('B', 64), "DurableNineStepV1",
    [new("fromStation", "来源工位", "string", true, "station.pickupNode")],
    [new("vehicleId", "执行车辆", "string", false, "select-agv.vehicleId")],
    [new("vehicleId", "执行车辆", "string", "select-agv")],
    [new("select-agv", "SelectAgv", "选择车辆", ["fromStation"], ["vehicleId"],
        "车辆在线且路线可达", "ManualRecovery", "ManualRecovery", 30, false),
     new("go-to-pickup", "GoToPickup", "移动至取货点", ["vehicleId", "fromStation"], ["arrival"],
        "车辆在线且路线可达", "ManualRecovery", "ManualRecovery", 30, false)],
    [new("select-agv", "vehicleId", "go-to-pickup", "vehicleId", "string", false)],
    [new("guard-vehicle", "VehicleUnavailable", "EnterManualRecovery", "select-agv", "车辆异常时停止自动执行")]);
var timedOut = (await tasks.TryCreateAsync("ST-C", "DUMP", 10, default)).Task.TaskId;
await tasks.MarkAcknowledgedAsync(timedOut, default);
await db.Updateable<DispatchTaskEntity>().SetColumns(t => new DispatchTaskEntity
{
    TaskTemplateJson = JsonSerializer.Serialize(template, new JsonSerializerOptions(JsonSerializerDefaults.Web))
}).Where(t => t.TaskId == timedOut).ExecuteCommandAsync();
var candidate = (await orchestration.GetDispatchCandidatesAsync(100, default)).Single(t => t.TaskId == timedOut);
Check(candidate.TimeoutSeconds == 30 && candidate.TimeoutAction == "ManualRecovery", "the candidate carries the SelectAgv timeout from its template");
var late = candidate.PolicyStartedAt.AddMinutes(5);
var afterTimeout = await queue.LoadAsync(policy, [], late, default);
Check(afterTimeout.Work.All(t => t.TaskId != timedOut), "an expired SelectAgv stop policy removes the task from global selection");
var goToPickup = (await tasks.GetStepsAsync(timedOut, default)).First(s => s.StepType == DispatchTaskStepType.GoToPickup);
Check(goToPickup.Status != DispatchTaskStepStatus.Ready, "the expired SelectAgv policy stops the ready pickup step instead of leaving it selectable");

Console.WriteLine($"PASS: {checks} global selection checks; isolated SQLite only.");
