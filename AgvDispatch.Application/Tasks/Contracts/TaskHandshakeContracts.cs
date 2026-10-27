using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Application.Tasks;

namespace AgvDispatch.Application.Abstractions;

public sealed record HandshakeCheckpoint(string TaskId, string StepId, string CycleId, string ContractVersion,
    string Phase, int Revision, string Reason, string? PendingSignal, bool? PendingValue, string? NextPhase,
    DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, IReadOnlyList<TaskSignalEvidence> Baseline);
public sealed record TaskSignalEvidence(string StationId, string VariableName, bool? Value, string Quality,
    DateTimeOffset? ObservedAt, long? Version, string Address);
public sealed record TaskRuntimeEvent(long Sequence, string TaskId, string StepId, string Kind, string Phase,
    string StationId, string Signal, bool? PreviousValue, bool? Value, string Quality, DateTimeOffset? ObservedAt,
    DateTimeOffset RecordedAt, string Reason)
{
    public string Evidence { get; init; } = "";
}
public sealed record TaskExitEvidence(string TaskId,string StepId,string VehicleId,string TransportStepId,string VendorTaskId,
    string VendorStation,string LocalStation,string MapId,string MapVersion,DateTimeOffset VehicleObservedAt,DateTimeOffset CapturedAt);
public sealed record TaskRuntimeQuery(string TaskId, string Kind = "", string StepId = "", string Signal = "",
    DateTimeOffset? From = null, DateTimeOffset? To = null, int Page = 1, int PageSize = 50, long? ThroughSequence = null);
public sealed record TaskRuntimePage(IReadOnlyList<TaskRuntimeEvent> Items, int TotalCount, int Page, int PageSize, long ThroughSequence);
public static class TaskRuntimeQueryValidation
{
    public static void Validate(TaskRuntimeQuery q)
    {
        if(q.Kind==null||q.StepId==null||q.Signal==null||string.IsNullOrWhiteSpace(q.TaskId)||q.TaskId.Length>64||q.Page<1||q.Page>100000||q.PageSize<1||q.PageSize>100||q.ThroughSequence<0||q.Kind.Length>32||q.StepId.Length>64||q.Signal.Length>128||q.From>q.To)
            throw new ArgumentException("事件筛选参数无效。");
        if(q.Kind is not ("" or "Signals" or "HandshakeStarted" or "HandshakeTransition" or "HandshakeReviewed" or "SignalInitial" or "SignalChanged" or "EquipmentClaimed" or "EquipmentReleased" or "ExitVerified"))
            throw new ArgumentException("事件类型无效。");
    }
}
public sealed record TaskHandoverDiagnosis(string State, string Reason, bool ReturnCommandCompleted, bool ExitPhysicallyVerified,
    IReadOnlyList<string> HeldResources, IReadOnlyList<string> RecordedReleaseReasons);
public sealed record TaskDiagnosticArchive(string FileName, string Sha256, string Base64, int EventCount, long ThroughSequence);
public interface ITaskDiagnosticArchiveService
{
    Task<TaskDiagnosticArchive> ExportAsync(string taskId,CancellationToken ct);
}

public interface ITaskHandshakeStore
{
    Task<bool> OwnsEquipmentAsync(string taskId,string equipmentId,CancellationToken ct) => Task.FromResult(false);
    Task ValidatePhysicalExitAsync(HandshakeCheckpoint checkpoint, TaskDumperExitProof evidence, CancellationToken ct)
        => throw new NotSupportedException("当前存储不支持28阶段出区证据复核。");
    Task<bool> RecordPhysicalExitAsync(HandshakeCheckpoint checkpoint, TaskDumperExitProof evidence, CancellationToken ct)
        => throw new NotSupportedException("当前存储不支持28阶段实际出区证据。");
    Task<bool> ReleaseDumperAfterExitAsync(HandshakeCheckpoint checkpoint, string equipmentId, TaskDumperExitProof evidence, CancellationToken ct)
        => throw new NotSupportedException("当前存储不支持28阶段设备交接。");
    Task<HandshakeCheckpoint> BeginAsync(DispatchTaskStepWorkItem step, string contractVersion, string initialPhase,
        IReadOnlyList<TaskSignalEvidence> baseline, CancellationToken ct);
    Task<HandshakeCheckpoint?> GetAsync(string taskId, string stepId, CancellationToken ct);
    Task<bool> MoveAsync(HandshakeCheckpoint expected, string phase, string reason, string? pendingSignal,
        bool? pendingValue, string? nextPhase, CancellationToken ct);
    Task RecordSignalsAsync(string taskId, string stepId, IReadOnlyList<TaskSignalEvidence> signals, CancellationToken ct);
    Task<TaskRuntimePage> QueryAsync(TaskRuntimeQuery query, CancellationToken ct);
    Task<string?> TryClaimEquipmentAsync(string taskId,string stepId,string equipmentId,CancellationToken ct);
    Task ReleaseEquipmentAsync(string taskId,CancellationToken ct);
    Task<bool> RecordExitAsync(HandshakeCheckpoint checkpoint,TaskExitEvidence evidence,CancellationToken ct);
}

public sealed class HandshakeExecutionOptions
{
    // New PLC timing must be commissioned with its matching PLC program; never silently migrate a live old task.
    public bool Enabled { get; set; }
    public string PlcContractVersion { get; set; } = "";
    public int StateMaxAgeSeconds { get; set; } = 5;
    public DumperExitConfiguration DumperExit { get; set; } = new();
    public const string Version = "plc-28-stage-v2";
    public const string LegacyVersion = "sender-owned-return-confirmed-v1";
}

public sealed class DumperExitConfiguration
{
    public string[] ResourceIds { get; set; } = [];
    public List<DumperExitBodyProfile> BodyProfiles { get; set; } = [];
    public double MinimumObservationIntervalSeconds { get; set; } = 0.2;
    public double MaximumAgeSeconds { get; set; } = 3;
    public double MinimumConfidence { get; set; } = 0.9;
    public double SafetyMarginMeters { get; set; } = 0.15;
    public double MaximumRouteDeviationMeters { get; set; } = 0.5;
    public DumperExitPolicy Policy => new(ResourceIds, MinimumObservationIntervalSeconds, MaximumAgeSeconds, MinimumConfidence, SafetyMarginMeters);
}

public sealed record TaskDumperExitProof(string TaskId, string StepId, string VehicleId, string VendorTaskId,
    string MapId, string MapVersionId, string MapDefinitionHash, DumperExitFrame First, DumperExitFrame Second,
    DumperExitBodyProfile Body, DumperExitPolicy Policy, double MaximumRouteDeviationMeters,
    DateTimeOffset VendorObservedAt, DateTimeOffset CapturedAt)
{
    public IReadOnlyList<TaskSignalEvidence> PlcSignals { get; init; } = [];
    public bool EndpointArrivalConfirmed { get; init; }
}
