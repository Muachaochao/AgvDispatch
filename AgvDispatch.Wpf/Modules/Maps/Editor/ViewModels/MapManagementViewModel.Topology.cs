using System.Collections.ObjectModel;
using System.Text.Json;
using AgvDispatch.Domain.Maps;
using Prism.Commands;

namespace AgvDispatch.Wpf.ViewModels;

public sealed record MapTopologyJunctionPreview(string NodeId, double X, double Y, string Action);

public sealed partial class MapManagementViewModel
{
    private MapIntersectionSplitPlan? _intersectionPlan;
    private string? _intersectionPlanSource;
    private double _axisConnectionTolerance = 0.01;
    private string _intersectionPreviewSummary = "选择 2–256 条路段后预览；只分割同图层的直线交叉与 T 形连接。";
    public ObservableCollection<MapTopologyJunctionPreview> TopologyJunctionPreview { get; } = [];
    public string IntersectionPreviewSummary { get => _intersectionPreviewSummary; private set => SetProperty(ref _intersectionPreviewSummary, value); }
    public double AxisConnectionTolerance { get => _axisConnectionTolerance; set => SetProperty(ref _axisConnectionTolerance, value); }
    public DelegateCommand PreviewIntersectionsCommand { get; private set; } = null!;
    public DelegateCommand ApplyIntersectionsCommand { get; private set; } = null!;
    public DelegateCommand ConnectHorizontalCommand { get; private set; } = null!;
    public DelegateCommand ConnectVerticalCommand { get; private set; } = null!;

    private void InitializeTopologyTools()
    {
        PreviewIntersectionsCommand = new(PreviewSelectedIntersections, () => CanManage() && _batchSelectedEdges.Count >= 2);
        ApplyIntersectionsCommand = CreateMapEditCommand(SplitPlannedIntersections, () => CanManage() && _intersectionPlan is { SplitEdges: > 0 });
        ConnectHorizontalCommand = CreateMapEditCommand(() => ConnectNodesAlongAxis(false), CanAlignNodes);
        ConnectVerticalCommand = CreateMapEditCommand(() => ConnectNodesAlongAxis(true), CanAlignNodes);
    }

    private void RefreshTopologyCommands()
    {
        PreviewIntersectionsCommand?.RaiseCanExecuteChanged(); ApplyIntersectionsCommand?.RaiseCanExecuteChanged();
        ConnectHorizontalCommand?.RaiseCanExecuteChanged(); ConnectVerticalCommand?.RaiseCanExecuteChanged();
    }

    private string TopologyPlanFingerprint() => JsonSerializer.Serialize(new
    {
        Snapshot = CaptureSnapshot(), Selected = _batchSelectedEdges.Select(edge => edge.Id).OrderBy(id => id).ToArray(), ShowEdgeLayer
    });

