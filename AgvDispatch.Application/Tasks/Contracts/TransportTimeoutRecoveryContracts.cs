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
}
