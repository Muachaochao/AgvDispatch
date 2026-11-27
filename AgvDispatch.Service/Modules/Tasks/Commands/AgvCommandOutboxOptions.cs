namespace AgvDispatch.Service;

public sealed class AgvCommandOutboxOptions
{
    public const string SectionName = "AgvCommandOutbox";

    public bool Enabled { get; set; } = true;
    public int PollIntervalMs { get; set; } = 500;
    public int RetryBaseDelayMs { get; set; } = 3000;
    public int MaximumRetryDelayMs { get; set; } = 60000;
    public int MaximumAttempts { get; set; } = 10;
    public int SendingLeaseMs { get; set; } = 15000;
    public int BatchSize { get; set; } = 10;
    public int MaximumConcurrentVehicles { get; set; } = 4;

    public string? GetConfigurationError()
    {
        if (!Enabled)
        {
            return null;
        }

        if (PollIntervalMs < 100 || RetryBaseDelayMs < 100 ||
            MaximumRetryDelayMs < RetryBaseDelayMs || SendingLeaseMs < 1000)
        {
            return "AGV命令Outbox的轮询、重试或租约时间配置无效。";
        }

        if (MaximumAttempts < 1 || BatchSize < 1)
        {
            return "AGV命令Outbox的MaximumAttempts和BatchSize必须大于0。";
        }

        if (MaximumConcurrentVehicles is < 1 or > 32)
        {
            return "AGV命令Outbox的MaximumConcurrentVehicles必须在1到32之间。";
        }

        return null;
    }
}
