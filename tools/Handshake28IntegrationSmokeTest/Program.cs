using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Service;
using SqlSugar;
using Microsoft.Extensions.Logging.Abstractions;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
async Task ExpectHold(Func<Task> action, string name)
{
    var held = false;
    try { await action(); } catch (InvalidOperationException) { held = true; }
    Check(held, name);
}

using (var sending = new Fixture())
{
    await sending.InitializeAsync();
    var task = await sending.CreateAsync("CNC-001", "AGV-1");
    await sending.BindAsync(task);
    await sending.PreparePickupAsync(task);
    var original = await sending.CommandAsync(task);
    sending.Signals.OverrideAddress("CNC-001", "FullBoxRemoved", "DB:changed:FullBoxRemoved");
    await sending.DispatchOutboxOnceAsync();
    var addressHeld = await sending.CommandAsync(task);
    Check(sending.Agv.Creates == 0 && addressHeld.Status == (int)AgvCommandStatus.Pending && addressHeld.AttemptCount == 0 &&
        addressHeld.CommandId == original.CommandId,
        "首段绑定后点表地址变化时保留原绑定和命令，不改用新地址发车");
    sending.Signals.Set("CNC-001", "FullBoxRemoved", false);
    foreach (var (signal, invalid) in new[]
    {
        ("BoxPresent", false), ("FullBoxRemoved", true),
        ("EmptyBoxTaskAcknowledged", true), ("EmptyBoxPlaced", true)
    })
    {
        sending.Signals.Set("CNC-001", signal, invalid);
        await sending.DispatchOutboxOnceAsync();
        var held = await sending.CommandAsync(task);
        Check(sending.Agv.Creates == 0 && held.Status == (int)AgvCommandStatus.Pending && held.AttemptCount == 0 &&
            held.CommandId == original.CommandId && held.StepId == original.StepId,
            $"首段Outbox准备后{signal}变化时不领取、不发车并保留原命令");
        sending.Signals.Set("CNC-001", signal, !invalid);
    }
    await sending.DispatchOutboxOnceAsync();
    var sent = await sending.CommandAsync(task);
    Check(sending.Agv.Creates == 1 && sent.Status == (int)AgvCommandStatus.Sent && sent.AttemptCount == 1 &&
        sent.CommandId == original.CommandId && sent.StepId == original.StepId,
        "首段有箱及0.5/0.6/0.7基线恢复后发送同一原命令，不重建任务");
}

using (var aliases = new Fixture())
{
    await aliases.InitializeAsync();
    aliases.SetPickupSite("CNC-001", "lm-pick-1");
    var task = await aliases.CreateAndPickupAsync("CNC-001", "AGV-1");
    await aliases.SetReadyAsync(task, DispatchTaskStepType.WaitForDumperReady);
    await aliases.WaitAndClaimAsync(task);
    await aliases.StartDumpAsync(task);
    aliases.Signals.Set("DUMPER", "DumpingCompletedMayLeave", true);
    await Task.Delay(2);
    await aliases.FinishReadyAsync(task, DispatchTaskStepType.DumpFullBox);
    await aliases.PrepareReturnAsync(task);
    await aliases.ProveExitAsync(task);
    await aliases.ExitAsync(task);
    aliases.Signals.Set("DUMPER", "DumpingCompletedMayLeave", false);
    await aliases.FinishExitAsync(task);
    await aliases.PlaceAsync(task);
    Check((await aliases.CurrentAsync(task)).StepType == DispatchTaskStepType.CompletePlcHandshake &&
        aliases.Signals.Get("CNC-001", "EmptyBoxPlaced") && (await aliases.InspectReturnEvidenceAsync(task))?.LocalStation == "PICK-1",
        "原工位以不区分大小写的AGV站点别名配置时，返箱核验解析为唯一逻辑节点并完成放箱通知");

    var original = aliases.Map.Current!;
    aliases.Map.Replace(new(original.MapId, original.VersionId, original.Definition with { Nodes = original.Definition.Nodes.Select(n => n.Id == "PICK-1" ? n with { AgvSiteId = "" } : n).ToArray() }, DateTimeOffset.UtcNow));
    Check(await aliases.InspectReturnEvidenceAsync(task) is null, "原工位别名映射缺失时拒绝返箱到站证据");
    aliases.Map.Replace(new(original.MapId, original.VersionId, original.Definition with { Nodes = original.Definition.Nodes.Append(new("DUPLICATE", 20, 0, MapNodeType.Station, true, [], "LM-PICK-1")).ToArray() }, DateTimeOffset.UtcNow));
    Check(await aliases.InspectReturnEvidenceAsync(task) is null, "原工位别名重复时拒绝使用歧义节点证明返箱");
    aliases.Map.Replace(new(original.MapId, original.VersionId, original.Definition with { Nodes = original.Definition.Nodes.Select(n => n.Id == "PICK-1" ? n with { Enabled = false } : n).ToArray() }, DateTimeOffset.UtcNow));
    Check(await aliases.InspectReturnEvidenceAsync(task) is null, "原工位节点禁用时拒绝返箱到站证据");
    aliases.Map.Replace(original);
    aliases.SetPickupSite("CNC-001", "lm-dump");
    aliases.Vehicles.Set("AGV-1", 0, 0, "LM-DUMP");
    Check(await aliases.InspectReturnEvidenceAsync(task) is null, "原站与倒料位通过不同别名指向同一节点时不能冒充返箱到原站");
    aliases.SetPickupSite("CNC-001", "lm-pick-1");
    aliases.Vehicles.Set("AGV-1", 10, 0, "LM-PICK-1");
    await aliases.FinishReadyAsync(task, DispatchTaskStepType.CompletePlcHandshake);
    Check((await aliases.TaskAsync(task)).Stage == DispatchTaskStage.Completed, "合法原工位别名的28阶段任务正常收尾，不永久停在返箱确认");
}

using var fixture = new Fixture();
await fixture.InitializeAsync();
var a = await fixture.CreateAsync("CNC-001", "AGV-1");
var root = await fixture.CurrentAsync(a);
var config = fixture.Options.DumperExit.ResourceIds;
fixture.Options.DumperExit.ResourceIds = [];
await ExpectHold(() => fixture.Engine.BindBeforeTransportAsync(root, default), "未配置倒料边界时首段绑定拒绝，不开始新28阶段任务");
fixture.Options.DumperExit.ResourceIds = config;
var originalMap = fixture.Map.Current!;
fixture.Map.Replace(new(originalMap.MapId, originalMap.VersionId,
    originalMap.Definition with { Resources = originalMap.Definition.Resources.Select(r => r with { Boundary = null }).ToArray() }, DateTimeOffset.UtcNow));
