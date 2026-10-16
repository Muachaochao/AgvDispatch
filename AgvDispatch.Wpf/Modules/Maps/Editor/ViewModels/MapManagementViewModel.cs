using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Agv;
using AgvDispatch.Domain.Maps;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Regions;

namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class MapManagementViewModel : BindableBase, INavigationAware
{
    private readonly ITrafficControlClient _client;
    private TrafficControlSession? _session;
    private MapProjectSummary? _selectedMap;
    private MapWorkspaceSnapshot? _workspace;
    private bool _isBusy;
    private string _statusMessage = "等待加载地图工作区。";
    private string _mapName = string.Empty;
    private string _description = string.Empty;
    private string _backgroundFilePath = string.Empty;
    private string _coordinateSystem = "LocalCartesian";
    private string _unit = "m";
    private double _coordinateScale = 1;
    private ImageSource? _underlayImage;
    private readonly DispatcherTimer _underlayRefreshTimer;
    private readonly DispatcherTimer _liveAgvRefreshTimer;
    private bool _liveAgvRefreshRunning;
    private SystemStatusSnapshot? _latestSystemStatus;
    private string _liveAgvSummary = "等待实时AGV状态。";
    private readonly DispatcherTimer _trackReplayTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IReadOnlyList<AgvTrackPointSnapshot> _replayTrack = [];
    private int _trackReplayGeneration;
    private int _replayIndex;
    private AgvDiagnostic? _selectedLiveAgv;
    private int _replayMinutes = 30;
    private string _replaySummary = "请选择车辆并加载历史轨迹。";
    private bool _isMapPointMode;
    private bool _isMapLineMode;
    private bool _isResourcePaintMode;
    private bool _snapToGrid = true;
    private bool _snapToExisting = true;
    private double _gridSize = 0.5;
    private double _snapDistance = 0.3;
    private EditableMapNode? _lineStartNode;
    private readonly Stack<MapEditorSnapshot> _undo = new();
    private readonly Stack<MapEditorSnapshot> _redo = new();
    private bool _restoringHistory;
    private MapEditorSnapshot? _pendingGridEdit;
    private string _simulationSource = string.Empty;
    private string _simulationDestination = string.Empty;
    private string _simulationBlockedResources = string.Empty;
    private string _simulationResult = "选择起点和终点后进行只读路径仿真。";
    private string _importPath = string.Empty;
    private MapImportFormat _importFormat = MapImportFormat.Auto;
    private double _importCoordinateScale = 1;
    private string _preflightSummary = "尚未执行全站点路径矩阵检查。";
    private string _versionDiffSummary = "尚未比较草稿与已发布版本。";
    private readonly List<EditableMapNode> _batchSelectedNodes = [];
    private bool _synchronizingNodeSelection;
    private double _batchDeltaX = 1;
    private double _batchDeltaY;
    private string _batchSelectionText = "未选择批量站点";
    private string _exportedBackupPath = string.Empty;
    private bool _isCanvasEditMode;
    private double _canvasZoom = 1;
    private double _canvasPanX;
    private double _canvasPanY;
    private (double X, double Y)? _dragStartMapPoint;
    private Dictionary<EditableMapNode, (double X, double Y)>? _dragStartPositions;
    private bool _dragHistoryRecorded;
    private MapVersionHistoryItem? _selectedHistoryVersion;
    private MapVersionSnapshot? _historyPreview;
    private string _historyPreviewSummary = "请选择历史版本进行预览。";
    private bool _isBoundaryVertexMode;
    private int? _dragBoundaryVertexIndex;
    private bool _boundaryDragHistoryRecorded;
    private string _activationConfirmation = string.Empty;
    private MapNodeType _drawNodeType = MapNodeType.Waypoint;
    private bool _drawEdgeBidirectional = true;
    private double _drawEdgeSpeedLimit = 1;
    private double _canvasWorldWidth = 50;
    private double _canvasWorldHeight = 30;
    private bool _showGrid = true;
    private bool _isMeasureMode;
    private bool _isBoundaryDrawMode;
    private (double X, double Y)? _measureStart;
    private (double X, double Y)? _measureEnd;
    private string _measureText = "测量工具：点击两个位置计算距离和角度。";
    private double _newNodeX;
    private double _newNodeY;
    private readonly List<EditableMapEdge> _batchSelectedEdges = [];
    private bool _synchronizingEdgeSelection;
    private double _batchEdgeSpeedLimit = 1;
    private bool _batchEdgeBidirectional = true;
    private bool _batchEdgeEnabled = true;
    private string _batchEdgeSelectionText = "未选择批量路段";
    private MapEditorSnapshot? _editorClipboard;
    private int _pasteGeneration;
    private string _editorCheckSummary = "实时检查：等待地图数据。";
    private bool _editorHasErrors;

    public MapManagementViewModel(ITrafficControlClient client)
    {
        _client = client;
        InitializeCurveTools();
        InitializeMapSwitchTools();
        RefreshCommand = new DelegateCommand(async () => await LoadMapsAsync(), () => !IsBusy);
        CreateCommand = new DelegateCommand(async () => await CreateAsync(), CanManage);
        SaveCommand = new DelegateCommand(async () => await SaveAsync(), () => CanManage() && _workspace is not null);
        PublishCommand = new DelegateCommand(async () => await PublishAsync(), () => CanUseOnline() && _workspace is not null);
        ActivateCommand = new DelegateCommand(async () => await ActivateAsync(), () => CanUseOnline() && CanActivateMapSafely() && ActivationConfirmation == ExpectedActivationText);
        GenerateUnderlayCommand = new DelegateCommand(async () => await GenerateUnderlayAsync(), () => CanManage() && _workspace is not null);
        SaveCalibrationCommand = new DelegateCommand(async () => await SaveCalibrationAsync(), () => CanManage() && _workspace is not null && CalibrationPoints.Count >= 2);
        AddCalibrationPointCommand = new DelegateCommand(AddCalibrationPoint, () => CanManage() && _workspace is not null);
        RemoveCalibrationPointCommand = new DelegateCommand(RemoveCalibrationPoint, () => CanManage() && SelectedCalibrationPoint is not null && CalibrationPoints.Count > 2);
        AddNodeCommand = CreateMapEditCommand(AddNode, () => CanManage() && _workspace is not null);
        RemoveNodeCommand = CreateMapEditCommand(RemoveSelectedNode, () => CanManage() && SelectedNode is not null);
        AddEdgeCommand = CreateMapEditCommand(AddEdge, () => CanManage() && _workspace is not null && Nodes.Count >= 2);
        RemoveEdgeCommand = CreateMapEditCommand(RemoveSelectedEdge, () => CanManage() && SelectedEdge is not null);
        AddResourceCommand = CreateMapEditCommand(AddResource, () => CanManage() && _workspace is not null);
        RemoveResourceCommand = CreateMapEditCommand(RemoveSelectedResource, () => CanManage() && SelectedResource is not null);
        UndoCommand = new DelegateCommand(Undo, () => CanManage() && _undo.Count > 0);
        RedoCommand = new DelegateCommand(Redo, () => CanManage() && _redo.Count > 0);
        SimulateRouteCommand = new DelegateCommand(SimulateRoute, () => _workspace is not null && Nodes.Count >= 2);
        PreviewImportCommand = new DelegateCommand(async () => await PreviewImportAsync(), () => CanManage() && _workspace is not null && !string.IsNullOrWhiteSpace(ImportPath));
        RunPreflightCommand = new DelegateCommand(async () => await RunPreflightAsync(), () => !IsBusy && _workspace is not null && Nodes.Count >= 2);
        CompareVersionsCommand = new DelegateCommand(CompareVersions, () => _workspace?.Published is not null);
        MoveSelectedNodesCommand = CreateMapEditCommand(MoveSelectedNodes, CanBatchEditNodes);
        DuplicateSelectedNodesCommand = CreateMapEditCommand(DuplicateSelectedNodes, CanBatchEditNodes);
        RemoveSelectedNodesCommand = CreateMapEditCommand(RemoveSelectedNodes, CanBatchEditNodes);
        SplitSelectedEdgeCommand = CreateMapEditCommand(SplitSelectedEdge, () => CanManage() && SelectedEdge is not null);
        ReverseSelectedEdgeCommand = CreateMapEditCommand(ReverseSelectedEdge, () => CanManage() && SelectedEdge is not null && !SelectedEdge.Bidirectional);
        ExportDraftCommand = new DelegateCommand(async () => await ExportMapAsync(MapExportVersion.Draft), () => CanManage() && _workspace is not null);
        ExportPublishedCommand = new DelegateCommand(async () => await ExportMapAsync(MapExportVersion.Published), () => CanManage() && _workspace?.Published is not null);
        ResetCanvasViewCommand = new DelegateCommand(ResetCanvasView);
        RefreshMapAuditsCommand = new DelegateCommand(async () => await LoadMapAuditsAsync(), () => IsOnlineEditing && _workspace is not null);
        RefreshVersionHistoryCommand = new DelegateCommand(async () => await LoadVersionHistoryAsync(), () => IsOnlineEditing && _workspace is not null);
        PreviewHistoryVersionCommand = new DelegateCommand(async () => await PreviewHistoryVersionAsync(), () => IsOnlineEditing && SelectedHistoryVersion is not null);
        RestoreHistoryToEditorCommand = CreateMapEditCommand(RestoreHistoryToEditor, () => CanManage() && _historyPreview is not null);
        MergeSelectedNodesCommand = CreateMapEditCommand(MergeSelectedNodes, () => CanManage() && _batchSelectedNodes.Count == 2);
        GenerateResourceBoundaryCommand = CreateMapEditCommand(GenerateResourceBoundary, () => CanManage() && SelectedResource is not null && _batchSelectedNodes.Count >= 3);
        SelectAllNodesCommand = new DelegateCommand(SelectAllNodes, () => CanManage() && Nodes.Count > 0);
        ApplyNodeTypeCommand = CreateMapEditCommand(ApplyNodeType, CanBatchEditNodes);
        AlignLeftCommand = CreateMapEditCommand(() => AlignSelectedNodes(NodeAlignment.Left), CanAlignNodes);
        AlignRightCommand = CreateMapEditCommand(() => AlignSelectedNodes(NodeAlignment.Right), CanAlignNodes);
        AlignTopCommand = CreateMapEditCommand(() => AlignSelectedNodes(NodeAlignment.Top), CanAlignNodes);
        AlignBottomCommand = CreateMapEditCommand(() => AlignSelectedNodes(NodeAlignment.Bottom), CanAlignNodes);
        AlignHorizontalCenterCommand = CreateMapEditCommand(() => AlignSelectedNodes(NodeAlignment.HorizontalCenter), CanAlignNodes);
        AlignVerticalCenterCommand = CreateMapEditCommand(() => AlignSelectedNodes(NodeAlignment.VerticalCenter), CanAlignNodes);
        DistributeHorizontalCommand = CreateMapEditCommand(() => DistributeSelectedNodes(true), CanDistributeNodes);
        DistributeVerticalCommand = CreateMapEditCommand(() => DistributeSelectedNodes(false), CanDistributeNodes);
        RotateClockwiseCommand = CreateMapEditCommand(() => RotateSelectedNodes(-90), CanAlignNodes);
        RotateCounterClockwiseCommand = CreateMapEditCommand(() => RotateSelectedNodes(90), CanAlignNodes);
        MirrorHorizontalCommand = CreateMapEditCommand(() => MirrorSelectedNodes(false), CanAlignNodes);
        MirrorVerticalCommand = CreateMapEditCommand(() => MirrorSelectedNodes(true), CanAlignNodes);
        AutoConnectSelectedCommand = CreateMapEditCommand(AutoConnectSelectedNodes, CanAlignNodes);
        OrthogonalConnectCommand = CreateMapEditCommand(OrthogonalConnectSelectedNodes, () => CanManage() && _batchSelectedNodes.Count == 2);
        CloseLoopCommand = CreateMapEditCommand(CloseSelectedLoop, () => CanManage() && _batchSelectedNodes.Count >= 3);
        CleanupTopologyCommand = CreateMapEditCommand(CleanupTopology, () => CanManage() && Edges.Count > 0);
        AddPreciseNodeCommand = CreateMapEditCommand(AddPreciseNode, () => CanManage() && _workspace is not null);
        RemoveLastBoundaryVertexCommand = CreateMapEditCommand(RemoveLastBoundaryVertex, CanEditBoundaryVertices);
        ClearBoundaryCommand = CreateMapEditCommand(ClearBoundary, CanEditBoundaryVertices);
        CompleteBoundaryCommand = CreateMapEditCommand(CompleteBoundary, () => CanEditBoundaryVertices() && SelectedResource!.ParseBoundary().Count >= 3);
        ApplyBatchEdgePropertiesCommand = CreateMapEditCommand(ApplyBatchEdgeProperties, () => CanManage() && _batchSelectedEdges.Count > 0);
        RemoveSelectedEdgesCommand = CreateMapEditCommand(RemoveSelectedEdges, () => CanManage() && _batchSelectedEdges.Count > 0);
        CopySelectionCommand = new DelegateCommand(CopySelection, () => _batchSelectedNodes.Count > 0);
        PasteSelectionCommand = CreateMapEditCommand(PasteSelection, () => CanManage() && _editorClipboard is { Nodes.Count: > 0 });
        DeleteSelectionCommand = CreateMapEditCommand(DeleteSelection, () => CanManage() && (_batchSelectedNodes.Count > 0 || _batchSelectedEdges.Count > 0));
        SelectAllEdgesCommand = new DelegateCommand(() => UpdateBatchEdgeSelection(Edges.Where(edge => ShowEdgeLayer && IsMapLayerVisible(edge.LayerId))), () => CanManage() && Edges.Count > 0);
        LoadTrackReplayCommand = new DelegateCommand(async () => await LoadTrackReplayAsync(), () => SelectedLiveAgv is not null && _workspace is not null);
        PlayTrackReplayCommand = new DelegateCommand(PlayTrackReplay, () => _replayTrack.Count > 1);
        PauseTrackReplayCommand = new DelegateCommand(PauseTrackReplay, () => _trackReplayTimer.IsEnabled);
        ClearTrackReplayCommand = new DelegateCommand(ClearTrackReplay, () => _replayTrack.Count > 0);
        _underlayRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _underlayRefreshTimer.Tick += async (_, _) => await RefreshUnderlayStateAsync();
        _liveAgvRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _liveAgvRefreshTimer.Tick += async (_, _) => await RefreshLiveAgvsAsync();
        _trackReplayTimer.Tick += (_, _) => AdvanceTrackReplay();
        InitializeWorkbenchTools();
        InitializeMapLayers();
        InitializeCadDetail();
        InitializeOfflineTools();
        InitializeTopologyTools();
    }

    public ObservableCollection<MapProjectSummary> Maps { get; } = [];
    public ObservableCollection<EditableMapNode> Nodes { get; } = [];
    public ObservableCollection<EditableMapEdge> Edges { get; } = [];
    public ObservableCollection<EditableTrafficResource> Resources { get; } = [];
    public ObservableCollection<MapCanvasNode> PreviewNodes { get; } = [];
    public ObservableCollection<MapCanvasEdge> PreviewEdges { get; } = [];
    public ObservableCollection<MapValidationIssue> ValidationIssues { get; } = [];
    public ObservableCollection<EditableCadLayer> CadLayers { get; } = [];
    public ObservableCollection<EditableCalibrationPoint> CalibrationPoints { get; } = [];
    public ObservableCollection<RouteSimulationStep> SimulationSteps { get; } = [];
    public ObservableCollection<MapRouteMatrixEntry> PreflightEntries { get; } = [];
    public ObservableCollection<MapDifference> VersionDifferences { get; } = [];
    public ObservableCollection<MapOperationAuditSnapshot> MapAudits { get; } = [];
    public ObservableCollection<MapCanvasResourceBoundary> PreviewResourceBoundaries { get; } = [];
    public ObservableCollection<MapCanvasResourceVertex> PreviewResourceVertices { get; } = [];
    public ObservableCollection<MapCanvasAgv> PreviewAgvs { get; } = [];
    public ObservableCollection<AgvDiagnostic> LiveAgvs { get; } = [];
    public ObservableCollection<MapCanvasTrackSegment> PreviewTrackSegments { get; } = [];
    public ObservableCollection<MapCanvasReplayMarker> PreviewReplayMarkers { get; } = [];
    public ObservableCollection<MapCanvasGridLine> PreviewGridLines { get; } = [];
    public ObservableCollection<MapCanvasMeasureSegment> PreviewMeasurements { get; } = [];
    public ObservableCollection<MapCanvasMeasureSegment> PreviewBoundarySegments { get; } = [];
    public ObservableCollection<MapVersionHistoryItem> VersionHistory { get; } = [];
    public ObservableCollection<MapDifference> HistoryPreviewDifferences { get; } = [];
    public IReadOnlyList<MapNodeType> NodeTypes { get; } = Enum.GetValues<MapNodeType>();
    public IReadOnlyList<TrafficResourceType> ResourceTypes { get; } = Enum.GetValues<TrafficResourceType>();
    public IReadOnlyList<MapImportFormat> ImportFormats { get; } = Enum.GetValues<MapImportFormat>();

    public DelegateCommand RefreshCommand { get; }
    public DelegateCommand CreateCommand { get; }
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand PublishCommand { get; }
    public DelegateCommand ActivateCommand { get; }
    public DelegateCommand GenerateUnderlayCommand { get; }
    public DelegateCommand SaveCalibrationCommand { get; }
    public DelegateCommand AddCalibrationPointCommand { get; }
    public DelegateCommand RemoveCalibrationPointCommand { get; }
    public DelegateCommand AddNodeCommand { get; }
    public DelegateCommand RemoveNodeCommand { get; }
    public DelegateCommand AddEdgeCommand { get; }
    public DelegateCommand RemoveEdgeCommand { get; }
    public DelegateCommand AddResourceCommand { get; }
    public DelegateCommand RemoveResourceCommand { get; }
    public DelegateCommand UndoCommand { get; }
    public DelegateCommand RedoCommand { get; }
    public DelegateCommand SimulateRouteCommand { get; }
    public DelegateCommand PreviewImportCommand { get; }
    public DelegateCommand RunPreflightCommand { get; }
    public DelegateCommand CompareVersionsCommand { get; }
    public DelegateCommand MoveSelectedNodesCommand { get; }
    public DelegateCommand DuplicateSelectedNodesCommand { get; }
    public DelegateCommand RemoveSelectedNodesCommand { get; }
    public DelegateCommand SplitSelectedEdgeCommand { get; }
    public DelegateCommand ReverseSelectedEdgeCommand { get; }
    public DelegateCommand ExportDraftCommand { get; }
    public DelegateCommand ExportPublishedCommand { get; }
    public DelegateCommand ResetCanvasViewCommand { get; }
    public DelegateCommand RefreshMapAuditsCommand { get; }
    public DelegateCommand RefreshVersionHistoryCommand { get; }
    public DelegateCommand PreviewHistoryVersionCommand { get; }
    public DelegateCommand RestoreHistoryToEditorCommand { get; }
    public DelegateCommand MergeSelectedNodesCommand { get; }
    public DelegateCommand GenerateResourceBoundaryCommand { get; }
    public DelegateCommand SelectAllNodesCommand { get; }
    public DelegateCommand ApplyNodeTypeCommand { get; }
    public DelegateCommand AlignLeftCommand { get; }
    public DelegateCommand AlignRightCommand { get; }
    public DelegateCommand AlignTopCommand { get; }
    public DelegateCommand AlignBottomCommand { get; }
    public DelegateCommand AlignHorizontalCenterCommand { get; }
    public DelegateCommand AlignVerticalCenterCommand { get; }
    public DelegateCommand DistributeHorizontalCommand { get; }
    public DelegateCommand DistributeVerticalCommand { get; }
    public DelegateCommand RotateClockwiseCommand { get; }
    public DelegateCommand RotateCounterClockwiseCommand { get; }
    public DelegateCommand MirrorHorizontalCommand { get; }
    public DelegateCommand MirrorVerticalCommand { get; }
    public DelegateCommand AutoConnectSelectedCommand { get; }
    public DelegateCommand OrthogonalConnectCommand { get; }
    public DelegateCommand CloseLoopCommand { get; }
    public DelegateCommand CleanupTopologyCommand { get; }
    public DelegateCommand AddPreciseNodeCommand { get; }
    public DelegateCommand RemoveLastBoundaryVertexCommand { get; }
    public DelegateCommand ClearBoundaryCommand { get; }
    public DelegateCommand CompleteBoundaryCommand { get; }
    public DelegateCommand ApplyBatchEdgePropertiesCommand { get; }
    public DelegateCommand RemoveSelectedEdgesCommand { get; }
    public DelegateCommand CopySelectionCommand { get; }
    public DelegateCommand PasteSelectionCommand { get; }
    public DelegateCommand DeleteSelectionCommand { get; }
    public DelegateCommand SelectAllEdgesCommand { get; }
    public DelegateCommand LoadTrackReplayCommand { get; }
    public DelegateCommand PlayTrackReplayCommand { get; }
    public DelegateCommand PauseTrackReplayCommand { get; }
    public DelegateCommand ClearTrackReplayCommand { get; }

    public MapProjectSummary? SelectedMap
    {
        get => _selectedMap;
        set
        {
            if (SetProperty(ref _selectedMap, value) && value is not null)
            {
                _ = LoadWorkspaceAsync(value.MapId);
            }
        }
    }

    private EditableMapNode? _selectedNode;
    public EditableMapNode? SelectedNode
    {
        get => _selectedNode;
        set { if (SetProperty(ref _selectedNode, value)) RemoveNodeCommand.RaiseCanExecuteChanged(); }
    }
    private EditableMapEdge? _selectedEdge;
    public EditableMapEdge? SelectedEdge
    {
        get => _selectedEdge;
        set
        {
            if (!SetProperty(ref _selectedEdge, value)) return;
            RemoveEdgeCommand.RaiseCanExecuteChanged();
            ApplyEdgeGeometryCommand.RaiseCanExecuteChanged();
            SplitSelectedEdgeCommand.RaiseCanExecuteChanged();
            ReverseSelectedEdgeCommand.RaiseCanExecuteChanged();
            RebuildPreview();
        }
    }
    private EditableTrafficResource? _selectedResource;
    public EditableTrafficResource? SelectedResource
    {
        get => _selectedResource;
        set
        {
            if (!SetProperty(ref _selectedResource, value)) return;
            RemoveResourceCommand.RaiseCanExecuteChanged();
            GenerateResourceBoundaryCommand.RaiseCanExecuteChanged();
            RaiseBoundaryCommands();
            RebuildPreview();
        }
    }
    private EditableCalibrationPoint? _selectedCalibrationPoint;
    public EditableCalibrationPoint? SelectedCalibrationPoint
    {
        get => _selectedCalibrationPoint;
        set { if (SetProperty(ref _selectedCalibrationPoint, value)) RemoveCalibrationPointCommand.RaiseCanExecuteChanged(); }
    }

    public string MapName
    {
        get => _mapName;
        set
        {
            if (!SetProperty(ref _mapName, value)) return;
            RaisePropertyChanged(nameof(ExpectedActivationText));
            ActivateCommand.RaiseCanExecuteChanged();
        }
    }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public string BackgroundFilePath { get => _backgroundFilePath; set => SetProperty(ref _backgroundFilePath, value); }
    public string CoordinateSystem { get => _coordinateSystem; set => SetProperty(ref _coordinateSystem, value); }
    public string Unit { get => _unit; set => SetProperty(ref _unit, value); }
    public double CoordinateScale { get => _coordinateScale; set => SetProperty(ref _coordinateScale, value); }
    public string VersionText => _workspace is null
        ? "未加载版本"
        : IsOfflineEditing ? "本地草稿 · 未上传"
        : $"草稿 V{_workspace.Draft.VersionNumber} · 已发布 {(_workspace.Published is null ? "无" : $"V{_workspace.Published.VersionNumber}")} · {(_workspace.Project.IsActive ? $"运行版本…{_workspace.Project.ActiveVersionId?[^6..]}" : "未激活")}";
    public string BackgroundFormat => _workspace?.Project.BackgroundFormat ?? "待创建";
    public ImageSource? UnderlayImage { get => _underlayImage; private set { if (SetProperty(ref _underlayImage, value)) ScheduleCadDetail(); } }
    public string UnderlayStatusText => _workspace is null
        ? "尚未生成CAD底图"
        : $"{_workspace.Project.UnderlayStatus} · {_workspace.Project.UnderlayMessage}";
    public string CalibrationText => _workspace?.Project.Calibration is not { } value
        ? "尚未进行坐标标定"
        : $"比例 {value.Scale:G8} · 旋转 {value.RotationDegrees:F4}° · RMS {value.RootMeanSquareError:G6}";
    public bool IsMapPointMode
    {
        get => _isMapPointMode;
        set
        {
            if (!SetProperty(ref _isMapPointMode, value)) return;
            if (value)
            {
                IsCurveControlMode = false;
                IsMapLineMode = false;
                IsResourcePaintMode = false;
                IsCanvasEditMode = false;
                IsBoundaryVertexMode = false;
                IsMeasureMode = false;
                IsBoundaryDrawMode = false;
            }
        }
    }
    public bool IsMapLineMode
    {
        get => _isMapLineMode;
        set
        {
            if (!SetProperty(ref _isMapLineMode, value)) return;
            _lineStartNode = null;
            if (value)
            {
                IsCurveControlMode = false;
                IsMapPointMode = false;
                IsResourcePaintMode = false;
                IsCanvasEditMode = false;
                IsBoundaryVertexMode = false;
                IsMeasureMode = false;
                IsBoundaryDrawMode = false;
            }
        }
    }
    public bool IsResourcePaintMode
    {
        get => _isResourcePaintMode;
        set
        {
            if (!SetProperty(ref _isResourcePaintMode, value)) return;
            if (value)
            {
                IsCurveControlMode = false;
                IsMapPointMode = false;
                IsMapLineMode = false;
                IsCanvasEditMode = false;
                IsBoundaryVertexMode = false;
                IsMeasureMode = false;
                IsBoundaryDrawMode = false;
            }
        }
    }
    public bool IsCanvasEditMode
    {
        get => _isCanvasEditMode;
        set
        {
            if (!SetProperty(ref _isCanvasEditMode, value)) return;
            if (value)
            {
                IsCurveControlMode = false;
                IsMapPointMode = false;
                IsMapLineMode = false;
                IsResourcePaintMode = false;
                IsBoundaryVertexMode = false;
                IsMeasureMode = false;
                IsBoundaryDrawMode = false;
            }
            else
            {
                EndNodeDrag();
            }
        }
    }
    public bool IsBoundaryVertexMode
    {
        get => _isBoundaryVertexMode;
        set
        {
            if (!SetProperty(ref _isBoundaryVertexMode, value)) return;
            EndBoundaryVertexDrag();
            if (value)
            {
                IsCurveControlMode = false;
                IsMapPointMode = false;
                IsMapLineMode = false;
                IsResourcePaintMode = false;
                IsCanvasEditMode = false;
                IsMeasureMode = false;
                IsBoundaryDrawMode = false;
            }
            RebuildPreview();
        }
    }
    public bool IsMeasureMode
    {
        get => _isMeasureMode;
        set
        {
            if (!SetProperty(ref _isMeasureMode, value)) return;
            _measureStart = null;
            _measureEnd = null;
            PreviewMeasurements.Clear();
            MeasureText = value ? "测量工具：点击起点。" : "测量工具未启用。";
            if (value)
            {
                IsCurveControlMode = false;
                IsMapPointMode = false; IsMapLineMode = false; IsResourcePaintMode = false;
                IsCanvasEditMode = false; IsBoundaryVertexMode = false; IsBoundaryDrawMode = false;
            }
        }
    }
    public bool IsBoundaryDrawMode
    {
        get => _isBoundaryDrawMode;
        set
        {
            if (!SetProperty(ref _isBoundaryDrawMode, value)) return;
            if (value)
            {
                IsCurveControlMode = false;
                IsMapPointMode = false; IsMapLineMode = false; IsResourcePaintMode = false;
                IsCanvasEditMode = false; IsBoundaryVertexMode = false; IsMeasureMode = false;
            }
            RebuildPreview();
            RaiseBoundaryCommands();
        }
    }
    public bool SnapToGrid { get => _snapToGrid; set => SetProperty(ref _snapToGrid, value); }
    public bool SnapToExisting { get => _snapToExisting; set => SetProperty(ref _snapToExisting, value); }
    public double GridSize
    {
        get => _gridSize;
        set
        {
            var valid = double.IsFinite(value) ? Math.Max(0.001, value) : 0.5;
            if (SetProperty(ref _gridSize, valid)) RebuildPreview();
        }
    }
    public double SnapDistance { get => _snapDistance; set => SetProperty(ref _snapDistance, Math.Max(0.001, value)); }
    public MapNodeType DrawNodeType { get => _drawNodeType; set => SetProperty(ref _drawNodeType, value); }
    public bool DrawEdgeBidirectional { get => _drawEdgeBidirectional; set => SetProperty(ref _drawEdgeBidirectional, value); }
    public double DrawEdgeSpeedLimit
    {
        get => _drawEdgeSpeedLimit;
        set => SetProperty(ref _drawEdgeSpeedLimit, double.IsFinite(value) ? Math.Max(0.01, value) : 1);
    }
    public double CanvasWorldWidth
    {
        get => _canvasWorldWidth;
        set { if (SetProperty(ref _canvasWorldWidth, double.IsFinite(value) ? Math.Max(1, value) : 50)) RebuildPreview(); }
    }
    public double CanvasWorldHeight
    {
        get => _canvasWorldHeight;
        set { if (SetProperty(ref _canvasWorldHeight, double.IsFinite(value) ? Math.Max(1, value) : 30)) RebuildPreview(); }
    }
    public bool ShowGrid { get => _showGrid; set { if (SetProperty(ref _showGrid, value)) RebuildPreview(); } }
    public string MeasureText { get => _measureText; private set => SetProperty(ref _measureText, value); }
    public double NewNodeX { get => _newNodeX; set => SetProperty(ref _newNodeX, value); }
    public double NewNodeY { get => _newNodeY; set => SetProperty(ref _newNodeY, value); }
    public double BatchEdgeSpeedLimit { get => _batchEdgeSpeedLimit; set => SetProperty(ref _batchEdgeSpeedLimit, double.IsFinite(value) ? Math.Max(0.01, value) : 1); }
    public bool BatchEdgeBidirectional { get => _batchEdgeBidirectional; set => SetProperty(ref _batchEdgeBidirectional, value); }
    public bool BatchEdgeEnabled { get => _batchEdgeEnabled; set => SetProperty(ref _batchEdgeEnabled, value); }
    public string BatchEdgeSelectionText { get => _batchEdgeSelectionText; private set => SetProperty(ref _batchEdgeSelectionText, value); }
    public string EditorCheckSummary { get => _editorCheckSummary; private set => SetProperty(ref _editorCheckSummary, value); }
    public bool EditorHasErrors { get => _editorHasErrors; private set => SetProperty(ref _editorHasErrors, value); }
    public string SimulationSource { get => _simulationSource; set => SetProperty(ref _simulationSource, value); }
    public string SimulationDestination { get => _simulationDestination; set => SetProperty(ref _simulationDestination, value); }
    public string SimulationBlockedResources { get => _simulationBlockedResources; set => SetProperty(ref _simulationBlockedResources, value); }
    public string SimulationResult { get => _simulationResult; private set => SetProperty(ref _simulationResult, value); }
    public string ImportPath
    {
        get => _importPath;
        set
        {
            if (SetProperty(ref _importPath, value)) PreviewImportCommand.RaiseCanExecuteChanged();
        }
    }
    public MapImportFormat ImportFormat { get => _importFormat; set => SetProperty(ref _importFormat, value); }
    public double ImportCoordinateScale { get => _importCoordinateScale; set => SetProperty(ref _importCoordinateScale, value); }
    public string PreflightSummary { get => _preflightSummary; private set => SetProperty(ref _preflightSummary, value); }
    public string VersionDiffSummary { get => _versionDiffSummary; private set => SetProperty(ref _versionDiffSummary, value); }
    public double BatchDeltaX { get => _batchDeltaX; set => SetProperty(ref _batchDeltaX, value); }
    public double BatchDeltaY { get => _batchDeltaY; set => SetProperty(ref _batchDeltaY, value); }
    public string BatchSelectionText { get => _batchSelectionText; private set => SetProperty(ref _batchSelectionText, value); }
    public string ExportedBackupPath { get => _exportedBackupPath; private set => SetProperty(ref _exportedBackupPath, value); }
    public double CanvasZoom
    {
        get => _canvasZoom;
        private set
        {
            if (SetProperty(ref _canvasZoom, value)) { RaisePropertyChanged(nameof(CanvasViewText)); ScheduleCadDetail(); ApplyDisplayReadability(); }
        }
    }
    public double CanvasPanX { get => _canvasPanX; private set { if (SetProperty(ref _canvasPanX, value)) ScheduleCadDetail(); } }
    public double CanvasPanY { get => _canvasPanY; private set { if (SetProperty(ref _canvasPanY, value)) ScheduleCadDetail(); } }
    public string CanvasViewText => $"{CanvasZoom:P0}";
    public string LiveAgvSummary { get => _liveAgvSummary; private set => SetProperty(ref _liveAgvSummary, value); }
    public AgvDiagnostic? SelectedLiveAgv
    {
        get => _selectedLiveAgv;
        set
        {
            var changed=_selectedLiveAgv?.VehicleId!=value?.VehicleId;
            if (SetProperty(ref _selectedLiveAgv, value)) LoadTrackReplayCommand.RaiseCanExecuteChanged();
            if(changed)ClearTrackReplay();
        }
    }
    public int ReplayMinutes { get => _replayMinutes; set => SetProperty(ref _replayMinutes, Math.Clamp(value, 1, 1440)); }
    public string ReplaySummary { get => _replaySummary; private set => SetProperty(ref _replaySummary, value); }
    public MapVersionHistoryItem? SelectedHistoryVersion
    {
        get => _selectedHistoryVersion;
        set
        {
            if (!SetProperty(ref _selectedHistoryVersion, value)) return;
            _historyPreview = null;
            HistoryPreviewDifferences.Clear();
            HistoryPreviewSummary = value is null ? "请选择历史版本进行预览。" : $"已选择V{value.VersionNumber}，点击“预览版本”。";
            PreviewHistoryVersionCommand.RaiseCanExecuteChanged();
            RestoreHistoryToEditorCommand.RaiseCanExecuteChanged();
        }
    }
    public string HistoryPreviewSummary { get => _historyPreviewSummary; private set => SetProperty(ref _historyPreviewSummary, value); }
    public string ActivationConfirmation
    {
        get => _activationConfirmation;
        set { if (SetProperty(ref _activationConfirmation, value)) ActivateCommand.RaiseCanExecuteChanged(); }
    }
    public string ExpectedActivationText => $"ACTIVATE {MapName}";
    public string CurrentUser => _session is null ? "未连接" : $"{_session.DisplayName} · {_session.CurrentPermissionText()}";

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) RaiseCommands(); }
    }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public async void OnNavigatedTo(NavigationContext navigationContext)
    {
        await ActivateEditorAsync();
    }
    public bool IsNavigationTarget(NavigationContext navigationContext) => true;
    public void OnNavigatedFrom(NavigationContext navigationContext)
    {
        DeactivateEditor();
    }

    public async Task ActivateEditorAsync(string? initialMapId = null)
    {
        _cadDetailActive = true;
        if (IsOfflineEditing) { await LoadMapsAsync(); return; }
        _underlayRefreshTimer.Start();
        _liveAgvRefreshTimer.Start();
        await LoadMapsAsync(initialMapId);
        await RefreshLiveAgvsAsync();
    }

    public void DeactivateEditor()
    {
        _trackReplayGeneration++;
        _cadDetailActive = false;
        ScheduleCadDetail();
        _underlayRefreshTimer.Stop();
        _liveAgvRefreshTimer.Stop();
        _trackReplayTimer.Stop();
    }

    private async Task LoadTrackReplayAsync()
    {
        if (IsOfflineEditing) return;
        if (SelectedLiveAgv is null || _workspace is null) return;
        ClearTrackReplay();
        var generation=_trackReplayGeneration;
        var vehicle=SelectedLiveAgv;
        var mapId=_workspace.Project.MapId;
        var version=_workspace.Project.ActiveVersionId;
        if(string.IsNullOrWhiteSpace(version)||_workspace.Published?.VersionId!=version)
        {ReplaySummary="当前工作区没有与运行版本一致的已发布地图，无法加载轨迹。";return;}
        try
        {
            var to = DateTimeOffset.Now;
            var query=new AgvTrackQueryRequest(
                vehicle.VehicleId,
                mapId,
                to.AddMinutes(-ReplayMinutes),
                to,
                10000,version);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var points=await _client.GetAgvTrackAsync(query,timeout.Token).WaitAsync(timeout.Token);
            if(generation!=_trackReplayGeneration||SelectedLiveAgv?.VehicleId!=vehicle.VehicleId||_workspace?.Project.MapId!=mapId||_workspace.Project.ActiveVersionId!=version)return;
            var checkedTrack=AgvTrackReplayValidator.Validate(points,query);
            if(!checkedTrack.Available){ReplaySummary=checkedTrack.Reason;return;}
            _replayTrack=checkedTrack.Points;
            _replayIndex = 0;
            RebuildTrackPreview();
            ReplaySummary = _replayTrack.Count == 0
                ? $"{vehicle.DisplayName}在地图版本{version}最近{ReplayMinutes}分钟没有轨迹记录。"
                : $"已加载{vehicle.DisplayName}版本{version}轨迹{_replayTrack.Count}点（达到10000点上限时可能截断），" +
                  $"{_replayTrack[0].SampledAt.LocalDateTime:HH:mm:ss}—{_replayTrack[^1].SampledAt.LocalDateTime:HH:mm:ss}。";
        }
        catch (Exception exception)
        {
            if(generation!=_trackReplayGeneration)return;
            ReplaySummary = $"轨迹加载失败：{exception.Message}";
            _replayTrack = [];
            RebuildTrackPreview();
        }
        RaiseTrackCommands();
    }

    private void PlayTrackReplay()
    {
        if (_replayTrack.Count < 2) return;
        if (_replayIndex >= _replayTrack.Count - 1) _replayIndex = 0;
        _trackReplayTimer.Start();
        RaiseTrackCommands();
    }

    private void PauseTrackReplay()
    {
        _trackReplayTimer.Stop();
        RaiseTrackCommands();
    }

    private void ClearTrackReplay()
    {
        _trackReplayGeneration++;
        PauseTrackReplay();
        _replayTrack = [];
        _replayIndex = 0;
        PreviewTrackSegments.Clear();
        PreviewReplayMarkers.Clear();
        ReplaySummary = "历史轨迹已清除。";
        RaiseTrackCommands();
    }

    private void AdvanceTrackReplay()
    {
        if (_replayTrack.Count < 2)
        {
            PauseTrackReplay();
            return;
        }
        _replayIndex++;
        if (_replayIndex >= _replayTrack.Count)
        {
            _replayIndex = _replayTrack.Count - 1;
            PauseTrackReplay();
        }
        RebuildTrackMarker();
        var point = _replayTrack[_replayIndex];
        ReplaySummary = $"回放 {_replayIndex + 1}/{_replayTrack.Count} · {point.SampledAt.LocalDateTime:yyyy-MM-dd HH:mm:ss} · " +
                        $"{point.PositionState} · 偏差{point.DistanceToRouteMeters?.ToString("F2") ?? "--"}m";
    }

    private void RaiseTrackCommands()
    {
        LoadTrackReplayCommand.RaiseCanExecuteChanged();
        PlayTrackReplayCommand.RaiseCanExecuteChanged();
        PauseTrackReplayCommand.RaiseCanExecuteChanged();
        ClearTrackReplayCommand.RaiseCanExecuteChanged();
    }

    private async Task RefreshLiveAgvsAsync()
    {
        if (IsOfflineEditing) return;
        if (_liveAgvRefreshRunning) return;
        _liveAgvRefreshRunning = true;
        var modeRevision = _editorModeRevision;
        try
        {
            var status = await _client.GetSystemStatusAsync(CancellationToken.None);
            if (IsOfflineEditing || modeRevision != _editorModeRevision) return;
            _latestSystemStatus = status;
            await RefreshMapSwitchAsync();
            if (IsOfflineEditing || modeRevision != _editorModeRevision) return;
            Replace(LiveAgvs, _latestSystemStatus.Agvs);
            RebuildAgvPreview();
        }
        catch (Exception exception)
        {
            if (IsOnlineEditing && modeRevision == _editorModeRevision) LiveAgvSummary = $"实时AGV状态读取失败：{exception.Message}";
        }
        finally
        {
            _liveAgvRefreshRunning = false;
        }
    }

    private async Task LoadMapsAsync(string? initialMapId = null)
    {
        if (IsBusy) return;
        if (IsOfflineEditing)
        {
            IsBusy = true;
            try
            {
                if (_workspace is not null) await LocalStore.SaveAsync(CaptureLocal());
                await LoadLocalListAsync(initialMapId);
            }
            catch (Exception exception) { StatusMessage = $"本地地图读取失败：{exception.Message}"; }
            finally { IsBusy = false; }
            return;
        }
        string? workspaceId = null;
        IsBusy = true;
        try
        {
            _session = await _client.GetSessionAsync(CancellationToken.None);
            RaisePropertyChanged(nameof(CurrentUser));
            var selectedId = initialMapId ?? SelectedMap?.MapId;
            var maps = await _client.GetMapsAsync(CancellationToken.None);
            Replace(Maps, maps);
            _selectedMap = Maps.FirstOrDefault(item => item.MapId == selectedId) ?? Maps.FirstOrDefault();
            RaisePropertyChanged(nameof(SelectedMap));
            workspaceId = _selectedMap?.MapId;
            StatusMessage = maps.Count == 0
                ? "尚未创建地图。点击“新建地图”，填写名称；可选CAD，也可从空白地图开始。"
                : $"已加载{maps.Count}张地图。";
        }
        catch (Exception exception) { _session = null; StatusMessage = $"地图列表加载失败：{exception.Message} 请启动调度服务后点“连接 / 刷新”。"; }
        finally { IsBusy = false; }
        if (workspaceId is not null) await LoadWorkspaceAsync(workspaceId);
        await RefreshMapSwitchAsync();
    }

    private async Task LoadWorkspaceAsync(string mapId)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (IsOfflineEditing && _workspace is not null && _workspace.Project.MapId != mapId) await LocalStore.SaveAsync(CaptureLocal());
            var workspace = IsOfflineEditing ? await LocalStore.LoadAsync(mapId) : await _client.GetMapWorkspaceAsync(mapId, CancellationToken.None);
            if (workspace is null) { StatusMessage = "地图工作区不存在。"; return; }
            ApplyWorkspace(workspace);
            StatusMessage = $"已打开{workspace.Project.Name}草稿V{workspace.Draft.VersionNumber}。";
        }
        catch (Exception exception) { StatusMessage = $"地图工作区加载失败：{exception.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task CreateAsync()
        => await CreateProjectAsync(new CreateMapRequest(MapName, Description, BackgroundFilePath, CoordinateSystem, Unit, CoordinateScale));

    public async Task<bool> CreateProjectAsync(CreateMapRequest request)
    {
        if (IsBusy) { StatusMessage = "正在处理地图请求，请稍后重试。"; return false; }
        if (string.IsNullOrWhiteSpace(request.Name)) { StatusMessage = "请填写地图名称。"; return false; }
        if (IsOfflineEditing)
        {
            IsBusy = true;
            try
            {
                if (_workspace is not null) await LocalStore.SaveAsync(CaptureLocal());
                var local = LocalStore.Create(request); await LocalStore.SaveAsync(local); ApplyWorkspace(local);
                await LoadLocalListAsync(local.Project.MapId); StatusMessage = "本地地图已创建，无需后台服务。"; return true;
            }
            catch (Exception exception) { StatusMessage = $"本地地图创建失败：{exception.Message}"; return false; }
            finally { IsBusy = false; }
        }
        IsBusy = true;
        try
        {
            _session = await _client.GetSessionAsync(CancellationToken.None);
            RaisePropertyChanged(nameof(CurrentUser));
            if (!_session.Permissions.HasFlag(TrafficPermission.ManageMap) && !_session.Permissions.HasFlag(TrafficPermission.Administer))
            { StatusMessage = "当前账号没有地图管理权限，请使用具备地图管理权限的账号。"; return false; }
            var result = await _client.CreateMapAsync(request, CancellationToken.None);
            StatusMessage = result.Message;
            if (result.Accepted && result.Workspace is not null)
            {
                ApplyWorkspace(result.Workspace);
                await ReloadMapListOnlyAsync(result.Workspace.Project.MapId);
            }
            return result.Accepted;
        }
        catch (Exception exception) { _session = null; StatusMessage = $"创建地图失败：{exception.Message} 请检查调度服务是否启动，再点击创建重试。"; return false; }
        finally { IsBusy = false; }
    }

    private async Task<bool> SaveAsync()
    {
        if (IsOfflineEditing) return await SaveOfflineAsync();
        if (!CanManage() || IsBusy || _workspace is null) return false;
        RefreshEditorCheck();
        if (EditorHasErrors)
        {
            StatusMessage = $"保存已阻止：{EditorCheckSummary} 请先修正红色结构错误。";
            return false;
        }
        IsBusy = true;
        try
        {
            var definition = BuildDefinition();
            var result = await _client.SaveMapDraftAsync(new SaveMapDraftRequest(
                _workspace.Project.MapId, _workspace.Draft.VersionId, MapName, Description,
                BackgroundFilePath, CoordinateSystem, Unit, CoordinateScale, definition,
                _workspace.Draft.EditToken),
                CancellationToken.None);
            StatusMessage = result.Message;
            if (result.Accepted && result.Workspace is not null)
            {
                ApplyWorkspace(result.Workspace);
                await ReloadMapListOnlyAsync(result.Workspace.Project.MapId);
                return true;
            }
            return false;
        }
        catch (Exception exception) { StatusMessage = $"保存地图失败：{exception.Message}"; return false; }
        finally { IsBusy = false; }
    }

    private async Task PublishAsync()
    {
        if (IsOfflineEditing) return;
        if (_workspace is null || !await SaveAsync()) return;
        var workspace = _workspace;
        if (workspace is null) return;
        IsBusy = true;
        try
        {
            var result = await _client.PublishMapAsync(
                new PublishMapRequest(workspace.Project.MapId, workspace.Draft.VersionId),
                CancellationToken.None);
            StatusMessage = result.Message;
            if (result.Accepted && result.Workspace is not null)
            {
                ApplyWorkspace(result.Workspace);
                await ReloadMapListOnlyAsync(result.Workspace.Project.MapId);
            }
        }
        catch (Exception exception) { StatusMessage = $"发布地图失败：{exception.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task GenerateUnderlayAsync()
    {
        if (_workspace is null || IsBusy) return;
        if (IsOfflineEditing)
        {
            IsBusy = true;
            try
            {
                var sameSource = string.Equals(BackgroundFilePath, _workspace.Project.BackgroundFilePath, StringComparison.OrdinalIgnoreCase);
                var snapshot = CaptureLocal();
                StatusMessage = "正在本地生成CAD底图，不需要调度服务……";
                var result = await LocalStore.RenderAsync(snapshot, new(snapshot.Project.MapId, BackgroundFilePath,
                    sameSource ? CadLayers.Where(l => l.Included).Select(l => l.Name).ToArray() : [],
                    UnderlayQualityIndex == 0 ? 2200 : UnderlayQualityIndex == 1 ? 4096 : 6144,
                    UnderlayQualityIndex == 0 ? 1400 : UnderlayQualityIndex == 1 ? 2608 : 3909));
                ApplyWorkspace(result); StatusMessage = result.Project.UnderlayMessage;
            }
            catch (Exception exception) { StatusMessage = $"本地底图生成失败：{exception.Message}"; }
            finally { IsBusy = false; }
            return;
        }
        var selectedLayers = string.Equals(BackgroundFilePath.Trim(), _workspace.Project.BackgroundFilePath,
            StringComparison.OrdinalIgnoreCase) ? CadLayers.Where(item => item.Included).Select(item => item.Name).ToArray() : [];
        if (!await SaveAsync()) return;
        IsBusy = true;
        try
        {
            var result = await _client.GenerateMapUnderlayAsync(
                new GenerateMapUnderlayRequest(_workspace.Project.MapId, BackgroundFilePath, selectedLayers,
                    UnderlayQualityIndex == 0 ? 2200 : UnderlayQualityIndex == 1 ? 4096 : 6144,
                    UnderlayQualityIndex == 0 ? 1400 : UnderlayQualityIndex == 1 ? 2608 : 3909),
                CancellationToken.None);
            StatusMessage = result.Message;
            if (result.Workspace is not null) ApplyWorkspace(result.Workspace);
        }
        catch (Exception exception) { StatusMessage = $"CAD底图任务提交失败：{exception.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task ActivateAsync()
    {
        if (IsOfflineEditing) return;
        if (_workspace?.Published is not { } published || IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _client.ActivateMapAsync(
                new ActivateMapRequest(_workspace.Project.MapId, published.VersionId, ActivationConfirmation,
                    SiteSafetyConfirmed, VehicleMapCompatibilityConfirmed, MapSwitchEvidence),
                CancellationToken.None);
            StatusMessage = result.Message;
            if (result.Workspace is not null)
            {
                ActivationConfirmation = string.Empty;
                OriginalWorkflowConfirmed = false;
                SiteSafetyConfirmed = false;
                VehicleMapCompatibilityConfirmed = false;
                MapSwitchEvidence = "";
                ApplyWorkspace(result.Workspace);
                await ReloadMapListOnlyAsync(result.Workspace.Project.MapId);
            }
        }
        catch (Exception exception) { StatusMessage = $"激活地图失败：{exception.Message}"; }
        finally { IsBusy = false; await RefreshMapSwitchAsync(); }
    }

    private async Task PreviewImportAsync()
    {
        if (_workspace is null || IsBusy || string.IsNullOrWhiteSpace(ImportPath)) return;
        if (MapLayers.Any(layer => layer.IsLocked))
        { StatusMessage = "当前地图包含锁定图层，请先解除锁定再替换拓扑。"; return; }
        IsBusy = true;
        try
        {
            var result = IsOfflineEditing ? await Task.Run(() =>
            {
                var definition = LocalStore.Import(new(ImportPath.Trim(), ImportFormat, ImportCoordinateScale));
                return new MapImportPreviewResult(true, "本地拓扑已导入编辑区，尚未保存。", definition, LocalStore.Validate(definition));
            }) : await _client.PreviewMapImportAsync(
                new PreviewMapImportRequest(ImportPath.Trim(), ImportFormat, ImportCoordinateScale),
                CancellationToken.None);
            StatusMessage = result.Message;
            if (!result.Accepted || result.Definition is null || result.Validation is null) return;
            RecordHistory();
            LoadEditable(Nodes, result.Definition.Nodes.Select(EditableMapNode.From));
            LoadEditable(Edges, result.Definition.Edges.Select(EditableMapEdge.FromDomain));
            LoadEditable(Resources, result.Definition.Resources.Select(EditableTrafficResource.From));
        LoadMapLayers(result.Definition.Layers);
        LoadEditorSettings(result.Definition.EditorSettings);
            Replace(ValidationIssues, result.Validation.Issues);
            SelectedNode = Nodes.FirstOrDefault();
            SelectedEdge = null;
            SelectedResource = null;
            SimulationSource = Nodes.FirstOrDefault(item => item.Enabled)?.Id ?? string.Empty;
            SimulationDestination = Nodes.LastOrDefault(item => item.Enabled)?.Id ?? string.Empty;
            RebuildPreview();
            RaiseCommands();
        }
        catch (Exception exception) { StatusMessage = $"地图导入预览失败：{exception.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task ExportMapAsync(MapExportVersion version)
    {
        if (_workspace is null || IsBusy) return;
        if (IsOfflineEditing)
        {
            if (version == MapExportVersion.Draft) SaveLocalAsCommand.Execute();
            return;
        }
        IsBusy = true;
        try
        {
            var result = await _client.ExportMapAsync(
                new ExportMapRequest(_workspace.Project.MapId, version), CancellationToken.None);
            StatusMessage = result.Accepted
                ? $"{result.Message} 文件{result.SizeBytes:N0}字节，SHA-256={result.Sha256}"
                : result.Message;
            if (result.Accepted) ExportedBackupPath = result.FilePath;
        }
        catch (Exception exception) { StatusMessage = $"地图导出失败：{exception.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task LoadMapAuditsAsync()
    {
        if (IsOfflineEditing) return;
        if (_workspace is null) return;
        var mapId = _workspace.Project.MapId; var revision = _editorModeRevision;
        try
        {
            var audits = await _client.GetMapAuditsAsync(
                _workspace.Project.MapId, 300, CancellationToken.None);
            if (IsOfflineEditing || revision != _editorModeRevision || _workspace?.Project.MapId != mapId) return;
            Replace(MapAudits, audits);
        }
        catch (Exception exception)
        {
            StatusMessage = $"地图审计记录加载失败：{exception.Message}";
        }
    }

    private async Task LoadVersionHistoryAsync()
    {
        if (IsOfflineEditing) return;
        if (_workspace is null) return;
        var mapId = _workspace.Project.MapId; var revision = _editorModeRevision;
        try
        {
            var selectedId = SelectedHistoryVersion?.VersionId;
            var versions = await _client.GetMapVersionHistoryAsync(
                _workspace.Project.MapId, CancellationToken.None);
            if (IsOfflineEditing || revision != _editorModeRevision || _workspace?.Project.MapId != mapId) return;
            Replace(VersionHistory, versions);
            SelectedHistoryVersion = VersionHistory.FirstOrDefault(item => item.VersionId == selectedId) ??
                                     VersionHistory.FirstOrDefault();
        }
        catch (Exception exception)
        {
            StatusMessage = $"地图版本历史加载失败：{exception.Message}";
        }
    }

    private async Task PreviewHistoryVersionAsync()
    {
        if (IsOfflineEditing) return;
        if (_workspace is null || SelectedHistoryVersion is null) return;
        try
        {
            _historyPreview = await _client.GetMapVersionAsync(
                _workspace.Project.MapId, SelectedHistoryVersion.VersionId, CancellationToken.None);
            HistoryPreviewDifferences.Clear();
            if (_historyPreview is null)
            {
                HistoryPreviewSummary = "历史版本不存在或已被清理。";
                return;
            }
            var differences = MapVersionDiffer.Compare(_historyPreview.Definition, BuildDefinition());
            Replace(HistoryPreviewDifferences, differences);
            HistoryPreviewSummary = $"V{_historyPreview.VersionNumber} {_historyPreview.Status}：节点{_historyPreview.Definition.Nodes.Count}、" +
                                    $"路段{_historyPreview.Definition.Edges.Count}、资源{_historyPreview.Definition.Resources.Count}；" +
                                    $"当前编辑区相对该版本有{differences.Count}项变化。";
            RestoreHistoryToEditorCommand.RaiseCanExecuteChanged();
        }
        catch (Exception exception)
        {
            HistoryPreviewSummary = $"历史版本预览失败：{exception.Message}";
        }
    }

    private void RestoreHistoryToEditor()
    {
        if (_historyPreview is null || !CanManage()) return;
        RecordHistory();
        LoadEditable(Nodes, _historyPreview.Definition.Nodes.Select(EditableMapNode.From));
        LoadEditable(Edges, _historyPreview.Definition.Edges.Select(EditableMapEdge.FromDomain));
        LoadEditable(Resources, _historyPreview.Definition.Resources.Select(EditableTrafficResource.From));
        LoadMapLayers(_historyPreview.Definition.Layers);
        LoadEditorSettings(_historyPreview.Definition.EditorSettings);
        Replace(ValidationIssues, _historyPreview.Validation.Issues);
        UpdateBatchNodeSelection([]);
        SelectedNode = Nodes.FirstOrDefault();
        SelectedEdge = null;
        SelectedResource = null;
        RebuildPreview();
        StatusMessage = $"历史V{_historyPreview.VersionNumber}已加载到可撤销编辑区，数据库尚未改变；确认后请保存草稿。";
        CompareVersions();
        RaiseCommands();
    }

    private async Task SaveCalibrationAsync()
    {
        if (_workspace is null || IsBusy || CalibrationPoints.Count < 2) return;
        if (IsOfflineEditing)
        {
            IsBusy = true;
            try
            {
                var local = CaptureLocal(); var calibration = LocalStore.Calibrate(CalibrationPoints.Select(p => p.ToDomain()).ToArray());
                local = local with { Project = local.Project with { Calibration = calibration } };
                await LocalStore.SaveAsync(local); ApplyWorkspace(local); StatusMessage = "本地标定已保存。";
            }
            catch (Exception exception) { StatusMessage = $"本地标定失败：{exception.Message}"; }
            finally { IsBusy = false; }
            return;
        }
        IsBusy = true;
        try
        {
            var result = await _client.SaveMapCalibrationAsync(
                new SaveMapCalibrationRequest(
                    _workspace.Project.MapId,
                    CalibrationPoints.Select(item => item.ToDomain()).ToArray()),
                CancellationToken.None);
            StatusMessage = result.Message;
            if (result.Workspace is not null) ApplyWorkspace(result.Workspace);
        }
        catch (Exception exception) { StatusMessage = $"坐标标定失败：{exception.Message}"; }
        finally { IsBusy = false; }
    }

    public async Task RefreshUnderlayStateAsync()
    {
        if (IsOfflineEditing) return;
        if (_workspace is null || IsBusy ||
            _workspace.Project.UnderlayStatus is not (CadUnderlayJobStatus.Pending or CadUnderlayJobStatus.Processing)) return;
        try
        {
            var mapId = _workspace.Project.MapId;
            var refreshed = await _client.GetMapWorkspaceAsync(mapId, CancellationToken.None);
            if (refreshed is null || _workspace?.Project.MapId != mapId) return;
            // Background conversion must never replace unsaved geometry, history or active tools.
            _workspace = _workspace with { Project = refreshed.Project };
            LoadEditable(CadLayers, refreshed.Project.CadLayers.Select(EditableCadLayer.From));
            UnderlayImage = LoadImage(refreshed.Project.PreviewPngPath);
            LoadDisplayImages(refreshed.Project);
            RebuildPreview();
            RaisePropertyChanged(nameof(UnderlayStatusText));
            RaisePropertyChanged(nameof(BackgroundFormat));
            RaisePropertyChanged(nameof(CalibrationText));
            StatusMessage = refreshed.Project.UnderlayMessage;
        }
        catch (Exception exception) { StatusMessage = $"底图状态刷新失败：{exception.Message}"; }
    }

    private async Task ReloadMapListOnlyAsync(string selectedId)
    {
        var maps = await _client.GetMapsAsync(CancellationToken.None);
        Replace(Maps, maps);
        _selectedMap = Maps.FirstOrDefault(item => item.MapId == selectedId);
        RaisePropertyChanged(nameof(SelectedMap));
    }

    private void ApplyWorkspace(MapWorkspaceSnapshot workspace)
    {
        ScheduleCadDetail();
        var mapChanged = _workspace?.Project.MapId != workspace.Project.MapId;
        if (_workspace is not null&&(mapChanged||_workspace.Project.ActiveVersionId!=workspace.Project.ActiveVersionId)) ClearTrackReplay();
        _workspace = workspace;
        RaisePropertyChanged(nameof(IsSmap3DPlanePreview));
        MapName = workspace.Project.Name;
        if (mapChanged) ActivationConfirmation = string.Empty;
        Description = workspace.Project.Description;
        BackgroundFilePath = workspace.Project.BackgroundFilePath;
        CoordinateSystem = workspace.Project.CoordinateSystem;
        Unit = workspace.Project.Unit;
        CoordinateScale = workspace.Project.CoordinateScale;
        LoadEditable(Nodes, workspace.Draft.Definition.Nodes.Select(EditableMapNode.From));
        LoadEditable(Edges, workspace.Draft.Definition.Edges.Select(EditableMapEdge.FromDomain));
        LoadEditable(Resources, workspace.Draft.Definition.Resources.Select(EditableTrafficResource.From));
        LoadMapLayers(workspace.Draft.Definition.Layers);
        LoadEditorSettings(workspace.Draft.Definition.EditorSettings);
        UpdateBatchNodeSelection([]);
        UpdateBatchEdgeSelection([]);
        Replace(ValidationIssues, workspace.Draft.Validation.Issues);
        LoadEditable(CadLayers, workspace.Project.CadLayers.Select(EditableCadLayer.From));
        LoadCalibration(workspace.Project.Calibration);
        _undo.Clear();
        _redo.Clear();
        _lineStartNode = null;
        var enabledNodes = Nodes.Where(item => item.Enabled).ToArray();
        if (!enabledNodes.Any(item => item.Id.Equals(SimulationSource, StringComparison.OrdinalIgnoreCase)))
            SimulationSource = enabledNodes.FirstOrDefault()?.Id ?? string.Empty;
        if (!enabledNodes.Any(item => item.Id.Equals(SimulationDestination, StringComparison.OrdinalIgnoreCase)))
            SimulationDestination = enabledNodes.LastOrDefault()?.Id ?? string.Empty;
        SimulationSteps.Clear();
        SimulationResult = "草稿已变化，请重新执行路径仿真。";
        PreflightEntries.Clear();
        PreflightSummary = "草稿已变化，请重新执行全站点路径矩阵检查。";
        CompareVersions();
        _ = LoadMapAuditsAsync();
        _ = LoadVersionHistoryAsync();
        UnderlayImage = LoadImage(workspace.Project.PreviewPngPath);
        LoadDisplayImages(workspace.Project);
        RebuildPreview();
        RaisePropertyChanged(nameof(VersionText));
        RaisePropertyChanged(nameof(BackgroundFormat));
        RaisePropertyChanged(nameof(UnderlayStatusText));
        RaisePropertyChanged(nameof(CalibrationText));
        RaisePropertyChanged(nameof(ExpectedActivationText));
        RaiseCommands();
    }

    private MapDefinition BuildDefinition() => new(
        MapName, BackgroundFilePath, CoordinateSystem, Unit,
        Nodes.Select(item => item.ToDomain()).ToArray(),
        Edges.Select(item => item.ToDomain(Nodes)).ToArray(),
        Resources.Select(item => item.ToDomain()).ToArray())
        { Layers = MapLayers.Select(layer => layer.ToDomain()).ToArray(), EditorSettings = new(CanvasWorldWidth, CanvasWorldHeight, GridSize, ShowGrid, SnapToGrid, SnapToExisting) { CurrentLayerId = CurrentLayer?.Id, SnapToAlignment = SnapToAlignment, SnapToGeometry = SnapToGeometry, Display = CaptureDisplay() } };

    private void AddNode()
    {
        var index = NextIndex(Nodes.Select(item => item.Id), "N");
        AddNodeAt(index * 2, 0);
    }

    private void AddNodeAt(double x, double y)
    {
        RecordHistory();
        AddNodeAtCore(x, y);
    }

    private void AddPreciseNode()
    {
        if (!CanManage() || _workspace is null || !double.IsFinite(NewNodeX) || !double.IsFinite(NewNodeY))
        {
            StatusMessage = "精确建点坐标必须是有效数字。";
            return;
        }
        var x = SnapCoordinate(NewNodeX);
        var y = SnapCoordinate(NewNodeY);
        AddNodeAt(x, y);
        StatusMessage = $"已精确新增{DrawNodeType}节点，地图坐标({x:F3}, {y:F3})。";
    }

    private EditableMapNode AddNodeAtCore(double x, double y)
    {
        var index = NextIndex(Nodes.Select(item => item.Id), "N");
        var node = new EditableMapNode { LayerId = DrawingLayerId, Id = $"N{index:000}", X = x, Y = y, Type = DrawNodeType };
        Attach(node);
        Nodes.Add(node);
        SelectedNode = node;
        RebuildPreview();
        AddEdgeCommand.RaiseCanExecuteChanged();
        SelectAllNodesCommand.RaiseCanExecuteChanged();
        return node;
    }

    public void HandleMapClick(double canvasX, double canvasY, double canvasWidth, double canvasHeight)
    {
        if (IsMeasureMode) HandleMapClickCore(canvasX, canvasY, canvasWidth, canvasHeight);
        else RunMapEdit(() => HandleMapClickCore(canvasX, canvasY, canvasWidth, canvasHeight));
    }

    private void HandleMapClickCore(double canvasX, double canvasY, double canvasWidth, double canvasHeight)
    {
        if ((!IsMapPointMode && !IsMapLineMode && !IsResourcePaintMode && !IsMeasureMode && !IsBoundaryDrawMode) ||
            canvasWidth <= 0 || canvasHeight <= 0) return;
        if (!TryCanvasToMap(canvasX, canvasY, canvasWidth, canvasHeight, out var mapped))
        {
            StatusMessage = "点击位置不在当前地图画布范围内。";
            return;
        }
        var x = SnapToGrid ? Math.Round(mapped.X / GridSize) * GridSize : mapped.X;
        var y = SnapToGrid ? Math.Round(mapped.Y / GridSize) * GridSize : mapped.Y;

        if (IsMeasureMode)
        {
            HandleMeasureClick(x, y);
            return;
        }
        if (!CanManage()) return;
        if (IsBoundaryDrawMode)
        {
            AddBoundaryVertex(x, y);
            return;
        }
        var nearest = IsMapPointMode || IsMapLineMode ? ResolvePlacementSnap(ref x, ref y, true) : null;

        if (IsResourcePaintMode)
        {
            ToggleResourceBinding(x, y);
            return;
        }

        if (IsMapPointMode)
        {
            if (nearest is not null)
            {
                SelectedNode = nearest;
                StatusMessage = $"已吸附到已有站点{nearest.Id}，未创建重复点。";
                return;
            }
            AddNodeAt(x, y);
            StatusMessage = $"已新增{DrawNodeType}节点，地图坐标({x:F3}, {y:F3})。";
            return;
        }

        var target = nearest;
        if (target is null)
        {
            RecordHistory();
            target = AddNodeAtCore(x, y);
        }
        if (_lineStartNode is null)
        {
            _lineStartNode = target;
            SelectedNode = target;
            StatusMessage = $"连续连线起点：{target.Id}。请点击下一站点。";
            return;
        }
        if (ReferenceEquals(_lineStartNode, target) || _lineStartNode.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "连线终点不能与起点相同。";
            return;
        }
        if (nearest is not null) RecordHistory();
        var edge = AddEdgeBetween(_lineStartNode, target);
        _lineStartNode = target;
        StatusMessage = $"已新增路段{edge.Id}，连续连线起点已移到{target.Id}。";
    }

    private void HandleMeasureClick(double x, double y)
    {
        if (_measureStart is null || _measureEnd is not null)
        {
            _measureStart = (x, y);
            _measureEnd = null;
            MeasureText = $"测量起点：({x:F3}, {y:F3})，请点击终点。";
        }
        else
        {
            _measureEnd = (x, y);
            var start = _measureStart.Value;
            var dx = x - start.X;
            var dy = y - start.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            MeasureText = $"距离 {distance:F3} m · 方位角 {angle:F1}° · ΔX {dx:F3} · ΔY {dy:F3}";
        }
        RebuildMeasurementPreview();
    }

    public EditableMapNode? FindNodeAtCanvas(double canvasX, double canvasY, double radius = 18)
    {
        if (!ShowNodeLayer) return null;
        var preview = PreviewNodes.Where(node => node.CanSelect)
            .Select(item => new
            {
                Item = item,
                Distance = Math.Sqrt(Math.Pow(item.Left + 7 - canvasX, 2) + Math.Pow(item.Top + 7 - canvasY, 2))
            })
            .Where(item => item.Distance <= radius / Math.Max(CanvasZoom, 0.1))
            .OrderBy(item => item.Distance)
            .FirstOrDefault()?.Item;
        return preview is null
            ? null
            : Nodes.FirstOrDefault(node => node.Id.Equals(preview.Id, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<EditableMapNode> FindNodesInCanvasRectangle(
        double left, double top, double right, double bottom)
    {
        if (!ShowNodeLayer) return [];
        var minimumX = Math.Min(left, right);
        var maximumX = Math.Max(left, right);
        var minimumY = Math.Min(top, bottom);
        var maximumY = Math.Max(top, bottom);
        var ids = PreviewNodes.Where(item => item.CanSelect).Where(item =>
                item.Left + 7 >= minimumX && item.Left + 7 <= maximumX &&
                item.Top + 7 >= minimumY && item.Top + 7 <= maximumY)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Nodes.Where(node => ids.Contains(node.Id)).ToArray();
    }

    public EditableMapEdge? FindEdgeAtCanvas(double canvasX, double canvasY, double tolerance = 10)
    {
        if (!ShowEdgeLayer) return null;
        var hit = PreviewEdges.Where(edge => edge.CanSelect)
            .Select(edge => new { Edge = edge, Distance = edge.Points.Zip(edge.Points.Skip(1), (a, b) => DistanceToSegment(canvasX, canvasY, a.X, a.Y, b.X, b.Y)).DefaultIfEmpty(double.PositiveInfinity).Min() })
            .Where(item => item.Distance <= tolerance / Math.Max(CanvasZoom, 0.1))
            .OrderBy(item => item.Distance)
            .FirstOrDefault()?.Edge;
        return hit is null ? null : Edges.FirstOrDefault(edge => edge.Id.Equals(hit.Id, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<EditableMapEdge> FindEdgesInCanvasRectangle(
        double left, double top, double right, double bottom)
    {
        if (!ShowEdgeLayer) return [];
        var minimumX = Math.Min(left, right);
        var maximumX = Math.Max(left, right);
        var minimumY = Math.Min(top, bottom);
        var maximumY = Math.Max(top, bottom);
        var ids = PreviewEdges.Where(edge => edge.CanSelect).Where(edge => edge.Points.Zip(edge.Points.Skip(1), (a, b) =>
                SegmentIntersectsRectangle(a.X, a.Y, b.X, b.Y, minimumX, minimumY, maximumX, maximumY)).Any(hit => hit))
            .Select(edge => edge.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Edges.Where(edge => ids.Contains(edge.Id)).ToArray();
    }

    private static bool SegmentIntersectsRectangle(
        double x1, double y1, double x2, double y2,
        double left, double top, double right, double bottom)
    {
        static bool Inside(double x, double y, double l, double t, double r, double b) =>
            x >= l && x <= r && y >= t && y <= b;
        if (Inside(x1, y1, left, top, right, bottom) || Inside(x2, y2, left, top, right, bottom)) return true;
        return SegmentsIntersect(x1, y1, x2, y2, left, top, right, top) ||
               SegmentsIntersect(x1, y1, x2, y2, right, top, right, bottom) ||
               SegmentsIntersect(x1, y1, x2, y2, right, bottom, left, bottom) ||
               SegmentsIntersect(x1, y1, x2, y2, left, bottom, left, top);
    }

    private static bool SegmentsIntersect(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        if (Math.Max(ax, bx) < Math.Min(cx, dx) || Math.Max(cx, dx) < Math.Min(ax, bx) ||
            Math.Max(ay, by) < Math.Min(cy, dy) || Math.Max(cy, dy) < Math.Min(ay, by)) return false;
        static double Cross(double px, double py, double qx, double qy, double rx, double ry) =>
            (qx - px) * (ry - py) - (qy - py) * (rx - px);
        var c1 = Cross(ax, ay, bx, by, cx, cy);
        var c2 = Cross(ax, ay, bx, by, dx, dy);
        var c3 = Cross(cx, cy, dx, dy, ax, ay);
        var c4 = Cross(cx, cy, dx, dy, bx, by);
        return ((c1 >= 0 && c2 <= 0) || (c1 <= 0 && c2 >= 0)) &&
               ((c3 >= 0 && c4 <= 0) || (c3 <= 0 && c4 >= 0));
    }

    public bool BeginNodeDrag(
        IEnumerable<EditableMapNode> nodes,
        double canvasX,
        double canvasY,
        double canvasWidth,
        double canvasHeight)
    {
        if (!IsCanvasEditMode || !CanManage() ||
            !TryCanvasToMap(canvasX, canvasY, canvasWidth, canvasHeight, out var mapPoint)) return false;
        var selected = nodes.Where(Nodes.Contains).Distinct().ToArray();
        if (selected.Any(IsNodeProtected)) return false;
        if (selected.Length == 0) return false;
        _dragStartMapPoint = mapPoint;
        _dragStartPositions = selected.ToDictionary(node => node, node => (node.X, node.Y));
        _dragHistoryRecorded = false;
        return true;
    }

    public void DragSelectedNodes(
        double canvasX,
        double canvasY,
        double canvasWidth,
        double canvasHeight)
    {
        if (_dragStartMapPoint is not { } start || _dragStartPositions is null ||
            !TryCanvasToMap(canvasX, canvasY, canvasWidth, canvasHeight, out var current)) return;
        var deltaX = current.X - start.X;
        var deltaY = current.Y - start.Y;
        if (SnapToAlignment)
        {
            var anchor = _dragStartPositions.First();
            var anchorX = anchor.Value.X + deltaX;
            var anchorY = anchor.Value.Y + deltaY;
            if (SnapToGrid) { anchorX = Math.Round(anchorX / GridSize) * GridSize; anchorY = Math.Round(anchorY / GridSize) * GridSize; }
            var excluded = _dragStartPositions.Keys.ToHashSet();
            ResolvePlacementSnap(ref anchorX, ref anchorY, true, excluded);
            deltaX = anchorX - anchor.Value.X; deltaY = anchorY - anchor.Value.Y;
            PreviewSnapGuides.Clear(); UpdateSnapGuides(anchorX, anchorY, excluded);
        }
        if (!_dragHistoryRecorded && Math.Sqrt(deltaX * deltaX + deltaY * deltaY) < 0.001) return;
        if (!_dragHistoryRecorded)
        {
            RecordHistory();
            _dragHistoryRecorded = true;
        }
        foreach (var pair in _dragStartPositions)
        {
            var x = pair.Value.X + deltaX;
            var y = pair.Value.Y + deltaY;
            pair.Key.X = SnapToGrid && !SnapToAlignment ? Math.Round(x / GridSize) * GridSize : x;
            pair.Key.Y = SnapToGrid && !SnapToAlignment ? Math.Round(y / GridSize) * GridSize : y;
        }
        RebuildPreview();
    }

    public void EndNodeDrag()
    {
        PreviewSnapGuides.Clear();
        if (_dragHistoryRecorded && _dragStartPositions is not null)
            StatusMessage = $"已拖动{_dragStartPositions.Count}个站点，可使用Ctrl+Z撤销。";
        _dragStartMapPoint = null;
        _dragStartPositions = null;
        _dragHistoryRecorded = false;
    }

    public void AdjustCanvasZoom(int wheelDelta)
    {
        if (wheelDelta == 0) return;
        CanvasZoom = Math.Clamp(CanvasZoom * (wheelDelta > 0 ? 1.15 : 1 / 1.15), 0.5, 5);
    }

    public void AdjustCanvasZoom(int wheelDelta, double anchorX, double anchorY, double canvasWidth, double canvasHeight)
    {
        if (!double.IsFinite(anchorX) || !double.IsFinite(anchorY) ||
            !double.IsFinite(canvasWidth) || !double.IsFinite(canvasHeight) || canvasWidth <= 0 || canvasHeight <= 0) return;
        var oldZoom = CanvasZoom;
        AdjustCanvasZoom(wheelDelta);
        // The canvas scales around its centre, then translates, inside the Viewbox.
        // Compensate in canvas units so the point under the pointer stays fixed.
        CanvasPanX += (anchorX - canvasWidth / 2) * (oldZoom - CanvasZoom);
        CanvasPanY += (anchorY - canvasHeight / 2) * (oldZoom - CanvasZoom);
    }

    public void PanCanvas(double deltaX, double deltaY)
    {
        CanvasPanX += deltaX;
        CanvasPanY += deltaY;
    }

    private void ResetCanvasView()
    {
        CanvasZoom = 1;
        CanvasPanX = 0;
        CanvasPanY = 0;
    }

    private bool TryCanvasToMap(
        double canvasX,
        double canvasY,
        double canvasWidth,
        double canvasHeight,
        out (double X, double Y) mapPoint)
    {
        mapPoint = default;
        if (canvasWidth <= 0 || canvasHeight <= 0) return false;
        if (_workspace?.Project.CadViewBox is not { } viewBox)
        {
            mapPoint = (
                Math.Clamp(canvasX / canvasWidth, 0, 1) * CanvasWorldWidth,
                (1 - Math.Clamp(canvasY / canvasHeight, 0, 1)) * CanvasWorldHeight);
            return true;
        }
        var scale = Math.Min(canvasWidth / viewBox.PixelWidth, canvasHeight / viewBox.PixelHeight);
        var displayWidth = viewBox.PixelWidth * scale;
        var displayHeight = viewBox.PixelHeight * scale;
        var offsetX = (canvasWidth - displayWidth) / 2;
        var offsetY = (canvasHeight - displayHeight) / 2;
        if (canvasX < offsetX || canvasX > offsetX + displayWidth ||
            canvasY < offsetY || canvasY > offsetY + displayHeight) return false;
        var cadX = viewBox.MinX + (canvasX - offsetX) / displayWidth * viewBox.Width;
        var cadY = viewBox.MinY + (1 - (canvasY - offsetY) / displayHeight) * viewBox.Height;
        mapPoint = _workspace.Project.Calibration?.Transform(cadX, cadY) ??
                   (cadX * CoordinateScale, cadY * CoordinateScale);
        return true;
    }

    private void ToggleResourceBinding(double x, double y)
    {
        if (SelectedResource is null)
        {
            StatusMessage = "请先在“交通资源”页选中要涂刷的资源。";
            return;
        }

        var byId = Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var closestEdge = Edges
            .Where(edge => ShowEdgeLayer && IsMapLayerVisible(edge.LayerId) && byId.ContainsKey(edge.From) && byId.ContainsKey(edge.To))
            .Select(edge => new
            {
                Edge = edge,
                Distance = MapEdgeGeometry.TryValidate(edge.ToDomain(Nodes), byId[edge.From].ToDomain(), byId[edge.To].ToDomain(), out _)
                    ? MapEdgeGeometry.DistanceTo(edge.ToDomain(Nodes), byId[edge.From].ToDomain(), byId[edge.To].ToDomain(), x, y)
                    : double.PositiveInfinity
            })
            .OrderBy(item => item.Distance)
            .FirstOrDefault();
        var closestNode = Nodes.Where(node => ShowNodeLayer && IsMapLayerVisible(node.LayerId))
            .Select(node => new { Node = node, Distance = Math.Sqrt(Math.Pow(node.X - x, 2) + Math.Pow(node.Y - y, 2)) })
            .OrderBy(item => item.Distance)
            .FirstOrDefault();
        var tolerance = Math.Max(SnapDistance, GridSize * 0.75);
        if (closestEdge is not null && closestEdge.Distance <= tolerance &&
            (closestNode is null || closestEdge.Distance <= closestNode.Distance))
        {
            RecordHistory();
            closestEdge.Edge.ResourceIds = ToggleListValue(closestEdge.Edge.ResourceIds, SelectedResource.Id, out var added);
            SelectedEdge = closestEdge.Edge;
            StatusMessage = $"已{(added ? "绑定" : "取消绑定")}资源{SelectedResource.Id}{(added ? "到" : "与")}路段{closestEdge.Edge.Id}。";
            return;
        }
        if (closestNode is not null && closestNode.Distance <= tolerance)
        {
            RecordHistory();
            closestNode.Node.ResourceIds = ToggleListValue(closestNode.Node.ResourceIds, SelectedResource.Id, out var added);
            SelectedNode = closestNode.Node;
            StatusMessage = $"已{(added ? "绑定" : "取消绑定")}资源{SelectedResource.Id}{(added ? "到" : "与")}节点{closestNode.Node.Id}。";
            return;
        }
        StatusMessage = $"点击处{tolerance:F2}米范围内没有可绑定的节点或路段。";
    }

    private static string ToggleListValue(string current, string value, out bool added)
    {
        var values = EditableMapNode.Split(current).ToList();
        var index = values.FindIndex(item => item.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            values.RemoveAt(index);
            added = false;
        }
        else
        {
            values.Add(value);
            added = true;
        }
        return string.Join(';', values);
    }

    private static double DistanceToSegment(
        double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var squaredLength = dx * dx + dy * dy;
        if (squaredLength <= double.Epsilon)
            return Math.Sqrt(Math.Pow(px - ax, 2) + Math.Pow(py - ay, 2));
        var t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / squaredLength, 0, 1);
        var nearestX = ax + t * dx;
        var nearestY = ay + t * dy;
        return Math.Sqrt(Math.Pow(px - nearestX, 2) + Math.Pow(py - nearestY, 2));
    }

    private void RemoveSelectedNode()
    {
        if (SelectedNode is null) return;
        RecordHistory();
        var id = SelectedNode.Id;
        Nodes.Remove(SelectedNode);
        foreach (var edge in Edges.Where(item => item.From == id || item.To == id).ToArray()) Edges.Remove(edge);
        SelectedNode = null;
        RebuildPreview();
        SelectAllNodesCommand.RaiseCanExecuteChanged();
    }

    public void UpdateBatchNodeSelection(IEnumerable<EditableMapNode> nodes)
    {
        if (_synchronizingNodeSelection) return;
        _synchronizingNodeSelection = true;
        var selected = nodes.Where(Nodes.Contains).Distinct().ToArray();
        _batchSelectedNodes.Clear();
        _batchSelectedNodes.AddRange(selected);
        var selectedSet = selected.ToHashSet();
        foreach (var node in Nodes) node.IsSelected = selectedSet.Contains(node);
        BatchSelectionText = _batchSelectedNodes.Count == 0
            ? "未选择批量站点"
            : $"已选择{_batchSelectedNodes.Count}个站点";
        MoveSelectedNodesCommand.RaiseCanExecuteChanged();
        DuplicateSelectedNodesCommand.RaiseCanExecuteChanged();
        RemoveSelectedNodesCommand.RaiseCanExecuteChanged();
        MergeSelectedNodesCommand.RaiseCanExecuteChanged();
        GenerateResourceBoundaryCommand.RaiseCanExecuteChanged();
        SelectAllNodesCommand.RaiseCanExecuteChanged(); ApplyNodeTypeCommand.RaiseCanExecuteChanged();
        AlignLeftCommand.RaiseCanExecuteChanged(); AlignRightCommand.RaiseCanExecuteChanged();
        AlignTopCommand.RaiseCanExecuteChanged(); AlignBottomCommand.RaiseCanExecuteChanged();
        AlignHorizontalCenterCommand.RaiseCanExecuteChanged(); AlignVerticalCenterCommand.RaiseCanExecuteChanged();
        DistributeHorizontalCommand.RaiseCanExecuteChanged(); DistributeVerticalCommand.RaiseCanExecuteChanged();
        RotateClockwiseCommand.RaiseCanExecuteChanged(); RotateCounterClockwiseCommand.RaiseCanExecuteChanged();
        MirrorHorizontalCommand.RaiseCanExecuteChanged(); MirrorVerticalCommand.RaiseCanExecuteChanged();
        AutoConnectSelectedCommand.RaiseCanExecuteChanged();
        RefreshTopologyCommands();
        OrthogonalConnectCommand.RaiseCanExecuteChanged(); CloseLoopCommand.RaiseCanExecuteChanged();
        CleanupTopologyCommand.RaiseCanExecuteChanged();
        CopySelectionCommand.RaiseCanExecuteChanged(); DeleteSelectionCommand.RaiseCanExecuteChanged();
        RebuildPreview();
        _synchronizingNodeSelection = false;
    }

    public void UpdateBatchEdgeSelection(IEnumerable<EditableMapEdge> edges)
    {
        if (_synchronizingEdgeSelection) return;
        _synchronizingEdgeSelection = true;
        try
        {
            var selected = edges.Where(Edges.Contains).Distinct().ToArray();
            _batchSelectedEdges.Clear();
            _batchSelectedEdges.AddRange(selected);
            var selectedSet = selected.ToHashSet();
            foreach (var edge in Edges) edge.IsSelected = selectedSet.Contains(edge);
            BatchEdgeSelectionText = selected.Length == 0 ? "未选择批量路段" : $"已选择{selected.Length}条路段";
            if (selected.FirstOrDefault() is { } first)
            {
                BatchEdgeSpeedLimit = first.SpeedLimit;
                BatchEdgeBidirectional = first.Bidirectional;
                BatchEdgeEnabled = first.Enabled;
                SelectedEdge = first;
            }
            ApplyBatchEdgePropertiesCommand.RaiseCanExecuteChanged();
            RemoveSelectedEdgesCommand.RaiseCanExecuteChanged();
            DeleteSelectionCommand.RaiseCanExecuteChanged(); SelectAllEdgesCommand.RaiseCanExecuteChanged();
            RefreshTopologyCommands();
            RebuildPreview();
        }
        finally { _synchronizingEdgeSelection = false; }
    }

    private void ApplyBatchEdgeProperties()
    {
        if (!CanManage() || _batchSelectedEdges.Count == 0) return;
        RecordHistory();
        foreach (var edge in _batchSelectedEdges)
        {
            edge.SpeedLimit = BatchEdgeSpeedLimit;
            edge.Bidirectional = BatchEdgeBidirectional;
            edge.Enabled = BatchEdgeEnabled;
        }
        RebuildPreview();
        StatusMessage = $"已批量更新{_batchSelectedEdges.Count}条路段的限速、方向和启用状态。";
    }

    private void RemoveSelectedEdges()
    {
        if (!CanManage() || _batchSelectedEdges.Count == 0) return;
        RecordHistory();
        var removing = _batchSelectedEdges.ToArray();
        foreach (var edge in removing) Edges.Remove(edge);
        UpdateBatchEdgeSelection([]);
        SelectedEdge = null;
        CleanupTopologyCommand.RaiseCanExecuteChanged();
        StatusMessage = $"已删除{removing.Length}条路段。";
    }

    private bool CanBatchEditNodes() => CanManage() && _batchSelectedNodes.Count > 0;
    private bool CanAlignNodes() => CanManage() && _batchSelectedNodes.Count >= 2;
    private bool CanDistributeNodes() => CanManage() && _batchSelectedNodes.Count >= 3;

    private void SelectAllNodes()
    {
        var visible = Nodes.Where(node => ShowNodeLayer && IsMapLayerVisible(node.LayerId)).ToArray();
        UpdateBatchNodeSelection(visible);
        SelectedNode = visible.LastOrDefault();
        StatusMessage = $"已选择{visible.Length}个可见节点。";
    }

    private void ApplyNodeType()
    {
        if (!CanBatchEditNodes()) return;
        RecordHistory();
        foreach (var node in _batchSelectedNodes) node.Type = DrawNodeType;
        RebuildPreview();
        StatusMessage = $"已将{_batchSelectedNodes.Count}个节点设置为{DrawNodeType}。";
    }

    private void AlignSelectedNodes(NodeAlignment alignment)
    {
        if (!CanAlignNodes()) return;
        RecordHistory();
        var x = alignment switch
        {
            NodeAlignment.Left => _batchSelectedNodes.Min(node => node.X),
            NodeAlignment.Right => _batchSelectedNodes.Max(node => node.X),
            _ => _batchSelectedNodes.Average(node => node.X)
        };
        var y = alignment switch
        {
            NodeAlignment.Bottom => _batchSelectedNodes.Min(node => node.Y),
            NodeAlignment.Top => _batchSelectedNodes.Max(node => node.Y),
            _ => _batchSelectedNodes.Average(node => node.Y)
        };
        foreach (var node in _batchSelectedNodes)
        {
            if (alignment is NodeAlignment.Left or NodeAlignment.Right or NodeAlignment.VerticalCenter) node.X = x;
            if (alignment is NodeAlignment.Top or NodeAlignment.Bottom or NodeAlignment.HorizontalCenter) node.Y = y;
        }
        RecalculateEdgeDistances(_batchSelectedNodes.Select(node => node.Id));
        RebuildPreview();
        StatusMessage = $"已对齐{_batchSelectedNodes.Count}个节点：{alignment}。";
    }

    private void DistributeSelectedNodes(bool horizontal)
    {
        if (!CanDistributeNodes()) return;
        RecordHistory();
        var ordered = horizontal
            ? _batchSelectedNodes.OrderBy(node => node.X).ToArray()
            : _batchSelectedNodes.OrderBy(node => node.Y).ToArray();
        var first = horizontal ? ordered[0].X : ordered[0].Y;
        var last = horizontal ? ordered[^1].X : ordered[^1].Y;
        var interval = (last - first) / (ordered.Length - 1);
        for (var index = 1; index < ordered.Length - 1; index++)
        {
            if (horizontal) ordered[index].X = first + interval * index;
            else ordered[index].Y = first + interval * index;
        }
        RecalculateEdgeDistances(_batchSelectedNodes.Select(node => node.Id));
        RebuildPreview();
        StatusMessage = $"已{(horizontal ? "水平" : "垂直")}等距分布{ordered.Length}个节点。";
    }

    private void RotateSelectedNodes(double degrees)
    {
        if (!CanAlignNodes()) return;
        RecordHistory();
        var centerX = _batchSelectedNodes.Average(node => node.X);
        var centerY = _batchSelectedNodes.Average(node => node.Y);
        var radians = degrees * Math.PI / 180;
        var cosine = Math.Cos(radians); var sine = Math.Sin(radians);
        TransformSelectedGeometry(point => new MapPoint2D(
            centerX + (point.X - centerX) * cosine - (point.Y - centerY) * sine,
            centerY + (point.X - centerX) * sine + (point.Y - centerY) * cosine));
        RecalculateEdgeDistances(_batchSelectedNodes.Select(node => node.Id));
        RebuildPreview();
        StatusMessage = $"已绕选择中心旋转{degrees:G}°。";
    }

    private void MirrorSelectedNodes(bool verticalAxis)
    {
        if (!CanAlignNodes()) return;
        RecordHistory();
        var centerX = _batchSelectedNodes.Average(node => node.X);
        var centerY = _batchSelectedNodes.Average(node => node.Y);
        TransformSelectedGeometry(point => new MapPoint2D(
            verticalAxis ? 2 * centerX - point.X : point.X,
            verticalAxis ? point.Y : 2 * centerY - point.Y));
        RecalculateEdgeDistances(_batchSelectedNodes.Select(node => node.Id));
        RebuildPreview();
        StatusMessage = $"已按{(verticalAxis ? "垂直" : "水平")}中心轴镜像{_batchSelectedNodes.Count}个节点。";
    }

    private void AutoConnectSelectedNodes()
    {
        if (!CanAlignNodes()) return;
        RecordHistory();
        var remaining = _batchSelectedNodes.ToList();
        var current = SelectedNode is not null && remaining.Remove(SelectedNode)
            ? SelectedNode
            : remaining.OrderBy(node => node.Id, StringComparer.OrdinalIgnoreCase).First();
        remaining.Remove(current);
        var created = 0;
        while (remaining.Count > 0)
        {
            var next = remaining.OrderBy(node => Math.Pow(node.X - current.X, 2) + Math.Pow(node.Y - current.Y, 2)).First();
            var before = Edges.Count;
            AddEdgeBetween(current, next);
            if (Edges.Count > before) created++;
            remaining.Remove(next);
            current = next;
        }
        RebuildPreview();
        StatusMessage = $"已按最近邻顺序自动连接所选节点，新增{created}条{(DrawEdgeBidirectional ? "双向" : "单向")}路段。";
    }

    private void OrthogonalConnectSelectedNodes()
    {
        if (_batchSelectedNodes.Count != 2 || !CanManage()) return;
        RecordHistory();
        var from = SelectedNode is not null && _batchSelectedNodes.Contains(SelectedNode)
            ? SelectedNode : _batchSelectedNodes[0];
        var to = _batchSelectedNodes.First(node => !ReferenceEquals(node, from));
        if (Math.Abs(from.X - to.X) < 1e-9 || Math.Abs(from.Y - to.Y) < 1e-9)
        {
            AddEdgeBetween(from, to);
            StatusMessage = $"节点已共线，已直接连接{from.Id}→{to.Id}。";
            return;
        }
        var corner = new EditableMapNode { LayerId = DrawingLayerId,
            Id = $"N{NextIndex(Nodes.Select(item => item.Id), "N"):000}",
            X = SnapCoordinate(to.X),
            Y = SnapCoordinate(from.Y),
            Type = MapNodeType.Waypoint,
            Enabled = true
        };
        Attach(corner);
        Nodes.Add(corner);
        AddEdgeBetween(from, corner);
        AddEdgeBetween(corner, to);
        UpdateBatchNodeSelection([from, corner, to]);
        SelectedNode = corner;
        StatusMessage = $"已通过新拐点{corner.Id}建立直角通道{from.Id}→{to.Id}。";
    }

    private void CloseSelectedLoop()
    {
        if (_batchSelectedNodes.Count < 3 || !CanManage()) return;
        RecordHistory();
        var centerX = _batchSelectedNodes.Average(node => node.X);
        var centerY = _batchSelectedNodes.Average(node => node.Y);
        var ordered = _batchSelectedNodes
            .OrderBy(node => Math.Atan2(node.Y - centerY, node.X - centerX)).ToArray();
        var created = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var before = Edges.Count;
            AddEdgeBetween(ordered[index], ordered[(index + 1) % ordered.Length]);
            if (Edges.Count > before) created++;
        }
        RebuildPreview();
        StatusMessage = $"已按中心角闭合所选节点，新增{created}条路段。请检查凹形区域是否需要手工调整。";
    }

    private void CleanupTopology()
    {
        if (!CanManage() || Edges.Count == 0) return;
        RecordHistory();
        var removed = 0;
        foreach (var selfLoop in Edges.Where(edge =>
                     edge.From.Equals(edge.To, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            Edges.Remove(selfLoop);
            removed++;
        }
        foreach (var group in Edges.GroupBy(edge =>
                     EdgeGeometryKey(edge, true),
                     StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToArray())
        {
            var keep = group.First();
            foreach (var duplicate in group.Skip(1).ToArray())
            {
                keep.SpeedLimit = Math.Min(keep.SpeedLimit, duplicate.SpeedLimit);
                keep.ResourceIds = string.Join(';', EditableMapNode.Split(keep.ResourceIds)
                    .Concat(EditableMapNode.Split(duplicate.ResourceIds))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                Edges.Remove(duplicate);
                removed++;
            }
        }
        SelectedEdge = null;
        RebuildPreview();
        StatusMessage = $"拓扑清理完成：移除{removed}条自环或重复路段；孤立节点未删除。";
    }

    private double SnapCoordinate(double value) => SnapToGrid
        ? Math.Round(value / GridSize) * GridSize
        : value;

    private void RecalculateEdgeDistances(IEnumerable<string> nodeIds)
    {
        var affected = nodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byId = Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var edge in Edges.Where(edge => affected.Contains(edge.From) || affected.Contains(edge.To)))
        {
            if (byId.TryGetValue(edge.From, out var from) && byId.TryGetValue(edge.To, out var to))
                UpdateEdgeGeometryDistance(edge);
        }
    }

    private void MoveSelectedNodes()
    {
        if (!CanBatchEditNodes() || (!double.IsFinite(BatchDeltaX) || !double.IsFinite(BatchDeltaY))) return;
        RecordHistory();
        foreach (var node in _batchSelectedNodes)
        {
            node.X += BatchDeltaX;
            node.Y += BatchDeltaY;
        }
        RebuildPreview();
        StatusMessage = $"已整体平移{_batchSelectedNodes.Count}个站点：ΔX={BatchDeltaX:G6}，ΔY={BatchDeltaY:G6}。";
    }

    private void DuplicateSelectedNodes()
    {
        if (!CanBatchEditNodes() || (!double.IsFinite(BatchDeltaX) || !double.IsFinite(BatchDeltaY))) return;
        RecordHistory();
        var selectedIds = _batchSelectedNodes.Select(node => node.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, EditableMapNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _batchSelectedNodes.ToArray())
        {
            var copy = new EditableMapNode { LayerId = DrawingLayerId,
                Id = $"N{NextIndex(Nodes.Select(item => item.Id), "N"):000}",
                X = source.X + BatchDeltaX,
                Y = source.Y + BatchDeltaY,
                Type = source.Type,
                Enabled = source.Enabled,
                ResourceIds = source.ResourceIds,
                AgvSiteId = string.Empty
            };
            Attach(copy);
            Nodes.Add(copy);
            map[source.Id] = copy;
        }
        foreach (var source in Edges.Where(edge => selectedIds.Contains(edge.From) && selectedIds.Contains(edge.To)).ToArray())
        {
            var copy = new EditableMapEdge { LayerId = DrawingLayerId,
                Id = $"E{NextIndex(Edges.Select(item => item.Id), "E"):000}",
                From = map[source.From].Id,
                To = map[source.To].Id,
                Distance = source.Distance,
                SpeedLimit = source.SpeedLimit,
                Bidirectional = source.Bidirectional,
                Enabled = source.Enabled,
                ResourceIds = source.ResourceIds,
                GeometryKind = source.GeometryKind,
                ControlPointsText = EditableTrafficResource.FormatBoundary(source.ParseControlPoints()
                    .Select(point => new MapPoint2D(point.X + BatchDeltaX, point.Y + BatchDeltaY)))
            };
            Attach(copy);
            Edges.Add(copy);
        }
        SelectedNode = map.Values.LastOrDefault();
        RebuildPreview();
        StatusMessage = $"已复制{map.Count}个站点及其内部路段；新站点Robokit映射已清空。";
    }

    private void CopySelection()
    {
        if (_batchSelectedNodes.Count == 0) return;
        var ids = _batchSelectedNodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _editorClipboard = new MapEditorSnapshot(
            _batchSelectedNodes.Select(node => node.ToDomain()).ToArray(),
            Edges.Where(edge => ids.Contains(edge.From) && ids.Contains(edge.To))
                .Select(edge => edge.ToDomain(Nodes)).ToArray(),
            []);
        _pasteGeneration = 0;
        PasteSelectionCommand.RaiseCanExecuteChanged();
        StatusMessage = $"已复制{_editorClipboard.Nodes.Count}个节点和{_editorClipboard.Edges.Count}条内部路段到地图编辑剪贴板。";
    }

    private void PasteSelection()
    {
        if (!CanManage() || _editorClipboard is not { Nodes.Count: > 0 } clipboard) return;
        RecordHistory();
        _pasteGeneration++;
        var offset = Math.Max(GridSize, 0.1) * 2 * _pasteGeneration;
        var map = new Dictionary<string, EditableMapNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in clipboard.Nodes)
        {
            var copy = new EditableMapNode { LayerId = DrawingLayerId,
                Id = $"N{NextIndex(Nodes.Select(item => item.Id), "N"):000}",
                X = source.X + offset,
                Y = source.Y + offset,
                Type = source.Type,
                Enabled = source.Enabled,
                ResourceIds = string.Join(';', source.ResourceIds),
                AgvSiteId = string.Empty
            };
            Attach(copy);
            Nodes.Add(copy);
            map[source.Id] = copy;
        }
        var pastedEdges = new List<EditableMapEdge>();
        foreach (var source in clipboard.Edges.Where(edge => map.ContainsKey(edge.From) && map.ContainsKey(edge.To)))
        {
            var copy = new EditableMapEdge { LayerId = DrawingLayerId,
                Id = $"E{NextIndex(Edges.Select(item => item.Id), "E"):000}",
                From = map[source.From].Id,
                To = map[source.To].Id,
                Distance = source.Distance,
                SpeedLimit = source.SpeedLimit,
                Bidirectional = source.Bidirectional,
                Enabled = source.Enabled,
                ResourceIds = string.Join(';', source.ResourceIds),
                GeometryKind = source.GeometryKind,
                ControlPointsText = EditableTrafficResource.FormatBoundary((source.ControlPoints ?? [])
                    .Select(point => new MapPoint2D(point.X + offset, point.Y + offset)))
            };
            Attach(copy);
            Edges.Add(copy);
            pastedEdges.Add(copy);
        }
        UpdateBatchNodeSelection(map.Values);
        UpdateBatchEdgeSelection(pastedEdges);
        SelectedNode = map.Values.LastOrDefault();
        RebuildPreview();
        StatusMessage = $"已粘贴{map.Count}个节点和{pastedEdges.Count}条路段，偏移{offset:F3}米；Robokit映射已清空。";
    }

    private void DeleteSelection()
    {
        if (!CanManage() || (_batchSelectedNodes.Count == 0 && _batchSelectedEdges.Count == 0)) return;
        RecordHistory();
        var nodeIds = _batchSelectedNodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = _batchSelectedEdges.Concat(Edges.Where(edge => nodeIds.Contains(edge.From) || nodeIds.Contains(edge.To)))
            .Distinct().ToArray();
        foreach (var edge in edges) Edges.Remove(edge);
        foreach (var node in _batchSelectedNodes.ToArray()) Nodes.Remove(node);
        var nodeCount = nodeIds.Count;
        UpdateBatchNodeSelection([]);
        UpdateBatchEdgeSelection([]);
        SelectedNode = null;
        SelectedEdge = null;
        RebuildPreview();
        StatusMessage = $"已删除{nodeCount}个节点和{edges.Length}条路段。";
    }

    private void RemoveSelectedNodes()
    {
        if (!CanBatchEditNodes()) return;
        RecordHistory();
        var ids = _batchSelectedNodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in Edges.Where(edge => ids.Contains(edge.From) || ids.Contains(edge.To)).ToArray()) Edges.Remove(edge);
        foreach (var node in _batchSelectedNodes.ToArray()) Nodes.Remove(node);
        var count = ids.Count;
        UpdateBatchNodeSelection([]);
        SelectedNode = null;
        RebuildPreview();
        StatusMessage = $"已删除{count}个站点及其关联路段。";
    }

    private void MergeSelectedNodes()
    {
        if (_batchSelectedNodes.Count != 2 || !CanManage()) return;
        var target = SelectedNode is not null && _batchSelectedNodes.Contains(SelectedNode)
            ? SelectedNode
            : _batchSelectedNodes[0];
        var source = _batchSelectedNodes.First(node => !ReferenceEquals(node, target));
        if (!string.IsNullOrWhiteSpace(target.AgvSiteId) && !string.IsNullOrWhiteSpace(source.AgvSiteId) &&
            !target.AgvSiteId.Equals(source.AgvSiteId, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = $"不能合并：{target.Id}和{source.Id}分别绑定了不同Robokit站点。请先确认并清除其中一个映射。";
            return;
        }

        RecordHistory();
        if (string.IsNullOrWhiteSpace(target.AgvSiteId)) target.AgvSiteId = source.AgvSiteId;
        target.ResourceIds = string.Join(';', EditableMapNode.Split(target.ResourceIds)
            .Concat(EditableMapNode.Split(source.ResourceIds))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (var edge in Edges)
        {
            if (edge.From.Equals(source.Id, StringComparison.OrdinalIgnoreCase)) edge.From = target.Id;
            if (edge.To.Equals(source.Id, StringComparison.OrdinalIgnoreCase)) edge.To = target.Id;
        }
        Nodes.Remove(source);
        foreach (var selfLoop in Edges.Where(edge => edge.From.Equals(edge.To, StringComparison.OrdinalIgnoreCase)).ToArray())
            Edges.Remove(selfLoop);

        foreach (var group in Edges.GroupBy(edge => EdgeGeometryKey(edge), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1).ToArray())
        {
            var duplicates = group.ToArray();
            var keep = duplicates[0];
            var hasOppositeDirections = duplicates.Any(edge =>
                !edge.From.Equals(keep.From, StringComparison.OrdinalIgnoreCase));
            keep.Bidirectional = duplicates.Any(edge => edge.Bidirectional) || hasOppositeDirections;
            keep.Enabled = duplicates.Any(edge => edge.Enabled);
            keep.SpeedLimit = duplicates.Where(edge => edge.SpeedLimit > 0)
                .Select(edge => edge.SpeedLimit).DefaultIfEmpty(keep.SpeedLimit).Min();
            keep.ResourceIds = string.Join(';', duplicates.SelectMany(edge => EditableMapNode.Split(edge.ResourceIds))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            foreach (var duplicate in duplicates.Skip(1)) Edges.Remove(duplicate);
        }
        RecalculateConnectedEdgeDistances(target.Id);
        UpdateBatchNodeSelection([target]);
        SelectedNode = target;
        RebuildPreview();
        StatusMessage = $"已将节点{source.Id}合并到{target.Id}，并清理自环及重复路段。";
    }

    private void RecalculateConnectedEdgeDistances(string nodeId)
    {
        var byId = Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var edge in Edges.Where(edge =>
                     edge.From.Equals(nodeId, StringComparison.OrdinalIgnoreCase) ||
                     edge.To.Equals(nodeId, StringComparison.OrdinalIgnoreCase)))
        {
            if (byId.TryGetValue(edge.From, out var from) && byId.TryGetValue(edge.To, out var to))
                UpdateEdgeGeometryDistance(edge);
        }
    }

    private static string CanonicalEdgeKey(string from, string to) =>
        string.Compare(from, to, StringComparison.OrdinalIgnoreCase) <= 0 ? $"{from}|{to}" : $"{to}|{from}";

    private void AddEdge()
    {
        if (Nodes.Count < 2) return;
        RecordHistory();
        AddEdgeBetween(Nodes[Nodes.Count - 2], Nodes[Nodes.Count - 1]);
    }

    private EditableMapEdge AddEdgeBetween(EditableMapNode from, EditableMapNode to)
    {
        var existing = Edges.FirstOrDefault(item =>
            (item.From.Equals(from.Id, StringComparison.OrdinalIgnoreCase) && item.To.Equals(to.Id, StringComparison.OrdinalIgnoreCase)) ||
            (item.Bidirectional && item.From.Equals(to.Id, StringComparison.OrdinalIgnoreCase) && item.To.Equals(from.Id, StringComparison.OrdinalIgnoreCase)));
        if (existing is not null)
        {
            SelectedEdge = existing;
            return existing;
        }
        var edge = new EditableMapEdge { LayerId = DrawingLayerId,
            Id = $"E{NextIndex(Edges.Select(item => item.Id), "E"):000}", From = from.Id, To = to.Id,
            SpeedLimit = DrawEdgeSpeedLimit, Bidirectional = DrawEdgeBidirectional, Enabled = true
        };
        InitializeEdgeGeometry(edge, from, to, DrawEdgeGeometryKind);
        Attach(edge); Edges.Add(edge); SelectedEdge = edge; RebuildPreview();
        CleanupTopologyCommand.RaiseCanExecuteChanged();
        return edge;
    }
    private void RemoveSelectedEdge()
    {
        if (SelectedEdge is null) return;
        RecordHistory(); Edges.Remove(SelectedEdge); SelectedEdge = null; RebuildPreview();
        CleanupTopologyCommand.RaiseCanExecuteChanged();
    }

    private void SplitSelectedEdge()
    {
        if (SelectedEdge is null) return;
        var edge = SelectedEdge;
        var from = Nodes.FirstOrDefault(node => node.Id.Equals(edge.From, StringComparison.OrdinalIgnoreCase));
        var to = Nodes.FirstOrDefault(node => node.Id.Equals(edge.To, StringComparison.OrdinalIgnoreCase));
        if (from is null || to is null)
        {
            StatusMessage = "当前路段端点不存在，无法拆分。";
            return;
        }
        var domain = edge.ToDomain(Nodes);
        if (!MapEdgeGeometry.TryValidate(domain, from.ToDomain(), to.ToDomain(), out var geometryError))
        {
            StatusMessage = $"无法拆分：{geometryError}";
            return;
        }
        var split = MapEdgeGeometry.Split(domain, from.ToDomain(), to.ToDomain());
        RecordHistory();
        var middle = new EditableMapNode { LayerId = DrawingLayerId,
            Id = $"N{NextIndex(Nodes.Select(item => item.Id), "N"):000}",
            X = split.SplitPoint.X,
            Y = split.SplitPoint.Y,
            Type = MapNodeType.Waypoint,
            Enabled = edge.Enabled
        };
        Attach(middle);
        Nodes.Add(middle);
        Edges.Remove(edge);
        var first = EditableMapEdge.FromDomain(split.First with
        { Id = $"E{NextIndex(Edges.Select(item => item.Id), "E"):000}", To = middle.Id });
        Attach(first);
        Edges.Add(first);
        var second = EditableMapEdge.FromDomain(split.Second with
        { Id = $"E{NextIndex(Edges.Select(item => item.Id), "E"):000}", From = middle.Id });
        Attach(second);
        Edges.Add(second);
        SelectedNode = middle;
        SelectedEdge = first;
        RebuildPreview();
        StatusMessage = $"路段{edge.Id}已在中点拆分为{first.Id}和{second.Id}；请按需要调整新节点类型和Robokit站点ID。";
    }

    private void ReverseSelectedEdge()
    {
        if (SelectedEdge is null || SelectedEdge.Bidirectional) return;
        RecordHistory();
        var reversed = MapEdgeGeometry.Reverse(SelectedEdge.ToDomain(Nodes));
        _updatingCurveGeometry = true;
        try
        {
            SelectedEdge.From = reversed.From;
            SelectedEdge.To = reversed.To;
            SelectedEdge.SetControlPoints(reversed.ControlPoints ?? []);
        }
        finally { _updatingCurveGeometry = false; }
        RebuildPreview();
        StatusMessage = $"已反转单行路段{SelectedEdge.Id}方向。";
    }
    private void AddResource()
    {
        RecordHistory();
        var item = new EditableTrafficResource { LayerId = DrawingLayerId, Id = $"R{NextIndex(Resources.Select(resource => resource.Id), "R"):000}", Capacity = 1 };
        Attach(item); Resources.Add(item); SelectedResource = item;
    }
    private void RemoveSelectedResource() { if (SelectedResource is null) return; RecordHistory(); Resources.Remove(SelectedResource); SelectedResource = null; }

    private void GenerateResourceBoundary()
    {
        if (SelectedResource is null || _batchSelectedNodes.Count < 3 || !CanManage()) return;
        RecordHistory();
        var centerX = _batchSelectedNodes.Average(node => node.X);
        var centerY = _batchSelectedNodes.Average(node => node.Y);
        var ordered = _batchSelectedNodes
            .OrderBy(node => Math.Atan2(node.Y - centerY, node.X - centerX))
            .Select(node => new MapPoint2D(node.X, node.Y))
            .ToArray();
        SelectedResource.Boundary = EditableTrafficResource.FormatBoundary(ordered);
        RebuildPreview();
        StatusMessage = $"已用{ordered.Length}个选中节点生成资源{SelectedResource.Id}的多边形边界。";
    }

    private bool CanEditBoundaryVertices() => CanManage() && SelectedResource is not null && !IsLayerLocked(SelectedResource.LayerId) &&
                                               SelectedResource.ParseBoundary().Count > 0;

    private void AddBoundaryVertex(double x, double y)
    {
        if (SelectedResource is null)
        {
            StatusMessage = "请先在“交通资源”页选择一个资源，再绘制边界。";
            return;
        }
        RecordHistory();
        var boundary = SelectedResource.ParseBoundary().ToList();
        boundary.Add(new MapPoint2D(x, y));
        SelectedResource.Boundary = EditableTrafficResource.FormatBoundary(boundary);
        RaiseBoundaryCommands();
        RebuildPreview();
        StatusMessage = $"资源{SelectedResource.Id}边界已添加第{boundary.Count}个顶点。";
    }

    private void RemoveLastBoundaryVertex()
    {
        if (SelectedResource is null) return;
        var boundary = SelectedResource.ParseBoundary().ToList();
        if (boundary.Count == 0) return;
        RecordHistory();
        boundary.RemoveAt(boundary.Count - 1);
        SelectedResource.Boundary = EditableTrafficResource.FormatBoundary(boundary);
        RaiseBoundaryCommands();
        RebuildPreview();
        StatusMessage = $"已撤掉资源{SelectedResource.Id}的最后一个边界点，剩余{boundary.Count}个。";
    }

    private void ClearBoundary()
    {
        if (SelectedResource is null || SelectedResource.ParseBoundary().Count == 0) return;
        RecordHistory();
        SelectedResource.Boundary = string.Empty;
        RaiseBoundaryCommands();
        RebuildPreview();
        StatusMessage = $"已清空资源{SelectedResource.Id}的边界。";
    }

    private void CompleteBoundary()
    {
        if (SelectedResource is null || SelectedResource.ParseBoundary().Count < 3) return;
        IsBoundaryDrawMode = false;
        IsBoundaryVertexMode = true;
        StatusMessage = $"资源{SelectedResource.Id}边界已闭合；可拖动橙色顶点继续微调。";
    }

    private void RaiseBoundaryCommands()
    {
        RemoveLastBoundaryVertexCommand.RaiseCanExecuteChanged();
        ClearBoundaryCommand.RaiseCanExecuteChanged();
        CompleteBoundaryCommand.RaiseCanExecuteChanged();
    }

    public bool BeginBoundaryVertexDrag(double canvasX, double canvasY)
    {
        if (!IsBoundaryVertexMode || SelectedResource is null || !CanManage() || IsLayerLocked(SelectedResource.LayerId)) return false;
        var hit = PreviewResourceVertices
            .Select(vertex => new
            {
                Vertex = vertex,
                Distance = Math.Sqrt(Math.Pow(vertex.Left + 6 - canvasX, 2) + Math.Pow(vertex.Top + 6 - canvasY, 2))
            })
            .Where(item => item.Distance <= 14 / Math.Max(CanvasZoom, 0.1))
            .OrderBy(item => item.Distance)
            .FirstOrDefault()?.Vertex;
        if (hit is null || !hit.ResourceId.Equals(SelectedResource.Id, StringComparison.OrdinalIgnoreCase)) return false;
        _dragBoundaryVertexIndex = hit.Index;
        _boundaryDragHistoryRecorded = false;
        return true;
    }

    public void DragBoundaryVertex(
        double canvasX, double canvasY, double canvasWidth, double canvasHeight)
    {
        if (_dragBoundaryVertexIndex is not { } index || SelectedResource is null ||
            !TryCanvasToMap(canvasX, canvasY, canvasWidth, canvasHeight, out var mapPoint)) return;
        var boundary = SelectedResource.ParseBoundary().ToList();
        if (index < 0 || index >= boundary.Count) return;
        var x = SnapToGrid ? Math.Round(mapPoint.X / GridSize) * GridSize : mapPoint.X;
        var y = SnapToGrid ? Math.Round(mapPoint.Y / GridSize) * GridSize : mapPoint.Y;
        if (!_boundaryDragHistoryRecorded &&
            Math.Sqrt(Math.Pow(boundary[index].X - x, 2) + Math.Pow(boundary[index].Y - y, 2)) < 0.001) return;
        if (!_boundaryDragHistoryRecorded)
        {
            RecordHistory();
            _boundaryDragHistoryRecorded = true;
        }
        boundary[index] = new MapPoint2D(x, y);
        SelectedResource.Boundary = EditableTrafficResource.FormatBoundary(boundary);
        RebuildPreview();
    }

    public void EndBoundaryVertexDrag()
    {
        if (_boundaryDragHistoryRecorded && SelectedResource is not null)
            StatusMessage = $"资源{SelectedResource.Id}边界顶点已移动，可使用Ctrl+Z撤销。";
        _dragBoundaryVertexIndex = null;
        _boundaryDragHistoryRecorded = false;
    }

    private void RecordHistory([System.Runtime.CompilerServices.CallerMemberName] string operation = "", bool merge = false)
    {
        if (_restoringHistory) return;
        PushHistory(CaptureSnapshot(), DescribeEdit(operation), merge);
        _redo.Clear();
        InvalidateDraftAnalysis();
        RaiseCommands();
    }

    private MapEditorSnapshot CaptureSnapshot() => new(
        Nodes.Select(item => item.ToDomain()).ToArray(),
        Edges.Select(item => item.ToDomain(Nodes)).ToArray(),
        Resources.Select(item => item.ToDomain()).ToArray()) { Layers = MapLayers.Select(layer => layer.ToDomain()).ToArray(), CurrentLayerId = CurrentLayer?.Id };

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var entry = _undo.Pop();
        _redo.Push(CaptureSnapshot() with { Operation = entry.Operation, RecordedAt = entry.RecordedAt });
        RestoreSnapshot(entry);
        StatusMessage = "已撤销上一步地图结构编辑。";
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        var entry = _redo.Pop();
        _undo.Push(CaptureSnapshot() with { Operation = entry.Operation, RecordedAt = entry.RecordedAt });
        RestoreSnapshot(entry);
        StatusMessage = "已重做地图结构编辑。";
    }

    private void RestoreSnapshot(MapEditorSnapshot snapshot)
    {
        EndNodeDrag(); EndBoundaryVertexDrag();
        EndCurveControlDrag();
        _restoringHistory = true;
        try
        {
            LoadEditable(Nodes, snapshot.Nodes.Select(EditableMapNode.From));
            LoadEditable(Edges, snapshot.Edges.Select(EditableMapEdge.FromDomain));
            LoadEditable(Resources, snapshot.Resources.Select(EditableTrafficResource.From));
            LoadMapLayers(snapshot.Layers);
            CurrentLayer = MapLayers.FirstOrDefault(layer => layer.Id == snapshot.CurrentLayerId) ?? CurrentLayer;
            UpdateBatchNodeSelection([]);
            UpdateBatchEdgeSelection([]);
            SelectedNode = null; SelectedEdge = null; SelectedResource = null; _lineStartNode = null;
            RebuildPreview();
            InvalidateDraftAnalysis();
        }
        finally { _restoringHistory = false; RaiseCommands(); }
    }

    public void BeginGridEdit()
    {
        if (_restoringHistory || _pendingGridEdit is not null || _workspace is null) return;
        _pendingGridEdit = CaptureSnapshot();
    }

    public void CompleteGridEdit(bool cancelled)
    {
        var before = _pendingGridEdit;
        _pendingGridEdit = null;
        if (cancelled || before is null || _restoringHistory) return;
        if (LockedObjectsChanged(before, CaptureSnapshot()))
        { RestoreSnapshot(before); StatusMessage = "锁定图层的属性修改已取消。"; return; }
        ApplyRenamedReferences(before);
        var after = CaptureSnapshot();
        if (SnapshotFingerprint(before) == SnapshotFingerprint(after)) return;
        PushHistory(before, "属性修改", true);
        _redo.Clear();
        InvalidateDraftAnalysis();
        RebuildPreview();
        RaiseCommands();
        StatusMessage = "属性修改已记录，可使用Ctrl+Z撤销。";
    }

    private void ApplyRenamedReferences(MapEditorSnapshot before)
    {
        for (var index = 0; index < Math.Min(before.Nodes.Count, Nodes.Count); index++)
        {
            var oldId = before.Nodes[index].Id;
            var newId = Nodes[index].Id.Trim();
            if (string.IsNullOrWhiteSpace(oldId) || string.IsNullOrWhiteSpace(newId) ||
                oldId.Equals(newId, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var edge in Edges)
            {
                if (edge.From.Equals(oldId, StringComparison.OrdinalIgnoreCase)) edge.From = newId;
                if (edge.To.Equals(oldId, StringComparison.OrdinalIgnoreCase)) edge.To = newId;
            }
            if (SimulationSource.Equals(oldId, StringComparison.OrdinalIgnoreCase)) SimulationSource = newId;
            if (SimulationDestination.Equals(oldId, StringComparison.OrdinalIgnoreCase)) SimulationDestination = newId;
        }

        for (var index = 0; index < Math.Min(before.Resources.Count, Resources.Count); index++)
        {
            var oldId = before.Resources[index].Id;
            var newId = Resources[index].Id.Trim();
            if (string.IsNullOrWhiteSpace(oldId) || string.IsNullOrWhiteSpace(newId) ||
                oldId.Equals(newId, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var node in Nodes) node.ResourceIds = ReplaceListValue(node.ResourceIds, oldId, newId);
            foreach (var edge in Edges) edge.ResourceIds = ReplaceListValue(edge.ResourceIds, oldId, newId);
            SimulationBlockedResources = ReplaceListValue(SimulationBlockedResources, oldId, newId);
        }
    }

    private static string ReplaceListValue(string current, string oldValue, string newValue) => string.Join(';',
        EditableMapNode.Split(current)
            .Select(value => value.Equals(oldValue, StringComparison.OrdinalIgnoreCase) ? newValue : value)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string SnapshotFingerprint(MapEditorSnapshot snapshot) => string.Join("\n",
        snapshot.Nodes.Select(node => $"N|{node.LayerId}|{node.Id}|{node.X:R}|{node.Y:R}|{node.Type}|{node.Enabled}|{node.AgvSiteId}|{string.Join(';', node.ResourceIds)}")
            .Concat(snapshot.Edges.Select(edge => $"E|{edge.LayerId}|{edge.Id}|{edge.From}|{edge.To}|{edge.Distance:R}|{edge.SpeedLimit:R}|{edge.Bidirectional}|{edge.Enabled}|{string.Join(';', edge.ResourceIds)}|{edge.GeometryKind}|{EditableTrafficResource.FormatBoundary(edge.ControlPoints ?? [])}"))
            .Concat(snapshot.Resources.Select(resource =>
                $"R|{resource.LayerId}|{resource.Id}|{resource.Type}|{resource.Capacity}|{string.Join(';', resource.Boundary?.Select(point => $"{point.X:R},{point.Y:R}") ?? [])}"))
            .Concat(snapshot.Layers.Select(layer => $"L|{layer.Id}|{layer.Name}|{layer.Visible}")));

    private void InvalidateDraftAnalysis()
    {
        SimulationSteps.Clear();
        SimulationResult = "草稿已变化，请重新执行路径仿真。";
        PreflightEntries.Clear();
        PreflightSummary = "草稿已变化，请重新执行全站点路径矩阵检查。";
        VersionDifferences.Clear();
        VersionDiffSummary = "草稿已变化，请重新比较版本差异。";
    }

    private void SimulateRoute()
    {
        SimulationSteps.Clear();
        if (string.IsNullOrWhiteSpace(SimulationSource) || string.IsNullOrWhiteSpace(SimulationDestination))
        {
            SimulationResult = "必须选择仿真起点和终点。";
            return;
        }
        try
        {
            var definition = BuildDefinition();
            var source = MapRouteSiteMapper.ResolveLogicalNodeId(definition, SimulationSource);
            var destination = MapRouteSiteMapper.ResolveLogicalNodeId(definition, SimulationDestination);
            var blocked = EditableMapNode.Split(SimulationBlockedResources)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var logicalPlan = definition.ToGraph().FindShortestRoute(source, destination, blocked);
            if (logicalPlan is null)
            {
                SimulationResult = $"不可达：{source} → {destination}。请检查方向、断路或封锁资源。";
                return;
            }
            var agvPlan = MapRouteSiteMapper.ToAgvSitePlan(definition, logicalPlan);
            var byId = definition.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < logicalPlan.Nodes.Count; index++)
            {
                var logicalId = logicalPlan.Nodes[index];
                var node = byId[logicalId];
                SimulationSteps.Add(new RouteSimulationStep(
                    index + 1, logicalId, agvPlan.Nodes[index], node.Type.ToString()));
            }
            SimulationResult = $"可达 · 成本{logicalPlan.Cost:F3} · {logicalPlan.EdgeIds.Count}段 · " +
                               $"占用资源：{(logicalPlan.ResourceIds.Count == 0 ? "无" : string.Join(',', logicalPlan.ResourceIds))}";
        }
        catch (Exception exception)
        {
            SimulationResult = $"仿真失败：{exception.Message}";
        }
    }

    private async Task RunPreflightAsync()
    {
        if (_workspace is null || IsBusy || Nodes.Count < 2) return;
        IsBusy = true;
        PreflightEntries.Clear();
        PreflightSummary = "正在计算全部运行站点的有向路径矩阵……";
        try
        {
            var definition = BuildDefinition();
            var report = await Task.Run(() =>
                MapDeploymentPreflightAnalyzer.Analyze(definition, 200, CancellationToken.None));
            Replace(PreflightEntries, report.Entries);
            var mapping = report.MissingAgvSiteNodeIds.Count == 0
                ? "站点映射完整"
                : $"缺少Robokit映射：{string.Join(',', report.MissingAgvSiteNodeIds.Take(10))}";
            var duplicates = report.DuplicateAgvSiteIds.Count == 0
                ? "无重复映射"
                : $"重复Robokit映射：{string.Join(',', report.DuplicateAgvSiteIds.Take(10))}";
            PreflightSummary = $"{(report.Ready ? "通过" : "未通过")} · 运行站点{report.OperationalNodeCount} · " +
                               $"路径对{report.RoutePairCount} · 可达{report.ReachablePairCount} · 不可达{report.UnreachablePairCount} · " +
                               $"{mapping} · {duplicates}";
            StatusMessage = report.Ready
                ? "发布前全站点路径矩阵检查通过。"
                : "发布前检查未通过，请处理不可达路径或站点映射问题。";
        }
        catch (Exception exception)
        {
            PreflightSummary = $"发布前检查失败：{exception.Message}";
        }
        finally { IsBusy = false; }
    }

    private void CompareVersions()
    {
        VersionDifferences.Clear();
        if (_workspace?.Published is not { } published)
        {
            VersionDiffSummary = "当前地图尚无已发布版本，整个草稿都属于首次发布内容。";
            return;
        }
        var differences = MapVersionDiffer.Compare(published.Definition, BuildDefinition());
        Replace(VersionDifferences, differences);
        VersionDiffSummary = differences.Count == 0
            ? $"草稿与已发布V{published.VersionNumber}内容一致。"
            : $"相对已发布V{published.VersionNumber}共有{differences.Count}项变化：新增{differences.Count(item => item.Kind == MapDifferenceKind.Added)}、删除{differences.Count(item => item.Kind == MapDifferenceKind.Removed)}、修改{differences.Count(item => item.Kind == MapDifferenceKind.Modified)}。";
    }
    private void AddCalibrationPoint()
    {
        var point = new EditableCalibrationPoint();
        Attach(point); CalibrationPoints.Add(point); SelectedCalibrationPoint = point; RaiseCommands();
    }
    private void RemoveCalibrationPoint()
    {
        if (SelectedCalibrationPoint is null || CalibrationPoints.Count <= 2) return;
        CalibrationPoints.Remove(SelectedCalibrationPoint); SelectedCalibrationPoint = null; RaiseCommands();
    }

    private void LoadCalibration(MapCalibrationSnapshot? calibration)
    {
        CalibrationPoints.Clear();
        var points = calibration?.ControlPoints ??
            [new MapCalibrationPoint(0, 0, 0, 0), new MapCalibrationPoint(1000, 0, 1, 0), new MapCalibrationPoint(0, 1000, 0, 1)];
        foreach (var value in points)
        {
            var item = EditableCalibrationPoint.From(value); Attach(item); CalibrationPoints.Add(item);
        }
    }

    private static ImageSource? LoadImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static int NextIndex(IEnumerable<string> ids, string prefix)
    {
        var used = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var index = 1;
        while (used.Contains($"{prefix}{index:000}")) index++;
        return index;
    }

    private void RebuildPreview()
    {
        RefreshEditorCheck();
        var visibleNodeIds = Nodes.Where(node => IsMapLayerVisible(node.LayerId)).Select(node => node.Id).ToHashSet();
        PreviewNodes.Clear(); PreviewEdges.Clear(); PreviewResourceBoundaries.Clear(); PreviewResourceVertices.Clear();
        PreviewCurveControls.Clear(); PreviewCurveGuides.Clear();
        PreviewGridLines.Clear(); PreviewMeasurements.Clear(); PreviewBoundarySegments.Clear();
        PreviewAgvs.Clear();
        PreviewTrackSegments.Clear(); PreviewReplayMarkers.Clear();
        if (_workspace?.Project.CadViewBox is { } viewBox)
        {
            AddGridPreview();
            const double canvasWidth = 900;
            const double canvasHeight = 520;
            var imageScale = Math.Min(canvasWidth / viewBox.PixelWidth, canvasHeight / viewBox.PixelHeight);
            var displayWidth = viewBox.PixelWidth * imageScale;
            var displayHeight = viewBox.PixelHeight * imageScale;
            var offsetX = (canvasWidth - displayWidth) / 2;
            var offsetY = (canvasHeight - displayHeight) / 2;
            var calibratedPoints = Nodes.ToDictionary(item => item.Id, item =>
            {
                var cad = ToCadCoordinates(item.X, item.Y, _workspace.Project.Calibration, CoordinateScale);
                return new MapCanvasNode(
                    item.Id,
                    offsetX + (cad.X - viewBox.MinX) / viewBox.Width * displayWidth - 7,
                    offsetY + (1 - (cad.Y - viewBox.MinY) / viewBox.Height) * displayHeight - 7,
                    item.Type.ToString(),
                    NodeFill(item));
            }, StringComparer.OrdinalIgnoreCase);
            AddResourceBoundaryPreviews(point =>
            {
                var cad = ToCadCoordinates(point.X, point.Y, _workspace.Project.Calibration, CoordinateScale);
                return new Point(
                    offsetX + (cad.X - viewBox.MinX) / viewBox.Width * displayWidth,
                    offsetY + (1 - (cad.Y - viewBox.MinY) / viewBox.Height) * displayHeight);
            });
            AddPreviewEdges(calibratedPoints);
            foreach (var point in calibratedPoints.Values.Where(point => visibleNodeIds.Contains(point.Id))) PreviewNodes.Add(point);
            RebuildMeasurementPreview();
            RebuildAgvPreview();
            RebuildTrackPreview();
            RebuildCurveControlPreview();
            ApplyLayerStyles();
            RebuildGeometrySnapTargets();
            return;
        }
        AddGridPreview();
        var points = Nodes.ToDictionary(item => item.Id, item => new MapCanvasNode(
            item.Id, item.X / CanvasWorldWidth * 900 - 7, (1 - item.Y / CanvasWorldHeight) * 520 - 7,
            item.Type.ToString(), NodeFill(item)), StringComparer.OrdinalIgnoreCase);
        AddResourceBoundaryPreviews(point => new Point(
            point.X / CanvasWorldWidth * 900,
            (1 - point.Y / CanvasWorldHeight) * 520));
        AddPreviewEdges(points);
        foreach (var point in points.Values.Where(point => visibleNodeIds.Contains(point.Id))) PreviewNodes.Add(point);
        RebuildMeasurementPreview();
        RebuildAgvPreview();
        RebuildTrackPreview();
        RebuildCurveControlPreview();
        ApplyLayerStyles();
        RebuildGeometrySnapTargets();
    }

    private void RefreshEditorCheck()
    {
        var errors = 0;
        var warnings = 0;
        errors += Nodes.Count(node => string.IsNullOrWhiteSpace(node.Id) || !double.IsFinite(node.X) || !double.IsFinite(node.Y));
        errors += Nodes.GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase).Count(group => group.Count() > 1);
        errors += Edges.GroupBy(edge => edge.Id, StringComparer.OrdinalIgnoreCase).Count(group => group.Count() > 1);
        errors += Resources.GroupBy(resource => resource.Id, StringComparer.OrdinalIgnoreCase).Count(group => group.Count() > 1);
        var nodeIds = Nodes.Where(node => !string.IsNullOrWhiteSpace(node.Id)).Select(node => node.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in Edges)
        {
            if (string.IsNullOrWhiteSpace(edge.Id) || !nodeIds.Contains(edge.From) || !nodeIds.Contains(edge.To) ||
                edge.From.Equals(edge.To, StringComparison.OrdinalIgnoreCase) ||
                !double.IsFinite(edge.SpeedLimit) || edge.SpeedLimit <= 0) errors++;
            if (TryGetEndpoints(edge, out var from, out var to) &&
                !MapEdgeGeometry.TryValidate(edge.ToDomain(Nodes), from.ToDomain(), to.ToDomain(), out _)) errors++;
        }
        foreach (var resource in Resources)
        {
            var boundary = resource.ParseBoundary();
            if (string.IsNullOrWhiteSpace(resource.Id) || resource.Capacity < 1 || boundary.Count is 1 or 2) errors++;
        }
        var connected = Edges.SelectMany(edge => new[] { edge.From, edge.To }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        warnings += Nodes.Count(node => node.Enabled && !connected.Contains(node.Id));
        warnings += Nodes.Count(node => node.Enabled &&
            node.Type is MapNodeType.Station or MapNodeType.WaitingPoint or MapNodeType.ChargingPoint &&
            string.IsNullOrWhiteSpace(node.AgvSiteId));
        EditorHasErrors = errors > 0;
        EditorCheckSummary = $"实时检查：{errors}个结构错误 · {warnings}个待确认项 · " +
                             $"{Nodes.Count}节点/{Edges.Count}路段/{Resources.Count}资源";
    }

    private void AddGridPreview()
    {
        if(!ShowGrid||GridSize<=0||!double.IsFinite(GridSize))return;
        var minX=0d;var minY=0d;var maxX=CanvasWorldWidth;var maxY=CanvasWorldHeight;
        if(_workspace?.Project.CadViewBox is { } frame)
        {
            var fit=Math.Min(900/frame.PixelWidth,520/frame.PixelHeight);var ox=(900-frame.PixelWidth*fit)/2;var oy=(520-frame.PixelHeight*fit)/2;
            var corners=new[]{new Point(ox,oy),new Point(900-ox,oy),new Point(ox,520-oy),new Point(900-ox,520-oy)};
            var world=new List<(double X,double Y)>();foreach(var corner in corners)if(TryCanvasToMap(corner.X,corner.Y,900,520,out var point))world.Add(point);
            if(world.Count!=4)return;minX=world.Min(p=>p.X);maxX=world.Max(p=>p.X);minY=world.Min(p=>p.Y);maxY=world.Max(p=>p.Y);
        }
        if(!double.IsFinite(maxX-minX)||!double.IsFinite(maxY-minY))return;
        var step=GridSize;while((maxX-minX+maxY-minY)/step>240)step*=2;
        void Add(double x1,double y1,double x2,double y2,int index)
        {
            if(TryMapToCanvas(x1,y1) is not { } a||TryMapToCanvas(x2,y2) is not { } b)return;
            PreviewGridLines.Add(new(a.X,a.Y,b.X,b.Y,index%5==0?"#A8BDD9":"#D3E0EE",index%5==0?1:.6));
        }
        var ix=0;for(var x=Math.Ceiling(minX/step)*step;x<=maxX+step*.001&&ix<242;x+=step,ix++)Add(x,minY,x,maxY,ix);
        var iy=0;for(var y=Math.Ceiling(minY/step)*step;y<=maxY+step*.001&&iy<242;y+=step,iy++)Add(minX,y,maxX,y,iy);
    }

    private void RebuildMeasurementPreview()
    {
        PreviewMeasurements.Clear();
        if (_measureStart is not { } start || _measureEnd is not { } end ||
            TryMapToCanvas(start.X, start.Y) is not { } from || TryMapToCanvas(end.X, end.Y) is not { } to) return;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        PreviewMeasurements.Add(new MapCanvasMeasureSegment(
            from.X, from.Y, to.X, to.Y, $"{distance:F3}m / {angle:F1}°",
            (from.X + to.X) / 2 + 5, (from.Y + to.Y) / 2 - 22, "#FFFFD54F", 3));
    }

    private void RebuildTrackPreview()
    {
        PreviewTrackSegments.Clear();
        PreviewReplayMarkers.Clear();
        if (_replayTrack.Count == 0) return;
        for (var index = 1; index < _replayTrack.Count; index++)
        {
            var previous = _replayTrack[index - 1];
            var current = _replayTrack[index];
            if(!AgvTrackReplayValidator.CanConnect(previous,current))continue;
            if (TryMapToCanvas(previous.X, previous.Y) is not { } from ||
                TryMapToCanvas(current.X, current.Y) is not { } to)
                continue;
            var abnormal = previous.PositionState == AgvMapPositionState.OffRoute ||
                           current.PositionState == AgvMapPositionState.OffRoute;
            PreviewTrackSegments.Add(new MapCanvasTrackSegment(
                from.X, from.Y, to.X, to.Y,
                abnormal ? "#FFFF5252" : "#FF40C4FF",
                abnormal ? 5 : 3));
        }
        RebuildTrackMarker();
    }

    private void RebuildTrackMarker()
    {
        PreviewReplayMarkers.Clear();
        if (_replayTrack.Count == 0) return;
        _replayIndex = Math.Clamp(_replayIndex, 0, _replayTrack.Count - 1);
        var point = _replayTrack[_replayIndex];
        if (TryMapToCanvas(point.X, point.Y) is not { } canvas) return;
        PreviewReplayMarkers.Add(new MapCanvasReplayMarker(
            canvas.X - 9,
            canvas.Y - 9,
            point.Angle ?? 0,
            point.PositionState == AgvMapPositionState.OffRoute ? "#FFFF3B30" : "#FF40C4FF"));
    }

    private void RebuildAgvPreview()
    {
        PreviewAgvs.Clear();
        if (_latestSystemStatus is null)
        {
            LiveAgvSummary = "等待实时AGV状态。";
            return;
        }
        if (_workspace is null || string.IsNullOrWhiteSpace(_latestSystemStatus.Safety.ActiveMapId))
        {
            LiveAgvSummary = "尚未激活运行地图，实时坐标不叠加。";
            return;
        }
        if (!string.Equals(_workspace.Project.MapId, _latestSystemStatus.Safety.ActiveMapId,
                StringComparison.OrdinalIgnoreCase))
        {
            LiveAgvSummary = $"当前编辑地图不是运行地图（运行：{_latestSystemStatus.Safety.ActiveMapId}）。";
            return;
        }

        foreach (var agv in _latestSystemStatus.Agvs)
        {
            if (agv.X is null || agv.Y is null ||
                !double.IsFinite(agv.X.Value) || !double.IsFinite(agv.Y.Value) ||
                TryMapToCanvas(agv.X.Value, agv.Y.Value) is not { } canvas)
                continue;
            var fill = agv.MapPositionState switch
            {
                AgvMapPositionState.Normal => "#FF34C759",
                AgvMapPositionState.OffRoute => "#FFFF3B30",
                AgvMapPositionState.Stale or AgvMapPositionState.LowConfidence => "#FFFFA726",
                _ => "#FF8E9BA6"
            };
            PreviewAgvs.Add(new MapCanvasAgv(
                agv.VehicleId,
                agv.DisplayName,
                canvas.X - 12,
                canvas.Y - 12,
                agv.Angle ?? 0,
                fill,
                agv.MapPositionState.ToString(),
                $"{agv.DisplayName} ({agv.VehicleId})\n坐标 {agv.X:F2}, {agv.Y:F2}\n" +
                $"电量 {(agv.BatteryPercent is null ? "--" : $"{agv.BatteryPercent:F0}%")} · {agv.MapPositionMessage}"));
        }
        var abnormal = _latestSystemStatus.Agvs.Count(agv => agv.MapPositionState != AgvMapPositionState.Normal);
        LiveAgvSummary = $"运行地图V{_latestSystemStatus.Safety.ActiveMapVersionId ?? "?"} · " +
                         $"车辆{_latestSystemStatus.Agvs.Count}台 · 定位异常{abnormal}台 · 每秒刷新";
    }

    private Point? TryMapToCanvas(double mapX, double mapY)
    {
        if (_workspace?.Project.CadViewBox is { } viewBox)
        {
            const double canvasWidth = 900;
            const double canvasHeight = 520;
            var imageScale = Math.Min(canvasWidth / viewBox.PixelWidth, canvasHeight / viewBox.PixelHeight);
            var displayWidth = viewBox.PixelWidth * imageScale;
            var displayHeight = viewBox.PixelHeight * imageScale;
            var offsetX = (canvasWidth - displayWidth) / 2;
            var offsetY = (canvasHeight - displayHeight) / 2;
            var cad = ToCadCoordinates(mapX, mapY, _workspace.Project.Calibration, CoordinateScale);
            return new Point(
                offsetX + (cad.X - viewBox.MinX) / viewBox.Width * displayWidth,
                offsetY + (1 - (cad.Y - viewBox.MinY) / viewBox.Height) * displayHeight);
        }
        return new Point(
            mapX / CanvasWorldWidth * 900,
            (1 - mapY / CanvasWorldHeight) * 520);
    }

    private void AddResourceBoundaryPreviews(Func<MapPoint2D, Point> mapToCanvas)
    {
        foreach (var resource in Resources.Where(resource => IsMapLayerVisible(resource.LayerId)))
        {
            var boundary = resource.ParseBoundary();
            var selected = ReferenceEquals(resource, SelectedResource);
            var canvasPoints = boundary.Select(mapToCanvas).ToArray();
            if (boundary.Count >= 3)
            {
                var points = new PointCollection(canvasPoints);
                points.Freeze();
                PreviewResourceBoundaries.Add(new MapCanvasResourceBoundary(
                    resource.Id, points, selected ? "#66FFA726" : "#334AB7C9",
                    selected ? "#FFFFA726" : "#FF4AB7C9", selected ? 3 : 2));
            }
            if (selected && IsBoundaryDrawMode)
            {
                for (var index = 1; index < canvasPoints.Length; index++)
                    PreviewBoundarySegments.Add(new MapCanvasMeasureSegment(
                        canvasPoints[index - 1].X, canvasPoints[index - 1].Y,
                        canvasPoints[index].X, canvasPoints[index].Y, string.Empty, 0, 0, "#FFFFA726", 3));
            }
            if (selected && (IsBoundaryVertexMode || IsBoundaryDrawMode))
            {
                for (var index = 0; index < canvasPoints.Length; index++)
                    PreviewResourceVertices.Add(new MapCanvasResourceVertex(
                        resource.Id, index, canvasPoints[index].X - 6, canvasPoints[index].Y - 6));
            }
        }
    }

    private void AddPreviewEdges(IReadOnlyDictionary<string, MapCanvasNode> points)
    {
        foreach (var edge in Edges.Where(edge => IsMapLayerVisible(edge.LayerId)))
        {
            if (!points.ContainsKey(edge.From) || !points.ContainsKey(edge.To) ||
                !TryGetEndpoints(edge, out var from, out var to)) continue;
            var domain = edge.ToDomain(Nodes);
            if (!MapEdgeGeometry.TryValidate(domain, from.ToDomain(), to.ToDomain(), out _)) continue;
            var samples = MapEdgeGeometry.Sample(domain, from.ToDomain(), to.ToDomain(), 128);
            var canvasPoints = new PointCollection(samples.Select(point => TryMapToCanvas(point.X, point.Y))
                .Where(point => point.HasValue).Select(point => point!.Value));
            if (canvasPoints.Count < 2) continue;
            canvasPoints.Freeze();
            var middle = canvasPoints.Count / 2;
            var tangentFrom = canvasPoints[Math.Max(0, middle - 1)];
            var tangentTo = canvasPoints[Math.Min(canvasPoints.Count - 1, middle + 1)];
            var midpoint = canvasPoints.Count == 2
                ? new Point((canvasPoints[0].X + canvasPoints[1].X) / 2, (canvasPoints[0].Y + canvasPoints[1].Y) / 2)
                : canvasPoints[middle];
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(canvasPoints[0], false, false);
                context.PolyLineTo(canvasPoints.Skip(1).ToArray(), true, false);
            }
            geometry.Freeze();
            PreviewEdges.Add(new MapCanvasEdge(
                edge.Id, canvasPoints[0].X, canvasPoints[0].Y, canvasPoints[^1].X, canvasPoints[^1].Y, edge.Bidirectional,
                !edge.Enabled ? "#FF54616A" : edge.IsSelected || ReferenceEquals(edge, SelectedEdge) ? "#FF34C759" :
                    IsBoundToSelectedResource(edge.ResourceIds) ? "#FFFFA726" : "#FF5C7180",
                edge.IsSelected || ReferenceEquals(edge, SelectedEdge) || IsBoundToSelectedResource(edge.ResourceIds) ? 6 : 3,
                midpoint.X, midpoint.Y,
                Math.Atan2(tangentTo.Y - tangentFrom.Y, tangentTo.X - tangentFrom.X) * 180 / Math.PI,
                edge.Bidirectional ? "↔" : "▶") { Points = canvasPoints, Geometry = geometry });
        }
    }

    private bool IsBoundToSelectedResource(string resourceIds) => SelectedResource is not null &&
        EditableMapNode.Split(resourceIds).Contains(SelectedResource.Id, StringComparer.OrdinalIgnoreCase);

    private string NodeFill(EditableMapNode node) => _batchSelectedNodes.Contains(node)
        ? "#FFFFD54F"
        : IsBoundToSelectedResource(node.ResourceIds) ? "#FFFFA726" : node.Type switch
        {
            MapNodeType.Station => "#FF34C759",
            MapNodeType.Intersection => "#FFAF7AC5",
            MapNodeType.WaitingPoint => "#FF40C4FF",
            MapNodeType.ChargingPoint => "#FFB7D84B",
            _ => "#FF2FA8FF"
        };

    private static (double X, double Y) ToCadCoordinates(
        double mapX,
        double mapY,
        MapCalibrationSnapshot? calibration,
        double coordinateScale)
    {
        if (calibration is null)
        {
            var scale = coordinateScale > 0 ? coordinateScale : 1;
            return (mapX / scale, mapY / scale);
        }
        var radians = calibration.RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var translatedX = (mapX - calibration.TranslationX) / calibration.Scale;
        var translatedY = (mapY - calibration.TranslationY) / calibration.Scale;
        return (
            cosine * translatedX + sine * translatedY,
            -sine * translatedX + cosine * translatedY);
    }

    private bool CanManage() => !IsBusy && (IsOfflineEditing || _session is not null &&
        (_session.Permissions.HasFlag(TrafficPermission.Administer) || _session.Permissions.HasFlag(TrafficPermission.ManageMap)));
    private void RaiseCommands()
    {
        RefreshEditHistory();
        RaiseOfflineCommands();
        RaisePropertyChanged(nameof(CanEditWorkbench));
        RaisePropertyChanged(nameof(CanOpenNewMap));
        RaisePropertyChanged(nameof(MapConnectionText));
        RaisePropertyChanged(nameof(CurrentUser));
        RefreshCommand.RaiseCanExecuteChanged(); CreateCommand.RaiseCanExecuteChanged(); SaveCommand.RaiseCanExecuteChanged();
        RaiseMapSwitchCommands();
        PublishCommand.RaiseCanExecuteChanged(); ActivateCommand.RaiseCanExecuteChanged(); AddNodeCommand.RaiseCanExecuteChanged(); RemoveNodeCommand.RaiseCanExecuteChanged();
        GenerateUnderlayCommand.RaiseCanExecuteChanged(); SaveCalibrationCommand.RaiseCanExecuteChanged();
        AddCalibrationPointCommand.RaiseCanExecuteChanged(); RemoveCalibrationPointCommand.RaiseCanExecuteChanged();
        AddEdgeCommand.RaiseCanExecuteChanged(); RemoveEdgeCommand.RaiseCanExecuteChanged(); AddResourceCommand.RaiseCanExecuteChanged();
        RemoveResourceCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged(); RedoCommand.RaiseCanExecuteChanged();
        SimulateRouteCommand.RaiseCanExecuteChanged();
        PreviewImportCommand.RaiseCanExecuteChanged();
        RunPreflightCommand.RaiseCanExecuteChanged(); CompareVersionsCommand.RaiseCanExecuteChanged();
        MoveSelectedNodesCommand.RaiseCanExecuteChanged(); DuplicateSelectedNodesCommand.RaiseCanExecuteChanged();
        RemoveSelectedNodesCommand.RaiseCanExecuteChanged();
        SplitSelectedEdgeCommand.RaiseCanExecuteChanged(); ReverseSelectedEdgeCommand.RaiseCanExecuteChanged();
        ApplyEdgeGeometryCommand.RaiseCanExecuteChanged();
        ExportDraftCommand.RaiseCanExecuteChanged(); ExportPublishedCommand.RaiseCanExecuteChanged();
        RefreshMapAuditsCommand.RaiseCanExecuteChanged();
        RefreshVersionHistoryCommand.RaiseCanExecuteChanged(); PreviewHistoryVersionCommand.RaiseCanExecuteChanged();
        RestoreHistoryToEditorCommand.RaiseCanExecuteChanged(); MergeSelectedNodesCommand.RaiseCanExecuteChanged();
        GenerateResourceBoundaryCommand.RaiseCanExecuteChanged();
        SelectAllNodesCommand.RaiseCanExecuteChanged(); ApplyNodeTypeCommand.RaiseCanExecuteChanged();
        AlignLeftCommand.RaiseCanExecuteChanged(); AlignRightCommand.RaiseCanExecuteChanged();
        AlignTopCommand.RaiseCanExecuteChanged(); AlignBottomCommand.RaiseCanExecuteChanged();
        AlignHorizontalCenterCommand.RaiseCanExecuteChanged(); AlignVerticalCenterCommand.RaiseCanExecuteChanged();
        DistributeHorizontalCommand.RaiseCanExecuteChanged(); DistributeVerticalCommand.RaiseCanExecuteChanged();
        RotateClockwiseCommand.RaiseCanExecuteChanged(); RotateCounterClockwiseCommand.RaiseCanExecuteChanged();
        MirrorHorizontalCommand.RaiseCanExecuteChanged(); MirrorVerticalCommand.RaiseCanExecuteChanged();
        AutoConnectSelectedCommand.RaiseCanExecuteChanged(); OrthogonalConnectCommand.RaiseCanExecuteChanged();
        CloseLoopCommand.RaiseCanExecuteChanged(); CleanupTopologyCommand.RaiseCanExecuteChanged();
        AddPreciseNodeCommand.RaiseCanExecuteChanged();
        ApplyBatchEdgePropertiesCommand.RaiseCanExecuteChanged(); RemoveSelectedEdgesCommand.RaiseCanExecuteChanged();
        CopySelectionCommand.RaiseCanExecuteChanged(); PasteSelectionCommand.RaiseCanExecuteChanged();
        DeleteSelectionCommand.RaiseCanExecuteChanged(); SelectAllEdgesCommand.RaiseCanExecuteChanged();
        RaiseBoundaryCommands();
        RefreshTopologyCommands();
    }
    private void Attach(INotifyPropertyChanged item) => AttachCurveAware(item);
    private void LoadEditable<T>(ObservableCollection<T> target, IEnumerable<T> items) where T : INotifyPropertyChanged
    {
        if (ReferenceEquals(target, Edges)) EndCurveControlDrag();
        target.Clear(); foreach (var item in items) { Attach(item); target.Add(item); }
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    { target.Clear(); foreach (var item in source) target.Add(item); }
}

internal static class TrafficControlSessionExtensions
{
    public static string CurrentPermissionText(this TrafficControlSession session) => session.Permissions.ToString();
}

public sealed class EditableMapNode : BindableBase
{
    private string _layerId = "navigation";
    public string LayerId { get => _layerId; set => SetProperty(ref _layerId, value); }
    private string _id = string.Empty; private double _x; private double _y; private MapNodeType _type;
    private bool _enabled = true; private string _resourceIds = string.Empty;
    private string _agvSiteId = string.Empty;
    private bool _isSelected;
    public string Id { get => _id; set => SetProperty(ref _id, value); }
    public double X { get => _x; set => SetProperty(ref _x, value); }
    public double Y { get => _y; set => SetProperty(ref _y, value); }
    public MapNodeType Type { get => _type; set => SetProperty(ref _type, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public string ResourceIds { get => _resourceIds; set => SetProperty(ref _resourceIds, value); }
    public string AgvSiteId { get => _agvSiteId; set => SetProperty(ref _agvSiteId, value); }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public MapNode ToDomain() => new(Id.Trim(), X, Y, Type, Enabled, Split(ResourceIds), string.IsNullOrWhiteSpace(AgvSiteId) ? null : AgvSiteId.Trim()) { LayerId = LayerId };
    public static EditableMapNode From(MapNode value) => new() { LayerId = value.LayerId, Id = value.Id, X = value.X, Y = value.Y, Type = value.Type, Enabled = value.Enabled, ResourceIds = string.Join(';', value.ResourceIds), AgvSiteId = value.AgvSiteId ?? string.Empty };
    internal static string[] Split(string value) => value.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed class EditableMapEdge : BindableBase
{
    private string _layerId = "navigation";
    public string LayerId { get => _layerId; set => SetProperty(ref _layerId, value); }
    private string _id = string.Empty, _from = string.Empty, _to = string.Empty, _resourceIds = string.Empty;
    private double _distance, _speedLimit = 1; private bool _bidirectional = true, _enabled = true;
    private bool _isSelected;
    private MapEdgeGeometryKind _geometryKind;
    private string _controlPointsText = string.Empty;
    public string Id { get => _id; set => SetProperty(ref _id, value); }
    public string From { get => _from; set => SetProperty(ref _from, value); }
    public string To { get => _to; set => SetProperty(ref _to, value); }
    public double Distance { get => _distance; set => SetProperty(ref _distance, value); }
    public double SpeedLimit { get => _speedLimit; set => SetProperty(ref _speedLimit, value); }
    public bool Bidirectional { get => _bidirectional; set => SetProperty(ref _bidirectional, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public string ResourceIds { get => _resourceIds; set => SetProperty(ref _resourceIds, value); }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public MapEdgeGeometryKind GeometryKind
    {
        get => _geometryKind;
        set { if (SetProperty(ref _geometryKind, value)) RaisePropertyChanged(nameof(GeometryLabel)); }
    }
    public string GeometryLabel => GeometryKind switch
    {
        MapEdgeGeometryKind.CircularArc => "圆弧",
        MapEdgeGeometryKind.CubicBezier => "三次贝塞尔",
        _ => "直线"
    };
    public string ControlPointsText { get => _controlPointsText; set => SetProperty(ref _controlPointsText, value); }
    public IReadOnlyList<MapPoint2D> ParseControlPoints()
    {
        if (string.IsNullOrWhiteSpace(ControlPointsText)) return [];
        // Preserve malformed entries as invalid coordinates so validation cannot silently
        // discard a typo and publish a different shape from the one entered by the user.
        return ControlPointsText.Split(';', StringSplitOptions.TrimEntries).Select(item =>
        {
            var pair = item.Split(',', StringSplitOptions.TrimEntries);
            return pair.Length == 2 &&
                   double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                   double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                ? new MapPoint2D(x, y) : new MapPoint2D(double.NaN, double.NaN);
        }).ToArray();
    }
    public void SetControlPoints(IEnumerable<MapPoint2D> points) =>
        ControlPointsText = EditableTrafficResource.FormatBoundary(points);
    public MapEdge ToDomain(IEnumerable<EditableMapNode> nodes)
    {
        var edge = new MapEdge(Id.Trim(), From.Trim(), To.Trim(), Distance, SpeedLimit, Bidirectional, Enabled,
            EditableMapNode.Split(ResourceIds)) { LayerId = LayerId, GeometryKind = GeometryKind, ControlPoints = ParseControlPoints() };
        var from = nodes.FirstOrDefault(item => item.Id.Equals(From, StringComparison.OrdinalIgnoreCase));
        var to = nodes.FirstOrDefault(item => item.Id.Equals(To, StringComparison.OrdinalIgnoreCase));
        if ((GeometryKind != MapEdgeGeometryKind.Line || Distance <= 0) && from is not null && to is not null &&
            MapEdgeGeometry.TryValidate(edge, from.ToDomain(), to.ToDomain(), out _))
            edge = edge with { Distance = MapEdgeGeometry.Length(edge, from.ToDomain(), to.ToDomain()) };
        return edge;
    }
    public static EditableMapEdge FromDomain(MapEdge value) => new()
    {
        LayerId = value.LayerId, Id = value.Id, From = value.From, To = value.To, Distance = value.Distance, SpeedLimit = value.SpeedLimit,
        Bidirectional = value.Bidirectional, Enabled = value.Enabled, ResourceIds = string.Join(';', value.ResourceIds),
        GeometryKind = value.GeometryKind, ControlPointsText = EditableTrafficResource.FormatBoundary(value.ControlPoints ?? [])
    };
}

public sealed class EditableTrafficResource : BindableBase
{
    private string _layerId = "navigation";
    public string LayerId { get => _layerId; set => SetProperty(ref _layerId, value); }
    private string _id = string.Empty; private TrafficResourceType _type; private int _capacity = 1;
    private string _boundary = string.Empty;
    public string Id { get => _id; set => SetProperty(ref _id, value); }
    public TrafficResourceType Type { get => _type; set => SetProperty(ref _type, value); }
    public int Capacity { get => _capacity; set => SetProperty(ref _capacity, value); }
    public string Boundary { get => _boundary; set => SetProperty(ref _boundary, value); }
    public TrafficResourceDefinition ToDomain() => new(Id.Trim(), Type, Capacity, ParseBoundary()) { LayerId = LayerId };
    public IReadOnlyList<MapPoint2D> ParseBoundary()
    {
        if (string.IsNullOrWhiteSpace(Boundary)) return [];
        var result = new List<MapPoint2D>();
        foreach (var item in Boundary.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = item.Split(',', StringSplitOptions.TrimEntries);
            if (pair.Length == 2 &&
                double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                double.IsFinite(x) && double.IsFinite(y))
                result.Add(new MapPoint2D(x, y));
        }
        return result;
    }
    public static string FormatBoundary(IEnumerable<MapPoint2D> points) => string.Join(';',
        points.Select(point => $"{point.X.ToString("R", CultureInfo.InvariantCulture)},{point.Y.ToString("R", CultureInfo.InvariantCulture)}"));
    public static EditableTrafficResource From(TrafficResourceDefinition value) => new()
    {
        LayerId = value.LayerId,
        Id = value.Id,
        Type = value.Type,
        Capacity = value.Capacity,
        Boundary = FormatBoundary(value.Boundary ?? [])
    };
}

public sealed record MapCanvasNode(string Id, double Left, double Top, string Type, string Fill)
{
    public double Opacity {get;init;}=1;public bool CanSelect {get;init;}=true;
    public string Label {get;init;}=Id;public string Tooltip {get;init;}="";public bool ShowLabel {get;init;}=true;
    public double GlyphScale {get;init;}=1;public double LabelScale {get;init;}=1;public double LabelLeft {get;init;}=-4;public double LabelTop {get;init;}=18;
}
public sealed record MapCanvasEdge(
    string Id, double X1, double Y1, double X2, double Y2, bool Bidirectional,
    string Stroke, double Thickness, double MidX, double MidY, double Angle, string DirectionGlyph)
{
    public PointCollection Points { get; init; } = [];
    public Geometry? Geometry { get; init; }
    public double Opacity { get; init; } = 1;
    public bool CanSelect { get; init; } = true;
    public bool ShowDirection {get;init;}=true;public double GlyphScale {get;init;}=1;
}
public sealed record MapCanvasResourceBoundary(
    string Id, PointCollection Points, string Fill, string Stroke, double Thickness)
{ public double Opacity { get; init; } = 1; }
public sealed record MapCanvasResourceVertex(string ResourceId, int Index, double Left, double Top);
public sealed record MapCanvasAgv(
    string VehicleId,
    string DisplayName,
    double Left,
    double Top,
    double Angle,
    string Fill,
    string State,
    string ToolTip);
public sealed record MapCanvasTrackSegment(
    double X1, double Y1, double X2, double Y2, string Stroke, double Thickness);
public sealed record MapCanvasReplayMarker(double Left, double Top, double Angle, string Fill);
public sealed record MapCanvasGridLine(
    double X1, double Y1, double X2, double Y2, string Stroke, double Thickness);
public sealed record MapCanvasMeasureSegment(
    double X1, double Y1, double X2, double Y2, string Label, double LabelX, double LabelY,
    string Stroke, double Thickness);

internal sealed record MapEditorSnapshot(
    IReadOnlyList<MapNode> Nodes,
    IReadOnlyList<MapEdge> Edges,
    IReadOnlyList<TrafficResourceDefinition> Resources)
{
    public IReadOnlyList<MapEditorLayer> Layers { get; init; } = [];
    public string? CurrentLayerId { get; init; }
    public string Operation { get; init; } = "地图编辑";
    public DateTimeOffset RecordedAt { get; init; }
}

internal enum NodeAlignment
{
    Left,
    Right,
    Top,
    Bottom,
    HorizontalCenter,
    VerticalCenter
}

public sealed record RouteSimulationStep(int Sequence, string LogicalNodeId, string AgvSiteId, string NodeType);

public sealed class EditableCadLayer : BindableBase
{
    private string _name = string.Empty; private bool _included;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public bool Included { get => _included; set => SetProperty(ref _included, value); }
    public static EditableCadLayer From(CadLayerSnapshot value) => new() { Name = value.Name, Included = value.Included };
}

public sealed class EditableCalibrationPoint : BindableBase
{
    private double _cadX, _cadY, _mapX, _mapY;
    public double CadX { get => _cadX; set => SetProperty(ref _cadX, value); }
    public double CadY { get => _cadY; set => SetProperty(ref _cadY, value); }
    public double MapX { get => _mapX; set => SetProperty(ref _mapX, value); }
    public double MapY { get => _mapY; set => SetProperty(ref _mapY, value); }
    public MapCalibrationPoint ToDomain() => new(CadX, CadY, MapX, MapY);
    public static EditableCalibrationPoint From(MapCalibrationPoint value) => new()
    { CadX = value.CadX, CadY = value.CadY, MapX = value.MapX, MapY = value.MapY };
}
