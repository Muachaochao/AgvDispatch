using AgvDispatch.Domain.Maps;
using AgvDispatch.Service;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var nodes = new[]
{
    new MapNode("A", 0, 0, MapNodeType.Station, true, []),
    new MapNode("B", 1, 0, MapNodeType.Waypoint, true, []),
    new MapNode("C", 2, 0, MapNodeType.Intersection, true, []),
    new MapNode("D", 3, 0, MapNodeType.Waypoint, true, []),
    new MapNode("E", 4, 0, MapNodeType.WaitingPoint, true, ["WAIT-E"])
};
var edges = new[]
{
    new MapEdge("AB", "A", "B", 1, 1, false, true, ["R1"]),
    new MapEdge("BC", "B", "C", 1, 1, false, true, ["R2"]),
    new MapEdge("CD", "C", "D", 1, 1, false, true, ["R3"]),
    new MapEdge("DE", "D", "E", 1, 1, false, true, ["R4"])
};
var resources = new[]
{
    new TrafficResourceDefinition("R1", TrafficResourceType.SingleLane),
    new TrafficResourceDefinition("R2", TrafficResourceType.Intersection),
    new TrafficResourceDefinition("R3", TrafficResourceType.SingleLane),
    new TrafficResourceDefinition("R4", TrafficResourceType.Station)
};
var options = new DispatchTaskStepOptions
{
    RollingReservationEnabled = true,
    RollingWindowMinimumEdges = 2,
    RollingWindowMaximumEdges = 12
};
var plan = TransportRouteWindows.Plan(
    new RoutePlan(["A", "B", "C", "D", "E"], ["AB", "BC", "CD", "DE"],
        ["R1", "R2", "R3", "R4", "WAIT-E"], 4),
    new MapDefinition("TEST", "", "RobotWorld", "m", nodes, edges, resources), options);

Check(plan.Count == 2, "路线应在中间安全路口拆成两个窗口。");
Check(plan[0].Path.SequenceEqual(["A", "B", "C"]), "首个窗口必须止于安全路口 C。");
Check(plan[0].ResourceIds.Contains("R1") && plan[0].ResourceIds.Contains("R2") &&
      !plan[0].ResourceIds.Contains("R3") && !plan[0].ResourceIds.Contains("R4") &&
      !plan[0].ResourceIds.Contains("WAIT-E"),
    "首个窗口只应占用已覆盖路段及安全点资源。");
