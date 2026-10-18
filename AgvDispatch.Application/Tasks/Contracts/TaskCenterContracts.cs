using AgvDispatch.Domain.Tasks;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Application.Abstractions;

public enum TaskListFilter { All, Waiting, Running, Failed, Completed, Cancelled }
public sealed record TaskListQuery(TaskListFilter Filter = TaskListFilter.All,
    string Search = "", int Page = 1, int PageSize = 50);
public sealed record TaskCenterRow(string TaskId, string StationId, string? VehicleId,
    DispatchTaskStage Stage, int Priority, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, string? ErrorMessage)
{
    public string? RequestedVehicleId { get; init; }
}
public sealed record TaskListPage(IReadOnlyList<TaskCenterRow> Items, int TotalCount, int Page, int PageSize);
public sealed record TaskStepDetail(string StepId, int Sequence, DispatchTaskStepType StepType,
    DispatchTaskStepStatus Status, string? VehicleId, string? SourceSite, string? DestinationSite,
    string? VendorTaskId, int RetryCount, DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt, string? ErrorMessage)
{
    public int TimeoutSeconds { get; init; }
    public bool IsTimedOut { get; init; }
    public string TimeoutText { get; init; } = "超时提示：关闭";
    public bool IsCurrent { get; init; }
    public string ElapsedText { get; init; } = "未开始";
    public string WaitingExplanation { get; init; } = "";
    public string ObservedPhase { get; init; } = "";
    public string CompletionRequirement { get; init; } = "";
    public string EvidenceBoundary { get; init; } = "";
    public string CommandStateExplanation { get; init; } = "";
    public IReadOnlyList<TaskPlcCondition> ProgressConditions { get; init; } = [];
}
public sealed record TaskCommandDetail(string CommandId, string StepId, string VehicleId,
    AgvCommandStatus Status, int AttemptCount, DateTimeOffset NextAttemptAt,
    DateTimeOffset? SentAt, string? VendorCommandId, string? LastError)
{ public TaskRouteDetail? Route {get;init;} }
public sealed record TaskRouteDetail(string TaskId,string StepId,string CommandId,string VehicleId,string MapId,string MapVersionId,string MapDefinitionHash,string[] NodeIds,string[] EdgeIds,string[] VendorSites,string Action);
public sealed record TaskResourceDetail(string ResourceId, string OwnerStepId, string VehicleId,
    DateTimeOffset AcquiredAt, DateTimeOffset ExpiresAt, bool HeartbeatExpired);
public sealed record TaskPlcCondition(string StationId, string VariableName, string Address,
    bool ExpectedValue, bool? CurrentValue, SignalQuality Quality, bool Satisfied)
{
    public string RuleId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Result { get; init; } = "Unknown";
    public string Reason { get; init; } = "";
    public DateTimeOffset? ObservedAt { get; init; }
    public long? SignalVersion { get; init; }
    public string Role { get; init; } = "执行条件";
}
public sealed record TaskExecutionEvent(string Id, DateTimeOffset OccurredAt, string StepId,
    string Kind, string Title, string Summary, string Source);
public sealed record TaskCancellationInfo(string RequestId, string Reason, string Operator, DateTimeOffset? RequestedAt,
    int SendAttempts, DateTimeOffset? SentAt, DateTimeOffset? ConfirmedAt);
