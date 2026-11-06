using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgvDispatch.Domain.Maps;

public sealed record MapCorridorTopology(
    IReadOnlyList<MapEdge> Edges,
    IReadOnlyList<TrafficResourceDefinition> Resources);

/// <summary>
/// Adds runtime-only single-lane guards for bidirectional and geometrically
/// reciprocal edges that do not already share an explicit traffic resource.
/// The published map definition and its hash are not changed.
/// </summary>
public static class MapCorridorResources
{
    public const string IdPrefix = "AUTO-CORRIDOR-";
    private const string RuntimeLayerId = "runtime-corridor-guard";

    public static MapCorridorTopology Enrich(MapDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Enrich(definition.Nodes, definition.Edges, definition.Resources);
    }

    public static IReadOnlyList<TrafficResourceDefinition> AllResources(MapDefinition definition) =>
        Enrich(definition).Resources;

    public static bool IsAutomatic(string? resourceId) =>
        resourceId?.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase) == true;

    public static MapCorridorTopology Enrich(
        IEnumerable<MapNode> nodes,
        IEnumerable<MapEdge> edges,
        IEnumerable<TrafficResourceDefinition> resources)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(resources);

        var nodeMap = nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var edgeArray = edges.ToArray();
        var resourceArray = resources.ToArray();
        var additions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var automaticResources = new List<TrafficResourceDefinition>();
        var usedIds = resourceArray.Select(resource => resource.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var groups = edgeArray
            .Select(edge => new CorridorEdge(edge, GeometryKey(edge, nodeMap)))
            .GroupBy(item => item.GeometryKey, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var members = group.ToArray();
            var directionCount = members
                .Select(item => DirectionKey(item.Edge, nodeMap))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (!members.Any(item => item.Edge.Bidirectional) && directionCount < 2)
                continue;

            var sharedResourceIds = members
                .Select(item => item.Edge.ResourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase))
                .Aggregate((left, right) =>
                {
                    left.IntersectWith(right);
                    return left;
                });
            if (sharedResourceIds.Count > 0)
                continue;

            var resourceId = CreateResourceId(group.Key, usedIds);
            automaticResources.Add(new TrafficResourceDefinition(
                resourceId,
                TrafficResourceType.SingleLane,
                1,
                Boundary(members.Select(item => item.Edge), nodeMap))
            {
                LayerId = RuntimeLayerId
            });
            foreach (var member in members)
            {
                if (!additions.TryGetValue(member.Edge.Id, out var ids))
                {
                    ids = [];
                    additions[member.Edge.Id] = ids;
                }
                ids.Add(resourceId);
            }
        }

        // A pair of one-way edges can meet at a shared Waypoint without having
        // identical geometry (for example V13 LM22->LM25 and LM24->LM25).
        // Per-route corridor hashes then differ and both vehicles enter the same
        // merge from opposite sides. Add one runtime-only capacity-one guard to
        // every incident edge of non-station Waypoints so the merge is serialized
        // by the formal traffic lease before any vehicle command is sent.
        foreach (var node in nodeMap.Values.Where(node =>
                     node.Enabled && node.Type == MapNodeType.Waypoint))
        {
            var incident = edgeArray.Where(edge => edge.Enabled &&
                (edge.From.Equals(node.Id, StringComparison.OrdinalIgnoreCase) ||
                 edge.To.Equals(node.Id, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (incident.Length < 2)
                continue;
            var resourceId = CreateResourceId("NODE|" + CoordinateKey(node), usedIds);
            automaticResources.Add(new TrafficResourceDefinition(
                resourceId,
                TrafficResourceType.SingleLane,
                1,
                Boundary(incident, nodeMap))
            {
                LayerId = RuntimeLayerId
            });
            foreach (var edge in incident)
            {
                if (!additions.TryGetValue(edge.Id, out var ids))
                {
                    ids = [];
                    additions[edge.Id] = ids;
                }
                ids.Add(resourceId);
            }
        }

        var enrichedEdges = edgeArray.Select(edge =>
            additions.TryGetValue(edge.Id, out var ids)
                ? edge with { ResourceIds = edge.ResourceIds.Concat(ids).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() }
                : edge).ToArray();
        return new MapCorridorTopology(
            Array.AsReadOnly(enrichedEdges),
            Array.AsReadOnly(resourceArray.Concat(automaticResources).ToArray()));
    }

    private static string GeometryKey(MapEdge edge, IReadOnlyDictionary<string, MapNode> nodes)
    {
        var from = nodes[edge.From];
        var to = nodes[edge.To];
        var points = MapEdgeGeometry.Sample(edge, from, to, 64).ToArray();
        if (string.CompareOrdinal(CoordinateKey(from), CoordinateKey(to)) > 0)
            Array.Reverse(points);
        return string.Join(';', points.Select(point =>
            RoundCoordinate(point.X) + "," + RoundCoordinate(point.Y)));
    }

    private static string DirectionKey(MapEdge edge, IReadOnlyDictionary<string, MapNode> nodes) =>
        CoordinateKey(nodes[edge.From]) + ">" + CoordinateKey(nodes[edge.To]);

    private static string CoordinateKey(MapNode node) =>
        RoundCoordinate(node.X) + "," + RoundCoordinate(node.Y);

    private static string RoundCoordinate(double value)
    {
        var rounded = Math.Round(value, 6, MidpointRounding.AwayFromZero);
        if (rounded == 0) rounded = 0;
        return rounded.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static string CreateResourceId(string geometryKey, ISet<string> usedIds)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(geometryKey)));
        var baseId = IdPrefix + hash[..20];
        var candidate = baseId;
        for (var suffix = 2; !usedIds.Add(candidate); suffix++)
            candidate = baseId + "-" + suffix.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private static IReadOnlyList<MapPoint2D> Boundary(
        IEnumerable<MapEdge> edges,
        IReadOnlyDictionary<string, MapNode> nodes)
    {
        var bounds = edges.Select(edge =>
            MapEdgeGeometry.ConservativeBounds(edge, nodes[edge.From], nodes[edge.To])).ToArray();
        const double padding = 0.01;
        var minX = bounds.Min(item => item.MinX) - padding;
        var minY = bounds.Min(item => item.MinY) - padding;
        var maxX = bounds.Max(item => item.MaxX) + padding;
        var maxY = bounds.Max(item => item.MaxY) + padding;
        return
        [
            new MapPoint2D(minX, minY),
            new MapPoint2D(maxX, minY),
            new MapPoint2D(maxX, maxY),
            new MapPoint2D(minX, maxY)
        ];
    }

    private sealed record CorridorEdge(MapEdge Edge, string GeometryKey);
}
