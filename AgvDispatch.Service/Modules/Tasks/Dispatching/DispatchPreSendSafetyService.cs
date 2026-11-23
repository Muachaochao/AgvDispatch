using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

/// <summary>
/// Common fifth-layer check for both assignment strategies and the final outbox
/// claim. Callers retain their map/charging operation leases; this helper never
/// opens a gate or acquires a second operation lease.
/// </summary>
public sealed class DispatchPreSendSafetyService(
    IMapExecutionGate executionGate, IActiveMapRuntime activeMap, IAgvStateCache vehicleStates,
    IAgvGateway agvGateway, MapOptions mapOptions, DispatchOrchestratorOptions dispatch,
    IDispatchOrchestrationRepository tasks, IDispatchTaskRepository taskReader,
    ITrafficResourceManager trafficResources, DispatchTaskCreationOptions creation,
    IPlcSignalCache signals, HandshakeExecutionOptions handshakeOptions,
    PersistentHandshakeExecutor? handshake = null, ITaskHandshakeStore? handshakes = null,
    IChargingDispatchGuard? charging = null, ITaskSwitchGuard? taskSwitchGuard = null,
    FleetTestOwnership? fleetTest = null, BusinessRunOwnership? businessRun = null,
    DispatchOptimizationService? optimization = null, IDispatchPipelineMonitor? monitor = null) : IDispatchPreSendSafetyService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<string?> CheckPreparationAsync(DispatchTaskStepWorkItem step, string vehicleId,
        IReadOnlyList<string> path, ActiveMapRuntimeSnapshot? expectedMap, CancellationToken ct) =>
        ObserveAsync(async () =>
        {
            var owned = step with { VehicleId = vehicleId };
            return await CheckCoreAsync(owned, path, expectedMap, preparing: true, recovering: false, ct);
        }, ct);

    public Task<string?> CheckPendingCommandAsync(AgvCommandOutboxItem command, CancellationToken ct) =>
        ObserveAsync(async () =>
        {
            if (command.CommandType != AgvCommandType.Transport || command.Status is not (AgvCommandStatus.Pending or AgvCommandStatus.RetryScheduled))
                return "待发命令类型或持久化状态不允许认领。";
            var payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(command.PayloadJson, JsonOptions);
            var businessStepId = payload is null
                ? ""
                : string.IsNullOrWhiteSpace(payload.BusinessStepId) ? payload.BusinessTaskId : payload.BusinessStepId;
            if (payload is null || businessStepId != command.StepId || payload.VehicleId != command.VehicleId ||
                payload.Path is not { Count: >= 2 } || payload.Source != payload.Path[0] || payload.Destination != payload.Path[^1])
                return "待发命令负载与原步骤、车辆或路径端点不一致。";
            var businessSource = payload.Windows.Count > 0 ? payload.Windows[0].Source : payload.Source;
            var businessDestination = payload.Windows.Count > 0 ? payload.Windows[^1].Destination : payload.Destination;
            var task = (await CurrentTasksAsync(ct)).SingleOrDefault(t => t.TaskId == command.TaskId);
            var original = (await taskReader.GetStepsAsync(command.TaskId, ct)).SingleOrDefault(s => s.StepId == command.StepId);
            if (task is null || original is null) return "原任务或运输步骤不在有效执行队列。";
            var step = new DispatchTaskStepWorkItem(task.TaskId, task.StationId, command.VehicleId, task.Stage,
                original.StepId, original.Sequence, original.StepType, original.Status,
                original.SourceSite, original.DestinationSite, original.UpdatedAt);
            return await CheckCoreAsync(step, payload.Path, activeMap.Current, preparing: false,
                recovering: command.AttemptCount > 0 && payload.WindowIndex == 0, ct,
                payload.WindowIndex > 0, payload.PreviousVendorTaskId,
                businessSource, businessDestination);
        }, ct);

    private async Task<string?> CheckCoreAsync(DispatchTaskStepWorkItem step, IReadOnlyList<string> path,
        ActiveMapRuntimeSnapshot? expectedMap, bool preparing, bool recovering, CancellationToken ct,
        bool continuationWindow = false, string? continuationFromVendorTaskId = null,
        string? businessSource = null, string? businessDestination = null)
    {
        if (path.Count < 2 || path.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(step.VehicleId))
            return "运输路径或指定车辆不完整。";
        if (executionGate.Mode is not (MapExecutionMode.Running or MapExecutionMode.Draining))
            return "地图切换或启动恢复屏障尚未放行执行。";
        if (preparing && step.StepType == DispatchTaskStepType.GoToPickup &&
            (!executionGate.CanStartNewWork || !dispatch.Enabled)) return "新增派车门禁尚未放行。";
        if (expectedMap is null || !ReferenceEquals(expectedMap, activeMap.Current)) return "运行地图缺失或已变化。";
        if (dispatch.EngineeringMapError(expectedMap) is { } mapError) return mapError;

        var current = await CurrentTasksAsync(ct);
        var task = current.SingleOrDefault(t => t.TaskId == step.TaskId);
        if (task is null || task.StationId != step.StationId) return "原任务已结束、冻结或工位归属不一致。";
        var original = (await taskReader.GetStepsAsync(step.TaskId, ct)).SingleOrDefault(s => s.StepId == step.StepId);
        if (original is null || original.StepType != step.StepType) return "原任务的运输步骤身份不一致。";
        var expectedStage = step.StepType switch
        {
            DispatchTaskStepType.GoToPickup => DispatchTaskStage.WaitingForVehicle,
            DispatchTaskStepType.DeliverFullBoxToDumper => DispatchTaskStage.PickingUp,
            DispatchTaskStepType.EnterDumpingPosition => DispatchTaskStage.WaitingForDumper,
            DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStage.Dumping,
            _ => (DispatchTaskStage)(-1)
        };
        var continuationStateMatches = continuationWindow &&
            original.Status is DispatchTaskStepStatus.Executing or DispatchTaskStepStatus.CommandPending or DispatchTaskStepStatus.Sending or DispatchTaskStepStatus.RetryScheduled &&
            !string.IsNullOrWhiteSpace(continuationFromVendorTaskId) &&
            task.VendorTaskId == continuationFromVendorTaskId;
        if (preparing
                ? original.Status != DispatchTaskStepStatus.Ready || task.Stage != expectedStage
                : continuationWindow
                    ? !continuationStateMatches
                    : original.Status is not (DispatchTaskStepStatus.CommandPending or DispatchTaskStepStatus.RetryScheduled) ||
                        task.Stage != DispatchTaskStage.SubmittingToAgv ||
                        !SameSite(expectedMap.Definition, original.SourceSite, businessSource ?? path[0]) ||
                        !SameSite(expectedMap.Definition, original.DestinationSite, businessDestination ?? path[^1]))
            return "原任务或步骤状态已经变化，不准备或认领新命令。";
        var firstPreparation = preparing && step.StepType == DispatchTaskStepType.GoToPickup;
        // Later transport steps become Ready before the command binds them, so their
        // row has no vehicle yet. While preparing, the parent task owns the vehicle;
        // recovery/sending still requires the exact persisted step binding.
        var originalVehicleMatches = preparing
            ? original.VehicleId is null || original.VehicleId == step.VehicleId
            : original.VehicleId == step.VehicleId;
        if ((!firstPreparation && (task.VehicleId != step.VehicleId || !originalVehicleMatches)) ||
            firstPreparation && (!string.IsNullOrWhiteSpace(task.VehicleId) && task.VehicleId != step.VehicleId ||
                !string.IsNullOrWhiteSpace(original.VehicleId) && original.VehicleId != step.VehicleId))
            return "任务、步骤与车辆责任不一致。";
        if (current.Any(t => t.TaskId != step.TaskId && string.Equals(t.VehicleId, step.VehicleId, StringComparison.OrdinalIgnoreCase)))
            return "该车辆仍由其他持久化任务占用。";
        if (taskSwitchGuard is not null && !await taskSwitchGuard.AllowsVehicleAsync(step.VehicleId!, step.TaskId, null, ct))
            return "车辆受控换单责任尚未交接。";
        if (fleetTest?.Allows(step.TaskId, step.VehicleId!) == false) return "车辆联调责任不允许当前任务。";
        if (step.StepType == DispatchTaskStepType.GoToPickup && !recovering &&
            (!dispatch.AllowsVehicle(step.StationId, step.VehicleId!) || !TaskVehicleSelection.Accepts(task, step.VehicleId!) ||
                charging?.AllowsNewBusiness(step.VehicleId!) == false ||
                businessRun?.Returning == true && businessRun.Vehicle == step.VehicleId)) return "工位车辆池、充电或回待机责任不允许新增派车。";

        // A previous physical attempt may have been accepted. Its reconciliation
        // must use the same stable StepId even after movement/PLC consumption.
        // The Robokit gateway queries that identity before any write and refuses
        // a second physical send when acceptance remains uncertain.
        if (!recovering)
        {
            if (await CheckVehicleAsync(step.VehicleId!, path[0], step.StepType == DispatchTaskStepType.GoToPickup, ct) is { } vehicleError)
                return vehicleError;
            if (await CheckForwardClearanceAsync(step, path, ct) is { } forwardError) return forwardError;
            if (await CheckPlcAsync(step, preparing, continuationWindow, ct) is { } plcError) return plcError;
        }
        // Traffic requests and command payloads are both written from the same
        // planned AGV route. Preserve the route's site ids here so preparation,
        // persisted reservation and all send-time checks compare the same identity.
        if (await trafficResources.CheckStepReservationAsync(step.StepId, step.TaskId, step.VehicleId!,
            expectedMap.MapId, expectedMap.VersionId, path[0], path[^1], ct) is { } trafficError) return trafficError;
        if (!ReferenceEquals(expectedMap, activeMap.Current) || executionGate.Mode is not (MapExecutionMode.Running or MapExecutionMode.Draining))
            return "核验期间地图或执行门禁已变化。";
        return null;
    }

    private async Task<string?> CheckForwardClearanceAsync(DispatchTaskStepWorkItem step,
        IReadOnlyList<string> path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var runtime = activeMap.Current;
        if (runtime is null || path.Count < 2) return "当前运行地图缺失，不能核对前方车辆占用。";
        var logicalNodes = path.Select(site =>
            MapRouteSiteMapper.ResolveLogicalNodeId(runtime.Definition, site)).ToArray();
        var routeEdges = new string[logicalNodes.Length - 1];
        for (var index = 0; index < routeEdges.Length; index++)
        {
            var source = logicalNodes[index];
            var destination = logicalNodes[index + 1];
            var edge = runtime.Definition.Edges.FirstOrDefault(candidate =>
                string.Equals(candidate.From, source, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.To, destination, StringComparison.OrdinalIgnoreCase) ||
                candidate.Bidirectional &&
                string.Equals(candidate.From, destination, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.To, source, StringComparison.OrdinalIgnoreCase));
            if (edge is null) return "下方路线包含无法解析的相邻路段，不能核对前方车辆占用。";
            routeEdges[index] = edge.Id;
        }

        var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow();
        foreach (var other in vehicleStates.GetAll().Where(vehicle =>
                     !string.Equals(vehicle.VehicleId, step.VehicleId, StringComparison.OrdinalIgnoreCase)))
        {
            // A vehicle whose telemetry is stale or offline still blocks the route station it
            // was last seen at. Only the position projection below needs fresh evidence (R4).
            var fresh = other.Connection == ConnectionState.Online && other.Quality == SignalQuality.Good &&
                other.LastSuccessfulPollAt is { } observed && observed <= now.AddSeconds(1) &&
                now - observed <= TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds);

            var stationIndex = -1;
            if (!string.IsNullOrWhiteSpace(other.CurrentStation))
            {
                var logicalStation = MapRouteSiteMapper.ResolveLogicalNodeId(runtime.Definition, other.CurrentStation);
                stationIndex = Array.FindIndex(logicalNodes, node =>
                    string.Equals(node, logicalStation, StringComparison.OrdinalIgnoreCase));
            }
            if (stationIndex > 0)
                return $"前方路线已有{other.VehicleId}占用：当前车辆{step.VehicleId}必须等待前车通过{path[stationIndex]}后再申请路权。";

            // During motion the vendor may return an empty current_station. Use the
            // same bounded map projection as traffic monitoring so a vehicle on one
            // of this window's directed edges still blocks the vehicle behind it.
            if (stationIndex < 0 && fresh)
            {
                var position = AgvMapPositionAnalyzer.Analyze(other.X, other.Y, other.LocalizationConfidence,
                    other.LastSuccessfulPollAt, runtime.Definition, now,
                    TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds),
                    mapOptions.MinimumLocalizationConfidence, mapOptions.MaximumRouteDeviationMeters);
                if (position.State == AgvMapPositionState.Normal && position.NearestEdgeId is { } edgeId &&
                    routeEdges.Contains(edgeId, StringComparer.OrdinalIgnoreCase))
                    return $"前方路线已有{other.VehicleId}在路段{edgeId}行驶：当前车辆{step.VehicleId}必须等待前车通过后再申请路权。";
            }
        }
        return null;
    }

    private static bool SameSite(MapDefinition? map, string? recorded, string? current) =>
        MapRouteSiteMapper.IsSameNode(map, recorded, current);

    private async Task<string?> CheckVehicleAsync(string vehicleId, string source, bool newBusiness, CancellationToken ct)
    {
        var vehicle = (await agvGateway.GetVehiclesAsync(ct)).SingleOrDefault(v => v.VehicleId == vehicleId);
        if (vehicle?.Connection != ConnectionState.Online || !vehicleStates.TryGet(vehicleId, out var robot) ||
            robot.Connection != ConnectionState.Online || robot.Quality != SignalQuality.Good)
            return "车辆离线或遥测质量无效。";
        var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow();
        if (robot.LastSuccessfulPollAt is not { } observed || observed > now.AddSeconds(1) ||
            now - observed > TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds)) return "车辆观察已过期或时间异常。";
        if (robot.EmergencyStopped != false || robot.Blocked != false || AgvObstaclePolicy.IsBlocked(robot) || AgvObstaclePolicy.HasBlockingAlarm(robot))
            return "车辆急停、阻挡、阻塞报警或健康状态未知。";
        if (robot.Tasks.Any(t => t.Status is 1 or 2 or 3) ||
            !dispatch.IdleOperatingStates.Contains(vehicle.OperatingState, StringComparer.OrdinalIgnoreCase)) return "车辆存在未结束的车端任务或尚未空闲。";
        var definition = activeMap.Current?.Definition;
        if (!SameSite(definition, vehicle.CurrentStation, source) ||
            !SameSite(definition, robot.CurrentStation, source))
            return "车辆实际起点与待发送路径不一致。";
        var position = AgvMapPositionAnalyzer.Analyze(robot.X, robot.Y, robot.LocalizationConfidence,
            robot.LastSuccessfulPollAt, activeMap.Current?.Definition, now, TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds),
            mapOptions.MinimumLocalizationConfidence, mapOptions.MaximumRouteDeviationMeters);
        if (position.State != AgvMapPositionState.Normal) return "车辆定位核验未通过：" + position.Message;
        var minimum = (decimal)(optimization?.Options.MinimumBatteryPercent ?? 20);
        if (newBusiness && (robot.BatteryPercent is null or < 0 or > 100 || robot.BatteryPercent < minimum ||
            vehicle.BatteryPercent is < 0 or > 100 || vehicle.BatteryPercent < minimum))
            return "电量未知或低于新业务最低电量。";
        return null;
    }

    private async Task<string?> CheckPlcAsync(DispatchTaskStepWorkItem step, bool preparing,
        bool continuationWindow, CancellationToken ct)
    {
        var allSteps = await taskReader.GetStepsAsync(step.TaskId, ct);
        var first = allSteps.FirstOrDefault(s => s.StepType == DispatchTaskStepType.GoToPickup);
        var binding = first is null || handshakes is null ? null : await handshakes.GetAsync(step.TaskId, first.StepId, ct);
        if (handshakeOptions.Enabled)
        {
            if (handshake is null || handshakes is null || handshakeOptions.PlcContractVersion != HandshakeExecutionOptions.Version)
                return "已启用的28阶段契约缺少匹配执行器、持久化绑定或版本。";
            if (!preparing) await handshake.ValidatePendingTransportAsync(step.TaskId, step.StepId, step.VehicleId!,
                continuationWindow, ct);
            else if (step.StepType == DispatchTaskStepType.GoToPickup) await handshake.BindBeforeTransportAsync(step, ct);
            else await handshake.ValidateTransportAsync(step, ct);
            return null;
        }
        if (binding is not null) return "原任务绑定28阶段契约，禁止降级为旧握手发送。";
        var conditions = new List<(string Station, string Variable, bool Expected)>
        {
            (step.StationId, "Faulted", false), (step.StationId, "CommunicationOnline", true),
            (creation.DumperStationId, "Faulted", false)
        };
        if (step.StepType == DispatchTaskStepType.GoToPickup)
        {
            conditions.Add((step.StationId, "BoxPresent", true));
            conditions.Add((step.StationId, "FullBoxRequestAcknowledged", true));
        }
        else
        {
            var priorType = step.StepType switch
            {
                DispatchTaskStepType.DeliverFullBoxToDumper => DispatchTaskStepType.PickupFullBox,
                DispatchTaskStepType.EnterDumpingPosition => DispatchTaskStepType.WaitForDumperReady,
                DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStepType.DumpFullBox,
                _ => (DispatchTaskStepType)(-1)
            };
            if (!allSteps.Any(s => s.StepType == priorType && s.Status == DispatchTaskStepStatus.Completed))
                return "后续运输缺少原任务前序步骤完成依据。";
            conditions.Add((step.StationId, "EmptyBoxTaskAcknowledged", true));
            if (step.StepType == DispatchTaskStepType.DeliverFullBoxToDumper) conditions.Add((step.StationId, "BoxPresent", false));
            if (step.StepType == DispatchTaskStepType.EnterDumpingPosition) conditions.Add((creation.DumperStationId, "DumperReady", true));
            if (step.StepType == DispatchTaskStepType.ReturnEmptyBox) conditions.Add((creation.DumperStationId, "DumpingCompletedMayLeave", true));
        }
        var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow();
        foreach (var condition in conditions)
        {
            signals.TryGet(condition.Station, condition.Variable, out var sample);
            var result = TaskSignalPredicate.EvaluateFresh(condition.Station, condition.Variable, condition.Expected,
                sample, now, handshakeOptions.StateMaxAgeSeconds, "发送前复核既有PLC条件");
            if (!result.Satisfied) return $"PLC发送条件未成立：{condition.Station}/{condition.Variable}；{result.Reason}";
        }
        return null;
    }

    private async Task<DispatchTaskWorkItem[]> CurrentTasksAsync(CancellationToken ct) =>
        (await tasks.GetDispatchCandidatesAsync(int.MaxValue, ct)).Concat(await tasks.GetSubmittingTasksAsync(ct))
            .Concat(await tasks.GetAssignedTasksAsync(ct)).DistinctBy(t => t.TaskId).ToArray();

    private async Task<string?> ObserveAsync(Func<Task<string?>> check, CancellationToken ct)
    {
        string? reason;
        try { reason = await check(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { reason = "安全复核未通过：" + ex.Message; }
        monitor?.ReportLayer("safety", reason is null ? "Ready" : "Blocked",
            reason ?? "原任务、地图、车辆、PLC契约与持久化路权复核通过。", 1);
        return reason;
    }
}
