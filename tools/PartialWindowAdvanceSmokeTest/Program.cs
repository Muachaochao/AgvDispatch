using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Service;

// B07-R8 分批路权：后续窗口在前车仅“通过”时可先推进到最远的空闲安全点。
var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL " + label);
    passed++;
    Console.WriteLine("PASS " + label);
}

// S0 -L1- N1(路口) -L2- N2(途经点) -L3- N3(路口) -L4- N4(途经点) -L5- N5(工位)
var nodes = new Dictionary<string, MapNode>(StringComparer.OrdinalIgnoreCase)
{
    ["S0"] = new("S0", 0, 0, MapNodeType.Intersection, true, ["X-S0"]),
    ["N1"] = new("N1", 1, 0, MapNodeType.Intersection, true, ["X-N1"]),
    ["N2"] = new("N2", 2, 0, MapNodeType.Waypoint, true, ["X-N2"]),
    ["N3"] = new("N3", 3, 0, MapNodeType.Intersection, true, ["X-N3"]),
    ["N4"] = new("N4", 4, 0, MapNodeType.Waypoint, true, ["X-N4"]),
    ["N5"] = new("N5", 5, 0, MapNodeType.Station, true, ["X-N5"]),
    ["P"] = new("P", 9, 9, MapNodeType.Station, true, ["X-P"]),
};
var edgeResources = new Dictionary<string, string> { ["E1"] = "L1", ["E2"] = "L2", ["E3"] = "L3", ["E4"] = "L4", ["E5"] = "L5" };
IReadOnlyList<string> ResourcesOf(AgvTransportRouteWindow window) =>
    window.Path.SelectMany(site => nodes[site].ResourceIds).Concat(window.EdgeIds.Select(edge => edgeResources[edge]))
        .Distinct().Order().ToArray();
IReadOnlyCollection<string> Parking(string site) => nodes.TryGetValue(site, out var node) ? node.ResourceIds : [];

var issued = new AgvTransportRouteWindow(0, "Q", "S0", ["Q", "S0"], ["E0"], ["X-S0"], false);
var nextPath = new[] { "S0", "N1", "N2", "N3", "N4", "N5" };
var next = new AgvTransportRouteWindow(1, "S0", "N5", nextPath, ["E1", "E2", "E3", "E4", "E5"], [], true);
next = next with { ResourceIds = ResourcesOf(next) };
var windows = new[] { issued, next };
TrafficBlockingHolder Passing(params string[] future) =>
    new("AGV-B", "TASK-B", "STEP-B", true, ["L5"], future, "P", "运输中");
var blockedL5 = new HashSet<string>(["L5"], StringComparer.OrdinalIgnoreCase);

var d = PartialWindowAdvance.Plan(windows, 1, id => nodes.GetValueOrDefault(id), ResourcesOf, blockedL5,
    [Passing("L5", "X-P")], Parking);
Check(d.Windows is { Count: 3 }, "前车只是通过远端路段：拆成 前段+剩余 两个窗口");
Check(d.Windows![1].Path.SequenceEqual(["S0", "N1", "N2", "N3"]) && d.Windows[1].Destination == "N3",
    "前段止于最远的空闲安全路口 N3（跳过不可停的途经点 N4）");
Check(d.Windows[2].Path.SequenceEqual(["N3", "N4", "N5"]) && d.Windows[2].Final && !d.Windows[1].Final,
    "剩余窗口从 N3 连续接续并保留终点动作");
Check(d.Windows.Select(w => w.Index).SequenceEqual([0, 1, 2]) && ReferenceEquals(d.Windows[0], issued) == false && d.Windows[0].Path.SequenceEqual(issued.Path),
    "窗口序号连续，已下发窗口不变");
Check(!d.Windows[1].ResourceIds.Contains("L5") && !d.Windows[1].ResourceIds.Contains("L4") && d.Windows[1].ResourceIds.Contains("X-N3"),
    "前段资源只含自身路段与停点，不含被占资源");
Check(d.Windows[2].ResourceIds.Contains("L5") && d.Windows[2].ResourceIds.Contains("X-N5"), "剩余窗口仍申请被占资源与终点");

var physicallyBlocked = PartialWindowAdvance.Plan(windows, 1, id => nodes.GetValueOrDefault(id),
    ResourcesOf, blockedL5, [Passing("L5")], Parking, occupiedEdges: new HashSet<string> { "E3" });
