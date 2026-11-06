using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Service;

/// <summary>Read-only observations. An obstacle clearing never proves task completion or a local detour.</summary>
public static class AgvObstacleDiagnostics
{
    public static AgvObstacleDiagnostic Analyze(AgvRobotSnapshot vehicle, DateTimeOffset now, TimeSpan staleAfter)
    {
        const string policy = "车端Blocked/52200优先；5秒搜索安全替代路线，安全条件全部通过后自动改道；10秒预警、30秒报警、60秒建议封路";
        const string evidence = "自动改道必须保留原任务、载荷、PLC责任和路权；位置、地图、车身包络或车端终态未知时冻结";
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
            "Prolonged" => duration >= 60
                ? "已连续受阻至少60秒；建议正式封锁实际受阻方向及必要上游汇入，并核对安全点、载荷和PLC。其他无冲突任务继续。"
                : duration >= 30
                    ? "已连续受阻至少30秒；后台应持续报警，保留原任务及路权，并继续核验安全替代路线或人工安全点方案。"
                    : "已连续受阻；后台正在检查替代路线和车辆停稳证据，保留原任务、载荷、PLC责任和路权。",
            "Blocked" => duration >= 10
                ? "车端持续受阻已进入预警；后台正在搜索并复核替代路线，仍不得发送第二条导航。"
                : duration >= 5
                    ? "车端持续受阻已满5秒；后台开始安全改道核验，原任务和路权保持不变。"
                    : "车辆受阻等待；车端先报Blocked/52200，后台保留原任务并等待安全改道门槛。",
            "Clear" => vehicle.LastObstacleClearedAt is null ? "当前未报告障碍受阻。" :
                "已观察到受阻信号清除；原任务是否继续或完成，以后续车端任务反馈为准。",
            _ => "车辆数据无效、过期或安全字段缺失，无法确认当前受阻或恢复状态。"
        };
        return new(state, vehicle.ObstacleSince, duration, vehicle.LastObstacleClearedAt, source, message, policy, evidence);
    }
}
