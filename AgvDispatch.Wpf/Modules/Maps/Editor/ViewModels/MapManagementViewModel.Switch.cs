using System.Collections.ObjectModel;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;

namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class MapManagementViewModel
{
    private MapSwitchStatus? _mapSwitchStatus;
    private string _mapSwitchSummary = "等待读取安全切换状态。";
    private string _mapSwitchEvidence = "";
    private bool _siteSafetyConfirmed;
    private bool _vehicleMapCompatibilityConfirmed;
    private bool _switchRefreshRunning;
    private bool _originalWorkflowConfirmed;
    public bool OriginalWorkflowConfirmed { get => _originalWorkflowConfirmed; set => SetProperty(ref _originalWorkflowConfirmed, value); }
    public string MapSwitchSummary { get => _mapSwitchSummary; private set => SetProperty(ref _mapSwitchSummary, value); }
    public string MapSwitchEvidence { get => _mapSwitchEvidence; set { SetProperty(ref _mapSwitchEvidence, value); ActivateCommand?.RaiseCanExecuteChanged(); } }
    public bool SiteSafetyConfirmed { get => _siteSafetyConfirmed; set { SetProperty(ref _siteSafetyConfirmed, value); ActivateCommand?.RaiseCanExecuteChanged(); } }
    public bool VehicleMapCompatibilityConfirmed { get => _vehicleMapCompatibilityConfirmed; set { SetProperty(ref _vehicleMapCompatibilityConfirmed, value); ActivateCommand?.RaiseCanExecuteChanged(); } }
    public ObservableCollection<string> MapSwitchBlockers { get; } = [];
    public ObservableCollection<MapSwitchSnapshot> MapSwitchHistory { get; } = [];
    public DelegateCommand BeginMapSwitchCommand { get; private set; } = null!;
    public DelegateCommand RefreshMapSwitchCommand { get; private set; } = null!;
    public DelegateCommand ContinueMapDrainCommand { get; private set; } = null!;
    public DelegateCommand ResumeMapSwitchCommand { get; private set; } = null!;
    public DelegateCommand CancelMapSwitchCommand { get; private set; } = null!;

    private void InitializeMapSwitchTools()
    {
        BeginMapSwitchCommand = new DelegateCommand(async () => await RunMapSwitchAsync("begin"),
            () => CanManage() && _workspace?.Published is not null && _mapSwitchStatus?.Current is null && _mapSwitchStatus?.Mode == MapExecutionMode.Running);
        RefreshMapSwitchCommand = new DelegateCommand(async () => await RefreshMapSwitchAsync(), () => !IsBusy && IsOnlineEditing);
        ContinueMapDrainCommand = new DelegateCommand(async () => await RunMapSwitchAsync("continue"),
            () => CanManage() && _mapSwitchStatus?.Mode == MapExecutionMode.Held && _mapSwitchStatus.Current?.Phase == MapSwitchPhase.Draining);
        ResumeMapSwitchCommand = new DelegateCommand(async () => await RunMapSwitchAsync("resume"),
            () => CanManage() && _mapSwitchStatus?.Current?.Phase == MapSwitchPhase.AwaitingResume);
        CancelMapSwitchCommand = new DelegateCommand(async () => await RunMapSwitchAsync("cancel"),
            () => CanManage() && _mapSwitchStatus?.Current?.Phase == MapSwitchPhase.Draining);
    }

    private async Task RefreshMapSwitchAsync()
    {
        if (IsOfflineEditing || _switchRefreshRunning) return;
        _switchRefreshRunning = true;
        var revision = _editorModeRevision;
        try
        {
            var status = await _client.GetMapSwitchAsync(CancellationToken.None);
            if (IsOfflineEditing || revision != _editorModeRevision) return;
            _mapSwitchStatus = status;
            var current = _mapSwitchStatus.Current;
            var mode = _mapSwitchStatus.Mode switch
            {
                MapExecutionMode.Running => "正常派单", MapExecutionMode.Draining => "停止新增派单，旧任务收尾",
                MapExecutionMode.Held => "派单与后续动作暂停，等待现场核对", _ => "初始化中"
            };
            MapSwitchSummary = current is null ? mode : $"{mode}\n切换：{current.ReleaseId}\n目标版本：{current.VersionId}";
            Replace(MapSwitchBlockers, _mapSwitchStatus.Blockers);
            Replace(MapSwitchHistory, _mapSwitchStatus.History);
            RaiseMapSwitchCommands();
        }
        catch (Exception exception)
        {
            _mapSwitchStatus = null;
            MapSwitchSummary = $"切换状态读取失败：{exception.Message}";
            RaiseMapSwitchCommands();
        }
        finally { _switchRefreshRunning = false; }
    }

    private async Task RunMapSwitchAsync(string action)
    {
        if (!CanUseOnline()) return;
        IsBusy = true;
        try
        {
            MapSwitchResult result;
            if (action == "begin" && _workspace?.Published is { } published)
                result = await _client.BeginMapSwitchAsync(new(_workspace.Project.MapId, published.VersionId), CancellationToken.None);
            else if (_mapSwitchStatus?.Current is { } current)
            {
                var request = new FinishMapSwitchRequest(current.ReleaseId, SiteSafetyConfirmed,
                    VehicleMapCompatibilityConfirmed, MapSwitchEvidence, OriginalWorkflowConfirmed);
                if (action == "continue") result = await _client.ContinueMapDrainAsync(request, CancellationToken.None);
                else result = action == "cancel"
                    ? await _client.CancelMapSwitchAsync(request, CancellationToken.None)
                    : await _client.ResumeMapSwitchAsync(request, CancellationToken.None);
            }
            else return;
            StatusMessage = result.Message;
            if (result.Accepted)
            {
                OriginalWorkflowConfirmed = false;
                SiteSafetyConfirmed = false;
                VehicleMapCompatibilityConfirmed = false;
                MapSwitchEvidence = "";
            }
        }
        catch (Exception exception) { StatusMessage = $"安全切换操作失败：{exception.Message}"; }
        finally { IsBusy = false; await RefreshMapSwitchAsync(); }
    }

    private bool CanActivateMapSafely() => IsOnlineEditing && _mapSwitchStatus?.Current is { Phase: MapSwitchPhase.Draining } current &&
        _workspace?.Published is { } published && current.MapId == _workspace.Project.MapId &&
        current.VersionId == published.VersionId && SiteSafetyConfirmed && VehicleMapCompatibilityConfirmed &&
        !string.IsNullOrWhiteSpace(MapSwitchEvidence);

    private void RaiseMapSwitchCommands()
    {
        ActivateCommand?.RaiseCanExecuteChanged();
        BeginMapSwitchCommand?.RaiseCanExecuteChanged(); RefreshMapSwitchCommand?.RaiseCanExecuteChanged();
        ContinueMapDrainCommand?.RaiseCanExecuteChanged();
        ResumeMapSwitchCommand?.RaiseCanExecuteChanged(); CancelMapSwitchCommand?.RaiseCanExecuteChanged();
    }
}
