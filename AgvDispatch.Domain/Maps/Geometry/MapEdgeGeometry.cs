namespace AgvDispatch.Domain.Maps;

public sealed record MapEdgeGeometrySplit(MapPoint2D SplitPoint, MapEdge First, MapEdge Second);
public readonly record struct MapGeometryBounds(double MinX,double MinY,double MaxX,double MaxY);

/// <summary>Map-coordinate geometry shared by rendering, planning and localization.</summary>
public static class MapEdgeGeometry
{
    // Analytic conservative bounds, not a sampled polyline guarantee.
    public static MapGeometryBounds ConservativeBounds(MapEdge edge,MapNode from,MapNode to)
    {
        Validate(edge,from,to);
        if(edge.GeometryKind==MapEdgeGeometryKind.CircularArc)
        {
            var arc=GetArc(edge,from,to);
            return new(arc.Center.X-arc.Radius,arc.Center.Y-arc.Radius,arc.Center.X+arc.Radius,arc.Center.Y+arc.Radius);
        }
        var points=new[]{Point(from),Point(to)}.Concat(edge.ControlPoints??[]).ToArray();
        // Bezier lies inside the convex hull of endpoints and controls.
        return new(points.Min(p=>p.X),points.Min(p=>p.Y),points.Max(p=>p.X),points.Max(p=>p.Y));
    }
    // Three-point circular geometry includes cubic products of coordinates. Reject only
    // magnitudes beyond any practical map range before those operations can overflow.
    private const double MaximumCoordinateMagnitude = 1e90;

    public static bool TryValidate(MapEdge edge, MapNode from, MapNode to, out string? error)
    {
        error = null;
        if (!double.IsFinite(from.X) || !double.IsFinite(from.Y) ||
            !double.IsFinite(to.X) || !double.IsFinite(to.Y))
            error = "路段端点坐标必须是有限数值。";
        else if (Math.Abs(from.X) > MaximumCoordinateMagnitude || Math.Abs(from.Y) > MaximumCoordinateMagnitude ||
                 Math.Abs(to.X) > MaximumCoordinateMagnitude || Math.Abs(to.Y) > MaximumCoordinateMagnitude)
            error = "路段端点坐标幅值过大，无法稳定计算几何。";
        else if (!Enum.IsDefined(edge.GeometryKind))
            error = "路段几何类型无效。";
        else if (edge.ControlPoints?.Any(point => point is null ||
                     !double.IsFinite(point.X) || !double.IsFinite(point.Y)) == true)
            error = "路段控制点必须是有效的有限坐标。";
        else if (edge.ControlPoints?.Any(point => Math.Abs(point.X) > MaximumCoordinateMagnitude ||
                                                  Math.Abs(point.Y) > MaximumCoordinateMagnitude) == true)
            error = "路段控制点坐标幅值过大，无法稳定计算几何。";
        else if ((edge.ControlPoints?.Count ?? 0) != (edge.GeometryKind switch
                 {
                     MapEdgeGeometryKind.CircularArc => 1,
                     MapEdgeGeometryKind.CubicBezier => 2,
                     _ => 0
                 }))
            error = "直线无需控制点，圆弧需要一个过弧点，三次Bezier需要两个控制点。";
        if (error is not null) return false;
        if (edge.GeometryKind == MapEdgeGeometryKind.CircularArc)
        {
            try { _ = GetArc(edge, from, to); }
            catch (InvalidOperationException exception) { error = exception.Message; return false; }
        }
        return true;
    }

    public static MapPoint2D PointAt(MapEdge edge, MapNode from, MapNode to, double t)
    {
        Validate(edge, from, to);
        if (!double.IsFinite(t) || t is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(t));
        if (t == 0) return Point(from);
        if (t == 1) return Point(to);
        return Evaluate(edge, from, to, t,
            edge.GeometryKind == MapEdgeGeometryKind.CircularArc ? GetArc(edge, from, to) : null);
    }

    public static IReadOnlyList<MapPoint2D> Sample(MapEdge edge, MapNode from, MapNode to, int segments = 64)
    {
        Validate(edge, from, to);
        if (segments is < 1 or > 8192) throw new ArgumentOutOfRangeException(nameof(segments));
        if (edge.GeometryKind == MapEdgeGeometryKind.Line) return [Point(from), Point(to)];
        var arc = edge.GeometryKind == MapEdgeGeometryKind.CircularArc ? GetArc(edge, from, to) : null;
        var points = new MapPoint2D[segments + 1];
        points[0] = Point(from);
        points[^1] = Point(to);
        for (var index = 1; index < segments; index++)
            points[index] = Evaluate(edge, from, to, index / (double)segments, arc);
        return points;
    }

