using AgvDispatch.Application.Tasks;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
    Console.WriteLine("PASS " + name);
}

var now = new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
MapPoint2D[] Box(double left, double bottom, double right, double top) =>
    [new(left, bottom), new(right, bottom), new(right, top), new(left, top)];
var boundary = Box(0, 0, 2, 2);
var map = new MapDefinition("Dumper exit", "test fixture", "LocalCartesian", "m", [], [],
    [new("DUMP", TrafficResourceType.Station, 1, boundary)]);
var first = new DumperExitFrame("AGV-1", "MAP", "V1", 4, 1, 0.95, now.AddSeconds(-2), SignalQuality.Good, true, false, false);
var second = first with { X = 4.1, ObservedAt = now.AddSeconds(-1) };
var profile = new DumperExitBodyProfile("AGV1-LOAD-MAX", "AGV-1", 0.5, 0.15, true, true);
var policy = new DumperExitPolicy(["DUMP"]);

DumperExitAssessment Evaluate(MapDefinition? m = null, DumperExitFrame? a = null, DumperExitFrame? b = null,
    DumperExitBodyProfile? body = null, DumperExitPolicy? p = null) =>
    DumperExitEvaluator.Evaluate(m ?? map, a ?? first, b ?? second, body ?? profile, p ?? policy, now);
void Refuse(string name, MapDefinition? m = null, DumperExitFrame? a = null, DumperExitFrame? b = null,
    DumperExitBodyProfile? body = null, DumperExitPolicy? p = null)
{
    var result = Evaluate(m, a, b, body, p);
    Check(!result.Verified && result.ResourceIds.Count == 0 && !string.IsNullOrWhiteSpace(result.Reason), name);
}

var valid = Evaluate();
Check(valid.Verified && valid.ResourceIds.SequenceEqual(["DUMP"]), "同车两次新鲜外区观测和核定整车载荷包络通过");
Check(DumperExitEvaluator.ValidateConfiguration(map, "AGV-1", profile, policy).Verified, "首段绑定可独立验证核定配置，不构造虚拟位置");
Check(!DumperExitEvaluator.ValidateConfiguration(map, "AGV-2", profile, policy).Verified, "配置验证拒绝其他车辆包络");
Check(!DumperExitEvaluator.ValidateConfiguration(map with { Resources = [map.Resources[0] with { Boundary = null }] }, "AGV-1", profile, policy).Verified,
    "首段配置验证就拒绝缺失边界，无需等到返程");
Check(!DumperExitEvaluator.ValidateConfiguration(map, "AGV-1", profile with { CalibrationVerified = false }, policy).Verified,
    "首段配置验证就拒绝未核定载荷包络");
Check(Evaluate(m: map with { Resources = [map.Resources[0] with { Boundary = boundary.Reverse().ToArray() }] }).Verified,
    "反向多边形绕序同样适用");
Check(Evaluate(p: policy with { ResourceIds = ["dump"] }).ResourceIds.SequenceEqual(["DUMP"]), "资源匹配不区分大小写且返回规范标识");
Check(Evaluate(a: first with { LocalizationConfidence = policy.MinimumConfidence }).Verified, "达到最低置信度允许评估");
Check(Evaluate(b: second with { ObservedAt = now }).Verified, "最新观测恰好为当前时刻允许");
Check(Evaluate(a: first with { ObservedAt = now.AddSeconds(-3) }).Verified, "观测恰好达到允许最大年龄");

Refuse("首帧仍在倒料区拒绝，不能只检查第二帧", a: first with { X = 1 });
Refuse("第二帧重入倒料区拒绝", b: second with { X = 1 });
Refuse("两帧中心均在区内拒绝", a: first with { X = 1 }, b: second with { X = 1.1 });
Refuse("定位中心在边界拒绝", a: first with { X = 2 });
Refuse("中心已在区外但完整车体尚未离开拒绝", a: first with { X = 2.4 });
Refuse("车体误差与裕量刚好接触边界拒绝", a: first with { X = 2.75 });
Refuse("载荷包络扩大后仍覆盖边界拒绝", body: profile with { RadiusFromReportedReferenceMeters = 2 });
Refuse("定位误差扩大后仍覆盖边界拒绝", body: profile with { MaximumPositionUncertaintyMeters = 1.5 });
Refuse("安全裕量扩大后仍覆盖边界拒绝", p: policy with { SafetyMarginMeters = 1.5 });
Check(Evaluate(a: first with { X = 2.751 }, b: second with { X = 2.752 }).Verified, "整车包络有明确额外间隙才通过");

