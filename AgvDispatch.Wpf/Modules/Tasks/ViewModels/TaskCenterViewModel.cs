using System.Collections.ObjectModel;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Regions;

namespace AgvDispatch.Wpf.ViewModels;

public sealed record TaskFilterChoice(TaskListFilter Value, string Label);

public sealed partial class TaskCenterViewModel : BindableBase, INavigationAware
{
    private readonly ITrafficControlClient _client;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _navigation;
    private CancellationTokenSource? _detailRequest;
    private TaskCenterRow? _selectedTask;
    private TaskCenterDetail? _detail;
    private TaskFilterChoice _selectedFilter;
    private string _search = "", _statusMessage = "等待连接后台服务。";
    private bool _isBusy, _updatingRows;
    private int _page = 1, _totalCount;

    public TaskCenterViewModel(ITrafficControlClient client, IRegionManager? regionManager = null)
    {
        _client = client;
        Filters = [new(TaskListFilter.All, "全部任务"), new(TaskListFilter.Waiting, "待调度"),
            new(TaskListFilter.Running, "执行中"), new(TaskListFilter.Failed, "异常 / 待处置"),
            new(TaskListFilter.Completed, "已完成"), new(TaskListFilter.Cancelled, "已取消")];
        _selectedFilter = Filters[0];
        RefreshCommand = new DelegateCommand(async () => await RefreshAsync(), () => !IsBusy);
        SearchCommand = new DelegateCommand(async () => { _page = 1; await RefreshAsync(); }, () => !IsBusy);
        PreviousPageCommand = new DelegateCommand(async () => { _page--; await RefreshAsync(); }, () => !IsBusy && _page > 1);
        NextPageCommand = new DelegateCommand(async () => { _page++; await RefreshAsync(); }, () => !IsBusy && _page < PageCount);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        InitializeHistory();
        InitializeTaskCreation();
        InitializeTaskParameters();
        InitializeTaskOperations(regionManager);
    }

