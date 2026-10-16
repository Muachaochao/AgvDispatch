using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using Prism.Commands;
using Prism.Mvvm;

namespace AgvDispatch.Wpf.ViewModels;

public sealed class TaskPickupEditor : BindableBase
{
    private string _stationId = "", _pickupSite = "";
    public string StationId { get => _stationId; set => SetProperty(ref _stationId, value); }
    public string PickupSite { get => _pickupSite; set => SetProperty(ref _pickupSite, value); }
}
public sealed class TaskActionEditor : BindableBase
{
    public DispatchTaskStepType StepType { get; init; }
    public string Name => StepType switch { DispatchTaskStepType.GoToPickup => "1 · 前往取货 / 取箱", DispatchTaskStepType.DeliverFullBoxToDumper => "3 · 送往倒料等待点",
        DispatchTaskStepType.EnterDumpingPosition => "5 · 进入倒料位", _ => "7 · 返空箱 / 放箱" };
    private string _operation = "", _height = "";
    private bool? _recognize, _pgv, _downPgv;
    public string Operation { get => _operation; set => SetProperty(ref _operation, value); }
    public string HeightInput { get => _height; set => SetProperty(ref _height, value); }
    public bool? Recognize { get => _recognize; set { if (SetProperty(ref _recognize, value)) RaisePropertyChanged(nameof(RecognizeInput)); } }
    public bool? UsePgv { get => _pgv; set { if (SetProperty(ref _pgv, value)) RaisePropertyChanged(nameof(PgvInput)); } }
    public bool? UseDownPgv { get => _downPgv; set { if (SetProperty(ref _downPgv, value)) RaisePropertyChanged(nameof(DownPgvInput)); } }
    private static string Label(bool? value) => value is true ? "是" : value is false ? "否" : "不指定";
    private static bool? Value(string label) => label == "是" ? true : label == "否" ? false : null;
    public string RecognizeInput { get => Label(Recognize); set => Recognize = Value(value); }
    public string PgvInput { get => Label(UsePgv); set => UsePgv = Value(value); }
    public string DownPgvInput { get => Label(UseDownPgv); set => UseDownPgv = Value(value); }
    public TaskActionParameter ToParameter()
    {
        double? height = null;
        if (!string.IsNullOrWhiteSpace(HeightInput))
        {
            if (!double.TryParse(HeightInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed) || parsed < 0)
                throw new ArgumentException($"{Name}：高度需填写非负数值，留空表示不指定。");
            height = parsed;
        }
        return new(StepType, Operation, height, Recognize, UsePgv, UseDownPgv);
    }
}