var cornerDistance = (profile.RadiusFromReportedReferenceMeters + profile.MaximumPositionUncertaintyMeters + policy.SafetyMarginMeters) / Math.Sqrt(2);
Refuse("车体圆与多边形角点相切拒绝", a: first with { X = 2 + cornerDistance, Y = 2 + cornerDistance });
Check(Evaluate(a: first with { X = 2 + cornerDistance + 0.01, Y = 2 + cornerDistance + 0.01 }).Verified,
    "角点外充分间隙通过");

// A first inside/outside pair fails; the subsequent outside/outside pair can establish exit.
var inside = first with { X = 1, ObservedAt = now.AddSeconds(-2.5) };
Check(!DumperExitEvaluator.Evaluate(map, inside, first, profile, policy, now).Verified &&
      DumperExitEvaluator.Evaluate(map, first, second, profile, policy, now).Verified,
    "出区后的下一次外外采样对可以确认，无需等待返程完成");

var concave = map with { Resources = [new("DUMP", TrafficResourceType.Station, 1,
    [new(0, 0), new(4, 0), new(4, 4), new(3, 4), new(3, 1), new(1, 1), new(1, 4), new(0, 4)])] };
Check(Evaluate(m: concave, a: first with { X = 2, Y = 3 }, b: second with { X = 2, Y = 3.1 }).Verified,
    "简单凹多边形缺口外且包络不相交允许");
Refuse("简单凹多边形实体内拒绝", m: concave, a: first with { X = 2, Y = 0.5 });

var multiple = map with { Resources = [map.Resources[0], new("EXIT", TrafficResourceType.SingleLane, 1, Box(5, 0, 7, 2))] };
var multiPolicy = policy with { ResourceIds = ["DUMP", "EXIT"] };
Check(Evaluate(m: multiple, p: multiPolicy).ResourceIds.SequenceEqual(["DUMP", "EXIT"]), "所有指定倒料与出口区域同时出区才通过");
Refuse("第二个指定区域仍占用时不返回任何可交接资源", m: multiple with
    { Resources = [multiple.Resources[0], multiple.Resources[1] with { Boundary = Box(4, 0, 6, 2) }] }, p: multiPolicy);
Refuse("指定资源缺失拒绝", p: policy with { ResourceIds = ["MISSING"] });
Refuse("空资源列表拒绝", p: policy with { ResourceIds = [] });
Refuse("无效资源标识拒绝", p: policy with { ResourceIds = [""] });
Refuse("重复资源标识拒绝", p: policy with { ResourceIds = ["DUMP", "dump"] });
Refuse("地图中重复资源标识拒绝", m: map with { Resources = [map.Resources[0], map.Resources[0] with { Id = "dump" }] });
Refuse("资源容量无效拒绝", m: map with { Resources = [map.Resources[0] with { Capacity = 0 }] });
Refuse("非米制地图拒绝", m: map with { Unit = "mm" });

var badBoundaries = new (string Name, IReadOnlyList<MapPoint2D>? Points)[]
{
    ("缺少边界", null),
    ("不足三个顶点", [new(0, 0), new(1, 0)]),
    ("零面积共线", [new(0, 0), new(1, 0), new(2, 0)]),
    ("相邻重复点", [new(0, 0), new(0, 0), new(1, 1)]),
    ("重复首尾点导致零长度边", [new(0, 0), new(1, 0), new(1, 1), new(0, 0)]),
    ("蝴蝶自交", [new(0, 0), new(2, 2), new(0, 2), new(2, 0)]),
    ("相邻回折边重叠", [new(0, 0), new(2, 0), new(1, 0), new(2, 2), new(0, 2)]),
    ("非相邻边触碰", [new(0, 0), new(2, 0), new(2, 2), new(1, 0), new(0, 2)]),
    ("非有限坐标", [new(0, 0), new(double.NaN, 0), new(0, 1)]),
    ("顶点超出坐标限度", [new(0, 0), new(1e7, 0), new(0, 1)]),
    ("空顶点", [new(0, 0), null!, new(0, 1)]),
    ("顶点数超限", Enumerable.Range(0, 257).Select(i => new MapPoint2D(Math.Cos(i * 2 * Math.PI / 257), Math.Sin(i * 2 * Math.PI / 257))).ToArray())
};
foreach (var (name, points) in badBoundaries)
    Refuse(name + "拒绝", m: map with { Resources = [map.Resources[0] with { Boundary = points }] });

