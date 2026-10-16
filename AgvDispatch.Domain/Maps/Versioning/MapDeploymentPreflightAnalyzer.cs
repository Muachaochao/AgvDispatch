namespace AgvDispatch.Domain.Maps;

public sealed record MapRouteMatrixEntry(
    string Source,
    string Destination,
    bool Reachable,
    double? Cost,
    int EdgeCount,
    IReadOnlyList<string> ResourceIds,
    string Message)
{
    public string ResourceText => ResourceIds.Count == 0 ? "无" : string.Join(',', ResourceIds);
}

public sealed record MapDeploymentPreflightReport(
    int OperationalNodeCount,
    int RoutePairCount,
    int ReachablePairCount,
    int UnreachablePairCount,
    IReadOnlyList<string> MissingAgvSiteNodeIds,
    IReadOnlyList<string> DuplicateAgvSiteIds,
    IReadOnlyList<MapRouteMatrixEntry> Entries)
{
    public bool Ready => UnreachablePairCount == 0 &&
                         MissingAgvSiteNodeIds.Count == 0 &&
                         DuplicateAgvSiteIds.Count == 0;
}

public static class MapDeploymentPreflightAnalyzer
{
    public static MapDeploymentPreflightReport Analyze(
        MapDefinition definition,
        int maximumSuccessfulSamples = 200,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (maximumSuccessfulSamples < 0) throw new ArgumentOutOfRangeException(nameof(maximumSuccessfulSamples));

        var operational = definition.Nodes.Where(node => node.Enabled && node.Type is
                MapNodeType.Station or MapNodeType.WaitingPoint or MapNodeType.ChargingPoint)
            .ToArray();
        var missing = operational.Where(node => string.IsNullOrWhiteSpace(node.AgvSiteId))
            .Select(node => node.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        var duplicates = definition.Nodes.Where(node => !string.IsNullOrWhiteSpace(node.AgvSiteId))
            .GroupBy(node => node.AgvSiteId!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        MapGraph graph;
        try
        {
            graph = definition.ToGraph();
        }
        catch (Exception exception)
        {
            return new MapDeploymentPreflightReport(
                operational.Length, operational.Length * Math.Max(0, operational.Length - 1),
                0, operational.Length * Math.Max(0, operational.Length - 1), missing, duplicates,
                [new MapRouteMatrixEntry("-", "-", false, null, 0, [], $"无法建立路径图：{exception.Message}")]);
        }

        var failures = new List<MapRouteMatrixEntry>();
        var successes = new List<MapRouteMatrixEntry>();
        var reachable = 0;
        foreach (var source in operational)
        {
            foreach (var destination in operational)
            {
                if (ReferenceEquals(source, destination)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                var plan = graph.FindShortestRoute(source.Id, destination.Id);
                if (plan is null)
                {
                    failures.Add(new MapRouteMatrixEntry(
                        source.Id, destination.Id, false, null, 0, [], "按当前路段方向不可达"));
                    continue;
                }
                reachable++;
                if (successes.Count < maximumSuccessfulSamples)
                {
                    successes.Add(new MapRouteMatrixEntry(
                        source.Id, destination.Id, true, plan.Cost, plan.EdgeIds.Count,
                        plan.ResourceIds, "可达"));
                }
            }
        }

        var pairCount = operational.Length * Math.Max(0, operational.Length - 1);
        return new MapDeploymentPreflightReport(
            operational.Length, pairCount, reachable, pairCount - reachable, missing, duplicates,
            failures.Concat(successes).ToArray());
    }
}
