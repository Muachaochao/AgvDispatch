using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed class TaskSwitchQueries(ITaskSystemQueryPort tasks, IManualTaskCreationService creation,
    ITaskSwitchService switches) : ITaskSwitchQueries
{
    public async Task<TaskSwitchOptions> GetOptionsAsync(string taskId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskId) || taskId.Length > 64)
            throw new ArgumentException("原任务编号无效。");
        var task = await tasks.GetTaskAsync(taskId, ct);
        if (task is null) throw new KeyNotFoundException("原任务不存在。");
        var creationOptions = await creation.GetOptionsAsync(ct);
        var existing = (await switches.ListAsync(ct))
            .Where(row => row.SourceTaskId.Equals(taskId, StringComparison.Ordinal))
            .OrderByDescending(row => row.UpdatedAt)
            .ToArray();
        var current = existing.FirstOrDefault(row => !TaskSwitchStages.IsTerminal(row.Stage)) ?? existing.FirstOrDefault();
        var vehicleId = task.VehicleId ?? current?.VehicleId;
        var blockers = current is { } active && !TaskSwitchStages.IsTerminal(active.Stage) ? active.Blockers : [];
        var eligible = current is null && task.Stage == DispatchTaskStage.GoingToPickup && !string.IsNullOrWhiteSpace(vehicleId);
        var message = current is { } activeRecord ? activeRecord.Message :
            task.Stage != DispatchTaskStage.GoingToPickup ? "第一阶段仅支持空车前往取箱首段导航执行中换单。" :
            string.IsNullOrWhiteSpace(vehicleId) ? "原任务尚未分配车辆，无需换单，可直接在调度队列处理。" :
            "可申请受控停止；后台会重新核对空载、顶升、车辆终态、PLC周期和交通占用。";
        return new(true, eligible, message, vehicleId, creationOptions.Stations, blockers, current);
    }
}

// Small read-only seam keeps the UI query independent of the heavier task-detail projection.
public interface ITaskSystemQueryPort
{
    Task<ActiveDispatchTask?> GetTaskAsync(string taskId, CancellationToken ct);
}

public sealed class TaskSystemQueryPort(ITaskCenterQueryService tasks) : ITaskSystemQueryPort
{
    public async Task<ActiveDispatchTask?> GetTaskAsync(string taskId, CancellationToken ct)
    {
        var detail = await tasks.GetDetailAsync(taskId, ct);
        return detail is null ? null : new ActiveDispatchTask(detail.Task.TaskId, detail.Task.StationId,
            detail.Task.VehicleId, null, detail.Task.Stage, detail.Task.CreatedAt, detail.Task.UpdatedAt);
    }
}
