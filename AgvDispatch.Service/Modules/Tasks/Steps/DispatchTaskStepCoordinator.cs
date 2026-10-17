using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;

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
    IAgvStateCache? vehicleStates = null, IActiveMapRuntime? activeMap = null, PersistentHandshakeExecutor? handshake = null) : IsolatedBackgroundService
{
    private readonly Dictionary<string, string> _conditionFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observedSteps = new(StringComparer.Ordinal);
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

            await Task.Delay(options.PollIntervalMs, stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var readySteps = await tasks.GetReadyStepsAsync(options.BatchSize, cancellationToken);
        var readyIds = readySteps.Select(s => s.StepId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _conditionFingerprints.Keys.Where(id => !readyIds.Contains(id)).ToArray()) _conditionFingerprints.Remove(id);
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
                if (handshake == null || !await handshake.ProcessAsync(step, cancellationToken)) await ProcessStepAsync(step, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                try
                {
                    await tasks.RecordReadyStepFailureAsync(
                        step.TaskId,
                        step.StepId,
                        exception.Message,
                        cancellationToken);
                }
                catch (Exception persistenceException)
                {
                    logger.LogError(
                        persistenceException,
                        "记录任务步骤失败原因时异常。TaskId={TaskId}, StepId={StepId}",
                        step.TaskId,
                        step.StepId);
                }

                logger.LogWarning(
                    exception,
                    "处理任务步骤失败，下轮将从数据库状态继续。TaskId={TaskId}, StepId={StepId}, StepType={StepType}",
                    step.TaskId,
                    step.StepId,
                    step.StepType);
            }
        }
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
        var plan = routePlanner.Plan(route.Item1, route.Item2, unavailable);
        if (plan == null)
        {
            var diagnostic = routePlanner.Plan(route.Item1, route.Item2, new HashSet<string>());
            await trafficResources.RecordRouteAsync(new(step.StepId, step.TaskId, step.VehicleId, route.Item1, route.Item2,
                diagnostic?.EdgeIds ?? [], diagnostic?.ResourceIds ?? [], true,
                diagnostic == null ? "地图路径不可达，请核对起终点、路段方向和启用状态。" : "候选路径被交通资源占用，等待容量释放；诊断路径不会发送车辆。") { MapId = planningMap?.MapId, MapVersionId = planningMap?.VersionId }, cancellationToken);
            return;
        }
        await trafficResources.RecordRouteAsync(new(step.StepId, step.TaskId, step.VehicleId, route.Item1, route.Item2,
            plan.EdgeIds, plan.ResourceIds, false, "规划完成，申请本运输路径资源。") { MapId = planningMap?.MapId, MapVersionId = planningMap?.VersionId }, cancellationToken);
        if (plan.Nodes.Count < 2)
        {
            throw new InvalidOperationException($"AGV路径节点不足：{route.Item1}->{route.Item2}。");
        }

        var reservation = await trafficResources.TryAcquireAsync(
            step.StepId,
            step.TaskId,
            step.VehicleId,
            plan.ResourceIds,
            cancellationToken);
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
        try
        {
            commandPrepared = await outbox.TryPrepareTransportStepAsync(
                    step.TaskId,
                    step.StepId,
                    step.VehicleId,
                    plan.Nodes,
                    dispatchOptions.GetAction(step.StepType),
                    cancellationToken);
            if (commandPrepared)
            {
                // New contract conservatively retains earlier path leases until verified full-cycle closure.
                if (handshake?.Enabled != true) await trafficResources.ReleaseTaskExceptStepAsync(
                    step.TaskId, step.StepId, cancellationToken);
                logger.LogInformation(
                    "后续AGV运输步骤已取得路权并写入Outbox。TaskId={TaskId}, StepType={StepType}, VehicleId={VehicleId}, From={Source}, To={Destination}, Resources={Resources}",
                    step.TaskId,
                    step.StepType,
                    step.VehicleId,
                    route.Item1,
                    route.Item2,
                    string.Join(',', plan.ResourceIds));
            }
            else
            {
                await trafficResources.ReleaseStepAsync(step.StepId, cancellationToken);
            }
        }
        catch
        {
            if (!commandPrepared)
            {
                await trafficResources.ReleaseStepAsync(step.StepId, cancellationToken);
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
