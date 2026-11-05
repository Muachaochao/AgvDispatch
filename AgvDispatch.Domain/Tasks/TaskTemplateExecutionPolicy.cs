namespace AgvDispatch.Domain.Tasks;

public static class TaskTemplateExecutionPolicy
{
    public const string HoldUntilCondition = "HoldUntilCondition";
    public const string Pause = "Pause";
    public const string ManualRecovery = "ManualRecovery";
    public const string Terminate = "Terminate";
    public const string NotifyAndHold = "NotifyAndHold";

    public const string VehicleUnavailable = "VehicleUnavailable";
    public const string RouteUnavailable = "RouteUnavailable";
    public const string PlcSignalNotReady = "PlcSignalNotReady";
    public const string TrafficResourceOccupied = "TrafficResourceOccupied";
    public const string HandshakeFaulted = "HandshakeFaulted";
    public const string Unclassified = "Unclassified";

    public static string NormalizeFailurePolicy(string? value) => value switch
    {
        Pause => Pause,
        ManualRecovery => ManualRecovery,
        Terminate => Terminate,
        _ => HoldUntilCondition
    };

    public static string NormalizeTimeoutAction(string? value) => value switch
    {
        Pause => Pause,
        ManualRecovery => ManualRecovery,
        Terminate => Terminate,
        _ => NotifyAndHold
    };

    public static bool StopsAutomation(string? policy) =>
        NormalizeFailurePolicy(policy) is Pause or ManualRecovery or Terminate;

    public static bool StopsAutomationForTimeout(string? action) =>
        NormalizeTimeoutAction(action) is Pause or ManualRecovery or Terminate;

    public static bool HasTimedOut(DateTimeOffset startedAt, int timeoutSeconds, DateTimeOffset now) =>
        timeoutSeconds > 0 && now >= startedAt.AddSeconds(timeoutSeconds);

    public static string ClassifyFailure(string? reason)
    {
        var text = reason ?? "";
        if (ContainsAny(text, "握手", "28阶段", "checkpoint", "PLC周期"))
            return HandshakeFaulted;
        if (ContainsAny(text, "车辆", "AGV", "车端", "定位", "通信状态"))
            return VehicleUnavailable;
        if (ContainsAny(text, "路权", "交通", "候车位", "资源", "独占", "占用"))
            return TrafficResourceOccupied;
        if (ContainsAny(text, "路线", "路径", "可达", "地图", "导航路线"))
            return RouteUnavailable;
        if (ContainsAny(text, "PLC", "信号", "质量", "回读", "条件"))
            return PlcSignalNotReady;
        return Unclassified;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}
