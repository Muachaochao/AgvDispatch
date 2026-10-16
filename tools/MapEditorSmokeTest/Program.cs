using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

public class RejectingTrafficProxy : System.Reflection.DispatchProxy
{
    public int Calls { get; private set; }
    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
    { Calls++; throw new IOException("测试后台不可用：" + method?.Name); }
}

internal static class Program
{
    private static int _checks;
    private static MapManagementViewModel? _offlinePreview;

    [STAThread]
    private static int Main(string[] args)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            CheckNodeEditing();
            CheckGeometryEditingAndDrawing();
            CheckCurveTransforms();
            CheckCurveCopies();
            CheckCurveSplittingAndReversing();
            CheckCurveControlDrag();
            CheckEndpointEditingAndCurveSelection();
            CheckInvalidGeometry();
            CheckGeometryLabelNotification();
            CheckControlDragCancellation();
            CheckSaveAndReload();
            CheckSafetySwitchCommands();
            CheckWorkbenchInteraction();
            CheckAnchoredZoomAndAlignment();
            CheckGenericMapSetup();
            CheckNewMapConnectionStates();
            CheckOfflineUpload();
            CheckUnderlayRefreshPreservesEditing();
            CheckCadDetailDisplay();
            CheckMapObjectLayers();
            CheckLayerProtectionHistoryAndGeometry();
            CheckIntersectionTopology();
            if (args.Contains("--benchmark", StringComparer.OrdinalIgnoreCase)) BenchmarkLargeMap();
            if (args.Contains("--render", StringComparer.OrdinalIgnoreCase)) RenderEditor();
            Console.WriteLine($"PASS: {_checks} map editor checks; node/edge editing, arc/Bezier rendering, undo/redo, move/rotate/mirror/drag, copies, split/reverse, control drag and JSON save/reload.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static MapManagementViewModel OpenEditor(out MemoryTrafficControlClient client)
    {
        client = new MemoryTrafficControlClient();
        var vm = new MapManagementViewModel(client) { SnapToGrid = false, SnapToExisting = false };
        vm.EnterOnlineAsync().GetAwaiter().GetResult();
        PumpUntil(() => !vm.IsBusy && vm.Nodes.Count == 4, "load isolated map");
        vm.SnapToAlignment = false;
        vm.SnapToGeometry = false;
        Require(vm.Edges.Count == 3 && vm.Resources.Count == 1, "load complete map fixture");
        Require(vm.SaveCommand.CanExecute(), "ManageMap permission enables editing");
        return vm;
    }

    private static MapDefinition CrossMap()
    {
        return new("Cross fixture", "", "LocalCartesian", "m",
            [new("A", 2, 6, MapNodeType.Waypoint, true, []), new("B", 10, 6, MapNodeType.Waypoint, true, []),
             new("C", 6, 2, MapNodeType.Waypoint, true, []), new("D", 6, 10, MapNodeType.Waypoint, true, [])],
            [new("H", "A", "B", 8, .7, false, true, ["R1"]), new("V", "C", "D", 8, 1.2, true, true, [])],
            [new("R1", TrafficResourceType.NarrowAisle)]);
    }

    private static MapManagementViewModel OpenTopologyEditor(MapDefinition definition)
    {
        var client = new MemoryTrafficControlClient(); client.SetDefinition(definition);
        var vm = new MapManagementViewModel(client);
        Complete(vm.EnterOnlineAsync());
        Require(vm.Nodes.Count == definition.Nodes.Count && vm.Edges.Count == definition.Edges.Count, "topology fixture loaded");
        return vm;
    }

    private static void CheckIntersectionTopology()
    {
        var map = CrossMap();
        MapIntersectionSplitPlan Plan(MapDefinition input) => MapIntersectionSplitter.Plan(input,
            input.Edges.Select(edge => edge.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var result = Plan(map); var joint = result.Junctions.Single();
        Require(result.SplitEdges == 2 && result.AddedNodes == 1 && result.AddedEdges == 2, "cross splits two roads sharing one junction");
        Near(joint.X, 6, "junction x"); Near(joint.Y, 6, "junction y");
        Require(joint.Type == MapNodeType.Intersection && joint.AgvSiteId is null, "junction does not invent vendor station identity");
        Require(result.Definition.Edges.Count(edge => edge.From == joint.Id || edge.To == joint.Id) == 4, "four segments connect to same node id");
        var horizontal = result.Definition.Edges.Where(edge => edge.ResourceIds.Contains("R1")).ToArray();
        Require(horizontal.Length == 2 && horizontal.All(edge => !edge.Bidirectional && edge.SpeedLimit == .7 && edge.Enabled), "split retains direction speed resource and enabled state");
        Require(horizontal[0].From == "A" && horizontal[0].To == joint.Id && horizontal[1].To == "B", "one-way chain orientation retained");
        Near(horizontal.Sum(edge => edge.Distance), 8, "split lengths conserve distance");
        Require(map.Nodes.Count == 4 && map.Edges.Count == 2, "planning does not mutate input");
        Require(map.ToGraph().FindShortestRoute("A", "D") is null && result.Definition.ToGraph().FindShortestRoute("A", "D") is not null,
            "intersection creates actual routable connection across roads");
        Require(result.Definition.ToGraph().FindShortestRoute("B", "A") is null, "splitting cannot reverse one-way road reachability");
        Require(Plan(result.Definition).SplitEdges == 0, "split is idempotent");
        var disabled = Plan(map with { Edges = map.Edges.Select(edge => edge with { Enabled = false }).ToArray() });
        Require(disabled.Junctions.All(node => !node.Enabled) && disabled.Definition.Edges.All(edge => !edge.Enabled), "disabled CAD candidates remain disabled");
        var tMap = map with { Nodes = map.Nodes.Select(node => node.Id == "C" ? node with { Y = 6 } : node).ToArray() };
        var tPlan = Plan(tMap);
        Require(tPlan.SplitEdges == 1 && tPlan.AddedNodes == 0 && tPlan.Junctions.Single().Id == "C", "T junction reuses existing endpoint without duplicates");
        var multi = map with { Nodes = map.Nodes.Concat([new MapNode("L", 4, 2, MapNodeType.Waypoint, true, []),
            new MapNode("U", 4, 10, MapNodeType.Waypoint, true, [])]).ToArray(),
            Edges = map.Edges.Append(new MapEdge("V2", "L", "U", 8, 1, false, true, [])).ToArray() };
        var multiPlan = Plan(multi);
        Require(multiPlan.SplitEdges == 3 && multiPlan.AddedNodes == 2 && multiPlan.AddedEdges == 4, "multiple crossings on one road split in parameter order");
        Near(multiPlan.Definition.Edges.Where(edge => edge.ResourceIds.Contains("R1")).Sum(edge => edge.Distance), 8, "multiple split total distance");
        var parallel = map with { Nodes = [map.Nodes[0], map.Nodes[1], map.Nodes[2] with { X = 2, Y = 7 }, map.Nodes[3] with { X = 10, Y = 7 }] };
        Require(Plan(parallel).SplitEdges == 0, "parallel roads unchanged");
        var overlap = parallel with { Nodes = parallel.Nodes.Select(node => node.Id is "C" or "D" ? node with { Y = 6 } : node).ToArray() };
        Require(Plan(overlap).SplitEdges == 0, "collinear overlap not treated as unique junction");
        var differentLayer = map with { Edges = [map.Edges[0], map.Edges[1] with { LayerId = "upper" }] };
        Require(Plan(differentLayer).SplitEdges == 0, "cross-layer crossings not connected");
        foreach (var layer in new[] { new MapEditorLayer("navigation", "Locked") { IsLocked = true },
                     new MapEditorLayer("navigation", "Hidden", false), new MapEditorLayer("navigation", "No select") { IsAllowSelect = false } })
            Require(Plan(map with { Layers = [layer] }).SplitEdges == 0, "protected layer skipped: " + layer.Name);
        var curved = map with { Edges = [map.Edges[0], map.Edges[1] with { GeometryKind = MapEdgeGeometryKind.CubicBezier, ControlPoints = [new(4, 4), new(8, 8)] }] };
        Require(Plan(curved).SkippedEdges == 1 && Plan(curved).SplitEdges == 0, "curves reported and not approximated into intersections");
        var ambiguous = map with { Nodes = map.Nodes.Concat([new MapNode("P", 6, 6, MapNodeType.Waypoint, true, []),
            new MapNode("Q", 6, 6, MapNodeType.Waypoint, true, [])]).ToArray() };
        Require(Plan(ambiguous).AmbiguousIntersections == 1 && Plan(ambiguous).SplitEdges == 0, "coincident distinct nodes require manual decision");
        var third = map with { Nodes = map.Nodes.Concat([new MapNode("SW", 2, 2, MapNodeType.Waypoint, true, []),
            new MapNode("NE", 10, 10, MapNodeType.Waypoint, true, [])]).ToArray(),
            Edges = map.Edges.Append(new MapEdge("DIAG", "SW", "NE", Math.Sqrt(128), 1, false, true, [])).ToArray() };
        Require(Plan(third).AddedNodes == 1 && Plan(third).SplitEdges == 3 && Plan(third).Junctions.Count == 1, "three roads at one junction share one node");
        var limitRejected = false;
        try { MapIntersectionSplitter.Plan(map, Enumerable.Range(0, 257).Select(i => "edge" + i).ToHashSet()); }
        catch (ArgumentException) { limitRejected = true; }
        Require(limitRejected, "intersection selection limit enforced");
        var vm = OpenTopologyEditor(map); var before = Fingerprint(vm);
        vm.UpdateBatchEdgeSelection(vm.Edges.ToArray()); Execute(vm.PreviewIntersectionsCommand);
        Require(Fingerprint(vm) == before && vm.EditHistory.Count == 0 && vm.TopologyJunctionPreview.Count == 1, "preview has no topology or history side effects");
        Execute(vm.ApplyIntersectionsCommand);
        Require(vm.Nodes.Count == 5 && vm.Edges.Count == 4 && vm.EditHistory.Count == 1, "apply creates junction in one undo step");
        Execute(vm.UndoCommand); Require(Fingerprint(vm) == before, "undo restores unsplit roads");
        Execute(vm.RedoCommand); Require(vm.Edges.Count == 4 && vm.Nodes.Count == 5, "redo restores junction");
        vm.DeactivateEditor();
        vm = OpenTopologyEditor(map); vm.UpdateBatchEdgeSelection(vm.Edges.ToArray()); Execute(vm.PreviewIntersectionsCommand);
        vm.Nodes[0].X += .1; var changed = Fingerprint(vm); Execute(vm.ApplyIntersectionsCommand);
        Require(Fingerprint(vm) == changed && vm.EditHistory.Count == 0 && vm.StatusMessage.Contains("重新预览"), "stale geometry plan rejected without mutation");
        vm.DeactivateEditor();
        vm = OpenTopologyEditor(tMap); vm.UpdateBatchEdgeSelection(vm.Edges.ToArray()); Execute(vm.PreviewIntersectionsCommand);
        Require(vm.TopologyJunctionPreview.Single().NodeId == "C", "preview lists reused T endpoint");
        vm.MapLayers.First().IsLocked = true; var locked = Fingerprint(vm); Execute(vm.ApplyIntersectionsCommand);
        Require(Fingerprint(vm) == locked && vm.Edges.Count == 2, "lock change invalidates old plan"); vm.DeactivateEditor();

        var gridNodes = new[] { new MapNode("P1", 2, 2, MapNodeType.Station, true, []), new MapNode("P2", 5, 2.005, MapNodeType.Station, true, []),
            new MapNode("P3", 8, 2, MapNodeType.Station, true, []), new MapNode("P4", 2, 8, MapNodeType.Station, true, []),
            new MapNode("P5", 5, 8, MapNodeType.Station, false, []), new MapNode("P6", 8, 8, MapNodeType.Station, true, []) };
        var grid = map with { Nodes = gridNodes, Edges = [], Resources = [] };
        vm = OpenTopologyEditor(grid); vm.UpdateBatchNodeSelection(vm.Nodes.ToArray());
        vm.DrawEdgeBidirectional = false; vm.DrawEdgeSpeedLimit = .6; vm.DrawEdgeGeometryKind = MapEdgeGeometryKind.CubicBezier;
        var gridBefore = Fingerprint(vm); Execute(vm.ConnectHorizontalCommand);
        Require(vm.Edges.Count == 4 && vm.Edges.All(edge => edge.GeometryKind == MapEdgeGeometryKind.Line && edge.SpeedLimit == .6 && !edge.Bidirectional), "row grouping uses straight adjacent connections and toolbar speed direction");
        Require(vm.Edges.Any(edge => edge.From == "P1" && edge.To == "P2") && !vm.Edges.Any(edge => edge.From == "P1" && edge.To == "P3"), "only adjacent row nodes connect");
        Require(vm.Edges.Count(edge => !edge.Enabled) == 2, "disabled endpoint keeps connection disabled");
        var history = vm.EditHistory.Count; Execute(vm.ConnectHorizontalCommand);
        Require(vm.Edges.Count == 4 && vm.EditHistory.Count == history, "repeated connect creates no duplicates or phantom undo");
        Execute(vm.UndoCommand); Require(Fingerprint(vm) == gridBefore, "axis connections undo as one operation");
        vm.UpdateBatchNodeSelection(vm.Nodes.ToArray()); Execute(vm.ConnectVerticalCommand);
        Require(vm.Edges.Count == 3 && vm.Edges.Any(edge => edge.From == "P1" && edge.To == "P4"), "column grouping connects bottom to top");
        Execute(vm.UndoCommand); vm.UpdateBatchNodeSelection(vm.Nodes.ToArray()); vm.AxisConnectionTolerance = double.NaN;
        Execute(vm.ConnectHorizontalCommand); Require(vm.Edges.Count == 0 && vm.EditHistory.Count == 1 && vm.EditHistory.Single().State == "已撤销", "invalid tolerance rejected without new undo");
        vm.DeactivateEditor();
        vm = OpenTopologyEditor(grid with { Layers = [new("navigation", "Locked") { IsLocked = true }] });
        vm.UpdateBatchNodeSelection(vm.Nodes.ToArray()); Execute(vm.ConnectHorizontalCommand);
        Require(vm.Edges.Count == 0 && vm.EditHistory.Count == 0, "axis connection excludes locked nodes without phantom undo"); vm.DeactivateEditor();
        vm = OpenTopologyEditor(grid with { Nodes = grid.Nodes.Select(node => node.Id == "P2" ? node with { LayerId = "upper" } : node).ToArray() });
        vm.UpdateBatchNodeSelection(vm.Nodes.Take(3).ToArray()); Execute(vm.ConnectHorizontalCommand);
        Require(vm.Edges.Count == 1 && vm.Edges.Single().From == "P1" && vm.Edges.Single().To == "P3", "axis groups do not connect across layers"); vm.DeactivateEditor();
    }

    private static void CheckNodeEditing()
    {
        var vm = OpenEditor(out _);
        var original = Fingerprint(vm);
        vm.NewNodeX = 25.125; vm.NewNodeY = 15.75; vm.DrawNodeType = MapNodeType.WaitingPoint;
        Execute(vm.AddPreciseNodeCommand);
        Require(vm.Nodes.Count == 5 && vm.SelectedNode?.Type == MapNodeType.WaitingPoint, "precise point creation");
        Near(vm.SelectedNode!.X, 25.125, "precise point X");
        Near(vm.SelectedNode.Y, 15.75, "precise point Y");
        var added = Fingerprint(vm);
        CheckUndoRedo(vm, original, added, "point creation");
        Execute(vm.UndoCommand);

        vm.BeginGridEdit();
        Node(vm, "A").Id = "A-RENAMED";
        vm.CompleteGridEdit(false);
        Require(Edge(vm, "ARC").From == "A-RENAMED", "node rename updates curved edge references");
        CheckUndoRedo(vm, original, Fingerprint(vm), "node rename");
        Execute(vm.UndoCommand);

        vm.SelectedNode = Node(vm, "B");
        Execute(vm.RemoveNodeCommand);
        Require(vm.Nodes.Count == 3 && vm.Edges.All(edge => edge.From != "B" && edge.To != "B"), "remove point and incident edges");
        Execute(vm.UndoCommand);
        Require(Fingerprint(vm) == original, "point removal undo restores curve data");
    }

    private static void CheckGeometryEditingAndDrawing()
    {
        var vm = OpenEditor(out _);
        Require(vm.PreviewEdges.Single(edge => edge.Id == "ARC").Points.Count > 2, "arc has sampled preview points");
        Require(vm.PreviewEdges.Single(edge => edge.Id == "BEZIER").Points.Count > 2, "Bezier has sampled preview points");
        Require(vm.PreviewEdges.All(edge => edge.Geometry is not null), "every edge has path geometry");
        var arcPreview = vm.PreviewEdges.Single(edge => edge.Id == "ARC");
        Require(arcPreview.Points.Any(point => point.Y < arcPreview.Y1 - 10), "arc preview follows bend above chord");

        foreach (var kind in new[] { MapEdgeGeometryKind.CircularArc, MapEdgeGeometryKind.CubicBezier, MapEdgeGeometryKind.Line })
        {
            vm.SelectedEdge = Edge(vm, "LINK");
            vm.DrawEdgeGeometryKind = kind;
            var before = Fingerprint(vm);
            Execute(vm.ApplyEdgeGeometryCommand);
            var edge = Edge(vm, "LINK");
            Require(edge.GeometryKind == kind && edge.ParseControlPoints().Count == ControlCount(kind), $"convert edge to {kind}");
            CheckUndoRedo(vm, before, Fingerprint(vm), $"convert edge to {kind}");
        }

        var bezier = Edge(vm, "BEZIER");
        var original = Fingerprint(vm);
        vm.BeginGridEdit();
        bezier.ControlPointsText = "12,7;20,7";
        vm.CompleteGridEdit(false);
        CheckUndoRedo(vm, original, Fingerprint(vm), "control point property edit");

        vm.NewNodeX = 25; vm.NewNodeY = 10;
        Execute(vm.AddPreciseNodeCommand);
        vm.NewNodeX = 30; vm.NewNodeY = 10;
        Execute(vm.AddPreciseNodeCommand);
        vm.DrawEdgeGeometryKind = MapEdgeGeometryKind.CubicBezier;
        vm.DrawEdgeBidirectional = false; vm.DrawEdgeSpeedLimit = 0.75;
        var beforeDraw = Fingerprint(vm);
        Execute(vm.AddEdgeCommand);
        Require(vm.SelectedEdge is { GeometryKind: MapEdgeGeometryKind.CubicBezier, Bidirectional: false } &&
                vm.SelectedEdge.ParseControlPoints().Count == 2, "new edge uses selected curve and direction");
        Near(vm.SelectedEdge!.SpeedLimit, 0.75, "new curve uses drawing speed");
        CheckUndoRedo(vm, beforeDraw, Fingerprint(vm), "draw curve");
    }

    private static void CheckCurveTransforms()
    {
        var vm = OpenEditor(out _);
        var original = Fingerprint(vm);
        SelectAll(vm);
        vm.BatchDeltaX = 3; vm.BatchDeltaY = 2;
        Execute(vm.MoveSelectedNodesCommand);
        AssertControls(vm, "ARC", [new(9, 8)], "move arc");
        AssertControls(vm, "BEZIER", [new(15, 8), new(23, 8)], "move Bezier");
        CheckUndoRedo(vm, original, Fingerprint(vm), "move whole map");
        Execute(vm.UndoCommand);

        SelectAll(vm);
        Execute(vm.RotateCounterClockwiseCommand);
        AssertControls(vm, "ARC", [new(7, -3)], "rotate arc");
        AssertControls(vm, "BEZIER", [new(7, 3), new(7, 11)], "rotate Bezier");
        CheckUndoRedo(vm, original, Fingerprint(vm), "rotate whole map");
        Execute(vm.UndoCommand);

        SelectAll(vm);
        Execute(vm.MirrorVerticalCommand);
        AssertControls(vm, "ARC", [new(16, 6)], "mirror arc");
        AssertControls(vm, "BEZIER", [new(10, 6), new(2, 6)], "mirror Bezier");
        CheckUndoRedo(vm, original, Fingerprint(vm), "mirror whole map");
        Execute(vm.UndoCommand);

        vm.IsCanvasEditMode = true;
        Require(vm.BeginNodeDrag(vm.Nodes.ToArray(), 180, 400, 900, 520), "begin group drag");
        vm.DragSelectedNodes(216, 400 - 520.0 / 30, 900, 520);
        vm.DragSelectedNodes(252, 400 - 520.0 / 15, 900, 520);
        vm.EndNodeDrag();
        AssertControls(vm, "ARC", [new(10, 8)], "continuous drag arc");
        AssertControls(vm, "BEZIER", [new(16, 8), new(24, 8)], "continuous drag Bezier");
        Near(Node(vm, "A").X, 6, "continuous drag uses original positions");
        Near(Node(vm, "A").Y, 4, "continuous drag Y");
        CheckUndoRedo(vm, original, Fingerprint(vm), "group drag is one undo step");
    }

    private static void CheckCurveCopies()
    {
        var vm = OpenEditor(out _);
        var original = Fingerprint(vm);
        SelectAll(vm);
        vm.BatchDeltaX = 4; vm.BatchDeltaY = 3;
        Execute(vm.DuplicateSelectedNodesCommand);
        Require(vm.Nodes.Count == 8 && vm.Edges.Count == 6, "duplicate points and internal curves");
        AssertCopiedCurves(vm, 4, 3, "duplicate");
        Require(vm.Nodes.Where(node => node.Id.StartsWith('N')).All(node => node.AgvSiteId.Length == 0), "duplicate clears vehicle site mappings");
        CheckUndoRedo(vm, original, Fingerprint(vm), "duplicate curves");
        Execute(vm.UndoCommand);

        SelectAll(vm);
        Execute(vm.CopySelectionCommand);
        Execute(vm.PasteSelectionCommand);
        Require(vm.Nodes.Count == 8 && vm.Edges.Count == 6, "paste points and internal curves");
        AssertCopiedCurves(vm, 1, 1, "paste");
        CheckUndoRedo(vm, original, Fingerprint(vm), "paste curves");
    }

    private static void CheckCurveSplittingAndReversing()
    {
        foreach (var id in new[] { "ARC", "BEZIER" })
        {
            var vm = OpenEditor(out _);
            var original = Fingerprint(vm);
            var edge = Edge(vm, id);
            var originalDomain = edge.ToDomain(vm.Nodes);
            var from = Node(vm, edge.From).ToDomain();
            var to = Node(vm, edge.To).ToDomain();
            vm.SelectedEdge = edge;
            Execute(vm.SplitSelectedEdgeCommand);
            Require(vm.Nodes.Count == 5 && vm.Edges.Count == 4 && vm.Edges.All(item => item.Id != id), $"split {id} topology");
            var middle = vm.SelectedNode!;
            Near(middle.X, id == "ARC" ? 6 : 16, $"split {id} midpoint X");
            Near(middle.Y, id == "ARC" ? 6 : 5, $"split {id} midpoint Y follows curve");
            var first = vm.Edges.Single(item => item.From == from.Id && item.To == middle.Id).ToDomain(vm.Nodes);
            var second = vm.Edges.Single(item => item.From == middle.Id && item.To == to.Id).ToDomain(vm.Nodes);
            Require(first.GeometryKind == originalDomain.GeometryKind && second.GeometryKind == originalDomain.GeometryKind,
                $"split {id} retains geometry type");
            foreach (var t in new[] { 0.1, 0.25, 0.5, 0.75, 0.9 })
            {
                var expected = MapEdgeGeometry.PointAt(originalDomain, from, to, t);
                var actual = t <= 0.5
                    ? MapEdgeGeometry.PointAt(first, from, middle.ToDomain(), t * 2)
                    : MapEdgeGeometry.PointAt(second, middle.ToDomain(), to, (t - 0.5) * 2);
                Near(actual.X, expected.X, $"split {id} preserves point X at {t}");
                Near(actual.Y, expected.Y, $"split {id} preserves point Y at {t}");
            }
            Require(first.SpeedLimit == originalDomain.SpeedLimit && first.Bidirectional == originalDomain.Bidirectional &&
                    first.ResourceIds.SequenceEqual(originalDomain.ResourceIds), $"split {id} retains traffic properties");
            Near(first.Distance + second.Distance, originalDomain.Distance, $"split {id} length", 0.002);
            CheckUndoRedo(vm, original, Fingerprint(vm), $"split {id}");
            Execute(vm.UndoCommand);

            vm.SelectedEdge = Edge(vm, id);
            vm.SelectedEdge.Bidirectional = false;
            var beforeReverse = Fingerprint(vm);
            Execute(vm.ReverseSelectedEdgeCommand);
            var reversed = Edge(vm, id).ToDomain(vm.Nodes);
            Require(reversed.From == to.Id && reversed.To == from.Id, $"reverse {id} endpoints");
            foreach (var t in new[] { 0.1, 0.4, 0.9 })
            {
                var expected = MapEdgeGeometry.PointAt(originalDomain, from, to, 1 - t);
                var actual = MapEdgeGeometry.PointAt(reversed, to, from, t);
                Near(actual.X, expected.X, $"reverse {id} preserves curve X at {t}");
                Near(actual.Y, expected.Y, $"reverse {id} preserves curve Y at {t}");
            }
            CheckUndoRedo(vm, beforeReverse, Fingerprint(vm), $"reverse {id}");
        }
    }

    private static void CheckCurveControlDrag()
    {
        var vm = OpenEditor(out _);
        var original = Fingerprint(vm);
        vm.SelectedEdge = Edge(vm, "BEZIER");
        vm.IsCurveControlMode = true;
        Require(vm.BeginCurveControlDrag(12.0 / 50 * 900, (1 - 6.0 / 30) * 520), "hit curve control handle");
        vm.DragCurveControl(13.0 / 50 * 900, (1 - 7.0 / 30) * 520, 900, 520);
        vm.DragCurveControl(14.0 / 50 * 900, (1 - 8.0 / 30) * 520, 900, 520);
        vm.EndCurveControlDrag();
        AssertControls(vm, "BEZIER", [new(14, 8), new(20, 6)], "drag first Bezier control");
        CheckUndoRedo(vm, original, Fingerprint(vm), "control drag is one undo step");
    }

    private static void CheckSaveAndReload()
    {
        var vm = OpenEditor(out var client);
        SelectAll(vm);
        vm.BatchDeltaX = 2; vm.BatchDeltaY = 1;
        Execute(vm.MoveSelectedNodesCommand);
        var before = Fingerprint(vm);
        Execute(vm.SaveCommand);
        PumpUntil(() => !vm.IsBusy, "save isolated draft");
        Require(client.SaveCount == 1, "save reaches client once");
        Require(Fingerprint(vm) == before, "saved workspace reapplies complete curve definition");
        Require(client.LastSavedRequest is { ExpectedEditToken: "fixture-1" } &&
                client.LastSavedRequest.Definition.Edges.Count(edge => edge.GeometryKind != MapEdgeGeometryKind.Line) == 2,
            "save request includes concurrency token and both curve types");
        Execute(vm.RefreshCommand);
        PumpUntil(() => !vm.IsBusy, "reload isolated draft");
        Require(Fingerprint(vm) == before && !vm.UndoCommand.CanExecute(), "JSON saved draft reloads and resets editor history");
        AssertControls(vm, "ARC", [new(8, 7)], "save/reload arc controls");
        AssertControls(vm, "BEZIER", [new(14, 7), new(22, 7)], "save/reload Bezier controls");
    }

    private static void CheckEndpointEditingAndCurveSelection()
    {
        var vm = OpenEditor(out _);
        var original = Fingerprint(vm);
        vm.BeginGridEdit();
        Node(vm, "C").X = 14;
        Node(vm, "C").Y = 3;
        vm.CompleteGridEdit(false);
        AssertControls(vm, "BEZIER", [new(14, 7), new(20, 6)], "moving Bezier start preserves endpoint tangent");
        CheckUndoRedo(vm, original, Fingerprint(vm), "single Bezier endpoint edit");
        Execute(vm.UndoCommand);

        vm.BeginGridEdit();
        Node(vm, "A").X = 4;
        Node(vm, "A").Y = 4;
        vm.CompleteGridEdit(false);
        AssertControls(vm, "ARC", [new(7, 7)], "moving arc start moves arc point by half endpoint delta");
        CheckUndoRedo(vm, original, Fingerprint(vm), "single arc endpoint edit");
        Execute(vm.UndoCommand);

        foreach (var location in new[] { (Id: "ARC", X: 6.0, Y: 6.0), (Id: "BEZIER", X: 16.0, Y: 5.0) })
        {
            var x = location.X / 50 * 900;
            var y = (1 - location.Y / 30) * 520;
            Require(vm.FindEdgeAtCanvas(x, y)?.Id == location.Id, "click selects actual " + location.Id + " bend");
            Require(vm.FindEdgesInCanvasRectangle(x - 2, y - 2, x + 2, y + 2).Single().Id == location.Id,
                "rectangle selects actual " + location.Id + " bend");
        }
        Require(vm.FindEdgeAtCanvas(16.0 / 50 * 900, (1 - 2.0 / 30) * 520, 2) is null,
            "Bezier chord away from rendered curve is not clickable");
        vm.SelectedResource = vm.Resources.Single();
        vm.IsResourcePaintMode = true;
        vm.HandleMapClick(16.0 / 50 * 900, (1 - 5.0 / 30) * 520, 900, 520);
        Require(Edge(vm, "BEZIER").ResourceIds == "R1", "resource brush binds actual curve bend");
        CheckUndoRedo(vm, original, Fingerprint(vm), "curve resource brush");
    }

    private static void CheckInvalidGeometry()
    {
        foreach (var input in new[]
                 {
                     (Id: "BEZIER", Controls: "12,typo;20,6"),
                     (Id: "BEZIER", Controls: "12,6"),
                     (Id: "BEZIER", Controls: "NaN,6;20,6"),
                     (Id: "ARC", Controls: "6,2")
                 })
        {
            var vm = OpenEditor(out var client);
            var original = Fingerprint(vm);
            vm.SelectedEdge = Edge(vm, input.Id);
            vm.BeginGridEdit();
            vm.SelectedEdge.ControlPointsText = input.Controls;
            vm.CompleteGridEdit(false);
            Require(vm.EditorHasErrors, "invalid " + input.Id + " control input is a structural error");
            Require(vm.PreviewEdges.All(edge => edge.Id != input.Id), "invalid curve is excluded from preview");
            Execute(vm.SaveCommand);
            Require(client.SaveCount == 0 && !vm.IsBusy, "invalid control geometry never reaches saved draft");
            Execute(vm.UndoCommand);
            Require(!vm.EditorHasErrors && Fingerprint(vm) == original, "undo repairs invalid curve input");
        }
    }

    private static void CheckGeometryLabelNotification()
    {
        var edge = new EditableMapEdge();
        var properties = new List<string?>();
        edge.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        edge.GeometryKind = MapEdgeGeometryKind.CircularArc;
        Require(edge.GeometryLabel == "圆弧" && properties.Contains(nameof(EditableMapEdge.GeometryLabel)),
            "geometry kind change notifies bound geometry label");
        properties.Clear();
        edge.GeometryKind = MapEdgeGeometryKind.CubicBezier;
        Require(edge.GeometryLabel == "三次贝塞尔" && properties.Contains(nameof(EditableMapEdge.GeometryLabel)),
            "Bezier kind change notifies bound geometry label");
    }

    private static void CheckControlDragCancellation()
    {
        var vm = OpenEditor(out _);
        var original = Fingerprint(vm);
        vm.SelectedEdge = Edge(vm, "BEZIER");
        vm.IsCurveControlMode = true;
        Require(vm.BeginCurveControlDrag(12.0 / 50 * 900, (1 - 6.0 / 30) * 520), "start control drag before undo");
        vm.DragCurveControl(14.0 / 50 * 900, (1 - 8.0 / 30) * 520, 900, 520);
        Execute(vm.UndoCommand);
        vm.DragCurveControl(15.0 / 50 * 900, (1 - 9.0 / 30) * 520, 900, 520);
        Require(Fingerprint(vm) == original && !vm.UndoCommand.CanExecute(),
            "undo cancels active drag without stale edge mutation or phantom history");

        vm.SelectedEdge = Edge(vm, "BEZIER");
        Require(vm.BeginCurveControlDrag(12.0 / 50 * 900, (1 - 6.0 / 30) * 520), "start control drag before mode change");
        vm.IsCurveControlMode = false;
        vm.DragCurveControl(15.0 / 50 * 900, (1 - 9.0 / 30) * 520, 900, 520);
        Require(Fingerprint(vm) == original && !vm.UndoCommand.CanExecute(), "leaving curve mode cancels active control drag");

        vm.IsCurveControlMode = true;
        var detachedEdge = Edge(vm, "BEZIER");
        var detachedControls = detachedEdge.ControlPointsText;
        Require(vm.BeginCurveControlDrag(12.0 / 50 * 900, (1 - 6.0 / 30) * 520), "start control drag before edge removal");
        vm.Edges.Remove(detachedEdge);
        vm.DragCurveControl(15.0 / 50 * 900, (1 - 9.0 / 30) * 520, 900, 520);
        Require(detachedEdge.ControlPointsText == detachedControls && !vm.UndoCommand.CanExecute(),
            "removed edge cannot be dragged or create phantom history");
        vm.EndCurveControlDrag();
    }

    private static void CheckSafetySwitchCommands()
    {
        var vm = OpenEditor(out var client);
        client.PreparePublished();
        Execute(vm.RefreshCommand);
        Require(vm.BeginMapSwitchCommand.CanExecute(), "published version exposes safe drain action");
        Require(!vm.ActivateCommand.CanExecute(), "activation disabled before beginning safe switch");
        Execute(vm.BeginMapSwitchCommand);
        Require(vm.MapSwitchSummary.Contains("收尾"), "UI displays draining phase");
        vm.ActivationConfirmation = vm.ExpectedActivationText;
        vm.SiteSafetyConfirmed = true;
        vm.VehicleMapCompatibilityConfirmed = true;
        Require(!vm.ActivateCommand.CanExecute(), "activation requires recorded confirmation evidence");
        vm.MapSwitchEvidence = "现场与车辆已核对";
        Require(vm.ActivateCommand.CanExecute(), "matching version and confirmation enable activation");
        Execute(vm.ActivateCommand);
        Require(client.LastActivation is { SiteSafetyConfirmed: true, VehicleMapCompatibilityConfirmed: true }, "activation sends physical confirmation fields");
        Require(!vm.SiteSafetyConfirmed && !vm.VehicleMapCompatibilityConfirmed && vm.MapSwitchEvidence == "", "activation clears confirmations for fresh release check");
        Require(vm.MapSwitchSummary.Contains("暂停"), "activation retains held state in UI");
        vm.SiteSafetyConfirmed = true; vm.VehicleMapCompatibilityConfirmed = true; vm.MapSwitchEvidence = "激活后重新核对";
        Execute(vm.ResumeMapSwitchCommand);
        Require(vm.MapSwitchSummary.Contains("正常派单"), "explicit resume returns UI to normal dispatch");
        vm.DeactivateEditor();
    }

    private static void CheckAnchoredZoomAndAlignment()
    {
        var vm = OpenEditor(out var client);
        var geometry = Fingerprint(vm);
        vm.PanCanvas(37, -23);
        const double x = 710, y = 130;
        double ScreenX() => 450 + (x - 450) * vm.CanvasZoom + vm.CanvasPanX;
        double ScreenY() => 260 + (y - 260) * vm.CanvasZoom + vm.CanvasPanY;
        var beforeX = ScreenX(); var beforeY = ScreenY();
        vm.AdjustCanvasZoom(120, x, y, 900, 520);
        Near(ScreenX(), beforeX, "zoom preserves pointer X after pan");
        Near(ScreenY(), beforeY, "zoom preserves pointer Y after pan");
        for (var i = 0; i < 40; i++) vm.AdjustCanvasZoom(120, x, y, 900, 520);
        Near(vm.CanvasZoom, 5, "zoom upper limit");
        Near(ScreenX(), beforeX, "zoom upper clamp preserves anchor");
        for (var i = 0; i < 80; i++) vm.AdjustCanvasZoom(-120, x, y, 900, 520);
        Near(vm.CanvasZoom, .5, "zoom lower limit");
        Near(ScreenY(), beforeY, "zoom lower clamp preserves anchor");
        vm.AdjustCanvasZoom(120, double.NaN, y, 900, 520);
        Near(vm.CanvasZoom, .5, "invalid zoom anchor ignored");
        Require(Fingerprint(vm) == geometry, "view changes preserve navigation geometry");

        vm.SnapToAlignment = true; vm.SnapToGrid = false; vm.SnapToExisting = false;
        vm.SnapDistance = .3;
        vm.SelectWorkbenchTool("point");
        var a = Node(vm, "A");
        // Fixture has no CAD frame: world (X,Y) maps to (X/50*900, (1-Y/30)*520).
        var targetX = a.X + .1; var targetY = a.Y + 4;
        var cx = targetX / vm.CanvasWorldWidth * 900;
        var cy = (1 - targetY / vm.CanvasWorldHeight) * 520;
        vm.UpdateWorkbenchPointer(cx, cy, 900, 520);
        Require(vm.PreviewSnapGuides.Count >= 3, "alignment preview and landing marker shown");
        vm.HandleMapClick(cx, cy, 900, 520);
        Near(vm.SelectedNode!.X, a.X, "placement uses same aligned X as preview");
        var aligned = Fingerprint(vm);
        Execute(vm.UndoCommand); Require(Fingerprint(vm) == geometry, "aligned placement undo restores map");
        Execute(vm.RedoCommand); Require(Fingerprint(vm) == aligned, "aligned placement redo restores map");
        vm.CancelWorkbenchTool(); Require(vm.PreviewSnapGuides.Count == 0, "cancel clears alignment guides");
        vm.SnapToAlignment = false;
        Execute(vm.SaveCommand);
        PumpUntil(() => !vm.IsBusy && client.SaveCount == 1, "alignment preference saves");
        vm.SnapToAlignment = true;
        Execute(vm.RefreshCommand);
        PumpUntil(() => !vm.IsBusy, "alignment preference reloads");
        Require(!vm.SnapToAlignment, "saved disabled alignment survives reload");

        var hiddenVm = OpenEditor(out _);
        hiddenVm.SnapToAlignment = true;
        hiddenVm.SelectWorkbenchTool("point");
        hiddenVm.MapLayers.Single().Visible = false;
        Execute(hiddenVm.AddMapLayerCommand);
        hiddenVm.IsMapPointMode = true;
        hiddenVm.HandleMapClick(cx, cy, 900, 520);
        Near(hiddenVm.SelectedNode!.X, targetX, "hidden layer cannot attract placement");

        var dragVm = OpenEditor(out _);
        dragVm.SnapToAlignment = true; dragVm.SnapDistance = .3;
        var movingA = Node(dragVm, "A"); var movingB = Node(dragVm, "B");
        var dx = movingB.X - movingA.X; var dy = movingB.Y - movingA.Y;
        Require(dragVm.BeginNodeDrag([movingA, movingB], 36, (1 - 2.0 / 30) * 520, 900, 520), "begin group alignment drag");
        dragVm.DragSelectedNodes(11.9 / 50 * 900, (1 - 6.0 / 30) * 520, 900, 520);
        Near(movingA.X, 12, "group anchor aligns with unselected C");
        Near(movingA.Y, 6, "dragged selection does not snap to itself");
        Near(movingB.X - movingA.X, dx, "group alignment preserves relative X");
        Near(movingB.Y - movingA.Y, dy, "group alignment preserves relative Y");
        dragVm.EndNodeDrag(); Require(dragVm.PreviewSnapGuides.Count == 0, "end drag clears guides");
        Execute(dragVm.UndoCommand);
        Near(Node(dragVm, "A").X, 2, "single undo restores complete aligned drag");
    }

    private static void CheckWorkbenchInteraction()
    {
        var vm = OpenEditor(out _);
        var original = Fingerprint(vm);
        foreach (var tool in vm.WorkbenchTools)
        {
            vm.SelectWorkbenchTool(tool.Key);
            Require(vm.WorkbenchTools.Count(item => item.Active) == 1 && tool.Active, tool.Key + " selects exactly one tool");
            vm.CancelWorkbenchTool();
            Require(vm.IsCanvasEditMode && !vm.IsPanMode && vm.WorkbenchTools.Single(item => item.Active).Key == "select", tool.Key + " cancels to selection");
            Require(Fingerprint(vm) == original, tool.Key + " selection/cancellation preserves map");
        }
        vm.SnapToExisting = true;
        var a = vm.PreviewNodes.Single(node => node.Id == "A");
        var b = vm.PreviewNodes.Single(node => node.Id == "B");
        vm.SelectWorkbenchTool("line");
        vm.HandleMapClick(a.Left + 7, a.Top + 7, 900, 520);
        vm.UpdateWorkbenchPointer(b.Left + 7, b.Top + 7, 900, 520);
        Require(vm.PreviewToolGuides.Count == 1, "pending road displays endpoint guide");
        vm.CancelWorkbenchTool();
        Require(vm.PreviewToolGuides.Count == 0, "cancel removes pending guide");
        vm.SelectWorkbenchTool("line");
        vm.HandleMapClick(b.Left + 7, b.Top + 7, 900, 520);
        Require(vm.Edges.Count == 3 && Fingerprint(vm) == original, "new road does not reuse cancelled start");
        vm.CancelWorkbenchTool();
        vm.ShowNodeLayer = false;
        Require(vm.FindNodeAtCanvas(a.Left + 7, a.Top + 7) is null && vm.FindNodesInCanvasRectangle(0, 0, 900, 520).Count == 0,
            "hidden nodes cannot be selected by click or rectangle");
        vm.ShowEdgeLayer = false;
        Require(vm.FindEdgesInCanvasRectangle(0, 0, 900, 520).Count == 0 && Fingerprint(vm) == original, "layer hiding preserves geometry and excludes selection");
        vm.SelectWorkbenchTool("bezier");
        Require(vm.ShowNodeLayer && vm.ShowEdgeLayer && vm.DrawEdgeGeometryKind == MapEdgeGeometryKind.CubicBezier, "curve tool reveals required layers");
        foreach (var (key, type) in new[] { ("station", MapNodeType.Station), ("waiting", MapNodeType.WaitingPoint), ("charging", MapNodeType.ChargingPoint), ("intersection", MapNodeType.Intersection) })
        {
            vm.SelectWorkbenchTool(key);
            vm.HandleMapClick(700, 350, 900, 520);
            Require(vm.SelectedNode?.Type == type && vm.Nodes.Count == 5, key + " template creates actual typed node");
            vm.CancelWorkbenchTool();
            Execute(vm.UndoCommand);
            Require(Fingerprint(vm) == original, key + " template can be undone");
        }
        vm.DeactivateEditor();
    }

    private static void CheckUnderlayRefreshPreservesEditing()
    {
        var vm = OpenEditor(out var client);
        client.SetUnderlayState(CadUnderlayJobStatus.Pending);
        Execute(vm.RefreshCommand);
        vm.SelectWorkbenchTool("point"); vm.HandleMapClick(700, 400, 900, 520);
        var unsaved = Fingerprint(vm);
        vm.SelectWorkbenchTool("line"); vm.SnapToExisting = true;
        var a = vm.PreviewNodes.Single(node => node.Id == "A");
        vm.HandleMapClick(a.Left + 7, a.Top + 7, 900, 520);
        client.SetUnderlayState(CadUnderlayJobStatus.Ready);
        vm.RefreshUnderlayStateAsync().GetAwaiter().GetResult();
        Require(vm.Nodes.Count == 5 && Fingerprint(vm) == unsaved && vm.UndoCommand.CanExecute(),
            "CAD completion refresh preserves unsaved geometry and undo history");
        vm.UpdateWorkbenchPointer(500, 300, 900, 520);
        Require(vm.IsMapLineMode && vm.PreviewToolGuides.Count == 1, "CAD completion preserves drawing tool and pending road start");
        vm.DeactivateEditor();
        var overview = new MapOverviewViewModel(client);
        Execute(overview.RefreshCommand);
        Require(overview.PreviewNodes.Count == 4 && overview.PreviewTitle.Contains("草稿"), "overview defaults to saved draft");
    }

    private static void CheckLayerProtectionHistoryAndGeometry()
    {
        var vm = OpenEditor(out var client);
        var layer = vm.MapLayers.Single();
        layer.Alpha = .4; layer.Color = "#123456"; layer.StrokeWidth = 4;
        Require(vm.PreviewNodes.All(node => node.Opacity == .4 && node.Fill == "#123456"), "layer node color/opacity applied");
        Require(vm.PreviewEdges.All(edge => edge.Opacity == .4 && edge.Thickness == 4 && edge.Stroke == "#123456"), "layer edge color/width/opacity applied");
        layer.IsAllowSelect = false;
        Require(vm.FindNodeAtCanvas(36, (1 - 2.0 / 30) * 520) is null && vm.FindEdgesInCanvasRectangle(0, 0, 900, 520).Count == 0, "unselectable layer stays visible but cannot be picked");
        layer.IsAllowSelect = true; layer.IsLocked = true;
        var locked = Fingerprint(vm); var historyCount = vm.EditHistory.Count;
        vm.SelectedNode = Node(vm, "A"); Execute(vm.RemoveNodeCommand);
        Require(Fingerprint(vm) == locked && vm.EditHistory.Count == historyCount, "locked deletion rolls back without phantom history");
        Require(!vm.BeginNodeDrag([Node(vm, "A")], 36, 485, 900, 520), "locked nodes refuse drag");
        vm.BeginGridEdit(); Node(vm, "A").X = 9; vm.CompleteGridEdit(false);
        Require(Fingerprint(vm) == locked, "locked inspector/grid edit restores geometry");
        vm.SelectedEdge = Edge(vm, "BEZIER"); vm.IsCurveControlMode = true;
        Require(!vm.BeginCurveControlDrag(12.0 / 50 * 900, (1 - 6.0 / 30) * 520), "locked control point refuses drag");
        Execute(vm.SaveCommand); PumpUntil(() => !vm.IsBusy && client.SaveCount == 1, "save layer settings");
        Execute(vm.RefreshCommand); PumpUntil(() => !vm.IsBusy, "reload layer settings");
        Require(vm.MapLayers.Single().ToDomain() is { IsLocked: true, IsAllowSelect: true, Alpha: .4, Color: "#123456", StrokeWidth: 4 }, "all layer properties survive save/reload");

        var endpoints = OpenEditor(out _);
        Execute(endpoints.AddMapLayerCommand);
        Edge(endpoints, "ARC").LayerId = endpoints.CurrentLayer!.Id;
        endpoints.CurrentLayer.IsLocked = true;
        Require(!endpoints.BeginNodeDrag([Node(endpoints, "A")], 36, 485, 900, 520), "locked road protects endpoints on unlocked node layer");
        var protectedGeometry = Fingerprint(endpoints);
        endpoints.SelectedNode = Node(endpoints, "A"); Execute(endpoints.RemoveNodeCommand);
        Require(Fingerprint(endpoints) == protectedGeometry, "cross-layer endpoint deletion preserves locked road");

        var curves = OpenEditor(out _); curves.SnapToGeometry = true; curves.SnapDistance = .3;
        curves.SelectWorkbenchTool("point");
        curves.HandleMapClick(12.1 / 50 * 900, (1 - 6.1 / 30) * 520, 900, 520);
        Near(curves.SelectedNode!.X, 12, "Bezier handle attracts point X"); Near(curves.SelectedNode.Y, 6, "Bezier handle attracts point Y");
        Require(curves.Edges.Count == 3, "geometry snap does not silently split road");
        Execute(curves.UndoCommand);
        curves.ShowEdgeLayer = false;
        curves.SelectWorkbenchTool("point");
        curves.HandleMapClick(12.1 / 50 * 900, (1 - 6.1 / 30) * 520, 900, 520);
        Near(curves.SelectedNode!.X, 12.1, "hidden road cannot attract placement");

        var history = OpenEditor(out _);
        for (var i = 0; i < MaximumHistoryWorkload; i++)
        { history.NewNodeX = 25 + i * .01; history.NewNodeY = 15; Execute(history.AddPreciseNodeCommand); }
        Require(history.EditHistory.Count == MapManagementViewModel.MaximumEditHistory, "history retention bounded");
        Require(history.EditHistory.All(row => row.Time != default && row.Operation != "地图编辑"), "history exposes categories and timestamps");
        Execute(history.UndoCommand); Require(history.EditHistory.Any(row => row.State == "已撤销"), "history shows undone state");
        history.NewNodeX = 40; history.NewNodeY = 20; Execute(history.AddPreciseNodeCommand);
        Require(!history.RedoCommand.CanExecute() && history.EditHistory.All(row => row.State == "已执行"), "new edit invalidates redo branch");
    }

    private const int MaximumHistoryWorkload = MapManagementViewModel.MaximumEditHistory + 2;

    private static void BenchmarkLargeMap()
    {
        var client = new MemoryTrafficControlClient(); client.SetLargeFixture();
        var vm = new MapManagementViewModel(client);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        vm.EnterOnlineAsync().GetAwaiter().GetResult(); PumpUntil(() => !vm.IsBusy && vm.Nodes.Count == 10000, "load large fixture");
        var loadMs = watch.Elapsed.TotalMilliseconds;
        vm.SnapToGrid = false; vm.SelectWorkbenchTool("point");
        for (var i = 0; i < 10; i++) vm.UpdateWorkbenchPointer(i * 7, i * 3, 900, 520);
        watch.Restart();
        for (var i = 0; i < 200; i++) vm.UpdateWorkbenchPointer(i * 37 % 900, i * 19 % 520, 900, 520);
        var pointerMs = watch.Elapsed.TotalMilliseconds;
        Require(vm.PreviewSnapGuides.Count <= 4, "large map guide count remains bounded");
        var result = new { Nodes = vm.Nodes.Count, Edges = vm.Edges.Count, LoadMs = loadMs, PointerUpdates = 200, PointerTotalMs = pointerMs, PointerAverageMs = pointerMs / 200 };
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "map-editor-benchmark.json"), JsonSerializer.Serialize(result));
        Console.WriteLine("BENCHMARK: " + JsonSerializer.Serialize(result)); vm.DeactivateEditor();
    }

    private static void CheckMapObjectLayers()
    {
        var vm = OpenEditor(out var client);
        Require(vm.MapLayers.Count == 1 && vm.CurrentLayer?.Id == "navigation", "legacy map gets editable navigation layer");
        Execute(vm.AddMapLayerCommand);
        var id = vm.CurrentLayer!.Id; vm.CurrentLayer.Name = "装配区";
        vm.SelectWorkbenchTool("station"); vm.HandleMapClick(700, 400, 900, 520);
        Require(vm.SelectedNode?.LayerId == id, "new station uses current named layer");
        var created = vm.SelectedNode!;
        vm.CurrentLayer.Visible = false;
        Require(vm.Nodes.Count == 5 && vm.PreviewNodes.Count == 4 && vm.FindNodeAtCanvas(700, 400) is null,
            "hidden object layer keeps data and prevents canvas picking");
        Execute(vm.UndoCommand);
        Require(vm.MapLayers.Single(layer => layer.Id == id).Visible && vm.PreviewNodes.Count == 5, "layer visibility undo restores preview");
        vm.CurrentLayer = vm.MapLayers.Single(layer => layer.Id == id);
        vm.UpdateBatchNodeSelection([vm.Nodes.Single(node => node.Id == "A")]);
        Execute(vm.MoveSelectionToLayerCommand);
        Require(vm.Nodes.Single(node => node.Id == "A").LayerId == id && vm.Edges.Single(edge => edge.Id == "ARC").From == "A",
            "moving station layer preserves route endpoints");
        vm.CanvasWorldWidth = 90; vm.CanvasWorldHeight = 45; vm.GridSize = 0.25;
        Execute(vm.SaveCommand);
        PumpUntil(() => !vm.IsBusy, "save layers");
        Require(client.LastSavedRequest!.Definition.Layers.Any(layer => layer.Id == id && layer.Name == "装配区") &&
            client.LastSavedRequest.Definition.Nodes.Single(node => node.Id == created.Id).LayerId == id, "saved version contains layer names and object membership");
        Execute(vm.RefreshCommand);
        Require(vm.MapLayers.Single(layer => layer.Id == id).Name == "装配区" && vm.Nodes.Single(node => node.Id == created.Id).LayerId == id,
            "JSON roundtrip restores named layers and membership");
        Require(vm.CanvasWorldWidth == 90 && vm.CanvasWorldHeight == 45 && vm.GridSize == 0.25, "map-specific grid and canvas dimensions survive reload");
        vm.CurrentLayer = vm.MapLayers.Single(layer => layer.Id == id);
        Execute(vm.RemoveMapLayerCommand);
        Require(vm.MapLayers.Count == 1 && vm.Nodes.Count == 5 && vm.Edges.Count == 3 && vm.Nodes.All(node => node.LayerId == "navigation"),
            "removing layer transfers objects without deleting topology");
        Execute(vm.UndoCommand);
        Require(vm.MapLayers.Count == 2 && vm.Nodes.Single(node => node.Id == created.Id).LayerId == id, "layer removal undo restores membership");
        vm.DeactivateEditor();
    }

    private static void CheckCadDetailDisplay()
    {
        var vm = OpenEditor(out var client);
        var path = Path.Combine(Path.GetTempPath(), "cad-ui-fixture-" + Guid.NewGuid().ToString("N") + ".png");
        var pixels = Enumerable.Repeat((byte)255, 16 * 16 * 4).ToArray();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 64)));
        using (var stream = File.Create(path)) encoder.Save(stream);
        client.SetCadFrame(path);
        Execute(vm.RefreshCommand);
        var activation = vm.ActivateEditorAsync();
        PumpUntil(() => activation.IsCompleted, "activate CAD editor timers");
        activation.GetAwaiter().GetResult();
        var geometry = Fingerprint(vm);
        Require(vm.UnderlayQualityIndex == 1 && vm.UnderlayOpacity == 1, "CAD defaults high resolution and full opacity");
        vm.UnderlayOpacity = 0; Near(vm.UnderlayOpacity, 0, "opacity supports fully transparent reference background");
        vm.UnderlayOpacity = double.NaN; Near(vm.UnderlayOpacity, 1, "invalid opacity reset");
        vm.UpdateCadViewport(new Rect(200, 100, 500, 300), 1600, 960);
        vm.AdjustCanvasZoom(120); vm.AdjustCanvasZoom(120); vm.AdjustCanvasZoom(120);
        PumpUntil(() => client.CadRequests.Count == 1, "debounced local CAD request");
        var first = client.CadRequests[0];
        Near(first.Request.Region.MinX, 200, "viewport world X");
        Near(first.Request.Region.MinY, 120, "viewport world Y inversion");
        vm.PanCanvas(30, 20);
        first.Completion.SetResult(new(path, new(200, 120, 500, 300, 1600, 960)));
        PumpUntil(() => client.CadRequests.Count == 2, "pan invalidates and debounces new detail");
        Require(vm.CadDetailImage is null, "late result discarded after pan");
        var second = client.CadRequests[1];
        second.Completion.SetResult(new(path, new(200, 120, 500, 300, 1600, 960)));
        PumpUntil(() => vm.CadDetailImage is not null, "current local detail displayed");
        Near(vm.CadDetailLeft, 200, "overlay left aligned"); Near(vm.CadDetailTop, 100, "overlay top aligned");
        Near(vm.CadDetailWidth, 500, "overlay width aligned"); Near(vm.CadDetailHeight, 300, "overlay height aligned");
        Require(Fingerprint(vm) == geometry, "detail display does not alter navigation geometry");
        vm.EnableCadDetail = false; Require(vm.CadDetailImage is null, "disable clears detail overlay");
        vm.EnableCadDetail = true;
        PumpUntil(() => client.CadRequests.Count == 3, "re-enable schedules detail");
        vm.DeactivateEditor();
        client.CadRequests[2].Completion.SetResult(new(path, new(200, 120, 500, 300, 1600, 960)));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Require(vm.CadDetailImage is null && Fingerprint(vm) == geometry, "closed editor rejects late detail result");
    }

    private static void CheckNewMapConnectionStates()
    {
        var root = Path.Combine(Path.GetTempPath(), "AgvOfflineSmoke-" + Guid.NewGuid().ToString("N"));
        var store = new AgvDispatch.Infrastructure.Maps.LocalMapEditorStore(root);
        var client = System.Reflection.DispatchProxy.Create<ITrafficControlClient, RejectingTrafficProxy>();
        var spy = (RejectingTrafficProxy)client;
        var vm = new MapManagementViewModel(client) { LocalStore = store };
        Complete(vm.ActivateEditorAsync());
        Require(vm.IsOfflineEditing && vm.CanEditWorkbench && spy.Calls == 0, "offline startup never calls service");
        var request = new CreateMapRequest("离线测试", "", "", "LocalCartesian", "m", 1);
        var create = vm.CreateProjectAsync(request); Complete(create);
        Require(create.Result && vm.SaveCommand.CanExecute() && spy.Calls == 0, "offline creation has no session or service dependency");
        Execute(vm.AddNodeCommand);
        var geometry = Fingerprint(vm);
        Execute(vm.SaveCommand);
        PumpUntil(() => !vm.IsBusy && vm.StatusMessage.Contains("本地草稿已保存"), "save offline map");
        Require(!vm.PublishCommand.CanExecute() && !vm.ActivateCommand.CanExecute() && !vm.RefreshMapSwitchCommand.CanExecute(), "offline mode rejects publishing and map switch");
        var file = Path.Combine(root, "copy.agvmap.json"); Complete(vm.SaveLocalCopyAsync(file));
        Require(File.Exists(file) && spy.Calls == 0, "offline export does not call backend");
        var reopened = new MapManagementViewModel(client) { LocalStore = store };
        Complete(reopened.OpenLocalFileAsync(file));
        Require(Fingerprint(reopened) == geometry && reopened.IsOfflineEditing, "local file round-trip preserves geometry");
        Complete(vm.EnterOnlineAsync());
        Require(vm.IsOfflineEditing && Fingerprint(vm) == geometry && vm.StatusMessage.Contains("保留离线草稿"), "failed connection preserves local work");
        var attempts = spy.Calls;
        Execute(vm.RefreshCommand);
        PumpUntil(() => !vm.IsBusy, "refresh local maps");
        Require(spy.Calls == attempts && Fingerprint(vm) == geometry, "offline refresh stays local after failed connection");
        var dxf = Path.Combine(root, "test.dxf");
        var document = new ACadSharp.CadDocument();
        document.ModelSpace.Entities.Add(new ACadSharp.Entities.Line(new CSMath.XYZ(0, 0, 0), new CSMath.XYZ(10, 10, 0)));
        ACadSharp.IO.DxfWriter.Write(dxf, document, false);
        vm.BackgroundFilePath = dxf; vm.UnderlayQualityIndex = 0;
        Execute(vm.GenerateUnderlayCommand);
        PumpUntil(() => !vm.IsBusy, "local CAD render");
        Require(vm.UnderlayImage is not null && spy.Calls == attempts, "CAD renders locally without service");
        vm.AdjustCanvasZoom(120); vm.AdjustCanvasZoom(120); vm.AdjustCanvasZoom(120);
        vm.UpdateCadViewport(new Rect(300, 150, 300, 200), 1000, 700);
        PumpUntil(() => vm.CadDetailImage is not null, "offline CAD detail render");
        Require(spy.Calls == attempts, "local high-resolution viewport does not use named pipe");
        vm.CalibrationPoints.Add(new EditableCalibrationPoint { CadX = 0, CadY = 0, MapX = 0, MapY = 0 });
        vm.CalibrationPoints.Add(new EditableCalibrationPoint { CadX = 10, CadY = 10, MapX = 10, MapY = 10 });
        Execute(vm.SaveCalibrationCommand); PumpUntil(() => !vm.IsBusy, "save local calibration");
        Require(vm.StatusMessage.Contains("本地标定已保存") && spy.Calls == attempts, "calibration is local");
        Execute(vm.ValidateLocalCommand);
        Require(spy.Calls == attempts && vm.ValidationIssues.Any(issue => issue.Code == "NODE_ISOLATED"), "offline validation reports isolated node without service");
        Execute(vm.AddNodeCommand); Execute(vm.UndoCommand);
        Require(vm.CanEditWorkbench && spy.Calls == attempts, "offline undo remains independent of session");
        var topologyFile = Path.Combine(root, "topology.json");
        var saved = store.LoadAsync(vm.SelectedMap!.MapId); Complete(saved);
        File.WriteAllText(topologyFile, JsonSerializer.Serialize(saved.Result.Draft.Definition, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }));
        vm.ImportPath = topologyFile; Execute(vm.PreviewImportCommand); PumpUntil(() => !vm.IsBusy, "offline topology import");
        Require(vm.Nodes.Count == 1 && spy.Calls == attempts, "topology JSON imports locally");
        _offlinePreview = vm;
        reopened.DeactivateEditor();
        vm.DeactivateEditor();
    }

    private static void Complete(Task task)
    {
        PumpUntil(() => task.IsCompleted, "complete async editor operation");
        task.GetAwaiter().GetResult();
    }

    private static void CheckOfflineUpload()
    {
        var store = new AgvDispatch.Infrastructure.Maps.LocalMapEditorStore(Path.Combine(Path.GetTempPath(), "AgvOfflineUpload-" + Guid.NewGuid().ToString("N")));
        var client = new MemoryTrafficControlClient { AcceptCreate = true };
        var vm = new MapManagementViewModel(client) { LocalStore = store };
        var task = vm.CreateProjectAsync(new("上传测试", "", "", "LocalCartesian", "m", 1)); Complete(task);
        Execute(vm.AddNodeCommand);
        Execute(vm.AddNodeCommand); Execute(vm.AddEdgeCommand);
        Require(client.LastCreateRequest is null && client.SaveCount == 0, "offline work never creates backend map even when service available");
        var localId = vm.SelectedMap!.MapId;
        var local = Fingerprint(vm);
        Complete(vm.EnterOnlineAsync());
        Require(vm.IsOnlineEditing && client.LastCreateRequest is null && client.SaveCount == 0, "enter online is read-only, no implicit upload");
        var keptTask = store.LoadAsync(localId); Complete(keptTask);
        Require(keptTask.Result.Draft.Definition.Nodes.Count == 2, "enter online saves previous local work");
        Complete(vm.EnterOfflineAsync());
        Require(vm.IsOfflineEditing && !vm.PublishCommand.CanExecute(), "online work can detach into local copy");
        // Upload the simple valid local graph again, explicitly.
        vm.SelectedMap = vm.Maps.First(p => p.MapId == localId);
        PumpUntil(() => !vm.IsBusy && vm.Nodes.Count == 2, "restore local project for upload");
        Complete(vm.UploadOfflineAsync());
        Require(vm.IsOnlineEditing && client.LastCreateRequest is not null && client.SaveCount == 1,
            "explicit upload creates backend draft and saves once: " + vm.StatusMessage);
        Require(client.LastActivation is null && client.PublishCount == 0 && Fingerprint(vm) == local,
            "upload does not publish or activate, geometry preserved");
        vm.DeactivateEditor();
    }

    private static void CheckGenericMapSetup()
    {
        var initial = new MapManagementViewModel(new MemoryTrafficControlClient());
        Require(initial.MapName == "" && initial.Description == "" && initial.BackgroundFilePath == "" && initial.Unit == "m" && initial.CoordinateScale == 1,
            "new editor has no site-specific name, description or CAD path");
        initial.DeactivateEditor();
        var vm = OpenEditor(out var client);
        var original = Fingerprint(vm);
        var request = new CreateMapRequest("客户甲-三层", "不同项目", "other-layout.dxf", "LocalCartesian", "mm", 0.001);
        var accepted = vm.CreateProjectAsync(request).GetAwaiter().GetResult();
        Require(!accepted && client.LastCreateRequest == request && Fingerprint(vm) == original,
            "new-map request uses independent input and rejection preserves current editing workspace");
        vm.CadLayers.Add(new EditableCadLayer { Name = "PREVIOUS-FILE-LAYER", Included = true });
        vm.SelectCadFile("customer-layout.dxf");
        Execute(vm.GenerateUnderlayCommand);
        PumpUntil(() => !vm.IsBusy && client.LastUnderlayRequest is not null, "generate selected CAD");
        Require(client.LastSavedRequest?.BackgroundFilePath == "customer-layout.dxf" && client.LastUnderlayRequest!.SourcePath == "customer-layout.dxf",
            "CAD generation first saves selected file to correct map");
        Require(client.LastUnderlayRequest!.IncludedLayers!.Count == 0, "replacement CAD does not send prior file layers");
        vm.DeactivateEditor();
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static void RenderEditor()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var name in new[] { "Light", "Dark", "Compact" })
        {
            app.Resources = new ResourceDictionary();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/HandyControl;component/Themes/Theme.xaml") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/AgvDispatch.Wpf;component/Themes/{(name == "Compact" ? "Light" : name)}.xaml") });
            foreach (var resource in new[] { "Typography", "IconGlyphs", "Controls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/AgvDispatch.Wpf;component/Styles/{resource}.xaml") });

            var vm = OpenEditor(out var switchClient);
            switchClient.PreparePublished();
            Execute(vm.RefreshCommand);
            Execute(vm.BeginMapSwitchCommand);
            vm.CanvasWorldWidth = 26; vm.CanvasWorldHeight = 12;
            vm.SelectedEdge = Edge(vm, "BEZIER");
            vm.IsCurveControlMode = true;
            ViewModelLocationProvider.SetDefaultViewModelFactory(_ => vm);
            var width = name == "Compact" ? 1280 : 1680;
            var height = name == "Compact" ? 760 : 980;
            var view = new MapManagementView { DataContext = vm, Width = width, Height = height };
            view.Background = (Brush)app.FindResource("AppBackgroundBrush");
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.UpdateLayout();
            var mapCanvas = (FrameworkElement)view.FindName("MapCanvas");
            var zoomAnchor = new Point(710, 130);
            var anchorBefore = mapCanvas.TransformToAncestor(view).Transform(zoomAnchor);
            vm.AdjustCanvasZoom(120, zoomAnchor.X, zoomAnchor.Y, mapCanvas.ActualWidth, mapCanvas.ActualHeight);
            view.UpdateLayout();
            var anchorAfter = mapCanvas.TransformToAncestor(view).Transform(zoomAnchor);
            Near(anchorAfter.X, anchorBefore.X, name + " actual WPF zoom anchor X");
            Near(anchorAfter.Y, anchorBefore.Y, name + " actual WPF zoom anchor Y");
            vm.AdjustCanvasZoom(-120, zoomAnchor.X, zoomAnchor.Y, mapCanvas.ActualWidth, mapCanvas.ActualHeight);
            view.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
            var canvas = (System.Windows.Controls.Canvas)view.FindName("MapCanvas");
            var drawingTools = (FrameworkElement)view.FindName("DrawingTools");
            var toolButtons = VisualDescendants(drawingTools).OfType<System.Windows.Controls.Primitives.ToggleButton>().ToArray();
            Require(toolButtons.Length == vm.WorkbenchTools.Count, name + " all drawing tools are present");
            foreach (var toolButton in toolButtons)
            {
                var label = VisualDescendants(toolButton).OfType<System.Windows.Controls.TextBlock>().Single();
                var labelBounds = label.TransformToAncestor(toolButton).TransformBounds(new Rect(label.RenderSize));
                Require(label.FontSize >= 11 && labelBounds.Left >= 0 && labelBounds.Right <= toolButton.ActualWidth + 0.1
                    && labelBounds.Top >= 0 && labelBounds.Bottom <= toolButton.ActualHeight + 0.1,
                    name + " full tool label fits inside button: " + label.Text);
            }
            Require(!VisualDescendants(view).OfType<System.Windows.Controls.Button>().Any(button =>
                button.Content is string text && new[] { "地图配置", "属性", "图层", "模型", "高级工具", "对象表", "地图分析", "安全切换" }.Contains(text)),
                name + " no duplicate top panel buttons");
            var bounds = canvas.TransformToAncestor(view).TransformBounds(new Rect(canvas.RenderSize));
            var canvasViewport = (FrameworkElement)view.FindName("CanvasViewport");
            Require(canvasViewport.ActualWidth * canvasViewport.ActualHeight > width * height * 0.5,
                name + " map viewport occupies over half of workspace including aspect-ratio padding");
            foreach (var marker in VisualDescendants(canvas).OfType<System.Windows.Controls.Canvas>()
                .Where(item => item.Width == 14 && item.DataContext is MapCanvasNode))
            {
                var node = (MapCanvasNode)marker.DataContext;
                var center = marker.TransformToAncestor(canvas).Transform(new Point(7, 7));
                Near(center.X, node.Left + 7, name + " marker X matches hit location");
                Near(center.Y, node.Top + 7, name + " marker Y matches hit location");
            }
            vm.SelectWorkbenchTool("bezier");
            canvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
            Require(vm.IsCanvasEditMode && !vm.IsMapLineMode, name + " actual right-button handler exits tool");
            vm.SelectWorkbenchTool("control");
            view.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            image.Render(view);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            var path = Path.Combine(AppContext.BaseDirectory, $"map-editor-{name.ToLowerInvariant()}.png");
            using (var stream = File.Create(path)) encoder.Save(stream);
            Require(File.Exists(path) && new FileInfo(path).Length > 1000, name + " editor renders to PNG");
            Console.WriteLine(path);
            Require(!vm.ShowMapSetup, name + " map configuration is closed by default");
            vm.ShowMapSetup = true;
            view.UpdateLayout();
            var setupPanel = (FrameworkElement)view.FindName("MapSetupPanel");
            var rightPanel = (FrameworkElement)view.FindName("RightEditorPanel");
            var objectPanel = (FrameworkElement)view.FindName("ObjectInspectorPanel");
            Require(rightPanel.Visibility == Visibility.Visible && setupPanel.Visibility == Visibility.Visible && objectPanel.Visibility == Visibility.Collapsed,
                name + " configuration replaces inspector in a single right panel");
            var setupBounds = setupPanel.TransformToAncestor(view).TransformBounds(new Rect(setupPanel.RenderSize));
            var viewport = (FrameworkElement)view.FindName("CanvasViewport");
            var viewportBounds = viewport.TransformToAncestor(view).TransformBounds(new Rect(viewport.RenderSize));
            Require(setupBounds.Left >= viewportBounds.Right - 1 && setupBounds.Width <= 320,
                name + " configuration is docked right without overlaying canvas");
            var setupImage = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            setupImage.Render(view);
            var setupEncoder = new PngBitmapEncoder(); setupEncoder.Frames.Add(BitmapFrame.Create(setupImage));
            using (var setupFile = File.Create(Path.Combine(AppContext.BaseDirectory, $"map-setup-{name.ToLowerInvariant()}.png"))) setupEncoder.Save(setupFile);
            vm.ShowInspector = false;
            view.UpdateLayout();
            Require(!vm.ShowMapSetup && rightPanel.Visibility == Visibility.Collapsed, name + " collapse hides all configuration and inspector content");
            Require(viewport.ActualWidth > viewportBounds.Width + 270, name + " collapse returns panel width to canvas");
            ((System.Windows.Controls.Button)view.FindName("MapSetupRailButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            view.UpdateLayout();
            Require(vm.ShowMapSetup && vm.ShowInspector && setupPanel.Visibility == Visibility.Visible, name + " rail button reopens configuration");
            var attributeButton = (System.Windows.Controls.Button)view.FindName("AttributeRailButton");
            attributeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            view.UpdateLayout();
            Require(!vm.ShowMapSetup && objectPanel.Visibility == Visibility.Visible && setupPanel.Visibility == Visibility.Collapsed, name + " attribute button switches to inspector");
            foreach (var entry in new[] { ("MapSetupRailButton", "配置"), ("AttributeRailButton", "属性"), ("LayerRailButton", "图层"), ("TemplateRailButton", "模板") })
            {
                var railButton = (System.Windows.Controls.Button)view.FindName(entry.Item1);
                Require(VisualDescendants(railButton).OfType<System.Windows.Controls.TextBlock>().Any(label => label.Text == entry.Item2),
                    name + " right rail contains visible label " + entry.Item2);
            }
            var inspectorTabs = (System.Windows.Controls.TabControl)view.FindName("InspectorTabs");
            ((System.Windows.Controls.Button)view.FindName("LayerRailButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(inspectorTabs.SelectedIndex == 1 && vm.ShowInspector && !vm.ShowMapSetup, name + " labeled layer entry opens layer tab");
            ((System.Windows.Controls.Button)view.FindName("TemplateRailButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(inspectorTabs.SelectedIndex == 2 && vm.ShowInspector && !vm.ShowMapSetup, name + " labeled template entry opens template tab");
            attributeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var detailToggle = (System.Windows.Controls.Primitives.ToggleButton)view.FindName("DetailsPanelButton");
            var detailTabs = (System.Windows.Controls.TabControl)view.FindName("DetailsTabs");
            var detailBounds = detailToggle.TransformToAncestor(view).TransformBounds(new Rect(detailToggle.RenderSize));
            Require(detailBounds.Left >= width - 90, name + " detail toggle is in right rail");
            detailToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            view.UpdateLayout();
            Require(vm.ShowDetails && detailTabs.Visibility == Visibility.Visible, name + " right detail toggle opens detailed functions");
            detailToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
            view.UpdateLayout();
            Require(!vm.ShowDetails && detailTabs.Visibility == Visibility.Collapsed, name + " right detail toggle closes detailed functions");
            var refreshButton = (System.Windows.Controls.Button)view.FindName("RefreshRailButton");
            var refreshBounds = refreshButton.TransformToAncestor(view).TransformBounds(new Rect(refreshButton.RenderSize));
            Require(refreshBounds.Left >= width - 90 && ReferenceEquals(refreshButton.Command, vm.RefreshCommand), name + " refresh command preserved in right rail");
            if (name == "Light")
            {
                var inspector = (System.Windows.Controls.TabControl)view.FindName("InspectorTabs");
                inspector.SelectedIndex = 1;
                view.UpdateLayout();
                var layerImage = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                layerImage.Render(view);
                var layerEncoder = new PngBitmapEncoder(); layerEncoder.Frames.Add(BitmapFrame.Create(layerImage));
                using (var layerFile = File.Create(Path.Combine(AppContext.BaseDirectory, "map-object-layers.png"))) layerEncoder.Save(layerFile);
                Require(inspector.SelectedItem is System.Windows.Controls.TabItem { Header: "图层" }, "actual named layer panel opens");
                inspector.SelectedIndex = 0;
                var overviewVm = new MapOverviewViewModel(switchClient);
                Execute(overviewVm.RefreshCommand);
                var overviewView = new MapOverviewView { DataContext = overviewVm, Width = 1280, Height = 760 };
                overviewView.Measure(new Size(1280, 760)); overviewView.Arrange(new Rect(0, 0, 1280, 760)); overviewView.UpdateLayout();
                var overviewImage = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32);
                overviewImage.Render(overviewView);
                var overviewEncoder = new PngBitmapEncoder(); overviewEncoder.Frames.Add(BitmapFrame.Create(overviewImage));
                using (var overviewFile = File.Create(Path.Combine(AppContext.BaseDirectory, "map-overview.png"))) overviewEncoder.Save(overviewFile);
                Require(overviewVm.PreviewNodes.Count == 4, "map overview actual XAML renders topology");
                var disconnected = new MapManagementViewModel(new MemoryTrafficControlClient { SessionUnavailable = true });
                var emptyView = new MapManagementView { DataContext = disconnected, Width = 1280, Height = 760 };
                emptyView.Measure(new Size(1280, 760)); emptyView.Arrange(new Rect(0, 0, 1280, 760)); emptyView.UpdateLayout();
                Require(((System.Windows.Controls.Button)emptyView.FindName("NewMapButton")).IsEnabled,
                    "actual new-map button is enabled before any session or map exists");
                disconnected.DeactivateEditor();
                var dialog = new AgvDispatch.Wpf.NewMapWindow(vm);
                dialog.Measure(new Size(dialog.Width, dialog.Height));
                dialog.Arrange(new Rect(0, 0, dialog.Width, dialog.Height));
                dialog.UpdateLayout();
                var content = (FrameworkElement)dialog.Content;
                dialog.Content = null;
                var dialogRoot = new System.Windows.Controls.Border { Background = new SolidColorBrush(Color.FromRgb(245, 247, 250)), Child = content };
                dialogRoot.Measure(new Size(520, 510));
                dialogRoot.Arrange(new Rect(0, 0, 520, 510));
                dialogRoot.UpdateLayout();
                var dialogImage = new RenderTargetBitmap(520, 510, 96, 96, PixelFormats.Pbgra32);
                dialogImage.Render(dialogRoot);
                var dialogEncoder = new PngBitmapEncoder();
                dialogEncoder.Frames.Add(BitmapFrame.Create(dialogImage));
                using var dialogFile = File.Create(Path.Combine(AppContext.BaseDirectory, "map-new-project.png"));
                dialogEncoder.Save(dialogFile);
                Require(content.ActualHeight > 400, "independent new map dialog lays out");
                dialog.Close();
                var snapVm = OpenEditor(out _);
                snapVm.SnapToAlignment = true; snapVm.SnapDistance = .3;
                snapVm.SelectWorkbenchTool("point");
                ViewModelLocationProvider.SetDefaultViewModelFactory(_ => snapVm);
                var snapView = new MapManagementView { DataContext = snapVm, Width = 1280, Height = 760 };
                snapView.Measure(new Size(1280, 760)); snapView.Arrange(new Rect(0, 0, 1280, 760)); snapView.UpdateLayout();
                snapVm.UpdateWorkbenchPointer(2.1 / 50 * 900, (1 - 6.0 / 30) * 520, 900, 520);
                snapView.UpdateLayout();
                var snapImage = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32); snapImage.Render(snapView);
                var snapEncoder = new PngBitmapEncoder(); snapEncoder.Frames.Add(BitmapFrame.Create(snapImage));
                using (var snapFile = File.Create(Path.Combine(AppContext.BaseDirectory, "map-editor-alignment.png"))) snapEncoder.Save(snapFile);
                Require(snapVm.PreviewSnapGuides.Count == 3, "render alignment line and exact landing marker");
                var historyVm = OpenEditor(out _);
                historyVm.NewNodeX = 25; historyVm.NewNodeY = 15; Execute(historyVm.AddPreciseNodeCommand);
                historyVm.NewNodeX = 30; Execute(historyVm.AddPreciseNodeCommand); Execute(historyVm.UndoCommand);
                historyVm.ShowDetails = true;
                ViewModelLocationProvider.SetDefaultViewModelFactory(_ => historyVm);
                var historyView = new MapManagementView { DataContext = historyVm, Width = 1280, Height = 760 };
                var historyTabs = (System.Windows.Controls.TabControl)historyView.FindName("DetailsTabs");
                historyTabs.SelectedItem = historyTabs.Items.OfType<System.Windows.Controls.TabItem>().Single(tab => Equals(tab.Header, "编辑历史"));
                historyView.Measure(new Size(1280, 760)); historyView.Arrange(new Rect(0, 0, 1280, 760)); historyView.UpdateLayout();
                var historyImage = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32); historyImage.Render(historyView);
                var historyEncoder = new PngBitmapEncoder(); historyEncoder.Frames.Add(BitmapFrame.Create(historyImage));
                using (var historyFile = File.Create(Path.Combine(AppContext.BaseDirectory, "map-editor-edit-history.png"))) historyEncoder.Save(historyFile);
                Require(historyVm.EditHistory.Count == 2 && historyVm.EditHistory.Any(row => row.State == "已撤销"), "actual history panel renders pending redo");
                var topologyVm = OpenTopologyEditor(CrossMap());
                topologyVm.UpdateBatchEdgeSelection(topologyVm.Edges.ToArray()); Execute(topologyVm.PreviewIntersectionsCommand);
                topologyVm.ShowDetails = true;
                ViewModelLocationProvider.SetDefaultViewModelFactory(_ => topologyVm);
                var topologyView = new MapManagementView { DataContext = topologyVm, Width = 1280, Height = 760 };
                ((System.Windows.Controls.TabControl)topologyView.FindName("DetailsTabs")).SelectedIndex = 1;
                ((System.Windows.Controls.TabControl)topologyView.FindName("AdvancedToolsTabs")).SelectedIndex = 2;
                topologyView.Measure(new Size(1280, 760)); topologyView.Arrange(new Rect(0, 0, 1280, 760)); topologyView.UpdateLayout();
                topologyVm.UpdateBatchEdgeSelection(topologyVm.Edges.ToArray()); Execute(topologyVm.PreviewIntersectionsCommand);
                topologyView.UpdateLayout();
                var topologyImage = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32); topologyImage.Render(topologyView);
                var topologyEncoder = new PngBitmapEncoder(); topologyEncoder.Frames.Add(BitmapFrame.Create(topologyImage));
                using (var topologyFile = File.Create(Path.Combine(AppContext.BaseDirectory, "map-editor-topology-preview.png"))) topologyEncoder.Save(topologyFile);
                Require(topologyVm.TopologyJunctionPreview.Count == 1 && topologyVm.ApplyIntersectionsCommand.CanExecute(), "actual topology preview panel renders actionable junction");
                topologyVm.DeactivateEditor();
            }
            vm.DeactivateEditor();
        }
        if (_offlinePreview is { } offlineVm)
        {
            ViewModelLocationProvider.SetDefaultViewModelFactory(_ => offlineVm);
            var offlineView = new MapManagementView { DataContext = offlineVm, Width = 1280, Height = 760 };
            offlineView.Measure(new Size(1280, 760)); offlineView.Arrange(new Rect(0, 0, 1280, 760)); offlineView.UpdateLayout();
            var offlineImage = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32); offlineImage.Render(offlineView);
            var offlineEncoder = new PngBitmapEncoder(); offlineEncoder.Frames.Add(BitmapFrame.Create(offlineImage));
            using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "map-editor-offline.png"))) offlineEncoder.Save(file);
            var buttons = VisualDescendants(offlineView).OfType<System.Windows.Controls.Button>().ToArray();
            Require(buttons.Any(button => Equals(button.Content, "保存本地") && button.IsEnabled), "offline view displays enabled local save");
            Require(buttons.Any(button => Equals(button.Content, "打开本地") && button.Visibility == Visibility.Visible), "offline view displays file open");
            Require(buttons.Any(button => Equals(button.Content, "校验发布") && !button.IsEnabled), "offline view disables backend publish");
        }
    }

    private static void AssertCopiedCurves(MapManagementViewModel vm, double x, double y, string operation)
    {
        var arc = vm.Edges.Single(edge => edge.Id != "ARC" && edge.GeometryKind == MapEdgeGeometryKind.CircularArc);
        var bezier = vm.Edges.Single(edge => edge.Id != "BEZIER" && edge.GeometryKind == MapEdgeGeometryKind.CubicBezier);
        AssertControls(vm, arc.Id, [new(6 + x, 6 + y)], operation + " arc");
        AssertControls(vm, bezier.Id, [new(12 + x, 6 + y), new(20 + x, 6 + y)], operation + " Bezier");
        Require(arc.ResourceIds == "R1" && bezier.SpeedLimit == 0.8, operation + " retains curve traffic properties");
    }

    private static void AssertControls(MapManagementViewModel vm, string id, MapPoint2D[] expected, string context)
    {
        var actual = Edge(vm, id).ParseControlPoints();
        Require(actual.Count == expected.Length, context + " control count");
        for (var index = 0; index < actual.Count; index++)
        {
            Near(actual[index].X, expected[index].X, context + $" control {index} X");
            Near(actual[index].Y, expected[index].Y, context + $" control {index} Y");
        }
    }

    private static void CheckUndoRedo(MapManagementViewModel vm, string before, string after, string operation)
    {
        Require(before != after, operation + " changes map");
        Execute(vm.UndoCommand);
        Require(Fingerprint(vm) == before, operation + " undo restores complete geometry");
        Execute(vm.RedoCommand);
        Require(Fingerprint(vm) == after, operation + " redo restores complete geometry");
    }

    private static int ControlCount(MapEdgeGeometryKind kind) => kind switch
    {
        MapEdgeGeometryKind.CircularArc => 1,
        MapEdgeGeometryKind.CubicBezier => 2,
        _ => 0
    };
    private static EditableMapNode Node(MapManagementViewModel vm, string id) => vm.Nodes.Single(node => node.Id == id);
    private static EditableMapEdge Edge(MapManagementViewModel vm, string id) => vm.Edges.Single(edge => edge.Id == id);
    private static void SelectAll(MapManagementViewModel vm) => vm.UpdateBatchNodeSelection(vm.Nodes.ToArray());
    private static string Fingerprint(MapManagementViewModel vm) => JsonSerializer.Serialize(new
    {
        Nodes = vm.Nodes.Select(node => node.ToDomain()),
        Edges = vm.Edges.Select(edge => edge.ToDomain(vm.Nodes)),
        Resources = vm.Resources.Select(resource => resource.ToDomain())
    });

    private static void Execute(ICommand command)
    {
        Require(command.CanExecute(null), "command is enabled");
        command.Execute(null);
    }

    private static void PumpUntil(Func<bool> done, string context)
    {
        if (done()) return;
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (done() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Require(done(), "timeout: " + context);
    }

    private static void Near(double actual, double expected, string message, double tolerance = 1e-7) =>
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"{message}: expected {expected:R}, actual {actual:R}");

    private static void Require(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class MemoryTrafficControlClient : ITrafficControlClient, ICadDetailClient
{
    public List<(CadDetailRequest Request, TaskCompletionSource<CadDetailResult> Completion)> CadRequests { get; } = [];
    public Task<CadDetailResult> GetCadDetailAsync(CadDetailRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CadDetailResult>();
        CadRequests.Add((request, completion));
        return completion.Task; // Intentionally ignores cancellation to exercise stale response guards.
    }
    public void SetCadFrame(string path)
    {
        var workspace = Workspace;
        _workspaceJson = JsonSerializer.Serialize(workspace with { Project = workspace.Project with
        { PreviewPngPath = path, CadViewBox = new(0, 0, 900, 520, 900, 520), UnderlayStatus = CadUnderlayJobStatus.Ready } }, JsonOptions);
    }
    public Task<CommunicationSettingsSnapshot> GetCommunicationSettingsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<CommunicationSettingsSnapshot> SaveCommunicationSettingsAsync(SaveCommunicationSettingsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ConnectionProbeResult> ProbeConnectionAsync(ConnectionProbeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private string _workspaceJson = JsonSerializer.Serialize(CreateWorkspace(), JsonOptions);
    public ActivateMapRequest? LastActivation { get; private set; }
    private MapSwitchSnapshot? _switch;
    private MapExecutionMode _mode = MapExecutionMode.Running;
    public void PreparePublished()
    {
        var workspace = Workspace;
        workspace = workspace with { Published = workspace.Draft with { VersionId = "SMOKE-PUBLISHED", Status = MapVersionStatus.Published } };
        _workspaceJson = JsonSerializer.Serialize(workspace, JsonOptions);
    }
    public Task<MapSwitchStatus> GetMapSwitchAsync(CancellationToken ct) => Task.FromResult(new MapSwitchStatus(_mode, _switch,
        _switch is null ? [] : ["执行任务2条，等待原任务收尾", "AGV-02尚未安全停靠", "翻料站PLC握手尚未完成"],
        _switch is null ? [] : [_switch]));
    public Task<MapSwitchResult> BeginMapSwitchAsync(BeginMapSwitchRequest request, CancellationToken ct)
    {
        _switch = new("SMOKE-RELEASE", request.MapId, request.VersionId, null, null, MapSwitchPhase.Draining,
            "测试工程师", "", DateTimeOffset.Now, DateTimeOffset.Now);
        _mode = MapExecutionMode.Draining;
        return Task.FromResult(new MapSwitchResult(true, "测试：停止新增派单，等待收尾"));
    }
    public Task<MapSwitchResult> ResumeMapSwitchAsync(FinishMapSwitchRequest request, CancellationToken ct)
    { _switch = null; _mode = MapExecutionMode.Running; return Task.FromResult(new MapSwitchResult(true, "测试：确认放行")); }
    public Task<MapSwitchResult> CancelMapSwitchAsync(FinishMapSwitchRequest request, CancellationToken ct)
    { _switch = null; _mode = MapExecutionMode.Running; return Task.FromResult(new MapSwitchResult(true, "测试：取消")); }
    public int SaveCount { get; private set; }
    public void SetDefinition(MapDefinition definition)
    {
        var workspace = Workspace;
        _workspaceJson = JsonSerializer.Serialize(workspace with { Draft = workspace.Draft with { Definition = definition } }, JsonOptions);
    }
    public void SetLargeFixture()
    {
        var workspace = Workspace;
        var nodes = Enumerable.Range(0, 10000).Select(i => new MapNode($"N{i}", i % 100 * .45, i / 100 * .28, MapNodeType.Waypoint, true, [])).ToArray();
        var edges = Enumerable.Range(0, 1000).Select(i => new MapEdge($"E{i}", nodes[i].Id, nodes[i + 1].Id,
            Math.Sqrt(Math.Pow(nodes[i + 1].X - nodes[i].X, 2) + Math.Pow(nodes[i + 1].Y - nodes[i].Y, 2)), 1, true, true, [])).ToArray();
        var definition = workspace.Draft.Definition with { Nodes = nodes, Edges = edges, Resources = [] };
        _workspaceJson = JsonSerializer.Serialize(workspace with { Draft = workspace.Draft with { Definition = definition } }, JsonOptions);
    }
    public void SetUnderlayState(CadUnderlayJobStatus status)
    {
        var workspace = Workspace;
        _workspaceJson = JsonSerializer.Serialize(workspace with { Project = workspace.Project with { UnderlayStatus = status, UnderlayMessage = status.ToString() } }, JsonOptions);
    }
    public SaveMapDraftRequest? LastSavedRequest { get; private set; }
    private MapWorkspaceSnapshot Workspace => JsonSerializer.Deserialize<MapWorkspaceSnapshot>(_workspaceJson, JsonOptions)!;

    public bool SessionUnavailable { get; set; }
    public bool EmptyMapList { get; set; }
    public TrafficPermission SessionPermissions { get; set; } = TrafficPermission.View | TrafficPermission.ManageMap;
    public Task<TrafficControlSession> GetSessionAsync(CancellationToken cancellationToken) => SessionUnavailable
        ? Task.FromException<TrafficControlSession>(new TimeoutException("后台服务未连接"))
        : Task.FromResult(new TrafficControlSession("map-smoke-fixture", "Map smoke fixture", SessionPermissions));
    public Task<IReadOnlyList<MapProjectSummary>> GetMapsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MapProjectSummary>>(EmptyMapList ? [] : [Workspace.Project]);
    public Task<MapWorkspaceSnapshot?> GetMapWorkspaceAsync(string mapId, CancellationToken cancellationToken) =>
        Task.FromResult<MapWorkspaceSnapshot?>(mapId == Workspace.Project.MapId ? Workspace : null);
    public Task<IReadOnlyList<MapOperationAuditSnapshot>> GetMapAuditsAsync(string mapId, int maximumCount, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MapOperationAuditSnapshot>>([]);
    public Task<IReadOnlyList<MapVersionHistoryItem>> GetMapVersionHistoryAsync(string mapId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MapVersionHistoryItem>>([]);

    public Task<MapOperationResult> SaveMapDraftAsync(SaveMapDraftRequest request, CancellationToken cancellationToken)
    {
        LastSavedRequest = JsonSerializer.Deserialize<SaveMapDraftRequest>(JsonSerializer.Serialize(request, JsonOptions), JsonOptions)!;
        var workspace = Workspace;
        if (request.ExpectedEditToken != workspace.Draft.EditToken)
            return Task.FromResult(new MapOperationResult(false, "stale fixture token"));
        SaveCount++;
        workspace = workspace with
        {
            Draft = workspace.Draft with { Definition = LastSavedRequest.Definition, EditToken = "fixture-" + (SaveCount + 1) },
            Project = workspace.Project with { Name = request.Name, Description = request.Description, BackgroundFilePath = request.BackgroundFilePath }
        };
        _workspaceJson = JsonSerializer.Serialize(workspace, JsonOptions);
        return Task.FromResult(new MapOperationResult(true, "isolated draft saved", Workspace));
    }

    private static MapWorkspaceSnapshot CreateWorkspace()
    {
        var now = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
        var nodes = new[]
        {
            new MapNode("A", 2, 2, MapNodeType.Station, true, [], "LM-A"),
            new MapNode("B", 10, 2, MapNodeType.Waypoint, true, [], "LM-B"),
            new MapNode("C", 12, 2, MapNodeType.Waypoint, true, [], "LM-C"),
            new MapNode("D", 20, 2, MapNodeType.Station, true, [], "LM-D")
        };
        var arc = new MapEdge("ARC", "A", "B", Math.PI * 4, 1, true, true, ["R1"])
        { GeometryKind = MapEdgeGeometryKind.CircularArc, ControlPoints = [new(6, 6)] };
        var bezier = new MapEdge("BEZIER", "C", "D", 0, 0.8, true, true, [])
        { GeometryKind = MapEdgeGeometryKind.CubicBezier, ControlPoints = [new(12, 6), new(20, 6)] };
        bezier = bezier with { Distance = MapEdgeGeometry.Length(bezier, nodes[2], nodes[3]) };
        var definition = new MapDefinition("Map smoke fixture", "", "LocalCartesian", "m", nodes,
            [arc, new MapEdge("LINK", "B", "C", 2, 1, true, true, []), bezier],
            [new TrafficResourceDefinition("R1", TrafficResourceType.NarrowAisle)]);
        var project = new MapProjectSummary("SMOKE-MAP", definition.Name, "isolated fixture", "", "None",
            definition.CoordinateSystem, definition.Unit, 1, "", "", [], null, null, CadUnderlayJobStatus.None,
            "No underlay", null, false, null, null, now);
        var draft = new MapVersionSnapshot("SMOKE-DRAFT", project.MapId, 1, MapVersionStatus.Draft, definition,
            new MapValidationReport([], 4, 3, 1), "fixture", now, now, null, "fixture-1");
        return new MapWorkspaceSnapshot(project, draft, null);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public Task<TaskListPage> GetTasksAsync(TaskListQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<TaskCenterDetail?> GetTaskDetailAsync(string taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SystemStatusSnapshot> GetSystemStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<AgvTrackPointSnapshot>> GetAgvTrackAsync(AgvTrackQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<AlarmSnapshot>> GetAlarmsAsync(int maximumCount, bool activeOnly, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<AlarmOperationResult> AcknowledgeAlarmAsync(AcknowledgeAlarmRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public CreateMapRequest? LastCreateRequest { get; private set; }
    public bool AcceptCreate { get; set; }
    public int PublishCount { get; private set; }
    public Task<MapOperationResult> CreateMapAsync(CreateMapRequest request, CancellationToken cancellationToken)
    {
        LastCreateRequest = request;
        if (!AcceptCreate) return Task.FromResult(new MapOperationResult(false, "isolated creation rejection"));
        var workspace = CreateWorkspace();
        workspace = workspace with { Project = workspace.Project with { MapId = "SMOKE-UPLOAD", Name = request.Name }, Draft = workspace.Draft with { MapId = "SMOKE-UPLOAD" } };
        _workspaceJson = JsonSerializer.Serialize(workspace, JsonOptions);
        return Task.FromResult(new MapOperationResult(true, "isolated new backend draft", Workspace));
    }
    public Task<MapOperationResult> PublishMapAsync(PublishMapRequest request, CancellationToken cancellationToken)
    { PublishCount++; throw new NotSupportedException(); }
    public GenerateMapUnderlayRequest? LastUnderlayRequest { get; private set; }
    public Task<MapOperationResult> GenerateMapUnderlayAsync(GenerateMapUnderlayRequest request, CancellationToken cancellationToken)
    { LastUnderlayRequest = request; return Task.FromResult(new MapOperationResult(true, "isolated underlay queued", Workspace)); }
    public Task<MapOperationResult> SaveMapCalibrationAsync(SaveMapCalibrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<MapOperationResult> ActivateMapAsync(ActivateMapRequest request, CancellationToken cancellationToken)
    {
        LastActivation = request;
        _switch = _switch! with { Phase = MapSwitchPhase.AwaitingResume };
        _mode = MapExecutionMode.Held;
        var workspace = Workspace;
        workspace = workspace with { Project = workspace.Project with { IsActive = true, ActiveVersionId = request.VersionId } };
        _workspaceJson = JsonSerializer.Serialize(workspace, JsonOptions);
        return Task.FromResult(new MapOperationResult(true, "测试：已激活，等待放行", Workspace));
    }
    public Task<MapImportPreviewResult> PreviewMapImportAsync(PreviewMapImportRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<MapExportResult> ExportMapAsync(ExportMapRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<MapVersionSnapshot?> GetMapVersionAsync(string mapId, string versionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<TrafficResourceSnapshot>> GetResourcesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<TrafficFlowSnapshot> GetTrafficFlowAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<TrafficResourceReleaseAuditSnapshot>> GetRecentAuditsAsync(int maximumCount, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<ManualReleaseWorkflowItem>> GetReleaseRequestsAsync(int maximumCount, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ManualReleaseWorkflowResult> SubmitReleaseAsync(SubmitManualReleaseRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ManualReleaseWorkflowResult> ReviewReleaseAsync(ReviewManualReleaseRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
}
