using System.Collections.ObjectModel;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;

namespace AgvDispatch.Wpf.ViewModels;

public sealed record TaskCreationVehicleChoice(string? Id, string Label, bool Available, string Explanation);
public sealed record TaskCreationStepRow(int Sequence, string Name, string Location, string VehicleAction,
    string WaitingCondition, string PlcHandshake, string ConfigurationNote)
{
    public string ValidationMessage { get; init; } = "";
    public bool HasErrors => ValidationMessage.Length > 0;
}

public sealed partial class TaskCenterViewModel
{
    private CancellationTokenSource? _creationRequest;
    private ManualTaskCreationOptions? _creationOptions;
    private ManualTaskCreationRequest? _pendingCreation;
    private bool _isCreationOpen, _isCreating;
    private int _creationPriority;
    private string _creationPriorityInput = "0";
    private string _creationMessage = "展开后加载运行地图、工位和车辆。";
    private ManualTaskStationChoice? _creationStation;
    private TaskCreationVehicleChoice? _creationVehicle;
    public ObservableCollection<ManualTaskStationChoice> CreationStations { get; } = [];
    public ObservableCollection<TaskCreationVehicleChoice> CreationVehicles { get; } = [];
    public ObservableCollection<TaskCreationStepRow> CreationStepPreview { get; } = [];
    public string CreationPreviewExplanation => CreationStepPreview.Count == 0
        ? "尚未取得步骤预览，请刷新创建选项；需要前后端版本一致。"
        : "以下为创建前流程预览，PLC 条件为执行到该步时的要求；不表示当前已满足。自动派车的实际车辆和起点在执行时确定。";
    public DelegateCommand RefreshCreationOptionsCommand { get; private set; } = null!;
    public DelegateCommand SubmitTaskCommand { get; private set; } = null!;
    public bool IsCreationOpen
    {
        get => _isCreationOpen;
        set { if (SetProperty(ref _isCreationOpen, value)) { if(value){IsParametersOpen = false; _ = LoadCreationOptionsAsync();}else ClearCreationRoutePreview("创建面板已关闭。"); } }
    }
    public bool IsCreating { get => _isCreating; private set { if (SetProperty(ref _isCreating, value)) { RaisePropertyChanged(nameof(CreationInputsEnabled)); RefreshCreationCommands(); } } }
    public bool CreationInputsEnabled => !IsCreating && !IsRoutePreviewBusy;
    public string CreationMessage { get => _creationMessage; private set => SetProperty(ref _creationMessage, value); }
    public string CreationMapText => _creationOptions?.MapVersionId is { } version ? $"运行地图：{_creationOptions.MapId} / {version}" : "尚未取得运行地图。";
    public string CreationStationExplanation => CreationStation?.Explanation ?? "请选择工位。";
    public string CreationVehicleExplanation => CreationVehicle?.Explanation ?? "请选择车辆。";
    public ManualTaskStationChoice? CreationStation
    {
        get => _creationStation;
        set { if (SetProperty(ref _creationStation, value)) { RaisePropertyChanged(nameof(CreationStationExplanation)); RefreshStepPreview(); RefreshCreationCommands(); } }
    }
    public TaskCreationVehicleChoice? CreationVehicle
    {
        get => _creationVehicle;
        set { if (SetProperty(ref _creationVehicle, value)) { RaisePropertyChanged(nameof(CreationVehicleExplanation)); RefreshStepPreview(); RefreshCreationCommands(); } }
    }
    public int CreationPriority { get => _creationPriority; set { if (SetProperty(ref _creationPriority, value)) RefreshCreationCommands(); } }
    public string CreationPriorityInput
    {
        get => _creationPriorityInput;
        set { if (SetProperty(ref _creationPriorityInput, value)) CreationPriority = int.TryParse(value, out var priority) ? priority : -1; }
    }
    private void InitializeTaskCreation()
    {
        InitializeCreationRoutePreview();
        RefreshCreationOptionsCommand = new(async () => await LoadCreationOptionsAsync(), () => !IsCreating && !IsRoutePreviewBusy);
        SubmitTaskCommand = new(async () => await SubmitTaskAsync(), () => !IsCreating && !IsRoutePreviewBusy && _creationOptions is { Available: true } &&
            CreationStation is { Available: true, ConfigurationValid: true } && CreationVehicle is { Available: true } &&
            CreationStepPreview.Count == 9 && CreationStepPreview.All(step => !step.HasErrors) && CreationPriority is >= 0 and <= 100);
    }
    private void RefreshCreationCommands()
    { RefreshCreationOptionsCommand?.RaiseCanExecuteChanged(); SubmitTaskCommand?.RaiseCanExecuteChanged(); PreviewCreationRoutesCommand?.RaiseCanExecuteChanged(); }

