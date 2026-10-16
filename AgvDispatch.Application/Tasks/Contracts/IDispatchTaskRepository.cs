using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Application.Abstractions;

public interface IDispatchTaskRepository
{
    Task<TaskCreationResult> TryCreateAsync(
        string stationId,
        string dumperStationId,
        int priority,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DispatchTaskStepSnapshot>> GetStepsAsync(
        string taskId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ActiveDispatchTask>> GetPendingAcknowledgementsAsync(
        CancellationToken cancellationToken);

    Task MarkAcknowledgedAsync(string taskId, CancellationToken cancellationToken);

    Task RecordAcknowledgementFailureAsync(
        string taskId,
        string errorMessage,
        CancellationToken cancellationToken);
}
