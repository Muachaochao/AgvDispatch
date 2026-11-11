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
        string? navigationCommandMode = null)
    {
        var selected = await allocator.GetAsync(step.TaskId, ct);
        if (selected is not null && (selected.VehicleId != step.VehicleId || selected.StepId != step.StepId ||
            selected.MapId != map.MapId || selected.MapVersionId != map.VersionId)) return;
        var sites = selected is null ? options.GetDumperWaitingSites() : [selected.WaitingSite];
        var unavailable = await traffic.GetUnavailableResourceIdsAsync(step.TaskId, ct);
        var counts = await allocator.GetSelectionCountsAsync(map.MapId, map.VersionId, ct);
        var candidate = Rank(sites, pickup, planner, unavailable, counts).FirstOrDefault();
        if (candidate.Plan is null)
            candidate = sites.Select(site => (Site: site, Plan: planner.Plan(pickup, site, new HashSet<string>())))
                .Where(c => c.Plan is { Nodes.Count: >= 2 }).OrderBy(c => c.Plan!.Cost).ThenBy(c => c.Site, StringComparer.Ordinal).FirstOrDefault();
        if (candidate.Plan is null) return;
        var first = TransportRouteWindows.Plan(
            candidate.Plan, map.Definition, stepOptions, navigationCommandMode)[0];
        var waitingResource = selected?.ResourceId ??
            DumperWaitingBaySafety.ExclusiveResourceIds(map.Definition, options, candidate.Site)
                .Order(StringComparer.Ordinal).FirstOrDefault();
        var resources = string.IsNullOrWhiteSpace(waitingResource)
            ? first.ResourceIds
            : first.ResourceIds.Append(waitingResource).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
        string? navigationCommandMode = null)
    {
        var selected = await allocator.GetAsync(step.TaskId, ct);
        if (selected is not null && (selected.VehicleId != step.VehicleId || selected.StepId != step.StepId ||
            selected.MapId != map.MapId || selected.MapVersionId != map.VersionId ||
            !options.GetDumperWaitingSites().Contains(selected.WaitingSite, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("原任务候车位与车辆、步骤、地图或当前配置不一致，保持原占用待核对。");
        var sites = selected is null ? options.GetDumperWaitingSites() : [selected.WaitingSite];
        var unavailable = await traffic.GetUnavailableResourceIdsAsync(step.TaskId, ct);
        var counts = await allocator.GetSelectionCountsAsync(map.MapId, map.VersionId, ct);
        var candidates = Rank(sites, pickup, planner, unavailable, counts);
        foreach (var candidate in candidates)
        {
            var resource = selected?.ResourceId ?? DumperWaitingBaySafety.ExclusiveResourceIds(map.Definition, options, candidate.Site)
                .Order(StringComparer.Ordinal).FirstOrDefault();
            if (resource is null || !candidate.Plan!.ResourceIds.Contains(resource, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("候车路线未包含该等待节点的独立容量1资源。");
            var windows = TransportRouteWindows.Plan(
                    candidate.Plan, map.Definition, stepOptions, navigationCommandMode)
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
            var result = await (trafficLayer ?? new DispatchTrafficReservationService(traffic))
                .AcquireWaitingBayAsync(observation, binding, allocator, ct);
            if (result.Acquired)
                return (candidate.Site, candidate.Plan, windows);
        }
        if (candidates.Length == 0)
        {
            var diagnostics = sites.Select(site => (Site: site, Plan: planner.Plan(pickup, site, new HashSet<string>())))
                .Where(c => c.Plan is { Nodes.Count: >= 2 }).OrderBy(c => c.Plan!.Cost).ToArray();
            var diagnosticCandidate = diagnostics.FirstOrDefault();
            var site = diagnosticCandidate.Site ?? selected?.WaitingSite ?? sites[0];
            var diagnostic = diagnosticCandidate.Plan;
            var reasons = string.Join("；", sites.Select(s =>
            {
                var plan = diagnostics.FirstOrDefault(d => d.Site == s).Plan;
                var blocked = plan?.ResourceIds.Where(unavailable.Contains).ToArray() ?? [];
                return plan is null ? $"{s}无可执行地图路线" : $"{s}受占用资源{string.Join('、', blocked)}影响";
            }));
            await (trafficLayer ?? new DispatchTrafficReservationService(traffic)).ObserveAsync(new(step.StepId, step.TaskId, step.VehicleId!, pickup, site,
                diagnostic?.EdgeIds ?? [], diagnostic?.ResourceIds ?? [], true,
                "候车位或其路径不可用，保持在原取箱点；" + reasons + "。未发送命令，取消和心跳过期不自动腾位。")
                { MapId = map.MapId, MapVersionId = map.VersionId }, ct);
        }
        return null;
    }

    // Balance only among near-equivalent feasible routes. A history preference
    // must never select a long detour, ignore an occupied resource or rebind a task.
    private static (string Site, RoutePlan? Plan)[] Rank(IReadOnlyList<string> sites, string pickup,
        IRoutePlanner planner, IReadOnlySet<string> unavailable, IReadOnlyDictionary<string, int> counts)
    {
        var candidates = sites.Select(site => (Site: site, Plan: planner.Plan(pickup, site, unavailable)))
            .Where(c => c.Plan is { Nodes.Count: >= 2 } && double.IsFinite(c.Plan.Cost)).ToArray();
        if (candidates.Length == 0) return [];
        var minimum = candidates.Min(c => c.Plan!.Cost);
        // Cost is supplied by the active planner; use a relative band, not metres/seconds assumptions.
        var band = Math.Max(Math.Abs(minimum) * 0.15, 0.001);
        return candidates.OrderBy(c => c.Plan!.Cost > minimum + band)
            .ThenBy(c => c.Plan!.Cost <= minimum + band ? counts.GetValueOrDefault(c.Site) : int.MaxValue)
            .ThenBy(c => c.Plan!.Cost).ThenBy(c => c.Site, StringComparer.Ordinal).ToArray();
    }
}
