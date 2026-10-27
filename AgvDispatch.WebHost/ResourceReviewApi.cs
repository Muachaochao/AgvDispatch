using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.WebHost;
public static class ResourceReviewApi
{
    public static void MapResourceReviewApi(this WebApplication app, bool enabled)
    {
        app.MapGet("/api/resource-review", async (DispatchPipe p, CancellationToken ct) => new
        {
            enabled,
            session = await p.ReadAsync<TrafficControlSession>(TrafficControlOperations.GetSession, new { }, ct),
            resources = await p.ReadAsync<TrafficResourceSnapshot[]>(TrafficControlOperations.GetResources, new { }, ct),
            requests = await p.ReadAsync<ManualReleaseWorkflowItem[]>(TrafficControlOperations.GetReleaseRequests, new { maximumCount = 100 }, ct),
            audits = await p.ReadAsync<TrafficResourceReleaseAuditSnapshot[]>(TrafficControlOperations.GetAudits, new { maximumCount = 100 }, ct)
        });
        app.MapPost("/api/resource-review/submit", async (SubmitManualReleaseRequest q, DispatchPipe p, CancellationToken ct) => enabled
            ? Results.Ok(await p.ReadAsync<ManualReleaseWorkflowResult>(TrafficControlOperations.SubmitRelease, q, ct))
            : Results.Json(new { error = "Web资源处置未启用；请使用允许恢复处置的启动配置。" }, statusCode: 403));
        app.MapPost("/api/resource-review/review", async (ReviewManualReleaseRequest q, DispatchPipe p, CancellationToken ct) => enabled
            ? Results.Ok(await p.ReadAsync<ManualReleaseWorkflowResult>(TrafficControlOperations.ReviewRelease, q, ct))
            : Results.Json(new { error = "Web资源处置未启用。" }, statusCode: 403));
    }
}