foreach (var bad in new[]
{
    second with { VehicleId = "AGV-2" }, second with { MapId = "OTHER" }, second with { MapVersionId = "V2" },
    second with { VehicleId = "" }, second with { MapId = "" }, second with { MapVersionId = "" }
}) Refuse("车辆地图版本身份不一致拒绝", b: bad);
Refuse("两帧均缺失地图身份拒绝", a: first with { MapId = "" }, b: second with { MapId = "" });
foreach (var quality in new[] { SignalQuality.Unknown, SignalQuality.Bad, SignalQuality.Stale, SignalQuality.Disabled })
{
    Refuse("首帧质量" + quality + "拒绝", a: first with { Quality = quality });
    Refuse("次帧质量" + quality + "拒绝", b: second with { Quality = quality });
}
foreach (var bad in new[]
{
    first with { Online = false }, first with { Blocked = true }, first with { EmergencyStopped = true },
    first with { X = double.NaN }, first with { Y = double.PositiveInfinity }, first with { X = 1e7 },
    first with { LocalizationConfidence = 0.1 }, first with { LocalizationConfidence = 1.1 }, first with { LocalizationConfidence = double.NaN },
    first with { ObservedAt = now.AddSeconds(1) }, first with { ObservedAt = now.AddSeconds(-4) }
}) Refuse("首帧定位/状态/新鲜度不满足拒绝", a: bad);
Refuse("第二帧未来时间拒绝", b: second with { ObservedAt = now.AddMilliseconds(1) });
Refuse("第二帧重复时刻拒绝", b: second with { ObservedAt = first.ObservedAt });
Refuse("第二帧时间倒退拒绝", b: second with { ObservedAt = first.ObservedAt.AddMilliseconds(-1) });
Refuse("两帧时间间隔不足拒绝", b: second with { ObservedAt = first.ObservedAt.AddMilliseconds(10) });

foreach (var bad in new[]
{
    profile with { CalibrationVerified = false }, profile with { IncludesMaximumPermittedLoad = false },
    profile with { ProfileId = "" }, profile with { VehicleId = "AGV-2" },
    profile with { RadiusFromReportedReferenceMeters = 0 }, profile with { RadiusFromReportedReferenceMeters = double.PositiveInfinity },
    profile with { MaximumPositionUncertaintyMeters = -0.1 }, profile with { MaximumPositionUncertaintyMeters = double.NaN }
}) Refuse("未核定或无效车体误差配置拒绝", body: bad);
foreach (var bad in new[]
{
    policy with { MinimumObservationIntervalSeconds = 0 }, policy with { MinimumObservationIntervalSeconds = double.NaN },
    policy with { MinimumObservationIntervalSeconds = 4 }, policy with { MaximumAgeSeconds = 0 },
    policy with { MaximumAgeSeconds = 31 }, policy with { MaximumAgeSeconds = double.NaN },
    policy with { MinimumConfidence = 0 }, policy with { MinimumConfidence = 2 },
    policy with { SafetyMarginMeters = 0 }, policy with { SafetyMarginMeters = double.NaN }
}) Refuse("非法评估配置拒绝", p: bad);

Check(!DumperExitEvaluator.Evaluate(map, null, second, profile, policy, now).Verified, "缺少首帧拒绝");
Check(!DumperExitEvaluator.Evaluate(map, first, null, profile, policy, now).Verified, "缺少次帧拒绝");
Check(!DumperExitEvaluator.Evaluate(map, first, second, null, policy, now).Verified, "缺少包络拒绝");
Check(!DumperExitEvaluator.Evaluate(null!, first, second, profile, policy, now).Verified, "缺少地图拒绝");
Check(!DumperExitEvaluator.Evaluate(map, first, second, profile, null!, now).Verified, "缺少策略拒绝");

var translated = map with { Resources = [map.Resources[0] with { Boundary = Box(999900, 999900, 999902, 999902) }] };
Check(Evaluate(m: translated, a: first with { X = 999904, Y = 999901 }, b: second with { X = 999904.1, Y = 999901 }).Verified,
    "大坐标平移不改变有效边界和出区判定");
Check(map.Resources[0].Boundary!.SequenceEqual(boundary) && first.X == 4 && profile.RadiusFromReportedReferenceMeters == 0.5,
    "评估不修改地图、观测、包络且无外部释放动作");

