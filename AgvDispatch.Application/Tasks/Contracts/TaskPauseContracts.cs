using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Application.Abstractions;

public sealed record TaskPauseRequest(string RequestId, string TaskId, DispatchTaskStage ExpectedStage, string Reason);
public sealed record TaskPauseResumeRequest(string RequestId, string TaskId, string Reason);
public sealed record TaskPauseResult(bool Accepted, string Message);

public interface ITaskPauseService
{
    Task<TaskPauseResult> PauseAsync(TrafficUserContext user, TaskPauseRequest request, CancellationToken ct);
    Task<TaskPauseResult> ResumeAsync(TrafficUserContext user, TaskPauseResumeRequest request, CancellationToken ct);
}
