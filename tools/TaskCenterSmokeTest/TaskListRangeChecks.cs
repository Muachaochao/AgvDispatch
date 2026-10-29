using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;

internal static class TaskListRangeChecks
{
    public static async Task RunAsync(Action<bool, string> require)
    {
        var path = Path.Combine(Path.GetTempPath(), "agv-task-range-" + Guid.NewGuid().ToString("N") + ".db");
        using var database = DatabaseFactory.Create(new() { DatabasePath = path });
        await new DatabaseInitializer(database).InitializeAsync(default);
        var reader = new TaskCenterReadRepository(database);
        var start = new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.FromHours(8));
        var rows = Enumerable.Range(0, 73).Select(i => new DispatchTaskEntity
        {
            TaskId = "RANGE-" + i.ToString("D3"), StationId = (i % 2 == 0 ? "CNC-A-" : "CNC-B-") + i,
            VehicleId = "AGV-" + i % 3, Priority = 0,
            Stage = i < 55 ? 90 : i < 61 ? 110 : i < 64 ? 10 : i < 68 ? 20 : i < 72 ? 100 : 999,
            CreatedAt = start.AddMinutes(i).LocalDateTime, UpdatedAt = start.AddMinutes(i + 5).LocalDateTime
        }).ToArray();
        await database.Insertable(rows).ExecuteCommandAsync();
        var snapshot = await reader.QueryAsync(new(), default);
        require(snapshot.Items.Count == 50 && snapshot.TotalCount == 73 &&
            snapshot.Summary is { Total: 73, Completed: 55, Cancelled: 6, Waiting: 3, Running: 4, Failed: 4, Other: 1 },
            "全量任务阶段统计覆盖73条，不受快照首50条限制");
        var second = await reader.QueryAsync(new(Page: 2), default);
        require(second.Items.Count == 23 && second.Summary == snapshot.Summary &&
            !snapshot.Items.Select(t => t.TaskId).Intersect(second.Items.Select(t => t.TaskId)).Any(),
            "历史第二页无重复，统计与总数跨页一致");
        require((await reader.QueryAsync(new(PageSize: 200), default)).Items.Count == 73,
            "后台支持200条一页，不截断原历史总数");
        var range = await reader.QueryAsync(new(From: start.AddMinutes(10), To: start.AddMinutes(20)), default);
        require(range.TotalCount == 10 && range.Items.Any(t => t.TaskId == "RANGE-010") &&
            range.Items.All(t => t.TaskId != "RANGE-020") && range.Summary is { Total: 10, Completed: 10 },
            "创建时间开始含边界、结束不含边界，统计仅计同一范围");
        var utc = await reader.QueryAsync(new(From: start.AddMinutes(10).ToUniversalTime(),
            To: start.AddMinutes(20).ToUniversalTime()), default);
        require(utc.Items.Select(t => t.TaskId).SequenceEqual(range.Items.Select(t => t.TaskId)),
            "ISO时间相同瞬间不同偏移得到相同查询结果");
        var combined = await reader.QueryAsync(new(TaskListFilter.Completed, "CNC-A", From: start.AddMinutes(10),
            To: start.AddMinutes(20), PageSize: 2), default);
        require(combined.Items.Count == 2 && combined.TotalCount == 5 && combined.Summary is { Total: 5, Completed: 5 },
            "状态、关键词、时间及分页共同作用于同一统计集合");
        var empty = await reader.QueryAsync(new(From: start.AddDays(1)), default);
        require(empty.TotalCount == 0 && empty.Items.Count == 0 && empty.Summary is { Total: 0, Other: 0 },
            "无匹配历史返回明确零统计");
        foreach (var bad in new[] { new TaskListQuery(From: start, To: start),
            new TaskListQuery(From: start.AddDays(1), To: start), new TaskListQuery(Page: 0),
            new TaskListQuery(PageSize: 201), new TaskListQuery(Filter: (TaskListFilter)999) })
        {
            var rejected = false;
            try { await reader.QueryAsync(bad, default); } catch (ArgumentException) { rejected = true; }
            require(rejected, "拒绝无效时间范围、页码、每页条数或状态，不查询数据库");
        }
    }
}