MapDefinition ReturnMap(params (string Id, double X, double Y)[] coordinates)
{
    var routeNodes = coordinates.Select(p => new MapNode(p.Id, p.X, p.Y, MapNodeType.Waypoint, true, [])).ToArray();
    var routeEdges = Enumerable.Range(0, routeNodes.Length - 1).Select(i => new MapEdge("E" + i,
        routeNodes[i].Id, routeNodes[i + 1].Id,
        Math.Sqrt(Math.Pow(routeNodes[i + 1].X - routeNodes[i].X, 2) + Math.Pow(routeNodes[i + 1].Y - routeNodes[i].Y, 2)),
        1, false, true, [])).ToArray();
    return map with { Nodes = routeNodes, Edges = routeEdges };
}
DumperExitAssessment Route(MapDefinition routeMap, DumperExitFrame? position = null,
    IReadOnlyList<string>? nodeIds = null, IReadOnlyList<string>? edgeIds = null, double deviation = 0.5) =>
    DumperExitEvaluator.VerifyReturnRouteAfterExit(routeMap,
        nodeIds ?? routeMap.Nodes.Select(n => n.Id).ToArray(), edgeIds ?? routeMap.Edges.Select(e => e.Id).ToArray(),
        position ?? first, profile, policy, deviation);
void RouteRefuse(string name, MapDefinition routeMap, DumperExitFrame? position = null,
    IReadOnlyList<string>? nodeIds = null, IReadOnlyList<string>? edgeIds = null, double deviation = 0.5)
{
    var result = Route(routeMap, position, nodeIds, edgeIds, deviation);
    Check(!result.Verified && result.ResourceIds.Count == 0, name);
}
var straightReturn = ReturnMap(("D", 1, 1), ("EXIT", 5, 1), ("HOME", 8, 1));
Check(Route(straightReturn).Verified, "原冻结返程后续直线路径不再进入倒料区允许");
Check(Route(straightReturn, first with { X = 5 }).Verified, "相邻路段共有端点的两种可能进度均安全允许");
Check(Route(straightReturn, first with { X = 8 }).Verified, "冻结返程终点外且全部未来位置安全允许");
RouteRefuse("中心出区但路线偏离范围与车体仍可能触边拒绝", straightReturn, first with { X = 3.5 });
RouteRefuse("定位不在原冻结返程拒绝", straightReturn, first with { X = 4, Y = 4 });
RouteRefuse("原冻结返程后续重新进入倒料区拒绝",
    ReturnMap(("D", 1, 1), ("EXIT", 5, 1), ("REENTER", 1, 1), ("HOME", -3, 1)));
RouteRefuse("未来路段掠过区域载荷包络时拒绝",
    ReturnMap(("D", 1, 1), ("EXIT", 5, 1), ("TOP", 5, 3), ("HOME", -3, 3)));
RouteRefuse("原路线多个非相邻位置均匹配产生进度歧义拒绝",
    ReturnMap(("D", 1, 1), ("A", 5, 1), ("B", 5, 4), ("C", 4, 4), ("D2", 4, 1), ("HOME", 8, 1)));
RouteRefuse("原路线未曾使用指定倒料资源拒绝", ReturnMap(("A", 4, 1), ("HOME", 8, 1)));
RouteRefuse("冻结路线顺序断链拒绝", straightReturn, nodeIds: ["D", "HOME", "EXIT"]);
RouteRefuse("冻结路线方向不符拒绝", straightReturn with { Edges =
    [straightReturn.Edges[0] with { From = "EXIT", To = "D" }, straightReturn.Edges[1]] });
Check(Route(straightReturn with { Edges =
    [straightReturn.Edges[0] with { From = "EXIT", To = "D", Bidirectional = true }, straightReturn.Edges[1]] }).Verified,
    "双向边按原冻结方向行驶允许");
RouteRefuse("禁用原冻结路段拒绝", straightReturn with { Edges =
    [straightReturn.Edges[0] with { Enabled = false }, straightReturn.Edges[1]] });
RouteRefuse("禁用原冻结节点拒绝", straightReturn with { Nodes =
    [straightReturn.Nodes[0], straightReturn.Nodes[1] with { Enabled = false }, straightReturn.Nodes[2]] });
