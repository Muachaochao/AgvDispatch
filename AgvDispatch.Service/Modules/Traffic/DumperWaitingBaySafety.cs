using AgvDispatch.Application.Abstractions;
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
        if(dumping is null)return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chosen=selectedSite is null?all:new[]{Resolve(map,selectedSite)}.Where(n=>n is not null).ToArray();
        if (selectedSite is not null && !IsPhysicalWaitingSite(map, selectedSite, dispatch.DumperDumpingSite))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return map.Resources.Where(r=>r.Capacity==1 && !dumping.ResourceIds.Contains(r.Id,StringComparer.OrdinalIgnoreCase) &&
            chosen.Any(n=>n!.ResourceIds.Contains(r.Id,StringComparer.OrdinalIgnoreCase)) &&
            (selectedSite is not null || all.Count(n=>n!.ResourceIds.Contains(r.Id,StringComparer.OrdinalIgnoreCase))==1))
            .Select(r=>r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A selected bay must remain a stopping node after configuration changes.
    /// The selector applies the stronger vehicle-envelope clearance test before
    /// binding a map-declared holding site.
    /// </summary>
    public static bool IsPhysicalWaitingSite(MapDefinition map, string site, string dumpingSite)
    {
        var node = Resolve(map, site);
        var dumping = Resolve(map, dumpingSite);
        if (node is not { Enabled: true, Type: MapNodeType.WaitingPoint, AllowTrafficStop: not false } ||
            dumping is null || node.Id.Equals(dumping.Id, StringComparison.OrdinalIgnoreCase))
            return false;
        return map.Resources.Any(resource => resource.Capacity == 1 &&
            node.ResourceIds.Contains(resource.Id, StringComparer.OrdinalIgnoreCase) &&
            !dumping.ResourceIds.Contains(resource.Id, StringComparer.OrdinalIgnoreCase) &&
            map.Nodes.Count(other => other.Enabled &&
                other.ResourceIds.Contains(resource.Id, StringComparer.OrdinalIgnoreCase)) == 1);
    }

    /// <summary>
    /// Equipment admission and the complete entry route form one atomic request.
    /// A vehicle must never hold the dumper while waiting for an entrance segment
    /// held by another vehicle that is itself waiting for the dumper.
    /// </summary>
    public static IReadOnlyList<string>? EquipmentEntryResources(IRoutePlanner planner,
        string waitingSite, string dumpingSite, IReadOnlyCollection<string> equipmentResources)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentException.ThrowIfNullOrWhiteSpace(waitingSite);
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpingSite);
        ArgumentNullException.ThrowIfNull(equipmentResources);
        var route = planner.Plan(waitingSite, dumpingSite, new HashSet<string>());
        if (route is not { Nodes.Count: >= 2 }) return null;
        return equipmentResources.Concat(route.ResourceIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static MapNode? Resolve(MapDefinition map, string? site)
    {
        var nodes = map.Nodes.Where(n => n.Enabled &&
            (n.Id.Equals(site, StringComparison.OrdinalIgnoreCase) || site is not null && string.Equals(n.AgvSiteId, site, StringComparison.OrdinalIgnoreCase))).ToArray();
        return nodes.Length == 1 ? nodes[0] : null;
    }
}