    private void RefreshStepPreview()
    {
        ClearCreationRoutePreview("选项已变化，请重新预览当前后台路线。");
        CreationStepPreview.Clear();
        if (_creationOptions is not null && CreationStation is not null)
        {
            var vehicle = _creationOptions.Vehicles.FirstOrDefault(vehicle => vehicle.VehicleId == CreationVehicle?.Id);
            var vehicleText = CreationVehicle?.Id ?? "自动派车（待分配）";
            foreach (var step in CreationStation.PreviewSteps)
            {
                var errors = step.ValidationErrors.ToList();
                if (step.Sequence == 1 && (CreationVehicle?.Id is { } id
                    ? !CreationStation.PickupReachableVehicleIds.Contains(id, StringComparer.OrdinalIgnoreCase)
                    : !_creationOptions.Vehicles.Any(candidate => candidate.Available && CreationStation.PickupReachableVehicleIds.Contains(candidate.VehicleId, StringComparer.OrdinalIgnoreCase))))
                    errors.Add("所选车辆或自动派车候选没有到取货点的可执行首段路线");
                var source = step.Sequence == 1 ? vehicle?.CurrentSite ?? "派车时确定起点" : step.SourceSite;
                var location = $"{source} → {step.DestinationSite}\nPLC 工位：{step.PlcStationId}";
                CreationStepPreview.Add(new(step.Sequence, step.Name, location,
                    $"{vehicleText} · {step.VehicleAction}", step.WaitingCondition, step.PlcHandshake, step.ConfigurationNote)
                { ValidationMessage = errors.Count == 0 ? "" : $"第 {step.Sequence} 步配置错误：" + string.Join("；", errors) });
            }
        }
        RaisePropertyChanged(nameof(CreationPreviewExplanation));
    }

    public async Task LoadCreationOptionsAsync()
    {
        if (IsCreating || _navigation is null || _navigation.IsCancellationRequested) return;
        _creationRequest?.Cancel(); _creationRequest?.Dispose();
        _creationRequest = CancellationTokenSource.CreateLinkedTokenSource(_navigation.Token);
        var token = _creationRequest.Token;
        IsCreating = true;
        try
        {
            var options = await _client.GetTaskCreationOptionsAsync(token); token.ThrowIfCancellationRequested();
            var stationId = CreationStation?.StationId; var vehicleId = CreationVehicle?.Id;
            _creationOptions = options;
            CreationStations.Clear(); foreach (var station in options.Stations) CreationStations.Add(station);
            CreationVehicles.Clear(); CreationVehicles.Add(options.RequiresExplicitVehicle ? new(null,"请选择车辆（提交前核验车端地图）",false,"必须明确选车，后台将核验真实站点、地图MD5和四段路线。") : new(null, "自动派车", true, "后台按可用车辆和路径选择；提交时会重新校验。"));
            foreach (var vehicle in options.Vehicles) CreationVehicles.Add(new(vehicle.VehicleId, vehicle.Label, vehicle.Available, vehicle.Explanation));
            CreationStation = CreationStations.FirstOrDefault(station => station.StationId == stationId) ?? CreationStations.FirstOrDefault(station => station.Available) ?? CreationStations.FirstOrDefault();
            CreationVehicle = CreationVehicles.FirstOrDefault(vehicle => vehicle.Id == vehicleId) ?? CreationVehicles[0];
            RefreshStepPreview();
            RaisePropertyChanged(nameof(CreationMapText)); CreationMessage = options.Explanation;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _creationOptions = null; RefreshStepPreview(); CreationMessage = "创建选项加载失败：" + exception.Message; RaisePropertyChanged(nameof(CreationMapText)); }
        finally { IsCreating = false; }
    }

    public async Task SubmitTaskAsync()
    {
        if (!SubmitTaskCommand.CanExecute() || _navigation is null || _navigation.IsCancellationRequested) return;
        var options = _creationOptions!;
        var candidate = new ManualTaskCreationRequest("", CreationStation!.StationId, CreationVehicle!.Id, CreationPriority, options.MapId!, options.MapVersionId!)
        { ParameterRevision = options.ParameterRevision };
        // Reuse the exact request after an uncertain transport result; changed inputs receive a new ID.
        if (_pendingCreation is null || _pendingCreation with { RequestId = "" } != candidate)
            _pendingCreation = candidate with { RequestId = Guid.NewGuid().ToString("N") };
        var submitted = _pendingCreation;
        string? confirmedTaskId = null;
        ClearCreationRoutePreview("正在提交；后台将独立重新核验。");
        IsCreating = true;
        try
        {
            var result = await _client.CreateTaskAsync(submitted, _navigation.Token);
            if (!result.Accepted) { CreationMessage = result.Message; _pendingCreation = null; return; }
            confirmedTaskId = result.TaskId;
            _pendingCreation = null;
            CreationMessage = $"{result.Message} 任务编号：{result.TaskId}";
            // Read back this exact task even if it is outside the current list filter or page.
            SelectedTask = null; ClearDetail();
            _selectedFilter = Filters[0]; RaisePropertyChanged(nameof(SelectedFilter)); Search = result.TaskId ?? ""; _page = 1;
            await RefreshAsync();
            var detail = await _client.GetTaskDetailAsync(result.TaskId!, _navigation.Token);
            _navigation.Token.ThrowIfCancellationRequested();
            if (detail is not null)
            {
                _updatingRows = true;
                try { SelectedTask = detail.Task; }
                finally { _updatingRows = false; }
                Detail = detail; Replace(Steps, detail.Steps); Replace(Commands, detail.Commands);
                Replace(Resources, detail.Resources); Replace(PlcConditions, detail.PlcConditions);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            CreationMessage = confirmedTaskId is null ? "提交结果尚未确认，可用相同参数重试核对，避免重复创建：" + exception.Message :
                $"任务 {confirmedTaskId} 已创建，详情加载失败，请刷新任务列表：{exception.Message}";
        }
        finally { IsCreating = false; }
    }
}
