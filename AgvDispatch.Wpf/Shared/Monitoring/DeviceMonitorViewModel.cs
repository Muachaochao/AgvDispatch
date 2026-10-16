using System.Collections.ObjectModel;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Regions;

namespace AgvDispatch.Wpf.ViewModels;

public abstract class DeviceMonitorViewModel : BindableBase, INavigationAware
{
    private readonly IDeviceMonitoringClient _client;
    private readonly bool _vehicles;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _lifetime;
    private IReadOnlyList<MonitoredDevice> _all = [];
    private bool _busy;
    private string _search = "", _message = "正在等待后台快照…", _summary = "暂无数据";
    private bool _attentionOnly;
    private MonitoredDevice? _selected;
    protected DeviceMonitorViewModel(IDeviceMonitoringClient client, bool vehicles)
    {
        _client = client; _vehicles = vehicles;
        RefreshCommand = new DelegateCommand(async () => await RefreshAsync(), () => !_busy);
        _timer.Tick += async (_, _) => await RefreshAsync();
    }
    public ObservableCollection<MonitoredDevice> Devices { get; } = [];
    public DelegateCommand RefreshCommand { get; }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string Search { get => _search; set { if (SetProperty(ref _search, value)) Filter(); } }
    public bool AttentionOnly { get => _attentionOnly; set { if (SetProperty(ref _attentionOnly, value)) Filter(); } }
    public MonitoredDevice? SelectedDevice { get => _selected; set => SetProperty(ref _selected, value); }
    private static bool Attention(MonitoredDevice d) => d.Status is "急停" or "车辆报警" or "受阻" or "故障" or "信号冲突" or "人工处理中" or "业务状态未就绪" or "等待业务状态判定" ||
        d.Quality != "正常" && !(d.Category == "PLC" && d.Status == "正常");
    private void Filter()
    {
        var id = SelectedDevice?.Id;
        var query = _all.Where(d => (!AttentionOnly || Attention(d)) &&
            (string.IsNullOrWhiteSpace(Search) || $"{d.Id} {d.Name} {d.Category} {d.Connection} {d.Status}".Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)));
        Devices.Clear();
        foreach (var d in query) Devices.Add(d);
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == id) ?? Devices.FirstOrDefault();
        Summary = $"配置 {_all.Count} 项  ·  需关注 {_all.Count(Attention)} 项  ·  当前显示 {Devices.Count} 项";
    }
    public async Task RefreshAsync()
    {
        if (_busy || _lifetime is null) return;
        var lifetime = _lifetime;
        _busy = true; RefreshCommand.RaiseCanExecuteChanged();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var snapshot = await _client.GetDeviceMonitoringAsync(timeout.Token);
            if (lifetime.IsCancellationRequested || _lifetime != lifetime) return;
            _all = _vehicles ? snapshot.Vehicles : snapshot.Equipment;
            Filter();
            Message = $"后台快照 {snapshot.GeneratedAt.ToLocalTime():yyyy.MM.dd HH:mm:ss}  ·  每 2 秒刷新  ·  只读监控";
        }
        catch (Exception ex)
        {
            if (lifetime.IsCancellationRequested || _lifetime != lifetime) return;
            // Never retain a healthy-looking selection when the service cannot be reached.
            _all = []; Devices.Clear(); SelectedDevice = null;
            Summary = "后台数据不可用";
            Message = ex is OperationCanceledException ? "后台请求超时，等待重试；不能据此判断现场状态。" : $"后台连接失败：{ex.Message}；不能据此判断现场状态。";
        }
        finally
        {
            _busy = false; RefreshCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsNavigationTarget(NavigationContext context) => true;
    public virtual void OnNavigatedTo(NavigationContext context)
    {
        _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = new();
        _timer.Start(); _ = RefreshAsync();
    }
    public virtual void OnNavigatedFrom(NavigationContext context)
    {
        _timer.Stop(); _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
    }
}
