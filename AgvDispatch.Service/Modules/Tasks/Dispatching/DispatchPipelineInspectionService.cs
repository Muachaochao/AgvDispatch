using System.Globalization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Service;

/// <summary>Monitoring only. Reads stored work and caches; never evaluates the actionful pre-send service or touches an execution lease.</summary>
public sealed class DispatchPipelineInspectionService(ISqlSugarClient database, DispatchOptimizationService optimization,
    IAgvStateCache vehicles, IStationStateService stations, IPlcSignalCache signals,
    ITrafficResourceControlService traffic, IMapExecutionGate gate, IActiveMapRuntime activeMap,
    MapOptions mapOptions, DispatchOrchestratorOptions dispatch, DispatchTaskCreationOptions creation,
    AgvCommandOutboxOptions outbox, HandshakeExecutionOptions handshake,
    TrafficSegmentReleasePolicy? segmentRelease = null,
    TrafficSegmentCommitOptions? segmentCommit = null,
    TrafficSegmentReleaseMonitor? segmentMonitor = null) : IDispatchPipelineInspectionService
{
    private const int Limit = 100;
    private const int RecentSentLimit = 20;

    public async Task<DispatchPipelineInspectionSnapshot> GetAsync(CancellationToken ct)
    {
        var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow();
        var cycle = await optimization.GetAsync(ct);
        var store = await ReadStoreAsync(ct);
        var robots = vehicles.GetAll().OrderBy(v => v.VehicleId, StringComparer.OrdinalIgnoreCase).ToArray();
        var stationRows = stations.GetAll().OrderBy(s => s.StationId, StringComparer.OrdinalIgnoreCase).ToArray();
        var plc = signals.GetAll().Where(s => s.Enabled).OrderBy(s => s.StationId).ThenBy(s => s.VariableName).ToArray();
        var resources = await traffic.GetResourcesAsync(ct);
        var waits = await traffic.GetWaitingAsync(new(Sort: TrafficWaitingSort.PriorityThenWait, PageSize: Limit), ct);
        var leases = resources.SelectMany(r => r.Leases).ToArray();
        var ownedVehicles = store.OwnedVehicles
            .Concat(leases.Select(l => l.VehicleId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var map = activeMap.Current;
        var baseLayers = DispatchPipelineLayers.Initial();
        DispatchPipelineInspectionLayer Layer(string key, string state, string summary, DateTimeOffset? observedAt,
            IReadOnlyList<DispatchPipelineMetric> metrics, IReadOnlyList<DispatchPipelineCheck> checks,
            IReadOnlyList<DispatchPipelineInspectionRecord> records, int total, string note)
        {
            var definition = baseLayers.Single(l => l.Key == key);
            return new() { Key = key, Number = definition.Number, Name = definition.Name, State = state, Summary = summary,
                ObservedAt = observedAt, Metrics = metrics, Checks = checks, Records = records.Take(Limit).ToArray(),
                TotalCount = total, Truncated = total > Math.Min(records.Count, Limit), Note = note };
        }
        var queueRecords = SampleGroups(store.Queue.Select(t => new DispatchPipelineInspectionRecord
        {
            Id = "call:" + t.TaskId, TaskId = t.TaskId, StationId = t.StationId, VehicleId = t.VehicleId,
            State = ((DispatchTaskStage)t.Stage).ToString(), Title = t.StationId + " · 原始呼叫",
            Reason = t.ErrorMessage ?? (t.Stage == 0 ? "原任务等待 PLC 应答；不重新创建呼叫。" : "原任务保留在持久化等待队列。"),
            UpdatedAt = Stamp(t.UpdatedAt), Link = TaskLink(t.TaskId),
            Facts = [F("原呼叫时间", Text(Stamp(t.CreatedAt))), F("基础优先级", t.Priority), F("原请求编号", t.CreateRequestId),
                F("指定车辆", t.RequestedVehicleId), F("等待分钟", Math.Max(0, (now - new DateTimeOffset(t.CreatedAt)).TotalMinutes).ToString("F1", CultureInfo.InvariantCulture))]
        }), stationRows.Select(s => new DispatchPipelineInspectionRecord
        {
            Id = "station:" + s.StationId, TaskId = s.ActiveTaskId, StationId = s.StationId,
            State = s.State.ToString(), Title = s.StationId + " · 当前工位缓存",
            Reason = s.BlockReason ?? "当前工位评估缓存；实际接单仍由原 PLC 采样和持久化去重控制。",
            UpdatedAt = s.EvaluatedAt, Link = "/communication",
            Facts = [F("满箱呼叫", Bool(s.FullBoxRequested)), F("箱体到位", Bool(s.BoxPresent)), F("工位故障", Bool(s.Faulted)),
                F("PLC通信许可", Bool(s.CommunicationOnline)), F("信号质量", s.SignalQuality), F("允许创建任务的缓存评估", s.CanCreateTask ? "是" : "否"),
                F("工位活动任务数", s.ActiveTaskCount), F("状态评估时间", Text(s.EvaluatedAt))]
        }));
        var queue = Layer("queue", !creation.Enabled || !creation.AutomaticCreationEnabled ? "Disabled" : store.QueueCount > 0 ? "Waiting" : "Idle",
            $"原呼叫等待 {store.QueueCount} 单（待PLC应答 {store.AckCount}）；工位缓存 {stationRows.Length} 项。", now,
            [M("持久化待派车", store.QueueCount - store.AckCount, "单"), M("待 PLC 应答", store.AckCount, "单"), M("工位缓存", stationRows.Length, "个")],
            [C("creation", "任务接单总开关", creation.Enabled ? "Pass" : "Disabled", Bool(creation.Enabled), "开启后才接纳新呼叫", "监控查询不创建任务或应答PLC。", now),
                C("automaticCreation", "PLC 自动接单", creation.AutomaticCreationEnabled ? "Pass" : "Disabled", Bool(creation.AutomaticCreationEnabled), "现场明确启用", "关闭时仍展示原始任务和工位缓存。", now),
                C("queue-read", "持久化原呼叫读取", "Pass", store.QueueCount + " 单", "原呼叫时间/优先级不重置", "只读数据库；下面同时列出工位状态缓存，二者是不同记录。", now)],
            queueRecords, store.QueueCount + stationRows.Length, "原始呼叫与工位缓存交替采样，合计最多100项；总数为两类记录之和。工位评估时间不能替代PLC原始采样时间。完整任务请打开业务任务。" );

        var candidateRecords = robots.Select(robot =>
        {
            var reason = TelemetryReason(robot, now, ownedVehicles, cycle.Options.MinimumBatteryPercent);
            return new DispatchPipelineInspectionRecord
            {
                Id = "vehicle:" + robot.VehicleId, VehicleId = robot.VehicleId,
                State = reason is null ? "Observed" : reason.Value.State, Title = robot.VehicleId + " · 当前遥测",
                Reason = (reason?.Reason ?? "当前缓存未发现以下基础异常。") + " 这是只读遥测观察，工位车辆池/路线/容量仍以实际候选周期为准。",
                UpdatedAt = robot.LastSuccessfulPollAt, Link = "/vehicles",
                Facts = [F("连接", robot.Connection), F("信号质量", robot.Quality), F("电量", robot.BatteryPercent is { } b ? b.ToString("0.##") + "%" : "未知"),
                    F("当前站点", robot.CurrentStation), F("急停", Bool(robot.EmergencyStopped)), F("阻挡", Bool(robot.Blocked)),
                    F("定位置信度", robot.LocalizationConfidence), F("当前车辆任务数", robot.Tasks.Count(t => t.Status is 1 or 2 or 3)),
                    F("持久化责任/路权", ownedVehicles.Contains(robot.VehicleId) ? "已有占用" : "当前所读责任中未见占用"), F("最新成功采样", Text(robot.LastSuccessfulPollAt))]
            };
        }).ToArray();
        var observedCandidates = cycle.Layers.Single(l => l.Key == "candidates");
        var freshRobotCount = robots.Count(r => r.Connection == ConnectionState.Online && r.Quality == SignalQuality.Good && Fresh(r.LastSuccessfulPollAt, now, mapOptions.PositionStaleSeconds));
        var candidates = Layer("candidates", !dispatch.Enabled ? "Disabled" : ObservationState(observedCandidates, now),
            $"当前车辆缓存 {robots.Length} 台；最近候选周期形成 {cycle.CandidateCount} 条候选边。", !dispatch.Enabled ? now : Valid(observedCandidates.UpdatedAt),
            [M("当前车辆缓存", robots.Length, "台"), M("最近周期可用车辆", cycle.EligibleVehicleCount, "台"), M("最近周期候选边", cycle.CandidateCount, "条")],
            [C("dispatch", "业务派车开关", dispatch.Enabled ? "Pass" : "Disabled", Bool(dispatch.Enabled), "启用后才进行实际筛选", "即使关闭也只读展示当前缓存。", now),
                C("vehicle-cache", "车辆缓存读取", robots.Length == 0 ? "Unknown" : "Pass", robots.Length + " 台缓存", "读取现有缓存记录", "缓存存在不证明已经成功采样，逐车查看状态和原采样时间。", now),
                C("vehicle-fresh", "成功且新鲜的车辆采样", freshRobotCount == 0 ? "Unknown" : freshRobotCount == robots.Length ? "Pass" : "Waiting", freshRobotCount + " / " + robots.Length + " 台", "在线、质量有效、成功采样未过期", "只核对遥测采样，不代表工位车辆池、路线或全部车辆健康条件已通过。", now),
                ObservationCheck("candidate-cycle", "最近一次实际候选筛选", observedCandidates, now)], candidateRecords, robots.Length,
            "逐车记录来自当前缓存，不触发车辆网络查询。基础观察不等于任务—车辆组合已满足完整硬约束；组合依据见匹配层最近周期。" );

        var matchingObservation = cycle.Layers.Single(l => l.Key == "matching");
        var matchingRecords = cycle.Tasks.Select(t => new DispatchPipelineInspectionRecord
        {
            Id = "match:" + t.TaskId, TaskId = t.TaskId, StationId = t.StationId,
            VehicleId = t.DispatchedVehicleId ?? t.RecommendedVehicleId, State = t.DispatchedVehicleId is not null ? "Prepared" : t.RecommendedVehicleId is not null ? "Recommended" : "Waiting",
            Title = t.StationId + " · 最近周期匹配", Reason = t.Reason, UpdatedAt = Valid(cycle.CapturedAt), Link = TaskLink(t.TaskId),
            Facts = [F("原呼叫时间", Text(t.CalledAt)), F("基础优先级", t.BasePriority), F("有效优先级", t.EffectivePriority),
                F("等待分钟（该周期）", t.WaitMinutes), F("全局推荐", t.RecommendedVehicleId), F("传统推荐", t.GreedyVehicleId),
                F("实际登记", t.DispatchedVehicleId), F("受阻层", t.BlockedLayer), F("候选车辆及依据", string.Join("；", t.Candidates.Select(v => v.VehicleId + "：" + v.Reason + (v.Eligible ? $"，估算{v.TotalCost:0.##}秒" : "，排除"))))]
        }).ToArray();
        var matching = Layer("matching", !dispatch.Enabled || cycle.Options.Mode == "Disabled" ? "Disabled" : ObservationState(matchingObservation, now),
            $"模式 {cycle.Options.Mode}；最近编排观察第 {cycle.CycleNumber} 轮，推荐 {cycle.Tasks.Count(t => t.RecommendedVehicleId is not null)} 单、登记 {cycle.Tasks.Count(t => t.DispatchedVehicleId is not null)} 单。",
            !dispatch.Enabled || cycle.Options.Mode == "Disabled" ? now : Valid(matchingObservation.UpdatedAt),
            [M("最近周期窗口", cycle.WindowTaskCount, "单"), M("候选图保留", cycle.RetainedCandidateCount, "条"), M("计算耗时", cycle.DurationMs.ToString("0.##", CultureInfo.InvariantCulture), "ms"), M("滚动间隔配置", cycle.Options.IntervalMs, "ms")],
            [C("optimization-mode", "优化模式配置", cycle.Options.Mode == "Disabled" ? "Disabled" : "Pass", cycle.Options.Mode, "Shadow仅推荐；Global用于后续派车", "模式开启不等于派车总开关已开或已运行匹配。", now),
                ObservationCheck("matching-cycle", "最近实际匹配观察", matchingObservation, now)], matchingRecords, cycle.Tasks.Count,
            "本层表格来自最近编排/匹配观察，时间与当前数据库采集时间分别显示。推荐、路权和实际命令登记是不同结果；没有新周期时不把旧推荐当作当前执行。" );

        var segmentObservations = segmentMonitor?.Active(TimeSpan.FromMinutes(15)).Select(observation =>
        {
            var verified = observation.Candidates.Where(candidate => candidate.EvidenceVerified)
                .Select(candidate => candidate.ResourceId).ToArray();
            var held = observation.Candidates.Where(candidate => !candidate.EvidenceVerified)
                .Select(candidate => candidate.ResourceId + "：" + candidate.Reason).ToArray();
            var progress = observation.Progress;
            return new DispatchPipelineInspectionRecord
            {
                Id = "segment-release:" + observation.StepId,
                TaskId = observation.TaskId,
                VehicleId = observation.VehicleId,
                ResourceId = verified.FirstOrDefault() ?? observation.Candidates.FirstOrDefault()?.ResourceId,
                State = observation.State,
                Title = observation.VehicleId + " · 通过即释放观察",
                Reason = observation.Reason,
                UpdatedAt = observation.ObservedAt,
                Link = TaskLink(observation.TaskId),
                Facts =
                [
                    F("原步骤", observation.StepId),
                    F("已证明通过", verified.Length == 0 ? "尚无" : string.Join(", ", verified)),
                    F("继续占用", held.Length == 0 ? "无未通过资源" : string.Join("；", held)),
                    F("两帧路段进度", progress is null ? "未取得" :
                        $"{progress.FirstLastFullyTraversedEdgeIndex} → {progress.SecondLastFullyTraversedEdgeIndex}"),
                    F("两帧定位", progress is null ? "未取得" :
                        $"({progress.FirstX:0.###},{progress.FirstY:0.###}) → ({progress.SecondX:0.###},{progress.SecondY:0.###})"),
                    F("定位误差界", progress is null ? "未取得" :
                        $"{progress.FirstPositionUncertaintyMeters:0.###} / {progress.SecondPositionUncertaintyMeters:0.###} m"),
                    F("整车包络", progress is null ? "未核定" :
                        $"{progress.BodyProfileId}，半径 {progress.BodyRadiusMeters:0.###} m"),
                    F("两帧采样", progress is null ? "未取得" :
                        $"{Text(progress.FirstObservedAt)} → {Text(progress.SecondObservedAt)}")
                ]
            };
        }) ?? [];
        var releasedSegmentRecords = store.ReleasedSegments.Select(release => new DispatchPipelineInspectionRecord
        {
            Id = "segment-released:" + release.AuditId,
            TaskId = release.TaskId,
            VehicleId = release.VehicleId,
            ResourceId = release.ResourceId,
            State = "Released",
            Title = release.ResourceId + " · 已通过并释放",
            Reason = "完整车体已离开资源边界，且原路线后续不再使用该资源；租约删除与逐资源审计已同事务提交。",
            UpdatedAt = Stamp(release.ReleasedAtUtc),
            Link = TaskLink(release.TaskId),
            Facts =
            [
                F("原步骤", release.StepId),
                F("原租约", release.LeaseId),
                F("取得时间", Text(Stamp(release.AcquiredAt))),
                F("释放时间", Text(Stamp(release.ReleasedAtUtc))),
                F("证据摘要", release.EvidenceHash),
                F("提交请求", release.RequestId)
            ]
        }).ToArray();
        var trafficRecords = SampleGroups(segmentObservations.Select(record => record),
            releasedSegmentRecords.Select(record => record), waits.Items.Select(w => new DispatchPipelineInspectionRecord
        {
            Id = "wait:" + w.StepId, TaskId = w.TaskId, VehicleId = w.VehicleId, ResourceId = w.Blockers.FirstOrDefault()?.ResourceId,
            State = w.State, Title = w.VehicleId + " · 路权等待", Reason = w.Reason, UpdatedAt = w.UpdatedAt, Link = TaskLink(w.TaskId),
            Facts = [F("原步骤", w.StepId), F("路径", w.Source + " → " + w.Destination), F("所需资源", string.Join(", ", w.ResourceIds)),
                F("阻塞责任", string.Join("；", w.Blockers.Select(b => $"{b.ResourceId}：{b.VehicleId}/{b.TaskId}/{b.StepId}"))), F("原等待时间", Text(w.WaitingSince)),
                F("等待秒数", w.WaitingSeconds), F("优先级", w.Priority)]
        }), resources.SelectMany(r => r.Leases.Select(l => new DispatchPipelineInspectionRecord
        {
            Id = "lease:" + l.LeaseId, TaskId = l.TaskId, VehicleId = l.VehicleId, ResourceId = r.ResourceId,
            State = l.HeartbeatExpired ? "Retained" : "Held", Title = r.ResourceId + " · 实际路权占用",
            Reason = l.HeartbeatExpired ? "心跳已过期，物理路权仍保留，不能当作空闲容量。" : "持久化实际租约；归属于下面的原步骤和车辆。", UpdatedAt = l.RenewedAt, Link = TaskLink(l.TaskId),
            Facts = [F("占用步骤", l.OwnerStepId), F("容量/当前占用", $"{r.Capacity}/{r.OccupiedCount}"), F("取得时间", Text(l.AcquiredAt)), F("心跳期限", Text(l.ExpiresAt)), F("原任务状态", l.TaskStage)]
        })), resources.Select(r => new DispatchPipelineInspectionRecord
        {
            Id = "resource:" + r.ResourceId, ResourceId = r.ResourceId, State = r.OccupiedCount >= r.Capacity ? "Full" : "Available",
            Title = r.ResourceId + " · 当前容量", Reason = "资源目录与持久化占用的只读汇总；剩余容量不代替整条路径申请。", UpdatedAt = now, Link = "/resource-review",
            Facts = [F("容量", r.Capacity), F("实际占用", r.OccupiedCount), F("剩余容量", Math.Max(0, r.Capacity - r.OccupiedCount)), F("类型", r.Type)]
        }));
        var trafficLayer = Layer("traffic", waits.TotalWaiting > 0 ? "Waiting" :
                segmentObservations.Any(record => record.State == "PassedPendingRelease") ? "Running" :
                leases.Length > 0 ? "Running" : "Idle",
            $"持久化等待 {waits.TotalWaiting} 单，实际占用 {leases.Length} 项，自动通过即释放观察 {segmentObservations.Count()} 项，最近释放审计 {releasedSegmentRecords.Length} 项。", now,
            [M("等待申请", waits.TotalWaiting, "单"), M("实际租约", leases.Length, "项"),
                M("自动释放观察", segmentObservations.Count(), "项"), M("最近释放资源", releasedSegmentRecords.Length, "项"),
                M("公共资源", resources.Count, "项")],
            [C("traffic-read", "实际申请与容量读取", "Pass", $"等待{waits.TotalWaiting} / 租约{leases.Length}", "按当前申请、原步骤与占用责任核对", "读取不申请、不续约、不释放路权；心跳过期不解锁。", now)],
            trafficRecords, waits.TotalWaiting + leases.Length + resources.Count +
                segmentObservations.Count() + releasedSegmentRecords.Length,
            "通过即释放观察、最近释放审计、等待申请、实际租约和资源容量交替采样，合计最多100项。只有两帧进度、完整车体和后续不重入全部满足才释放；未通过资源继续占用。" );

        var safetyObservation = cycle.Layers.Single(l => l.Key == "safety");
        var safetyRecords = plc.Select(s => new DispatchPipelineInspectionRecord
        {
            Id = "plc:" + s.StationId + ":" + s.VariableName, StationId = s.StationId,
            State = !Fresh(s.UpdatedAt, now, handshake.StateMaxAgeSeconds) ? "Stale" : s.Quality != SignalQuality.Good || s.Value is null ? "Unknown" : "Observed",
            Title = s.StationId + " · " + s.ChineseName, Reason = s.ErrorMessage ?? "当前 PLC 点值缓存，仅为观察；该值是否满足发送许可须按原任务阶段复核。", UpdatedAt = s.UpdatedAt, Link = "/communication",
            Facts = [F("变量", s.VariableName), F("点表地址", s.Address), F("当前值", Bool(s.Value)), F("信号质量", s.Quality), F("原采样时间", Text(s.UpdatedAt)), F("信号版本", s.Version)]
        }).ToArray();
        var safetyLayer = Layer("safety", !dispatch.Enabled && !outbox.Enabled ? "Disabled" : gate.Mode is MapExecutionMode.Starting or MapExecutionMode.Held ? "Blocked" : ObservationState(safetyObservation, now),
            "只读查看地图门禁、PLC缓存及最近安全复核观察；完整发送复核只在实际执行链中发生。",
            !dispatch.Enabled && !outbox.Enabled || gate.Mode is MapExecutionMode.Starting or MapExecutionMode.Held ? now : Valid(safetyObservation.UpdatedAt),
            [M("PLC点值缓存", plc.Length, "项"), M("有效新鲜点值", plc.Count(s => s.Value is not null && s.Quality == SignalQuality.Good && Fresh(s.UpdatedAt, now, handshake.StateMaxAgeSeconds)), "项")],
            [C("execution-map", "运行地图", map is null ? "Fail" : "Pass", map is null ? "缺少运行地图" : map.MapId + " / " + map.VersionId, "明确运行地图及版本", "地图存在只证明这一项，不等于其他条件已复核。", now),
                C("map-gate", "新增任务执行门禁", gate.CanStartNewWork ? "Pass" : "Fail", gate.Mode.ToString(), "Running 且允许新增任务", gate.NewWorkBlockReason ?? "门禁当前允许新增任务，仍须满足全部任务级条件。", now),
                ObservationCheck("last-safety", "最近实际安全复核", safetyObservation, now),
                C("pre-send", "本次查看是否执行发送复核", "NotChecked", "未执行", "仅真实登记/发送时逐项复核", "监控读取不调用会绑定握手的安全服务；不申请路权、不授予车辆/PLC许可。")],
            safetyRecords, plc.Length, "点值按工位/变量排序，最多100项；地址来自当前真实点表。点值新鲜不等于当前运输段许可成立；历史安全结果保留原观察时间。" );

        DispatchPipelineInspectionRecord CommandRecord(AgvCommandOutboxEntity c) => new()
        {
            Id = "command:" + c.CommandId, TaskId = c.TaskId, VehicleId = c.VehicleId,
            State = ((AgvCommandStatus)c.Status).ToString(), Title = c.VehicleId + (c.Status == 30 ? " · 近期已发送历史" : " · 原持久化命令"),
            Reason = c.LastError ?? (c.Status == 30 ? "历史发送记录；不能据此认定车辆此刻仍在执行。" : "按原步骤与幂等编号等待、发送或恢复。"), UpdatedAt = Stamp(c.UpdatedAt), Link = TaskLink(c.TaskId),
            Facts = [F("原命令编号", c.CommandId), F("原步骤", c.StepId), F("幂等编号", c.IdempotencyKey), F("发送尝试", c.AttemptCount), F("下次尝试", Text(Stamp(c.NextAttemptAt))),
                F("厂家命令编号", c.VendorCommandId), F("发送时间", Text(Stamp(c.SentAt))), F("命令地图", c.ExecutionMapId + " / " + c.ExecutionMapVersionId)]
        };
        var commands = SampleGroups(store.Commands.Where(c => c.Status != 30).Select(CommandRecord),
            store.Commands.Where(c => c.Status == 30).Select(CommandRecord), store.Steps.Select(s => new DispatchPipelineInspectionRecord
        {
            Id = "step:" + s.StepId, TaskId = s.TaskId, VehicleId = s.VehicleId,
            State = ((DispatchTaskStepStatus)s.Status).ToString(), Title = "原任务步骤 · " + ((DispatchTaskStepType)s.StepType),
            Reason = s.ErrorMessage ?? "当前未闭环任务中的原步骤；状态只读保留。", UpdatedAt = Stamp(s.UpdatedAt), Link = TaskLink(s.TaskId),
            Facts = [F("原步骤", s.StepId), F("顺序", s.Sequence), F("路线", s.SourceSite + " → " + s.DestinationSite), F("厂家任务编号", s.VendorTaskId), F("重试计数", s.RetryCount)]
        }));
        var execution = Layer("execution", !outbox.Enabled ? "Disabled" : store.OpenCommandCount > 0 ? "Waiting" : store.ActiveTaskCount > 0 ? "Running" : "Idle",
            $"保留待发/发送中/重试/失败原命令 {store.OpenCommandCount} 条（含未来重试）；未闭环任务 {store.ActiveTaskCount} 单。", now,
            [M("需跟踪命令", store.OpenCommandCount, "条"), M("未闭环任务", store.ActiveTaskCount, "单"), M("未闭环任务步骤", store.StepCount, "项")],
            [C("outbox", "车辆命令发送开关", outbox.Enabled ? "Pass" : "Disabled", Bool(outbox.Enabled), "明确启用并通过原执行门禁", "读取不领取命令、不消耗重试次数。", now),
                C("command-identity", "原命令恢复责任", "NotChecked", "仅查看原编号", "厂家回执与任务反馈按原步骤核对", "本次查询不向车辆发命令，也不证明厂家已接收任何尚未确认的命令。")], commands,
            store.OpenCommandCount + store.RecentSentCount + store.StepCount,
            "需跟踪命令、最近20条已发送历史和未闭环任务步骤交替采样，合计最多100项。步骤源只取最近100个未闭环任务；原始历史和完整步骤请查看业务任务。" );

        trafficLayer = trafficLayer with
        {
            Checks =
            [
                .. trafficLayer.Checks,
                C("segment-release-observation", "通过即释放评估",
                    segmentRelease?.Enabled == true ? "Pass" : "Disabled",
                    segmentRelease?.Enabled == true ? "已启用" : "未启用",
                    "两帧权威进度与完整包络",
                    segmentRelease?.Enabled == true
                        ? "已启用只读评估；评估命中后仍须签发凭据并重新采集。"
                        : "未启用时所有资源保持到正常步骤交接或任务收尾。", now),
                C("segment-release-commit", "逐资源原子释放",
                    segmentCommit?.Enabled == true ? "Pass" : "Disabled",
                    segmentCommit?.Enabled == true ? "已启用" : "未启用",
                    "凭据、最新证据与租约同事务核对",
                    segmentCommit?.Enabled == true
                        ? "删除租约与审计写入必须同事务成功，失败则全部保留。"
                        : "只观察不删除租约。", now)
            ]
        };

        return new() { CapturedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(), CycleNumber = cycle.CycleNumber,
            // Gate-only publications also increment CycleNumber. Only the compute
            // path records positive elapsed work; do not label a disabled gate as a calculation.
            OptimizationCapturedAt = cycle.DurationMs > 0 ? Valid(cycle.CapturedAt) : null, Mode = cycle.Options.Mode,
            Summary = !dispatch.Enabled && !outbox.Enabled ? "业务派车和车辆命令发送均未启用；当前只读观察，不会自动派车。" :
                "按层核对当前缓存、持久化责任和最近运行观察；匹配推荐不代表已发车。",
            Gates = [new("creation", "任务接单", creation.Enabled, "接单总开关；原任务不因开关关闭而删除。"),
                new("automaticCreation", "PLC 自动接单", creation.AutomaticCreationEnabled, "是否从PLC呼叫自动创建持久化任务。"),
                new("dispatch", "业务派车", dispatch.Enabled, "决定是否进行任务筛选与派车编排。"), new("outbox", "车辆命令发送", outbox.Enabled, "原命令仍须通过地图、车辆、PLC及路权复核。"),
                new("map", "新增任务地图门禁", gate.CanStartNewWork, gate.Mode + "；" + (gate.NewWorkBlockReason ?? "当前允许进入新增任务检查。")),
                new("optimization", "优化计算配置", cycle.Options.Mode != "Disabled", cycle.Options.Mode + "；配置开启不等于当前派车开关已开或已计算。")],
            Layers = [queue, candidates, matching, trafficLayer, safetyLayer, execution] };
    }

    private async Task<StoredObservation> ReadStoreAsync(CancellationToken ct)
    {
        var result = new StoredObservation();
        var read = await database.Ado.UseTranAsync(async () =>
        {
            result.QueueCount = await database.Queryable<DispatchTaskEntity>().Where(t => t.Stage == 0 || t.Stage == 10).CountAsync(ct);
            result.AckCount = await database.Queryable<DispatchTaskEntity>().Where(t => t.Stage == 0).CountAsync(ct);
            result.Queue = await database.Queryable<DispatchTaskEntity>().Where(t => t.Stage == 0 || t.Stage == 10).OrderByDescending(t => t.Priority).OrderBy(t => t.CreatedAt).Take(Limit).ToListAsync(ct);
            result.ActiveTaskCount = await database.Queryable<DispatchTaskEntity>().Where(t => t.Stage != 90 && t.Stage != 110).CountAsync(ct);
            result.OwnedVehicles = await database.Queryable<DispatchTaskEntity>().Where(t => t.Stage != 90 && t.Stage != 110 && t.VehicleId != null)
                .Select(t => t.VehicleId!).Distinct().ToListAsync(ct);
            result.ActiveTasks = await database.Queryable<DispatchTaskEntity>().Where(t => t.Stage != 90 && t.Stage != 110).OrderByDescending(t => t.UpdatedAt).Take(Limit).ToListAsync(ct);
            var taskIds = result.ActiveTasks.Select(t => t.TaskId).ToArray();
            result.StepCount = await database.Queryable<DispatchTaskStepEntity, DispatchTaskEntity>((s, t) => new JoinQueryInfos(JoinType.Inner, s.TaskId == t.TaskId))
                .Where((s, t) => t.Stage != 90 && t.Stage != 110).CountAsync(ct);
            if (taskIds.Length > 0)
            {
                result.Steps = await database.Queryable<DispatchTaskStepEntity>().Where(s => taskIds.Contains(s.TaskId)).OrderBy(s => s.Status).OrderByDescending(s => s.UpdatedAt).Take(Limit).ToListAsync(ct);
            }
            result.OpenCommandCount = await database.Queryable<AgvCommandOutboxEntity>().Where(c => c.Status != 30 && c.Status != 100).CountAsync(ct);
            result.Commands = await database.Queryable<AgvCommandOutboxEntity>().Where(c => c.Status != 30 && c.Status != 100).OrderBy(c => c.NextAttemptAt).Take(Limit).ToListAsync(ct);
            var recent = await database.Queryable<AgvCommandOutboxEntity>().Where(c => c.Status == 30).OrderByDescending(c => c.UpdatedAt).Take(RecentSentLimit).ToListAsync(ct);
            result.RecentSentCount = recent.Count; result.Commands.AddRange(recent);
            result.ReleasedSegments = await database.Queryable<TrafficSegmentCommitLeaseEntity>()
                .OrderByDescending(row => row.ReleasedAtUtc).Take(RecentSentLimit).ToListAsync(ct);
        });
        if (!read.IsSuccess) throw new InvalidOperationException("读取六层任务观察失败。", read.ErrorException);
        return result;
    }
    private sealed class StoredObservation
    {
        public int QueueCount, AckCount, ActiveTaskCount, StepCount, OpenCommandCount, RecentSentCount;
        public List<DispatchTaskEntity> Queue = [], ActiveTasks = [];
        public List<DispatchTaskStepEntity> Steps = [];
        public List<AgvCommandOutboxEntity> Commands = [];
        public List<TrafficSegmentCommitLeaseEntity> ReleasedSegments = [];
        public List<string> OwnedVehicles = [];
    }
    private (string State, string Reason)? TelemetryReason(AgvRobotSnapshot robot, DateTimeOffset now, IReadOnlySet<string> owners, double battery)
    {
        if (!Fresh(robot.LastSuccessfulPollAt, now, mapOptions.PositionStaleSeconds)) return ("Stale", "最新成功遥测已过期、缺失或时间异常。");
        if (robot.Connection != ConnectionState.Online || robot.Quality != SignalQuality.Good) return ("Unknown", "车辆离线或遥测质量不可靠。");
        if (robot.EmergencyStopped != false || robot.Blocked != false || AgvObstaclePolicy.IsBlocked(robot) || AgvObstaclePolicy.HasBlockingAlarm(robot)) return ("Excluded", "急停、阻挡、报警或健康状态未知。");
        if (robot.BatteryPercent is null or < 0 or > 100 || robot.BatteryPercent < (decimal)battery) return ("Excluded", "电量无效或低于新业务最低电量。");
        if (robot.X is null || robot.Y is null || robot.LocalizationConfidence is null || robot.LocalizationConfidence < mapOptions.MinimumLocalizationConfidence) return ("Unknown", "定位坐标或置信度不满足基础观察条件。");
        if (owners.Contains(robot.VehicleId) || robot.Tasks.Any(t => t.Status is 1 or 2 or 3)) return ("Occupied", "当前所读任务或路权显示车辆有原责任，不可视为空闲候选。");
        return null;
    }
    private static DispatchPipelineCheck ObservationCheck(string key, string label, DispatchPipelineLayerSnapshot layer, DateTimeOffset now) =>
        C(key, label, Valid(layer.UpdatedAt) is null ? "NotChecked" : !Fresh(layer.UpdatedAt, now, 15) ? "Stale" : layer.State switch
        { "Ready" => "Pass", "Blocked" => "Fail", "Waiting" => "Waiting", "Disabled" => "Disabled", _ => "NotChecked" },
            layer.State, "该层实际执行记录及其原观察时刻", layer.Summary, Valid(layer.UpdatedAt));
    private static string ObservationState(DispatchPipelineLayerSnapshot layer, DateTimeOffset now) =>
        Valid(layer.UpdatedAt) is null ? "Unknown" : !Fresh(layer.UpdatedAt, now, 15) ? "Stale" : layer.State;
    private static DateTimeOffset? Valid(DateTimeOffset value) => value > DateTimeOffset.UnixEpoch ? value : null;
    private static bool Fresh(DateTimeOffset? value, DateTimeOffset now, double seconds) => value is { } v && v > DateTimeOffset.UnixEpoch && v <= now.AddSeconds(1) && now - v <= TimeSpan.FromSeconds(seconds);
    private static DateTimeOffset? Stamp(DateTime? value) => value is { } v && v > DateTime.UnixEpoch ? new DateTimeOffset(v) : null;
    private static string Text(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? "尚无记录";
    private static string Bool(bool? value) => value is true ? "是" : value is false ? "否" : "未知";
    private static string TaskLink(string task) => "/tasks/" + Uri.EscapeDataString(task);
    private static DispatchPipelineFact F(string label, object? value) => new(label, Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text : "未记录");
    private static DispatchPipelineMetric M(string label, object value, string? unit = null) => new(label, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "未知", unit);
    private static DispatchPipelineCheck C(string key, string label, string state, string actual, string expected, string reason, DateTimeOffset? observedAt = null) => new(key, label, state, actual, expected, reason, observedAt);
    private static IReadOnlyList<DispatchPipelineInspectionRecord> SampleGroups(params IEnumerable<DispatchPipelineInspectionRecord>[] groups)
    {
        var readers = groups.Select(g => g.GetEnumerator()).ToArray();
        var sample = new List<DispatchPipelineInspectionRecord>(Limit);
        try
        {
            var more = true;
            while (more && sample.Count < Limit)
            {
                more = false;
                foreach (var reader in readers)
                {
                    if (sample.Count == Limit) break;
                    if (!reader.MoveNext()) continue;
                    sample.Add(reader.Current); more = true;
                }
            }
            return sample;
        }
        finally { foreach (var reader in readers) reader.Dispose(); }
    }
}
