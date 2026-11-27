using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Application.Runtime;

namespace AgvDispatch.Service;

public sealed class DispatchTaskStepCoordinator(
    ILogger<DispatchTaskStepCoordinator> logger,
    DispatchTaskStepOptions options,
    DispatchOrchestratorOptions dispatchOptions,
    DispatchTaskCreationOptions creationOptions,
    IDispatchOrchestrationRepository tasks,
    IAgvCommandOutboxRepository outbox,
    IPlcSignalCache signals,
    IPlcGateway plcGateway,
    IRoutePlanner routePlanner,
    ITrafficResourceManager trafficResources,
    IMapExecutionGate executionGate,
    IExecutionEvidenceJournal? journal = null,
    IAgvStateCache? vehicleStates = null, IActiveMapRuntime? activeMap = null, PersistentHandshakeExecutor? handshake = null,
    IDispatchPreSendSafetyService? preSendSafety = null, DispatchTrafficReservationService? trafficLayer = null,
    IDispatchPipelineMonitor? pipeline = null, DispatchOptimizationService? optimization = null, MapOptions? mapOptions = null,
    AgvOptions? agvOptions = null, GlobalPickupRoutePlanner? pickupStaging = null,
    DispatchTrafficWakeup? trafficWakeup = null, TimeProvider? clock = null) : IsolatedBackgroundService
{
    private readonly TimeProvider _clock = clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider;
    private TimeSpan BusinessDelay(int milliseconds) => _clock is SimulationTimeProvider simulation
        ? simulation.ScaleForBusiness(TimeSpan.FromMilliseconds(milliseconds))
        : TimeSpan.FromMilliseconds(milliseconds);
    private readonly DispatchTrafficReservationService _trafficLayer = trafficLayer ?? new(trafficResources, pipeline, tasks, optimization);
    private readonly Dictionary<string, string> _conditionFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observedSteps = new(StringComparer.Ordinal);
    private GlobalPickupRoutePlanner? RollingRoutes => optimization?.Options.Mode.Equals("Global", StringComparison.OrdinalIgnoreCase) == true &&
        pickupStaging is { SupportsRollingRoutes: true } ? pickupStaging : null;

    private string[] FleetVehicleIds() => vehicleStates?.GetAll().Select(vehicle => vehicle.VehicleId).ToArray() ?? [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !dispatchOptions.Enabled)
        {
            logger.LogInformation("任务步骤协调器未启用；DispatchTaskSteps和Dispatching必须同时开启。");
            return;
        }

        if (options.PollIntervalMs < 100 || options.BatchSize < 1)
        {
            throw new InvalidOperationException("DispatchTaskSteps配置无效。");
        }
        if (options.RollingWindowMinimumEdges < 1 ||
            options.RollingWindowMaximumEdges < options.RollingWindowMinimumEdges ||
            options.RollingWindowMaximumEdges > 1000 ||
            options.RollingWindowHardMaximumEdges is < 0 or > 1000 ||
            options.RollingWindowHardMaximumEdges > 0 &&
                options.RollingWindowHardMaximumEdges < options.RollingWindowMaximumEdges ||
            !RollingWindowBoundaries.IsKnown(options.RollingWindowBoundary))
            throw new InvalidOperationException("滚动路权窗口边数配置无效。");

        var configurationError = dispatchOptions.GetConfigurationError();
        if (configurationError is not null)
        {
            throw new InvalidOperationException(configurationError);
        }

        logger.LogInformation(
            "任务步骤协调器启动。周期={PollIntervalMs}ms，批量={BatchSize}",
            options.PollIntervalMs,
            options.BatchSize);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var execution = await executionGate.EnterAsync(MapExecutionOperation.ExistingWork, stoppingToken);
                if (execution is not null) await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "任务步骤协调周期失败。");
            }

            if (RollingRoutes is not null && trafficWakeup is not null)
                await trafficWakeup.WaitForFollowupAsync((int)BusinessDelay(options.PollIntervalMs).TotalMilliseconds, stoppingToken);
            else
                await Task.Delay(BusinessDelay(options.PollIntervalMs), stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var assigned = await tasks.GetAssignedTasksAsync(cancellationToken);
        var exitKeys = assigned.Where(t=>t.Stage is DispatchTaskStage.ReturningEmptyBox or DispatchTaskStage.WaitingForPlcConfirmation).Select(t=>"exit:"+t.TaskId).ToHashSet(StringComparer.Ordinal);
        foreach(var key in _conditionFingerprints.Keys.Where(k=>k.StartsWith("exit:",StringComparison.Ordinal)&&!exitKeys.Contains(k)).ToArray()) _conditionFingerprints.Remove(key);
        if(handshake?.Enabled==true)
            foreach(var task in assigned.Where(t=>t.Stage is DispatchTaskStage.ReturningEmptyBox or DispatchTaskStage.WaitingForPlcConfirmation))
            {
                try { await handshake.ProcessExitAsync(task,cancellationToken); }
                catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){throw;}
                catch(DumperExitPendingException pending)
                {
                    var key="exit:"+task.TaskId;
                    var fingerprint="waiting:"+pending.Message;
                    if(!_conditionFingerprints.TryGetValue(key,out var prior)||prior!=fingerprint)
                    {
                        logger.LogInformation("等待整车完全离开倒料区，保持原设备及路权。TaskId={TaskId}, Reason={Reason}",task.TaskId,pending.Message);
                        if(journal!=null)await journal.RecordAsync(task.TaskId,"","DumperExitWaiting",new {Reason=pending.Message},cancellationToken);
                        _conditionFingerprints[key]=fingerprint;
                    }
                }
                catch(Exception exception)
                {
                    // Retain equipment and road ownership; a failed exit observation never stops other tasks' ready-step polling.
                    var key="exit:"+task.TaskId;
                    var reason=exception.GetBaseException().Message;
                    var fingerprint="blocked:"+reason;
                    if(!_conditionFingerprints.TryGetValue(key,out var prior)||prior!=fingerprint)
                    {
                        logger.LogWarning(exception,"28阶段实际出区核验未通过，保持原责任。TaskId={TaskId}",task.TaskId);
                        if(journal!=null)await journal.RecordAsync(task.TaskId,"","DumperExitBlocked",new {Reason=reason,Context=exception.Message},cancellationToken);
                        _conditionFingerprints[key]=fingerprint;
                    }
                }
            }
        var priorities = await _trafficLayer.PrioritiesAsync(cancellationToken);
        var readySteps = (await tasks.GetReadyStepsAsync(int.MaxValue, cancellationToken))
            .OrderByDescending(s => priorities.GetValueOrDefault(s.TaskId)?.EffectivePriority ?? double.MinValue)
            .ThenBy(s => priorities.GetValueOrDefault(s.TaskId)?.CalledAt ?? DateTimeOffset.MaxValue)
            .ThenBy(s => s.TaskId, StringComparer.Ordinal).ThenBy(s => s.Sequence)
            .Take(options.BatchSize).ToArray();
        // Publish every eligible contender before the first grant. Otherwise a low-priority
        // step polled first could consume a just-freed resource before its peer is observed.
        foreach (var step in readySteps)
        {
            try
            {
                if (step.StepType is DispatchTaskStepType.DeliverFullBoxToDumper or
                    DispatchTaskStepType.EnterDumpingPosition or DispatchTaskStepType.ReturnEmptyBox)
                    await ObserveTransportAsync(step, cancellationToken);
                else if (step.StepType == DispatchTaskStepType.WaitForDumperReady && handshake?.Enabled == true)
                    await handshake.ObserveEquipmentWaitingAsync(step, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // Normal processing below records the original step failure. Observing a
                // proposal never clears ownership or prepares an external command.
                logger.LogDebug(exception, "后续步骤路权候选尚未满足条件。StepId={StepId}", step.StepId);
            }
        }
        var readyIds = readySteps.Select(s => s.StepId).ToHashSet(StringComparer.Ordinal);
        // Timeout hold keys ("timeout:<StepId>") live as long as their step stays Ready. Keeping them
        // is what stops the hold from being re-recorded every cycle.
        foreach (var id in _conditionFingerprints.Keys.Where(id => !id.StartsWith("exit:",StringComparison.Ordinal) &&
                     !readyIds.Contains(id.StartsWith(TimeoutHoldKeyPrefix, StringComparison.Ordinal) ? id[TimeoutHoldKeyPrefix.Length..] : id)).ToArray())
            _conditionFingerprints.Remove(id);
        foreach (var step in readySteps)
        {
            try
            {
                using var evidenceScope = journal?.BeginScope(step.TaskId, step.StepId);
                if (journal != null && !_observedSteps.Contains(step.StepId)) await journal.RecordAsync(step.TaskId, step.StepId, "StepObservation",
                    new { step, Plc = signals.GetStation(step.StationId), Vehicles = vehicleStates?.GetAll() }, cancellationToken);
                _observedSteps.Add(step.StepId);
                if (journal != null)
                {
                    var observed = TaskStepProgressRules.Observe(step.StepType, step.StationId, creationOptions.DumperStationId, signals);
                    // Only changes in value/quality are journaled; observation timestamps alone do not generate poll spam.
                    var fingerprint = string.Join("|", observed.Select(c => $"{c.RuleId}:{c.CurrentValue}:{c.Quality}"));
                    if (observed.Count > 0 && (!_conditionFingerprints.TryGetValue(step.StepId, out var previous) || previous != fingerprint))
                    {
                        await journal.RecordAsync(step.TaskId, step.StepId, "StepConditionsObserved", new { Conditions = observed }, cancellationToken);
                        _conditionFingerprints[step.StepId] = fingerprint;
                    }
                }
                if (await ApplyTimeoutPolicyAsync(step, cancellationToken)) continue;
                if (handshake == null || !await handshake.ProcessAsync(step, cancellationToken)) await ProcessStepAsync(step, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await ApplyFailurePolicyAsync(step, exception, cancellationToken);

                logger.LogWarning(
                    exception,
                    "处理任务步骤失败，下轮将从数据库状态继续。TaskId={TaskId}, StepId={StepId}, StepType={StepType}",
                    step.TaskId,
                    step.StepId,
                    step.StepType);
            }
        }
    }

    private const string TimeoutHoldKeyPrefix = "timeout:";

    private async Task<bool> ApplyTimeoutPolicyAsync(
        DispatchTaskStepWorkItem step,
        CancellationToken cancellationToken)
    {
        if (step.StartedAt is not { } startedAt || !TaskTemplateExecutionPolicy.HasTimedOut(
                startedAt, step.TimeoutSeconds, _clock.GetUtcNow())) return false;
        var elapsed = _clock.GetUtcNow() - startedAt;

        var action = TaskTemplateExecutionPolicy.NormalizeTimeoutAction(step.TimeoutAction);
        var node = string.IsNullOrWhiteSpace(step.TemplateNodeId) ? step.StepType.ToString() : step.TemplateNodeId;
        var message = $"任务节点 {node} 已运行 {(long)elapsed.TotalSeconds} 秒，超过阈值 {step.TimeoutSeconds} 秒；" +
                      $"超时处置为 {action}。保留原步骤、路权和设备责任，禁止自动重发。";
        if (TaskTemplateExecutionPolicy.StopsAutomationForTimeout(action))
        {
            var stopped = await tasks.StopReadyStepForPolicyAsync(
                step.TaskId, step.StepId, action, message, cancellationToken);
            if (stopped)
            {
                await RecordPolicyEvidenceAsync(step, "NodeTimeoutStopped",
                    new { step.TemplateNodeId, step.TimeoutSeconds, Action = action, ElapsedSeconds = (long)elapsed.TotalSeconds, Reason = message },
                    cancellationToken);
                logger.LogWarning(
                    "任务节点超时并按策略停止自动执行。TaskId={TaskId}, StepId={StepId}, Action={Action}, TimeoutSeconds={TimeoutSeconds}",
                    step.TaskId, step.StepId, action, step.TimeoutSeconds);
            }
            return stopped;
        }

        // The fingerprint excludes the elapsed seconds so a held step is recorded once, not every cycle.
        var key = TimeoutHoldKeyPrefix + step.StepId;
        var fingerprint = $"{node}|{action}|{step.TimeoutSeconds}";
        if (!_conditionFingerprints.TryGetValue(key, out var prior) || prior != fingerprint)
        {
            await tasks.RecordReadyStepFailureAsync(step.TaskId, step.StepId, message, cancellationToken);
            await RecordPolicyEvidenceAsync(step, "NodeTimeoutHold",
                new { step.TemplateNodeId, step.TimeoutSeconds, Action = action, ElapsedSeconds = (long)elapsed.TotalSeconds, Reason = message },
                cancellationToken);
            _conditionFingerprints[key] = fingerprint;
            logger.LogWarning(
                "任务节点超阈值，按策略保持等待。TaskId={TaskId}, StepId={StepId}, Action={Action}, TimeoutSeconds={TimeoutSeconds}",
                step.TaskId, step.StepId, action, step.TimeoutSeconds);
        }
        return false;
    }

    private async Task ApplyFailurePolicyAsync(
        DispatchTaskStepWorkItem step,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var policy = TaskTemplateExecutionPolicy.NormalizeFailurePolicy(step.FailurePolicy);
        var trigger = TaskTemplateExecutionPolicy.ClassifyFailure(exception.GetBaseException().Message);
        var reason = $"任务节点 {(string.IsNullOrWhiteSpace(step.TemplateNodeId) ? step.StepType.ToString() : step.TemplateNodeId)} " +
                     $"失败策略 {policy}；白名单条件 {trigger}；{exception.GetBaseException().Message}";
        try
        {
            if (TaskTemplateExecutionPolicy.StopsAutomation(policy))
            {
                var stopped = await tasks.StopReadyStepForPolicyAsync(
                    step.TaskId, step.StepId, policy, reason, cancellationToken);
                await RecordPolicyEvidenceAsync(step, "NodeFailureStopped",
                    new { step.TemplateNodeId, Policy = policy, Trigger = trigger, Reason = reason, Stopped = stopped },
                    cancellationToken);
            }
            else
            {
                await tasks.RecordReadyStepFailureAsync(
                    step.TaskId, step.StepId, reason, cancellationToken);
                await RecordPolicyEvidenceAsync(step, "NodeFailureHeld",
                    new { step.TemplateNodeId, Policy = policy, Trigger = trigger, Reason = reason },
                    cancellationToken);
            }
        }
        catch (Exception persistenceException)
        {
            logger.LogError(
                persistenceException,
                "记录任务步骤失败策略时异常。TaskId={TaskId}, StepId={StepId}, Policy={Policy}",
                step.TaskId,
                step.StepId,
                policy);
        }
    }

    private async Task RecordPolicyEvidenceAsync(
        DispatchTaskStepWorkItem step,
        string kind,
        object evidence,
        CancellationToken cancellationToken)
    {
        if (journal is null) return;
        try
        {
            await journal.RecordAsync(step.TaskId, step.StepId, kind, evidence, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "记录节点策略证据失败。TaskId={TaskId}, StepId={StepId}, Kind={Kind}",
                step.TaskId,
                step.StepId,
                kind);
        }
    }

    private async Task ObserveTransportAsync(DispatchTaskStepWorkItem step, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(step.VehicleId)) return;
        if (handshake?.Enabled == true) await handshake.ValidateTransportAsync(step, ct);
        var current = activeMap?.Current;
        if (current is null) return;
        var pickup = dispatchOptions.GetPickupSite(step.StationId);
        if (pickup is null) return;
        if (step.StepType == DispatchTaskStepType.DeliverFullBoxToDumper && dispatchOptions.GetDumperWaitingSites().Count > 1)
        {
            if (!VehicleReadyAt(step.VehicleId, pickup, current)) return;
            if (trafficResources is ITaskWaitingBayAllocator allocator)
                await TaskWaitingBayRouter.ObserveWaitingAsync(step, pickup, dispatchOptions, current, routePlanner,
                    trafficResources, allocator, options, _trafficLayer, ct, agvOptions?.NavigationCommandMode,
                    RollingRoutes, FleetVehicleIds());
            return;
        }
        var waiting = dispatchOptions.GetDumperWaitingSite()!;
        if (dispatchOptions.GetDumperWaitingSites().Count > 1 && step.StepType == DispatchTaskStepType.EnterDumpingPosition)
        {
            var binding = trafficResources is ITaskWaitingBayAllocator allocator ? await allocator.GetAsync(step.TaskId, ct) : null;
            if (binding is null || binding.VehicleId != step.VehicleId || binding.MapId != current.MapId || binding.MapVersionId != current.VersionId) return;
            waiting = binding.WaitingSite;
        }
        var route = step.StepType switch
        {
            DispatchTaskStepType.DeliverFullBoxToDumper => (pickup, waiting),
            DispatchTaskStepType.EnterDumpingPosition => (waiting, dispatchOptions.DumperDumpingSite),
            _ => (dispatchOptions.DumperDumpingSite, pickup)
        };
        var unavailable = await trafficResources.GetUnavailableResourceIdsAsync(step.TaskId, ct);
        var adaptive = PlanFollowup(step.StepType, current, route.Item1, route.Item2, unavailable);
        var plan = adaptive?.Route ?? routePlanner.Plan(route.Item1, route.Item2, unavailable) ??
            routePlanner.Plan(route.Item1, route.Item2, new HashSet<string>());
        if (plan is not { Nodes.Count: >= 2 } || !VehicleReadyAt(step.VehicleId, route.Item1, current)) return;
        var windows = adaptive?.Windows ?? TransportRouteWindows.Plan(
            plan, current, options, agvOptions?.NavigationCommandMode,
            forceAtomic: IsDumperEntryHandshake(step.StepType));
        windows = TransportRouteWindows.NormalizeResources(windows, current);
        if (adaptive is null && step.StepType == DispatchTaskStepType.ReturnEmptyBox)
            windows = TransportRouteWindows.IncludeDumperExitClearance(windows, current.Definition, route.Item1);
        var firstWindow = windows[0];
        await _trafficLayer.ObserveAsync(new(step.StepId, step.TaskId, step.VehicleId,
            firstWindow.Source, firstWindow.Destination, firstWindow.EdgeIds, firstWindow.ResourceIds, true,
            "后续运输候选已核对，按原任务优先级等待首个安全路权窗口。")
            { MapId = current.MapId, MapVersionId = current.VersionId }, ct);
    }

    private bool IsDumperEntryHandshake(DispatchTaskStepType type) =>
        handshake?.Enabled == true && type == DispatchTaskStepType.EnterDumpingPosition;

    private StagedPickupRoute? PlanFollowup(DispatchTaskStepType type, ActiveMapRuntimeSnapshot? map,
        string source, string destination, IReadOnlySet<string> unavailable) =>
        map is not null && type is (DispatchTaskStepType.DeliverFullBoxToDumper or DispatchTaskStepType.ReturnEmptyBox)
            ? RollingRoutes?.PlanFollowup(routePlanner, map, source, destination, unavailable,
                FleetVehicleIds(), type == DispatchTaskStepType.ReturnEmptyBox ? source : null)
            : null;

    private bool VehicleReadyAt(string vehicleId, string source, ActiveMapRuntimeSnapshot current)
    {
        if (vehicleStates is null) return true; // Optional compatibility adapter; runtime always provides telemetry.
        if (!vehicleStates.TryGet(vehicleId, out var vehicle) || vehicle.Connection != ConnectionState.Online ||
            vehicle.Quality != SignalQuality.Good || vehicle.EmergencyStopped != false || vehicle.Blocked != false ||
            AgvObstaclePolicy.IsBlocked(vehicle) || AgvObstaclePolicy.HasBlockingAlarm(vehicle) ||
            vehicle.LastSuccessfulPollAt is not { } observed || observed > _clock.GetUtcNow().AddSeconds(1) ||
            _clock.GetUtcNow() - observed > TimeSpan.FromSeconds(mapOptions?.PositionStaleSeconds ?? 5) || vehicle.Tasks.Any(t => t.Status is 1 or 2 or 3)) return false;
        return current.Definition.Nodes.Any(n => n.Enabled &&
            MapRouteSiteMapper.IsSameNode(current.Definition, n.Id, source) &&
            MapRouteSiteMapper.IsSameNode(current.Definition, n.Id, vehicle.CurrentStation));
    }

    private Task ProcessStepAsync(
        DispatchTaskStepWorkItem step,
        CancellationToken cancellationToken) => step.StepType switch
        {
            DispatchTaskStepType.GoToPickup => Task.CompletedTask,
            DispatchTaskStepType.PickupFullBox => ProcessPickupAsync(step, cancellationToken),
            DispatchTaskStepType.DeliverFullBoxToDumper => PrepareTransportAsync(step, cancellationToken),
            DispatchTaskStepType.WaitForDumperReady => ProcessDumperReadyAsync(step, cancellationToken),
            DispatchTaskStepType.EnterDumpingPosition => PrepareTransportAsync(step, cancellationToken),
            DispatchTaskStepType.DumpFullBox => ProcessDumpingAsync(step, cancellationToken),
            DispatchTaskStepType.ReturnEmptyBox => PrepareTransportAsync(step, cancellationToken),
            DispatchTaskStepType.PlaceEmptyBox => ProcessEmptyBoxPlacementAsync(step, cancellationToken),
            DispatchTaskStepType.CompletePlcHandshake => CompletePlcHandshakeAsync(step, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(step.StepType), step.StepType, null)
        };

    private async Task PrepareTransportAsync(
        DispatchTaskStepWorkItem step,
        CancellationToken cancellationToken)
    {
        if(handshake?.Enabled==true)await handshake.ValidateTransportAsync(step,cancellationToken);
        if (string.IsNullOrWhiteSpace(step.VehicleId))
        {
            throw new InvalidOperationException($"步骤{step.StepId}缺少已分配车辆。");
        }

        var pickupSite = dispatchOptions.GetPickupSite(step.StationId) ??
            throw new InvalidOperationException($"工位{step.StationId}未配置AGV地图站点。");
        var waitingSite = dispatchOptions.GetDumperWaitingSite()!;
        AgvDispatch.Domain.Maps.RoutePlan? selectedPlan = null;
        IReadOnlyList<AgvTransportRouteWindow>? selectedWindows = null;
        string? retainedWaitingResource = null;
        if (dispatchOptions.GetDumperWaitingSites().Count > 1)
        {
            var allocator = trafficResources as ITaskWaitingBayAllocator ??
                throw new InvalidOperationException("多候车位缺少持久化分配器。");
            var current = activeMap?.Current ?? throw new InvalidOperationException("候车选位缺少运行地图。");
            if (step.StepType == DispatchTaskStepType.DeliverFullBoxToDumper)
            {
                var chosen = await TaskWaitingBayRouter.AcquireAsync(step, pickupSite, dispatchOptions, current,
                    routePlanner, trafficResources, allocator, options, cancellationToken, _trafficLayer,
                    agvOptions?.NavigationCommandMode, RollingRoutes, FleetVehicleIds());
                if (chosen is null) return;
                waitingSite = chosen.Value.Site;
                selectedPlan = chosen.Value.Plan;
                selectedWindows = chosen.Value.Windows;
                retainedWaitingResource = await allocator.GetAsync(step.TaskId, cancellationToken) is { } binding
                    ? binding.ResourceId : null;
            }
            else
            {
                var chosen = await allocator.GetAsync(step.TaskId, cancellationToken) ??
                    throw new InvalidOperationException("原任务缺少已保存的候车位，不按默认点继续。");
                if (chosen.VehicleId != step.VehicleId || chosen.MapId != current.MapId || chosen.MapVersionId != current.VersionId)
                    throw new InvalidOperationException("原任务候车位车辆或地图不一致。");
                waitingSite = chosen.WaitingSite;
            }
        }
        var dumpingSite = dispatchOptions.DumperDumpingSite;
        var route = step.StepType switch
        {
            DispatchTaskStepType.DeliverFullBoxToDumper => (pickupSite, waitingSite),
            DispatchTaskStepType.EnterDumpingPosition => (waitingSite, dumpingSite),
            DispatchTaskStepType.ReturnEmptyBox => (dumpingSite, pickupSite),
            _ => throw new InvalidOperationException($"步骤{step.StepId}不是运输步骤。")
        };

        var planningMap = activeMap?.Current;
        var unavailable = await trafficResources.GetUnavailableResourceIdsAsync(
            step.TaskId,
            cancellationToken);
        var adaptive = selectedPlan is null
            ? PlanFollowup(step.StepType, planningMap, route.Item1, route.Item2, unavailable)
            : null;
        var plan = selectedPlan ?? adaptive?.Route ?? routePlanner.Plan(route.Item1, route.Item2, unavailable);
        if (plan == null)
        {
            var diagnostic = routePlanner.Plan(route.Item1, route.Item2, new HashSet<string>());
            var bayBlocked = step.StepType == DispatchTaskStepType.DeliverFullBoxToDumper && planningMap is not null &&
                DumperWaitingBaySafety.ExclusiveResourceIds(planningMap.Definition, dispatchOptions).Any(unavailable.Contains);
            var hasDiagnosticRoute = diagnostic is not null && diagnostic.Nodes.Count >= 2;
            await _trafficLayer.ObserveAsync(new(step.StepId, step.TaskId, step.VehicleId,
                hasDiagnosticRoute ? diagnostic!.Nodes[0] : route.Item1,
                hasDiagnosticRoute ? diagnostic!.Nodes[^1] : route.Item2,
                diagnostic?.EdgeIds ?? [], diagnostic?.ResourceIds ?? [], true,
                diagnostic == null ? "地图路径不可达，请核对起终点、路段方向和启用状态。" : bayBlocked ?
                    "候车位容量已被其他任务占用，后车在原取箱工位等待；前车断连、取消或心跳过期不自动释放，诊断路线不会发车。" :
                    "候选路径被交通资源占用，等待容量释放；诊断路径不会发送车辆。") { MapId = planningMap?.MapId, MapVersionId = planningMap?.VersionId }, cancellationToken);
            return;
        }
        if (plan.Nodes.Count < 2)
        {
            throw new InvalidOperationException($"AGV路径节点不足：{route.Item1}->{route.Item2}。");
        }
        var windows = selectedWindows ?? adaptive?.Windows;
        if (windows is null)
        {
            windows = planningMap is null
                ? [new AgvTransportRouteWindow(0, plan.Nodes[0], plan.Nodes[^1], plan.Nodes,
                    plan.EdgeIds, plan.ResourceIds, true)]
                : TransportRouteWindows.Plan(
                    plan, planningMap, options, agvOptions?.NavigationCommandMode,
                    forceAtomic: IsDumperEntryHandshake(step.StepType));
            if (planningMap is not null && step.StepType == DispatchTaskStepType.ReturnEmptyBox)
                windows = TransportRouteWindows.IncludeDumperExitClearance(
                    windows, planningMap.Definition, dumpingSite);
        }
        if (planningMap is not null)
            windows = TransportRouteWindows.NormalizeResources(windows, planningMap);
        if (!string.IsNullOrWhiteSpace(retainedWaitingResource))
        {
            // A selected capacity-1 waiting bay stays reserved while the vehicle
            // advances through later route windows.
            windows = windows.Select(window => window with
            {
                ResourceIds = window.ResourceIds.Append(retainedWaitingResource)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()
            }).ToArray();
        }
        var firstWindow = windows[0];
        var observation = new TrafficRouteObservation(step.StepId, step.TaskId, step.VehicleId,
            firstWindow.Source, firstWindow.Destination, firstWindow.EdgeIds, firstWindow.ResourceIds, false,
            "规划完成，申请首个安全路权窗口；后续窗口在车辆停稳后续约。")
            { MapId = planningMap?.MapId, MapVersionId = planningMap?.VersionId };

        var reservation = selectedWindows is not null
            ? new TrafficReservationResult(true)
            : await _trafficLayer.AcquireAsync(observation, cancellationToken);
        if (!reservation.Acquired)
        {
            logger.LogDebug(
                "后续运输步骤等待交通资源。TaskId={TaskId}, StepId={StepId}, ResourceId={ResourceId}, BlockingTaskId={BlockingTaskId}",
                step.TaskId,
                step.StepId,
                reservation.BlockingResourceId,
                reservation.BlockingTaskId);
            return;
        }

        var commandPrepared = false;
        async Task ReleaseUncommittedAsync(string reason)
        {
            if ((string.IsNullOrWhiteSpace(retainedWaitingResource)))
            {
                await trafficResources.ReleaseStepAsync(step.StepId, cancellationToken);
                return;
            }
            await trafficResources.ReleaseStepExceptResourcesAsync(
                step.StepId,
                [retainedWaitingResource],
                reason + "；保留本任务已绑定的独立候车位。",
                cancellationToken);
        }
        try
        {
            if (preSendSafety is not null && await preSendSafety.CheckPreparationAsync(step, step.VehicleId, firstWindow.Path,
                planningMap, cancellationToken) is { } rejection)
            {
                await ReleaseUncommittedAsync("安全条件不满足，撤回未授予的公共路段建议");
                await _trafficLayer.SuspendRecommendationAsync(observation with { Reason = "安全条件不满足，撤回未授予的排队推荐：" + rejection }, cancellationToken);
                logger.LogDebug("后续运输步骤等待安全复核。StepId={StepId}, Reason={Reason}", step.StepId, rejection);
                return;
            }
            commandPrepared = await outbox.TryPrepareTransportWindowAsync(
                    step.TaskId,
                    step.StepId,
                    step.VehicleId,
                    route.Item1,
                    route.Item2,
                    firstWindow,
                    windows,
                    null,
                    dispatchOptions.GetAction(step.StepType),
                    cancellationToken);
            if (commandPrepared)
            {
                pipeline?.ReportLayer("execution", "Waiting", "后续运输步骤已沿用原任务与步骤原子登记；发送前再次经过安全复核。", 1);
                // The previous transport step is complete once the next transport
                // command is atomically prepared. Keep the dump-area ownership
                // through the return leg until physical exit handover is verified.
                if (handshake?.Enabled != true || step.StepType != DispatchTaskStepType.ReturnEmptyBox)
                    await trafficResources.ReleaseTaskExceptStepAsync(
                        step.TaskId, step.StepId, cancellationToken);
                logger.LogInformation(
                    "后续AGV运输步骤已取得路权并写入Outbox。TaskId={TaskId}, StepType={StepType}, VehicleId={VehicleId}, From={Source}, To={Destination}, Resources={Resources}",
                    step.TaskId,
                    step.StepType,
                    step.VehicleId,
                    firstWindow.Source,
                    firstWindow.Destination,
                    string.Join(',', firstWindow.ResourceIds));
            }
            else
            {
                await ReleaseUncommittedAsync("运输命令未准备成功，释放未提交的公共路段");
            }
        }
        catch
        {
            if (!commandPrepared)
            {
                await ReleaseUncommittedAsync("运输命令准备异常，释放未提交的公共路段");
            }
            throw;
        }
    }

    private async Task ProcessPickupAsync(
        DispatchTaskStepWorkItem step,
        CancellationToken cancellationToken)
    {
        if (!TryGetGood(step.StationId, "BoxPresent", out var boxPresent))
        {
            return;
        }

        if (boxPresent)
        {
            return;
        }

        var removedAsserted = IsGoodValue(step.StationId, "FullBoxRemoved", true);
        var emptyTaskAsserted = IsGoodValue(
            step.StationId,
            "EmptyBoxTaskAcknowledged",
            true);
        if (!removedAsserted || !emptyTaskAsserted)
        {
            if (!removedAsserted)
            {
                await plcGateway.WriteSignalAsync(
                    step.StationId,
                    "FullBoxRemoved",
                    true,
                    cancellationToken);
            }

            if (!emptyTaskAsserted)
            {
                await plcGateway.WriteSignalAsync(
                    step.StationId,
                    "EmptyBoxTaskAcknowledged",
                    true,
                    cancellationToken);
            }

            return;
        }

        await plcGateway.WriteSignalAsync(
            step.StationId,
            "FullBoxRequestAcknowledged",
            false,
            cancellationToken);
        await plcGateway.WriteSignalAsync(
            step.StationId,
            "FullBoxRemoved",
            false,
            cancellationToken);
        await CompleteAsync(step, cancellationToken);
    }

    private async Task ProcessDumperReadyAsync(
        DispatchTaskStepWorkItem step,
        CancellationToken cancellationToken)
    {
        if (!TryGetGood(creationOptions.DumperStationId, "Faulted", out var faulted) || faulted)
        {
            return;
        }

        if (!IsGoodValue(creationOptions.DumperStationId, "AgvAtWaitingPosition", true))
        {
            await plcGateway.WriteSignalAsync(
                creationOptions.DumperStationId,
                "AgvAtWaitingPosition",
                true,
                cancellationToken);
            return;
        }

        if (!TryGetGood(creationOptions.DumperStationId, "DumperReady", out var ready) || !ready)
        {
            return;
        }

        await plcGateway.WriteSignalAsync(
            creationOptions.DumperStationId,
            "AgvAtWaitingPosition",
            false,
            cancellationToken);
        await CompleteAsync(step, cancellationToken);
    }

    private async Task ProcessDumpingAsync(
        DispatchTaskStepWorkItem step,
        CancellationToken cancellationToken)
    {
        if (!TryGetGood(creationOptions.DumperStationId, "Faulted", out var faulted) || faulted)
        {
            return;
        }

        if (!IsGoodValue(creationOptions.DumperStationId, "AgvAtDumpingPosition", true))
        {
            await plcGateway.WriteSignalAsync(
                creationOptions.DumperStationId,
                "AgvAtDumpingPosition",
                true,
                cancellationToken);
            return;
        }

        if (!TryGetGood(
                creationOptions.DumperStationId,
                "DumpingCompletedMayLeave",
                out var mayLeave) || !mayLeave)
        {
            return;
        }

        await plcGateway.WriteSignalAsync(
            creationOptions.DumperStationId,
            "AgvAtDumpingPosition",
            false,
            cancellationToken);
        await CompleteAsync(step, cancellationToken);
    }

    private async Task ProcessEmptyBoxPlacementAsync(
        DispatchTaskStepWorkItem step,
        CancellationToken cancellationToken)
    {
        if (!IsGoodValue(step.StationId, "EmptyBoxPlaced", true))
        {
            await plcGateway.WriteSignalAsync(
                step.StationId,
                "EmptyBoxPlaced",
                true,
                cancellationToken);
            return;
        }

        if (!TryGetGood(step.StationId, "BoxPresent", out var boxPresent) || !boxPresent)
        {
            return;
        }

        await CompleteAsync(step, cancellationToken);
    }

    private async Task CompletePlcHandshakeAsync(
        DispatchTaskStepWorkItem step,
        CancellationToken cancellationToken)
    {
        await WriteIfNeededAsync(
            step.StationId,
            "EmptyBoxTaskAcknowledged",
            false,
            cancellationToken);
        await WriteIfNeededAsync(step.StationId, "EmptyBoxPlaced", false, cancellationToken);
        await WriteIfNeededAsync(
            step.StationId,
            "FullBoxRequestAcknowledged",
            false,
            cancellationToken);
        await WriteIfNeededAsync(step.StationId, "FullBoxRemoved", false, cancellationToken);
        await CompleteAsync(step, cancellationToken);
    }

    private async Task CompleteAsync(
        DispatchTaskStepWorkItem step,
        CancellationToken cancellationToken)
    {
        if (await tasks.CompleteReadyStepAsync(step.TaskId, step.StepId, cancellationToken))
        {
            if (step.StepType == DispatchTaskStepType.CompletePlcHandshake)
            {
                await trafficResources.ReleaseTaskAsync(step.TaskId, cancellationToken);
            }

            logger.LogInformation(
                "任务步骤完成。TaskId={TaskId}, StepId={StepId}, StepType={StepType}",
                step.TaskId,
                step.StepId,
                step.StepType);
        }
    }

    private bool TryGetGood(string stationId, string variableName, out bool value)
    {
        signals.TryGet(stationId, variableName, out var snapshot);
        return TaskSignalPredicate.TryRead(snapshot, out value);
    }

    private bool IsGoodValue(string stationId, string variableName, bool expected)
    {
        signals.TryGet(stationId, variableName, out var snapshot);
        return TaskSignalPredicate.Evaluate(stationId, variableName, expected, snapshot).Satisfied;
    }

    private async Task WriteIfNeededAsync(
        string stationId,
        string variableName,
        bool value,
        CancellationToken cancellationToken)
    {
        if (signals.TryGet(stationId, variableName, out var snapshot) &&
            snapshot.Quality == SignalQuality.Good &&
            snapshot.Value == value)
        {
            return;
        }

        await plcGateway.WriteSignalAsync(stationId, variableName, value, cancellationToken);
    }
}
