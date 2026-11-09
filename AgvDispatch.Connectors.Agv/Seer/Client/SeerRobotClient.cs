using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Connectors.Agv;

public sealed class SeerRobotClient : IAsyncDisposable
{
    private const ushort QueryAllStatus = 1100;
    private const ushort QueryTaskStatus = 1110;
    private const ushort ResumeTask = 3002;
    private const ushort CancelTask = 3003;
    private const ushort GoToTarget = 3051;
    private const ushort Translate = 3055;
    private const ushort GoToTargetList = 3066;

    private readonly AgvOptions _options;
    private readonly SeerTcpChannel _status;
    private readonly SeerTcpChannel _navigation;
    private bool _disposed;
    private volatile bool _communicationEnabled;

    public SeerRobotClient(AgvRobotOptions robot, AgvOptions options)
    {
        Robot = robot;
        _communicationEnabled = robot.Enabled;
        _options = options;
        _status = new(robot.IpAddress, robot.Port, options);
        _navigation = robot.NavigationPort == robot.Port ? _status : new(robot.IpAddress, robot.NavigationPort, options);
    }

    public AgvRobotOptions Robot { get; }
    public bool IsConnected => _status.IsConnected;
    public bool CommunicationEnabled => _communicationEnabled;
    public async Task SetCommunicationEnabledAsync(bool enabled)
    {
        _communicationEnabled = false;
        await _status.SetEnabledAsync(enabled);
        if (!ReferenceEquals(_status, _navigation)) await _navigation.SetEnabledAsync(enabled);
        Robot.Enabled = enabled;
        _communicationEnabled = enabled;
    }

    public async Task<(AgvRobotTelemetry Telemetry, TimeSpan ResponseTime)> PollAsync(
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var allStatus = await RequestAsync(
            QueryAllStatus,
            new { return_laser = false, return_beams3D = false },
            cancellationToken);
        using var taskStatus = await RequestAsync(QueryTaskStatus, new { }, cancellationToken);
        stopwatch.Stop();
        return (ParseTelemetry(allStatus.RootElement, taskStatus.RootElement), stopwatch.Elapsed);
    }

    public async Task<SeerTaskSnapshot?> QueryTaskAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        using var response = await RequestAsync(
            QueryTaskStatus,
            new { task_ids = new[] { taskId } },
            cancellationToken);
        return ParseTasks(response.RootElement).FirstOrDefault(task =>
            string.Equals(task.TaskId, taskId, StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<SeerTaskSnapshot>> QueryTasksAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        using var response = await RequestAsync(QueryTaskStatus, new { task_ids = ids }, ct);
        var rows = ParseTasks(response.RootElement);
        if (rows.Any(r=>!ids.Contains(r.TaskId,StringComparer.Ordinal)) || rows.GroupBy(r=>r.TaskId,StringComparer.Ordinal).Any(g=>g.Count()>1))
            throw new InvalidDataException("1110任务链响应含额外或重复编号，不能核定发送结果。");
        return rows;
    }

    public async Task GoToTargetAsync(
        string taskId,
        IReadOnlyList<string> path,
        AgvTransportAction? action,
        bool useSpecifiedPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ValidatePath(path);

        if (useSpecifiedPath)
        {
            var ids = AgvTransportSegmentIds.For(taskId, path.Count);
            var tasks = new List<Dictionary<string, object?>>(path.Count - 1);
            for (var index = 1; index < path.Count; index++)
            {
                var isFinal = index == path.Count - 1;
                var item = new Dictionary<string, object?>
                {
                    ["source_id"] = path[index - 1],
                    ["id"] = path[index],
                    ["task_id"] = ids[index - 1]
                };
                if (isFinal)
                {
                    AddAction(item, action);
                }

                tasks.Add(item);
            }

            using var response = await RequestAsync(
                GoToTargetList,
                new Dictionary<string, object?> { ["move_task_list"] = tasks },
                cancellationToken);
            return;
        }

        if (path.Count != 2)
        {
            throw new InvalidOperationException("3051单目标调试模式只允许包含起点和终点的直接路径。");
        }

        var payload = new Dictionary<string, object?>
        {
            ["source_id"] = path[0],
            ["id"] = path[1],
            ["task_id"] = taskId
        };
        AddAction(payload, action);
        using var directResponse = await RequestAsync(GoToTarget, payload, cancellationToken);
    }

    /// <summary>
    /// One isolated recovery move from the current pose to a named station.
    /// Robokit 3051 supports SELF_POSITION as source_id, but its manual marks
    /// this API as unsuitable for ordinary multi-vehicle scheduling. Callers
    /// must hold the traffic and execution gates and use it only as a guarded,
    /// single-vehicle fallback after the original navigation is terminal.
    /// </summary>
    public async Task GoFromCurrentPoseBackwardAsync(
        string taskId,
        string targetSite,
        double maximumSpeedMetersPerSecond,
        double maximumAccelerationMetersPerSecondSquared,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSite);
        if (!double.IsFinite(maximumSpeedMetersPerSecond) ||
            maximumSpeedMetersPerSecond is <= 0 or > 0.3)
            throw new ArgumentOutOfRangeException(nameof(maximumSpeedMetersPerSecond),
                "路段中段退回安全站点的3051限速必须在0至0.3m/s之间。");
        if (!double.IsFinite(maximumAccelerationMetersPerSecondSquared) ||
            maximumAccelerationMetersPerSecondSquared is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(maximumAccelerationMetersPerSecondSquared),
                "路段中段退回安全站点的3051限加速度必须在0至1m/s²之间。");

        var payload = new Dictionary<string, object?>
        {
            ["source_id"] = "SELF_POSITION",
            ["id"] = targetSite,
            ["task_id"] = taskId,
            ["method"] = "backward",
            ["max_speed"] = maximumSpeedMetersPerSecond,
            ["max_acc"] = maximumAccelerationMetersPerSecondSquared
        };
        using var response = await RequestAsync(GoToTarget, payload, cancellationToken);
    }

