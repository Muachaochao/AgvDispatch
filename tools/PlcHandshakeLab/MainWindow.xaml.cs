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

public sealed record SignalCard(int Index, string Name, string AddressLabel, string Bit, string Expectation, Brush Lamp, Brush Border, bool Changed);
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

    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public partial class MainWindow : Window
{
    private readonly LabSession _session = new();
    private readonly LabViewModel _viewModel = new();
    private readonly DispatcherTimer _timer = new();
    private bool _updating;
    private string _status = "已暂停 · 可单步或自动演示";

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
        Closed += (_, _) => _timer.Stop();
        Loaded += OnLoaded;
        Refresh();
    }

    private void Refresh()
    {
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
    private void SelectStage(int index) { Stop("单步 / 跳转 · 已加载标准电平"); _session.GoTo(index); Refresh(); }
    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
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
            File.WriteAllText(dialog.FileName, _session.ExportCsv(), new UTF8Encoding(true));
            _session.Note("导出", "已导出最近500条以内的模拟事件");
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
}
