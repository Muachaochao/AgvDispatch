namespace AgvDispatch.Domain.Maps;

public sealed record MapIntersectionSplitPlan(MapDefinition Definition, int SplitEdges, int AddedNodes,
    int AddedEdges, int SkippedEdges, int AmbiguousIntersections, IReadOnlyList<MapNode> Junctions);

/// <summary>Explicitly requested, same-layer straight-road intersections. Does not infer crossings between layers.</summary>
public static class MapIntersectionSplitter
{
    public const int MaximumSelectedEdges = 256;
    private const double Epsilon = 1e-7;

    public static MapIntersectionSplitPlan Plan(MapDefinition map, IReadOnlySet<string> selectedIds)
    {
        if (selectedIds.Count > MaximumSelectedEdges)
            throw new ArgumentException($"每次最多分析 {MaximumSelectedEdges} 条所选路段。");
        var nodes = map.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var layers = map.Layers.ToDictionary(layer => layer.Id, StringComparer.OrdinalIgnoreCase);
        bool Available(string layerId) => !layers.TryGetValue(layerId, out var layer) ||
            (layer.Visible && !layer.IsLocked && layer.IsAllowSelect);
        var selected = map.Edges.Where(edge => selectedIds.Contains(edge.Id)).ToArray();
        var roads = selected.Where(edge => edge.GeometryKind == MapEdgeGeometryKind.Line && Available(edge.LayerId) &&
            nodes.TryGetValue(edge.From, out var from) && nodes.TryGetValue(edge.To, out var to) &&
            MapEdgeGeometry.TryValidate(edge, from, to, out _) &&
            Math.Max(Math.Max(Math.Abs(from.X), Math.Abs(from.Y)), Math.Max(Math.Abs(to.X), Math.Abs(to.Y))) < 1e9 &&
            MapEdgeGeometry.Length(edge, from, to) > Epsilon).ToArray();
        var addedNodes = new List<MapNode>();
        const double cellSize = 1e-6;
        var spatialNodes = new Dictionary<(long X, long Y), List<MapNode>>();
        (long X, long Y) Cell(double x, double y) => ((long)Math.Floor(x / cellSize), (long)Math.Floor(y / cellSize));
        void IndexNode(MapNode node)
        {
            if (!double.IsFinite(node.X) || !double.IsFinite(node.Y) || Math.Abs(node.X) >= 1e9 || Math.Abs(node.Y) >= 1e9) return;
            var cell = Cell(node.X, node.Y);
            if (!spatialNodes.TryGetValue(cell, out var bucket)) spatialNodes[cell] = bucket = [];
            bucket.Add(node);
        }
        foreach (var node in map.Nodes) IndexNode(node);
        IEnumerable<MapNode> Nearby(double x, double y)
        {
            var cell = Cell(x, y);
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (spatialNodes.TryGetValue((cell.X + dx, cell.Y + dy), out var bucket))
                    foreach (var node in bucket) yield return node;
        }
        var cuts = roads.ToDictionary(edge => edge.Id, _ => new List<(double T, string NodeId)>(), StringComparer.OrdinalIgnoreCase);
        var nodeIds = nodes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edgeIds = map.Edges.Select(edge => edge.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextNode = 1; var nextEdge = 1; var ambiguous = 0;
        string Allocate(HashSet<string> used, string prefix, ref int index)
        {
            while (!used.Add($"{prefix}{index:000}")) index++;
            return $"{prefix}{index++:000}";
        }
        void Cut(MapEdge edge, double t, string id)
        {
            var length = MapEdgeGeometry.Length(edge, nodes[edge.From], nodes[edge.To]);
            if (t * length <= Epsilon || (1 - t) * length <= Epsilon) return;
            if (!cuts[edge.Id].Any(cut => cut.NodeId == id)) cuts[edge.Id].Add((t, id));
        }
        for (var i = 0; i < roads.Length; i++)
        for (var j = i + 1; j < roads.Length; j++)
        {
            var a = roads[i]; var b = roads[j];
            if (!a.LayerId.Equals(b.LayerId, StringComparison.OrdinalIgnoreCase)) continue;
            var p = nodes[a.From]; var q = nodes[b.From];
            var r = new MapPoint2D(nodes[a.To].X - p.X, nodes[a.To].Y - p.Y);
            var s = new MapPoint2D(nodes[b.To].X - q.X, nodes[b.To].Y - q.Y);
            var denominator = r.X * s.Y - r.Y * s.X;
            var lr = Math.Sqrt(r.X * r.X + r.Y * r.Y); var ls = Math.Sqrt(s.X * s.X + s.Y * s.Y);
            if (Math.Abs(denominator) <= 1e-12 * lr * ls) continue; // Collinear overlap is not a unique junction.
            var dx = q.X - p.X; var dy = q.Y - p.Y;
            var t = (dx * s.Y - dy * s.X) / denominator;
            var u = (dx * r.Y - dy * r.X) / denominator;
            if (t < 0 || t > 1 || u < 0 || u > 1) continue;
            var aInside = t * lr > Epsilon && (1 - t) * lr > Epsilon;
            var bInside = u * ls > Epsilon && (1 - u) * ls > Epsilon;
            if (!aInside && !bInside) continue;
            var x = p.X + t * r.X; var y = p.Y + t * r.Y;
            var endpoints = new[] { p, nodes[a.To], q, nodes[b.To] }.DistinctBy(node => node.Id)
                .Where(node => Math.Abs(node.X - x) <= Epsilon && Math.Abs(node.Y - y) <= Epsilon).ToArray();
            // Other-layer coincident nodes do not identify a connection in this layer.
            var matches = Nearby(x, y).Where(node => node.LayerId.Equals(a.LayerId, StringComparison.OrdinalIgnoreCase) ||
                    endpoints.Any(endpoint => endpoint.Id == node.Id))
                .Where(node => Math.Abs(node.X - x) <= Epsilon && Math.Abs(node.Y - y) <= Epsilon).ToArray();
            if (matches.Length > 1 || (matches.Length == 1 && !Available(matches[0].LayerId)))
            { ambiguous++; continue; }
            var junction = matches.SingleOrDefault();
            if (junction is null)
            {
                junction = new(Allocate(nodeIds, "N", ref nextNode), x, y, MapNodeType.Intersection, a.Enabled || b.Enabled, [])
                { LayerId = a.LayerId };
                addedNodes.Add(junction);
                IndexNode(junction);
            }
            else if (addedNodes.Contains(junction) && !junction.Enabled && (a.Enabled || b.Enabled))
            {
                var replacement = junction with { Enabled = true };
                addedNodes[addedNodes.IndexOf(junction)] = replacement;
                var bucket = spatialNodes[Cell(junction.X, junction.Y)];
                bucket[bucket.IndexOf(junction)] = replacement;
                junction = replacement;
            }
            Cut(a, t, junction.Id); Cut(b, u, junction.Id);
        }
        var allNodes = nodes.Values.Concat(addedNodes).ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var resultEdges = new List<MapEdge>(); var splitCount = 0;
        foreach (var edge in map.Edges)
        {
            if (!cuts.TryGetValue(edge.Id, out var points) || points.Count == 0) { resultEdges.Add(edge); continue; }
            splitCount++;
            var chain = new[] { edge.From }.Concat(points.OrderBy(point => point.T).Select(point => point.NodeId))
                .Append(edge.To).ToArray();
            for (var index = 0; index < chain.Length - 1; index++)
            {
                var segment = edge with { Id = index == 0 ? edge.Id : Allocate(edgeIds, "E", ref nextEdge),
                    From = chain[index], To = chain[index + 1] };
                resultEdges.Add(segment with { Distance = MapEdgeGeometry.Length(segment, allNodes[segment.From], allNodes[segment.To]) });
            }
        }
        return new(map with { Nodes = map.Nodes.Concat(addedNodes).ToArray(), Edges = resultEdges }, splitCount,
            addedNodes.Count, resultEdges.Count - map.Edges.Count, selected.Length - roads.Length, ambiguous,
            cuts.Values.SelectMany(points => points).Select(point => point.NodeId).Distinct().Select(id => allNodes[id]).ToArray());
    }
}
