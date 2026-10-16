using AgvDispatch.Domain.Tasks;
namespace AgvDispatch.Application.Abstractions;
public sealed record TaskCancelRequest(string RequestId, string TaskId, DispatchTaskStage ExpectedStage, string Reason);
public sealed record TaskCancelResult(bool Accepted, string Message);
public sealed record TaskRecoveryReviewRequest(string TaskId, string Reason);
public interface ITaskCancellationService
{
    Task<TaskCancelResult> RequestAsync(TrafficUserContext user, TaskCancelRequest request, CancellationToken ct);
    Task ProcessAsync(CancellationToken ct);
}
