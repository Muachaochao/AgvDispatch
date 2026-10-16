using System.Windows;
using System.Windows.Media;
using System.Collections.ObjectModel;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Regions;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AgvDispatch.Wpf.ViewModels;

public sealed class MapOverviewViewModel : BindableBase, INavigationAware
{
    private readonly ITrafficControlClient _client;
    private MapProjectSummary? _selectedMap;
    private bool _isBusy;
    private string _statusMessage = "等待加载地图配置。";
    private string _activeMapText = "未激活运行地图";
    private string _versionText = "草稿 -- / 已发布 --";
    private string _topologyText = "0 节点 · 0 路段 · 0 资源";
    private string _validationText = "尚未加载校验结果";
    private string _backgroundText = "未配置底图";
    private string _mapDescription = "请选择地图查看当前配置。";
    private MapWorkspaceSnapshot? _workspace;
    private ImageSource? _underlayImage;
    private string _previewTitle = "选择地图", _previewHint = "尚未创建地图，请打开编辑器新建。";
    private bool _showPublished;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MapOverviewViewModel(ITrafficControlClient client)
    {
        _client = client;
        RefreshCommand = new DelegateCommand(async () => await LoadAsync(), () => !IsBusy);
        _refreshTimer.Tick += async (_, _) => await LoadAsync();
    }

    public ObservableCollection<MapProjectSummary> Maps { get; } = [];
    public ObservableCollection<MapOverviewNode> PreviewNodes { get; } = [];
    public ObservableCollection<MapOverviewEdge> PreviewEdges { get; } = [];
    public DelegateCommand RefreshCommand { get; }

