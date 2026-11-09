using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

/// <summary>
/// Splits an already validated route into independently reserved windows ending
/// at stable map nodes. No route is recomputed here; edge orientation and source
/// geometry remain the responsibility of the active route planner.
/// </summary>
public static class TransportRouteWindows
{
    public static IReadOnlyList<AgvTransportRouteWindow> Plan(RoutePlan route, MapDefinition map,
        DispatchTaskStepOptions options, string? navigationCommandMode = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(options);
        // Routing uses the runtime-enriched graph, where reciprocal/merge edges
        // receive AUTO-CORRIDOR-* guards. The published map remains unchanged, so
        // window resources must be calculated against the same enriched topology.
        var topology = MapCorridorResources.Enrich(map);
        var resourceMap = map with { Edges = topology.Edges, Resources = topology.Resources };
        // A 3051 GotargetCommissioning task carries only source and destination in
        // the vendor request. Splitting it into several stops changes the operation
        // contract, so only SpecifiedPath/3066 may use rolling route windows.
        var modeSupportsWindows = navigationCommandMode is null ||
            navigationCommandMode.Equals("SpecifiedPath", StringComparison.OrdinalIgnoreCase);
        if (!options.RollingReservationEnabled || !modeSupportsWindows || route.Nodes.Count < 3)
            return [Window(route, resourceMap, 0, route.Nodes.Count - 1, true)];

        var minimum = Math.Clamp(options.RollingWindowMinimumEdges, 1, 100);
        var windows = new List<AgvTransportRouteWindow>();
        var start = 0;
        while (start < route.Nodes.Count - 1)
        {
            var remainingEdges = route.Nodes.Count - 1 - start;
            if (remainingEdges <= minimum)
            {
                windows.Add(Window(route, resourceMap, start, route.Nodes.Count - 1, true));
                break;
            }

            var lower = start + minimum;
            // MaximumEdges is a planning target, not permission to stop in the
            // middle of a corridor. Search forward for the nearest safe node even
            // when it lies beyond the preferred maximum. If the route contains no
            // interior safe node, keep the whole route as one atomic window.
            var boundary = -1;
            for (var index = lower; index < route.Nodes.Count - 1; index++)
            {
                if (!IsStableStop(route.Nodes[index], resourceMap)) continue;
                boundary = index;
                break;
            }

            if (boundary < 0)
            {
                windows.Add(Window(route, resourceMap, start, route.Nodes.Count - 1, true));
                break;
            }

            windows.Add(Window(route, resourceMap, start, boundary, false));
            start = boundary;
        }

        if (windows.Count == 0 || windows[^1].Final is false)
            windows.Add(Window(route, resourceMap, start, route.Nodes.Count - 1, true));
        return windows.Select((window, index) => window with { Index = index }).ToArray();
    }

    /// <summary>
    /// The first return window alone may end in an intersection immediately outside
    /// the dumper. If another vehicle enters the next window while the returning
    /// vehicle is still there, both can block each other. Join the first two safe
    /// windows for the dumper return so its exit corridor is reserved atomically.
    /// The remaining route stays rolling and is still acquired only at the next
    /// verified safe point.
    /// </summary>
    public static IReadOnlyList<AgvTransportRouteWindow> IncludeDumperExitClearance(
        IReadOnlyList<AgvTransportRouteWindow> windows, MapDefinition map, string dumpingSite)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(map);
        if (windows.Count < 2 || windows[0].Final ||
            !MapRouteSiteMapper.IsSameNode(map, windows[0].Source, dumpingSite) ||
            !MapRouteSiteMapper.IsSameNode(map, windows[0].Destination, windows[1].Source))
            return windows;

        var first = windows[0];
        var second = windows[1];
        var merged = new AgvTransportRouteWindow(
            0,
            first.Source,
            second.Destination,
            first.Path.Concat(second.Path.Skip(1)).ToArray(),
            first.EdgeIds.Concat(second.EdgeIds).ToArray(),
            first.ResourceIds.Concat(second.ResourceIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            second.Final);

        return windows.Skip(2).Prepend(merged)
            .Select((window, index) => window with { Index = index })
            .ToArray();
    }

    private static bool IsStableStop(string site, MapDefinition map)
    {
        var logical = MapRouteSiteMapper.ResolveLogicalNodeId(map, site);
        var node = map.Nodes.SingleOrDefault(item => item.Id.Equals(logical, StringComparison.OrdinalIgnoreCase));
        return node is { Enabled: true, AllowTrafficStop: not false } &&
            node.Type is MapNodeType.Station or MapNodeType.WaitingPoint or MapNodeType.ChargingPoint or MapNodeType.Intersection;
    }

    private static AgvTransportRouteWindow Window(RoutePlan route, MapDefinition map, int start, int end, bool final)
    {
        var nodes = route.Nodes.Skip(start).Take(end - start + 1).ToArray();
        var edges = route.EdgeIds.Skip(start).Take(end - start).ToArray();
        if (nodes.Length < 2 || edges.Length != nodes.Length - 1)
            throw new InvalidOperationException("运输滚动窗口的节点与路段不连续。");

        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNodeResources(resources, map, nodes[0]);
        for (var edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
        {
            var edge = map.Edges.SingleOrDefault(item => item.Id.Equals(edges[edgeIndex], StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"滚动窗口引用了未知路段{edges[edgeIndex]}。");
            resources.UnionWith(edge.ResourceIds);
            AddNodeResources(resources, map, nodes[edgeIndex + 1]);
        }

        return new(0, nodes[0], nodes[^1], nodes, edges,
            resources.Order(StringComparer.OrdinalIgnoreCase).ToArray(), final);
    }

    private static void AddNodeResources(ISet<string> target, MapDefinition map, string site)
    {
        var logical = MapRouteSiteMapper.ResolveLogicalNodeId(map, site);
        var node = map.Nodes.SingleOrDefault(item => item.Id.Equals(logical, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"滚动窗口引用了未知节点{site}。");
        foreach (var resource in node.ResourceIds) target.Add(resource);
    }
}
