namespace AgvDispatch.Domain.Maps;

public sealed class MapGraph
{
    private readonly Dictionary<string, MapNode> _nodes;
    private readonly Dictionary<string, TrafficResourceDefinition> _resources;
    private readonly Dictionary<string, List<Traversal>> _outgoing;

    public MapGraph(
        IEnumerable<MapNode> nodes,
        IEnumerable<MapEdge> edges,
        IEnumerable<TrafficResourceDefinition> resources)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(resources);
        _nodes = nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        _resources = resources.ToDictionary(resource => resource.Id, StringComparer.OrdinalIgnoreCase);
        _outgoing = _nodes.Keys.ToDictionary(
            id => id,
            _ => new List<Traversal>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var resource in _resources.Values)
        {
            if (string.IsNullOrWhiteSpace(resource.Id) || resource.Capacity < 1)
            {
                throw new InvalidOperationException("交通资源编号为空或容量小于1。");
            }
        }

        foreach (var node in _nodes.Values)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                throw new InvalidOperationException("地图节点编号不能为空。");
            }

            ValidateResourceReferences(node.ResourceIds, $"节点{node.Id}");
        }

        var edgeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var suppliedEdge in edges)
        {
            var edge = suppliedEdge;
            if (string.IsNullOrWhiteSpace(edge.Id) ||
                string.IsNullOrWhiteSpace(edge.From) ||
                string.IsNullOrWhiteSpace(edge.To))
            {
                throw new InvalidOperationException("地图路段编号、起点和终点不能为空。");
            }

            if (!edgeIds.Add(edge.Id))
            {
                throw new InvalidOperationException($"地图路段编号重复：{edge.Id}。");
            }

            if (!_nodes.ContainsKey(edge.From) || !_nodes.ContainsKey(edge.To))
            {
                throw new InvalidOperationException($"路段{edge.Id}引用了不存在的节点。");
            }

            if (!MapEdgeGeometry.TryValidate(edge, _nodes[edge.From], _nodes[edge.To], out var geometryError))
                throw new InvalidOperationException($"路段{edge.Id}几何无效：{geometryError}");
            if (edge.GeometryKind != MapEdgeGeometryKind.Line)
                edge = edge with { Distance = MapEdgeGeometry.Length(edge, _nodes[edge.From], _nodes[edge.To]) };

            if (!double.IsFinite(edge.Distance) || edge.Distance <= 0 || edge.SpeedLimit < 0)
            {
                throw new InvalidOperationException($"路段{edge.Id}的距离或限速无效。");
            }

            ValidateResourceReferences(edge.ResourceIds, $"路段{edge.Id}");
            _outgoing[edge.From].Add(new Traversal(edge, edge.From, edge.To, false));
            if (edge.Bidirectional)
            {
                _outgoing[edge.To].Add(new Traversal(edge, edge.To, edge.From, true));
            }
        }
    }

    public IReadOnlyDictionary<string, TrafficResourceDefinition> Resources => _resources;

    // A transport command needs actual adjacent edges, even when its end station is
    // the current station. Do not turn a zero-length shortest path into a completed action.
    public RoutePlan? FindShortestTransportRoute(
        string source,
        string destination,
        IReadOnlySet<string>? unavailableResources = null,
        IReadOnlyDictionary<string, double>? edgePenalties = null,
        IReadOnlySet<string>? forbiddenIntermediateNodes = null)
    {
        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            return FindShortestRoute(source, destination, unavailableResources, edgePenalties);
        if (!_nodes.TryGetValue(source, out var origin) || !origin.Enabled ||
            forbiddenIntermediateNodes?.Contains(origin.Id) == true ||
            origin.ResourceIds.Any(id => unavailableResources?.Contains(id) == true)) return null;

        RoutePlan? best = null;
        foreach (var first in _outgoing[origin.Id].OrderBy(t => t.Edge.Id, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(t => t.To, StringComparer.OrdinalIgnoreCase))
        {
            // A same-node edge is not an established Robokit movement. A real exit
            // and return, including an explicitly bidirectional edge, is required.
            if (!first.Edge.Enabled || !_nodes[first.To].Enabled || forbiddenIntermediateNodes?.Contains(first.To) == true ||
                string.Equals(first.To, origin.Id, StringComparison.OrdinalIgnoreCase) ||
                first.Edge.ResourceIds.Concat(_nodes[first.To].ResourceIds)
                    .Any(id => unavailableResources?.Contains(id) == true)) continue;
            var back = FindShortestRoute(first.To, origin.Id, unavailableResources, edgePenalties, forbiddenIntermediateNodes);
            if (back is not { Nodes.Count: >= 2 }) continue;
            var penalty = edgePenalties is not null && edgePenalties.TryGetValue(first.Edge.Id, out var configured) &&
                          double.IsFinite(configured) ? Math.Max(0, configured) : 0;
            var cost = first.Edge.Distance / (first.Edge.SpeedLimit > 0 ? first.Edge.SpeedLimit : 1) + penalty + back.Cost;
            if (!double.IsFinite(cost) || best is not null && cost >= best.Cost) continue;
            best = new RoutePlan(new[] { origin.Id }.Concat(back.Nodes).ToArray(),
                new[] { first.Edge.Id }.Concat(back.EdgeIds).ToArray(),
                origin.ResourceIds.Concat(first.Edge.ResourceIds).Concat(back.ResourceIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), cost);
        }
        return best;
    }

    public RoutePlan? FindShortestRoute(
        string source,
        string destination,
        IReadOnlySet<string>? unavailableResources = null,
        IReadOnlyDictionary<string, double>? edgePenalties = null,
        IReadOnlySet<string>? unavailableNodes = null)
    {
        if (!_nodes.TryGetValue(source, out var sourceNode) || !sourceNode.Enabled ||
            !_nodes.TryGetValue(destination, out var destinationNode) || !destinationNode.Enabled ||
            unavailableNodes?.Contains(sourceNode.Id) == true || unavailableNodes?.Contains(destinationNode.Id) == true)
        {
            return null;
        }

        if (unavailableResources is not null &&
            sourceNode.ResourceIds.Any(unavailableResources.Contains))
        {
            return null;
        }

        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            return new RoutePlan([source], [], sourceNode.ResourceIds.Distinct(
                StringComparer.OrdinalIgnoreCase).ToArray(), 0);
        }

        var distances = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [source] = 0
        };
        var previous = new Dictionary<string, Traversal>(StringComparer.OrdinalIgnoreCase);
        var queue = new PriorityQueue<string, double>();
        queue.Enqueue(source, 0);

        while (queue.TryDequeue(out var current, out var queuedCost))
        {
            if (queuedCost > distances[current])
            {
                continue;
            }

            if (string.Equals(current, destination, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            foreach (var traversal in _outgoing[current])
            {
                if (!traversal.Edge.Enabled || !_nodes[traversal.To].Enabled || unavailableNodes?.Contains(traversal.To) == true)
                {
                    continue;
                }

                var required = traversal.Edge.ResourceIds
                    .Concat(_nodes[traversal.To].ResourceIds);
                if (unavailableResources is not null && required.Any(unavailableResources.Contains))
                {
                    continue;
                }

                var speed = traversal.Edge.SpeedLimit > 0 ? traversal.Edge.SpeedLimit : 1;
                var penalty = edgePenalties is not null &&
                              edgePenalties.TryGetValue(traversal.Edge.Id, out var configuredPenalty) &&
                              double.IsFinite(configuredPenalty)
                    ? Math.Max(0, configuredPenalty)
                    : 0;
                var candidate = queuedCost + traversal.Edge.Distance / speed + penalty;
                if (distances.TryGetValue(traversal.To, out var known) && candidate >= known)
                {
                    continue;
                }

                distances[traversal.To] = candidate;
                previous[traversal.To] = traversal;
                queue.Enqueue(traversal.To, candidate);
            }
        }

        if (!distances.TryGetValue(destination, out var totalCost))
        {
            return null;
        }

        var traversals = new List<Traversal>();
        var cursor = destination;
        while (!string.Equals(cursor, source, StringComparison.OrdinalIgnoreCase))
        {
            var traversal = previous[cursor];
            traversals.Add(traversal);
            cursor = traversal.From;
        }

        traversals.Reverse();
        var routeNodes = new List<string> { source };
        routeNodes.AddRange(traversals.Select(item => item.To));
        var resourceIds = sourceNode.ResourceIds
            .Concat(traversals.SelectMany(item => item.Edge.ResourceIds))
            .Concat(traversals.SelectMany(item => _nodes[item.To].ResourceIds))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new RoutePlan(
            routeNodes,
            traversals.Select(item => item.Edge.Id).ToArray(),
            resourceIds,
            totalCost);
    }

    private void ValidateResourceReferences(IEnumerable<string> resourceIds, string owner)
    {
        foreach (var resourceId in resourceIds)
        {
            if (!_resources.ContainsKey(resourceId))
            {
                throw new InvalidOperationException($"{owner}引用了不存在的交通资源{resourceId}。");
            }
        }
    }

    private sealed record Traversal(MapEdge Edge, string From, string To, bool Reversed);
}
