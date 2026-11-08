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
public sealed record TaskTemplatePortBinding(string Key, string Label, string Type, bool Required, string Source);
public sealed record TaskTemplateVariableBinding(string Key, string Label, string Type, string Producer);
public sealed record TaskTemplateNodeBinding(string Id, string Block, string Name, IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs, string Condition, string FailurePolicy, string TimeoutAction, int TimeoutSeconds, bool AutoRetry);
public sealed record TaskTemplateConnectionBinding(string FromNodeId, string Output, string ToNodeId, string Input, string Type, bool External);
public sealed record TaskTemplateGuardBinding(string Id, string Trigger, string Action, string TargetNodeId, string Description);
public sealed record TaskTemplateBinding(string Id, int Version, string ContentHash, string Engine,
    IReadOnlyList<TaskTemplatePortBinding> Inputs, IReadOnlyList<TaskTemplatePortBinding> Outputs,
    IReadOnlyList<TaskTemplateVariableBinding> Variables, IReadOnlyList<TaskTemplateNodeBinding> Nodes,
    IReadOnlyList<TaskTemplateConnectionBinding> Connections, IReadOnlyList<TaskTemplateGuardBinding> Guards);
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
    public TaskTemplateBinding? TaskTemplate { get; init; }
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
    Task<TaskParameterResult> SaveEngineeringBundleAsync(TrafficUserContext user, TaskParameterRequest request, CancellationToken ct)
        => throw new NotSupportedException("当前后台不支持工程包地图切换后的参数提交，请更新后台。");
}
