using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Service;

/// <summary>Read-only observations. An obstacle clearing never proves task completion or a local detour.</summary>
public static class AgvObstacleDiagnostics
{
    public static AgvObstacleDiagnostic Analyze(AgvRobotSnapshot vehicle, DateTimeOffset now, TimeSpan staleAfter)
    {
        const string policy = "指定路径；保留原任务，不自动取消或改道";
        const string evidence = "未采集车端局部轨迹，未验证自主绕障";
        var source = vehicle.Blocked == true ? "Blocked" : "";
        if (vehicle.Alarms.Any(AgvObstaclePolicy.IsLocalObstacle))
            source = source.Length > 0 ? "Blocked + 52200" : "52200";
        var fresh = vehicle.Connection == ConnectionState.Online && vehicle.Quality == SignalQuality.Good &&
            vehicle.LastSuccessfulPollAt is { } observed && observed <= now.AddSeconds(1) && now - observed < staleAfter;
        var duration = fresh && AgvObstaclePolicy.IsBlocked(vehicle) && vehicle.ObstacleSince is { } since &&
            vehicle.LastSuccessfulPollAt >= since
            ? Math.Max(0, (vehicle.LastSuccessfulPollAt.Value - since).TotalSeconds) : (double?)null;
        var state = !fresh ? "Unknown" : vehicle.EmergencyStopped == true || AgvObstaclePolicy.HasBlockingAlarm(vehicle)
            ? "SafetyStop" : vehicle.EmergencyStopped is null ? "Unknown" : AgvObstaclePolicy.IsBlocked(vehicle)
            ? duration >= 30 ? "Prolonged" : "Blocked" : vehicle.Blocked == false ? "Clear" : "Unknown";
        var message = state switch
        {
            "SafetyStop" => "急停或严重报警优先，相关流程须按恢复条件核对。",
            "Prolonged" => "已连续受阻至少30秒，请现场检查障碍和通道；继续保留原任务及路权，不自动取消或换路。",
            "Blocked" => "车辆受阻等待；由车端处理停车或局部避让，障碍移除后继续查询原任务。",
            "Clear" => vehicle.LastObstacleClearedAt is null ? "当前未报告障碍受阻。" :
                "已观察到受阻信号清除；原任务是否继续或完成，以后续车端任务反馈为准。",
            _ => "车辆数据无效、过期或安全字段缺失，无法确认当前受阻或恢复状态。"
        };
        return new(state, vehicle.ObstacleSince, duration, vehicle.LastObstacleClearedAt, source, message, policy, evidence);
    }
}
