using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;

var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception("FAILED: " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}

var root = Path.Combine(Path.GetTempPath(), "AGV-startup-lease-sweep-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var database = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "test.db") });
var catalog = new TrafficResourceCatalog(
[
    new("R-ORPHAN", TrafficResourceType.NarrowAisle), new("R-PREPARED", TrafficResourceType.NarrowAisle),
    new("R-EXEC", TrafficResourceType.NarrowAisle), new("R-CANCEL", TrafficResourceType.NarrowAisle)
]);
await new DatabaseInitializer(database, catalog).InitializeAsync(default);
var manager = new TrafficResourceManager(database, catalog, new(TimeSpan.FromMinutes(2)));

void Step(string stepId, DispatchTaskStepStatus status) => database.Insertable(new DispatchTaskStepEntity
{
    StepId = stepId, TaskId = "T-" + stepId, Sequence = 1, StepType = (int)DispatchTaskStepType.GoToPickup,
    Status = (int)status, VehicleId = "V1", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
}).ExecuteCommand();

// Lease taken before the command was registered, then a crash: Ready, leased, no command.
Step("S-ORPHAN", DispatchTaskStepStatus.Ready);
Check((await manager.TryAcquireAsync("S-ORPHAN", "T-S-ORPHAN", "V1", ["R-ORPHAN"], default)).Acquired,
    "模拟崩溃前已取得的就绪步骤路权");
// Ready step whose command was registered: this is a normal pending send, not an orphan.
Step("S-PREPARED", DispatchTaskStepStatus.Ready);
Check((await manager.TryAcquireAsync("S-PREPARED", "T-S-PREPARED", "V1", ["R-PREPARED"], default)).Acquired,
    "已登记命令的就绪步骤路权取得");
database.Insertable(new AgvCommandOutboxEntity
{
    CommandId = "CMD-PREPARED", IdempotencyKey = "STEP:S-PREPARED:TRANSPORT", TaskId = "T-S-PREPARED", StepId = "S-PREPARED",
    VehicleId = "V1", CommandType = (int)AgvCommandType.Transport, Status = (int)AgvCommandStatus.Pending,
    PayloadJson = "{}", NextAttemptAt = DateTime.Now, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
}).ExecuteCommand();
// An executing step keeps its window leases; the sweep must never touch it.
Step("S-EXEC", DispatchTaskStepStatus.Executing);
Check((await manager.TryAcquireAsync("S-EXEC", "T-S-EXEC", "V1", ["R-EXEC"], default)).Acquired,
    "执行中步骤路权取得");

var swept = await manager.RollbackAbandonedReadyStepLeasesAsync(default);
Check(swept == 1, "启动扫描只回滚一个无命令的就绪步骤");
var remaining = database.Queryable<TrafficResourceLeaseEntity>().Select(l => l.OwnerStepId).ToList();
Check(!remaining.Contains("S-ORPHAN"), "崩溃遗留的无命令就绪路权被启动扫描释放");
Check(remaining.Contains("S-PREPARED") && remaining.Contains("S-EXEC"),
    "已登记命令的就绪步骤和执行中步骤的路权保持不变");
Check(await manager.RollbackAbandonedReadyStepLeasesAsync(default) == 0,
    "重复启动扫描幂等");

// The step rollback must not be defeated by a token that is already cancelled (R15).
Step("S-CANCEL", DispatchTaskStepStatus.Ready);
Check((await manager.TryAcquireAsync("S-CANCEL", "T-S-CANCEL", "V1", ["R-CANCEL"], default)).Acquired,
    "取消回滚测试的路权取得");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    await manager.ReleaseStepAsync("S-CANCEL", cancelled.Token);
}
Check(!database.Queryable<TrafficResourceLeaseEntity>().Any(l => l.OwnerStepId == "S-CANCEL"),
    "已取消的令牌不会使步骤回滚留下路权");

Console.WriteLine($"Traffic startup lease sweep: {checks} checks passed.");
