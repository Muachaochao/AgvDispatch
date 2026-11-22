using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public static class TaskWaitingBayRouter
{
    public static async Task ObserveWaitingAsync(DispatchTaskStepWorkItem step, string pickup, DispatchOrchestratorOptions options,
        ActiveMapRuntimeSnapshot map, IRoutePlanner planner, ITrafficResourceManager traffic,
        ITaskWaitingBayAllocator allocator, DispatchTaskStepOptions stepOptions,
        DispatchTrafficReservationService trafficLayer, CancellationToken ct,
        string? navigationCommandMode = null, GlobalPickupRoutePlanner? rolling = null,
        IReadOnlyCollection<string>? vehicleIds = null)
    {
        var selected = await allocator.GetAsync(step.TaskId, ct);
        if (selected is not null && (selected.VehicleId != step.VehicleId || selected.StepId != step.StepId ||
            selected.MapId != map.MapId || selected.MapVersionId != map.VersionId)) return;
        if (selected is not null &&
            !DumperWaitingBaySafety.IsPhysicalWaitingSite(map.Definition, selected.WaitingSite, options.DumperDumpingSite))
            return;
        var sites = selected is null
            ? CandidateSites(pickup, options, map, rolling, vehicleIds)
            : [selected.WaitingSite];
        var unavailable = await traffic.GetUnavailableResourceIdsAsync(step.TaskId, ct);
        var counts = await allocator.GetSelectionCountsAsync(map.MapId, map.VersionId, ct);
        var ranked = Rank(sites, pickup, planner, unavailable, counts, map, options, selected?.ResourceId, rolling, vehicleIds).FirstOrDefault();
        if (ranked.Plan is null)
        {
            await ObserveReferenceAsync(step, pickup, sites, planner, unavailable, map, trafficLayer, ct);
            return;
        }
        var first = (ranked.Windows ?? TransportRouteWindows.Plan(
            ranked.Plan, map, stepOptions, navigationCommandMode))[0];
        var resources = first.ResourceIds.Append(ranked.Resource).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await trafficLayer.ObserveAsync(new(step.StepId, step.TaskId, step.VehicleId!, first.Source, first.Destination,
            first.EdgeIds, resources, true, "独立候车位和首个路权窗口按原任务优先级排队；尚未选位或授予路权。")
            { MapId = map.MapId, MapVersionId = map.VersionId }, ct);
    }

    // Choose and reserve together before registering a vehicle command. A failed command
    // preparation can release its leases, but cannot change the task's recorded bay.
    public static async Task<(string Site, RoutePlan Plan, IReadOnlyList<AgvTransportRouteWindow> Windows)?> AcquireAsync(
        DispatchTaskStepWorkItem step, string pickup, DispatchOrchestratorOptions options,
        ActiveMapRuntimeSnapshot map, IRoutePlanner planner, ITrafficResourceManager traffic,
        ITaskWaitingBayAllocator allocator, DispatchTaskStepOptions stepOptions,
        CancellationToken ct, DispatchTrafficReservationService? trafficLayer = null,
        string? navigationCommandMode = null, GlobalPickupRoutePlanner? rolling = null,
        IReadOnlyCollection<string>? vehicleIds = null)
    {
        var selected = await allocator.GetAsync(step.TaskId, ct);
        if (selected is not null && (selected.VehicleId != step.VehicleId || selected.StepId != step.StepId ||
            selected.MapId != map.MapId || selected.MapVersionId != map.VersionId ||
            !DumperWaitingBaySafety.IsPhysicalWaitingSite(map.Definition, selected.WaitingSite, options.DumperDumpingSite)))
            throw new InvalidOperationException("原任务候车位与车辆、步骤、地图或当前配置不一致，保持原占用待核对。");
        var sites = selected is null
            ? CandidateSites(pickup, options, map, rolling, vehicleIds)
            : [selected.WaitingSite];
        var unavailable = await traffic.GetUnavailableResourceIdsAsync(step.TaskId, ct);
        var counts = await allocator.GetSelectionCountsAsync(map.MapId, map.VersionId, ct);
        var layer = trafficLayer ?? new DispatchTrafficReservationService(traffic);
        var candidates = Rank(sites, pickup, planner, unavailable, counts, map, options, selected?.ResourceId, rolling, vehicleIds);
        foreach (var candidate in candidates)
        {
            var resource = candidate.Resource;
            if (!candidate.Plan!.ResourceIds.Contains(resource, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("候车路线未包含该等待节点的独立容量1资源。");
            var windows = (candidate.Windows ?? TransportRouteWindows.Plan(
                    candidate.Plan, map, stepOptions, navigationCommandMode))
                .Select(window => window with
                {
                    ResourceIds = window.ResourceIds.Append(resource).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase).ToArray()
                }).ToArray();
            var first = windows[0];
            var observation = new TrafficRouteObservation(step.StepId, step.TaskId, step.VehicleId!, first.Source, first.Destination,
                first.EdgeIds, first.ResourceIds, false, $"选择候车位{candidate.Site}（本地图已选{counts.GetValueOrDefault(candidate.Site)}次）；仅在可达且路权可申请的泊位中均衡选择，保留原绑定和离位核验。")
                { MapId = map.MapId, MapVersionId = map.VersionId };
            var binding = selected ?? new(step.TaskId, step.VehicleId!, step.StepId, map.MapId, map.VersionId, candidate.Site, resource);
            var result = await layer.AcquireWaitingBayAsync(observation, binding, allocator, ct);
            if (result.Acquired)
                return (candidate.Site, candidate.Plan, windows);
        }
        if (candidates.Length == 0)
        {
            await ObserveReferenceAsync(step, pickup, sites, planner, unavailable, map, layer, ct);
        }
        return null;
    }

    private static Task ObserveReferenceAsync(DispatchTaskStepWorkItem step, string pickup,
        IReadOnlyList<string> sites, IRoutePlanner planner, IReadOnlySet<string> unavailable,
        ActiveMapRuntimeSnapshot map, DispatchTrafficReservationService trafficLayer, CancellationToken ct)
    {
        var noResourceExclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var diagnostic = sites.Select(site => (Site: site, Plan: planner.Plan(pickup, site, noResourceExclusions)))
            .Where(candidate => candidate.Plan is { Nodes.Count: >= 2 } && double.IsFinite(candidate.Plan.Cost))
            .OrderBy(candidate => candidate.Plan!.Cost).ThenBy(candidate => candidate.Site, StringComparer.Ordinal)
            .FirstOrDefault();
        var route = diagnostic.Plan;
        var resources = route?.ResourceIds ?? [];
        var reason = route is null ? "当前找不到可达候车位，车辆留在取箱点。"
            : $"当前没有可执行的候车路线，车辆留在取箱点。参考路线关联 {resources.Count(unavailable.Contains)} 项占满资源。";
        return trafficLayer.SuspendRecommendationAsync(new(step.StepId, step.TaskId, step.VehicleId!, pickup,
            diagnostic.Site ?? "", route?.EdgeIds ?? [], resources, true, reason)
            { MapId = map.MapId, MapVersionId = map.VersionId, WaitingCategory = "ReferenceRoute" }, ct);
    }

    private const int MaximumMapHoldingCandidates = 12;

    private static IReadOnlyList<string> CandidateSites(string pickup, DispatchOrchestratorOptions options,
        ActiveMapRuntimeSnapshot map, GlobalPickupRoutePlanner? rolling, IReadOnlyCollection<string>? vehicleIds)
    {
        var configured = options.GetDumperWaitingSites()
            .Where(site => !site.Equals(options.DumperDumpingSite, StringComparison.OrdinalIgnoreCase));
        var mapDeclared = rolling?.SafeHoldingSites(map, vehicleIds ?? []) ?? [];
        var pickupNode = map.Graph.FindRouteNode(pickup);
        var nearestMapSites = pickupNode is null
            ? mapDeclared
            : mapDeclared
                .Select(site => (Site: site, Node: map.Graph.FindRouteNode(site)))
                .Where(item => item.Node is not null)
                .OrderBy(item => Math.Sqrt(
                    Math.Pow(item.Node!.X - pickupNode.X, 2) +
                    Math.Pow(item.Node.Y - pickupNode.Y, 2)))
                .ThenBy(item => item.Site, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumMapHoldingCandidates)
                .Select(item => item.Site)
                .ToArray();
        return configured.Concat(nearestMapSites)
            .Where(site => !string.IsNullOrWhiteSpace(site))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Balance only among near-equivalent feasible routes. A history preference
    // must never select a long detour, ignore an occupied resource or rebind a task.
    private static (string Site, string Resource, RoutePlan? Plan, IReadOnlyList<AgvTransportRouteWindow>? Windows, double Cost)[] Rank(
        IReadOnlyList<string> sites, string pickup, IRoutePlanner planner, IReadOnlySet<string> unavailable,
        IReadOnlyDictionary<string, int> counts, ActiveMapRuntimeSnapshot map, DispatchOrchestratorOptions options,
        string? boundResource, GlobalPickupRoutePlanner? rolling, IReadOnlyCollection<string>? vehicleIds)
    {
        var candidates = sites.Select(site => (Site: site, Resource: boundResource ??
                DumperWaitingBaySafety.ExclusiveResourceIds(map.Definition, options, site)
                    .Order(StringComparer.Ordinal).FirstOrDefault()))
            .Where(candidate => candidate.Resource is not null && !unavailable.Contains(candidate.Resource))
            .Select(candidate =>
        {
            var proposal = rolling?.PlanFollowup(planner, map, pickup, candidate.Site, unavailable, vehicleIds ?? []);
            var plan = rolling is null ? planner.Plan(pickup, candidate.Site, unavailable) : proposal?.Route;
            return (candidate.Site, Resource: candidate.Resource!, Plan: plan, Windows: proposal?.Windows,
                Cost: (plan?.Cost ?? double.PositiveInfinity) + (proposal?.DeferredSeconds ?? 0));
        }).Where(c => c.Plan is { Nodes.Count: >= 2 } && double.IsFinite(c.Cost)).ToArray();
        if (candidates.Length == 0) return [];
        var minimum = candidates.Min(c => c.Cost);
        // Cost is supplied by the active planner; use a relative band, not metres/seconds assumptions.
        var band = Math.Max(Math.Abs(minimum) * 0.15, 0.001);
        return candidates.OrderBy(c => c.Cost > minimum + band)
            .ThenBy(c => c.Cost <= minimum + band ? counts.GetValueOrDefault(c.Site) : int.MaxValue)
            .ThenBy(c => c.Cost).ThenBy(c => c.Site, StringComparer.Ordinal).ToArray();
    }
}
