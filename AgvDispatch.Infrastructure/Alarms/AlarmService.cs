using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed class AlarmService(ISqlSugarClient database) : IAlarmService
{
    public async Task SynchronizeAsync(
        IReadOnlyCollection<AlarmCandidate> candidates,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        cancellationToken.ThrowIfCancellationRequested();
        var byKey = candidates
            .GroupBy(item => item.RuleKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (byKey.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("报警规则键不能为空。");
        }

        var now = observedAt.LocalDateTime;
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var active = await database.Queryable<AlarmRecordEntity>()
                .Where(item => item.Status == (int)AlarmLifecycleStatus.Open ||
                               item.Status == (int)AlarmLifecycleStatus.Acknowledged)
                .ToListAsync(cancellationToken);

            foreach (var existing in active)
            {
                if (byKey.Remove(existing.RuleKey, out var candidate))
                {
                    await database.Updateable<AlarmRecordEntity>()
                        .SetColumns(item => new AlarmRecordEntity
                        {
                            SourceType = Truncate(candidate.SourceType, 64),
                            SourceId = Truncate(candidate.SourceId, 128),
                            Code = Truncate(candidate.Code, 64),
                            Title = Truncate(candidate.Title, 256),
                            Message = Truncate(candidate.Message, 2000),
                            Severity = (int)candidate.Severity,
                            LastObservedAt = now,
                            UpdatedAt = now
                        })
                        .Where(item => item.AlarmId == existing.AlarmId)
                        .ExecuteCommandAsync(cancellationToken);
                }
                else
                {
                    await database.Updateable<AlarmRecordEntity>()
                        .SetColumns(item => new AlarmRecordEntity
                        {
                            Status = (int)AlarmLifecycleStatus.Recovered,
                            RecoveredAt = now,
                            RecoveryMessage = "报警条件已消失，系统自动恢复。",
                            UpdatedAt = now
                        })
                        .Where(item => item.AlarmId == existing.AlarmId &&
                                       (item.Status == (int)AlarmLifecycleStatus.Open ||
                                        item.Status == (int)AlarmLifecycleStatus.Acknowledged))
                        .ExecuteCommandAsync(cancellationToken);
                }
            }

            if (byKey.Count > 0)
            {
                var rows = byKey.Values.Select(candidate => new AlarmRecordEntity
                {
                    AlarmId = $"ALM-{Guid.NewGuid():N}",
                    RuleKey = Truncate(candidate.RuleKey, 256),
                    SourceType = Truncate(candidate.SourceType, 64),
                    SourceId = Truncate(candidate.SourceId, 128),
                    Code = Truncate(candidate.Code, 64),
                    Title = Truncate(candidate.Title, 256),
                    Message = Truncate(candidate.Message, 2000),
                    Severity = (int)candidate.Severity,
                    Status = (int)AlarmLifecycleStatus.Open,
                    FirstOccurredAt = now,
                    LastObservedAt = now,
                    UpdatedAt = now
                }).ToArray();
                await database.Insertable(rows).ExecuteCommandAsync(cancellationToken);
            }
        });

        if (!transaction.IsSuccess)
        {
            throw new InvalidOperationException("同步报警状态事务失败。", transaction.ErrorException);
        }
    }

    public async Task<IReadOnlyList<AlarmSnapshot>> GetRecentAsync(
        int maximumCount,
        bool activeOnly,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var query = database.Queryable<AlarmRecordEntity>();
        if (activeOnly)
        {
            query = query.Where(item => item.Status == (int)AlarmLifecycleStatus.Open ||
                                        item.Status == (int)AlarmLifecycleStatus.Acknowledged);
        }
        var rows = await query
            .OrderBy(item => item.Status)
            .OrderByDescending(item => item.Severity)
            .OrderByDescending(item => item.LastObservedAt)
            .Take(maximumCount)
            .ToListAsync(cancellationToken);
        return rows.Select(ToSnapshot).ToArray();
    }

    public async Task<AlarmListPage> GetPageAsync(
        TrafficUserContext user,
        AlarmListQuery query,
        CancellationToken cancellationToken)
    {
        if (!user.Has(TrafficPermission.View))
            throw new UnauthorizedAccessException("当前软件账号没有查看报警记录的权限。");
        var search = query.Search?.Trim() ?? "";
        if (search.Length > 128)
            throw new ArgumentException("报警搜索关键词最多128个字符。");
        if (query.Page is < 1 or > 100000 || query.PageSize is not (20 or 50 or 100 or 200))
            throw new ArgumentException("报警页码或每页条数无效。请使用每页20、50、100或200条。");
        if (query.From is { } from && query.To is { } to && from >= to)
            throw new ArgumentException("报警开始时间必须早于结束时间。");

        AlarmLifecycleStatus? status = query.Status?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" => null,
            "open" => AlarmLifecycleStatus.Open,
            "acknowledged" => AlarmLifecycleStatus.Acknowledged,
            "recovered" => AlarmLifecycleStatus.Recovered,
            _ => throw new ArgumentException("报警状态筛选无效。")
        };
        AlarmSeverity? severity = query.Severity?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" => null,
            "info" => AlarmSeverity.Info,
            "warning" => AlarmSeverity.Warning,
            "error" => AlarmSeverity.Error,
            "critical" => AlarmSeverity.Critical,
            _ => throw new ArgumentException("报警级别筛选无效。")
        };

        var fromAt = query.From?.LocalDateTime;
        var toAt = query.To?.LocalDateTime;
        ISugarQueryable<AlarmRecordEntity> Filtered() => database.Queryable<AlarmRecordEntity>()
            .WhereIF(search.Length > 0, item => item.AlarmId.Contains(search) ||
                item.RuleKey.Contains(search) ||
                item.SourceId.Contains(search) ||
                item.Code.Contains(search) ||
                item.Title.Contains(search) ||
                item.Message.Contains(search))
            .WhereIF(fromAt.HasValue, item => item.FirstOccurredAt >= fromAt!.Value)
            .WhereIF(toAt.HasValue, item => item.FirstOccurredAt < toAt!.Value);

        cancellationToken.ThrowIfCancellationRequested();
        var total = await Filtered().CountAsync(cancellationToken);
        var open = await Filtered().Where(item => item.Status == (int)AlarmLifecycleStatus.Open).CountAsync(cancellationToken);
        var acknowledged = await Filtered().Where(item => item.Status == (int)AlarmLifecycleStatus.Acknowledged).CountAsync(cancellationToken);
        var recovered = await Filtered().Where(item => item.Status == (int)AlarmLifecycleStatus.Recovered).CountAsync(cancellationToken);
        var critical = await Filtered().Where(item => item.Severity == (int)AlarmSeverity.Critical).CountAsync(cancellationToken);
        var rows = await Filtered()
            .WhereIF(status.HasValue, item => item.Status == (int)status!.Value)
            .WhereIF(severity.HasValue, item => item.Severity == (int)severity!.Value)
            .OrderByDescending(item => item.LastObservedAt)
            .OrderByDescending(item => item.Severity)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
        return new(rows.Select(ToSnapshot).ToArray(), total, query.Page, query.PageSize,
            new(total, open, acknowledged, recovered, critical));
    }

    public async Task<AlarmOperationResult> AcknowledgeAsync(
        TrafficUserContext user,
        AcknowledgeAlarmRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        if (!user.Has(TrafficPermission.AcknowledgeAlarm))
        {
            throw new UnauthorizedAccessException("当前软件账号没有报警确认权限。");
        }
        if (string.IsNullOrWhiteSpace(request.AlarmId))
        {
            return new AlarmOperationResult(false, "未选择需要确认的报警。");
        }
        if (string.IsNullOrWhiteSpace(request.Comment) || request.Comment.Trim().Length < 2)
        {
            return new AlarmOperationResult(false, "报警确认说明至少需要2个字符。");
        }

        var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
        var updated = await database.Updateable<AlarmRecordEntity>()
            .SetColumns(item => new AlarmRecordEntity
            {
                Status = (int)AlarmLifecycleStatus.Acknowledged,
                AcknowledgedBy = Truncate(user.WindowsIdentity, 256),
                AcknowledgedAt = now,
                AcknowledgeComment = Truncate(request.Comment.Trim(), 1000),
                UpdatedAt = now
            })
            .Where(item => item.AlarmId == request.AlarmId &&
                           item.Status == (int)AlarmLifecycleStatus.Open)
            .ExecuteCommandAsync(cancellationToken);
        if (updated == 1)
        {
            return new AlarmOperationResult(true, $"报警{request.AlarmId}已由{user.DisplayName}确认。");
        }

        var current = await database.Queryable<AlarmRecordEntity>()
            .Where(item => item.AlarmId == request.AlarmId)
            .FirstAsync(cancellationToken);
        return current is null
            ? new AlarmOperationResult(false, "报警不存在。")
            : new AlarmOperationResult(false,
                $"报警当前状态为{(AlarmLifecycleStatus)current.Status}，不能重复确认。");
    }

    private static AlarmSnapshot ToSnapshot(AlarmRecordEntity row) => new(
        row.AlarmId,
        row.RuleKey,
        row.SourceType,
        row.SourceId,
        row.Code,
        row.Title,
        row.Message,
        (AlarmSeverity)row.Severity,
        (AlarmLifecycleStatus)row.Status,
        ToOffset(row.FirstOccurredAt),
        ToOffset(row.LastObservedAt),
        row.AcknowledgedBy,
        row.AcknowledgedAt.HasValue ? ToOffset(row.AcknowledgedAt.Value) : null,
        row.AcknowledgeComment,
        row.RecoveredAt.HasValue ? ToOffset(row.RecoveredAt.Value) : null,
        row.RecoveryMessage);

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Local));

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