RouteRefuse("缺少原冻结路段拒绝", straightReturn, edgeIds: ["MISSING", "E1"]);
RouteRefuse("原冻结节点边数不符拒绝", straightReturn, edgeIds: ["E0"]);
RouteRefuse("未核定曲线采样误差时明确拒绝", straightReturn with { Edges =
    [straightReturn.Edges[0] with { GeometryKind = MapEdgeGeometryKind.CubicBezier, ControlPoints = [new(2, 1), new(4, 1)] }, straightReturn.Edges[1]] });
RouteRefuse("非法路线偏离阈值拒绝", straightReturn, deviation: double.NaN);
RouteRefuse("零路线偏离阈值拒绝", straightReturn, deviation: 0);
RouteRefuse("未来边界缺失拒绝", straightReturn with { Resources = [map.Resources[0] with { Boundary = null }] });

var futureCurve = ReturnMap(("D", 1, 1), ("EXIT", 6, 1), ("HOME", 9, 5));
futureCurve = futureCurve with { Edges = [futureCurve.Edges[0], futureCurve.Edges[1] with
    { GeometryKind = MapEdgeGeometryKind.CubicBezier, ControlPoints = [new(7, 2), new(8, 4)] }] };
Check(Route(futureCurve).Verified, "直线出区后未来Bezier完整控制凸包远离倒料区允许");
RouteRefuse("当前定位可能在Bezier保守边界内时保持占用", futureCurve, first with { X = 7, Y = 3 });
RouteRefuse("当前定位既可能是直线也可能是区外曲线时拒绝", futureCurve, first with { X = 6, Y = 1 });
RouteRefuse("Bezier控制包络可能回穿倒料区时拒绝", futureCurve with { Edges =
    [futureCurve.Edges[0], futureCurve.Edges[1] with { ControlPoints = [new(1, 1), new(8, 4)] }] });
var futureArc = futureCurve with { Edges = [futureCurve.Edges[0], futureCurve.Edges[1] with
    { GeometryKind = MapEdgeGeometryKind.CircularArc, ControlPoints = [new(9, 1)] }] };
Check(Route(futureArc).Verified, "未来圆弧全圆保守边界与倒料区分离允许");
Check(Route(futureArc, first with { X = 5.25, Y = 1 }).Verified,
    "位于原直线上但远离真实圆弧时不被全圆矩形误阻塞");
var looseBezier = futureCurve with { Edges = [futureCurve.Edges[0], futureCurve.Edges[1] with
    { ControlPoints = [new(4, 4), new(8, 4)] }] };
Check(Route(looseBezier, first with { X = 4.25, Y = 1 }).Verified,
    "位于原直线上但远离真实Bezier时不被整条控制矩形误阻塞");
foreach (var curved in new[] { futureCurve, looseBezier, futureArc })
{
    var edge=curved.Edges[1]; var from=curved.Nodes[1]; var to=curved.Nodes[2];
    var cover=MapEdgeGeometry.ConservativeCover(edge,from,to);
    Check(Enumerable.Range(0,4097).All(i=>
    {
        var p=MapEdgeGeometry.PointAt(edge,from,to,i/4096.0);
        return cover.Any(b=>p.X>=b.MinX&&p.X<=b.MaxX&&p.Y>=b.MinY&&p.Y<=b.MaxY);
    }), "曲线细分包络覆盖全参数区间的密集回归位置");
    var near=MapEdgeGeometry.PointAt(edge,from,to,.5);
    RouteRefuse("真实曲线上位置仍拒绝推断已出曲线",curved,first with { X=near.X,Y=near.Y });
    var reversed=edge with { From=edge.To,To=edge.From,Bidirectional=true,ControlPoints=edge.GeometryKind==MapEdgeGeometryKind.CubicBezier?edge.ControlPoints!.Reverse().ToArray():edge.ControlPoints };
    var reverseMap=curved with { Edges=[curved.Edges[0],reversed] };
    RouteRefuse("反向存储双向曲线仍拒绝真实曲线定位",reverseMap,first with { X=near.X,Y=near.Y });
}
RouteRefuse("当前定位可能处于全圆保守边界时拒绝", futureArc, first with { X = 7, Y = 3 });
RouteRefuse("曲线控制点超出几何核定范围拒绝", futureCurve with { Edges =
    [futureCurve.Edges[0], futureCurve.Edges[1] with { ControlPoints = [new(1e7, 2), new(8, 4)] }] });

Console.WriteLine($"PASS: {checks} dumper exit geometry checks. No signals or leases changed.");