await ExpectHold(() => fixture.Engine.BindBeforeTransportAsync(root, default), "缺少资源多边形边界在首段就拒绝");
fixture.Map.Replace(originalMap);
var originalProfiles = fixture.Options.DumperExit.BodyProfiles;
fixture.Options.DumperExit.BodyProfiles = originalProfiles.Select(p => p with { CalibrationVerified = false }).ToList();
await ExpectHold(() => fixture.Engine.BindBeforeTransportAsync(root, default), "未核定整车及载荷包络在首段就拒绝");
fixture.Options.DumperExit.BodyProfiles = originalProfiles;
fixture.Signals.Set("CNC-001", "FullBoxRequested", true);
await ExpectHold(() => fixture.Engine.BindBeforeTransportAsync(root, default), "PLC未撤呼叫时禁止首段取箱运输");
fixture.Signals.Set("CNC-001", "FullBoxRequested", false);
fixture.Signals.Set("CNC-001", "FullBoxRequestAcknowledged", false);
await ExpectHold(() => fixture.Engine.BindBeforeTransportAsync(root, default), "未建立上位机受理应答时禁止首段运输");
fixture.Signals.Set("CNC-001", "FullBoxRequestAcknowledged", true);
await fixture.BindAsync(a);
Check((await fixture.Store.GetAsync(a, root.StepId, default))?.ContractVersion == "plc-28-stage-v2", "新任务绑定明确28阶段契约版本");

await fixture.SetReadyAsync(a, DispatchTaskStepType.PickupFullBox);
fixture.Signals.Set("CNC-001", "BoxPresent", false);
await fixture.TickAsync(a);
Check(await fixture.PhaseAsync(a) == "NotifyRemoved", "观察实际无箱后单独进入取箱通知阶段");
fixture.Signals.FailAfterNextWrite = true;
try { await fixture.TickAsync(a); throw new Exception("Expected simulated lost response"); } catch (IOException) { }
var pending = (await fixture.Store.GetAsync(a, (await fixture.CurrentAsync(a)).StepId, default))!;
Check(pending.Phase == "WritePending" && pending.PendingSignal == "CNC-001/FullBoxRemoved", "取箱写失应答保留原写意图");
var beforeRestartWrites = fixture.Signals.Writes.Count;
fixture.RestartEngine();
await ExpectHold(() => fixture.TickAsync(a), "重建执行器后不自动重发未知取箱写入");
Check(fixture.Signals.Writes.Count == beforeRestartWrites, "重启未知写入没有重复PLC写");
Check(await fixture.Store.MoveAsync(pending, pending.NextPhase!, "isolated explicit recovery review", null, null, null, default), "夹具明确核对原意图后CAS续接");
await fixture.TickAsync(a);
fixture.Signals.Set("CNC-001", "FullBoxRequested", true);
await fixture.TickAsync(a);
Check(await fixture.PhaseAsync(a) == "AwaitPickupReadback" && fixture.Signals.Get("CNC-001", "FullBoxRequestAcknowledged"),
    "呼叫仍高时不提前撤受理应答或完成取箱握手");
fixture.Signals.Set("CNC-001", "FullBoxRequested", false);
await fixture.FinishReadyAsync(a, DispatchTaskStepType.PickupFullBox);
Check(fixture.Signals.Get("CNC-001", "FullBoxRemoved") && fixture.Signals.Get("CNC-001", "EmptyBoxTaskAcknowledged"),
    "取箱握手完成后0.5和返箱责任保持为1");
Check(!fixture.Signals.Writes.Any(w => w.Station == "CNC-001" && w.Signal == "FullBoxRemoved" && !w.Value), "取箱阶段不再清0.5");

await fixture.SetReadyAsync(a, DispatchTaskStepType.WaitForDumperReady);
for (var i = 0; i < 8 && await fixture.PhaseAsync(a) != "AwaitReady"; i++) await fixture.TickAsync(a);
Check(await fixture.EquipmentOwnerAsync() == a, "首任务实际取得倒料设备独占归属");
var b = await fixture.CreateAndPickupAsync("CNC-002", "AGV-2");
await fixture.SetReadyAsync(b, DispatchTaskStepType.WaitForDumperReady);
await ExpectHold(() => fixture.TickAsync(b), "第二任务不能消费第一任务倒料许可");
var equipmentWait = await fixture.Db.Queryable<TrafficRequestEntity>().Where(r => r.TaskId == b && r.State == "Waiting").SingleAsync();
Check(equipmentWait.WaitingCategory == "Equipment" && equipmentWait.Reason.Contains(a), "设备归属等待进入正式等待记录，保留占用方及类别而非仅写异常文字");
Check(fixture.Signals.Get("DUMPER", "AgvAtWaitingPosition") &&
    await fixture.Store.GetAsync(b, (await fixture.CurrentAsync(b)).StepId, default) is null,
    "前车等待申请为高时后车先查原归属，不误建ReviewRequired检查点");
var c = await fixture.CreateAndPickupAsync("CNC-003", "AGV-3");
await fixture.SetReadyAsync(c, DispatchTaskStepType.WaitForDumperReady);
await ExpectHold(() => fixture.TickAsync(c), "第三任务等待同一倒料设备");
await fixture.WaitAndClaimAsync(a);

await fixture.StartDumpAsync(a);
fixture.Signals.Set("DUMPER", "DumpingCompletedMayLeave", true);
await Task.Delay(2);
await fixture.FinishReadyAsync(a, DispatchTaskStepType.DumpFullBox);
Check(fixture.Signals.Get("DUMPER", "AgvAtDumpingPosition"), "Done完成仅允许离开，倒料周期仍保持");
await fixture.PrepareReturnAsync(a);
var returned = (await fixture.Writer.GetStepsAsync(a, default)).Single(s => s.StepType == DispatchTaskStepType.ReturnEmptyBox);
Check(returned.Status == DispatchTaskStepStatus.Executing, "使用真实Outbox准备与发送确认，将返程保持Executing");
Check(TransportRouteSnapshots.Read(await fixture.CommandAsync(a)).NodeIds.SequenceEqual(["DUMP", "EXIT", "PICK-1"]), "原发送返程冻结有序路径和身份");
Check(await fixture.LeaseCountAsync(a, "DUMP-AREA") > 0 && await fixture.LeaseCountAsync(a, "RETURN-1") == 1, "出区前同时保留倒料与返程其他路权");

