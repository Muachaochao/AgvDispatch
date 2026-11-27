using System.Diagnostics;
using System.Text.Json;
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
        if (optimization is null || _clock.GetUtcNow() < _nextOptimizationAt) return;
        _nextOptimizationAt = _clock.GetUtcNow().AddMilliseconds(optimization.Options.IntervalMs);
        optimization.ReportLayer("matching", options.Enabled ? "Blocked" : "Disabled", message);
        optimization.Publish(new() { Message = message });
    }

    private string? VehicleExclusion(AgvVehicleStatus vehicle, IReadOnlySet<string> occupied, DispatchAssignmentOptions policy) =>
        _candidateLayer.CheckVehicle(vehicle, occupied, policy);
    // Kept as a compatibility seam for the independent route-unit regression.
    private static (double Travel, double TrafficWait) EstimateRoute(RoutePlan route, ActiveMapRuntimeSnapshot? map, DispatchAssignmentOptions policy) =>
        DispatchCandidateFilter.EstimateRoute(route, map, policy);

    private async Task RunOptimizationAsync(DispatchAssignmentOptions policy, int slots,
        IReadOnlyList<DispatchTaskWorkItem> activeTasks, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var now = _clock.GetUtcNow();
        var hits = routeCache?.Hits ?? 0; var misses = routeCache?.Misses ?? 0;
        var global = policy.Mode.Equals("Global", StringComparison.OrdinalIgnoreCase);
        ActiveMapRuntimeSnapshot? map;
        DispatchQueueWindow queue;
        DispatchCandidateGraph graph;
        {
            // Shadow is invoked by the legacy cycle with its original lease held.
            using var chargingLease = global && charging is not null ? await charging.EnterBusinessAsync(ct) : null;
            // Planning reads the map and vehicle status but does not claim work. A
            // shared execution lease keeps the map stable without blocking other
            // vehicles behind candidate/status queries.
            using var planning = global ? await executionGate.EnterAsync(MapExecutionOperation.ExistingWork, ct) : null;
            if (global && (planning is null || !ReferenceEquals(policy, optimization?.Options))) return;
            map = activeMap.Current;
            if (global && options.EngineeringMapError(map) is not null) return;
            optimization?.ReportLayer("queue", "Running", "读取持久化呼叫，固定既有任务和路权，按优先级与呼叫时间选择窗口。");
            queue = await _queueLayer.LoadAsync(policy, activeTasks, now, ct);
            optimization?.ReportLayer("queue", "Ready", $"持久化等待 {queue.Waiting.Length} 单，本轮窗口 {queue.Window.Count} 单；窗口外及固定任务继续保留。", queue.Waiting.Length);
            optimization?.ReportLayer("candidates", "Running", "筛选健康、能力车辆池、电量、区域和合法路线，不申请路权、不登记命令。");
            graph = await _candidateLayer.BuildAsync(queue, policy, map, ct);
            if (global) await _trafficLayer.ObserveWaitingAsync(graph.WaitingRoutes, ct);
        }
        var planningMs = watch.Elapsed.TotalMilliseconds;
        IReadOnlyList<DispatchTaskWorkItem> waiting = queue.Waiting;
        var work = queue.Work; var input = queue.Input; var window = queue.Window; var ready = queue.Ready;
        var vehicles = graph.Vehicles; var generalReasons = graph.GeneralReasons; var edges = graph.Edges;
        var plans = graph.Plans; var diagnostics = graph.Diagnostics; var taskReasons = graph.TaskReasons;
        // First-leg handshake binding is the durable vehicle commitment. Once it
        // exists, do not let rematching alternate vehicles while the traffic
        // request still points at the previous attempt.
        if (handshakes is not null)
        {
            var boundVehicles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in work)
            {
                if (!ready.TryGetValue(task.TaskId, out var firstStep)) continue;
                var checkpoint = await handshakes.GetAsync(task.TaskId, firstStep.StepId, ct);
                if (BindingVehicle(checkpoint) is { Length: > 0 } vehicle) boundVehicles[task.TaskId] = vehicle;
            }
            if (boundVehicles.Count > 0)
                edges = edges.Where(edge => !boundVehicles.TryGetValue(edge.TaskId, out var vehicle) ||
                    string.Equals(edge.VehicleId, vehicle, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        optimization?.ReportLayer("candidates", edges.Count == 0 && window.Count > 0 ? "Waiting" : "Ready",
            $"通过硬约束 {generalReasons.Count(p => p.Value is null)} 台车，形成 {edges.Count} 条可执行候选边。", edges.Count);
        optimization?.ReportLayer("matching", "Running", "最小费用最大流计算；只有等待任务与未占用车辆参与。");
        var result = _optimizer.Optimize(input, edges, policy, now, slots);
        optimization?.ReportLayer("matching", policy.Mode.Equals("Shadow", StringComparison.OrdinalIgnoreCase) ? "Observing" : "Ready",
            $"本轮推荐 {result.Assignments.Count} 单；已发车任务保持原车。" + (policy.Mode.Equals("Shadow", StringComparison.OrdinalIgnoreCase) ? "影子结果只供比对。" : "推荐须继续经过路权与安全层。"), result.Assignments.Count);
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
        var blockedLayers = new Dictionary<string, string>();
        var preflightMs = 0d;
        var commitMs = 0d;
        if (global && slots > 0)
        {
            var failedPairs = new HashSet<(string, string)>();
            var remaining = edges.ToList();
            var prefetched = new Dictionary<(string TaskId, string VehicleId), Task<string?>>();
            var firstMatch = _optimizer.Optimize(input, remaining, policy, now, slots);
            var prefetchWatch = Stopwatch.StartNew();
            foreach (var assignment in firstMatch.Assignments)
            {
                var task = work.Single(t => t.TaskId == assignment.TaskId);
                var vehicle = vehicles.Single(v => v.VehicleId == assignment.VehicleId);
                var plan = plans[(task.TaskId, vehicle.VehicleId)];
                prefetched[(task.TaskId, vehicle.VehicleId)] = CheckOptimizedVehicleMapAsync(
                    vehicle, plan, map, options.GetDumperWaitingSites().ToArray(),
                    options.DumperDumpingSite, ct);
            }
            if (prefetched.Count > 0)
                await Task.WhenAll(prefetched.Values);
            preflightMs += prefetchWatch.Elapsed.TotalMilliseconds;
            // Commit one recommendation then rematch: a failed reservation never steals another task's sole vehicle.
            var match = firstMatch;
            while (prepared.Count < slots && remaining.Count > 0)
            {
                var next = match.Assignments.FirstOrDefault();
                if (next is null) break;
                var task = work.Single(t => t.TaskId == next.TaskId);
                var vehicle = vehicles.Single(v => v.VehicleId == next.VehicleId);
                string? outcome;
                try
                {
                    var stage = Stopwatch.StartNew();
                    var plan = plans[(task.TaskId, vehicle.VehicleId)];
                    var waitingSites = options.GetDumperWaitingSites().ToArray();
                    var dumpingSite = options.DumperDumpingSite;
                    if (!prefetched.TryGetValue((task.TaskId, vehicle.VehicleId), out var preflight))
                    {
                        preflight = CheckOptimizedVehicleMapAsync(
                            vehicle, plan, map, waitingSites, dumpingSite, ct);
                    }
                    var preflightFailure = await preflight;
                    if (!prefetched.ContainsKey((task.TaskId, vehicle.VehicleId)))
                        preflightMs += stage.Elapsed.TotalMilliseconds;
                    if (preflightFailure is not null)
                    {
                        outcome = preflightFailure;
                        blockedLayers[task.TaskId] = "safety";
                        optimization?.ReportLayer("safety", "Blocked", outcome);
                    }
                    else
                    {
                        stage.Restart();
                        outcome = await TryPrepareOptimizedAsync(task, ready[task.TaskId], vehicle, plan,
                            next.EffectivePriority, map, policy, waitingSites, dumpingSite, ct,
                            layer => blockedLayers[task.TaskId] = layer,
                            graph.StagedPairs.Contains((task.TaskId, vehicle.VehicleId)));
                        commitMs += stage.Elapsed.TotalMilliseconds;
                    }
                }
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
                    blockedLayers[task.TaskId] = "execution";
                }
                taskReasons[task.TaskId] = outcome ?? "已取得路权并原子登记首段命令，保持原任务与车辆责任。";
                if (outcome is null)
                {
                    prepared[task.TaskId] = vehicle.VehicleId;
                    blockedLayers.Remove(task.TaskId);
                    remaining.RemoveAll(e => e.TaskId == task.TaskId || e.VehicleId == vehicle.VehicleId);
                    var energy = diagnostics[task.TaskId].FirstOrDefault(row => row.VehicleId == vehicle.VehicleId)?.Energy;
                    if (energy is not null && evidenceJournal is not null)
                    {
                        try
                        {
                            await evidenceJournal.RecordAsync(task.TaskId, ready[task.TaskId].StepId, "DispatchEnergyForecast",
                                new { vehicleId = vehicle.VehicleId, stationId = task.StationId, energy }, ct);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception error) { logger.LogWarning(error, "续航预估证据写入失败。TaskId={TaskId}", task.TaskId); }
                    }
                }
                else
                {
                    failedPairs.Add((task.TaskId, vehicle.VehicleId));
                    remaining.RemoveAll(e => failedPairs.Contains((e.TaskId, e.VehicleId)));
                }
                match = _optimizer.Optimize(
                    input.Where(t => !prepared.ContainsKey(t.TaskId)).ToArray(),
                    remaining, policy, now, slots - prepared.Count);
            }
        }
        if (global && edges.Count > 0)
            logger.LogInformation("全局匹配分段耗时。候选和排队={PlanningMs:F1}ms，车端地图核验={PreflightMs:F1}ms，门禁等待和登记={CommitMs:F1}ms，候选={Candidates}，登记={Prepared}",
                planningMs, preflightMs, commitMs, edges.Count, prepared.Count);
        var rowsOut = window.Select(score => new DispatchOptimizationTask(score.TaskId, score.StationId, score.CalledAt,
            score.BasePriority, score.EffectivePriority, score.WaitingSeconds / 60,
            result.Assignments.FirstOrDefault(a => a.TaskId == score.TaskId)?.VehicleId,
            greedy.GetValueOrDefault(score.TaskId)?.VehicleId, prepared.GetValueOrDefault(score.TaskId),
            taskReasons.GetValueOrDefault(score.TaskId) ?? result.Unassigned.FirstOrDefault(u => u.TaskId == score.TaskId)?.Reason ??
                (global ? "本轮未登记，等待下一周期。" : "影子推荐：实际派车沿用现有编排流程。"), diagnostics[score.TaskId])
            { BlockedLayer = prepared.ContainsKey(score.TaskId) ? null : blockedLayers.GetValueOrDefault(score.TaskId) ??
                (!ready.ContainsKey(score.TaskId) ? "queue" : !edges.Any(e => e.TaskId == score.TaskId) ? "candidates" :
                !result.Assignments.Any(a => a.TaskId == score.TaskId) ? "matching" : null) }).ToArray();
        var greedyPriority = window.Where(w => greedy.ContainsKey(w.TaskId)).Sum(w => w.EffectivePriority);
        var improved = result.Assignments.Count > greedy.Count || result.Assignments.Count == greedy.Count &&
            (result.Assignments.Sum(a => a.EffectivePriority) > greedyPriority ||
             result.Assignments.Sum(a => a.EffectivePriority) == greedyPriority && result.TotalTravelSeconds < greedy.Values.Sum(e => e.TravelSeconds) - .001);
        optimization!.Publish(new()
        {
            CapturedAt = now, DurationMs = watch.Elapsed.TotalMilliseconds, WaitingTaskCount = waiting.Count,
            WindowTaskCount = window.Count, EligibleVehicleCount = generalReasons.Count(p => p.Value is null),
            CandidateCount = edges.Count, RetainedCandidateCount = result.RetainedCandidateCount,
            RouteCacheHits = ((routeCache?.Hits ?? 0) - hits) + graph.RouteMemoHits,
            RouteCacheMisses = ((routeCache?.Misses ?? 0) - misses) + graph.RouteMemoMisses, Tasks = rowsOut,
            Message = $"{(global ? "全局派车" : "影子比对")}：推荐 {result.Assignments.Count} 单，贪心 {greedy.Count} 单，登记 {prepared.Count} 单；" +
                (slots == 0 ? "并发槽位已满，既有任务保持不动。" : "等待任务继续保存在原队列。") +
                (waiting.Count != work.Length ? $" {waiting.Count - work.Length} 个已有车辆或路权的任务保持固定。" : "")
        }, !global, !global && improved);
    }

    private async Task<string?> CheckOptimizedVehicleMapAsync(AgvVehicleStatus vehicle, RoutePlan planned,
        ActiveMapRuntimeSnapshot? map, IReadOnlyList<string> waitingSites, string dumpingSite, CancellationToken ct)
    {
        if (robokitMap is null) return null;
        if (map is null || !ReferenceEquals(map, activeMap.Current) || !executionGate.CanStartNewWork)
            return "运行地图或新增派车门禁已变化。";
        var check = await robokitMap.CheckWaitingSitesAsync(vehicle.VehicleId, vehicle.CurrentStation!,
            planned.Nodes[^1], waitingSites, dumpingSite, map.MapId, map.VersionId, ct);
        return check.Accepted ? null : "派车前车端地图核验未通过：" + check.Message;
    }

    private async Task<string?> TryPrepareOptimizedAsync(DispatchTaskWorkItem task, DispatchTaskStepWorkItem step,
        AgvVehicleStatus vehicle, RoutePlan planned, double priority, ActiveMapRuntimeSnapshot? map,
        DispatchAssignmentOptions policy, IReadOnlyList<string> waitingSites, string dumpingSite,
        CancellationToken ct, Action<string>? rejectedLayer = null, bool staged = false)
    {
        string Reject(string layer, string reason)
        { rejectedLayer?.Invoke(layer); optimization?.ReportLayer(layer, layer == "traffic" ? "Waiting" : "Blocked", reason); return reason; }
        bool ContextCurrent() => executionGate.CanStartNewWork && ReferenceEquals(map, activeMap.Current) &&
            ReferenceEquals(policy, optimization?.Options) && dumpingSite == options.DumperDumpingSite &&
            options.EngineeringMapError(map) is null && options.AllowsVehicle(task.StationId, vehicle.VehicleId) &&
            MapRouteSiteMapper.IsSameNode(map?.Definition, planned.Nodes[^1], options.GetPickupSite(task.StationId)) &&
            waitingSites.SequenceEqual(options.GetDumperWaitingSites(), StringComparer.Ordinal);

        // D2: vendor vehicle status is network I/O. Read it before taking the
        // exclusive new-work lease; the outbox revalidates the same vehicle at
        // the physical send boundary anyway.
        var currentVehicles = await agvGateway.GetVehiclesAsync(ct);
        var current = currentVehicles.SingleOrDefault(v => v.VehicleId == vehicle.VehicleId);
        if (current is null) return Reject("safety", "车辆状态已丢失。");
        using var chargingLease = charging is null ? null : await charging.EnterBusinessAsync(ct);
        using var execution = await executionGate.EnterAsync(MapExecutionOperation.NewWork, ct);
        if (execution is null || !ContextCurrent())
            return Reject("safety", "运行地图、倒料配置、派车策略或新增派车门禁已变化。");
        // Predecessors are rechecked under the new-work lease: a window selected before a predecessor completed
        // must not be committed against a stale dependency snapshot.
        if (task.DependsOnTaskIds.Count > 0 &&
            !DispatchQueueWindowService.DependenciesComplete(task, (await tasks.GetTaskDependencyStatesAsync(task.DependsOnTaskIds.ToArray(), ct))
                .ToDictionary(state => state.TaskId, StringComparer.OrdinalIgnoreCase)))
            return Reject("queue", "前置任务尚未完成，本单保持等待，不预占车辆或路权。");
        var admissionState = await outbox.GetPickupAdmissionAsync(task.TaskId, ct);
        if (!admissionState.Allowed)
        {
            await _trafficLayer.SuspendRecommendationAsync(new(step.StepId, task.TaskId, vehicle.VehicleId,
                planned.Nodes[0], planned.Nodes[^1], [], [], true, admissionState.Reason)
                { MapId=map?.MapId, MapVersionId=map?.VersionId }, ct);
            return Reject("traffic", admissionState.Reason);
        }
        var active = (await tasks.GetSubmittingTasksAsync(ct)).Concat(await tasks.GetAssignedTasksAsync(ct)).ToArray();
        if (active.Length >= options.MaxConcurrentTasks) return Reject("traffic", "车队并发槽位已用完。");
        var busy = active
            .Where(t => t.VehicleId is not null).Select(t => t.VehicleId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        busy.UnionWith(await trafficResources.GetHeldVehicleIdsAsync(ct));
        var exclusion = VehicleExclusion(current, busy, policy);
        if (exclusion is not null) return Reject("safety", exclusion);
        if (!MapRouteSiteMapper.IsSameNode(map?.Definition, current!.CurrentStation, vehicle.CurrentStation))
            return Reject("safety", "车辆起点已变化，留待下一周期重新规划。");
        if (fleetTest?.Allows(task.TaskId, vehicle.VehicleId) == false || charging?.AllowsNewBusiness(vehicle.VehicleId) == false ||
            taskSwitchGuard is not null && !await taskSwitchGuard.AllowsVehicleAsync(vehicle.VehicleId, task.TaskId, null, ct)) return Reject("safety", "车辆责任门禁已变化。");
        var unavailable = await trafficResources.GetUnavailableResourceIdsAsync(task.TaskId, ct);
        var currentTask = (await tasks.GetDispatchCandidatesAsync(int.MaxValue, ct)).SingleOrDefault(t => t.TaskId == task.TaskId);
        var currentStep = (await tasks.GetReadyStepsAsync(int.MaxValue, ct)).SingleOrDefault(s => s.StepId == step.StepId);
        if (currentTask is null || currentTask.VehicleId is not null || currentStep is null ||
            (await trafficResources.GetHeldTaskIdsAsync(ct)).Contains(task.TaskId)) return Reject("safety", "任务、首步骤或既有路权责任已变化，保留持久化原记录。");
        // Bypass cache at the final check. A recommendation is never proof of a current executable route.
        var route = routePlanner.Plan(current.CurrentStation!, planned.Nodes[^1],
            staged ? new HashSet<string>() : unavailable);
        StagedPickupRoute? partial = null;
        if (staged && map is not null && pickupStaging is not null)
        {
            var vehicleIds = currentVehicles.Select(v => v.VehicleId).ToArray();
            if (route is not null && SamePlan(route, planned))
                partial = pickupStaging.TryStage(route, map, unavailable, vehicleIds);
            else
            {
                // A nominated route via a safe holding bay need not be the shortest full route.
                partial = pickupStaging.TryStageViaHolding(routePlanner, map, current.CurrentStation!,
                    planned.Nodes[^1], unavailable, vehicleIds);
                if (partial is null && route is not null)
                {
                    var directFirst = PlanInitialTransportWindows(route, map)[0];
                    var proactive = pickupStaging.TryStageViaHolding(routePlanner, map,
                        current.CurrentStation!, planned.Nodes[^1],
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase), vehicleIds,
                        allowClearRoute: true);
                    if (proactive is not null && directFirst.EdgeIds.Count >= 32 &&
                        proactive.Windows[0].EdgeIds.Count * 2 <= directFirst.EdgeIds.Count &&
                        proactive.Route.Cost <= route.Cost * 1.15)
                        partial = proactive;
                }
                if (partial is not null) route = partial.Route;
            }
        }
        if (route is null || !SamePlan(route, planned) || !ReferenceEquals(map, activeMap.Current))
            return Reject("traffic", "道路、地图或公共资源已变化，重新进入匹配。");
        if (staged && (DispatchCandidateFilter.PhysicalPickupOccupants(planned.Nodes[^1], map?.Definition, currentVehicles)
                .Any(id => !id.Equals(vehicle.VehicleId, StringComparison.OrdinalIgnoreCase)) ||
                DispatchCandidateFilter.PhysicalIntermediateOccupants(route, map?.Definition, currentVehicles, vehicle.VehicleId).Count > 0))
            return Reject("safety", "取箱工位或路线实际占用已变化，重新进入匹配。");
        if (staged && partial is null && route.ResourceIds.Any(unavailable.Contains))
            return Reject("traffic", "首窗路权或候车点净空不足，重新进入匹配。");
        var windows = partial?.Windows ?? PlanInitialTransportWindows(route, map);
        if (map is not null) windows = TransportRouteWindows.NormalizeResources(windows, map);
        var firstWindow = windows[0];
        var reservation = await _trafficLayer.AcquireAsync(new(step.StepId, task.TaskId, vehicle.VehicleId,
            firstWindow.Source, firstWindow.Destination, firstWindow.EdgeIds, firstWindow.ResourceIds,
            false, "滚动全局匹配完成，仅按有效优先级申请首个安全路权窗口。")
            { MapId = map?.MapId, MapVersionId = map?.VersionId, QueuePriority = priority }, ct);
        if (!reservation.Acquired) return Reject("traffic", "等待公共资源 " + reservation.BlockingResourceId + "；已取得的有效路权继续保留。");
        var commandPrepared = false;
        try
        {
            if (!ContextCurrent() ||
                !agvStates.TryGet(vehicle.VehicleId, out var fresh) ||
                !MapRouteSiteMapper.IsSameNode(map?.Definition, fresh.CurrentStation, current.CurrentStation) ||
                VehicleExclusion(current, busy, policy) is not null) return Reject("safety", "路权取得后实时状态变化，未登记命令。");
            optimization?.ReportLayer("safety", "Running", "发车前复核当前地图、车辆、PLC 许可及实际路权。");
            if (preSendSafety is not null && await preSendSafety.CheckPreparationAsync(step, vehicle.VehicleId, firstWindow.Path, map, ct) is { } rejected)
                return Reject("safety", rejected);
            if (preSendSafety is null && handshake is not null) await handshake.BindBeforeTransportAsync(step with { VehicleId = vehicle.VehicleId }, ct);
            if (!ContextCurrent()) return Reject("safety", "登记前派车上下文已变化，保留原任务。");
            optimization?.ReportLayer("safety", "Ready", "本候选通过发车前复核；命令将以原步骤编号原子登记。");
            commandPrepared = await outbox.TryPrepareTransportWindowAsync(
                task.TaskId, step.StepId, vehicle.VehicleId, route.Nodes[0], route.Nodes[^1],
                firstWindow, windows, null, options.GetAction(DispatchTaskStepType.GoToPickup), ct);
            if (!commandPrepared) return Reject("execution", "任务或首步骤已变化，首段命令未重复登记。");
            if (handshakeOptions?.Enabled != true) await _trafficLayer.HandoffAsync(task.TaskId, step.StepId, ct);
            optimization?.ReportLayer("execution", "Waiting", "首段命令已持久化，等待发送前再次复核；恢复与重试沿用原步骤编号。", 1);
            logger.LogInformation(
                "滚动全局匹配已登记首段命令。TaskId={TaskId}, VehicleId={VehicleId}, WindowTo={WindowDestination}, WindowCount={WindowCount}, EffectivePriority={Priority}",
                task.TaskId, vehicle.VehicleId, firstWindow.Destination, windows.Count, priority);
            return null;
        }
        finally
        {
            if (!commandPrepared) await _trafficLayer.RollbackUncommittedAsync(step.StepId, CancellationToken.None);
        }
    }

    private IReadOnlyList<AgvTransportRouteWindow> PlanInitialTransportWindows(
        RoutePlan route, ActiveMapRuntimeSnapshot? map)
    {
        if (map is null)
        {
            return
            [
                new AgvTransportRouteWindow(
                    0, route.Nodes[0], route.Nodes[^1], route.Nodes, route.EdgeIds, route.ResourceIds, true)
            ];
        }

        var windows = pickupStaging?.PlanInitialWindows(route, map) ??
            TransportRouteWindows.Plan(route, map, _stepOptions, agvOptions?.NavigationCommandMode);
        return TransportRouteWindows.NormalizeResources(windows, map);
    }

    private static bool SamePlan(RoutePlan left, RoutePlan right) =>
        left.Nodes.SequenceEqual(right.Nodes) && left.EdgeIds.SequenceEqual(right.EdgeIds) &&
        left.ResourceIds.SequenceEqual(right.ResourceIds);

    private static string? BindingVehicle(HandshakeCheckpoint? checkpoint)
    {
        var context = checkpoint?.Baseline.FirstOrDefault(item =>
            item.StationId == "__context" && item.VariableName == "Binding")?.Address;
        if (string.IsNullOrWhiteSpace(context)) return null;
        try
        {
            using var document = JsonDocument.Parse(context);
            return document.RootElement.TryGetProperty("Vehicle", out var vehicle)
                ? vehicle.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
