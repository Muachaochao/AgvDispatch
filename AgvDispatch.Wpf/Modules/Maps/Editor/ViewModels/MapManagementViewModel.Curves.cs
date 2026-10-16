using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using AgvDispatch.Domain.Maps;
using Prism.Commands;

namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class MapManagementViewModel
{
    private MapEdgeGeometryKind _drawEdgeGeometryKind;
    private bool _isCurveControlMode;
    private bool _updatingCurveGeometry;
    private bool _transformingCurveGeometry;
    private EditableMapEdge? _dragCurveEdge;
    private int _dragCurveControlIndex;
    private bool _curveDragHistoryRecorded;

    public ObservableCollection<MapCanvasCurveControl> PreviewCurveControls { get; } = [];
    public ObservableCollection<MapCanvasGridLine> PreviewCurveGuides { get; } = [];
    public IReadOnlyList<MapGeometryChoice> GeometryChoices { get; } =
    [
        new(MapEdgeGeometryKind.Line, "直线"),
        new(MapEdgeGeometryKind.CircularArc, "圆弧"),
        new(MapEdgeGeometryKind.CubicBezier, "三次贝塞尔")
    ];
    public DelegateCommand ApplyEdgeGeometryCommand { get; private set; } = null!;
    public MapEdgeGeometryKind DrawEdgeGeometryKind
    {
        get => _drawEdgeGeometryKind;
        set => SetProperty(ref _drawEdgeGeometryKind, value);
    }
    public bool IsCurveControlMode
    {
        get => _isCurveControlMode;
        set
        {
            if (!SetProperty(ref _isCurveControlMode, value)) return;
            EndCurveControlDrag();
            if (value)
            {
                IsMapPointMode = false; IsMapLineMode = false; IsResourcePaintMode = false;
                IsCanvasEditMode = false; IsBoundaryVertexMode = false;
                IsMeasureMode = false; IsBoundaryDrawMode = false;
                StatusMessage = "曲线控制柄：先点选曲线路段，再拖动黄色控制点；也可在路段表填写精确坐标。";
            }
            RebuildPreview();
        }
    }

    private void InitializeCurveTools() => ApplyEdgeGeometryCommand = CreateMapEditCommand(
        ApplyEdgeGeometry, () => CanManage() && SelectedEdge is not null);

    private void ApplyEdgeGeometry()
    {
        if (SelectedEdge is not { } edge || !CanManage() ||
            !TryGetEndpoints(edge, out var from, out var to)) return;
        RecordHistory();
        InitializeEdgeGeometry(edge, from, to, DrawEdgeGeometryKind);
        IsCurveControlMode = DrawEdgeGeometryKind != MapEdgeGeometryKind.Line;
        RebuildPreview();
        StatusMessage = $"路段{edge.Id}已设为{GeometryChoices.First(item => item.Kind == edge.GeometryKind).Label}，长度{edge.Distance:F3}米。";
    }

    private void InitializeEdgeGeometry(EditableMapEdge edge, EditableMapNode from,
        EditableMapNode to, MapEdgeGeometryKind kind)
    {
        var dx = to.X - from.X; var dy = to.Y - from.Y;
        _updatingCurveGeometry = true;
        try
        {
            edge.GeometryKind = kind;
            edge.SetControlPoints(kind switch
            {
                MapEdgeGeometryKind.CircularArc => [new((from.X + to.X) / 2 - dy / 4, (from.Y + to.Y) / 2 + dx / 4)],
                MapEdgeGeometryKind.CubicBezier =>
                [new(from.X + dx / 3 - dy / 4, from.Y + dy / 3 + dx / 4),
                 new(from.X + 2 * dx / 3 - dy / 4, from.Y + 2 * dy / 3 + dx / 4)],
                _ => []
            });
            UpdateEdgeGeometryDistance(edge);
        }
        finally { _updatingCurveGeometry = false; }
    }

    private bool TryGetEndpoints(EditableMapEdge edge, out EditableMapNode from, out EditableMapNode to)
    {
        from = Nodes.FirstOrDefault(node => node.Id.Equals(edge.From, StringComparison.OrdinalIgnoreCase))!;
        to = Nodes.FirstOrDefault(node => node.Id.Equals(edge.To, StringComparison.OrdinalIgnoreCase))!;
        return from is not null && to is not null;
    }

    private void UpdateEdgeGeometryDistance(EditableMapEdge edge)
    {
        if (!TryGetEndpoints(edge, out var from, out var to)) return;
        var domain = edge.ToDomain(Nodes);
        var distance = MapEdgeGeometry.TryValidate(domain, from.ToDomain(), to.ToDomain(), out _)
            ? MapEdgeGeometry.Length(domain, from.ToDomain(), to.ToDomain()) : 0;
        if (Math.Abs(edge.Distance - distance) > 1e-9) edge.Distance = distance;
    }

    private void AttachCurveAware(INotifyPropertyChanged item)
    {
        var lastPosition = item is EditableMapNode node ? new MapPoint2D(node.X, node.Y) : null;
        item.PropertyChanged += (_, args) =>
        {
            if (item is EditableMapNode moved && args.PropertyName is nameof(EditableMapNode.X) or nameof(EditableMapNode.Y))
            {
                var dx = moved.X - lastPosition!.X; var dy = moved.Y - lastPosition.Y;
                lastPosition = new(moved.X, moved.Y);
                if (!_transformingCurveGeometry && double.IsFinite(dx) && double.IsFinite(dy))
                {
                    _updatingCurveGeometry = true;
                    try
                    {
                        foreach (var edge in Edges.Where(edge =>
                            edge.From.Equals(moved.Id, StringComparison.OrdinalIgnoreCase) ||
                            edge.To.Equals(moved.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            var controls = edge.ParseControlPoints().ToArray();
                            var isFrom = edge.From.Equals(moved.Id, StringComparison.OrdinalIgnoreCase);
                            if (edge.GeometryKind == MapEdgeGeometryKind.CubicBezier && controls.Length == 2)
                            {
                                var index = isFrom ? 0 : 1;
                                controls[index] = new(controls[index].X + dx, controls[index].Y + dy);
                            }
                            else if (edge.GeometryKind == MapEdgeGeometryKind.CircularArc && controls.Length == 1)
                                controls[0] = new(controls[0].X + dx / 2, controls[0].Y + dy / 2);
                            edge.SetControlPoints(controls);
                            UpdateEdgeGeometryDistance(edge);
                        }
                    }
                    finally { _updatingCurveGeometry = false; }
                }
            }
            if (_updatingCurveGeometry || _transformingCurveGeometry) return;
            if (item is EditableMapEdge changed &&
                args.PropertyName is nameof(EditableMapEdge.GeometryKind) or nameof(EditableMapEdge.ControlPointsText)
                    or nameof(EditableMapEdge.From) or nameof(EditableMapEdge.To))
            {
                _updatingCurveGeometry = true;
                try { UpdateEdgeGeometryDistance(changed); }
                finally { _updatingCurveGeometry = false; }
            }
            RebuildPreview();
            if (ReferenceEquals(item, SelectedEdge)) ReverseSelectedEdgeCommand.RaiseCanExecuteChanged();
        };
    }

    // Rigid transforms apply to internal geometry as well as nodes. For an external edge,
    // only the Bezier handle attached to the selected endpoint follows the transform.
    private void TransformSelectedGeometry(Func<MapPoint2D, MapPoint2D> transform)
    {
        var selected = _batchSelectedNodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _transformingCurveGeometry = true;
        try
        {
            foreach (var edge in Edges)
            {
                var moveFrom = selected.Contains(edge.From); var moveTo = selected.Contains(edge.To);
                var controls = edge.ParseControlPoints().ToArray();
                if (moveFrom && moveTo)
                    edge.SetControlPoints(controls.Select(transform));
                else if (edge.GeometryKind == MapEdgeGeometryKind.CubicBezier && controls.Length == 2)
                {
                    if (moveFrom) controls[0] = transform(controls[0]);
                    if (moveTo) controls[1] = transform(controls[1]);
                    edge.SetControlPoints(controls);
                }
            }
            foreach (var node in _batchSelectedNodes)
            {
                var target = transform(new(node.X, node.Y));
                node.X = target.X; node.Y = target.Y;
            }
            RecalculateEdgeDistances(selected);
        }
        finally { _transformingCurveGeometry = false; }
    }

    public bool BeginCurveControlDrag(double canvasX, double canvasY)
    {
        if (!IsCurveControlMode || !CanManage()) return false;
        var hit = PreviewCurveControls.OrderBy(control =>
                Math.Pow(control.Left + 6 - canvasX, 2) + Math.Pow(control.Top + 6 - canvasY, 2))
            .FirstOrDefault(control => Math.Sqrt(Math.Pow(control.Left + 6 - canvasX, 2) +
                Math.Pow(control.Top + 6 - canvasY, 2)) <= 14 / Math.Max(CanvasZoom, 0.1));
        if (hit is null) return false;
        _dragCurveEdge = Edges.FirstOrDefault(edge => edge.Id == hit.EdgeId);
        if (_dragCurveEdge is not null && IsLayerLocked(_dragCurveEdge.LayerId)) { _dragCurveEdge = null; return false; }
        _dragCurveControlIndex = hit.Index;
        _curveDragHistoryRecorded = false;
        return _dragCurveEdge is not null;
    }

    public void DragCurveControl(double canvasX, double canvasY, double canvasWidth, double canvasHeight)
    {
        if (_dragCurveEdge is not { } edge || !IsCurveControlMode || !Edges.Contains(edge) || !CanManage() || IsLayerLocked(edge.LayerId) ||
            !TryCanvasToMap(canvasX, canvasY, canvasWidth, canvasHeight, out var point)) return;
        var controls = edge.ParseControlPoints().ToArray();
        if (_dragCurveControlIndex >= controls.Length) return;
        var target = new MapPoint2D(SnapCoordinate(point.X), SnapCoordinate(point.Y));
        if (controls[_dragCurveControlIndex] == target) return;
        if (!_curveDragHistoryRecorded) { RecordHistory(); _curveDragHistoryRecorded = true; }
        controls[_dragCurveControlIndex] = target;
        edge.SetControlPoints(controls);
    }

    public void EndCurveControlDrag()
    {
        if (_curveDragHistoryRecorded) StatusMessage = "曲线控制点已调整，可使用Ctrl+Z撤销。";
        _dragCurveEdge = null;
        _curveDragHistoryRecorded = false;
    }

    private void RebuildCurveControlPreview()
    {
        PreviewCurveControls.Clear(); PreviewCurveGuides.Clear();
        if (!IsCurveControlMode || SelectedEdge is not { } edge ||
            !TryGetEndpoints(edge, out var from, out var to)) return;
        var controls = edge.ParseControlPoints();
        for (var index = 0; index < controls.Count; index++)
        {
            if (TryMapToCanvas(controls[index].X, controls[index].Y) is not { } point) continue;
            PreviewCurveControls.Add(new(edge.Id, index, point.X - 6, point.Y - 6,
                edge.GeometryKind == MapEdgeGeometryKind.CircularArc ? "过弧点" : $"C{index + 1}"));
            if (edge.GeometryKind != MapEdgeGeometryKind.CubicBezier) continue;
            var node = index == 0 ? from : to;
            if (TryMapToCanvas(node.X, node.Y) is { } endpoint)
                PreviewCurveGuides.Add(new(endpoint.X, endpoint.Y, point.X, point.Y, "#FFFFD54F", 1.5));
        }
    }

    private static string EdgeGeometryKey(EditableMapEdge edge, bool includeDirection = false)
    {
        var reverse = !includeDirection || edge.Bidirectional
            ? string.Compare(edge.From, edge.To, StringComparison.OrdinalIgnoreCase) > 0 : false;
        var controls = edge.ParseControlPoints();
        if (reverse && edge.GeometryKind == MapEdgeGeometryKind.CubicBezier) controls = controls.Reverse().ToArray();
        return $"{(reverse ? edge.To : edge.From)}|{(reverse ? edge.From : edge.To)}|{edge.GeometryKind}|" +
               EditableTrafficResource.FormatBoundary(controls) + (includeDirection ? $"|{edge.Bidirectional}" : "");
    }
}

public sealed record MapGeometryChoice(MapEdgeGeometryKind Kind, string Label);
public sealed record MapCanvasCurveControl(string EdgeId, int Index, double Left, double Top, string Label);
