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

public sealed record AlarmSynchronizationPolicy(int CriticalRecoveryHoldSeconds = 30)
{
    public TimeSpan CriticalRecoveryHold =>
        TimeSpan.FromSeconds(Math.Clamp(CriticalRecoveryHoldSeconds, 0, 3600));
}

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
public sealed record AlarmListQuery(
    string? Search,
    string? Status,
    string? Severity,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page,
    int PageSize);
public sealed record AlarmListSummary(
    int Total,
    int Open,
    int Acknowledged,
    int Recovered,
    int Critical);
public sealed record AlarmListPage(
    IReadOnlyList<AlarmSnapshot> Items,
    int TotalCount,
    int Page,
    int PageSize,
    AlarmListSummary Summary);

public interface IAlarmService
{
    Task SynchronizeAsync(
        IReadOnlyCollection<AlarmCandidate> candidates,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken,
        AlarmSynchronizationPolicy? policy = null);

    Task<IReadOnlyList<AlarmSnapshot>> GetRecentAsync(
        int maximumCount,
        bool activeOnly,
        CancellationToken cancellationToken);

    Task<AlarmListPage> GetPageAsync(
        TrafficUserContext user,
        AlarmListQuery query,
        CancellationToken cancellationToken);

    Task<AlarmOperationResult> AcknowledgeAsync(
        TrafficUserContext user,
        AcknowledgeAlarmRequest request,
        CancellationToken cancellationToken);
}
