using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

/// <summary>Optional no-through clearance around a dumper; its own approach and return remain eligible.</summary>
public static class DumperTransitClearance
{
    public static IReadOnlyList<MapDirectedTraversal> BlockedTraversals(
        MapDefinition map, string source, string destination, string dumpingSite, double clearance,
        IReadOnlySet<string>? guardedApproachEdges = null, string? sharedResourceId = null)
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
        var shared = guardedApproachEdges is { Count: > 0 } && !string.IsNullOrWhiteSpace(sharedResourceId) &&
            nodes.TryGetValue(destination, out var dock) && dock.Enabled && dock.Type == MapNodeType.ChargingPoint &&
            dump.ResourceIds.Contains(sharedResourceId, StringComparer.OrdinalIgnoreCase)
            ? map.Resources.SingleOrDefault(resource => resource.Id.Equals(sharedResourceId, StringComparison.OrdinalIgnoreCase) &&
                resource.Capacity == 1 && resource.Boundary is { Count: >= 3 } boundary &&
                boundary.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y)) &&
                boundary.Max(point => point.X) > boundary.Min(point => point.X) &&
                boundary.Max(point => point.Y) > boundary.Min(point => point.Y))
            : null;
        var blocked = new List<MapDirectedTraversal>();
        foreach (var edge in map.Edges.Where(edge => edge.Enabled))
        {
            if (!nodes.TryGetValue(edge.From, out var from) || !nodes.TryGetValue(edge.To, out var to))
                throw new ArgumentException("道路端点缺失，不能核对倒料位通行净距。");
            var distance = MapEdgeGeometry.DistanceTo(edge, from, to, dump.X, dump.Y);
            if (!double.IsFinite(distance)) throw new ArgumentException("道路几何不可用，不能核对倒料位通行净距。");
            if (distance > clearance) continue;
            if (shared is not null && guardedApproachEdges!.Contains(edge.Id) &&
                edge.ResourceIds.Contains(shared.Id, StringComparer.OrdinalIgnoreCase)) continue;
            blocked.Add(new(edge.Id, edge.From, edge.To));
            if (edge.Bidirectional) blocked.Add(new(edge.Id, edge.To, edge.From));
        }
        return blocked;
    }
}
