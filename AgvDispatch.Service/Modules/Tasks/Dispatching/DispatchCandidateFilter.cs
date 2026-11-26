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
    IChargingDispatchGuard? charging = null, ITaskSwitchGuard? taskSwitchGuard = null,
    GlobalPickupRoutePlanner? pickupStaging = null, DispatchEnergyForecast? energyForecast = null) : IDispatchCandidateFilter
{
    private readonly AgvDispatch.Application.Tasks.TemporaryDetourDeferral detours = new();
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
            activeMap.Current?.Definition, AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(), TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds),
            mapOptions.MinimumLocalizationConfidence, mapOptions.MaximumRouteDeviationMeters, reuseGeometry: true);
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
        var edges = route.EdgeIds.Select(id => map.Graph.Edges.GetValueOrDefault(id)).ToArray();
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
        var planningRoutes = new CycleRoutePlanner(routePlanner, routeCache);
        var dumperWaitingSites = options.GetDumperWaitingSites();
        var reservedStageSites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var waitingRoutes = new List<TrafficRouteObservation>();
        var canStage = pickupStaging is not null && map is not null &&
            policy.Mode.Equals("Global", StringComparison.OrdinalIgnoreCase);
        var vehicleIds = vehicles.Select(vehicle => vehicle.VehicleId).ToArray();
        var work = queue.Work.ToDictionary(t => t.TaskId, StringComparer.OrdinalIgnoreCase);
        detours.RetainTasks(work.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        foreach (var score in queue.Window)
        {
            ct.ThrowIfCancellationRequested();
            var task = work[score.TaskId];
            var rows = result.Diagnostics[task.TaskId] = [];
            string? taskStageSite = null;
            var pickup = options.GetPickupSite(task.StationId);
            if (pickup is null || !queue.Ready.TryGetValue(task.TaskId, out var step))
            { result.TaskReasons[task.TaskId] = pickup is null ? "未配置取货站点。" : "首步骤尚未就绪。"; continue; }
            // Candidate builds can overlap the active-map refresh boundary. Use
            // the live runtime definition as a fallback so vendor IDs are still
            // resolved before the physical-occupancy guard runs.
            var definition = map?.Definition ?? activeMap.Current?.Definition;
            var physicalOccupants = PhysicalPickupOccupants(pickup, definition, vehicles);
            var unavailable = await trafficResources.GetUnavailableResourceIdsAsync(task.TaskId, ct);
            var returnRoute = policy.ReturnCostWeight == 0 ? null : options.StandbySites.Select(site =>
                    planningRoutes.Plan(pickup, site, new HashSet<string>()))
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
                var route = planningRoutes.Plan(vehicle.CurrentStation!, pickup, unavailable);
                var nominal = unavailable.Count == 0 || (route is not null && !canStage)
                    ? null : planningRoutes.Plan(vehicle.CurrentStation!, pickup, new HashSet<string>());
                var staged = canStage && nominal is { Nodes.Count: >= 3 }
                    ? pickupStaging!.TryStage(nominal, map!, unavailable, vehicleIds) : null;
                if (staged is null && canStage && unavailable.Count > 0)
                    staged = pickupStaging!.TryStageViaHolding(planningRoutes, map!, vehicle.CurrentStation!, pickup,
                        unavailable, vehicleIds, excludedHoldingSites: reservedStageSites);
                var proactiveChoice = false;
                if (staged is null && canStage && route is { Nodes.Count: >= 3 })
                {
                    var directFirst = pickupStaging!.PlanInitialWindows(route, map!)[0];
                    var proactive = pickupStaging.TryStageViaHolding(planningRoutes, map!,
                        vehicle.CurrentStation!, pickup, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        vehicleIds, allowClearRoute: true, excludedHoldingSites: reservedStageSites);
                    if (proactive is not null && ShouldUseProactiveStage(directFirst, proactive, route))
                    {
                        staged = proactive;
                        proactiveChoice = true;
                    }
                }
                if (staged is not null && (proactiveChoice || route is null ||
                    EffectiveRouteSeconds(staged.Route, map, policy) + staged.DeferredSeconds < EffectiveRouteSeconds(route, map, policy)))
                    route = staged.Route;
                else
                    staged = null;
                if (route is not { Nodes.Count: >= 2 })
                {
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
                DispatchCandidateEnergy? energy = null;
                if (energyForecast?.Enabled == true)
                {
                    energy = energyForecast.Estimate(vehicle.VehicleId, battery, pickup, options.DumperDumpingSite,
                        dumperWaitingSites, route, staged?.DeferredSeconds ?? 0, map, planningRoutes, policy);
                    if (energyForecast.Enforced && energy?.Safe != true)
                    {
                        var energyReason = energy is null ? "缺少完整业务或到桩参考路线，无法估算续航。"
                            : $"预计到桩 {energy.AtChargerBatteryPercent:0.0}%，扣除预测误差后低于安全余量 {energy.MinimumArrivalBatteryPercent:0.0}%。";
                        rows.Add(new(vehicle.VehicleId, 0, 0, energyReason, false) { Energy = energy });
                        continue;
                    }
                }
                var sourceZone = policy.SiteZones.FirstOrDefault(p =>
                    MapRouteSiteMapper.IsSameNode(definition, p.Key, vehicle.CurrentStation)).Value;
                var estimate = EstimateRoute(route, map, policy);
                // Compare only; the reference deliberately ignores temporary peers
                // and leases and must never enter Plans, Outbox or reservations.
                var reference = planningRoutes.PlanWithoutVehicleOccupancyForComparison(vehicle.CurrentStation!, pickup);
                var referenceTravel = reference is null ? estimate.Travel : EstimateRoute(reference, map, policy).Travel;
                var deferral = detours.RemainingSeconds(task.TaskId, vehicle.VehicleId, map?.VersionId ?? "",
                    estimate.Travel, referenceTravel, AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow());
                if (deferral > 0)
                {
                    rows.Add(new(vehicle.VehicleId, estimate.Travel, estimate.Travel,
                        $"临时占用导致大幅绕行：可行路线预计{estimate.Travel:0}秒，通道空闲参考{referenceTravel:0}秒；" +
                        $"先等待并重新匹配，最多再等{Math.Ceiling(deferral):0}秒。未给该候选分配车辆或锁定路线。", false));
                    continue;
                }
                var batteryRisk = 1 - (double)battery / 100;
                if (energyForecast?.Enforced == true && energy is not null)
                {
                    var arrivalRisk = 100 - energy.AtChargerBatteryPercent;
                    var consumption = energy.CurrentBatteryPercent - energy.AtChargerBatteryPercent;
                    batteryRisk = Math.Clamp((arrivalRisk + consumption) / 200, 0, 1);
                }
                var candidate = new DispatchAssignmentCandidate(task.TaskId, vehicle.VehicleId, estimate.Travel)
                {
                    TrafficWaitSeconds = estimate.TrafficWait + (staged?.DeferredSeconds ?? 0), ReturnSeconds = returnSeconds,
                    CrossZoneSeconds = sourceZone is not null && targetZone is not null && !sourceZone.Equals(targetZone, StringComparison.OrdinalIgnoreCase) ? policy.CrossZoneSeconds : 0,
                    BatteryRiskSeconds = policy.BatteryRiskSeconds * batteryRisk,
                    StandbyBonusSeconds = options.StandbySites.Contains(vehicle.CurrentStation!, StringComparer.OrdinalIgnoreCase) ? policy.StandbyBonusSeconds : 0
                };
                result.Edges.Add(candidate); result.Plans[(task.TaskId, vehicle.VehicleId)] = route;
                if (staged is not null) result.StagedPairs.Add((task.TaskId, vehicle.VehicleId));
                if (staged is not null) taskStageSite ??= staged.Windows[0].Destination;
                var candidateReason = staged is null ? "已通过在线、定位、健康、电量、车辆池和路径筛选；发车前仍须实时核验路权。"
                    : $"首窗可到候车点 {staged.Windows[0].Destination}；后续窗口占用计入预计等待，发车前仍须实时核验。";
                if (energyForecast?.Enabled == true && energy is null)
                    candidateReason += "缺少完整业务或到桩参考路线，当前只观察续航。";
                rows.Add(new(vehicle.VehicleId, candidate.TravelSeconds, candidate.TotalCostSeconds, candidateReason, true)
                { Energy = energy, TrafficWaitSeconds = candidate.TrafficWaitSeconds, ReturnSeconds = candidate.ReturnSeconds, CrossZoneSeconds = candidate.CrossZoneSeconds,
                    BatteryRiskSeconds = candidate.BatteryRiskSeconds, StandbyBonusSeconds = candidate.StandbyBonusSeconds });
            }
            if (!result.Edges.Any(e => e.TaskId == task.TaskId) && blocked is { } b)
            {
                var first = canStage
                    ? TransportRouteWindows.NormalizeResources(
                        pickupStaging!.PlanInitialWindows(b.Route, map!), map!)[0]
                    : null;
                var referenceOnly = map is null || first is not null && !first.ResourceIds.Any(unavailable.Contains);
                var window = referenceOnly ? null : first;
                waitingRoutes.Add(new(step.StepId, task.TaskId, b.Vehicle.VehicleId,
                    window?.Source ?? b.Route.Nodes[0], window?.Destination ?? b.Route.Nodes[^1],
                    window?.EdgeIds ?? b.Route.EdgeIds, window?.ResourceIds ?? b.Route.ResourceIds, true,
                    referenceOnly ? "参考路线：当前未找到可执行的首窗方案。" : "首窗必要资源尚未可用，按呼叫顺序和动态优先级等待。")
                {
                    MapId = map?.MapId, MapVersionId = map?.VersionId,
                    QueueEligible = !referenceOnly, QueuePriority = referenceOnly ? null : score.EffectivePriority,
                    WaitingCategory = referenceOnly ? "ReferenceRoute" : "Route"
                });
            }
            if (taskStageSite is not null) reservedStageSites.Add(taskStageSite);
        }
        return result with
        {
            WaitingRoutes = waitingRoutes,
            RouteMemoHits = planningRoutes.Hits,
            RouteMemoMisses = planningRoutes.Misses
        };
    }

    private static double EffectiveRouteSeconds(RoutePlan route, ActiveMapRuntimeSnapshot? map, DispatchAssignmentOptions policy)
    {
        var estimate = EstimateRoute(route, map, policy);
        return estimate.Travel + estimate.TrafficWait;
    }

    private static bool ShouldUseProactiveStage(AgvTransportRouteWindow direct,
        StagedPickupRoute staged, RoutePlan directRoute)
    {
        var first = staged.Windows[0];
        return direct.EdgeIds.Count >= 32 &&
            first.EdgeIds.Count * 2 <= direct.EdgeIds.Count &&
            staged.Route.Cost <= directRoute.Cost * 1.15;
    }

    private sealed class CycleRoutePlanner(IRoutePlanner fallback, DispatchRouteCache? cache) : IRoutePlanner
    {
        private readonly Dictionary<string, RoutePlan?> routes = new(StringComparer.Ordinal);
        public long Hits { get; private set; }
        public long Misses { get; private set; }

        public RoutePlan? Plan(string source, string destination, IReadOnlySet<string> unavailable)
        {
            var key = "P\u001f" + source + "\u001f" + destination + "\u001f" +
                string.Join('\u001e', unavailable.Order(StringComparer.OrdinalIgnoreCase));
            if (routes.TryGetValue(key, out var cached))
            {
                Hits++;
                return cached;
            }
            Misses++;
            var route = cache is null
                ? fallback.Plan(source, destination, unavailable)
                : cache.Plan(source, destination, unavailable);
            routes[key] = route;
            return route;
        }

        public RoutePlan? PlanWithoutVehicleOccupancyForComparison(string source, string destination) =>
            Compare(source, destination, false);

        public RoutePlan? PlanChargingApproachForComparison(string source, string destination) => Compare(source, destination, true);

        private RoutePlan? Compare(string source, string destination, bool charging)
        {
            var key = (charging ? "CH\u001f" : "C\u001f") + source + "\u001f" + destination;
            if (routes.TryGetValue(key, out var cached))
            {
                Hits++;
                return cached;
            }
            Misses++;
            var route = charging ? fallback.PlanChargingApproachForComparison(source, destination)
                : fallback.PlanWithoutVehicleOccupancyForComparison(source, destination);
            routes[key] = route;
            return route;
        }
    }
}