fixture.Vehicles.Set("AGV-1", 0, 0, "LM-DUMP");
await fixture.ExitAsync(a);
Check(fixture.Signals.Get("DUMPER", "AgvAtDumpingPosition") && await fixture.EquipmentOwnerAsync() == a, "区内定位不清周期或交接设备");
fixture.Vehicles.Set("AGV-1", 4, 0, null, observedAt: DateTimeOffset.UtcNow.AddMinutes(-1));
await fixture.ExitAsync(a);
Check(fixture.Signals.Get("DUMPER", "AgvAtDumpingPosition"), "过期区外定位不能证明出区");
fixture.Vehicles.Set("AGV-1", 4, 0, null);
fixture.Agv.State = AgvTransportTaskState.Unknown;
for (var i = 0; i < 2; i++) { await Task.Delay(120); fixture.Vehicles.Set("AGV-1", 4, 0, null); await fixture.ExitAsync(a); }
await fixture.ExitAsync(a);
Check(fixture.Agv.Queries > 0 && fixture.Signals.Get("DUMPER", "AgvAtDumpingPosition") && await fixture.ReturnPhaseAsync(a) == "AwaitPhysicalExit", "原任务查询Unknown不能清周期");
fixture.Agv.State = AgvTransportTaskState.GoingToDestination;
fixture.Preflight.Accepted = false;
var mapCalls = fixture.Preflight.Calls;
await fixture.ExitAsync(a);
Check(fixture.Preflight.Calls > mapCalls && fixture.Signals.Get("DUMPER", "AgvAtDumpingPosition"), "车端地图未核验不能清周期");
fixture.Preflight.Accepted = true;
await fixture.ProveExitAsync(a);
Check(await fixture.ReturnPhaseAsync(a) == "ClearDumpingAfterExit", "两次区外证据持久化后进入清倒料周期阶段");
Check((await fixture.Writer.GetStepsAsync(a, default)).Single(s => s.StepType == DispatchTaskStepType.ReturnEmptyBox).Status == DispatchTaskStepStatus.Executing,
    "实际出区核定不需要等返程完成");

fixture.Vehicles.TryGet("AGV-1", out var latestOutside);
fixture.Vehicles.Set("AGV-1", 0, 0, "LM-DUMP", latestOutside.LastSuccessfulPollAt!.Value.AddTicks(1));
await fixture.ExitAsync(a);
Check(fixture.Signals.Get("DUMPER", "AgvAtDumpingPosition") && await fixture.ReturnPhaseAsync(a) == "ClearDumpingAfterExit",
    "间隔不足但最新位置已经回区时，不复用旧外外证据清周期");

fixture.Signals.FailAfterNextWrite = true;
var lostClear = false;
for (var i = 0; i < 8 && !lostClear; i++)
{
    await Task.Delay(120); fixture.Vehicles.Set("AGV-1", 4, 0, null);
    try { await fixture.ExitAsync(a, swallowHold: false); } catch (IOException) { lostClear = true; }
}
Check(lostClear, "重新取得有效区外证据后尝试清位并注入失应答");
var returnCheckpoint = (await fixture.Store.GetAsync(a, returned.StepId, default))!;
Check(returnCheckpoint.Phase == "WritePending" && returnCheckpoint.PendingSignal == "DUMPER/AgvAtDumpingPosition", "出区清位失应答仍保留原意图");
var exitWriteCount = fixture.Signals.Writes.Count;
fixture.RestartEngine();
await fixture.ExitAsync(a);
Check(fixture.Signals.Writes.Count == exitWriteCount && await fixture.EquipmentOwnerAsync() == a, "清位失应答重启不重写也不提前交接");
Check(await fixture.Store.MoveAsync(returnCheckpoint, returnCheckpoint.NextPhase!, "isolated explicit exit write review", null, null, null, default), "夹具核对原清位意图后恢复");
await fixture.ExitAsync(a);
Check(await fixture.ReturnPhaseAsync(a) == "AwaitDumperClosed" && await fixture.EquipmentOwnerAsync() == a, "PLC Done尚未清零时保持设备与路权");
fixture.Signals.Set("DUMPER", "DumpingCompletedMayLeave", false);
await fixture.ExitAsync(a);
Check(await fixture.ReturnPhaseAsync(a) == "ReleaseDumper", "PLC当前闭合推进到待交接，但尚未交接");
var injectedClosureChange = false;
fixture.Preflight.BeforeCheck = () =>
{
    fixture.Signals.Set("DUMPER", "DumpingCompletedMayLeave", true);
    injectedClosureChange = true;
};
for (var i = 0; i < 8 && !injectedClosureChange; i++)
{
    await Task.Delay(120); fixture.Vehicles.Set("AGV-1", 4, 0, null); await fixture.ExitAsync(a);
}
Check(injectedClosureChange && await fixture.EquipmentOwnerAsync() == a && await fixture.LeaseCountAsync(a, "DUMP-AREA") > 0,
    "地图/任务查询期间Done重新变高时，最终交接重新核对共享闭合状态");
fixture.Preflight.BeforeCheck = null;
fixture.Signals.Set("DUMPER", "DumpingCompletedMayLeave", false);
await fixture.FinishExitAsync(a);
Check(await fixture.EquipmentOwnerAsync() is null, "Done闭合后交接设备归属");
Check(await fixture.LeaseCountAsync(a, "DUMP-AREA") == 0 && await fixture.LeaseCountAsync(a, "RETURN-1") == 1,
    "仅释放本任务倒料租约，保留其他返程路权");
Check(fixture.Signals.Get("CNC-001", "FullBoxRemoved") && fixture.Signals.Get("CNC-001", "EmptyBoxTaskAcknowledged"), "倒料交接后取箱记录与返箱责任继续保持");
Check((await fixture.Writer.GetStepsAsync(a, default)).Single(s => s.StepType == DispatchTaskStepType.ReturnEmptyBox).Status == DispatchTaskStepStatus.Executing,
    "设备已经可交接时第一车仍在返程中");
