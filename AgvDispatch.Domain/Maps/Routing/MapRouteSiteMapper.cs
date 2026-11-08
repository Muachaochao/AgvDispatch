namespace AgvDispatch.Domain.Maps;

public static class MapRouteSiteMapper
{
    public static string ResolveLogicalNodeId(MapDefinition definition, string? nodeOrAgvSiteId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var normalized = NormalizeSiteId(nodeOrAgvSiteId);
        if (normalized is null) return "";
        return TryResolveNode(definition, normalized, out var node) ? node!.Id : normalized;
    }

    public static bool TryResolveNode(MapDefinition definition, string? nodeOrAgvSiteId, out MapNode? node)
    {
        ArgumentNullException.ThrowIfNull(definition);
        node = null;
        var normalized = NormalizeSiteId(nodeOrAgvSiteId);
        if (normalized is null) return false;

        // An engineer may use the same text as a node ID on one row and as an
        // AGV site ID on another. Only accept an alias that identifies one
        // logical node. Ambiguous aliases must fail closed rather than silently
        // selecting the first map row.
        var matches = definition.Nodes
            .Where(item =>
                item.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(item.AgvSiteId) &&
                 item.AgvSiteId.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length != 1) return false;
        node = matches[0].First();
        return true;
    }

    public static bool IsSameNode(MapDefinition? definition, string? left, string? right)
    {
        var normalizedLeft = NormalizeSiteId(left);
        var normalizedRight = NormalizeSiteId(right);
        if (normalizedLeft is null || normalizedRight is null) return false;
        if (definition is null)
            return normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
        return ResolveLogicalNodeId(definition, normalizedLeft)
            .Equals(ResolveLogicalNodeId(definition, normalizedRight), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSameSequence(MapDefinition? definition, IReadOnlyList<string>? left,
        IReadOnlyList<string>? right)
    {
        if (left is null || right is null || left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!IsSameNode(definition, left[index], right[index])) return false;
        }
        return true;
    }

    public static string? NormalizeSiteId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }

    public static RoutePlan ToAgvSitePlan(MapDefinition definition, RoutePlan logicalPlan)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(logicalPlan);
        return logicalPlan with
        {
            Nodes = logicalPlan.Nodes.Select(id =>
                TryResolveNode(definition, id, out var node) &&
                !string.IsNullOrWhiteSpace(node!.AgvSiteId)
                    ? node.AgvSiteId!
                    : id).ToArray()
        };
    }
}
