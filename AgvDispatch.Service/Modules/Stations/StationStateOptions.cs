namespace AgvDispatch.Service;

public sealed class StationStateOptions
{
    public const string SectionName = "StationState";

    public int RefreshIntervalMs { get; set; } = 2000;

    public string? GetConfigurationError() =>
        RefreshIntervalMs is < 100 or > 60000
            ? "StationState:RefreshIntervalMs必须在100到60000之间。"
            : null;
}