var writesAfterHandover = fixture.Signals.Writes.Count;
fixture.RestartEngine();
await fixture.ExitAsync(a);
Check(fixture.Signals.Writes.Count == writesAfterHandover && await fixture.ReturnPhaseAsync(a) == "ExitHandoverComplete", "完成交接重启后不会重复清位或回滚阶段");

await fixture.WaitAndClaimAsync(b);
Check(await fixture.EquipmentOwnerAsync() == b, "第一车返程未完成，第二任务可取得倒料归属");
await fixture.StartDumpAsync(b);
var bDumpLease = await fixture.LeaseCountAsync(b, "DUMP-AREA");
Check(bDumpLease > 0 && fixture.Signals.Get("DUMPER", "AgvAtDumpingPosition"), "第二车建立自己的倒料周期及路权");
await ExpectHold(() => fixture.TickAsync(c), "第三任务继续等待第二车归属");
var sharedWriteCount = fixture.Signals.Writes.Count(w => w.Station == "DUMPER");
await fixture.PlaceAsync(a);
fixture.Signals.Set("CNC-001", "FullBoxRequested", true);
await fixture.TickAsync(a);
Check(fixture.Signals.Get("CNC-001", "FullBoxRemoved") && fixture.Signals.Get("CNC-001", "EmptyBoxTaskAcknowledged"), "收尾时呼叫未低不能解除本轮责任或复用旧高电平");
fixture.Signals.Set("CNC-001", "FullBoxRequested", false);
for (var i = 0; i < 10 && await fixture.PhaseAsync(a) != "ClearEmptyResponsibility"; i++) await fixture.TickAsync(a);
Check(await fixture.PhaseAsync(a) == "ClearEmptyResponsibility" && !fixture.Signals.Get("CNC-001", "FullBoxRemoved") &&
    !fixture.Signals.Get("CNC-001", "EmptyBoxPlaced") && fixture.Signals.Get("CNC-001", "EmptyBoxTaskAcknowledged"),
    "清返箱责任前已确认本轮通知闭合及呼叫低");
await fixture.TickAsync(a);
fixture.Signals.Set("CNC-001", "FullBoxRequested", true);
await fixture.FinishReadyAsync(a, DispatchTaskStepType.CompletePlcHandshake);
Check((await fixture.TaskAsync(a)).Stage == DispatchTaskStage.Completed, "第一任务返箱后完整28阶段闭合");
Check(fixture.Signals.Get("CNC-001", "FullBoxRequested"), "责任清零后到来的下一轮呼叫不阻断旧任务完成，也不被旧任务清除");
Check(fixture.Signals.Get("DUMPER", "AgvAtDumpingPosition") && await fixture.EquipmentOwnerAsync() == b &&
    await fixture.LeaseCountAsync(b, "DUMP-AREA") == bDumpLease, "第一车收尾不清第二车周期、归属或倒料路权");
Check(fixture.Signals.Writes.Count(w => w.Station == "DUMPER") == sharedWriteCount, "第一车最终收尾不写后车共享PLC输出");
var stationClears = fixture.Signals.Writes.Where(w => w.Station == "CNC-001" && !w.Value &&
    w.Signal is "FullBoxRemoved" or "EmptyBoxPlaced" or "EmptyBoxTaskAcknowledged").Select(w => w.Signal).ToArray();
Check(stationClears.SequenceEqual(["FullBoxRemoved", "EmptyBoxPlaced", "EmptyBoxTaskAcknowledged"]), "25清取箱、26清放箱、27最后解除返箱责任");
Check(await fixture.LeaseCountAsync(a) == 0, "第一车完成只清理自身剩余路权");

fixture.Signals.Set("DUMPER", "DumpingCompletedMayLeave", true);
await Task.Delay(2);
await fixture.FinishReadyAsync(b, DispatchTaskStepType.DumpFullBox);
await fixture.PrepareReturnAsync(b, markSent: false);
var pendingReturn = await fixture.CommandAsync(b);
fixture.Signals.Set("DUMPER", "Faulted", true);
await fixture.DispatchOutboxOnceAsync();
Check(fixture.Agv.Creates == 0 && (await fixture.CommandAsync(b)).AttemptCount == 0, "Outbox准备后设备故障，真实发送轮询不领取也不发车");
fixture.Signals.Set("DUMPER", "Faulted", false);
fixture.Signals.Set("CNC-002", "EmptyBoxTaskAcknowledged", false);
await fixture.DispatchOutboxOnceAsync();
Check(fixture.Agv.Creates == 0 && (await fixture.CommandAsync(b)).CommandId == pendingReturn.CommandId, "返箱责任丢失时保留原待发命令并阻断发送");
fixture.Signals.Set("CNC-002", "EmptyBoxTaskAcknowledged", true);
fixture.Signals.Set("DUMPER", "DumpingCompletedMayLeave", false);
await fixture.DispatchOutboxOnceAsync();
Check(fixture.Agv.Creates == 0 && (await fixture.CommandAsync(b)).Status == (int)AgvCommandStatus.Pending, "返程许可撤回时不领取命令、不增加尝试次数");
fixture.Signals.Set("DUMPER", "DumpingCompletedMayLeave", true);
await fixture.Outbox.TryClaimAsync(pendingReturn.CommandId, DateTimeOffset.UtcNow, default);
await fixture.Outbox.ScheduleRetryAsync(pendingReturn.CommandId, "isolated known-before-send failure", DateTimeOffset.UtcNow.AddMilliseconds(-1), 3, default);
await fixture.DispatchOutboxOnceAsync();
var sentRetry = await fixture.CommandAsync(b);
Check(fixture.Agv.Creates == 1 && sentRetry.Status == (int)AgvCommandStatus.Sent && sentRetry.CommandId == pendingReturn.CommandId &&
    sentRetry.StepId == pendingReturn.StepId && sentRetry.AttemptCount == 2, "原命令RetryScheduled仍可核验后按原ID发送，不永久卡住或创建新命令");
