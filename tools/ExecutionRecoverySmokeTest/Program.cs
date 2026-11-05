using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;

var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
var ct = CancellationToken.None;
var admin = new TrafficUserContext("recovery-test\\engineer", "工程师", TrafficPermission.View | TrafficPermission.RecoverExecution);
var viewer = new TrafficUserContext("recovery-test\\viewer", "观察员", TrafficPermission.View);
async Task<RecoveryDecisionRequest> Request(Fixture f, string owner, RecoveryDecision decision = RecoveryDecision.ContinueOriginal)
{
    var status = await f.Recovery.GetAsync(ct); var ticket = status.Items.Single(i => i.TaskId == owner);
    return new(status.SessionId, ticket.TicketId, ticket.Revision, decision, "BOX-001 属于原任务", "现场已核对", true, true, true);
}
await using (var f = await Fixture.Create())
{
    Check(!f.Barrier.IsOpen && await f.Gate.EnterAsync(MapExecutionOperation.ExistingWork, ct) == null, "初始化前所有执行入口关闭");
    await f.Boot();
    Check(f.Barrier.IsOpen, "空数据库且未配置现场设备安全启动");
}
await using (var f = await Fixture.Create())
{
    var task = await f.Tasks.TryCreateAsync("PLC-01", "DUMPER", 1, ct);
    await f.Boot(); await f.Recovery.InspectAsync(ct);
    Check(f.Barrier.IsOpen, "完全未开始的请求可自动核对，不重建任务");
    var repeated = await f.Tasks.TryCreateAsync("PLC-01", "DUMPER", 1, ct);
    Check(!repeated.Created && repeated.Task.TaskId == task.Task.TaskId, "重复工位业务请求关联原任务");
}
await using (var f = await Fixture.Create())
{
    var id = await f.Prepare(); await f.Boot(); f.Fresh(); await f.Recovery.InspectAsync(ct);
    Check(!f.Barrier.IsOpen && !f.Gate.CanStartNewWork, "命令入库未发送时先冻结派单和发送");
    var dispatcher = new AgvCommandOutboxDispatcher(NullLogger<AgvCommandOutboxDispatcher>.Instance,
        new AgvCommandOutboxOptions { Enabled = true, PollIntervalMs = 100 }, f.Outbox, f.Gateway, f.Gate);
    await dispatcher.StartAsync(ct); await Task.Delay(250); await dispatcher.StopAsync(ct); dispatcher.Dispose();
    Check(f.Gateway.Sends == 0, "实际发送后台服务无法越过恢复屏障");
    var denied = false; try { await f.Recovery.DecideAsync(viewer, await Request(f, id), ct); } catch (UnauthorizedAccessException) { denied = true; }
    Check(denied, "只读账号不能处置恢复");
    var request = await Request(f, id);
    Check(!(await f.Recovery.DecideAsync(admin, request with { Reason = " " }, ct)).Accepted, "空原因仍拒绝，快捷原因可少于10字");
    Check(!(await f.Recovery.DecideAsync(admin, request with { LoadAndOwnershipConfirmed = false }, ct)).Accepted, "未确认载荷归属拒绝续跑");
    f.Vehicles.Items[0] = f.Vehicles.Items[0] with { CurrentStation = "OTHER" };
    Check(!(await f.Recovery.DecideAsync(admin, request, ct)).Accepted, "停机期间移动车辆，原未发送路线不再匹配");
    f.Fresh(); Check((await f.Recovery.DecideAsync(admin, request, ct)).Accepted, "原起点、原命令、载荷和PLC核对后允许准备续接");
    var status = await f.Recovery.GetAsync(ct);
    Check(!status.Released && status.Audits.Any(a => a.Decision == "ContinueOriginal"), "人工决定先持久化且不会立即解锁发送");
    f.Vehicles.Items[0] = f.Vehicles.Items[0] with { X = 2 };
    Check(!(await f.Recovery.ReleaseAsync(admin, new(status.SessionId, "全部核对一致申请恢复原任务执行"), ct)).Accepted, "确认后车辆物理位置再次变化，放行重新拒绝");
    f.Fresh(); await f.Recovery.InspectAsync(ct);
    status = await f.Recovery.GetAsync(ct);
    Check((await f.Recovery.ReleaseAsync(admin, new(status.SessionId, "全部核对一致申请恢复原任务执行"), ct)).Accepted, "放行提交成功后才开启执行屏障");
    f.Gate.SetMode(MapExecutionMode.Held);
    Check(await f.Gate.EnterAsync(MapExecutionOperation.ExistingWork, ct) == null, "启动恢复放行不能绕过地图切换屏障");
    await f.Boot(); f.Fresh(); await f.Recovery.InspectAsync(ct);
    Check(!f.Barrier.IsOpen && !(await f.Recovery.GetAsync(ct)).Items.Single().Verified, "恢复后再次断电启动，不能沿用上一批次人工确认");
}
await using (var f = await Fixture.Create())
{
    var id = await f.Prepare(); var command = (await f.Outbox.GetDueCommandsAsync(DateTimeOffset.Now, 10, ct)).Single();
    await f.Outbox.TryClaimAsync(command.CommandId, DateTimeOffset.Now, ct);
    var step = await f.Step(id); f.Gateway.Status = new(step.StepId, step.StepId, AgvTransportTaskState.GoingToDestination, "AGV-01", DateTimeOffset.Now);
    await f.Boot(); f.Fresh(); await f.Recovery.InspectAsync(ct);
    Check((await f.Recovery.DecideAsync(admin, await Request(f, id), ct)).Accepted, "车辆接受但应答未落库，按原StepId核对并补绑定");
    Check(f.Gateway.Sends == 0 && (await f.Step(id)).Status == (int)DispatchTaskStepStatus.Executing, "绑定原厂家任务，没有重复发送运动命令");
    Check(f.Db.Queryable<AgvCommandOutboxEntity>().Single().Status == (int)AgvCommandStatus.Sent, "命令、步骤、任务和审计原子补记");
}
await using (var f = await Fixture.Create())
{
    var id = await f.Prepare(); var command = (await f.Outbox.GetDueCommandsAsync(DateTimeOffset.Now, 10, ct)).Single();
    await f.Outbox.TryClaimAsync(command.CommandId, DateTimeOffset.Now, ct);
    await f.Boot(); f.Fresh(); await f.Recovery.InspectAsync(ct);
    Check(!(await f.Recovery.DecideAsync(admin, await Request(f, id), ct)).Accepted && !f.Barrier.IsOpen, "车辆历史任务被清理：禁止把未知当未执行重发");
    Check(f.Db.Queryable<AgvCommandOutboxEntity>().Single().Status == (int)AgvCommandStatus.Sending, "未知发送结果保留原发送状态，不自动回收租约");
}
await using (var f = await Fixture.Create())
{
    var id = await f.Prepare(); var command = (await f.Outbox.GetDueCommandsAsync(DateTimeOffset.Now, 10, ct)).Single();
    await f.Outbox.TryClaimAsync(command.CommandId, DateTimeOffset.Now, ct); await f.Outbox.MarkSentAsync(command.CommandId, command.StepId, ct);
    await f.Db.Insertable(new TrafficResourceLeaseEntity { LeaseId = "lease", ResourceId = "NARROW", OwnerStepId = command.StepId, TaskId = id, VehicleId = "AGV-01",
        AcquiredAt = DateTime.Now, RenewedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(-1) }).ExecuteCommandAsync();
    await f.Boot(); f.Fresh("SITE-B"); f.Gateway.Status = new(command.StepId, command.StepId, AgvTransportTaskState.Completed, "AGV-01", DateTimeOffset.Now);
    await f.Recovery.InspectAsync(ct);
    var request = await Request(f, id, RecoveryDecision.RecordPhysicalCompletion);
    // Inject a crash-equivalent persistence failure at the audit boundary.
    await f.Db.Ado.ExecuteCommandAsync("CREATE TRIGGER reject_recovery_audit BEFORE INSERT ON execution_evidence WHEN NEW.Kind='RecordPhysicalCompletion' BEGIN SELECT RAISE(ABORT,'audit-failure'); END;");
    var failed = false; try { await f.Recovery.DecideAsync(admin, request, ct); } catch (InvalidOperationException) { failed = true; }
    Check(failed && (await f.Step(id)).Status == (int)DispatchTaskStepStatus.Executing && !f.Barrier.IsOpen, "补记中断：步骤推进与审计一起回滚，保持冻结");
    await f.Db.Ado.ExecuteCommandAsync("DROP TRIGGER reject_recovery_audit;");
    Check((await f.Recovery.DecideAsync(admin, request, ct)).Accepted, "车辆已到站但数据库未记完成，核对后补记原运输");
    Check((await f.Step(id)).StepType == (int)DispatchTaskStepType.PickupFullBox && !f.Barrier.IsOpen, "到站补记只激活下一节点，PLC不直接执行");
    Check(f.Db.Queryable<TrafficResourceLeaseEntity>().Count() == 1 && f.Gateway.Sends == 0, "过期占用和物料关联保留，没有重复运动或误释放");
    Check(!(await f.Recovery.DecideAsync(admin, request, ct)).Accepted, "同一人工补记请求重复到达不会重复推进");
}
foreach (var physicalStep in new[] { DispatchTaskStepType.PickupFullBox, DispatchTaskStepType.DumpFullBox })
await using (var f = await Fixture.Create(new MapSwitchSafetySettings { CheckPlcSignals = true }))
{
    var id = await f.Prepare();
    await f.Db.Deleteable<AgvCommandOutboxEntity>().ExecuteCommandAsync();
    await f.Db.Updateable<DispatchTaskStepEntity>().SetColumns(s => new DispatchTaskStepEntity { Status = 50 }).Where(s => s.TaskId == id && s.StepType < (int)physicalStep).ExecuteCommandAsync();
    await f.Db.Updateable<DispatchTaskStepEntity>().SetColumns(s => new DispatchTaskStepEntity { Status = 10, VehicleId = "AGV-01" }).Where(s => s.TaskId == id && s.StepType == (int)physicalStep).ExecuteCommandAsync();
    await f.Db.Updateable<DispatchTaskEntity>().SetColumns(t => new DispatchTaskEntity { Stage = 30 }).Where(t => t.TaskId == id).ExecuteCommandAsync();
    await f.Boot(); f.Fresh(); f.Signals.Items.Add(SwitchSignalCache.Ready() with { VariableName = "BoxPresent", Value = false });
    await f.Recovery.InspectAsync(ct);
    Check(!f.Barrier.IsOpen, physicalStep + "断电后不以布尔位自动重做物理动作");
    Check(!(await f.Recovery.DecideAsync(admin, await Request(f, id, RecoveryDecision.RecordPhysicalCompletion), ct)).Accepted, physicalStep + "没有动作周期证据禁止强制完成");
    var plc = new CountingPlc(); var journal = new ExecutionEvidenceJournal(f.Db);
    var wrapped = new JournaledPlcGateway(plc, journal, f.Signals, f.Barrier);
    var blocked = false; try { await wrapped.WriteSignalAsync("PLC-01", "Start", true, ct); } catch (InvalidOperationException) { blocked = true; }
    Check(blocked && plc.Writes == 0, "PLC双方重启/恢复等待期间，网关写入口本身也关闭");
}
await using (var f = await Fixture.Create(new MapSwitchSafetySettings { RequiredVehicleIds = ["AGV-01"] }))
{
    await f.Boot(); await f.Recovery.InspectAsync(ct);
    Check(!f.Barrier.IsOpen, "无数据库任务但配置现场车辆时，须核对无主载荷和现场状态");
    f.Fresh(); f.Vehicles.Items[0] = f.Vehicles.Items[0] with { Tasks = [new("UNKNOWN-JOB", 2, null)] };
    await f.Recovery.InspectAsync(ct);
    Check(!(await f.Recovery.DecideAsync(admin, await Request(f, "__SITE__"), ct)).Accepted, "发现未关联车辆任务，不允许全场放行");
    f.Fresh(); await f.Recovery.InspectAsync(ct);
    Check((await f.Recovery.DecideAsync(admin, await Request(f, "__SITE__"), ct)).Accepted, "新鲜停车证据加人工空载归属确认后允许全场核对");
}
await using (var f = await Fixture.Create(new MapSwitchSafetySettings { RequiredVehicleIds = ["AGV-01"] }))
{
    await f.Boot(); f.Fresh();
    f.Vehicles.Items.Add(SwitchVehicleCache.Ready("AGV-02") with
    {
        Connection = ConnectionState.Disconnected, Quality = SignalQuality.Disabled,
        X = double.PositiveInfinity, Y = double.NegativeInfinity
    });
    await f.Recovery.InspectAsync(ct);
    Check((await f.Recovery.DecideAsync(admin, await Request(f, "__SITE__"), ct)).Accepted,
        "停用车辆的未初始化无穷坐标不破坏活跃车辆的全场恢复指纹");
}
await using (var f = await Fixture.Create())
{
    await f.Boot(); var journal = new ExecutionEvidenceJournal(f.Db); var plc = new CountingPlc();
    var wrapped = new JournaledPlcGateway(plc, journal, f.Signals, f.Barrier);
    using (journal.BeginScope("task", "pickup")) await wrapped.WriteSignalAsync("PLC-01", "FullBoxRemoved", true, ct);
    var evidence = f.Db.Queryable<ExecutionEvidenceEntity>().Where(e => e.TaskId == "task").OrderBy(e => e.OccurredAt).ToList();
    Check(plc.Writes == 1 && evidence.Select(e => e.Kind).SequenceEqual(new[] { "PlcWriteIntent", "PlcWriteAcknowledged" }), "PLC写前意图和写后响应持久化关联原任务节点");
    await f.Db.Ado.ExecuteCommandAsync("CREATE TRIGGER reject_intent BEFORE INSERT ON execution_evidence WHEN NEW.Kind='PlcWriteIntent' BEGIN SELECT RAISE(ABORT,'intent-failure'); END;");
    var failed = false; try { await wrapped.WriteSignalAsync("PLC-01", "Start", true, ct); } catch { failed = true; }
    Check(failed && plc.Writes == 1, "写前证据保存失败时禁止产生物理写操作");
}

