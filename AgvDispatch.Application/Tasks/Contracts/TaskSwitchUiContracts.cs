namespace AgvDispatch.Application.Abstractions;

public sealed record TaskSwitchOptions(bool Enabled, bool Eligible, string Message, string? VehicleId,
    IReadOnlyList<ManualTaskStationChoice> Stations, string[] Blockers, TaskSwitchRecord? Current)
{
    public string ModeHint { get; init; } = TaskSwitchModes.Auto;
    public string ModeDescription { get; init; } = "";
}
public interface ITaskSwitchQueries
{
    Task<TaskSwitchOptions> GetOptionsAsync(string taskId, CancellationToken ct);
}
