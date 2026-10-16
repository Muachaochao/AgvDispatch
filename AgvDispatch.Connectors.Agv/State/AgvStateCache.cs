using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Connectors.Agv;

public sealed class AgvStateCache : IAgvStateCache
{
    private readonly object _sync = new();
    private readonly Dictionary<string, AgvRobotSnapshot> _snapshots;
    private long _version;

    public AgvStateCache(AgvOptions options)
    {
        var now = DateTimeOffset.Now;
        _snapshots = options.Robots.ToDictionary(
            robot => robot.VehicleId,
            robot => new AgvRobotSnapshot(
                robot.VehicleId,
                string.IsNullOrWhiteSpace(robot.DisplayName) ? robot.VehicleId : robot.DisplayName,
                robot.IpAddress,
                robot.Port,
                ConnectionState.Disconnected,
                options.Enabled && robot.Enabled ? SignalQuality.Unknown : SignalQuality.Disabled,
                null, null, null, null, null, null, null, null, null, null, null, null, null,
                [], [], null, now, 0, null,
                options.Enabled && robot.Enabled ? null : "AGV采集未启用。",
                0),
            StringComparer.OrdinalIgnoreCase);
    }

    public event Action<AgvRobotStateChanged>? StateChanged;

    public long Version
    {
        get { lock (_sync) { return _version; } }
    }

    public IReadOnlyList<AgvRobotSnapshot> GetAll()
    {
        lock (_sync)
        {
            return _snapshots.Values
                .OrderBy(snapshot => snapshot.VehicleId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public bool TryGet(string vehicleId, out AgvRobotSnapshot snapshot)
    {
        lock (_sync)
        {
            return _snapshots.TryGetValue(vehicleId, out snapshot!);
        }
    }

    public void ApplyTelemetry(string vehicleId, AgvRobotTelemetry telemetry, TimeSpan responseTime)
    {
        Update(vehicleId, previous => previous with
        {
            Connection = ConnectionState.Online,
            Quality = SignalQuality.Good,
            X = telemetry.X,
            Y = telemetry.Y,
            Angle = telemetry.Angle,
            LocalizationConfidence = telemetry.LocalizationConfidence,
            CurrentStation = telemetry.CurrentStation,
            LastStation = telemetry.LastStation,
            BatteryPercent = telemetry.BatteryPercent,
            Blocked = telemetry.Blocked,
            EmergencyStopped = telemetry.EmergencyStopped,
            NavigationState = telemetry.NavigationState,
            VelocityX = telemetry.VelocityX, VelocityY = telemetry.VelocityY,
            AngularVelocity = telemetry.AngularVelocity, Charging = telemetry.Charging,
            BatteryVoltage = telemetry.BatteryVoltage, BatteryCurrent = telemetry.BatteryCurrent,
            BatteryTemperature = telemetry.BatteryTemperature,
            SourceStation = telemetry.SourceStation,
            TargetStation = telemetry.TargetStation,
            PathPercentage = telemetry.PathPercentage,
            Tasks = telemetry.Tasks,
            Alarms = telemetry.Alarms,
            LastSuccessfulPollAt = telemetry.ReadAt,
            ConsecutiveFailures = 0,
            LastResponseTimeMs = responseTime.TotalMilliseconds,
            ErrorMessage = null
        });
    }

    public void MarkConnecting(string vehicleId) => Update(vehicleId, previous => previous with
    {
        Connection = ConnectionState.Connecting,
        Quality = previous.LastSuccessfulPollAt.HasValue
            ? SignalQuality.Stale
            : SignalQuality.Unknown,
        ErrorMessage = "正在连接AGV。"
    });

    public void MarkFailure(string vehicleId, Exception exception, TimeSpan staleAfter)
    {
        Update(vehicleId, previous =>
        {
            var failures = previous.ConsecutiveFailures + 1;
            var stale = previous.LastSuccessfulPollAt.HasValue &&
                DateTimeOffset.Now - previous.LastSuccessfulPollAt.Value >= staleAfter;
            return previous with
            {
                Connection = failures >= 3 ? ConnectionState.Interrupted : ConnectionState.Degraded,
                Quality = stale ? SignalQuality.Stale : SignalQuality.Bad,
                ConsecutiveFailures = failures,
                ErrorMessage = exception.Message
            };
        });
    }

    private void Update(string vehicleId, Func<AgvRobotSnapshot, AgvRobotSnapshot> change)
    {
        AgvRobotSnapshot previous;
        AgvRobotSnapshot current;
        var now = DateTimeOffset.Now;
        lock (_sync)
        {
            if (!_snapshots.TryGetValue(vehicleId, out previous!))
            {
                return;
            }

            current = change(previous) with { UpdatedAt = now, Version = ++_version };
            _snapshots[vehicleId] = current;
        }

        Publish(new AgvRobotStateChanged(vehicleId, previous, current, now));
    }

    private void Publish(AgvRobotStateChanged change)
    {
        var subscribers = StateChanged;
        if (subscribers is null)
        {
            return;
        }

        foreach (Action<AgvRobotStateChanged> subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(change); }
            catch { /* 状态缓存不能被订阅方异常中断。 */ }
        }
    }
}