Check(physicallyBlocked.Windows?[1].Destination == "N1",
    "最远停点前方有车体占位时选更近的可用安全点");
Check(PartialWindowAdvance.Plan(windows, 1, id => nodes.GetValueOrDefault(id), ResourcesOf,
    blockedL5, [Passing("L5")], Parking, occupiedEdges: new HashSet<string> { "E1" }).Windows is null,
    "首段有车体占位时保持原地");
var unknown = new TrafficBlockingHolder("AGV-C", "CHARGE-1", "CHARGE-1-LOCK", false, ["L5"], [], null, "充电锁");
Check(PartialWindowAdvance.Plan(windows, 1, id => nodes.GetValueOrDefault(id), ResourcesOf, blockedL5, [unknown], Parking).Windows is null,
    "占用方未来路线不可证明（充电/人工/停车）：保持整窗等待");
Check(PartialWindowAdvance.Plan(windows, 1, id => nodes.GetValueOrDefault(id), ResourcesOf, blockedL5, null, Parking).Windows is null,
    "无法读取占用方：保持整窗等待");

var oncoming = PartialWindowAdvance.Plan(windows, 1, id => nodes.GetValueOrDefault(id), ResourcesOf, blockedL5,
    [Passing("L5", "L4", "X-N3", "L3")], Parking);
Check(oncoming.Windows is { Count: 3 } && oncoming.Windows[1].Destination == "N1",
    "对向来车后续要用 L3/N3：不推进到 N3，只推进到不冲突的 N1");
Check(PartialWindowAdvance.Plan(windows, 1, id => nodes.GetValueOrDefault(id), ResourcesOf, blockedL5,
        [Passing("L5", "L1", "X-N1", "L2", "X-N3", "L3")], Parking).Windows is null,
    "对向来车要用整段前方：不拆分");

var parking = new TrafficBlockingHolder("AGV-B", "TASK-B", "STEP-B", true, ["X-N5"], ["X-N5"], "N5", "运输中");
Check(PartialWindowAdvance.Plan(windows, 1, id => nodes.GetValueOrDefault(id), ResourcesOf,
        new HashSet<string>(["X-N5"]), [parking], Parking).Windows is null,
    "前车将停靠在被占资源（其终点）：不拆分");
Check(PartialWindowAdvance.Plan(windows, 1, id => nodes.GetValueOrDefault(id), ResourcesOf,
        new HashSet<string>(["L5", "L4"]), [Passing("L5")], Parking).Windows is null,
    "被占资源 L4 没有对应占用方：不拆分");
Check(PartialWindowAdvance.Plan(windows, 1, id => nodes.GetValueOrDefault(id), ResourcesOf,
        new HashSet<string>(), [], Parking).Windows is null, "整窗可用时不拆分");
Check(PartialWindowAdvance.Plan([next with { Index = 0 }], 0, id => nodes.GetValueOrDefault(id), ResourcesOf,
        blockedL5, [Passing("L5")], Parking).Windows is null, "首窗（未下发的第 0 窗）不由续约拆分");
var withRetained = PartialWindowAdvance.Plan(windows, 1, id => nodes.GetValueOrDefault(id), ResourcesOf, blockedL5,
    [Passing("L5")], Parking, ["BODY-Q"]);
Check(withRetained.Windows![1].ResourceIds.Contains("BODY-Q") && !withRetained.Windows[2].ResourceIds.Contains("BODY-Q"),
    "动态续接保留的车体占位并入前段，不带入剩余窗口");
var vetoed = new Dictionary<string, MapNode>(nodes, StringComparer.OrdinalIgnoreCase)
{
    ["N3"] = nodes["N3"] with { AllowTrafficStop = false }
};
var veto = PartialWindowAdvance.Plan(windows, 1, id => vetoed.GetValueOrDefault(id), ResourcesOf, blockedL5, [Passing("L5")], Parking);
Check(veto.Windows![1].Destination == "N1", "地图禁止停车的路口不作为分批停点");
var minimum = PartialWindowAdvance.Plan(windows, 1, id => nodes.GetValueOrDefault(id), ResourcesOf,
    new HashSet<string>(["L5"]), [Passing("L5", "L3", "X-N3")], Parking, null, minimumEdges: 2);
Check(minimum.Windows is null, "推进段短于最少段数（N1 只有 1 段）时不拆分");
Console.WriteLine($"PartialWindowAdvanceSmokeTest: {passed} checks passed");
