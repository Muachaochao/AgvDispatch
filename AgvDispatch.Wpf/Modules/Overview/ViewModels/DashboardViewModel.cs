using System.Collections.ObjectModel;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using Prism.Mvvm;
using Prism.Regions;
using Prism.Commands;

namespace AgvDispatch.Wpf.ViewModels;

public sealed class DashboardViewModel : BindableBase, INavigationAware
{
    private readonly ITrafficControlClient _client;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _refreshing;
    private string _updateText = "正在读取后台状态…";
    private string? _loadedMapKey;
    private OverviewVehicle? _selectedVehicle;
    private string _mapTitle = "运行地图 · 未激活";
    private bool _isAgvPanelOpen;
    public DashboardViewModel(ITrafficControlClient client)
    {
        _client = client;
        _timer.Tick += async (_, _) => await RefreshAsync();
        SelectVehicleCommand = new DelegateCommand<string>(id =>
        {
            if (OnlineVehicles.FirstOrDefault(v => v.VehicleId == id) is not { } vehicle) return;
            SelectedVehicle = vehicle;
            IsAgvPanelOpen = true;
        });
        SetUnavailable("等待后台连接");
    }
    public ObservableCollection<OverviewMetric> Metrics { get; } = [];
    public ObservableCollection<OverviewMetric> Connections { get; } = [];
    public ObservableCollection<DispatchTaskDiagnostic> ActiveTasks { get; } = [];
    public ObservableCollection<OverviewVehicle> Vehicles { get; } = [];
    public ObservableCollection<OverviewVehicle> OnlineVehicles { get; } = [];
    public bool IsAgvPanelOpen { get => _isAgvPanelOpen; set => SetProperty(ref _isAgvPanelOpen, value); }
    public string OnlineVehicleText => $"在线 AGV · {OnlineVehicles.Count} 台";
    public RuntimeMapScene Map { get; } = new();
    public DelegateCommand<string> SelectVehicleCommand { get; }
    public OverviewVehicle? SelectedVehicle { get => _selectedVehicle; set => SetProperty(ref _selectedVehicle, value); }
    public string MapTitle { get => _mapTitle; private set => SetProperty(ref _mapTitle, value); }
    public string UpdateText { get => _updateText; private set => SetProperty(ref _updateText, value); }
    public async void OnNavigatedTo(NavigationContext navigationContext) { _timer.Start(); await RefreshAsync(); }
    public void OnNavigatedFrom(NavigationContext navigationContext) => _timer.Stop();
    public bool IsNavigationTarget(NavigationContext navigationContext) => true;
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var s = await _client.GetSystemStatusAsync(timeout.Token);
            Metrics.Clear(); Connections.Clear(); ActiveTasks.Clear();
            Metrics.Add(new("在线车辆", $"{s.Agvs.Count(a => a.Connection == ConnectionState.Online)} / {s.Agvs.Count}", s.Safety.AgvCommunicationEnabled ? "车辆通信已启用" : "车辆通信未启用", "#3096DE"));
            Metrics.Add(new("等待调度", s.Tasks.WaitingCount.ToString(), $"活动任务 {s.Tasks.ActiveCount}", "#8C74DF"));
            Metrics.Add(new("执行中任务", s.Tasks.RunningCount.ToString(), s.Safety.DispatchingEnabled ? "自动调度已启用" : "自动调度未启用", "#22A88C"));
            Metrics.Add(new("异常任务", s.Tasks.FaultedCount.ToString(), "需关注的任务数量", "#D99735"));
            Connections.Add(new("PLC通信", s.Safety.PlcCommunicationEnabled ? StateText(s.Plc.State) : "未启用", $"有效点位 {s.Plc.GoodSignals} / {s.Plc.EnabledSignals}", s.Plc.State == ConnectionState.Online ? "#22A88C" : "#D99735"));
            Connections.Add(new("AGV通信", s.Safety.AgvCommunicationEnabled ? "采集中" : "未启用", $"已配置 {s.Agvs.Count} 台车辆", "#3096DE"));
            Connections.Add(new("后台服务", "已连接", $"已运行 {s.Service.Uptime.TotalHours:F1} 小时", "#22A88C"));
            Connections.Add(new("数据库", s.Database.Online ? "正常" : "异常", $"响应 {s.Database.ResponseTimeMs:F1} ms", s.Database.Online ? "#22A88C" : "#D99735"));
            foreach (var task in s.Tasks.ActiveTasks) ActiveTasks.Add(task);
            var selectedId = SelectedVehicle?.VehicleId;
            Vehicles.Clear();
            foreach (var agv in s.Agvs) Vehicles.Add(new(agv, s.GeneratedAt));
            OnlineVehicles.Clear();
            foreach (var vehicle in Vehicles.Where(v => !OverviewVehicle.IsStale(v.Source, s.GeneratedAt))) OnlineVehicles.Add(vehicle);
            RaisePropertyChanged(nameof(OnlineVehicleText));
            SelectedVehicle = OnlineVehicles.FirstOrDefault(v => v.VehicleId == selectedId) ?? OnlineVehicles.FirstOrDefault();
            var mapKey = $"{s.Safety.ActiveMapId}/{s.Safety.ActiveMapVersionId}";
            if (_loadedMapKey != mapKey)
            {
                Map.SetMap(null); _loadedMapKey = null;
                if (s.Safety.ActiveMapId is { } mapId && s.Safety.ActiveMapVersionId is { } versionId)
                {
                    try
                    {
                        var version = await _client.GetMapVersionAsync(mapId, versionId, timeout.Token);
                        if (version is not null && version.MapId == mapId && version.VersionId == versionId)
                        {
                            Map.SetMap(version.Definition); _loadedMapKey = mapKey;
                            MapTitle = $"{version.Definition.Name} · 运行版本 V{version.VersionNumber}";
                        }
                        else MapTitle = "运行地图版本不可用，等待重新加载";
                    }
                    catch { MapTitle = "运行地图加载失败，等待重新加载"; }
                }
                else { _loadedMapKey = mapKey; MapTitle = "运行地图 · 未激活"; }
            }
            Map.UpdateVehicles(s.Agvs, DateTimeOffset.Now);
            UpdateText = $"最近更新 {s.GeneratedAt.LocalDateTime:HH:mm:ss} · 每3秒刷新 · 运行地图 {s.Safety.ActiveMapId ?? "未激活"}";
        }
        catch (Exception)
        {
            SetUnavailable("连接不可用");
            UpdateText = "后台服务连接不可用，等待恢复；当前数值未知。";
        }
        finally { _refreshing = false; }
    }
    private void SetUnavailable(string message)
    {
        Metrics.Clear(); Connections.Clear(); ActiveTasks.Clear();
        Vehicles.Clear(); SelectedVehicle = null;
        OnlineVehicles.Clear(); RaisePropertyChanged(nameof(OnlineVehicleText));
        Map.SetMap(null); _loadedMapKey = null; MapTitle = "运行地图 · 状态未知";
        foreach (var label in new[] { "在线车辆", "等待调度", "执行中任务", "异常任务" }) Metrics.Add(new(label, "—", message, "#8A98AB"));
        foreach (var label in new[] { "PLC通信", "AGV通信", "后台服务", "数据库" }) Connections.Add(new(label, "未知", message, "#8A98AB"));
    }
    private static string StateText(ConnectionState state) => state switch
    { ConnectionState.Online => "已连接", ConnectionState.Connecting => "连接中", ConnectionState.Degraded => "不稳定", ConnectionState.Faulted => "故障", _ => "未连接" };
}
public sealed record OverviewMetric(string Label, string Value, string Detail, string Color);
