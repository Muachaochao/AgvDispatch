namespace AgvDispatch.Service;

public sealed class ObstacleRecoveryOptions
{
    public const string SectionName = "ObstacleRecovery";

    public bool Enabled { get; set; } = true;
    public bool AutomaticDetourEnabled { get; set; } = true;
    // 3051 SELF_POSITION is documented for isolated/test use and warns against
    // ordinary scheduling use. Keep the production default off until a vehicle
    // and route profile have been signed off on site.
    public bool AutomaticSafetyPointRetreatEnabled { get; set; }
    public int PollIntervalMs { get; set; } = 1000;
    // DetourAfterSeconds starts read-only assessment, never cancellation.
    // These are observation thresholds, not a vendor task-release SLA.
    public int DetourAfterSeconds { get; set; } = 30;
    public int CancelAfterSeconds { get; set; } = 120;
    // Measured from the persisted cancellation intent; reminder only, no retry.
    public int CancelTerminalWaitWarningSeconds { get; set; } = 30;
    public int WarningAfterSeconds { get; set; } = 60;
    public int AlarmAfterSeconds { get; set; } = 120;
    public int ClosureSuggestionAfterSeconds { get; set; } = 300;
    public int StableStopObservationMs { get; set; } = 500;
    // Backoff after a send was refused before reaching the vehicle; doubles per refusal, capped at 60 s.
    public int SendRejectionRetrySeconds { get; set; } = 5;
    public double StoppedVelocityToleranceMetersPerSecond { get; set; } = 0.02;
    public double MinimumLocalizationConfidence { get; set; } = 0.5;
    public double MaximumStationDeviationMeters { get; set; } = 0.3;
    public double SafetyPointMaximumRetreatDistanceMeters { get; set; } = 5;
    public double SafetyPointRetreatMaximumSpeedMetersPerSecond { get; set; } = 0.1;
    public double SafetyPointRetreatMaximumAccelerationMetersPerSecondSquared { get; set; } = 0.15;

    public void Validate()
    {
        if (PollIntervalMs is < 250 or > 60000)
            throw new InvalidOperationException("ObstacleRecovery:PollIntervalMs必须在250至60000之间。");
        if (DetourAfterSeconds is < 1 or > 300)
            throw new InvalidOperationException("ObstacleRecovery:DetourAfterSeconds必须在1至300秒之间。");
        if (WarningAfterSeconds < DetourAfterSeconds ||
            AlarmAfterSeconds <= WarningAfterSeconds ||
            ClosureSuggestionAfterSeconds <= AlarmAfterSeconds)
            throw new InvalidOperationException("障碍恢复升级阈值必须依次为只读评估、预警、报警、建议封路。");
        if (CancelAfterSeconds < DetourAfterSeconds || CancelAfterSeconds > 3600)
            throw new InvalidOperationException("取消观察时间必须不小于只读评估时间且不超过3600秒。");
        if (CancelTerminalWaitWarningSeconds is < 1 or > 3600)
            throw new InvalidOperationException("取消终态等待提醒必须在1至3600秒之间；该时间不代表厂家释放时限。");
        if (StableStopObservationMs is < 250 or > 10000)
            throw new InvalidOperationException("ObstacleRecovery:StableStopObservationMs必须在250至10000毫秒之间。");
        if (SendRejectionRetrySeconds is < 1 or > 300)
            throw new InvalidOperationException("ObstacleRecovery:SendRejectionRetrySeconds必须在1至300秒之间。");
        if (StoppedVelocityToleranceMetersPerSecond is <= 0 or > 0.2)
            throw new InvalidOperationException("ObstacleRecovery:StoppedVelocityToleranceMetersPerSecond必须在0至0.2之间。");
        if (MinimumLocalizationConfidence is < 0 or > 1)
            throw new InvalidOperationException("ObstacleRecovery:MinimumLocalizationConfidence必须在0至1之间。");
        if (MaximumStationDeviationMeters is <= 0 or > 5)
            throw new InvalidOperationException("ObstacleRecovery:MaximumStationDeviationMeters必须在0至5米之间。");
        if (SafetyPointMaximumRetreatDistanceMeters is <= 0 or > 50)
            throw new InvalidOperationException("ObstacleRecovery:SafetyPointMaximumRetreatDistanceMeters必须在0至50米之间。");
        if (SafetyPointRetreatMaximumSpeedMetersPerSecond is <= 0 or > 0.3)
            throw new InvalidOperationException("ObstacleRecovery:SafetyPointRetreatMaximumSpeedMetersPerSecond必须在0至0.3m/s之间。");
        if (SafetyPointRetreatMaximumAccelerationMetersPerSecondSquared is <= 0 or > 1)
            throw new InvalidOperationException("ObstacleRecovery:SafetyPointRetreatMaximumAccelerationMetersPerSecondSquared必须在0至1m/s²之间。");
    }
}
