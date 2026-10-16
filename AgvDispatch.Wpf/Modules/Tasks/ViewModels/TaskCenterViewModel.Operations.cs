using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using Prism.Commands;
using Prism.Regions;
namespace AgvDispatch.Wpf.ViewModels;
public sealed partial class TaskCenterViewModel
{
    private IRegionManager? _regions;
    private bool _taskOperationBusy;
    private string _taskOperationReason = "", _taskOperationMessage = "取消将冻结后续步骤；原车辆任务仍须确认结束，路权和PLC握手另行现场核对。";
    private TaskCancelRequest? _pendingCancel;
    public DelegateCommand CancelDispatchTaskCommand { get; private set; } = null!;
    public DelegateCommand BeginTaskRecoveryCommand { get; private set; } = null!;
    public string TaskOperationReason { get => _taskOperationReason; set { if (SetProperty(ref _taskOperationReason, value)) UpdateTaskOperationCommands(); } }
    public string TaskOperationMessage { get => _taskOperationMessage; private set => SetProperty(ref _taskOperationMessage, value); }
    private bool CanOperateTask() => !_taskOperationBusy && !IsBusy && Detail != null && _navigation?.IsCancellationRequested == false && TaskOperationReason.Trim().Length is >= 10 and <= 1000;
    private void InitializeTaskOperations(IRegionManager? regionManager)
    {
        _regions = regionManager;
        CancelDispatchTaskCommand = new(async () => await CancelDispatchTaskAsync(), () => CanOperateTask() && Detail!.Task.Stage is not
            (DispatchTaskStage.Completed or DispatchTaskStage.Cancelled or DispatchTaskStage.CancellationPending or DispatchTaskStage.CancellationReview));
        BeginTaskRecoveryCommand = new(async () => await BeginTaskRecoveryAsync(), () => CanOperateTask() && Detail!.Task.Stage is not
            (DispatchTaskStage.Completed or DispatchTaskStage.Cancelled or DispatchTaskStage.CancellationPending));
    }
    private void UpdateTaskOperationCommands() { CancelDispatchTaskCommand?.RaiseCanExecuteChanged(); BeginTaskRecoveryCommand?.RaiseCanExecuteChanged(); }
    public async Task CancelDispatchTaskAsync()
    {
        if (!CancelDispatchTaskCommand.CanExecute()) return;
        var task = Detail!.Task;
        var candidate = new TaskCancelRequest("", task.TaskId, task.Stage, TaskOperationReason.Trim());
        if (_pendingCancel == null || _pendingCancel with { RequestId = "" } != candidate) _pendingCancel = candidate with { RequestId = Guid.NewGuid().ToString("N") };
        _taskOperationBusy = true; UpdateTaskOperationCommands();
        try
        {
            var result = await _client.CancelDispatchTaskAsync(_pendingCancel, _navigation!.Token);
            TaskOperationMessage = result.Message;
            if (result.Accepted) _pendingCancel = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { TaskOperationMessage = "取消结果尚未确认；请刷新原任务核对，保持原参数重试：" + error.Message; }
        finally { _taskOperationBusy = false; UpdateTaskOperationCommands(); }
        await RefreshAsync();
    }
    private void ReconcileCancellationReceipt(TaskCenterDetail? detail)
    {
        if (_pendingCancel != null && detail?.Task.TaskId == _pendingCancel.TaskId &&
            detail.Cancellation?.RequestId == _pendingCancel.RequestId && detail.Cancellation.Reason == _pendingCancel.Reason)
        {
            _pendingCancel = null;
            TaskOperationMessage = "已从后台核对原取消请求：" + detail.WaitingExplanation;
        }
    }
    public async Task BeginTaskRecoveryAsync()
    {
        if (!BeginTaskRecoveryCommand.CanExecute()) return;
        var id = Detail!.Task.TaskId;
        _taskOperationBusy = true; UpdateTaskOperationCommands();
        try
        {
            var result = await _client.BeginTaskRecoveryReviewAsync(new(id, TaskOperationReason.Trim()), _navigation!.Token);
            TaskOperationMessage = result.Message;
            if (result.Accepted) _regions?.RequestNavigate(NavigationCatalog.ContentRegion, "ExecutionRecoveryView", new NavigationParameters { { "taskId", id } });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { TaskOperationMessage = "恢复核对请求待确认；请进入现场恢复页面核对屏障状态：" + error.Message; }
        finally { _taskOperationBusy = false; UpdateTaskOperationCommands(); }
    }
}
