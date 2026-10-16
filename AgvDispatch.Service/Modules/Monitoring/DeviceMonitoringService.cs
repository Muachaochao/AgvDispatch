using System.Globalization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Stations;

namespace AgvDispatch.Service;

public sealed class DeviceMonitoringService(IAgvStateCache vehicles, AgvOptions options,
    PlcFleet fleet, IPlcSignalCache signals, IStationStateService stations, IActiveTaskLookup tasks) : IDeviceMonitoringService
{
    public async Task<DeviceMonitoringSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var activeTasks = await tasks.GetActiveTasksAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var cachedVehicles = vehicles.GetAll().ToDictionary(v => v.VehicleId, StringComparer.OrdinalIgnoreCase);
        var agvs = options.Robots.Select(robot =>
        {
            cachedVehicles.TryGetValue(robot.VehicleId, out var v);
            var enabled = options.Enabled && robot.Enabled;
            var quality = Effective(v?.Quality ?? SignalQuality.Unknown, v?.LastSuccessfulPollAt, enabled, now, options.StaleAfterMs);
            var fields = new List<MonitoringField>();
            void Add(string name, object? value) => fields.Add(new(name, Format(value), Quality(quality), "—", Time(v?.LastSuccessfulPollAt)));
            Add("当前位置 X / Y（米）", v?.X is { } x && v.Y is { } y ? $"{x:F3} / {y:F3}" : null);
            Add("角度（弧度）", v?.Angle); Add("定位置信度", v?.LocalizationConfidence);
            Add("当前站点", v?.CurrentStation); Add("上一站点", v?.LastStation);
            Add("起点 → 目标", v is null ? null : $"{Format(v.SourceStation)} → {Format(v.TargetStation)}");
            Add("路径进度（厂家原始值）", v?.PathPercentage);
            Add("电量（%）", v?.BatteryPercent); Add("充电状态", v?.Charging);
            Add("电压 / 电流 / 温度", v is null ? null : $"{Format(v.BatteryVoltage)} V / {Format(v.BatteryCurrent)} A / {Format(v.BatteryTemperature)} °C");
            Add("速度 X / Y / 角速度", v is null ? null : $"{Format(v.VelocityX)} / {Format(v.VelocityY)} / {Format(v.AngularVelocity)}");
            Add("阻挡", v?.Blocked); Add("急停", v?.EmergencyStopped);
            fields.Add(new("状态 / 导航端口", $"{robot.Port} / {robot.NavigationPort}", "配置", "—", Time(now)));
            Add("连续失败次数", v?.ConsecutiveFailures); Add("轮询耗时（ms）", v?.LastResponseTimeMs);
            fields.Add(new("通信状态", enabled && v is not null ? Connection(v.Connection) : "通信未启用", "诊断", "—", Time(v?.UpdatedAt)));
            var assigned = activeTasks.Where(t => string.Equals(t.VehicleId, robot.VehicleId, StringComparison.OrdinalIgnoreCase)).ToArray();
            fields.Add(new("上位机业务任务", assigned.Length == 0 ? "无活动绑定任务" : string.Join("；", assigned.Select(t => $"{t.TaskId} / {t.StationId} / {t.Stage}")), "数据库", "—", Time(now)));
            if (v is not null)
            {
                foreach (var task in v.Tasks) Add($"厂家任务 {task.TaskId}", $"状态码 {task.Status}（{(task.Status == 3 ? "暂停" : task.Status == 7 ? "超时" : "厂家状态")}） / 类型 {Format(task.Type)}");
                foreach (var alarm in v.Alarms) Add($"报警 {alarm.Code} / {alarm.Severity}", alarm.Message);
            }
            return new MonitoredDevice(robot.VehicleId, robot.DisplayName, "AGV", $"{robot.IpAddress}:{robot.Port}",
                !enabled ? "通信未启用" : quality != SignalQuality.Good ? Quality(quality) : v?.EmergencyStopped == true ? "急停" : v?.Alarms.Any(a => a.Severity >= AgvDispatch.Domain.Agvs.AgvAlarmSeverity.Error) == true ? "车辆报警" : v?.Blocked == true ? "受阻" : Format(v?.NavigationState),
                Quality(quality), Time(v?.LastSuccessfulPollAt), v?.ErrorMessage ?? "仅显示后台缓存；厂家任务状态码保留原值。", fields);
        }).ToArray();
        var stationStates = stations.GetAll().ToDictionary(s => s.StationId, StringComparer.OrdinalIgnoreCase);
        var equipment = new List<MonitoredDevice>();
        foreach (var plc in fleet.Connections)
        {
            var connection = plc.Supervisor.Current;
            equipment.Add(new(plc.Options.DeviceId, plc.Options.DisplayName, "PLC", $"{plc.Options.IpAddress}:{plc.Options.Port}",
                !plc.Options.Enabled ? "通信未启用" : connection.State == ConnectionState.Online &&
                    (connection.LastSuccessfulReadAt is null || now - connection.LastSuccessfulReadAt > TimeSpan.FromMilliseconds(plc.Options.StaleAfterMs)) ? "数据过期" : Connection(connection.State), "连接诊断", Time(connection.LastSuccessfulReadAt),
                connection.Message ?? "PLC连接状态", [
                    new("西门子系列", plc.Options.SiemensSeries, "配置", "—", "—"),
                    new("连续失败次数", Format(connection.ConsecutiveFailures), "诊断", "—", Time(connection.UpdatedAt)),
                    new("读取耗时（ms）", Format(connection.LastResponseTimeMs), "诊断", "—", Time(connection.UpdatedAt))]));
            foreach (var group in plc.Catalog.Signals.GroupBy(s => s.StationId))
            {
                stationStates.TryGetValue(group.Key, out var state);
                var fields = group.Select(point =>
                {
                    signals.TryGet(point.StationId, point.VariableName, out var signal);
                    var q = Effective(signal?.Quality ?? SignalQuality.Unknown, signal?.UpdatedAt,
                        plc.Options.Enabled && point.Enabled, now, plc.Options.StaleAfterMs);
                    return new MonitoringField($"{point.ChineseName} · {point.VariableName}", Format(signal?.Value),
                        Quality(q), $"{point.Address} / {point.Direction}", Time(signal?.UpdatedAt), signal?.ErrorMessage);
                }).ToList();
                var good = fields.Count > 0 && fields.All(f => f.Quality == "正常");
                var stateUsable = good && state is not null && state.SignalQuality == SignalQuality.Good &&
                    now - state.EvaluatedAt <= TimeSpan.FromMilliseconds(plc.Options.StaleAfterMs);
                var status = !plc.Options.Enabled ? "通信未启用" : !good ? "信号未就绪" : state is null ? "等待业务状态判定" : !stateUsable ? "业务状态未就绪" : Station(state.State);
                if (good && string.Equals(group.First().DeviceType, "Dumper", StringComparison.OrdinalIgnoreCase))
                {
                    bool On(string key) => signals.TryGet(group.Key, key, out var s) && s.Value == true;
                    status = On("Faulted") ? "故障" : On("DumpingCompletedMayLeave") ? "倒料完成 / 允许离开" : On("DumperReady") ? "准备就绪" : On("AgvAtDumpingPosition") ? "车辆在倒料位（等待PLC完成）" : On("AgvAtWaitingPosition") ? "车辆在等待位" : "等待握手";
                }
                fields.Add(new("允许创建任务", stateUsable ? Format(state!.CanCreateTask) : "未知 / 不适用", "业务判定", "—", Time(state?.EvaluatedAt)));
                fields.Add(new("活动任务 / 数量", state is null ? "—" : $"{Format(state.ActiveTaskId)} / {state.ActiveTaskCount}", "业务判定", "—", Time(state?.EvaluatedAt)));
                var category = group.First().DeviceType switch { "ChipRemovalStation" => "排屑工位", "Dumper" => "倒料机", var other => other };
                equipment.Add(new(group.Key, group.Key, category, plc.Options.DeviceId,
                    status, !plc.Options.Enabled ? "未启用" : good ? "正常" : "非正常 / 未采集", Time(state?.EvaluatedAt),
                    state?.BlockReason ?? "查看原始点位质量和 PLC 握手条件；未接入的设备不推测运行状态。", fields));
            }
        }
        return new(now, agvs, equipment);
    }
    private static SignalQuality Effective(SignalQuality q, DateTimeOffset? at, bool enabled, DateTimeOffset now, int staleMs) =>
        !enabled ? SignalQuality.Disabled : q == SignalQuality.Good && (at is null || now - at > TimeSpan.FromMilliseconds(staleMs)) ? SignalQuality.Stale : q;
    private static string Format(object? value) => value switch { null => "—", bool b => b ? "是 / 1" : "否 / 0", IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "—" };
    private static string Time(DateTimeOffset? at) => at?.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss") ?? "尚未采集";
    private static string Quality(SignalQuality q) => q switch { SignalQuality.Good => "正常", SignalQuality.Stale => "数据过期", SignalQuality.Bad => "采集异常", SignalQuality.Disabled => "未启用", _ => "等待首次采集" };
    private static string Connection(ConnectionState s) => s switch { ConnectionState.Online => "正常", ConnectionState.Connecting => "连接中", ConnectionState.Degraded => "不稳定", ConnectionState.Interrupted => "中断", ConnectionState.Faulted => "故障", _ => "未连接" };
    private static string Station(StationOperationalState s) => s switch {
        StationOperationalState.Idle => "空闲", StationOperationalState.FullBoxWaitingForPickup => "满箱待取",
        StationOperationalState.PickupInProgress => "取箱中", StationOperationalState.WaitingForEmptyBox => "等待空箱",
        StationOperationalState.EmptyBoxPlacementInProgress => "放箱中", StationOperationalState.Completed => "完成",
        StationOperationalState.Faulted => "故障", StationOperationalState.SignalConflict => "信号冲突",
        StationOperationalState.ManualIntervention => "人工处理中", StationOperationalState.Offline => "离线", _ => "未知" };
}
