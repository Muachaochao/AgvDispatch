namespace AgvDispatch.Domain.Agvs;

/// <summary>Local obstacle waiting never suppresses a fatal or unrelated error.</summary>
public static class AgvObstaclePolicy
{
    public static bool IsObstacleCode(string? code) =>
        string.Equals(code?.Trim(), "52200", StringComparison.Ordinal);

    public static bool IsLocalObstacle(AgvAlarmSnapshot alarm) =>
        alarm.Severity < AgvAlarmSeverity.Fatal && IsObstacleCode(alarm.Code);

    public static bool IsBlocked(AgvRobotSnapshot snapshot) =>
        snapshot.Blocked == true || snapshot.Alarms.Any(IsLocalObstacle);

    public static bool HasBlockingAlarm(AgvRobotSnapshot snapshot) =>
        snapshot.Alarms.Any(alarm => alarm.Severity >= AgvAlarmSeverity.Error && !IsLocalObstacle(alarm));
}
