using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.WebHost;

public static class AlarmApi
{
    public sealed record WebAcknowledgeAlarmRequest(string AlarmId, string Comment, bool Confirmed);

    public static void MapAlarmApi(this WebApplication app, bool actionsEnabled)
    {
        app.MapPost("/api/alarms/{id}/acknowledge", async (
            string id,
            WebAcknowledgeAlarmRequest request,
            DispatchPipe pipe,
            CancellationToken ct) =>
        {
            if (!actionsEnabled)
            {
                return Results.Json(
                    new { error = "Web告警确认未启用；当前只能只读查看。" },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
            {
                return Results.BadRequest(new { error = "告警编号无效。" });
            }
            if (!string.Equals(request.AlarmId, id, StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "告警身份不一致，请刷新后重新核对。" });
            }
            var comment = request.Comment?.Trim() ?? "";
            if (comment.Length is < 2 or > 1000)
            {
                return Results.BadRequest(new { error = "确认说明必须为2至1000个字符。" });
            }
            if (!request.Confirmed)
            {
                return Results.BadRequest(new { error = "必须明确确认：确认告警不等于恢复条件或解除任务责任。" });
            }

            var result = await pipe.ReadAsync<AlarmOperationResult>(
                TrafficControlOperations.AcknowledgeAlarm,
                new AcknowledgeAlarmRequest(id, comment),
                ct);
            return Results.Ok(result);
        });
    }
}