// Cancellation is tested against the actual SQLite transactions and shared execution gate.
await using (var f = await Fixture.Create())
{
    await f.Boot(); var id = await f.Prepare();
    var command = (await f.Outbox.GetDueCommandsAsync(DateTimeOffset.Now, 10, ct)).Single();
    await f.Outbox.TryClaimAsync(command.CommandId, DateTimeOffset.Now, ct);
    var step = await f.Step(id);
    var cancellation = new TaskCancellationService(f.Db, f.Gate, f.Gateway);
    await cancellation.RequestAsync(admin, new(Guid.NewGuid().ToString("N"), id, DispatchTaskStage.SubmittingToAgv, "测试接受回执落库失败后仍核对原车辆任务"), ct);
    f.Gateway.Status = new(step.StepId, step.StepId, AgvTransportTaskState.GoingToDestination, "AGV-01", DateTimeOffset.Now);
    await f.Db.Ado.ExecuteCommandAsync("CREATE TRIGGER reject_cancel_accept BEFORE INSERT ON execution_evidence WHEN NEW.Kind='TaskCancellationSendAccepted' BEGIN SELECT RAISE(ABORT,'accept-audit-failure'); END;");
    await cancellation.ProcessAsync(ct);
    Check(f.Gateway.Cancels == 1 && f.Db.Queryable<DispatchTaskEntity>().Single().CancelSentAt == null && f.Db.Queryable<DispatchTaskEntity>().Single().Stage == 120, "接受审计失败不会绕过事务保存成功状态");
    await f.Db.Ado.ExecuteCommandAsync("DROP TRIGGER reject_cancel_accept;");
    f.Gateway.Status = f.Gateway.Status with { State = AgvTransportTaskState.Cancelled, UpdatedAt = DateTimeOffset.Now };
    await new TaskCancellationService(f.Db, f.Gate, f.Gateway).ProcessAsync(ct);
    Check(f.Gateway.Cancels == 1 && f.Db.Queryable<DispatchTaskEntity>().Single().Stage == 130, "接受审计失败重启后核对原任务结束而不重复取消");
}
await using (var f = await Fixture.Create())
{
    await f.Boot(); var id = await f.Prepare();
    var cancellation = new TaskCancellationService(f.Db, f.Gate, f.Gateway);
    var request = new TaskCancelRequest(Guid.NewGuid().ToString("N"), id, DispatchTaskStage.SubmittingToAgv, "现场申请停止原任务并核对载荷PLC与路权");
    Check(!(await cancellation.RequestAsync(admin, request with { ExpectedStage = DispatchTaskStage.GoingToPickup }, ct)).Accepted, "过期任务阶段拒绝取消");
    await f.Db.Ado.ExecuteCommandAsync("CREATE TRIGGER reject_cancel BEFORE INSERT ON execution_evidence WHEN NEW.Kind='TaskCancellationRequested' BEGIN SELECT RAISE(ABORT,'cancel-audit-failure'); END;");
    var rolledBack = false; try { await cancellation.RequestAsync(admin, request, ct); } catch (InvalidOperationException) { rolledBack = true; }
    Check(rolledBack && (await f.Step(id)).Status == (int)DispatchTaskStepStatus.CommandPending && f.Db.Queryable<DispatchTaskEntity>().Single().CancelRequestId == null, "取消审计失败时任务与命令冻结原子回滚");
    await f.Db.Ado.ExecuteCommandAsync("DROP TRIGGER reject_cancel;");
    var laterStep = f.Db.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == id && s.Sequence == 3).Single();
    await f.Db.Insertable(new AgvCommandOutboxEntity { CommandId = "extra-uncertain", IdempotencyKey = "extra-uncertain", TaskId = id, StepId = laterStep.StepId,
        VehicleId = "AGV-02", CommandType = (int)AgvCommandType.Transport, Status = (int)AgvCommandStatus.RetryScheduled, PayloadJson = "{}", AttemptCount = 1,
        NextAttemptAt = DateTime.Now, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommandAsync();
    Check(!(await cancellation.RequestAsync(admin, request, ct)).Accepted && f.Db.Queryable<DispatchTaskEntity>().Single().CancelRequestId == null,
        "其他步骤存在未知发送结果时不把当前步骤取消当作全任务停止");
    await f.Db.Deleteable<AgvCommandOutboxEntity>().Where(c => c.CommandId == "extra-uncertain").ExecuteCommandAsync();
    Check((await cancellation.RequestAsync(admin, request, ct)).Accepted, "未发送运输可冻结并直接进入现场核对");
    await cancellation.ProcessAsync(ct);
    Check(f.Gateway.Cancels == 0 && f.Gateway.Sends == 0 && f.Db.Queryable<DispatchTaskEntity>().Single().Stage == 130, "未发送取消不调用厂家也不自动关闭任务");
    Check((await f.Outbox.GetDueCommandsAsync(DateTimeOffset.Now, 10, ct)).Count == 0 && (await new DispatchOrchestrationRepository(f.Db).GetReadyStepsAsync(10, ct)).Count == 0, "冻结步骤及Outbox不会继续发送或PLC推进");
    Check((await cancellation.RequestAsync(admin, request, ct)).Accepted && !(await cancellation.RequestAsync(admin, request with { Reason = "同编号改变取消操作依据会被拒绝处理" }, ct)).Accepted, "取消回执幂等且拒绝同编号不同参数");
    Check((await new DispatchTaskReadRepository(f.Db).GetActiveTasksAsync(ct)).Count == 1 && !(await f.Tasks.TryCreateAsync("PLC-01", "DUMPER", 1, ct)).Created, "现场核对阶段保留活动任务并阻止重复工位任务");
}
await using (var f = await Fixture.Create())
{
    await f.Boot(); var id = await f.Prepare();
    var command = (await f.Outbox.GetDueCommandsAsync(DateTimeOffset.Now, 10, ct)).Single();
    await f.Outbox.TryClaimAsync(command.CommandId, DateTimeOffset.Now, ct);
    var step = await f.Step(id);
    var cancellation = new TaskCancellationService(f.Db, f.Gate, f.Gateway);
    var request = new TaskCancelRequest(Guid.NewGuid().ToString("N"), id, DispatchTaskStage.SubmittingToAgv, "原发送结果不明现在申请取消并核对现场");
    Check((await cancellation.RequestAsync(admin, request, ct)).Accepted, "发送应答丢失按原StepId持久化取消目标");
    await cancellation.ProcessAsync(ct);
    Check(f.Gateway.Cancels == 0 && f.Db.Queryable<DispatchTaskEntity>().Single().Stage == 120, "404或历史缺失不能当作取消成功");
    foreach (var status in new[] {
        new AgvTransportTaskStatus(step.StepId, "OTHER", AgvTransportTaskState.GoingToDestination, "AGV-01", DateTimeOffset.Now),
        new AgvTransportTaskStatus(step.StepId, step.StepId, AgvTransportTaskState.GoingToDestination, "OTHER", DateTimeOffset.Now),
        new AgvTransportTaskStatus(step.StepId, step.StepId, AgvTransportTaskState.GoingToDestination, "AGV-01", DateTimeOffset.Now.AddMinutes(-1)) })
    { f.Gateway.Status = status; await cancellation.ProcessAsync(ct); Check(f.Gateway.Cancels == 0, "错误归属或陈旧厂家记录不发送取消"); }
    f.Gateway.Status = new(step.StepId, step.StepId, AgvTransportTaskState.GoingToDestination, "AGV-01", DateTimeOffset.Now);
    f.Gateway.FailCancelResponse = true; await cancellation.ProcessAsync(ct);
    Check(f.Gateway.Cancels == 1 && f.Db.Queryable<DispatchTaskEntity>().Single().CancelSentAt == null, "取消接口响应丢失保留未确认意图和重试计划");
    cancellation = new TaskCancellationService(f.Db, f.Gate, f.Gateway);
    await cancellation.ProcessAsync(ct); Check(f.Gateway.Cancels == 1, "重启读取持久化退避期不立即重复取消");
    await f.Db.Updateable<DispatchTaskEntity>().SetColumns(t => t.CancelNextAttemptAt == DateTime.Now.AddSeconds(-1)).Where(t => t.TaskId == id).ExecuteCommandAsync();
    f.Gateway.Status = f.Gateway.Status with { UpdatedAt = DateTimeOffset.Now }; await cancellation.ProcessAsync(ct);
    Check(f.Db.Queryable<ExecutionEvidenceEntity>().Any(e => e.Kind == "TaskCancellationSendAccepted") && f.Db.Queryable<ExecutionEvidenceEntity>().Any(e => e.Kind == "TaskCancellationProcessingFailed"), "取消接受与处理异常形成独立持久化审计");
    Check(f.Gateway.Cancels == 2 && f.Db.Queryable<DispatchTaskEntity>().Single().Stage == 120, "接口接受仅等待厂家任务结束不标记业务取消完成");
    await new TaskCancellationService(f.Db, f.Gate, f.Gateway).ProcessAsync(ct);
    Check(f.Gateway.Cancels == 2, "已接受取消请求跨重启仅查询结果不重发");
    await f.Db.Ado.ExecuteCommandAsync("CREATE TRIGGER reject_cancel_confirm BEFORE INSERT ON execution_evidence WHEN NEW.Kind='TaskCancellationVehicleConfirmed' BEGIN SELECT RAISE(ABORT,'confirm-audit-failure'); END;");
    f.Gateway.Status = f.Gateway.Status with { State = AgvTransportTaskState.Cancelled, UpdatedAt = DateTimeOffset.Now }; await cancellation.ProcessAsync(ct);
    Check(f.Db.Queryable<DispatchTaskEntity>().Single().Stage == 120 && f.Db.Queryable<DispatchTaskEntity>().Single().CancelConfirmedAt == null,
        "车辆确认审计失败时错误保存也不会绕过确认事务回滚");
    await f.Db.Ado.ExecuteCommandAsync("DROP TRIGGER reject_cancel_confirm;");
    await cancellation.ProcessAsync(ct);
    Check(f.Db.Queryable<DispatchTaskEntity>().Single().Stage == 130 && f.Db.Queryable<ExecutionEvidenceEntity>().Any(e => e.Kind == "TaskCancellationVehicleConfirmed"), "厂家取消确认落库进入现场核对并保留审计");
    f.Settings = new MapSwitchSafetySettings { CheckPlcSignals = true };
    f.Recovery = new(f.Db, f.Barrier, f.Gate, f.Gateway, f.Vehicles, f.Signals, f.Settings, new ActiveMapRuntime());
    Check((await f.Recovery.BeginReviewAsync(admin, new(id, "取消后核对原车辆物料PLC和路权现场状态"), ct)).Accepted && !f.Barrier.IsOpen, "运行中恢复请求冻结全场且创建现场核对批次");
    var batch = (await f.Recovery.GetAsync(ct)).SessionId;
    Check((await f.Recovery.BeginReviewAsync(admin, new(id, "重复核对请求保持当前现场恢复批次不变化"), ct)).Accepted && (await f.Recovery.GetAsync(ct)).SessionId == batch, "重复现场恢复请求不丢失当前批次");
    f.Fresh();
    foreach (var variable in new[] { "FullBoxRequestAcknowledged", "FullBoxRemoved", "EmptyBoxTaskAcknowledged", "EmptyBoxPlaced" })
        f.Signals.Items.Add(SwitchSignalCache.Ready() with { VariableName = variable });
    foreach (var variable in new[] { "Faulted", "AgvAtWaitingPosition", "AgvAtDumpingPosition" })
        f.Signals.Items.Add(SwitchSignalCache.Ready() with { StationId = "DUMPER", VariableName = variable });
    await f.Recovery.InspectAsync(ct);
    f.Db.Insertable(new TrafficResourceLeaseEntity { LeaseId = "cancel-lease", TaskId = id, OwnerStepId = step.StepId, VehicleId = "AGV-01", ResourceId = "NARROW", AcquiredAt = DateTime.Now, RenewedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(1) }).ExecuteCommand();
    await f.Recovery.InspectAsync(ct);
    Check(!(await f.Recovery.DecideAsync(admin, await Request(f, id, RecoveryDecision.CloseCancelled), ct)).Accepted, "原路权未人工处置不能关闭取消");
    var release = new ManualReleaseWorkflowService(f.Db, new(TimeSpan.FromMinutes(15)), f.Vehicles, f.Settings);
    var releaser = new TrafficUserContext("cancel-test", "现场操作员", TrafficPermission.Administer);
    f.Vehicles.Items[0] = f.Vehicles.Items[0] with { VelocityX = 0.5 };
    Check(!(await release.SubmitAsync(releaser, new("cancel-lease", "原车辆仍移动禁止释放此路权", true, "RELEASE NARROW"), ct)).Accepted, "取消核对中的车辆移动时人工路权释放也被拒绝");
    f.Vehicles.Items[0] = f.Vehicles.Items[0] with { VelocityX = 0 };
    Check((await release.SubmitAsync(releaser, new("cancel-lease", "已确认车辆停车并离开原交通区域", true, "RELEASE NARROW"), ct)).Accepted &&
        f.Db.Queryable<TrafficResourceReleaseAuditEntity>().Count() == 1, "取消已确认且停车后沿用单人现场确认解锁并保存审计");
    await f.Recovery.InspectAsync(ct);
    f.Vehicles.Items[0] = f.Vehicles.Items[0] with { VelocityX = 0.5 };
    Check(!(await f.Recovery.DecideAsync(admin, await Request(f, id, RecoveryDecision.CloseCancelled), ct)).Accepted, "车辆仍移动不能关闭取消");
    f.Vehicles.Items[0] = f.Vehicles.Items[0] with { VelocityX = 0 };
    var signalIndex = f.Signals.Items.FindIndex(s => s.VariableName == "FullBoxRemoved");
    f.Signals.Items[signalIndex] = f.Signals.Items[signalIndex] with { Value = true };
    Check(!(await f.Recovery.DecideAsync(admin, await Request(f, id, RecoveryDecision.CloseCancelled), ct)).Accepted, "原PLC握手未清理不能关闭取消");
    f.Signals.Items[signalIndex] = f.Signals.Items[signalIndex] with { Value = false };
    await f.Db.Ado.ExecuteCommandAsync("CREATE TRIGGER reject_close_cancel BEFORE INSERT ON execution_evidence WHEN NEW.Kind='CloseCancelled' BEGIN SELECT RAISE(ABORT,'close-audit-failure'); END;");
    var closeRolledBack = false; try { await f.Recovery.DecideAsync(admin, await Request(f, id, RecoveryDecision.CloseCancelled), ct); } catch (InvalidOperationException) { closeRolledBack = true; }
    Check(closeRolledBack && f.Db.Queryable<DispatchTaskEntity>().Single().Stage == 130 && f.Db.Queryable<DispatchTaskStepEntity>().Where(s => s.Status == 100).Count() == 0,
        "取消关闭审计失败时终结状态与步骤原子回滚");
    await f.Db.Ado.ExecuteCommandAsync("DROP TRIGGER reject_close_cancel;");
    Check((await f.Recovery.DecideAsync(admin, await Request(f, id, RecoveryDecision.CloseCancelled), ct)).Accepted, "停车载荷归属PLC握手路权全部核对后关闭取消");
    Check(f.Db.Queryable<DispatchTaskEntity>().Single().Stage == 110 && f.Db.Queryable<DispatchTaskStepEntity>().Where(s => s.Status == 100).Count() == 9 && f.Gateway.Sends == 0, "关闭取消原子终结剩余步骤且不重建运输");
    Check(!(await f.Recovery.ReleaseAsync(admin, new(batch, "现场全部核对完成申请恢复后续任务执行"), ct)).Accepted, "全场条目未核对前取消关闭也不能放行");
    var readable = (await f.Recovery.GetAsync(ct)).Items.Single(i => i.TaskId == id);
    Check(readable.VehicleSummary.Contains("AGV-01") && readable.PlcSummary.Contains("AgvAtDumpingPosition") && readable.ResourceSummary.Contains("无占用") && readable.LoadSummary.Contains("BOX-001"), "恢复摘要显示车辆PLC原路权及人工载荷核对结果");
    var closedAt = f.Db.Queryable<DispatchTaskEntity>().Single().UpdatedAt;
    var dumperIndex = f.Signals.Items.FindIndex(s => s.StationId == "DUMPER" && s.VariableName == "AgvAtDumpingPosition");
    f.Signals.Items[dumperIndex] = f.Signals.Items[dumperIndex] with { Value = true };
    await f.Recovery.InspectAsync(ct);
    Check(!(await f.Recovery.GetAsync(ct)).Items.Single(i => i.TaskId == id).Verified &&
        !(await f.Recovery.DecideAsync(admin, await Request(f, id, RecoveryDecision.CloseCancelled), ct)).Accepted, "关闭取消后倒料握手变化撤销确认并保持屏障");
    f.Signals.Items[dumperIndex] = f.Signals.Items[dumperIndex] with { Value = false };
    Check((await f.Recovery.DecideAsync(admin, await Request(f, id, RecoveryDecision.CloseCancelled), ct)).Accepted &&
        f.Db.Queryable<DispatchTaskEntity>().Single().UpdatedAt == closedAt, "已关闭取消可重新核对而不重置结束时间或重建任务");
    Check((await f.Recovery.DecideAsync(admin, await Request(f, "__SITE__"), ct)).Accepted, "取消关闭后仍须核对全场设备载荷与PLC");
    Check((await f.Recovery.ReleaseAsync(admin, new(batch, "现场全部核对完成申请恢复后续任务执行"), ct)).Accepted && f.Barrier.IsOpen, "取消现场关闭及全场核对形成完整放行闭环");
    var history = (await new TaskCenterReadRepository(f.Db).GetDetailAsync(id, ct))!.OperationHistory;
    Check(new[] { "提交取消请求", "车辆取消接口接受（待结束确认）", "车辆原任务结束确认", "进入现场恢复核对", "人工解锁路权", "现场核对后关闭取消", "恢复批次最终放行" }.All(label => history.Any(e => e.Operation == label)), "任务历史读取完整真实取消核对解锁及放行审计链");

}
await using (var f = await Fixture.Create())
{
    var id = await f.Prepare(); var command = (await f.Outbox.GetDueCommandsAsync(DateTimeOffset.Now, 10, ct)).Single();
    await f.Outbox.TryClaimAsync(command.CommandId, DateTimeOffset.Now, ct); var step = await f.Step(id);
    await f.Outbox.MarkSentAsync(command.CommandId, step.StepId, ct);
    await new DispatchOrchestrationRepository(f.Db).FailExecutingStepAsync(id, step.StepId, DispatchTaskStage.Faulted, "查询途中断线，现场需要核实到站", ct);
    f.Gateway.Status = new(step.StepId, step.StepId, AgvTransportTaskState.Completed, "AGV-01", DateTimeOffset.Now);
    await f.Boot(); f.Fresh("SITE-B"); await f.Recovery.InspectAsync(ct);
    Check((await f.Recovery.DecideAsync(admin, await Request(f, id, RecoveryDecision.RecordPhysicalCompletion), ct)).Accepted, "故障运输获得原厂家完成与到站停车证据后允许补记原步骤");
    Check((await f.Step(id)).StepType == (int)DispatchTaskStepType.PickupFullBox && (await f.Step(id)).StartedAt != null && f.Gateway.Sends == 0 && !f.Barrier.IsOpen, "故障恢复激活原下一步骤并启动计时且等待PLC核对");
}

// Identity verification never infers physical completion from correlation alone.
{
    var now = DateTimeOffset.Now;
    var plc = new PlcExecutionIdentity("PLC-01", "TASK", "BOOT-1", "CYCLE-1", "BOX-001", "STEP", "VENDOR", SignalQuality.Good, now, true);
    var load = new VehicleContainerIdentity("AGV-01", ContainerLoadState.Loaded, "BOX-001", "STEP", "VENDOR", SignalQuality.Good, now, true);
    var baseline = new ExecutionIdentityBaseline(plc, load, now);
    RecoveryIdentityVerification Verify(PlcExecutionIdentity? p, VehicleContainerIdentity? v, ExecutionIdentityBaseline? b, bool strict = false) =>
        ExecutionIdentityVerifier.Verify("TASK", "PLC-01", "STEP", "VENDOR", "AGV-01", p, v, b, now.AddSeconds(-1), now, TimeSpan.FromSeconds(10), strict);
    Check(Verify(plc, load, baseline).MachineVerified, "同一启动周期容器及原运输身份匹配");
    Check(Verify(null, null, null).BlockReason == null && !Verify(null, null, null).MachineVerified, "未接入身份保持未知并保留人工流程");
    Check(Verify(null, null, null, true).BlockReason != null, "严格身份模式拒绝未接入字段");
    Check(!Verify(plc, load, null).MachineVerified && Verify(plc, load, null, true).BlockReason != null, "当前身份不能反推原意图基线");
    foreach (var invalid in new[] {
        plc with { DeviceSessionId = "BOOT-2" }, plc with { CycleId = "CYCLE-2" },
        plc with { TaskId = "OTHER" }, plc with { StationId = "OTHER" },
        plc with { OriginalBusinessStepId = "OTHER" }, plc with { OriginalVendorTaskId = "OTHER" },
        plc with { Quality = SignalQuality.Unknown }, plc with { IsCoherent = false },
        plc with { SchemaVersion = 2 }, plc with { ObservedAt = now.AddMinutes(-1) },
        plc with { ObservedAt = now.AddMinutes(1) }, plc with { CycleId = "" },
        plc with { ContainerId = "box-001" } })
        Check(Verify(invalid, load, baseline).BlockReason != null, "身份异常不能被人工勾选绕过：" + invalid);
    foreach (var invalid in new[] {
        load with { VehicleId = "OTHER" }, load with { ContainerId = null },
        load with { OriginalBusinessStepId = "OTHER" }, load with { OriginalVendorTaskId = "OTHER" },
        load with { LoadState = ContainerLoadState.Empty }, load with { LoadState = (ContainerLoadState)99 },
        load with { Quality = SignalQuality.Unknown }, load with { IsCoherent = false } })
        Check(Verify(plc, invalid, baseline).BlockReason != null, "车辆容器异常阻止身份核验：" + invalid);
    Check(!Verify(plc, load with { LoadState = ContainerLoadState.Unknown }, baseline).MachineVerified, "未知载荷不当作空载");
    Check(Verify(plc, load with { LoadState = ContainerLoadState.Empty, ContainerId = null }, baseline).MachineVerified, "明确空载可关联原PLC周期但不证明动作完成");
    Check(Verify(plc, load, baseline with { Plc = plc with { ObservedAt = now.AddMinutes(-1) } }).BlockReason != null, "原意图不能使用陈旧身份基线");
    Check(Verify(plc with { ContainerId = "BOX-002" }, load with { ContainerId = "BOX-002" }, baseline).BlockReason != null, "PLC和车辆都换箱仍拒绝复用原容器基线");
}
await using (var f = await Fixture.Create())
{
    var id = await f.Prepare(); var step = await f.Step(id);
    var cache = new ExecutionIdentityCache();
    var plc = new PlcExecutionIdentity("PLC-01", id, "BOOT-1", "CYCLE-1", "BOX-001", step.StepId, step.StepId, SignalQuality.Good, DateTimeOffset.Now, true);
    var load = new VehicleContainerIdentity("AGV-01", ContainerLoadState.Loaded, "BOX-001", step.StepId, step.StepId, SignalQuality.Good, DateTimeOffset.Now, true);
    cache.Publish(plc); cache.Publish(load);
    var journal = new ExecutionEvidenceJournal(f.Db, cache);
    using (journal.BeginScope(id, step.StepId)) await journal.RecordCurrentAsync("AgvSendIntent", new { command = "original" }, ct);
    var command = (await f.Outbox.GetDueCommandsAsync(DateTimeOffset.Now, 10, ct)).Single();
    await f.Outbox.TryClaimAsync(command.CommandId, DateTimeOffset.Now, ct); await f.Outbox.MarkSentAsync(command.CommandId, step.StepId, ct);
    f.Recovery = new(f.Db, f.Barrier, f.Gate, f.Gateway, f.Vehicles, f.Signals, f.Settings, new ActiveMapRuntime(), cache, new RecoveryIdentityOptions { RequireMachineIdentity = true });
    f.Gateway.Status = new(step.StepId, step.StepId, AgvTransportTaskState.GoingToDestination, "AGV-01", DateTimeOffset.Now);
    await f.Boot(); f.Fresh(); cache.Publish(plc with { ObservedAt = DateTimeOffset.Now }); cache.Publish(load with { ObservedAt = DateTimeOffset.Now });
    await f.Recovery.InspectAsync(ct);
    var ticket = (await f.Recovery.GetAsync(ct)).Items.Single(i => i.TaskId == id);
    Check(ticket.IdentityState == "Verified" && !ticket.Verified, "设备身份匹配不自动确认现场或放行");
    Check(!(await f.Recovery.DecideAsync(admin, await Request(f, id), ct)).Accepted, "设备承载容器要求人工填写精确编号");
    Check((await f.Recovery.DecideAsync(admin, (await Request(f, id)) with { MaterialReference = "BOX-001" }, ct)).Accepted, "精确编号与原车辆原运输现场核对一致允许续接准备");
    var verified = (await f.Recovery.GetAsync(ct)).Items.Single(i => i.TaskId == id);
    cache.Publish(plc with { ObservedAt = DateTimeOffset.Now }); cache.Publish(load with { ObservedAt = DateTimeOffset.Now });
    await f.Recovery.InspectAsync(ct);
    var refreshed = (await f.Recovery.GetAsync(ct)).Items.Single(i => i.TaskId == id);
    Check(refreshed.Verified && refreshed.Revision == verified.Revision, "身份读数仅刷新时间不撤销有效核对");
    cache.Publish(plc with { CycleId = "CYCLE-2", ObservedAt = DateTimeOffset.Now });
    var status = await f.Recovery.GetAsync(ct);
    Check(!(await f.Recovery.ReleaseAsync(admin, new(status.SessionId, "现场全部核对完成申请恢复原任务执行"), ct)).Accepted, "周期变化无需等待界面刷新即可阻止放行");
    await f.Recovery.InspectAsync(ct);
    var revoked = (await f.Recovery.GetAsync(ct)).Items.Single(i => i.TaskId == id);
    Check(!revoked.Verified && revoked.IdentityBlocksRecovery && revoked.Revision > verified.Revision, "周期变化撤销旧现场核对并增加修订号");
    Check(!(await f.Recovery.DecideAsync(admin, (await Request(f, id)) with { MaterialReference = "BOX-001" }, ct)).Accepted && f.Gateway.Sends == 0 && f.Gateway.Cancels == 0,
        "身份冲突即使全部勾选也拒绝续接且不发送设备命令");
    Check(f.Db.Queryable<ExecutionEvidenceEntity>().Where(e => e.Kind == "AgvSendIntent").Single().Evidence.Contains("ExecutionIdentityBaseline") &&
        revoked.Evidence.Contains("CYCLE-2") && status.Audits.Any(a => a.Decision == "ContinueOriginal"), "原意图基线与恢复核验证据持久化");
}
await using (var f = await Fixture.Create())
{
    var id = await f.Prepare(); var step = await f.Step(id);
    await new ExecutionEvidenceJournal(f.Db).RecordAsync(id, step.StepId, "AgvSendIntent", new { command = "legacy-first" }, ct);
    var cache = new ExecutionIdentityCache();
    var plc = new PlcExecutionIdentity("PLC-01", id, "BOOT-1", "CYCLE-1", "BOX-001", step.StepId, step.StepId, SignalQuality.Good, DateTimeOffset.Now, true);
    var load = new VehicleContainerIdentity("AGV-01", ContainerLoadState.Loaded, "BOX-001", step.StepId, step.StepId, SignalQuality.Good, DateTimeOffset.Now, true);
    cache.Publish(plc); cache.Publish(load);
    await new ExecutionEvidenceJournal(f.Db, cache).RecordAsync(id, step.StepId, "AgvSendIntent", new { command = "retry" }, ct);
    await f.Db.Ado.ExecuteCommandAsync("UPDATE execution_evidence SET OccurredAt='2026-01-01 00:00:00' WHERE Kind='AgvSendIntent'");
    var command = (await f.Outbox.GetDueCommandsAsync(DateTimeOffset.Now, 10, ct)).Single();
    await f.Outbox.TryClaimAsync(command.CommandId, DateTimeOffset.Now, ct); await f.Outbox.MarkSentAsync(command.CommandId, step.StepId, ct);
    f.Recovery = new(f.Db, f.Barrier, f.Gate, f.Gateway, f.Vehicles, f.Signals, f.Settings, new ActiveMapRuntime(), cache, new RecoveryIdentityOptions { RequireMachineIdentity = true });
    f.Gateway.Status = new(step.StepId, step.StepId, AgvTransportTaskState.GoingToDestination, "AGV-01", DateTimeOffset.Now);
    await f.Boot(); f.Fresh(); cache.Publish(plc with { ObservedAt = DateTimeOffset.Now }); cache.Publish(load with { ObservedAt = DateTimeOffset.Now }); await f.Recovery.InspectAsync(ct);
    var item = (await f.Recovery.GetAsync(ct)).Items.Single(i => i.TaskId == id);
    Check(item.IdentityState == "Unavailable" && item.IdentityBlocksRecovery && item.IdentitySummary.Contains("基线"), "时间戳相同仍坚持首条意图，重试不能补造历史基线");
}

await using (var f = await Fixture.Create())
{
    var id = await f.Prepare(); var step = await f.Step(id); var cache = new ExecutionIdentityCache();
    var plcReader = new RecoveryPlcIdentityReader(new("PLC-01", id, "BOX-001", step.StepId, step.StepId, SignalQuality.Good));
    var vehicleReader = new RecoveryVehicleIdentityReader(new("AGV-01", ContainerLoadState.Loaded, "BOX-001", step.StepId, step.StepId, true, SignalQuality.Good));
    var acquisition = new ExecutionIdentityAcquisition(new() { Enabled = true }, cache, [plcReader], [vehicleReader]);
    await acquisition.PollOnceAsync(ct);
    await new ExecutionEvidenceJournal(f.Db, cache).RecordAsync(id, step.StepId, "AgvSendIntent", new { command = "read-only-source" }, ct);
    var command = (await f.Outbox.GetDueCommandsAsync(DateTimeOffset.Now, 10, ct)).Single();
    await f.Outbox.TryClaimAsync(command.CommandId, DateTimeOffset.Now, ct); await f.Outbox.MarkSentAsync(command.CommandId, step.StepId, ct);
    f.Recovery = new(f.Db, f.Barrier, f.Gate, f.Gateway, f.Vehicles, f.Signals, f.Settings, new ActiveMapRuntime(), cache, new RecoveryIdentityOptions { RequireMachineIdentity = true });
    f.Gateway.Status = new(step.StepId, step.StepId, AgvTransportTaskState.GoingToDestination, "AGV-01", DateTimeOffset.Now);
    await f.Boot(); f.Fresh(); await acquisition.PollOnceAsync(ct); await f.Recovery.InspectAsync(ct);
    Check((await f.Recovery.GetAsync(ct)).Items.Single(i => i.TaskId == id).IdentityState == "Verified", "只读适配器经原意图基线接入恢复数据库证据闭环");
    Check((await f.Recovery.DecideAsync(admin, (await Request(f, id)) with { MaterialReference = "BOX-001" }, ct)).Accepted, "模拟原运输现场核对与严格身份核验共同通过");
    plcReader.Fail = true; await acquisition.PollOnceAsync(ct);
    var status = await f.Recovery.GetAsync(ct);
    Check(!(await f.Recovery.ReleaseAsync(admin, new(status.SessionId, "只读采集断连后不能沿用旧核对放行"), ct)).Accepted, "采集断连立即拒绝旧身份放行");
    await f.Recovery.InspectAsync(ct);
    Check(!(await f.Recovery.GetAsync(ct)).Items.Single(i => i.TaskId == id).Verified, "采集断连撤销保存的现场核对");
    plcReader.Fail = false; await acquisition.PollOnceAsync(ct); await f.Recovery.InspectAsync(ct);
    Check((await f.Recovery.GetAsync(ct)).Items.Single(i => i.TaskId == id).IdentityState == "Verified" &&
        !(await f.Recovery.GetAsync(ct)).Items.Single(i => i.TaskId == id).Verified, "重新读到一致身份不自动恢复人工确认");
    vehicleReader.Frame = vehicleReader.Frame with { OriginalBusinessStepId = "OTHER" };
    await acquisition.PollOnceAsync(ct); await f.Recovery.InspectAsync(ct);
    Check((await f.Recovery.GetAsync(ct)).Items.Single(i => i.TaskId == id).IdentityBlocksRecovery &&
        !(await f.Recovery.DecideAsync(admin, (await Request(f, id)) with { MaterialReference = "BOX-001" }, ct)).Accepted, "读取质量Good但原运输归属改变也拒绝处置");
    vehicleReader.Frame = vehicleReader.Frame with { OriginalBusinessStepId = step.StepId }; plcReader.Stamp = plcReader.Stamp with { DeviceSessionId = "BOOT-2", CycleId = "0" };
    await acquisition.PollOnceAsync(ct); await f.Recovery.InspectAsync(ct);
    Check((await f.Recovery.GetAsync(ct)).Items.Single(i => i.TaskId == id).IdentitySummary.Contains("启动编号"), "控制器复位和周期归零通过只读采集阻止旧任务身份复用");
    Check(f.Gateway.Sends == 0 && f.Gateway.Cancels == 0 && f.Db.Queryable<ExecutionEvidenceEntity>().Any(e => e.Kind == "VerificationRevoked"), "模拟联调只保存核验证据不发送或取消实车动作");
}

await using(var f=await Fixture.Create(new MapSwitchSafetySettings{CheckPlcSignals=true}))
{
    var id=await f.Prepare();
    var original=await f.Step(id);
    await f.Db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=50,CompletedAt=DateTime.Now}).Where(s=>s.StepId==original.StepId).ExecuteCommandAsync();
    var next=await f.Step(id);
    await f.Db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=10,StartedAt=DateTime.Now}).Where(s=>s.StepId==next.StepId).ExecuteCommandAsync();
    await f.Db.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)DispatchTaskStage.PickingUp).Where(t=>t.TaskId==id).ExecuteCommandAsync();
    var work=(await new DispatchOrchestrationRepository(f.Db).GetReadyStepsAsync(100,ct)).Single(s=>s.TaskId==id);
    var signal=SwitchSignalCache.Ready() with{VariableName="FullBoxRemoved",Value=true};
    var store=new TaskHandshakeStore(f.Db);
    var state=await store.BeginAsync(work,HandshakeExecutionOptions.Version,"NotifyRemoved",[new("PLC-01","FullBoxRemoved",true,"Good",signal.UpdatedAt,signal.Version,signal.Address)],ct);
    await store.MoveAsync(state,"WritePending","isolated lost write response","PLC-01/FullBoxRemoved",true,"NotifyEmptyResponsibility",ct);
    await f.Boot();f.Fresh("SITE-B");f.Signals.Items.Add(signal with{UpdatedAt=DateTimeOffset.Now});await f.Recovery.InspectAsync(ct);
    var request=await Request(f,id);
    Check(!(await f.Recovery.DecideAsync(admin,request,ct)).Accepted,"待确认写入未明确确认，拒绝原节点续接");
    Check(!(await f.Recovery.DecideAsync(admin,request with{Decision=RecoveryDecision.RecordPhysicalCompletion,HandshakePendingWriteConfirmed=true},ct)).Accepted,"新PLC契约不能用补记完成跳过握手阶段");
    var resumed=await f.Recovery.DecideAsync(admin,(await Request(f,id)) with{HandshakePendingWriteConfirmed=true},ct);
    Check(resumed.Accepted,"新鲜同地址期望值及完整现场核对后允许原阶段续接："+resumed.Message);
    var restored=(await store.GetAsync(id,next.StepId,ct))!;
    Check(restored.Phase=="NotifyEmptyResponsibility"&&restored.PendingSignal==null,"恢复原子提交原写入核对及下一子阶段");
    Check(f.Gateway.Sends==0&&f.Db.Queryable<TaskRuntimeEventEntity>().Any(e=>e.TaskId==id&&e.Kind=="HandshakeReviewed"),"恢复只保存核对证据，不重发设备动作");
    await store.MoveAsync(restored,"AwaitPickupReadback","isolated state change",null,null,null,ct);
    await f.Recovery.InspectAsync(ct);
    Check(!(await f.Recovery.GetAsync(ct)).Items.Single(i=>i.TaskId==id).Verified,"握手阶段改变使旧恢复确认失效");
}
await using(var f=await Fixture.Create())
{
    var id=await f.Prepare();var step=await f.Step(id);
    await f.Db.Insertable(new TaskHandshakeEntity{TaskId=id,StepId=step.StepId,CycleId=id,ContractVersion=HandshakeExecutionOptions.LegacyVersion,Phase="WritePending",Revision=1,
        PendingSignal="PLC-01/FullBoxRemoved",PendingValue=true,NextPhase="NotifyEmptyResponsibility",StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow}).ExecuteCommandAsync();
    await f.Boot();f.Fresh();f.Signals.Items.Add(SwitchSignalCache.Ready() with{VariableName="FullBoxRemoved",Value=true});await f.Recovery.InspectAsync(ct);
    var result=await f.Recovery.DecideAsync(admin,(await Request(f,id)) with{HandshakePendingWriteConfirmed=true},ct);
    Check(!result.Accepted&&result.Message.Contains("其他版本"),"旧契约待确认写入即使人工勾选且读回匹配也不迁入28阶段");
    var original=f.Db.Queryable<TaskHandshakeEntity>().Single();
    Check(original.Phase=="WritePending"&&original.ContractVersion==HandshakeExecutionOptions.LegacyVersion&&original.Revision==1&&f.Gateway.Sends==0,"拒绝旧契约恢复保留原版本阶段和修订且不重发");
}
await using(var f=await Fixture.Create(new MapSwitchSafetySettings{CheckPlcSignals=true}))
{
    var id=await f.Prepare(); var first=await f.Step(id);
    var command=(await f.Outbox.GetDueCommandsAsync(DateTimeOffset.Now,10,ct)).Single();
    await f.Outbox.TryClaimAsync(command.CommandId,DateTimeOffset.Now,ct); await f.Outbox.MarkSentAsync(command.CommandId,command.StepId,ct);
    await f.Db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=50,CompletedAt=DateTime.Now}).Where(s=>s.StepId==first.StepId).ExecuteCommandAsync();
    var pickup=await f.Step(id);
    await f.Db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=10,VehicleId="AGV-01"}).Where(s=>s.StepId==pickup.StepId).ExecuteCommandAsync();
    await f.Db.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)DispatchTaskStage.PickingUp).Where(t=>t.TaskId==id).ExecuteCommandAsync();
    var expected=new Dictionary<string,bool>{{"FullBoxRequested",false},{"BoxPresent",false},{"Faulted",false},{"CommunicationOnline",true},{"FullBoxRequestAcknowledged",true},{"FullBoxRemoved",false},{"EmptyBoxTaskAcknowledged",false},{"EmptyBoxPlaced",false}};
    var baseline=expected.Select((p,i)=>new TaskSignalEvidence("PLC-01",p.Key,p.Key=="BoxPresent"||p.Value,"Good",DateTimeOffset.Now,1,"DB1.DBX0."+i)).ToArray();
    await f.Boot();f.Fresh("SITE-B");f.Signals.Items.Clear();
    foreach(var pair in expected)f.Signals.Items.Add(SwitchSignalCache.Ready() with{VariableName=pair.Key,Address=baseline.Single(b=>b.VariableName==pair.Key).Address,Value=pair.Value});
    await f.Recovery.InspectAsync(ct);
    Check(!(await f.Recovery.DecideAsync(admin,await Request(f,id),ct)).Accepted,"取箱机械完成后的新握手节点缺少原28阶段绑定时不能套用新初态");
    await f.Db.Insertable(new TaskHandshakeEntity{TaskId=id,StepId=first.StepId,CycleId=id,ContractVersion=HandshakeExecutionOptions.Version,Phase="TransportBound",Revision=1,BaselineJson=System.Text.Json.JsonSerializer.Serialize(baseline),StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow}).ExecuteCommandAsync();
    await f.Recovery.InspectAsync(ct);
    foreach(var pair in expected)
    {
        var at=f.Signals.Items.FindIndex(s=>s.VariableName==pair.Key);var original=f.Signals.Items[at];
        f.Signals.Items[at]=original with{Value=!pair.Value};
        Check(!(await f.Recovery.DecideAsync(admin,await Request(f,id),ct)).Accepted,"新取箱节点恢复拒绝异常基线："+pair.Key);
        f.Signals.Items[at]=original;
    }
    var boxAt=f.Signals.Items.FindIndex(s=>s.VariableName=="BoxPresent");var box=f.Signals.Items[boxAt];
    f.Signals.Items[boxAt]=box with{Address="DB9.DBX9.1"};
    Check(!(await f.Recovery.DecideAsync(admin,await Request(f,id),ct)).Accepted,"新取箱节点恢复不改用重映射传感器地址");
    f.Signals.Items[boxAt]=box with{UpdatedAt=DateTimeOffset.Now.AddMinutes(-1)};
    Check(!(await f.Recovery.DecideAsync(admin,await Request(f,id),ct)).Accepted,"新取箱节点恢复拒绝过期传感器");
    f.Signals.Items[boxAt]=box with{UpdatedAt=DateTimeOffset.Now.AddMinutes(1)};
    Check(!(await f.Recovery.DecideAsync(admin,await Request(f,id),ct)).Accepted,"新取箱节点恢复拒绝未来传感器时间");
    f.Signals.Items[boxAt]=box with{UpdatedAt=DateTimeOffset.Now};
    var result=await f.Recovery.DecideAsync(admin,await Request(f,id),ct);
    Check(result.Accepted,"原取箱运输机械完成、阶段6基线和原地址核对后可续接尚未开始的取箱通知："+result.Message);
    Check(f.Db.Queryable<TaskHandshakeEntity>().Count()==1&&f.Db.Queryable<TaskHandshakeEntity>().Single().Phase=="TransportBound"&&f.Gateway.Sends==0,"核对不创建新取箱检查点、不写通知、不重复发车");
    Check((await f.Recovery.DecideAsync(admin,await Request(f,"__SITE__"),ct)).Accepted,"取箱后恢复仍单独核对全场载荷及PLC状态");
    var status=await f.Recovery.GetAsync(ct);
    Check((await f.Recovery.ReleaseAsync(admin,new(status.SessionId,"原取箱机械终态及未开始取箱通知初态已逐项核对"),ct)).Accepted,"恢复仅放开原执行器后续阶段，未把取箱通知补记完成");
}
foreach(var exitPhase in new[]{"NoExitCheckpoint","AwaitPhysicalExit","ClearDumpingAfterExit","AwaitDumperClosed","ReleaseDumper","ExitHandoverComplete","WritePending"})
await using(var f=await Fixture.Create(new MapSwitchSafetySettings{CheckPlcSignals=true}))
{
    var id=await f.Prepare();var taskSteps=await f.Tasks.GetStepsAsync(id,ct);var returning=taskSteps.Single(s=>s.StepType==DispatchTaskStepType.ReturnEmptyBox);
    await f.Db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=50,CompletedAt=DateTime.Now}).Where(s=>s.TaskId==id&&s.Sequence<returning.Sequence).ExecuteCommandAsync();
    await f.Db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=40,VehicleId="AGV-01",VendorTaskId=returning.StepId,DestinationSite="SITE-B"}).Where(s=>s.StepId==returning.StepId).ExecuteCommandAsync();
    await f.Db.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)DispatchTaskStage.ReturningEmptyBox).Where(t=>t.TaskId==id).ExecuteCommandAsync();
    var source=new Dictionary<string,bool>{{"FullBoxRequested",false},{"BoxPresent",true},{"Faulted",false},{"CommunicationOnline",true},{"FullBoxRequestAcknowledged",false},{"FullBoxRemoved",true},{"EmptyBoxTaskAcknowledged",true},{"EmptyBoxPlaced",false}};
    var baseline=source.Select((p,i)=>new TaskSignalEvidence("PLC-01",p.Key,p.Value,"Good",DateTimeOffset.Now,1,"DB1.DBX0."+i)).ToArray();
    foreach(var pair in new[]{(taskSteps.Single(s=>s.StepType==DispatchTaskStepType.GoToPickup).StepId,"TransportBound"),(taskSteps.Single(s=>s.StepType==DispatchTaskStepType.DumpFullBox).StepId,"ReadyToComplete")})
        await f.Db.Insertable(new TaskHandshakeEntity{TaskId=id,StepId=pair.StepId,CycleId=id,ContractVersion=HandshakeExecutionOptions.Version,Phase=pair.Item2,Revision=1,BaselineJson=System.Text.Json.JsonSerializer.Serialize(baseline),StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow}).ExecuteCommandAsync();
    if(exitPhase!="NoExitCheckpoint")
        await f.Db.Insertable(new TaskHandshakeEntity{TaskId=id,StepId=returning.StepId,CycleId=id,ContractVersion=HandshakeExecutionOptions.Version,Phase=exitPhase,Revision=1,BaselineJson=System.Text.Json.JsonSerializer.Serialize(baseline.Concat(new[]{new TaskSignalEvidence("DUMPER","AgvAtDumpingPosition",true,"Good",DateTimeOffset.Now,1,"DB1.DBX610.1")})),
            PendingSignal=exitPhase=="WritePending"?"DUMPER/AgvAtDumpingPosition":null,PendingValue=exitPhase=="WritePending"?false:null,NextPhase=exitPhase=="WritePending"?"AwaitDumperClosed":null,StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow}).ExecuteCommandAsync();
    await f.Db.Insertable(new TrafficResourceLeaseEntity{LeaseId="return-lease",ResourceId="NARROW",TaskId=id,OwnerStepId=returning.StepId,VehicleId="AGV-01",AcquiredAt=DateTime.Now,RenewedAt=DateTime.Now,ExpiresAt=DateTime.Now.AddMinutes(1)}).ExecuteCommandAsync();
    if(exitPhase!="ExitHandoverComplete")await f.Db.Insertable(new TaskEquipmentClaimEntity{EquipmentId="DUMPER",TaskId=id,AcquiredAtUtc=DateTime.UtcNow}).ExecuteCommandAsync();
    f.Gateway.Status=new(returning.StepId,returning.StepId,AgvTransportTaskState.Completed,"AGV-01",DateTimeOffset.Now);
    await f.Boot();f.Fresh("SITE-B");f.Signals.Items.Clear();
    foreach(var b in baseline)f.Signals.Items.Add(SwitchSignalCache.Ready() with{VariableName=b.VariableName,Address=b.Address,Value=b.Value,UpdatedAt=DateTimeOffset.Now});
    foreach(var name in new[]{"Faulted","AgvAtWaitingPosition","AgvAtDumpingPosition","DumperReady","DumpingCompletedMayLeave"})f.Signals.Items.Add(SwitchSignalCache.Ready() with{StationId="DUMPER",VariableName=name,Address=name=="AgvAtDumpingPosition"?"DB1.DBX610.1":"DB1.DBX610.0",Value=false});
    await f.Recovery.InspectAsync(ct);
    Check(!(await f.Recovery.DecideAsync(admin,await Request(f,id),ct)).Accepted,"车端已完成的原返程须核实机械终态，不按执行中续跑："+exitPhase);
    if(exitPhase=="WritePending")
    {
        Check(!(await f.Recovery.DecideAsync(admin,await Request(f,id,RecoveryDecision.RecordPhysicalCompletion),ct)).Accepted,"返程机械补记不能绕过未知PLC写入的明确确认");
        var index=f.Signals.Items.FindIndex(s=>s.StationId=="DUMPER"&&s.VariableName=="AgvAtDumpingPosition");
        f.Signals.Items[index]=f.Signals.Items[index] with{Value=true};
        Check(!(await f.Recovery.DecideAsync(admin,(await Request(f,id,RecoveryDecision.RecordPhysicalCompletion)) with{HandshakePendingWriteConfirmed=true},ct)).Accepted,"明确确认也不能替代原清位的新鲜期望值回读");
        f.Signals.Items[index]=f.Signals.Items[index] with{Value=false};
        await f.Db.Ado.ExecuteCommandAsync("CREATE TRIGGER reject_return_recovery BEFORE INSERT ON execution_evidence WHEN NEW.Kind='RecordPhysicalCompletion' BEGIN SELECT RAISE(ABORT,'return-recovery-audit-failure'); END;");
        var failed=false;try{await f.Recovery.DecideAsync(admin,(await Request(f,id,RecoveryDecision.RecordPhysicalCompletion)) with{HandshakePendingWriteConfirmed=true},ct);}catch(InvalidOperationException){failed=true;}
        var unchanged=await f.Db.Queryable<TaskHandshakeEntity>().Where(h=>h.StepId==returning.StepId).FirstAsync();
        Check(failed&&unchanged.Phase=="WritePending"&&unchanged.Revision==1&&(await f.Step(id)).Status==40&&!f.Barrier.IsOpen,"返程补记审计失败时原写入确认与机械终态一起回滚");
        await f.Db.Ado.ExecuteCommandAsync("DROP TRIGGER reject_return_recovery;");
    }
    var recorded=await f.Recovery.DecideAsync(admin,(await Request(f,id,RecoveryDecision.RecordPhysicalCompletion)) with{HandshakePendingWriteConfirmed=exitPhase=="WritePending"},ct);
    Check(recorded.Accepted,"原返程已完成可只补机械终态，待出区阶段不依赖关闭中的恢复屏障："+exitPhase+" / "+recorded.Message);
    Check((await f.Step(id)).StepType==(int)DispatchTaskStepType.PlaceEmptyBox&&!f.Barrier.IsOpen&&f.Gateway.Sends==0,"返程补记仅激活原放箱通知节点，屏障仍关闭："+exitPhase);
    var saved=await f.Db.Queryable<TaskHandshakeEntity>().Where(h=>h.StepId==returning.StepId).FirstAsync();
    Check(exitPhase=="NoExitCheckpoint"?saved==null:saved.Phase==(exitPhase=="WritePending"?"AwaitDumperClosed":exitPhase)&&saved.PendingSignal==null,"原出区检查点保留，仅明确确认的原写入推进其下一阶段："+exitPhase);
    Check(f.Db.Queryable<TrafficResourceLeaseEntity>().Count()==1&&f.Db.Queryable<TaskEquipmentClaimEntity>().Count()==(exitPhase=="ExitHandoverComplete"?0:1)&&!f.Db.Queryable<TaskRuntimeEventEntity>().Any(e=>e.Kind=="ExitVerified"||e.Kind=="EquipmentReleased"),"机械补记没有代替出区证据、清位或设备路权交接："+exitPhase);
    var responsibility=f.Signals.Items.FindIndex(s=>s.StationId=="PLC-01"&&s.VariableName=="EmptyBoxTaskAcknowledged");
    f.Signals.Items[responsibility]=f.Signals.Items[responsibility] with{Value=false};
    Check(!(await f.Recovery.DecideAsync(admin,await Request(f,id),ct)).Accepted,"新的放箱通知节点仍要求原返箱责任保持，不能用机械补记绕过："+exitPhase);
    f.Signals.Items[responsibility]=f.Signals.Items[responsibility] with{Value=true};
    var resumed=await f.Recovery.DecideAsync(admin,await Request(f,id),ct);
    Check(resumed.Accepted,"可核对原工位保持并续接未开始的放箱通知节点，出区交接留给执行器："+exitPhase+" / "+resumed.Message);
    Check((await f.Recovery.DecideAsync(admin,await Request(f,"__SITE__"),ct)).Accepted,"返程后恢复仍显式核对全场设备与载荷："+exitPhase);
    var status=await f.Recovery.GetAsync(ct);
    var released=await f.Recovery.ReleaseAsync(admin,new(status.SessionId,"原返程到站和来源PLC保持均已核对，恢复原流程"),ct);
    Check(released.Accepted&&f.Barrier.IsOpen,"核对后恢复屏障能放行，出区检查点与占用仍未改动："+exitPhase+" / "+released.Message);
    Check(f.Db.Queryable<TrafficResourceLeaseEntity>().Count()==1&&f.Db.Queryable<TaskEquipmentClaimEntity>().Count()==(exitPhase=="ExitHandoverComplete"?0:1)&&!f.Db.Queryable<TaskRuntimeEventEntity>().Any(e=>e.Kind=="ExitVerified"||e.Kind=="EquipmentReleased")&&f.Signals.Items.Single(s=>s.VariableName=="EmptyBoxPlaced").Value==false,"恢复放行只打开执行屏障，不产生出区证明、放箱通知或设备路权释放："+exitPhase);
}
await using(var f=await Fixture.Create(new MapSwitchSafetySettings{CheckPlcSignals=true}))
{
    var id=await f.Prepare();var taskSteps=await f.Tasks.GetStepsAsync(id,ct);var returning=taskSteps.Single(s=>s.StepType==DispatchTaskStepType.ReturnEmptyBox);
    await f.Db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=50,CompletedAt=DateTime.Now}).Where(s=>s.TaskId==id&&s.Sequence<returning.Sequence).ExecuteCommandAsync();
    await f.Db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=100,VehicleId="AGV-01"}).Where(s=>s.StepId==returning.StepId).ExecuteCommandAsync();
    var cancellingTask=await f.Db.Queryable<DispatchTaskEntity>().Where(t=>t.TaskId==id).FirstAsync();
    cancellingTask.Stage=(int)DispatchTaskStage.CancellationReview;cancellingTask.VehicleId="AGV-01";cancellingTask.CancelRequestId=Guid.NewGuid().ToString("N");cancellingTask.CancelVehicleId="AGV-01";cancellingTask.CancelConfirmedAt=DateTime.Now;
    await f.Db.Updateable(cancellingTask).ExecuteCommandAsync();
    await f.Db.Insertable(new TaskHandshakeEntity{TaskId=id,StepId=returning.StepId,CycleId=id,ContractVersion=HandshakeExecutionOptions.Version,Phase="AwaitPhysicalExit",Revision=1,StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow}).ExecuteCommandAsync();
    await f.Db.Insertable(new TaskEquipmentClaimEntity{EquipmentId="DUMPER",TaskId="FOLLOWING-TASK",AcquiredAtUtc=DateTime.UtcNow}).ExecuteCommandAsync();
    await f.Boot();f.Fresh();
    foreach(var name in new[]{"FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced"})f.Signals.Items.Add(SwitchSignalCache.Ready() with{VariableName=name});
    f.Signals.Items.Add(SwitchSignalCache.Ready() with{VariableName="BoxPresent",Value=true});
    foreach(var name in new[]{"Faulted","AgvAtWaitingPosition","AgvAtDumpingPosition","DumperReady","DumpingCompletedMayLeave"})f.Signals.Items.Add(SwitchSignalCache.Ready() with{StationId="DUMPER",VariableName=name,Value=true});
    await f.Recovery.InspectAsync(ct);
    Check(!(await f.Recovery.DecideAsync(admin,await Request(f,id,RecoveryDecision.CloseCancelled),ct)).Accepted,"尚无本任务出区交接证明时不忽略共享倒料周期冲突");
    await f.Db.Updateable<TaskHandshakeEntity>().SetColumns(h=>new TaskHandshakeEntity{Phase="ExitHandoverComplete",Revision=h.Revision+1}).Where(h=>h.StepId==returning.StepId).ExecuteCommandAsync();
    await f.Db.Insertable(new TaskEquipmentClaimEntity{EquipmentId="CONTRADICTORY-OWN-CLAIM",TaskId=id,AcquiredAtUtc=DateTime.UtcNow}).ExecuteCommandAsync();
    await f.Recovery.InspectAsync(ct);
    var contradictory=await f.Recovery.DecideAsync(admin,await Request(f,id,RecoveryDecision.CloseCancelled),ct);
    Check(!contradictory.Accepted&&contradictory.Message.Contains("矛盾"),"已交接与本任务残留设备归属矛盾时保持冻结："+contradictory.Message);
    await f.Db.Deleteable<TaskEquipmentClaimEntity>().Where(e=>e.TaskId==id).ExecuteCommandAsync();await f.Recovery.InspectAsync(ct);
    var closed=await f.Recovery.DecideAsync(admin,await Request(f,id,RecoveryDecision.CloseCancelled),ct);
    Check(closed.Accepted,"已出区交接的前车可关闭取消，不要求后车共享倒料位或故障低："+closed.Message);
    Check(f.Db.Queryable<TaskEquipmentClaimEntity>().Single().TaskId=="FOLLOWING-TASK"&&f.Signals.Items.Where(s=>s.StationId=="DUMPER").All(s=>s.Value==true)&&f.Gateway.Sends==0&&f.Gateway.Cancels==0,"前车取消关闭保留后车设备归属和全部共享信号，不产生车辆指令");
}
foreach (var scenario in new[] { "disabled-bound", "wrong-version", "enabled-unbound" })
{
    await using var f = await Fixture.Create();
    await f.Boot();
    var created = await f.Tasks.TryCreateAsync("PLC-01", "DUMPER", 1, ct);
    await f.Tasks.MarkAcknowledgedAsync(created.Task.TaskId, ct);
    var work = (await new DispatchOrchestrationRepository(f.Db).GetReadyStepsAsync(10, ct)).Single();
    var store = new TaskHandshakeStore(f.Db);
    if (scenario != "enabled-unbound")
        await store.BeginAsync(work, HandshakeExecutionOptions.Version, "TransportBound", [], ct);
    await f.Outbox.TryPrepareTransportStepAsync(work.TaskId, work.StepId, "AGV-01", ["SITE-A", "SITE-B"], null, ct);
    var mode = new HandshakeExecutionOptions { Enabled = scenario != "disabled-bound", PlcContractVersion = scenario == "wrong-version" ? "other-version" : HandshakeExecutionOptions.Version };
    using var dispatcher = new AgvCommandOutboxDispatcher(NullLogger<AgvCommandOutboxDispatcher>.Instance,
        new AgvCommandOutboxOptions { Enabled = true, PollIntervalMs = 100 }, f.Outbox, f.Gateway, f.Gate,
        handshakes: store, taskReader: f.Tasks, handshakeOptions: mode);
    await dispatcher.StartAsync(ct); await Task.Delay(250); await dispatcher.StopAsync(ct);
    Check(f.Gateway.Sends == 0 && (await f.Outbox.GetDueCommandsAsync(DateTimeOffset.Now, 10, ct)).Single().AttemptCount == 0,
        "契约模式不匹配不领取或发送原Outbox：" + scenario);
}
Console.WriteLine($"Execution recovery: {checks} checks passed.");