    public IReadOnlyList<TaskFilterChoice> Filters { get; }
    public ObservableCollection<TaskCenterRow> Tasks { get; } = [];
    public ObservableCollection<TaskOperationHistoryEntry> OperationHistory { get; } = [];
    private TaskOperationHistoryEntry? _selectedOperation;
    public TaskOperationHistoryEntry? SelectedOperation { get => _selectedOperation; set => SetProperty(ref _selectedOperation, value); }
    public ObservableCollection<TaskStepDetail> Steps { get; } = [];
    public ObservableCollection<TaskCommandDetail> Commands { get; } = [];
    public ObservableCollection<TaskResourceDetail> Resources { get; } = [];
    public ObservableCollection<TaskPlcCondition> PlcConditions { get; } = [];
    public DelegateCommand RefreshCommand { get; }
    public DelegateCommand SearchCommand { get; }
    public DelegateCommand PreviousPageCommand { get; }
    public DelegateCommand NextPageCommand { get; }
    public string Search { get => _search; set => SetProperty(ref _search, value); }
    public TaskFilterChoice SelectedFilter
    {
        get => _selectedFilter;
        set { if (value is not null && SetProperty(ref _selectedFilter, value)) { _page = 1; _ = RefreshAsync(); } }
    }
    public TaskCenterRow? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value) && !_updatingRows)
            {
                ClearDetail();
                _ = LoadDetailAsync();
            }
        }
    }
    public TaskCenterDetail? Detail { get => _detail; private set { if (SetProperty(ref _detail, value)) UpdateTaskOperationCommands(); } }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public int PageCount => Math.Max(1, (int)Math.Ceiling(_totalCount / 50.0));
    public string PageText => $"第 {_page} / {PageCount} 页 · 共 {_totalCount} 条 · 每页 50 条";
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) UpdateCommands(); }
    }

    public void OnNavigatedTo(NavigationContext navigationContext)
    {
        _navigation?.Cancel();
        _navigation?.Dispose();
        _navigation = new();
        _ = RefreshAsync();
        _timer.Start();
    }
    public bool IsNavigationTarget(NavigationContext navigationContext) => true;
    public void OnNavigatedFrom(NavigationContext navigationContext)
    {
        ClearCreationRoutePreview("已离开任务中心。");
        _timer.Stop();
        _navigation?.Cancel();
        _detailRequest?.Cancel();
        _creationRequest?.Cancel();
        _historyRequest?.Cancel();
        _historyExportRequest?.Cancel();
    }

    private async Task RefreshAsync()
    {
        ExpireCreationRoutePreview();
        if (IsBusy || _navigation is null || _navigation.IsCancellationRequested) return;
        var token = _navigation.Token;
        var query = new TaskListQuery(SelectedFilter.Value, Search, _page, 50);
        IsBusy = true;
        try
        {
            await RecheckCreationRouteMapContextAsync(token);
            var result = await _client.GetTasksAsync(query, token);
            token.ThrowIfCancellationRequested();
            // Do not apply a response from the previous filter/search/page.
            if (query.Filter != SelectedFilter.Value || query.Search != Search || query.Page != _page) return;
            _totalCount = result.TotalCount;
            if (_page > PageCount) { _page = PageCount; return; }
            var selectedId = SelectedTask?.TaskId;
            _updatingRows = true;
            try
            {
                Tasks.Clear();
                foreach (var row in result.Items) Tasks.Add(row);
                SelectedTask = Tasks.FirstOrDefault(t => t.TaskId == selectedId) ?? Tasks.FirstOrDefault();
            }
            finally { _updatingRows = false; }
            if (SelectedTask?.TaskId != Detail?.Task.TaskId) ClearDetail();
            RaisePropertyChanged(nameof(PageText));
            StatusMessage = result.TotalCount == 0 ? "没有符合条件的任务。" : "后台已连接 · 每3秒刷新";
            await LoadDetailAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            StatusMessage = $"任务加载失败（已有数据可能已过期）：{exception.Message}";
        }
        finally { IsBusy = false; }
    }

    private async Task LoadDetailAsync()
    {
        _detailRequest?.Cancel();
        _detailRequest?.Dispose();
        _detailRequest = null;
        if (SelectedTask is null || _navigation is null || _navigation.IsCancellationRequested) return;
        _detailRequest = CancellationTokenSource.CreateLinkedTokenSource(_navigation.Token);
        var token = _detailRequest.Token;
        var id = SelectedTask.TaskId;
        try
        {
            var detail = await _client.GetTaskDetailAsync(id, token);
            token.ThrowIfCancellationRequested();
            if (SelectedTask?.TaskId != id) return;
            Detail = detail;
            ReconcileCancellationReceipt(detail);
            Replace(Steps, detail?.Steps ?? []);
            Replace(Commands, detail?.Commands ?? []);
            Replace(Resources, detail?.Resources ?? []);
            Replace(PlcConditions, detail?.PlcConditions ?? []);
            if (detail is null) StatusMessage = "所选任务已不存在，请刷新列表。";
            else await LoadHistoryAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (SelectedTask?.TaskId == id) StatusMessage = $"任务详情加载失败（详情可能已过期）：{exception.Message}";
        }
    }
    private void ClearDetail()
    {
        _detailRequest?.Cancel();
        Detail = null;
        Steps.Clear(); Commands.Clear(); Resources.Clear(); PlcConditions.Clear();
        ResetHistoryForTask();
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear(); foreach (var item in values) target.Add(item);
    }
    private void UpdateCommands()
    {
        UpdateTaskOperationCommands();
        RefreshCommand.RaiseCanExecuteChanged(); SearchCommand.RaiseCanExecuteChanged();
        PreviousPageCommand.RaiseCanExecuteChanged(); NextPageCommand.RaiseCanExecuteChanged();
    }
}
