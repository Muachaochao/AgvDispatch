using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Maps;
using Microsoft.Win32;
using Prism.Commands;
namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class MapManagementViewModel
{
    public ILocalMapEditorStore LocalStore { get; init; } = new LocalMapEditorStore();
    private bool _isOfflineEditing = true;
    private int _editorModeRevision;
    private MapWorkspaceSnapshot? _offlineCheckpoint;
    public bool IsOfflineEditing => _isOfflineEditing;
    public bool IsOnlineEditing => !_isOfflineEditing;
    public string EditorModeText => IsOfflineEditing ? "离线编辑" : "在线发布";
    public string SaveButtonText => IsOfflineEditing ? "保存本地" : "保存草稿";
    public DelegateCommand OfflineModeCommand { get; private set; } = null!;
    public DelegateCommand OnlineModeCommand { get; private set; } = null!;
    public DelegateCommand OpenLocalCommand { get; private set; } = null!;
    public DelegateCommand SaveLocalAsCommand { get; private set; } = null!;
    public DelegateCommand UploadOfflineCommand { get; private set; } = null!;
    public DelegateCommand ValidateLocalCommand { get; private set; } = null!;
    private bool CanUseOnline() => !IsOfflineEditing && CanManage();
    private void InitializeOfflineTools()
    {
        OfflineModeCommand = new DelegateCommand(async () => await EnterOfflineAsync(), () => !IsBusy && !IsOfflineEditing);
        OnlineModeCommand = new DelegateCommand(async () => await EnterOnlineAsync(), () => !IsBusy && IsOfflineEditing);
        OpenLocalCommand = new DelegateCommand(async () =>
        {
            var picker = new OpenFileDialog { Filter = "地图文件 (*.agvmap.json;*.smap)|*.agvmap.json;*.smap|项目离线地图 (*.agvmap.json)|*.agvmap.json|Robokit地图（2D / 3D包二维预览）(*.smap)|*.smap", CheckFileExists = true };
            if (picker.ShowDialog() == true) await OpenLocalFileAsync(picker.FileName);
        }, () => IsOfflineEditing && !IsBusy);
        SaveLocalAsCommand = new DelegateCommand(async () =>
        {
            var picker = new SaveFileDialog { Filter = "离线地图 (*.agvmap.json)|*.agvmap.json", DefaultExt = ".agvmap.json", FileName = "地图.agvmap.json" };
            if (picker.ShowDialog() == true) await SaveLocalCopyAsync(picker.FileName);
        }, () => IsOfflineEditing && !IsBusy && _workspace is not null);
        UploadOfflineCommand = new DelegateCommand(async () => await UploadOfflineAsync(), () => !IsBusy && IsOfflineEditing && _workspace is not null);
        ValidateLocalCommand = new DelegateCommand(() =>
        {
            if (_workspace is null) return;
            var report = LocalStore.Validate(BuildDefinition()); Replace(ValidationIssues, report.Issues);
            StatusMessage = $"本地校验：错误 {report.ErrorCount}，警告 {report.WarningCount}。未发布或激活地图。";
        }, () => !IsBusy && _workspace is not null);
    }
    private void ChangeMode(bool offline)
    {
        _isOfflineEditing = offline;
        _editorModeRevision++;
        _session = null; _mapSwitchStatus = null; _latestSystemStatus = null;
        LiveAgvs.Clear(); PreviewAgvs.Clear(); MapAudits.Clear(); VersionHistory.Clear();
        MapSwitchBlockers.Clear(); MapSwitchHistory.Clear(); ClearTrackReplay();
        _underlayRefreshTimer.Stop(); _liveAgvRefreshTimer.Stop(); ScheduleCadDetail();
        foreach (var property in new[] { nameof(IsOfflineEditing), nameof(IsOnlineEditing), nameof(EditorModeText), nameof(SaveButtonText) }) RaisePropertyChanged(property);
        RaiseCommands(); RaiseMapSwitchCommands();
    }
    private void RaiseOfflineCommands()
    {
        OfflineModeCommand?.RaiseCanExecuteChanged(); OnlineModeCommand?.RaiseCanExecuteChanged(); OpenLocalCommand?.RaiseCanExecuteChanged();
        SaveLocalAsCommand?.RaiseCanExecuteChanged(); UploadOfflineCommand?.RaiseCanExecuteChanged();
        ValidateLocalCommand?.RaiseCanExecuteChanged();
    }
    private MapWorkspaceSnapshot CaptureLocal()
    {
        if (_workspace is null) throw new InvalidOperationException("请先新建或打开本地地图。");
        var definition = BuildDefinition(); var now = DateTimeOffset.Now;
        var changedSource = !string.Equals(BackgroundFilePath, _workspace.Project.BackgroundFilePath, StringComparison.OrdinalIgnoreCase);
        return _workspace with { Project = _workspace.Project with { Name = MapName, Description = Description, BackgroundFilePath = BackgroundFilePath,
            CoordinateSystem = CoordinateSystem, Unit = Unit, CoordinateScale = CoordinateScale, UpdatedAt = now,
            PreviewPngPath = changedSource ? "" : _workspace.Project.PreviewPngPath, PreviewSvgPath = changedSource ? "" : _workspace.Project.PreviewSvgPath,
            CadViewBox = changedSource ? null : _workspace.Project.CadViewBox, Calibration = changedSource ? null : _workspace.Project.Calibration,
            CadLayers = changedSource ? [] : _workspace.Project.CadLayers, UnderlayStatus = changedSource ? CadUnderlayJobStatus.None : _workspace.Project.UnderlayStatus },
            Draft = _workspace.Draft with { Definition = definition, Validation = LocalStore.Validate(definition), UpdatedAt = now, EditToken = Guid.NewGuid().ToString("N") }, Published = null };
    }
    private async Task LoadLocalListAsync(string? mapId = null)
    {
        Replace(Maps, await LocalStore.ListAsync());
        _selectedMap = Maps.FirstOrDefault(p => p.MapId == (mapId ?? _workspace?.Project.MapId)) ?? Maps.FirstOrDefault();
        RaisePropertyChanged(nameof(SelectedMap));
        if (_selectedMap is not null) ApplyWorkspace(await LocalStore.LoadAsync(_selectedMap.MapId));
        StatusMessage = Maps.Count == 0 ? "离线编辑：新建地图或打开本地文件，无需调度服务。" : "离线地图已加载。";
    }
    public async Task EnterOnlineAsync()
    {
        if (IsBusy) return;
        var saved = IsOfflineEditing && _workspace is not null ? CaptureLocal() : null;
        IsBusy = true;
        try
        {
            if (saved is not null) { await LocalStore.SaveAsync(saved); _offlineCheckpoint = saved; }
            // Connection reads only; upload is a separate explicitly requested operation.
            var session = await _client.GetSessionAsync(CancellationToken.None);
            var maps = await _client.GetMapsAsync(CancellationToken.None);
            var workspace = maps.Count == 0 ? null : await _client.GetMapWorkspaceAsync(maps[0].MapId, CancellationToken.None);
            ChangeMode(false); _session = session; _workspace = null; RaisePropertyChanged(nameof(IsSmap3DPlanePreview));
            Replace(Maps, maps); _selectedMap = maps.FirstOrDefault(); RaisePropertyChanged(nameof(SelectedMap));
            if (workspace is not null) ApplyWorkspace(workspace);
            else { Nodes.Clear(); Edges.Clear(); Resources.Clear(); UnderlayImage = null; RebuildPreview(); }
            StatusMessage = "在线发布模式：离线草稿已保留；发布与激活仍需分别操作。";
            if (_cadDetailActive) { _underlayRefreshTimer.Start(); _liveAgvRefreshTimer.Start(); }
        }
        catch (Exception exception) { StatusMessage = $"后台连接失败，保留离线草稿：{exception.Message}"; }
        finally { IsBusy = false; RaiseCommands(); }
    }
    public async Task EnterOfflineAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
        // Preserve unsaved online edits as an independent local copy before disconnecting.
        if (!IsOfflineEditing && _workspace is not null)
        {
            var clone = LocalStore.Create(new(MapName, Description, BackgroundFilePath, CoordinateSystem, Unit, CoordinateScale));
            _offlineCheckpoint = clone with { Project = _workspace.Project with { MapId = clone.Project.MapId, IsActive = false, ActiveVersionId = null, PublishedVersionId = null },
                Draft = clone.Draft with { Definition = BuildDefinition(), Validation = LocalStore.Validate(BuildDefinition()) } };
            await LocalStore.SaveAsync(_offlineCheckpoint);
        }
        ChangeMode(true); _workspace = null; RaisePropertyChanged(nameof(IsSmap3DPlanePreview));
        await LoadLocalListAsync(_offlineCheckpoint?.Project.MapId);
        }
        catch (Exception exception) { StatusMessage = $"切到离线未完成，原编辑区保留：{exception.Message}"; }
        finally { IsBusy = false; RaiseCommands(); }
    }
    public bool IsSmap3DPlanePreview => _workspace?.Project.BackgroundFormat=="SMAP3D-2D";
    public async Task OpenLocalFileAsync(string path)
    {
        if (!IsOfflineEditing || IsBusy) return;
        IsBusy = true;
        try
        {
            if (_workspace is not null) await LocalStore.SaveAsync(CaptureLocal());
            var workspace = await LocalStore.OpenFileAsync(path); ApplyWorkspace(workspace); await LoadLocalListAsync(workspace.Project.MapId);
            if(workspace.Project.BackgroundFormat=="SMAP3D-2D")StatusMessage="3D包的二维部分预览完成（不是三维点云显示，节点/路径停用）："+workspace.Project.UnderlayMessage;
            else if(workspace.Project.BackgroundFormat=="SMAP")StatusMessage="Smap原生导入完成（离线预览，节点/路径停用）："+workspace.Project.UnderlayMessage;
        }
        catch (Exception exception) { StatusMessage = $"本地地图打开失败：{exception.Message}"; }
        finally { IsBusy = false; }
    }
    public async Task SaveLocalCopyAsync(string path)
    {
        if (!IsOfflineEditing || IsBusy || _workspace is null) return;
        try { var snapshot = CaptureLocal(); await LocalStore.ExportAsync(snapshot, path); StatusMessage = $"本地文件已保存：{path}"; }
        catch (Exception exception) { StatusMessage = $"本地文件保存失败：{exception.Message}"; }
    }
    private async Task<bool> SaveOfflineAsync()
    {
        if (_workspace is null || IsBusy) return false;
        IsBusy = true;
        try
        {
            var snapshot = CaptureLocal(); await LocalStore.SaveAsync(snapshot); _workspace = snapshot; _offlineCheckpoint = snapshot;
            Replace(ValidationIssues, snapshot.Draft.Validation.Issues);
            StatusMessage = $"本地草稿已保存：{LocalStore.DirectoryPath}（允许保留未完成的编辑）";
            RaisePropertyChanged(nameof(VersionText)); return true;
        }
        catch (Exception exception) { StatusMessage = $"本地保存失败：{exception.Message}"; return false; }
        finally { IsBusy = false; }
    }
    public async Task UploadOfflineAsync()
    {
        if (!IsOfflineEditing || IsBusy || _workspace is null) return;
        var local = CaptureLocal(); IsBusy = true;
        try
        {
            await LocalStore.SaveAsync(local); _offlineCheckpoint = local;
            var session = await _client.GetSessionAsync(CancellationToken.None);
            if (!session.Permissions.HasFlag(TrafficPermission.ManageMap) && !session.Permissions.HasFlag(TrafficPermission.Administer)) throw new UnauthorizedAccessException("后台不允许地图写入。");
            if (!local.Draft.Validation.IsValid) throw new InvalidOperationException("本地校验存在错误，请先修正再上传。");
            var created = await _client.CreateMapAsync(new(MapName + "（离线上传）", Description, BackgroundFilePath, CoordinateSystem, Unit, CoordinateScale), CancellationToken.None);
            if (!created.Accepted || created.Workspace is not { } target) throw new InvalidOperationException(created.Message);
            var saved = await _client.SaveMapDraftAsync(new(target.Project.MapId, target.Draft.VersionId, target.Project.Name, Description, BackgroundFilePath,
                CoordinateSystem, Unit, CoordinateScale, local.Draft.Definition with { Name = target.Project.Name }, target.Draft.EditToken), CancellationToken.None);
            if (!saved.Accepted || saved.Workspace is null) throw new InvalidOperationException($"后台已创建草稿 {target.Project.MapId}，上传未完成；本地文件保留。{saved.Message}");
            ChangeMode(false); _session = session; ApplyWorkspace(saved.Workspace); await ReloadMapListOnlyAsync(target.Project.MapId);
            StatusMessage = "已上传为新的后台草稿，未发布、未激活。CAD底图与标定须在后台核对并生成。";
            if (_cadDetailActive) { _underlayRefreshTimer.Start(); _liveAgvRefreshTimer.Start(); }
        }
        catch (Exception exception) { StatusMessage = $"离线草稿保留，上传未完成：{exception.Message}"; }
        finally { IsBusy = false; RaiseCommands(); }
    }
}
