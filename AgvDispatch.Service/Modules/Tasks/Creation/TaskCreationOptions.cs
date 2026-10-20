namespace AgvDispatch.Service;

public sealed class DispatchTaskCreationOptions
{
    public const string SectionName = "TaskCreation";

    public bool Enabled { get; set; } = true;
    // Manual tasks still need acknowledgement processing when automatic calls are disabled.
    public bool AutomaticCreationEnabled { get; set; } = true;
    public int DefaultPriority { get; set; } = 0;
    public int AcknowledgementRetryIntervalMs { get; set; } = 3000;
    public string DumperStationId { get; set; } = "DUMPER-001";
}