    public MapProjectSummary? SelectedMap
    {
        get => _selectedMap;
        set
        {
            if (SetProperty(ref _selectedMap, value) && value is not null) _ = LoadWorkspaceAsync(value.MapId);
        }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) RefreshCommand.RaiseCanExecuteChanged(); }
    }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ActiveMapText { get => _activeMapText; private set => SetProperty(ref _activeMapText, value); }
    public string VersionText { get => _versionText; private set => SetProperty(ref _versionText, value); }
    public string TopologyText { get => _topologyText; private set => SetProperty(ref _topologyText, value); }
    public string ValidationText { get => _validationText; private set => SetProperty(ref _validationText, value); }
    public string BackgroundText { get => _backgroundText; private set => SetProperty(ref _backgroundText, value); }
    public string MapDescription { get => _mapDescription; private set => SetProperty(ref _mapDescription, value); }
    public ImageSource? UnderlayImage { get => _underlayImage; private set => SetProperty(ref _underlayImage, value); }
    public string PreviewTitle { get => _previewTitle; private set => SetProperty(ref _previewTitle, value); }
    public string PreviewHint { get => _previewHint; private set => SetProperty(ref _previewHint, value); }
    public bool ShowPublished { get => _showPublished; set { if (SetProperty(ref _showPublished, value) && _workspace is not null) ApplyWorkspace(_workspace); } }

    public async void OnNavigatedTo(NavigationContext navigationContext) { _refreshTimer.Start(); await LoadAsync(); }
    public bool IsNavigationTarget(NavigationContext navigationContext) => true;
    public void OnNavigatedFrom(NavigationContext navigationContext) => _refreshTimer.Stop();

    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var selectedId = SelectedMap?.MapId;
            var maps = await _client.GetMapsAsync(CancellationToken.None);
            Replace(Maps, maps);
            var selected = Maps.FirstOrDefault(map => map.MapId == selectedId) ??
                           Maps.FirstOrDefault(map => map.IsActive) ?? Maps.FirstOrDefault();
            _selectedMap = selected;
            RaisePropertyChanged(nameof(SelectedMap));
            ActiveMapText = Maps.FirstOrDefault(map => map.IsActive) is { } active
                ? $"{active.Name} · {active.ActiveVersionId ?? "版本未知"}"
                : "未激活运行地图";
            StatusMessage = maps.Count == 0
                ? "当前还没有地图配置，请点击“编辑、更新与发布地图”创建第一张地图。"
                : $"已加载{maps.Count}张地图配置。";
            if (selected is not null) await LoadWorkspaceCoreAsync(selected.MapId);
            else ClearOverview();
        }
        catch (Exception exception)
        {
            StatusMessage = $"地图配置加载失败：{exception.Message}";
            ClearOverview();
        }
        finally { IsBusy = false; }
    }

    private async Task LoadWorkspaceAsync(string mapId)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await LoadWorkspaceCoreAsync(mapId); }
        catch (Exception exception) { StatusMessage = $"地图详情加载失败：{exception.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task LoadWorkspaceCoreAsync(string mapId)
    {
        var workspace = await _client.GetMapWorkspaceAsync(mapId, CancellationToken.None);
        if (workspace is null) { ClearOverview(); return; }
        ApplyWorkspace(workspace);
    }
    private void ApplyWorkspace(MapWorkspaceSnapshot workspace)
    {
        _workspace = workspace;
        var version = ShowPublished ? workspace.Published ?? workspace.Draft : workspace.Draft;
        var definition = version.Definition;
        MapDescription = workspace.Project.Description;
        VersionText = $"草稿 V{workspace.Draft.VersionNumber} / 已发布 {(workspace.Published is null ? "--" : $"V{workspace.Published.VersionNumber}")}";
        TopologyText = $"{definition.Nodes.Count} 节点 · {definition.Edges.Count} 路段 · {definition.Resources.Count} 资源";
        ValidationText = $"{version.Validation.ErrorCount} 错误 · {version.Validation.WarningCount} 警告";
        BackgroundText = string.IsNullOrWhiteSpace(workspace.Project.BackgroundFilePath)
            ? "未配置底图"
            : $"{Path.GetFileName(workspace.Project.BackgroundFilePath)} · {workspace.Project.UnderlayMessage}";
        UnderlayImage = LoadUnderlay(workspace.Project.PreviewPngPath);
        PreviewTitle = $"{workspace.Project.Name} · {(version.Status == MapVersionStatus.Published ? "已发布" : "草稿")} V{version.VersionNumber}";
        PreviewHint = workspace.Project.UnderlayStatus is CadUnderlayJobStatus.Pending or CadUnderlayJobStatus.Processing
            ? "CAD底图正在转换；可以在编辑器继续绘制，转换刷新不会覆盖未保存点线。"
            : definition.Nodes.Count == 0 ? (UnderlayImage is null ? "尚无底图和导航点线。打开编辑器导入CAD或从空白地图绘制。" : "已加载CAD底图；尚未绘制导航节点与路段。")
            : "蓝：节点  绿：站点  紫：路口  黄：单向路段";
        BuildPreview(definition);
        StatusMessage = $"正在查看{PreviewTitle}。{workspace.Project.UnderlayMessage}";
    }
    private static ImageSource? LoadUnderlay(string path)
    {
        if (!File.Exists(path)) return null;
        try { var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(path)); image.EndInit(); image.Freeze(); return image; }
        catch { return null; }
    }

    private void BuildPreview(MapDefinition definition)
    {
        PreviewNodes.Clear(); PreviewEdges.Clear();
        if (definition.Nodes.Count == 0) return;
        var byId = definition.Nodes.GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var curves = new Dictionary<MapEdge, IReadOnlyList<MapPoint2D>>();
        foreach (var edge in definition.Edges)
            if (byId.TryGetValue(edge.From, out var from) && byId.TryGetValue(edge.To, out var to) &&
                MapEdgeGeometry.TryValidate(edge, from, to, out _))
                curves[edge] = MapEdgeGeometry.Sample(edge, from, to, 128);
        var allPoints = definition.Nodes.Select(node => new MapPoint2D(node.X, node.Y))
            .Concat(curves.Values.SelectMany(points => points)).ToArray();
        var minX = allPoints.Min(point => point.X); var maxX = allPoints.Max(point => point.X);
        var minY = allPoints.Min(point => point.Y); var maxY = allPoints.Max(point => point.Y);
        var width = Math.Max(maxX - minX, 1); var height = Math.Max(maxY - minY, 1);
        const double padding = 34; const double canvasWidth = 900; const double canvasHeight = 520;
        var scale = Math.Min((canvasWidth - padding * 2) / width, (canvasHeight - padding * 2) / height);
        Point Project(MapPoint2D point)
        {
            if (UnderlayImage is not null && _workspace?.Project.CadViewBox is { } box)
            {
                var project = _workspace.Project;
                var cx = point.X / project.CoordinateScale; var cy = point.Y / project.CoordinateScale;
                if (project.Calibration is { } calibration)
                {
                    var radians = calibration.RotationDegrees * Math.PI / 180;
                    var dx = (point.X - calibration.TranslationX) / calibration.Scale;
                    var dy = (point.Y - calibration.TranslationY) / calibration.Scale;
                    cx = dx * Math.Cos(radians) + dy * Math.Sin(radians);
                    cy = -dx * Math.Sin(radians) + dy * Math.Cos(radians);
                }
                var imageScale = Math.Min(canvasWidth / box.PixelWidth, canvasHeight / box.PixelHeight);
                var dw = box.PixelWidth * imageScale; var dh = box.PixelHeight * imageScale;
                return new((canvasWidth - dw) / 2 + (cx - box.MinX) / box.Width * dw,
                    (canvasHeight - dh) / 2 + (1 - (cy - box.MinY) / box.Height) * dh);
            }
            return new((canvasWidth - width * scale) / 2 + (point.X - minX) * scale,
                (canvasHeight + height * scale) / 2 - (point.Y - minY) * scale);
        }
        var points = byId.Values.ToDictionary(node => node.Id, node =>
        {
            var point = Project(new(node.X, node.Y));
            return new MapOverviewNode(node.Id, point.X - 6, point.Y - 6,
                node.Type switch
                {
                    MapNodeType.Station => "#FF34C759", MapNodeType.Intersection => "#FFAF7AC5",
                    MapNodeType.WaitingPoint => "#FF40C4FF", MapNodeType.ChargingPoint => "#FFFFC84A",
                    _ => "#FF2FA8FF"
                });
        }, StringComparer.OrdinalIgnoreCase);
        foreach (var (edge, samples) in curves)
        {
            if (definition.Layers.Any(layer => layer.Id == edge.LayerId && !layer.Visible)) continue;
            var canvasPoints = new PointCollection(samples.Select(Project));
            canvasPoints.Freeze();
            PreviewEdges.Add(new MapOverviewEdge(canvasPoints[0].X, canvasPoints[0].Y,
                canvasPoints[^1].X, canvasPoints[^1].Y,
                edge.Bidirectional ? "#FF5C8298" : "#FFFFA726", edge.Enabled ? 3 : 1)
                { Points = canvasPoints });
        }
        var visibleIds = definition.Nodes.Where(node => !definition.Layers.Any(layer => layer.Id == node.LayerId && !layer.Visible)).Select(node => node.Id).ToHashSet();
        foreach (var node in points.Values.Where(node => visibleIds.Contains(node.Id))) PreviewNodes.Add(node);
    }

    private void ClearOverview()
    {
        PreviewNodes.Clear(); PreviewEdges.Clear();
        _workspace = null; UnderlayImage = null; PreviewTitle = "选择地图"; PreviewHint = "尚未创建地图，请打开编辑器新建。";
        MapDescription = "尚未建立地图配置。";
        VersionText = "草稿 -- / 已发布 --";
        TopologyText = "0 节点 · 0 路段 · 0 资源";
        ValidationText = "尚未校验";
        BackgroundText = "未配置底图";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    { target.Clear(); foreach (var item in source) target.Add(item); }
}

public sealed record MapOverviewNode(string Id, double Left, double Top, string Fill);
public sealed record MapOverviewEdge(double X1, double Y1, double X2, double Y2, string Stroke, double Thickness)
{
    public PointCollection Points { get; init; } = [];
}
