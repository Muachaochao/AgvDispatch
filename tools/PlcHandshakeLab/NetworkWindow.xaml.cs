using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PlcHandshakeLab;

public sealed record NetworkPointRow(int Signal, string Owner, string Name, string Address, string Value, string Applied, string Quality, string Source, Brush Color, bool CanSet, bool CanClear);
public sealed record StageResultRow(int Stage, string Name, string Outcome, string Time, string Note);

public partial class NetworkWindow : Window
{
    private readonly ObservableCollection<PointMapping> _mapping = [];
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private NetworkLab? _lab;
    private S7LabServer? _server;
    private bool _busy;
    private bool _loading;
    private bool _closing;
    private bool _closeRequested;
    private Task? _stopTask;
    private NetworkEvent? _lastEvent;
    private string SelectedStation => Station.SelectedItem as string ?? "CNC-001";
    private readonly Dictionary<string, int> _markedStages = new();
    private string? _fatal;
    private string _pointsSignature = "";
    private string _resultSignature = "";

    public NetworkWindow()
    {
        InitializeComponent();
        Title += " · " + typeof(NetworkWindow).Assembly.GetName().Version;
        MappingGrid.ItemsSource = _mapping;
        StageChoice.ItemsSource = Sequence.Stages.Select((s, i) => $"{i + 1:00} · {s.Name}").ToArray();
        LoadConfig(NetworkLabConfig.CreateDefault());
        _refresh.Tick += async (_, _) =>
        {
            try
            {
                if (_server is not null)
                {
                    if ((_server.Fault ?? _lab?.Fault) is { } fault) throw new IOException(fault);
                    var health = CommunicationStatus.Evaluate(true, _server.Clients, _server.LastDataRequest, DateTimeOffset.Now);
                    _lab?.UpdateCommunication(health.Fresh);
                    _lab?.TickAutomatic();
                }
                RefreshPanel();
            }
            catch (Exception ex) { await HaltForFaultAsync(ex); }
        };
        _refresh.Start();
        Closing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            if (_closeRequested) return;
            _closeRequested = true;
            Dispatcher.BeginInvoke(async () =>
            {
                await StopAsync(); _refresh.Stop();
                try { _lab?.Dispose(); } catch (IOException) { /* Existing journal is preserved on a failing disk. */ }
                _lab = null; _closing = true; Close();
            });
        };
        Loaded += RunSmokeAsync;
        RefreshPanel();
    }

    private void LoadConfig(NetworkLabConfig config)
    {
        config.Validate();
        BindIp.Text = config.BindAddress; PortText.Text = config.Port.ToString(); PeerIp.Text = config.AllowedClient;
        DelayText.Text = config.AutoDoneSeconds.ToString(CultureInfo.InvariantCulture);
        DumperId.Text = config.DumperStationId;
        _mapping.Clear(); foreach (var p in config.Points) _mapping.Add(p.Copy());
        ReloadStations();
    }

    private void ReloadStations()
    {
        _loading = true;
        var selected = SelectedStation;
        Station.ItemsSource = _mapping.Where(p => p.Signal == 0).Select(p => p.Station).ToArray();
        Station.SelectedItem = selected;
        if (Station.SelectedIndex < 0) Station.SelectedIndex = 0;
        StageChoice.SelectedIndex = _markedStages.GetValueOrDefault(SelectedStation);
        _loading = false;
    }

    private NetworkLabConfig ReadConfig()
    {
        if (!MappingGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !MappingGrid.CommitEdit(DataGridEditingUnit.Row, true))
            throw new ArgumentException("点表编辑含无效值，请修正后再操作。");
        var config = new NetworkLabConfig
        {
            BindAddress = BindIp.Text.Trim(), Port = int.Parse(PortText.Text, CultureInfo.InvariantCulture),
            AllowedClient = PeerIp.Text.Trim(), AutoDoneSeconds = double.Parse(DelayText.Text, CultureInfo.InvariantCulture),
            DumperStationId = DumperId.Text.Trim(),
            Points = _mapping.Select(p => p.Copy()).ToList()
        };
        config.Validate(); return config;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_server is not null || _busy) return;
        NetworkLab? next = null;
        try
        {
            var config = ReadConfig();
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgvDispatch", "PlcHandshakeLab", DateTime.Now.ToString("yyyyMMdd"));
            var session = DateTime.Now.ToString("HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8];
            next = new NetworkLab(config, Path.Combine(directory, session + ".jsonl"));
            File.WriteAllText(Path.Combine(directory, session + ".config.json"), JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            next.SelectStage(SelectedStation, 0);
            next.Note("工具版本", typeof(NetworkWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "未知版本");
            next.SetAutomatic(AutoMode.SelectedIndex == 1);
            var server = new S7LabServer(next);
            server.Start();
            var previous = _lab; _lab = next; _server = server; next = null;
            try { previous?.Dispose(); } catch (IOException) { /* Previous session files remain on disk. */ }
            _fatal = null; _lastEvent = null;
            _markedStages.Clear(); _markedStages[SelectedStation] = 0;
            _loading = true; StageChoice.SelectedIndex = 0; _loading = false;
            Settings.IsEnabled = false; MappingGrid.IsReadOnly = true; StopButton.IsEnabled = true; StartButton.IsEnabled = false;
            Message.Text = "已启动独立模拟PLC。调度请使用此处IP/端口及匹配点表；人工确认车辆实际动作后再操作P点。";
            JournalLabel.Text = "完整日志：" + _lab.JournalPath;
            RefreshPanel();
        }
        catch (Exception ex) { next?.Dispose(); Message.Text = "启动失败：" + ex.Message; }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();
    private async Task StopAsync()
    {
        if (_stopTask is not null) { await _stopTask; return; }
        _stopTask = StopCoreAsync();
        try { await _stopTask; } finally { _stopTask = null; }
    }
    private async Task StopCoreAsync()
    {
        if (_busy) return;
        _busy = true;
        var server = _server; _server = null;
        try
        {
            if (server is not null) await server.DisposeAsync();
            if (server is not null && _lab is not null) _lab.Note("停止", "服务停止；调度连接断开，当前内存仅供回看。再次启动创建新会话并应用初值。");
        }
        catch (Exception ex) { Message.Text = "停止服务：" + ex.Message; }
        finally
        {
            Settings.IsEnabled = true; MappingGrid.IsReadOnly = false; StopButton.IsEnabled = false; StartButton.IsEnabled = true;
            _busy = false; RefreshPanel();
        }
    }

    private async Task HaltForFaultAsync(Exception ex)
    {
        _fatal = ex.Message;
        await StopAsync();
        Message.Text = "联调已停止：" + ex.Message + "。请保留记录并修复后再开新会话。";
    }

    private void AutoMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _lab is null || _server is null) return;
        try { _lab.SetAutomatic(AutoMode.SelectedIndex == 1); RefreshPanel(); }
        catch (Exception ex)
        {
            Message.Text = ex.Message;
            _loading = true; AutoMode.SelectedIndex = _lab.Automatic ? 1 : 0; _loading = false;
        }
    }
    private void Station_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || StageChoice is null) return;
        _loading = true; StageChoice.SelectedIndex = _markedStages.GetValueOrDefault(SelectedStation); _loading = false;
        PhysicalConfirmed.IsChecked = false;
        RefreshPanel();
    }
    private void Stage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || StageChoice.SelectedIndex < 0) return;
        try
        {
            _markedStages[SelectedStation] = StageChoice.SelectedIndex;
            PhysicalConfirmed.IsChecked = false;
            if (_server is not null) _lab?.SelectStage(SelectedStation, StageChoice.SelectedIndex);
            RefreshPanel();
        }
        catch (Exception ex) { Message.Text = ex.Message; }
    }
    private void StageBack_Click(object sender, RoutedEventArgs e) => StageChoice.SelectedIndex = Math.Max(0, StageChoice.SelectedIndex - 1);
    private void StageNext_Click(object sender, RoutedEventArgs e) => StageChoice.SelectedIndex = Math.Min(27, StageChoice.SelectedIndex + 1);
    private void SetHigh_Click(object sender, RoutedEventArgs e) => ManualSet((int)((Button)sender).Tag, true);
    private void SetLow_Click(object sender, RoutedEventArgs e) => ManualSet((int)((Button)sender).Tag, false);
    private void ManualSet(int signal, bool value)
    {
        if (_server is null || _lab is null) return;
        try
        {
            _lab.ManualSet(signal is >= 6 and <= 9 or 12 ? NetworkLabConfig.Shared : SelectedStation, signal, value);
            Message.Text = $"请求 {SequenceName(signal)}={(value ? 1 : 0)} 已应用到模拟DB。请继续观察调度输出和实际车辆。";
            RefreshPanel();
        }
        catch (Exception ex) { Message.Text = "未应用：" + ex.Message; }
    }
    private static string SequenceName(int signal) => signal < 10 ? Sequence.Signals[signal].Name : signal == 10 ? "工位故障" : signal == 11 ? "下游在线" : "倒料设备故障";

    private void RefreshPanel()
    {
        if (Points is null || _busy) return;
        var snapshot = _lab?.Snapshot(SelectedStation);
        var active = _server is not null && _server.Fault is null && _fatal is null;
        var now = DateTimeOffset.Now;
        var health = CommunicationStatus.Evaluate(active, _server?.Clients ?? 0, _server?.LastDataRequest, now);
        HealthText.Text = health.Label;
        HealthBanner.Background = health.Fresh ? Brushes.Honeydew : Brushes.MistyRose;
        HealthText.Foreground = health.Fresh ? Brushes.DarkGreen : Brushes.DarkRed;
        AutoState.Text = "模拟周期：" + (_lab?.AutomaticStatus ?? "尚未启动") + "；工位故障仅影响本站撤呼叫，共享倒料由倒料故障点控制。";
        ConnectionStatus.Text = _server is null ? "服务未运行 · 停止后的电平只供回看" : _server.Fault ??
            $"{_lab!.Config.BindAddress}:{_server.Port} · 连接 {_server.Clients} · 最近请求 {(_server.LastRequest is { } last ? last.ToString("HH:mm:ss") + $" / {(DateTimeOffset.Now - last).TotalSeconds:F1}s前" : "尚无")} ";
        if (_server?.Fault is { } fault) Message.Text = fault;
        var signature = active + ":" + health.Fresh + ":" + _lab?.Automatic + ":" + SelectedStation + ":" + now.ToUnixTimeSeconds() + ":" + string.Join(";", (snapshot ?? []).Select(s => s.Mapping.Address + ":" + s.Value + ":" + s.UpdatedAt.Ticks));
        if (signature != _pointsSignature)
        {
        _pointsSignature = signature;
        Points.ItemsSource = (snapshot ?? []).Select(s => new NetworkPointRow(s.Mapping.Signal, s.Mapping.Owner, s.Mapping.Name,
            s.Mapping.Address, (s.Value ? "1" : "0") + (s.Mapping.Owner == "H" && (!health.Fresh || !CommunicationStatus.PointFresh(s, now)) ? "·旧" : ""),
            $"{(s.Source == "初值" ? "初值" : s.Requested)} / {s.UpdatedAt:HH:mm:ss}",
            s.HostAccessedAt is { } at ? $"{(health.Fresh && CommunicationStatus.PointFresh(s, now) ? "已交换" : "旧记录")} {(now - at).TotalSeconds:F1}s前" : "尚未交换",
            $"{s.Source}；最后变化{s.UpdatedAt:O}；{s.HostAccess} {s.HostAccessedAt:O}。交换表示S7读或写，不等于物理动作完成。",
            s.Mapping.Owner == "H" && (!health.Fresh || !CommunicationStatus.PointFresh(s, now)) ? Brushes.Gray : s.Value ? Brushes.Teal : Brushes.SlateGray,
            active && s.Mapping.Owner == "P" && (!_lab!.Automatic || s.Mapping.Signal is not (8 or 9)),
            active && s.Mapping.Owner == "P" && (!_lab!.Automatic || s.Mapping.Signal is not (0 or 8 or 9)))).ToArray();
        }
        var stage = Math.Max(0, StageChoice.SelectedIndex);
        StageCondition.Text = Sequence.Stages[stage].Condition;
        var stateBySignal = (snapshot ?? []).Where(s => s.Mapping.Signal < 10).ToDictionary(s => s.Mapping.Signal);
        var differences = stateBySignal.Values.Where(s => !Sequence.Matches(stage, s.Mapping.Signal, s.Value)).ToArray();
        var h = differences.Where(s => s.Mapping.Owner == "H").Select(s => s.Mapping.Name + "→" + Sequence.Stages[stage].Pattern[s.Mapping.Signal]);
        var p = differences.Where(s => s.Mapping.Owner == "P").Select(s => s.Mapping.Name + "→" + Sequence.Stages[stage].Pattern[s.Mapping.Signal]);
        WaitInfo.Text = _lab is null ? "启动服务后显示阶段对照。" : $"本阶段人工标记已停留 {_lab.StageElapsed(SelectedStation):F1} 秒\nH待观察：{string.Join("、", h.DefaultIfEmpty("本步电平已符合"))}";
        Guidance.Text = _lab is null ? "按现场实际情况操作P点；按钮不会跳过调度执行。" : $"P待操作参考：{string.Join("、", p.DefaultIfEmpty("本步电平已符合"))}\n电平对照：{(differences.Length == 0 ? "符合参考值，仍需核实阶段条件" : "存在差异，可保留等待并记录现场情况")}\n自动模式只处理呼叫、Ready、Done，箱体检测始终人工给出。";
        ObserveButton.IsEnabled = active;
        StageTestButton.IsEnabled = active;
        ConfirmClosureButton.IsEnabled = active;
        var results = _lab?.TestResults() ?? [];
        var round = _lab?.TestRound(SelectedStation) ?? 1;
        var resultSignature = SelectedStation + ":" + round + ":" + results.Length;
        if (_resultSignature != resultSignature)
        {
            _resultSignature = resultSignature;
            var rows = Enumerable.Range(1, 28).Select(index =>
            {
                var last = results.LastOrDefault(r => r.Station == SelectedStation && r.Round == round && r.Stage == index);
                return new StageResultRow(index, Sequence.Stages[index - 1].Name, last?.Outcome ?? "未测", last?.Time.ToString("HH:mm:ss") ?? "", last?.Note ?? "");
            }).ToArray();
            StageResults.ItemsSource = rows;
            TestSummary.Text = $"{SelectedStation} · 第{round}轮 · 通过 {rows.Count(r => r.Outcome == "通过")}/28 · 失败 {rows.Count(r => r.Outcome == "失败")} · 跳过 {rows.Count(r => r.Outcome == "跳过")} · 未测 {rows.Count(r => r.Outcome == "未测")}";
        }
        var events = _lab?.Events();
        if (events is { Length: > 0 } && !ReferenceEquals(events[^1], _lastEvent))
        {
            _lastEvent = events[^1];
            Events.ItemsSource = events.Select(e => $"{e.Time:HH:mm:ss.fff} [{e.Kind}] {e.Station} {e.Point} {e.Before} → {e.After} {e.Detail}").ToArray();
            Events.ScrollIntoView(Events.Items[^1]);
            Events.UpdateLayout();
            FindScrollViewer(Events)?.ScrollToHorizontalOffset(0);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private void Observe_Click(object sender, RoutedEventArgs e)
    {
        if (_lab is null || _server is null) return;
        try
        {
            var kind = ((ComboBoxItem)ObservationKind.SelectedItem).Content.ToString();
            _lab.Observe(SelectedStation, Vehicle.Text.Trim(), TaskId.Text.Trim(), kind + "；" + ObservationNote.Text.Trim());
            ObservationNote.Clear(); Message.Text = "已记录观察、阶段、车辆/任务标记及当前信号快照；未改变电平。"; RefreshPanel();
        }
        catch (Exception ex) { Message.Text = "记录失败：" + ex.Message; }
    }

    private void ConfirmClosure_Click(object sender, RoutedEventArgs e)
    {
        if (_lab is null || _server is null) return;
        try { _lab.ConfirmCycleClosed(ObservationNote.Text); Message.Text = "模拟周期已核对闭合；只清除内部保留，未修改P/H或调度任务。"; RefreshPanel(); }
        catch (Exception ex) { Message.Text = ex.Message; }
    }
    private void StageTest_Click(object sender, RoutedEventArgs e)
    {
        if (_lab is null || _server is null) return;
        try
        {
            var outcome = ((ComboBoxItem)TestOutcome.SelectedItem).Content.ToString()!;
            _lab.RecordStageTest(SelectedStation, outcome, Vehicle.Text, TaskId.Text, ObservationNote.Text, PhysicalConfirmed.IsChecked == true);
            PhysicalConfirmed.IsChecked = false;
            Message.Text = "已保存本阶段结果及点位快照；实际条件需由操作者负责核验。"; RefreshPanel();
        }
        catch (Exception ex) { Message.Text = "结果未保存：" + ex.Message; }
    }
    private void BeginRound_Click(object sender, RoutedEventArgs e)
    {
        if (_lab is null || _server is null) return;
        _lab.BeginTestRound(SelectedStation); PhysicalConfirmed.IsChecked = false;
        Message.Text = "已开始新一轮测试记录；点位和任务未复位。"; RefreshPanel();
    }
    private void ExportStageReport_Click(object sender, RoutedEventArgs e) => SaveText("Markdown报告|*.md", $"PLC逐阶段结果-{DateTime.Now:yyyyMMdd-HHmmss}.md",
        () => _lab?.ExportStageReport() ?? throw new InvalidOperationException("尚无会话。"));

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_server is not null) return;
        var dialog = new OpenFileDialog { Filter = "JSON配置|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { LoadConfig(JsonSerializer.Deserialize<NetworkLabConfig>(File.ReadAllText(dialog.FileName)) ?? throw new ArgumentException("配置为空")); Message.Text = "配置已导入。"; }
        catch (Exception ex) { Message.Text = "导入失败：" + ex.Message; }
    }
    private void AddStation_Click(object sender, RoutedEventArgs e)
    {
        if (_server is not null) return;
        var name = NewStation.Text.Trim();
        if (name.Length == 0 || name == NetworkLabConfig.Shared || _mapping.Any(p => p.Station == name)) { Message.Text = "工位名称为空或重复。"; return; }
        var offset = _mapping.Where(p => p.Station != NetworkLabConfig.Shared).Max(p => p.Byte) + 2;
        foreach (var p in NetworkLabConfig.CreateDefault().Points.Where(p => p.Station == "CNC-001"))
        { var copy = p.Copy(); copy.Station = name; copy.Byte = offset; _mapping.Add(copy); }
        ReloadStations(); Message.Text = "已添加工位。请核定地址，启动前会检查地址冲突。";
    }
    private void RemoveStation_Click(object sender, RoutedEventArgs e)
    {
        if (_server is not null || MappingGrid.SelectedItem is not PointMapping selected) return;
        if (selected.Station == NetworkLabConfig.Shared || _mapping.Where(p => p.Signal == 0).Count() <= 1) { Message.Text = "共享倒料和最后一个工位不能移除。"; return; }
        foreach (var p in _mapping.Where(p => p.Station == selected.Station).ToArray()) _mapping.Remove(p);
        ReloadStations();
    }
    private void SaveConfig_Click(object sender, RoutedEventArgs e) => SaveText("JSON配置|*.json", "plc-lab-config.json", () => JsonSerializer.Serialize(ReadConfig(), new JsonSerializerOptions { WriteIndented = true }));
    private void SaveMappingCsv_Click(object sender, RoutedEventArgs e) => SaveText("CSV点表|*.csv", "plc-lab-signals.csv", () => ReadConfig().ExportSignalCsv());
    private void ExportSession_Click(object sender, RoutedEventArgs e) => SaveText("CSV记录|*.csv", $"PLC联调-{DateTime.Now:yyyyMMdd-HHmmss}.csv", () => _lab?.ExportCsv() ?? throw new InvalidOperationException("尚无联调会话。"));
    private void SaveText(string filter, string name, Func<string> content)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = name };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, content(), new UTF8Encoding(true)); Message.Text = "已导出：" + dialog.FileName; }
        catch (Exception ex) { Message.Text = "导出失败：" + ex.Message; }
    }

    private async void RunSmokeAsync(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs(); var at = Array.IndexOf(args, "--network-smoke-test");
        if (at < 0 || at + 1 >= args.Length) return;
        var path = Path.GetFullPath(args[at + 1]);
        void RenderWindow(string destination)
        {
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); using var stream = File.Create(destination); encoder.Save(stream);
        }
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            PortText.Text = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(); listener.Stop();
            Start_Click(this, new());
            if (_server is null || _lab is null) throw new InvalidOperationException(Message.Text);
            ManualSet(0, true);
            StageChoice.SelectedIndex = 2;
            var before = _lab.Snapshot(SelectedStation).Select(s => s.Value).ToArray();
            StageChoice.SelectedIndex = 4;
            if (!_lab.Snapshot(SelectedStation).Select(s => s.Value).SequenceEqual(before)) throw new InvalidOperationException("标记阶段改变了电平");
            if (Points.Items.Cast<NetworkPointRow>().Any(p => p.Owner == "H" && (p.CanSet || p.CanClear))) throw new InvalidOperationException("H被允许手动输入");
            AutoMode.SelectedIndex = 1;
            if (!_lab.Automatic) throw new InvalidOperationException("模式切换失败");
            AutoMode.SelectedIndex = 0;
            using var uiClient = new System.Net.Sockets.TcpClient();
            await uiClient.ConnectAsync(System.Net.IPAddress.Loopback, _server.Port);
            var uiStream = uiClient.GetStream();
            // One real TCP H BOOL write exercises the server while the WPF dispatcher is running.
            await uiStream.WriteAsync(Convert.FromHexString("0300002402F080320100000001000E00050501120A100100010001840000040003000101"));
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var head = new byte[4]; await uiStream.ReadExactlyAsync(head, budget.Token);
            var response = new byte[System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2))];
            head.CopyTo(response, 0); await uiStream.ReadExactlyAsync(response.AsMemory(4), budget.Token);
            if (response[21] != 0xff || !_lab.Snapshot(SelectedStation).Single(s => s.Mapping.Signal == 2).Value)
                throw new InvalidOperationException("WPF运行时S7写回未显示");
            ManualSet(0, false);
            ObservationNote.Text = "UI检查：无真实动作"; Observe_Click(this, new());
            if (!_lab.ExportCsv().Contains("UI检查")) throw new InvalidOperationException("观察未落盘");
            Vehicle.Text = "UI-TEST"; TaskId.Text = "UI-ONLY"; ObservationNote.Text = "仅检验结果记录，不代表实车通过";
            TestOutcome.SelectedIndex = 1; StageTest_Click(this, new());
            if (_lab.TestResults().Single().Outcome != "失败" || !_lab.ExportStageReport().Contains("未测"))
                throw new InvalidOperationException("阶段结果或未测状态未保存");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
            RenderWindow(path);
            NetworkTabs.SelectedIndex = 1;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!TestSummary.Text.Contains("失败 1") || StageResults.Items.Count != 28) throw new InvalidOperationException("阶段结果页未反映28阶段及失败记录");
            RenderWindow(path + ".stages.png");
            NetworkTabs.SelectedIndex = 0;
            await StopAsync();
            var closed = await uiStream.ReadAsync(new byte[1], budget.Token);
            if (closed != 0) throw new InvalidOperationException("停止后调度连接未断开");
            RefreshPanel();
            if (!HealthText.Text.Contains("服务未运行") || Points.Items.Cast<NetworkPointRow>().Where(p => p.Owner == "H").Any(p => !p.Value.Contains("旧")))
                throw new InvalidOperationException("停止后H旧值未明确标记");
            RenderWindow(path + ".disconnected.png");
            File.WriteAllText(path + ".result.txt", "PASS: 独立S7启动/停止、P输入、H只读、阶段隔离、模式切换、观察落盘、阶段结果、断连旧值及窗口渲染。");
            Close();
        }
        catch (Exception ex) { File.WriteAllText(path + ".result.txt", ex.ToString()); await StopAsync(); System.Windows.Application.Current.Shutdown(1); }
    }
}
