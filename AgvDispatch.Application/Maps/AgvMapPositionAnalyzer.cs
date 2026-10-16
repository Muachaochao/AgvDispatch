using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Application.Maps;

public static class AgvMapPositionAnalyzer
{
    public static AgvMapPositionAnalysis Analyze(
        double? x,
        double? y,
        double? localizationConfidence,
        DateTimeOffset? lastSuccessfulPollAt,
        MapDefinition? map,
        DateTimeOffset now,
        TimeSpan staleAfter,
        double minimumLocalizationConfidence,
        double maximumRouteDeviationMeters)
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

        var nodes = map.Nodes.Where(node => node.Enabled)
            .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var nearest = map.Edges
            .Where(edge => edge.Enabled && nodes.ContainsKey(edge.From) && nodes.ContainsKey(edge.To) &&
                           MapEdgeGeometry.TryValidate(edge, nodes[edge.From], nodes[edge.To], out _))
            .Select(edge => new
            {
                edge.Id,
                Distance = MapEdgeGeometry.DistanceTo(edge, nodes[edge.From], nodes[edge.To], x.Value, y.Value)
            })
            .OrderBy(item => item.Distance)
            .FirstOrDefault();
        if (nearest is null)
            return Result(AgvMapPositionState.MapUnavailable, "运行地图没有可用于定位校验的路段。");
        if (nearest.Distance > maximumRouteDeviationMeters)
            return new AgvMapPositionAnalysis(
                AgvMapPositionState.OffRoute,
                nearest.Distance,
                nearest.Id,
                $"偏离最近路段{nearest.Id}约{nearest.Distance:F2}米。");
        return new AgvMapPositionAnalysis(
            AgvMapPositionState.Normal,
            nearest.Distance,
            nearest.Id,
            $"定位正常，距路段{nearest.Id}约{nearest.Distance:F2}米。");
    }

    private static AgvMapPositionAnalysis Result(AgvMapPositionState state, string message) =>
        new(state, null, null, message);

}
