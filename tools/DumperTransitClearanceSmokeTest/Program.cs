using AgvDispatch.Domain.Maps;
using AgvDispatch.Service;

var checks = 0;
void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; Console.WriteLine("PASS " + reason); }
MapNode Node(string id, double x, double y) => new(id, x, y, MapNodeType.Station, true, [], "LM-" + id);
MapEdge Edge(string id, string from, string to) => new(id, from, to, 1, 1, true, true, []);
var map = new MapDefinition("synthetic", "", "RobotWorld", "m",
    [Node("DUMP", 0, 0), Node("S", 2, -3), Node("T", 2, 3), Node("NEAR", 1.15, 0), Node("FAR", 4, 0)],
    [Edge("SN", "S", "NEAR"), Edge("NT", "NEAR", "T"), Edge("SF", "S", "FAR"), Edge("FT", "FAR", "T")], []);
var held = DumperTransitClearance.BlockedTraversals(map, "S", "T", "LM-DUMP", 1.35);
Check(held.Count == 4 && held.All(t => t.EdgeId is "SN" or "NT"), "倒料位相邻窄路双向禁止无关穿行，保留远端路线");
var route = map.ToGraph().FindShortestTransportRoute("S", "T", unavailableTraversals: held);
Check(route?.Nodes.SequenceEqual(["S", "FAR", "T"]) == true, "原图不变，规划绕过近障路段");
Check(DumperTransitClearance.BlockedTraversals(map, "DUMP", "T", "DUMP", 1.35).Count == 0 &&
    DumperTransitClearance.BlockedTraversals(map, "S", "DUMP", "DUMP", 1.35).Count == 0, "倒料进位和原车返程仍由正式路权核验");
Check(DumperTransitClearance.BlockedTraversals(map, "S", "T", "MISSING", 0).Count == 0, "默认关闭时不改变现有运行行为");
foreach (var radius in new[] { double.NaN, -1, double.PositiveInfinity, 51 })
{
    var rejected = false;
    try { DumperTransitClearance.BlockedTraversals(map, "S", "T", "DUMP", radius); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "无效净距拒绝规划：" + radius);
}
var disconnected = map with { Edges = map.Edges.Where(e => e.Id is "SN" or "NT").ToArray() };
Check(disconnected.ToGraph().FindShortestTransportRoute("S", "T", unavailableTraversals:
    DumperTransitClearance.BlockedTraversals(disconnected, "S", "T", "DUMP", 1.35)) is null,
    "没有足够净距的替代路线时保持等待，不缩小车体或穿越");
var guarded = disconnected with
{
    Nodes = disconnected.Nodes.Select(n => n.Id == "T" ? n with { Type = MapNodeType.ChargingPoint }
        : n.Id == "DUMP" ? n with { ResourceIds = ["DUMP-ZONE"] } : n).ToArray(),
    Edges = disconnected.Edges.Select(e => e with { ResourceIds = ["DUMP-ZONE"] }).ToArray(),
    Resources = [new("DUMP-ZONE", TrafficResourceType.Station, 1,
        [new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)])],
};
var guardedEdges = new HashSet<string> { "SN", "NT" };
var guardedBlocked = DumperTransitClearance.BlockedTraversals(guarded, "S", "T", "DUMP", 1.35, guardedEdges, "DUMP-ZONE");
Check(guarded.ToGraph().FindShortestTransportRoute("S", "T", unavailableTraversals: guardedBlocked)?
    .ResourceIds.Contains("DUMP-ZONE") == true, "配置明确且绑定倒料独占路权时允许充电进桩");
Check(guarded.ToGraph().FindShortestTransportRoute("S", "T", new HashSet<string> { "DUMP-ZONE" },
    unavailableTraversals: guardedBlocked) is null, "倒料占用时仍拒绝充电进桩");
Check(DumperTransitClearance.BlockedTraversals(guarded, "T", "S", "DUMP", 1.35, guardedEdges, "DUMP-ZONE").Count == 4
    && DumperTransitClearance.BlockedTraversals(guarded, "S", "T", "DUMP", 1.35, guardedEdges, "WRONG").Count == 4,
    "非进桩方向或缺少正确共享资源时保留原限制");
Console.WriteLine($"PASS {checks} dumper-clearance checks.");
