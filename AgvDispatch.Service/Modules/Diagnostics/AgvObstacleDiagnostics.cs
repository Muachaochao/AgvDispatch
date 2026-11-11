using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Service;

/// <summary>Read-only observations. An obstacle clearing never proves task completion or a local detour.</summary>
public static class AgvObstacleDiagnostics
{
    public static AgvObstacleDiagnostic Analyze(AgvRobotSnapshot vehicle, DateTimeOffset now, TimeSpan staleAfter, ObstacleRecoveryOptions? options = null)
    {
        options ??= new ObstacleRecoveryOptions();
        var policy = $"车端Blocked/52200优先；{options.DetourAfterSeconds}秒只读评估、不取消，{options.WarningAfterSeconds}秒提醒，{options.CancelAfterSeconds}秒才允许考虑受控取消，{options.AlarmAfterSeconds}秒报警，{options.ClosureSuggestionAfterSeconds}秒升级人工及封路建议；原任务全链终态及安全核验通过才可自动改道续接";
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
            ? duration >= options.WarningAfterSeconds ? "Prolonged" : "Blocked" : vehicle.Blocked == false ? "Clear" : "Unknown";
        var message = state switch
        {
            "SafetyStop" => "急停或严重报警优先，相关流程须按恢复条件核对。",
            "Prolonged" => duration >= options.ClosureSuggestionAfterSeconds
                ? $"已连续受阻至少{options.ClosureSuggestionAfterSeconds}秒；升级人工处理及封锁实际受阻方向建议，保留任务、载荷、PLC和路权。"
                : duration >= options.AlarmAfterSeconds
                    ? $"已连续受阻至少{options.AlarmAfterSeconds}秒；持续报警，取消、退站或改道仍须独立观察时间和安全核验通过。"
                    : $"已连续受阻至少{options.WarningAfterSeconds}秒；提醒人工，继续只读核对和搜索候选，保留原导航和全部业务责任。",
            "Blocked" => duration >= options.DetourAfterSeconds
                ? $"车端受阻已满{options.DetourAfterSeconds}秒；只读核对状态及搜索替代路线，不取消导航。"
                : "车辆受阻等待；保留原导航、载荷、PLC和路权，立即处理急停、互锁及严重报警。",
            "Clear" => vehicle.LastObstacleClearedAt is null ? "当前未报告障碍受阻。" :
                "已观察到受阻信号清除；原任务是否继续或完成，以后续车端任务反馈为准。",
            _ => "车辆数据无效、过期或安全字段缺失，无法确认当前受阻或恢复状态。"
        };
        return new(state, vehicle.ObstacleSince, duration, vehicle.LastObstacleClearedAt, source, message, policy, evidence);
    }
}
