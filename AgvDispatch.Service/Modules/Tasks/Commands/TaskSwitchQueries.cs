using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed class TaskSwitchQueries(ITaskSystemQueryPort tasks, IManualTaskCreationService creation,
    ITaskSwitchService switches, TaskHandoffOptions? handoffOptions = null) : ITaskSwitchQueries
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
        var supported = task.Stage == DispatchTaskStage.GoingToPickup || TaskSwitchModes.IsBusinessCleanupStage(task.Stage);
        var eligible = current is null && supported && !string.IsNullOrWhiteSpace(vehicleId);
        var modeHint = !supported ? "" : task.Stage == DispatchTaskStage.GoingToPickup
            ? TaskSwitchModes.Auto
            : TaskSwitchModes.CompleteBusinessReturn;
        var message = current is { } activeRecord ? activeRecord.Message :
            !supported ? "当前任务阶段不支持自动换单；已完成、故障、取消中或尚未派车的任务保持原处置流程。" :
            string.IsNullOrWhiteSpace(vehicleId) ? "原任务尚未分配车辆，无需换单，可直接在调度队列处理。" :
            task.Stage == DispatchTaskStage.GoingToPickup
                ? "前往取箱途中由后台按车端载荷自动选择：空载下降时取消原导航，已载箱或顶升到位时继续完成原业务返箱。"
                : "原车已承担箱体责任：继续按九步执行倒料、返箱和PLC收尾，原任务完成后才创建新任务。";
        var modeDescription = modeHint switch
        {
            TaskSwitchModes.CompleteBusinessReturn => "完成原业务返箱后再切换；不会中途取消载箱任务。",
            "" => "当前阶段没有可执行的自动换单方式。",
            _ => "系统按实时载荷与顶升状态自动判定取消空车导航或完成返箱收尾。",
        };
        var readyHandoffs = handoffOptions?.ReadySites().Select(site => site.HandoffId).ToArray() ?? [];
        var handoffMessage = handoffOptions is not { Enabled: true }
            ? "指定交接位未启用；当前只允许完成原业务返箱后切换。"
            : readyHandoffs.Length == 0
                ? "指定交接位尚无可执行配置；需完成站点、PLC五信号、接收车辆、路线、标定和现场联调。"
                : $"已核定 {readyHandoffs.Length} 个交接位；载箱直接交接执行分支仍需启用对应车端与PLC动作契约。";
        return new(true, eligible, message, vehicleId, creationOptions.Stations, blockers, current)
        {
            ModeHint = modeHint,
            ModeDescription = modeDescription,
            HandoffReadySites = readyHandoffs,
            HandoffMessage = handoffMessage,
        };
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
