using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed class RecoveryBarrier : IRecoveryBarrier
{
    private int _open;
    public bool IsOpen => Volatile.Read(ref _open) == 1;
    internal void Set(bool open) => Volatile.Write(ref _open, open ? 1 : 0);
}

// This first stage deliberately reconciles the whole site before opening any writer.
// Recovery never calls CreateTransportTask, writes PLC outputs, or releases traffic leases.
public sealed partial class ExecutionRecoveryService(ISqlSugarClient database, RecoveryBarrier barrier,
    IMapExecutionGate gate, IAgvGateway gateway, IAgvStateCache vehicles, IPlcSignalCache signals,
    MapSwitchSafetySettings settings, IActiveMapRuntime map, IExecutionIdentityCache? identities = null, RecoveryIdentityOptions? identityOptions = null,
    IChargingRecoveryEvidence? chargingRecovery = null, ITrafficResourceManager? traffic = null) : IExecutionRecoveryService
{
    private string _sessionId = "";
    private DateTimeOffset _started;
    private const string SiteTask = "__SITE__";

    public async Task InitializeAsync(CancellationToken ct)
    {
        using var lease = await gate.EnterControlAsync(ct);
        await InitializeCoreAsync(ct);
    }
    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        barrier.Set(false);
        _started = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow();
        _sessionId = Guid.NewGuid().ToString("N");
        // Before the boot snapshot: a Ready step holding leases with no command is an abandoned
        // acquisition from a crash. Nothing else would ever release it (R7).
        if (traffic is not null) await traffic.RollbackAbandonedReadyStepLeasesAsync(ct);
        var active = await database.Queryable<DispatchTaskEntity>()
            .Where(t => t.Stage != (int)DispatchTaskStage.Completed && t.Stage != (int)DispatchTaskStage.Cancelled)
            .ToListAsync(ct);
        var commandOwners = await database.Queryable<AgvCommandOutboxEntity>()
            .Where(c => c.Status != (int)AgvCommandStatus.Sent && c.Status != (int)AgvCommandStatus.Cancelled)
            .Select(c => c.TaskId).ToListAsync(ct);
        var bootLeases = await database.Queryable<TrafficResourceLeaseEntity>().ToListAsync(ct);
        var leaseOwners = bootLeases.Select(l => l.TaskId).ToList();
        var owners = active.Select(t => t.TaskId).Concat(commandOwners).Concat(leaseOwners).Distinct().ToList();
        var chargingSnapshots = new List<ChargingBootOwnership>();
        if (chargingRecovery is not null)
            foreach (var owner in owners)
                if (await chargingRecovery.ReadRecoverySessionAsync(owner, ct) is { } charging)
                    chargingSnapshots.Add(new(charging, bootLeases.Where(l => l.TaskId == owner).ToArray()));
        if (settings.RequiredVehicleIds.Count > 0 || settings.CheckPlcSignals) owners.Add(SiteTask);
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            await database.Insertable(new ExecutionRecoverySessionEntity { SessionId = _sessionId, StartedAt = _started.LocalDateTime }).ExecuteCommandAsync(ct);
            foreach (var owner in owners.Distinct())
            {
                await database.Insertable(new ExecutionRecoveryTicketEntity
                {
                    TicketId = Guid.NewGuid().ToString("N"), SessionId = _sessionId, TaskId = owner,
                    Reason = "启动恢复：等待现场核对；保留命令、任务和路权。", Revision = 1
                }).ExecuteCommandAsync(ct);
            }
            foreach (var charging in chargingSnapshots)
                await AuditAsync(charging.Session.SessionId, "", "service", "ChargingBootOwnership",
                    "启动保留充电会话与原租约归属；须由充电恢复入口核对停止并实际离位后关闭。",
                    JsonSerializer.Serialize(charging), ct);
            await AuditAsync("", "", "service", "BootHold", "每次启动重新核对，上一启动的人工确认不作为本次放行依据。",
                JsonSerializer.Serialize(new { active, Commands = commandOwners, Leases = leaseOwners, MapId = map.Current?.MapId, MapVersion = map.Current?.VersionId }), ct);
            if (owners.Count == 0)
                await MarkReleasedAsync("service", "无未完成任务、残留命令、占用和已配置现场设备。", ct);
        });
        if (!transaction.IsSuccess) throw new InvalidOperationException("启动恢复快照保存失败，执行保持关闭。", transaction.ErrorException);
        if (owners.Count == 0) barrier.Set(true);
    }

    public async Task<RecoveryResult> BeginReviewAsync(TrafficUserContext user, TaskRecoveryReviewRequest request, CancellationToken ct)
    {
        if (!user.Has(TrafficPermission.RecoverExecution)) throw new UnauthorizedAccessException("需要恢复处置权限。");
        if (string.IsNullOrWhiteSpace(request.TaskId) || request.TaskId.Length > 64 || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 1 or > 1000)
            return new(false, "请填写任务及非空、最多1000字的现场核对依据。");
        using var lease = await gate.EnterControlAsync(ct);
        var task = await database.Queryable<DispatchTaskEntity>().Where(t => t.TaskId == request.TaskId).FirstAsync(ct);
        if (task == null || task.Stage is 90 or 110 or 120) return new(false, "任务已结束或仍在等待车辆取消确认，当前不能进入恢复处置。");
        if (!barrier.IsOpen && await database.Queryable<ExecutionRecoverySessionEntity>().Where(s => s.SessionId == _sessionId).AnyAsync(ct)) return new(true, "已有恢复核对批次，请在现场恢复页面继续；本次未重置已有确认。");
        await InitializeCoreAsync(ct);
        await AuditAsync(task.TaskId, "", user.WindowsIdentity, "RuntimeReview", request.Reason.Trim(), "全场后续派单、运输发送和PLC推进保持冻结；车辆已接受的动作须另行取消。", ct);
        return new(true, "已建立全场现场核对批次并冻结后续推进；请进入现场恢复页面核对全部条目。");
    }

    public async Task InspectAsync(CancellationToken ct)
    {
        if (barrier.IsOpen || _sessionId.Length == 0) return;
        using var lease = await gate.EnterControlAsync(ct);
        foreach (var ticket in await TicketsAsync(ct))
        {
            var before = ticket.Reason;
            var fingerprint = ticket.Fingerprint;
            var observation = await ObserveAsync(
                ticket, ct, allowOfflineVehicle: ticket.OfflineVehicleConfirmed);
            if (ticket.Verified && (fingerprint != observation.Fingerprint || observation.HardBlock != null || observation.SegmentContinuationBlock != null ||
                (ticket.MaterialReference != null && (ticket.PhysicalFingerprint != PhysicalFingerprint(observation) ||
                    ticket.VerifiedAt == null || AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime - ticket.VerifiedAt > TimeSpan.FromMinutes(5)))))
            {
                ticket.Verified = false; ticket.Revision++;
                await AuditAsync(ticket.TaskId, ticket.StepId, "service", "VerificationRevoked", "确认后物理状态、执行节点或证据有效期变化，恢复重新等待核对。", observation.Evidence, ct);
            }
            ticket.Fingerprint = observation.Fingerprint;
            ticket.StepId = observation.Step?.StepId ?? "";
            ticket.Evidence = observation.Evidence;
            if (!ticket.Verified)
            {
                ticket.Reason = observation.AutoSafe && observation.Identity.State != "ChargingClosed" ? "已核对：原任务已结束或无需执行，且不存在发送记录和路权占用。" : observation.Reason;
                if (observation.AutoSafe)
                {
                    ticket.Verified = true;
                    ticket.Revision++;
                    await AuditAsync(ticket.TaskId, ticket.StepId, "service", observation.Identity.State == "ChargingClosed" ? "VerifiedChargingClosed" : "VerifiedUnstarted", ticket.Reason, ticket.Evidence, ct);
                }
                else if (before != ticket.Reason || fingerprint != ticket.Fingerprint) ticket.Revision++;
                if (!observation.AutoSafe && (before != ticket.Reason || fingerprint != ticket.Fingerprint))
                    await AuditAsync(ticket.TaskId, ticket.StepId, "service", "RecoveryWaiting", ticket.Reason, ticket.Evidence, ct);
            }
            await database.Updateable(ticket).ExecuteCommandAsync(ct);
        }
        // Only a site with exclusively unstarted requests may resume without a human.
        var tickets = await TicketsAsync(ct);
        if (tickets.Count > 0 && tickets.All(t => t.Verified && t.MaterialReference == null))
        {
            var result = await database.Ado.UseTranAsync(() => MarkReleasedAsync("service", "遗留请求均未开始或已按专项现场核对关闭，无待续接动作及资源占用。", ct));
            if (!result.IsSuccess) throw result.ErrorException;
            barrier.Set(true);
        }
    }

    public async Task<RecoveryStatus> GetAsync(CancellationToken ct)
    {
        using var lease = await gate.EnterControlAsync(ct);
        var items = new List<RecoveryItem>();
        foreach (var ticket in await TicketsAsync(ct))
        {
            var task = await database.Queryable<DispatchTaskEntity>().Where(t => t.TaskId == ticket.TaskId).FirstAsync(ct);
            var step = await CurrentStepAsync(ticket.TaskId, ct);
            var charging = task is null && chargingRecovery is not null ? await chargingRecovery.ReadRecoverySessionAsync(ticket.TaskId, ct) : null;
            var vehicleId = step?.VehicleId ?? task?.VehicleId ?? charging?.VehicleId;
            vehicles.TryGet(vehicleId ?? "", out var vehicle);
            var leases = await database.Queryable<TrafficResourceLeaseEntity>().Where(l => l.TaskId == ticket.TaskId).ToListAsync(ct);
            var segmentReleases=await database.Queryable<TrafficSegmentCommitLeaseEntity>().Where(a=>a.TaskId==ticket.TaskId).ToListAsync(ct);
            var dumper = task == null ? null : await database.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == task.TaskId && s.StepType == (int)DispatchTaskStepType.WaitForDumperReady).FirstAsync(ct);
            var plcRows = task == null ? signals.GetAll() : signals.GetAll().Where(s => s.StationId == task.StationId || s.StationId == dumper?.SourceSite).ToArray();
            var identity = StoredIdentity(ticket.Evidence);
            var pendingWrite = await database.Queryable<TaskHandshakeEntity>().Where(h => h.TaskId == ticket.TaskId && h.StepId == ticket.StepId && h.PendingSignal != null).AnyAsync(ct);
            items.Add(new(ticket.TicketId, ticket.TaskId, ticket.StepId, task?.StationId ?? "全场核对",
                vehicleId, charging is not null ? "充电会话核对" : step == null ? "无执行步骤" : ((DispatchTaskStepType)step.StepType).ToString(),
                ticket.Verified ? "已核对" : "等待核对", ticket.Reason, ticket.Evidence, ticket.Verified, ticket.MaterialReference, ticket.Revision) { TaskStage = task?.Stage, RequiresPendingWriteConfirmation = pendingWrite, IdentitySummary = identity.Summary, IdentityState = identity.State,
                IdentityBlocksRecovery = identity.BlockReason != null,
                MachineContainerSummary = identity.Vehicle == null ? "设备容器身份未接入。" : identity.Vehicle.LoadState == ContainerLoadState.Loaded ?
                    "设备报告承载容器：" + identity.Vehicle.ContainerId + "（" + identity.Vehicle.Quality + "）" : identity.Vehicle.LoadState == ContainerLoadState.Empty ? "设备报告车辆空载（" + identity.Vehicle.Quality + "）。" : "设备容器状态未知。",
                VehicleSummary = vehicle == null ? "未取得关联车辆状态；未分配车辆的请求仍须现场核对箱体。" :
                    $"车辆 {vehicle.VehicleId} · 当前站点 {vehicle.CurrentStation ?? "未知"} · 状态 {vehicle.Connection}/{vehicle.Quality} · 速度 X/Y/角度 {vehicle.VelocityX}/{vehicle.VelocityY}/{vehicle.AngularVelocity}",
                PlcSummary = plcRows.Count == 0 ? "未取得取货工位 / 倒料机 PLC 状态。" : string.Join("；", plcRows.Where(s => s.Enabled).Select(s =>
                    $"{s.StationId}/{s.VariableName}={s.Value?.ToString() ?? "未知"}（{s.Quality}）")),
                ResourceSummary = (segmentReleases.Count==0?"":"已按证据释放："+string.Join("、",segmentReleases.Select(a=>$"{a.ResourceId}（原步骤 {a.StepId}）"))+"；当前剩余：") + (leases.Count == 0 ? "原任务无占用路权；恢复操作不会自动申请或释放路权。" : string.Join("；", leases.Select(l =>
                    $"{l.ResourceId} · {l.VehicleId} · 原步骤 {l.OwnerStepId} · {(l.ExpiresAt < AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime ? "心跳已过期，占用保留" : "占用保留")}"))) });
        }
        var audits = await database.Queryable<ExecutionEvidenceEntity>().Where(a => a.SessionId != "")
            .OrderByDescending(a => a.OccurredAt).Take(200).ToListAsync(ct);
        return new(_sessionId, barrier.IsOpen, barrier.IsOpen ? "启动恢复已放行；仍受地图切换控制。" : "恢复屏障关闭：派单、发送、PLC推进全部等待现场核对。", items,
            audits.Select(a => new RecoveryAudit(a.Id, a.SessionId, a.TaskId, a.StepId, a.Operator, a.Kind, a.Reason, a.Evidence, a.OccurredAt)).ToArray());
    }

    public async Task<RecoveryResult> DecideAsync(TrafficUserContext user, RecoveryDecisionRequest request, CancellationToken ct)
    {
        if (!user.Has(TrafficPermission.RecoverExecution)) throw new UnauthorizedAccessException("需要恢复处置权限或管理员权限。");
        using var lease = await gate.EnterControlAsync(ct);
        if (barrier.IsOpen || request.SessionId != _sessionId) return await RejectAsync(user, request.TicketId, "启动批次已变化或已放行，请刷新。", ct);
        var ticket = (await TicketsAsync(ct)).FirstOrDefault(t => t.TicketId == request.TicketId);
        if (ticket == null || ticket.Verified || ticket.Revision != request.Revision) return await RejectAsync(user, request.TicketId, "恢复条目已变化，请刷新后重新核对。", ct);
        if (!Enum.IsDefined(request.Decision) || !request.VehicleAndLocationConfirmed || !request.LoadAndOwnershipConfirmed || !request.PlcCycleConfirmed ||
            string.IsNullOrWhiteSpace(request.MaterialReference) || request.MaterialReference.Length > 128 || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 4000)
            return await RejectAsync(user, request.TicketId, "须确认车辆定位、载荷归属和PLC周期，填写物料/容器编号（无载荷填明确的空载核对说明）及非空现场依据。", ct);
        var observed = await ObserveAsync(
            ticket,
            ct,
            allowOfflineVehicle: request.VehicleOfflineConfirmed);
        if(request.Decision==RecoveryDecision.ContinueOriginal&&observed.SegmentContinuationBlock is not null)
            return await RejectAsync(user,request.TicketId,observed.SegmentContinuationBlock,ct);
        if (observed.Fingerprint != ticket.Fingerprint) return await RejectAsync(user, request.TicketId, "数据库执行节点已变化，请刷新。", ct);
        if (observed.HardBlock != null) return await RejectAsync(user, request.TicketId, observed.HardBlock, ct);
        if (observed.Identity.Vehicle is { LoadState: ContainerLoadState.Loaded } load && request.MaterialReference.Trim() != load.ContainerId)
            return await RejectAsync(user, request.TicketId, "设备返回承载容器 " + load.ContainerId + "，请填写同一容器编号，不能用其他编号或说明替代。", ct);
        var task = observed.Task;
        var step = observed.Step;
        var completedIntermediateWindow = task is not null && step is not null &&
            CompletedIntermediateWindowAtSafePoint(step, observed.Command, observed.Vendor, observed.Vehicle);
        var obstacleRecoveryPending = task is not null && step is not null &&
            await ObstacleRecoveryAwaitingReplacementAsync(step, observed.Vendor, ct);
        if (request.Decision == RecoveryDecision.CloseCancelled)
            return await CloseCancelledAsync(user, request, ticket, observed, ct);
        var recoveringFault = task?.Stage == (int)DispatchTaskStage.Faulted && step?.Status == (int)DispatchTaskStepStatus.Failed &&
            step.VendorTaskId != null && request.Decision == RecoveryDecision.RecordPhysicalCompletion;
        if (recoveringFault) step!.Status = (int)DispatchTaskStepStatus.Executing;
        var completing = request.Decision == RecoveryDecision.RecordPhysicalCompletion;
        if(task!=null&&step!=null)
        {
            var handshakeBlock=await HandshakeReviewBlockAsync(task.TaskId,step.StepId,request,ct);
            if(handshakeBlock!=null)return await RejectAsync(user,ticket.TicketId,handshakeBlock,ct);
        }
        if (ticket.TaskId == SiteTask && completing) return await RejectAsync(user, request.TicketId, "全场核对不能补记任务完成。", ct);
        if (ticket.TaskId != SiteTask && (task == null || step == null || (task.Stage is 90 or 110 or 120 or 130 || task.Stage == 100 && !recoveringFault)))
            return await RejectAsync(user, request.TicketId, "残留命令、故障或孤立占用需专项处置；此页面不会释放占用或重置任务。", ct);
        if (task != null && step != null)
        {
            if (completing)
            {
                var completionBlock = CompletionBlock(task, step, observed.Vehicle, observed.Vendor);
                if (completionBlock != null) return await RejectAsync(user, request.TicketId, completionBlock, ct);
            }
            else if (step.Status == (int)DispatchTaskStepStatus.Executing)
            {
                if (!VendorMatches(step, task, observed.Vendor) ||
                    (!Running(observed.Vendor!.State) && !completedIntermediateWindow &&
                     !obstacleRecoveryPending))
                    return await RejectAsync(user, request.TicketId, "原运输任务未获车辆确认，不能继续。已完成运输请使用到站补记；历史缺失保持等待。", ct);
            }
            else if (observed.Command != null)
            {
                if (observed.Command.AttemptCount > 0 || observed.Command.Status != (int)AgvCommandStatus.Pending)
                {
                    if (!VendorMatches(step, task, observed.Vendor) || (!Running(observed.Vendor!.State) && observed.Vendor.State != AgvTransportTaskState.Completed))
                        return await RejectAsync(user, request.TicketId, "发送结果不明且车辆原任务记录不可核对，禁止重发。不能把历史404当成未执行。", ct);
                }
                else if (!MapRouteSiteMapper.IsSameNode(map.Current?.Definition,
                             observed.Vehicle?.CurrentStation, step.SourceSite))
                    return await RejectAsync(user, request.TicketId, "未发送命令的车辆已离开原起点，需保留等待并重新核对路线。", ct);
            }
            else if (step.Status != (int)DispatchTaskStepStatus.Ready)
                return await RejectAsync(user, request.TicketId, "步骤状态不支持恢复，保持原记录。", ct);
            if (!completing && observed.Command == null && step.Status == (int)DispatchTaskStepStatus.Ready && step.StepType != (int)DispatchTaskStepType.GoToPickup)
            {
                var previous = await database.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == task.TaskId && s.Sequence < step.Sequence && s.DestinationSite != null && s.Status == 50 &&
                    (s.StepType == 10 || s.StepType == 30 || s.StepType == 50 || s.StepType == 70))
                    .OrderByDescending(s => s.Sequence).FirstAsync(ct);
                if (previous?.DestinationSite != null &&
                    !MapRouteSiteMapper.IsSameNode(map.Current?.Definition,
                        observed.Vehicle?.CurrentStation, previous.DestinationSite))
                    return await RejectAsync(user, request.TicketId, "车辆已离开原流程停靠站，不能继续PLC节点或下一段运输。", ct);
                var plcBlock = await PlcContinuationBlockAsync(task, step, ct);
                if (plcBlock != null) return await RejectAsync(user, request.TicketId, plcBlock, ct);
            }
            if (!completing && step.Status != (int)DispatchTaskStepStatus.Executing &&
                !(observed.Command?.AttemptCount > 0 && Running(observed.Vendor!.State)))
            {
                var stopped = VehicleBlock(observed.Vehicle, true);
                if (stopped != null && task.VehicleId != null) return await RejectAsync(user, request.TicketId, stopped, ct);
            }
        }
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            if(task!=null&&step!=null&&(request.Decision==RecoveryDecision.ContinueOriginal||
                request.Decision==RecoveryDecision.RecordPhysicalCompletion&&step.StepType==(int)DispatchTaskStepType.ReturnEmptyBox))
                await ResolveHandshakeWriteAsync(task.TaskId,step.StepId,request,user.WindowsIdentity,ct);
            if (!recoveringFault && task != null && step != null && observed.Command is { } command && command.AttemptCount > 0)
                await BindAcceptedCommandAsync(task, step, command, observed.Vendor!, ct);
            if (completing && task != null && step != null)
            {
                await CompleteStepAsync(task, step, ct);
                ticket.Verified = false;
                ticket.StepId = (await CurrentStepAsync(ticket.TaskId, ct))?.StepId ?? "";
                ticket.Reason = "已补记原步骤；下一节点仍需核对，屏障保持关闭。原路权和物料归属保留。";
            }
            else
            {
                ticket.Verified = observed.Vendor?.State != AgvTransportTaskState.Completed ||
                    completedIntermediateWindow;
                ticket.Reason = obstacleRecoveryPending
                    ? "原导航已由障碍恢复取消且厂家任务已终态；放行后由原任务重新核验并发送替代路线，不重发原导航。"
                    : completedIntermediateWindow
                    ? "当前滚动窗口已到安全点并停稳；恢复放行后由原任务申请下一窗口，不重发当前命令。"
                    : ticket.Verified
                        ? "人工核对：继续原节点，原任务/步骤/命令编号保留。"
                        : "原命令已确认接受且完成，请刷新后补记已到站；禁止重发。";
            }
            ticket.MaterialReference = request.MaterialReference.Trim();
            ticket.VerifiedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
            ticket.PhysicalFingerprint = PhysicalFingerprint(observed with { Step = step });
            ticket.Revision++;
            ticket.Evidence = observed.Evidence;
            ticket.Fingerprint = await FingerprintAsync(ticket.TaskId, ct);
            await database.Updateable(ticket).ExecuteCommandAsync(ct);
            await AuditAsync(ticket.TaskId, step?.StepId ?? "", user.WindowsIdentity, request.Decision.ToString(), request.Reason.Trim(),
                JsonSerializer.Serialize(new
                {
                    request.MaterialReference,
                    request.VehicleAndLocationConfirmed,
                    request.LoadAndOwnershipConfirmed,
                    request.PlcCycleConfirmed,
                    request.VehicleOfflineConfirmed,
                    Observation = observed.Evidence
                }), ct);
        });
        if (!transaction.IsSuccess) throw new InvalidOperationException("恢复决定未提交，执行继续保持关闭。", transaction.ErrorException);
        return new(true, ticket.Reason);
    }

    public async Task<RecoveryResult> ReleaseAsync(TrafficUserContext user, RecoveryReleaseRequest request, CancellationToken ct)
    {
        if (!user.Has(TrafficPermission.RecoverExecution)) throw new UnauthorizedAccessException("需要恢复处置权限或管理员权限。");
        using var lease = await gate.EnterControlAsync(ct);
        if (request.SessionId != _sessionId || barrier.IsOpen) return new(false, "批次变化或已放行，请刷新。");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 4000) return new(false, "请填写非空的现场放行依据。");
        foreach (var ticket in await TicketsAsync(ct))
        {
            if (!ticket.Verified) return new(false, $"{ticket.TaskId}尚未核对。");
            var observed = await ObserveAsync(
                ticket, ct, allowOfflineVehicle: ticket.OfflineVehicleConfirmed);
            if(observed.SegmentContinuationBlock is not null)return new(false,observed.SegmentContinuationBlock);
            var closedOffline =
                ticket.OfflineVehicleConfirmed &&
                observed.Task?.Stage == (int)DispatchTaskStage.Cancelled &&
                observed.Step == null;
            if (observed.HardBlock != null ||
                (!closedOffline && observed.Fingerprint != ticket.Fingerprint) ||
                (!closedOffline && ticket.MaterialReference != null &&
                 (ticket.PhysicalFingerprint != PhysicalFingerprint(observed) ||
                  ticket.VerifiedAt == null ||
                  AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime - ticket.VerifiedAt > TimeSpan.FromMinutes(5))))
                return new(false, observed.HardBlock ?? $"{ticket.TaskId}执行节点已变化，需重新核对。");
            if (observed.Step?.Status == (int)DispatchTaskStepStatus.Executing &&
                (!VendorMatches(observed.Step, observed.Task!, observed.Vendor) ||
                 (!Running(observed.Vendor!.State) &&
                  !CompletedIntermediateWindowAtSafePoint(
                      observed.Step, observed.Command, observed.Vendor, observed.Vehicle) &&
                  !await ObstacleRecoveryAwaitingReplacementAsync(
                      observed.Step, observed.Vendor, ct))))
                return new(false, "放行前原运输状态发生变化，请重新核对并补记到站。");
        }
        var result = await database.Ado.UseTranAsync(() => MarkReleasedAsync(user.WindowsIdentity, request.Reason.Trim(), ct));
        if (!result.IsSuccess) throw new InvalidOperationException("放行记录保存失败，屏障保持关闭。", result.ErrorException);
        barrier.Set(true);
        return new(true, "恢复已放行；地图切换屏障继续独立生效。没有释放任何交通占用。");
    }

    private async Task<RecoveryResult> CloseCancelledAsync(TrafficUserContext user, RecoveryDecisionRequest request,
        ExecutionRecoveryTicketEntity ticket, Observation observed, CancellationToken ct)
    {
        var task = observed.Task;
        var legacyTerminalCancelled = task?.Stage == (int)DispatchTaskStage.Cancelled &&
            task.CancelRequestId == null && task.CancelConfirmedAt == null;
        var legacyUnsentReviewed = task?.Stage == (int)DispatchTaskStage.CancellationReview &&
            task.ErrorMessage?.StartsWith("原运输命令未发送", StringComparison.Ordinal) == true;
        var stoppedOrUnassigned = observed.Vehicle is { Blocked: false, EmergencyStopped: false } ||
            task is { VehicleId: null, CancelVehicleId: null };
        var vehicleIsOffline =
            observed.Vehicle is null ||
            observed.Vehicle.Connection != ConnectionState.Online ||
            observed.Vehicle.Quality != SignalQuality.Good;
        var offlineCloseConfirmed =
            request.VehicleOfflineConfirmed &&
            vehicleIsOffline &&
            task is { Stage: (int)DispatchTaskStage.CancellationReview, CancelConfirmedAt: not null };
        var fieldConfirmedOfflineClose = task?.Stage == (int)DispatchTaskStage.CancellationReview &&
            (task.CancelRequestId != null || legacyUnsentReviewed) && request.VehicleAndLocationConfirmed &&
            request.LoadAndOwnershipConfirmed && stoppedOrUnassigned;
        if (task == null || (task.Stage != (int)DispatchTaskStage.CancellationReview && task.Stage != (int)DispatchTaskStage.Cancelled) ||
            !legacyTerminalCancelled && !fieldConfirmedOfflineClose &&
            (task.CancelRequestId == null || task.CancelConfirmedAt == null))
            return await RejectAsync(user, ticket.TicketId, "须先完成车辆取消/结束确认，才能关闭取消任务。", ct);
        if (await database.Queryable<TrafficResourceLeaseEntity>().Where(l => l.TaskId == task.TaskId).AnyAsync(ct))
            return await RejectAsync(user, ticket.TicketId, "原路权仍占用，请通过交通管制人工释放流程核查并处置；本操作不会自动释放。", ct);
        if (legacyTerminalCancelled)
        {
            var vehicleBlock = VehicleBlock(observed.Vehicle, true);
            if (vehicleBlock != null) return await RejectAsync(user, ticket.TicketId, vehicleBlock, ct);
            if (observed.Vehicle!.Tasks.Any(t => t.Status is 1 or 2 or 3))
                return await RejectAsync(user, ticket.TicketId, "车辆仍有活动任务，不能关闭历史取消记录。", ct);
        }
        else if (task.CancelVehicleId != null)
        {
            if (request.VehicleOfflineConfirmed)
            {
                if (!vehicleIsOffline)
                    return await RejectAsync(user, ticket.TicketId,
                        "车辆仍在线或状态新鲜，不能按离线方式关闭；请先确认车辆关机或通信确实断开。", ct);
            }
            else
            {
                var vehicleBlock = VehicleBlock(observed.Vehicle, true);
                if (vehicleBlock != null) return await RejectAsync(user, ticket.TicketId, vehicleBlock, ct);
                if (observed.Vehicle!.Tasks.Any(t => t.Status is 1 or 2 or 3))
                    return await RejectAsync(user, ticket.TicketId, "车辆仍有活动任务，不能关闭取消。", ct);
            }
        }
        var confirmedNoVendor = IsConfirmedPreSendRejection(task, observed.Step, observed.Command);
        if (!offlineCloseConfirmed && !confirmedNoVendor && task.CancelVendorId != null &&
            (observed.Vendor == null || observed.Vendor.VendorTaskId != task.CancelVendorId ||
             observed.Vendor.BusinessTaskId != task.CancelVendorId || observed.Vendor.VehicleId != task.CancelVehicleId ||
             observed.Vendor.UpdatedAt < _started || AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow() - observed.Vendor.UpdatedAt > settings.StateMaxAge ||
             observed.Vendor.UpdatedAt > AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().AddSeconds(1) ||
             observed.Vendor.State is not (AgvTransportTaskState.Cancelled or AgvTransportTaskState.Completed or AgvTransportTaskState.Failed)))
            return await RejectAsync(user, ticket.TicketId, "缺少新鲜、同编号和同车辆的原任务结束记录。", ct);
        if (!settings.CheckPlcSignals) return await RejectAsync(user, ticket.TicketId, "关闭取消必须启用PLC采集并核对原握手清理。", ct);
        var steps = await database.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == task.TaskId).ToListAsync(ct);
        var dumper = steps.FirstOrDefault(s => s.StepType == (int)DispatchTaskStepType.WaitForDumperReady)?.SourceSite;
        var handedOver=await ExitHandoverCompleteAsync(task.TaskId,ct);
        var stage28=await database.Queryable<TaskHandshakeEntity>().Where(h=>h.TaskId==task.TaskId&&h.ContractVersion==HandshakeExecutionOptions.Version).AnyAsync(ct);
        var ownClaims=await database.Queryable<TaskEquipmentClaimEntity>().Where(e=>e.TaskId==task.TaskId).ToListAsync(ct);
        if(handedOver&&ownClaims.Count>0)
            return await RejectAsync(user,ticket.TicketId,"已交接记录与本任务残留设备归属矛盾，保留记录并专项核对；不能再次释放或清共享位。",ct);
        foreach (var station in new[] { task.StationId, handedOver?null:dumper }.Where(s => s != null).Distinct())
        {
            var block = PlcBlock(station);
            if (block != null) return await RejectAsync(user, ticket.TicketId, block, ct);
        }
        bool Cleared(string station, string variable) => signals.TryGet(station, variable, out var signal) && signal.Enabled &&
            signal.Quality == SignalQuality.Good && signal.Value == false && signal.UpdatedAt >= _started &&
            signal.UpdatedAt <= AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().AddSeconds(1) && AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow() - signal.UpdatedAt <= settings.StateMaxAge;
        if (new[] { "FullBoxRequestAcknowledged", "FullBoxRemoved", "EmptyBoxTaskAcknowledged", "EmptyBoxPlaced" }.Any(v => !Cleared(task.StationId, v)) ||
            !handedOver && (dumper == null || !Cleared(dumper, "AgvAtWaitingPosition") || !Cleared(dumper, "AgvAtDumpingPosition") || stage28&&(!Cleared(dumper,"DumperReady") || !Cleared(dumper,"DumpingCompletedMayLeave"))))
            return await RejectAsync(user, ticket.TicketId, "取货工位或倒料机原握手尚未清理；需现场核对PLC周期，禁止自动复位。", ct);
        var result = await database.Ado.UseTranAsync(async () =>
        {
            var alreadyClosed = task.Stage == (int)DispatchTaskStage.Cancelled;
            if (!alreadyClosed)
            {
                task.Stage = (int)DispatchTaskStage.Cancelled; task.UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
                task.ErrorMessage = request.VehicleOfflineConfirmed
                    ? "取消已关闭：车辆已确认关机离线，安全位置、载荷、PLC握手和路权均完成现场核对。"
                    : "取消已关闭：车辆、载荷、PLC握手和路权均完成现场核对。";
                await database.Updateable(task).ExecuteCommandAsync(ct);
                await database.Updateable<DispatchTaskStepEntity>().SetColumns(s => new DispatchTaskStepEntity
                    { Status = (int)DispatchTaskStepStatus.Cancelled, CompletedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime, UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime })
                    .Where(s => s.TaskId == task.TaskId && s.Status != (int)DispatchTaskStepStatus.Completed).ExecuteCommandAsync(ct);
            }
            if (observed.Step != null) observed.Step.Status = (int)DispatchTaskStepStatus.Cancelled;
            var equipmentReleased=await database.Deleteable<TaskEquipmentClaimEntity>().Where(e=>e.TaskId==task.TaskId).ExecuteCommandAsync(ct);
            if(equipmentReleased>0)await database.Insertable(new TaskRuntimeEventEntity{TaskId=task.TaskId,StepId=ticket.StepId,Kind="EquipmentReleased",RecordedAtUtc=AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow().UtcDateTime,Reason="原恢复流程核对取消闭合后释放设备归属；操作人："+user.WindowsIdentity}).ExecuteCommandAsync(ct);
            ticket.Verified = true; ticket.MaterialReference = request.MaterialReference.Trim();
            ticket.OfflineVehicleConfirmed = request.VehicleOfflineConfirmed;
            ticket.VerifiedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
            ticket.Revision++; ticket.Fingerprint = await FingerprintAsync(task.TaskId, ct);
            ticket.PhysicalFingerprint = PhysicalFingerprint(observed);
            await database.Updateable(ticket).ExecuteCommandAsync(ct);
            await AuditAsync(task.TaskId, ticket.StepId, user.WindowsIdentity, "CloseCancelled", request.Reason.Trim(), observed.Evidence, ct);
        });
        if (!result.IsSuccess) throw new InvalidOperationException("取消关闭事务未提交，保留冻结。", result.ErrorException);
        return new(true, "取消任务已关闭；未发送PLC指令或释放路权。核对全部恢复条目后才能放行其他任务。");
    }
    private async Task<List<ExecutionRecoveryTicketEntity>> TicketsAsync(CancellationToken ct) =>
        await database.Queryable<ExecutionRecoveryTicketEntity>().Where(t => t.SessionId == _sessionId).ToListAsync(ct);
    private async Task<DispatchTaskStepEntity?> CurrentStepAsync(string taskId, CancellationToken ct) =>
        await database.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == taskId && s.Status != (int)DispatchTaskStepStatus.Completed)
            .OrderBy(s => s.Sequence).FirstAsync(ct);
    private async Task<string> FingerprintAsync(string taskId, CancellationToken ct)
    {
        var task = await database.Queryable<DispatchTaskEntity>().Where(t => t.TaskId == taskId).FirstAsync(ct);
        var steps = await database.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == taskId).OrderBy(s => s.Sequence).ToListAsync(ct);
        var commands = await database.Queryable<AgvCommandOutboxEntity>().Where(c => c.TaskId == taskId).OrderBy(c => c.CommandId).ToListAsync(ct);
        var leases = await database.Queryable<TrafficResourceLeaseEntity>().Where(l => l.TaskId == taskId).OrderBy(l => l.LeaseId).ToListAsync(ct);
        var handshakes=await database.Queryable<TaskHandshakeEntity>().Where(h=>h.TaskId==taskId).OrderBy(h=>h.StepId).ToListAsync(ct);
        var equipment=await database.Queryable<TaskEquipmentClaimEntity>().Where(h=>h.TaskId==taskId).OrderBy(h=>h.EquipmentId).ToListAsync(ct);
        var segmentReleases=await database.Queryable<TrafficSegmentCommitLeaseEntity>().Where(a=>a.TaskId==taskId).OrderBy(a=>a.AuditId).ToListAsync(ct);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { task, steps, commands, leases, segmentReleases, handshakes, equipment, Map = map.Current?.VersionId }))));
    }
    private sealed record Observation(DispatchTaskEntity? Task, DispatchTaskStepEntity? Step, AgvCommandOutboxEntity? Command,
        AgvRobotSnapshot? Vehicle, AgvTransportTaskStatus? Vendor, bool AutoSafe, string Reason, string? HardBlock, string Evidence, string Fingerprint, RecoveryIdentityVerification Identity,string? SegmentContinuationBlock=null);
    private string PhysicalFingerprint(Observation observation)
    {
        static double? RoundedFinite(double? value) => value is { } number && double.IsFinite(number) ? Math.Round(number, 2) : null;
        object Describe(AgvRobotSnapshot v, bool moving) => new { v.VehicleId, v.CurrentStation, v.EmergencyStopped, v.Blocked,
            X = moving ? (double?)null : RoundedFinite(v.X), Y = moving ? (double?)null : RoundedFinite(v.Y),
            Tasks = v.Tasks.OrderBy(t => t.TaskId).Select(t => new { t.TaskId, t.Status }).ToArray() };
        var vehicleData = observation.Task == null && observation.Step == null
            ? JsonSerializer.Serialize(vehicles.GetAll().Where(v => v.Quality != SignalQuality.Disabled).OrderBy(v => v.VehicleId)
                .Select(v => Describe(v, v.Tasks.Any(t => t.Status is 1 or 2 or 3))))
            : observation.Vehicle == null ? "no-vehicle" : JsonSerializer.Serialize(Describe(observation.Vehicle, observation.Step?.Status == (int)DispatchTaskStepStatus.Executing));
        var data = JsonSerializer.Serialize(new { Identity = IdentityFingerprint(observation), Vehicle = vehicleData, VendorState = observation.Vendor?.State,
            Plc = (observation.Task == null || observation.Task.CancelRequestId != null ? signals.GetAll() : signals.GetStation(PlcStation(observation.Task, observation.Step)))
                .Where(s => s.Enabled).OrderBy(s => s.StationId).ThenBy(s => s.VariableName).Select(s => new { s.StationId, s.VariableName, s.Value, s.Quality }) });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    }
    private async Task<Observation> ObserveAsync(
        ExecutionRecoveryTicketEntity ticket,
        CancellationToken ct,
        bool allowOfflineVehicle = false)
    {
        var task = await database.Queryable<DispatchTaskEntity>().Where(t => t.TaskId == ticket.TaskId).FirstAsync(ct);
        var step = await CurrentStepAsync(ticket.TaskId, ct);
        var commands = await database.Queryable<AgvCommandOutboxEntity>().Where(c => c.TaskId == ticket.TaskId).ToListAsync(ct);
        var command = commands.Where(c => c.StepId == step?.StepId)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(step?.VendorTaskId) &&
                c.VendorCommandId == step!.VendorTaskId)
            ?? commands.Where(c => c.StepId == step?.StepId).OrderByDescending(c => c.CreatedAt).FirstOrDefault();
        var leases = await database.Queryable<TrafficResourceLeaseEntity>().Where(l => l.TaskId == ticket.TaskId).ToListAsync(ct);
        if (task is null && chargingRecovery is not null && await chargingRecovery.ReadRecoverySessionAsync(ticket.TaskId, ct) is { } charging)
            return await ObserveChargingAsync(ticket, charging, commands, leases, ct);
        var segmentReleases=await database.Queryable<TrafficSegmentCommitLeaseEntity>().Where(a=>a.TaskId==ticket.TaskId).ToListAsync(ct);
        var segmentRequestIds=segmentReleases.Select(a=>a.RequestId).Distinct().ToArray();
        var segmentCommits=segmentRequestIds.Length==0?[]:await database.Queryable<TrafficSegmentCommitEntity>().Where(c=>segmentRequestIds.Contains(c.RequestId)).ToListAsync(ct);
        var taskSteps = await database.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == ticket.TaskId).Select(s => s.StepId).ToListAsync(ct);
        var vehicleId = step?.VehicleId ?? task?.VehicleId;
        AgvRobotSnapshot? vehicle = null;
        if (vehicleId != null && vehicles.TryGet(vehicleId, out var found)) vehicle = found;
        var closingCancelled = task?.Stage == (int)DispatchTaskStage.CancellationReview &&
            leases.Count == 0 &&
            (task.CancelRequestId != null ||
             task.ErrorMessage?.StartsWith("原运输命令未发送", StringComparison.Ordinal) == true);
        string? hard = null;
        AgvTransportTaskStatus? vendor = null;
        if (ticket.TaskId == SiteTask) hard = await SiteBlockAsync(ct, allowOfflineVehicle);
        else if (vehicleId != null &&
                 !(allowOfflineVehicle &&
                   task?.Stage is (int)DispatchTaskStage.CancellationReview or (int)DispatchTaskStage.Cancelled &&
                   task.CancelConfirmedAt != null &&
                   vehicle is { Connection: not ConnectionState.Online }))
            hard = VehicleBlock(vehicle, false);
        if (ticket.TaskId != SiteTask && task != null && settings.CheckPlcSignals && !closingCancelled)
            hard ??= PlcBlock(PlcStation(task, step));
        if (task?.CancelRequestId != null && settings.CheckPlcSignals && !closingCancelled &&
            !await ExitHandoverCompleteAsync(task.TaskId,ct))
        {
            var dumper = await database.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == task.TaskId && s.StepType == (int)DispatchTaskStepType.WaitForDumperReady).FirstAsync(ct);
            if (dumper?.SourceSite != null) hard ??= PlcBlock(dumper.SourceSite);
        }
        if (commands.Any(c => c.ExecutionMapVersionId != null && (map.Current?.VersionId != c.ExecutionMapVersionId || map.Current?.MapId != c.ExecutionMapId)))
            hard = "原命令绑定的运行地图与当前地图不一致，禁止继续或自动重规划。";
        if (leases.Any(l => !taskSteps.Contains(l.OwnerStepId) || l.VehicleId != vehicleId))
            hard = "路权占用与原步骤或原车辆归属不一致，禁止继续执行。";
        var id = task?.CancelVendorId ?? step?.VendorTaskId ?? command?.VendorCommandId ?? (command?.AttemptCount > 0 ? step?.StepId : null);
        var confirmedNoVendor = IsConfirmedPreSendRejection(task, step, command);
        var confirmedOfflineCancellation = allowOfflineVehicle && leases.Count == 0 &&
            task?.Stage is (int)DispatchTaskStage.CancellationReview or (int)DispatchTaskStage.Cancelled &&
            task.CancelRequestId != null && task.CancelConfirmedAt != null &&
            (vehicle is null || vehicle.Connection != ConnectionState.Online || vehicle.Quality != SignalQuality.Good);
        if (id != null && hard == null && !confirmedNoVendor && !confirmedOfflineCancellation)
        {
            try { vendor = await gateway.GetTransportTaskStatusAsync(id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { hard = "厂家任务查询失败：" + e.Message; }
        }
        // FleetTest standby navigation is a direct AGV transport task rather than a
        // DispatchTask. Once its custom lease has been reconciled and released, the
        // remaining BootHold ticket is verified as a closed no-resource intent. Other
        // orphan task IDs remain hard-blocked.
        var releasedFleetStandby=task==null&&commands.Count==0&&leases.Count==0&&
            ticket.TaskId.StartsWith("FLEET-STANDBY-",StringComparison.OrdinalIgnoreCase);
        var unstarted = releasedFleetStandby || task != null && task.Stage is 0 or 10 && task.VehicleId == null && commands.Count == 0 && leases.Count == 0 &&
                        step?.StepType == (int)DispatchTaskStepType.GoToPickup && (step.Status == (int)DispatchTaskStepStatus.Ready || step.Status == (int)DispatchTaskStepStatus.Pending) &&
                        !(await database.Queryable<ExecutionEvidenceEntity>().Where(e => e.TaskId == ticket.TaskId && (e.Kind == "PlcWriteIntent" || e.Kind == "AgvSendIntent")).AnyAsync(ct));
        var identity = await ObserveIdentityAsync(task, step, command, unstarted, ct);
        hard ??= identity.BlockReason;
        var segmentBlock=step is not null&&(step.Status is 20 or 30 or 40 or 60)&&segmentReleases.Any(a=>a.StepId==step.StepId)
            ?"原运输已有逐段释放记录，当前恢复流程尚未接入重启后的可信控制器周期和原命令进度核验，禁止直接续接；保留剩余路权，可在满足原有条件后核对取消或补记已到站。":null;
        var reason = hard ?? segmentBlock ?? (ticket.TaskId == SiteTask ? "设备状态可采集；全场条目须逐任务关联身份并现场核对空载/物料归属和PLC周期。" :
            task == null || task.Stage is 90 or 100 or 110 ? "故障任务或孤立命令/占用：禁止自动重派和自动释放。" :
            command?.AttemptCount > 0 && vendor == null ? "发送结果不明；车辆历史缺失不能证明未执行，保持等待。" :
            step?.Status == (int)DispatchTaskStepStatus.Executing ? "核对原运输编号、定位和载荷归属后继续；已到站则补记原步骤。" :
            "PLC动作及物料状态需要现场确认；布尔完成位不能证明本次动作已完成。");
        var evidence = JsonSerializer.Serialize(new { CapturedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow(), Boot = _started, task, step, commands, leases, vehicle, vendor,
            Plc = task == null || task.CancelRequestId != null ? signals.GetAll() : signals.GetStation(PlcStation(task, step)),
            Vehicles = ticket.TaskId == SiteTask ? vehicles.GetAll().Where(v => v.Quality != SignalQuality.Disabled).ToArray() : null,
            MapId = map.Current?.MapId, MapVersion = map.Current?.VersionId, Identity = identity, SegmentReleases=segmentReleases, SegmentCommitResults=segmentCommits, SegmentContinuationBlock=segmentBlock,
            LoadQuality = identity.Vehicle?.Quality.ToString() ?? "Unknown: identity adapter not connected", PlcCycleQuality = identity.State });
        var closedWithoutVendor = task is { Stage: (int)DispatchTaskStage.Cancelled, CancelConfirmedAt: not null } &&
                                  confirmedNoVendor;
        return new(task, step, command, vehicle, vendor, (unstarted || closedWithoutVendor) && hard == null,
            reason, hard, evidence, await FingerprintAsync(ticket.TaskId, ct), identity, segmentBlock);
    }
    private string? VehicleBlock(AgvRobotSnapshot? vehicle, bool stopped)
    {
        if (vehicle == null || vehicle.Connection != ConnectionState.Online || vehicle.Quality != SignalQuality.Good ||
            vehicle.LastSuccessfulPollAt == null || vehicle.LastSuccessfulPollAt < _started ||
            AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow() - vehicle.LastSuccessfulPollAt > settings.StateMaxAge || vehicle.LastSuccessfulPollAt > AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().AddSeconds(1))
            return "车辆状态缺失、离线或不是本次启动后的新鲜采集。";
        if (vehicle.EmergencyStopped != false || vehicle.Blocked != false || vehicle.Alarms.Any(a => a.Severity >= AgvAlarmSeverity.Error)) return "车辆急停、阻塞或报警状态未通过。";
        if (vehicle.X == null || vehicle.Y == null || !double.IsFinite(vehicle.X.Value) || !double.IsFinite(vehicle.Y.Value) ||
            vehicle.LocalizationConfidence == null || !double.IsFinite(vehicle.LocalizationConfidence.Value) || vehicle.LocalizationConfidence < settings.MinimumLocalizationConfidence)
            return "车辆定位证据不足。";
        if (map.Current is { } current && AgvMapPositionAnalyzer.Analyze(vehicle.X, vehicle.Y, vehicle.LocalizationConfidence,
            vehicle.LastSuccessfulPollAt, current.Definition, AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow(), settings.StateMaxAge,
            settings.MinimumLocalizationConfidence, settings.MaximumRouteDeviationMeters).State != AgvMapPositionState.Normal)
            return "车辆实际定位不能匹配当前运行地图。";
        if (stopped && (vehicle.VelocityX == null || vehicle.VelocityY == null || vehicle.AngularVelocity == null ||
            !double.IsFinite(vehicle.VelocityX.Value) || !double.IsFinite(vehicle.VelocityY.Value) || !double.IsFinite(vehicle.AngularVelocity.Value) ||
            Math.Abs(vehicle.VelocityX.Value) > settings.StoppedVelocityTolerance || Math.Abs(vehicle.VelocityY.Value) > settings.StoppedVelocityTolerance || Math.Abs(vehicle.AngularVelocity.Value) > settings.StoppedVelocityTolerance))
            return "补记物理动作或无主车辆核对要求已确认停车。";
        return null;
    }
    private string? PlcBlock(string? station = null)
    {
        var relevant = (station == null ? signals.GetAll() : signals.GetStation(station)).Where(s => s.Enabled).ToArray();
        if (relevant.Length == 0 || relevant.Any(s => s.Quality != SignalQuality.Good || s.Value == null || s.UpdatedAt == null || s.UpdatedAt < _started || AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow() - s.UpdatedAt > settings.StateMaxAge))
            return "PLC采集不完整或未取得本次启动后的新鲜信号。";
        if (relevant.Any(s => s.VariableName == "Faulted" && s.Value != false)) return "PLC存在故障，保持等待。";
        return null;
    }
    private static bool IsConfirmedPreSendRejection(DispatchTaskEntity? task, DispatchTaskStepEntity? step,
        AgvCommandOutboxEntity? command) =>
        task is { CancelConfirmedAt: not null } &&
        task.Stage is (int)DispatchTaskStage.CancellationReview or (int)DispatchTaskStage.Cancelled &&
        step is not null && string.IsNullOrWhiteSpace(step.VendorTaskId) &&
        command is not null && command.SentAt is null && string.IsNullOrWhiteSpace(command.VendorCommandId) &&
        (command.LastError?.StartsWith("AGV_PRE_SEND_REJECTED;", StringComparison.Ordinal) == true ||
         step.ErrorMessage?.StartsWith("AGV_PRE_SEND_REJECTED;", StringComparison.Ordinal) == true);
    private static string PlcStation(DispatchTaskEntity task, DispatchTaskStepEntity? step) =>
        step?.StepType is (int)DispatchTaskStepType.DumpFullBox or (int)DispatchTaskStepType.WaitForDumperReady ? step.SourceSite ?? task.StationId : task.StationId;
    private async Task<bool> ExitHandoverCompleteAsync(string taskId,CancellationToken ct)
    {
        var checkpoint=await database.Queryable<TaskHandshakeEntity>().Where(h=>h.TaskId==taskId&&h.ContractVersion==HandshakeExecutionOptions.Version&&h.Phase=="ExitHandoverComplete"&&h.PendingSignal==null).FirstAsync(ct);
        return checkpoint!=null&&await database.Queryable<DispatchTaskStepEntity>().Where(s=>s.TaskId==taskId&&s.StepId==checkpoint.StepId&&s.StepType==(int)DispatchTaskStepType.ReturnEmptyBox).AnyAsync(ct);
    }
    private async Task<string?> PlcContinuationBlockAsync(DispatchTaskEntity task, DispatchTaskStepEntity step,CancellationToken ct)
    {
        if (step.StepType is (int)DispatchTaskStepType.DeliverFullBoxToDumper or (int)DispatchTaskStepType.EnterDumpingPosition or (int)DispatchTaskStepType.ReturnEmptyBox) return null;
        if (!settings.CheckPlcSignals) return "物理握手恢复需要启用PLC采集，不允许在无现场证据时继续。";
        if (step.StepType == (int)DispatchTaskStepType.WaitForDumperReady)
        {
            // A vehicle can already be parked on its own waiting resource while
            // another task still owns the shared dump resource. In that state
            // the backend has correctly not asserted AgvAtWaitingPosition yet.
            // Requiring that future handshake to be true blocks startup recovery
            // and creates a circular wait. Resume only the original node when the
            // vehicle is on the completed delivery destination, owns a distinct
            // waiting-bay lease, and does not own the dump resource.
            var delivery = await database.Queryable<DispatchTaskStepEntity>()
                .Where(s => s.TaskId == task.TaskId &&
                    s.StepType == (int)DispatchTaskStepType.DeliverFullBoxToDumper &&
                    s.Status == (int)DispatchTaskStepStatus.Completed)
                .OrderByDescending(s => s.Sequence).FirstAsync(ct);
            var taskLeases = await database.Queryable<TrafficResourceLeaseEntity>()
                .Where(l => l.TaskId == task.TaskId)
                .ToListAsync(ct);
            var waitingLeases = delivery is null ? [] : taskLeases.Where(l =>
                l.OwnerStepId == delivery.StepId &&
                (l.ResourceId.StartsWith("WAIT-", StringComparison.OrdinalIgnoreCase) ||
                 l.ResourceId.StartsWith("DUMPER-WAIT-", StringComparison.OrdinalIgnoreCase))).ToArray();
            var ownsDump = taskLeases.Any(l =>
                l.ResourceId.StartsWith("DUMP-", StringComparison.OrdinalIgnoreCase) ||
                l.ResourceId.StartsWith("DUMPER-AREA-", StringComparison.OrdinalIgnoreCase));
            var onDeliveryDestination = task.VehicleId is { } vehicleId &&
                vehicles.TryGet(vehicleId, out var vehicle) &&
                vehicle.Connection == ConnectionState.Online &&
                vehicle.Quality == SignalQuality.Good &&
                delivery?.DestinationSite is { } destination &&
                MapRouteSiteMapper.IsSameNode(map.Current?.Definition, vehicle.CurrentStation, destination);
            if (onDeliveryDestination && waitingLeases.Length > 0 && !ownsDump) return null;
        }
        var checkpoint=await database.Queryable<TaskHandshakeEntity>().Where(h=>h.TaskId==task.TaskId&&h.StepId==step.StepId).FirstAsync(ct);
        if(checkpoint!=null)
        {
            if(checkpoint.ContractVersion!=HandshakeExecutionOptions.Version||checkpoint.Phase=="ReviewRequired")return "新契约原始绑定/阶段不可核对，保持等待。";
            var handedOver=await ExitHandoverCompleteAsync(task.TaskId,ct);
            foreach(var baseline in TaskHandshakeStore.Describe(checkpoint).Baseline.Where(b=>b.StationId!="__context"&&(!handedOver||b.StationId==task.StationId)))
                if(!signals.TryGet(baseline.StationId,baseline.VariableName,out var sample)||!sample.Enabled||sample.Address!=baseline.Address||sample.Quality!=SignalQuality.Good||sample.Value==null||sample.UpdatedAt is not {} time||time<_started||time>AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().AddSeconds(1)||AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()-time>settings.StateMaxAge)
                    return "新契约原信号地址或本次恢复观察无效，禁止续接。";
            return null; // Resume the persisted phase; the executor still evaluates its own guards. Never reassert earlier notifications.
        }
        if(step.StepType==(int)DispatchTaskStepType.PickupFullBox)
        {
            var transport=await database.Queryable<DispatchTaskStepEntity>().Where(s=>s.TaskId==task.TaskId&&s.StepType==(int)DispatchTaskStepType.GoToPickup).FirstAsync(ct);
            var binding=transport==null?null:await database.Queryable<TaskHandshakeEntity>().Where(h=>h.TaskId==task.TaskId&&h.StepId==transport.StepId&&h.ContractVersion==HandshakeExecutionOptions.Version).FirstAsync(ct);
            if(binding!=null)return UnstartedPickupReviewBlock(task,transport!,binding);
        }
        if(step.StepType==(int)DispatchTaskStepType.PlaceEmptyBox)
        {
            var root=await database.Queryable<DispatchTaskStepEntity>().Where(s=>s.TaskId==task.TaskId&&s.StepType==(int)DispatchTaskStepType.GoToPickup).FirstAsync(ct);
            var binding=root==null?null:await database.Queryable<TaskHandshakeEntity>().Where(h=>h.TaskId==task.TaskId&&h.StepId==root.StepId&&h.ContractVersion==HandshakeExecutionOptions.Version).FirstAsync(ct);
            if(binding!=null)
            {
                var returned=await database.Queryable<DispatchTaskStepEntity>().Where(s=>s.TaskId==task.TaskId&&s.StepType==(int)DispatchTaskStepType.ReturnEmptyBox&&s.Status==50).FirstAsync(ct);
                if(returned==null)return "返程机械终态尚未核实，不能续接放箱通知节点。";
                var exit=await database.Queryable<TaskHandshakeEntity>().Where(h=>h.TaskId==task.TaskId&&h.StepId==returned.StepId).FirstAsync(ct);
                if(exit!=null&&(exit.ContractVersion!=HandshakeExecutionOptions.Version||exit.PendingSignal!=null||exit.Phase is not ("AwaitPhysicalExit" or "ClearDumpingAfterExit" or "AwaitDumperClosed" or "ReleaseDumper" or "ExitHandoverComplete")))
                    return "原返程出区阶段或待确认写入尚未核对，保留原责任。";
                var dumping=await database.Queryable<DispatchTaskStepEntity>().Where(s=>s.TaskId==task.TaskId&&s.StepType==(int)DispatchTaskStepType.DumpFullBox).FirstAsync(ct);
                var dump=dumping==null?null:await database.Queryable<TaskHandshakeEntity>().Where(h=>h.TaskId==task.TaskId&&h.StepId==dumping.StepId&&h.ContractVersion==HandshakeExecutionOptions.Version&&h.Phase=="ReadyToComplete"&&h.PendingSignal==null).FirstAsync(ct);
                if(dump==null)return "缺少原倒料完成握手，不重建出区周期。";
                var original=TaskHandshakeStore.Describe(binding).Baseline.Where(b=>b.StationId==task.StationId).ToArray();
                var expected=new Dictionary<string,bool>{{"FullBoxRequested",false},{"BoxPresent",true},{"Faulted",false},{"CommunicationOnline",true},{"FullBoxRequestAcknowledged",false},{"FullBoxRemoved",true},{"EmptyBoxTaskAcknowledged",true},{"EmptyBoxPlaced",false}};
                foreach(var pair in expected)
                {
                    var prior=original.Where(b=>b.VariableName==pair.Key).ToArray();
                    if(prior.Length!=1||!signals.TryGet(task.StationId,pair.Key,out var signal)||!signal.Enabled||signal.Address!=prior[0].Address||signal.Quality!=SignalQuality.Good||signal.Value!=pair.Value||
                        signal.UpdatedAt is not {} time||time<_started||time>AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().AddSeconds(1)||AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()-time>settings.StateMaxAge)
                        return "返程后原工位保持信号或本次恢复采集不成立："+pair.Key;
                }
                return null; // Only unfreeze; the executor must still prove exit/handoff before writing EmptyBoxPlaced.
            }
        }
        var station = PlcStation(task, step);
        bool Is(string name, bool value) => signals.TryGet(station, name, out var signal) && signal.Quality == SignalQuality.Good && signal.Value == value;
        var safe = (DispatchTaskStepType)step.StepType switch
        {
            DispatchTaskStepType.PickupFullBox => Is("BoxPresent", false) && Is("FullBoxRemoved", true) && Is("EmptyBoxTaskAcknowledged", true),
            DispatchTaskStepType.DumpFullBox => Is("AgvAtDumpingPosition", true),
            DispatchTaskStepType.WaitForDumperReady => Is("AgvAtWaitingPosition", true),
            DispatchTaskStepType.PlaceEmptyBox => Is("EmptyBoxPlaced", true) && Is("BoxPresent", true),
            DispatchTaskStepType.CompletePlcHandshake => Is("BoxPresent", true),
            _ => false
        };
        return safe ? null : "PLC原握手没有保持，或物理条件冲突。禁止重新置位触发动作；保持等待，需现场PLC周期核对及握手恢复。";
    }
    private async Task<string?> SiteBlockAsync(
        CancellationToken ct,
        bool allowOfflineVehicles = false)
    {
        var active = await database.Queryable<DispatchTaskStepEntity>()
            .Where(s => s.Status != (int)DispatchTaskStepStatus.Pending &&
                        s.Status != (int)DispatchTaskStepStatus.Completed &&
                        s.Status != (int)DispatchTaskStepStatus.Cancelled)
            .ToListAsync(ct);
        foreach (var id in settings.RequiredVehicleIds.Concat(vehicles.GetAll().Where(v => v.Tasks.Any(t => t.Status is 1 or 2 or 3)).Select(v => v.VehicleId)).Distinct())
        {
            vehicles.TryGet(id, out var v);
            var matching = active.Where(s => s.VehicleId == id).ToArray();
            if (allowOfflineVehicles &&
                (v is null || v.Connection != ConnectionState.Online || v.Quality != SignalQuality.Good) &&
                matching.Length == 0)
                continue;
            var error = v?.Charging == true && matching.Length == 0
                ? ParkedChargingVehicleBlock(v)
                : VehicleBlock(v, matching.Length == 0);
            if (error != null) return id + "：" + error;
            if (v!.Tasks.Any(t => t.Status is 1 or 2 or 3 && !matching.Any(s => s.StepId == t.TaskId || s.VendorTaskId == t.TaskId)))
                return id + "存在不能关联原任务的车辆任务。";
        }
        return settings.CheckPlcSignals ? PlcBlock() : null;
    }
    private string? ParkedChargingVehicleBlock(AgvRobotSnapshot? vehicle)
    {
        if (vehicle == null || vehicle.Connection != ConnectionState.Online || vehicle.Quality != SignalQuality.Good ||
            vehicle.LastSuccessfulPollAt == null || vehicle.LastSuccessfulPollAt < _started ||
            AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow() - vehicle.LastSuccessfulPollAt > settings.StateMaxAge || vehicle.LastSuccessfulPollAt > AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().AddSeconds(1))
            return "充电车辆状态缺失、离线或不是本次启动后的新鲜采集。";
        if (vehicle.EmergencyStopped != false || vehicle.Blocked != false || vehicle.Alarms.Any(a => a.Severity >= AgvAlarmSeverity.Error))
            return "充电车辆急停、阻塞或报警状态未通过。";
        if (vehicle.X == null || vehicle.Y == null || !double.IsFinite(vehicle.X.Value) || !double.IsFinite(vehicle.Y.Value) ||
            vehicle.LocalizationConfidence == null || !double.IsFinite(vehicle.LocalizationConfidence.Value) ||
            vehicle.LocalizationConfidence < settings.MinimumLocalizationConfidence)
            return "充电车辆定位证据不足。";
        if (vehicle.VelocityX == null || vehicle.VelocityY == null || vehicle.AngularVelocity == null ||
            !double.IsFinite(vehicle.VelocityX.Value) || !double.IsFinite(vehicle.VelocityY.Value) ||
            !double.IsFinite(vehicle.AngularVelocity.Value) ||
            Math.Abs(vehicle.VelocityX.Value) > settings.StoppedVelocityTolerance ||
            Math.Abs(vehicle.VelocityY.Value) > settings.StoppedVelocityTolerance ||
            Math.Abs(vehicle.AngularVelocity.Value) > settings.StoppedVelocityTolerance)
            return "充电车辆未确认静止。";
        return null;
    }
    private static bool Running(AgvTransportTaskState state) => state is AgvTransportTaskState.Suspended or AgvTransportTaskState.Queued or AgvTransportTaskState.Assigned or
        AgvTransportTaskState.GoingToSource or AgvTransportTaskState.AtSource or AgvTransportTaskState.Loading or AgvTransportTaskState.GoingToDestination or AgvTransportTaskState.AtDestination;
    private bool CompletedIntermediateWindowAtSafePoint(DispatchTaskStepEntity step,
        AgvCommandOutboxEntity? command, AgvTransportTaskStatus? vendor, AgvRobotSnapshot? vehicle)
    {
        if (step.Status != (int)DispatchTaskStepStatus.Executing || command is null ||
            command.AttemptCount <= 0 || vendor?.State != AgvTransportTaskState.Completed ||
            vehicle is null || VehicleBlock(vehicle, true) is not null ||
            string.IsNullOrWhiteSpace(vehicle.CurrentStation))
            return false;
        try
        {
            var payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(
                command.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (payload?.Windows is not { Count: > 1 } ||
                payload.WindowIndex < 0 || payload.WindowIndex >= payload.Windows.Count - 1)
                return false;
            var currentWindow = payload.Windows[payload.WindowIndex];
            return !currentWindow.Final &&
                MapRouteSiteMapper.IsSameNode(map.Current?.Definition,
                    vehicle.CurrentStation, currentWindow.Destination);
        }
        catch (JsonException)
        {
            return false;
        }
    }
    private async Task<bool> ObstacleRecoveryAwaitingReplacementAsync(
        DispatchTaskStepEntity step, AgvTransportTaskStatus? vendor, CancellationToken ct)
    {
        if (step.Status != (int)DispatchTaskStepStatus.Executing ||
            vendor?.State is not (AgvTransportTaskState.Completed or AgvTransportTaskState.Failed or
                AgvTransportTaskState.Cancelled))
            return false;
        var current = map.Current;
        if (current is null) return false;
        var originalVendorTaskId = step.VendorTaskId ?? step.StepId;
        var definitionHash = EngineeringRuntimeIdentity.MapHash(current.Definition);
        return await database.Queryable<ObstacleRecoveryEntity>()
            .Where(row => row.TaskId == step.TaskId && row.StepId == step.StepId &&
                row.OriginalVendorTaskId == originalVendorTaskId &&
                row.State != ObstacleRecoveryStates.Recovered &&
                row.NewVendorTaskId == null && row.SendAttemptedAtUtc == null &&
                row.CancelRequestedAtUtc != null &&
                row.MapId == current.MapId && row.MapVersionId == current.VersionId &&
                row.DefinitionHash == definitionHash)
            .AnyAsync(ct);
    }
    private bool VendorMatches(DispatchTaskStepEntity step, DispatchTaskEntity task, AgvTransportTaskStatus? status) =>
        status != null && status.BusinessTaskId == (step.VendorTaskId ?? step.StepId) &&
        status.VendorTaskId == (step.VendorTaskId ?? step.StepId) &&
        (task.VendorTaskId == null || task.VendorTaskId == status.VendorTaskId) &&
        status.VehicleId == (step.VehicleId ?? task.VehicleId) && (task.VehicleId == null || status.VehicleId == task.VehicleId) &&
        status.UpdatedAt >= _started && status.UpdatedAt <= AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().AddSeconds(1) && AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow() - status.UpdatedAt <= settings.StateMaxAge;
    private string? CompletionBlock(DispatchTaskEntity task, DispatchTaskStepEntity step, AgvRobotSnapshot? vehicle, AgvTransportTaskStatus? vendor)
    {
        var physical = VehicleBlock(vehicle, true);
        if (physical != null) return physical;
        if (step.Status == (int)DispatchTaskStepStatus.Executing)
        {
            if (!VendorMatches(step, task, vendor) || vendor!.State != AgvTransportTaskState.Completed ||
                !MapRouteSiteMapper.IsSameNode(map.Current?.Definition,
                    vehicle!.CurrentStation, step.DestinationSite))
                return "运输补记需要原任务完成记录、同一车辆、目的站和停车证据一致。";
            if (map.Current is { } current)
            {
                var destination = current.Definition.Nodes.FirstOrDefault(n =>
                    n.Enabled && MapRouteSiteMapper.IsSameNode(current.Definition, n.Id, step.DestinationSite));
                if (destination == null || Math.Sqrt(Math.Pow(vehicle.X!.Value - destination.X, 2) + Math.Pow(vehicle.Y!.Value - destination.Y, 2)) > settings.MaximumStationDeviationMeters)
                    return "目的站编号与实际坐标不一致，不能补记到站。";
            }
            return null;
        }
        if (step.Status != (int)DispatchTaskStepStatus.Ready || !settings.CheckPlcSignals) return "PLC步骤补记需要启用PLC并取得新鲜信号。";
        if ((DispatchTaskStepType)step.StepType is DispatchTaskStepType.CompletePlcHandshake or DispatchTaskStepType.WaitForDumperReady)
            return "末端清握手及等待步骤通过原节点继续核对；不允许人工补记终结任务或释放占用。";
        string variable = (DispatchTaskStepType)step.StepType switch
        { DispatchTaskStepType.PickupFullBox => "BoxPresent", DispatchTaskStepType.PlaceEmptyBox => "BoxPresent", DispatchTaskStepType.DumpFullBox => "DumpingCompletedMayLeave", _ => "" };
        var expected = (DispatchTaskStepType)step.StepType != DispatchTaskStepType.PickupFullBox;
        // Dumper station may differ from source station: require the operator to continue the
        // original handshake until manufacturer cycle/retention contracts are available.
        if (variable.Length == 0 || (DispatchTaskStepType)step.StepType == DispatchTaskStepType.DumpFullBox)
            return "翻料需要动作编号及握手复位证据；当前仅布尔协议，保留原节点人工核对，不直接补记。";
        if (!signals.TryGet(task.StationId, variable, out var value) || value.Value != expected) return "PLC物理条件与完成结论冲突。";
        // Skipping pickup/placement would bypass output cleanup. Do not synthesize these writes.
        return "取放料补记还需PLC动作编号及握手清理流程；当前使用继续原节点并现场确认PLC周期。";
    }
    private async Task BindAcceptedCommandAsync(DispatchTaskEntity task, DispatchTaskStepEntity step, AgvCommandOutboxEntity command, AgvTransportTaskStatus vendor, CancellationToken ct)
    {
        if (step.Status == (int)DispatchTaskStepStatus.Executing) return;
        if (task.Stage != (int)DispatchTaskStage.SubmittingToAgv || command.Status is 30 or 100) throw new InvalidOperationException("命令/任务绑定状态冲突。");
        command.Status = (int)AgvCommandStatus.Sent; command.VendorCommandId = vendor.VendorTaskId; command.SentAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime; command.UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
        step.Status = (int)DispatchTaskStepStatus.Executing; step.VendorTaskId = vendor.VendorTaskId; step.StartedAt ??= AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime; step.UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
        task.VendorTaskId = vendor.VendorTaskId; task.VehicleId = step.VehicleId;
        task.Stage = (int)((DispatchTaskStepType)step.StepType switch
        { DispatchTaskStepType.GoToPickup => DispatchTaskStage.GoingToPickup, DispatchTaskStepType.DeliverFullBoxToDumper => DispatchTaskStage.GoingToDumper,
          DispatchTaskStepType.EnterDumpingPosition => DispatchTaskStage.Dumping, DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStage.ReturningEmptyBox, _ => throw new InvalidOperationException("非运输命令。") });
        task.UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
        await database.Updateable(command).ExecuteCommandAsync(ct); await database.Updateable(step).ExecuteCommandAsync(ct); await database.Updateable(task).ExecuteCommandAsync(ct);
    }
    private async Task CompleteStepAsync(DispatchTaskEntity task, DispatchTaskStepEntity step, CancellationToken ct)
    {
        var next = await database.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == task.TaskId && s.Sequence == step.Sequence + 1 && s.Status == 0).FirstAsync(ct)
            ?? throw new InvalidOperationException("下一执行节点缺失，不能补记。");
        task.Stage = (int)((DispatchTaskStepType)step.StepType switch
        { DispatchTaskStepType.GoToPickup => DispatchTaskStage.PickingUp, DispatchTaskStepType.DeliverFullBoxToDumper => DispatchTaskStage.WaitingForDumper,
          DispatchTaskStepType.EnterDumpingPosition => DispatchTaskStage.Dumping, DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStage.WaitingForPlcConfirmation,
          _ => throw new InvalidOperationException("当前只允许核实运输到站补记。") });
        step.Status = 50; step.CompletedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime; step.UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
        next.Status = 10; next.StartedAt ??= AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime; next.VehicleId = task.VehicleId; next.UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
        task.VendorTaskId = null; task.ErrorMessage = null; task.UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
        await database.Updateable(step).ExecuteCommandAsync(ct); await database.Updateable(next).ExecuteCommandAsync(ct); await database.Updateable(task).ExecuteCommandAsync(ct);
    }
    private async Task AuditAsync(string task, string step, string user, string decision, string reason, string evidence, CancellationToken ct) =>
        await database.Insertable(new ExecutionEvidenceEntity { Id = Guid.NewGuid().ToString("N"), SessionId = _sessionId, TaskId = task, StepId = step,
            Operator = user, Kind = decision, Reason = reason, Evidence = evidence, OccurredAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime }).ExecuteCommandAsync(ct);
    private async Task<RecoveryResult> RejectAsync(TrafficUserContext user, string ticketId, string reason, CancellationToken ct)
    {
        var ticket = await database.Queryable<ExecutionRecoveryTicketEntity>().Where(t => t.TicketId == ticketId && t.SessionId == _sessionId).FirstAsync(ct);
        await AuditAsync(ticket?.TaskId ?? "", ticket?.StepId ?? "", user.WindowsIdentity, "RecoveryRejected", reason,
            ticket?.Evidence ?? "无有效恢复条目", ct);
        return new(false, reason);
    }
    private async Task MarkReleasedAsync(string user, string reason, CancellationToken ct)
    {
        var affected = await database.Updateable<ExecutionRecoverySessionEntity>().SetColumns(s => new ExecutionRecoverySessionEntity { Released = true, ReleasedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime })
            .Where(s => s.SessionId == _sessionId && !s.Released).ExecuteCommandAsync(ct);
        if (affected != 1) throw new InvalidOperationException("恢复批次放行状态冲突。");
        await AuditAsync("", "", user, "Release", reason, "不重建任务，不释放路权；放行之后仍遵循地图执行屏障。", ct);
    }
}
