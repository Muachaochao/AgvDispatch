namespace AgvDispatch.Service;

public sealed class DispatchTaskStepOptions
{
    public const string SectionName = "DispatchTaskSteps";

    public bool Enabled { get; set; } = true;
    public int PollIntervalMs { get; set; } = 500;
    public int BatchSize { get; set; } = 100;
    // Reserve and dispatch one bounded route window at a time. The vehicle stops
    // at a configured node role before requesting the next window.
    public bool RollingReservationEnabled { get; set; } = true;
    public int RollingWindowMinimumEdges { get; set; } = 2;
    public int RollingWindowMaximumEdges { get; set; } = 12;
}
