using System.Collections.ObjectModel;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Regions;

namespace AgvDispatch.Wpf.ViewModels;

public sealed class CommunicationDiagnosticViewModel : BindableBase, INavigationAware
{
    private readonly ITrafficControlClient _client;
    private readonly DispatcherTimer _timer;
    private SystemStatusSnapshot? _snapshot;
    private bool _isBusy;
    private string _statusMessage = "等待连接后台诊断接口。";

    public CommunicationDiagnosticViewModel(ITrafficControlClient client)
    {
        _client = client;
        RefreshCommand = new DelegateCommand(async () => await RefreshAsync(), () => !IsBusy);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync(true);
    }

    public ObservableCollection<AgvDiagnostic> Agvs { get; } = [];
    public ObservableCollection<DispatchTaskDiagnostic> Tasks { get; } = [];
    public DelegateCommand RefreshCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string GeneratedAt => _snapshot?.GeneratedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
    public string ServiceState => _snapshot is null
        ? "未连接"
        : $"运行中 · PID {_snapshot.Service.ProcessId} · {FormatUptime(_snapshot.Service.Uptime)}";
    public string DatabaseState => _snapshot is null
        ? "未知"
        : $"{(_snapshot.Database.Online ? "正常" : "异常")} · {_snapshot.Database.ResponseTimeMs:F1} ms";
    public string PlcState => _snapshot is null
        ? "未知"
        : $"{_snapshot.Plc.State} · {_snapshot.Plc.LastResponseTimeMs?.ToString("F1") ?? "—"} ms";
    public string AgvState => _snapshot is null
        ? "0 / 0"
        : $"{_snapshot.Agvs.Count(item => item.Connection == Domain.Devices.ConnectionState.Online)} / {_snapshot.Agvs.Count} 在线";
    public string TaskState => _snapshot is null
        ? "0"
        : $"{_snapshot.Tasks.ActiveCount} 活动 · {_snapshot.Tasks.FaultedCount} 故障";

    public string PlcConnectionDetails => _snapshot is null
        ? "—"
        : $"设备：{_snapshot.Plc.DisplayName} ({_snapshot.Plc.DeviceId})    状态更新时间：{_snapshot.Plc.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}    连续失败：{_snapshot.Plc.ConsecutiveFailures}\n" +
          $"最近成功读取：{FormatTime(_snapshot.Plc.LastSuccessfulReadAt)}    点位版本：{_snapshot.Plc.SignalVersion}    {_snapshot.Plc.Message ?? "无异常信息"}";

    public string PlcSignalDetails => _snapshot is null
        ? "—"
        : $"总点位 {_snapshot.Plc.TotalSignals} · 启用 {_snapshot.Plc.EnabledSignals} · Good {_snapshot.Plc.GoodSignals} · Stale {_snapshot.Plc.StaleSignals} · Bad {_snapshot.Plc.BadSignals} · Unknown {_snapshot.Plc.UnknownSignals} · Disabled {_snapshot.Plc.DisabledSignals}";

    public string PipeDetails => _snapshot is null
        ? "—"
        : $"Pipe：{_snapshot.NamedPipe.PipeName}    当前连接：{_snapshot.NamedPipe.ActiveConnections}    请求总数：{_snapshot.NamedPipe.TotalRequests}    失败：{_snapshot.NamedPipe.FailedRequests}\n" +
          $"当前身份：{_snapshot.NamedPipe.CurrentDisplayName} ({_snapshot.NamedPipe.CurrentWindowsIdentity})    权限：{_snapshot.NamedPipe.CurrentPermissions}\n" +
          $"最近请求：{_snapshot.NamedPipe.LastOperation ?? "—"} / {_snapshot.NamedPipe.LastClientIdentity ?? "—"} / {FormatTime(_snapshot.NamedPipe.LastRequestAt)}";

    public string SafetyDetails => _snapshot is null
        ? "—"
        : $"PLC通信={OnOff(_snapshot.Safety.PlcCommunicationEnabled)}  PLC写入={OnOff(_snapshot.Safety.PlcWritesAllowed)}  " +
          $"AGV通信={OnOff(_snapshot.Safety.AgvCommunicationEnabled)}  AGV命令={OnOff(_snapshot.Safety.AgvCommandsAllowed)}  " +
          $"地图={OnOff(_snapshot.Safety.MapEnabled)}  自动调度={OnOff(_snapshot.Safety.DispatchingEnabled)}\n" +
          $"活动地图={_snapshot.Safety.ActiveMapId ?? "无"}  版本={_snapshot.Safety.ActiveMapVersionId ?? "无"}";

    public async void OnNavigatedTo(NavigationContext navigationContext)
    {
        await RefreshAsync();
        _timer.Start();
    }

    public bool IsNavigationTarget(NavigationContext navigationContext) => true;
    public void OnNavigatedFrom(NavigationContext navigationContext) => _timer.Stop();

    private async Task RefreshAsync(bool silent = false)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            _snapshot = await _client.GetSystemStatusAsync(CancellationToken.None);
            Replace(Agvs, _snapshot.Agvs);
            Replace(Tasks, _snapshot.Tasks.ActiveTasks);
            RaiseSnapshotProperties();
            if (!silent)
            {
                StatusMessage = "系统诊断快照已从Windows Service加载。";
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"诊断快照加载失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseSnapshotProperties()
    {
        RaisePropertyChanged(nameof(GeneratedAt));
        RaisePropertyChanged(nameof(ServiceState));
        RaisePropertyChanged(nameof(DatabaseState));
        RaisePropertyChanged(nameof(PlcState));
        RaisePropertyChanged(nameof(AgvState));
        RaisePropertyChanged(nameof(TaskState));
        RaisePropertyChanged(nameof(PlcConnectionDetails));
        RaisePropertyChanged(nameof(PlcSignalDetails));
        RaisePropertyChanged(nameof(PipeDetails));
        RaisePropertyChanged(nameof(SafetyDetails));
    }

    private static string FormatTime(DateTimeOffset? value) =>
        value?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

    private static string FormatUptime(TimeSpan value) =>
        $"{(int)value.TotalDays}天 {value.Hours:D2}:{value.Minutes:D2}:{value.Seconds:D2}";

    private static string OnOff(bool enabled) => enabled ? "开启" : "关闭";

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }
}
