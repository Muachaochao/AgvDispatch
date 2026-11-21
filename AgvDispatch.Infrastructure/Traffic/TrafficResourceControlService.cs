using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TrafficResourceControlService(
    ISqlSugarClient database,
    TrafficResourceCatalog catalog, IActiveMapRuntime? activeMap = null, IAgvStateCache? vehicleStates = null, TrafficMapDisplayOptions? mapOptions = null, TimeProvider? clock = null, TrafficWaitingAlertOptions? waitingAlerts = null) : ITrafficResourceControlService
{
    public async Task<IReadOnlyList<TrafficResourceSnapshot>> GetResourcesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var leases = await database.Queryable<TrafficResourceLeaseEntity>()
            .OrderBy(lease => lease.ResourceId)
            .OrderBy(lease => lease.AcquiredAt)
            .ToListAsync(cancellationToken);
        var persistedDefinitions = await database.Queryable<TrafficResourceDefinitionEntity>()
            .ToListAsync(cancellationToken);
        var taskIds = leases.Select(lease => lease.TaskId).Distinct().ToArray();
        var tasks = taskIds.Length == 0
            ? []
            : await database.Queryable<DispatchTaskEntity>()
                .Where(task => taskIds.Contains(task.TaskId))
                .ToListAsync(cancellationToken);
        var stageByTask = tasks.ToDictionary(
            task => task.TaskId,
            task => (DispatchTaskStage?)task.Stage,
            StringComparer.OrdinalIgnoreCase);
        var now = DateTime.Now;
        var definitionById = persistedDefinitions.ToDictionary(
            resource => resource.ResourceId,
            StringComparer.OrdinalIgnoreCase);
        var resourceIds = catalog.All.Select(resource => resource.Id)
            .Concat(persistedDefinitions.Select(resource => resource.ResourceId))
            .Concat(leases.Select(lease => lease.ResourceId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return resourceIds.Select(resourceId =>
        {
            var resourceLeases = leases
                .Where(lease => lease.ResourceId.Equals(
                    resourceId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(lease => new TrafficResourceLeaseSnapshot(
                    lease.LeaseId,
                    lease.ResourceId,
                    lease.OwnerStepId,
                    lease.TaskId,
                    lease.VehicleId,
                    new DateTimeOffset(lease.AcquiredAt),
                    new DateTimeOffset(lease.RenewedAt),
                    new DateTimeOffset(lease.ExpiresAt),
                    lease.ExpiresAt < now,
                    stageByTask.GetValueOrDefault(lease.TaskId)))
                .ToArray();
            var configured = catalog.TryGet(resourceId, out var definition);
            definitionById.TryGetValue(resourceId, out var persisted);
            return new TrafficResourceSnapshot(
                resourceId,
                configured
                    ? definition.Type
                    : persisted is null ? null : (TrafficResourceType?)persisted.ResourceType,
                configured ? definition.Capacity : persisted?.Capacity ?? 1,
                resourceLeases.Select(lease => lease.TaskId)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                resourceLeases)
            {
                RuntimeBoundary = configured && MapCorridorResources.IsAutomatic(resourceId) ? definition.Boundary : null
            };
        }).ToArray();
    }

    public async Task<IReadOnlyList<TrafficResourceReleaseAuditSnapshot>> GetRecentAuditsAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var audits = await database.Queryable<TrafficResourceReleaseAuditEntity>()
            .OrderByDescending(audit => audit.ReleasedAt)
            .Take(maximumCount)
            .ToListAsync(cancellationToken);
        return audits.Select(audit => new TrafficResourceReleaseAuditSnapshot(
            audit.AuditId,
            audit.ResourceId,
            audit.TaskId,
            audit.VehicleId,
            audit.Operator,
            audit.RequestedBy,
            audit.ApprovedBy,
            audit.Reason,
            audit.PreviousTaskStage.HasValue
                ? (DispatchTaskStage?)audit.PreviousTaskStage.Value
                : null,
            audit.HeartbeatExpired,
            new DateTimeOffset(audit.ReleasedAt))).ToArray();
    }

    public async Task<TrafficDispatchDiagnosis?> GetDispatchDiagnosisAsync(
        string taskId,
        string stepId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(stepId))
            return null;
        var row = await database.Queryable<TrafficRequestEntity>()
            .Where(request => request.TaskId == taskId && request.StepId == stepId)
            .FirstAsync(cancellationToken);
        return row is null ? null : new TrafficDispatchDiagnosis(
            row.TaskId,
            row.StepId,
            row.VehicleId,
            row.Source,
            row.Destination,
            row.State,
            row.Reason,
            new DateTimeOffset(row.UpdatedAt));
    }

}
