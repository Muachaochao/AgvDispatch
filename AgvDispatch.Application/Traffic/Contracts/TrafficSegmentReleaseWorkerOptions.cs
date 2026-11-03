namespace AgvDispatch.Application.Abstractions;

/// <summary>
/// Background observation and release cadence. Observation and the final
/// database commit remain separately gated by TrafficSegmentReleasePolicy
/// and TrafficSegmentCommitOptions.
/// </summary>
public sealed class TrafficSegmentReleaseWorkerOptions
{
    public bool Enabled { get; set; }
    public int PollIntervalMs { get; set; } = 750;
    public int MaximumStepsPerCycle { get; set; } = 32;
    public double FrameWaitSeconds { get; set; } = 4;

    public void Validate()
    {
        if (PollIntervalMs is < 250 or > 10_000)
            throw new InvalidOperationException("TrafficSegmentRelease:PollIntervalMs 必须在250～10000之间。");
        if (MaximumStepsPerCycle is < 1 or > 200)
            throw new InvalidOperationException("TrafficSegmentRelease:MaximumStepsPerCycle 必须在1～200之间。");
        if (!double.IsFinite(FrameWaitSeconds) || FrameWaitSeconds is < 0.5 or > 5)
            throw new InvalidOperationException("TrafficSegmentRelease:FrameWaitSeconds 必须在0.5～5秒之间。");
    }
}
