using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Application.Maps;

namespace AgvDispatch.Service;

public sealed class DispatchTaskOrchestrator(
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
    IMapExecutionGate executionGate, IRobokitTaskMapPreflight? robokitMap = null, HandshakeExecutionOptions? handshakeOptions = null, PersistentHandshakeExecutor? handshake = null, BusinessRunOwnership? businessRun = null, FleetTestOwnership? fleetTest=null) : IsolatedBackgroundService
{
    private readonly HashSet<string> _missingRouteWarnings =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _waitingBayError;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
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

            await Task.Delay(options.PollIntervalMs, stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        using var execution = await executionGate.EnterAsync(MapExecutionOperation.ExistingWork, cancellationToken);
        if (execution is null) return;
        var connection = await agvGateway.GetConnectionStatusAsync(cancellationToken);
        if (connection.State is not (ConnectionState.Online or ConnectionState.Degraded))
        {
            return;
        }

        await SynchronizeExecutingTasksAsync(cancellationToken);
        var submitting = await tasks.GetSubmittingTasksAsync(cancellationToken);
        var assigned = await tasks.GetAssignedTasksAsync(cancellationToken);
        foreach (var activeTask in submitting.Concat(assigned).DistinctBy(task => task.TaskId))
        {
            await trafficResources.RenewTaskAsync(activeTask.TaskId, cancellationToken);
        }
        if (!executionGate.CanStartNewWork) return;
        if (options.EngineeringMapError(activeMap.Current) is not null) return;
        var bayError = DumperWaitingBaySafety.GetError(activeMap.Current?.Definition, options,
            handshakeOptions is { Enabled: true, PlcContractVersion: HandshakeExecutionOptions.Version });
        if (bayError is not null)
        {
            if (_waitingBayError != bayError) logger.LogWarning("新派车被候车位门禁阻止：{Reason}", bayError);
            _waitingBayError = bayError;
            return;
        }
        _waitingBayError = null;
        var occupiedSlots = submitting.Count + assigned.Count;
        var availableSlots = options.MaxConcurrentTasks - occupiedSlots;
        if (availableSlots <= 0)
        {
            return;
        }

        var occupiedVehicles = submitting
            .Concat(assigned)
            .Select(task => task.VehicleId)
            .Where(vehicleId => !string.IsNullOrWhiteSpace(vehicleId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vehicles = await agvGateway.GetVehiclesAsync(cancellationToken);
        var idleVehicles = vehicles
            .Where(vehicle => businessRun?.Returning != true || vehicle.VehicleId != businessRun.Vehicle)
            .Where(vehicle => vehicle.Connection == ConnectionState.Online)
            .Where(vehicle => IsPositionSafeForNewTask(vehicle.VehicleId))
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
        var selectedVehicles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dispatchedCount = 0;
        foreach (var task in candidates)
        {
            if (dispatchedCount >= availableSlots || selectedVehicles.Count >= idleVehicles.Length)
            {
                break;
            }

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

                continue;
            }

            var readyStep = (await tasks.GetReadyStepsAsync(100, cancellationToken))
                .FirstOrDefault(step =>
                    step.TaskId == task.TaskId &&
                    step.StepType == DispatchTaskStepType.GoToPickup);
            if (readyStep is null)
            {
                continue;
            }

            var unavailable = await trafficResources.GetUnavailableResourceIdsAsync(
                task.TaskId,
                cancellationToken);
            var planningMap = activeMap.Current;
            var routeCandidates = idleVehicles
                .Where(vehicle => options.AllowsVehicle(task.StationId, vehicle.VehicleId))
                .Where(vehicle => fleetTest?.Allows(task.TaskId,vehicle.VehicleId)!=false)
                .Where(vehicle => TaskVehicleSelection.Accepts(task, vehicle.VehicleId))
                .Where(vehicle => !selectedVehicles.Contains(vehicle.VehicleId))
                .Where(vehicle => !string.IsNullOrWhiteSpace(vehicle.CurrentStation))
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
                    var checkedMap=planningMap is null ? new RobokitTaskMapCheck(false,"运行地图不存在。") :
                        await robokitMap.CheckAsync(vehicle.VehicleId,vehicle.CurrentStation!,pickupSite,options.GetDumperWaitingSite()!,options.DumperDumpingSite,
                            planningMap.MapId,planningMap.VersionId,cancellationToken);
                    if(!checkedMap.Accepted || !ReferenceEquals(planningMap,activeMap.Current))
                    {
                        var reason = !ReferenceEquals(planningMap, activeMap.Current)
                            ? "车端核验期间运行地图已变化，本候选不发送。"
                            : checkedMap.Message;
                        await trafficResources.RecordRouteAsync(new(readyStep.StepId,task.TaskId,vehicle.VehicleId,vehicle.CurrentStation!,pickupSite,
                            route.EdgeIds,route.ResourceIds,true,"派车前车端地图/路线核验未通过："+reason){MapId=planningMap?.MapId,MapVersionId=planningMap?.VersionId},cancellationToken);
                        logger.LogWarning(
                            "派车前车端地图/路线核验未通过。TaskId={TaskId}, VehicleId={VehicleId}, From={Source}, To={Destination}, Reason={Reason}",
                            task.TaskId, vehicle.VehicleId, vehicle.CurrentStation, pickupSite, reason);
                        continue;
                    }
                }
                await trafficResources.RecordRouteAsync(new(readyStep.StepId, task.TaskId, vehicle.VehicleId,
                    vehicle.CurrentStation!, pickupSite, route.EdgeIds, route.ResourceIds,
                    false, "规划完成，申请到取货站路径资源。") { MapId = planningMap?.MapId, MapVersionId = planningMap?.VersionId }, cancellationToken);
                var reservation = await trafficResources.TryAcquireAsync(
                    readyStep.StepId,
                    task.TaskId,
                    vehicle.VehicleId,
                    route.ResourceIds,
                    cancellationToken);
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
                    if(fleetTest?.Allows(task.TaskId,vehicle.VehicleId)==false)
                    {await trafficResources.ReleaseStepAsync(readyStep.StepId,cancellationToken);continue;}
                    if(handshake!=null)await handshake.BindBeforeTransportAsync(readyStep with {VehicleId=vehicle.VehicleId},cancellationToken);
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
                continue;
            }
        }
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
                var status = await agvGateway.GetTransportTaskStatusAsync(
                    task.VendorTaskId,
                    cancellationToken);
                if (status is null)
                {
                    continue;
                }

                if (status.State == AgvTransportTaskState.Completed)
                {
                    await tasks.CompleteExecutingStepAsync(
                        task.TaskId,
                        task.VendorTaskId,
                        cancellationToken);
                    logger.LogInformation(
                        "AGV运输步骤完成，已激活下一业务步骤。TaskId={TaskId}, VendorTaskId={VendorTaskId}",
                        task.TaskId,
                        task.VendorTaskId);
                    continue;
                }

                if (status.State is AgvTransportTaskState.Failed or AgvTransportTaskState.Cancelled)
                {
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
