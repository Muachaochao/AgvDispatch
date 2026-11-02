using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Application.Abstractions;

public static class TaskSwitchStages
{
    public const string Requested = "Requested", AwaitingStop = "AwaitingStop", ClosingOriginal = "ClosingOriginal",
        ReadyToSwitch = "ReadyToSwitch", SubmittingReplacement = "SubmittingReplacement", Completed = "Completed",
        ReviewHold = "ReviewHold", Abandoned = "Abandoned";
    public static bool IsTerminal(string stage) => stage is Completed or Abandoned;
}

public sealed record TaskSwitchRequest(string RequestId, string TaskId, DispatchTaskStage ExpectedStage,
    string TargetStationId, int Priority, string Reason, bool SiteConfirmed);
public sealed record TaskSwitchResumeRequest(string SwitchId, long ExpectedRevision, string Reason, bool SiteConfirmed);
public sealed record TaskSwitchRecord(string SwitchId, string SourceTaskId, string VehicleId, string TargetStationId,
    string? TargetTaskId, string Stage, string Message, string[] Blockers, long Revision, DateTimeOffset UpdatedAt,
    string MapId, string MapVersionId, string ParameterRevision, string ReplacementRequestId, string CancelRequestId,
    TaskSwitchRequest Request, string Operator)
{
    public string? ResumeStage { get; init; }
    public bool ReplacementCreateAttempted { get; init; }
}
public sealed record TaskSwitchResult(bool Accepted, string Message, TaskSwitchRecord? Current);
public sealed record TaskSwitchInspection(string VehicleId, string MapId, string MapVersionId,
    string ParameterRevision, string[] Blockers);
public sealed record TaskSwitchClosureObservation(bool CancelConfirmed, bool OriginalClosed, string MapId,
    string MapVersionId, string ParameterRevision, string[] Blockers)
{
    public bool CanCloseOriginal { get; init; }
}

// Inspect/observe/find are read-only. Each mutating port call must check the pinned map and original ownership
// under the execution gate. Cancellation and creation use the persisted request IDs across every retry/restart.
public interface ITaskSwitchPort
{
    Task<TaskSwitchInspection> InspectSourceAsync(TaskSwitchRequest request, CancellationToken ct);
    Task RequestCancelAsync(TaskSwitchRecord session, CancellationToken ct);
    Task<TaskSwitchClosureObservation> ObserveCloseAsync(TaskSwitchRecord session, CancellationToken ct);
    Task CloseOriginalAsync(TaskSwitchRecord session, CancellationToken ct);
    Task<string> CreateReplacementAsync(TaskSwitchRecord session, CancellationToken ct);
    Task<string?> FindReplacementAsync(TaskSwitchRecord session, CancellationToken ct);
}

public interface ITaskSwitchService
{
    Task InitializeAsync(CancellationToken ct);
    Task<TaskSwitchRecord?> GetAsync(string switchId, CancellationToken ct);
    Task<IReadOnlyList<TaskSwitchRecord>> ListAsync(CancellationToken ct);
    Task<TaskSwitchResult> RequestAsync(TrafficUserContext user, TaskSwitchRequest request, CancellationToken ct);
    Task<TaskSwitchResult> ResumeAsync(TrafficUserContext user, TaskSwitchResumeRequest request, CancellationToken ct);
    Task<TaskSwitchResult> AbandonAsync(TrafficUserContext user, TaskSwitchResumeRequest request, CancellationToken ct);
    Task ProcessAsync(CancellationToken ct);
}

// The durable vehicle reservation also covers ReviewHold and restart reconciliation.
public interface ITaskSwitchGuard
{
    Task<bool> AllowsVehicleAsync(string vehicleId, string? taskId, string? requestId, CancellationToken ct);
}
