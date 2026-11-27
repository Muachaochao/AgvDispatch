using System.Collections.Concurrent;
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
    ILogger<ObstacleRecoveryCoordinator>? logger = null, DispatchEnergyForecast? energyForecast = null,
    IRoutePlanner? forecastRoutes = null, IExecutionEvidenceJournal? evidenceJournal = null) : IsolatedBackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider;
    private readonly SemaphoreSlim _cycle = new(1, 1);
    // Retry state for pre-send rejections is kept in memory. A restart only makes the
    // next attempt earlier; the persisted send intent has already been cleared.
    private readonly ConcurrentDictionary<string, int> _sendRejections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _sendRetryAfter = new(StringComparer.Ordinal);
    // Per-record timing and isolation state. It lives in memory: a restart only makes
    // the next observation start earlier, and never alters the persisted record.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFreshSeen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _clearSince = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _blockedAfterTerminal = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _routeWaitSince = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RecordBackoff> _recordBackoff = new(StringComparer.Ordinal);
    private sealed record RecordBackoff(int Failures, DateTimeOffset RetryAfter);
    private ObstacleRecoveryPreSendLimits PreSendLimits => new(
        options.MinimumLocalizationConfidence, options.StoppedVelocityToleranceMetersPerSecond);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        logger?.LogInformation(
            "车端障碍恢复已启用。AutomaticDetour={AutomaticDetour}, AssessmentAfter={DetourAfter}s, CancelAfter={CancelAfter}s, CancelWaitWarning={CancelWaitWarning}s, WarningAfter={WarningAfter}s, AlarmAfter={AlarmAfter}s, ClosureAfter={ClosureAfter}s",
            options.AutomaticDetourEnabled, options.DetourAfterSeconds,
            options.CancelAfterSeconds, options.CancelTerminalWaitWarningSeconds,
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
                await RunIsolatedAsync(row.RecoveryId, now,
                    () => ProcessRecoveryAsync(row, robot, now, cancellationToken), cancellationToken);
            }

            var executing = await taskReader.GetExecutingTasksAsync(cancellationToken);
            foreach (var task in executing
                         .Where(item => !string.IsNullOrWhiteSpace(item.VehicleId) &&
                                        !string.IsNullOrWhiteSpace(item.VendorTaskId))
                         .GroupBy(item => item.VehicleId!, StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.OrderBy(item => item.CreatedAt).First()))
            {
                if (handledVehicles.Contains(task.VehicleId!)) continue;
                await RunIsolatedAsync("create:" + task.TaskId, now, async () =>
                {
                    if (!vehicles.TryGet(task.VehicleId!, out var robot) ||
                        !FreshBlocked(robot, now)) return;
                    var step = await FindExecutingTransportStepAsync(task, cancellationToken);
                    if (step is null) return;
                    var row = await CreateRecoveryAsync(task, step, robot, now, cancellationToken);
                    if (row is null) return;
                    handledVehicles.Add(row.VehicleId);
                    await ProcessRecoveryAsync(row, robot, now, cancellationToken);
                }, cancellationToken);
            }
        }
        finally { _cycle.Release(); }
    }

    // A failure in one record or one task's creation is logged and backed off. Other
    // records and vehicles are still observed in the same cycle. The persisted record
    // is left as it was: no state change is made on the failure path.
    private async Task RunIsolatedAsync(string key, DateTimeOffset now, Func<Task> work,
        CancellationToken cancellationToken)
    {
        if (_recordBackoff.TryGetValue(key, out var backoff) && now < backoff.RetryAfter)
            return;
        try
        {
            await work();
            _recordBackoff.TryRemove(key, out _);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var failures = (backoff?.Failures ?? 0) + 1;
            var wait = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failures - 1, 6))));
            _recordBackoff[key] = new RecordBackoff(failures, now + wait);
            logger?.LogWarning(error,
                "障碍恢复单条记录观察失败；原任务、载荷、PLC责任和路权保持不变，{Seconds}秒后隔离重试。Key={Key}",
                wait.TotalSeconds, key);
        }
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
        var originalPath = await ResolveFullOriginalPathAsync(task, step, chain, cancellationToken);
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
            OriginalPathJson = JsonSerializer.Serialize(originalPath, Json),
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

    private async Task<IReadOnlyList<string>> ResolveFullOriginalPathAsync(
        DispatchTaskWorkItem task,
        DispatchTaskStepEntity step,
        AgvTransportChain? chain,
        CancellationToken cancellationToken)
    {
        // The gateway chain lookup must expose the currently sent rolling window
        // so 3066 identity and acceptance remain strict. Recovery has a different
        // need: it must know the complete route frozen when that window was sent.
        var commands = await database.Queryable<AgvCommandOutboxEntity>()
            .Where(item => item.TaskId == task.TaskId && item.StepId == step.StepId &&
                           item.CommandType == (int)AgvCommandType.Transport)
            .OrderByDescending(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command.ExecutionRouteSnapshotJson))
                continue;
            TransportRouteSnapshot snapshot;
            try
            {
                snapshot = TransportRouteSnapshots.Read(command);
            }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            {
                throw new InvalidOperationException(
                    "发送时冻结的完整路线快照无效，禁止根据滚动窗口猜测恢复路线。", error);
            }
            if (!string.Equals(snapshot.VehicleId, task.VehicleId, StringComparison.OrdinalIgnoreCase) ||
                snapshot.AgvSites.Length is < 2 or > 2048 ||
                snapshot.AgvSites.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException(
                    "发送时冻结的完整路线快照车辆或站点无效，禁止继续自动改道。");
            return snapshot.AgvSites;
        }
        return chain?.Path ?? [];
    }

    private async Task ProcessRecoveryAsync(
        ObstacleRecoveryEntity row,
        AgvRobotSnapshot robot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Interrupted or invalid telemetry cannot contribute to continuous stop evidence.
        if (!FreshTelemetry(robot, now) || !StationaryWithGoodPosition(robot))
            ClearStableStop(row);
        RealignBlockedStartAfterGap(row, robot, now);
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
        // A replacement identity is persisted together with the send intent and is
        // only turned into Executing by the bind transaction. A send that was
        // interrupted or failed after it may have reached the vehicle, so the record
        // stays uncertain. It is never resent here; it is only reconciled read-only.
        if (row.NewVendorTaskId is not null &&
            row.State is ObstacleRecoveryStates.SendIntent or ObstacleRecoveryStates.Uncertain)
        {
            await ReconcileUncertainReplacementAsync(row, now, cancellationToken);
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
            var expectedCancelObservation = (row.CancelRequestedAtUtc is not null || row.OriginalTerminalAtUtc is not null) &&
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
        if (row.SafetyPointRetreatAttemptedAtUtc is not null &&
            string.IsNullOrWhiteSpace(row.SafetyPointRetreatTaskId))
        {
            await FreezeAsync(row, "安全点退回发送意图存在但缺少恢复任务编号，结果不确定。",
                "只查询车端现有任务和车辆实际位置，禁止重发3051或3055；核对载荷、PLC和路权。",
                cancellationToken);
            return;
        }
        if (row.SafetyPointRetreatTaskId is not null &&
            row.SafetyPointReachedAtUtc is null)
        {
            await ContinueSafetyPointRetreatAsync(row, robot, cancellationToken);
            return;
        }
        if (row.NewVendorTaskId is not null)
        {
            // Only the bind transaction sets Executing. Other states that carry an
            // identity keep their own meaning and are not rewritten here.
            if (row.State == ObstacleRecoveryStates.Executing)
            {
                // One cycle covers one replacement. When the step has finished, or the
                // vehicle is blocked again by an obstacle that began after the bind, the
                // cycle is closed; the next pass opens a new one for the executing step.
                if (step.Status is (int)DispatchTaskStepStatus.Completed or (int)DispatchTaskStepStatus.Cancelled)
                {
                    await MarkRecoveredAsync(row, robot, "替代导航所属步骤已结束，本轮障碍恢复关闭。", cancellationToken);
                    return;
                }
                if (FreshBlocked(robot, now) && robot.ObstacleSince is { } newBlockage &&
                    row.SendAttemptedAtUtc is { } boundAt &&
                    newBlockage > new DateTimeOffset(DateTime.SpecifyKind(boundAt, DateTimeKind.Utc)))
                {
                    await MarkRecoveredAsync(row, robot,
                        "替代导航执行中再次受阻，关闭上一轮并按新的受阻起点开启新一轮。", cancellationToken);
                    return;
                }
                row.Message = "替代路线已发送并绑定原业务任务，等待车辆到点及后续PLC确认。";
                row.NextAction = "核对新3066车端任务全链、车辆实际运动、PLC反馈和原路权交接。";
                await SaveAsync(row, cancellationToken);
            }
            return;
        }
        if (!FreshTelemetry(robot, now))
        {
            ClearStableStop(row);
            _clearSince.TryRemove(row.RecoveryId, out _);
            _blockedAfterTerminal.TryRemove(row.RecoveryId, out _);
            row.State = ObstacleRecoveryStates.SafetyHold;
            row.Message = "车辆遥测或通信不新鲜，暂停自动恢复推进。";
            row.NextAction = "恢复通信和稳定定位后只读查询原任务；不得取消、改道、释放载荷或路权。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        // An obstacle that clears must stay clear for ClearConfirmSeconds before the
        // record closes. A flickering blocked signal would otherwise close and reopen
        // the record and restart the blockage timer every time.
        if (AgvObstaclePolicy.IsBlocked(robot))
        {
            _clearSince.TryRemove(row.RecoveryId, out _);
            if (row.OriginalTerminalAtUtc is { } terminal &&
                robot.LastSuccessfulPollAt is { } observed && observed.UtcDateTime > terminal)
                _blockedAfterTerminal[row.RecoveryId] = observed;
        }
        else
        {
            var clearStart = _clearSince.GetOrAdd(row.RecoveryId, now);
            if (now - clearStart < TimeSpan.FromSeconds(options.ClearConfirmSeconds))
            {
                row.Message = "车端障碍信号已消失，等待连续清除确认后再结束恢复，避免间歇抖动重置受阻计时。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
                return;
            }
        }
        if (!AgvObstaclePolicy.IsBlocked(robot))
        {
            if (row.CancelRequestedAtUtc is null && row.OriginalTerminalAtUtc is null)
            {
                ClearStableStop(row);
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
                row.Message = row.CancelRequestedAtUtc is null
                    ? "障碍已清除，原车端窗口已有终态；仍须核验停稳后续接剩余业务路线。"
                    : "障碍已清除，但原车端任务已请求取消；继续等待原所有分段终态。";
                row.NextAction = "原任务全链终态且车辆在可信站点连续停稳后，发送已核验替代路线以续接原业务任务。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
            }
        }

        if (elapsed < options.DetourAfterSeconds)
        {
            await SetStateAsync(row, elapsed,
                $"车端连续受阻{elapsed:0.0}秒，仍在保留原导航的阻挡观察期。",
                $"保留原任务和路权；{options.DetourAfterSeconds}秒后自动搜索替代路线。",
                cancellationToken);
            return;
        }

        if (row.CancelRequestedAtUtc is null && row.OriginalTerminalAtUtc is null)
        {
            if (options.AutomaticSafetyPointRetreatEnabled &&
                !IsAtTrustedStation(robot))
            {
                var retreat = PlanSafetyPointRetreat(robot);
                if (!retreat.Available)
                {
                    row.State = elapsed >= options.AlarmAfterSeconds
                        ? ObstacleRecoveryStates.Alarm
                        : ObstacleRecoveryStates.NoRoute;
                    row.Message = retreat.Message;
                    row.NextAction =
                        "保留原任务、载荷、PLC责任和路权；自动退站条件不完整时不发送3051或3055，改为人工核验或继续等待车端恢复。";
                    row.Revision++;
                    await SaveAsync(row, cancellationToken);
                    return;
                }

                var retreatChanged = row.SafetyPointTarget != retreat.TargetSite ||
                    row.SafetyPointBlockedSource != retreat.BlockedSource ||
                    row.SafetyPointBlockedTarget != retreat.BlockedTarget;
                row.SafetyPointTarget = retreat.TargetSite;
                row.SafetyPointBlockedSource = retreat.BlockedSource;
                row.SafetyPointBlockedTarget = retreat.BlockedTarget;
                row.SafeSite = retreat.TargetSite;
                row.CandidatePathJson = "[]";
                row.State = ObstacleRecoveryStates.SafetyPointReady;
                row.Message =
                    $"车辆位于{retreat.BlockedSource}→{retreat.BlockedTarget}路段中，已选择后方安全站{retreat.TargetSite}，预计倒走{retreat.RemainingDistanceMeters:0.00}米。";
                row.NextAction =
                    retreat.RemainingDistanceMeters <= 1.0
                        ? "先精确取消原3066并等待全链终态；停稳、定位、载荷、PLC、路权和包络全部通过后，只发送一次3055直线后退。"
                        : "先精确取消原3066并等待全链终态；停稳、定位、载荷、PLC、路权和包络全部通过后，只发送一次低速3051退回安全站。";
                row.BlockedEdgeIdsJson = JsonSerializer.Serialize(
                    new[] { retreat.BlockedEdgeId }, Json);
                row.Revision++;
                await SaveAsync(row, cancellationToken);
                if (retreatChanged)
                    await AddEvidenceAsync(row, "ObstacleSafetyPointRetreatPlanned",
                    "道路中段受阻；普通替代路线不得冒用历史站点起点，已形成隔离单车低速退站候选。",
                    new
                    {
                        retreat.TargetSite,
                        retreat.BlockedSource,
                        retreat.BlockedTarget,
                        retreat.BlockedEdgeId,
                        retreat.RemainingDistanceMeters,
                        robot.X,
                        robot.Y,
                        robot.SourceStation,
                        robot.TargetStation
                    }, cancellationToken);
                await TryRequestCancellationAsync(
                    row, robot, task, step, [], cancellationToken,
                    safetyPointRetreat: true);
                return;
            }

            if (!IsAtTrustedStation(robot))
            {
                // Mid-road or unknown station: a plan from the last reported station
                // would be a guess. Report it as its own state and keep the task.
                row.CandidatePathJson = "[]";
                row.State = elapsed >= options.AlarmAfterSeconds
                    ? ObstacleRecoveryStates.Alarm
                    : ObstacleRecoveryStates.NotAtStation;
                row.Message = $"车端连续受阻{elapsed:0}秒，车辆不在可信命名站点（路段中间或站点未知）；不按历史站点规划替代路线。";
                row.NextAction = "保留原任务、载荷、PLC责任和路权；车辆需停在可信站点，或由人工安全转移；到站后自动重新评估。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
                return;
            }

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
                    ? "正在复核停稳、车身包络、PLC、路权与厂家原任务终态；并满足独立取消观察时间后，才允许请求取消；取消后仍须全链终态才能改道。"
                    : "自动改道未启用；候选已保存，原任务和路权继续保留。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
                if (candidateChanged)
                    await AddEvidenceAsync(row, "ObstacleDetourPlanned",
                        "替代路线已通过当前地图、道路封锁、交通资源与最短路网规划；尚未发送。",
                        new { plan.Route, plan.BlockedEdgeIds, plan.SafeSite, robot.CurrentStation },
                        cancellationToken);
                if (!options.AutomaticDetourEnabled) return;
                // Probe the replacement's public rights before the original navigation is
                // cancelled, so a queued route never ends the original navigation first.
                var probe = await traffic.TryAcquireAsync(row.StepId, row.TaskId, row.VehicleId,
                    plan.Resources, cancellationToken);
                if (!probe.Acquired)
                {
                    await HoldForRouteRightsAsync(row, probe.BlockingResourceId, now, cancellationToken);
                    return;
                }
                _routeWaitSince.TryRemove(row.RecoveryId, out _);
                await TryRequestCancellationAsync(row, robot, task, step, plan.Route, cancellationToken);
                return;
            }

            row.CandidatePathJson = "[]";
            if (plan.BlockedEdgeIds.Count != 0)
                row.BlockedEdgeIdsJson = JsonSerializer.Serialize(plan.BlockedEdgeIds, Json);
            row.SafeSite = plan.SafeSite;
            // Before cancellation a missing route only warns and alarms. The original
            // navigation keeps running and the task stage is untouched.
            row.State = elapsed >= options.AlarmAfterSeconds ? ObstacleRecoveryStates.Alarm :
                elapsed >= options.WarningAfterSeconds ? ObstacleRecoveryStates.Warning :
                ObstacleRecoveryStates.NoRoute;
            row.Message = plan.Message;
            row.NextAction = NoRouteNextAction(row.State, elapsed, plan.SafeSite);
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
        CancellationToken cancellationToken,
        bool safetyPointRetreat = false)
    {
        // D2: cancelling an already-running task is existing work. A shared
        // execution lease keeps the map stable for the vendor call without
        // serializing dispatch, steps or releases for every other vehicle.
        using var control = await executionGate.EnterAsync(MapExecutionOperation.ExistingWork, cancellationToken);
        if (control is null)
        {
            row.Message = "地图切换或恢复门禁暂未放行，保留原任务和取消意图。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        var refusal = safetyPointRetreat
            ? await SafetyPointRetreatSafetyReasonAsync(row, robot, cancellationToken)
            : await AutomaticSafetyReasonAsync(row, robot, candidate, cancellationToken);
        if (refusal is not null)
        {
            row.Message = "自动改道尚未发送：" + refusal;
            row.NextAction = "保持原任务、载荷、PLC责任和路权；修正条件后后台会自动重试规划与核验。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        var originalVendorPath = await ResolveOriginalVendorWindowPathAsync(
            row.OriginalVendorTaskId, row.VehicleId, cancellationToken);
        if (originalVendorPath.Count < 2)
        {
            row.State = ObstacleRecoveryStates.Uncertain;
            row.Message = "原3066当前窗口路径或车辆身份缺失，暂不取消。";
            row.NextAction = "核对Outbox中该W窗口的原发送载荷；不得按完整业务路线猜造车端分段编号。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        var original = await gateway.GetTransportTaskStatusAsync(
            row.OriginalVendorTaskId, row.VehicleId, originalVendorPath, cancellationToken);
        if (original is null || original.State == AgvTransportTaskState.Unknown)
        {
            row.Message = "原3066任务全链状态尚未完整取得，暂不取消。";
            row.NextAction = "只读查询原所有分段；缺失或未知时保持原任务。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        var observationSeconds = (_clock.GetUtcNow().UtcDateTime - row.BlockedSinceUtc).TotalSeconds;
        if (observationSeconds < options.CancelAfterSeconds)
        {
            row.Message = $"已只读核对原任务为{original.State}，候选方案尚未发送；连续受阻{observationSeconds:0}秒。";
            row.NextAction = $"保留原导航、载荷、PLC和路权；受阻满{options.CancelAfterSeconds}秒才允许考虑受控取消，并重新核验全部安全条件。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        if (original.State == AgvTransportTaskState.Completed)
        {
            if (AtBusinessDestination(robot, step))
            {
                await RecordOriginalTerminalAsync(row, original, cancellationToken);
                await MarkRecoveredAsync(row, robot, "原导航已完整到达业务目的站，障碍未造成未完成运输。",
                    cancellationToken);
                return;
            }
            await RecordOriginalTerminalAsync(row, original, cancellationToken);
            row.State = ObstacleRecoveryStates.AwaitingOriginalTerminal;
            row.Message = "当前车端滚动窗口已完成，但原业务步骤尚未到达目的站；无第二条导航正在运行。";
            row.NextAction = "重新累计停稳证据后，从当前可信站点仅发送一条剩余路线，继续原业务任务。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        if (original.State is AgvTransportTaskState.Failed or AgvTransportTaskState.Cancelled)
        {
            // The vendor may terminate before we request cancellation. Continue
            // through the terminal recovery gate without inventing a cancel request.
            await RecordOriginalTerminalAsync(row, original, cancellationToken);
            ClearStableStop(row);
            row.State = ObstacleRecoveryStates.AwaitingOriginalTerminal;
            row.Message = "原车端任务已终态，等待连续停稳后发送替代路线。";
            row.NextAction = "再次核验车辆、载荷、PLC、替代路线、路权与车身包络。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }

        row.CancelRequestedAtUtc = _clock.GetUtcNow().UtcDateTime;
        ClearStableStop(row);
        row.State = ObstacleRecoveryStates.CancelIntent;
        row.Message = "安全前置条件已通过，已持久化原3066任务精确取消意图。";
        row.NextAction = "等待原所有厂家分段终态；取消应答本身不证明任务结束。";
        row.Revision++;
        await SaveAsync(row, cancellationToken);
        await AddEvidenceAsync(row, "ObstacleOriginalCancelIntent",
            "取消意图已持久化；尚不能证明厂家已收到或任务已释放。",
            new { row.CancelRequestedAtUtc, Candidate = candidate }, cancellationToken);
        try
        {
            row.CancelAttemptedAtUtc = _clock.GetUtcNow().UtcDateTime;
            await SaveAsync(row, cancellationToken);
            await AddEvidenceAsync(row, "ObstacleOriginalCancelAttempted",
                "开始调用网关精确取消；这是调用边界，不是逐报文发送时间。",
                new { row.CancelAttemptedAtUtc }, cancellationToken);
            await gateway.CancelTaskAsync(row.OriginalVendorTaskId, cancellationToken);
            row.CancelAcknowledgedAtUtc = _clock.GetUtcNow().UtcDateTime;
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
        using var control = await executionGate.EnterAsync(MapExecutionOperation.ExistingWork, cancellationToken);
        if (control is null)
        {
            row.State = ObstacleRecoveryStates.SafetyHold;
            row.Message = "地图切换或恢复门禁暂未放行，保留原任务与路权。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        if (originalPath.Count < 2)
        {
            await FreezeAsync(row, "原导航路径快照缺失，无法核定取消范围。",
                "现场核对原厂家任务与车辆位置；禁止发送替代导航。", cancellationToken);
            return;
        }
        AgvTransportTaskStatus? original;
        try
        {
            var originalVendorPath = await ResolveOriginalVendorWindowPathAsync(
                row.OriginalVendorTaskId, row.VehicleId, cancellationToken);
            if (originalVendorPath.Count < 2)
            {
                row.State = ObstacleRecoveryStates.Uncertain;
                row.Message = "原3066当前窗口路径身份缺失，禁止猜造分段编号。";
                row.NextAction = "核对Outbox原W窗口载荷和车辆；恢复前不得发送第二条导航。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
                return;
            }
            original = await gateway.GetTransportTaskStatusAsync(
                row.OriginalVendorTaskId, row.VehicleId, originalVendorPath, cancellationToken);
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
            row.NextAction = CancellationWaitAction(row);
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        if (original.State == AgvTransportTaskState.Completed)
        {
            if (AtBusinessDestination(robot, step))
            {
                await RecordOriginalTerminalAsync(row, original, cancellationToken);
                await MarkRecoveredAsync(row, robot, "原导航已完整到达业务目的站，无需替代任务。",
                    cancellationToken);
                return;
            }
            if (row.OriginalTerminalAtUtc is null)
            {
                await RecordOriginalTerminalAsync(row, original, cancellationToken);
                row.State = ObstacleRecoveryStates.AwaitingOriginalTerminal;
                row.Message = "当前滚动窗口已终态；等待重新累计停稳后发送剩余业务路线。";
                row.NextAction = "只读核对车辆位于可信中间站、载荷、PLC、路权和车辆包络。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
                return;
            }
        }
        if (original.State is not (AgvTransportTaskState.Completed or
            AgvTransportTaskState.Failed or AgvTransportTaskState.Cancelled))
        {
            row.State = ObstacleRecoveryStates.AwaitingOriginalTerminal;
            row.Message = $"取消请求后原任务仍为{original.State}，已在途任务不能重发。";
            row.NextAction = CancellationWaitAction(row);
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        if (row.OriginalTerminalAtUtc is null)
        {
            await RecordOriginalTerminalAsync(row, original, cancellationToken);
            row.State = ObstacleRecoveryStates.AwaitingOriginalTerminal;
            row.Message = "原3066全链已确认终态，重新等待取消后的连续停稳和车端队列清空。";
            row.NextAction = "再次核验车辆无活动任务、位置与起点一致、PLC、路权和车辆包络；通过后才发送替代路线。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }

        if (row.SafetyPointTarget is not null &&
            row.SafetyPointReachedAtUtc is null)
        {
            await ContinueSafetyPointRetreatAsync(
                row, robot, cancellationToken, controlHeld: true);
            return;
        }

        var allowClearedOriginalRoute =
            row.OriginalTerminalAtUtc is not null &&
            robot.Blocked == false &&
            !AgvObstaclePolicy.IsBlocked(robot) &&
            _blockedAfterTerminal.TryGetValue(row.RecoveryId, out var postTerminalBlockedAt) &&
            robot.LastObstacleClearedAt is { } clearedAt && clearedAt > postTerminalBlockedAt;
        var plan = await PlanAsync(
            row, robot, task, step, originalPath, cancellationToken,
            excludeInferredBlocked: !allowClearedOriginalRoute,
            allowOriginalRoute: allowClearedOriginalRoute);
        if (plan.BlockedEdgeIds.Count != 0)
            row.BlockedEdgeIdsJson = JsonSerializer.Serialize(plan.BlockedEdgeIds, Json);
        if (!plan.Available)
        {
            // Not frozen: the cancelled task stays resumable. The next pass plans
            // again and sends as soon as a route, the stop evidence and the lock pass.
            row.State = elapsed >= options.AlarmAfterSeconds ? ObstacleRecoveryStates.Alarm :
                elapsed >= options.WarningAfterSeconds ? ObstacleRecoveryStates.Warning :
                ObstacleRecoveryStates.NoRoute;
            row.Message = "原任务已取消，但当前没有安全替代路线：" + plan.Message;
            row.NextAction = string.IsNullOrWhiteSpace(plan.SafeSite)
                ? "保持原业务任务与路权，后台继续重新规划；不得让车辆恢复空闲。"
                : $"推荐安全点{plan.SafeSite}；后台继续重新规划，无替代路线前不得宣告任务完成。" +
                  (elapsed >= options.ClosureSuggestionAfterSeconds ? "已超过封路建议时间，请人工核对封路。" : "");
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
        if (_sendRetryAfter.TryGetValue(row.RecoveryId, out var retryAfter) &&
            _clock.GetUtcNow().UtcDateTime < retryAfter)
        {
            row.Message = $"上次替代导航发送前被拒绝，{(retryAfter - _clock.GetUtcNow().UtcDateTime).TotalSeconds:0}秒后重新核验并发送。";
            row.NextAction = "保持原任务、载荷、PLC责任和路权；退避结束后自动重试，发送门禁逐次核验。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        if (energyForecast?.Enabled == true && forecastRoutes is not null && robot.BatteryPercent is {} battery &&
            energyForecast.Reassess(row.VehicleId, battery, (DispatchTaskStepType)step.StepType,
                dispatchOptions.GetPickupSite(task.StationId), dispatchOptions.DumperDumpingSite, dispatchOptions.GetDumperWaitingSites(),
                new(plan.Route, plan.EdgeIds, plan.Resources, 0), activeMap.Current, forecastRoutes) is {} energy)
        {
            var blockedByEnergy = energyForecast.Enforced && !energy.Safe;
            const string energyHold = "替代路线预计到桩电量不足，保留原任务、载荷和PLC责任，等待低电量处置。";
            if (evidenceJournal is not null && (!blockedByEnergy || row.Message != energyHold))
                await evidenceJournal.RecordAsync(row.TaskId, row.StepId, "DispatchEnergyForecast",
                    new { vehicleId = row.VehicleId, stationId = task.StationId, energy, trigger = "obstacle", currentSite = robot.CurrentStation }, cancellationToken);
            if (blockedByEnergy)
            {
                row.State = ObstacleRecoveryStates.SafetyHold;
                row.Message = energyHold;
                row.NextAction = "核对剩余电量与可达安全路线。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
                return;
            }
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
            await HoldForRouteRightsAsync(row, reservation.BlockingResourceId, _clock.GetUtcNow(), cancellationToken);
            return;
        }
        _routeWaitSince.TryRemove(row.RecoveryId, out _);

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
                row.OriginalVendorTaskId, PreSendLimits, cancellationToken);
            if (!string.Equals(sent, newRoot, StringComparison.Ordinal))
                throw new InvalidOperationException("替代导航返回编号与持久化意图不一致。");
            await BindReplacementAsync(row, task, step, plan, newRoot, cancellationToken);
            _sendRejections.TryRemove(row.RecoveryId, out _);
            _sendRetryAfter.TryRemove(row.RecoveryId, out _);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsPreSendRejection(error))
        {
            await RejectReplacementSendAsync(row, plan.Route, error, cancellationToken);
        }
        catch (Exception error)
        {
            // The request may have reached the vehicle. Keep it uncertain: this state is
            // sticky until a read-only reconciliation of the new vendor identity.
            row.State = ObstacleRecoveryStates.Uncertain;
            row.Message = "替代导航发送结果不确定，只查询原新编号并禁止重发：" + Truncate(error.Message);
            row.NextAction = "按新厂家编号查询任务；核对车辆位置、载荷、PLC与原任务编号，无法证明时冻结。";
            row.Revision++;
            await SaveAsync(row, CancellationToken.None);
            await AddEvidenceAsync(row, "ObstacleDetourSendUncertain",
                row.Message, new { NewVendorTaskId = newRoot, Route = plan.Route }, CancellationToken.None);
        }
    }

    // Refusals raised before any command reached the vehicle. Anything else after
    // the send attempt (I/O, timeout, partial acceptance) is treated as uncertain.
    private static bool IsPreSendRejection(Exception error) =>
        error is AgvRecoverySendRejectedException or AgvCommandRejectedException
            or NotSupportedException or ArgumentException;

    private async Task RejectReplacementSendAsync(ObstacleRecoveryEntity row, IReadOnlyList<string> route,
        Exception error, CancellationToken cancellationToken)
    {
        var attempts = _sendRejections.AddOrUpdate(row.RecoveryId, 1, (_, count) => count + 1);
        var wait = TimeSpan.FromSeconds(Math.Min(60,
            options.SendRejectionRetrySeconds * Math.Pow(2, Math.Min(attempts - 1, 10))));
        _sendRetryAfter[row.RecoveryId] = _clock.GetUtcNow().UtcDateTime + wait;
        row.NewVendorTaskId = null;
        row.SendAttemptedAtUtc = null;
        row.State = ObstacleRecoveryStates.DetourReady;
        row.Message = "替代导航发送前被拒绝，未到达车辆，原任务、载荷和路权保持：" + Truncate(error.Message);
        row.NextAction = $"{wait.TotalSeconds:0}秒后重新核验并尝试发送；未发出的命令不会重复发送。";
        row.Revision++;
        await SaveAsync(row, CancellationToken.None);
        await AddEvidenceAsync(row, "ObstacleDetourSendRejected", row.Message,
            new { Route = route, Attempts = attempts, WaitSeconds = wait.TotalSeconds }, CancellationToken.None);
    }

    // Read-only reconciliation of an uncertain replacement. It never sends, never
    // generates a new identity and never releases the route or the original task.
    // The replacement waits for public right-of-way. The state reports a defined timeout
    // after RouteRightWaitTimeoutSeconds, but the wait is still retried. No alarm rule is
    // added here; the original navigation is not touched by this path.
    private async Task HoldForRouteRightsAsync(ObstacleRecoveryEntity row, string? blockingResource,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = _routeWaitSince.GetOrAdd(row.RecoveryId, now);
        var waited = now - since;
        var timedOut = waited >= TimeSpan.FromSeconds(options.RouteRightWaitTimeoutSeconds);
        row.State = timedOut ? ObstacleRecoveryStates.RouteWaitTimeout : ObstacleRecoveryStates.DetourReady;
        row.Message = $"替代路线等待公共路权{blockingResource}，已等待{waited.TotalSeconds:0}秒；原导航保留，尚未发送替代路线。";
        row.NextAction = timedOut
            ? $"公共路权等待超过{options.RouteRightWaitTimeoutSeconds}秒，需人工核对占用车辆与交通队列；仍保留原任务、载荷和路权，后台继续重试。"
            : "保留原任务和已持有路权；按交通队列等待，不绕开安全或公平顺序。";
        row.Revision++;
        await SaveAsync(row, cancellationToken);
    }

    // A blocked start that spans a telemetry gap (or a restart with no in-process history)
    // is not continuous blockage. It moves to the state cache's new blockage start.
    private void RealignBlockedStartAfterGap(ObstacleRecoveryEntity row, AgvRobotSnapshot robot, DateTimeOffset now)
    {
        if (!FreshTelemetry(robot, now)) return;
        var stale = TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds);
        var gap = !_lastFreshSeen.TryGetValue(row.RecoveryId, out var lastFresh) || now - lastFresh >= stale;
        _lastFreshSeen[row.RecoveryId] = now;
        // Once cancellation has been requested the blockage start only paces warnings;
        // moving it would stall the cancel-path flow, so realignment is pre-cancel only.
        if (row.CancelRequestedAtUtc is not null || row.OriginalTerminalAtUtc is not null)
            return;
        if (gap && AgvObstaclePolicy.IsBlocked(robot) && robot.ObstacleSince is { } blockedAgain &&
            blockedAgain > new DateTimeOffset(DateTime.SpecifyKind(row.BlockedSinceUtc, DateTimeKind.Utc)))
            row.BlockedSinceUtc = blockedAgain.UtcDateTime;
    }

    private async Task ReconcileUncertainReplacementAsync(ObstacleRecoveryEntity row, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (row.State != ObstacleRecoveryStates.Uncertain)
        {
            row.State = ObstacleRecoveryStates.Uncertain;
            row.Message = "替代导航发送意图已持久化但未绑定，结果不确定；只读对账，禁止重发。";
            row.NextAction = "按新厂家编号只读查询车端任务链；无法证明时保持不确定，由现场核对。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        // Read-only lookups at most every 30 seconds.
        if (now - new DateTimeOffset(DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc)) < TimeSpan.FromSeconds(30))
            return;
        AgvTransportTaskStatus? status = null;
        string? failure = null;
        try
        {
            status = await gateway.GetTransportTaskStatusAsync(row.NewVendorTaskId!, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            failure = Truncate(error.Message);
        }
        row.Message = failure is not null
            ? "只读对账失败，替代导航结果仍不确定：" + failure
            : status is null || status.State == AgvTransportTaskState.Unknown
                ? "只读对账：车端尚未查到该替代编号，结果仍不确定，禁止重发。"
                : $"只读对账：车端已查到替代编号，状态{status.State}；仍需现场确认后才能绑定，禁止重发。";
        row.NextAction = "按新厂家编号、车辆位置、载荷和PLC核对；无法证明时由操作员处置。";
        row.Revision++;
        await SaveAsync(row, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ResolveOriginalVendorWindowPathAsync(
        string vendorTaskId,
        string vehicleId,
        CancellationToken cancellationToken)
    {
        var chain = await chains.FindAsync(vendorTaskId, cancellationToken);
        if (chain is null ||
            !string.Equals(chain.BusinessTaskId, vendorTaskId, StringComparison.Ordinal) ||
            !string.Equals(chain.VehicleId, vehicleId, StringComparison.OrdinalIgnoreCase) ||
            chain.Path.Count is < 2 or > 2048 ||
            chain.Path.Any(string.IsNullOrWhiteSpace))
            return [];
        return chain.Path;
    }

    private bool AtBusinessDestination(AgvRobotSnapshot robot, DispatchTaskStepEntity step)
    {
        var map = activeMap.Current;
        if (map is null || string.IsNullOrWhiteSpace(robot.CurrentStation) ||
            string.IsNullOrWhiteSpace(step.DestinationSite))
            return false;
        var destination = map.Definition.Nodes.Where(node => node.Enabled &&
            MapRouteSiteMapper.IsSameNode(map.Definition, node.Id, step.DestinationSite)).ToArray();
        return destination.Length == 1 &&
            MapRouteSiteMapper.IsSameNode(
                map.Definition, destination[0].Id, robot.CurrentStation);
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
            plan.Route, dispatchOptions.GetAction((DispatchTaskStepType)step.StepType))
        {
            // The replacement belongs to the original business step; release and
            // handoff identity checks compare this id with the step.
            BusinessStepId = row.StepId
        }, Json);
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

    private async Task ContinueSafetyPointRetreatAsync(
        ObstacleRecoveryEntity row,
        AgvRobotSnapshot robot,
        CancellationToken cancellationToken,
        bool controlHeld = false)
    {
        if (controlHeld)
        {
            await ContinueSafetyPointRetreatCoreAsync(row, robot, cancellationToken);
            return;
        }
        using var control = await executionGate.EnterAsync(MapExecutionOperation.ExistingWork, cancellationToken);
        if (control is null)
        {
            row.State = ObstacleRecoveryStates.SafetyHold;
            row.Message = "地图切换或恢复门禁暂未放行，安全点退回保持待命。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        await ContinueSafetyPointRetreatCoreAsync(row, robot, cancellationToken);
    }

    private async Task ContinueSafetyPointRetreatCoreAsync(
        ObstacleRecoveryEntity row,
        AgvRobotSnapshot robot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(row.SafetyPointTarget))
        {
            await FreezeAsync(row, "安全点退回目标缺失，禁止从当前位姿盲发3051。",
                "核对当前路段、最近后方站点和车辆位置；必要时人工转移。", cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(row.SafetyPointRetreatTaskId))
        {
            var refusal = await SafetyPointRetreatSafetyReasonAsync(
                row, robot, cancellationToken);
            if (refusal is not null)
            {
                row.State = ObstacleRecoveryStates.SafetyHold;
                row.Message = "安全点退回发送前核验未通过：" + refusal;
                row.NextAction =
                    "保持原业务任务、载荷、PLC责任和路权；修正条件后后台自动重试，禁止绕过核验。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
                return;
            }

            var retreat = PlanSafetyPointRetreat(
                robot, row.SafetyPointBlockedSource, row.SafetyPointBlockedTarget);
            if (!retreat.Available ||
                !string.Equals(retreat.TargetSite, row.SafetyPointTarget, StringComparison.OrdinalIgnoreCase))
            {
                row.State = ObstacleRecoveryStates.SafetyHold;
                row.Message = "安全点退回计划已变化：" +
                    (retreat.Available ? "目标站点与持久化记录不一致。" : retreat.Message);
                row.NextAction =
                    "重新核对受阻路段、车辆位置和最近后方安全站；修正前禁止发送3051或3055。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
                return;
            }

            if (_sendRetryAfter.TryGetValue(row.RecoveryId, out var retreatRetryAfter) &&
                _clock.GetUtcNow().UtcDateTime < retreatRetryAfter)
            {
                row.Message = $"上次安全点退回发送前被车端拒绝，{(retreatRetryAfter - _clock.GetUtcNow().UtcDateTime).TotalSeconds:0}秒后重新核验。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
                return;
            }

            var shortTranslation = retreat.RemainingDistanceMeters <= 1.0;
            var retreatMethod = shortTranslation ? "3055-Translation" : "3051-Backward";
            var retreatId = "SAFETYRETURN-" + Guid.NewGuid().ToString("N");
            row.SafetyPointRetreatTaskId = retreatId;
            row.SafetyPointRetreatMethod = retreatMethod;
            row.SafetyPointRetreatAttemptedAtUtc = _clock.GetUtcNow().UtcDateTime;
            row.State = ObstacleRecoveryStates.SafetyPointSendIntent;
            row.Message = shortTranslation
                ? $"已持久化一次3055直线平动后退意图：{retreat.RemainingDistanceMeters:0.00}米 → {row.SafetyPointTarget}。"
                : $"已持久化一次3051低速倒走意图：当前位置 → {row.SafetyPointTarget}。";
            row.NextAction =
                shortTranslation
                    ? "发送后只按实时位置、停稳和站点一致确认；结果未知时禁止重复发送3055。"
                    : "发送后按恢复任务编号查询1110；结果未知时只查询原编号，不允许补发倒退。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            try
            {
                if (gateway is not IAgvSafetyPointRecoveryGateway retreatGateway)
                    throw new NotSupportedException(
                        "当前AGV适配器未启用道路中段安全点退回端口。");
                var sent = await retreatGateway.CreateObstacleSafetyPointRetreatAsync(
                    retreatId, row.VehicleId, row.SafetyPointTarget,
                    row.SafetyPointBlockedSource!,
                    row.SafetyPointBlockedTarget!,
                    options.SafetyPointRetreatMaximumSpeedMetersPerSecond,
                    options.SafetyPointRetreatMaximumAccelerationMetersPerSecondSquared,
                    retreat.RemainingDistanceMeters,
                    new DateTimeOffset(DateTime.SpecifyKind(row.BlockedSinceUtc, DateTimeKind.Utc)),
                    row.OriginalVendorTaskId, PreSendLimits, cancellationToken);
                if (!string.Equals(sent, retreatId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "安全点退回返回任务编号与持久化意图不一致。");
                row.State = ObstacleRecoveryStates.SafetyPointMoving;
                row.Message = shortTranslation
                    ? $"3055直线后退已发送，车辆正在退向{row.SafetyPointTarget}。"
                    : $"3051安全点退回已接收，车辆正在倒向{row.SafetyPointTarget}。";
                row.NextAction =
                    shortTranslation
                        ? "等待实时位置进入目标站点、连续停稳且站点一致；到站前不发送替代业务路线。"
                        : "等待1110终态、车辆到站、连续停稳和站点定位一致；到站前不发送替代业务路线。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
                await AddEvidenceAsync(row, "ObstacleSafetyPointRetreatSent",
                    "原链终态和全部安全门通过后，按距离选择一次3055直线后退或3051隔离倒走。",
                    new
                    {
                        RetreatTaskId = retreatId,
                        Method = retreatMethod,
                        row.SafetyPointTarget,
                        RemainingDistanceMeters = retreat.RemainingDistanceMeters,
                        options.SafetyPointRetreatMaximumSpeedMetersPerSecond,
                        options.SafetyPointRetreatMaximumAccelerationMetersPerSecondSquared,
                        robot.X,
                        robot.Y,
                        robot.SourceStation,
                        robot.TargetStation
                    }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (IsPreSendRejection(error))
            {
                // Refused before any motion command was accepted: clear the retreat
                // intent so the gated pass may send again after the backoff.
                var attempts = _sendRejections.AddOrUpdate(row.RecoveryId, 1, (_, count) => count + 1);
                var wait = TimeSpan.FromSeconds(Math.Min(60,
                    options.SendRejectionRetrySeconds * Math.Pow(2, Math.Min(attempts - 1, 10))));
                _sendRetryAfter[row.RecoveryId] = _clock.GetUtcNow().UtcDateTime + wait;
                row.SafetyPointRetreatTaskId = null;
                row.SafetyPointRetreatMethod = null;
                row.SafetyPointRetreatAttemptedAtUtc = null;
                row.State = ObstacleRecoveryStates.SafetyHold;
                row.Message = "安全点退回发送前被拒绝，未到达车辆：" + Truncate(error.Message);
                row.NextAction = $"{wait.TotalSeconds:0}秒后重新核验；修正门禁条件前禁止绕过核验或重复发送。";
                row.Revision++;
                await SaveAsync(row, CancellationToken.None);
                await AddEvidenceAsync(row, "ObstacleSafetyPointRetreatRejected", row.Message,
                    new { Attempts = attempts, WaitSeconds = wait.TotalSeconds }, CancellationToken.None);
            }
            catch (Exception error)
            {
                row.State = ObstacleRecoveryStates.Uncertain;
                row.Message = "安全点退回发送结果不确定，只查询并禁止重发：" +
                    Truncate(error.Message);
                row.NextAction =
                    $"按恢复编号{retreatId}和车辆实际位置核对；不得再次发送{retreatMethod}、取消业务任务或释放路权。";
                row.Revision++;
                await SaveAsync(row, CancellationToken.None);
                await AddEvidenceAsync(row, "ObstacleSafetyPointRetreatUncertain",
                    row.Message, new { RetreatTaskId = retreatId, row.SafetyPointTarget },
                    CancellationToken.None);
            }
            return;
        }

        if (string.Equals(row.SafetyPointRetreatMethod, "3055-Translation", StringComparison.Ordinal))
        {
            if (row.State is ObstacleRecoveryStates.Uncertain or ObstacleRecoveryStates.SafetyHold)
                return;
            if (AtSafetyPoint(robot, row.SafetyPointTarget) &&
                StationaryWithGoodPosition(robot))
            {
                row.SafetyPointReachedAtUtc = _clock.GetUtcNow().UtcDateTime;
                row.State = ObstacleRecoveryStates.SafetyPointReached;
                row.Message = $"车辆已通过3055直线后退到安全站{row.SafetyPointTarget}并停稳。";
                row.NextAction =
                    "按新起点重新规划原目的站；原业务任务、载荷、PLC责任和路权继续保留。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
                await AddEvidenceAsync(row, "ObstacleSafetyPointReached",
                    row.Message,
                    new { robot.X, robot.Y, robot.CurrentStation, robot.LocalizationConfidence },
                    cancellationToken);
                return;
            }

            var started = row.SafetyPointRetreatAttemptedAtUtc is { } value
                ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
                : _clock.GetUtcNow();
            var elapsed = _clock.GetUtcNow() - started;
            if (robot.Connection == ConnectionState.Online &&
                elapsed.TotalSeconds > options.WarningAfterSeconds)
            {
                await FreezeAsync(row,
                    $"3055直线后退超过{options.WarningAfterSeconds}秒，车辆仍未在{row.SafetyPointTarget}形成可信停稳。",
                    "只核对车辆实时位置、障碍和当前站点；禁止重复发送3055，必要时人工安全转移。",
                    cancellationToken);
                return;
            }

            row.State = ObstacleRecoveryStates.SafetyPointMoving;
            row.Message =
                $"3055直线后退中，车辆正在退向{row.SafetyPointTarget}；尚未确认到站停稳。";
            row.NextAction =
                "持续核对实时位置、速度、定位和站点；到站前不发送替代业务路线。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }

        AgvTransportTaskStatus? status;
        try
        {
            status = await gateway.GetTransportTaskStatusAsync(
                row.SafetyPointRetreatTaskId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            row.State = ObstacleRecoveryStates.Uncertain;
            row.Message = "查询3051安全点退回状态失败，不重发：" + Truncate(error.Message);
            row.NextAction =
                "通信恢复后只查询原恢复任务编号和车辆位置；保持原业务责任与路权。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }

        if (status is null || status.State == AgvTransportTaskState.Unknown)
        {
            row.State = ObstacleRecoveryStates.Uncertain;
            row.Message = "3051安全点退回任务状态未知。";
            row.NextAction =
                "只查询原恢复编号；未证明终态前不得发送新的3051或3066。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            return;
        }
        if (status.State is AgvTransportTaskState.Failed or AgvTransportTaskState.Cancelled)
        {
            await FreezeAsync(row,
                $"3051安全点退回未完成：状态{status.State}。",
                "核对车辆实际位置、障碍、定位和载荷；禁止重发原3051，必要时人工安全转移。",
                cancellationToken);
            return;
        }
        if (status.State == AgvTransportTaskState.Completed)
        {
            if (!AtSafetyPoint(robot, row.SafetyPointTarget) ||
                !StationaryWithGoodPosition(robot))
            {
                row.State = ObstacleRecoveryStates.SafetyPointMoving;
                row.Message =
                    $"3051任务已完成，但车辆尚未在{row.SafetyPointTarget}形成连续可信停稳。";
                row.NextAction =
                    "继续核对实时坐标、站点、速度、定位质量；未确认到站前不发送替代业务路线。";
                row.Revision++;
                await SaveAsync(row, cancellationToken);
                return;
            }

            row.SafetyPointReachedAtUtc = _clock.GetUtcNow().UtcDateTime;
            row.State = ObstacleRecoveryStates.SafetyPointReached;
            row.Message = $"车辆已退到安全站{row.SafetyPointTarget}并停稳。";
            row.NextAction =
                "按新起点重新规划原目的站；原业务任务、载荷、PLC责任和路权继续保留。";
            row.Revision++;
            await SaveAsync(row, cancellationToken);
            await AddEvidenceAsync(row, "ObstacleSafetyPointReached",
                row.Message,
                new { robot.X, robot.Y, robot.CurrentStation, robot.LocalizationConfidence },
                cancellationToken);
            return;
        }

        row.State = ObstacleRecoveryStates.SafetyPointMoving;
        row.Message = $"3051安全点退回归任务状态为{status.State}，车辆继续退向{row.SafetyPointTarget}。";
        row.NextAction =
            "保持原业务任务和责任；持续读取原恢复编号，不发送第二条运动命令。";
        row.Revision++;
        await SaveAsync(row, cancellationToken);
    }

    private async Task<string?> SafetyPointRetreatSafetyReasonAsync(
        ObstacleRecoveryEntity row,
        AgvRobotSnapshot robot,
        CancellationToken cancellationToken)
    {
        if (executionGate.Mode != MapExecutionMode.Running || !executionGate.CanStartNewWork)
            return executionGate.NewWorkBlockReason ?? "地图执行门禁未放行。";
        if (string.IsNullOrWhiteSpace(row.SafetyPointTarget))
            return "后方安全站点未唯一确定。";
        if (!FreshTelemetry(robot, _clock.GetUtcNow()))
            return "车辆通信或遥测不新鲜。";
        if (robot.EmergencyStopped != false || robot.Charging == true ||
            AgvObstaclePolicy.HasBlockingAlarm(robot))
            return "急停、充电或非局部避障严重报警优先。";
        if ((row.CancelRequestedAtUtc is not null || row.OriginalTerminalAtUtc is not null) &&
            robot.Tasks.Any(task => task.Status is 1 or 2 or 3))
            return "车端仍有已接收、执行中或暂停的任务，必须先核清原任务链。";
        if (robot.LocalizationConfidence is not { } confidence ||
            !double.IsFinite(confidence) || confidence < options.MinimumLocalizationConfidence)
            return "车辆定位置信度不足。";
        if (!StationaryWithGoodPosition(robot))
            return "车辆尚未形成可信停稳证据。";
        var retreat = PlanSafetyPointRetreat(
            robot, row.SafetyPointBlockedSource, row.SafetyPointBlockedTarget);
        if (!retreat.Available)
            return retreat.Message;
        if (!string.Equals(retreat.TargetSite, row.SafetyPointTarget,
                StringComparison.OrdinalIgnoreCase))
            return "当前实际路段对应的后方安全站已变化，禁止沿用旧退站目标。";
        var envelope = await envelopes.ReadAsync(row.VehicleId, cancellationToken);
        if (envelope is null || !envelope.CalibrationVerified ||
            !envelope.IncludesMaximumPermittedLoad ||
            envelope.RadiusFromReportedReferenceMeters <= 0)
            return "缺少已标定且包含最大载箱尺寸的车辆包络。";
        var stopKey = $"RETREAT|{retreat.BlockedSource}|{retreat.BlockedTarget}|{retreat.TargetSite}";
        if (!StableStopped(row, stopKey, _clock.GetUtcNow()))
            return StableStopReason(row, options.StableStopObservationMs);
        return await timeoutRecovery.ValidateReplacementPreconditionsAsync(
            row.TaskId, row.StepId, cancellationToken);
    }

    private SafetyPointRetreatAssessment PlanSafetyPointRetreat(
        AgvRobotSnapshot robot,
        string? persistedSource = null,
        string? persistedTarget = null)
    {
        var map = activeMap.Current;
        if (map is null)
            return SafetyPointRetreatAssessment.Refuse("当前运行地图不可用。");
        if (robot.X is not { } x || !double.IsFinite(x) ||
            robot.Y is not { } y || !double.IsFinite(y))
            return SafetyPointRetreatAssessment.Refuse("车辆实时坐标不可用。");
        var sourceName = !string.IsNullOrWhiteSpace(persistedSource)
            ? persistedSource
            : robot.SourceStation;
        var targetName = !string.IsNullOrWhiteSpace(persistedTarget)
            ? persistedTarget
            : robot.TargetStation;
        if (string.IsNullOrWhiteSpace(sourceName) ||
            string.IsNullOrWhiteSpace(targetName))
            return SafetyPointRetreatAssessment.Refuse(
                "1110没有同时返回当前路段起点和终点，无法确定后方安全站。");
        if (!string.IsNullOrWhiteSpace(robot.SourceStation) &&
            !string.Equals(robot.SourceStation, sourceName, StringComparison.OrdinalIgnoreCase))
            return SafetyPointRetreatAssessment.Refuse(
                "实时1110路段起点与先前持久化的受阻路段不一致。");
        if (!string.IsNullOrWhiteSpace(robot.TargetStation) &&
            !string.Equals(robot.TargetStation, targetName, StringComparison.OrdinalIgnoreCase))
            return SafetyPointRetreatAssessment.Refuse(
                "实时1110路段终点与先前持久化的受阻路段不一致。");
        var sourceMatches = map.Definition.Nodes.Where(node => node.Enabled &&
            MapRouteSiteMapper.IsSameNode(map.Definition, node.Id, sourceName)).ToArray();
        var targetMatches = map.Definition.Nodes.Where(node => node.Enabled &&
            MapRouteSiteMapper.IsSameNode(map.Definition, node.Id, targetName)).ToArray();
        if (sourceMatches.Length != 1 || targetMatches.Length != 1)
            return SafetyPointRetreatAssessment.Refuse(
                "当前路段起终点在运行地图中不是唯一有效站点。");
        var source = sourceMatches[0];
        var target = targetMatches[0];
        var edge = map.Definition.Edges.FirstOrDefault(item =>
            item.Enabled &&
            string.Equals(item.From, source.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.To, target.Id, StringComparison.OrdinalIgnoreCase) &&
            MapEdgeGeometry.TryValidate(item, source, target, out _));
        if (edge is null)
            return SafetyPointRetreatAssessment.Refuse(
                "当前1110路段与运行地图的有向道路不一致。");
        var lateral = MapEdgeGeometry.DistanceTo(edge, source, target, x, y);
        if (lateral > options.MaximumStationDeviationMeters)
            return SafetyPointRetreatAssessment.Refuse(
                $"车辆实时位置偏离当前路段{lateral:0.00}米，不能发送自动倒走。");
        var points = MapEdgeGeometry.Flatten(edge, source, target, 0.01);
        var nearest = NearestPolylineProgress(points, x, y);
        // Flatten runs from the source (rear safe point) to the target.
        // Distance back to the source is progress, not distance to the target.
        var remaining = Math.Max(0, nearest.AlongMeters);
        if (remaining <= options.MaximumStationDeviationMeters)
            return SafetyPointRetreatAssessment.Refuse(
                "车辆已经接近后方站点，应先按可信站点路径重新核验，无需自动倒走。");
        if (remaining > options.SafetyPointMaximumRetreatDistanceMeters)
            return SafetyPointRetreatAssessment.Refuse(
                $"后方安全站{source.AgvSiteId}约{remaining:0.00}米，超过自动倒走上限{options.SafetyPointMaximumRetreatDistanceMeters:0.00}米。");
        if (remaining <= 1.0)
        {
            // 3055 vx is in the vehicle frame and does not follow a map curve.
            if (edge.GeometryKind != MapEdgeGeometryKind.Line ||
                robot.Angle is not { } heading || !double.IsFinite(heading))
                return SafetyPointRetreatAssessment.Refuse(
                    "3055短退要求直线路段和可信车头朝向，弯道或朝向未知时需人工处理。");
            var forward = Math.Atan2(target.Y - source.Y, target.X - source.X);
            var headingError = Math.Abs(Math.Atan2(Math.Sin(heading - forward), Math.Cos(heading - forward)));
            if (headingError > 0.1 || lateral > 0.05)
                return SafetyPointRetreatAssessment.Refuse(
                    "车头朝向或横向偏差不满足沿当前直边后退条件，禁止直接发送3055。");
        }
        return new(true, "",
            source.AgvSiteId ?? source.Id, source.AgvSiteId ?? source.Id,
            target.AgvSiteId ?? target.Id, edge.Id, remaining);
    }

    private bool IsAtTrustedStation(AgvRobotSnapshot robot)
    {
        var map = activeMap.Current;
        if (map is null || string.IsNullOrWhiteSpace(robot.CurrentStation) ||
            robot.X is not { } x || robot.Y is not { } y ||
            !double.IsFinite(x) || !double.IsFinite(y))
            return false;
        var stations = map.Definition.Nodes.Where(node => node.Enabled &&
            MapRouteSiteMapper.IsSameNode(map.Definition, node.Id, robot.CurrentStation)).ToArray();
        return stations.Length == 1 &&
            Math.Sqrt(Math.Pow(x - stations[0].X, 2) + Math.Pow(y - stations[0].Y, 2)) <=
            options.MaximumStationDeviationMeters;
    }

    private bool AtSafetyPoint(AgvRobotSnapshot robot, string targetSite)
    {
        var map = activeMap.Current;
        if (map is null || robot.X is not { } x || robot.Y is not { } y ||
            !double.IsFinite(x) || !double.IsFinite(y))
            return false;
        var stations = map.Definition.Nodes.Where(node => node.Enabled &&
            MapRouteSiteMapper.IsSameNode(map.Definition, node.Id, targetSite)).ToArray();
        return stations.Length == 1 &&
            string.Equals(
                MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, robot.CurrentStation),
                stations[0].Id, StringComparison.OrdinalIgnoreCase) &&
            Math.Sqrt(Math.Pow(x - stations[0].X, 2) + Math.Pow(y - stations[0].Y, 2)) <=
            options.MaximumStationDeviationMeters;
    }

    private bool StationaryWithGoodPosition(AgvRobotSnapshot robot)
    {
        if (robot.LocalizationConfidence is not { } confidence ||
            !double.IsFinite(confidence) || confidence < options.MinimumLocalizationConfidence ||
            robot.VelocityX is not { } vx || !double.IsFinite(vx) ||
            Math.Abs(vx) > options.StoppedVelocityToleranceMetersPerSecond ||
            robot.VelocityY is not { } vy || !double.IsFinite(vy) ||
            Math.Abs(vy) > options.StoppedVelocityToleranceMetersPerSecond ||
            robot.AngularVelocity is not { } w || !double.IsFinite(w) ||
            Math.Abs(w) > options.StoppedVelocityToleranceMetersPerSecond)
            return false;
        return true;
    }

    private static (double AlongMeters, double LateralMeters) NearestPolylineProgress(
        IReadOnlyList<MapPoint2D> points,
        double x,
        double y)
    {
        var best = (Along: 0d, Lateral: double.PositiveInfinity);
        var accumulated = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            var a = points[index - 1];
            var b = points[index];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-12) continue;
            var t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / (length * length), 0, 1);
            var px = a.X + dx * t;
            var py = a.Y + dy * t;
            var lateral = Math.Sqrt(Math.Pow(x - px, 2) + Math.Pow(y - py, 2));
            if (lateral < best.Lateral)
                best = (accumulated + length * t, lateral);
            accumulated += length;
        }
        return best;
    }

    private static double PolylineLength(IReadOnlyList<MapPoint2D> points) =>
        Enumerable.Range(1, Math.Max(0, points.Count - 1))
            .Sum(index => Math.Sqrt(
                Math.Pow(points[index].X - points[index - 1].X, 2) +
                Math.Pow(points[index].Y - points[index - 1].Y, 2)));

    private async Task<RouteAssessment> PlanAsync(
        ObstacleRecoveryEntity row,
        AgvRobotSnapshot robot,
        DispatchTaskEntity task,
        DispatchTaskStepEntity step,
        IReadOnlyList<string> originalPath,
        CancellationToken cancellationToken,
        bool excludeInferredBlocked = true,
        bool allowOriginalRoute = false)
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
            MapRouteSiteMapper.IsSameNode(map.Definition, node.Id, robot.CurrentStation)).ToArray();
        var destination = map.Definition.Nodes.Where(node => node.Enabled &&
            !string.IsNullOrWhiteSpace(step.DestinationSite) &&
            MapRouteSiteMapper.IsSameNode(map.Definition, node.Id, step.DestinationSite)).ToArray();
        if (current.Length != 1 || destination.Length != 1)
            return RouteAssessment.Refuse("当前站点或原任务目的站在运行地图中不是唯一有效点。");
        var positions = originalPath.Select((site, index) => (site, index))
            .Where(item => MapRouteSiteMapper.IsSameNode(
                map.Definition, item.site, robot.CurrentStation))
            .ToArray();
        if (positions.Length != 1 || positions[0].index + 1 >= originalPath.Count)
            return RouteAssessment.Refuse("车辆当前点不在原路线中段，无法唯一确定实际受阻下一段。");
        var nextSite = originalPath[positions[0].index + 1];
        var next = MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, nextSite);
        var blockedEdges = Deserialize(row.BlockedEdgeIdsJson).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (excludeInferredBlocked)
        {
            var inferred = map.Definition.Edges.Where(edge => edge.Enabled &&
                SupportsDirection(edge, current[0].Id, next)).ToArray();
            if (inferred.Length == 0)
                return RouteAssessment.Refuse("原车端地图在后台工程图中没有对应前向道路。");
            blockedEdges.UnionWith(inferred.Select(edge => edge.Id));

            if (AgvObstaclePolicy.IsBlocked(robot) &&
                (!string.IsNullOrWhiteSpace(robot.SourceStation) || !string.IsNullOrWhiteSpace(robot.TargetStation)))
            {
                var reportedTarget = MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, robot.TargetStation);
                var reported = map.Definition.Edges.Where(edge => edge.Enabled &&
                    SupportsDirection(edge, current[0].Id, reportedTarget)).ToArray();
                if (!MapRouteSiteMapper.IsSameNode(map.Definition, current[0].Id, robot.SourceStation) ||
                    !map.Definition.Nodes.Any(node => node.Enabled &&
                        node.Id.Equals(reportedTarget, StringComparison.OrdinalIgnoreCase)) ||
                    reported.Length == 0)
                    return RouteAssessment.Refuse("车端受阻路段与当前可信站点或运行地图不一致，暂不自动改道。");
                blockedEdges.UnionWith(reported.Select(edge => edge.Id));
            }
        }
        else
            blockedEdges.Clear();

        var blockedTraversals = map.Definition.Edges.Where(edge => edge.Enabled && blockedEdges.Contains(edge.Id))
            .SelectMany(edge => edge.Bidirectional
                ? new[]
                {
                    new MapDirectedTraversal(edge.Id, edge.From, edge.To),
                    new MapDirectedTraversal(edge.Id, edge.To, edge.From)
                }
                : new[] { new MapDirectedTraversal(edge.Id, edge.From, edge.To) });
        var unavailableTraversals = blockedTraversals
            .Concat(closures.BlockedTraversals)
            .Distinct()
            .ToArray();
        var unavailableResources = await traffic.GetUnavailableResourceIdsAsync(task.TaskId, cancellationToken);
        var protectedNodes = dispatchOptions.GetDumperWaitingSites()
            .Append(dispatchOptions.DumperDumpingSite)
            .Concat(dispatchOptions.StandbySites)
            .Where(site => !string.IsNullOrWhiteSpace(site))
            .Select(site => MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, site))
            .Where(id => !map.Definition.Nodes.Any(node =>
                node.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && node.AllowTransitThrough))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Never route a recovery command through the currently reported site of
        // another vehicle. Traffic resource leases are not enough here: an idle
        // or offline vehicle still has a physical body occupying that station.
        var occupiedByOtherVehicles = vehicles.GetAll()
            .Where(vehicle => !string.Equals(vehicle.VehicleId, row.VehicleId,
                StringComparison.OrdinalIgnoreCase))
            .Where(vehicle => !string.IsNullOrWhiteSpace(vehicle.CurrentStation))
            .Select(vehicle => MapRouteSiteMapper.ResolveLogicalNodeId(
                map.Definition, vehicle.CurrentStation))
            .Where(id => map.Definition.Nodes.Any(node =>
                node.Enabled && node.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        protectedNodes.UnionWith(occupiedByOtherVehicles);
        var route = map.Graph.FindShortestTransportRoute(current[0].Id, destination[0].Id,
            unavailableResources, null, protectedNodes, unavailableTraversals);
        var safeSite = await RecommendSafeSiteAsync(map, current[0].Id, unavailableResources,
            unavailableTraversals, protectedNodes, cancellationToken);
        if (route is null)
            return new(false, $"已推断受阻道路为{robot.CurrentStation}→{nextSite}，当前路网没有安全替代路线。",
                [], blockedEdges.ToArray(), safeSite);
        var sitePlan = MapRouteSiteMapper.ToAgvSitePlan(map.Definition, route);
        if (sitePlan.Nodes.Count < 2 ||
            !MapRouteSiteMapper.IsSameNode(map.Definition, sitePlan.Nodes[0], robot.CurrentStation) ||
            !MapRouteSiteMapper.IsSameNode(map.Definition, sitePlan.Nodes[^1], step.DestinationSite) ||
            sitePlan.Nodes.Zip(sitePlan.Nodes.Skip(1)).Any(pair => unavailableTraversals.Any(traversal =>
                MapRouteSiteMapper.IsSameNode(map.Definition, traversal.FromNodeId, pair.First) &&
                MapRouteSiteMapper.IsSameNode(map.Definition, traversal.ToNodeId, pair.Second))) ||
            (!allowOriginalRoute &&
             sitePlan.Nodes.SequenceEqual(originalPath, StringComparer.OrdinalIgnoreCase)))
            return new(false, "规划结果与实际起点、原目的站或避障要求不一致。",
                sitePlan.Nodes, blockedEdges.ToArray(), safeSite);
        return new(true, $"已形成替代路线：{string.Join("→", sitePlan.Nodes)}。",
            sitePlan.Nodes, blockedEdges.ToArray(), safeSite)
        {
            Resources = route.ResourceIds,
            EdgeIds = route.EdgeIds
        };
    }

    private static bool SupportsDirection(MapEdge edge, string source, string target) =>
        string.Equals(edge.From, source, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(edge.To, target, StringComparison.OrdinalIgnoreCase) ||
        edge.Bidirectional &&
        string.Equals(edge.To, source, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(edge.From, target, StringComparison.OrdinalIgnoreCase);

    private async Task<string?> AutomaticSafetyReasonAsync(
        ObstacleRecoveryEntity row,
        AgvRobotSnapshot robot,
        IReadOnlyList<string> candidate,
        CancellationToken cancellationToken)
    {
        if (executionGate.Mode != MapExecutionMode.Running || !executionGate.CanStartNewWork)
            return executionGate.NewWorkBlockReason ?? "地图执行门禁未放行。";
        if (row.OriginalTerminalAtUtc is not null &&
            robot.Tasks.Any(task => task.Status is 1 or 2 or 3))
            return "车端仍有活动任务，禁止在原任务终态核验后并发发送新导航。";
        if (candidate.Count < 2 || !MapRouteSiteMapper.IsSameNode(
                activeMap.Current?.Definition, candidate[0], robot.CurrentStation))
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
            !double.IsFinite(confidence) || confidence < options.MinimumLocalizationConfidence)
            return "车辆定位置信度不足。";
        var map = activeMap.Current;
        var station = map?.Definition.Nodes.Where(node => node.Enabled &&
            MapRouteSiteMapper.IsSameNode(map.Definition, node.Id, robot.CurrentStation)).ToArray();
        if (map is null || station is not { Length: 1 } ||
            robot.X is not { } x || !double.IsFinite(x) ||
            robot.Y is not { } y || !double.IsFinite(y) ||
            Math.Sqrt(Math.Pow(x - station[0].X, 2) + Math.Pow(y - station[0].Y, 2)) >
                options.MaximumStationDeviationMeters)
            return "车辆不在可信命名站点，普通替代3066不得冒用历史站点起点；请使用独立安全点退回流程或人工核验。";
        var tolerance = options.StoppedVelocityToleranceMetersPerSecond;
        if (robot.VelocityX is not { } vx || !double.IsFinite(vx) || Math.Abs(vx) > tolerance ||
            robot.VelocityY is not { } vy || !double.IsFinite(vy) || Math.Abs(vy) > tolerance ||
            robot.AngularVelocity is not { } w || !double.IsFinite(w) || Math.Abs(w) > tolerance)
            return "车辆尚未形成可信停稳证据。";
        var envelope = await envelopes.ReadAsync(row.VehicleId, cancellationToken);
        if (envelope is null || !envelope.CalibrationVerified || !envelope.IncludesMaximumPermittedLoad ||
            envelope.RadiusFromReportedReferenceMeters <= 0)
            return "缺少已标定且包含最大载箱尺寸的车辆包络。";
        if (!StableStopped(row, robot.CurrentStation, _clock.GetUtcNow()))
            return StableStopReason(row, options.StableStopObservationMs);
        return await timeoutRecovery.ValidateReplacementPreconditionsAsync(
            row.TaskId, row.StepId, cancellationToken);
    }

    // Stop evidence is read from the state cache, not from the coordinator's
    // wall-clock poll cadence. The newest fresh, stationary frame starts or extends
    // a run. A run continues only while the cache's previous frame lies at or after
    // the last counted frame and is itself fresh, consecutive and stationary, so a
    // gap or a moving frame restarts it. Spacing is on the telemetry timeline, so a
    // simulation rate scales frames and window alike. The run counts as stopped after
    // two counted frames spanning at least the observation window.
    private bool StableStopped(ObstacleRecoveryEntity row, string? key, DateTimeOffset now)
    {
        var consecutive = vehicles.TryGetConsecutiveTelemetry(row.VehicleId, out var previous, out var latest);
        if (string.IsNullOrWhiteSpace(key) || latest is null ||
            latest.LastSuccessfulPollAt is not { } later || later > now ||
            !StationaryWithGoodPosition(latest))
        {
            ClearStableStop(row);
            return false;
        }
        var last = row.StableStopLastObservedAtUtc is { } lastValue
            ? new DateTimeOffset(DateTime.SpecifyKind(lastValue, DateTimeKind.Utc))
            : (DateTimeOffset?)null;
        var since = row.StableStopSinceUtc is { } sinceValue
            ? new DateTimeOffset(DateTime.SpecifyKind(sinceValue, DateTimeKind.Utc))
            : (DateTimeOffset?)null;
        var sameRun = since is not null && last is not null &&
                      string.Equals(row.StableStopKey, key, StringComparison.OrdinalIgnoreCase);
        var window = TimeSpan.FromMilliseconds(options.StableStopObservationMs);
        // Re-reading a counted frame adds no evidence.
        if (sameRun && later == last)
            return row.StableStopSamples >= 2 && later - since!.Value >= window;
        var contiguous = consecutive && sameRun && previous is not null &&
                         previous.LastSuccessfulPollAt is { } earlier &&
                         StationaryWithGoodPosition(previous) && earlier >= last!.Value && earlier < later;
        if (!contiguous)
        {
            row.StableStopKey = key;
            row.StableStopSinceUtc = later.UtcDateTime;
            row.StableStopLastObservedAtUtc = later.UtcDateTime;
            row.StableStopSamples = 1;
            return false;
        }
        row.StableStopLastObservedAtUtc = later.UtcDateTime;
        if (row.StableStopSamples < int.MaxValue)
            row.StableStopSamples++;
        return row.StableStopSamples >= 2 && later - since!.Value >= window;
    }

    private string StableStopReason(ObstacleRecoveryEntity row, int targetMilliseconds)
    {
        if (row.StableStopSinceUtc is not { } value)
            return $"尚未取得连续新鲜且静止的车端遥测，停稳观察需不少于{targetMilliseconds}毫秒（业务时钟）。";
        var since = new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        var elapsed = Math.Max(0, (_clock.GetUtcNow() - since).TotalMilliseconds);
        return $"停稳观察{elapsed:0}毫秒（业务时钟，已取得{row.StableStopSamples}个连续遥测帧），需不少于{targetMilliseconds}毫秒。";
    }

    private static void ClearStableStop(ObstacleRecoveryEntity row)
    {
        row.StableStopKey = null;
        row.StableStopSinceUtc = null;
        row.StableStopSamples = 0;
        row.StableStopLastObservedAtUtc = null;
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

    private string NoRouteNextAction(string state, double elapsed, string? safeSite)
    {
        var closure = elapsed >= options.ClosureSuggestionAfterSeconds
            ? $"已超过{options.ClosureSuggestionAfterSeconds}秒封路建议时间，请人工核对封路（只封锁实际受阻方向及必要上游汇入，不自动封路）；"
            : "";
        return state switch
        {
            ObstacleRecoveryStates.Alarm =>
                $"持续受阻{elapsed:0}秒；保持原任务、载荷和责任。{closure}不应暂停无关任务。",
            ObstacleRecoveryStates.Warning =>
                $"持续受阻{elapsed:0}秒；继续搜索安全替代路线，并检查车端定位、阻塞范围和邻近车辆。",
            _ =>
                $"未找到安全替代路线；继续保留原任务。{(string.IsNullOrWhiteSpace(safeSite) ? "" : $"推荐安全点{safeSite}。")}"
        };
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
        _clearSince.TryRemove(row.RecoveryId, out _);
        _blockedAfterTerminal.TryRemove(row.RecoveryId, out _);
        _routeWaitSince.TryRemove(row.RecoveryId, out _);
        row.State = ObstacleRecoveryStates.Recovered;
        row.Message = message;
        row.NextAction = "按原任务后续车端状态、PLC反馈、载荷与路权交接继续核对。";
        ClearStableStop(row);
        row.Revision++;
        await SaveAsync(row, cancellationToken);
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

    private string CancellationWaitAction(ObstacleRecoveryEntity row)
    {
        var since = row.CancelRequestedAtUtc;
        var seconds = since is null ? 0 : Math.Max(0, (_clock.GetUtcNow().UtcDateTime - since.Value).TotalSeconds);
        return since is not null && seconds >= options.CancelTerminalWaitWarningSeconds
            ? $"自取消意图起已等待{seconds:0}秒，超过{options.CancelTerminalWaitWarningSeconds}秒提醒线；请核对控制器原任务链。继续只读等待，不重发取消或导航，不释放载荷、PLC和路权。"
            : "继续只读等待原全部分段终态；保留原业务任务、载荷和路权，不重发取消或导航。";
    }

    private async Task RecordOriginalTerminalAsync(ObstacleRecoveryEntity row,
        AgvTransportTaskStatus original, CancellationToken cancellationToken)
    {
        if (row.OriginalTerminalAtUtc is not null) return;
        row.OriginalTerminalAtUtc = _clock.GetUtcNow().UtcDateTime;
        ClearStableStop(row);
        var result = await database.Ado.UseTranAsync(async () =>
        {
            await SaveAsync(row, cancellationToken);
            await AddEvidenceAsync(row, "ObstacleOriginalTerminalObserved",
                "只读查询已观察到原任务全链终态；仍须独立核验连续停稳及其他安全条件。",
                new { row.CancelRequestedAtUtc, row.CancelAttemptedAtUtc, row.CancelAcknowledgedAtUtc,
                    row.OriginalTerminalAtUtc, Status = original }, cancellationToken);
        });
        if (!result.IsSuccess)
        {
            row.OriginalTerminalAtUtc = null;
            throw new InvalidOperationException("原导航终态和审计未同时保存，继续保持恢复核对。", result.ErrorException);
        }
    }

    private async Task SaveAsync(ObstacleRecoveryEntity row, CancellationToken cancellationToken)
    {
        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await store.SaveAsync(row, cancellationToken);
    }

    private static string SerializeEvidence(ObstacleRecoveryEntity row, object evidence)
    {
        var data = JsonSerializer.SerializeToNode(evidence, Json)!.AsObject();
        data["RecoveryId"] = row.RecoveryId;
        data["VehicleId"] = row.VehicleId;
        data["OriginalVendorTaskId"] = row.OriginalVendorTaskId;
        data["BlockedSinceUtc"] = JsonSerializer.SerializeToNode(row.BlockedSinceUtc, Json);
        return data.ToJsonString(Json);
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
            Evidence = SerializeEvidence(row, evidence),
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

    private sealed record SafetyPointRetreatAssessment(
        bool Available,
        string Message,
        string TargetSite,
        string BlockedSource,
        string BlockedTarget,
        string BlockedEdgeId,
        double RemainingDistanceMeters)
    {
        public static SafetyPointRetreatAssessment Refuse(string message) =>
            new(false, message, "", "", "", "", 0);
    }

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
