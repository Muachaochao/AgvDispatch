using System.Collections.ObjectModel;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Regions;
namespace AgvDispatch.Wpf.ViewModels;

public sealed class ExecutionRecoveryViewModel : BindableBase, INavigationAware
{
    private readonly ITrafficControlClient _client;
    private readonly DispatcherTimer _timer;
    private RecoveryStatus? _snapshot;
    private RecoveryItem? _selected;
    private bool _busy, _vehicle, _load, _plc, _authorized;
    private bool _updatingSelection;
    private string? _targetTaskId;
    private string _reason = "", _material = "", _message = "等待加载启动恢复状态。";
    public ExecutionRecoveryViewModel(ITrafficControlClient client)
    {
        _client = client;
        RefreshCommand = new DelegateCommand(async () => await RefreshAsync(), () => !_busy);
        ContinueCommand = new DelegateCommand(async () => await DecideAsync(RecoveryDecision.ContinueOriginal), () => CanDecide() && Selected?.TaskStage is not (110 or 120 or 130));
        CloseCancelledCommand = new DelegateCommand(async () => await DecideAsync(RecoveryDecision.CloseCancelled), () => CanDecide() && Selected?.TaskStage is 130 or 110);
        CompleteCommand = new DelegateCommand(async () => await DecideAsync(RecoveryDecision.RecordPhysicalCompletion), () => CanDecide() && Selected?.TaskStage is not (110 or 120 or 130));
        ReleaseCommand = new DelegateCommand(async () => await ReleaseAsync(), () => !_busy && _authorized && _snapshot?.Released == false && Items.All(i => i.Verified && !i.IdentityBlocksRecovery) && Reason.Trim().Length >= 10);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }
    public ObservableCollection<RecoveryItem> Items { get; } = [];
    public ObservableCollection<RecoveryAudit> Audits { get; } = [];
    public DelegateCommand RefreshCommand { get; }
    public DelegateCommand ContinueCommand { get; }
    public DelegateCommand CloseCancelledCommand { get; }
    public DelegateCommand CompleteCommand { get; }
    public DelegateCommand ReleaseCommand { get; }
    public RecoveryItem? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value) && !_updatingSelection) { VehicleConfirmed = false; LoadConfirmed = false; PlcConfirmed = false; Material = value?.MaterialReference ?? ""; Changed(); } }
    }
    public string Reason { get => _reason; set { SetProperty(ref _reason, value); Changed(); } }
    public string Material { get => _material; set { SetProperty(ref _material, value); Changed(); } }
    public bool VehicleConfirmed { get => _vehicle; set { SetProperty(ref _vehicle, value); Changed(); } }
    public bool LoadConfirmed { get => _load; set { SetProperty(ref _load, value); Changed(); } }
    public bool PlcConfirmed { get => _plc; set { SetProperty(ref _plc, value); Changed(); } }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string BarrierText => _snapshot == null ? "未连接：不可处置" : _snapshot.Released ? "现场恢复已放行" : "现场恢复等待核对";
    public string PermissionText => _authorized ? "已连接后台，可按安全条件处置恢复" : "等待后台连接，暂不可处置恢复";
    private bool CanDecide() => !_busy && _authorized && _snapshot?.Released == false && Selected?.Verified == false && Selected.IdentityBlocksRecovery == false &&
        VehicleConfirmed && LoadConfirmed && PlcConfirmed && Material.Trim().Length > 0 && Reason.Trim().Length >= 10;
    private void Changed()
    { RefreshCommand?.RaiseCanExecuteChanged(); ContinueCommand?.RaiseCanExecuteChanged(); CompleteCommand?.RaiseCanExecuteChanged(); ReleaseCommand?.RaiseCanExecuteChanged(); CloseCancelledCommand?.RaiseCanExecuteChanged(); }
    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true; Changed();
        try
        {
            var session = await _client.GetSessionAsync(CancellationToken.None);
            _authorized = (session.Permissions & (TrafficPermission.Administer | TrafficPermission.RecoverExecution)) != 0;
            var snapshot = await _client.GetExecutionRecoveryAsync(CancellationToken.None);
            var selected = _selected?.TicketId;
            var previousRevision = _selected?.Revision;
            _snapshot = snapshot;
            if (Message == "等待加载启动恢复状态。") Message = snapshot.Message;
            _updatingSelection = true;
            Items.Clear(); foreach (var i in snapshot.Items) Items.Add(i);
            // Preserve typed attestations only while the same database node remains current.
            _selected = Items.FirstOrDefault(i => i.TicketId == selected) ?? Items.FirstOrDefault(i => i.TaskId == _targetTaskId);
            RaisePropertyChanged(nameof(Selected));
            _updatingSelection = false;
            if (_selected?.Revision != previousRevision) { VehicleConfirmed = false; LoadConfirmed = false; PlcConfirmed = false; }
            Audits.Clear(); foreach (var a in snapshot.Audits) Audits.Add(a);
            RaisePropertyChanged(nameof(BarrierText)); RaisePropertyChanged(nameof(PermissionText));
        }
        catch (Exception e) { _snapshot = null; Message = "读取恢复状态失败：" + e.Message; RaisePropertyChanged(nameof(BarrierText)); }
        finally { _updatingSelection = false; _busy = false; Changed(); }
    }
    private async Task DecideAsync(RecoveryDecision decision)
    {
        if (!CanDecide() || Selected == null || _snapshot == null) return;
        _busy = true; Changed();
        try
        {
            var result = await _client.DecideExecutionRecoveryAsync(new(_snapshot.SessionId, Selected.TicketId, Selected.Revision,
                decision, Material, Reason, VehicleConfirmed, LoadConfirmed, PlcConfirmed), CancellationToken.None);
            Message = result.Message;
            if (result.Accepted) { VehicleConfirmed = false; LoadConfirmed = false; PlcConfirmed = false; Reason = ""; }
        }
        catch (Exception e) { Message = "恢复处置失败：" + e.Message; }
        finally { _busy = false; Changed(); }
        await RefreshAsync();
    }
    private async Task ReleaseAsync()
    {
        if (_snapshot == null || _busy) return;
        _busy = true; Changed();
        try { Message = (await _client.ReleaseExecutionRecoveryAsync(new(_snapshot.SessionId, Reason), CancellationToken.None)).Message; }
        catch (Exception e) { Message = "恢复放行失败：" + e.Message; }
        finally { _busy = false; Changed(); }
        await RefreshAsync();
    }
    public async void OnNavigatedTo(NavigationContext context) { _targetTaskId = context?.Parameters.GetValue<string>("taskId"); await RefreshAsync(); _timer.Start(); }
    public bool IsNavigationTarget(NavigationContext context) => true;
    public void OnNavigatedFrom(NavigationContext context) => _timer.Stop();
}
