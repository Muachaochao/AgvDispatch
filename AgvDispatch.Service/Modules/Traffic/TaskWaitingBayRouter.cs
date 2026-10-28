using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public static class TaskWaitingBayRouter
{
    // Choose and reserve together before registering a vehicle command. A failed command
    // preparation can release its leases, but cannot change the task's recorded bay.
    public static async Task<(string Site, RoutePlan Plan)?> AcquireAsync(
        DispatchTaskStepWorkItem step, string pickup, DispatchOrchestratorOptions options,
        ActiveMapRuntimeSnapshot map, IRoutePlanner planner, ITrafficResourceManager traffic,
        ITaskWaitingBayAllocator allocator, CancellationToken ct)
    {
        var selected = await allocator.GetAsync(step.TaskId, ct);
        if (selected is not null && (selected.VehicleId != step.VehicleId || selected.StepId != step.StepId ||
            selected.MapId != map.MapId || selected.MapVersionId != map.VersionId ||
            !options.GetDumperWaitingSites().Contains(selected.WaitingSite, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("原任务候车位与车辆、步骤、地图或当前配置不一致，保持原占用待核对。");
        var sites = selected is null ? options.GetDumperWaitingSites() : [selected.WaitingSite];
        var unavailable = await traffic.GetUnavailableResourceIdsAsync(step.TaskId, ct);
        var candidates = sites.Select(site => (Site: site, Plan: planner.Plan(pickup, site, unavailable)))
            .Where(c => c.Plan is { Nodes.Count: >= 2 }).OrderBy(c => c.Plan!.Cost).ThenBy(c => c.Site, StringComparer.Ordinal).ToArray();
        foreach (var candidate in candidates)
        {
            var resource = selected?.ResourceId ?? DumperWaitingBaySafety.ExclusiveResourceIds(map.Definition, options, candidate.Site)
                .Order(StringComparer.Ordinal).FirstOrDefault();
            if (resource is null || !candidate.Plan!.ResourceIds.Contains(resource, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("候车路线未包含该等待节点的独立容量1资源。");
            await traffic.RecordRouteAsync(new(step.StepId, step.TaskId, step.VehicleId!, pickup, candidate.Site,
                candidate.Plan.EdgeIds, candidate.Plan.ResourceIds, false, "申请独立候车位；选位与占用在同一事务保存。")
                { MapId = map.MapId, MapVersionId = map.VersionId }, ct);
            var binding = selected ?? new(step.TaskId, step.VehicleId!, step.StepId, map.MapId, map.VersionId, candidate.Site, resource);
            if ((await allocator.TryAcquireAsync(binding, candidate.Plan.ResourceIds, ct)).Acquired)
                return (candidate.Site, candidate.Plan);
        }
        if (candidates.Length == 0)
        {
            var site = selected?.WaitingSite ?? sites[0];
            var diagnostic = planner.Plan(pickup, site, new HashSet<string>());
            await traffic.RecordRouteAsync(new(step.StepId, step.TaskId, step.VehicleId!, pickup, site,
                diagnostic?.EdgeIds ?? [], diagnostic?.ResourceIds ?? [], true,
                "候车位或其路径不可用，保持在原取箱点；未准备或发送命令，取消和心跳过期不自动腾位。")
                { MapId = map.MapId, MapVersionId = map.VersionId }, ct);
        }
        return null;
    }
}
