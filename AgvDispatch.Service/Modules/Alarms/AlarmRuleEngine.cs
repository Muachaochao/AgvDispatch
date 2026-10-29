using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Application.Maps;

namespace AgvDispatch.Service;

public sealed class AlarmRuleEngine(
    ILogger<AlarmRuleEngine> logger,
    AlarmOptions options,
    PlcFleet plcFleet,
    AgvOptions agvOptions,
    IPlcSignalCache plcSignals,
    IAgvStateCache agvStates,
    IActiveMapRuntime activeMap,
    MapOptions mapOptions,
    IStationStateService stationStates,
    IActiveTaskLookup activeTasks,
    ITrafficResourceControlService trafficResources,
    ITrafficFlowMonitor trafficFlow,
    IAlarmService alarms) : IsolatedBackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> _offRouteSince =
        new(StringComparer.OrdinalIgnoreCase);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("报警规则引擎未启用。");
            return;
        }
        if (options.EvaluationIntervalMs < 500 ||
            options.LowBatteryCriticalPercent <= 0 ||
            options.LowBatteryWarningPercent <= options.LowBatteryCriticalPercent)
        {
            throw new InvalidOperationException("Alarm配置无效。");
        }

        logger.LogInformation(
            "报警规则引擎启动。周期={IntervalMs}ms，低电量={Warning}%/{Critical}%",
            options.EvaluationIntervalMs,
            options.LowBatteryWarningPercent,
            options.LowBatteryCriticalPercent);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var candidates = await EvaluateAsync(stoppingToken);
                await alarms.SynchronizeAsync(candidates, DateTimeOffset.Now, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "报警规则计算失败；本轮不改变已有报警状态。");
            }

            try
            {
                await Task.Delay(options.EvaluationIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<IReadOnlyCollection<AlarmCandidate>> EvaluateAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<AlarmCandidate>();
        if (plcFleet.Enabled)
        {
            AddPlcAlarms(result);
            AddStationAlarms(result);
        }
        if (agvOptions.Enabled)
        {
            AddAgvAlarms(result);
        }
        var tasks = await activeTasks.GetActiveTasksAsync(cancellationToken);
        foreach (var task in tasks.Where(item => item.Stage == DispatchTaskStage.Faulted))
        {
            result.Add(new AlarmCandidate(
                $"TASK:{task.TaskId}:FAULTED", "Task", task.TaskId, "TASK_FAULTED",
                $"任务{task.TaskId}执行故障",
                $"工位={task.StationId}，AGV={task.VehicleId ?? "未分配"}，更新时间={task.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
                AlarmSeverity.Error));
        }
        var resources = await trafficResources.GetResourcesAsync(cancellationToken);
        foreach (var lease in resources.SelectMany(resource => resource.Leases)
                     .Where(lease => lease.HeartbeatExpired))
        {
            result.Add(new AlarmCandidate(
                $"TRAFFIC:{lease.LeaseId}:EXPIRED", "TrafficResource", lease.ResourceId,
                "TRAFFIC_LEASE_EXPIRED", $"交通资源{lease.ResourceId}路权心跳过期",
                $"任务={lease.TaskId}，步骤={lease.OwnerStepId}，AGV={lease.VehicleId}；路权仍保持锁定。",
                AlarmSeverity.Error));
        }
        foreach (var deadlock in trafficFlow.GetSnapshot().Deadlocks)
        {
            result.Add(new AlarmCandidate(
                deadlock.CandidateId, "TrafficFlow", deadlock.EdgeId,
                "TRAFFIC_DEADLOCK_CANDIDATE", $"路段{deadlock.EdgeId}检测到潜在交通死锁",
                $"车辆={string.Join(',', deadlock.VehicleIds)}；{deadlock.Reason}；{deadlock.Suggestion}",
                AlarmSeverity.Critical));
        }
        return result;
    }

    private void AddPlcAlarms(List<AlarmCandidate> result)
    {
        foreach (var node in plcFleet.Connections.Where(n => n.Options.Enabled))
        {
            var connection = node.Supervisor.Current;
            if (connection.State == ConnectionState.Online) continue;
            var severity = connection.State is ConnectionState.Interrupted or ConnectionState.Faulted
                ? AlarmSeverity.Critical
                : AlarmSeverity.Warning;
            result.Add(new AlarmCandidate(
                $"PLC:{connection.DeviceId}:CONNECTION", "PLC", connection.DeviceId, "PLC_CONNECTION",
                $"PLC连接状态异常：{connection.State}",
                connection.Message ?? $"PLC连续失败{connection.ConsecutiveFailures}次。",
                severity));
        }
        foreach (var node in plcFleet.Connections.Where(n => n.Options.Enabled))
        {
            var stations = node.Catalog.Signals.Select(s => s.StationId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var enabled = plcSignals.GetAll().Where(s => s.Enabled && stations.Contains(s.StationId)).ToArray();
            AddSignalQualityAlarm(result, enabled, SignalQuality.Bad, AlarmSeverity.Error, "PLC_SIGNAL_BAD", node.Options.DeviceId);
            AddSignalQualityAlarm(result, enabled, SignalQuality.Stale, AlarmSeverity.Warning, "PLC_SIGNAL_STALE", node.Options.DeviceId);
        }
    }

    private static void AddSignalQualityAlarm(
        List<AlarmCandidate> result,
        IReadOnlyCollection<PlcSignalSnapshot> signals,
        SignalQuality quality,
        AlarmSeverity severity,
        string code, string deviceId)
    {
        var affected = signals.Where(signal => signal.Quality == quality).ToArray();
        if (affected.Length == 0) return;
        result.Add(new AlarmCandidate(
            $"PLC:{deviceId}:SIGNALS:{quality}", "PLC", deviceId, code,
            $"PLC存在{affected.Length}个{quality}质量点位",
            string.Join("；", affected.Take(5).Select(item => $"{item.StationId}.{item.VariableName}")) +
            (affected.Length > 5 ? "；…" : string.Empty),
            severity));
    }

    private void AddStationAlarms(List<AlarmCandidate> result)
    {
        foreach (var station in stationStates.GetAll())
        {
            if (station.State == StationOperationalState.Faulted)
            {
                result.Add(new AlarmCandidate(
                    $"STATION:{station.StationId}:FAULTED", "Station", station.StationId,
                    "STATION_FAULTED", $"工位{station.StationId}故障",
                    station.BlockReason ?? "PLC工位故障信号已置位。", AlarmSeverity.Error));
            }
            else if (station.State == StationOperationalState.SignalConflict)
            {
                result.Add(new AlarmCandidate(
                    $"STATION:{station.StationId}:CONFLICT", "Station", station.StationId,
                    "STATION_SIGNAL_CONFLICT", $"工位{station.StationId}信号冲突",
                    station.BlockReason ?? "工位信号组合不符合业务约束。", AlarmSeverity.Error));
            }
        }
    }

    private void AddAgvAlarms(List<AlarmCandidate> result)
    {
        var enabledIds = agvOptions.Robots.Where(robot => robot.Enabled)
            .Select(robot => robot.VehicleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var agv in agvStates.GetAll().Where(item => enabledIds.Contains(item.VehicleId)))
        {
            AddMapPositionAlarm(result, agv);
            if (agv.Connection != ConnectionState.Online)
            {
                result.Add(new AlarmCandidate(
                    $"AGV:{agv.VehicleId}:CONNECTION", "AGV", agv.VehicleId,
                    "AGV_CONNECTION", $"{agv.DisplayName}连接状态异常：{agv.Connection}",
                    agv.ErrorMessage ?? $"连续失败{agv.ConsecutiveFailures}次。",
                    agv.Connection is ConnectionState.Interrupted or ConnectionState.Faulted
                        ? AlarmSeverity.Critical : AlarmSeverity.Warning));
            }
            if (agv.BatteryPercent.HasValue && agv.BatteryPercent <= options.LowBatteryWarningPercent)
            {
                var critical = agv.BatteryPercent <= options.LowBatteryCriticalPercent;
                result.Add(new AlarmCandidate(
                    $"AGV:{agv.VehicleId}:LOW_BATTERY", "AGV", agv.VehicleId,
                    "AGV_LOW_BATTERY", $"{agv.DisplayName}电量过低",
                    $"当前电量={agv.BatteryPercent:F1}%",
                    critical ? AlarmSeverity.Critical : AlarmSeverity.Warning));
            }
            if (agv.EmergencyStopped == true)
            {
                result.Add(new AlarmCandidate(
                    $"AGV:{agv.VehicleId}:EMERGENCY_STOP", "AGV", agv.VehicleId,
                    "AGV_EMERGENCY_STOP", $"{agv.DisplayName}急停触发",
                    "AGV上报急停状态，禁止继续派发动作。", AlarmSeverity.Critical));
            }
            var obstacle = AgvObstacleDiagnostics.Analyze(agv, DateTimeOffset.UtcNow,
                TimeSpan.FromMilliseconds(agvOptions.StaleAfterMs));
            if (obstacle.State is "Blocked" or "Prolonged")
            {
                result.Add(new AlarmCandidate(
                    $"AGV:{agv.VehicleId}:BLOCKED", "AGV", agv.VehicleId,
                    "AGV_BLOCKED", $"{agv.DisplayName}运行受阻",
                    $"当前位置={agv.CurrentStation ?? "未知"}，来源={obstacle.Source}。{obstacle.Message}",
                    AlarmSeverity.Warning));
                if (obstacle.State == "Prolonged")
                    result.Add(new AlarmCandidate(
                        $"AGV:{agv.VehicleId}:BLOCKED_PROLONGED", "AGV", agv.VehicleId,
                        "AGV_BLOCKED_PROLONGED", $"{agv.DisplayName}持续受阻超过30秒",
                        $"本段连续观察时长={obstacle.DurationSeconds:F0}秒。{obstacle.Message}", AlarmSeverity.Warning));
            }
            foreach (var alarm in agv.Alarms)
            {
                result.Add(new AlarmCandidate(
                    $"AGV:{agv.VehicleId}:VENDOR:{alarm.Code}", "AGV", agv.VehicleId,
                    $"AGV_VENDOR_{alarm.Code}", $"{agv.DisplayName}厂家报警 {alarm.Code}",
                    alarm.Message ?? "厂家未提供报警说明。", MapSeverity(alarm.Severity)));
            }
        }
    }

    private void AddMapPositionAlarm(List<AlarmCandidate> result, AgvRobotSnapshot agv)
    {
        var now = DateTimeOffset.Now;
        var position = AgvMapPositionAnalyzer.Analyze(
            agv.X, agv.Y, agv.LocalizationConfidence, agv.LastSuccessfulPollAt,
            activeMap.Current?.Definition, now, TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds),
            mapOptions.MinimumLocalizationConfidence, mapOptions.MaximumRouteDeviationMeters);
        if (position.State != AgvMapPositionState.OffRoute)
        {
            _offRouteSince.Remove(agv.VehicleId);
            return;
        }
        if (!_offRouteSince.TryGetValue(agv.VehicleId, out var since))
        {
            _offRouteSince[agv.VehicleId] = now;
            return;
        }
        var duration = now - since;
        if (duration < TimeSpan.FromSeconds(mapOptions.OffRouteAlarmDelaySeconds)) return;
        result.Add(new AlarmCandidate(
            $"AGV:{agv.VehicleId}:OFF_ROUTE", "AGV", agv.VehicleId,
            "AGV_OFF_ROUTE", $"{agv.DisplayName}持续偏离运行路网",
            $"已持续{duration.TotalSeconds:F0}秒；{position.Message}", AlarmSeverity.Error));
    }

    private static AlarmSeverity MapSeverity(AgvAlarmSeverity severity) => severity switch
    {
        AgvAlarmSeverity.Fatal => AlarmSeverity.Critical,
        AgvAlarmSeverity.Error => AlarmSeverity.Error,
        AgvAlarmSeverity.Warning => AlarmSeverity.Warning,
        _ => AlarmSeverity.Info
    };
}
