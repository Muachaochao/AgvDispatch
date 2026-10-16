using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Application.Abstractions;

public interface IDispatchOrchestrationRepository
{
    Task<IReadOnlyList<DispatchTaskWorkItem>> GetDispatchCandidatesAsync(
        int maximumCount,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DispatchTaskWorkItem>> GetSubmittingTasksAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DispatchTaskWorkItem>> GetExecutingTasksAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DispatchTaskWorkItem>> GetAssignedTasksAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DispatchTaskStepWorkItem>> GetReadyStepsAsync(
        int maximumCount,
        CancellationToken cancellationToken);

    Task<bool> TryClaimForSubmissionAsync(
        string taskId,
        string vehicleId,
        CancellationToken cancellationToken);

    Task MarkSubmittedAsync(
        string taskId,
        string vendorTaskId,
        string? vehicleId,
        CancellationToken cancellationToken);

    Task AdvanceStageAsync(
        string taskId,
        DispatchTaskStage targetStage,
        string? vehicleId,
        string? message,
        CancellationToken cancellationToken);

    Task RecordSubmissionFailureAsync(
        string taskId,
        string errorMessage,
        CancellationToken cancellationToken);

    Task CompleteExecutingStepAsync(
        string taskId,
        string vendorTaskId,
        CancellationToken cancellationToken);

    Task FailExecutingStepAsync(
        string taskId,
        string vendorTaskId,
        DispatchTaskStage terminalStage,
        string? errorMessage,
        CancellationToken cancellationToken);

    Task<bool> CompleteReadyStepAsync(
        string taskId,
        string stepId,
        CancellationToken cancellationToken);

    Task RecordReadyStepFailureAsync(
        string taskId,
        string stepId,
        string errorMessage,
        CancellationToken cancellationToken);
}
