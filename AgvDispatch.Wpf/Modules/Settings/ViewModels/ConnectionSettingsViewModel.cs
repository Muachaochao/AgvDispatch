using System.Collections.ObjectModel;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Wpf.Services;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Regions;

namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class ConnectionSettingsViewModel : BindableBase, INavigationAware
{
    private readonly ITrafficControlClient _client;
    private readonly IUserFeedback? _feedback;
    private CancellationTokenSource? _navigation;
    private CommunicationSettingsSnapshot? _snapshot;
    private CommunicationConfiguration? _configuration;
    private PlcConnectionSetting? _selectedPlc;
    private AgvConnectionSetting? _selectedAgv;
    private AgvMapBindingSetting? _selectedMapBinding;
    private bool _isBusy;
    private string _expectedRevision = "";
    private string _message = "等待后台服务。";
    public ConnectionSettingsViewModel(ITrafficControlClient client, IUserFeedback? feedback = null)
    {
        _client = client;
        _feedback = feedback;
        InitializeMapReview();
        AddMapBindingCommand=new DelegateCommand(()=>
        {
            var item=new AgvMapBindingSetting{VehicleId=SelectedAgv?.VehicleId ?? "",LocalMapId=Snapshot?.RuntimeMapId ?? "",LocalMapVersion=Snapshot?.RuntimeMapVersion ?? ""};
            MapBindings.Add(item);SelectedMapBinding=item;
        },()=>!IsBusy&&CanManage);
        RemoveMapBindingCommand=new DelegateCommand(()=>{if(SelectedMapBinding is not null)MapBindings.Remove(SelectedMapBinding);SelectedMapBinding=MapBindings.FirstOrDefault();},()=>!IsBusy&&CanManage&&SelectedMapBinding is not null);
        ReloadCommand = new DelegateCommand(async () => await ReloadAsync(), () => !IsBusy);
        RefreshStatusCommand = new DelegateCommand(async () => await RefreshStatusAsync(), () => !IsBusy);
        SaveCommand = new DelegateCommand(async () => await SaveAsync(), () => !IsBusy && CanManage && Configuration is not null);
        AddPlcCommand = new DelegateCommand(() =>
        {
            var item = new PlcConnectionSetting { DeviceId = NextId("PLC", Plcs.Select(p => p.DeviceId)) };
            Plcs.Add(item); SelectedPlc = item;
        }, () => !IsBusy && CanManage);
        RemovePlcCommand = new DelegateCommand(() =>
        { if (SelectedPlc is not null) Plcs.Remove(SelectedPlc); SelectedPlc = Plcs.FirstOrDefault(); }, () => !IsBusy && CanManage && SelectedPlc is not null && Plcs.Count > 1);
        AddAgvCommand = new DelegateCommand(() =>
        {
            var item = new AgvConnectionSetting { VehicleId = NextId("AGV", Agvs.Select(p => p.VehicleId)) };
            Agvs.Add(item); SelectedAgv = item;
        }, () => !IsBusy && CanManage);
        RemoveAgvCommand = new DelegateCommand(() =>
        { if (SelectedAgv is not null) Agvs.Remove(SelectedAgv); SelectedAgv = Agvs.FirstOrDefault(); }, () => !IsBusy && CanManage && SelectedAgv is not null);
        ProbePlcCommand = new DelegateCommand(async () => await ProbeAsync(SelectedPlc?.IpAddress, SelectedPlc?.Port), () => !IsBusy && CanManage && SelectedPlc is not null);
        ProbeAgvCommand = new DelegateCommand(async () => await ProbeAsync(SelectedAgv?.IpAddress, SelectedAgv?.Port), () => !IsBusy && CanManage && SelectedAgv is not null);
    }
    public ObservableCollection<AgvMapBindingSetting> MapBindings {get;}=[];
    public AgvMapBindingSetting? SelectedMapBinding {get=>_selectedMapBinding;set{if(ReferenceEquals(_selectedMapBinding,value))return;if(_selectedMapBinding is not null)_selectedMapBinding.PropertyChanged-=MapReviewBindingChanged;SetProperty(ref _selectedMapBinding,value);if(value is not null)value.PropertyChanged+=MapReviewBindingChanged;ClearMapReview("绑定选择已变化，请重新导入核对报告。");UpdateCommands();}}
    public DelegateCommand AddMapBindingCommand {get;}
    public DelegateCommand RemoveMapBindingCommand {get;}
    public string RuntimeMapText=>$"当前运行地图：{Snapshot?.RuntimeMapId ?? "无"} / {Snapshot?.RuntimeMapVersion ?? "无"}";
    public ObservableCollection<PlcConnectionSetting> Plcs { get; } = [];
    public ObservableCollection<AgvConnectionSetting> Agvs { get; } = [];
    public IReadOnlyList<string> SiemensSeries { get; } = ["S1200", "S1500", "S300", "S400", "S200Smart", "S200"];
    public DelegateCommand ReloadCommand { get; }
    public DelegateCommand RefreshStatusCommand { get; }
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand AddPlcCommand { get; }
    public DelegateCommand RemovePlcCommand { get; }
    public DelegateCommand AddAgvCommand { get; }
    public DelegateCommand RemoveAgvCommand { get; }
    public DelegateCommand ProbePlcCommand { get; }
    public DelegateCommand ProbeAgvCommand { get; }
    public CommunicationConfiguration? Configuration { get => _configuration; private set => SetProperty(ref _configuration, value); }
    public CommunicationSettingsSnapshot? Snapshot { get => _snapshot; private set => SetProperty(ref _snapshot, value); }
    public PlcConnectionSetting? SelectedPlc { get => _selectedPlc; set { SetProperty(ref _selectedPlc, value); UpdateCommands(); } }
    public AgvConnectionSetting? SelectedAgv { get => _selectedAgv; set { if(ReferenceEquals(_selectedAgv,value))return;if(_selectedAgv is not null)_selectedAgv.PropertyChanged-=MapReviewBindingChanged;SetProperty(ref _selectedAgv,value);if(value is not null)value.PropertyChanged+=MapReviewBindingChanged;ClearMapReview("车辆选择已变化，请重新导入核对报告。"); UpdateCommands(); } }
    public bool CanManage => Snapshot?.CanManage == true;
    public bool CanSave => !IsBusy && CanManage && Configuration is not null;
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string ApplyStatus => Snapshot is null ? "尚未连接后台" : Snapshot.RestartRequired
        ? "已保存待应用：重启 Windows Service 后生效" : "保存配置与当前运行配置一致";
    public bool IsBusy { get => _isBusy; private set { SetProperty(ref _isBusy, value); UpdateCommands(); } }
    public void ReportValidationError()
    {
        Message = "请修正红框中的数值或输入格式后再保存。";
        _feedback?.Warning(Message);
    }
    public void OnNavigatedTo(NavigationContext context)
    {
        _navigation?.Cancel(); _navigation?.Dispose(); _navigation = new();
        if (Configuration is null) _ = ReloadAsync();
    }
    public bool IsNavigationTarget(NavigationContext context) => true;
    public void OnNavigatedFrom(NavigationContext context) { _navigation?.Cancel(); ClearMapReview("离线核对报告已清空。"); }
    private void Apply(CommunicationSettingsSnapshot snapshot)
    {
        ClearMapReview("配置已重新加载，请重新导入核对报告。");
        _expectedRevision = snapshot.Revision;
        Snapshot = snapshot; Configuration = snapshot.Saved;
        Plcs.Clear(); foreach (var item in Configuration.Plcs) Plcs.Add(item);
        Agvs.Clear(); foreach (var item in Configuration.Agvs) Agvs.Add(item);
        SelectedPlc = Plcs.FirstOrDefault(); SelectedAgv = Agvs.FirstOrDefault();
        MapBindings.Clear();foreach(var item in Configuration.MapBindings ?? [])MapBindings.Add(item);SelectedMapBinding=MapBindings.FirstOrDefault();
        RaisePropertyChanged(nameof(RuntimeMapText));
        RaisePropertyChanged(nameof(CanManage)); RaisePropertyChanged(nameof(ApplyStatus));
    }
    private async Task ReloadAsync()
    {
        if (IsBusy || _navigation is null || _navigation.IsCancellationRequested) return;
        IsBusy = true;
        var token = _navigation.Token;
        try
        {
            var snapshot = await _client.GetCommunicationSettingsAsync(token);
            token.ThrowIfCancellationRequested(); Apply(snapshot);
            Message = CanManage ? "已加载。重新加载会丢弃未保存修改；保存只改变下次启动配置。" : "当前账号仅可查看；需要ManageConnections权限才能保存或测试。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Message = $"加载失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }
    private async Task SaveAsync()
    {
        if (Configuration is null || Snapshot is null || IsBusy || _navigation is null || _navigation.IsCancellationRequested) return;
        IsBusy = true;
        var token = _navigation.Token;
        try
        {
            Configuration.Plcs = Plcs.ToList(); Configuration.Agvs = Agvs.ToList();Configuration.MapBindings=MapBindings.ToList();
            var result = await _client.SaveCommunicationSettingsAsync(new(_expectedRevision, Configuration), token);
            token.ThrowIfCancellationRequested(); Apply(result);
            Message = "保存成功。请在安全停机后重启后台服务；没有自动启用PLC写入、AGV动作或自动调度。";
            _feedback?.Success(Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Message = $"保存失败：{ex.Message}"; _feedback?.Error(Message); }
        finally { IsBusy = false; }
    }
    private async Task ProbeAsync(string? ip, int? port)
    {
        if (IsBusy || ip is null || port is null || _navigation is null || _navigation.IsCancellationRequested) return;
        IsBusy = true;
        var token = _navigation.Token;
        try
        {
            var result = await _client.ProbeConnectionAsync(new(ip, port.Value), token);
            token.ThrowIfCancellationRequested();
            Message = $"{ip}:{port} · {result.ElapsedMs:F0} ms · {result.Message}";
            if (result.Reachable) _feedback?.Success(Message);
            else _feedback?.Warning(Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Message = $"测试失败：{ex.Message}"; _feedback?.Error(Message); }
        finally { IsBusy = false; }
    }
    private static string NextId(string prefix, IEnumerable<string> values)
    {
        var used = values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++) if (!used.Contains($"{prefix}-{n:00}")) return $"{prefix}-{n:00}";
    }
    private void UpdateCommands()
    {
        RaisePropertyChanged(nameof(CanSave));
        ReloadCommand.RaiseCanExecuteChanged(); RefreshStatusCommand.RaiseCanExecuteChanged(); SaveCommand.RaiseCanExecuteChanged();
        AddMapBindingCommand.RaiseCanExecuteChanged();RemoveMapBindingCommand.RaiseCanExecuteChanged();
        AddPlcCommand.RaiseCanExecuteChanged(); RemovePlcCommand.RaiseCanExecuteChanged();
        AddAgvCommand.RaiseCanExecuteChanged(); RemoveAgvCommand.RaiseCanExecuteChanged();
        ProbePlcCommand.RaiseCanExecuteChanged(); ProbeAgvCommand.RaiseCanExecuteChanged();
    }
    private async Task RefreshStatusAsync()
    {
        if (IsBusy || _navigation is null || _navigation.IsCancellationRequested) return;
        var token = _navigation.Token;
        IsBusy = true;
        try
        {
            var snapshot = await _client.GetCommunicationSettingsAsync(token);
            token.ThrowIfCancellationRequested();
            Snapshot = snapshot;
            RaisePropertyChanged(nameof(CanManage)); RaisePropertyChanged(nameof(ApplyStatus));
            Message = "已刷新运行状态，保留本地编辑内容；如配置版本已变化，保存时仍会拒绝覆盖。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Message = $"状态刷新失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }
}
