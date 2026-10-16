using System.Windows.Media;
using System.Collections.ObjectModel;
using AgvDispatch.Domain.Maps;
using Prism.Commands;
using Prism.Mvvm;
namespace AgvDispatch.Wpf.ViewModels;

public sealed class MapEditorTool(string key, string label, string icon, string hint) : BindableBase
{
    private bool _active;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public Geometry Icon { get; } = Geometry.Parse(icon);
    public string Hint { get; } = label + " · " + hint;
    public bool Active { get => _active; set => SetProperty(ref _active, value); }
}
public sealed partial class MapManagementViewModel
{
    public void SelectCadFile(string fileName)
    {
        BackgroundFilePath = fileName;
        StatusMessage = "已选择CAD文件。确认单位与换算比例，再生成底图；新文件会重新读取图层并清除旧标定。";
    }
    public sealed record NodeTypeChoice(MapNodeType Kind, string Label);
    public IReadOnlyList<NodeTypeChoice> WorkbenchNodeTypes { get; } =
    [new(MapNodeType.Waypoint, "路径节点"), new(MapNodeType.Station, "业务站点"),
     new(MapNodeType.Intersection, "交叉口"), new(MapNodeType.WaitingPoint, "等待点"), new(MapNodeType.ChargingPoint, "充电点")];
    private bool _showInspector = true, _showDetails, _showMapSetup, _isPanMode;
    private bool _showNodeLayer = true, _showEdgeLayer = true, _showResourceLayer = true, _showUnderlayLayer = true, _showVehicleLayer = true;
    private string _pointerText = "X —  Y —  m";
    public IReadOnlyList<MapEditorTool> WorkbenchTools { get; } =
    [
        new("select", "选择", "M3,2 L3,21 8,16 12,23 15,21 11,14 18,14 Z", "左键选择/拖动；Ctrl多选，空白处框选"),
        new("pan", "平移", "M12,2 L12,22 M2,12 L22,12 M8,6 L12,2 16,6 M8,18 L12,22 16,18 M6,8 L2,12 6,16 M18,8 L22,12 18,16", "左键拖动画布；滚轮缩放"),
        new("point", "路径节点", "M8,12 A4,4 0 1 1 16,12 A4,4 0 1 1 8,12 M12,2 L12,6 M12,18 L12,22 M2,12 L6,12 M18,12 L22,12", "左键连续放置节点，右键退出"),
        new("station", "业务站点", "M4,5 L20,5 20,19 4,19 Z M8,9 L16,9 M8,13 L16,13 M12,19 L12,23", "左键放置站点，再填写厂家站点编号"),
        new("line", "直线路段", "M3,20 L21,4 M2,18 L5,21 M19,2 L23,6", "依次点击起点、终点，可连续画路"),
        new("arc", "圆弧路段", "M3,20 C3,1 21,1 21,20 M1,18 L5,22 M19,18 L23,22", "点击两端建立圆弧，控制柄调整弧上点"),
        new("bezier", "贝塞尔路段", "M2,20 C2,1 22,23 22,4 M2,20 L2,2 M22,4 L22,22 M0,2 L4,2 M20,22 L24,22", "点击两端建立曲线，控制柄调整切线"),
        new("control", "曲线控制柄", "M3,19 C5,2 19,21 21,5 M3,19 L6,3 M21,5 L18,21 M4,1 L8,1 8,5 4,5 Z M16,19 L20,19 20,23 16,23 Z", "先选曲线，再拖黄色控制点"),
        new("boundary", "区域边界", "M3,4 L20,3 22,19 7,22 Z M1,2 L5,2 5,6 1,6 Z M18,1 L22,1 22,5 18,5 Z", "选交通资源后逐点绘制，完成闭合"),
        new("vertex", "边界顶点", "M3,4 L20,3 22,19 7,22 Z M18,1 L22,1 22,5 18,5 Z", "选择资源后拖动顶点"),
        new("paint", "资源绑定", "M4,18 L17,5 21,9 8,22 Z M16,4 L19,1 24,6 21,9 M3,23 L8,22", "选交通资源，点击点或边切换绑定"),
        new("measure", "测量", "M2,7 L22,7 22,17 2,17 Z M6,7 L6,12 M10,7 L10,14 M14,7 L14,12 M18,7 L18,14", "点击两点测距离和方位角")
    ];
    public DelegateCommand<string> SelectWorkbenchToolCommand { get; private set; } = null!;
    public ObservableCollection<MapCanvasGridLine> PreviewToolGuides { get; } = [];
    public DelegateCommand CancelWorkbenchToolCommand { get; private set; } = null!;
    public bool ShowInspector { get => _showInspector; set { SetProperty(ref _showInspector, value); if (!value) ShowMapSetup = false; } }
    public bool ShowDetails { get => _showDetails; set => SetProperty(ref _showDetails, value); }
    public bool ShowMapSetup { get => _showMapSetup; set { SetProperty(ref _showMapSetup, value); if (value) ShowInspector = true; } }
    public bool IsPanMode { get => _isPanMode; private set => SetProperty(ref _isPanMode, value); }
    public bool ShowNodeLayer { get => _showNodeLayer; set { if(SetProperty(ref _showNodeLayer,value)){if(!value)ClearHiddenSelection();RefreshDisplay();} } }
    public bool ShowEdgeLayer { get => _showEdgeLayer; set { if(SetProperty(ref _showEdgeLayer,value)){if(!value)ClearHiddenSelection();RefreshDisplay();} } }
    public bool ShowResourceLayer { get => _showResourceLayer; set { if(SetProperty(ref _showResourceLayer,value)){if(!value)ClearHiddenSelection();RefreshDisplay();} } }
    public bool ShowUnderlayLayer { get => _showUnderlayLayer; set { if (SetProperty(ref _showUnderlayLayer, value)) ScheduleCadDetail(); } }
    public bool ShowVehicleLayer { get => _showVehicleLayer; set => SetProperty(ref _showVehicleLayer, value); }
    public string PointerText { get => _pointerText; private set => SetProperty(ref _pointerText, value); }
    public bool CanEditWorkbench => CanManage();
    public bool CanOpenNewMap => !IsBusy;
    public string MapConnectionText => IsOfflineEditing ? "离线模式：新建、编辑、CAD与保存不需要调度服务。" : _session is null ? "在线模式：服务连接不可用，可切回离线继续编辑。" :
        (_session.Permissions.HasFlag(AgvDispatch.Application.Abstractions.TrafficPermission.ManageMap) ||
         _session.Permissions.HasFlag(AgvDispatch.Application.Abstractions.TrafficPermission.Administer))
            ? "已连接调度后台，可创建地图。" : "已连接，但当前账号没有地图管理权限。";
    public string WorkbenchCursor => IsPanMode ? "Hand" : IsCanvasEditMode ? "Arrow" : "Cross";
    public string WorkbenchToolText => IsPanMode ? "平移" : IsMapPointMode ? (DrawNodeType == MapNodeType.Waypoint ? "路径节点" : "业务站点") :
        IsMapLineMode ? GeometryChoices.First(c => c.Kind == DrawEdgeGeometryKind).Label + "路段" : IsCurveControlMode ? "曲线控制柄" :
        IsBoundaryDrawMode ? "区域边界" : IsBoundaryVertexMode ? "边界顶点" : IsResourcePaintMode ? "资源绑定" : IsMeasureMode ? "测量" : "选择";
    public string WorkbenchHint => IsPanMode ? "左键拖动画布 · 滚轮缩放 · 右键退出平移" : IsMapPointMode ? "左键连续放置 · 右键 / Esc 返回选择" :
        IsMapLineMode ? "左键：起点 → 终点 → 下一段 · 右键 / Esc 结束连续画路" : IsCurveControlMode ? "点选曲线后拖动黄色控制柄 · 右键 / Esc 返回选择" :
        IsBoundaryDrawMode ? "选择资源后逐点绘制 · 点击“闭合区域”完成 · 右键结束当前工具" :
        IsBoundaryVertexMode ? "选择交通资源，再拖动边界顶点 · 右键 / Esc 返回选择" :
        IsResourcePaintMode ? "选择交通资源，点击节点或路段切换绑定 · 右键 / Esc 返回选择" : IsMeasureMode ? "点击两点测量 · 右键 / Esc 退出" :
        "左键选择/拖动 · Ctrl 多选 · 空白处框选 · 中键平移 · 滚轮缩放";
    private void InitializeWorkbenchTools()
    {
        SelectWorkbenchToolCommand = new DelegateCommand<string>(SelectWorkbenchTool);
        CancelWorkbenchToolCommand = new DelegateCommand(CancelWorkbenchTool);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsMapPointMode) or nameof(IsMapLineMode) or nameof(IsCanvasEditMode) or nameof(IsMeasureMode) or
                nameof(IsBoundaryDrawMode) or nameof(IsBoundaryVertexMode) or nameof(IsResourcePaintMode) or nameof(IsCurveControlMode) or nameof(DrawEdgeGeometryKind) or nameof(DrawNodeType) or nameof(IsPanMode))
                RefreshWorkbenchTools();
        };
        IsCanvasEditMode = true;
        RefreshWorkbenchTools();
    }
    private void RefreshWorkbenchTools()
    {
        foreach (var tool in WorkbenchTools) tool.Active = tool.Key switch
        {
            "select" => IsCanvasEditMode && !IsPanMode, "pan" => IsPanMode,
            "point" => IsMapPointMode && DrawNodeType == MapNodeType.Waypoint, "station" => IsMapPointMode && DrawNodeType != MapNodeType.Waypoint,
            "line" => IsMapLineMode && DrawEdgeGeometryKind == MapEdgeGeometryKind.Line,
            "arc" => IsMapLineMode && DrawEdgeGeometryKind == MapEdgeGeometryKind.CircularArc,
            "bezier" => IsMapLineMode && DrawEdgeGeometryKind == MapEdgeGeometryKind.CubicBezier,
            "control" => IsCurveControlMode, "boundary" => IsBoundaryDrawMode, "vertex" => IsBoundaryVertexMode,
            "paint" => IsResourcePaintMode, "measure" => IsMeasureMode, _ => false
        };
        RaisePropertyChanged(nameof(WorkbenchToolText)); RaisePropertyChanged(nameof(WorkbenchHint));
        RaisePropertyChanged(nameof(WorkbenchCursor));
    }
    public void CancelWorkbenchTool()
    {
        IsPanMode = false; IsMapPointMode = false; IsMapLineMode = false; IsCurveControlMode = false;
        IsBoundaryDrawMode = false; IsBoundaryVertexMode = false; IsResourcePaintMode = false; IsMeasureMode = false;
        _lineStartNode = null; EndNodeDrag(); EndBoundaryVertexDrag(); EndCurveControlDrag();
        PreviewToolGuides.Clear();
        PreviewSnapGuides.Clear();
        IsCanvasEditMode = true;
        StatusMessage = "已取消当前工具，返回选择；已完成的地图对象保留。";
        RefreshWorkbenchTools();
    }
    public void SelectWorkbenchTool(string key)
    {
        CancelWorkbenchTool();
        if (key is "point" or "station" or "waiting" or "charging" or "intersection" or "line" or "arc" or "bezier" or "boundary")
            if (CurrentLayer is { Visible: false } layer) layer.Visible = true;
        switch (key)
        {
            case "pan": IsCanvasEditMode = false; IsPanMode = true; break;
            case "point": case "station":
                if (!ShowNodeLayer) ShowNodeLayer = true;
                DrawNodeType = key == "point" ? MapNodeType.Waypoint : MapNodeType.Station; IsMapPointMode = true; break;
            case "waiting": case "charging": case "intersection":
                ShowNodeLayer = true;
                DrawNodeType = key == "waiting" ? MapNodeType.WaitingPoint : key == "charging" ? MapNodeType.ChargingPoint : MapNodeType.Intersection;
                IsMapPointMode = true; break;
            case "line": case "arc": case "bezier":
                ShowNodeLayer = true; ShowEdgeLayer = true;
                DrawNodeType = MapNodeType.Waypoint;
                DrawEdgeGeometryKind = key == "line" ? MapEdgeGeometryKind.Line : key == "arc" ? MapEdgeGeometryKind.CircularArc : MapEdgeGeometryKind.CubicBezier;
                IsMapLineMode = true; break;
            case "control": ShowEdgeLayer = true; IsCurveControlMode = true; break;
            case "boundary": ShowResourceLayer = true; IsBoundaryDrawMode = true; break;
            case "vertex": ShowResourceLayer = true; IsBoundaryVertexMode = true; break;
            case "paint": ShowResourceLayer = true; IsResourcePaintMode = true; break;
            case "measure": IsMeasureMode = true; break;
        }
        StatusMessage = WorkbenchToolText + "：" + WorkbenchHint;
        RefreshWorkbenchTools();
    }
    public void UpdateWorkbenchPointer(double x, double y, double width, double height)
    {
        PreviewToolGuides.Clear();
        PreviewSnapGuides.Clear();
        if (!TryCanvasToMap(x, y, width, height, out var point)) { PointerText = "画布范围外"; return; }
        var mapX = point.X; var mapY = point.Y;
        if (IsMapPointMode || IsMapLineMode)
        {
            mapX = SnapToGrid ? Math.Round(mapX / GridSize) * GridSize : mapX;
            mapY = SnapToGrid ? Math.Round(mapY / GridSize) * GridSize : mapY;
            ResolvePlacementSnap(ref mapX, ref mapY, true);
            UpdateSnapGuides(mapX, mapY);
            if (TryMapToCanvas(mapX, mapY) is { } snapped) { x = snapped.X; y = snapped.Y; }
        }
        PointerText = $"X {mapX:F3}  Y {mapY:F3}  m";
        if (IsMapLineMode && _lineStartNode is { } start && TryMapToCanvas(start.X, start.Y) is { } origin)
            PreviewToolGuides.Add(new(origin.X, origin.Y, x, y, "#2563EB", 1.4));
    }
}
