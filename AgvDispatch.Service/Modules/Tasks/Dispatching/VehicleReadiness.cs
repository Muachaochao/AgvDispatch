using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Service;

/// <summary>
/// Single source of truth for the common "may this vehicle do work now"
/// checks. Callers still add their own route, station and operation-specific
/// evidence checks after this result.
/// </summary>
public static class VehicleReadiness
{
    public static string? Check(
        AgvRobotSnapshot? robot,
        AgvVehicleStatus? vehicle,
        DateTimeOffset now,
        TimeSpan maximumAge,
        IReadOnlyCollection<string>? idleOperatingStates = null,
        decimal? minimumBatteryPercent = null,
        bool rejectCharging = false)
    {
        if (vehicle is not null && vehicle.Connection != ConnectionState.Online)
            return "车辆离线。";
        if (robot is null || robot.Connection != ConnectionState.Online || robot.Quality != SignalQuality.Good)
            return "车辆离线或遥测质量无效。";
        if (robot.LastSuccessfulPollAt is not { } observed ||
            observed > now.AddSeconds(1) ||
            now - observed > maximumAge)
            return "车辆观察已过期或时间异常。";
        if (robot.EmergencyStopped != false || robot.Blocked != false ||
            AgvObstaclePolicy.IsBlocked(robot) || AgvObstaclePolicy.HasBlockingAlarm(robot))
            return "车辆急停、阻挡、阻塞报警或健康状态未知。";
        if (rejectCharging && robot.Charging == true)
            return "车端报告正在充电，不能发送运输命令。";
        if (robot.Tasks.Any(task => task.Status is 1 or 2 or 3))
            return "车辆存在未结束的车端任务。";
        if (vehicle is not null && idleOperatingStates is not null &&
            !idleOperatingStates.Contains(vehicle.OperatingState, StringComparer.OrdinalIgnoreCase))
            return "车辆未空闲。";
        if (minimumBatteryPercent is { } minimum &&
            (robot.BatteryPercent is null or < 0 or > 100 || robot.BatteryPercent < minimum ||
             vehicle is not null && (vehicle.BatteryPercent is < 0 or > 100 || vehicle.BatteryPercent < minimum)))
            return "电量未知、无效或低于业务最低电量。";
        return null;
    }
}
