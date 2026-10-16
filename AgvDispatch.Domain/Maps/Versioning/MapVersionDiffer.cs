namespace AgvDispatch.Domain.Maps;

public enum MapDifferenceKind
{
    Added,
    Removed,
    Modified
}

public sealed record MapDifference(string ObjectType, string ObjectId, MapDifferenceKind Kind, string Details);

public static class MapVersionDiffer
{
    public static IReadOnlyList<MapDifference> Compare(MapDefinition baseline, MapDefinition draft)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(draft);
        var result = new List<MapDifference>();
        CompareObjects(baseline.Nodes, draft.Nodes, node => node.Id, NodeFingerprint, "节点", result);
        CompareObjects(baseline.Edges, draft.Edges, edge => edge.Id, EdgeFingerprint, "路段", result);
        CompareObjects(baseline.Resources, draft.Resources, resource => resource.Id, ResourceFingerprint, "交通资源", result);
        return result.OrderBy(item => item.ObjectType, StringComparer.Ordinal)
            .ThenBy(item => item.ObjectId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void CompareObjects<T>(
        IEnumerable<T> baseline,
        IEnumerable<T> draft,
        Func<T, string> id,
        Func<T, string> fingerprint,
        string objectType,
        ICollection<MapDifference> result)
    {
        var before = baseline.GroupBy(id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var after = draft.GroupBy(id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var removed in before.Keys.Except(after.Keys, StringComparer.OrdinalIgnoreCase))
            result.Add(new MapDifference(objectType, removed, MapDifferenceKind.Removed, "草稿中已删除"));
        foreach (var added in after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase))
            result.Add(new MapDifference(objectType, added, MapDifferenceKind.Added, "草稿中已新增"));
        foreach (var common in before.Keys.Intersect(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (!fingerprint(before[common]).Equals(fingerprint(after[common]), StringComparison.Ordinal))
                result.Add(new MapDifference(objectType, common, MapDifferenceKind.Modified, "属性或关联关系已修改"));
        }
    }

    private static string NodeFingerprint(MapNode node) =>
        $"{node.X:R}|{node.Y:R}|{node.Type}|{node.Enabled}|{node.AgvSiteId}|{Sorted(node.ResourceIds)}";
    private static string EdgeFingerprint(MapEdge edge) =>
        $"{edge.From}|{edge.To}|{edge.Distance:R}|{edge.SpeedLimit:R}|{edge.Bidirectional}|{edge.Enabled}|{Sorted(edge.ResourceIds)}|{edge.GeometryKind}|{string.Join(';', edge.ControlPoints?.Select(point => $"{point.X:R},{point.Y:R}") ?? [])}";
    private static string ResourceFingerprint(TrafficResourceDefinition resource) =>
        $"{resource.Type}|{resource.Capacity}|{string.Join(';', resource.Boundary?.Select(point => $"{point.X:R},{point.Y:R}") ?? [])}";
    private static string Sorted(IEnumerable<string> values) => string.Join(';',
        values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
}
