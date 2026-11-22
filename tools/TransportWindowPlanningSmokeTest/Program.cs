using AgvDispatch.Domain.Maps;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Tasks;
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

var stagingNodes = new[]
{
    new MapNode("START", 0, 0, MapNodeType.Station, true, []),
    new MapNode("PASS", 3, 0, MapNodeType.Intersection, true, []),
    new MapNode("BAY", 6, 0, MapNodeType.WaitingPoint, true, ["BAY-LOCK"]) { AllowTrafficStop = true },
    new MapNode("PICK", 10, 0, MapNodeType.Station, true, [])
};
var stagingEdges = new[]
{
    new MapEdge("E1", "START", "PASS", 3, 1, false, true, ["ENTRY"]),
    new MapEdge("E2", "PASS", "BAY", 3, 1, false, true, []),
    new MapEdge("E3", "BAY", "PICK", 4, 1, false, true, ["DOWNSTREAM"])
};
var stagingMap = new MapDefinition("STAGING", "", "RobotWorld", "m", stagingNodes, stagingEdges,
    [new("BAY-LOCK", TrafficResourceType.Station), new("ENTRY", TrafficResourceType.Intersection),
        new("DOWNSTREAM", TrafficResourceType.Intersection)]);
var stagingRoute = new RoutePlan(stagingNodes.Select(n => n.Id).ToArray(), stagingEdges.Select(e => e.Id).ToArray(),
    ["BAY-LOCK", "ENTRY", "DOWNSTREAM"], 10);
var handshake = new HandshakeExecutionOptions();
handshake.DumperExit.BodyProfiles = [new("body", "V", .49, .05, true, true)];
var staging = new GlobalPickupRoutePlanner(options, new AgvOptions { NavigationCommandMode = "SpecifiedPath" }, handshake);
var blockedDownstream = new HashSet<string> { "DOWNSTREAM" };
ActiveMapRuntimeSnapshot Activate(MapDefinition map) => new(map.Name, "v1", map, DateTimeOffset.UtcNow);
var activeStaging = Activate(stagingMap);
Check(staging.SafeHoldingSites(activeStaging, ["V"]).SequenceEqual(["BAY"]),
    "活动地图中满足容量和净空的独立等待点可自动补充为候车候选。");
Check(staging.SafeHoldingSites(activeStaging, ["UNKNOWN"]).Count == 0,
    "缺少任何车队成员核定包络时不得产生地图候车候选。");
handshake.DumperExit.BodyProfiles = [new("body", "V", 2, .05, true, true)];
Check(staging.SafeHoldingSites(activeStaging, ["V"]).Count == 0,
    "同一地图的满载尺寸变化后，应重新判断候车净空。");
handshake.DumperExit.BodyProfiles = [new("body", "V", .49, .05, true, true)];
Check(staging.SafeHoldingSites(Activate(stagingMap with
    { Edges = [stagingEdges[0], stagingEdges[1], stagingEdges[2] with { Bidirectional = true }] }), ["V"]).Count == 0,
    "重新激活的地图即使沿用编号和版本，也应根据当前几何判断。");
var partial = staging.TryStage(stagingRoute, activeStaging, blockedDownstream, ["V"]);
Check(partial is not null && partial.Windows.Count == 2 &&
      partial.Windows[0].Destination == "BAY" && !partial.Windows[0].ResourceIds.Contains("DOWNSTREAM") &&
      partial.Windows[1].ResourceIds.Contains("DOWNSTREAM") && partial.DeferredSeconds > 0,
    "后段有租约时，只申请到合格停点的首窗，保留后窗并计入等待成本。");
Check(staging.TryStage(stagingRoute, activeStaging, new HashSet<string> { "DOWNSTREAM", "ENTRY" }, ["V"]) is null,
    "首窗资源被占时不能派车。");
Check(staging.TryStage(stagingRoute, Activate(stagingMap with
    { Edges = [stagingEdges[0], stagingEdges[1], stagingEdges[2] with { Bidirectional = true }] }),
    blockedDownstream, ["V"]) is null, "双向共用路段不能成为提前发车的等待落脚点。");
Check(staging.TryStage(stagingRoute, activeStaging, blockedDownstream, ["V", "UNKNOWN"]) is null,
    "车队缺少核定车体时不得使用提前派车模式。");
Check(staging.TryStage(stagingRoute, Activate(stagingMap with
    {
        Nodes = [..stagingNodes, new("CROSS1", 6.1, -2, MapNodeType.Waypoint, true, []),
            new("CROSS2", 6.1, 2, MapNodeType.Waypoint, true, [])],
        Edges = [..stagingEdges, new("CROSSING", "CROSS1", "CROSS2", 4, 1, false, true, [])]
    }), blockedDownstream, ["V"]) is null, "停点车体覆盖其他道路时必须留在原位。");

