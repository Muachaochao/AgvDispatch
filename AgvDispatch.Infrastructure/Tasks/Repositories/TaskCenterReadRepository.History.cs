using System.Text.Json;
using System.Globalization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;
namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TaskCenterReadRepository
{
    private const int HistoryLimit = 200, EvidencePreviewLimit = 4096;
    private static readonly string[] OperationKinds = ["TaskCancellationRequested", "TaskCancellationSendIntent", "TaskCancellationSendAccepted",
        "TaskCancellationProcessingFailed", "TaskCancellationVehicleConfirmed", "BootHold", "RuntimeReview", "VerifiedUnstarted",
        "ContinueOriginal", "RecordPhysicalCompletion", "CloseCancelled", "VerificationRevoked", "RecoveryRejected", "Release"];
    private async Task<(IReadOnlyList<TaskOperationHistoryEntry> Items, int Total)> ReadOperationHistoryAsync(string taskId, CancellationToken ct)
    {
        // Session-wide release belongs to a task only when that session contains its recovery ticket.
        // EXISTS avoids multiplying an event when a session contains duplicate tickets.
        var (events, releases) = HistorySources(new(taskId));
        var total = await events.Clone().CountAsync(ct) + await releases.Clone().CountAsync(ct);
        var eventRows = await events.OrderByDescending(e => e.OccurredAt).OrderByDescending(e => e.Id).Take(HistoryLimit).ToListAsync(ct);
        var releaseRows = await releases.OrderByDescending(r => r.ReleasedAt).OrderByDescending(r => r.AuditId).Take(HistoryLimit).ToListAsync(ct);
        var items = eventRows.Select(e => Entry("E:" + e.Id, e.OccurredAt,
            e.Kind.StartsWith("TaskCancellation", StringComparison.Ordinal) ? "取消" : "恢复核对",
            OperationLabel(e.Kind), e.Operator, e.StepId, e.SessionId, "", e.TaskId == taskId ? "当前任务" : "关联恢复批次 · 全场",
            e.Reason, e.Evidence)).Concat(releaseRows.Select(r => Entry("R:" + r.AuditId, r.ReleasedAt, "路权", "人工解锁路权",
                r.RequestedBy ?? r.Operator, r.OwnerStepId, "", r.ResourceId, "当前任务", r.Reason, JsonSerializer.Serialize(new
                { r.LeaseId, r.ResourceId, r.OwnerStepId, r.TaskId, r.VehicleId, r.Operator, r.RequestedBy, r.ApprovedBy,
                    r.PreviousTaskStage, r.HeartbeatExpired, r.AcquiredAt, r.RenewedAt, r.ExpiresAt, r.ReleasedAt }))));
        // Keep the newest bounded window, displayed chronologically with a stable tie-break.
        return (items.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id, StringComparer.Ordinal)
            .Take(HistoryLimit).Reverse().ToArray(), total);
    }
    private (ISugarQueryable<ExecutionEvidenceEntity> Events, ISugarQueryable<TrafficResourceReleaseAuditEntity> Releases) HistorySources(TaskHistoryQuery query)
    {
        var taskId = query.TaskId;
        var operationKinds = OperationKinds;
        var events = database.Queryable<ExecutionEvidenceEntity>().Where(e => operationKinds.Contains(e.Kind) &&
            (e.TaskId == taskId || ((e.TaskId == "" || e.TaskId == "__SITE__") && e.SessionId != "" &&
                SqlFunc.Subqueryable<ExecutionRecoveryTicketEntity>().Where(t => t.SessionId == e.SessionId && t.TaskId == taskId).Any())));
        var releases = database.Queryable<TrafficResourceReleaseAuditEntity>().Where(r => r.TaskId == taskId);
        if (query.Category == TaskHistoryCategory.Cancellation) events = events.Where(e => e.Kind.StartsWith("TaskCancellation"));
        else if (query.Category == TaskHistoryCategory.Recovery) events = events.Where(e => !e.Kind.StartsWith("TaskCancellation"));
        else if (query.Category == TaskHistoryCategory.TrafficRelease) events = events.Where(e => false);
        if (query.Category is TaskHistoryCategory.Cancellation or TaskHistoryCategory.Recovery) releases = releases.Where(r => false);
        if (!string.IsNullOrEmpty(query.SessionId))
        {
            var session = query.SessionId;
            events = events.Where(e => e.SessionId == session);
            // Release audits have no recovery batch identity; do not infer it from nearby timestamps.
            releases = releases.Where(r => false);
        }
        if (query.From is { } from)
        {
            var at = from.LocalDateTime;
            // SQLite audit timestamps are stored to milliseconds. Binding a DateTime omits .000
            // in the provider, which makes inclusive upper bounds exclude an exact timestamp.
            var ticks = at.Ticks % TimeSpan.TicksPerMillisecond;
            if (ticks != 0)
            {
                var delta = TimeSpan.TicksPerMillisecond - ticks;
                if (at.Ticks > DateTime.MaxValue.Ticks - delta) return (events.Where(e => false), releases.Where(r => false));
                at = at.AddTicks(delta);
            }
            var bound = at.ToString(at.Millisecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            events = events.Where("OccurredAt >= @historyFrom", new { historyFrom = bound });
            releases = releases.Where("ReleasedAt >= @releaseFrom", new { releaseFrom = bound });
        }
        if (query.To is { } to)
        {
            var bound = to.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            events = events.Where("OccurredAt <= @historyTo", new { historyTo = bound });
            releases = releases.Where("ReleasedAt <= @releaseTo", new { releaseTo = bound });
        }
        return (events, releases);
    }
    public async Task<TaskHistoryPage> QueryHistoryAsync(TaskHistoryQuery query, CancellationToken ct)
    {
        ValidateHistoryQuery(query);
        var (events, releases) = HistorySources(query);
        var keys = database.UnionAll(
            events.Select(e => new HistoryKey { Id = e.Id, OccurredAt = e.OccurredAt, Source = 0 }),
            releases.Select(r => new HistoryKey { Id = r.AuditId, OccurredAt = r.ReleasedAt, Source = 1 }));
        var total = await keys.Clone().CountAsync(ct);
        var page = Math.Min(query.Page, Math.Max(1, (total + query.PageSize - 1) / query.PageSize));
        var window = await keys.OrderByDescending(k => k.OccurredAt).OrderByDescending(k => k.Source).OrderByDescending(k => k.Id)
            .Skip((page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        var mapped = await ReadHistoryWindowAsync(window, query.TaskId, false, ct);
        var (allEvents, _) = HistorySources(new(query.TaskId));
        var sessions = await allEvents.Where(e => e.SessionId != "").Select(e => e.SessionId).Distinct().ToListAsync(ct);
        return new(window.Select(k => mapped[(k.Source == 0 ? "E:" : "R:") + k.Id]).ToArray(), total, page, query.PageSize,
            sessions.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }
    internal static void ValidateHistoryQuery(TaskHistoryQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.TaskId) || query.TaskId.Length > 64 || !Enum.IsDefined(query.Category) ||
            query.Page < 1 || query.Page > 100000 || query.PageSize < 1 || query.PageSize > 100 ||
            query.SessionId is null || query.SessionId.Length > 64 || (query.From.HasValue && query.To.HasValue && query.From > query.To))
            throw new ArgumentException("历史查询参数无效：每页1~100条，起始时间不得晚于结束时间，批次编号最多64字符。");
    }
    private async Task<Dictionary<string, TaskOperationHistoryEntry>> ReadHistoryWindowAsync(IReadOnlyList<HistoryKey> window, string taskId, bool fullEvidence, CancellationToken ct)
    {
        var eventIds = window.Where(k => k.Source == 0).Select(k => k.Id).ToArray();
        var releaseIds = window.Where(k => k.Source == 1).Select(k => k.Id).ToArray();
        var eventRows = eventIds.Length == 0 ? [] : await database.Queryable<ExecutionEvidenceEntity>().Where(e => eventIds.Contains(e.Id)).ToListAsync(ct);
        var releaseRows = releaseIds.Length == 0 ? [] : await database.Queryable<TrafficResourceReleaseAuditEntity>().Where(r => releaseIds.Contains(r.AuditId)).ToListAsync(ct);
        var mapped = eventRows.Select(e => Entry("E:" + e.Id, e.OccurredAt,
            e.Kind.StartsWith("TaskCancellation", StringComparison.Ordinal) ? "取消" : "恢复核对", OperationLabel(e.Kind),
            e.Operator, e.StepId, e.SessionId, "", e.TaskId == taskId ? "当前任务" : "关联恢复批次 · 全场", e.Reason, e.Evidence, fullEvidence))
            .Concat(releaseRows.Select(r => Entry("R:" + r.AuditId, r.ReleasedAt, "路权", "人工解锁路权", r.RequestedBy ?? r.Operator,
                r.OwnerStepId, "", r.ResourceId, "当前任务", r.Reason, JsonSerializer.Serialize(r), fullEvidence))).ToDictionary(e => e.Id);
        return mapped;
    }
    // Caller owns a dedicated read transaction: all pages and task metadata share one snapshot.
    internal async Task<int> WriteHistoryAsync(TaskHistoryQuery query, Func<TaskOperationHistoryEntry, Task> write, CancellationToken ct, Action<int>? started = null)
    {
        ValidateHistoryQuery(query);
        var (events, releases) = HistorySources(query);
        var keys = database.UnionAll(events.Select(e => new HistoryKey { Id = e.Id, OccurredAt = e.OccurredAt, Source = 0 }),
            releases.Select(r => new HistoryKey { Id = r.AuditId, OccurredAt = r.ReleasedAt, Source = 1 }));
        var total = await keys.Clone().CountAsync(ct);
        started?.Invoke(total);
        for (var offset = 0; offset < total; offset += 100)
        {
            ct.ThrowIfCancellationRequested();
            var window = await keys.Clone().OrderByDescending(k => k.OccurredAt).OrderByDescending(k => k.Source).OrderByDescending(k => k.Id)
                .Skip(offset).Take(100).ToListAsync(ct);
            var mapped = await ReadHistoryWindowAsync(window, query.TaskId, true, ct);
            foreach (var key in window)
            {
                ct.ThrowIfCancellationRequested();
                await write(mapped[(key.Source == 0 ? "E:" : "R:") + key.Id]);
            }
        }
        return total;
    }
    public sealed class HistoryKey
    {
        public string Id { get; set; } = "";
        public DateTime OccurredAt { get; set; }
        public int Source { get; set; }
    }
    private static TaskOperationHistoryEntry Entry(string id, DateTime at, string category, string operation, string user,
        string step, string session, string resource, string scope, string reason, string evidence, bool fullEvidence = false) =>
        new(id, Time(at), category, operation, user, step, session, resource, scope, reason,
            !fullEvidence && evidence.Length > EvidencePreviewLimit ? evidence[..EvidencePreviewLimit] : evidence,
            evidence.Length, !fullEvidence && evidence.Length > EvidencePreviewLimit);
    private static string OperationLabel(string kind) => kind switch
    {
        "TaskCancellationRequested" => "提交取消请求", "TaskCancellationSendIntent" => "准备发送车辆取消",
        "TaskCancellationSendAccepted" => "车辆取消接口接受（待结束确认）", "TaskCancellationProcessingFailed" => "取消处理异常（待核对）",
        "TaskCancellationVehicleConfirmed" => "车辆原任务结束确认", "BootHold" => "启动恢复冻结", "RuntimeReview" => "进入现场恢复核对",
        "VerifiedUnstarted" => "核对未开始任务", "ContinueOriginal" => "现场核对 · 继续原节点",
        "RecordPhysicalCompletion" => "核实到站 · 补记原运输", "CloseCancelled" => "现场核对后关闭取消",
        "VerificationRevoked" => "状态变化 · 撤销确认", "RecoveryRejected" => "恢复处置被拒绝", "Release" => "恢复批次最终放行", _ => kind
    };
}
