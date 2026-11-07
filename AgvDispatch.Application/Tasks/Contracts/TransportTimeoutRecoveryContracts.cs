using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Application.Abstractions;

public enum TransportTimeoutRecoveryOutcome
{
    AwaitingObstacleClear = 10,
    SafetyHold = 20,
    ManualRelocationRequired = 30,
    ManualIntervention = 40,
    ReadyForControlledDetour = 50
}

public sealed record TransportTimeoutRecoveryAssessment(
    TransportTimeoutRecoveryOutcome Outcome,
    DispatchTaskStepType StepType,
    string VehicleId,
    string OriginalVendorTaskId,
    string? CurrentStation,
    string DestinationSite,
    IReadOnlyList<string> OriginalPath,
    IReadOnlyList<string> CandidatePath,
    string Message);

public sealed record TransportTimeoutRecoverySnapshot(
    string RecoveryId,
    string TaskId,
    string StepId,
    string VehicleId,
    DispatchTaskStepType StepType,
    string State,
    string Outcome,
    string OriginalVendorTaskId,
    string? NewVendorTaskId,
    string? CurrentStation,
    string DestinationSite,
    IReadOnlyList<string> OriginalPath,
    IReadOnlyList<string> CandidatePath,
    string MapId,
    string MapVersionId,
    string DefinitionHash,
    string PreviewHash,
    bool SendAttempted,
    string Message,
    int Revision,
    DateTimeOffset UpdatedAt);

public sealed record TransportTimeoutRecoveryRequest(
    string TaskId,
    int ExpectedRevision,
    bool ExecuteConfirmed,
    bool VehicleAndLocationConfirmed,
    bool LoadAndOwnershipConfirmed,
    bool PlcConditionsConfirmed,
    string Reason);

public sealed record TransportTimeoutRecoveryQuery(string TaskId);

public sealed record TransportTimeoutRecoveryResult(
    bool Accepted,
    string Message,
    TransportTimeoutRecoverySnapshot? Snapshot = null);

/// <summary>
/// Captures a confirmed Robokit navigation timeout as an explicit business
/// recovery state. It never sends cancellation, navigation, or PLC output.
/// </summary>
public interface ITransportTimeoutRecoveryService
{
    Task<TransportTimeoutRecoveryAssessment?> CaptureAsync(
        DispatchTaskWorkItem task,
        AgvTransportTaskStatus status,
        CancellationToken cancellationToken);

    Task<TransportTimeoutRecoverySnapshot?> GetAsync(string taskId, CancellationToken cancellationToken);

    Task<TransportTimeoutRecoveryResult> ExecuteAsync(
        TrafficUserContext user,
        TransportTimeoutRecoveryRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Re-evaluates unresolved timeout holds after fresh telemetry or a
    /// controlled relocation. This only updates read-only eligibility and
    /// never sends a navigation command or releases the original task.
    /// </summary>
    Task ReassessPendingAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task<AgvTransportTaskStatus?> GetReplacementStatusAsync(
        DispatchTaskWorkItem task,
        CancellationToken cancellationToken);

    /// <summary>Read-only revalidation shared by automatic obstacle recovery.
    /// Returns a Chinese reason when load, PLC, handshake or step identity is not
    /// safe for a replacement navigation; null means the evidence passed.</summary>
    Task<string?> ValidateReplacementPreconditionsAsync(
        string taskId,
        string stepId,
        CancellationToken cancellationToken) =>
        Task.FromResult<string?>("当前后台未提供自动改道前置条件核验。");
}