var followup = staging.PlanFollowup(new FollowupPlanner(stagingRoute), activeStaging, "START", "PICK",
    blockedDownstream, ["V"]);
Check(followup?.Windows[0].Destination == "BAY", "已接单送箱也能在合格点分段通行。");
Check(staging.PlanFollowup(new FollowupPlanner(stagingRoute), activeStaging, "START", "PICK",
    blockedDownstream, ["V"], dumperExit: "START") is null,
    "返箱首段合并倒料出口保护后如仍有占用，禁止出区。");

var holdingNodes = new[]
{
    new MapNode("S", 0, 0, MapNodeType.Station, true, []),
    new MapNode("A", 2, 0, MapNodeType.Waypoint, true, []),
    new MapNode("HOLD", 4, 3, MapNodeType.WaitingPoint, true, ["HOLD-LOCK"]) { AllowTrafficStop = true },
    new MapNode("B", 6, 0, MapNodeType.Waypoint, true, []),
    new MapNode("C", 8, 0, MapNodeType.Waypoint, true, []),
    new MapNode("T", 10, 0, MapNodeType.Station, true, [])
};
var holdingEdges = new[]
{
    new MapEdge("SA", "S", "A", 2, 1, false, true, ["ENTRY"]),
    new MapEdge("AH", "A", "HOLD", 4, 1, false, true, []),
    new MapEdge("HB", "HOLD", "B", 4, 1, false, true, []),
    new MapEdge("AB", "A", "B", 4, 1, false, true, []),
    new MapEdge("BC", "B", "C", 2, 1, false, true, []),
    new MapEdge("CT", "C", "T", 2, 1, false, true, ["BLOCKED"])
};
var holdingMap = new MapDefinition("HOLDING", "", "RobotWorld", "m", holdingNodes, holdingEdges,
    [new("ENTRY", TrafficResourceType.Intersection), new("HOLD-LOCK", TrafficResourceType.Station),
        new("BLOCKED", TrafficResourceType.Intersection)]);
var directHoldingRoute = new RoutePlan(["S", "A", "B", "C", "T"], ["SA", "AB", "BC", "CT"],
    ["ENTRY", "BLOCKED"], 10);
var toHoldingRoute = new RoutePlan(["S", "A", "HOLD"], ["SA", "AH"], ["ENTRY"], 6);
var fromHoldingRoute = new RoutePlan(["HOLD", "B", "C", "T"], ["HB", "BC", "CT"],
    ["BLOCKED"], 8);
var holdingPlanner = new KeyedPlanner(
    new Dictionary<(string From, string To), RoutePlan>
    {
        [("S", "T")] = directHoldingRoute,
        [("S", "HOLD")] = toHoldingRoute,
        [("HOLD", "T")] = fromHoldingRoute
    });
var viaHolding = staging.TryStageViaHolding(
    holdingPlanner, Activate(holdingMap), "S", "T", new HashSet<string> { "BLOCKED" }, ["V"]);
Check(viaHolding is not null && viaHolding.Windows.Count == 2 &&
      viaHolding.Windows[0].Destination == "HOLD" &&
      viaHolding.Route.Nodes.SequenceEqual(["S", "A", "HOLD", "B", "C", "T"]) &&
      !viaHolding.Windows[0].ResourceIds.Contains("BLOCKED"),
    "普通路线没有安全停点时，应允许经核定净空的独立候车位先驶离，再等待后段释放。");

var throughHoldingNodes = holdingNodes
    .Select(node => node.Id == "HOLD" ? node with { Id = "THROUGH" } : node)
    .ToArray();
var throughHoldingEdges = new[]
{
    holdingEdges[0],
    new MapEdge("AH", "A", "THROUGH", 4, 1, false, true, []),
    new MapEdge("HB", "THROUGH", "B", 4, 1, false, true, []),
    holdingEdges[3],
    holdingEdges[4],
    holdingEdges[5]
};
var throughHoldingPlanner = new KeyedPlanner(
    new Dictionary<(string From, string To), RoutePlan>
    {
        [("S", "T")] = directHoldingRoute,
        [("S", "THROUGH")] = toHoldingRoute with { Nodes = ["S", "A", "THROUGH"] },
        [("THROUGH", "T")] = fromHoldingRoute with { Nodes = ["THROUGH", "B", "C", "T"] }
    });
Check(staging.TryStageViaHolding(
        throughHoldingPlanner,
        Activate(holdingMap with { Nodes = throughHoldingNodes, Edges = throughHoldingEdges }),
        "S", "T", new HashSet<string> { "BLOCKED" }, ["V"]) is { Windows.Count: 2 } throughStage &&
      throughStage.Windows[0].Destination == "THROUGH",
    "独立停点位于两个不同交叉点之间时，只要单入口、单出口和净空都满足，也可用于分段续行。");
