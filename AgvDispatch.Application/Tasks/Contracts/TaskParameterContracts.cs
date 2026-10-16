using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Application.Abstractions;

public sealed record TaskPickupParameter(string StationId, string PickupSite);
public sealed record TaskActionParameter(DispatchTaskStepType StepType, string Operation, double? JackHeight,
    bool? Recognize, bool? UsePgv, bool? UseDownPgv);
public sealed record TaskStepTimeoutParameter(DispatchTaskStepType StepType, int Seconds);
public sealed record TaskParameterConfiguration(string DumperStationId, string WaitingSite, string DumpingSite,
    IReadOnlyList<TaskPickupParameter> Pickups, IReadOnlyList<TaskActionParameter> Actions)
{
    // Older saved profiles omit this property and keep timeout prompts disabled.
    public IReadOnlyList<TaskStepTimeoutParameter> StepTimeouts { get; init; } =
        Enum.GetValues<DispatchTaskStepType>().Select(type => new TaskStepTimeoutParameter(type, 0)).ToArray();
}
public sealed record TaskParameterAudit(DateTimeOffset At, string Operator, string Revision);
public sealed record TaskParameterSnapshot(string Revision, TaskParameterConfiguration Configuration,
    string? MapId, string? MapVersionId, bool CanSave, string Explanation, IReadOnlyList<TaskParameterAudit> Audits);
public sealed record TaskParameterRequest(string RequestId, string ExpectedRevision, string MapId, string MapVersionId,
    TaskParameterConfiguration Configuration);
public sealed record TaskParameterValidationRow(string StationId, ManualTaskStepPreview Step);
public sealed record TaskParameterResult(bool Accepted, string Message, IReadOnlyList<TaskParameterValidationRow> Steps,
    TaskParameterSnapshot? Snapshot = null);
public interface ITaskParameterService
{
    Task<TaskParameterSnapshot> GetAsync(CancellationToken ct);
    Task<TaskParameterResult> ValidateAsync(TaskParameterRequest request, CancellationToken ct);
    Task<TaskParameterResult> SaveAsync(TrafficUserContext user, TaskParameterRequest request, CancellationToken ct);
}
