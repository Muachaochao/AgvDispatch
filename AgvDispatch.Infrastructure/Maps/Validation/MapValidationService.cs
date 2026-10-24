using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Infrastructure.Maps;

public sealed class MapValidationService : IMapValidationService
{
    public MapValidationReport Validate(MapDefinition definition, double coordinateTolerance = 0.01)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!double.IsFinite(coordinateTolerance) || coordinateTolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coordinateTolerance));
        }

        var issues = new List<MapValidationIssue>();
        if(definition.AreaAnnotations is { } areas)
        {
            if(areas.Count>2000)Error(issues,"AREA_ANNOTATION_BUDGET","厂家区域标注超过2000个。",definition.Name);
            FindDuplicateIds(areas.Select(a=>a.Id),"AREA_ANNOTATION_DUPLICATE","厂家区域标注",issues);
            foreach(var a in areas)
            {
                bool Color(string value)=>value is {Length:7}&&value[0]=='#'&&value.Skip(1).All(Uri.IsHexDigit);
                if(string.IsNullOrWhiteSpace(a.Id)||a.Text is null||a.Text.Length>2048||a.Boundary is null||a.Boundary.Count is <3 or >10000||a.Boundary.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y))||!Color(a.StrokeColor)||!Color(a.FillColor)||!double.IsFinite(a.FillOpacity)||a.FillOpacity is <0 or >1)
                    Error(issues,"AREA_ANNOTATION_INVALID","厂家区域标注边界或显示属性无效。",a.Id);
            }
        }
        FindDuplicateIds(definition.Layers.Select(layer => layer.Id), "LAYER_DUPLICATE", "图层", issues);
        foreach (var layer in definition.Layers)
        {
            if (string.IsNullOrWhiteSpace(layer.Id) || string.IsNullOrWhiteSpace(layer.Name))
                Error(issues, "LAYER_INVALID", "图层编号或名称为空。", layer.Id);
            if (!double.IsFinite(layer.Alpha) || layer.Alpha < 0 || layer.Alpha > 1 ||
                !double.IsFinite(layer.StrokeWidth) || layer.StrokeWidth < .25 || layer.StrokeWidth > 12 ||
                layer.Color is { Length: > 0 } color && !((color.Length is 7 or 9) && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit)))
                Error(issues, "LAYER_STYLE_INVALID", "图层透明度、颜色或线宽无效。", layer.Id);
        }
        if (definition.EditorSettings is { } settings &&
            (!double.IsFinite(settings.WorldWidth) || settings.WorldWidth < 1 ||
             !double.IsFinite(settings.WorldHeight) || settings.WorldHeight < 1 ||
             !double.IsFinite(settings.GridSize) || settings.GridSize <= 0))
            Error(issues, "EDITOR_SETTINGS_INVALID", "画布尺寸和网格间距必须是有效正数。", definition.Name);
        FindDuplicateIds(definition.Nodes.Select(node => node.Id), "NODE_DUPLICATE", "节点", issues);
        FindDuplicateIds(definition.Edges.Select(edge => edge.Id), "EDGE_DUPLICATE", "路段", issues);
        FindDuplicateIds(
            definition.Resources.Select(resource => resource.Id),
            "RESOURCE_DUPLICATE",
            "资源",
            issues);

        var nodes = definition.Nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.Id))
            .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var resources = definition.Resources
            .Where(resource => !string.IsNullOrWhiteSpace(resource.Id))
            .GroupBy(resource => resource.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var node in definition.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || !double.IsFinite(node.X) || !double.IsFinite(node.Y))
            {
                Error(issues, "NODE_INVALID", "节点编号或坐标无效。", node.Id);
            }
            ValidateResourceReferences(node.ResourceIds, node.Id, resources, issues);
            ValidateOverlappingResourceAssignment(node.Id, node.ResourceIds, resources, issues);
        }

        foreach (var resource in definition.Resources)
        {
            if (string.IsNullOrWhiteSpace(resource.Id) || resource.Capacity < 1)
            {
                Error(issues, "RESOURCE_INVALID", "交通资源编号为空或容量小于1。", resource.Id);
            }
            if (resource.Boundary is { Count: > 0 } boundary)
            {
                if (boundary.Count < 3 || boundary.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                    Error(issues, "RESOURCE_BOUNDARY_INVALID", $"交通资源{resource.Id}边界至少需要3个有效坐标点。", resource.Id);
                else if (Math.Abs(PolygonArea(boundary)) <= coordinateTolerance * coordinateTolerance)
                    Error(issues, "RESOURCE_BOUNDARY_ZERO_AREA", $"交通资源{resource.Id}边界面积过小或点位共线。", resource.Id);
                else if (PolygonSelfIntersects(boundary, coordinateTolerance))
                    Error(issues, "RESOURCE_BOUNDARY_SELF_INTERSECTION", $"交通资源{resource.Id}边界存在自交。", resource.Id);
            }
        }

        foreach (var edge in definition.Edges)
        {
            if (string.IsNullOrWhiteSpace(edge.Id) ||
                string.IsNullOrWhiteSpace(edge.From) ||
                string.IsNullOrWhiteSpace(edge.To) ||
                !double.IsFinite(edge.Distance) || edge.Distance <= 0 ||
                !double.IsFinite(edge.SpeedLimit) || edge.SpeedLimit < 0)
            {
                Error(issues, "EDGE_INVALID", "路段编号、端点、距离或限速无效。", edge.Id);
            }
            if (!nodes.ContainsKey(edge.From))
            {
                Error(issues, "EDGE_FROM_MISSING", $"路段{edge.Id}起点{edge.From}不存在。", edge.Id, edge.From);
            }
            if (!nodes.ContainsKey(edge.To))
            {
                Error(issues, "EDGE_TO_MISSING", $"路段{edge.Id}终点{edge.To}不存在。", edge.Id, edge.To);
            }
            if (edge.From.Equals(edge.To, StringComparison.OrdinalIgnoreCase))
            {
                Error(issues, "EDGE_SELF_LOOP", $"路段{edge.Id}起终点相同。", edge.Id, edge.From);
            }
            if (nodes.TryGetValue(edge.From, out var geometryFrom) && nodes.TryGetValue(edge.To, out var geometryTo))
            {
                if (!MapEdgeGeometry.TryValidate(edge, geometryFrom, geometryTo, out var geometryError))
                    Error(issues, "EDGE_GEOMETRY_INVALID", $"路段{edge.Id}几何无效：{geometryError}", edge.Id);
                else if (!double.IsFinite(MapEdgeGeometry.Length(edge, geometryFrom, geometryTo)))
                    Error(issues, "EDGE_GEOMETRY_INVALID", $"路段{edge.Id}几何长度无法计算为有限数值。", edge.Id);
            }
            ValidateResourceReferences(edge.ResourceIds, edge.Id, resources, issues);
            ValidateOverlappingResourceAssignment(edge.Id, edge.ResourceIds, resources, issues);
        }

        ValidateNearDuplicateNodes(definition.Nodes, coordinateTolerance, issues);
        ValidateAgvSiteMappings(definition.Nodes, issues);
        ValidateDuplicateSegments(definition.Edges, issues);
        ValidateParallelGeometries(definition.Edges, issues);
        ValidateConnectivity(definition, nodes, issues);
        ValidateDirectionReachability(definition, nodes, issues);
        ValidateGeometricCrossings(definition, nodes, coordinateTolerance, issues);
        ValidateUnusedResources(definition, resources, issues);
        ValidateResourceBoundaryCoverage(definition, resources, nodes, issues);
        return new MapValidationReport(
            issues
                .OrderByDescending(issue => issue.Severity)
                .ThenBy(issue => issue.Code, StringComparer.Ordinal)
                .ToArray(),
            definition.Nodes.Count,
            definition.Edges.Count,
            definition.Resources.Count);
    }

    private static double PolygonArea(IReadOnlyList<MapPoint2D> points)
    {
        var area = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var next = points[(index + 1) % points.Count];
            area += points[index].X * next.Y - next.X * points[index].Y;
        }
        return area / 2d;
    }

    private static bool PolygonSelfIntersects(IReadOnlyList<MapPoint2D> points, double tolerance)
    {
        for (var first = 0; first < points.Count; first++)
        {
            var firstNext = (first + 1) % points.Count;
            for (var second = first + 1; second < points.Count; second++)
            {
                var secondNext = (second + 1) % points.Count;
                if (first == second || firstNext == second || secondNext == first) continue;
                if (SegmentsCross(points[first], points[firstNext], points[second], points[secondNext], tolerance))
                    return true;
            }
        }
        return false;
    }

    private static bool SegmentsCross(
        MapPoint2D a, MapPoint2D b, MapPoint2D c, MapPoint2D d, double tolerance)
    {
        var o1 = Cross(a, b, c); var o2 = Cross(a, b, d);
        var o3 = Cross(c, d, a); var o4 = Cross(c, d, b);
        return ((o1 > tolerance && o2 < -tolerance) || (o1 < -tolerance && o2 > tolerance)) &&
               ((o3 > tolerance && o4 < -tolerance) || (o3 < -tolerance && o4 > tolerance));
    }

    private static double Cross(MapPoint2D a, MapPoint2D b, MapPoint2D point) =>
        (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);

    private static void ValidateResourceBoundaryCoverage(
        MapDefinition definition,
        IReadOnlyDictionary<string, TrafficResourceDefinition> resources,
        IReadOnlyDictionary<string, MapNode> nodes,
        ICollection<MapValidationIssue> issues)
    {
        foreach (var node in definition.Nodes)
        {
            foreach (var resourceId in node.ResourceIds)
            {
                if (!resources.TryGetValue(resourceId, out var resource) || resource.Boundary is not { Count: >= 3 } boundary)
                    continue;
                if (!PointInPolygon(new MapPoint2D(node.X, node.Y), boundary))
                    Warning(issues, "RESOURCE_NODE_OUTSIDE_BOUNDARY",
                        $"节点{node.Id}绑定了资源{resourceId}，但坐标不在该资源边界内。", node.Id, resourceId);
            }
        }
        foreach (var edge in definition.Edges)
        {
            if (!nodes.TryGetValue(edge.From, out var from) || !nodes.TryGetValue(edge.To, out var to) ||
                !MapEdgeGeometry.TryValidate(edge, from, to, out _)) continue;
            var midpoint = MapEdgeGeometry.PointAt(edge, from, to, 0.5);
            foreach (var resourceId in edge.ResourceIds)
            {
                if (!resources.TryGetValue(resourceId, out var resource) || resource.Boundary is not { Count: >= 3 } boundary)
                    continue;
                if (!PointInPolygon(midpoint, boundary))
                    Warning(issues, "RESOURCE_EDGE_OUTSIDE_BOUNDARY",
                        $"路段{edge.Id}绑定了资源{resourceId}，但路径几何中点不在该资源边界内。", edge.Id, resourceId);
            }
        }
    }

    private static bool PointInPolygon(MapPoint2D point, IReadOnlyList<MapPoint2D> polygon)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            var a = polygon[current]; var b = polygon[previous];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static void ValidateAgvSiteMappings(
        IReadOnlyList<MapNode> nodes,
        ICollection<MapValidationIssue> issues)
    {
        foreach (var duplicate in nodes
                     .Where(node => !string.IsNullOrWhiteSpace(node.AgvSiteId))
                     .GroupBy(node => node.AgvSiteId!, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            Error(
                issues,
                "AGV_SITE_DUPLICATE",
                $"Robokit站点{duplicate.Key}被多个地图节点重复绑定。",
                duplicate.Select(node => node.Id).ToArray());
        }

        foreach (var node in nodes.Where(node =>
                     node.Enabled &&
                     node.Type is MapNodeType.Station or MapNodeType.WaitingPoint or MapNodeType.ChargingPoint &&
                     string.IsNullOrWhiteSpace(node.AgvSiteId)))
        {
            Warning(
                issues,
                "AGV_SITE_MISSING",
                $"业务站点{node.Id}尚未绑定Robokit站点ID，发布前必须补齐。",
                node.Id);
        }
    }

    private static void FindDuplicateIds(
        IEnumerable<string> ids,
        string code,
        string label,
        ICollection<MapValidationIssue> issues)
    {
        foreach (var duplicate in ids
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            Error(issues, code, $"{label}编号{duplicate.Key}重复{duplicate.Count()}次。", duplicate.Key);
        }
    }

    private static void ValidateResourceReferences(
        IEnumerable<string> resourceIds,
        string ownerId,
        IReadOnlyDictionary<string, TrafficResourceDefinition> resources,
        ICollection<MapValidationIssue> issues)
    {
        foreach (var resourceId in resourceIds)
        {
            if (!resources.ContainsKey(resourceId))
            {
                Error(
                    issues,
                    "RESOURCE_REFERENCE_MISSING",
                    $"对象{ownerId}引用的交通资源{resourceId}不存在。",
                    ownerId,
                    resourceId);
            }
        }
    }

    private static void ValidateOverlappingResourceAssignment(
        string ownerId,
        IEnumerable<string> resourceIds,
        IReadOnlyDictionary<string, TrafficResourceDefinition> resources,
        ICollection<MapValidationIssue> issues)
    {
        var exclusive = resourceIds
            .Where(resources.ContainsKey)
            .Select(id => resources[id])
            .Where(resource => resource.Capacity == 1 && resource.Type is
                TrafficResourceType.NarrowAisle or
                TrafficResourceType.SingleLane or
                TrafficResourceType.Intersection)
            .ToArray();
        if (exclusive.Length > 1)
        {
            Warning(
                issues,
                "RESOURCE_OVERLAP",
                $"对象{ownerId}同时属于多个容量为1的互斥区，请确认是否重复圈选。",
                new[] { ownerId }.Concat(exclusive.Select(resource => resource.Id)).ToArray());
        }
    }

    private static void ValidateNearDuplicateNodes(
        IReadOnlyList<MapNode> nodes,
        double tolerance,
        ICollection<MapValidationIssue> issues)
    {
        for (var left = 0; left < nodes.Count; left++)
        {
            for (var right = left + 1; right < nodes.Count; right++)
            {
                if (Distance(nodes[left], nodes[right]) <= tolerance)
                {
                    Warning(
                        issues,
                        "NODE_NEAR_DUPLICATE",
                        $"节点{nodes[left].Id}和{nodes[right].Id}距离小于容差{tolerance}，可能是重复标点。",
                        nodes[left].Id,
                        nodes[right].Id);
                }
            }
        }
    }

    private static void ValidateDuplicateSegments(
        IReadOnlyList<MapEdge> edges,
        ICollection<MapValidationIssue> issues)
    {
        foreach (var group in edges.GroupBy(
                     GeometrySignature,
                     StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            // Two explicit opposite one-way traversals are not duplicate work.
            // Keep direction-specific identities/properties from vendor maps.
            var pair=group.ToArray();
            if(pair.Length==2 && pair.All(e=>!e.Bidirectional) &&
                pair[0].From.Equals(pair[1].To,StringComparison.OrdinalIgnoreCase) &&
                pair[0].To.Equals(pair[1].From,StringComparison.OrdinalIgnoreCase)) continue;
            var ids = group.Select(edge => edge.Id).ToArray();
            Error(
                issues,
                "EDGE_OVERLAP",
                $"多个路段使用相同端点和路径几何：{string.Join(',', ids)}。",
                ids);
        }
    }

    private static void ValidateParallelGeometries(IReadOnlyList<MapEdge> edges,
        ICollection<MapValidationIssue> issues)
    {
        foreach (var group in edges.Where(edge => edge.Enabled).GroupBy(
                     edge => CanonicalSegment(edge.From, edge.To), StringComparer.OrdinalIgnoreCase))
        {
            var alternatives = group.ToArray();
            for (var first = 0; first < alternatives.Length; first++)
            {
                for (var second = first + 1; second < alternatives.Length; second++)
                {
                    var a = alternatives[first]; var b = alternatives[second];
                    if (GeometrySignature(a).Equals(GeometrySignature(b), StringComparison.OrdinalIgnoreCase)) continue;
                    if (!a.Bidirectional && !b.Bidirectional &&
                        !a.From.Equals(b.From, StringComparison.OrdinalIgnoreCase)) continue;
                    Error(issues, "EDGE_PARALLEL_GEOMETRY",
                        $"路段{a.Id}与{b.Id}具有同方向的不同路径几何。当前导航按站点序列，无法区分同方向的不同曲线；请为各路径设置不同的中间站点。",
                        a.Id, b.Id);
                }
            }
        }
    }

    private static void ValidateConnectivity(
        MapDefinition definition,
        IReadOnlyDictionary<string, MapNode> nodes,
        ICollection<MapValidationIssue> issues)
    {
        var adjacency = nodes.Keys.ToDictionary(
            id => id,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        foreach (var edge in definition.Edges.Where(edge => edge.Enabled))
        {
            if (!adjacency.ContainsKey(edge.From) || !adjacency.ContainsKey(edge.To))
            {
                continue;
            }
            adjacency[edge.From].Add(edge.To);
            adjacency[edge.To].Add(edge.From);
        }

        foreach (var node in definition.Nodes.Where(node => node.Enabled &&
                     adjacency.TryGetValue(node.Id, out var links) && links.Count == 0))
        {
            Error(issues, "NODE_ISOLATED", $"节点{node.Id}没有连接任何启用路段。", node.Id);
        }

        var enabled = definition.Nodes.Where(node => node.Enabled).Select(node => node.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var components = new List<List<string>>();
        while (enabled.Count > 0)
        {
            var start = enabled.First();
            var component = Traverse(start, adjacency);
            enabled.ExceptWith(component);
            components.Add(component.ToList());
        }

        if (components.Count > 1)
        {
            var summary = string.Join(" | ", components.Select(component =>
                string.Join(',', component.Take(5)) + (component.Count > 5 ? "..." : string.Empty)));
            Error(
                issues,
                "MAP_DISCONNECTED",
                $"地图存在{components.Count}个互不连通的区域：{summary}。",
                components.SelectMany(component => component).ToArray());
        }
    }

    private static void ValidateDirectionReachability(
        MapDefinition definition,
        IReadOnlyDictionary<string, MapNode> nodes,
        ICollection<MapValidationIssue> issues)
    {
        var adjacency = nodes.Keys.ToDictionary(
            id => id,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        foreach (var edge in definition.Edges.Where(edge => edge.Enabled))
        {
            if (!adjacency.ContainsKey(edge.From) || !adjacency.ContainsKey(edge.To))
            {
                continue;
            }
            adjacency[edge.From].Add(edge.To);
            if (edge.Bidirectional)
            {
                adjacency[edge.To].Add(edge.From);
            }
        }

        var stations = definition.Nodes.Where(node =>
                node.Enabled && node.Type is MapNodeType.Station or MapNodeType.WaitingPoint or
                    MapNodeType.ChargingPoint)
            .Select(node => node.Id)
            .ToArray();
        foreach (var station in stations)
        {
            var reachable = Traverse(station, adjacency);
            var unreachable = stations.Where(target => !reachable.Contains(target)).ToArray();
            if (unreachable.Length > 0)
            {
                Error(
                    issues,
                    "DIRECTION_UNREACHABLE",
                    $"从站点{station}无法按路段方向到达：{string.Join(',', unreachable)}。",
                    new[] { station }.Concat(unreachable).ToArray());
            }
        }
    }

    private static void ValidateGeometricCrossings(
        MapDefinition definition,
        IReadOnlyDictionary<string, MapNode> nodes,
        double tolerance,
        ICollection<MapValidationIssue> issues)
    {
        var edges = definition.Edges.Where(edge => edge.Enabled &&
            nodes.ContainsKey(edge.From) && nodes.ContainsKey(edge.To) &&
            MapEdgeGeometry.TryValidate(edge, nodes[edge.From], nodes[edge.To], out _))
            .Select(edge => new
            {
                Edge = edge,
                Points = MapEdgeGeometry.Flatten(edge, nodes[edge.From], nodes[edge.To], tolerance / 4)
            }).ToArray();
        for (var left = 0; left < edges.Length; left++)
        {
            for (var right = left + 1; right < edges.Length; right++)
            {
                var first = edges[left].Edge;
                var second = edges[right].Edge;
                // Two explicit reverse entries describing the very same lane are not a crossing.
                // Separate overlap checks still reject same-direction and bidirectional duplicates.
                if(!first.Bidirectional&&!second.Bidirectional &&
                    first.From.Equals(second.To,StringComparison.OrdinalIgnoreCase)&&first.To.Equals(second.From,StringComparison.OrdinalIgnoreCase)&&
                    GeometrySignature(first).Equals(GeometrySignature(second),StringComparison.OrdinalIgnoreCase))continue;
                var sharedPoints = new[] { first.From, first.To }.Intersect(
                        new[] { second.From, second.To }, StringComparer.OrdinalIgnoreCase)
                    .Select(id => new MapPoint2D(nodes[id].X, nodes[id].Y)).ToArray();
                if (!PathsCross(edges[left].Points, edges[right].Points, sharedPoints, tolerance))
                {
                    continue;
                }

                var commonResource = first.ResourceIds.Intersect(
                    second.ResourceIds,
                    StringComparer.OrdinalIgnoreCase).Any();
                var code = commonResource ? "EDGE_CROSSING_WITH_RESOURCE" : "EDGE_CROSSING_UNPROTECTED";
                var message = commonResource
                    ? $"路段{first.Id}与{second.Id}几何相交但没有交点节点，请确认地图拓扑。"
                    : $"路段{first.Id}与{second.Id}几何相交，既没有交点节点也没有共享路口资源。";
                Error(issues, code, message, first.Id, second.Id);
            }
        }
    }

    private static bool PathsCross(IReadOnlyList<MapPoint2D> first, IReadOnlyList<MapPoint2D> second,
        IReadOnlyList<MapPoint2D> allowedSharedEndpoints, double tolerance)
    {
        if (first.Max(point => point.X) + tolerance < second.Min(point => point.X) ||
            second.Max(point => point.X) + tolerance < first.Min(point => point.X) ||
            first.Max(point => point.Y) + tolerance < second.Min(point => point.Y) ||
            second.Max(point => point.Y) + tolerance < first.Min(point => point.Y)) return false;
        for (var i = 1; i < first.Count; i++)
        {
            for (var j = 1; j < second.Count; j++)
            {
                var a = first[i - 1]; var b = first[i]; var c = second[j - 1]; var d = second[j];
                if (Math.Max(a.X, b.X) + tolerance < Math.Min(c.X, d.X) ||
                    Math.Max(c.X, d.X) + tolerance < Math.Min(a.X, b.X) ||
                    Math.Max(a.Y, b.Y) + tolerance < Math.Min(c.Y, d.Y) ||
                    Math.Max(c.Y, d.Y) + tolerance < Math.Min(a.Y, b.Y)) continue;
                var rx = b.X - a.X; var ry = b.Y - a.Y;
                var sx = d.X - c.X; var sy = d.Y - c.Y;
                var denominator = rx * sy - ry * sx;
                var firstLength = Math.Sqrt(rx * rx + ry * ry);
                var secondLength = Math.Sqrt(sx * sx + sy * sy);
                if (firstLength <= 1e-12 || secondLength <= 1e-12 ||
                    Math.Abs(denominator) <= 1e-12 * firstLength * secondLength) continue;
                var qx = c.X - a.X; var qy = c.Y - a.Y;
                var t = (qx * sy - qy * sx) / denominator;
                var u = (qx * ry - qy * rx) / denominator;
                if (t < -tolerance / firstLength || t > 1 + tolerance / firstLength ||
                    u < -tolerance / secondLength || u > 1 + tolerance / secondLength) continue;
                var intersection = new MapPoint2D(a.X + t * rx, a.Y + t * ry);
                if (allowedSharedEndpoints.Any(point =>
                        Math.Pow(intersection.X - point.X, 2) + Math.Pow(intersection.Y - point.Y, 2) <= tolerance * tolerance))
                    continue;
                return true;
            }
        }
        return false;
    }

    private static string GeometrySignature(MapEdge edge)
    {
        var reversed = string.Compare(edge.From, edge.To, StringComparison.OrdinalIgnoreCase) > 0;
        var controls = reversed ? edge.ControlPoints?.Reverse() : edge.ControlPoints;
        return $"{CanonicalSegment(edge.From, edge.To)}|{edge.GeometryKind}|" +
               string.Join(';', controls?.Select(point => point is null ? "INVALID" : $"{point.X:R},{point.Y:R}") ?? []);
    }

    private static void ValidateUnusedResources(
        MapDefinition definition,
        IReadOnlyDictionary<string, TrafficResourceDefinition> resources,
        ICollection<MapValidationIssue> issues)
    {
        var used = definition.Nodes.SelectMany(node => node.ResourceIds)
            .Concat(definition.Edges.SelectMany(edge => edge.ResourceIds))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceId in resources.Keys.Where(id => !used.Contains(id)))
        {
            Warning(issues, "RESOURCE_UNUSED", $"交通资源{resourceId}没有绑定节点或路段。", resourceId);
        }
    }

    private static HashSet<string> Traverse(
        string start,
        IReadOnlyDictionary<string, HashSet<string>> adjacency)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        visited.Add(start);
        queue.Enqueue(start);
        while (queue.TryDequeue(out var current))
        {
            foreach (var next in adjacency[current])
            {
                if (visited.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }
        return visited;
    }

    private static double Distance(MapNode left, MapNode right) => Math.Sqrt(
        Math.Pow(right.X - left.X, 2) + Math.Pow(right.Y - left.Y, 2));

    private static string CanonicalSegment(string from, string to) =>
        string.Compare(from, to, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{from}|{to}"
            : $"{to}|{from}";

    private static void Error(
        ICollection<MapValidationIssue> issues,
        string code,
        string message,
        params string[] ids) => issues.Add(new MapValidationIssue(
        MapValidationSeverity.Error,
        code,
        message,
        ids.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray()));

    private static void Warning(
        ICollection<MapValidationIssue> issues,
        string code,
        string message,
        params string[] ids) => issues.Add(new MapValidationIssue(
        MapValidationSeverity.Warning,
        code,
        message,
        ids.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray()));
}
