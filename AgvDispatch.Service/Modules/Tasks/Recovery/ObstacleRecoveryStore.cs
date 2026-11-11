using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Service;

public sealed class ObstacleRecoveryStore(
    ISqlSugarClient database,
    ObstacleRecoveryOptions options,
    TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ObstacleRecoveryEntity?> FindAsync(string taskId, string stepId, CancellationToken cancellationToken) =>
        await database.Queryable<ObstacleRecoveryEntity>()
            .Where(row => row.TaskId == taskId && row.StepId == stepId)
            .FirstAsync(cancellationToken);

    public async Task<IReadOnlyList<ObstacleRecoveryEntity>> ListOpenAsync(CancellationToken cancellationToken) =>
        await database.Queryable<ObstacleRecoveryEntity>()
            .Where(row => row.State != ObstacleRecoveryStates.Recovered)
            .OrderBy(row => row.UpdatedAt)
            .ToListAsync(cancellationToken);

    public async Task<ObstacleRecoverySnapshot?> GetForVehicleAsync(string vehicleId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<ObstacleRecoveryEntity>()
            .Where(row => row.VehicleId == vehicleId && row.State != ObstacleRecoveryStates.Recovered)
            .OrderByDescending(row => row.UpdatedAt)
            .FirstAsync(cancellationToken);
        return row is null ? null : Snapshot(row);
    }

    public async Task<IReadOnlyList<ObstacleRecoverySnapshot>> ListSnapshotsAsync(CancellationToken cancellationToken)
    {
        var rows = await ListOpenAsync(cancellationToken);
        return rows.Select(Snapshot).ToArray();
    }

    public async Task SaveAsync(ObstacleRecoveryEntity row, CancellationToken cancellationToken)
    {
        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        var existing = await database.Queryable<ObstacleRecoveryEntity>()
            .Where(item => item.TaskId == row.TaskId && item.StepId == row.StepId)
            .FirstAsync(cancellationToken);
        if (existing is null)
        {
            try
            {
                await database.Insertable(row).ExecuteCommandAsync(cancellationToken);
                return;
            }
            catch
            {
                // A second process may have inserted the same logical lifecycle
                // between the read and insert. Only absorb that race when the
                // unique TaskId+StepId row is now observable.
                existing = await database.Queryable<ObstacleRecoveryEntity>()
                    .Where(item => item.TaskId == row.TaskId && item.StepId == row.StepId)
                    .FirstAsync(cancellationToken);
                if (existing is null) throw;
            }
        }

        // obstacle_recovery is unique by TaskId+StepId. A later block on the
        // same original step reopens the existing audit identity instead of
        // inserting a second row and failing the coordinator every cycle.
        if (!existing.RecoveryId.Equals(row.RecoveryId, StringComparison.OrdinalIgnoreCase))
        {
            row.RecoveryId = existing.RecoveryId;
            row.CreatedAt = existing.CreatedAt;
            row.Revision = Math.Max(existing.Revision + 1, row.Revision);
        }
        await database.Updateable(row)
            .Where(item => item.RecoveryId == existing.RecoveryId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public ObstacleRecoverySnapshot Snapshot(ObstacleRecoveryEntity row)
    {
        var blockedSince = new DateTimeOffset(DateTime.SpecifyKind(row.BlockedSinceUtc, DateTimeKind.Utc));
        var duration = Math.Max(0, (_clock.GetUtcNow() - blockedSince).TotalSeconds);
        return new(row.RecoveryId, row.TaskId, row.StepId, row.VehicleId,
            row.OriginalVendorTaskId, row.State, row.Message ?? "", blockedSince,
            Deserialize(row.OriginalPathJson), Deserialize(row.CandidatePathJson),
            Deserialize(row.BlockedEdgeIdsJson), row.SafeSite, row.MapId, row.MapVersionId,
            row.DefinitionHash, row.NewVendorTaskId, row.SendAttemptedAtUtc is not null,
            new DateTimeOffset(DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc)))
        {
            CancelRequestedAt = ToUtc(row.CancelRequestedAtUtc),
            CancelAttemptedAt = ToUtc(row.CancelAttemptedAtUtc),
            CancelAcknowledgedAt = ToUtc(row.CancelAcknowledgedAtUtc),
            OriginalTerminalAt = ToUtc(row.OriginalTerminalAtUtc),
            AssessmentAfterSeconds = options.DetourAfterSeconds,
            CancelAfterSeconds = options.CancelAfterSeconds,
            CancelTerminalWaitWarningSeconds = options.CancelTerminalWaitWarningSeconds,
            DurationSeconds = duration,
            NextAction = row.NextAction ?? "",
            AutomaticDetourEnabled = options.AutomaticDetourEnabled,
            AutomaticSafetyPointRetreatEnabled = options.AutomaticSafetyPointRetreatEnabled,
            SafetyPointTarget = row.SafetyPointTarget,
            SafetyPointBlockedSource = row.SafetyPointBlockedSource,
            SafetyPointBlockedTarget = row.SafetyPointBlockedTarget,
            SafetyPointRetreatTaskId = row.SafetyPointRetreatTaskId,
            SafetyPointRetreatMethod = row.SafetyPointRetreatMethod,
            SafetyPointRetreatAttemptedAt = ToUtc(row.SafetyPointRetreatAttemptedAtUtc),
            SafetyPointReachedAt = ToUtc(row.SafetyPointReachedAtUtc)
        };
    }

    private static DateTimeOffset? ToUtc(DateTime? value) => value is null
        ? null
        : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private static string[] Deserialize(string value)
    {
        try { return JsonSerializer.Deserialize<string[]>(value, Json) ?? []; }
        catch (JsonException) { return []; }
    }
}