    private void PreviewSelectedIntersections()
    {
        _intersectionPlan = null; _intersectionPlanSource = null; TopologyJunctionPreview.Clear();
        try
        {
            if (!ShowEdgeLayer) throw new InvalidOperationException("请先显示路段图层。");
            var before = CaptureSnapshot();
            var map = new MapDefinition("分割预览", "", "LocalCartesian", "m", before.Nodes, before.Edges, before.Resources)
            { Layers = before.Layers };
            var plan = MapIntersectionSplitter.Plan(map, _batchSelectedEdges.Select(edge => edge.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
            _intersectionPlan = plan; _intersectionPlanSource = TopologyPlanFingerprint();
            foreach (var node in plan.Junctions)
                TopologyJunctionPreview.Add(new(node.Id, node.X, node.Y, before.Nodes.Any(old => old.Id == node.Id) ? "复用端点/节点" : "新增交叉口"));
            IntersectionPreviewSummary = $"拟分割 {plan.SplitEdges} 条路段，新增 {plan.AddedNodes} 个交叉口、{plan.AddedEdges} 条路段；" +
                $"跳过 {plan.SkippedEdges} 条曲线/隐藏/锁定/不可选/无效路段，{plan.AmbiguousIntersections} 处歧义。跨图层及重叠线不连接。";
            StatusMessage = "分割预览未改变地图；核对连接位置后点击“应用分割”。";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { IntersectionPreviewSummary = StatusMessage = exception.Message; }
        RefreshTopologyCommands();
    }

    private void SplitPlannedIntersections()
    {
        if (_intersectionPlan is not { SplitEdges: > 0 } plan) return;
        if (_intersectionPlanSource != TopologyPlanFingerprint())
        {
            _intersectionPlan = null; TopologyJunctionPreview.Clear();
            IntersectionPreviewSummary = StatusMessage = "地图、图层或选择已变化，请重新预览后应用。";
            RefreshTopologyCommands(); return;
        }
        RecordHistory();
        var snapshot = CaptureSnapshot() with { Nodes = plan.Definition.Nodes, Edges = plan.Definition.Edges };
        _intersectionPlan = null; _intersectionPlanSource = null;
        RestoreSnapshot(snapshot);
        TopologyJunctionPreview.Clear();
        IntersectionPreviewSummary = StatusMessage = $"已分割 {plan.SplitEdges} 条路段，新增 {plan.AddedNodes} 个交叉口；方向、限速、资源和启用状态保留，可撤销一步恢复。请核对交叉口交通资源。";
        RefreshTopologyCommands();
    }

    private void ConnectNodesAlongAxis(bool vertical)
    {
        if (_batchSelectedNodes.Count > 256 || !double.IsFinite(AxisConnectionTolerance) || AxisConnectionTolerance < 0 || AxisConnectionTolerance > 1)
        { StatusMessage = "每次最多选择 256 个节点；行列容差需为 0–1 米。"; return; }
        if (!double.IsFinite(DrawEdgeSpeedLimit) || DrawEdgeSpeedLimit <= 0)
        { StatusMessage = "请先设置有效的正数限速。"; return; }
        var usable = _batchSelectedNodes.Where(node => IsMapLayerVisible(node.LayerId) && IsLayerSelectable(node.LayerId) &&
            !IsNodeProtected(node) && double.IsFinite(node.X) && double.IsFinite(node.Y)).ToArray();
        var pairs = new List<(EditableMapNode From, EditableMapNode To)>();
        foreach (var layer in usable.GroupBy(node => node.LayerId))
        {
            var remaining = layer.OrderBy(node => vertical ? node.X : node.Y).ToList();
            while (remaining.Count > 0)
            {
                var anchor = vertical ? remaining[0].X : remaining[0].Y;
                var band = remaining.TakeWhile(node => (vertical ? node.X : node.Y) - anchor <= AxisConnectionTolerance).ToArray();
                remaining.RemoveRange(0, band.Length);
                var ordered = band.OrderBy(node => vertical ? node.Y : node.X).ToArray();
                for (var i = 1; i < ordered.Length; i++)
                {
                    var from = ordered[i - 1]; var to = ordered[i];
                    if (Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2)) <= 1e-7) continue;
                    if (Edges.Any(edge => CanonicalEdgeKey(edge.From, edge.To) == CanonicalEdgeKey(from.Id, to.Id))) continue;
                    pairs.Add((from, to));
                }
            }
        }
        if (pairs.Count == 0) { StatusMessage = "没有可新增的同图层相邻连线；请检查所选节点、容差及已有连接。"; return; }
        RecordHistory(vertical ? "ConnectVertical" : "ConnectHorizontal");
        var edgeIndex = NextIndex(Edges.Select(edge => edge.Id), "E");
        var used = Edges.Select(edge => edge.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in pairs)
        {
            while (!used.Add($"E{edgeIndex:000}")) edgeIndex++;
            var edge = new EditableMapEdge { Id = $"E{edgeIndex++:000}", From = from.Id, To = to.Id,
                LayerId = from.LayerId, SpeedLimit = DrawEdgeSpeedLimit, Bidirectional = DrawEdgeBidirectional,
                Enabled = from.Enabled && to.Enabled, GeometryKind = MapEdgeGeometryKind.Line };
            Attach(edge); Edges.Add(edge);
        }
        RebuildPreview(); RaiseCommands();
        StatusMessage = $"已按{(vertical ? "纵向从下到上" : "横向从左到右")}连接同图层相邻节点，新增 {pairs.Count} 条直线；容差 {AxisConnectionTolerance:g} 米。";
    }
}
