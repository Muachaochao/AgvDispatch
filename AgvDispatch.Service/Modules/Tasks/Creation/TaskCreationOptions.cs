namespace AgvDispatch.Service;

public sealed class DispatchTaskCreationOptions
{
    public const string SectionName = "TaskCreation";

    public bool Enabled { get; set; } = true;
    // Manual tasks still need acknowledgement processing when automatic calls are disabled.
    public bool AutomaticCreationEnabled { get; set; } = true;
    public int DefaultPriority { get; set; } = 0;
    public Dictionary<string, int> StationPriorities { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int PriorityFor(string stationId) => StationPriorities.TryGetValue(stationId, out var priority)
        ? priority : DefaultPriority;
    public int AcknowledgementRetryIntervalMs { get; set; } = 3000;
    public string DumperStationId { get; set; } = "DUMPER-001";

    public string? GetConfigurationError()
    {
        if (DefaultPriority is < 0 or > 100)
            return "TaskCreation:DefaultPriority必须在0到100之间。";
        if (StationPriorities.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Value is < 0 or > 100))
            return "TaskCreation:StationPriorities中的工位不能为空，优先级必须在0到100之间。";
        if (AcknowledgementRetryIntervalMs is < 100 or > 60000)
            return "TaskCreation:AcknowledgementRetryIntervalMs必须在100到60000之间。";
        if (Enabled && string.IsNullOrWhiteSpace(DumperStationId))
            return "TaskCreation:DumperStationId不能为空。";
        return null;
    }
}
