using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

/// <summary>Logical single-bay capacity. This does not certify geometric collision avoidance.</summary>
public static class DumperWaitingBaySafety
{
    public static string? GetError(MapDefinition? map, DispatchOrchestratorOptions dispatch, bool holdsRouteLeases)
    {
        if (dispatch.MaxConcurrentTasks <= 1) return null;
        if (!holdsRouteLeases) return "多车候车位保护要求启用当前28阶段契约，持续保持路径路权至任务闭环。";
        if (map is null) return "多车候车位保护缺少当前运行地图。";
        var waiting = Resolve(map, dispatch.GetDumperWaitingSite());
        var dumping = Resolve(map, dispatch.DumperDumpingSite);
        if (waiting is null || dumping is null || waiting.Id.Equals(dumping.Id, StringComparison.OrdinalIgnoreCase))
            return "多车模式需配置唯一且不同的等待节点与倒料节点；业务编号或厂家站点编号必须唯一映射。";
        if (ExclusiveResourceIds(map, dispatch).Count == 0)
            return "多车等待节点必须绑定容量为1、且不与倒料节点共用的独立交通资源；不允许两车同时驶入单等待位。";
        return null;
    }

    public static IReadOnlySet<string> ExclusiveResourceIds(MapDefinition map, DispatchOrchestratorOptions dispatch)
    {
        var waiting = Resolve(map, dispatch.GetDumperWaitingSite());
        var dumping = Resolve(map, dispatch.DumperDumpingSite);
        if (waiting is null || dumping is null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return map.Resources.Where(r => r.Capacity == 1 && waiting.ResourceIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase) &&
            !dumping.ResourceIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase)).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static MapNode? Resolve(MapDefinition map, string? site)
    {
        var nodes = map.Nodes.Where(n => n.Enabled &&
            (n.Id.Equals(site, StringComparison.OrdinalIgnoreCase) || site is not null && string.Equals(n.AgvSiteId, site, StringComparison.OrdinalIgnoreCase))).ToArray();
        return nodes.Length == 1 ? nodes[0] : null;
    }
}
