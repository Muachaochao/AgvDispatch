using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace PlcHandshakeLab;

public sealed record HistoryStageRow(int Stage, string Name, string Outcome, string Time, string Vehicle, string TaskId, string Note);

public partial class HistoryWindow : Window
{
    private readonly SessionCapture _capture;
    private readonly SessionHistory _history;
    private Task? _exportTask;
    private CancellationTokenSource? _cancellation;
    private bool _closing;
    private bool _closeRequested;
    private bool _closed;
    public HistoryWindow(SessionCapture capture, SessionHistory history)
    {
        InitializeComponent(); _capture = capture; _history = history;
        SourceInfo.Text = $"日志：{capture.JournalPath}\n快照：{capture.CapturedAt:yyyy-MM-dd HH:mm:ss} · {capture.Length:N0}字节 · 有效事件{history.EventCount:N0}条\n原工具版本：{history.SourceVersion}";
        Warnings.Text = history.Warning.Length == 0 ? "只显示原记录；“未测”不算通过，复测历史保留在完整报告中。" : history.Warning;
        GroupChoice.ItemsSource = history.Groups; GroupChoice.SelectedIndex = history.Groups.Count > 0 ? 0 : -1;
        Events.ItemsSource = history.RecentEvents.Select(e => $"{e.Time:yyyy-MM-dd HH:mm:ss.fff} · {e.Kind} · {e.Station} · 阶段{e.Stage} · {e.Point} {e.Before}→{e.After}\t{e.Detail}").ToArray();
        Closed += (_, _) => _closed = true;
        Closing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            if (_closeRequested) return;
            _closeRequested = true;
            Dispatcher.BeginInvoke(async () => await CloseForOwnerAsync());
        };
    }
    private void Group_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (GroupChoice.SelectedItem is not SessionGroup group) return;
        var rows = Enumerable.Range(1, 28).Select(stage =>
        {
            var last = _history.Results.LastOrDefault(r => r.Station == group.Station && r.Round == group.Round && r.Stage == stage);
            return new HistoryStageRow(stage, Sequence.Stages[stage - 1].Name, last?.Outcome ?? "未测", last?.Time.ToString("HH:mm:ss") ?? "", last?.Vehicle ?? "", last?.TaskId ?? "", last?.Note ?? "");
        }).ToArray();
        Results.ItemsSource = rows;
        Summary.Text = $"通过 {rows.Count(r => r.Outcome == "通过")} · 失败 {rows.Count(r => r.Outcome == "失败")} · 跳过 {rows.Count(r => r.Outcome == "跳过")} · 未测 {rows.Count(r => r.Outcome == "未测")}";
    }
    private async void Csv_Click(object sender, RoutedEventArgs e) => await ExportAsync("csv");
    private async void Report_Click(object sender, RoutedEventArgs e) => await ExportAsync("md");
    private async void Bundle_Click(object sender, RoutedEventArgs e) => await ExportAsync("zip");
    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private async Task ExportAsync(string format)
    {
        if (_exportTask is not null || _closeRequested) return;
        var dialog = new SaveFileDialog { Filter = $"联调文件|*.{format}", FileName = $"历史联调-{DateTime.Now:yyyyMMdd-HHmmss}.{format}" };
        if (dialog.ShowDialog(this) != true) return;
        var destination = dialog.FileName;
        try
        {
            using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
            CancelButton.IsEnabled = true; Progress.Text = "后台导出中；固定为打开窗口时的日志范围。";
            _exportTask = Task.Run(() =>
            {
                if (format == "zip") SessionExports.Bundle(_capture, destination, cancellation.Token);
                else if (format == "md") SessionExports.Report(_capture, destination, cancellation.Token);
                else SessionExports.Csv(_capture, destination, cancellation.Token);
            });
            await _exportTask; Progress.Text = "已导出：" + destination;
        }
        catch (OperationCanceledException) { Progress.Text = "已取消，原目标文件未替换。"; }
        catch (Exception ex) { Progress.Text = "导出失败：" + ex.Message; }
        finally { _exportTask = null; _cancellation = null; CancelButton.IsEnabled = false; }
    }
    internal bool HasReadOnlyResults => Results.IsReadOnly && Results.Items.Count == 28;
    internal async Task PrepareCloseAsync()
    {
        _closeRequested = true; _cancellation?.Cancel();
        if (_exportTask is { } task) try { await task; } catch (Exception) { /* Export removes its temporary file. */ }
        _capture.Dispose();
    }
    internal async Task CloseForOwnerAsync()
    {
        await PrepareCloseAsync();
        if (!_closed) { _closing = true; Close(); }
    }
}
