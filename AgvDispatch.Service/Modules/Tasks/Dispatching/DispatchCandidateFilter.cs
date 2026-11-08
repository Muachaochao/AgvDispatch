using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed class DispatchCandidateFilter(DispatchOrchestratorOptions options, IAgvGateway agvGateway,
    IAgvStateCache agvStates, IActiveMapRuntime activeMap, MapOptions mapOptions,
    IRoutePlanner routePlanner, ITrafficResourceManager trafficResources, DispatchRouteCache? routeCache = null,
    BusinessRunOwnership? businessRun = null, FleetTestOwnership? fleetTest = null,
    IChargingDispatchGuard? charging = null, ITaskSwitchGuard? taskSwitchGuard = null) : IDispatchCandidateFilter
{
    public string? CheckVehicle(AgvVehicleStatus vehicle, IReadOnlySet<string> occupied, DispatchAssignmentOptions policy)
    {
        if (occupied.Contains(vehicle.VehicleId)) return "已有派发任务或有效路权，保持原车辆责任。";
        if (vehicle.Connection != ConnectionState.Online) return "车辆离线。";
        if (!options.IdleOperatingStates.Contains(vehicle.OperatingState, StringComparer.OrdinalIgnoreCase)) return "车辆未空闲。";
        if (!agvStates.TryGet(vehicle.VehicleId, out var robot) || robot.Connection != ConnectionState.Online || robot.Quality != SignalQuality.Good)
            return "车辆遥测质量无效。";
        if (robot.EmergencyStopped != false || robot.Blocked != false || AgvObstaclePolicy.IsBlocked(robot) || AgvObstaclePolicy.HasBlockingAlarm(robot))
            return "车辆急停、阻挡、报警或状态未知。";
        if (robot.Tasks.Any(t => t.Status is 1 or 2 or 3)) return "车端已有未结束任务。";
        var position = AgvMapPositionAnalyzer.Analyze(robot.X, robot.Y, robot.LocalizationConfidence, robot.LastSuccessfulPollAt,
            activeMap.Current?.Definition, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds),
            mapOptions.MinimumLocalizationConfidence, mapOptions.MaximumRouteDeviationMeters);
        if (position.State != AgvMapPositionState.Normal) return "定位无效、过期或偏离地图。";
        if (string.IsNullOrWhiteSpace(vehicle.CurrentStation)) return "车辆当前站点未知。";
        if (robot.BatteryPercent is null || robot.BatteryPercent < (decimal)policy.MinimumBatteryPercent || vehicle.BatteryPercent < (decimal)policy.MinimumBatteryPercent ||
            robot.BatteryPercent > 100 || vehicle.BatteryPercent > 100)
            return "电量未知、无效或低于业务最低电量。";
        if (charging?.AllowsNewBusiness(vehicle.VehicleId) == false) return "充电责任或电量门禁尚未放行。";
        if (businessRun?.Returning == true && vehicle.VehicleId == businessRun.Vehicle) return "正在执行回待机位收尾。";
        return null;
    }

    public static (double Travel, double TrafficWait) EstimateRoute(RoutePlan route, ActiveMapRuntimeSnapshot? map, DispatchAssignmentOptions policy)
    {
        if (map is null || route.EdgeIds.Count == 0) return (route.Cost, 0);
        var edges = route.EdgeIds.Select(id => map.Definition.Edges.FirstOrDefault(e => e.Id == id)).ToArray();
        if (edges.Any(e => e is null)) return (route.Cost, 0);
        var graphTravel = edges.Sum(e => e!.Distance / (e.SpeedLimit > 0 ? e.SpeedLimit : 1));
        var travel = edges.Sum(e => e!.Distance / (e.SpeedLimit > 0 ? Math.Min(e.SpeedLimit, policy.NominalSpeedMetersPerSecond) : policy.NominalSpeedMetersPerSecond));
        return (travel, Math.Max(0, route.Cost - graphTravel));
    }

    public static IReadOnlyList<string> PhysicalPickupOccupants(
        string pickup,
        MapDefinition? definition,
        IEnumerable<AgvVehicleStatus> vehicles)
    {
        var pickupLogicalNode = definition is null ? pickup : MapRouteSiteMapper.ResolveLogicalNodeId(definition, pickup);
        return vehicles
            .Where(vehicle => !string.IsNullOrWhiteSpace(vehicle.CurrentStation))
            .Where(vehicle => string.Equals(
                definition is null ? vehicle.CurrentStation : MapRouteSiteMapper.ResolveLogicalNodeId(definition, vehicle.CurrentStation!),
                pickupLogicalNode, StringComparison.OrdinalIgnoreCase))
            .Select(vehicle => vehicle.VehicleId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> PhysicalIntermediateOccupants(
        RoutePlan route,
        MapDefinition? definition,
        IEnumerable<AgvVehicleStatus> vehicles,
        string movingVehicleId)
    {
        if (route.Nodes.Count <= 2) return [];
        var intermediate = route.Nodes
            .Skip(1)
            .Take(route.Nodes.Count - 2)
            .Select(node => definition is null ? node : MapRouteSiteMapper.ResolveLogicalNodeId(definition, node))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return vehicles
            .Where(vehicle => !vehicle.VehicleId.Equals(movingVehicleId, StringComparison.OrdinalIgnoreCase))
            .Where(vehicle => !string.IsNullOrWhiteSpace(vehicle.CurrentStation))
            .Where(vehicle => intermediate.Contains(
                definition is null
                    ? vehicle.CurrentStation!
                    : MapRouteSiteMapper.ResolveLogicalNodeId(definition, vehicle.CurrentStation!)))
            .Select(vehicle => vehicle.VehicleId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<DispatchCandidateGraph> BuildAsync(DispatchQueueWindow queue, DispatchAssignmentOptions policy,
        ActiveMapRuntimeSnapshot? map, CancellationToken ct)
    {
        if (policy.GetConfigurationError() is { } error) throw new ArgumentException(error, nameof(policy));
        var vehicles = (await agvGateway.GetVehiclesAsync(ct)).OrderBy(v => v.VehicleId, StringComparer.OrdinalIgnoreCase).ToArray();
        var reasons = vehicles.ToDictionary(v => v.VehicleId, v => CheckVehicle(v, queue.OccupiedVehicles, policy), StringComparer.OrdinalIgnoreCase);
        var result = new DispatchCandidateGraph { Vehicles = vehicles, GeneralReasons = reasons };
        var waitingRoutes = new List<TrafficRouteObservation>();
        var work = queue.Work.ToDictionary(t => t.TaskId, StringComparer.OrdinalIgnoreCase);
        foreach (var score in queue.Window)
        {
            ct.ThrowIfCancellationRequested();
            var task = work[score.TaskId];
            var rows = result.Diagnostics[task.TaskId] = [];
            var pickup = options.GetPickupSite(task.StationId);
            if (pickup is null || !queue.Ready.TryGetValue(task.TaskId, out var step))
            { result.TaskReasons[task.TaskId] = pickup is null ? "未配置取货站点。" : "首步骤尚未就绪。"; continue; }
            // Candidate builds can overlap the active-map refresh boundary. Use
            // the live runtime definition as a fallback so vendor IDs are still
            // resolved before the physical-occupancy guard runs.
            var definition = map?.Definition ?? activeMap.Current?.Definition;
            var physicalOccupants = PhysicalPickupOccupants(pickup, definition, vehicles);
            var unavailable = await trafficResources.GetUnavailableResourceIdsAsync(task.TaskId, ct);
            var returnRoute = policy.ReturnCostWeight == 0 ? null : options.StandbySites.Select(site => Plan(pickup, site, new HashSet<string>()))
                .Where(r => r is not null).OrderBy(r => r!.Cost).FirstOrDefault();
            var returnEstimate = returnRoute is null ? (Travel: 0d, TrafficWait: 0d) : EstimateRoute(returnRoute, map, policy);
            var returnSeconds = (returnEstimate.Travel + returnEstimate.TrafficWait) * policy.ReturnCostWeight;
            var targetZone = policy.SiteZones.FirstOrDefault(p =>
                MapRouteSiteMapper.IsSameNode(definition, p.Key, pickup)).Value;
            (AgvVehicleStatus Vehicle, RoutePlan Route)? blocked = null;
            foreach (var vehicle in vehicles)
            {
                var reason = reasons[vehicle.VehicleId];
                reason ??= !options.AllowsVehicle(task.StationId, vehicle.VehicleId) ? "工位车辆池不允许该车辆。" :
                    !TaskVehicleSelection.Accepts(task, vehicle.VehicleId) ? "任务限定其他车辆。" :
                    fleetTest?.Allows(task.TaskId, vehicle.VehicleId) == false ? "联调车辆责任不允许该任务。" : null;
                if (reason is null && taskSwitchGuard is not null && !await taskSwitchGuard.AllowsVehicleAsync(vehicle.VehicleId, task.TaskId, null, ct))
                    reason = "受控换单尚未交接。";
                if (reason is not null) { rows.Add(new(vehicle.VehicleId, 0, 0, reason, false)); continue; }
                var otherOccupants = physicalOccupants
                    .Where(occupant => !string.Equals(occupant, vehicle.VehicleId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (otherOccupants.Length > 0)
                {
                    rows.Add(new(vehicle.VehicleId, 0, 0,
                        $"目标取货站被{string.Join('、', otherOccupants)}实际占用，等待其驶离后再派车；不得提前锁定目标站路权。",
                        false));
                    continue;
                }
                var route = Plan(vehicle.CurrentStation!, pickup, unavailable);
                if (route is not { Nodes.Count: >= 2 })
                {
                    var nominal = unavailable.Count == 0 ? null : Plan(vehicle.CurrentStation!, pickup, new HashSet<string>());
                    if (nominal is { Nodes.Count: >= 2 } && (blocked is null || nominal.Cost < blocked.Value.Route.Cost)) blocked = (vehicle, nominal);
                    rows.Add(new(vehicle.VehicleId, 0, 0, nominal is null ? "无合法可达路线（含封路、同点取货闭合路径检查）。" : "必要公共资源容量已占用，等待释放。", false));
                    continue;
                }
                var intermediateOccupants = PhysicalIntermediateOccupants(route, definition, vehicles, vehicle.VehicleId);
                if (intermediateOccupants.Count > 0)
                {
                    rows.Add(new(vehicle.VehicleId, 0, 0,
                        $"路径中间节点被{string.Join('、', intermediateOccupants)}实际占用；先让当前车辆按原任务驶离，禁止驶入占用节点等待。",
                        false));
                    continue;
                }
                var battery = Math.Min(vehicle.BatteryPercent, agvStates.TryGet(vehicle.VehicleId, out var state) ? state.BatteryPercent ?? 0 : 0);
                var sourceZone = policy.SiteZones.FirstOrDefault(p => p.Key.Equals(vehicle.CurrentStation, StringComparison.OrdinalIgnoreCase)).Value;
                var estimate = EstimateRoute(route, map, policy);
                var candidate = new DispatchAssignmentCandidate(task.TaskId, vehicle.VehicleId, estimate.Travel)
                {
                    TrafficWaitSeconds = estimate.TrafficWait, ReturnSeconds = returnSeconds,
                    CrossZoneSeconds = sourceZone is not null && targetZone is not null && !sourceZone.Equals(targetZone, StringComparison.OrdinalIgnoreCase) ? policy.CrossZoneSeconds : 0,
                    BatteryRiskSeconds = policy.BatteryRiskSeconds * (1 - (double)battery / 100),
                    StandbyBonusSeconds = options.StandbySites.Contains(vehicle.CurrentStation!, StringComparer.OrdinalIgnoreCase) ? policy.StandbyBonusSeconds : 0
                };
                result.Edges.Add(candidate); result.Plans[(task.TaskId, vehicle.VehicleId)] = route;
                rows.Add(new(vehicle.VehicleId, candidate.TravelSeconds, candidate.TotalCostSeconds,
                    "已通过在线、定位、健康、电量、车辆池和路径筛选；发车前仍须实时核验路权。", true)
                { TrafficWaitSeconds = candidate.TrafficWaitSeconds, ReturnSeconds = candidate.ReturnSeconds, CrossZoneSeconds = candidate.CrossZoneSeconds,
                    BatteryRiskSeconds = candidate.BatteryRiskSeconds, StandbyBonusSeconds = candidate.StandbyBonusSeconds });
            }
            if (!result.Edges.Any(e => e.TaskId == task.TaskId) && blocked is { } b)
                waitingRoutes.Add(new(step.StepId, task.TaskId, b.Vehicle.VehicleId, b.Route.Nodes[0], b.Route.Nodes[^1],
                    b.Route.EdgeIds, b.Route.ResourceIds, true, "滚动窗口：必要公共资源尚未可用，保留呼叫顺序和动态优先级。")
                    { MapId = map?.MapId, MapVersionId = map?.VersionId, QueuePriority = score.EffectivePriority });
        }
        return result with { WaitingRoutes = waitingRoutes };
    }

    private RoutePlan? Plan(string source, string destination, IReadOnlySet<string> unavailable) =>
        routeCache is null ? routePlanner.Plan(source, destination, unavailable) : routeCache.Plan(source, destination, unavailable);
}
