using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Connectors.Agv;

public sealed class AgvStateCache : IAgvStateCache
{
    private readonly object _sync = new();
    private readonly Dictionary<string, AgvRobotSnapshot> _snapshots;
    private readonly Dictionary<string, AgvRobotSnapshot> _previousTelemetry = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _staleAfter;
    private readonly TimeProvider _clock;
    private long _version;
    private readonly HashSet<string> _disconnected = new(StringComparer.OrdinalIgnoreCase);

    public void SetCommunicationEnabled(string vehicleId, bool enabled)
    {
        lock (_sync)
        {
            if (enabled) _disconnected.Remove(vehicleId);
            else if (!_disconnected.Add(vehicleId)) return;
        }
        Update(vehicleId, p => p with { Connection = ConnectionState.Disconnected,
            Quality = enabled ? SignalQuality.Unknown : SignalQuality.Disabled,
            ObstacleSince = null,
            LastSuccessfulPollAt = null, ErrorMessage = enabled ? "等待重新采集。" : "操作员已断开通信。" }, true);
    }

    public AgvStateCache(AgvOptions options, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _staleAfter = TimeSpan.FromMilliseconds(Math.Max(1, options.StaleAfterMs));
        foreach (var robot in options.Robots.Where(r=>!options.Enabled||!r.Enabled)) _disconnected.Add(robot.VehicleId);
        var now = _clock.GetUtcNow();
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

    public bool TryGetConsecutiveTelemetry(string vehicleId, out AgvRobotSnapshot previous, out AgvRobotSnapshot latest)
    {
        lock (_sync)
        {
            _snapshots.TryGetValue(vehicleId, out latest!);
            return _previousTelemetry.TryGetValue(vehicleId, out previous!) &&
                latest is { LastSuccessfulPollAt: { } at } && _clock.GetUtcNow() - at < _staleAfter;
        }
    }

    public void ApplyTelemetry(string vehicleId, AgvRobotTelemetry telemetry, TimeSpan responseTime)
    {
        Update(vehicleId, previous =>
        {
            // Duplicate/out-of-order samples cannot extend or prove the end of a blockage.
            if (previous.LastSuccessfulPollAt is { } last && telemetry.ReadAt <= last)
                return previous with { ObstacleSince = null, Quality = SignalQuality.Stale,
                    ErrorMessage = "AGV采样时间重复或倒退，等待新的有效采集。" };
            var now = _clock.GetUtcNow();
            var valid = telemetry.ReadAt <= now && now - telemetry.ReadAt < _staleAfter;
            var consecutive = valid && previous.Connection == ConnectionState.Online &&
                previous.Quality == SignalQuality.Good && previous.LastSuccessfulPollAt is { } observed &&
                telemetry.ReadAt - observed < _staleAfter;
            var blocked = telemetry.Blocked == true || telemetry.Alarms.Any(AgvObstaclePolicy.IsLocalObstacle);
            var cleared = valid && telemetry.Blocked == false && !blocked;
            return previous with
            {
                Connection = ConnectionState.Online,
                Quality = valid ? SignalQuality.Good : SignalQuality.Stale,
                ObstacleSince = valid && blocked ? consecutive ? previous.ObstacleSince ?? telemetry.ReadAt : telemetry.ReadAt : null,
                LastObstacleClearedAt = consecutive && previous.ObstacleSince is not null && cleared
                    ? telemetry.ReadAt : previous.LastObstacleClearedAt,
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
            };
        });
    }

    public void MarkConnecting(string vehicleId) => Update(vehicleId, previous => previous with
    {
        Connection = ConnectionState.Connecting,
        ObstacleSince = null,
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
                _clock.GetUtcNow() - previous.LastSuccessfulPollAt.Value >= staleAfter;
            return previous with
            {
                Connection = failures >= 3 ? ConnectionState.Interrupted : ConnectionState.Degraded,
                ObstacleSince = null,
                Quality = stale ? SignalQuality.Stale : SignalQuality.Bad,
                ConsecutiveFailures = failures,
                ErrorMessage = exception.Message
            };
        });
    }

    private void Update(string vehicleId, Func<AgvRobotSnapshot, AgvRobotSnapshot> change, bool allowDisconnected = false)
    {
        AgvRobotSnapshot previous;
        AgvRobotSnapshot current;
        var now = _clock.GetUtcNow();
        lock (_sync)
        {
            if (!allowDisconnected && _disconnected.Contains(vehicleId)) return;
            if (!_snapshots.TryGetValue(vehicleId, out previous!))
            {
                return;
            }

            current = change(previous) with { UpdatedAt = now, Version = ++_version };
            if (current is { Connection: ConnectionState.Online, Quality: SignalQuality.Good, LastSuccessfulPollAt: { } at } &&
                previous is { Connection: ConnectionState.Online, Quality: SignalQuality.Good, LastSuccessfulPollAt: { } prior } &&
                at > prior && at - prior < _staleAfter)
                _previousTelemetry[vehicleId] = previous;
            else
                _previousTelemetry.Remove(vehicleId);
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