    /// <summary>
    /// Move straight backwards along the current edge without asking Robokit to
    /// plan a new route. This is limited to one metre because 3055 is a local
    /// translation primitive, not a replacement for route navigation.
    /// </summary>
    public async Task TranslateBackwardAsync(
        double distanceMeters,
        double maximumSpeedMetersPerSecond,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(distanceMeters) || distanceMeters is <= 0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(distanceMeters),
                "3055平动后退距离必须在0至1米之间。");
        if (!double.IsFinite(maximumSpeedMetersPerSecond) ||
            maximumSpeedMetersPerSecond is <= 0 or > 0.1)
            throw new ArgumentOutOfRangeException(nameof(maximumSpeedMetersPerSecond),
                "3055平动后退速度必须在0至0.1m/s之间。");

        using var response = await RequestAsync(
            Translate,
            new
            {
                dist = distanceMeters,
                vx = -maximumSpeedMetersPerSecond,
                vy = 0.0,
                mode = 1
            },
            cancellationToken);
    }

    public async Task CancelNavigationAsync(
        string taskId,
        bool smartCancel,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        using var response = await RequestAsync(
            CancelTask,
            new { smartCancel, taskId },
            cancellationToken);
    }

    public async Task ResumeNavigationAsync(CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(ResumeTask, null, cancellationToken);
    }

    internal async Task<JsonDocument> RequestAsync(ushort messageType, object? payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_communicationEnabled) throw new InvalidOperationException("车辆通信未启用，请在车辆管理中连接。");
        var channel = messageType switch
        {
            >= 1000 and < 2000 => _status,
            >= 3000 and < 4000 => _navigation,
            _ => throw new NotSupportedException($"未配置Robokit API {messageType}对应的通道。")
        };
        var document = await channel.RequestAsync(messageType, payload, cancellationToken);
        try { EnsureApiSuccess(document.RootElement, messageType); return document; }
        catch { document.Dispose(); throw; }
    }

    private static AgvRobotTelemetry ParseTelemetry(
        JsonElement allStatus,
        JsonElement taskStatus)
    {
        var tasks = ParseTasks(taskStatus);
        var alarms = ParseAlarms(allStatus);
        var battery = FindDecimal(allStatus, "battery_level");
        return new AgvRobotTelemetry(
            FindDouble(allStatus, "x"),
            FindDouble(allStatus, "y"),
            FindDouble(allStatus, "angle"),
            FindDouble(allStatus, "confidence"),
            FindString(allStatus, "current_station"),
            FindString(allStatus, "last_station"),
            battery.HasValue ? battery.Value * 100m : null,
            FindBoolean(allStatus, "blocked"),
            FindBoolean(allStatus, "emergency"),
            FindValueAsString(allStatus, "navigation_status") ?? FindValueAsString(allStatus, "task_status"),
            FindString(taskStatus, "source_name"),
            FindString(taskStatus, "target_name"),
            FindDouble(taskStatus, "percentage"),
            tasks,
            alarms,
            DateTimeOffset.Now)
        {
            VelocityX = FindDouble(allStatus, "vx"), VelocityY = FindDouble(allStatus, "vy"),
            AngularVelocity = FindDouble(allStatus, "w"), Charging = FindBoolean(allStatus, "charging"),
            BatteryVoltage = FindDouble(allStatus, "voltage"), BatteryCurrent = FindDouble(allStatus, "current"),
            BatteryTemperature = FindDouble(allStatus, "battery_temp")
        };
    }

    private static IReadOnlyList<SeerTaskSnapshot> ParseTasks(JsonElement root)
    {
        if (!TryFindProperty(root, "task_status_list", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var tasks = new List<SeerTaskSnapshot>();
        foreach (var item in list.EnumerateArray())
        {
            var taskId = FindString(item, "task_id");
            var status = FindInt32(item, "status");
            if (!string.IsNullOrWhiteSpace(taskId) && status.HasValue)
            {
                tasks.Add(new SeerTaskSnapshot(taskId, status.Value, FindInt32(item, "type")));
            }
        }

        return tasks;
    }

    private static void ValidatePath(IReadOnlyList<string> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count < 2 || path.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("AGV路径至少需要两个非空站点。", nameof(path));
        }

        for (var index = 1; index < path.Count; index++)
        {
            if (string.Equals(path[index - 1], path[index], StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("AGV路径不能包含相邻重复站点。", nameof(path));
            }
        }
    }

    private static void AddAction(
        IDictionary<string, object?> payload,
        AgvTransportAction? action)
    {
        if (action is null || string.IsNullOrWhiteSpace(action.Operation))
        {
            return;
        }

        payload["operation"] = action.Operation;
        AddIfPresent(payload, "jack_height", action.JackHeight);
        AddIfPresent(payload, "recognize", action.Recognize);
        AddIfPresent(payload, "use_pgv", action.UsePgv);
        AddIfPresent(payload, "use_down_pgv", action.UseDownPgv);
    }

    private static void AddIfPresent<T>(
        IDictionary<string, object?> payload,
        string name,
        T? value) where T : struct
    {
        if (value.HasValue)
        {
            payload[name] = value.Value;
        }
    }

    private static IReadOnlyList<AgvAlarmSnapshot> ParseAlarms(JsonElement root)
    {
        var alarms = new List<AgvAlarmSnapshot>();
        AddAlarms(root, "fatals", AgvAlarmSeverity.Fatal, alarms);
        AddAlarms(root, "errors", AgvAlarmSeverity.Error, alarms);
        AddAlarms(root, "warnings", AgvAlarmSeverity.Warning, alarms);
        AddAlarms(root, "notices", AgvAlarmSeverity.Notice, alarms);
        return alarms;
    }

    private static void AddAlarms(
        JsonElement root,
        string propertyName,
        AgvAlarmSeverity severity,
        ICollection<AgvAlarmSnapshot> target)
    {
        if (!TryFindProperty(root, propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var alarm in array.EnumerateArray())
        {
            var code = FindValueAsString(alarm, "code") ??
                FindValueAsString(alarm, "id") ?? "UNKNOWN";
            var message = FindString(alarm, "description") ??
                FindString(alarm, "message") ?? FindString(alarm, "desc");
            target.Add(new AgvAlarmSnapshot(code, severity, message));
        }
    }

    private static void EnsureApiSuccess(JsonElement root, ushort requestType)
    {
        var returnCode = FindInt32(root, "ret_code");
        if (returnCode is null or 0)
        {
            return;
        }

        var message = FindString(root, "err_msg") ?? "未提供错误信息";
        if (requestType is GoToTarget or GoToTargetList)
        {
            throw new AgvCommandRejectedException(requestType, returnCode.Value, message);
        }

        throw new InvalidOperationException(
            $"Robokit API {requestType}返回错误{returnCode}：{message}");
    }

    private static bool TryFindProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindProperty(property.Value, propertyName, out value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindProperty(item, propertyName, out value))
                {
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? FindString(JsonElement root, string name) =>
        TryFindProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? FindValueAsString(JsonElement root, string name) =>
        TryFindProperty(root, name, out var value) ? value.ToString() : null;

    private static double? FindDouble(JsonElement root, string name) =>
        TryFindProperty(root, name, out var value) && value.TryGetDouble(out var number)
            ? number
            : null;

    private static decimal? FindDecimal(JsonElement root, string name) =>
        TryFindProperty(root, name, out var value) && value.TryGetDecimal(out var number)
            ? number
            : null;

    private static int? FindInt32(JsonElement root, string name) =>
        TryFindProperty(root, name, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? FindBoolean(JsonElement root, string name)
    {
        if (!TryFindProperty(root, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            _ => null
        };
    }

    public void Disconnect() { _status.Disconnect(); _navigation.Disconnect(); }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _status.DisposeAsync();
        await _navigation.DisposeAsync();
    }
}
