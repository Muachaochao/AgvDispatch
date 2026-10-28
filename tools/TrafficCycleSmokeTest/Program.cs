using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

var checks = 0;
void Check(bool passed, string name) { if (!passed) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
foreach (var size in new[] { 3, 5, 12 })
{
    await using var f = await Fixture.Create(Enumerable.Range(0, size).Select(i => ("R" + i, 1)).ToArray());
    for (var i = 0; i < size; i++) { await f.Task("T" + i); await f.Hold("T" + i, "R" + i); }
    for (var i = 0; i < size; i++) await f.Wait("T" + i, "R" + ((i + 1) % size));
    var leases = f.Db.Queryable<TrafficResourceLeaseEntity>().Count();
    var events = f.Db.Queryable<TrafficLifecycleEventEntity>().Count();
    var page = await f.Control.GetWaitingAsync(new(PageSize: 1), default);
    var risk = page.CycleRisks.Single();
    Check(risk.TaskIds.Count == size && risk.VehicleIds.Count == size && risk.ResourceIds.Count == size && risk.Links.Count == size,
        $"{size}任务循环包含完整任务、车辆、资源与持有步骤证据");
    Check(page.Items.Count == 1 && page.TotalWaiting == size &&
        page.Items[0].Reason.Contains("闭合资源等待风险"), $"{size}车全量分析不受等待页大小限制并在行原因显示风险");
    var filtered = await f.Control.GetWaitingAsync(new("NOT-IN-THIS-MAP", 100), default);
    Check(filtered.TotalMatched == 0 && filtered.CycleRisks.Single().CycleId == risk.CycleId, "筛选不会隐藏全场循环或改变稳定编号");
    Check(risk.Message.Contains("不证明物理死锁") && risk.Message.Contains("不自动解锁"), "风险不宣称物理死锁或授予解锁许可");
    Check(f.Db.Queryable<TrafficResourceLeaseEntity>().Count() == leases && f.Db.Queryable<TrafficLifecycleEventEntity>().Count() == events,
        "全量诊断为只读，查询不修改路权及历史");

    await f.Db.Updateable<TrafficResourceLeaseEntity>().SetColumns(l => l.ExpiresAt == DateTime.Now.AddDays(-1))
        .Where(l => l.LeaseId != "").ExecuteCommandAsync();
    var expired = (await f.Control.GetWaitingAsync(new(), default)).CycleRisks.Single();
    Check(expired.CycleId == risk.CycleId && expired.Links.All(l => l.HeartbeatExpired), "心跳过期仍保留循环证据及占用，编号不漂移");
    f.Control = new(f.Db, f.Catalog);
    Check((await f.Control.GetWaitingAsync(new(), default)).CycleRisks.Single().CycleId == risk.CycleId,
        "重建查询组件后从持久化原归属复现风险");
    var reloaded = new TrafficResourceManager(f.Db, f.Catalog, new(TimeSpan.FromSeconds(20)));
    Check(!(await reloaded.TryAcquireAsync("WAIT-T0", "T0", "VEH-T0", ["R1"], default)).Acquired,
        "重建资源管理器不会因租约过期授予冲突资源");
    await f.Manager.ReleaseTaskAsync("T0", default);
    var cleared = await f.Control.GetWaitingAsync(new(), default);
    Check(cleared.CycleRisks.Count == 0 && f.Db.Queryable<TrafficResourceLeaseEntity>().Count() == leases - 1,
        "执行既有正常释放后风险消失，其余车辆占用保留");
}

await using (var f = await Fixture.Create(("A", 1), ("B", 1), ("C", 1)))
{
    foreach (var id in new[] { "A", "B", "C", "QUEUE-1", "QUEUE-2" }) await f.Task(id);
    await f.Hold("A", "A"); await f.Hold("B", "B"); await f.Hold("C", "C");
    await f.Wait("A", "B"); await f.Wait("B", "C");
    await f.Wait("QUEUE-1", "B", "C"); await f.Wait("QUEUE-2", "B", "C");
    var page = await f.Control.GetWaitingAsync(new(), default);
    Check(page.TotalWaiting == 4 && page.CycleRisks.Count == 0, "多车排队及菱形汇合不当作循环等待");
}
await using (var f = await Fixture.Create(("R", 2), ("A", 1)))
{
    foreach (var id in new[] { "A", "B", "C" }) await f.Task(id);
    await f.Hold("A", "A"); await f.Hold("B", "R"); await f.Hold("C", "R");
    await f.Wait("A", "R"); await f.Wait("B", "A");
    Check((await f.Control.GetWaitingAsync(new(), default)).CycleRisks.Count == 0,
        "容量2存在可完成的第三方占用时消减图环，不误报闭合等待");
    await f.Wait("C", "A");
    var risk = (await f.Control.GetWaitingAsync(new(), default)).CycleRisks.Single();
    Check(risk.TaskIds.Count == 3 && risk.Links.Count == 4, "容量2两个占用方都被阻塞才报告三任务闭合风险");
    await f.Db.Updateable<DispatchTaskStepEntity>().SetColumns(s => s.Status == (int)DispatchTaskStepStatus.Completed)
        .Where(s => s.StepId == "WAIT-C").ExecuteCommandAsync();
    Check((await f.Control.GetWaitingAsync(new(), default)).CycleRisks.Count == 0,
        "已结束的历史等待步骤不继续制造容量循环");
}
await using (var f = await Fixture.Create(("R", 2), ("A", 1)))
{
    await f.Task("A"); await f.Task("B");
    await f.Hold("A", "A"); await f.Hold("B", "R"); await f.Hold("B", "R", "HOLD-B-SECOND");
    await f.Wait("B", "A"); await f.Wait("A", "R");
    Check((await f.Control.GetWaitingAsync(new(), default)).CycleRisks.Count == 0,
        "同任务多个占用步骤只占一个容量，不膨胀为多个阻塞方");
}
await using (var f = await Fixture.Create(("R", 1), ("A", 1)))
{
    await f.Task("A"); await f.Task("B"); await f.Hold("A", "A"); await f.Hold("B", "R");
    await f.Wait("A", "R"); await f.Wait("B", "A");
    await f.Db.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.CancellationReview)
        .Where(t => t.TaskId == "B").ExecuteCommandAsync();
    var page = await f.Control.GetWaitingAsync(new(), default);
    Check(page.CycleRisks.Count == 0 && f.Db.Queryable<TrafficResourceLeaseEntity>().Count() == 2,
        "取消核对不沿用旧等待产生循环，也不清除其原占用");
    await f.Db.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.GoingToPickup)
        .Where(t => t.TaskId == "B").ExecuteCommandAsync();
    await f.Db.Updateable<TrafficRequestEntity>().SetColumns(r => r.VehicleId == "WRONG-VEHICLE")
        .Where(r => r.StepId == "WAIT-B").ExecuteCommandAsync();
    Check((await f.Control.GetWaitingAsync(new(), default)).CycleRisks.Count == 0,
        "请求车辆与任务归属不符时不编造循环链证据");
}
await using (var f = await Fixture.Create(("A", 1), ("B", 1), ("C", 1), ("D", 1)))
{
    foreach (var id in new[] { "A", "B", "C", "D", "TAIL" }) await f.Task(id);
    foreach (var id in new[] { "A", "B", "C", "D" }) await f.Hold(id, id);
    await f.Wait("A", "B"); await f.Wait("B", "A"); await f.Wait("C", "D"); await f.Wait("D", "C"); await f.Wait("TAIL", "A");
    var page = await f.Control.GetWaitingAsync(new(), default);
    Check(page.CycleRisks.Count == 2 && page.CycleRisks.All(r => r.TaskIds.Count == 2 && !r.TaskIds.Contains("TAIL")),
        "独立循环分组稳定去重，等待循环的普通上游队列不混入成员");
    await f.Db.Updateable<TrafficRequestEntity>().SetColumns(r => r.WaitingCategory == "Equipment")
        .Where(r => r.StepId == "WAIT-A").ExecuteCommandAsync();
    page = await f.Control.GetWaitingAsync(new(), default);
    Check(page.CycleRisks.Count == 1 && page.CycleRisks[0].TaskIds.Contains("C"),
        "设备PLC许可等待不冒充道路资源循环");
    await f.Db.Updateable<TrafficResourceLeaseEntity>().SetColumns(l => l.VehicleId == "WRONG-OWNER")
        .Where(l => l.TaskId == "C").ExecuteCommandAsync();
    Check((await f.Control.GetWaitingAsync(new(), default)).CycleRisks.Count == 0,
        "占用车辆与当前任务身份冲突时不拼出确定循环证据");
}
Console.WriteLine($"Traffic cycle analysis: {checks} checks passed. Isolated SQLite only; no vehicle or PLC connection.");