await fixture.ProveExitAsync(b);
await fixture.ExitAsync(b);
fixture.Signals.Set("DUMPER", "DumpingCompletedMayLeave", false);
var bReturning = (await fixture.Writer.GetStepsAsync(b, default)).Single(s => s.StepType == DispatchTaskStepType.ReturnEmptyBox);
await fixture.Orchestrator.CompleteExecutingStepAsync(b, bReturning.VendorTaskId!, default);
fixture.Vehicles.Set("AGV-2", 10, 5, "LM-PICK-2");
await fixture.TickAsync(b);
Check(!fixture.Signals.Get("CNC-002", "EmptyBoxPlaced"), "原返程已完成且到原站，出区交接尚未结束仍不提前通知放箱");
await fixture.FinishExitAsync(b);
await fixture.WaitAndClaimAsync(c);
Check(await fixture.EquipmentOwnerAsync() == c && fixture.Signals.Get("CNC-002", "EmptyBoxTaskAcknowledged"), "第二车交接后第三任务取得设备，第二车返箱责任仍保持");

var repeated = await fixture.CreateAsync("CNC-001", "AGV-1");
await fixture.BindAsync(repeated);
Check(repeated != a && (await fixture.Store.GetAsync(repeated, (await fixture.CurrentAsync(repeated)).StepId, default))?.ContractVersion == HandshakeExecutionOptions.Version,
    "同工位上一轮完成后可受理新任务并建立独立28阶段绑定");
// Keep this new task separate from the independent old-contract fixture below.
await fixture.Db.Updateable<DispatchTaskEntity>().SetColumns(t => new DispatchTaskEntity { Stage = (int)DispatchTaskStage.Cancelled }).Where(t => t.TaskId == repeated).ExecuteCommandAsync();
var old = await fixture.CreateAsync("CNC-001", "AGV-1");
var oldRoot = await fixture.CurrentAsync(old);
await fixture.Store.BeginAsync(oldRoot, HandshakeExecutionOptions.LegacyVersion, "TransportBound", [], default);
var oldWrites = fixture.Signals.Writes.Count;
await ExpectHold(() => fixture.Engine.BindBeforeTransportAsync(oldRoot, default), "旧契约检查点不静默迁移为28阶段");
Check(fixture.Signals.Writes.Count == oldWrites && (await fixture.Store.GetAsync(old, oldRoot.StepId, default))?.ContractVersion == HandshakeExecutionOptions.LegacyVersion,
    "旧任务原契约保持且没有设备写入");
Check(fixture.Signals.Writes.All(w => w.Signal is not "BoxPresent" and not "DumperReady" and not "DumpingCompletedMayLeave"), "执行器从不写PLC侧检测与许可反馈");
Console.WriteLine($"PASS: {checks} 28-stage SQLite/Outbox/handshake/equipment/traffic integration checks. Evidence database: {fixture.DatabasePath}");

