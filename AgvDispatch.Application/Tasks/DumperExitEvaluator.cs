using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Application.Tasks;

/// <summary>A position observation in the verified active map coordinate frame.</summary>
public sealed record DumperExitFrame(
    string VehicleId, string MapId, string MapVersionId,
    double X, double Y, double LocalizationConfidence, DateTimeOffset ObservedAt,
    SignalQuality Quality, bool Online, bool Blocked, bool EmergencyStopped);

/// <summary>The enclosing radius is measured from the position reference reported by the vehicle.</summary>
public sealed record DumperExitBodyProfile(
    string ProfileId, string VehicleId, double RadiusFromReportedReferenceMeters,
    double MaximumPositionUncertaintyMeters, bool CalibrationVerified,
    bool IncludesMaximumPermittedLoad);

public sealed record DumperExitPolicy(
    IReadOnlyList<string> ResourceIds,
    double MinimumObservationIntervalSeconds = 0.2,
    double MaximumAgeSeconds = 3,
    double MinimumConfidence = 0.8,
    double SafetyMarginMeters = 0.1);

public sealed record DumperExitAssessment(bool Verified, string Reason, IReadOnlyList<string> ResourceIds)
{
    // Only valid, fresh observations whose full envelope still touches the area are
    // ordinary physical-exit waiting. Invalid configuration or evidence is never waiting.
    public bool WaitingForPhysicalExit { get; init; }
}

/// <summary>
/// Pure geometric exit evidence; never writes a PLC bit or releases a lease. The caller must
/// load this exact map version, verify its coordinate binding, bind observations to the active
/// task/cycle, and commit any resulting handoff under its own recovery and ownership gates.
/// No command acceptance, navigation state, or arrival/return event constitutes exit evidence.
/// </summary>
public static class DumperExitEvaluator
{
    private const double Epsilon = 1e-8;

