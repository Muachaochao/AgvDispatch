using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;

var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception("FAILED: " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}

var root = Path.Combine(Path.GetTempPath(), "AGV-window-rollback-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var database = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "test.db") });
await new DatabaseInitializer(database).InitializeAsync(default);
var catalog = new TrafficResourceCatalog(
[
    new("STOP", TrafficResourceType.Station),
    new("NEXT", TrafficResourceType.NarrowAisle),
    new("EDGE-OLD", TrafficResourceType.NarrowAisle),
    new("BAY", TrafficResourceType.Station),
    new("AUTO-CORRIDOR-OLD", TrafficResourceType.SingleLane),
    new("CORR", TrafficResourceType.NarrowAisle),
    new("CORR2", TrafficResourceType.NarrowAisle)
]);
var manager = new TrafficResourceManager(database, catalog, new(TimeSpan.FromMinutes(2)));

// Seed a step that stands at STOP (its current window boundary), still covers EDGE-OLD and
// an automatic guard with its body, and is bound to the waiting bay BAY.
await manager.TryAcquireAsync("S-ROLL", "T-ROLL", "V1", ["STOP", "EDGE-OLD", "AUTO-CORRIDOR-OLD", "BAY"], default);
database.Insertable(new TaskWaitingBayEntity
{
    TaskId = "T-ROLL", VehicleId = "V1", StepId = "S-ROLL", MapId = "MAP", MapVersionId = "V1",
    WaitingSite = "BAY-SITE", ResourceId = "BAY", SelectedAt = DateTime.Now
}).ExecuteCommand();
var heldBefore = (await manager.GetStepHeldResourceIdsAsync("T-ROLL", "S-ROLL", default))!;
Check(heldBefore.Count == 4, "起始状态持有停点、车体覆盖路段、自动走廊和候车位");

// The next window starts at the stop, so it already contains STOP. Its acquisition adds NEXT.
var nextWindow = new AgvTransportRouteWindow(1, "STOP-SITE", "NEXT-SITE", ["STOP-SITE", "NEXT-SITE"], ["E-NEXT"],
    ["NEXT", "STOP"], true);
var acquired = await manager.TryAcquireAsync("S-ROLL", "T-ROLL", "V1", nextWindow.ResourceIds, default);
Check(acquired.Acquired, "下一窗口资源可以取得并新增租约");

// Failure: only the lease this attempt added (NEXT) is rolled back.
await manager.ReleaseStepExceptResourcesAsync("S-ROLL",
    DispatchTaskOrchestrator.RollbackKeepSet(heldBefore, "BAY"), "测试：命令准备失败的回滚", CancellationToken.None);
var afterFailure = (await manager.GetStepHeldResourceIdsAsync("T-ROLL", "S-ROLL", default))!.ToHashSet();
Check(afterFailure.SetEquals(heldBefore), "准备失败只回滚本次新增租约，车辆停点、车体覆盖与候车位仍保留");
Check(database.Queryable<TaskWaitingBayEntity>().Any(b => b.TaskId == "T-ROLL" && b.ResourceId == "BAY"),
    "准备失败后候车位绑定仍然存在");

// Success with a next window that does not list the bay: the bay must survive the handoff
// (the old code rebuilt the window without it and released the bay lease).
// The retry acquires the next window again (its NEXT lease was rolled back above) before the handoff.
Check((await manager.TryAcquireAsync("S-ROLL", "T-ROLL", "V1", nextWindow.ResourceIds, default)).Acquired,
    "重试取得下一窗口资源");
var handoff = DispatchTaskOrchestrator.HandoffKeepSet(nextWindow.ResourceIds, afterFailure, "BAY");
await manager.ReleaseStepExceptResourcesAsync("S-ROLL", handoff, "测试：窗口交接", CancellationToken.None);
var afterHandoff = (await manager.GetStepHeldResourceIdsAsync("T-ROLL", "S-ROLL", default))!.ToHashSet();
Check(afterHandoff.Contains("BAY") && afterHandoff.Contains("NEXT") && afterHandoff.Contains("STOP"),
    "窗口交接后候车位、下一窗口与停点资源保留");
Check(!afterHandoff.Contains("EDGE-OLD"), "窗口交接释放已不再占用的旧路段");
Check(afterHandoff.Contains("AUTO-CORRIDOR-OLD"), "未经几何核验的自动走廊资源在交接时保留");
Check(await manager.GetAsync("T-ROLL", default) is not null, "窗口交接后候车位绑定仍然存在");

// Attaching the bound bay re-adds it to every remaining window, never to issued ones.
var windows = new[]
{
    new AgvTransportRouteWindow(0, "A", "B", ["A", "B"], ["E1"], ["E1-RES"], false),
    new AgvTransportRouteWindow(1, "B", "C", ["B", "C"], ["E2"], ["E2-RES"], false),
    new AgvTransportRouteWindow(2, "C", "D", ["C", "D"], ["E3"], ["E3-RES"], true)
};
var attached = DispatchTaskOrchestrator.AttachWaitingBay(windows, 1, "BAY");
Check(!attached[0].ResourceIds.Contains("BAY") && attached[1].ResourceIds.Contains("BAY") &&
      attached[2].ResourceIds.Contains("BAY"), "候车位只附加到已下发窗口之后的剩余窗口");
Check(ReferenceEquals(DispatchTaskOrchestrator.AttachWaitingBay(windows, 1, null), windows),
    "无绑定候车位时窗口保持原样");

// R11: a diagnostic route must not join the FIFO queue; a real acquisition still does.
var high = new TrafficRouteObservation("S-H", "T-H", "V-H", "A", "B", ["E-CORR"], ["CORR"], true, "诊断路线")
{ QueueEligible = false };
await manager.RecordRouteAsync(high, default);
var lowAfterDiagnostic = await manager.TryAcquireAsync("S-L", "T-L", "V-L", ["CORR"], default);
Check(lowAfterDiagnostic.Acquired, "诊断路线不排队，后续低优先级任务可以取得空闲资源");

var queued = new TrafficRouteObservation("S-H2", "T-H2", "V-H2", "A", "B", ["E-CORR2"], ["CORR2"], true, "真实申请")
{ QueueEligible = true };
await manager.RecordRouteAsync(queued, default);
var lowBehindQueue = await manager.TryAcquireAsync("S-L2", "T-L2", "V-L2", ["CORR2"], default);
Check(!lowBehindQueue.Acquired, "真实排队的前序申请仍然阻止后续任务越过");

Console.WriteLine($"Traffic window rollback: {checks} checks passed.");
