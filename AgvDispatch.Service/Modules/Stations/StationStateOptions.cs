namespace AgvDispatch.Service;

public sealed class StationStateOptions
{
    public const string SectionName = "StationState";

    public int RefreshIntervalMs { get; set; } = 2000;
}
