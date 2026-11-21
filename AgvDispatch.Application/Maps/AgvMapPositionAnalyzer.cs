using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using System.Runtime.CompilerServices;

namespace AgvDispatch.Application.Maps;

public static class AgvMapPositionAnalyzer
{
    private static readonly ConditionalWeakTable<MapDefinition, RoadGeometry[]> PublishedRoads = new();

    // Only published/runtime definitions are eligible for reuse. Editors may
    // populate the backing collections of a definition in place.
    public static AgvMapPositionAnalysis Analyze(
        double? x,
        double? y,
        double? localizationConfidence,
        DateTimeOffset? lastSuccessfulPollAt,
        MapDefinition? map,
        DateTimeOffset now,
        TimeSpan staleAfter,
        double minimumLocalizationConfidence,
        double maximumRouteDeviationMeters,
        bool reuseGeometry = false)
    {
        if (map is null)
            return Result(AgvMapPositionState.MapUnavailable, "尚未激活运行地图。");
        if (x is null || y is null || !double.IsFinite(x.Value) || !double.IsFinite(y.Value))
            return Result(AgvMapPositionState.PositionUnavailable, "机器人未返回有效定位坐标。");
        if (lastSuccessfulPollAt is null || now - lastSuccessfulPollAt.Value > staleAfter)
            return Result(AgvMapPositionState.Stale, "机器人定位数据已经超时。");
        if (localizationConfidence is { } confidence &&
            (!double.IsFinite(confidence) || confidence < minimumLocalizationConfidence))
            return Result(AgvMapPositionState.LowConfidence,
                $"定位置信度{confidence:P0}低于阈值{minimumLocalizationConfidence:P0}。");

        var roads = reuseGeometry ? PublishedRoads.GetValue(map, BuildRoadIndex) : EnumerateRoads(map);
        string? nearest = null;
        var distance = double.PositiveInfinity;
        foreach (var road in roads)
        {
            if (road.Bounds is { } bounds && bounds.CannotBeCloser(x.Value, y.Value, distance))
                continue;
            var candidate = MapEdgeGeometry.DistanceTo(road.Edge, road.From, road.To, x.Value, y.Value);
            if (nearest is not null && candidate.CompareTo(distance) >= 0)
                continue;
            nearest = road.Edge.Id;
            distance = candidate;
        }
        if (nearest is null)
            return Result(AgvMapPositionState.MapUnavailable, "运行地图没有可用于定位校验的路段。");
        if (distance > maximumRouteDeviationMeters)
            return new AgvMapPositionAnalysis(
                AgvMapPositionState.OffRoute,
                distance,
                nearest,
                $"偏离最近路段{nearest}约{distance:F2}米。");
        return new AgvMapPositionAnalysis(
            AgvMapPositionState.Normal,
            distance,
            nearest,
            $"定位正常，距路段{nearest}约{distance:F2}米。");
    }

    private static IEnumerable<RoadGeometry> EnumerateRoads(MapDefinition map)
    {
        var nodes = map.Nodes.Where(node => node.Enabled).ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var edge in map.Edges)
            if (edge.Enabled && nodes.TryGetValue(edge.From, out var from) && nodes.TryGetValue(edge.To, out var to) &&
                MapEdgeGeometry.TryValidate(edge, from, to, out _))
                yield return new(edge, from, to, null);
    }

    private static RoadGeometry[] BuildRoadIndex(MapDefinition map) => EnumerateRoads(map).Select(road =>
        // Arc distance uses an angular tolerance. Keep its exact distance
        // evaluation even just outside the nominal curve bounds.
        road.Edge.GeometryKind == MapEdgeGeometryKind.CircularArc ? road :
            road with { Bounds = new RoadBounds(MapEdgeGeometry.ConservativeBounds(road.Edge, road.From, road.To)) })
        .ToArray();

    private readonly record struct RoadGeometry(MapEdge Edge, MapNode From, MapNode To, RoadBounds? Bounds);

    private readonly record struct RoadBounds(MapGeometryBounds Bounds)
    {
        public bool CannotBeCloser(double x, double y, double distance)
        {
            var dx = Math.Max(Bounds.MinX - x, x - Bounds.MaxX);
            var dy = Math.Max(Bounds.MinY - y, y - Bounds.MaxY);
            var roundoff = 1e-12 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));
            return Math.Max(dx, dy) > distance + roundoff;
        }
    }

    private static AgvMapPositionAnalysis Result(AgvMapPositionState state, string message) =>
        new(state, null, null, message);

}
