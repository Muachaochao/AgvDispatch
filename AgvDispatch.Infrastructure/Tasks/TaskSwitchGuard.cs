using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

// Pure database reader, deliberately independent from the coordinator/port to avoid DI and mutex cycles.
public sealed class TaskSwitchGuard(ISqlSugarClient database) : ITaskSwitchGuard
{
    public async Task<bool> AllowsVehicleAsync(string vehicleId, string? taskId, string? requestId, CancellationToken ct)
    {
        var sessions = await database.Queryable<TaskSwitchEntity>()
            .Where(r => r.VehicleId == vehicleId && r.Stage != TaskSwitchStages.Abandoned).ToListAsync(ct);
        foreach (var session in sessions)
        {
            // The creator may commit a normal task just before the coordinator records its receipt.
            var target = await database.Queryable<DispatchTaskEntity>()
                .FirstAsync(t => t.CreateRequestId == session.ReplacementRequestId, ct);
            if (session.Stage == TaskSwitchStages.Completed)
            {
                // Normal assignment owns the car from SubmittingToAgv onward. Until then retain the handoff lock.
                if (target is not null && target.Stage is not ((int)DispatchTaskStage.Created) and not ((int)DispatchTaskStage.WaitingForVehicle)) continue;
            }
            var matchingTask = !string.IsNullOrWhiteSpace(taskId) && (taskId == session.TargetTaskId || taskId == target?.TaskId);
            var matchingRequest = !string.IsNullOrWhiteSpace(requestId) && requestId == session.ReplacementRequestId;
            if (!matchingTask && !matchingRequest) return false;
        }
        return true;
    }
}
