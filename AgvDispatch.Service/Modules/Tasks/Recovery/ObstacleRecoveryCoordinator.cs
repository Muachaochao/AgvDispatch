using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using SqlSugar;

namespace AgvDispatch.Service;

/// <summary>
/// Converts a vehicle-originated local obstacle report into one auditable
/// recovery lifecycle. It never infers an obstacle from zero speed. The only
/// cancellation/send path is after a fresh trusted pose, alternate graph route,
/// traffic grant, vehicle envelope, PLC/handshake evidence and at least two
/// stopped observations. Load, task, PLC and lease ownership stay with the
/// original business task throughout.
/// </summary>
public sealed class ObstacleRecoveryCoordinator(
    ISqlSugarClient database,
    IAgvStateCache vehicles,
    IActiveMapRuntime activeMap,
    IDispatchOrchestrationRepository taskReader,
    IAgvTransportChainLookup chains,
    IRoadClosureService roadClosures,
    ITrafficResourceManager traffic,
    ITrafficBodyEnvelopeProvider envelopes,
    IMapExecutionGate executionGate,
    IAgvGateway gateway,
    ITransportTimeoutRecoveryService timeoutRecovery,
    ObstacleRecoveryStore store,
    ObstacleRecoveryOptions options,
    MapOptions mapOptions,
    DispatchOrchestratorOptions dispatchOptions,
    TimeProvider? clock = null,
    ILogger<ObstacleRecoveryCoordinator>? logger = null) : IsolatedBackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, StableStopObservation> _stableStops =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cycle = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        logger?.LogInformation(
            "车端障碍恢复已启用。AutomaticDetour={AutomaticDetour}, DetourAfter={DetourAfter}s, WarningAfter={WarningAfter}s, AlarmAfter={AlarmAfter}s, ClosureAfter={ClosureAfter}s",
            options.AutomaticDetourEnabled, options.DetourAfterSeconds,
            options.WarningAfterSeconds, options.AlarmAfterSeconds,
            options.ClosureSuggestionAfterSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.PollIntervalMs));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ObserveOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger?.LogWarning(error,
                    "障碍恢复周期失败；原任务、载荷、PLC责任和路权保持不变。");
            }
        }
    }

    public async Task ObserveOnceAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return;
        await _cycle.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.GetUtcNow();
            var open = await store.ListOpenAsync(cancellationToken);
            var handledVehicles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in open.OrderByDescending(item => item.UpdatedAt))
            {
                if (!handledVehicles.Add(row.VehicleId)) continue;
                if (!vehicles.TryGet(row.VehicleId, out var robot)) continue;
                await ProcessRecoveryAsync(row, robot, now, cancellationToken);
            }

            var executing = await taskReader.GetExecutingTasksAsync(cancellationToken);
            foreach (var task in executing
                         .Where(item => !string.IsNullOrWhiteSpace(item.VehicleId) &&
                                        !string.IsNullOrWhiteSpace(item.VendorTaskId))
                         .GroupBy(item => item.VehicleId!, StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.OrderBy(item => item.CreatedAt).First()))
            {
                if (handledVehicles.Contains(task.VehicleId!)) continue;
                if (!vehicles.TryGet(task.VehicleId!, out var robot) ||
                    !FreshBlocked(robot, now)) continue;
                var step = await FindExecutingTransportStepAsync(task, cancellationToken);
                if (step is null) continue;
                var row = await CreateRecoveryAsync(task, step, robot, now, cancellationToken);
                if (row is null) continue;
                handledVehicles.Add(row.VehicleId);
                await ProcessRecoveryAsync(row, robot, now, cancellationToken);
            }
        }
        finally { _cycle.Release(); }
    }

    private async Task<ObstacleRecoveryEntity?> CreateRecoveryAsync(
        DispatchTaskWorkItem task,
        DispatchTaskStepEntity step,
        AgvRobotSnapshot robot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (robot.ObstacleSince is not { } blockedSince || blockedSince > now.AddSeconds(1))
            return null;
        var chain = await chains.FindAsync(task.VendorTaskId!, cancellationToken);
        if (chain is not null &&
            (!string.Equals(chain.VehicleId, task.VehicleId, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(chain.BusinessTaskId, task.VendorTaskId, StringComparison.Ordinal)))
            chain = null;
        var map = activeMap.Current;
        var row = new ObstacleRecoveryEntity
        {
            RecoveryId = Guid.NewGuid().ToString("N"),
            TaskId = task.TaskId,
            StepId = step.StepId,
            VehicleId = task.VehicleId!,
            OriginalVendorTaskId = task.VendorTaskId!,
            State = ObstacleRecoveryStates.Blocked,
            Message = "车端已报告Blocked/52200，后台正在确认连续受阻时间。",
            NextAction = $"继续观察；连续受阻{options.DetourAfterSeconds}秒后自动搜索安全替代路线。",
            BlockedSinceUtc = blockedSince.UtcDateTime,
            OriginalPathJson = JsonSerializer.Serialize(chain?.Path ?? [], Json),
            CandidatePathJson = "[]",
            BlockedEdgeIdsJson = "[]",
            MapId = map?.MapId ?? "",
            MapVersionId = map?.VersionId ?? "",
            DefinitionHash = map is null ? "" : EngineeringRuntimeIdentity.MapHash(map.Definition),
            Revision = 1,
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime
        };
        await store.SaveAsync(row, cancellationToken);
        await AddEvidenceAsync(row, "ObstacleRecoveryDetected",
            "车端先报障碍；创建恢复记录，尚未取消、改道或释放任何业务责任。",
            new { robot.VehicleId, robot.CurrentStation, robot.ObstacleSince, Source = ObstacleSource(robot) },
            cancellationToken);
        return row;
    }

    private async Task ProcessRecoveryAsync(
        ObstacleRecoveryEntity row,
        AgvRobotSnapshot robot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var blockedSince = new DateTimeOffset(DateTime.SpecifyKind(row.BlockedSinceUtc, DateTimeKind.Utc));
        var elapsed = Math.Max(0, (now - blockedSince).TotalSeconds);
        var originalPath = Deserialize(row.OriginalPathJson);
        var task = await database.Queryable<DispatchTaskEntity>()
            .Where(item => item.TaskId == row.TaskId).FirstAsync(cancellationToken);
        var step = await database.Queryable<DispatchTaskStepEntity>()
            .Where(item => item.StepId == row.StepId).FirstAsync(cancellationToken);
        if (task is null || step is null)
        {
            await FreezeAsync(row, "原业务任务或步骤已不存在，恢复记录冻结。",
                "现场核对任务编号、车辆、载荷和路控；不得新建同用途任务代替。", cancellationToken);
            return;
        }
        if (row.SendAttemptedAtUtc is not null && string.IsNullOrWhiteSpace(row.NewVendorTaskId))
        {
            await FreezeAsync(row, "替代导航发送意图存在但缺少新厂家编号，结果不确定。",
                "只查询现车任务，禁止重发；核对车辆位置、载荷和PLC。", cancellationToken);
            return;
        }
        if (task.Stage == (int)DispatchTaskStage.Completed)
        {
            await MarkRecoveredAsync(row, robot, "原业务任务已完成，无需继续改道。", cancellationToken);
            return;
        }
        if (task.Stage is (int)DispatchTaskStage.Cancelled or
            (int)DispatchTaskStage.CancellationPending or (int)DispatchTaskStage.CancellationReview)
        {
            await MarkRecoveredAsync(row, robot,
                "原业务任务已进入取消或取消核对，不再自动发送替代路线。",
                cancellationToken);
            return;
        }
        if (task.Stage == (int)DispatchTaskStage.Faulted)
        {
            // The ordinary orchestrator may observe the vendor chain terminate
            // between CancelIntent and replacement binding. That expected fault
            // must not become a second, unrelated freeze while this coordinator
            // still owns a persisted cancellation intent.
            var expectedCancelObservation = row.CancelRequestedAtUtc is not null &&
                row.State != ObstacleRecoveryStates.Frozen && row.NewVendorTaskId is null;
            if (!expectedCancelObservation)
            {
                if (row.State != ObstacleRecoveryStates.Frozen)
                {
                    await FreezeAsync(row, "原业务任务已故障冻结，不再自动发送替代路线。",
                        "核对车辆、载荷、PLC和路权；按取消、换单或补记完成流程人工处置。",
                        cancellationToken);
                }
                return;
            }
        }
        if (row.NewVendorTaskId is not null)
        {
            row.State = ObstacleRecoveryStates.Executing;
            row.Message = "替代路线已发送并绑定原业务任务，等待车辆到点及后续PLC确认。";
            row.NextAction = "核对新3066车端任务全链、车辆实际运动、PLC反馈和原路权交接。";
            await SaveAsync(row, cancellationToken);
            return;
        }
        if (!FreshTelemetry(robot, now))
        {
            _stableStops.Remove(row.VehicleId);
            row.State = ObstacleRecoveryStates.SafetyHold;
            row.Message = "车辆遥测或通信不新鲜，暂停自动恢复推进。";
            row.NextAction = "恢复通信和稳定定位后只读查询原任务；不得取消、改道、释放载荷或路权。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        if (!AgvObstaclePolicy.IsBlocked(robot))
        {
            _stableStops.Remove(row.VehicleId);
            if (row.CancelRequestedAtUtc is null)
            {
                await MarkRecoveredAsync(row, robot,
                    "车端Blocked/52200已清除；继续查询原任务，障碍清除本身不证明任务完成。",
                    cancellationToken);
                // A fresh clear closes this recovery lifecycle. Falling through
                // would reuse the old blocked-since interval and later freeze a
                // task that has already resumed or completed under a verified
                // replacement navigation.
                return;
            }
            else
            {
                row.State = ObstacleRecoveryStates.AwaitingOriginalTerminal;
                row.Message = "障碍已清除，但原车端任务已请求取消；继续等待原所有分段终态。";
                row.NextAction = "原任务全链终态且车辆在可信站点连续停稳后，发送已核验替代路线以续接原业务任务。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
            }
        }

        if (elapsed < options.DetourAfterSeconds)
        {
            await SetStateAsync(row, elapsed,
                $"车端连续受阻{elapsed:0.0}秒，尚在车端自动恢复窗口。",
                $"保留原任务和路权；{options.DetourAfterSeconds}秒后自动搜索替代路线。",
                cancellationToken);
            return;
        }

        if (row.CancelRequestedAtUtc is null)
        {
            var plan = await PlanAsync(row, robot, task, step, originalPath, cancellationToken);
            if (plan.Available)
            {
                var candidateChanged = !Deserialize(row.CandidatePathJson)
                    .SequenceEqual(plan.Route, StringComparer.OrdinalIgnoreCase);
                row.CandidatePathJson = JsonSerializer.Serialize(plan.Route, Json);
                row.BlockedEdgeIdsJson = JsonSerializer.Serialize(plan.BlockedEdgeIds, Json);
                row.SafeSite = plan.SafeSite;
                row.State = ObstacleRecoveryStates.DetourReady;
                row.Message = $"已生成并复核候选路线：{string.Join(" → ", plan.Route)}。";
                row.NextAction = options.AutomaticDetourEnabled
                    ? "正在复核停稳、车身包络、PLC、路权与厂家原任务终态；全部通过后自动取消原导航并改道。"
                    : "自动改道未启用；候选已保存，原任务和路权继续保留。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
                if (candidateChanged)
                    await AddEvidenceAsync(row, "ObstacleDetourPlanned",
                        "替代路线已通过当前地图、道路封锁、交通资源与最短路网规划；尚未发送。",
                        new { plan.Route, plan.BlockedEdgeIds, plan.SafeSite, robot.CurrentStation },
                        cancellationToken);
                if (!options.AutomaticDetourEnabled) return;
                await TryRequestCancellationAsync(row, robot, task, step, plan.Route, cancellationToken);
                return;
            }

            row.CandidatePathJson = "[]";
            row.BlockedEdgeIdsJson = JsonSerializer.Serialize(plan.BlockedEdgeIds, Json);
            row.SafeSite = plan.SafeSite;
            if (elapsed >= options.ClosureSuggestionAfterSeconds)
            {
                await FreezeNoRouteAsync(row, plan.Message, cancellationToken);
                return;
            }
            row.State = elapsed >= options.AlarmAfterSeconds ? ObstacleRecoveryStates.Alarm :
                elapsed >= options.WarningAfterSeconds ? ObstacleRecoveryStates.Warning :
                ObstacleRecoveryStates.NoRoute;
            row.Message = plan.Message;
            row.NextAction = row.State switch
            {
                ObstacleRecoveryStates.Alarm =>
                    $"持续受阻{elapsed:0}秒；保持原任务、载荷和责任。建议核对是否封路，仅封锁实际受阻方向及必要上游汇入，不应暂停无关任务。",
                ObstacleRecoveryStates.Warning =>
                    $"持续受阻{elapsed:0}秒；继续搜索安全替代路线，并检查车端定位、阻塞范围和邻近车辆。",
                _ =>
                    $"未找到安全替代路线；继续保留原任务。{(string.IsNullOrWhiteSpace(plan.SafeSite) ? "" : $"推荐安全点{plan.SafeSite}。")}"
            };
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }

        await ContinueCancellationAsync(row, robot, task, step, originalPath, elapsed, cancellationToken);
    }

    private async Task TryRequestCancellationAsync(
        ObstacleRecoveryEntity row,
        AgvRobotSnapshot robot,
        DispatchTaskEntity task,
        DispatchTaskStepEntity step,
        IReadOnlyList<string> candidate,
        CancellationToken cancellationToken)
    {
        using var control = await executionGate.EnterControlAsync(cancellationToken);
        var refusal = await AutomaticSafetyReasonAsync(row, robot, candidate, cancellationToken);
        if (refusal is not null)
        {
            row.Message = "自动改道尚未发送：" + refusal;
            row.NextAction = "保持原任务、载荷、PLC责任和路权；修正条件后后台会自动重试规划与核验。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        var original = await gateway.GetTransportTaskStatusAsync(
            row.OriginalVendorTaskId, row.VehicleId, Deserialize(row.OriginalPathJson), cancellationToken);
        if (original is null || original.State == AgvTransportTaskState.Unknown)
        {
            row.Message = "原3066任务全链状态尚未完整取得，暂不取消。";
            row.NextAction = "只读查询原所有分段；缺失或未知时保持原任务。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        if (original.State == AgvTransportTaskState.Completed)
        {
            await MarkRecoveredAsync(row, robot, "原导航已完成，障碍未造成未完成运输。", cancellationToken);
            return;
        }
        if (original.State is AgvTransportTaskState.Failed or AgvTransportTaskState.Cancelled)
        {
            row.OriginalTerminalAtUtc = _clock.GetUtcNow().UtcDateTime;
            row.State = ObstacleRecoveryStates.AwaitingOriginalTerminal;
            row.Message = "原车端任务已终态，等待连续停稳后发送替代路线。";
            row.NextAction = "再次核验车辆、载荷、PLC、替代路线、路权与车身包络。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }

        row.CancelRequestedAtUtc = _clock.GetUtcNow().UtcDateTime;
        _stableStops.Remove(row.VehicleId);
        row.State = ObstacleRecoveryStates.CancelIntent;
        row.Message = "安全前置条件已通过，已持久化原3066任务精确取消意图。";
        row.NextAction = "等待原所有厂家分段终态；取消应答本身不证明任务结束。";
        row.Revision++;
        await SaveAsync(row, cancellationToken);
        try
        {
            await gateway.CancelTaskAsync(row.OriginalVendorTaskId, cancellationToken);
            row.State = ObstacleRecoveryStates.AwaitingOriginalTerminal;
            row.Message = "原3066取消请求已应答，等待全部分段终态和连续停稳。";
            row.NextAction = "只读查询原任务链；任何缺失、未知或仍活动状态均阻止替代导航。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            row.State = ObstacleRecoveryStates.Uncertain;
            row.Message = "自动取消结果不确定，禁止重发：" + Truncate(error.Message);
            row.NextAction = "只查询原3066任务链；按厂家编号、车辆、载荷和PLC现场核对。";
        }
        row.Revision++;
        await SaveAsync(row, CancellationToken.None);
        await AddEvidenceAsync(row, "ObstacleOriginalCancelRequested",
            row.Message, new { original, Candidate = candidate }, CancellationToken.None);
    }

    private async Task ContinueCancellationAsync(
        ObstacleRecoveryEntity row,
        AgvRobotSnapshot robot,
        DispatchTaskEntity task,
        DispatchTaskStepEntity step,
        IReadOnlyList<string> originalPath,
        double elapsed,
        CancellationToken cancellationToken)
    {
        using var control = await executionGate.EnterControlAsync(cancellationToken);
        if (originalPath.Count < 2)
        {
            await FreezeAsync(row, "原导航路径快照缺失，无法核定取消范围。",
                "现场核对原厂家任务与车辆位置；禁止发送替代导航。", cancellationToken);
            return;
        }
        AgvTransportTaskStatus? original;
        try
        {
            original = await gateway.GetTransportTaskStatusAsync(
                row.OriginalVendorTaskId, row.VehicleId, originalPath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            row.State = ObstacleRecoveryStates.Uncertain;
            row.Message = "查询原3066任务终态失败，不重发取消或导航：" + Truncate(error.Message);
            row.NextAction = "恢复通信后只查询原厂家编号；必要时现场核对。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        if (original is null || original.State == AgvTransportTaskState.Unknown)
        {
            row.State = row.CancelRequestedAtUtc is null
                ? ObstacleRecoveryStates.Uncertain
                : ObstacleRecoveryStates.AwaitingOriginalTerminal;
            row.Message = "原3066任务全链尚未确认终态。";
            row.NextAction = "保持原任务和路权，只读查询所有分段；不得发送第二条导航。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        if (original.State == AgvTransportTaskState.Completed)
        {
            await MarkRecoveredAsync(row, robot, "原导航已完整完成，无需替代任务。", cancellationToken);
            return;
        }
        if (original.State is not (AgvTransportTaskState.Failed or AgvTransportTaskState.Cancelled))
        {
            row.State = ObstacleRecoveryStates.AwaitingOriginalTerminal;
            row.Message = $"取消请求后原任务仍为{original.State}，已在途任务不能重发。";
            row.NextAction = elapsed >= options.ClosureSuggestionAfterSeconds
                ? "持续取消等待已超过60秒；核对是否封锁实际受阻方向，并人工核对控制器任务状态。"
                : "继续只读等待原全部分段终态；保留原业务任务、载荷和路权。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        if (row.OriginalTerminalAtUtc is null)
        {
            row.OriginalTerminalAtUtc = _clock.GetUtcNow().UtcDateTime;
            row.State = ObstacleRecoveryStates.AwaitingOriginalTerminal;
            row.Message = "原3066全链已确认终态，重新等待取消后的连续停稳和车端队列清空。";
            row.NextAction = "再次核验车辆无活动任务、位置与起点一致、PLC、路权和车辆包络；通过后才发送替代路线。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }

        var plan = await PlanAsync(row, robot, task, step, originalPath, cancellationToken);
        if (!plan.Available)
        {
            if (elapsed >= options.ClosureSuggestionAfterSeconds)
            {
                await FreezeNoRouteAsync(row, plan.Message, cancellationToken);
                return;
            }
            row.State = elapsed >= options.AlarmAfterSeconds ? ObstacleRecoveryStates.Alarm :
                elapsed >= options.WarningAfterSeconds ? ObstacleRecoveryStates.Warning :
                ObstacleRecoveryStates.NoRoute;
            row.Message = "原任务已取消，但当前没有安全替代路线：" + plan.Message;
            row.NextAction = string.IsNullOrWhiteSpace(plan.SafeSite)
                ? "保持原业务任务冻结证据，检查车端定位与地图；不得让车辆恢复空闲。"
                : $"推荐安全点{plan.SafeSite}；核对载荷后人工确认受控转移，禁止直接宣告任务完成。";
            row.SafeSite = plan.SafeSite;
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }

        row.CandidatePathJson = JsonSerializer.Serialize(plan.Route, Json);
        row.BlockedEdgeIdsJson = JsonSerializer.Serialize(plan.BlockedEdgeIds, Json);
        row.SafeSite = plan.SafeSite;
        var refusal = await AutomaticSafetyReasonAsync(row, robot, plan.Route, cancellationToken);
        if (refusal is not null)
        {
            row.State = ObstacleRecoveryStates.SafetyHold;
            row.Message = "原任务已终态，替代路线发送前核验未通过：" + refusal;
            row.NextAction = "保持原业务任务、载荷和PLC责任；不得释放路权或让车辆成为空闲可派。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        await traffic.RecordRouteAsync(new TrafficRouteObservation(
            row.StepId, row.TaskId, row.VehicleId, plan.Route[0], plan.Route[^1],
            plan.EdgeIds, plan.Resources, false,
            "车端障碍自动改道：按当前路线重新登记必需路权；原任务和责任保持不变。")
        {
            MapId = row.MapId,
            MapVersionId = row.MapVersionId
        }, cancellationToken);
        var reservation = await traffic.TryAcquireAsync(
            row.StepId, row.TaskId, row.VehicleId, plan.Resources, cancellationToken);
        if (!reservation.Acquired)
        {
            row.State = ObstacleRecoveryStates.DetourReady;
            row.Message = $"替代路线等待公共路权{reservation.BlockingResourceId}。";
            row.NextAction = "保留原任务和已持有路权；按交通队列等待，不绕开安全或公平顺序。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }

        var newRoot = ObstacleRecoveryTaskIds.NewId();
        row.NewVendorTaskId = newRoot;
        row.SendAttemptedAtUtc = _clock.GetUtcNow().UtcDateTime;
        row.State = ObstacleRecoveryStates.SendIntent;
        row.Message = "已持久化替代导航发送意图；最多发送一次。";
        row.NextAction = "发送后核对整条新3066任务链；结果未知时只查询并禁止重发。";
        row.Revision++;
        await SaveAsync(row, cancellationToken);
        try
        {
            if (gateway is not IAgvObstacleRecoveryGateway recoveryGateway)
                throw new NotSupportedException("当前AGV适配器不支持车端受阻状态下的受控改道发送。");
            var sent = await recoveryGateway.CreateObstacleRecoveryTransportTaskAsync(
                newRoot, row.VehicleId, plan.Route,
                dispatchOptions.GetAction((DispatchTaskStepType)step.StepType),
                new DateTimeOffset(DateTime.SpecifyKind(row.BlockedSinceUtc, DateTimeKind.Utc)),
                row.OriginalVendorTaskId, cancellationToken);
            if (!string.Equals(sent, newRoot, StringComparison.Ordinal))
                throw new InvalidOperationException("替代导航返回编号与持久化意图不一致。");
            await BindReplacementAsync(row, task, step, plan, newRoot, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            row.State = ObstacleRecoveryStates.Uncertain;
            row.Message = "替代导航发送结果不确定，只查询原新编号并禁止重发：" + Truncate(error.Message);
            row.NextAction = "按新厂家编号查询任务；核对车辆位置、载荷、PLC与原任务编号，无法证明时冻结。";
            row.Revision++;
            await SaveAsync(row, CancellationToken.None);
            await AddEvidenceAsync(row, "ObstacleDetourSendUncertain",
                row.Message, new { NewVendorTaskId = newRoot, Route = plan.Route }, CancellationToken.None);
        }
    }

    private async Task BindReplacementAsync(
        ObstacleRecoveryEntity row,
        DispatchTaskEntity task,
        DispatchTaskStepEntity step,
        RouteAssessment plan,
        string newRoot,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var targetStage = TargetStage((DispatchTaskStepType)step.StepType);
        step.Status = (int)DispatchTaskStepStatus.Executing;
        step.VendorTaskId = newRoot;
        step.SourceSite = plan.Route[0];
        step.DestinationSite = plan.Route[^1];
        step.ErrorMessage = null;
        step.UpdatedAt = now;
        task.VendorTaskId = newRoot;
        task.VehicleId = row.VehicleId;
        task.Stage = (int)targetStage;
        task.ErrorMessage = "车端障碍自动改道已发送；继续沿用原业务任务、载荷和PLC责任。";
        task.UpdatedAt = now;
        row.State = ObstacleRecoveryStates.Executing;
        row.Message = task.ErrorMessage;
        row.NextAction = "核对新3066任务链、车辆实际运动、PLC反馈和原路权交接。";
        row.Revision++;
        row.UpdatedAt = now;
        var previousCommand = await database.Queryable<AgvCommandOutboxEntity>()
            .Where(item => item.TaskId == row.TaskId && item.StepId == row.StepId &&
                           item.CommandType == (int)AgvCommandType.Transport &&
                           item.Status == (int)AgvCommandStatus.Sent)
            .OrderByDescending(item => item.CreatedAt)
            .FirstAsync(cancellationToken);
        var map = activeMap.Current ??
            throw new InvalidOperationException("替代导航绑定前运行地图已移除。");
        if (map.MapId != row.MapId || map.VersionId != row.MapVersionId ||
            !string.Equals(EngineeringRuntimeIdentity.MapHash(map.Definition), row.DefinitionHash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("替代导航绑定前地图身份已变化。");
        var request = await database.Queryable<TrafficRequestEntity>()
            .Where(item => item.TaskId == row.TaskId && item.StepId == row.StepId)
            .FirstAsync(cancellationToken) ??
            throw new InvalidOperationException("替代导航缺少当前路权请求。");
        var commandId = "CMD-" + Guid.NewGuid().ToString("N");
        var payloadJson = JsonSerializer.Serialize(new AgvTransportCommandPayload(
            newRoot, row.VehicleId, plan.Route[0], plan.Route[^1],
            plan.Route, dispatchOptions.GetAction((DispatchTaskStepType)step.StepType)), Json);
        var routeSnapshot = TransportRouteSnapshots.Capture(row.TaskId, row.StepId, commandId,
            row.VehicleId, payloadJson, map, request);
        var replacementCommand = new AgvCommandOutboxEntity
        {
            CommandId = commandId,
            IdempotencyKey = "OBSTACLE:" + newRoot,
            TaskId = row.TaskId,
            StepId = row.StepId,
            VehicleId = row.VehicleId,
            CommandType = (int)AgvCommandType.Transport,
            Status = (int)AgvCommandStatus.Sent,
            PayloadJson = payloadJson,
            AttemptCount = 1,
            ExecutionMapId = map.MapId,
            ExecutionMapVersionId = map.VersionId,
            ExecutionRouteSnapshotJson = TransportRouteSnapshots.Serialize(routeSnapshot),
            ExecutionRouteSnapshotHash = TransportRouteSnapshots.Hash(TransportRouteSnapshots.Serialize(routeSnapshot)),
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            SentAt = now,
            VendorCommandId = newRoot
        };
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            await database.Updateable(step).ExecuteCommandAsync(cancellationToken);
            await database.Updateable(task).ExecuteCommandAsync(cancellationToken);
            await database.Updateable(row).ExecuteCommandAsync(cancellationToken);
            if (previousCommand is not null)
            {
                previousCommand.Status = (int)AgvCommandStatus.Cancelled;
                previousCommand.LastError = "车端障碍自动改道已由后续受控命令替代；原始路线快照保持不可变。";
                previousCommand.UpdatedAt = now;
                await database.Updateable(previousCommand).ExecuteCommandAsync(cancellationToken);
            }
            await database.Insertable(replacementCommand).ExecuteCommandAsync(cancellationToken);
            await database.Insertable(new ExecutionEvidenceEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                SessionId = "",
                TaskId = row.TaskId,
                StepId = row.StepId,
                Operator = "service:obstacle-auto-detour",
                Kind = "ObstacleAutomaticDetourSent",
                Reason = "车端先报受阻；原任务终态和全部安全条件通过后发送一次替代3066。",
                Evidence = JsonSerializer.Serialize(new
                {
                    row.RecoveryId,
                    row.OriginalVendorTaskId,
                    NewVendorTaskId = newRoot,
                    plan.Route,
                    plan.EdgeIds,
                    plan.Resources,
                    row.MapId,
                    row.MapVersionId,
                    row.DefinitionHash
                }, Json),
                OccurredAt = now
            }).ExecuteCommandAsync(cancellationToken);
        });
        if (!transaction.IsSuccess)
        {
            row.State = ObstacleRecoveryStates.Uncertain;
            row.Message = Truncate("替代导航已发送，但任务/步骤绑定落库失败；禁止重发：" +
                transaction.ErrorException?.Message);
            row.NextAction = "按新厂家编号和原业务任务现场核对；不得释放载荷、PLC责任或路权。";
            row.Revision++;
            await store.SaveAsync(row, CancellationToken.None);
            return;
        }
        logger?.LogInformation(
            "车端障碍自动改道已发送。TaskId={TaskId}, StepId={StepId}, Vehicle={Vehicle}, NewVendorTaskId={NewVendorTaskId}",
            row.TaskId, row.StepId, row.VehicleId, newRoot);
    }

    private async Task<RouteAssessment> PlanAsync(
        ObstacleRecoveryEntity row,
        AgvRobotSnapshot robot,
        DispatchTaskEntity task,
        DispatchTaskStepEntity step,
        IReadOnlyList<string> originalPath,
        CancellationToken cancellationToken)
    {
        var map = activeMap.Current;
        if (map is null) return RouteAssessment.Refuse("当前运行地图不可用。");
        var closures = await roadClosures.ListAsync(cancellationToken);
        if (!closures.Ready || closures.IdentityConflict || closures.MapId != map.MapId ||
            closures.VersionId != map.VersionId ||
            !string.Equals(closures.DefinitionHash, EngineeringRuntimeIdentity.MapHash(map.Definition),
                StringComparison.OrdinalIgnoreCase))
            return RouteAssessment.Refuse("道路封锁状态未就绪、地图身份不一致或存在冲突。");
        if (string.IsNullOrWhiteSpace(robot.CurrentStation))
            return RouteAssessment.Refuse("车辆当前站点未知。");
        var current = map.Definition.Nodes.Where(node => node.Enabled &&
            string.Equals(node.AgvSiteId, robot.CurrentStation, StringComparison.OrdinalIgnoreCase)).ToArray();
        var destination = map.Definition.Nodes.Where(node => node.Enabled &&
            !string.IsNullOrWhiteSpace(step.DestinationSite) &&
            string.Equals(node.AgvSiteId, step.DestinationSite, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (current.Length != 1 || destination.Length != 1)
            return RouteAssessment.Refuse("当前站点或原任务目的站在运行地图中不是唯一有效点。");
        var positions = originalPath.Select((site, index) => (site, index))
            .Where(item => string.Equals(item.site, robot.CurrentStation, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (positions.Length != 1 || positions[0].index + 1 >= originalPath.Count)
            return RouteAssessment.Refuse("车辆当前点不在原路线中段，无法唯一确定实际受阻下一段。");
        var nextSite = originalPath[positions[0].index + 1];
        var next = MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, nextSite);
        var inferred = map.Definition.Edges.Where(edge => edge.Enabled &&
                (string.Equals(edge.From, current[0].Id, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(edge.To, next, StringComparison.OrdinalIgnoreCase) ||
                 edge.Bidirectional &&
                 string.Equals(edge.From, next, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(edge.To, current[0].Id, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(edge => edge.Bidirectional
                ? new[]
                {
                    new MapDirectedTraversal(edge.Id, current[0].Id, next),
                    new MapDirectedTraversal(edge.Id, next, current[0].Id)
                }
                : new[] { new MapDirectedTraversal(edge.Id, current[0].Id, next) })
            .ToList();
        if (inferred.Count == 0)
            return RouteAssessment.Refuse("原车端地图在后台工程图中没有对应前向道路。");
        inferred.AddRange(closures.BlockedTraversals);
        var unavailableTraversals = inferred.Distinct().ToArray();
        var unavailableResources = await traffic.GetUnavailableResourceIdsAsync(task.TaskId, cancellationToken);
        var protectedNodes = dispatchOptions.GetDumperWaitingSites()
            .Append(dispatchOptions.DumperDumpingSite)
            .Concat(dispatchOptions.StandbySites)
            .Where(site => !string.IsNullOrWhiteSpace(site))
            .Select(site => MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, site))
            .Where(id => !map.Definition.Nodes.Any(node =>
                node.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && node.AllowTransitThrough))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var route = map.Graph.FindShortestTransportRoute(current[0].Id, destination[0].Id,
            unavailableResources, null, protectedNodes, unavailableTraversals);
        var safeSite = await RecommendSafeSiteAsync(map, current[0].Id, unavailableResources,
            unavailableTraversals, protectedNodes, cancellationToken);
        if (route is null)
            return new(false, $"已推断受阻道路为{robot.CurrentStation}→{nextSite}，当前路网没有安全替代路线。",
                [], unavailableTraversals.Select(item => item.EdgeId).Distinct().ToArray(), safeSite);
        var sitePlan = MapRouteSiteMapper.ToAgvSitePlan(map.Definition, route);
        if (sitePlan.Nodes.Count < 2 ||
            !string.Equals(sitePlan.Nodes[0], robot.CurrentStation, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sitePlan.Nodes[^1], step.DestinationSite, StringComparison.OrdinalIgnoreCase) ||
            sitePlan.Nodes.SequenceEqual(originalPath, StringComparer.OrdinalIgnoreCase))
            return new(false, "规划结果与实际起点、原目的站或避障要求不一致。",
                sitePlan.Nodes, unavailableTraversals.Select(item => item.EdgeId).Distinct().ToArray(), safeSite);
        return new(true, $"已形成替代路线：{string.Join("→", sitePlan.Nodes)}。",
            sitePlan.Nodes, unavailableTraversals.Select(item => item.EdgeId).Distinct().ToArray(), safeSite)
        {
            Resources = route.ResourceIds,
            EdgeIds = route.EdgeIds
        };
    }

    private async Task<string?> AutomaticSafetyReasonAsync(
        ObstacleRecoveryEntity row,
        AgvRobotSnapshot robot,
        IReadOnlyList<string> candidate,
        CancellationToken cancellationToken)
    {
        if (executionGate.Mode != MapExecutionMode.Running || !executionGate.CanStartNewWork)
            return executionGate.NewWorkBlockReason ?? "地图执行门禁未放行。";
        if (candidate.Count < 2 || !string.Equals(candidate[0], robot.CurrentStation, StringComparison.OrdinalIgnoreCase))
            return "替代路线起点与当前可信站点不一致。";
        if (robot.Connection != ConnectionState.Online || robot.Quality != SignalQuality.Good ||
            robot.LastSuccessfulPollAt is not { } observed ||
            observed > _clock.GetUtcNow().AddSeconds(1) ||
            _clock.GetUtcNow() - observed >= TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds))
            return "车辆通信或遥测不新鲜。";
        if (robot.EmergencyStopped != false || robot.Charging == true ||
            AgvObstaclePolicy.HasBlockingAlarm(robot))
            return "急停、充电或非局部避障严重报警优先。";
        if (robot.LocalizationConfidence is not { } confidence ||
            confidence < options.MinimumLocalizationConfidence)
            return "车辆定位置信度不足。";
        var map = activeMap.Current;
        var station = map?.Definition.Nodes.Where(node => node.Enabled &&
            string.Equals(node.AgvSiteId, robot.CurrentStation, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (map is null || station is not { Length: 1 } ||
            robot.X is not { } x || !double.IsFinite(x) ||
            robot.Y is not { } y || !double.IsFinite(y) ||
            Math.Sqrt(Math.Pow(x - station[0].X, 2) + Math.Pow(y - station[0].Y, 2)) >
                options.MaximumStationDeviationMeters)
            return "车辆尚未停靠在可信命名站点，当前厂家接口不能从路段中间冒充站点重发；等待车端局部恢复或人工安全点处置。";
        var tolerance = options.StoppedVelocityToleranceMetersPerSecond;
        if (robot.VelocityX is not { } vx || !double.IsFinite(vx) || Math.Abs(vx) > tolerance ||
            robot.VelocityY is not { } vy || !double.IsFinite(vy) || Math.Abs(vy) > tolerance ||
            robot.AngularVelocity is not { } w || !double.IsFinite(w) || Math.Abs(w) > tolerance)
            return "车辆尚未形成可信停稳证据。";
        var envelope = await envelopes.ReadAsync(row.VehicleId, cancellationToken);
        if (envelope is null || !envelope.CalibrationVerified || !envelope.IncludesMaximumPermittedLoad ||
            envelope.RadiusFromReportedReferenceMeters <= 0)
            return "缺少已标定且包含最大载箱尺寸的车辆包络。";
        if (!StableStopped(row.VehicleId, robot.CurrentStation, _clock.GetUtcNow()))
            return $"停稳观察尚未达到{options.StableStopObservationMs}毫秒。";
        return await timeoutRecovery.ValidateReplacementPreconditionsAsync(
            row.TaskId, row.StepId, cancellationToken);
    }

    private bool StableStopped(string vehicleId, string? station, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(station))
        {
            _stableStops.Remove(vehicleId);
            return false;
        }
        if (!_stableStops.TryGetValue(vehicleId, out var observation) ||
            !string.Equals(observation.Station, station, StringComparison.OrdinalIgnoreCase) ||
            now < observation.Since)
        {
            _stableStops[vehicleId] = new(station, now);
            return false;
        }
        return now - observation.Since >= TimeSpan.FromMilliseconds(options.StableStopObservationMs);
    }

    private async Task<string?> RecommendSafeSiteAsync(
        ActiveMapRuntimeSnapshot map,
        string current,
        IReadOnlySet<string> unavailableResources,
        IReadOnlyCollection<MapDirectedTraversal> unavailableTraversals,
        IReadOnlySet<string> protectedNodes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safeSites = dispatchOptions.StandbySites.Concat(dispatchOptions.GetDumperWaitingSites())
            .Where(site => !string.IsNullOrWhiteSpace(site))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (safeSites.Length == 0) return null;
        var currentSite = map.Definition.Nodes.FirstOrDefault(node =>
            string.Equals(node.Id, current, StringComparison.OrdinalIgnoreCase))?.AgvSiteId;
        if (currentSite is not null && safeSites.Contains(currentSite, StringComparer.OrdinalIgnoreCase))
            return currentSite;
        var candidates = safeSites
            .Select(site => (Site: site, Node: MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, site)))
            .DistinctBy(item => item.Node, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                var route = map.Graph.FindShortestTransportRoute(current, item.Node, unavailableResources,
                    null, protectedNodes, unavailableTraversals);
                return route is null ? (item.Site, Cost: (double?)null) : (item.Site, Cost: (double?)route.Cost);
            })
            .Where(item => item.Cost is not null)
            .OrderBy(item => item.Cost)
            .FirstOrDefault();
        await Task.CompletedTask;
        return candidates.Site;
    }

    private async Task FreezeNoRouteAsync(ObstacleRecoveryEntity row, string reason,
        CancellationToken cancellationToken)
    {
        await FreezeAsync(row,
            $"持续受阻超过{options.ClosureSuggestionAfterSeconds}秒且无替代路线：{reason}",
            "建议正式封锁实际受阻方向及必要上游汇入；仅影响该路段，其他无冲突任务继续。" +
            (string.IsNullOrWhiteSpace(row.SafeSite) ? "" : $"推荐安全点{row.SafeSite}，需人工核对载荷后受控转移。") +
            " 任务、载荷、PLC责任和路权保留；无法完成时再走取消或换单恢复流程。",
            cancellationToken);
    }

    private async Task FreezeAsync(ObstacleRecoveryEntity row, string message, string nextAction,
        CancellationToken cancellationToken)
    {
        var task = await database.Queryable<DispatchTaskEntity>()
            .Where(item => item.TaskId == row.TaskId).FirstAsync(cancellationToken);
        var step = await database.Queryable<DispatchTaskStepEntity>()
            .Where(item => item.StepId == row.StepId).FirstAsync(cancellationToken);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (task is not null)
        {
            task.Stage = (int)DispatchTaskStage.Faulted;
            task.ErrorMessage = Truncate(message);
            task.UpdatedAt = now;
        }
        if (step is not null && step.Status is not ((int)DispatchTaskStepStatus.Completed or (int)DispatchTaskStepStatus.Cancelled))
        {
            step.Status = (int)DispatchTaskStepStatus.ManualIntervention;
            step.ErrorMessage = Truncate(message);
            step.UpdatedAt = now;
        }
        row.State = ObstacleRecoveryStates.Frozen;
        row.Message = Truncate(message);
        row.NextAction = Truncate(nextAction);
        row.Revision++;
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            if (task is not null) await database.Updateable(task).ExecuteCommandAsync(cancellationToken);
            if (step is not null) await database.Updateable(step).ExecuteCommandAsync(cancellationToken);
            await database.Updateable(row).ExecuteCommandAsync(cancellationToken);
        });
        if (transaction.IsSuccess)
            await AddEvidenceAsync(row, "ObstacleRecoveryFrozen", message,
                new { row.SafeSite, row.BlockedEdgeIdsJson, row.State }, cancellationToken);
    }

    private async Task MarkRecoveredAsync(ObstacleRecoveryEntity row, AgvRobotSnapshot robot,
        string message, CancellationToken cancellationToken)
    {
        row.State = ObstacleRecoveryStates.Recovered;
        row.Message = message;
        row.NextAction = "按原任务后续车端状态、PLC反馈、载荷与路权交接继续核对。";
        row.Revision++;
        await SaveAsync(row, cancellationToken);
        _stableStops.Remove(row.VehicleId);
        await AddEvidenceAsync(row, "ObstacleRecoveryObservationCleared", message,
            new
            {
                robot.CurrentStation,
                robot.Blocked,
                robot.LastObstacleClearedAt,
                robot.LastSuccessfulPollAt,
                robot.VelocityX,
                robot.VelocityY,
                robot.AngularVelocity
            }, cancellationToken);
    }

    private async Task SetStateAsync(ObstacleRecoveryEntity row, double elapsed,
        string message, string nextAction, CancellationToken cancellationToken)
    {
        row.State = elapsed >= options.DetourAfterSeconds
            ? ObstacleRecoveryStates.DetourReady : ObstacleRecoveryStates.Blocked;
        row.Message = message;
        row.NextAction = nextAction;
        row.Revision++;
        await SaveAsync(row, cancellationToken);
    }

    private async Task SaveAsync(ObstacleRecoveryEntity row, CancellationToken cancellationToken)
    {
        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await store.SaveAsync(row, cancellationToken);
    }

    private Task AddEvidenceAsync(ObstacleRecoveryEntity row, string kind, string reason,
        object evidence, CancellationToken cancellationToken) =>
        database.Insertable(new ExecutionEvidenceEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = "",
            TaskId = row.TaskId,
            StepId = row.StepId,
            Operator = "service:obstacle-recovery",
            Kind = kind,
            Reason = Truncate(reason),
            Evidence = JsonSerializer.Serialize(evidence, Json),
            OccurredAt = _clock.GetUtcNow().UtcDateTime
        }).ExecuteCommandAsync(cancellationToken);

    private async Task<DispatchTaskStepEntity?> FindExecutingTransportStepAsync(
        DispatchTaskWorkItem task,
        CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<DispatchTaskStepEntity>()
            .Where(step => step.TaskId == task.TaskId &&
                           step.VendorTaskId == task.VendorTaskId &&
                           step.Status == (int)DispatchTaskStepStatus.Executing)
            .ToListAsync(cancellationToken);
        return rows.Count == 1 && IsTransport((DispatchTaskStepType)rows[0].StepType) ? rows[0] : null;
    }

    private bool FreshBlocked(AgvRobotSnapshot robot, DateTimeOffset now) =>
        FreshTelemetry(robot, now) &&
        AgvObstaclePolicy.IsBlocked(robot) &&
        robot.ObstacleSince is not null;

    private bool FreshTelemetry(AgvRobotSnapshot robot, DateTimeOffset now) =>
        robot.Connection == ConnectionState.Online &&
        robot.Quality == SignalQuality.Good &&
        robot.LastSuccessfulPollAt is { } observed &&
        observed <= now.AddSeconds(1) &&
        now - observed < TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds);

    private static string ObstacleSource(AgvRobotSnapshot robot)
    {
        var blocked = robot.Blocked == true;
        var alarm = robot.Alarms.Any(AgvObstaclePolicy.IsLocalObstacle);
        return blocked && alarm ? "Blocked + 52200" : blocked ? "Blocked" : alarm ? "52200" : "";
    }

    private static bool IsTransport(DispatchTaskStepType step) =>
        step is DispatchTaskStepType.GoToPickup
            or DispatchTaskStepType.DeliverFullBoxToDumper
            or DispatchTaskStepType.EnterDumpingPosition
            or DispatchTaskStepType.ReturnEmptyBox;

    private static DispatchTaskStage TargetStage(DispatchTaskStepType step) => step switch
    {
        DispatchTaskStepType.GoToPickup => DispatchTaskStage.GoingToPickup,
        DispatchTaskStepType.DeliverFullBoxToDumper => DispatchTaskStage.GoingToDumper,
        DispatchTaskStepType.EnterDumpingPosition => DispatchTaskStage.Dumping,
        DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStage.ReturningEmptyBox,
        _ => throw new InvalidOperationException("不是可改道的运输步骤。")
    };

    private static string[] Deserialize(string value)
    {
        try { return JsonSerializer.Deserialize<string[]>(value, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string Truncate(string value) => value.Length <= 1000 ? value : value[..1000];

    private sealed record StableStopObservation(string Station, DateTimeOffset Since);

    private sealed record RouteAssessment(
        bool Available,
        string Message,
        IReadOnlyList<string> Route,
        IReadOnlyList<string> BlockedEdgeIds,
        string? SafeSite)
    {
        public IReadOnlyList<string> Resources { get; init; } = [];
        public IReadOnlyList<string> EdgeIds { get; init; } = [];
        public static RouteAssessment Refuse(string message) => new(false, message, [], [], null);
    }
}
