using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed class DispatchTaskReadRepository(ISqlSugarClient database) : IActiveTaskLookup
{
    public async Task<IReadOnlyList<ActiveDispatchTask>> GetActiveTasksAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completed = (int)DispatchTaskStage.Completed;
        var cancelled = (int)DispatchTaskStage.Cancelled;
        var entities = await database.Queryable<DispatchTaskEntity>()
            .Where(task => task.Stage != completed && task.Stage != cancelled)
            .OrderBy(task => task.CreatedAt)
            .ToListAsync(cancellationToken);

        return entities.Select(task => new ActiveDispatchTask(
            task.TaskId,
            task.StationId,
            task.VehicleId,
            task.VendorTaskId,
            (DispatchTaskStage)task.Stage,
            new DateTimeOffset(DateTime.SpecifyKind(task.CreatedAt, DateTimeKind.Local)),
            new DateTimeOffset(DateTime.SpecifyKind(task.UpdatedAt, DateTimeKind.Local))))
            .ToArray();
    }
}