public sealed record TaskOperationHistoryEntry(string Id, DateTimeOffset OccurredAt, string Category, string Operation,
    string Operator, string StepId, string SessionId, string ResourceId, string Scope, string Reason,
    string Evidence, int EvidenceCharacters, bool EvidenceTruncated)
{
    public string EvidenceDisplay => EvidenceTruncated
        ? $"证据共 {EvidenceCharacters} 字符，仅显示前 {Evidence.Length} 字符；完整原文仍保存在审计数据库。\n\n{Evidence}" : Evidence;
}
public sealed record TaskCenterDetail(TaskCenterRow Task, IReadOnlyList<TaskStepDetail> Steps,
    IReadOnlyList<TaskCommandDetail> Commands, IReadOnlyList<TaskResourceDetail> Resources,
    IReadOnlyList<TaskPlcCondition> PlcConditions, string WaitingExplanation)
{
    public IReadOnlyList<TaskOperationHistoryEntry> OperationHistory { get; init; } = [];
    public int OperationHistoryTotal { get; init; }
    public TaskCancellationInfo? Cancellation { get; init; }
    public IReadOnlyList<TaskExecutionEvent> ExecutionTimeline { get; init; } = [];
    public int ExecutionTimelineTotal { get; init; }
    public DateTimeOffset? DiagnosticsObservedAt { get; init; }
    public string HandshakeMode { get; init; } = "ObservationOnly";
    public string HandshakeContractVersion { get; init; } = "";
    public IReadOnlyList<HandshakeCheckpoint> Handshakes { get; init; } = [];
    public TaskHandoverDiagnosis? Handover { get; init; }
}
public sealed record TaskDetailRequest(string TaskId, bool IncludeOperationHistory = true);
public enum TaskHistoryCategory { All, Cancellation, Recovery, TrafficRelease }
public sealed record TaskHistoryQuery(string TaskId, TaskHistoryCategory Category = TaskHistoryCategory.All,
    DateTimeOffset? From = null, DateTimeOffset? To = null, string SessionId = "", int Page = 1, int PageSize = 50);
public sealed record TaskHistoryPage(IReadOnlyList<TaskOperationHistoryEntry> Items, int TotalCount, int Page, int PageSize,
    IReadOnlyList<string> SessionIds);


public interface ITaskCenterReadRepository
{
    Task<TaskListPage> QueryAsync(TaskListQuery query, CancellationToken cancellationToken);
    Task<TaskCenterDetail?> GetDetailAsync(string taskId, CancellationToken cancellationToken);
    Task<TaskCenterDetail?> GetDetailAsync(TaskDetailRequest request, CancellationToken cancellationToken) => GetDetailAsync(request.TaskId, cancellationToken);
    Task<TaskHistoryPage> QueryHistoryAsync(TaskHistoryQuery query, CancellationToken cancellationToken) => throw new NotSupportedException("后台不支持历史分页，请更新服务。");
}
public interface ITaskCenterQueryService : ITaskCenterReadRepository { }

public sealed record TaskHistoryExportRequest(TaskHistoryQuery Filter, string ExportId = "");
public sealed record TaskHistoryExportResult(bool Accepted, string Message, string FilePath = "", string Sha256 = "", long SizeBytes = 0, int RecordCount = 0);
public interface ITaskHistoryExportService
{
    Task<TaskHistoryExportResult> ExportAsync(TrafficUserContext user, TaskHistoryExportRequest request, CancellationToken ct);
    Task<TaskHistoryExportProgress> GetProgressAsync(TrafficUserContext user, TaskHistoryExportJobRequest request, CancellationToken ct);
    Task<TaskHistoryExportCancelResult> CancelAsync(TrafficUserContext user, TaskHistoryExportJobRequest request, CancellationToken ct);
    Task<TaskHistoryExportCache> GetCacheAsync(TrafficUserContext user, TaskHistoryExportCacheRequest request, CancellationToken ct);
    Task<TaskHistoryExportCleanupResult> CleanupAsync(TrafficUserContext user, TaskHistoryExportCacheRequest request, CancellationToken ct);
}

public sealed record TaskHistoryExportJobRequest(string ExportId);
public sealed record TaskHistoryExportProgress(string ExportId, string Stage, string Message, int CompletedRecords = 0, int TotalRecords = 0, int Percent = 0, bool IsTerminal = false);
public sealed record TaskHistoryExportCancelResult(bool Accepted, string Message);
public sealed record TaskHistoryExportCacheRequest(int RetentionDays = 7);
public sealed record TaskHistoryExportCacheItem(string FileName, string Kind, DateTimeOffset UpdatedAt, long SizeBytes, bool CanClean);
public sealed record TaskHistoryExportCache(IReadOnlyList<TaskHistoryExportCacheItem> Items, int TotalCount, long TotalBytes, int EligibleCount, int RetentionDays);
public sealed record TaskHistoryExportCleanupResult(int DeletedCount, long FreedBytes, int FailedCount, string Message);