Check(plan[0].Final == false && plan[1].Final, "只有末窗口可以在终点执行车辆动作。");
Check(plan[1].Path.SequenceEqual(["C", "D", "E"]), "后续窗口必须从安全停靠点连续接续。");
Check(plan[1].ResourceIds.Contains("WAIT-E"), "末窗口应保留目标等待位资源。");
var automaticCorridors = MapCorridorResources.Enrich(
        new MapDefinition("TEST", "", "RobotWorld", "m", nodes, edges, resources))
    .Resources.Where(resource => MapCorridorResources.IsAutomatic(resource.Id))
    .Select(resource => resource.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
Check(automaticCorridors.Count > 0 &&
      plan.SelectMany(window => window.ResourceIds).Any(automaticCorridors.Contains),
    "滚动窗口必须携带运行时自动路段资源，不能只保留显式站点资源。");
var protectedExit = TransportRouteWindows.IncludeDumperExitClearance(
    plan, new MapDefinition("TEST", "", "RobotWorld", "m", nodes, edges, resources), "A");
Check(protectedExit.Count == 1 && protectedExit[0].Source == "A" && protectedExit[0].Destination == "E" &&
      protectedExit[0].Path.SequenceEqual(["A", "B", "C", "D", "E"]) &&
      protectedExit[0].ResourceIds.Contains("R1") && protectedExit[0].ResourceIds.Contains("R4"),
    "倒料返程首窗必须合并下一安全窗，原子保留出口走廊且不改变完整路线。");
Check(ReferenceEquals(plan, TransportRouteWindows.IncludeDumperExitClearance(
          plan, new MapDefinition("TEST", "", "RobotWorld", "m", nodes, edges, resources), "OTHER")),
    "非倒料起点不能触发出口清空合并。");

var disabled = TransportRouteWindows.Plan(
    new RoutePlan(["A", "B", "C", "D", "E"], ["AB", "BC", "CD", "DE"], ["R1", "R2", "R3", "R4"], 4),
    new MapDefinition("TEST", "", "RobotWorld", "m", nodes, edges, resources),
    new DispatchTaskStepOptions { RollingReservationEnabled = false });
Check(disabled.Count == 1 && disabled[0].Final, "关闭滚动窗口后必须保持兼容的整条路线命令。");

var longNodes = new[]
{
    new MapNode("A", 0, 0, MapNodeType.Station, true, []),
    new MapNode("B", 1, 0, MapNodeType.Waypoint, true, []),
    new MapNode("C", 2, 0, MapNodeType.Waypoint, true, []),
    new MapNode("D", 3, 0, MapNodeType.Waypoint, true, []),
    new MapNode("E", 4, 0, MapNodeType.Waypoint, true, []),
    new MapNode("F", 5, 0, MapNodeType.Intersection, true, []),
    new MapNode("G", 6, 0, MapNodeType.Station, true, [])
};
var longEdges = new[]
{
    new MapEdge("AB", "A", "B", 1, 1, false, true, ["R1"]),
    new MapEdge("BC", "B", "C", 1, 1, false, true, ["R2"]),
    new MapEdge("CD", "C", "D", 1, 1, false, true, ["R3"]),
    new MapEdge("DE", "D", "E", 1, 1, false, true, ["R4"]),
    new MapEdge("EF", "E", "F", 1, 1, false, true, ["R5"]),
    new MapEdge("FG", "F", "G", 1, 1, false, true, ["R6"])
};
var longMap = new MapDefinition("LONG", "", "RobotWorld", "m", longNodes, longEdges,
    new[] { "R1", "R2", "R3", "R4", "R5", "R6" }.Select(id =>
        new TrafficResourceDefinition(id, TrafficResourceType.SingleLane)).ToArray());
var beyondPreferredMaximum = TransportRouteWindows.Plan(
    new RoutePlan(longNodes.Select(node => node.Id).ToArray(), longEdges.Select(edge => edge.Id).ToArray(),
        ["R1", "R2", "R3", "R4", "R5", "R6"], 6),
    longMap,
    new DispatchTaskStepOptions
    {
        RollingReservationEnabled = true,
        RollingWindowMinimumEdges = 2,
        RollingWindowMaximumEdges = 3
    });
Check(beyondPreferredMaximum.Count == 2 &&
      beyondPreferredMaximum[0].Destination == "F" &&
      beyondPreferredMaximum[0].Final == false,
    "最大窗口内没有安全点时必须继续搜索到最近安全点，不能在普通路径点截断。");

var unsafeNodes = longNodes.Take(6).Select(node => node with { Type = MapNodeType.Waypoint }).ToArray();
var unsafeMap = longMap with { Nodes = unsafeNodes, Edges = longEdges.Take(5).ToArray() };
var noSafeInterior = TransportRouteWindows.Plan(
    new RoutePlan(["A", "B", "C", "D", "E", "F"], ["AB", "BC", "CD", "DE", "EF"],
        ["R1", "R2", "R3", "R4", "R5"], 5),
    unsafeMap, options);
Check(noSafeInterior.Count == 1 && noSafeInterior[0].Final,
    "道路中段没有安全点时不得停在任意普通路径点，必须保持整条路线单窗口。");

var legacy3051 = TransportRouteWindows.Plan(
    new RoutePlan(["A", "B", "C", "D", "E"], ["AB", "BC", "CD", "DE"],
        ["R1", "R2", "R3", "R4"], 4),
    new MapDefinition("TEST", "", "RobotWorld", "m", nodes, edges, resources), options,
    "GotargetCommissioning");
Check(legacy3051.Count == 1 && legacy3051[0].Final,
    "3051两点任务必须保持单窗口，不能发送中间停靠任务。");

Console.WriteLine("PASS rolling reservation windows: safe-only boundaries, dumper exit clearance, forward fallback, single-window fallback, 3051 compatibility.");

var transitOnlyMap = new MapDefinition("TRANSIT", "", "RobotWorld", "m",
    nodes.Select(n => n.Id == "C" ? n with { AllowTrafficStop = false } : n).ToArray(), edges, resources);
var transitOnlyPlan = TransportRouteWindows.Plan(
    new RoutePlan(["A", "B", "C", "D", "E"], ["AB", "BC", "CD", "DE"], [], 4), transitOnlyMap, options);
Check(transitOnlyPlan.Count == 1 && transitOnlyPlan[0].Destination == "E",
    "共用通道路口即使标为Intersection，也不能在明确禁止停车时拆窗；必须取得到下一个可停点的完整路权。");
var bypassUnsafeJunction = TransportRouteWindows.Plan(
    new RoutePlan(longNodes.Select(n => n.Id).ToArray(), longEdges.Select(e => e.Id).ToArray(), [], 6),
    longMap with { Nodes = longNodes.Select(n => n.Id == "C" ? n with { Type = MapNodeType.Intersection, AllowTrafficStop = false } : n).ToArray() }, options);
Check(bypassUnsafeJunction.Count == 2 && bypassUnsafeJunction[0].Destination == "F",
    "禁止停车的中途路口不能成为窗口终点，应继续搜索真正允许停车的后续站点。");
Console.WriteLine("PASS explicit transit-only intersections: no unsafe stop, forward search, existing-map compatibility.");
