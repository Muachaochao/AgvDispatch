using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Infrastructure.Maps;

public sealed class MapCalibrationService : IMapCalibrationService
{
    public MapCalibrationSnapshot Calculate(IReadOnlyList<MapCalibrationPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
        {
            throw new ArgumentException("地图坐标标定至少需要两个控制点。", nameof(points));
        }
        if (points.Any(point =>
                !double.IsFinite(point.CadX) || !double.IsFinite(point.CadY) ||
                !double.IsFinite(point.MapX) || !double.IsFinite(point.MapY)))
        {
            throw new ArgumentException("标定控制点不能包含无穷大或非数字。", nameof(points));
        }

        var cadCenterX = points.Average(point => point.CadX);
        var cadCenterY = points.Average(point => point.CadY);
        var mapCenterX = points.Average(point => point.MapX);
        var mapCenterY = points.Average(point => point.MapY);
        double dot = 0, cross = 0, sourceEnergy = 0;
        foreach (var point in points)
        {
            var sourceX = point.CadX - cadCenterX;
            var sourceY = point.CadY - cadCenterY;
            var targetX = point.MapX - mapCenterX;
            var targetY = point.MapY - mapCenterY;
            dot += sourceX * targetX + sourceY * targetY;
            cross += sourceX * targetY - sourceY * targetX;
            sourceEnergy += sourceX * sourceX + sourceY * sourceY;
        }
        if (sourceEnergy < 1e-12)
        {
            throw new ArgumentException("CAD标定点不能重合。", nameof(points));
        }

        var magnitude = Math.Sqrt(dot * dot + cross * cross);
        if (magnitude < 1e-12)
        {
            throw new ArgumentException("控制点无法计算稳定的旋转与比例。", nameof(points));
        }
        var scale = magnitude / sourceEnergy;
        var cosine = dot / magnitude;
        var sine = cross / magnitude;
        var translationX = mapCenterX - scale * (cosine * cadCenterX - sine * cadCenterY);
        var translationY = mapCenterY - scale * (sine * cadCenterX + cosine * cadCenterY);
        var squaredError = points.Sum(point =>
        {
            var x = scale * (cosine * point.CadX - sine * point.CadY) + translationX;
            var y = scale * (sine * point.CadX + cosine * point.CadY) + translationY;
            return Math.Pow(x - point.MapX, 2) + Math.Pow(y - point.MapY, 2);
        });
        return new MapCalibrationSnapshot(
            scale,
            Math.Atan2(sine, cosine) * 180d / Math.PI,
            translationX,
            translationY,
            Math.Sqrt(squaredError / points.Count),
            points.ToArray());
    }
}
