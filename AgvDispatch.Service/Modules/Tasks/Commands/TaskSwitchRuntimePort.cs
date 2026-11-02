using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Service;

// Phase one ends the original business responsibility before creating a replacement through the ordinary
// executor. It never resets PLC bits, drops traffic leases, lowers a jack, or reuses a transport StepId.
public sealed class TaskSwitchRuntimePort(ISqlSugarClient database, ITaskCancellationService cancellation,
    IManualTaskCreationService creation, IManualTaskCreationRepository receipts, IAgvStateCache vehicles,
    ITaskSwitchVehicleProbe probe, IPlcSignalCache signals, IActiveMapRuntime map, IMapExecutionGate gate,
    TaskParameterRuntime parameters, DispatchOrchestratorOptions dispatch,
    FleetTestOwnership fleet, IChargingDispatchGuard charging, MapOptions mapOptions) : ITaskSwitchPort
{
    private static TrafficUserContext Operator(TaskSwitchRecord session) => TrafficUserContext.LocalOperation(session.Operator);
    private bool Fresh(DateTimeOffset? stamp, DateTimeOffset? after = null) => stamp is { } t &&
        t <= DateTimeOffset.Now.AddSeconds(1) && DateTimeOffset.Now - t <= TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds) &&
        (after is null || t >= after);
    private static bool Zero(double? value) => value is { } v && double.IsFinite(v) && Math.Abs(v) < .01;

    private void Context(List<string> blockers)
    {
        if (!gate.CanStartNewWork) blockers.Add(gate.NewWorkBlockReason ?? "地图或启动恢复尚未放行。");
        if (map.Current is null) blockers.Add("尚无激活运行地图。");
        if (fleet.Active) blockers.Add("多车联调仍在管理原车辆；多车并发换单属于第三阶段，本阶段保持冻结。");
    }
    private void Health(string vehicleId, List<string> blockers, bool stopped, DateTimeOffset? after = null)
    {
        if (!vehicles.TryGet(vehicleId, out var vehicle)) { blockers.Add("尚无原车辆实时状态。"); return; }
        if (vehicle.Connection != ConnectionState.Online || vehicle.Quality != SignalQuality.Good || !Fresh(vehicle.LastSuccessfulPollAt, after))
            blockers.Add("原车辆离线、采集过期或尚无换单所需的新鲜状态。");
        if (vehicle.EmergencyStopped != false || vehicle.Blocked != false || vehicle.Charging != false)
            blockers.Add("原车辆急停、阻挡、充电状态未确认正常。");
        if (vehicle.LocalizationConfidence is not { } confidence || !double.IsFinite(confidence) || confidence < mapOptions.MinimumLocalizationConfidence ||
            vehicle.X is not { } x || !double.IsFinite(x) || vehicle.Y is not { } y || !double.IsFinite(y)) blockers.Add("原车辆定位状态不满足换单条件。");
        if (vehicle.Alarms.Any(a => a.Severity is AgvAlarmSeverity.Error or AgvAlarmSeverity.Fatal)) blockers.Add("原车辆仍有错误或严重报警。");
        if (stopped && (!Zero(vehicle.VelocityX) || !Zero(vehicle.VelocityY) || !Zero(vehicle.AngularVelocity)))
            blockers.Add("原车辆尚未通过速度反馈确认停稳。");
        if (charging.OwnsVehicle(vehicleId)) blockers.Add("原车辆仍由充电会话持有。");
    }
    private Task<DispatchTaskEntity?> Source(string id, CancellationToken ct) => database.Queryable<DispatchTaskEntity>().FirstAsync(t => t.TaskId == id, ct)!;

    public async Task<TaskSwitchInspection> InspectSourceAsync(TaskSwitchRequest request, CancellationToken ct)
    {
        var options = await creation.GetOptionsAsync(ct);
        using var lease = await gate.EnterControlAsync(ct);
        var blockers = new List<string>(); Context(blockers);
        var source = await Source(request.TaskId, ct);
        var vehicleId = source?.VehicleId ?? "";
        var resolvedMode = TaskSwitchModes.CancelEmptyNavigation;
        if (source is null) blockers.Add("原任务不存在。");
        else
        {
            if (source.Stage != (int)request.ExpectedStage) blockers.Add("原任务状态已变化，请刷新。");
            if (string.IsNullOrWhiteSpace(source.VehicleId) || source.CancelRequestId is not null)
                blockers.Add("原任务尚无车辆归属或已有取消申请，不能进入受控换单。");
            var steps = await database.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == source.TaskId).OrderBy(s => s.Sequence).ToListAsync(ct);
            var lateBusinessStage = TaskSwitchModes.IsBusinessCleanupStage((DispatchTaskStage)source.Stage);
            if (source.Stage == (int)DispatchTaskStage.GoingToPickup)
            {
                if (steps.Count == 0 || steps[0].StepType != (int)DispatchTaskStepType.GoToPickup ||
                    steps[0].Status != (int)DispatchTaskStepStatus.Executing ||
                    steps.Skip(1).Any(s => s.Status != (int)DispatchTaskStepStatus.Pending))
                    blockers.Add("原首段运输或后续步骤状态不符合换单前置范围。");
            }
            else if (lateBusinessStage)
            {
                if (steps.All(s => s.StepType != (int)DispatchTaskStepType.CompletePlcHandshake))
                    blockers.Add("原任务缺少完整返箱收尾步骤，不能按原业务完成换单。");
            }
            else blockers.Add("仅支持前往取箱首段，以及已取箱后的返箱、倒料和PLC收尾阶段。");
            if (request.TargetStationId == source.StationId) blockers.Add("目标工位与原工位相同，无需换单。");
            if (string.IsNullOrWhiteSpace(vehicleId)) blockers.Add("原车辆归属缺失。");
            else
            {
                Health(vehicleId, blockers, false);
                var evidence = await probe.ReadAsync(vehicleId, ct);
                if (source.Stage == (int)DispatchTaskStage.GoingToPickup)
                {
                    resolvedMode = TaskSwitchModes.Resolve((DispatchTaskStage)source.Stage,
                        evidence.EmptyAndLowered && evidence.Blockers.Count == 0,
                        evidence.BusinessCleanupReady);
                    if (resolvedMode == "")
                    {
                        blockers.AddRange(evidence.Blockers);
                        blockers.AddRange(evidence.BusinessCleanupBlockers);
                        blockers.Add("恰好到站时未能明确核定空载下降或载箱上升状态；保留原任务，禁止猜测取消方式。");
                    }
                }
                else resolvedMode = TaskSwitchModes.CompleteBusinessReturn;
                if (request.Mode != TaskSwitchModes.Auto && request.Mode != resolvedMode)
                    blockers.Add("所选换单方式与当前任务、车辆和载荷状态不符。");
                if (await database.Queryable<DispatchTaskEntity>().AnyAsync(t => t.TaskId != source.TaskId && t.VehicleId == vehicleId &&
                    t.Stage != (int)DispatchTaskStage.Completed && t.Stage != (int)DispatchTaskStage.Cancelled, ct)) blockers.Add("原车辆还有其他未闭合业务任务。");
                if (!dispatch.AllowsVehicle(request.TargetStationId, vehicleId)) blockers.Add("目标工位车辆池不包含原车辆。");
            }
        }
        var target = options.Stations.SingleOrDefault(s => s.StationId == request.TargetStationId);
        if (target is null) blockers.Add("目标工位不在当前工程配置中。");
        else if (!target.ConfigurationValid) blockers.Add("目标工位：" + target.Explanation);
        return new(vehicleId, map.Current?.MapId ?? "", map.Current?.VersionId ?? "", parameters.Revision, blockers.Distinct().ToArray())
        {
            ResolvedMode = resolvedMode,
        };
    }

    public async Task RequestCancelAsync(TaskSwitchRecord session, CancellationToken ct)
    {
        // Replayed cancellation is reconciled by the original persistent cancellation service.
        var current = await Source(session.SourceTaskId, ct);
        if (current?.CancelRequestId != session.CancelRequestId)
        {
            var inspection = await InspectSourceAsync(session.Request, ct);
            if (inspection.VehicleId != session.VehicleId || inspection.MapId != session.MapId || inspection.MapVersionId != session.MapVersionId ||
                inspection.ParameterRevision != session.ParameterRevision || inspection.Blockers.Length > 0)
                throw new InvalidOperationException(string.Join("；", inspection.Blockers.Append("取消前原任务或工程版本需重新核对。")));
        }
        var result = await cancellation.RequestAsync(Operator(session), new(session.CancelRequestId, session.SourceTaskId,
            session.Request.ExpectedStage, session.Request.Reason) { RequirePickupNavigation = true }, ct);
        if (!result.Accepted) throw new InvalidOperationException(result.Message);
    }

    public async Task<TaskSwitchClosureObservation> ObserveCloseAsync(TaskSwitchRecord session, CancellationToken ct)
    {
        using var lease = await gate.EnterControlAsync(ct);
        return await ObserveCore(session, ct);
    }
    private async Task<TaskSwitchClosureObservation> ObserveCore(TaskSwitchRecord session, CancellationToken ct)
    {
        var blockers = new List<string>(); Context(blockers);
        var source = await Source(session.SourceTaskId, ct);
        if (source is null || source.VehicleId != session.VehicleId) blockers.Add("原任务或原车辆归属缺失/不符。");
        if (TaskSwitchModes.IsCleanup(session.ResolvedMode))
        {
            if (source is null) return CleanupObservation(false, false);
            if (source.Stage is (int)DispatchTaskStage.Faulted or (int)DispatchTaskStage.Cancelled or
                (int)DispatchTaskStage.CancellationPending or (int)DispatchTaskStage.CancellationReview)
            {
                blockers.Add("原业务任务未以Completed结束，不能自动创建替代任务；请按原故障或取消流程核对。");
                return CleanupObservation(false, false);
            }
            Health(session.VehicleId, blockers, false);
            if (source.Stage != (int)DispatchTaskStage.Completed) return CleanupObservation(false, false);

            var steps = await database.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == source.TaskId).ToListAsync(ct);
            if (steps.Count == 0 || steps.Any(s => s.Status != (int)DispatchTaskStepStatus.Completed))
                blockers.Add("原任务已标记完成，但九步记录尚未全部闭合。");
            if (await database.Queryable<TrafficResourceLeaseEntity>().AnyAsync(l => l.TaskId == source.TaskId || l.VehicleId == session.VehicleId, ct))
                blockers.Add("原任务完成后仍有交通路权，须核实实际离区。");
            if (await database.Queryable<TaskEquipmentClaimEntity>().AnyAsync(e => e.TaskId == source.TaskId, ct))
                blockers.Add("原任务完成后仍持有倒料设备归属。");
            if (await database.Queryable<TaskHandshakeEntity>().AnyAsync(h => h.TaskId == source.TaskId && h.PendingSignal != null, ct))
                blockers.Add("原任务完成后仍有PLC写入结果待确认。");
            if (await database.Queryable<AgvCommandOutboxEntity>().AnyAsync(c => c.VehicleId == session.VehicleId &&
                c.Status != (int)AgvCommandStatus.Sent && c.Status != (int)AgvCommandStatus.Cancelled, ct))
                blockers.Add("原车辆仍有未闭合命令队列。");
            var probeEvidence = await probe.ReadAsync(session.VehicleId, ct);
            if (!probeEvidence.TasksKnown) blockers.AddRange(probeEvidence.BusinessCleanupBlockers.Where(reason => reason.StartsWith("车端全任务列表", StringComparison.Ordinal)));
            return CleanupObservation(source.Stage == (int)DispatchTaskStage.Completed && blockers.Count == 0,
                source.Stage == (int)DispatchTaskStage.Completed);

            TaskSwitchClosureObservation CleanupObservation(bool terminal, bool closed) => new(false, closed,
                map.Current?.MapId ?? "", map.Current?.VersionId ?? "", parameters.Revision, blockers.Distinct().ToArray())
            { CleanupConfirmed = terminal };
        }
        if (source?.CancelRequestId is null && session.Stage is TaskSwitchStages.Requested or TaskSwitchStages.ReviewHold)
        {
            if (source?.Stage != (int)session.Request.ExpectedStage) blockers.Add("取消前原任务阶段已经变化。");
            return Observation(false, false);
        }
        if (source?.CancelRequestId != session.CancelRequestId || source?.CancelVehicleId != session.VehicleId)
            blockers.Add("原取消申请或车辆编号不匹配。");
        var confirmed = source?.CancelConfirmedAt is not null && source.Stage is (int)DispatchTaskStage.CancellationReview or (int)DispatchTaskStage.Cancelled;
        if (!confirmed) return Observation(false, false);
        var after = new DateTimeOffset(source!.CancelConfirmedAt!.Value);
        Health(session.VehicleId, blockers, true, after);
        var evidence = await probe.ReadAsync(session.VehicleId, ct); blockers.AddRange(evidence.Blockers);
        if (!evidence.EmptyAndLowered || !Fresh(evidence.ObservedAt, after)) blockers.Add("停止后尚未取得新鲜空载、顶升下降反馈；到站取箱动作可能已经发生。");
        if (!evidence.TasksKnown || !evidence.NoActiveTasks) blockers.Add("车端完整任务列表尚未确认全部终结。");
        var leases = await database.Queryable<TrafficResourceLeaseEntity>().Where(l => l.TaskId == session.SourceTaskId || l.VehicleId == session.VehicleId).ToListAsync(ct);
        if (leases.Count > 0) blockers.Add("原路权仍保留：" + string.Join("、", leases.Select(l => l.ResourceId)) + "。须核实实际离区并通过交通资源处置；本流程不自动释放。");
        if (await database.Queryable<TaskEquipmentClaimEntity>().AnyAsync(e => e.TaskId == session.SourceTaskId, ct)) blockers.Add("原任务仍持有设备归属。");
        if (await database.Queryable<TaskHandshakeEntity>().AnyAsync(h => h.TaskId == session.SourceTaskId && h.PendingSignal != null, ct)) blockers.Add("原PLC写入结果仍待确认。");
        if (await database.Queryable<AgvCommandOutboxEntity>().AnyAsync(c => c.VehicleId == session.VehicleId && c.Status != (int)AgvCommandStatus.Sent && c.Status != (int)AgvCommandStatus.Cancelled, ct))
            blockers.Add("原车辆还有未闭合的命令队列。");
        foreach (var (name, expected) in new (string, bool)[] { ("CommunicationOnline", true), ("Faulted", false), ("BoxPresent", true),
            ("FullBoxRequested", false), ("FullBoxRequestAcknowledged", false), ("FullBoxRemoved", false), ("EmptyBoxTaskAcknowledged", false), ("EmptyBoxPlaced", false) })
        {
            if (!signals.TryGet(source.StationId, name, out var signal) || !signal.Enabled || signal.Quality != SignalQuality.Good || !Fresh(signal.UpdatedAt, after) || signal.Value != expected)
                blockers.Add($"原工位 {source.StationId}/{name} 未确认 {expected}（需停止后的有效采集）；须按原PLC周期核对，不能强制清零。");
        }
        var closed = source.Stage == (int)DispatchTaskStage.Cancelled;
        return Observation(true, closed) with { CanCloseOriginal = !closed && blockers.Count == 0 };

        TaskSwitchClosureObservation Observation(bool cancelConfirmed, bool originalClosed) => new(cancelConfirmed, originalClosed,
            map.Current?.MapId ?? "", map.Current?.VersionId ?? "", parameters.Revision, blockers.Distinct().ToArray());
    }

    public async Task CloseOriginalAsync(TaskSwitchRecord session, CancellationToken ct)
    {
        using var lease = await gate.EnterControlAsync(ct);
        var observation = await ObserveCore(session, ct);
        if (observation.OriginalClosed && observation.Blockers.Length == 0) return;
        if (!observation.CanCloseOriginal || observation.Blockers.Length > 0 || observation.MapId != session.MapId || observation.MapVersionId != session.MapVersionId || observation.ParameterRevision != session.ParameterRevision)
            throw new InvalidOperationException("原任务尚不满足自动关闭：" + string.Join("；", observation.Blockers));
        var result = await database.Ado.UseTranAsync(async () =>
        {
            var changed = await database.Updateable<DispatchTaskEntity>().SetColumns(t => new DispatchTaskEntity
                { Stage = (int)DispatchTaskStage.Cancelled, UpdatedAt = DateTime.Now, ErrorMessage = "空车受控换单：原运输、箱体、PLC周期及路权已核对闭合。" })
                .Where(t => t.TaskId == session.SourceTaskId && t.Stage == (int)DispatchTaskStage.CancellationReview && t.CancelRequestId == session.CancelRequestId).ExecuteCommandAsync(ct);
            if (changed != 1) throw new InvalidOperationException("原取消节点已变化，未关闭原任务。");
            await database.Updateable<DispatchTaskStepEntity>().SetColumns(s => new DispatchTaskStepEntity
                { Status = (int)DispatchTaskStepStatus.Cancelled, CompletedAt = DateTime.Now, UpdatedAt = DateTime.Now })
                .Where(s => s.TaskId == session.SourceTaskId && s.Status != (int)DispatchTaskStepStatus.Completed).ExecuteCommandAsync(ct);
            await database.Insertable(new ExecutionEvidenceEntity { Id = Guid.NewGuid().ToString("N"), TaskId = session.SourceTaskId,
                StepId = "", SessionId = session.SwitchId, Kind = "TaskSwitchOriginalClosed", Operator = session.Operator,
                Reason = session.Request.Reason, Evidence = System.Text.Json.JsonSerializer.Serialize(new { session.SwitchId, observation }), OccurredAt = DateTime.Now }).ExecuteCommandAsync(ct);
        });
        if (!result.IsSuccess) throw new InvalidOperationException("原关闭及证据未一起提交，保持换单冻结。", result.ErrorException);
    }

    public async Task<string> CreateReplacementAsync(TaskSwitchRecord session, CancellationToken ct)
    {
        var observed = await ObserveCloseAsync(session, ct);
        if (!observed.TerminalConfirmed || !observed.OriginalClosed || observed.Blockers.Length > 0 || observed.MapId != session.MapId || observed.MapVersionId != session.MapVersionId || observed.ParameterRevision != session.ParameterRevision)
            throw new InvalidOperationException("创建新任务前原责任或工程版本需重新核对：" + string.Join("；", observed.Blockers));
        var result = await creation.CreateAsync(Operator(session), new(session.ReplacementRequestId, session.TargetStationId,
            session.VehicleId, session.Request.Priority, session.MapId, session.MapVersionId) { ParameterRevision = session.ParameterRevision }, ct);
        if (!result.Accepted || string.IsNullOrWhiteSpace(result.TaskId)) throw new InvalidOperationException(result.Message);
        return result.TaskId;
    }
    public async Task<string?> FindReplacementAsync(TaskSwitchRecord session, CancellationToken ct)
    {
        var receipt = await receipts.FindRequestAsync(session.ReplacementRequestId, ct);
        if (receipt is null) return null;
        if (receipt.StationId != session.TargetStationId || receipt.RequestedVehicleId != session.VehicleId || receipt.Priority != session.Request.Priority ||
            receipt.MapId != session.MapId || receipt.MapVersionId != session.MapVersionId || receipt.ParameterRevision != session.ParameterRevision)
            throw new InvalidOperationException("原新任务申请回执参数不符，禁止替换或重建。");
        return receipt.TaskId;
    }
}
