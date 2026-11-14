using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using AgvDispatch.Application.Abstractions;
using Microsoft.Win32;
using Microsoft.Data.Sqlite;

namespace AgvDispatch.Wpf.Management;

public sealed class ManagementViewModel : INotifyPropertyChanged
{
    private readonly SiteEnvironment _site;
    private readonly SiteOperations _operations;
    private readonly SiteArchives _archives;
    private readonly PaiceSimulationOperations _paiceOperations;
    private bool _refreshing;
    private bool _connectionsRefreshing;
    private bool _busy;
    private int _selectedPage;
    private string _message = "准备检查日常版本与运行状态。";
    private string _serviceState = "检查中";
    private string _webState = "检查中";
    private string _paiceState = "等待检查排策智演";
    private string _paiceFrontendState = "未就绪";
    private string _paiceBackendState = "未就绪";
    private bool _paiceConfigured;
    private bool _paiceRunning;
    private PaiceOwnedRecordState _paiceRecordState;
    private string _release = "—";
    private string _runtime = "尚未采集后台状态。";
    private string _paths = "";
    private string _network = "";
    private string _journal = "";
    private string _logText = "点击读取最近后台日志。";
    private string _connectionsState = "后台未连接时不能读取设备配置。";
    private string _lastCheck = "尚未检查";
    private string _maximum = "3";
    private string _policy = "—";
    private string _probeResult = "测试只建立 TCP 连接，不发送 PLC 写入或车辆命令。";
    private string _databaseState = "尚未检查";
    private bool _canStart;
    private bool _canStop;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; Changed(name); }

    public ManagementViewModel(SiteEnvironment site)
    {
        _site = site; _operations = new(site); _archives = new(site); _paiceOperations = new(site);
        try { _paiceConfigured = _paiceOperations.Info is not null; }
        catch (Exception ex) { _paiceState = ex.Message; }
        RefreshCommand = new AsyncAction(async () => { await RefreshAsync(); await ReadConnectionsAsync(); }, () => !IsBusy);
        StartCommand = Operation(async () => SetMessage(await _operations.LaunchAsync(false)));
        StopCommand = Operation(async () => { if (Confirm("停止当前日常后台和 Web？\n核对任务、路权、待发命令、充电及在线车辆停稳状态；离线车辆不阻止空闲系统停止。\n此操作不向车辆发送停车指令。")) SetMessage(await _operations.LaunchAsync(true)); });
        RestartCommand = Operation(async () => { if (!Confirm("重启当前日常系统？\n先核对任务清场、充电及在线车辆状态；离线车辆不阻止空闲系统停止。重新启动沿用原有配置。")) return; SetMessage(await _operations.LaunchAsync(true)); SetMessage(await _operations.LaunchAsync(false)); });
        StartPaiceCommand = Operation(async () =>
        {
            var observation = await _paiceOperations.StartAsync();
            SetMessage(observation.Message);
        });
        StopPaiceCommand = Operation(async () =>
        {
            if (!Confirm("停止独立排策智演前端与后端？\n该操作只停止 5270/5271，不停止 AGV 正式后台，也不向现场车辆发送命令。")) return;
            var observation = await _paiceOperations.StopAsync();
            SetMessage(observation.Message);
        });
        OpenWebCommand = new RelayAction(() => Open(SiteEnvironment.WebUrl));
        OpenEngineeringCommand = new RelayAction(() => Open(SiteEnvironment.WebUrl + "#/engineering?tab=communication"));
        OpenPipelineCommand = new RelayAction(() => Open(SiteEnvironment.WebUrl + "#/dispatch-pipeline"));
        OpenPaiceCommand = new RelayAction(() => Open((_paiceOperations.Info ?? throw new InvalidOperationException("尚未配置排策智演。")).Url));
        OpenRecoveryCommand = new RelayAction(() => Open(SiteEnvironment.WebUrl + "#/recovery"));
        OpenSettingsCommand = new RelayAction(() => SelectedPage = 4);
        OpenInformationCommand = new RelayAction(() => SelectedPage = 2);
        OpenBackupPageCommand = new RelayAction(() => SelectedPage = 1);
        CheckDatabaseCommand = Operation(async () =>
        {
            _databaseState = await Task.Run(() =>
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _site.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 3 }.ToString());
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA quick_check";
                return string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal) ? "检查通过" : "完整性异常";
            });
            Changed(nameof(DatabaseState)); SetMessage("SQLite 数据库：" + _databaseState);
        });
        OpenDataCommand = new RelayAction(() => OpenExisting(_site.DataRoot));
        OpenBackupCommand = new RelayAction(() => OpenFolder(_site.BackupRoot));
        OpenDiagnosticCommand = new RelayAction(() => OpenFolder(_site.DiagnosticRoot));
        CopyUrlCommand = new RelayAction(() => { Clipboard.SetText(SiteEnvironment.WebUrl); SetMessage("已复制本机 Web 地址。"); });
        NetworkSettingsCommand = new RelayAction(() => Open("ms-settings:network-status"));
        ProbeCommand = Operation(async () => { if (!int.TryParse(ProbePort, out var port)) throw new ArgumentException("端口必须为数字。"); _probeResult = await SiteOperations.ProbeAsync(ProbeAddress.Trim(), port); Changed(nameof(ProbeResult)); SetMessage(_probeResult); });
        SavePolicyCommand = Operation(() => { if (!int.TryParse(Maximum, out var maximum)) throw new ArgumentException("并发数必须为整数。"); _operations.SaveConcurrency(maximum); SetMessage("并发数已保存并备份原入口配置；下次启动生效。执行权限保持原配置。"); return Task.CompletedTask; });
        BackupCommand = Operation(async () => { var file = await Task.Run(_archives.CreateBackup); SetMessage("备份完成：" + file); });
        RestoreCommand = Operation(async () =>
        {
            var dialog = new OpenFileDialog { Title = "选择本工具生成的同版本运行数据备份", Filter = "运行数据备份 (*.zip)|*.zip", InitialDirectory = Directory.Exists(_site.BackupRoot) ? _site.BackupRoot : _site.Root };
            if (dialog.ShowDialog() != true || !Confirm("将恢复地图、工程方案、任务和通信配置。\n恢复前数据会另存保留；恢复后不自动启动系统。\n确认恢复所选备份？")) return;
            SetMessage(await Task.Run(() => _archives.Restore(dialog.FileName)));
        });
        ExportCommand = Operation(async () => { var status = $"入口版本：{Release}\n后台：{ServiceState}\nWeb：{WebState}\n{Paths}\n{Runtime}\n{ConnectionsState}"; var journal = Journal; SetMessage("诊断包已生成：" + await Task.Run(() => _archives.ExportDiagnostics(status, journal))); });
        ReadLogCommand = Operation(async () =>
        {
            var release = _site.ReadRelease();
            var roots = new[] { Path.Combine(release.Bundle, "Service", "logs"), Path.Combine(_site.DataRoot, "后台5199", "logs") };
            var latest = roots.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.log", SearchOption.TopDirectoryOnly)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            _logText = latest is null ? "没有找到后台日志文件。" : latest + Environment.NewLine + await Task.Run(() => SiteArchives.ReadTail(latest)); Changed(nameof(LogText));
        });
    }

    public string[] Pages { get; } = ["服务管理", "版本与备份", "软件信息", "日志管理", "系统设置"];
    public int SelectedPage { get => _selectedPage; set { if (value < 0 || value >= Pages.Length) return; Set(ref _selectedPage, value); foreach (var name in new[] { nameof(PageTitle), nameof(PageDescription), nameof(OverviewVisibility), nameof(ServiceVisibility), nameof(NetworkVisibility), nameof(ConnectionVisibility), nameof(BackupVisibility), nameof(LogVisibility) }) Changed(name); } }
    public string PageTitle => Pages[SelectedPage];
    public string PageDescription => SelectedPage switch { 0 => "", 1 => "核对当前发布版本，备份与恢复现场数据。", 2 => "查看软件版本、后台观察和数据位置。", 3 => "查看后台日志，导出现场诊断信息。", _ => "维护启动配置，检查网络和设备连接。" };
    private Visibility Show(int index) => SelectedPage == index ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OverviewVisibility => Show(2);
    public Visibility ServiceVisibility => Show(0);
    public Visibility NetworkVisibility => Show(4);
    public Visibility ConnectionVisibility => Show(4);
    public Visibility BackupVisibility => Show(1);
    public Visibility LogVisibility => Show(3);
    public string AssistantVersion => typeof(ManagementViewModel).Assembly.GetName().Version?.ToString(3) ?? "—";
    public string DatabaseState => _databaseState;
    public string DatabaseConnection => File.Exists(_site.DatabasePath) ? "文件可用" : "文件缺失";
    public string ProjectConnection => Directory.Exists(Path.Combine(_site.DataRoot, "Web工程方案")) ? "目录可用" : "目录缺失";
    public string BackendConnection => _serviceState.StartsWith("运行中") ? "已连接" : _serviceState.StartsWith("未运行") ? "未连接" : "未就绪";
    public string WebConnection => _webState.StartsWith("已就绪") ? "已连接" : _webState.StartsWith("未运行") ? "未连接" : "未就绪";
    public string PaiceFrontendState => _paiceFrontendState;
    public string PaiceBackendState => _paiceBackendState;
    public string PaiceFrontendConnection => _paiceFrontendState.StartsWith("已就绪") ? "已连接" : "未连接";
    public string PaiceBackendConnection => _paiceBackendState.StartsWith("健康") ? "已连接" : "未连接";
    public string PaiceSimulationState => _paiceState;
    public bool CanStartSystem => CanOperate && _canStart;
    public bool CanStopSystem => CanOperate && _canStop;
    public bool CanRestartSystem => CanOperate && _canStop;
    public bool CanStartPaiceSimulation => CanOperate && _paiceConfigured && !_paiceRunning;
    public bool CanStopPaiceSimulation => CanOperate && _paiceConfigured &&
                                          (_paiceRunning || _paiceRecordState == PaiceOwnedRecordState.Stale);
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); Changed(nameof(CanOperate)); Changed(nameof(CanStartSystem)); Changed(nameof(CanStopSystem)); Changed(nameof(CanRestartSystem)); Changed(nameof(CanStartPaiceSimulation)); Changed(nameof(CanStopPaiceSimulation)); CommandManager.InvalidateRequerySuggested(); } }
    public bool CanOperate => !IsBusy;
    public string Message => _message;
    public string ServiceState => _serviceState;
    public string WebState => _webState;
    public string Release => _release;
    public string Runtime => _runtime;
    public string Paths => _paths;
    public string Network => _network;
    public string Journal => _journal;
    public string LogText => _logText;
    public string LastCheck => _lastCheck;
    public string ConnectionsState => _connectionsState;
    public string Policy => _policy;
    public string Maximum { get => _maximum; set => Set(ref _maximum, value); }
    public string ProbeAddress { get; set; } = "127.0.0.1";
    public string ProbePort { get; set; } = "5199";
    public string ProbeResult => _probeResult;
    public ObservableCollection<ConnectionRow> Connections { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand StartPaiceCommand { get; }
    public ICommand StopPaiceCommand { get; }
    public ICommand OpenWebCommand { get; }
    public ICommand OpenEngineeringCommand { get; }
    public ICommand OpenPipelineCommand { get; }
    public ICommand OpenPaiceCommand { get; }
    public ICommand OpenRecoveryCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenInformationCommand { get; }
    public ICommand OpenBackupPageCommand { get; }
    public ICommand CheckDatabaseCommand { get; }
    public ICommand OpenDataCommand { get; }
    public ICommand OpenBackupCommand { get; }
    public ICommand OpenDiagnosticCommand { get; }
    public ICommand CopyUrlCommand { get; }
    public ICommand NetworkSettingsCommand { get; }
    public ICommand ProbeCommand { get; }
    public ICommand SavePolicyCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ReadLogCommand { get; }

    public async Task RefreshAsync()
    {
        if (_refreshing || IsBusy) return;
        _refreshing = true;
        try
        {
            var release = _site.ReadRelease();
            _release = release.ReleaseId;
            _paths = $"版本目录：{release.Bundle}\n数据库：{_site.DatabasePath}\nWeb 工程方案：{Path.Combine(_site.DataRoot, "Web工程方案")}";
            _policy = release.ProductionDispatch ? "沿用已保存的生产调度配置" : "生产调度未启用";
            if (_lastCheck == "尚未检查") Maximum = release.MaxConcurrentTasks.ToString();
            var session = ProcessIdentity.Read(release);
            var serviceAlive = ProcessIdentity.Matches(session?.Service);
            var webAlive = ProcessIdentity.Matches(session?.Web);
            _canStart = !serviceAlive && !webAlive; _canStop = serviceAlive || webAlive;
            if (!File.Exists(_site.DatabasePath)) _databaseState = "文件缺失";
            _serviceState = serviceAlive ? $"运行中 · PID {session!.Service.Id}" : "未运行 / 非当前受管进程";
            _webState = webAlive ? $"运行中 · PID {session!.Web.Id}" : "未运行 / 非当前受管进程";
            _runtime = "后台未连接，任务、车辆和路权状态未知。";
            if (serviceAlive)
            {
                try
                {
                    var status = await new LocalBackendClient(release.PipeName).ReadStatusAsync();
                    if (status.Service.ProcessId != session!.Service.Id || DateTimeOffset.UtcNow - status.GeneratedAt > TimeSpan.FromSeconds(10)) throw new InvalidDataException("后台身份或状态时效不匹配。");
                    _runtime = $"实际后台版本：{status.Execution?.InformationalVersion ?? status.Service.Version}\n采样：{status.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n任务：活动 {status.Tasks.ActiveCount} · 等待 {status.Tasks.WaitingCount} · 执行 {status.Tasks.RunningCount} · 异常 {status.Tasks.FaultedCount}\n地图：{status.Safety.ActiveMapId ?? "未启用"} / {status.Safety.ActiveMapVersionId ?? "—"}\nPLC 通信：{On(status.Safety.PlcCommunicationEnabled)} · AGV 通信：{On(status.Safety.AgvCommunicationEnabled)} · 调度：{On(status.Safety.DispatchingEnabled)}";
                }
                catch (Exception ex) { _serviceState = "进程存在 · 后台未就绪"; _runtime = "状态采集失败：" + ex.Message; }
            }
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var config = JsonDocument.Parse(await http.GetStringAsync(SiteEnvironment.WebUrl + "api/config"));
                var root = config.RootElement;
                if (!webAlive || root.GetProperty("servicePipeName").GetString() != release.PipeName || root.GetProperty("adapter").GetProperty("informationalVersion").GetString()?.Split('+')[0] != release.ReleaseId)
                    _webState = "5199 有响应 · 版本或进程身份不匹配";
                else _webState = "已就绪 · 127.0.0.1:5199";
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException) { if (webAlive) _webState = "进程存在 · Web 未就绪"; }
            try { ApplyPaiceObservation(await _paiceOperations.ObserveAsync()); }
            catch (Exception ex)
            {
                _paiceConfigured = false;
                _paiceRunning = false;
                _paiceFrontendState = "配置错误";
                _paiceBackendState = "配置错误";
                _paiceState = ex.Message;
            }
            _network = SiteOperations.ReadNetwork();
            _lastCheck = DateTime.Now.ToString("HH:mm:ss") + " 已检查";
        }
        catch (Exception ex) { _canStart = false; _canStop = false; _serviceState = "配置检查失败"; _webState = "未确认"; _paiceFrontendState = "未确认"; _paiceBackendState = "未确认"; _paiceState = ex.Message; _runtime = ex.Message; _connectionsState = "配置无效，设备状态未确认。"; Connections.Clear(); }
        finally
        {
            _refreshing = false;
            foreach (var name in new[] { nameof(Release), nameof(Paths), nameof(Policy), nameof(ServiceState), nameof(WebState), nameof(PaiceFrontendState), nameof(PaiceBackendState), nameof(PaiceFrontendConnection), nameof(PaiceBackendConnection), nameof(PaiceSimulationState), nameof(Runtime), nameof(Network), nameof(LastCheck), nameof(ConnectionsState), nameof(DatabaseState), nameof(DatabaseConnection), nameof(ProjectConnection), nameof(BackendConnection), nameof(WebConnection), nameof(CanStartSystem), nameof(CanStopSystem), nameof(CanRestartSystem), nameof(CanStartPaiceSimulation), nameof(CanStopPaiceSimulation) }) Changed(name);
        }
    }

    private void ApplyPaiceObservation(PaiceSimulationObservation observation)
    {
        _paiceConfigured = observation.Configured;
        _paiceRunning = observation.Running;
        _paiceRecordState = observation.RecordState;
        _paiceFrontendState = observation.FrontendState;
        _paiceBackendState = observation.BackendState;
        _paiceState = observation.Message;
    }

    public async Task ReadConnectionsAsync()
    {
        if (IsBusy || _connectionsRefreshing) return;
        _connectionsRefreshing = true;
        Connections.Clear();
        try
        {
            var release = _site.ReadRelease();
            var session = ProcessIdentity.Read(release);
            if (!ProcessIdentity.Matches(session?.Service)) throw new InvalidOperationException("当前版本的受管后台未运行。");
            var client = new LocalBackendClient(release.PipeName);
            var status = await client.ReadStatusAsync();
            if (status.Service.ProcessId != session!.Service.Id || DateTimeOffset.UtcNow - status.GeneratedAt > TimeSpan.FromSeconds(10)) throw new InvalidDataException("后台身份或状态时效不匹配。");
            var settings = await client.ReadConnectionsAsync();
            _connectionsState = settings.RestartRequired ? "存在已保存但尚未应用的通信配置；表中分别列出当前配置与保存配置。" : "当前配置与保存配置一致。设备状态来自后台采样。";
            foreach (var plc in settings.Active.Plcs)
            {
                var saved = settings.Saved.Plcs.FirstOrDefault(p => p.DeviceId == plc.DeviceId);
                var observed = settings.PlcConnections.FirstOrDefault(p => p.DeviceId == plc.DeviceId);
                Connections.Add(new(plc.DeviceId, "PLC", $"{plc.IpAddress}:{plc.Port}", saved is null ? "已从保存配置移除" : $"{saved.IpAddress}:{saved.Port}", observed is null ? "无连接观察" : $"{observed.State} · {observed.LastSuccessfulReadAt?.ToLocalTime().ToString("HH:mm:ss") ?? "未成功读取"}"));
            }
            foreach (var agv in settings.Active.Agvs)
            {
                var saved = settings.Saved.Agvs.FirstOrDefault(p => p.VehicleId == agv.VehicleId);
                var observed = status.Agvs.FirstOrDefault(p => p.VehicleId == agv.VehicleId);
                Connections.Add(new(agv.VehicleId, "AGV", $"{agv.IpAddress}:{agv.Port}", saved is null ? "已从保存配置移除" : $"{saved.IpAddress}:{saved.Port}", observed is null ? "无观察" : $"{observed.Connection} · {observed.Quality} · {observed.CurrentStation ?? "未知站点"}"));
            }
            foreach (var plc in settings.Saved.Plcs.Where(p => !settings.Active.Plcs.Any(a => a.DeviceId == p.DeviceId)))
                Connections.Add(new(plc.DeviceId, "PLC", "尚未应用", $"{plc.IpAddress}:{plc.Port}", "已保存，待后台应用"));
            foreach (var agv in settings.Saved.Agvs.Where(p => !settings.Active.Agvs.Any(a => a.VehicleId == p.VehicleId)))
                Connections.Add(new(agv.VehicleId, "AGV", "尚未应用", $"{agv.IpAddress}:{agv.Port}", "已保存，待后台应用"));
        }
        catch (Exception ex) { _connectionsState = "未能读取设备状态：" + ex.Message; }
        finally { _connectionsRefreshing = false; }
        Changed(nameof(ConnectionsState));
    }

    private ICommand Operation(Func<Task> action) => new AsyncAction(async () =>
    {
        IsBusy = true;
        try { await action(); }
        catch (Exception ex) { SetMessage("操作未完成：" + (ex is OperationCanceledException ? "等待超时，结果未确认，请刷新状态；不要重复启动或停止。" : ex.Message)); }
        finally { IsBusy = false; await RefreshAsync(); }
    }, () => !IsBusy && !_refreshing);
    private static string On(bool value) => value ? "启用" : "关闭";
    private static bool Confirm(string message) => MessageBox.Show(message, "AGV 现场管理工具", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    private void SetMessage(string message) { _message = message; _journal = $"[{DateTime.Now:HH:mm:ss}] {message}\n" + _journal; if (_journal.Length > 30000) _journal = _journal[..30000]; Changed(nameof(Message)); Changed(nameof(Journal)); }
    private void Open(string target) { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception ex) { SetMessage("无法打开：" + ex.Message); } }
    private void OpenExisting(string path) { if (Directory.Exists(path)) Open(path); else SetMessage("目录尚不存在：" + path); }
    private void OpenFolder(string path) { try { Directory.CreateDirectory(path); Open(path); } catch (Exception ex) { SetMessage(ex.Message); } }
}

public sealed record ConnectionRow(string Id, string Kind, string ActiveEndpoint, string SavedEndpoint, string State);

public sealed class RelayAction(Action action) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

public sealed class AsyncAction(Func<Task> action, Func<bool> canExecute) : ICommand
{
    private bool _running;
    public bool CanExecute(object? parameter) => !_running && canExecute();
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; CommandManager.InvalidateRequerySuggested();
        try { await action(); }
        finally { _running = false; CommandManager.InvalidateRequerySuggested(); }
    }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