public sealed class TaskTimeoutEditor : BindableBase
{
    public DispatchTaskStepType StepType { get; init; }
    public string Name => StepType switch
    {
        DispatchTaskStepType.GoToPickup => "1 · 前往取货", DispatchTaskStepType.PickupFullBox => "2 · 取箱握手",
        DispatchTaskStepType.DeliverFullBoxToDumper => "3 · 前往倒料等待点", DispatchTaskStepType.WaitForDumperReady => "4 · 等待倒料机就绪",
        DispatchTaskStepType.EnterDumpingPosition => "5 · 进入倒料位", DispatchTaskStepType.DumpFullBox => "6 · 倒料握手",
        DispatchTaskStepType.ReturnEmptyBox => "7 · 返空箱", DispatchTaskStepType.PlaceEmptyBox => "8 · 放箱握手", _ => "9 · PLC 收尾"
    };
    private string _secondsInput = "0";
    public string SecondsInput { get => _secondsInput; set => SetProperty(ref _secondsInput, value); }
    public TaskStepTimeoutParameter ToParameter()
    {
        if (!int.TryParse(SecondsInput, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 0 or > 604800)
            throw new ArgumentException($"{Name}：超时阈值需填写 0–604800 整数秒，0 表示关闭。");
        return new(StepType, seconds);
    }
}

public sealed partial class TaskCenterViewModel
{
    private TaskParameterSnapshot? _parameterSnapshot;
    private TaskParameterRequest? _pendingParameterSave;
    private bool _isParametersOpen, _isParametersBusy, _applyingParameters;
    private string _dumperStationInput = "", _waitingSiteInput = "", _dumpingSiteInput = "", _parameterMessage = "展开后加载后台当前任务参数。";
    private TaskPickupEditor? _selectedParameterPickup;
    public ObservableCollection<TaskPickupEditor> ParameterPickups { get; } = [];
    public ObservableCollection<TaskTimeoutEditor> ParameterTimeouts { get; } = [];
    public ObservableCollection<TaskActionEditor> ParameterActions { get; } = [];
    public ObservableCollection<TaskParameterValidationRow> ParameterValidationSteps { get; } = [];
    public IReadOnlyList<string> ParameterOperations { get; } = ["Wait", "JackLoad", "JackUnload", "JackHeight"];
    public IReadOnlyList<string> ParameterBooleans { get; } = ["不指定", "是", "否"];
    public DelegateCommand LoadTaskParametersCommand { get; private set; } = null!;
    public DelegateCommand ValidateTaskParametersCommand { get; private set; } = null!;
    public DelegateCommand SaveTaskParametersCommand { get; private set; } = null!;
    public DelegateCommand AddParameterPickupCommand { get; private set; } = null!;
    public DelegateCommand RemoveParameterPickupCommand { get; private set; } = null!;
    public bool IsParametersOpen
    {
        get => _isParametersOpen;
        set { if (SetProperty(ref _isParametersOpen, value) && value) { IsCreationOpen = false; _ = LoadTaskParametersAsync(); } }
    }
    public bool IsParametersBusy { get => _isParametersBusy; private set { if (SetProperty(ref _isParametersBusy, value)) { RaisePropertyChanged(nameof(ParameterInputsEnabled)); UpdateParameterCommands(); } } }
    public bool ParameterInputsEnabled => !IsParametersBusy && _parameterSnapshot is not null;
    public string ParameterMessage { get => _parameterMessage; private set => SetProperty(ref _parameterMessage, value); }
    public string ParameterRevisionText => _parameterSnapshot is null ? "尚未加载参数。" : $"参数版本：{_parameterSnapshot.Revision} · 运行地图：{_parameterSnapshot.MapId} / {_parameterSnapshot.MapVersionId}";
    public string DumperStationInput { get => _dumperStationInput; set { if (SetProperty(ref _dumperStationInput, value)) ParameterEdited(); } }
    public string WaitingSiteInput { get => _waitingSiteInput; set { if (SetProperty(ref _waitingSiteInput, value)) ParameterEdited(); } }
    public string DumpingSiteInput { get => _dumpingSiteInput; set { if (SetProperty(ref _dumpingSiteInput, value)) ParameterEdited(); } }
    public TaskPickupEditor? SelectedParameterPickup { get => _selectedParameterPickup; set { if (SetProperty(ref _selectedParameterPickup, value)) UpdateParameterCommands(); } }
    private void InitializeTaskParameters()
    {
        LoadTaskParametersCommand = new(async () => await LoadTaskParametersAsync(), () => !IsParametersBusy);
        ValidateTaskParametersCommand = new(async () => await ValidateTaskParametersAsync(), () => !IsParametersBusy && _parameterSnapshot?.MapVersionId is not null);
        SaveTaskParametersCommand = new(async () => await SaveTaskParametersAsync(), () => !IsParametersBusy && _parameterSnapshot is { CanSave: true });
        AddParameterPickupCommand = new(() => { var row = new TaskPickupEditor(); Watch(row); ParameterPickups.Add(row); SelectedParameterPickup = row; ParameterEdited(); }, () => ParameterInputsEnabled && ParameterPickups.Count < 100);
        RemoveParameterPickupCommand = new(() => { if (SelectedParameterPickup is { } row) { ParameterPickups.Remove(row); SelectedParameterPickup = ParameterPickups.FirstOrDefault(); ParameterEdited(); } }, () => ParameterInputsEnabled && SelectedParameterPickup is not null);
    }
    private void Watch(BindableBase row) => row.PropertyChanged += (_, _) => ParameterEdited();
    private void ParameterEdited()
    {
        if (_applyingParameters) return;
        ParameterValidationSteps.Clear(); ParameterMessage = "参数已修改，请校验后保存。"; UpdateParameterCommands();
    }
    private void UpdateParameterCommands()
    {
        LoadTaskParametersCommand?.RaiseCanExecuteChanged(); ValidateTaskParametersCommand?.RaiseCanExecuteChanged(); SaveTaskParametersCommand?.RaiseCanExecuteChanged();
        AddParameterPickupCommand?.RaiseCanExecuteChanged(); RemoveParameterPickupCommand?.RaiseCanExecuteChanged();
    }
    private void ApplyParameterSnapshot(TaskParameterSnapshot snapshot)
    {
        _applyingParameters = true;
        try
        {
            _parameterSnapshot = snapshot; _pendingParameterSave = null;
            DumperStationInput = snapshot.Configuration.DumperStationId; WaitingSiteInput = snapshot.Configuration.WaitingSite; DumpingSiteInput = snapshot.Configuration.DumpingSite;
            ParameterPickups.Clear();
            foreach (var pickup in snapshot.Configuration.Pickups) { var row = new TaskPickupEditor { StationId = pickup.StationId, PickupSite = pickup.PickupSite }; Watch(row); ParameterPickups.Add(row); }
            ParameterActions.Clear();
            foreach (var action in snapshot.Configuration.Actions)
            {
                var row = new TaskActionEditor { StepType = action.StepType, Operation = action.Operation, HeightInput = action.JackHeight?.ToString("G", CultureInfo.InvariantCulture) ?? "",
                    Recognize = action.Recognize, UsePgv = action.UsePgv, UseDownPgv = action.UseDownPgv }; Watch(row); ParameterActions.Add(row);
            }
            ParameterTimeouts.Clear();
            foreach (var timeout in snapshot.Configuration.StepTimeouts.OrderBy(row => row.StepType))
            {
                var row = new TaskTimeoutEditor { StepType = timeout.StepType, SecondsInput = timeout.Seconds.ToString(CultureInfo.InvariantCulture) };
                Watch(row); ParameterTimeouts.Add(row);
            }
            SelectedParameterPickup = ParameterPickups.FirstOrDefault(); ParameterMessage = snapshot.Explanation;
            RaisePropertyChanged(nameof(ParameterRevisionText)); RaisePropertyChanged(nameof(ParameterInputsEnabled));
        }
        finally { _applyingParameters = false; UpdateParameterCommands(); }
    }
    private TaskParameterRequest BuildParameterRequest() => new("", _parameterSnapshot!.Revision, _parameterSnapshot.MapId ?? "", _parameterSnapshot.MapVersionId ?? "",
        new(DumperStationInput, WaitingSiteInput, DumpingSiteInput, ParameterPickups.Select(row => new TaskPickupParameter(row.StationId, row.PickupSite)).ToArray(), ParameterActions.Select(row => row.ToParameter()).ToArray()) { StepTimeouts = ParameterTimeouts.Select(row => row.ToParameter()).ToArray() });
    public async Task LoadTaskParametersAsync()
    {
        if (IsParametersBusy || _navigation is null || _navigation.IsCancellationRequested) return;
        IsParametersBusy = true;
        try { var snapshot = await _client.GetTaskParametersAsync(_navigation.Token); _navigation.Token.ThrowIfCancellationRequested(); ApplyParameterSnapshot(snapshot); ParameterValidationSteps.Clear(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _parameterSnapshot = null; ParameterMessage = "任务参数加载失败：" + error.Message; RaisePropertyChanged(nameof(ParameterInputsEnabled)); }
        finally { IsParametersBusy = false; }
    }
    public async Task ValidateTaskParametersAsync()
    {
        if (!ValidateTaskParametersCommand.CanExecute() || _navigation is null) return;
        IsParametersBusy = true;
        try { var result = await _client.ValidateTaskParametersAsync(BuildParameterRequest() with { RequestId = Guid.NewGuid().ToString("N") }, _navigation.Token); _navigation.Token.ThrowIfCancellationRequested(); Replace(ParameterValidationSteps, result.Steps); ParameterMessage = result.Message; }
        catch (OperationCanceledException) { }
        catch (Exception error) { ParameterValidationSteps.Clear(); ParameterMessage = "参数校验失败：" + error.Message; }
        finally { IsParametersBusy = false; }
    }
    public async Task SaveTaskParametersAsync()
    {
        if (!SaveTaskParametersCommand.CanExecute() || _navigation is null) return;
        IsParametersBusy = true;
        var sent = false;
        try
        {
            var candidate = BuildParameterRequest();
            if (_pendingParameterSave is null || JsonSerializer.Serialize(_pendingParameterSave with { RequestId = "" }) != JsonSerializer.Serialize(candidate))
                _pendingParameterSave = candidate with { RequestId = Guid.NewGuid().ToString("N") };
            sent = true;
            var result = await _client.SaveTaskParametersAsync(_pendingParameterSave, _navigation.Token); _navigation.Token.ThrowIfCancellationRequested();
            Replace(ParameterValidationSteps, result.Steps); ParameterMessage = result.Message;
            if (result.Accepted && result.Snapshot is { } snapshot)
            {
                ApplyParameterSnapshot(snapshot); ParameterMessage = result.Message;
                await LoadCreationOptionsAsync();
            }
            else _pendingParameterSave = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ParameterMessage = sent ? "保存结果尚未确认；保持参数重试核对，或重新加载查看当前版本：" + error.Message : error.Message; }
        finally { IsParametersBusy = false; }
    }
}
