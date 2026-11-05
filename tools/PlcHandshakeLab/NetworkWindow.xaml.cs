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

public sealed record NetworkPointRow(int Signal, string Owner, string Name, string Address, string Value, string Applied, string Quality, string Source, Brush Color, bool CanSet, bool CanClear, string Expected);
public sealed record StageResultRow(int Stage, string Name, string Outcome, string Time, string Note)
{
    public string Tooltip => $"{Stage:00} · {Name} · {Outcome}";
}

public partial class NetworkWindow : Window
{
    private readonly ObservableCollection<PointMapping> _mapping = [];
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private NetworkLab? _lab;
    private S7LabServer? _server;
    private LabControlServer? _control;
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
    private StageResultRow[] _resultRows = [];
    private int _inspectionStage = 1;
    private CancellationTokenSource? _exportCancellation;
    private Task? _exportTask;

    public NetworkWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            PPoints.RowHeight = Points.RowHeight = ActualHeight < 820 ? 36 : 46;
        };
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
                _exportCancellation?.Cancel();
                if (_exportTask is { } exporting)
                    try { await exporting; } catch (Exception) { /* Export handler reports failure and removes its temporary file. */ }
                foreach (var history in OwnedWindows.OfType<HistoryWindow>().ToArray()) await history.CloseForOwnerAsync();
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
        var selectedBatch = BatchStationList.SelectedItems.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batchStations = _mapping.Where(p => p.Signal == 0).Select(p => p.Station).ToArray();
        BatchStationList.ItemsSource = batchStations;
        BatchStationList.UnselectAll();
        if (selectedBatch.Count == 0)
            selectedBatch = batchStations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var station in batchStations.Where(selectedBatch.Contains))
            BatchStationList.SelectedItems.Add(station);
        StageChoice.SelectedIndex = _markedStages.GetValueOrDefault(SelectedStation);
        _loading = false;
        _pointsSignature = "";
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
            var control = new LabControlServer(next);
            try { control.Start(); } catch { server.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
            _control = control;
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
            _lab?.StopBatchCalls();
            if (server is not null) await server.DisposeAsync();
            if (_control is not null) { await _control.DisposeAsync(); _control = null; }
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
        if (AutoMode.SelectedIndex == 0 && _lab.BatchCallActive)
        {
            Message.Text = "同批自动多轮运行期间不能切换到人工模式；请先停止后续轮次并核对当前P/H闭合。";
            _loading = true; AutoMode.SelectedIndex = 1; _loading = false;
            return;
        }
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
        _inspectionStage = Math.Max(1, StageChoice.SelectedIndex + 1);
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
    private void BatchStation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && BatchCallStatusText is not null) RefreshPanel();
    }
    private void BatchCallOnce_Click(object sender, RoutedEventArgs e) => StartBatchCalls(1);
    private void BatchCallRounds_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(BatchRoundsText.Text.Trim(), out var rounds) || rounds is < 1 or > 100)
        {
            Message.Text = "自动多轮次数应为1至100；当前没有写入任何工位。";
            return;
        }
        StartBatchCalls(rounds);
    }
    private void StartBatchCalls(int rounds)
    {
        if (_server is null || _lab is null) return;
        try
        {
            var health = CommunicationStatus.Evaluate(true, _server.Clients, _server.LastDataRequest, DateTimeOffset.Now);
            if (!health.Fresh) throw new InvalidOperationException("调度尚未连接或S7通信无效，不能同批触发。");
            var stations = BatchStationList.SelectedItems.Cast<string>().ToArray();
            _lab.StartBatchCalls(stations, rounds);
            Message.Text = $"已提交{stations.Length}个工位的同批呼叫；当前第1轮，后续轮次只在全部工位完整闭环后触发。";
            RefreshPanel();
        }
        catch (Exception ex) { Message.Text = "同批呼叫未启动：" + ex.Message; RefreshPanel(); }
    }
    private void BatchStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _lab?.StopBatchCalls();
            Message.Text = "已停止后续自动轮次；没有清除P/H电平，当前轮次仍须按正常流程闭合。";
            RefreshPanel();
        }
        catch (Exception ex) { Message.Text = "停止同批轮次失败：" + ex.Message; }
    }
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
        var displayedPoints = snapshot ?? _mapping.Where(p => p.Station == SelectedStation || p.Station == NetworkLabConfig.Shared)
            .Select(p => new PointState(p, p.Initial, DateTimeOffset.MinValue, "初值配置", "")).ToArray();
        var stage = Math.Max(0, StageChoice.SelectedIndex);
        var active = _server is not null && _server.Fault is null && _fatal is null;
        var now = DateTimeOffset.Now;
        var health = CommunicationStatus.Evaluate(active, _server?.Clients ?? 0, _server?.LastDataRequest, now);
        HealthText.Text = health.Label;
        HealthBanner.Background = health.Fresh ? Brushes.Honeydew : Brushes.MistyRose;
        HealthText.Foreground = health.Fresh ? Brushes.DarkGreen : Brushes.DarkRed;
        if (_lab is not null && AutoMode.SelectedIndex != (_lab.Automatic ? 1 : 0))
        { _loading = true; AutoMode.SelectedIndex = _lab.Automatic ? 1 : 0; _loading = false; }
        AutoState.Text = "模拟周期：" + (_lab?.AutomaticStatus ?? "尚未启动") + "；" + (_lab?.CallPlanStatus ?? "未启动循环呼叫") + "；模拟反馈不作为物理验收证据。";
        BatchCallStatusText.Text = _lab?.BatchCallStatus ?? "启动服务并连接调度后，可把所选工位在同一次扫描中置1。";
        var batchRunning = _lab?.BatchCallActive == true;
        BatchCallOnceButton.IsEnabled = active && _lab is not null && !batchRunning && BatchStationList.SelectedItems.Count >= 2;
        BatchCallRoundsButton.IsEnabled = BatchCallOnceButton.IsEnabled;
        BatchStopButton.IsEnabled = active && batchRunning;
        ConnectionStatus.Text = _server is null ? "服务未运行 · 停止后的电平只供回看" : _server.Fault ??
            $"{_lab!.Config.BindAddress}:{_server.Port} · 连接 {_server.Clients} · 最近请求 {(_server.LastRequest is { } last ? last.ToString("HH:mm:ss") + $" / {(DateTimeOffset.Now - last).TotalSeconds:F1}s前" : "尚无")} ";
        if (_server?.Fault is { } fault) Message.Text = fault;
        var signature = active + ":" + health.Fresh + ":" + _lab?.Automatic + ":" + SelectedStation + ":" + stage + ":" + now.ToUnixTimeSeconds() + ":" + string.Join(";", displayedPoints.Select(s => s.Mapping.Address + ":" + s.Value + ":" + s.UpdatedAt.Ticks));
        if (signature != _pointsSignature)
        {
        _pointsSignature = signature;
        var rows = displayedPoints.Select(s => new NetworkPointRow(s.Mapping.Signal, s.Mapping.Owner, s.Mapping.Name,
            s.Mapping.Address, snapshot is null ? "—" : (s.Value ? "1" : "0") + (s.Mapping.Owner == "H" && (!health.Fresh || !CommunicationStatus.PointFresh(s, now)) ? "·旧" : ""),
            $"{(s.Source == "初值" ? "初值" : s.Requested)} / {s.UpdatedAt:HH:mm:ss}",
            s.HostAccessedAt is { } at ? $"{(health.Fresh && CommunicationStatus.PointFresh(s, now) ? "已交换" : "旧记录")} {(now - at).TotalSeconds:F1}s前" : "尚未交换",
            $"{s.Source}；请求值{s.Requested}；最后变化/应用{s.UpdatedAt:O}；{s.HostAccess} {s.HostAccessedAt:O}。交换表示S7读或写，不等于物理动作完成。",
            s.Mapping.Owner == "H" && (!health.Fresh || !CommunicationStatus.PointFresh(s, now)) ? Brushes.Gray : s.Value ? Brushes.Teal : Brushes.SlateGray,
            active && s.Mapping.Owner == "P" && (!_lab!.Automatic || s.Mapping.Signal is not (8 or 9)),
            active && s.Mapping.Owner == "P" && (!_lab!.Automatic || s.Mapping.Signal is not (0 or 8 or 9)),
            s.Mapping.Signal < 10 ? "参考 " + LabPresentation.Expected(stage, s.Mapping.Signal) : "")).ToArray();
        PPoints.ItemsSource = rows.Where(p => p.Signal < 10 && p.Owner == "P").ToArray();
        Points.ItemsSource = rows.Where(p => p.Signal < 10 && p.Owner == "H").ToArray();
        FaultPoints.ItemsSource = rows.Where(p => p.Signal >= 10).ToArray();
        var faults = displayedPoints.Where(p => p.Mapping.Signal is 10 or 12 && p.Value || p.Mapping.Signal == 11 && !p.Value).Select(p => p.Mapping.Name).ToArray();
        FaultSummary.Text = snapshot is null ? "故障与下游联锁 · 未启动，展开查看配置" : faults.Length == 0 ? "故障与下游联锁 · 内存无故障，展开设置" : "联锁异常：" + string.Join("、", faults) + " · 展开核对";
        FaultSummary.Foreground = faults.Length > 0 && snapshot is not null ? Brushes.DarkRed : Brushes.DarkSlateGray;
        }
        StageCondition.Text = Sequence.Stages[stage].Condition;
        var stateBySignal = (snapshot ?? []).Where(s => s.Mapping.Signal < 10).ToDictionary(s => s.Mapping.Signal);
        var differences = stateBySignal.Values.Where(s => !Sequence.Matches(stage, s.Mapping.Signal, s.Value)).ToArray();
        var h = differences.Where(s => s.Mapping.Owner == "H").Select(s => s.Mapping.Name + "→" + Sequence.Stages[stage].Pattern[s.Mapping.Signal]);
        var p = differences.Where(s => s.Mapping.Owner == "P").Select(s => s.Mapping.Name + "→" + Sequence.Stages[stage].Pattern[s.Mapping.Signal]);
        WaitInfo.Text = _lab is null ? "启动服务后显示阶段对照。" : $"{string.Join("、", h.DefaultIfEmpty("本步H电平已符合"))}\n{(health.Fresh ? "仍须核对各点新鲜度及实际条件" : "通信无效，保留值不能作为新反馈")}\n人工标记停留 {_lab.StageElapsed(SelectedStation):F1} 秒";
        Guidance.Text = _lab is null ? "按现场实际情况操作P点；参考值见左侧，未启动不显示实际电平。" : $"{string.Join("、", p.DefaultIfEmpty("本步P电平已符合"))}\n先核实实际条件，再操作；选阶段不会写信号。";
        ObserveButton.IsEnabled = active;
        StageTestButton.IsEnabled = active;
        ConfirmClosureButton.IsEnabled = active;
        var results = _lab?.TestResults() ?? [];
        var round = _lab?.TestRound(SelectedStation) ?? 1;
        var resultSignature = SelectedStation + ":" + round + ":" + results.Length;
        if (_resultSignature != resultSignature)
        {
            _resultSignature = resultSignature;
            _resultRows = Enumerable.Range(1, 28).Select(index =>
            {
                var last = results.LastOrDefault(r => r.Station == SelectedStation && r.Round == round && r.Stage == index);
                return new StageResultRow(index, Sequence.Stages[index - 1].Name, last?.Outcome ?? "未测", last?.Time.ToString("HH:mm:ss") ?? "", last?.Note ?? "");
            }).ToArray();
            StageProgress.ItemsSource = _resultRows;
            ApplyResultFilter();
            TestSummary.Text = $"{SelectedStation} · 第{round}轮 · 通过 {_resultRows.Count(r => r.Outcome == "通过")}/28 · 失败 {_resultRows.Count(r => r.Outcome == "失败")} · 跳过 {_resultRows.Count(r => r.Outcome == "跳过")} · 未测 {_resultRows.Count(r => r.Outcome == "未测")}";
            RoundProgress.Value = _resultRows.Count(r => r.Outcome != "未测");
        }
        ProgressLabel.Text = $"已记录 {RoundProgress.Value}/28 · 人工标记 {stage + 1:00}";
        RefreshInspection();
        var events = _lab?.Events();
        if (events is { Length: > 0 } && !ReferenceEquals(events[^1], _lastEvent))
        {
            _lastEvent = events[^1];
            var filtered = events.Where(e => LabPresentation.MatchesEvent(e, EventFilter.SelectedIndex)).ToArray();
            Events.ItemsSource = filtered.Select(e => $"{e.Time:HH:mm:ss.fff} [{e.Kind}] {e.Station} {e.Point} {e.Before} → {e.After} {e.Detail}").ToArray();
            EventCount.Text = $"本页 {filtered.Length} / 最近 {events.Length} 条 · 筛选不影响完整导出";
            if (Events.Items.Count > 0) Events.ScrollIntoView(Events.Items[^1]);
            Events.UpdateLayout();
            FindScrollViewer(Events)?.ScrollToHorizontalOffset(0);
        }
    }

    private void EventFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Events is null) return;
        _lastEvent = null;
        RefreshPanel();
    }

    private void OpenEvents_Click(object sender, RoutedEventArgs e) => NetworkTabs.SelectedIndex = 4;

    private void ResultFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (StageResults is not null) ApplyResultFilter();
    }

    private void ApplyResultFilter()
    {
        StageResults.ItemsSource = _resultRows.Where(r => FailedOnly.IsChecked != true || r.Outcome == "失败").ToArray();
        StageResults.SelectedItem = StageResults.Items.Cast<StageResultRow>().FirstOrDefault(r => r.Stage == _inspectionStage);
    }

    private void InspectStage_Click(object sender, RoutedEventArgs e)
    {
        _inspectionStage = (int)((Button)sender).Tag;
        ApplyResultFilter();
        RefreshInspection();
    }

    private void ResultSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StageResults.SelectedItem is not StageResultRow row) return;
        _inspectionStage = row.Stage;
        RefreshInspection();
    }

    private void RefreshInspection()
    {
        if (InspectionPoints is null) return;
        var stage = _inspectionStage - 1;
        InspectionTitle.Text = $"查看 {_inspectionStage:00} · {Sequence.Stages[stage].Name}（不改变人工标记）";
        var last = _lab?.TestResults().LastOrDefault(r => r.Station == SelectedStation && r.Round == _lab.TestRound(SelectedStation) && r.Stage == _inspectionStage);
        InspectionNote.Text = "下表为当前内存与本阶段参考值；保存时记录见下方。";
        SavedInspection.Text = last is null ? "本阶段本轮尚无保存结果。" : $"保存于 {last.Time:HH:mm:ss} · {last.Outcome} · 车辆 {last.Vehicle} · 任务 {last.TaskId}\n观察：{last.Note}\n保存时差异：{(last.Differences.Length == 0 ? "电平符合参考，实际条件以原记录为准" : last.Differences)}\n保存时快照：{last.Snapshot}";
        var now = DateTimeOffset.Now;
        var health = CommunicationStatus.Evaluate(_server is not null && _fatal is null, _server?.Clients ?? 0, _server?.LastDataRequest, now);
        InspectionPoints.ItemsSource = LabPresentation.Compare(stage, _lab?.Snapshot(SelectedStation), health.Fresh, now);
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
    private async void ExportStageReport_Click(object sender, RoutedEventArgs e) => await ExportAsync("md");

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
    private async void ExportSession_Click(object sender, RoutedEventArgs e) => await ExportAsync("csv");
    private async void ExportBundle_Click(object sender, RoutedEventArgs e) => await ExportAsync("zip");
    private void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();
    private async Task ExportAsync(string format)
    {
        if (_exportTask is not null || _closeRequested) return;
        if (_lab is null) { Message.Text = "尚无联调会话。历史记录请使用“历史会话只读回看”。"; return; }
        var dialog = new SaveFileDialog { Filter = $"联调文件|*.{format}", FileName = $"PLC联调-{DateTime.Now:yyyyMMdd-HHmmss}.{format}" };
        if (dialog.ShowDialog(this) != true) return;
        var destination = dialog.FileName;
        try
        {
            using var capture = _lab.CaptureSession();
            using var cancellation = new CancellationTokenSource();
            _exportCancellation = cancellation; CancelExportButton.IsEnabled = true;
            ExportProgress.Text = "后台导出中 · 点位操作与S7通信继续；内容截至 " + capture.CapturedAt.ToString("HH:mm:ss");
            _exportTask = Task.Run(() =>
            {
                if (format == "zip") SessionExports.Bundle(capture, destination, cancellation.Token);
                else if (format == "md") SessionExports.Report(capture, destination, cancellation.Token);
                else SessionExports.Csv(capture, destination, cancellation.Token);
            });
            await _exportTask;
            Message.Text = "已导出：" + dialog.FileName;
        }
        catch (OperationCanceledException) { Message.Text = "导出已取消，原目标文件保留。"; }
        catch (Exception ex) { Message.Text = "导出失败（不停止S7服务）：" + ex.Message; }
        finally { _exportTask = null; _exportCancellation = null; CancelExportButton.IsEnabled = false; ExportProgress.Text = ""; }
    }
    private async void History_Click(object sender, RoutedEventArgs e)
    {
        if (!HistoryButton.IsEnabled || _closeRequested) return;
        var dialog = new OpenFileDialog { Filter = "原会话日志|*.jsonl", InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgvDispatch", "PlcHandshakeLab") };
        if (dialog.ShowDialog(this) != true) return;
        SessionCapture? capture = null;
        try
        {
            HistoryButton.IsEnabled = false;
            var source = dialog.FileName;
            Message.Text = "后台读取历史记录中；当前S7服务继续运行。";
            var loaded = await Task.Run(() =>
            {
                var opened = SessionCapture.OpenHistory(source);
                try { return (Capture: opened, History: SessionHistory.Read(opened)); }
                catch { opened.Dispose(); throw; }
            });
            capture = loaded.Capture;
            var history = loaded.History;
            if (_closeRequested) { capture.Dispose(); return; }
            new HistoryWindow(capture, history) { Owner = this }.Show();
            capture = null;
        }
        catch (Exception ex) { capture?.Dispose(); Message.Text = "读取历史失败：" + ex.Message; }
        finally { HistoryButton.IsEnabled = true; }
    }
    private void SaveText(string filter, string name, Func<string> content)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = name };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, content(), new UTF8Encoding(true)); Message.Text = "已导出：" + dialog.FileName; }
        catch (Exception ex) { Message.Text = "导出失败：" + ex.Message; }
    }

    private async void RunSmokeAsync(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--network-auto-serve"))
        {
            try
            {
                var configAt = Array.IndexOf(args, "--config");
                if (configAt >= 0)
                {
                    if (configAt + 1 >= args.Length) throw new ArgumentException("--config 缺少配置文件路径。");
                    var config = JsonSerializer.Deserialize<NetworkLabConfig>(
                        File.ReadAllText(Path.GetFullPath(args[configAt + 1])),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        ?? throw new InvalidDataException("PLC联调台配置为空。");
                    LoadConfig(config);
                }
                AutoMode.SelectedIndex = 1;
                Start_Click(this, new());
                if (_server is null) throw new InvalidOperationException(Message.Text);
                if (!args.Contains("--show")) Hide();
                return;
            }
            catch (Exception ex)
            {
                Message.Text = "自动服务启动失败：" + ex.Message;
                return;
            }
        }
        var at = Array.IndexOf(args, "--network-smoke-test");
        if (at < 0 || at + 1 >= args.Length) return;
        var path = Path.GetFullPath(args[at + 1]);
        void RenderWindow(string destination, Window? target = null)
        {
            target ??= this; target.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(target);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); using var stream = File.Create(destination); encoder.Save(stream);
        }
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            PortText.Text = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(); listener.Stop();
            Start_Click(this, new());
            if (_server is null || _lab is null) throw new InvalidOperationException(Message.Text);
            if (BatchStationList.Items.Count != 4 || BatchStationList.SelectedItems.Count != 4)
                throw new InvalidOperationException("同批呼叫页未默认选择当前四个工位");
            NetworkTabs.SelectedIndex = 1;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
            RenderWindow(path + ".batch.png");
            NetworkTabs.SelectedIndex = 0;
            ManualSet(0, true);
            StageChoice.SelectedIndex = 2;
            var before = _lab.Snapshot(SelectedStation).Select(s => s.Value).ToArray();
            StageChoice.SelectedIndex = 4;
            if (!_lab.Snapshot(SelectedStation).Select(s => s.Value).SequenceEqual(before)) throw new InvalidOperationException("标记阶段改变了电平");
            if (Points.Items.Cast<NetworkPointRow>().Any(p => p.Owner == "H" && (p.CanSet || p.CanClear))) throw new InvalidOperationException("H被允许手动输入");
            if (PPoints.Items.Count != 4 || Points.Items.Count != 6 || FaultPoints.Items.Count != 3 || FaultExpander.IsExpanded)
                throw new InvalidOperationException("核心10点未分为4个P/6个H，或故障点未独立折叠");
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
            NetworkTabs.SelectedIndex = 2;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!TestSummary.Text.Contains("失败 1") || StageResults.Items.Count != 28) throw new InvalidOperationException("阶段结果页未反映28阶段及失败记录");
            var inspectedBefore = _lab.Snapshot(SelectedStation).Select(s => s.Value).ToArray();
            var markedBefore = StageChoice.SelectedIndex;
            var journalBefore = new FileInfo(_lab.JournalPath).Length;
            FailedOnly.IsChecked = true;
            if (StageResults.Items.Count != 1 || ((StageResultRow)StageResults.Items[0]).Stage != 5 || RoundProgress.Value != 1 || StageProgress.Items.Count != 28)
                throw new InvalidOperationException("失败筛选或28阶段进度错误");
            StageResults.SelectedIndex = 0;
            if (_inspectionStage != 5 || InspectionPoints.Items.Count != 10 || !SavedInspection.Text.Contains("UI-ONLY"))
                throw new InvalidOperationException("阶段点击未显示当前对照与独立保存记录");
            InspectStage_Click(new Button { Tag = 22 }, new());
            if (((SignalComparison)InspectionPoints.Items[1]).Expected != "0/1") throw new InvalidOperationException("第22阶段箱体参考范围错误");
            if (StageChoice.SelectedIndex != markedBefore || !_lab.Snapshot(SelectedStation).Select(s => s.Value).SequenceEqual(inspectedBefore) || new FileInfo(_lab.JournalPath).Length != journalBefore)
                throw new InvalidOperationException("只读查看或筛选改变了人工标记、点位或日志");
            FailedOnly.IsChecked = false;
            StageResults.SelectedItem = StageResults.Items.Cast<StageResultRow>().Single(r => r.Stage == 5);
            RenderWindow(path + ".stages.png");
            using (var evidence = _lab.CaptureSession())
                await Task.Run(() => SessionExports.Bundle(evidence, path + ".evidence.zip"));
            var historyCapture = _lab.CaptureSession();
            var history = await Task.Run(() => SessionHistory.Read(historyCapture));
            var historyWindow = new HistoryWindow(historyCapture, history) { Owner = this };
            historyWindow.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!historyWindow.HasReadOnlyResults || history.Results.Single().Outcome != "失败") throw new InvalidOperationException("历史窗口未只读重建28阶段结果");
            RenderWindow(path + ".history.png", historyWindow);
            historyWindow.Close();
            NetworkTabs.SelectedIndex = 0;
            _lab.Note("拒绝写入", "UI筛选检查，无真实设备");
            var eventTotal = _lab.Events().Length;
            EventFilter.SelectedIndex = 3;
            RefreshPanel();
            if (!Events.Items.Cast<string>().Any(line => line.Contains("拒绝写入")) || !Events.Items.Cast<string>().Any(line => line.Contains("阶段测试")) ||
                Events.Items.Cast<string>().Any(line => line.Contains("[阶段标记]") || line.Contains("[现场观察]") || line.Contains("[监听]")))
                throw new InvalidOperationException("异常筛选未保留失败或拒绝写入");
            EventFilter.SelectedIndex = 2;
            if (Events.Items.Count == 0 || Events.Items.Cast<string>().Any(line => line.Contains("阶段标记"))) throw new InvalidOperationException("通信筛选错误");
            if (_lab.Events().Length != eventTotal || !_lab.ExportCsv().Contains("现场观察")) throw new InvalidOperationException("日志筛选损失原始证据");
            EventFilter.SelectedIndex = 0;
            // Verify the default collapsed layout at the minimum supported window size too.
            Width = MinWidth; Height = MinHeight;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
            foreach (var grid in new[] { PPoints, Points })
            {
                var scroll = FindScrollViewer(grid);
                if (scroll is null || scroll.ScrollableHeight > .01 || scroll.ScrollableWidth > .01)
                    throw new InvalidOperationException($"最小窗口{grid.Name}核心点位需要滚动 H={scroll?.ScrollableHeight} W={scroll?.ScrollableWidth}，未固定展示10点");
            }
            RenderWindow(path + ".compact.png");
            Width = 1380; Height = 900;
            NetworkTabs.SelectedIndex = 4;
            LogExpander.IsExpanded = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            RenderWindow(path + ".events.png");
            LogExpander.IsExpanded = false;
            NetworkTabs.SelectedIndex = 0;
            await StopAsync();
            var closed = await uiStream.ReadAsync(new byte[1], budget.Token);
            if (closed != 0) throw new InvalidOperationException("停止后调度连接未断开");
            RefreshPanel();
            if (!HealthText.Text.Contains("服务未运行") || Points.Items.Cast<NetworkPointRow>().Where(p => p.Owner == "H").Any(p => !p.Value.Contains("旧")))
                throw new InvalidOperationException("停止后H旧值未明确标记");
            RenderWindow(path + ".disconnected.png");
            File.WriteAllText(path + ".result.txt", "PASS: 独立S7启动/停止、4P/6H固定显示及故障折叠、最小窗口无点位滚动、阶段只读对照/0或1范围、28阶段进度及失败筛选、日志分类不损失证据、模式切换、观察落盘、断连旧值、后台证据包、只读历史及窗口渲染。");
            Close();
        }
        catch (Exception ex) { File.WriteAllText(path + ".result.txt", ex.ToString()); await StopAsync(); System.Windows.Application.Current.Shutdown(1); }
    }
}
