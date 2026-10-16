namespace AgvDispatch.Domain.Maps;

public static class MapRouteSiteMapper
{
    public static string ResolveLogicalNodeId(MapDefinition definition, string nodeOrAgvSiteId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeOrAgvSiteId);
        return definition.Nodes.FirstOrDefault(node =>
                   node.Id.Equals(nodeOrAgvSiteId, StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrWhiteSpace(node.AgvSiteId) &&
                    node.AgvSiteId.Equals(nodeOrAgvSiteId, StringComparison.OrdinalIgnoreCase)))?.Id
               ?? nodeOrAgvSiteId;
    }

    public static RoutePlan ToAgvSitePlan(MapDefinition definition, RoutePlan logicalPlan)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(logicalPlan);
        var nodes = definition.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        return logicalPlan with
        {
            Nodes = logicalPlan.Nodes.Select(id =>
                nodes.TryGetValue(id, out var node) && !string.IsNullOrWhiteSpace(node.AgvSiteId)
                    ? node.AgvSiteId!
                    : id).ToArray()
        };
    }
}