Check(staging.TryStageViaHolding(
        holdingPlanner, Activate(holdingMap with
        {
            Edges = [..holdingEdges, new MapEdge("CROSS", "X1", "X2", 2, 1, false, true, [])],
            Nodes = [..holdingNodes, new("X1", 4, 1, MapNodeType.Waypoint, true, []),
                new("X2", 4, 5, MapNodeType.Waypoint, true, [])]
        }), "S", "T", new HashSet<string> { "BLOCKED" }, ["V"]) is null,
    "候车位包络覆盖交叉道路时不得成为提前停车点。");

var sent = new AgvTransportCommandPayload("TASK-W01", "V", "START", "BAY", partial!.Windows[0].Path, null)
{
    Windows = partial.Windows
};
Check(GlobalPickupRoutePlanner.IsForwardContinuation(sent,
        new RoutePlan(["BAY", "PICK"], ["E3"], [], 4), stagingMap),
    "从当前候车点继续向前的路线可以参与比较。");
Check(!GlobalPickupRoutePlanner.IsForwardContinuation(sent,
        new RoutePlan(["BAY", "START", "PICK"], ["E2", "E1"], [], 16), stagingMap),
    "动态改线不能绕回同一步骤已经驶离的起点。");

using var wakeup = new DispatchTrafficWakeup();
Check(!await wakeup.WaitAsync(0, default), "没有新提交时保留定时兜底。");
var pending = wakeup.WaitAsync(5000, default);
wakeup.Publish();
Check(await pending, "路权提交能立即唤醒调度。");
Check(await wakeup.WaitForFollowupAsync(0, default), "同一次释放也唤醒后续运输步骤。");
Parallel.For(0, 100, _ => wakeup.Publish());
Check(await wakeup.WaitAsync(0, default) && !await wakeup.WaitAsync(0, default),
    "一批并发释放合并成一次唤醒。");
Console.WriteLine("PASS staged pickup gating, maximum-load clearance, and coalesced release wakeup.");

var aliasMap = stagingMap with
{
    Nodes = stagingNodes.Select(node => node with { AgvSiteId = "ROBOT-" + node.Id }).ToArray()
};
var active = new ActiveMapRuntimeSnapshot("fixture", "v1", aliasMap, DateTimeOffset.UtcNow);
var aliasRoute = stagingRoute with { Nodes = stagingRoute.Nodes.Select(id => "ROBOT-" + id).ToArray() };
var indexed = TransportRouteWindows.Plan(aliasRoute, active, options);
var compatible = TransportRouteWindows.Plan(aliasRoute, aliasMap, options);
Check(indexed.Count == compatible.Count && indexed.Zip(compatible).All(pair =>
    pair.First.Path.SequenceEqual(pair.Second.Path) && pair.First.EdgeIds.SequenceEqual(pair.Second.EdgeIds) &&
    pair.First.ResourceIds.SequenceEqual(pair.Second.ResourceIds)), "snapshot and compatible window paths include the same site aliases and corridor guards");
var changed = new ActiveMapRuntimeSnapshot("fixture", "v2", aliasMap with
{
    Edges = [stagingEdges[0] with { ResourceIds = ["UPDATED"] }, ..stagingEdges.Skip(1)],
    Resources = [..aliasMap.Resources, new("UPDATED", TrafficResourceType.Intersection)]
}, DateTimeOffset.UtcNow);
Check(active.DefinitionHash == EngineeringRuntimeIdentity.MapHash(aliasMap) && active.DefinitionHash != changed.DefinitionHash &&
    TransportRouteWindows.Plan(aliasRoute, changed, options)[0].ResourceIds.Contains("UPDATED") &&
    !indexed[0].ResourceIds.Contains("UPDATED"), "new runtime uses its own map identity and new edge resources");
try
{
    TransportRouteWindows.Plan(aliasRoute with { EdgeIds = ["MISSING", "E2", "E3"] }, active, options);
    throw new Exception("unknown route edge should be rejected");
}
catch (InvalidOperationException error) when (error.Message.Contains("MISSING")) { }
Console.WriteLine("PASS indexed active-map windows, aliases, unknown edge rejection and map replacement.");

sealed class FollowupPlanner(RoutePlan route) : IRoutePlanner
{
    public RoutePlan? Plan(string source, string destination, IReadOnlySet<string> unavailable) =>
        route.ResourceIds.Any(unavailable.Contains) ? null : route;
}

sealed class KeyedPlanner(IReadOnlyDictionary<(string From, string To), RoutePlan> routes) : IRoutePlanner
{
    public RoutePlan? Plan(string source, string destination, IReadOnlySet<string> unavailable) =>
        routes.GetValueOrDefault((source, destination)) is { } route &&
        !route.ResourceIds.Any(unavailable.Contains) ? route : null;
}
