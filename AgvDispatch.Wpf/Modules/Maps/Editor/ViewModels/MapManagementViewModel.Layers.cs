using System.Collections.ObjectModel;
using AgvDispatch.Domain.Maps;
using Prism.Commands;
using Prism.Mvvm;

namespace AgvDispatch.Wpf.ViewModels;

public sealed class EditableMapLayer(string id, string name, bool visible) : BindableBase
{
    private string _name = name;
    private bool _visible = visible;
    private bool _isLocked, _isAllowSelect = true;
    private double _alpha = 1, _strokeWidth = 2;
    private string? _color;
    public string Id { get; } = id;
    public event Action? Changing;
    public string Name { get => _name; set { if (_name != value) Changing?.Invoke(); SetProperty(ref _name, value); } }
    public bool Visible { get => _visible; set { if (_visible != value) Changing?.Invoke(); SetProperty(ref _visible, value); } }
    public bool IsLocked { get => _isLocked; set { if (_isLocked != value) Changing?.Invoke(); SetProperty(ref _isLocked, value); } }
    public bool IsAllowSelect { get => _isAllowSelect; set { if (_isAllowSelect != value) Changing?.Invoke(); SetProperty(ref _isAllowSelect, value); } }
    public double Alpha { get => _alpha; set { var next = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1; if (_alpha != next) Changing?.Invoke(); SetProperty(ref _alpha, next); } }
    public double StrokeWidth { get => _strokeWidth; set { var next = double.IsFinite(value) ? Math.Clamp(value, .25, 12) : 2; if (_strokeWidth != next) Changing?.Invoke(); SetProperty(ref _strokeWidth, next); } }
    public string? Color { get => _color; set { var next = NormalizeLayerColor(value); if (_color != next) Changing?.Invoke(); SetProperty(ref _color, next); } }
    private static string? NormalizeLayerColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return (text.Length is 7 or 9) && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit) ? text.ToUpperInvariant() : null;
    }
    public MapEditorLayer ToDomain() => new(Id, string.IsNullOrWhiteSpace(Name) ? "未命名图层" : Name.Trim(), Visible)
    { IsLocked = IsLocked, IsAllowSelect = IsAllowSelect, Alpha = Alpha, Color = Color, StrokeWidth = StrokeWidth };
}

