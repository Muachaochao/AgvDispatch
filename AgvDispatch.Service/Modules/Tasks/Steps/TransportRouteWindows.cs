using AgvDispatch.Application.Abstractions;
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
        DispatchTaskStepOptions options, string? navigationCommandMode = null,
        Func<MapNode, bool>? canStageAt = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        var topology = MapCorridorResources.Enrich(map);
        var edgeIndex = topology.Edges.ToDictionary(edge => edge.Id, StringComparer.OrdinalIgnoreCase);
        var nodeIndex = map.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        return PlanCore(route, site => nodeIndex.GetValueOrDefault(MapRouteSiteMapper.ResolveLogicalNodeId(map, site)),
            edgeIndex, options, navigationCommandMode, canStageAt);
    }

    public static IReadOnlyList<AgvTransportRouteWindow> Plan(RoutePlan route, ActiveMapRuntimeSnapshot map,
        DispatchTaskStepOptions options, string? navigationCommandMode = null, Func<MapNode, bool>? canStageAt = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        return PlanCore(route, map.Graph.FindRouteNode, map.Graph.Edges, options, navigationCommandMode, canStageAt);
    }

    /// <summary>
    /// Recomputes each window's resource set from the window's own path and
    /// edges. This is a send-time guard against an upstream route aggregate
    /// accidentally contaminating a short window with later stop resources.
    /// </summary>
    public static IReadOnlyList<AgvTransportRouteWindow> NormalizeResources(
        IReadOnlyList<AgvTransportRouteWindow> windows, MapDefinition map)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(map);
        var topology = MapCorridorResources.Enrich(map);
        var nodes = map.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        return NormalizeCore(windows, site =>
            nodes.GetValueOrDefault(MapRouteSiteMapper.ResolveLogicalNodeId(map, site)),
            topology.Edges.ToDictionary(edge => edge.Id, StringComparer.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<AgvTransportRouteWindow> NormalizeResources(
        IReadOnlyList<AgvTransportRouteWindow> windows, ActiveMapRuntimeSnapshot map)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(map);
        return NormalizeCore(windows, map.Graph.FindRouteNode, map.Graph.Edges);
    }

    private static IReadOnlyList<AgvTransportRouteWindow> NormalizeCore(
        IReadOnlyList<AgvTransportRouteWindow> windows,
        Func<string, MapNode?> findNode,
        IReadOnlyDictionary<string, MapEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(findNode);
        ArgumentNullException.ThrowIfNull(edges);
        return windows.Select(window =>
        {
            if (window.Path.Count < 2 || window.EdgeIds.Count != window.Path.Count - 1 ||
                !string.Equals(window.Source, window.Path[0], StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(window.Destination, window.Path[^1], StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("运输滚动窗口的起终点、节点与路段不连续。");
            var nodes = window.Path.Select(site => findNode(site) ??
                throw new InvalidOperationException($"滚动窗口引用了未知节点{site}。")).ToArray();
            return window with { ResourceIds = Resources(nodes, window.EdgeIds, edges) };
        }).ToArray();
    }

    private static IReadOnlyList<AgvTransportRouteWindow> PlanCore(RoutePlan route, Func<string, MapNode?> findNode,
        IReadOnlyDictionary<string, MapEdge> edges, DispatchTaskStepOptions options, string? navigationCommandMode,
        Func<MapNode, bool>? canStageAt)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(options);
        var nodes = route.Nodes.Select(site => findNode(site) ??
            throw new InvalidOperationException($"滚动窗口引用了未知节点{site}。")).ToArray();
        // A 3051 GotargetCommissioning task carries only source and destination in
        // the vendor request. Splitting it into several stops changes the operation
        // contract, so only SpecifiedPath/3066 may use rolling route windows.
        var modeSupportsWindows = navigationCommandMode is null ||
            navigationCommandMode.Equals("SpecifiedPath", StringComparison.OrdinalIgnoreCase);
        if (!options.RollingReservationEnabled || !modeSupportsWindows || route.Nodes.Count < 3)
            return [Window(route, nodes, edges, 0, route.Nodes.Count - 1, true)];

        var minimum = Math.Clamp(options.RollingWindowMinimumEdges, 1, 100);
        var maximum = Math.Max(minimum, Math.Clamp(options.RollingWindowMaximumEdges, 1, 1000));
        var farthest = string.Equals(options.RollingWindowBoundary, RollingWindowBoundaries.FarthestWithinMaximum,
            StringComparison.OrdinalIgnoreCase);
        var windows = new List<AgvTransportRouteWindow>();
        var start = 0;
        while (start < route.Nodes.Count - 1)
        {
            var remainingEdges = route.Nodes.Count - 1 - start;
            if (remainingEdges <= minimum || farthest && remainingEdges <= maximum)
            {
                windows.Add(Window(route, nodes, edges, start, route.Nodes.Count - 1, true));
                break;
            }

            var lower = start + minimum;
            // MaximumEdges is a planning target, not permission to stop in the
            // middle of a corridor. Search forward for the nearest safe node even
            // when it lies beyond the preferred maximum. If the route contains no
            // interior safe node, keep the whole route as one atomic window.
            var boundary = -1;
            if (farthest)
            {
                // Each window ends with a full stop and a renewal; with dense safe points the nearest-stop
                // rule stops every few metres. Prefer the farthest safe stop the preferred maximum allows.
                for (var index = Math.Min(start + maximum, route.Nodes.Count - 2); index >= lower; index--)
                {
                    if (!IsStableStop(nodes[index], canStageAt)) continue;
                    boundary = index;
                    break;
                }
            }
            for (var index = farthest ? Math.Max(lower, start + maximum + 1) : lower;
                 boundary < 0 && index < route.Nodes.Count - 1; index++)
            {
                if (!IsStableStop(nodes[index], canStageAt)) continue;
                boundary = index;
            }

            if (boundary < 0)
            {
                windows.Add(Window(route, nodes, edges, start, route.Nodes.Count - 1, true));
                break;
            }

            windows.Add(Window(route, nodes, edges, start, boundary, false));
            start = boundary;
        }

        if (windows.Count == 0 || windows[^1].Final is false)
            windows.Add(Window(route, nodes, edges, start, route.Nodes.Count - 1, true));
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

    private static bool IsStableStop(MapNode node, Func<MapNode, bool>? canStageAt) =>
        node is { Enabled: true, AllowTrafficStop: not false } &&
            node.Type is MapNodeType.Station or MapNodeType.WaitingPoint or MapNodeType.ChargingPoint or MapNodeType.Intersection &&
            (canStageAt is null || canStageAt(node));

    private static AgvTransportRouteWindow Window(RoutePlan route, MapNode[] routeNodes,
        IReadOnlyDictionary<string, MapEdge> edgeIndex, int start, int end, bool final)
    {
        var nodes = route.Nodes.Skip(start).Take(end - start + 1).ToArray();
        var edges = route.EdgeIds.Skip(start).Take(end - start).ToArray();
        if (nodes.Length < 2 || edges.Length != nodes.Length - 1)
            throw new InvalidOperationException("运输滚动窗口的节点与路段不连续。");

        return new(0, nodes[0], nodes[^1], nodes, edges,
            Resources(routeNodes.Skip(start).Take(end - start + 1).ToArray(), edges, edgeIndex), final);
    }

    private static string[] Resources(IReadOnlyList<MapNode> nodes, IReadOnlyList<string> edges,
        IReadOnlyDictionary<string, MapEdge> edgeIndex)
    {
        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        resources.UnionWith(nodes[0].ResourceIds);
        for (var offset = 0; offset < edges.Count; offset++)
        {
            var edge = edgeIndex.GetValueOrDefault(edges[offset])
                ?? throw new InvalidOperationException($"滚动窗口引用了未知路段{edges[offset]}。");
            resources.UnionWith(edge.ResourceIds);
            resources.UnionWith(nodes[offset + 1].ResourceIds);
        }
        return resources.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
