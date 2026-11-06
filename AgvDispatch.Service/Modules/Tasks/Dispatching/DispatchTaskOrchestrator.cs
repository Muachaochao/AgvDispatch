using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Application.Maps;

namespace AgvDispatch.Service;

public sealed partial class DispatchTaskOrchestrator(
    ILogger<DispatchTaskOrchestrator> logger,
    DispatchOrchestratorOptions options,
    IDispatchOrchestrationRepository tasks,
    IAgvCommandOutboxRepository outbox,
    IAgvGateway agvGateway,
    IAgvStateCache agvStates,
    IActiveMapRuntime activeMap,
    MapOptions mapOptions,
    IRoutePlanner routePlanner,
    ITrafficResourceManager trafficResources,
    IMapExecutionGate executionGate, IRobokitTaskMapPreflight? robokitMap = null,
    HandshakeExecutionOptions? handshakeOptions = null, PersistentHandshakeExecutor? handshake = null,
    BusinessRunOwnership? businessRun = null, FleetTestOwnership? fleetTest=null,
    IChargingDispatchGuard? charging=null, ITaskSwitchGuard? taskSwitchGuard=null,
    DispatchOptimizationService? optimization=null, DispatchRouteCache? routeCache=null,
    IDispatchQueueWindowService? queueLayer=null, IDispatchCandidateFilter? candidateLayer=null,
    DispatchTrafficReservationService? trafficLayer=null, IDispatchPreSendSafetyService? preSendSafety=null,
    ITaskHandshakeStore? handshakes=null, ITransportTimeoutRecoveryService? timeoutRecovery=null,
    CompletedTransportRouteReleaseService? completedRouteRelease=null) : IsolatedBackgroundService
{
    private readonly IDispatchQueueWindowService _queueLayer = queueLayer ?? new DispatchQueueWindowService(tasks, trafficResources);
    private readonly IDispatchCandidateFilter _candidateLayer = candidateLayer ?? new DispatchCandidateFilter(options, agvGateway, agvStates,
        activeMap, mapOptions, routePlanner, trafficResources, routeCache, businessRun, fleetTest, charging, taskSwitchGuard);
    private readonly DispatchTrafficReservationService _trafficLayer = trafficLayer ?? new(trafficResources, optimization, tasks, optimization);
    private readonly HashSet<string> _missingRouteWarnings =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectWaitTimeouts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dependencyWaitWarnings =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _waitingBayError;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            ReportOptimizationGate("任务编排未启用；优化配置可查看，当前不会计算业务派车或发车。");
            logger.LogInformation("AGV任务编排服务未启用，待厂家接口和地图站点配置完成后开启。");
            return;
        }

        var configurationError = options.GetConfigurationError();
        if (configurationError is not null)
        {
            throw new InvalidOperationException(configurationError);
        }

        logger.LogInformation(
            "AGV任务编排服务启动。周期={PollIntervalMs}ms，最大并发={MaxConcurrentTasks}，倒料站点={DumperSite}",
            options.PollIntervalMs,
            options.MaxConcurrentTasks,
            options.GetDumperWaitingSite());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "AGV任务编排周期执行失败。");
            }

            await Task.Delay(optimization is not null && optimization.Options.Mode != "Disabled"
                ? Math.Min(options.PollIntervalMs, optimization.Options.IntervalMs) : options.PollIntervalMs, stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        using var chargingLease = charging is null ? null : await charging.EnterBusinessAsync(cancellationToken);
        using var execution = await executionGate.EnterAsync(MapExecutionOperation.ExistingWork, cancellationToken);
        if (execution is null) { ReportOptimizationGate("地图恢复或切换门禁尚未放行。" ); return; }
        var connection = await agvGateway.GetConnectionStatusAsync(cancellationToken);
        if (connection.State is not (ConnectionState.Online or ConnectionState.Degraded))
        {
            ReportOptimizationGate("AGV通信未就绪，保留等待队列。");
            return;
        }

        await SynchronizeExecutingTasksAsync(cancellationToken);
        await _trafficLayer.PrepareCycleAsync(cancellationToken);
        var submitting = await tasks.GetSubmittingTasksAsync(cancellationToken);
        var assigned = await tasks.GetAssignedTasksAsync(cancellationToken);
        foreach (var activeTask in submitting.Concat(assigned).DistinctBy(task => task.TaskId))
        {
            await trafficResources.RenewTaskAsync(activeTask.TaskId, cancellationToken);
        }
        if (!executionGate.CanStartNewWork) { ReportOptimizationGate("地图执行门禁尚未允许新增派车。"); return; }
        if (options.EngineeringMapError(activeMap.Current) is { } mapError) { ReportOptimizationGate(mapError); return; }
        var bayError = DumperWaitingBaySafety.GetError(activeMap.Current?.Definition, options,
            handshakeOptions is { Enabled: true, PlcContractVersion: HandshakeExecutionOptions.Version });
        if (bayError is not null)
        {
            if (_waitingBayError != bayError) logger.LogWarning("新派车被候车位门禁阻止：{Reason}", bayError);
            _waitingBayError = bayError;
            ReportOptimizationGate(bayError);
            return;
        }
        _waitingBayError = null;
        if (options.RequireGlobalMatching && !string.Equals(optimization?.Options.Mode, "Global", StringComparison.OrdinalIgnoreCase))
        {
            ReportOptimizationGate("正式业务要求全局匹配；当前优化策略未处于 Global，保留原队列并暂停新增派车。");
            return;
        }
        var occupiedSlots = submitting.Count + assigned.Count;
        var availableSlots = options.MaxConcurrentTasks - occupiedSlots;
        if (optimization is not null && !optimization.Options.Mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            if (DateTimeOffset.UtcNow < _nextOptimizationAt) return;
            var policy = optimization.Options;
            _nextOptimizationAt = DateTimeOffset.UtcNow.AddMilliseconds(policy.IntervalMs);
            var global = policy.Mode.Equals("Global", StringComparison.OrdinalIgnoreCase);
            await RunOptimizationAsync(policy, Math.Max(0, availableSlots), submitting.Concat(assigned).ToArray(), cancellationToken);
            if (global) return;
        }
        if (availableSlots <= 0)
        {
            return;
        }

        var occupiedVehicles = submitting
            .Concat(assigned)
            .Select(task => task.VehicleId)
            .Where(vehicleId => !string.IsNullOrWhiteSpace(vehicleId))
            .Select(vehicleId => vehicleId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vehicles = await agvGateway.GetVehiclesAsync(cancellationToken);
        var heldVehicles = await trafficResources.GetHeldVehicleIdsAsync(cancellationToken);
        occupiedVehicles.UnionWith(heldVehicles);
        var idleVehicles = vehicles
            .Where(vehicle => charging?.AllowsNewBusiness(vehicle.VehicleId) != false)
            .Where(vehicle => businessRun?.Returning != true || vehicle.VehicleId != businessRun.Vehicle)
            .Where(vehicle => vehicle.Connection == ConnectionState.Online)
            .Where(vehicle => IsPositionSafeForNewTask(vehicle.VehicleId))
            .Where(vehicle => VehicleExclusion(vehicle, occupiedVehicles, optimization?.Options ?? new DispatchAssignmentOptions()) is null)
            .Where(vehicle => options.IdleOperatingStates.Contains(
                vehicle.OperatingState,
                StringComparer.OrdinalIgnoreCase))
            .Where(vehicle => !occupiedVehicles.Contains(vehicle.VehicleId))
            .ToArray();
        if (idleVehicles.Length == 0)
        {
            return;
        }

        var candidates = await tasks.GetDispatchCandidatesAsync(int.MaxValue, cancellationToken);
        var dependencyIds = candidates.SelectMany(task => task.DependsOnTaskIds)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var dependencyStates = dependencyIds.Length == 0
            ? new Dictionary<string, DispatchTaskDependencyState>(StringComparer.OrdinalIgnoreCase)
            : (await tasks.GetTaskDependencyStatesAsync(dependencyIds, cancellationToken))
                .ToDictionary(state => state.TaskId, StringComparer.OrdinalIgnoreCase);
        var selectedVehicles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dispatchedCount = 0;
        foreach (var task in candidates)
        {
            if (dispatchedCount >= availableSlots || selectedVehicles.Count >= idleVehicles.Length)
            {
                break;
            }
            var unmetDependency = task.DependsOnTaskIds.FirstOrDefault(id =>
                !dependencyStates.TryGetValue(id, out var state) ||
                !state.Exists ||
                state.Stage != DispatchTaskStage.Completed);
            if (unmetDependency is not null)
            {
                if (_dependencyWaitWarnings.Add(task.TaskId + ":" + unmetDependency))
                {
                    logger.LogInformation(
                        "任务等待前置任务完成，不进入派车。TaskId={TaskId}, DependencyTaskId={DependencyTaskId}, Stage={Stage}",
                        task.TaskId,
                        unmetDependency,
                        dependencyStates.TryGetValue(unmetDependency, out var state) ? state.Stage : null);
                }
                continue;
            }
            foreach (var dependencyId in task.DependsOnTaskIds)
                _dependencyWaitWarnings.Remove(task.TaskId + ":" + dependencyId);

            var readyStep = (await tasks.GetReadyStepsAsync(100, cancellationToken))
                .FirstOrDefault(step =>
                    step.TaskId == task.TaskId &&
                    step.StepType == DispatchTaskStepType.GoToPickup);
            if (readyStep is null)
            {
                continue;
            }
            var selectionStartedAt = task.PolicyStartedAt;
            if (task.DependsOnTaskIds.Count > 0 &&
                task.DependsOnTaskIds.Select(id => dependencyStates.GetValueOrDefault(id)?.UpdatedAt)
                    .Where(value => value is not null).Max() is { } dependencyReadyAt &&
                dependencyReadyAt > selectionStartedAt)
                selectionStartedAt = dependencyReadyAt;
            if (await ApplySelectAgvTimeoutAsync(task with { PolicyStartedAt = selectionStartedAt },
                    readyStep, cancellationToken))
                continue;

            var pickupSite = options.GetPickupSite(task.StationId);
            if (pickupSite is null)
            {
                if (_missingRouteWarnings.Add(task.StationId))
                {
                    logger.LogWarning(
                        "工位没有配置AGV取货站点，任务保持待派车。StationId={StationId}, TaskId={TaskId}",
                        task.StationId,
                        task.TaskId);
                }
                if (await ApplySelectAgvConditionAsync(task, readyStep, TaskTemplateExecutionPolicy.RouteUnavailable,
                        $"工位 {task.StationId} 未配置取货站点，派车条件不可用。", cancellationToken))
                    continue;
                continue;
            }

            var unavailable = await trafficResources.GetUnavailableResourceIdsAsync(
                task.TaskId,
                cancellationToken);
            var planningMap = activeMap.Current;
            var physicalPickupOccupants = DispatchCandidateFilter.PhysicalPickupOccupants(
                pickupSite, planningMap?.Definition, vehicles);
            var ownableVehicles = new List<AgvVehicleStatus>();
            foreach (var vehicle in idleVehicles
                         .Where(vehicle => options.AllowsVehicle(task.StationId, vehicle.VehicleId))
                         .Where(vehicle => fleetTest?.Allows(task.TaskId,vehicle.VehicleId)!=false)
                         .Where(vehicle => TaskVehicleSelection.Accepts(task, vehicle.VehicleId))
                         .Where(vehicle => !selectedVehicles.Contains(vehicle.VehicleId)))
                if (taskSwitchGuard is null || await taskSwitchGuard.AllowsVehicleAsync(vehicle.VehicleId, task.TaskId, null, cancellationToken))
                    ownableVehicles.Add(vehicle);
            var routeCandidates = ownableVehicles
                .Where(vehicle => !string.IsNullOrWhiteSpace(vehicle.CurrentStation))
                // A station already occupied by another car keeps the original
                // task unassigned and takes no path resources. Its current occupant
                // remains eligible for its own exit/return cycle.
                .Where(vehicle => physicalPickupOccupants.All(occupant =>
                    string.Equals(occupant, vehicle.VehicleId, StringComparison.OrdinalIgnoreCase)))
                .Select(vehicle => new
                {
                    Vehicle = vehicle,
                    Route = routePlanner.Plan(vehicle.CurrentStation!, pickupSite, unavailable)
                })
                .Where(candidate => candidate.Route is { Nodes.Count: >= 2 })
                .OrderBy(candidate => candidate.Route!.Cost)
                .ThenByDescending(candidate => candidate.Vehicle.BatteryPercent)
                .ToArray();
            if (routeCandidates.Length == 0)
            {
                if (physicalPickupOccupants.Count > 0)
                {
                    var occupiedReason =
                        $"目标取货站{task.StationId}被{string.Join('、', physicalPickupOccupants)}实际占用，等待其驶离后再派车；不得提前锁定目标站路权。";
                    await trafficResources.RecordRouteAsync(new(
                        readyStep.StepId, task.TaskId, physicalPickupOccupants[0],
                        pickupSite, pickupSite, [], [], true, occupiedReason)
                    {
                        MapId = planningMap?.MapId,
                        MapVersionId = planningMap?.VersionId
                    }, cancellationToken);
                    if (await ApplySelectAgvConditionAsync(
                            task, readyStep, TaskTemplateExecutionPolicy.VehicleUnavailable,
                            occupiedReason, cancellationToken))
                        continue;
                    continue;
                }
                var diagnostic = idleVehicles.Where(v => options.AllowsVehicle(task.StationId, v.VehicleId) && fleetTest?.Allows(task.TaskId,v.VehicleId)!=false && TaskVehicleSelection.Accepts(task, v.VehicleId) && !selectedVehicles.Contains(v.VehicleId) && !string.IsNullOrWhiteSpace(v.CurrentStation)).Select(v => new
                { Vehicle = v, Source = v.CurrentStation!, Route = routePlanner.Plan(v.CurrentStation!, pickupSite, new HashSet<string>()) })
                    .OrderBy(v => v.Route?.Cost ?? double.MaxValue).FirstOrDefault();
                if (diagnostic != null) await trafficResources.RecordRouteAsync(new(readyStep.StepId, task.TaskId, diagnostic.Vehicle.VehicleId,
                    diagnostic.Source, pickupSite, diagnostic.Route?.EdgeIds ?? [], diagnostic.Route?.ResourceIds ?? [], true,
                    diagnostic.Route == null ? "地图路径不可达或车辆起点无效；车辆已在取货点时必须配置真实已启用的进出闭合路线，禁止同点空路径代替取箱。" : "候选路径被交通资源占用，等待容量释放；诊断路径不会发送车辆。") { MapId = planningMap?.MapId, MapVersionId = planningMap?.VersionId }, cancellationToken);
                var routeKey = $"ANY->{pickupSite}";
                if (_missingRouteWarnings.Add(routeKey))
                {
                    logger.LogWarning(
                        "没有空闲车辆具备到取货点的可用路径，任务保持待派车。Destination={Destination}, TaskId={TaskId}",
                        pickupSite,
                        task.TaskId);
                }
                var trigger = ownableVehicles.Count == 0
                    ? TaskTemplateExecutionPolicy.VehicleUnavailable
                    : diagnostic?.Route is null
                        ? TaskTemplateExecutionPolicy.RouteUnavailable
                        : TaskTemplateExecutionPolicy.TrafficResourceOccupied;
                var reason = ownableVehicles.Count == 0
                    ? "没有满足车辆池、任务选择或车辆可用条件的派车候选。"
                    : diagnostic?.Route is null
                        ? "候选车辆均没有到取货点的可执行路线。"
                        : "候选路线均被交通资源占用。";
                if (await ApplySelectAgvConditionAsync(task, readyStep, trigger, reason, cancellationToken))
                    continue;
                continue;
            }

            var dispatched = false;
            foreach (var candidate in routeCandidates)
            {
                var vehicle = candidate.Vehicle;
                if(fleetTest?.Allows(task.TaskId,vehicle.VehicleId)==false)continue;
                var route = candidate.Route!;
                if(robokitMap is not null)
                {
                    var checkedMap=new RobokitTaskMapCheck(planningMap is not null,"运行地图不存在。");
                    if(planningMap is not null)
                        foreach(var waiting in options.GetDumperWaitingSites())
                        {
                            checkedMap=await robokitMap.CheckAsync(vehicle.VehicleId,vehicle.CurrentStation!,pickupSite,waiting,options.DumperDumpingSite,
                                planningMap.MapId,planningMap.VersionId,cancellationToken);
                            if(!checkedMap.Accepted)break;
                        }
                    if(!checkedMap.Accepted || !ReferenceEquals(planningMap,activeMap.Current))
                    {
                        var reason = !ReferenceEquals(planningMap, activeMap.Current)
                            ? "车端核验期间运行地图已变化，本候选不发送。"
                            : checkedMap.Message;
                        await trafficResources.RecordRouteAsync(new(readyStep.StepId,task.TaskId,vehicle.VehicleId,route.Nodes[0],route.Nodes[^1],
                            route.EdgeIds,route.ResourceIds,true,"派车前车端地图/路线核验未通过："+reason){MapId=planningMap?.MapId,MapVersionId=planningMap?.VersionId},cancellationToken);
                        logger.LogWarning(
                            "派车前车端地图/路线核验未通过。TaskId={TaskId}, VehicleId={VehicleId}, From={Source}, To={Destination}, Reason={Reason}",
                            task.TaskId, vehicle.VehicleId, vehicle.CurrentStation, pickupSite, reason);
                        continue;
                    }
                }
                var reservation = await _trafficLayer.AcquireAsync(new(readyStep.StepId, task.TaskId, vehicle.VehicleId,
                    route.Nodes[0], route.Nodes[^1], route.EdgeIds, route.ResourceIds,
                    false, "规划完成，申请到取货站路径资源。") { MapId = planningMap?.MapId, MapVersionId = planningMap?.VersionId }, cancellationToken);
                if (!reservation.Acquired)
                {
                    logger.LogDebug(
                        "候选AGV路径资源被占用。TaskId={TaskId}, VehicleId={VehicleId}, ResourceId={ResourceId}, BlockingTaskId={BlockingTaskId}",
                        task.TaskId,
                        vehicle.VehicleId,
                        reservation.BlockingResourceId,
                        reservation.BlockingTaskId);
                    continue;
                }

                var commandPrepared = false;
                try
                {
                    var currentVehicle = (await agvGateway.GetVehiclesAsync(cancellationToken))
                        .SingleOrDefault(v => v.VehicleId == vehicle.VehicleId);
                    if (currentVehicle is null || currentVehicle.CurrentStation != vehicle.CurrentStation ||
                        VehicleExclusion(currentVehicle, occupiedVehicles, optimization?.Options ?? new DispatchAssignmentOptions()) is not null ||
                        !executionGate.CanStartNewWork || !ReferenceEquals(planningMap, activeMap.Current))
                    { await trafficResources.ReleaseStepAsync(readyStep.StepId, cancellationToken); continue; }
                    if(fleetTest?.Allows(task.TaskId,vehicle.VehicleId)==false)
                    {await trafficResources.ReleaseStepAsync(readyStep.StepId,cancellationToken);continue;}
                    if (preSendSafety is not null && await preSendSafety.CheckPreparationAsync(readyStep, vehicle.VehicleId, route.Nodes,
                        planningMap, cancellationToken) is { } rejected)
                    { await trafficResources.ReleaseStepAsync(readyStep.StepId, cancellationToken); continue; }
                    if(preSendSafety is null && handshake!=null)await handshake.BindBeforeTransportAsync(readyStep with {VehicleId=vehicle.VehicleId},cancellationToken);
                    commandPrepared = await outbox.TryPrepareTransportStepAsync(
                        task.TaskId,
                        readyStep.StepId,
                        vehicle.VehicleId,
                        route.Nodes,
                        options.GetAction(DispatchTaskStepType.GoToPickup),
                        cancellationToken);
                    if (!commandPrepared)
                    {
                        await trafficResources.ReleaseStepAsync(
                            readyStep.StepId,
                            cancellationToken);
                        await trafficResources.RecordRouteAsync(new(readyStep.StepId, task.TaskId,
                            vehicle.VehicleId, vehicle.CurrentStation!, pickupSite, route.EdgeIds, route.ResourceIds,
                            false, "首段车端命令未能登记，未发送；请核对任务、步骤和命令队列。")
                            { MapId = planningMap?.MapId, MapVersionId = planningMap?.VersionId }, cancellationToken);
                        logger.LogWarning(
                            "首段车端命令未能登记。TaskId={TaskId}, VehicleId={VehicleId}, From={Source}, To={Destination}",
                            task.TaskId, vehicle.VehicleId, vehicle.CurrentStation, pickupSite);
                        break;
                    }

                    if (handshakeOptions?.Enabled != true) await trafficResources.ReleaseTaskExceptStepAsync(
                        task.TaskId,
                        readyStep.StepId,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    if (!commandPrepared)
                    {
                        await trafficResources.ReleaseStepAsync(
                            readyStep.StepId,
                            cancellationToken);
                        await trafficResources.RecordRouteAsync(new(readyStep.StepId, task.TaskId,
                            vehicle.VehicleId, vehicle.CurrentStation!, pickupSite, route.EdgeIds, route.ResourceIds,
                            false, "派车前准备失败，未发送：" + exception.Message)
                            { MapId = planningMap?.MapId, MapVersionId = planningMap?.VersionId }, cancellationToken);
                        logger.LogWarning(exception,
                            "派车前准备失败，未发送。TaskId={TaskId}, VehicleId={VehicleId}, From={Source}, To={Destination}",
                            task.TaskId, vehicle.VehicleId, vehicle.CurrentStation, pickupSite);
                        continue;
                    }
                    throw;
                }

                selectedVehicles.Add(vehicle.VehicleId);
                dispatchedCount++;
                dispatched = true;
                logger.LogInformation(
                    "已按路径成本选择AGV并写入首步骤。TaskId={TaskId}, VehicleId={VehicleId}, From={Source}, To={Destination}, Cost={Cost}, Resources={Resources}",
                    task.TaskId,
                    vehicle.VehicleId,
                    route.Nodes[0],
                    route.Nodes[^1],
                    route.Cost,
                    string.Join(',', route.ResourceIds));
                break;
            }

            if (!dispatched)
            {
                var trigger = routeCandidates.Length > 0
                    ? TaskTemplateExecutionPolicy.TrafficResourceOccupied
                    : TaskTemplateExecutionPolicy.RouteUnavailable;
                if (await ApplySelectAgvConditionAsync(task, readyStep, trigger,
                        "本轮派车没有形成可发送的首段命令，按 SelectAgv 节点策略处理。", cancellationToken))
                    continue;
                continue;
            }
        }
    }

    private async Task<bool> ApplySelectAgvTimeoutAsync(
        DispatchTaskWorkItem task,
        DispatchTaskStepWorkItem readyStep,
        CancellationToken cancellationToken)
    {
        if (!TaskTemplateExecutionPolicy.HasTimedOut(
                task.PolicyStartedAt, task.TimeoutSeconds, DateTimeOffset.UtcNow)) return false;
        var elapsed = DateTimeOffset.UtcNow - task.PolicyStartedAt;
        var action = TaskTemplateExecutionPolicy.NormalizeTimeoutAction(task.TimeoutAction);
        var reason = $"SelectAgv 已等待 {(long)elapsed.TotalSeconds} 秒，超过模板阈值 {task.TimeoutSeconds} 秒；" +
                     $"超时处置 {action}，保留原任务与现场责任，禁止自动重发。";
        if (!TaskTemplateExecutionPolicy.StopsAutomationForTimeout(action))
        {
            if (_selectWaitTimeouts.Add(task.TaskId + ":timeout"))
            {
                logger.LogWarning(
                    "SelectAgv 超时，按模板策略保持派车窗口。TaskId={TaskId}, Action={Action}, TimeoutSeconds={TimeoutSeconds}",
                    task.TaskId, action, task.TimeoutSeconds);
            }
            return false;
        }
        var stopped = await tasks.StopReadyStepForPolicyAsync(
            task.TaskId, readyStep.StepId, action, reason, cancellationToken);
        if (stopped)
        {
            _selectWaitTimeouts.Remove(task.TaskId + ":timeout");
            logger.LogWarning(
                "SelectAgv 超时，按模板策略冻结任务。TaskId={TaskId}, Action={Action}, TimeoutSeconds={TimeoutSeconds}",
                task.TaskId, action, task.TimeoutSeconds);
        }
        return stopped;
    }

    private async Task<bool> ApplySelectAgvConditionAsync(
        DispatchTaskWorkItem task,
        DispatchTaskStepWorkItem readyStep,
        string trigger,
        string reason,
        CancellationToken cancellationToken)
    {
        var policy = TaskTemplateExecutionPolicy.NormalizeFailurePolicy(task.FailurePolicy);
        if (!TaskTemplateExecutionPolicy.StopsAutomation(policy)) return false;
        var message = $"SelectAgv 白名单条件 {trigger}；失败策略 {policy}；{reason}";
        var stopped = await tasks.StopReadyStepForPolicyAsync(
            task.TaskId, readyStep.StepId, policy, message, cancellationToken);
        if (stopped)
        {
            logger.LogWarning(
                "SelectAgv 条件触发节点失败策略并冻结任务。TaskId={TaskId}, Trigger={Trigger}, Policy={Policy}",
                task.TaskId, trigger, policy);
        }
        return stopped;
    }

    private bool IsPositionSafeForNewTask(string vehicleId)
    {
        if (!agvStates.TryGet(vehicleId, out var agv)) return false;
        var position = AgvMapPositionAnalyzer.Analyze(
            agv.X, agv.Y, agv.LocalizationConfidence, agv.LastSuccessfulPollAt,
            activeMap.Current?.Definition, DateTimeOffset.Now,
            TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds),
            mapOptions.MinimumLocalizationConfidence, mapOptions.MaximumRouteDeviationMeters);
        return position.State == AgvMapPositionState.Normal;
    }

    private async Task SynchronizeExecutingTasksAsync(CancellationToken cancellationToken)
    {
        var executing = await tasks.GetExecutingTasksAsync(cancellationToken);
        foreach (var task in executing)
        {
            if (string.IsNullOrWhiteSpace(task.VendorTaskId))
            {
                await tasks.AdvanceStageAsync(
                    task.TaskId,
                    DispatchTaskStage.Faulted,
                    task.VehicleId,
                    "执行中任务缺少厂家任务编号。",
                    cancellationToken);
                continue;
            }

            try
            {
                var status = timeoutRecovery is null
                    ? null
                    : await timeoutRecovery.GetReplacementStatusAsync(task, cancellationToken);
                status ??= await agvGateway.GetTransportTaskStatusAsync(
                    task.VendorTaskId,
                    cancellationToken);
                if (status is null)
                {
                    continue;
                }

                if (status.State == AgvTransportTaskState.Completed)
                {
                    // 1110 and the safety telemetry are separate observations. A cached
                    // terminal row must not finish/release work while this vehicle is blocked.
                    if (string.IsNullOrWhiteSpace(task.VehicleId) ||
                        !string.Equals(task.VehicleId, status.VehicleId, StringComparison.OrdinalIgnoreCase) ||
                        !agvStates.TryGet(task.VehicleId, out var robot) ||
                        robot.Connection != ConnectionState.Online || robot.Quality != SignalQuality.Good ||
                        robot.LastSuccessfulPollAt is not { } observed ||
                        observed > DateTimeOffset.UtcNow.AddSeconds(1) ||
                        DateTimeOffset.UtcNow - observed >= TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds) ||
                        robot.Blocked != false || robot.EmergencyStopped != false ||
                        AgvObstaclePolicy.IsBlocked(robot) || AgvObstaclePolicy.HasBlockingAlarm(robot))
                        continue;
                    var completedStepId = await tasks.CompleteExecutingStepAsync(
                        task.TaskId,
                        task.VendorTaskId,
                        cancellationToken);
                    if (completedRouteRelease is not null)
                    {
                        var release = await completedRouteRelease.ReleaseAsync(
                            task.TaskId, completedStepId, cancellationToken);
                        if (release.ReleasedCount > 0)
                        {
                            logger.LogInformation(
                                "AGV到站后已释放前序路段。TaskId={TaskId}, StepId={StepId}, VehicleId={VehicleId}, Released={Released}, Reason={Reason}",
                                task.TaskId, completedStepId, task.VehicleId,
                                release.ReleasedCount, release.Reason);
                        }
                    }
                    logger.LogInformation(
                        "AGV运输步骤完成，已激活下一业务步骤。TaskId={TaskId}, VendorTaskId={VendorTaskId}",
                        task.TaskId,
                        task.VendorTaskId);
                    continue;
                }

                if (status.State is AgvTransportTaskState.Failed or AgvTransportTaskState.Cancelled)
                {
                    if (status.State == AgvTransportTaskState.Failed &&
                        status.VendorStatusCode == 7 &&
                        timeoutRecovery is not null &&
                        await timeoutRecovery.CaptureAsync(task, status, cancellationToken) is { } assessment)
                    {
                        logger.LogWarning(
                            "AGV导航任务超时，已进入显式恢复状态。TaskId={TaskId}, StepType={StepType}, VehicleId={VehicleId}, Outcome={Outcome}, Message={Message}",
                            task.TaskId,
                            assessment.StepType,
                            assessment.VehicleId,
                            assessment.Outcome,
                            assessment.Message);
                        continue;
                    }
                    var terminalStage = status.State == AgvTransportTaskState.Cancelled
                        ? DispatchTaskStage.Cancelled
                        : DispatchTaskStage.Faulted;
                    await tasks.FailExecutingStepAsync(
                        task.TaskId,
                        task.VendorTaskId,
                        terminalStage,
                        status.Message,
                        cancellationToken);
                    logger.LogWarning(
                        "AGV运输步骤异常结束。TaskId={TaskId}, VendorState={VendorState}, Stage={Stage}",
                        task.TaskId,
                        status.State,
                        terminalStage);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "查询AGV任务状态失败，本轮保留原状态。TaskId={TaskId}, VendorTaskId={VendorTaskId}",
                    task.TaskId,
                    task.VendorTaskId);
            }
        }
    }

}

public static class TaskVehicleSelection
{
    public static bool Accepts(DispatchTaskWorkItem task, string vehicleId) =>
        string.IsNullOrWhiteSpace(task.RequestedVehicleId) || task.RequestedVehicleId.Equals(vehicleId, StringComparison.OrdinalIgnoreCase);
}