sealed class Fixture : IDisposable
{
    public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), "agv-28-" + Guid.NewGuid().ToString("N"), "test.db");
    public ISqlSugarClient Db { get; private set; } = null!;
    public DispatchTaskRepository Writer { get; private set; } = null!;
    public DispatchOrchestrationRepository Orchestrator { get; private set; } = null!;
    public TaskHandshakeStore Store { get; private set; } = null!;
    public TrafficResourceManager Traffic { get; private set; } = null!;
    public AgvCommandOutboxRepository Outbox { get; private set; } = null!;
    public SignalHarness Signals { get; } = new();
    public VehicleHarness Vehicles { get; } = new();
    public GatewayHarness Agv { get; } = new();
    public PreflightHarness Preflight { get; } = new();
    public ActiveMapRuntime Map { get; } = new();
    public PersistentHandshakeExecutor Engine { get; private set; } = null!;
    public HandshakeExecutionOptions Options { get; } = new()
    {
        Enabled = true, PlcContractVersion = HandshakeExecutionOptions.Version, StateMaxAgeSeconds = 30,
        DumperExit = new()
        {
            ResourceIds = ["dump-area"], MinimumObservationIntervalSeconds = 0.1, MaximumAgeSeconds = 3,
            MinimumConfidence = 0.9, SafetyMarginMeters = 0.15, MaximumRouteDeviationMeters = 0.25,
            BodyProfiles = Enumerable.Range(1, 3).Select(i => new DumperExitBodyProfile("BODY-" + i, "AGV-" + i, 0.5, 0.05, true, true)).ToList()
        }
    };
    private readonly DispatchTaskCreationOptions creation = new() { DumperStationId = "DUMPER" };
    private readonly DispatchOrchestratorOptions dispatch = new()
    {
        DumperWaitingSite = "WAIT", DumperDumpingSite = "DUMP",
        StationPickupSites = new() { ["CNC-001"] = "PICK-1", ["CNC-002"] = "PICK-2", ["CNC-003"] = "PICK-3" }
    };
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        Db = DatabaseFactory.Create(new() { DatabasePath = DatabasePath });
        await new DatabaseInitializer(Db).InitializeAsync(default);
        Writer = new(Db); Orchestrator = new(Db); Store = new(Db, Map, Signals);
        MapPoint2D[] Box(double x, double y, double w, double h) => [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];
        var nodes = new List<MapNode>
        {
            new("DUMP", 0, 0, MapNodeType.Station, true, ["DUMP-AREA"], "LM-DUMP"),
            new("EXIT", 5, 0, MapNodeType.Waypoint, true, [], "LM-EXIT"),
            new("WAIT", -5, 0, MapNodeType.WaitingPoint, true, [], "LM-WAIT")
        };
        var edges = new List<MapEdge> { new("DUMP-EXIT", "DUMP", "EXIT", 5, 1, true, true, ["DUMP-AREA"]) };
        var resources = new List<TrafficResourceDefinition> { new("DUMP-AREA", TrafficResourceType.Station, 1, Box(-1, -1, 2, 2)) };
        for (var i = 1; i <= 3; i++)
        {
            var y = (i - 1) * 5;
            nodes.Add(new("PICK-" + i, 10, y, MapNodeType.Station, true, [], "LM-PICK-" + i));
            edges.Add(new("EXIT-PICK-" + i, "EXIT", "PICK-" + i, Math.Sqrt(25 + y * y), 1, true, true, ["RETURN-" + i]));
            resources.Add(new("RETURN-" + i, TrafficResourceType.SingleLane, 1, Box(6, y, 2, 1)));
            Vehicles.Set("AGV-" + i, 10, y, "LM-PICK-" + i);
        }
        Map.Replace(new("MAP", "V1", new("28 integration", "synthetic fixture", "LocalCartesian", "m", nodes, edges, resources), DateTimeOffset.UtcNow));
        Traffic = new(Db, new(resources), new(TimeSpan.FromMinutes(2)));
        Outbox = new(Db, Map);
        RestartEngine();
    }
    public void RestartEngine() => Engine = new(Store, Signals, Signals, Orchestrator, Writer, Traffic, creation, dispatch, Options, Vehicles, Map, Agv, Preflight);
    public void SetPickupSite(string station,string site) => dispatch.StationPickupSites[station] = site;
    public async Task<TaskExitEvidence?> InspectReturnEvidenceAsync(string task)
    {
        var method = typeof(PersistentHandshakeExecutor).GetMethod("ReturnEvidenceAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return await (Task<TaskExitEvidence?>)method.Invoke(Engine, [await CurrentAsync(task), CancellationToken.None])!;
    }
    public async Task<string> CreateAsync(string station, string vehicle)
    {
        Signals.Initialize(station, "DUMPER");
        var created = await Writer.TryCreateAsync(station, "DUMPER", 1, default);
        var id = created.Task.TaskId;
        await Writer.MarkAcknowledgedAsync(id, default);
        // The separately tested creation coordinator has acknowledged the request;
        // this fixture starts its executor scope at stage 4, after PLC withdrew it.
        Signals.Set(station, "FullBoxRequestAcknowledged", true);
        Signals.Set(station, "FullBoxRequested", false);
        await Db.Updateable<DispatchTaskEntity>().SetColumns(t => new DispatchTaskEntity { VehicleId = vehicle }).Where(t => t.TaskId == id).ExecuteCommandAsync();
        return id;
    }
    public async Task BindAsync(string task) { Signals.Refresh(); await Engine.BindBeforeTransportAsync(await CurrentAsync(task), default); }
    public async Task<string> CreateAndPickupAsync(string station, string vehicle)
    {
        var task = await CreateAsync(station, vehicle); await BindAsync(task);
        await SetReadyAsync(task, DispatchTaskStepType.PickupFullBox);
        Signals.Set(station, "BoxPresent", false);
        await FinishReadyAsync(task, DispatchTaskStepType.PickupFullBox);
        return task;
    }
    public async Task<DispatchTaskStepWorkItem> CurrentAsync(string task) => (await Orchestrator.GetReadyStepsAsync(100, default)).Single(s => s.TaskId == task);
    public async Task<DispatchTaskWorkItem> TaskAsync(string task) => (await Orchestrator.GetAssignedTasksAsync(default)).FirstOrDefault(t => t.TaskId == task) ??
        new(task, (await Db.Queryable<DispatchTaskEntity>().SingleAsync(t => t.TaskId == task)).StationId, null, null,
            (DispatchTaskStage)(await Db.Queryable<DispatchTaskEntity>().SingleAsync(t => t.TaskId == task)).Stage, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    public async Task<string> PhaseAsync(string task) => (await Store.GetAsync(task, (await CurrentAsync(task)).StepId, default))?.Phase ?? "";
    public async Task<string> ReturnPhaseAsync(string task)
    {
        var step = (await Writer.GetStepsAsync(task, default)).Single(s => s.StepType == DispatchTaskStepType.ReturnEmptyBox);
        return (await Store.GetAsync(task, step.StepId, default))?.Phase ?? "";
    }
    public async Task TickAsync(string task) { Signals.Refresh(); await Engine.ProcessAsync(await CurrentAsync(task), default); }
    public async Task SetReadyAsync(string task, DispatchTaskStepType type)
    {
        var parent = await Db.Queryable<DispatchTaskEntity>().SingleAsync(t => t.TaskId == task);
        var step = (await Writer.GetStepsAsync(task, default)).Single(s => s.StepType == type);
        await Db.Updateable<DispatchTaskStepEntity>().SetColumns(s => new DispatchTaskStepEntity
            { Status = 50, CompletedAt = DateTime.Now, VehicleId = parent.VehicleId, VendorTaskId = "fixture-prior" })
            .Where(s => s.TaskId == task && s.Sequence < step.Sequence && s.Status != 50).ExecuteCommandAsync();
        await Db.Updateable<DispatchTaskStepEntity>().SetColumns(s => new DispatchTaskStepEntity
            { Status = 10, StartedAt = DateTime.Now, CompletedAt = null, VehicleId = parent.VehicleId, UpdatedAt = DateTime.Now })
            .Where(s => s.TaskId == task && s.StepId == step.StepId).ExecuteCommandAsync();
        var stage = type switch
        {
            DispatchTaskStepType.PickupFullBox => DispatchTaskStage.PickingUp,
            DispatchTaskStepType.WaitForDumperReady => DispatchTaskStage.WaitingForDumper,
            DispatchTaskStepType.DumpFullBox or DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStage.Dumping,
            _ => DispatchTaskStage.WaitingForPlcConfirmation
        };
        await Db.Updateable<DispatchTaskEntity>().SetColumns(t => new DispatchTaskEntity { Stage = (int)stage, UpdatedAt = DateTime.Now }).Where(t => t.TaskId == task).ExecuteCommandAsync();
    }
    public async Task FinishReadyAsync(string task, DispatchTaskStepType type)
    {
        for (var i = 0; i < 30; i++)
        {
            var step = (await Writer.GetStepsAsync(task, default)).Single(s => s.StepType == type);
            if (step.Status == DispatchTaskStepStatus.Completed) return;
            await TickAsync(task);
        }
        throw new Exception($"{type} did not finish; phase={await PhaseAsync(task)}");
    }
    public async Task WaitAndClaimAsync(string task)
    {
        if ((await CurrentAsync(task)).StepType != DispatchTaskStepType.WaitForDumperReady) await SetReadyAsync(task, DispatchTaskStepType.WaitForDumperReady);
        Signals.Set("DUMPER", "DumperReady", false);
        for (var i = 0; i < 10 && await PhaseAsync(task) != "AwaitReady"; i++) await TickAsync(task);
        Signals.Set("DUMPER", "DumperReady", true);
        for (var i = 0; i < 10 && await PhaseAsync(task) != "AwaitReadyConsumed"; i++) await TickAsync(task);
        Signals.Set("DUMPER", "DumperReady", false);
        await FinishReadyAsync(task, DispatchTaskStepType.WaitForDumperReady);
    }
    public async Task StartDumpAsync(string task)
    {
        var enter = (await Writer.GetStepsAsync(task, default)).Single(s => s.StepType == DispatchTaskStepType.EnterDumpingPosition);
        var parent = await TaskAsync(task);
        await Traffic.RecordRouteAsync(new(enter.StepId, task, parent.VehicleId!, "WAIT", "DUMP", [], ["DUMP-AREA"], false, "isolated entering grant") { MapId = "MAP", MapVersionId = "V1" }, default);
        if (!(await Traffic.TryAcquireAsync(enter.StepId, task, parent.VehicleId!, ["DUMP-AREA"], default)).Acquired) throw new Exception("Fixture dump lease blocked");
        await SetReadyAsync(task, DispatchTaskStepType.DumpFullBox);
        Signals.Set("DUMPER", "DumpingCompletedMayLeave", false);
        for (var i = 0; i < 10 && await PhaseAsync(task) != "AwaitCurrentDone"; i++) await TickAsync(task);
    }
    public async Task PrepareReturnAsync(string task, bool markSent = true)
    {
        var current = await CurrentAsync(task);
        if (current.StepType != DispatchTaskStepType.ReturnEmptyBox) throw new Exception("Return step not ready");
        var vehicle = current.VehicleId!; var index = vehicle[^1].ToString();
        await Traffic.RecordRouteAsync(new(current.StepId, task, vehicle, "DUMP", "PICK-" + index,
            ["DUMP-EXIT", "EXIT-PICK-" + index], ["DUMP-AREA", "RETURN-" + index], false, "isolated frozen return route") { MapId = "MAP", MapVersionId = "V1" }, default);
        if (!(await Traffic.TryAcquireAsync(current.StepId, task, vehicle, ["DUMP-AREA", "RETURN-" + index], default)).Acquired) throw new Exception("Return lease blocked");
        if (!await Outbox.TryPrepareTransportStepAsync(task, current.StepId, vehicle, ["LM-DUMP", "LM-EXIT", "LM-PICK-" + index], null, default)) throw new Exception("Return outbox not prepared");
        if (!markSent) return;
        var command = await CommandAsync(task);
        if (await Outbox.TryClaimAsync(command.CommandId, DateTimeOffset.UtcNow, default) is null) throw new Exception("Return outbox not claimed");
        await Outbox.MarkSentAsync(command.CommandId, current.StepId, default);
        Agv.Tasks[current.StepId] = (task, vehicle);
        Vehicles.Set(vehicle, 0, 0, "LM-DUMP");
    }
    public async Task PreparePickupAsync(string task)
    {
        var current = await CurrentAsync(task);
        if (current.StepType != DispatchTaskStepType.GoToPickup) throw new Exception("Pickup step not ready");
        var vehicle = current.VehicleId!; var index = vehicle[^1].ToString();
        await Traffic.RecordRouteAsync(new(current.StepId, task, vehicle, "PICK-" + index, "PICK-" + index,
            ["EXIT-PICK-" + index, "EXIT-PICK-" + index], ["RETURN-" + index], false, "isolated original pickup loop") { MapId = "MAP", MapVersionId = "V1" }, default);
        if (!(await Traffic.TryAcquireAsync(current.StepId, task, vehicle, ["RETURN-" + index], default)).Acquired) throw new Exception("Pickup lease blocked");
        if (!await Outbox.TryPrepareTransportStepAsync(task, current.StepId, vehicle, ["LM-PICK-" + index, "LM-EXIT", "LM-PICK-" + index], null, default)) throw new Exception("Pickup outbox not prepared");
    }
    public Task<AgvCommandOutboxEntity> CommandAsync(string task) => Db.Queryable<AgvCommandOutboxEntity>().SingleAsync(c => c.TaskId == task);
    public async Task ExitAsync(string task, bool swallowHold = true)
    {
        Signals.Refresh();
        try { await Engine.ProcessExitAsync(await TaskAsync(task), default); }
        catch (InvalidOperationException) when (swallowHold) { }
    }
    public async Task ProveExitAsync(string task)
    {
        var parent = await TaskAsync(task);
        for (var i = 0; i < 8; i++)
        {
            Vehicles.Set(parent.VehicleId!, 4, 0, null);
            await ExitAsync(task, false);
            if (await ReturnPhaseAsync(task) == "ClearDumpingAfterExit") return;
            await Task.Delay(120);
        }
        throw new Exception("Physical exit proof not established: " + await ReturnPhaseAsync(task));
    }
    public async Task FinishExitAsync(string task)
    {
        var parent = await TaskAsync(task);
        for (var i = 0; i < 12; i++)
        {
            if (Vehicles.TryGet(parent.VehicleId!, out var position))
                Vehicles.Set(parent.VehicleId!, position.X!.Value, position.Y!.Value, position.CurrentStation);
            await ExitAsync(task, false);
            if (await ReturnPhaseAsync(task) == "ExitHandoverComplete") return;
            await Task.Delay(120);
        }
        throw new Exception("Exit handover did not complete: " + await ReturnPhaseAsync(task));
    }
    public async Task PlaceAsync(string task)
    {
        var parent = await TaskAsync(task);
        var returned = (await Writer.GetStepsAsync(task, default)).Single(s => s.StepType == DispatchTaskStepType.ReturnEmptyBox);
        await Orchestrator.CompleteExecutingStepAsync(task, returned.VendorTaskId!, default);
        var index = parent.VehicleId![^1] - '0';
        Vehicles.Set(parent.VehicleId, 10, (index - 1) * 5, "LM-PICK-" + index);
        await TickAsync(task);
        Signals.Set(parent.StationId, "BoxPresent", true);
        await FinishReadyAsync(task, DispatchTaskStepType.PlaceEmptyBox);
    }
    public async Task DispatchOutboxOnceAsync()
    {
        using var sender = new AgvCommandOutboxDispatcher(NullLogger<AgvCommandOutboxDispatcher>.Instance, new() { Enabled = true },
            Outbox, Agv, new MapExecutionGate(), handshakes: Store, taskReader: Writer, handshakeOptions: Options, handshake: Engine);
        var method = typeof(AgvCommandOutboxDispatcher).GetMethod("DispatchDueCommandsAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)method.Invoke(sender, [CancellationToken.None])!;
    }
    public async Task<string?> EquipmentOwnerAsync() => (await Db.Queryable<TaskEquipmentClaimEntity>().Where(e => e.EquipmentId == "DUMPER").FirstAsync())?.TaskId;
    public Task<int> LeaseCountAsync(string task, string? resource = null) => resource is null
        ? Db.Queryable<TrafficResourceLeaseEntity>().Where(l => l.TaskId == task).CountAsync()
        : Db.Queryable<TrafficResourceLeaseEntity>().Where(l => l.TaskId == task && l.ResourceId == resource).CountAsync();
    public void Dispose() => Db?.Dispose();
}

sealed record PlcWrite(string Station, string Signal, bool Value);
sealed class SignalHarness : IPlcSignalCache, IPlcGateway
{
    private readonly Dictionary<string, PlcSignalSnapshot> values = [];
    private long version;
    public bool FailAfterNextWrite;
    public List<PlcWrite> Writes { get; } = [];
    public event Action<PlcSignalChanged>? SignalChanged { add { } remove { } }
    public long Version => version;
    public void Initialize(string station, string dumper)
    {
        foreach (var name in TaskHandshakeSignals.Pickup) Set(station, name, name is "BoxPresent" or "CommunicationOnline");
        foreach (var name in TaskHandshakeSignals.Dumper) if (!values.ContainsKey(dumper + "/" + name)) Set(dumper, name, false);
    }
    public void Set(string station, string name, bool value) => values[station + "/" + name] = new(station, name, name,
        "DB:" + station + ":" + name, "BOOL", "logical", "configured", true, value, SignalQuality.Good,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ++version);
    public void OverrideAddress(string station, string name, string address) => values[station + "/" + name] =
        values[station + "/" + name] with { Address = address, UpdatedAt = DateTimeOffset.UtcNow, Version = ++version };
    public void Refresh() { foreach (var key in values.Keys.ToArray()) values[key] = values[key] with { UpdatedAt = DateTimeOffset.UtcNow, Version = ++version }; }
    public bool Get(string station, string name) => values[station + "/" + name].Value == true;
    public IReadOnlyList<PlcSignalSnapshot> GetAll() => values.Values.ToArray();
    public IReadOnlyList<PlcSignalSnapshot> GetStation(string station) => values.Values.Where(v => v.StationId == station).ToArray();
    public bool TryGet(string station, string name, out PlcSignalSnapshot snapshot) => values.TryGetValue(station + "/" + name, out snapshot!);
    public Task WriteSignalAsync(string station, string name, bool value, CancellationToken ct)
    {
        Writes.Add(new(station, name, value)); Set(station, name, value);
        if (FailAfterNextWrite) { FailAfterNextWrite = false; throw new IOException("isolated lost PLC write response"); }
        return Task.CompletedTask;
    }
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<bool?> ReadSignalAsync(string station, string name, CancellationToken ct) => Task.FromResult<bool?>(Get(station, name));
    public Task<StationSignal?> ReadStationAsync(string station, CancellationToken ct) => throw new NotSupportedException();
}

sealed class VehicleHarness : IAgvStateCache
{
    private readonly Dictionary<string, AgvRobotSnapshot> values = [];
    public void Set(string vehicle, double x, double y, string? station, DateTimeOffset? observedAt = null)
    {
        var time = observedAt ?? DateTimeOffset.UtcNow;
        values[vehicle] = new(vehicle, vehicle, "127.0.0.1", 0, ConnectionState.Online, SignalQuality.Good,
            x, y, 0, 0.99, station, null, 100, false, false, "Running", null, null, null, [], [], time, time, 0, null, null, Version + 1);
    }
    public event Action<AgvRobotStateChanged>? StateChanged { add { } remove { } }
    public long Version => values.Count == 0 ? 0 : values.Values.Max(v => v.Version);
    public IReadOnlyList<AgvRobotSnapshot> GetAll() => values.Values.ToArray();
    public bool TryGet(string id, out AgvRobotSnapshot result) => values.TryGetValue(id, out result!);
}

sealed class GatewayHarness : IAgvGateway
{
    public Dictionary<string, (string Task, string Vehicle)> Tasks { get; } = [];
    public AgvTransportTaskState State = AgvTransportTaskState.GoingToDestination;
    public int Queries;
    public int Creates;
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string vendorTaskId, CancellationToken ct)
    {
        Queries++;
        return Task.FromResult<AgvTransportTaskStatus?>(Tasks.TryGetValue(vendorTaskId, out var task)
            ? new(vendorTaskId, vendorTaskId, State, task.Vehicle, DateTimeOffset.UtcNow) : null);
    }
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => Task.FromResult(new DeviceConnectionStatus("AGV", "isolated fleet", ConnectionState.Online, DateTimeOffset.UtcNow));
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AgvVehicleStatus>>(Enumerable.Range(1, 3)
            .Select(i => new AgvVehicleStatus("AGV-" + i, ConnectionState.Online, "Idle", 100, null, DateTimeOffset.UtcNow)).ToArray());
    public Task<string> CreateTransportTaskAsync(string businessTaskId, string? preferredVehicleId, IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken ct)
    {
        Creates++;
        Tasks[businessTaskId] = (businessTaskId, preferredVehicleId!);
        return Task.FromResult(businessTaskId);
    }
    public Task CancelTaskAsync(string vendorTaskId, CancellationToken ct) => throw new NotSupportedException();
}

sealed class PreflightHarness : IRobokitTaskMapPreflight
{
    public bool Accepted = true;
    public int Calls;
    public Action? BeforeCheck;
    public Task<RobokitTaskMapCheck> CheckCurrentMapAsync(string vehicleId, string mapId, string versionId, CancellationToken ct)
    {
        Calls++;
        BeforeCheck?.Invoke();
        return Task.FromResult(new RobokitTaskMapCheck(Accepted && mapId == "MAP" && versionId == "V1", "isolated current-map proof"));
    }
    public Task<RobokitTaskMapCheck> CheckAsync(string vehicleId, string currentSite, string pickup, string waiting, string dumping, string mapId, string versionId, CancellationToken ct) => CheckCurrentMapAsync(vehicleId, mapId, versionId, ct);
}
