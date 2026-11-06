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
        if (await database.Queryable<ObstacleRecoveryEntity>().AnyAsync(
                item => item.RecoveryId == row.RecoveryId, cancellationToken))
            await database.Updateable(row).ExecuteCommandAsync(cancellationToken);
        else
            await database.Insertable(row).ExecuteCommandAsync(cancellationToken);
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
            DurationSeconds = duration,
            NextAction = row.NextAction ?? "",
            AutomaticDetourEnabled = options.AutomaticDetourEnabled
        };
    }

    private static string[] Deserialize(string value)
    {
        try { return JsonSerializer.Deserialize<string[]>(value, Json) ?? []; }
        catch (JsonException) { return []; }
    }
}