    /// <summary>Validates commissioning inputs without inventing a vehicle position.</summary>
    public static DumperExitAssessment ValidateConfiguration(
        MapDefinition map, string vehicleId, DumperExitBodyProfile? profile, DumperExitPolicy policy)
    {
        static DumperExitAssessment Reject(string reason) => new(false, reason, []);
        if (map is null || policy is null || map.Unit != "m")
            return Reject("缺少米制地图或出区评估配置。");
        if (policy.ResourceIds is null || policy.ResourceIds.Count == 0 ||
            policy.ResourceIds.Any(string.IsNullOrWhiteSpace) ||
            policy.ResourceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != policy.ResourceIds.Count)
            return Reject("倒料区域资源未指定、重复或无效。");
        if (!double.IsFinite(policy.MinimumObservationIntervalSeconds) || policy.MinimumObservationIntervalSeconds < 0.1 ||
            !double.IsFinite(policy.MaximumAgeSeconds) || policy.MaximumAgeSeconds <= 0 || policy.MaximumAgeSeconds > 30 ||
            policy.MinimumObservationIntervalSeconds > policy.MaximumAgeSeconds ||
            !double.IsFinite(policy.MinimumConfidence) || policy.MinimumConfidence <= 0 || policy.MinimumConfidence > 1 ||
            !double.IsFinite(policy.SafetyMarginMeters) || policy.SafetyMarginMeters <= 0 || policy.SafetyMarginMeters > 10)
            return Reject("出区评估阈值无效。");
        if (string.IsNullOrWhiteSpace(vehicleId) || profile is null || string.IsNullOrWhiteSpace(profile.ProfileId) || profile.VehicleId != vehicleId ||
            !profile.CalibrationVerified || !profile.IncludesMaximumPermittedLoad ||
            !double.IsFinite(profile.RadiusFromReportedReferenceMeters) || profile.RadiusFromReportedReferenceMeters <= 0 || profile.RadiusFromReportedReferenceMeters > 100 ||
            !double.IsFinite(profile.MaximumPositionUncertaintyMeters) || profile.MaximumPositionUncertaintyMeters < 0 || profile.MaximumPositionUncertaintyMeters > 10)
            return Reject("未核定原车定位参考点、整车与最大载荷包络及定位误差上限。");

        if (map.Resources is null || map.Resources.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id)) ||
            map.Resources.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != map.Resources.Count)
            return Reject("地图资源标识缺失或重复，无法唯一确定倒料边界。");
        var resources = map.Resources.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var verified = new List<string>();
        foreach (var id in policy.ResourceIds)
        {
            if (!resources.TryGetValue(id, out var resource) || resource.Capacity < 1 || !ValidBoundary(resource.Boundary))
                return Reject($"倒料资源 {id} 缺少有效简单多边形边界，不能推断已出区。");
            verified.Add(resource.Id);
        }
        return new(true, "倒料边界、核定整车载荷与定位误差包络、出区阈值配置有效；尚未提供实际出区证据。", verified);
    }

    public static DumperExitAssessment Evaluate(
        MapDefinition map, DumperExitFrame? first, DumperExitFrame? second,
        DumperExitBodyProfile? profile, DumperExitPolicy policy, DateTimeOffset now)
    {
        static DumperExitAssessment Reject(string reason) => new(false, reason, []);
        if (first is null || second is null)
            return Reject("需要两次独立的新鲜定位观测，单次定位不能确认出区。");
        var configuration = ValidateConfiguration(map, first.VehicleId, profile, policy);
        if (!configuration.Verified) return configuration;
        if (new[] { first.VehicleId, first.MapId, first.MapVersionId }.Any(string.IsNullOrWhiteSpace) ||
            first.VehicleId != second.VehicleId || first.MapId != second.MapId || first.MapVersionId != second.MapVersionId)
            return Reject("两次定位的车辆、地图或版本不一致或身份缺失。");

        foreach (var frame in new[] { first, second })
        {
            if (!frame.Online || frame.Quality != SignalQuality.Good || frame.Blocked || frame.EmergencyStopped ||
                frame.ObservedAt > now || now - frame.ObservedAt > TimeSpan.FromSeconds(policy.MaximumAgeSeconds))
                return Reject("出区定位离线、质量未知/无效、阻塞、急停、过期或时间在未来。");
            if (!FiniteCoordinate(frame.X) || !FiniteCoordinate(frame.Y) ||
                !double.IsFinite(frame.LocalizationConfidence) || frame.LocalizationConfidence < policy.MinimumConfidence || frame.LocalizationConfidence > 1)
                return Reject("出区坐标或定位置信度不可用。");
        }
        if (second.ObservedAt <= first.ObservedAt ||
            (second.ObservedAt - first.ObservedAt).TotalSeconds < policy.MinimumObservationIntervalSeconds)
            return Reject("出区观测重复、时间倒退或采样间隔不足。");

        var resources = map.Resources.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var verified = new List<string>();
        var radius = profile!.RadiusFromReportedReferenceMeters + profile.MaximumPositionUncertaintyMeters + policy.SafetyMarginMeters;
        foreach (var id in policy.ResourceIds)
        {
            if (!resources.TryGetValue(id, out var resource) || resource.Capacity < 1 || !ValidBoundary(resource.Boundary))
                return Reject($"倒料资源 {id} 缺少有效简单多边形边界，不能推断已出区。");
            if (!EntireEnvelopeOutside(first, radius, resource.Boundary!) || !EntireEnvelopeOutside(second, radius, resource.Boundary!))
                return Reject($"两次观测的整车、载荷、定位误差及安全裕量尚未全部离开倒料资源 {id}。") with { WaitingForPhysicalExit = true };
            verified.Add(resource.Id);
        }
        return new(true, "两次新鲜同车同图定位均确认整车及最大载荷包络完全在全部指定倒料边界外；尚未执行任何信号或资源交接。", verified);
    }

    /// <summary>
    /// Verifies the remaining portion of the original, already-sent frozen return route.
    /// Progress is established only on straight edges. Curves are accepted solely when
    /// their analytic conservative bounding box lies entirely outside every dumper envelope.
    /// For every possible
    /// location on that route within the deviation bound, the entire remaining swept circle
    /// must avoid all specified resources. A later reentry or ambiguous nonadjacent route
    /// occurrence prevents handoff, even when the current position itself is outside.
    /// Freshness, task/cycle identity and the two-observation proof remain caller gates.
    /// </summary>
    public static DumperExitAssessment VerifyReturnRouteAfterExit(
        MapDefinition map, IReadOnlyList<string> orderedNodeIds, IReadOnlyList<string> orderedEdgeIds,
        DumperExitFrame frame, DumperExitBodyProfile profile, DumperExitPolicy policy,
        double maximumRouteDeviationMeters = 0.5,bool endpointArrivalConfirmed = false)
    {
        static DumperExitAssessment Reject(string reason) => new(false, reason, []);
        if (map is null || frame is null || profile is null || policy is null || map.Unit != "m" ||
            !double.IsFinite(maximumRouteDeviationMeters) || maximumRouteDeviationMeters <= 0 || maximumRouteDeviationMeters > 2)
            return Reject("缺少冻结返程或路线偏离阈值无效。");
        if (new[] { frame.VehicleId, frame.MapId, frame.MapVersionId, profile.ProfileId }.Any(string.IsNullOrWhiteSpace) ||
            frame.VehicleId != profile.VehicleId || !profile.CalibrationVerified || !profile.IncludesMaximumPermittedLoad ||
            !double.IsFinite(profile.RadiusFromReportedReferenceMeters) || profile.RadiusFromReportedReferenceMeters <= 0 || profile.RadiusFromReportedReferenceMeters > 100 ||
            !double.IsFinite(profile.MaximumPositionUncertaintyMeters) || profile.MaximumPositionUncertaintyMeters < 0 || profile.MaximumPositionUncertaintyMeters > 10 ||
            !double.IsFinite(policy.MinimumConfidence) || policy.MinimumConfidence <= 0 || policy.MinimumConfidence > 1 ||
            !double.IsFinite(policy.SafetyMarginMeters) || policy.SafetyMarginMeters <= 0 || policy.SafetyMarginMeters > 10 ||
            !FiniteCoordinate(frame.X) || !FiniteCoordinate(frame.Y) || !double.IsFinite(frame.LocalizationConfidence) ||
            frame.LocalizationConfidence < policy.MinimumConfidence || frame.LocalizationConfidence > 1 ||
            !frame.Online || frame.Quality != SignalQuality.Good || frame.Blocked || frame.EmergencyStopped)
            return Reject("返程定位身份、状态或完整车体误差包络未经核定。");
        if (orderedNodeIds is null || orderedEdgeIds is null || orderedEdgeIds.Count == 0 || orderedEdgeIds.Count > 10000 ||
            orderedNodeIds.Count != orderedEdgeIds.Count + 1 || orderedNodeIds.Any(string.IsNullOrWhiteSpace) || orderedEdgeIds.Any(string.IsNullOrWhiteSpace))
            return Reject("原发送冻结返程节点与路段序列不完整。");
        if (map.Nodes is null || map.Edges is null || map.Resources is null ||
            map.Nodes.Any(n => n is null || string.IsNullOrWhiteSpace(n.Id)) ||
            map.Edges.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id)) ||
            map.Resources.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id)) ||
            map.Nodes.Select(n => n.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != map.Nodes.Count ||
            map.Edges.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != map.Edges.Count ||
            map.Resources.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != map.Resources.Count)
            return Reject("冻结返程地图对象缺失或标识重复。");
        if (policy.ResourceIds is null || policy.ResourceIds.Count == 0 || policy.ResourceIds.Any(string.IsNullOrWhiteSpace) ||
            policy.ResourceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != policy.ResourceIds.Count)
            return Reject("冻结返程未指定唯一倒料资源。");
        var nodes = map.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var edges = map.Edges.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var resources = map.Resources.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var boundaries = new List<TrafficResourceDefinition>();
        foreach (var id in policy.ResourceIds)
        {
            if (!resources.TryGetValue(id, out var resource) || resource.Capacity < 1 || !ValidBoundary(resource.Boundary))
                return Reject($"冻结返程倒料资源 {id} 缺少有效边界。");
            boundaries.Add(resource);
        }

        var radius = profile.RadiusFromReportedReferenceMeters + profile.MaximumPositionUncertaintyMeters +
            policy.SafetyMarginMeters + maximumRouteDeviationMeters;
        var segments = new List<(MapPoint2D From, MapPoint2D To, MapGeometryBounds? CurveBounds)>();
        var curveCovers = new Dictionary<int, IReadOnlyList<MapGeometryBounds>>();
        for (var i = 0; i < orderedEdgeIds.Count; i++)
        {
            if (!nodes.TryGetValue(orderedNodeIds[i], out var from) || !nodes.TryGetValue(orderedNodeIds[i + 1], out var to) ||
                !from.Enabled || !to.Enabled || !FiniteCoordinate(from.X) || !FiniteCoordinate(from.Y) || !FiniteCoordinate(to.X) || !FiniteCoordinate(to.Y) ||
                !edges.TryGetValue(orderedEdgeIds[i], out var edge) || !edge.Enabled ||
                !(edge.From == from.Id && edge.To == to.Id || edge.Bidirectional && edge.From == to.Id && edge.To == from.Id) ||
                !MapEdgeGeometry.TryValidate(edge, from, to, out _))
                return Reject("冻结返程存在断链、禁用路段、坐标错误或方向不符。");
            var a = new MapPoint2D(from.X, from.Y);
            var b = new MapPoint2D(to.X, to.Y);
            if (Distance(a, b, b) <= Epsilon) return Reject("冻结返程包含零长度路段。");
            MapGeometryBounds? curveBounds = null;
            if (edge.GeometryKind != MapEdgeGeometryKind.Line && !IsStraightDegenerateBezier(edge, from, to))
            {
                // Bezier stays inside its control hull; an arc stays inside the full-circle
                // box. These are analytic outer bounds, never an unverified sampled chord.
                var bounds = MapEdgeGeometry.ConservativeBounds(edge, from, to);
                if (!FiniteCoordinate(bounds.MinX) || !FiniteCoordinate(bounds.MinY) ||
                    !FiniteCoordinate(bounds.MaxX) || !FiniteCoordinate(bounds.MaxY) ||
                    bounds.MinX > bounds.MaxX || bounds.MinY > bounds.MaxY)
                    return Reject("冻结返程曲线保守边界无效。");
                curveBounds = bounds;
                var oriented = edge.GeometryKind == MapEdgeGeometryKind.CubicBezier && edge.From != from.Id
                    ? edge with { ControlPoints = edge.ControlPoints!.Reverse().ToArray() } : edge;
                curveCovers[i] = MapEdgeGeometry.ConservativeCover(oriented, from, to);
            }
            segments.Add((a, b, curveBounds));
        }

        var point = new MapPoint2D(frame.X, frame.Y);
        var candidates = new List<(int Index, double EarliestT)>();
        var finalNode=nodes[orderedNodeIds[^1]];
        var finalPoint=new MapPoint2D(finalNode.X,finalNode.Y);
        var endpointConfirmed=endpointArrivalConfirmed&&Distance(point,finalPoint,finalPoint)<=maximumRouteDeviationMeters+Epsilon;
        for (var i = 0; i < segments.Count; i++)
        {
            var (a, b, curveBounds) = segments[i];
            if (curveBounds is {} bounds)
            {
                    if (!endpointConfirmed&&curveCovers[i].Any(part => PointBoundsDistance(point, part) <= maximumRouteDeviationMeters + Epsilon))
                    return Reject("当前定位可能处于曲线保守边界内，不能从采样推断已通过曲线，等待区外直线定位。");
                continue;
            }
            if (Distance(point, a, b) > maximumRouteDeviationMeters + Epsilon) continue;
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            var projection = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared;
            var perpendicularX = point.X - a.X - projection * dx;
            var perpendicularY = point.Y - a.Y - projection * dy;
            var halfInterval = Math.Sqrt(Math.Max(0, maximumRouteDeviationMeters * maximumRouteDeviationMeters -
                perpendicularX * perpendicularX - perpendicularY * perpendicularY) / lengthSquared);
            candidates.Add((i, Math.Clamp(projection - halfInterval, 0, 1)));
        }
        // The confirmed endpoint follows the last edge in frozen-route order. Keep
        // all earlier geometric matches: arrival cannot erase a nonadjacent overlap
        // or prove that an earlier possible position has passed its final area use.
        if(endpointConfirmed)
            candidates.Add((segments.Count,1));
        if (candidates.Count == 0) return Reject("当前定位偏离原冻结返程，无法确定后续路径。");
        if (candidates.Count > 2 || candidates.Count == 2 && candidates[1].Index != candidates[0].Index + 1)
            return Reject("定位同时对应冻结返程的多个非相邻位置，路径进度有歧义。");

        // Account conservatively for the permitted cross-track corridor as well as the
        // body/localization envelope; do not assume future motion is an exact centerline.
        foreach (var resource in boundaries)
        {
            var polygon = resource.Boundary!;
            if (!segments.Any(s => s.CurveBounds is null && !SegmentEnvelopeOutside(s.From, s.To, radius, polygon)))
                return Reject($"冻结返程未经过倒料资源 {resource.Id}，无法核定最后一次使用。");
            foreach (var candidate in candidates)
                for (var i = candidate.Index; i < segments.Count; i++)
                {
                    var (a, b, curveBounds) = segments[i];
                    if (i == candidate.Index)
                        a = new(a.X + (b.X - a.X) * candidate.EarliestT, a.Y + (b.Y - a.Y) * candidate.EarliestT);
                    if (curveBounds is { } bounds)
                    {
                        var resourceBounds = new MapGeometryBounds(polygon.Min(p => p.X), polygon.Min(p => p.Y),
                            polygon.Max(p => p.X), polygon.Max(p => p.Y));
                        if (BoundsDistance(bounds, resourceBounds) <= radius + Epsilon)
                            return Reject($"冻结返程后续曲线保守包络可能触碰倒料资源 {resource.Id}，保持占用。");
                    }
                    else if (!SegmentEnvelopeOutside(a, b, radius, polygon))
                        return Reject($"冻结返程可能位置尚未通过倒料资源 {resource.Id} 的最后一次使用，或后续会重新触碰该区域。");
                }
        }
        return new(true, "冻结返程所有可能直线位置均已通过全部倒料资源最后一次使用，后续整车载荷包络及曲线保守边界不再触碰；尚未释放资源。",
            boundaries.Select(r => r.Id).ToArray());
    }

    private static bool FiniteCoordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= 1e6;

    // SEER imports geometrically straight cubic paths as DegenerateBezier. Treat only
    // this vendor-verified, collinear form as a straight route; arbitrary Bezier curves
    // retain the conservative curve-boundary gate.
    private static bool IsStraightDegenerateBezier(MapEdge edge, MapNode from, MapNode to)
    {
        if (edge.GeometryKind != MapEdgeGeometryKind.CubicBezier ||
            !string.Equals(edge.VendorPath?.ClassName, "DegenerateBezier", StringComparison.OrdinalIgnoreCase) ||
            edge.ControlPoints is not { Count: 2 } controls)
            return false;
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= Epsilon) return false;
        var parameters = new double[2];
        for (var i = 0; i < controls.Count; i++)
        {
            var point = controls[i];
            parameters[i] = ((point.X - from.X) * dx + (point.Y - from.Y) * dy) / (length * length);
            // SMAP decimal serialization rounds control points independently from the
            // endpoint slope. Two millimetres remains far below vehicle/route tolerances
            // while still excluding a materially curved path.
            if (Distance(point, new MapPoint2D(from.X, from.Y), new MapPoint2D(to.X, to.Y)) > 0.002 ||
                !double.IsFinite(parameters[i]) || parameters[i] < -Epsilon || parameters[i] > 1 + Epsilon)
                return false;
        }
        return parameters[0] <= parameters[1] + Epsilon || parameters[0] + Epsilon >= parameters[1];
    }

    private static double BoundsDistance(MapGeometryBounds a, MapGeometryBounds b)
    {
        var dx = Math.Max(0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
        var dy = Math.Max(0, Math.Max(a.MinY - b.MaxY, b.MinY - a.MaxY));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double PointBoundsDistance(MapPoint2D point, MapGeometryBounds bounds)
    {
        var dx = Math.Max(0, Math.Max(bounds.MinX - point.X, point.X - bounds.MaxX));
        var dy = Math.Max(0, Math.Max(bounds.MinY - point.Y, point.Y - bounds.MaxY));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool EntireEnvelopeOutside(DumperExitFrame frame, double radius, IReadOnlyList<MapPoint2D> polygon)
        => EntireEnvelopeOutside(new MapPoint2D(frame.X, frame.Y), radius, polygon);

    private static bool EntireEnvelopeOutside(MapPoint2D point, double radius, IReadOnlyList<MapPoint2D> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[j];
            var b = polygon[i];
            // Touching the inflated envelope is insufficient, regardless of polygon winding.
            if (Distance(point, a, b) <= radius + Epsilon) return false;
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return !inside;
    }

    private static bool SegmentEnvelopeOutside(MapPoint2D a, MapPoint2D b, double radius, IReadOnlyList<MapPoint2D> polygon)
    {
        if (!EntireEnvelopeOutside(a, radius, polygon) || !EntireEnvelopeOutside(b, radius, polygon)) return false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var c = polygon[i];
            var d = polygon[(i + 1) % polygon.Count];
            if (Intersects(a, b, c, d) || Distance(a, c, d) <= radius + Epsilon || Distance(b, c, d) <= radius + Epsilon ||
                Distance(c, a, b) <= radius + Epsilon || Distance(d, a, b) <= radius + Epsilon) return false;
        }
        return true;
    }

    private static bool ValidBoundary(IReadOnlyList<MapPoint2D>? polygon)
    {
        if (polygon is null || polygon.Count < 3 || polygon.Count > 256 ||
            polygon.Any(p => p is null || !FiniteCoordinate(p.X) || !FiniteCoordinate(p.Y))) return false;
        double twiceArea = 0;
        var origin = polygon[0];
        for (var i = 0; i < polygon.Count; i++)
        {
            var next = (i + 1) % polygon.Count;
            if (Distance(polygon[i], polygon[next], polygon[next]) <= Epsilon) return false;
            twiceArea += Cross(origin, polygon[i], polygon[next]);
            for (var j = i + 1; j < polygon.Count; j++)
                if (j != next && (j + 1) % polygon.Count != i &&
                    Intersects(polygon[i], polygon[next], polygon[j], polygon[(j + 1) % polygon.Count])) return false;
        }
        return Math.Abs(twiceArea) > Epsilon;
    }

    private static double Cross(MapPoint2D a, MapPoint2D b, MapPoint2D c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool Intersects(MapPoint2D a, MapPoint2D b, MapPoint2D c, MapPoint2D d) =>
        (Cross(a, b, c) * Cross(a, b, d) < 0 && Cross(c, d, a) * Cross(c, d, b) < 0) ||
        Distance(a, c, d) <= Epsilon || Distance(b, c, d) <= Epsilon ||
        Distance(c, a, b) <= Epsilon || Distance(d, a, b) <= Epsilon;

    private static double Distance(MapPoint2D point, MapPoint2D a, MapPoint2D b)
    {
        var x = b.X - a.X;
        var y = b.Y - a.Y;
        var squaredLength = x * x + y * y;
        if (squaredLength <= Epsilon * Epsilon)
            return Math.Sqrt((point.X - a.X) * (point.X - a.X) + (point.Y - a.Y) * (point.Y - a.Y));
        var t = Math.Clamp(((point.X - a.X) * x + (point.Y - a.Y) * y) / squaredLength, 0, 1);
        var dx = point.X - a.X - t * x;
        var dy = point.Y - a.Y - t * y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
