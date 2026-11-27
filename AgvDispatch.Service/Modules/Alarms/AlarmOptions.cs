namespace AgvDispatch.Service;

public sealed class AlarmOptions
{
    public const string SectionName = "Alarm";
    public bool Enabled { get; set; } = true;
    public int EvaluationIntervalMs { get; set; } = 2000;
    public decimal LowBatteryWarningPercent { get; set; } = 30;
    public decimal LowBatteryCriticalPercent { get; set; } = 15;
    public int CriticalRecoveryHoldSeconds { get; set; } = 30;

    public string? GetConfigurationError()
    {
        if (EvaluationIntervalMs is < 100 or > 60000)
            return "Alarm:EvaluationIntervalMs必须在100到60000之间。";
        if (LowBatteryCriticalPercent is < 0 or > 100 ||
            LowBatteryWarningPercent is < 0 or > 100 ||
            LowBatteryCriticalPercent > LowBatteryWarningPercent)
            return "Alarm低电阈值必须满足0≤严重阈值≤预警阈值≤100。";
        if (CriticalRecoveryHoldSeconds is < 0 or > 604800)
            return "Alarm:CriticalRecoveryHoldSeconds必须在0到604800之间。";
        return null;
    }
}
