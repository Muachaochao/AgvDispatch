using System.Collections.Concurrent;
using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TaskHistoryExportService
{
    private readonly ConcurrentDictionary<string, ExportJob> _jobs = new(StringComparer.Ordinal);
    private string CacheRoot => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!, "exports", "task-history");
    public async Task<TaskHistoryExportResult> ExportAsync(TrafficUserContext user, TaskHistoryExportRequest request, CancellationToken ct)
    {
        DemandView(user); ct.ThrowIfCancellationRequested();
        var filter = request.Filter with { Page = 1, PageSize = 100 };
        TaskCenterReadRepository.ValidateHistoryQuery(filter);
        var id = request.ExportId == "" ? Guid.NewGuid().ToString("N") : ValidId(request.ExportId);
        PruneJobs();
        var proposed = new ExportJob(id, user.WindowsIdentity, filter, ct);
        var job = _jobs.GetOrAdd(id, proposed);
        if (job != proposed)
        {
            proposed.Cancel.Dispose();
            if (!job.Owner.Equals(user.WindowsIdentity, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("不能访问其他操作人的导出任务。");
            if (job.Filter != null && job.Filter != filter) throw new ArgumentException("导出编号不可用于不同筛选条件。");
            var previous = await job.Completion.Task.WaitAsync(ct);
            return previous.Accepted && !File.Exists(previous.FilePath) ? new(false, "导出缓存已清理，请重新导出。") : previous;
        }
        try
        {
            if (File.Exists(Path.Combine(CacheRoot, "任务处置历史-" + id + ".zip")))
                throw new InvalidOperationException("此导出编号已过期，请使用新的导出编号。");
            var result = await GenerateAsync(user, request with { Filter = filter, ExportId = id }, job, job.Cancel.Token);
            if (!result.Accepted) job.Update("Failed", result.Message, 0, job.Total, 0, true);
            job.Completion.TrySetResult(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            job.Update("Cancelled", "后台导出已取消，未完成文件已清理", job.Progress.CompletedRecords, job.Total, job.Progress.Percent, true);
            var result = new TaskHistoryExportResult(false, "后台导出已取消。"); job.Completion.TrySetResult(result);
            if (ct.IsCancellationRequested) throw;
            return result;
        }
        catch (Exception error)
        {
            job.Update("Failed", "导出失败：" + error.Message, job.Progress.CompletedRecords, job.Total, job.Progress.Percent, true);
            job.Completion.TrySetResult(new(false, job.Progress.Message)); throw;
        }
    }
    public Task<TaskHistoryExportProgress> GetProgressAsync(TrafficUserContext user, TaskHistoryExportJobRequest request, CancellationToken ct)
    {
        DemandView(user); ct.ThrowIfCancellationRequested(); var id = ValidId(request.ExportId);
        if (!_jobs.TryGetValue(id, out var job)) return Task.FromResult(new TaskHistoryExportProgress(id, "NotFound", "导出尚未登记或进度已过期"));
        if (!job.Owner.Equals(user.WindowsIdentity, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("不能访问其他操作人的导出任务。");
        lock (job.Sync) return Task.FromResult(job.Progress);
    }
    public Task<TaskHistoryExportCancelResult> CancelAsync(TrafficUserContext user, TaskHistoryExportJobRequest request, CancellationToken ct)
    {
        DemandView(user); ct.ThrowIfCancellationRequested(); var id = ValidId(request.ExportId); PruneJobs();
        // A tombstone makes cancellation safe even when it arrives before the export connection.
        var proposed = new ExportJob(id, user.WindowsIdentity, null, CancellationToken.None);
        proposed.Cancel.Cancel(); proposed.Update("Cancelled", "导出开始前已取消", 0, 0, 0, true);
        proposed.Completion.TrySetResult(new(false, "导出开始前已取消。"));
        var job = _jobs.GetOrAdd(id, proposed);
        if (job != proposed) proposed.Cancel.Dispose();
        if (!job.Owner.Equals(user.WindowsIdentity, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("不能取消其他操作人的导出任务。");
        lock (job.Sync)
        {
            if (job.Progress.Stage == "Cancelled") return Task.FromResult(new TaskHistoryExportCancelResult(true, "后台导出已取消。"));
            if (job.Progress.IsTerminal) return Task.FromResult(new TaskHistoryExportCancelResult(false, "后台已结束；可取消前端保存。"));
            job.Cancel.Cancel();
            job.Update("Cancelling", "已请求取消，正在停止并清理临时文件", job.Progress.CompletedRecords, job.Total, job.Progress.Percent);
            return Task.FromResult(new TaskHistoryExportCancelResult(true, "已请求后台取消；等待临时文件清理完成。"));
        }
    }
    private static void DemandView(TrafficUserContext user) { if (!user.Has(TrafficPermission.View)) throw new UnauthorizedAccessException("需要查看任务权限。"); }
    private static string ValidId(string id) => Guid.TryParseExact(id, "N", out var value) ? value.ToString("N") : throw new ArgumentException("导出编号须为32位GUID。");
    private void PruneJobs()
    {
        var terminal = _jobs.Values.Where(j => j.Progress.IsTerminal).OrderByDescending(j => j.StartedAt).ToArray();
        foreach (var job in terminal.Where((j, index) => index >= 256 || j.StartedAt < AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow().AddHours(-1)))
            if (_jobs.TryRemove(job.Id, out _)) job.Cancel.Dispose();
    }
    private sealed class ExportJob
    {
        public ExportJob(string id, string owner, TaskHistoryQuery? filter, CancellationToken ct)
        { Id = id; Owner = owner; Filter = filter; Cancel = CancellationTokenSource.CreateLinkedTokenSource(ct); Progress = new(id, "Preparing", "正在登记导出任务"); }
        public readonly object Sync = new();
        public string Id { get; } public string Owner { get; } public TaskHistoryQuery? Filter { get; }
        public DateTimeOffset StartedAt { get; } = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow();
        public CancellationTokenSource Cancel { get; }
        public TaskCompletionSource<TaskHistoryExportResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskHistoryExportProgress _progress = null!;
        public TaskHistoryExportProgress Progress { get { lock (Sync) return _progress; } private set => _progress = value; }
        public int Total => Progress.TotalRecords;
        public void Update(string stage, string message, int done, int total, int percent, bool terminal = false)
        { lock (Sync) Progress = new(Id, stage, message, done, total, percent, terminal); }
    }
}
