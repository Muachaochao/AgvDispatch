namespace AgvDispatch.Service;

public sealed class AlarmOptions
{
    public const string SectionName = "Alarm";
    public bool Enabled { get; set; } = true;
    public int EvaluationIntervalMs { get; set; } = 2000;
    public decimal LowBatteryWarningPercent { get; set; } = 30;
    public decimal LowBatteryCriticalPercent { get; set; } = 15;
    public int CriticalRecoveryHoldSeconds { get; set; } = 30;
}