    /// <summary>Flatten to a polyline with a map-unit deviation tolerance.</summary>
    public static IReadOnlyList<MapPoint2D> Flatten(MapEdge edge, MapNode from, MapNode to, double tolerance = 0.001)
    {
        Validate(edge, from, to);
        if (!double.IsFinite(tolerance) || tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (edge.GeometryKind == MapEdgeGeometryKind.Line) return [Point(from), Point(to)];
        if (edge.GeometryKind == MapEdgeGeometryKind.CircularArc)
        {
            var arc = GetArc(edge, from, to);
            var maximumAngle = 2 * Math.Acos(Math.Clamp(1 - tolerance / arc.Radius, -1, 1));
            var segments = maximumAngle > 0
                ? (int)Math.Clamp(Math.Ceiling(Math.Abs(arc.Sweep) / maximumAngle), 2, 8192)
                : 8192;
            return Sample(edge, from, to, segments);
        }
        var points = new List<MapPoint2D> { Point(from) };
        FlattenBezier(Point(from), edge.ControlPoints![0], edge.ControlPoints[1], Point(to), tolerance, 0, points);
        return points;
    }

    public static double Length(MapEdge edge, MapNode from, MapNode to)
    {
        Validate(edge, from, to);
        if (edge.GeometryKind == MapEdgeGeometryKind.Line) return Distance(Point(from), Point(to));
        if (edge.GeometryKind == MapEdgeGeometryKind.CircularArc)
        {
            var arc = GetArc(edge, from, to);
            return arc.Radius * Math.Abs(arc.Sweep);
        }
        var points = Flatten(edge, from, to, 0.0001);
        return Enumerable.Range(1, points.Count - 1).Sum(index => Distance(points[index - 1], points[index]));
    }

    public static double DistanceTo(MapEdge edge, MapNode from, MapNode to, double x, double y)
    {
        Validate(edge, from, to);
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        var point = new MapPoint2D(x, y);
        if (edge.GeometryKind == MapEdgeGeometryKind.Line) return DistanceToSegment(point, Point(from), Point(to));
        if (edge.GeometryKind == MapEdgeGeometryKind.CircularArc)
        {
            var arc = GetArc(edge, from, to);
            var angle = Math.Atan2(y - arc.Center.Y, x - arc.Center.X);
            var offset = arc.Sweep >= 0 ? PositiveAngle(angle - arc.StartAngle) : PositiveAngle(arc.StartAngle - angle);
            if (offset <= Math.Abs(arc.Sweep) + 1e-12)
                return Math.Abs(Distance(point, arc.Center) - arc.Radius);
            return Math.Min(Distance(point, Point(from)), Distance(point, Point(to)));
        }
        var points = Flatten(edge, from, to);
        return Enumerable.Range(1, points.Count - 1)
            .Min(index => DistanceToSegment(point, points[index - 1], points[index]));
    }

    public static MapEdge Reverse(MapEdge edge) => edge with
    {
        From = edge.To,
        To = edge.From,
        ControlPoints = edge.ControlPoints?.Reverse().ToArray()
    };

    // The caller assigns the inserted node ID and the two new edge IDs.
    public static MapEdgeGeometrySplit Split(MapEdge edge, MapNode from, MapNode to, double t = 0.5)
    {
        Validate(edge, from, to);
        if (!double.IsFinite(t) || t is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(t));
        var middle = PointAt(edge, from, to, t);
        IReadOnlyList<MapPoint2D>? firstControls = null, secondControls = null;
        if (edge.GeometryKind == MapEdgeGeometryKind.CircularArc)
        {
            firstControls = [PointAt(edge, from, to, t / 2)];
            secondControls = [PointAt(edge, from, to, t + (1 - t) / 2)];
        }
        else if (edge.GeometryKind == MapEdgeGeometryKind.CubicBezier)
        {
            var a = Lerp(Point(from), edge.ControlPoints![0], t);
            var b = Lerp(edge.ControlPoints[0], edge.ControlPoints[1], t);
            var c = Lerp(edge.ControlPoints[1], Point(to), t);
            firstControls = [a, Lerp(a, b, t)];
            secondControls = [Lerp(b, c, t), c];
        }
        var first = edge with { ControlPoints = firstControls };
        var second = edge with { ControlPoints = secondControls };
        var middleNode = new MapNode("GEOMETRY-SPLIT", middle.X, middle.Y, MapNodeType.Waypoint, true, []);
        first = first with { Distance = Length(first, from, middleNode) };
        second = second with { Distance = Length(second, middleNode, to) };
        return new MapEdgeGeometrySplit(middle, first, second);
    }

    private static void Validate(MapEdge edge, MapNode from, MapNode to)
    {
        if (!TryValidate(edge, from, to, out var error)) throw new InvalidOperationException(error);
    }

    private static MapPoint2D Evaluate(MapEdge edge, MapNode from, MapNode to, double t, ArcData? arc)
    {
        if (arc is not null)
        {
            var angle = arc.StartAngle + arc.Sweep * t;
            return new MapPoint2D(arc.Center.X + arc.Radius * Math.Cos(angle), arc.Center.Y + arc.Radius * Math.Sin(angle));
        }
        if (edge.GeometryKind == MapEdgeGeometryKind.Line) return Lerp(Point(from), Point(to), t);
        var a = Lerp(Point(from), edge.ControlPoints![0], t);
        var b = Lerp(edge.ControlPoints[0], edge.ControlPoints[1], t);
        var c = Lerp(edge.ControlPoints[1], Point(to), t);
        return Lerp(Lerp(a, b, t), Lerp(b, c, t), t);
    }

    private static ArcData GetArc(MapEdge edge, MapNode from, MapNode to)
    {
        var control = edge.ControlPoints![0];
        // Work relative to the start point to avoid cancellation for large CAD coordinates.
        var bx = control.X - from.X; var by = control.Y - from.Y;
        var cx = to.X - from.X; var cy = to.Y - from.Y;
        var determinant = 2 * (bx * cy - by * cx);
        var energy = Math.Max(bx * bx + by * by, cx * cx + cy * cy);
        if (!double.IsFinite(energy) || Math.Abs(determinant) <= 1e-10 * Math.Max(energy, 1e-12))
            throw new InvalidOperationException("圆弧起点、过弧点和终点不能重合或共线。");
        var bSquared = bx * bx + by * by; var cSquared = cx * cx + cy * cy;
        var ox = (cy * bSquared - by * cSquared) / determinant;
        var oy = (bx * cSquared - cx * bSquared) / determinant;
        var center = new MapPoint2D(from.X + ox, from.Y + oy);
        var radius = Math.Sqrt(ox * ox + oy * oy);
        var start = Math.Atan2(-oy, -ox);
        var end = Math.Atan2(cy - oy, cx - ox);
        var controlAngle = Math.Atan2(by - oy, bx - ox);
        var positiveSweep = PositiveAngle(end - start);
        var sweep = PositiveAngle(controlAngle - start) <= positiveSweep ? positiveSweep : positiveSweep - 2 * Math.PI;
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(center.X) || !double.IsFinite(center.Y))
            throw new InvalidOperationException("圆弧无法计算稳定的圆心或半径。");
        return new ArcData(center, radius, start, sweep);
    }

