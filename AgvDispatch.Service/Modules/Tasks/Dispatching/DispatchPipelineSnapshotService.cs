using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

/// <summary>Read-only live observations also work while dispatch is disabled. They never create a call, lease or command.</summary>
public sealed class DispatchPipelineSnapshotService(DispatchOptimizationService optimization, IDispatchOrchestrationRepository tasks,
    IDispatchTaskRepository taskReader, ITrafficResourceManager traffic, IAgvCommandOutboxRepository outbox,
    DispatchOrchestratorOptions dispatchOptions, DispatchTaskCreationOptions creationOptions,
    AgvCommandOutboxOptions outboxOptions, IMapExecutionGate gate,
    ITrafficResourceControlService? trafficControl = null) : IDispatchOptimizationService
{
    public async Task<DispatchOptimizationSnapshot> GetAsync(CancellationToken ct)
    {
        var snapshot = await optimization.GetAsync(ct);
        var waiting = await tasks.GetDispatchCandidatesAsync(int.MaxValue, ct);
        var pendingAck = await taskReader.GetPendingAcknowledgementsAsync(ct);
        var heldTasks = await traffic.GetHeldTaskIdsAsync(ct);
        var due = await outbox.GetDueCommandsAsync(AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(), int.MaxValue, ct);
        var layers = snapshot.Layers.ToDictionary(l => l.Key);
        void Observe(string key, string state, string summary, int? count = null) => layers[key] = layers[key] with
        { State = state, Summary = summary, ItemCount = count, UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow() };
        Observe("queue", creationOptions.Enabled ? "Ready" : "Disabled",
            $"持久化队列：待 PLC 应答 {pendingAck.Count} 单，待派车 {waiting.Count} 单；" +
            (creationOptions.Enabled && creationOptions.AutomaticCreationEnabled ? "自动接单按工位活动任务去重。" : "PLC 自动接单未启用，原任务继续保留。"), waiting.Count + pendingAck.Count);
        if (!dispatchOptions.Enabled)
        {
            Observe("candidates", "Disabled", "业务派车未启用，尚未进行本轮车辆筛选。");
            Observe("matching", "Disabled", "业务派车未启用，尚未进行本轮全局匹配。");
        }
        else if (dispatchOptions.RequireGlobalMatching && !snapshot.Options.Mode.Equals("Global", StringComparison.OrdinalIgnoreCase))
            Observe("matching", "Blocked", "正式业务要求 Global；当前模式不符，保留原队列并暂停新增派车。");
        else if (snapshot.Options.Mode == "Disabled") Observe("matching", "Disabled", "全局匹配已关闭；传统派车仍须经过路权和安全复核。");
        if (layers["traffic"].UpdatedAt < AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow().AddSeconds(-15))
        {
            // A dispatch cycle can stop while a durable resource request remains waiting.
            // Read the complete count (one row is enough for the payload), never infer
            // the absence of waiting work from the number of already granted leases.
            var waitingCount = trafficControl is null ? (int?)null :
                (await trafficControl.GetWaitingAsync(new(PageSize: 1), ct)).TotalWaiting;
            Observe("traffic", waitingCount is > 0 ? "Waiting" : heldTasks.Count == 0 ? "Idle" : "Running",
                (waitingCount is { } count ? $"持久化路权等待 {count} 单；" : "") +
                $"当前 {heldTasks.Count} 个任务持有实际路权；释放仍按原交接及恢复规则处理。",
                waitingCount is > 0 ? waitingCount : heldTasks.Count);
        }
        if (gate.Mode is MapExecutionMode.Starting or MapExecutionMode.Held)
            Observe("safety", "Blocked", $"地图执行状态为 {gate.Mode}，恢复/地图屏障尚未放行。");
        else if (!dispatchOptions.Enabled && !outboxOptions.Enabled) Observe("safety", "Disabled", "业务派车和命令发送未启用；保存优化策略不授予车辆或 PLC 动作许可。");
        if (!outboxOptions.Enabled) Observe("execution", "Disabled", $"Outbox 发送未启用；保留 {due.Count} 条到期原命令，保持原任务、步骤和幂等编号。", due.Count);
        else if (layers["execution"].UpdatedAt < AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow().AddSeconds(-15))
            Observe("execution", due.Count == 0 ? "Idle" : "Waiting", $"当前 {due.Count} 条原命令到期；发送前须通过安全层，重试沿用原步骤编号。", due.Count);
        return snapshot with { WaitingTaskCount = waiting.Count, Layers = layers.Values.OrderBy(l => l.Number).ToArray() };
    }
    public Task<DispatchOptimizationSnapshot> SaveOptionsAsync(TrafficUserContext user, SaveDispatchOptimizationOptionsRequest request, CancellationToken ct) =>
        optimization.SaveOptionsAsync(user, request, ct);
}
