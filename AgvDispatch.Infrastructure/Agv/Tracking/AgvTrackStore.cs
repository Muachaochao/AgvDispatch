using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed class AgvTrackStore(ISqlSugarClient database) : IAgvTrackStore
{
    public async Task AppendAsync(AgvTrackPointSnapshot point, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(point.VehicleId) || string.IsNullOrWhiteSpace(point.MapId) ||
            !double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentException("轨迹点缺少车辆、地图或有效坐标。", nameof(point));
        await database.Insertable(new AgvTrackPointEntity
        {
            VehicleId = point.VehicleId.Trim(),
            MapId = point.MapId.Trim(),
            MapVersionId = point.MapVersionId.Trim(),
            X = point.X,
            Y = point.Y,
            Angle = point.Angle,
            LocalizationConfidence = point.LocalizationConfidence,
            PositionState = (int)point.PositionState,
            DistanceToRouteMeters = point.DistanceToRouteMeters,
            NearestEdgeId = point.NearestEdgeId,
            SampledAt = point.SampledAt.LocalDateTime
        }).ExecuteCommandAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgvTrackPointSnapshot>> QueryAsync(
        AgvTrackQueryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.VehicleId) || string.IsNullOrWhiteSpace(request.MapId))
            throw new ArgumentException("轨迹查询必须指定车辆和地图。", nameof(request));
        if (request.To <= request.From || request.To - request.From > TimeSpan.FromDays(1))
            throw new ArgumentException("单次轨迹查询时间范围必须大于0且不超过24小时。", nameof(request));
        var maximumCount = Math.Clamp(request.MaximumCount, 2, 10000);
        var from = request.From.LocalDateTime;
        var to = request.To.LocalDateTime;
        var rows = await database.Queryable<AgvTrackPointEntity>()
            .Where(item => item.VehicleId == request.VehicleId && item.MapId == request.MapId &&
                           item.SampledAt >= from && item.SampledAt <= to)
            .WhereIF(!string.IsNullOrWhiteSpace(request.MapVersionId),item=>item.MapVersionId==request.MapVersionId)
            .OrderBy(item => item.SampledAt, OrderByType.Desc)
            .Take(maximumCount)
            .ToListAsync(cancellationToken);
        rows.Reverse();
        return rows.Select(ToSnapshot).ToArray();
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return database.Deleteable<AgvTrackPointEntity>()
            .Where(item => item.SampledAt < cutoff.LocalDateTime)
            .ExecuteCommandAsync(cancellationToken);
    }

    private static AgvTrackPointSnapshot ToSnapshot(AgvTrackPointEntity row) => new(
        row.Id,
        row.VehicleId,
        row.MapId,
        row.MapVersionId,
        row.X,
        row.Y,
        row.Angle,
        row.LocalizationConfidence,
        (AgvMapPositionState)row.PositionState,
        row.DistanceToRouteMeters,
        row.NearestEdgeId,
        new DateTimeOffset(row.SampledAt));
}
