namespace AgvDispatch.Application.Abstractions;
public enum TrafficWaitingAlertLevel { None, Normal, Warning, Critical, Unknown, ClockAnomaly }
public sealed class TrafficWaitingAlertOptions
{
    public int WarningSeconds { get; set; } = 60;
    public int CriticalSeconds { get; set; } = 300;
    public void Validate()
    {
        if (WarningSeconds < 1 || CriticalSeconds <= WarningSeconds || CriticalSeconds > 604800)
            throw new ArgumentException("TrafficWaitingAlerts阈值须为正整数，严重阈值大于预警阈值且最多7天。");
    }
    public TrafficWaitingAlertLevel Evaluate(string state, double? seconds, bool clockValid)
    {
        Validate();
        if (state != "Waiting") return TrafficWaitingAlertLevel.None;
        if (!clockValid) return TrafficWaitingAlertLevel.ClockAnomaly;
        if (seconds is not { } value || !double.IsFinite(value) || value < 0) return TrafficWaitingAlertLevel.Unknown;
        return value >= CriticalSeconds ? TrafficWaitingAlertLevel.Critical : value >= WarningSeconds ? TrafficWaitingAlertLevel.Warning : TrafficWaitingAlertLevel.Normal;
    }
}