sealed class Fixture : IAsyncDisposable
{
    public required ISqlSugarClient Db;
    public RecoveryBarrier Barrier = new();
    public required MapExecutionGate Gate;
    public SwitchVehicleCache Vehicles = new(); public SwitchSignalCache Signals = new();
    public RecoveryGateway Gateway = new(); public MapSwitchSafetySettings Settings = new();
    public required DispatchTaskRepository Tasks; public required AgvCommandOutboxRepository Outbox;
    public required ExecutionRecoveryService Recovery;
    public static async Task<Fixture> Create(MapSwitchSafetySettings? settings = null)
    {
        var db = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = Path.Combine(Path.GetTempPath(), "agv-recovery-" + Guid.NewGuid().ToString("N"), "test.db") });
        await new DatabaseInitializer(db).InitializeAsync(CancellationToken.None);
        var f = new Fixture { Db = db, Gate = null!, Tasks = new(db), Outbox = new(db), Recovery = null! };
        if (settings != null) f.Settings = settings;
        f.Gate = new MapExecutionGate(f.Barrier); f.Gate.SetMode(MapExecutionMode.Running);
        f.Recovery = new(db, f.Barrier, f.Gate, f.Gateway, f.Vehicles, f.Signals, f.Settings, new ActiveMapRuntime());
        return f;
    }
    public Task Boot() => Recovery.InitializeAsync(CancellationToken.None);
    public void Fresh(string station = "SITE-A")
    {
        Vehicles.Items.Clear(); Vehicles.Items.Add(SwitchVehicleCache.Ready() with { CurrentStation = station });
        Signals.Items.Clear(); Signals.Items.Add(SwitchSignalCache.Ready());
        if (Gateway.Status != null) Gateway.Status = Gateway.Status with { UpdatedAt = DateTimeOffset.Now };
    }
    public async Task<string> Prepare()
    {
        var task = await Tasks.TryCreateAsync("PLC-01", "DUMPER", 1, CancellationToken.None);
        await Tasks.MarkAcknowledgedAsync(task.Task.TaskId, CancellationToken.None);
        var step = await Step(task.Task.TaskId);
        await Outbox.TryPrepareTransportStepAsync(task.Task.TaskId, step.StepId, "AGV-01", ["SITE-A", "SITE-B"], null, CancellationToken.None);
        return task.Task.TaskId;
    }
    public Task<DispatchTaskStepEntity> Step(string id) => Db.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == id && s.Status != 50).OrderBy(s => s.Sequence).FirstAsync();
    public ValueTask DisposeAsync() { Db.Dispose(); return ValueTask.CompletedTask; }
}
sealed class RecoveryGateway : IAgvGateway
{
    public int Sends, Cancels; public bool FailCancelResponse; public AgvTransportTaskStatus? Status;
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => Task.FromResult(new DeviceConnectionStatus("fleet", "fleet", ConnectionState.Online, DateTimeOffset.Now));
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgvVehicleStatus>>([]);
    public Task<string> CreateTransportTaskAsync(string business, string? vehicle, IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken ct) { Sends++; return Task.FromResult(business); }
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id, CancellationToken ct) => Task.FromResult(Status);
    public Task CancelTaskAsync(string id, CancellationToken ct)
    { Cancels++; if (FailCancelResponse) { FailCancelResponse = false; throw new IOException("lost cancellation reply"); } return Task.CompletedTask; }
}
sealed class CountingPlc : IPlcGateway
{
    public int Writes;
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<bool?> ReadSignalAsync(string station, string signal, CancellationToken ct) => Task.FromResult<bool?>(null);
    public Task<StationSignal?> ReadStationAsync(string station, CancellationToken ct) => Task.FromResult<StationSignal?>(null);
    public Task WriteSignalAsync(string station, string signal, bool value, CancellationToken ct) { Writes++; return Task.CompletedTask; }
}

sealed class RecoveryPlcIdentityReader(PlcIdentityFields fields) : IPlcExecutionIdentityReader
{
    public string StationId => "PLC-01"; public bool Fail;
    public PlcIdentityStamp Stamp = new("BOOT-1", "100", "1", true, SignalQuality.Good);
    public Task<PlcIdentityStamp> ReadStampAsync(CancellationToken ct) => Fail ? throw new IOException("simulated disconnect") : Task.FromResult(Stamp);
    public Task<PlcIdentityFields> ReadFieldsAsync(CancellationToken ct) => Task.FromResult(fields);
}
sealed class RecoveryVehicleIdentityReader(VehicleIdentityFrame frame) : IVehicleContainerIdentityReader
{
    public string VehicleId => "AGV-01"; public VehicleIdentityFrame Frame = frame;
    public Task<VehicleIdentityFrame> ReadAtomicAsync(CancellationToken ct) => Task.FromResult(Frame);
}
