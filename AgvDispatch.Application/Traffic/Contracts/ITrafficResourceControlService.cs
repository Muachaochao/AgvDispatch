using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Application.Abstractions;

public sealed record TrafficResourceLeaseSnapshot(
    string LeaseId,
    string ResourceId,
    string OwnerStepId,
    string TaskId,
    string VehicleId,
    DateTimeOffset AcquiredAt,
    DateTimeOffset RenewedAt,
    DateTimeOffset ExpiresAt,
    bool HeartbeatExpired,
    DispatchTaskStage? TaskStage);

public sealed record TrafficResourceSnapshot(
    string ResourceId,
    TrafficResourceType? Type,
    int Capacity,
    int OccupiedCount,
    IReadOnlyList<TrafficResourceLeaseSnapshot> Leases)
{
    public IReadOnlyList<MapPoint2D>? RuntimeBoundary { get; init; }
}

public sealed record TrafficResourceReleaseAuditSnapshot(
    string AuditId,
    string ResourceId,
    string TaskId,
    string VehicleId,
    string Operator,
    string? RequestedBy,
    string? ApprovedBy,
    string Reason,
    DispatchTaskStage? PreviousTaskStage,
    bool HeartbeatExpired,
    DateTimeOffset ReleasedAt);

public sealed record TrafficDispatchDiagnosis(
    string TaskId,
    string StepId,
    string VehicleId,
    string Source,
    string Destination,
    string State,
    string Reason,
    DateTimeOffset UpdatedAt);

public interface ITrafficResourceControlService
{
    Task<TrafficBlockingDetails> GetBlockingAsync(TrafficBlockingQuery query,CancellationToken ct)=>throw new NotSupportedException("后台未支持阻塞链详情。");
    Task<TrafficWaitingPage> GetWaitingAsync(TrafficWaitingQuery query, CancellationToken ct) => throw new NotSupportedException("后台未支持等待队列。");
    Task<TrafficMapSnapshot> GetMapAsync(TrafficMapQuery query, CancellationToken ct) => throw new NotSupportedException("后台未支持交通地图联动。");
    Task<TrafficLifecycleSnapshot> GetLifecycleAsync(CancellationToken ct) => throw new NotSupportedException("后台尚不支持路权过程监控。");
    Task<IReadOnlyList<TrafficResourceSnapshot>> GetResourcesAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TrafficResourceReleaseAuditSnapshot>> GetRecentAuditsAsync(
        int maximumCount,
        CancellationToken cancellationToken);
    Task<TrafficDispatchDiagnosis?> GetDispatchDiagnosisAsync(
        string taskId,
        string stepId,
        CancellationToken cancellationToken) => Task.FromResult<TrafficDispatchDiagnosis?>(null);
}
