namespace AgvDispatch.Service;

public sealed class ObstacleRecoveryOptions
{
    public const string SectionName = "ObstacleRecovery";

    public bool Enabled { get; set; } = true;
    public bool AutomaticDetourEnabled { get; set; } = true;
    public int PollIntervalMs { get; set; } = 1000;
    public int DetourAfterSeconds { get; set; } = 5;
    public int WarningAfterSeconds { get; set; } = 10;
    public int AlarmAfterSeconds { get; set; } = 30;
    public int ClosureSuggestionAfterSeconds { get; set; } = 60;
    public int StableStopObservationMs { get; set; } = 500;
    public double StoppedVelocityToleranceMetersPerSecond { get; set; } = 0.02;
    public double MinimumLocalizationConfidence { get; set; } = 0.5;

    public void Validate()
    {
        if (PollIntervalMs is < 250 or > 60000)
            throw new InvalidOperationException("ObstacleRecovery:PollIntervalMs必须在250至60000之间。");
        if (DetourAfterSeconds is < 1 or > 300)
            throw new InvalidOperationException("ObstacleRecovery:DetourAfterSeconds必须在1至300秒之间。");
        if (WarningAfterSeconds < DetourAfterSeconds ||
            AlarmAfterSeconds <= WarningAfterSeconds ||
            ClosureSuggestionAfterSeconds <= AlarmAfterSeconds)
            throw new InvalidOperationException("障碍恢复升级阈值必须依次为改道、预警、报警、建议封路。");
        if (StableStopObservationMs is < 250 or > 10000)
            throw new InvalidOperationException("ObstacleRecovery:StableStopObservationMs必须在250至10000毫秒之间。");
        if (StoppedVelocityToleranceMetersPerSecond is <= 0 or > 0.2)
            throw new InvalidOperationException("ObstacleRecovery:StoppedVelocityToleranceMetersPerSecond必须在0至0.2之间。");
        if (MinimumLocalizationConfidence is < 0 or > 1)
            throw new InvalidOperationException("ObstacleRecovery:MinimumLocalizationConfidence必须在0至1之间。");
    }
}
