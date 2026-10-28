using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
namespace AgvDispatch.Infrastructure.Persistence;
public sealed partial class TrafficResourceControlService
{
    public async Task<TrafficWaitingPage> GetWaitingAsync(TrafficWaitingQuery query, CancellationToken ct)
    {
        if (query.PageNumber < 1 || query.PageNumber > 100000 || query.PageSize is < 1 or > 200 ||
            query.MinimumPriority is < 0 or > 100 || query.BlockingResourceId?.Length > 128 || !Enum.IsDefined(query.Sort))
            throw new ArgumentException("等待队列筛选或分页参数无效。");
        ct.ThrowIfCancellationRequested();
        TrafficWaitingPage? page = null;
        // Count, filter, sort, and the returned page share one read snapshot. No traffic state is changed.
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var requests = await database.Queryable<TrafficRequestEntity>().Where(r => r.State == "Waiting").ToListAsync(ct);
            var taskIds = requests.Select(r => r.TaskId).Distinct().ToArray();
            var stepIds = requests.Select(r => r.StepId).Distinct().ToArray();
            var tasks = taskIds.Length == 0 ? [] : await database.Queryable<DispatchTaskEntity>().Where(t => taskIds.Contains(t.TaskId)).ToListAsync(ct);
            var steps = stepIds.Length == 0 ? [] : await database.Queryable<DispatchTaskStepEntity>().Where(s => stepIds.Contains(s.StepId)).ToListAsync(ct);
            var pending = steps.Where(s => s.Status is 0 or 10 or 60).ToDictionary(s => s.StepId);
            var leases = await database.Queryable<TrafficResourceLeaseEntity>().ToListAsync(ct);
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            var byTask = tasks.ToDictionary(t => t.TaskId);
            var cycles = TrafficCycleRiskDetector.Find(requests, byTask, pending, leases, catalog, DateTime.Now);
            var waiting = requests.Where(r => pending.TryGetValue(r.StepId, out var step) && step.TaskId == r.TaskId)
                .Select(r => ProjectRequest(r, leases, byTask, DateTime.Now, now)).Where(r => r.State == "Waiting").ToArray();
            waiting = waiting.Select(r => cycles.FirstOrDefault(c => c.TaskIds.Contains(r.TaskId, StringComparer.OrdinalIgnoreCase)) is { } cycle
                ? r with { Reason = r.Reason + " " + cycle.Message } : r).ToArray();
            var resource = query.BlockingResourceId?.Trim();
            var filtered = waiting.Where(r => (query.MinimumPriority == null || r.Priority >= query.MinimumPriority) &&
                (string.IsNullOrWhiteSpace(resource) || r.Blockers.Any(b => string.Equals(b.ResourceId, resource, StringComparison.OrdinalIgnoreCase)))).ToArray();
            var ordered = query.Sort == TrafficWaitingSort.PriorityThenWait ?
                filtered.OrderByDescending(r => r.Priority ?? int.MinValue).ThenByDescending(r => r.WaitingSeconds ?? -1).ThenBy(r => r.StepId, StringComparer.Ordinal) :
                filtered.OrderByDescending(r => r.WaitingSeconds ?? -1).ThenByDescending(r => r.Priority ?? int.MinValue).ThenBy(r => r.StepId, StringComparer.Ordinal);
            page = new(now, waiting.Length, filtered.Length, query.PageNumber, query.PageSize,
                ordered.Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize).ToArray(),
                waiting.SelectMany(r => r.Blockers).Select(b => b.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray())
                { CycleRisks = cycles };
        });
        if (!transaction.IsSuccess) throw new InvalidOperationException("读取等待队列失败。", transaction.ErrorException);
        return page!;
    }
}
