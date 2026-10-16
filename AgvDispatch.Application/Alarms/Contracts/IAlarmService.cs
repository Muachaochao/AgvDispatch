namespace AgvDispatch.Application.Abstractions;

public enum AlarmSeverity
{
    Info = 0,
    Warning = 10,
    Error = 20,
    Critical = 30
}

public enum AlarmLifecycleStatus
{
    Open = 0,
    Acknowledged = 10,
    Recovered = 20
}

public sealed record AlarmCandidate(
    string RuleKey,
    string SourceType,
    string SourceId,
    string Code,
    string Title,
    string Message,
    AlarmSeverity Severity);

public sealed record AlarmSnapshot(
    string AlarmId,
    string RuleKey,
    string SourceType,
    string SourceId,
    string Code,
    string Title,
    string Message,
    AlarmSeverity Severity,
    AlarmLifecycleStatus Status,
    DateTimeOffset FirstOccurredAt,
    DateTimeOffset LastObservedAt,
    string? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAt,
    string? AcknowledgeComment,
    DateTimeOffset? RecoveredAt,
    string? RecoveryMessage);

public sealed record AcknowledgeAlarmRequest(string AlarmId, string Comment);
public sealed record AlarmOperationResult(bool Accepted, string Message);

public interface IAlarmService
{
    Task SynchronizeAsync(
        IReadOnlyCollection<AlarmCandidate> candidates,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AlarmSnapshot>> GetRecentAsync(
        int maximumCount,
        bool activeOnly,
        CancellationToken cancellationToken);

    Task<AlarmOperationResult> AcknowledgeAsync(
        TrafficUserContext user,
        AcknowledgeAlarmRequest request,
        CancellationToken cancellationToken);
}
