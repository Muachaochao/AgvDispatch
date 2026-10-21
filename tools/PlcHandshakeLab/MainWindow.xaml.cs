using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PlcHandshakeLab;

public sealed record SignalCard(int Index, string Name, string AddressLabel, string Bit, string Expectation, Brush Lamp, Brush Border, bool Changed, bool CanToggle = true);
public sealed record StageRow(int Number, string Name, string[] Cells);

public sealed class LabViewModel : INotifyPropertyChanged
{
    public ObservableCollection<SignalCard> Signals { get; } = [];
    public ObservableCollection<LabEvent> Logs { get; } = [];
    public StageRow[] Rows { get; } = Sequence.Stages.Select((s, i) => new StageRow(i + 1, s.Name,
        Enumerable.Range(0, 10).Select(j => Sequence.Edge(i, j)).ToArray())).ToArray();
    public string StageTitle { get; private set; } = "";
    public string Condition { get; private set; } = "";
    public string Status { get; private set; } = "";
    public string Comparison { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh(LabSession session, string status)
    {
        StageTitle = $"{session.Current + 1:00} / 28   {Sequence.Stages[session.Current].Name}";
        Condition = Sequence.Stages[session.Current].Condition;
        Status = status;
        var mismatches = Enumerable.Range(0, 10).Where(i => !Sequence.Matches(session.Current, i, session.Values[i])).ToArray();
        Comparison = mismatches.Length == 0 ? "电平符合本步标准（不代表物理条件已验证）" : "电平偏差：" + string.Join("、", mismatches.Select(i => Sequence.Signals[i].Name));
        Signals.Clear();
        for (var i = 0; i < 10; i++)
        {
            var definition = Sequence.Signals[i];
            var expected = Sequence.Stages[session.Current].Pattern[i];
            Signals.Add(new(i, definition.Name, $"{definition.Owner} : {definition.Address}", session.Values[i] ? "1" : "0",
                $"本步标准：{(expected == '?' ? "0 或 1" : expected.ToString())}",
                session.Values[i] ? Brushes.Teal : new SolidColorBrush(Color.FromRgb(202, 213, 225)),
                mismatches.Contains(i) ? Brushes.IndianRed : new SolidColorBrush(Color.FromRgb(218, 227, 237)), session.Changed[i]));
        }
        Logs.Clear();
        foreach (var entry in session.Events) Logs.Add(entry);
        OnChanged(nameof(StageTitle));
        OnChanged(nameof(Condition));
        OnChanged(nameof(Status));
        OnChanged(nameof(Comparison));
    }

    public void RefreshLive(LivePlcSnapshot? snapshot, string station, string status, IReadOnlyList<LabEvent> events, bool allowCall)
    {
        StageTitle = $"{station} · 虚拟总PLC实时信号";
        Condition = "触发满箱呼叫后，在Web刷新条件、选择车辆并提交任务。箱体、许可、倒料反馈由设备模型产生，H侧输出由正式后台写入。";
        Status = snapshot is null ? "未知 / 未取得新鲜设备快照" : $"采样 {snapshot.ObservedAt.ToLocalTime():HH:mm:ss.fff} · 故障={snapshot.Faulted} · 下游在线={snapshot.DownstreamOnline}";
        Comparison = status;
        Signals.Clear();
        for (var i = 0; i < 10; i++)
        {
            var definition = Sequence.Signals[i];
            var address = i < 6 && station == "CNC-002" ? definition.Address.Replace("0.", "2.") : definition.Address;
            bool? value = snapshot?.Values[i];
            Signals.Add(new(i, definition.Name, $"{definition.Owner} : DB1.DBX{address}", value is null ? "?" : value.Value ? "1" : "0",
                i == 0 ? "输入：置1发起呼叫" : "只读：设备实际值",
                value is null ? Brushes.SlateGray : value.Value ? Brushes.Teal : Brushes.LightGray, Brushes.LightSlateGray, false, i == 0 && allowCall));
        }
        Logs.Clear();
        foreach (var entry in events) Logs.Add(entry);
        OnChanged(nameof(StageTitle)); OnChanged(nameof(Condition)); OnChanged(nameof(Status)); OnChanged(nameof(Comparison));
    }

    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public partial class MainWindow : Window
{
    private readonly LabSession _session = new();
    private readonly LabViewModel _viewModel = new();
    private readonly DispatcherTimer _timer = new();
    private bool _updating;
    private string _status = "已暂停 · 可单步或自动演示";
    private LivePlcConnection? _live;
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<LabEvent> _liveEvents = [];
    private readonly Dictionary<string, bool[]> _previous = new();
    private string? _pendingStation;
    private DateTimeOffset _pendingAt;
    private string _liveMessage = "";
    private string SelectedStation => ((ComboBoxItem)LiveStation.SelectedItem).Content.ToString()!;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Timeline.Columns.Add(new DataGridTextColumn { Header = "步骤", Binding = new Binding("Number"), Width = 50 });
        Timeline.Columns.Add(new DataGridTextColumn { Header = "阶段", Binding = new Binding("Name"), Width = 188 });
        for (var i = 0; i < 10; i++)
        {
            var signal = Sequence.Signals[i];
            var cell = new Style(typeof(TextBlock));
            cell.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            cell.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            cell.Setters.Add(new Setter(TextBlock.FontSizeProperty, 15.0));
            Timeline.Columns.Add(new DataGridTextColumn { Header = $"{signal.Name}\n{signal.Owner}:{signal.Address}", Binding = new Binding($"Cells[{i}]"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 86, ElementStyle = cell });
        }
        _timer.Tick += (_, _) =>
        {
            SetInterval();
            if (!_session.Advance(Loop.IsChecked == true)) Stop(_session.IsAligned ? "本轮演示结束" : "存在电平偏差，演示已暂停");
            Refresh();
        };
        _liveTimer.Tick += (_, _) => PollLive();
        Closed += (_, _) => { _timer.Stop(); _liveTimer.Stop(); };
        Loaded += OnLoaded;
        Refresh();
    }

    private void Refresh()
    {
        if (_live is not null) { PollLive(); return; }
        _viewModel.Refresh(_session, _status);
        _updating = true;
        Timeline.SelectedIndex = _session.Current;
        Timeline.UpdateLayout();
        if (Timeline.SelectedItem is { } selected) Timeline.ScrollIntoView(selected);
        _updating = false;
        if (_viewModel.Logs.Count > 0) EventList.ScrollIntoView(_viewModel.Logs[^1]);
        PlayButton.Content = _timer.IsEnabled ? "● 演示进行中" : "▶ 自动演示";
    }

    private void SetInterval() => _timer.Interval = TimeSpan.FromSeconds(new[] { .5, 1, 2, 3 }[Math.Max(0, Speed.SelectedIndex)]);
    private void Stop(string status) { _timer.Stop(); _status = status; }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_live is not null) return;
        if (!_session.IsAligned) { Stop("请恢复本步标准值或修正偏差后播放"); Refresh(); return; }
        if (_session.Current == 27) _session.Reset();
        _status = "自动演示 · 各步条件假定成立";
        SetInterval();
        _timer.Start();
        Refresh();
    }
    private void Pause_Click(object sender, RoutedEventArgs e) { Stop("已暂停"); Refresh(); }
    private void Previous_Click(object sender, RoutedEventArgs e) => SelectStage(Math.Max(0, _session.Current - 1));
    private void Next_Click(object sender, RoutedEventArgs e) => SelectStage(Math.Min(27, _session.Current + 1));
    private void Reset_Click(object sender, RoutedEventArgs e) { Stop("已重置至正常接料"); _session.Reset(); Refresh(); }
    private void Restore_Click(object sender, RoutedEventArgs e) => SelectStage(_session.Current);
    private void SelectStage(int index) { if (_live is not null) return; Stop("单步 / 跳转 · 已加载标准电平"); _session.GoTo(index); Refresh(); }
    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_live is not null) { if ((int)((Button)sender).Tag == 0) Call_Click(sender, e); return; }
        Stop("手动模拟 · 自动演示已暂停");
        _session.Toggle((int)((Button)sender).Tag);
        Refresh();
    }
    private void Timeline_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && Timeline.SelectedIndex >= 0) SelectStage(Timeline.SelectedIndex);
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = $"PLC握手模拟日志-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var csv = _live is null ? _session.ExportCsv() : "时间,类型,点位,原值,新值,说明\r\n" + string.Join("\r\n", _liveEvents.Select(entry => string.Join(",", new[] {entry.Time.ToString("O"),entry.Kind,entry.Signal,entry.Before,entry.After,entry.Detail}.Select(value => "\"" + value.Replace("\"", "\"\"") + "\""))));
            File.WriteAllText(dialog.FileName, csv, new UTF8Encoding(true));
            if (_live is null) _session.Note("导出", "已导出最近500条以内的模拟事件");
            Refresh();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, exception.Message, "日志导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // 可重复的离屏渲染及UI动作检查；不触发任何现场连接。
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Refresh();
        var args = Environment.GetCommandLineArgs();
        var labArg = Array.IndexOf(args, "--lab");
        if (labArg >= 0 && labArg + 1 < args.Length) { LabRoot.Text = args[labArg + 1]; ConnectLab_Click(this, new()); }
        var liveSmoke = Array.IndexOf(args, "--live-smoke-test");
        if (liveSmoke >= 0) { await RunLiveSmokeAsync(args, liveSmoke); return; }
        var position = Array.IndexOf(args, "--smoke-test");
        if (position < 0) return;
        try
        {
            if (position + 1 >= args.Length) throw new ArgumentException("缺少截图路径");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Timeline.SelectedIndex = 9;
            if (_session.Current != 9) throw new InvalidOperationException("表格跳转未生效");
            _session.Toggle(8);
            Refresh();
            if (_session.IsAligned || !_viewModel.Comparison.Contains("Ready")) throw new InvalidOperationException("偏差显示失败");
            SelectStage(18);
            if (_session.Values[7] || !_session.Values[9]) throw new InvalidOperationException("退出阶段电平错误");
            Speed.SelectedIndex = 0;
            var tickObserved = new TaskCompletionSource();
            EventHandler tickObserver = (_, _) => tickObserved.TrySetResult();
            _timer.Tick += tickObserver;
            Play_Click(this, new());
            if (!_timer.IsEnabled) throw new InvalidOperationException("自动播放未启动");
            await tickObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _timer.Tick -= tickObserver;
            if (_session.Current != 19) throw new InvalidOperationException("定时自动推进失败");
            Pause_Click(this, new());
            if (_timer.IsEnabled) throw new InvalidOperationException("暂停失败");
            Speed.SelectedIndex = 1;
            SelectStage(21);
            _session.Toggle(1);
            Refresh();
            if (!_session.IsAligned) throw new InvalidOperationException("第22步可选检测值错误");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (Timeline.SelectedIndex != 21) throw new InvalidOperationException("当前阶段行未选中");
            Timeline.ScrollIntoView(Timeline.SelectedItem);
            EventList.ScrollIntoView(_viewModel.Logs[^1]);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.GetFullPath(args[position + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = File.Create(path)) encoder.Save(stream);
            File.WriteAllText(path + ".result.txt", "PASS: WPF窗口加载、偏差提示、自动启动/暂停、退出阶段及第22步可选值、截图渲染。");
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception exception)
        {
            if (position + 1 < args.Length) File.WriteAllText(args[position + 1] + ".result.txt", exception.ToString());
            System.Windows.Application.Current.Shutdown(1);
        }
    }

    private void BrowseLab_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择Web联调虚拟设备目录" };
        if (dialog.ShowDialog(this) == true) LabRoot.Text = dialog.FolderName;
    }
    private void ConnectLab_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var connection = new LivePlcConnection(LabRoot.Text);
            connection.Read(SelectedStation);
            Stop("联调时禁止自动演示");
            _live = connection; _previous.Clear(); _pendingStation = null;
            _liveMessage = "已连接虚拟PLC。先选择工位，再触发满箱呼叫。";
            ModeLabel.Text = "虚拟PLC联调 · 不连接现场设备";
            OfflineControls.IsEnabled = false; Timeline.IsEnabled = false; LabRoot.IsEnabled = false;
            AddLiveEvent("连接", "", "", "", "连接 " + connection.Root);
            _liveTimer.Start(); PollLive();
        }
        catch (Exception ex) { LiveStatus.Text = "连接失败：" + ex.Message; }
    }
    private void DisconnectLab_Click(object sender, RoutedEventArgs e)
    {
        _liveTimer.Stop(); _live = null; _pendingStation = null;
        OfflineControls.IsEnabled = true; Timeline.IsEnabled = true; LabRoot.IsEnabled = true; CallButton.IsEnabled = false;
        ModeLabel.Text = "离线模拟 · 不连接现场 PLC";
        LiveStatus.Text = "已断开观察；没有停止设备或清除点位。";
        Stop("离线28阶段 · 已暂停"); Refresh();
    }
    private void LiveStation_Changed(object sender, SelectionChangedEventArgs e)
    { if (_live is not null) PollLive(); }
    private void Call_Click(object sender, RoutedEventArgs e)
    {
        if (_live is null || _pendingStation is not null) return;
        try
        {
            var before = _live.Read(SelectedStation);
            _liveMessage = _live.Call(SelectedStation);
            if (!before.Values[0]) { _pendingStation = SelectedStation; _pendingAt = DateTimeOffset.UtcNow; }
            AddLiveEvent("虚拟输入", SelectedStation + "/满箱呼叫", "", "", _liveMessage);
        }
        catch (Exception ex) { _liveMessage = "未触发：" + ex.Message; }
        PollLive();
    }
    private void PollLive()
    {
        if (_live is null) return;
        try
        {
            if (_pendingStation is not null)
            {
                var pending = _live.Read(_pendingStation);
                if (pending.Values[0]) { _liveMessage = _pendingStation + " 满箱呼叫已反馈为1，请在Web刷新条件并提交。"; _pendingStation = null; }
                else if (DateTimeOffset.UtcNow - _pendingAt > TimeSpan.FromSeconds(5)) { _pendingStation = null; _liveMessage = "5秒内未观察到呼叫反馈；请核实设备日志，不认定输入已生效。"; }
            }
            var snapshot = _live.Read(SelectedStation);
            _previous.TryGetValue(SelectedStation, out var previous);
            for (var i = 0; i < 10; i++) if (previous is null || previous[i] != snapshot.Values[i])
                AddLiveEvent(previous is null ? "初始观察" : "信号变化", SelectedStation + "/" + Sequence.Signals[i].Name,
                    previous is null ? "?" : previous[i] ? "1" : "0", snapshot.Values[i] ? "1" : "0", "虚拟设备实时快照；非28阶段推演");
            _previous[SelectedStation] = snapshot.Values;
            CallButton.IsEnabled = _pendingStation is null && !snapshot.Faulted && snapshot.DownstreamOnline && snapshot.Values[1] && !snapshot.Values[0] && !snapshot.Values[2] && !snapshot.Values[4];
            LiveStatus.Text = _liveMessage;
            _viewModel.RefreshLive(snapshot, SelectedStation, _liveMessage, _liveEvents, CallButton.IsEnabled);
        }
        catch (Exception ex)
        {
            CallButton.IsEnabled = false;
            LiveStatus.Text = "观察失败：" + ex.Message;
            _viewModel.RefreshLive(null, SelectedStation, LiveStatus.Text, _liveEvents, false);
        }
    }
    private void AddLiveEvent(string kind, string signal, string before, string after, string detail)
    {
        _liveEvents.Add(new(DateTimeOffset.Now, 0, kind, signal, before, after,
            string.IsNullOrEmpty(signal) ? detail : $"{signal} {before} → {after}；{detail}"));
        if (_liveEvents.Count > 500) _liveEvents.RemoveAt(0);
    }
}