    private static void FlattenBezier(MapPoint2D p0, MapPoint2D p1, MapPoint2D p2, MapPoint2D p3,
        double tolerance, int depth, List<MapPoint2D> result)
    {
        if (depth >= 14 || Math.Max(DistanceToSegment(p1, p0, p3), DistanceToSegment(p2, p0, p3)) <= tolerance)
        {
            result.Add(p3);
            return;
        }
        var a = Lerp(p0, p1, 0.5); var b = Lerp(p1, p2, 0.5); var c = Lerp(p2, p3, 0.5);
        var d = Lerp(a, b, 0.5); var e = Lerp(b, c, 0.5); var middle = Lerp(d, e, 0.5);
        FlattenBezier(p0, a, d, middle, tolerance, depth + 1, result);
        FlattenBezier(middle, e, c, p3, tolerance, depth + 1, result);
    }

    private static double DistanceToSegment(MapPoint2D point, MapPoint2D from, MapPoint2D to)
    {
        var dx = to.X - from.X; var dy = to.Y - from.Y;
        var energy = dx * dx + dy * dy;
        var t = energy > 0 ? Math.Clamp(((point.X - from.X) * dx + (point.Y - from.Y) * dy) / energy, 0, 1) : 0;
        return Distance(point, Lerp(from, to, t));
    }

    private static MapPoint2D Point(MapNode node) => new(node.X, node.Y);
    private static MapPoint2D Lerp(MapPoint2D a, MapPoint2D b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    private static double Distance(MapPoint2D a, MapPoint2D b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static double PositiveAngle(double angle) => (angle % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
    private sealed record ArcData(MapPoint2D Center, double Radius, double StartAngle, double Sweep);
}
