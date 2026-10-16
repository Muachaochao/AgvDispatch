namespace AgvDispatch.Service;

public sealed class DispatchTaskStepOptions
{
    public const string SectionName = "DispatchTaskSteps";

    public bool Enabled { get; set; } = true;
    public int PollIntervalMs { get; set; } = 500;
    public int BatchSize { get; set; } = 100;
}
