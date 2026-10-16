using System.Collections.ObjectModel;
using System.Globalization;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;

namespace AgvDispatch.Wpf.ViewModels;

public sealed record HistoryCategoryChoice(TaskHistoryCategory Value, string Label);
public sealed record HistorySessionChoice(string Value, string Label);
public sealed partial class TaskCenterViewModel
{
    public IReadOnlyList<HistoryCategoryChoice> HistoryCategories { get; } = [new(TaskHistoryCategory.All, "全部类别"),
        new(TaskHistoryCategory.Cancellation, "取消"), new(TaskHistoryCategory.Recovery, "恢复核对"), new(TaskHistoryCategory.TrafficRelease, "路权解锁")];
    public ObservableCollection<HistorySessionChoice> HistorySessions { get; } = [new("", "全部批次")];
    private HistoryCategoryChoice _historyCategory = new(TaskHistoryCategory.All, "全部类别");
    private HistorySessionChoice _historySession = new("", "全部批次");
    private string _historyFromText = "", _historyToText = "", _historyMessage = "";
    private int _historyPage = 1, _historyTotal;
    private bool _historyBusy;
    private TaskHistoryQuery _historyFilter = new("");
    private CancellationTokenSource? _historyRequest;
    public HistoryCategoryChoice HistoryCategory { get => _historyCategory; set { if (value is not null) SetProperty(ref _historyCategory, value); } }
    public HistorySessionChoice HistorySession
    {
        get => _historySession;
        set
        {
            // Batch choices are replaced on refresh; the ComboBox needs the new item instance
            // even when the record value equals the previous choice.
            if (value is not null && !ReferenceEquals(_historySession, value))
            { _historySession = value; RaisePropertyChanged(nameof(HistorySession)); }
        }
    }
    public string HistoryFromText { get => _historyFromText; set => SetProperty(ref _historyFromText, value); }
    public string HistoryToText { get => _historyToText; set => SetProperty(ref _historyToText, value); }
    public string HistoryMessage { get => _historyMessage; private set => SetProperty(ref _historyMessage, value); }
    public bool IsHistoryBusy { get => _historyBusy; private set { SetProperty(ref _historyBusy, value); RaisePropertyChanged(nameof(OperationHistorySummary)); UpdateHistoryCommands(); } }
    public int HistoryPageCount => Math.Max(1, (_historyTotal + 49) / 50);
    public string OperationHistorySummary => Detail == null ? "请选择任务。" : IsHistoryBusy ? "正在查询操作历史…" :
        $"第 {_historyPage} / {HistoryPageCount} 页 · 共 {_historyTotal} 条 · 每页50条 · 时间倒序，最新在前";
    public DelegateCommand ApplyHistoryFilterCommand { get; private set; } = null!;
    public DelegateCommand ResetHistoryFilterCommand { get; private set; } = null!;
    public DelegateCommand PreviousHistoryPageCommand { get; private set; } = null!;
    public DelegateCommand NextHistoryPageCommand { get; private set; } = null!;
    private void InitializeHistory()
    {
        _historyCategory = HistoryCategories[0]; _historySession = HistorySessions[0];
        InitializeHistoryExport();
        ApplyHistoryFilterCommand = new(async () => await ApplyHistoryFilterAsync(), () => Detail != null && !IsHistoryBusy && !IsHistoryExporting);
        ResetHistoryFilterCommand = new(async () => { ResetHistoryForTask(); await LoadHistoryAsync(); }, () => Detail != null && !IsHistoryBusy && !IsHistoryExporting);
        PreviousHistoryPageCommand = new(async () => { _historyPage--; ClearHistoryRows(); await LoadHistoryAsync(); }, () => Detail != null && !IsHistoryBusy && !IsHistoryExporting && _historyPage > 1);
        NextHistoryPageCommand = new(async () => { _historyPage++; ClearHistoryRows(); await LoadHistoryAsync(); }, () => Detail != null && !IsHistoryBusy && !IsHistoryExporting && _historyPage < HistoryPageCount);
    }
    public async Task ApplyHistoryFilterAsync()
    {
        if (Detail == null) return;
        if (!TryHistoryTime(HistoryFromText, out var from) || !TryHistoryTime(HistoryToText, out var to))
        { HistoryMessage = "时间格式：yyyy-MM-dd HH:mm:ss（本地时间），留空表示不限。"; return; }
        if (from.HasValue && to.HasValue && from > to) { HistoryMessage = "起始时间不得晚于结束时间。"; return; }
        _historyFilter = new("", HistoryCategory.Value, from, to, HistorySession.Value);
        _historyPage = 1; ClearHistoryRows(); await LoadHistoryAsync();
    }
    private static bool TryHistoryTime(string text, out DateTimeOffset? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateTimeOffset.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time)) return false;
        result = time; return true;
    }
    private async Task LoadHistoryAsync()
    {
        _historyRequest?.Cancel();
        if (SelectedTask == null || Detail == null || _navigation == null || _navigation.IsCancellationRequested) return;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_navigation.Token);
        _historyRequest = request;
        var query = _historyFilter with { TaskId = SelectedTask.TaskId, Page = _historyPage };
        IsHistoryBusy = true;
        try
        {
            var result = await _client.GetTaskHistoryAsync(query, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (_historyRequest != request || SelectedTask?.TaskId != query.TaskId) return;
            var selectedId = SelectedOperation?.Id;
            _historyPage = result.Page; _historyTotal = result.TotalCount;
            Replace(OperationHistory, result.Items);
            SelectedOperation = OperationHistory.FirstOrDefault(e => e.Id == selectedId) ?? OperationHistory.FirstOrDefault();
            var session = HistorySession.Value;
            Replace(HistorySessions, new[] { new HistorySessionChoice("", "全部批次") }
                .Concat(result.SessionIds.Select(id => new HistorySessionChoice(id, id))));
            // Keep a chosen batch even when it currently has no matching records.
            if (!HistorySessions.Any(s => s.Value == session)) HistorySessions.Add(new(session, session));
            HistorySession = HistorySessions.First(s => s.Value == session);
            HistoryMessage = (result.TotalCount == 0 ? "没有符合条件的记录。" : "") +
                (query.SessionId != "" ? "此批次仅含明确关联的操作；取消请求和路权解锁未记录批次编号，不包含在内。" :
                "全场事件仅关联本任务参与的恢复批次；长证据可在所选记录中查看。");
            RaisePropertyChanged(nameof(OperationHistorySummary));
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        { if (_historyRequest == request) HistoryMessage = "历史查询失败（已有记录可能已过期）：" + error.Message; }
        finally
        {
            if (_historyRequest == request) { _historyRequest = null; IsHistoryBusy = false; }
            request.Dispose();
        }
    }
    private void ClearHistoryRows() { OperationHistory.Clear(); SelectedOperation = null; }
    private void ResetHistoryForTask()
    {
        _historyExportRequest?.Cancel();
        _historyRequest?.Cancel(); _historyRequest = null; IsHistoryBusy = false;
        _historyPage = 1; _historyTotal = 0; _historyFilter = new("");
        HistoryCategory = HistoryCategories[0]; HistoryFromText = ""; HistoryToText = "";
        Replace(HistorySessions, new[] { new HistorySessionChoice("", "全部批次") }); HistorySession = HistorySessions[0];
        HistoryMessage = ""; ClearHistoryRows(); RaisePropertyChanged(nameof(OperationHistorySummary)); UpdateHistoryCommands();
    }
    private void UpdateHistoryCommands()
    {
        CancelHistoryExportCommand?.RaiseCanExecuteChanged();
        CleanExportCacheCommand?.RaiseCanExecuteChanged();
        ExportHistoryCommand?.RaiseCanExecuteChanged();
        ApplyHistoryFilterCommand?.RaiseCanExecuteChanged(); ResetHistoryFilterCommand?.RaiseCanExecuteChanged();
        PreviousHistoryPageCommand?.RaiseCanExecuteChanged(); NextHistoryPageCommand?.RaiseCanExecuteChanged();
    }
}
