using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

/// <summary>Optional no-through clearance around a dumper; its own approach and return remain eligible.</summary>
public static class DumperTransitClearance
{
    public static IReadOnlyList<MapDirectedTraversal> BlockedTraversals(
        MapDefinition map, string source, string destination, string dumpingSite, double clearance)
    {
        if (clearance == 0) return [];
        if (!double.IsFinite(clearance) || clearance is < 0 or > 50 || map.Unit != "m")
            throw new ArgumentException("倒料位通行净距无效，保持原任务等待。");
        var dumpId = MapRouteSiteMapper.ResolveLogicalNodeId(map, dumpingSite);
        var dump = map.Nodes.SingleOrDefault(node => node.Enabled && node.Id.Equals(dumpId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("倒料位缺少唯一有效地图节点，不能核对通行净距。");
        if (source.Equals(dump.Id, StringComparison.OrdinalIgnoreCase) || destination.Equals(dump.Id, StringComparison.OrdinalIgnoreCase))
            return [];
        var nodes = map.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var blocked = new List<MapDirectedTraversal>();
        foreach (var edge in map.Edges.Where(edge => edge.Enabled))
        {
            if (!nodes.TryGetValue(edge.From, out var from) || !nodes.TryGetValue(edge.To, out var to))
                throw new ArgumentException("道路端点缺失，不能核对倒料位通行净距。");
            var distance = MapEdgeGeometry.DistanceTo(edge, from, to, dump.X, dump.Y);
            if (!double.IsFinite(distance)) throw new ArgumentException("道路几何不可用，不能核对倒料位通行净距。");
            if (distance > clearance) continue;
            blocked.Add(new(edge.Id, edge.From, edge.To));
            if (edge.Bidirectional) blocked.Add(new(edge.Id, edge.To, edge.From));
        }
        return blocked;
    }
}
