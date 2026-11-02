using System.Diagnostics;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed partial class DispatchTaskOrchestrator
{
    private DateTimeOffset _nextOptimizationAt;
    private readonly DispatchAssignmentOptimizer _optimizer = new();
    private void ReportOptimizationGate(string message)
    {
        if (optimization is null || DateTimeOffset.UtcNow < _nextOptimizationAt) return;
        _nextOptimizationAt = DateTimeOffset.UtcNow.AddMilliseconds(optimization.Options.IntervalMs);
        optimization.Publish(new() { Message = message });
    }

    private string? VehicleExclusion(AgvVehicleStatus vehicle, IReadOnlySet<string> occupied, DispatchAssignmentOptions policy)
    {
        if (occupied.Contains(vehicle.VehicleId)) return "已有派发任务，保持原车辆责任。";
        if (vehicle.Connection != ConnectionState.Online) return "车辆离线。";
        if (!options.IdleOperatingStates.Contains(vehicle.OperatingState, StringComparer.OrdinalIgnoreCase)) return "车辆未空闲。";
        if (!agvStates.TryGet(vehicle.VehicleId, out var robot) || robot.Connection != ConnectionState.Online || robot.Quality != SignalQuality.Good)
            return "车辆遥测质量无效。";
        if (robot.EmergencyStopped != false || robot.Blocked != false || AgvObstaclePolicy.IsBlocked(robot) || AgvObstaclePolicy.HasBlockingAlarm(robot))
            return "车辆急停、阻挡、报警或状态未知。";
        if (robot.Tasks.Any(t => t.Status is 1 or 2 or 3)) return "车端已有未结束任务。";
        if (!IsPositionSafeForNewTask(vehicle.VehicleId)) return "定位无效、过期或偏离地图。";
        if (string.IsNullOrWhiteSpace(vehicle.CurrentStation)) return "车辆当前站点未知。";
        if (robot.BatteryPercent is null || robot.BatteryPercent < (decimal)policy.MinimumBatteryPercent || vehicle.BatteryPercent < (decimal)policy.MinimumBatteryPercent)
            return "电量未知或低于业务最低电量。";
        if (charging?.AllowsNewBusiness(vehicle.VehicleId) == false) return "充电责任或电量门禁尚未放行。";
        if (businessRun?.Returning == true && vehicle.VehicleId == businessRun.Vehicle) return "正在执行回待机位收尾。";
        return null;
    }

    private RoutePlan? CachedRoute(string source, string destination, IReadOnlySet<string> unavailable) =>
        routeCache is null ? routePlanner.Plan(source, destination, unavailable) : routeCache.Plan(source, destination, unavailable);

    private static (double Travel, double TrafficWait) EstimateRoute(RoutePlan route, ActiveMapRuntimeSnapshot? map, DispatchAssignmentOptions policy)
    {
        // Graph.Cost already uses distance / edge speed plus live congestion penalties.
        // Split these units instead of dividing seconds by speed a second time.
        if (map is null || route.EdgeIds.Count == 0) return (route.Cost, 0);
        var edges = route.EdgeIds.Select(id => map.Definition.Edges.FirstOrDefault(e => e.Id == id)).ToArray();
        if (edges.Any(e => e is null)) return (route.Cost, 0);
        var graphTravel = edges.Sum(e => e!.Distance / (e.SpeedLimit > 0 ? e.SpeedLimit : 1));
        var travel = edges.Sum(e => e!.Distance / (e.SpeedLimit > 0 ? Math.Min(e.SpeedLimit, policy.NominalSpeedMetersPerSecond) : policy.NominalSpeedMetersPerSecond));
        return (travel, Math.Max(0, route.Cost - graphTravel));
    }

    private async Task RunOptimizationAsync(DispatchAssignmentOptions policy, int slots,
        IReadOnlyList<DispatchTaskWorkItem> activeTasks, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;
        var hits = routeCache?.Hits ?? 0; var misses = routeCache?.Misses ?? 0;
        var map = activeMap.Current;
        var waiting = await tasks.GetDispatchCandidatesAsync(int.MaxValue, ct);
        var held = await trafficResources.GetHeldTaskIdsAsync(ct);
        var occupied = activeTasks.Select(t => t.VehicleId).Where(id => id is not null).Select(id => id!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        occupied.UnionWith(await trafficResources.GetHeldVehicleIdsAsync(ct));
        var work = waiting.Where(t => t.VehicleId is null && !held.Contains(t.TaskId)).ToArray();
        var input = work.Select(t => new DispatchAssignmentTask(t.TaskId, t.StationId, t.Priority, t.CreatedAt)
        { Deadline = policy.StationDeadlineMinutes.FirstOrDefault(p => p.Key.Equals(t.StationId, StringComparison.OrdinalIgnoreCase)).Value is > 0 and var minutes ? t.CreatedAt.AddMinutes(minutes) : null }).ToArray();
        var window = _optimizer.SelectWindow(input, policy, now);
        var ready = (await tasks.GetReadyStepsAsync(int.MaxValue, ct))
            .Where(s => s.StepType == DispatchTaskStepType.GoToPickup).GroupBy(s => s.TaskId)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var vehicles = (await agvGateway.GetVehiclesAsync(ct)).OrderBy(v => v.VehicleId, StringComparer.OrdinalIgnoreCase).ToArray();
        var generalReasons = vehicles.ToDictionary(v => v.VehicleId, v => VehicleExclusion(v, occupied, policy), StringComparer.OrdinalIgnoreCase);
        var edges = new List<DispatchAssignmentCandidate>();
        var plans = new Dictionary<(string, string), RoutePlan>();
        var diagnostics = new Dictionary<string, List<DispatchOptimizationCandidate>>(StringComparer.OrdinalIgnoreCase);
        var taskReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var score in window)
        {
            ct.ThrowIfCancellationRequested();
            var task = work.Single(t => t.TaskId == score.TaskId);
            var rows = diagnostics[task.TaskId] = [];
            var pickup = options.GetPickupSite(task.StationId);
            if (pickup is null || !ready.TryGetValue(task.TaskId, out var step))
            { taskReasons[task.TaskId] = pickup is null ? "未配置取货站点。" : "首步骤尚未就绪。"; continue; }
            var unavailable = await trafficResources.GetUnavailableResourceIdsAsync(task.TaskId, ct);
            var returnRoute = policy.ReturnCostWeight == 0 ? null : options.StandbySites.Select(site => CachedRoute(pickup, site, new HashSet<string>()))
                .Where(r => r is not null).OrderBy(r => r!.Cost).FirstOrDefault();
            var returnEstimate = returnRoute is null ? (Travel: 0d, TrafficWait: 0d) : EstimateRoute(returnRoute, map, policy);
            var returnSeconds = (returnEstimate.Travel + returnEstimate.TrafficWait) * policy.ReturnCostWeight;
            var targetZone = policy.SiteZones.FirstOrDefault(p => p.Key.Equals(pickup, StringComparison.OrdinalIgnoreCase)).Value;
            (AgvVehicleStatus Vehicle, RoutePlan Route)? blocked = null;
            foreach (var vehicle in vehicles)
            {
                var reason = generalReasons[vehicle.VehicleId];
                reason ??= !options.AllowsVehicle(task.StationId, vehicle.VehicleId) ? "工位车辆池不允许该车辆。" :
                    !TaskVehicleSelection.Accepts(task, vehicle.VehicleId) ? "任务限定其他车辆。" :
                    fleetTest?.Allows(task.TaskId, vehicle.VehicleId) == false ? "联调车辆责任不允许该任务。" : null;
                if (reason is null && taskSwitchGuard is not null && !await taskSwitchGuard.AllowsVehicleAsync(vehicle.VehicleId, task.TaskId, null, ct))
                    reason = "受控换单尚未交接。";
                if (reason is not null) { rows.Add(new(vehicle.VehicleId, 0, 0, reason, false)); continue; }
                var route = CachedRoute(vehicle.CurrentStation!, pickup, unavailable);
                if (route is not { Nodes.Count: >= 2 })
                {
                    var nominal = unavailable.Count == 0 ? null : CachedRoute(vehicle.CurrentStation!, pickup, new HashSet<string>());
                    if (nominal is { Nodes.Count: >= 2 } && (blocked is null || nominal.Cost < blocked.Value.Route.Cost)) blocked = (vehicle, nominal);
                    rows.Add(new(vehicle.VehicleId, 0, 0, nominal is null ? "无合法可达路线（含封路、同点取货闭合路径检查）。" : "必要公共资源容量已占用，等待释放。", false));
                    continue;
                }
                var battery = Math.Min(vehicle.BatteryPercent, agvStates.TryGet(vehicle.VehicleId, out var state) ? state.BatteryPercent ?? 0 : 0);
                var standby = options.StandbySites.Contains(vehicle.CurrentStation!, StringComparer.OrdinalIgnoreCase);
                var sourceZone = policy.SiteZones.FirstOrDefault(p => p.Key.Equals(vehicle.CurrentStation, StringComparison.OrdinalIgnoreCase)).Value;
                var estimate = EstimateRoute(route, map, policy);
                var candidate = new DispatchAssignmentCandidate(task.TaskId, vehicle.VehicleId, estimate.Travel)
                {
                    TrafficWaitSeconds = estimate.TrafficWait,
                    ReturnSeconds = returnSeconds,
                    CrossZoneSeconds = sourceZone is not null && targetZone is not null && !sourceZone.Equals(targetZone, StringComparison.OrdinalIgnoreCase) ? policy.CrossZoneSeconds : 0,
                    BatteryRiskSeconds = policy.BatteryRiskSeconds * (1 - (double)battery / 100),
                    StandbyBonusSeconds = standby ? policy.StandbyBonusSeconds : 0
                };
                edges.Add(candidate); plans[(task.TaskId, vehicle.VehicleId)] = route;
                rows.Add(new(vehicle.VehicleId, candidate.TravelSeconds, candidate.TotalCostSeconds,
                    "已通过在线、定位、健康、电量、车辆池和路径筛选；发车前仍须实时核验路权。", true)
                { TrafficWaitSeconds = candidate.TrafficWaitSeconds, ReturnSeconds = candidate.ReturnSeconds, CrossZoneSeconds = candidate.CrossZoneSeconds,
                    BatteryRiskSeconds = candidate.BatteryRiskSeconds, StandbyBonusSeconds = candidate.StandbyBonusSeconds });
            }
            // Keep the persisted resource queue alive even when the current capacity prevents a candidate edge.
            if (policy.Mode.Equals("Global", StringComparison.OrdinalIgnoreCase) && !edges.Any(e => e.TaskId == task.TaskId) && blocked is { } b)
                await trafficResources.RecordRouteAsync(new(step.StepId, task.TaskId, b.Vehicle.VehicleId, b.Vehicle.CurrentStation!, pickup,
                    b.Route.EdgeIds, b.Route.ResourceIds, true, "滚动窗口：必要公共资源尚未可用，保留呼叫顺序和动态优先级。")
                    { MapId = map?.MapId, MapVersionId = map?.VersionId, QueuePriority = score.EffectivePriority }, ct);
        }
        var result = _optimizer.Optimize(input, edges, policy, now, slots);
        // Same hard-feasible graph, original persisted order and nearest vehicle: a comparable greedy baseline.
        var greedy = new Dictionary<string, DispatchAssignmentCandidate>();
        var greedyVehicles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in waiting.Where(t => window.Any(w => w.TaskId == t.TaskId)))
        {
            if (greedy.Count >= slots) break;
            var candidate = edges.Where(e => e.TaskId == task.TaskId && !greedyVehicles.Contains(e.VehicleId))
                .OrderBy(e => plans[(e.TaskId, e.VehicleId)].Cost)
                .ThenByDescending(e => vehicles.Single(v => v.VehicleId == e.VehicleId).BatteryPercent)
                .ThenBy(e => e.VehicleId, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (candidate is not null) { greedy[task.TaskId] = candidate; greedyVehicles.Add(candidate.VehicleId); }
        }
        var prepared = new Dictionary<string, string>();
        var global = policy.Mode.Equals("Global", StringComparison.OrdinalIgnoreCase);
        if (global && slots > 0)
        {
            var failedPairs = new HashSet<(string, string)>();
            var remaining = edges.ToList();
            // Commit one recommendation then rematch: a failed reservation never steals another task's sole vehicle.
            while (prepared.Count < slots && remaining.Count > 0)
            {
                var match = _optimizer.Optimize(input.Where(t => !prepared.ContainsKey(t.TaskId)).ToArray(), remaining, policy, now, slots - prepared.Count);
                var next = match.Assignments.FirstOrDefault();
                if (next is null) break;
                var task = work.Single(t => t.TaskId == next.TaskId);
                var vehicle = vehicles.Single(v => v.VehicleId == next.VehicleId);
                string? outcome;
                try { outcome = await TryPrepareOptimizedAsync(task, ready[task.TaskId], vehicle, plans[(task.TaskId, vehicle.VehicleId)],
                    next.EffectivePriority, map, policy, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Preparation may already have succeeded before a later bookkeeping exception.
                    // Never reuse that vehicle within this cycle; persisted ownership resolves the next one.
                    logger.LogWarning(ex, "全局匹配候选准备失败，保留原任务。TaskId={TaskId}", task.TaskId);
                    outcome = "派车准备异常，核对持久化原任务：" + ex.Message;
                    // The persisted command may already consume a concurrency slot.
                    // Stop this cycle and reload all owners on the next one.
                    remaining.Clear();
                }
                taskReasons[task.TaskId] = outcome ?? "已取得路权并原子登记首段命令，保持原任务与车辆责任。";
                if (outcome is null)
                {
                    prepared[task.TaskId] = vehicle.VehicleId;
                    remaining.RemoveAll(e => e.TaskId == task.TaskId || e.VehicleId == vehicle.VehicleId);
                }
                else
                {
                    failedPairs.Add((task.TaskId, vehicle.VehicleId));
                    remaining.RemoveAll(e => failedPairs.Contains((e.TaskId, e.VehicleId)));
                }
            }
        }
        var rowsOut = window.Select(score => new DispatchOptimizationTask(score.TaskId, score.StationId, score.CalledAt,
            score.BasePriority, score.EffectivePriority, score.WaitingSeconds / 60,
            result.Assignments.FirstOrDefault(a => a.TaskId == score.TaskId)?.VehicleId,
            greedy.GetValueOrDefault(score.TaskId)?.VehicleId, prepared.GetValueOrDefault(score.TaskId),
            taskReasons.GetValueOrDefault(score.TaskId) ?? result.Unassigned.FirstOrDefault(u => u.TaskId == score.TaskId)?.Reason ??
                (global ? "本轮未登记，等待下一周期。" : "影子推荐：实际派车沿用现有编排流程。"), diagnostics[score.TaskId])).ToArray();
        var greedyPriority = window.Where(w => greedy.ContainsKey(w.TaskId)).Sum(w => w.EffectivePriority);
        var improved = result.Assignments.Count > greedy.Count || result.Assignments.Count == greedy.Count &&
            (result.Assignments.Sum(a => a.EffectivePriority) > greedyPriority ||
             result.Assignments.Sum(a => a.EffectivePriority) == greedyPriority && result.TotalTravelSeconds < greedy.Values.Sum(e => e.TravelSeconds) - .001);
        optimization!.Publish(new()
        {
            CapturedAt = now, DurationMs = watch.Elapsed.TotalMilliseconds, WaitingTaskCount = waiting.Count,
            WindowTaskCount = window.Count, EligibleVehicleCount = generalReasons.Count(p => p.Value is null),
            CandidateCount = edges.Count, RetainedCandidateCount = result.RetainedCandidateCount,
            RouteCacheHits = (routeCache?.Hits ?? 0) - hits, RouteCacheMisses = (routeCache?.Misses ?? 0) - misses, Tasks = rowsOut,
            Message = $"{(global ? "全局派车" : "影子比对")}：推荐 {result.Assignments.Count} 单，贪心 {greedy.Count} 单，登记 {prepared.Count} 单；" +
                (slots == 0 ? "并发槽位已满，既有任务保持不动。" : "等待任务继续保存在原队列。") +
                (waiting.Count != work.Length ? $" {waiting.Count - work.Length} 个已有车辆或路权的任务保持固定。" : "")
        }, !global, !global && improved);
    }

    private async Task<string?> TryPrepareOptimizedAsync(DispatchTaskWorkItem task, DispatchTaskStepWorkItem step,
        AgvVehicleStatus vehicle, RoutePlan planned, double priority, ActiveMapRuntimeSnapshot? map,
        DispatchAssignmentOptions policy, CancellationToken ct)
    {
        if (!executionGate.CanStartNewWork || !ReferenceEquals(map, activeMap.Current)) return "运行地图或新增派车门禁已变化。";
        if (robokitMap is not null)
        {
            if (map is null) return "运行地图不存在。";
            foreach (var waiting in options.GetDumperWaitingSites())
            {
                var check = await robokitMap.CheckAsync(vehicle.VehicleId, vehicle.CurrentStation!, planned.Nodes[^1], waiting,
                    options.DumperDumpingSite, map.MapId, map.VersionId, ct);
                if (!check.Accepted) return "派车前车端地图核验未通过：" + check.Message;
            }
        }
        var current = (await agvGateway.GetVehiclesAsync(ct)).SingleOrDefault(v => v.VehicleId == vehicle.VehicleId);
        var busy = (await tasks.GetSubmittingTasksAsync(ct)).Concat(await tasks.GetAssignedTasksAsync(ct))
            .Where(t => t.VehicleId is not null).Select(t => t.VehicleId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        busy.UnionWith(await trafficResources.GetHeldVehicleIdsAsync(ct));
        var exclusion = current is null ? "车辆状态已丢失。" : VehicleExclusion(current, busy, policy);
        if (exclusion is not null) return exclusion;
        if (current!.CurrentStation != vehicle.CurrentStation) return "车辆起点已变化，留待下一周期重新规划。";
        if (fleetTest?.Allows(task.TaskId, vehicle.VehicleId) == false || charging?.AllowsNewBusiness(vehicle.VehicleId) == false ||
            taskSwitchGuard is not null && !await taskSwitchGuard.AllowsVehicleAsync(vehicle.VehicleId, task.TaskId, null, ct)) return "车辆责任门禁已变化。";
        var unavailable = await trafficResources.GetUnavailableResourceIdsAsync(task.TaskId, ct);
        var currentTask = (await tasks.GetDispatchCandidatesAsync(int.MaxValue, ct)).SingleOrDefault(t => t.TaskId == task.TaskId);
        var currentStep = (await tasks.GetReadyStepsAsync(int.MaxValue, ct)).SingleOrDefault(s => s.StepId == step.StepId);
        if (currentTask is null || currentTask.VehicleId is not null || currentStep is null ||
            (await trafficResources.GetHeldTaskIdsAsync(ct)).Contains(task.TaskId)) return "任务、首步骤或既有路权责任已变化，保留持久化原记录。";
        // Bypass cache at the final check. A recommendation is never proof of a current executable route.
        var route = routePlanner.Plan(current.CurrentStation!, planned.Nodes[^1], unavailable);
        if (route is null || !route.Nodes.SequenceEqual(planned.Nodes) || !route.EdgeIds.SequenceEqual(planned.EdgeIds) ||
            !route.ResourceIds.SequenceEqual(planned.ResourceIds) || !ReferenceEquals(map, activeMap.Current)) return "道路、地图或公共资源已变化，重新进入匹配。";
        await trafficResources.RecordRouteAsync(new(step.StepId, task.TaskId, vehicle.VehicleId, current.CurrentStation!, route.Nodes[^1],
            route.EdgeIds, route.ResourceIds, false, "滚动全局匹配完成，按有效优先级申请路权。")
            { MapId = map?.MapId, MapVersionId = map?.VersionId, QueuePriority = priority }, ct);
        var reservation = await trafficResources.TryAcquireAsync(step.StepId, task.TaskId, vehicle.VehicleId, route.ResourceIds, ct);
        if (!reservation.Acquired) return "等待公共资源 " + reservation.BlockingResourceId + "；已取得的有效路权继续保留。";
        var commandPrepared = false;
        try
        {
            if (!executionGate.CanStartNewWork || !ReferenceEquals(map, activeMap.Current) ||
                !agvStates.TryGet(vehicle.VehicleId, out var fresh) || fresh.CurrentStation != current.CurrentStation ||
                VehicleExclusion(current, busy, policy) is not null) return "路权取得后实时状态变化，未登记命令。";
            if (handshake is not null) await handshake.BindBeforeTransportAsync(step with { VehicleId = vehicle.VehicleId }, ct);
            commandPrepared = await outbox.TryPrepareTransportStepAsync(task.TaskId, step.StepId, vehicle.VehicleId, route.Nodes,
                options.GetAction(DispatchTaskStepType.GoToPickup), ct);
            if (!commandPrepared) return "任务或首步骤已变化，首段命令未重复登记。";
            if (handshakeOptions?.Enabled != true) await trafficResources.ReleaseTaskExceptStepAsync(task.TaskId, step.StepId, ct);
            logger.LogInformation("滚动全局匹配已登记首段命令。TaskId={TaskId}, VehicleId={VehicleId}, EffectivePriority={Priority}", task.TaskId, vehicle.VehicleId, priority);
            return null;
        }
        finally
        {
            if (!commandPrepared) await trafficResources.ReleaseStepAsync(step.StepId, CancellationToken.None);
        }
    }
}
