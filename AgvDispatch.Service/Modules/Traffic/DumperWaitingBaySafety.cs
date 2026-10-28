using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

/// <summary>Independent physical bays, each capacity one. This does not certify geometric collision avoidance.</summary>
public static class DumperWaitingBaySafety
{
    public static string? GetError(MapDefinition? map, DispatchOrchestratorOptions dispatch, bool holdsRouteLeases)
    {
        if (dispatch.MaxConcurrentTasks <= 1 && dispatch.GetDumperWaitingSites().Count <= 1) return null;
        if (!holdsRouteLeases) return "多车候车位保护要求启用当前28阶段契约，持续保持路径路权至任务闭环。";
        if (map is null) return "多车候车位保护缺少当前运行地图。";
        var dumping = Resolve(map, dispatch.DumperDumpingSite);
        var sites=dispatch.GetDumperWaitingSites();
        var waiting=sites.Select(site=>Resolve(map,site)).ToArray();
        if(sites.Count==0 || dumping is null || waiting.Any(n=>n is null || n.Id.Equals(dumping.Id,StringComparison.OrdinalIgnoreCase)) ||
            waiting.Select(n=>n!.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=waiting.Length)
            return "多车模式需配置唯一且不同的等待节点与倒料节点；业务编号或厂家站点编号必须唯一映射。";
        foreach(var site in sites)
            if(ExclusiveResourceIds(map,dispatch,site).Count==0)
                return "每个候车节点必须绑定容量为1、且不与倒料点或其他候车位共用的独立交通资源；不允许两车同时驶入单等待位。";
        return null;
    }

    public static IReadOnlySet<string> ExclusiveResourceIds(MapDefinition map, DispatchOrchestratorOptions dispatch,string? selectedSite=null)
    {
        var dumping = Resolve(map, dispatch.DumperDumpingSite);
        var all=dispatch.GetDumperWaitingSites().Select(site=>Resolve(map,site)).Where(n=>n is not null).ToArray();
        var chosen=selectedSite is null?all:all.Where(n=>n!.Id.Equals(Resolve(map,selectedSite)?.Id,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(dumping is null)return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return map.Resources.Where(r=>r.Capacity==1 && !dumping.ResourceIds.Contains(r.Id,StringComparer.OrdinalIgnoreCase) &&
            chosen.Any(n=>n!.ResourceIds.Contains(r.Id,StringComparer.OrdinalIgnoreCase)) &&
            all.Count(n=>n!.ResourceIds.Contains(r.Id,StringComparer.OrdinalIgnoreCase))==1)
            .Select(r=>r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static MapNode? Resolve(MapDefinition map, string? site)
    {
        var nodes = map.Nodes.Where(n => n.Enabled &&
            (n.Id.Equals(site, StringComparison.OrdinalIgnoreCase) || site is not null && string.Equals(n.AgvSiteId, site, StringComparison.OrdinalIgnoreCase))).ToArray();
        return nodes.Length == 1 ? nodes[0] : null;
    }
}