sealed class Fixture : IAsyncDisposable
{
    public ISqlSugarClient Db = null!;
    public TrafficResourceCatalog Catalog = null!;
    public TrafficResourceManager Manager = null!;
    public TrafficResourceControlService Control = null!;
    private string folder = "";
    public static async Task<Fixture> Create(params (string Id, int Capacity)[] resources)
    {
        var f = new Fixture { folder = Path.Combine(Path.GetTempPath(), "agv-traffic-cycles-" + Guid.NewGuid().ToString("N")) };
        f.Db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(f.folder, "private.db") });
        f.Catalog = new(resources.Select(r => new TrafficResourceDefinition(r.Id, TrafficResourceType.NarrowAisle, r.Capacity)));
        await new DatabaseInitializer(f.Db, f.Catalog).InitializeAsync(default);
        f.Manager = new(f.Db, f.Catalog, new(TimeSpan.FromSeconds(20))); f.Control = new(f.Db, f.Catalog); return f;
    }
    public async Task Task(string id)
    {
        await Db.Insertable(new DispatchTaskEntity { TaskId = id, StationId = "ST-" + id, VehicleId = "VEH-" + id,
            Stage = (int)DispatchTaskStage.GoingToPickup, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommandAsync();
    }
    private async Task Step(string task, string id)
    {
        if (await Db.Queryable<DispatchTaskStepEntity>().AnyAsync(s => s.StepId == id)) return;
        var sequence = await Db.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == task).CountAsync() + 1;
        await Db.Insertable(new DispatchTaskStepEntity { TaskId = task, StepId = id, Status = (int)DispatchTaskStepStatus.Pending,
            StepType = (int)DispatchTaskStepType.GoToPickup, Sequence = sequence,
            CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommandAsync();
    }
    public async Task Hold(string task, string resource, string? stepId = null)
    {
        var step = stepId ?? "HOLD-" + task; await Step(task, step);
        await Manager.RecordRouteAsync(new(step, task, "VEH-" + task, "FROM", "TO", [], [resource], false, "申请资源") { MapId = "M", MapVersionId = "V" }, default);
        if (!(await Manager.TryAcquireAsync(step, task, "VEH-" + task, [resource], default)).Acquired) throw new Exception("fixture acquire " + task);
    }
    public async Task Wait(string task, params string[] resources)
    {
        var step = "WAIT-" + task; await Step(task, step);
        await Manager.RecordRouteAsync(new(step, task, "VEH-" + task, "FROM", "TO", [], resources, true, "等待容量") { MapId = "M", MapVersionId = "V" }, default);
    }
    public ValueTask DisposeAsync() { Db.Dispose(); return ValueTask.CompletedTask; }
}
