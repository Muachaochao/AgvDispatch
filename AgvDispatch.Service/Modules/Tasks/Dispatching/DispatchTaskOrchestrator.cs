using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Application.Maps;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Application.Runtime;

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
    CompletedTransportRouteReleaseService? completedRouteRelease=null,
    DispatchTaskStepOptions? stepOptions=null, AgvOptions? agvOptions=null,
    GlobalPickupRoutePlanner? pickupStaging=null, DispatchTrafficWakeup? trafficWakeup=null,
    TimeProvider? clock=null) : IsolatedBackgroundService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private TimeSpan BusinessDelay(int milliseconds) => _clock is SimulationTimeProvider simulation
        ? simulation.ScaleForBusiness(TimeSpan.FromMilliseconds(milliseconds))
        : TimeSpan.FromMilliseconds(milliseconds);
    private readonly IDispatchQueueWindowService _queueLayer = queueLayer ?? new DispatchQueueWindowService(tasks, trafficResources);
    private readonly IDispatchCandidateFilter _candidateLayer = candidateLayer ?? new DispatchCandidateFilter(options, agvGateway, agvStates,
        activeMap, mapOptions, routePlanner, trafficResources, routeCache, businessRun, fleetTest, charging, taskSwitchGuard, pickupStaging);
    private readonly DispatchTrafficReservationService _trafficLayer = trafficLayer ?? new(trafficResources, optimization, tasks, optimization);
    private readonly DispatchTaskStepOptions _stepOptions = stepOptions ?? new();
    private readonly HashSet<string> _missingRouteWarnings =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectWaitTimeouts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dependencyWaitWarnings =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TransportContinuationPreview> _continuationPreviews = new(StringComparer.Ordinal);
    private readonly TemporaryDetourDeferral _continuationDetours = new();
    private bool UsesDynamicContinuation => optimization?.Options.Mode.Equals("Global", StringComparison.OrdinalIgnoreCase) == true &&
        pickupStaging is { SupportsRollingRoutes: true };
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

            var interval = optimization is not null && optimization.Options.Mode != "Disabled"
                ? Math.Min(options.PollIntervalMs, optimization.Options.IntervalMs) : options.PollIntervalMs;
            if (trafficWakeup is null)
                await Task.Delay(BusinessDelay(interval), stoppingToken);
            else if (await trafficWakeup.WaitAsync(interval, stoppingToken) &&
                optimization?.Options.Mode.Equals("Global", StringComparison.OrdinalIgnoreCase) == true)
                _nextOptimizationAt = DateTimeOffset.MinValue;
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var global = await RunCycleUnderGateAsync(cancellationToken);
        if (global is not null)
            await RunOptimizationAsync(global.Policy, global.Slots, global.ActiveTasks, cancellationToken);
    }

    private sealed record GlobalCycle(DispatchAssignmentOptions Policy, int Slots, DispatchTaskWorkItem[] ActiveTasks);

    private async Task<GlobalCycle?> RunCycleUnderGateAsync(CancellationToken cancellationToken)
    {
        using var chargingLease = charging is null ? null : await charging.EnterBusinessAsync(cancellationToken);
        using var execution = await executionGate.EnterAsync(MapExecutionOperation.ExistingWork, cancellationToken);
        if (execution is null) { ReportOptimizationGate("地图恢复或切换门禁尚未放行。" ); return null; }
        var connection = await agvGateway.GetConnectionStatusAsync(cancellationToken);
        if (connection.State is not (ConnectionState.Online or ConnectionState.Degraded))
        {
            ReportOptimizationGate("AGV通信未就绪，保留等待队列。");
            return null;
        }

        await SynchronizeExecutingTasksAsync(cancellationToken);
        await _trafficLayer.PrepareCycleAsync(cancellationToken);
        var submitting = await tasks.GetSubmittingTasksAsync(cancellationToken);
        var assigned = await tasks.GetAssignedTasksAsync(cancellationToken);
        foreach (var activeTask in submitting.Concat(assigned).DistinctBy(task => task.TaskId))
        {
            await trafficResources.RenewTaskAsync(activeTask.TaskId, cancellationToken);
        }
        if (!executionGate.CanStartNewWork) { ReportOptimizationGate("地图执行门禁尚未允许新增派车。"); return null; }
        if (options.EngineeringMapError(activeMap.Current) is { } mapError) { ReportOptimizationGate(mapError); return null; }
        var bayError = DumperWaitingBaySafety.GetError(activeMap.Current?.Definition, options,
            handshakeOptions is { Enabled: true, PlcContractVersion: HandshakeExecutionOptions.Version });
        if (bayError is not null)
        {
            if (_waitingBayError != bayError) logger.LogWarning("新派车被候车位门禁阻止：{Reason}", bayError);
            _waitingBayError = bayError;
            ReportOptimizationGate(bayError);
            return null;
        }
        _waitingBayError = null;
        if (options.RequireGlobalMatching && !string.Equals(optimization?.Options.Mode, "Global", StringComparison.OrdinalIgnoreCase))
        {
            ReportOptimizationGate("正式业务要求全局匹配；当前优化策略未处于 Global，保留原队列并暂停新增派车。");
            return null;
        }
        var occupiedSlots = submitting.Count + assigned.Count;
        var availableSlots = options.MaxConcurrentTasks - occupiedSlots;
        if (availableSlots <= 0)
        {
            return null;
        }
        if (optimization is not null && !optimization.Options.Mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            if (_clock.GetUtcNow() < _nextOptimizationAt) return null;
            var policy = optimization.Options;
            _nextOptimizationAt = _clock.GetUtcNow().AddMilliseconds(policy.IntervalMs);
            var active = submitting.Concat(assigned).ToArray();
            if (policy.Mode.Equals("Global", StringComparison.OrdinalIgnoreCase))
                return new(policy, availableSlots, active);
            await RunOptimizationAsync(policy, availableSlots, active, cancellationToken);
        }
        await RunLegacyDispatchAsync(submitting, assigned, availableSlots, cancellationToken);
        return null;
    }

    private async Task RunLegacyDispatchAsync(IReadOnlyList<DispatchTaskWorkItem> submitting,
        IReadOnlyList<DispatchTaskWorkItem> assigned, int availableSlots, CancellationToken cancellationToken)
    {
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

        // A full-factory wave can hold hundreds of waiting tasks. Planning the
        // entire queue in every control-gate cycle starves step execution and
        // traffic release work. The repository returns candidates in stable
        // priority/call-time order, so a bounded window is sufficient to fill
        // the currently available vehicle slots without changing their order.
        var candidateLimit = Math.Max(25, Math.Min(200, availableSlots * 25));
        var candidates = await tasks.GetDispatchCandidatesAsync(candidateLimit, cancellationToken);
        var readySteps = (await tasks.GetReadyStepsAsync(200, cancellationToken))
            .Where(step => step.StepType == DispatchTaskStepType.GoToPickup)
            .ToDictionary(step => step.TaskId, StringComparer.OrdinalIgnoreCase);
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

            var readyStep = readySteps.GetValueOrDefault(task.TaskId);
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

            var admissionState = await outbox.GetPickupAdmissionAsync(task.TaskId, cancellationToken);
            if (!admissionState.Allowed)
            {
                optimization?.ReportLayer("traffic", "Waiting", admissionState.Reason);
                await _trafficLayer.SuspendRecommendationAsync(new(readyStep.StepId, task.TaskId, "", pickupSite, pickupSite,
                    [], [], true, admissionState.Reason) { MapId=activeMap.Current?.MapId, MapVersionId=activeMap.Current?.VersionId }, cancellationToken);
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
                .Where(candidate => DispatchCandidateFilter.PhysicalIntermediateOccupants(
                    candidate.Route!, planningMap?.Definition, idleVehicles, candidate.Vehicle.VehicleId).Count == 0)
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
                var windows = PlanInitialTransportWindows(route, planningMap);
                var firstWindow = windows[0];
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
                    firstWindow.Source, firstWindow.Destination, firstWindow.EdgeIds, firstWindow.ResourceIds,
                    false, "规划完成，仅申请到取货站的首个安全路权窗口。") { MapId = planningMap?.MapId, MapVersionId = planningMap?.VersionId }, cancellationToken);
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
                    if (currentVehicle is null ||
                        !MapRouteSiteMapper.IsSameNode(planningMap?.Definition,
                            currentVehicle.CurrentStation, vehicle.CurrentStation) ||
                        VehicleExclusion(currentVehicle, occupiedVehicles, optimization?.Options ?? new DispatchAssignmentOptions()) is not null ||
                        !executionGate.CanStartNewWork || !ReferenceEquals(planningMap, activeMap.Current))
                    { await trafficResources.ReleaseStepAsync(readyStep.StepId, cancellationToken); continue; }
                    if(fleetTest?.Allows(task.TaskId,vehicle.VehicleId)==false)
                    {await trafficResources.ReleaseStepAsync(readyStep.StepId,cancellationToken);continue;}
                    if (preSendSafety is not null && await preSendSafety.CheckPreparationAsync(readyStep, vehicle.VehicleId, firstWindow.Path,
                        planningMap, cancellationToken) is { } rejected)
                    { await trafficResources.ReleaseStepAsync(readyStep.StepId, cancellationToken); continue; }
                    if(preSendSafety is null && handshake!=null)await handshake.BindBeforeTransportAsync(readyStep with {VehicleId=vehicle.VehicleId},cancellationToken);
                    commandPrepared = await outbox.TryPrepareTransportWindowAsync(
                        task.TaskId,
                        readyStep.StepId,
                        vehicle.VehicleId,
                        route.Nodes[0],
                        route.Nodes[^1],
                        firstWindow,
                        windows,
                        null,
                        options.GetAction(DispatchTaskStepType.GoToPickup),
                        cancellationToken);
                    if (!commandPrepared)
                    {
                        await trafficResources.ReleaseStepAsync(
                            readyStep.StepId,
                            cancellationToken);
                        await trafficResources.RecordRouteAsync(new(readyStep.StepId, task.TaskId,
                            vehicle.VehicleId, vehicle.CurrentStation!, pickupSite, firstWindow.EdgeIds, firstWindow.ResourceIds,
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
                            vehicle.VehicleId, vehicle.CurrentStation!, pickupSite, firstWindow.EdgeIds, firstWindow.ResourceIds,
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
                    "已按路径成本选择AGV并写入首步骤。TaskId={TaskId}, VehicleId={VehicleId}, From={Source}, WindowTo={WindowDestination}, FinalDestination={Destination}, WindowCount={WindowCount}, Cost={Cost}, Resources={Resources}",
                    task.TaskId,
                    vehicle.VehicleId,
                    route.Nodes[0],
                    firstWindow.Destination,
                    route.Nodes[^1],
                    windows.Count,
                    route.Cost,
                    string.Join(',', firstWindow.ResourceIds));
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
                task.PolicyStartedAt, task.TimeoutSeconds, _clock.GetUtcNow())) return false;
        var elapsed = _clock.GetUtcNow() - task.PolicyStartedAt;
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
            activeMap.Current?.Definition, _clock.GetLocalNow(),
            TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds),
            mapOptions.MinimumLocalizationConfidence, mapOptions.MaximumRouteDeviationMeters);
        return position.State == AgvMapPositionState.Normal;
    }

    private async Task SynchronizeExecutingTasksAsync(CancellationToken cancellationToken)
    {
        var executing = await tasks.GetExecutingTasksAsync(cancellationToken);
        var activeCommands = executing.Select(task => task.VendorTaskId).ToHashSet(StringComparer.Ordinal);
        _continuationDetours.RetainTasks(activeCommands.OfType<string>().ToHashSet(StringComparer.Ordinal));
        foreach (var obsolete in _continuationPreviews.Keys.Where(id => !activeCommands.Contains(id)).ToArray())
            _continuationPreviews.Remove(obsolete);
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

                if (UsesDynamicContinuation && status.State == AgvTransportTaskState.GoingToDestination &&
                    status.VendorTaskId == task.VendorTaskId && status.VehicleId == task.VehicleId)
                    await PreviewTransportContinuationAsync(task, cancellationToken);

                if (status.State == AgvTransportTaskState.Completed)
                {
                    // 1110 and the safety telemetry are separate observations. A cached
                    // terminal row must not finish/release work while this vehicle is blocked.
                    if (string.IsNullOrWhiteSpace(task.VehicleId) ||
                        !string.Equals(task.VehicleId, status.VehicleId, StringComparison.OrdinalIgnoreCase) ||
                        !agvStates.TryGet(task.VehicleId, out var robot) ||
                        robot.Connection != ConnectionState.Online || robot.Quality != SignalQuality.Good ||
                        robot.LastSuccessfulPollAt is not { } observed ||
                        observed > _clock.GetUtcNow().AddSeconds(1) ||
                        _clock.GetUtcNow() - observed >= TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds) ||
                        robot.Blocked != false || robot.EmergencyStopped != false ||
                        AgvObstaclePolicy.IsBlocked(robot) || AgvObstaclePolicy.HasBlockingAlarm(robot))
                        continue;
                    var windowOutcome = await TryAdvanceTransportWindowAsync(task, status, cancellationToken);
                    if (windowOutcome != TransportWindowAdvanceOutcome.NotApplicable)
                    {
                        if (windowOutcome == TransportWindowAdvanceOutcome.Prepared)
                        {
                            logger.LogInformation(
                                "AGV下一滚动窗口已取得路权并写入Outbox。TaskId={TaskId}, VendorTaskId={VendorTaskId}, VehicleId={VehicleId}",
                                task.TaskId, task.VendorTaskId, task.VehicleId);
                        }
                        continue;
                    }
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

    private sealed record TransportContinuationPreview(
        AgvTransportCommandPayload? Payload, ActiveMapRuntimeSnapshot Map, DateTimeOffset CheckedAt,
        IReadOnlySet<string> UnavailableResources);

    private async Task PreviewTransportContinuationAsync(DispatchTaskWorkItem task, CancellationToken ct)
    {
        if (activeMap.Current is not { } map) return;
        var commandId = task.VendorTaskId!;
        _continuationPreviews.TryGetValue(commandId, out var previous);
        var now = _clock.GetUtcNow();
        if (ReferenceEquals(previous?.Map, map) && now - previous!.CheckedAt < TimeSpan.FromMilliseconds(500))
            return;
        var payload = previous?.Payload;
        if (!ReferenceEquals(previous?.Map, map))
        {
            var command = await outbox.GetTransportCommandByVendorAsync(task.TaskId, commandId, ct);
            payload = ReadTransportPayload(command);
        }
        if (payload is null || payload.WindowIndex < 0 || payload.WindowIndex >= payload.Windows.Count - 1)
        {
            _continuationPreviews[commandId] = new(null, map, now, new HashSet<string>());
            return;
        }
        var unavailable = await trafficResources.GetUnavailableResourceIdsAsync(task.TaskId, ct);
        if (previous is not null && ReferenceEquals(previous.Map, map) && unavailable.SetEquals(previous.UnavailableResources))
        {
            _continuationPreviews[commandId] = previous with { CheckedAt = now };
            return;
        }
        var blocked = payload.Windows.Skip(payload.WindowIndex + 1).Any(window => window.ResourceIds.Any(unavailable.Contains));
        var alternate = blocked ? PlanRemainingTransport(task, payload, map, unavailable) : null;
        _continuationPreviews[commandId] = new(payload, map, now, unavailable);
        if (alternate is not null)
            logger.LogDebug("车辆行驶中已预算后续路线，到站按最新状态重新核验。TaskId={TaskId}, NextStop={NextStop}",
                task.TaskId, alternate.Windows[0].Destination);
    }

    private StagedPickupRoute? PlanRemainingTransport(DispatchTaskWorkItem task, AgvTransportCommandPayload payload,
        ActiveMapRuntimeSnapshot map, IReadOnlySet<string> unavailable)
    {
        var alternative = pickupStaging?.PlanFollowup(routePlanner, map, payload.Destination, payload.Windows[^1].Destination,
            unavailable, agvStates.GetAll().Select(vehicle => vehicle.VehicleId).Append(task.VehicleId!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        return alternative is not null && GlobalPickupRoutePlanner.IsForwardContinuation(payload, alternative.Route, map.Definition)
            ? alternative : null;
    }

    private bool IsContinuationWorthTaking(DispatchTaskWorkItem task, AgvTransportCommandPayload payload,
        ActiveMapRuntimeSnapshot map, StagedPickupRoute reroute, bool nextBlocked)
    {
        var policy = optimization!.Options;
        var oldEdges = payload.Windows.Skip(payload.WindowIndex + 1).SelectMany(window => window.EdgeIds).ToArray();
        var (oldTravel, _) = EstimateRoute(new RoutePlan([], oldEdges, [], 0), map, policy);
        var (travel, traffic) = EstimateRoute(reroute.Route, map, policy);
        var cost = travel + traffic + reroute.DeferredSeconds;
        if (!nextBlocked)
            return reroute.DeferredSeconds == 0 && cost <= oldTravel + 30;

        // Retain the queue position for a short-lived blockage before accepting
        // a long detour. This clock belongs to the current sent window.
        return _continuationDetours.RemainingSeconds(task.VendorTaskId!, task.VehicleId!, map.VersionId,
            cost, oldTravel, _clock.GetUtcNow(), maximumRatio: 1.5, minimumExtraSeconds: 30) == 0;
    }

    private static AgvTransportCommandPayload? ReadTransportPayload(AgvCommandOutboxItem? command) =>
        command is null ? null : System.Text.Json.JsonSerializer.Deserialize<AgvTransportCommandPayload>(
            command.PayloadJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    private async Task<TransportWindowAdvanceOutcome> TryAdvanceTransportWindowAsync(DispatchTaskWorkItem task, AgvTransportTaskStatus terminal,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task.VendorTaskId) || string.IsNullOrWhiteSpace(task.VehicleId))
            return TransportWindowAdvanceOutcome.NotApplicable;
        var command = await outbox.GetTransportCommandByVendorAsync(task.TaskId, task.VendorTaskId, cancellationToken);
        if (command is null) return TransportWindowAdvanceOutcome.NotApplicable;
        var payload = ReadTransportPayload(command);
        if (payload is null || payload.Windows.Count == 0 ||
            payload.WindowIndex < 0 || payload.WindowIndex >= payload.Windows.Count - 1)
            return TransportWindowAdvanceOutcome.NotApplicable;

        var open = await outbox.GetOpenTransportCommandAsync(task.TaskId, cancellationToken);
        if (open is not null) return TransportWindowAdvanceOutcome.ExistingOpenCommand;

        if (!agvStates.TryGet(task.VehicleId, out var robot) ||
            robot.LastSuccessfulPollAt is not { } observed ||
            robot.Connection != ConnectionState.Online || robot.Quality != SignalQuality.Good ||
            string.IsNullOrWhiteSpace(robot.CurrentStation) ||
            (activeMap.Current?.Definition is { } definition
                ? !MapRouteSiteMapper.IsSameNode(definition, robot.CurrentStation, payload.Destination)
                : !string.Equals(robot.CurrentStation, payload.Destination, StringComparison.OrdinalIgnoreCase)) ||
            observed > _clock.GetUtcNow().AddSeconds(1) ||
            _clock.GetUtcNow() - observed >= TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds) ||
            robot.Blocked != false || robot.EmergencyStopped != false ||
            robot.VelocityX is not { } vx || !double.IsFinite(vx) || Math.Abs(vx) > 0.02 ||
            robot.VelocityY is not { } vy || !double.IsFinite(vy) || Math.Abs(vy) > 0.02 ||
            robot.AngularVelocity is not { } va || !double.IsFinite(va) || Math.Abs(va) > 0.02)
            return TransportWindowAdvanceOutcome.WaitingAtSafePoint;

        var map = activeMap.Current;
        var nextIndex = payload.WindowIndex + 1;
        var windows = map is null
            ? payload.Windows
            : TransportRouteWindows.NormalizeResources(payload.Windows, map);
        var next = windows[nextIndex];
        // Free only geometrically cleared OLD resources before requesting the
        // next window. Waiting for the next grant first creates hold-and-wait
        // cycles with a vehicle returning from the dumper.
        if (completedRouteRelease is not null)
            await completedRouteRelease.ReleaseWindowAsync(task.TaskId, command.StepId, terminal, cancellationToken);
        IReadOnlyList<string>? retained = null;
        if (UsesDynamicContinuation && map is not null)
        {
            // Do not let a changed path release the previous window's footprint.
            // Any lease still present after the geometric pass remains protected.
            retained = await trafficResources.GetStepHeldResourceIdsAsync(task.TaskId, command.StepId, cancellationToken);
            var unavailable = await trafficResources.GetUnavailableResourceIdsAsync(task.TaskId, cancellationToken);
            if (windows.Skip(nextIndex).Any(window => window.ResourceIds.Any(unavailable.Contains)) &&
                PlanRemainingTransport(task, payload, map, unavailable) is { } reroute &&
                IsContinuationWorthTaking(task, payload, map, reroute, next.ResourceIds.Any(unavailable.Contains)))
            {
                windows = GlobalPickupRoutePlanner.ReplaceRemainingWindows(payload, reroute.Windows);
                if (map is not null)
                    windows = TransportRouteWindows.NormalizeResources(windows, map);
                next = windows[nextIndex];
                if (!next.EdgeIds.SequenceEqual(payload.Windows[nextIndex].EdgeIds, StringComparer.OrdinalIgnoreCase))
                    logger.LogInformation("滚动窗口采用新规划的剩余路线。TaskId={TaskId}, StepId={StepId}, From={From}, To={To}",
                        task.TaskId, command.StepId, next.Source, next.Destination);
            }
            if (retained is { Count: > 0 })
            {
                next = next with { ResourceIds = next.ResourceIds.Concat(retained)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray() };
                windows = windows.Select((window, index) => index == nextIndex ? next : window).ToArray();
            }
        }
        var observation = new TrafficRouteObservation(command.StepId, task.TaskId, task.VehicleId,
            next.Source, next.Destination, next.EdgeIds, next.ResourceIds, false,
            "车辆已在安全点停稳，原任务申请下一滚动路权窗口。")
            { MapId = map?.MapId, MapVersionId = map?.VersionId };
        var acquired = await _trafficLayer.AcquireAsync(observation, cancellationToken);
        if (!acquired.Acquired) return TransportWindowAdvanceOutcome.WaitingForTraffic;
        // Acquire first, then release resources not reused by the next window.
        // This keeps an overlapping intersection or waiting-bay anchor leased
        // without a gap while freeing corridors already fully passed.
        if (retained is null)
            await trafficResources.ReleaseStepExceptResourcesAsync(command.StepId, next.ResourceIds,
                "车辆已完整通过上一路权窗口，释放不再复用的资源并保留下一窗口资源。", cancellationToken);

        var fullSource = payload.Windows[0].Source;
        var fullDestination = payload.Windows[^1].Destination;
        var prepared = await outbox.TryPrepareTransportWindowAsync(
            task.TaskId, command.StepId, task.VehicleId, fullSource, fullDestination,
            next, windows, task.VendorTaskId, payload.FinalAction, cancellationToken);
        if (!prepared)
        {
            var waiting = trafficResources is ITaskWaitingBayAllocator allocator
                ? await allocator.GetAsync(task.TaskId, cancellationToken)
                : null;
            if (retained is null && waiting is null)
                await trafficResources.ReleaseStepAsync(command.StepId, cancellationToken);
            else
                await trafficResources.ReleaseStepExceptResourcesAsync(
                    command.StepId, waiting is null ? retained! : [..retained ?? [], waiting.ResourceId],
                    "下一窗口命令准备失败；释放新增的租约，保留原窗口未确认离开的资源和已绑定候车位。",
                    cancellationToken);
        }
        return prepared
            ? TransportWindowAdvanceOutcome.Prepared
            : TransportWindowAdvanceOutcome.PreparationFailed;
    }

    private enum TransportWindowAdvanceOutcome
    {
        NotApplicable,
        ExistingOpenCommand,
        WaitingAtSafePoint,
        WaitingForTraffic,
        PreparationFailed,
        Prepared
    }

}

public static class TaskVehicleSelection
{
    public static bool Accepts(DispatchTaskWorkItem task, string vehicleId) =>
        string.IsNullOrWhiteSpace(task.RequestedVehicleId) || task.RequestedVehicleId.Equals(vehicleId, StringComparison.OrdinalIgnoreCase);
}