public sealed partial class MapManagementViewModel
{
    public ObservableCollection<EditableMapLayer> MapLayers { get; } = [];
    private EditableMapLayer? _currentLayer;
    public EditableMapLayer? CurrentLayer {get=>_currentLayer;set{if(SetProperty(ref _currentLayer,value)&&OnlyCurrentLayer){ClearHiddenSelection();RefreshDisplay();}}}
    public DelegateCommand AddMapLayerCommand { get; private set; } = null!;
    public DelegateCommand MoveSelectionToLayerCommand { get; private set; } = null!;
    public DelegateCommand RemoveMapLayerCommand { get; private set; } = null!;
    private void InitializeMapLayers()
    {
        AddMapLayerCommand = new DelegateCommand(() =>
        {
            if (!CanManage() || _workspace is null) return;
            RecordHistory();
            CurrentLayer = AddMapLayer(new("layer-" + Guid.NewGuid().ToString("N"), $"图层 {MapLayers.Count + 1}"));
            StatusMessage = "已新增图层。可修改名称，新绘制对象进入当前图层。";
        });
        MoveSelectionToLayerCommand = CreateMapEditCommand(() =>
        {
            if (!CanManage() || CurrentLayer is null) return;
            RecordHistory();
            foreach (var node in _batchSelectedNodes) node.LayerId = CurrentLayer.Id;
            foreach (var edge in _batchSelectedEdges) edge.LayerId = CurrentLayer.Id;
            if (SelectedResource is { } resource) resource.LayerId = CurrentLayer.Id;
            RebuildPreview();
            StatusMessage = $"已将选中对象移入“{CurrentLayer.Name}”；路径连接关系保留。";
        });
        RemoveMapLayerCommand = CreateMapEditCommand(() =>
        {
            if (!CanManage() || CurrentLayer is null || MapLayers.Count <= 1) return;
            var source = CurrentLayer;
            var target = MapLayers.First(layer => layer != source);
            RecordHistory();
            foreach (var node in Nodes.Where(node => node.LayerId == source.Id)) node.LayerId = target.Id;
            foreach (var edge in Edges.Where(edge => edge.LayerId == source.Id)) edge.LayerId = target.Id;
            foreach (var resource in Resources.Where(resource => resource.LayerId == source.Id)) resource.LayerId = target.Id;
            MapLayers.Remove(source); CurrentLayer = target; RebuildPreview();
            StatusMessage = $"已移除图层，对象转入“{target.Name}”，未删除任何地图对象。";
        });
    }
    private EditableMapLayer AddMapLayer(MapEditorLayer value)
    {
        var layer = new EditableMapLayer(value.Id, value.Name, value.Visible)
        { IsLocked = value.IsLocked, IsAllowSelect = value.IsAllowSelect, Alpha = value.Alpha, Color = value.Color, StrokeWidth = value.StrokeWidth };
        layer.Changing += () => RecordHistory("图层设置", true);
        layer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(EditableMapLayer.Visible) or nameof(EditableMapLayer.IsLocked) or nameof(EditableMapLayer.IsAllowSelect) || args.PropertyName==nameof(EditableMapLayer.Alpha)&&layer.Alpha==0)
            {
                CancelWorkbenchTool(); UpdateBatchNodeSelection([]); UpdateBatchEdgeSelection([]);
                SelectedNode = null; SelectedEdge = null; SelectedResource = null;
            }
            RebuildPreview(); InvalidateDraftAnalysis(); RaiseCommands();
        };
        MapLayers.Add(layer);
        return layer;
    }
    private void LoadMapLayers(IEnumerable<MapEditorLayer> values)
    {
        MapLayers.Clear();
        foreach (var layer in values.Where(layer => !string.IsNullOrWhiteSpace(layer.Id)).DistinctBy(layer => layer.Id)) AddMapLayer(layer);
        if (MapLayers.Count == 0) AddMapLayer(new("navigation", "导航图层"));
        foreach (var id in Nodes.Select(node => node.LayerId).Concat(Edges.Select(edge => edge.LayerId)).Concat(Resources.Select(resource => resource.LayerId)).Distinct())
            if (!MapLayers.Any(layer => layer.Id == id)) AddMapLayer(new(id, "导入图层"));
        CurrentLayer = MapLayers.First();
    }
    private bool IsMapLayerVisible(string id) => MapLayers.FirstOrDefault(layer => layer.Id == id)?.Visible != false && MapLayers.FirstOrDefault(layer=>layer.Id==id)?.Alpha != 0 && (!OnlyCurrentLayer||CurrentLayer?.Id==id);
    private string DrawingLayerId => CurrentLayer?.Id ?? "navigation";
    private void LoadEditorSettings(MapEditorSettings? settings)
    {
        CanvasWorldWidth = settings?.WorldWidth ?? 50;
        CanvasWorldHeight = settings?.WorldHeight ?? 30;
        GridSize = settings?.GridSize ?? 0.5;
        ShowGrid = settings?.ShowGrid ?? true;
        SnapToAlignment = settings?.SnapToAlignment ?? true;
        SnapToGeometry = settings?.SnapToGeometry ?? true;
        RestoreDisplay(settings?.Display);
        if (settings is not null)
        {
            SnapToGrid = settings.SnapToGrid; SnapToExisting = settings.SnapToExisting;
            CurrentLayer = MapLayers.FirstOrDefault(layer => layer.Id == settings.CurrentLayerId) ?? CurrentLayer;
        }
    }
}
