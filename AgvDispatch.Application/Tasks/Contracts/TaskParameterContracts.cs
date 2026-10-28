using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Application.Abstractions;

public sealed record TaskPickupParameter(string StationId, string PickupSite)
{
    // Null retains legacy unrestricted selection. An explicit list is a strict whitelist.
    public string? VehiclePoolId { get; init; }
    public IReadOnlyList<string>? AllowedVehicleIds { get; init; }
}
public sealed record TaskActionParameter(DispatchTaskStepType StepType, string Operation, double? JackHeight,
    bool? Recognize, bool? UsePgv, bool? UseDownPgv);
public sealed record TaskStepTimeoutParameter(DispatchTaskStepType StepType, int Seconds);
public sealed record TaskStandbyParameter(string Id, string Site, string VehiclePoolId, IReadOnlyList<string> AllowedVehicleIds, int Priority = 50);
public sealed record TaskParameterConfiguration(string DumperStationId, string WaitingSite, string DumpingSite,
    IReadOnlyList<TaskPickupParameter> Pickups, IReadOnlyList<TaskActionParameter> Actions)
{
    // Empty preserves the historic single-bay configuration. WaitingSite is the first/default bay.
    public IReadOnlyList<string> WaitingSites { get; init; } = [];
    // Older saved profiles omit this property and keep timeout prompts disabled.
    public IReadOnlyList<TaskStepTimeoutParameter> StepTimeouts { get; init; } =
        Enum.GetValues<DispatchTaskStepType>().Select(type => new TaskStepTimeoutParameter(type, 0)).ToArray();
    public EngineeringRuntimeSource? EngineeringSource { get; init; }
    public IReadOnlyList<TaskStandbyParameter> Standbys { get; init; } = [];
}
public sealed record EngineeringRuntimeSource(string VersionId, string ContentHash, string MapDefinitionHash);
public sealed record TaskParameterAudit(DateTimeOffset At, string Operator, string Revision);
public sealed record TaskParameterSnapshot(string Revision, TaskParameterConfiguration Configuration,
    string? MapId, string? MapVersionId, bool CanSave, string Explanation, IReadOnlyList<TaskParameterAudit> Audits)
{
    public string? SavedRequestId { get; init; }
}
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
