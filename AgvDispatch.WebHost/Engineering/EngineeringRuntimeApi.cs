using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.WebHost.Engineering;

public static class EngineeringRuntimeApi
{
    public sealed record PreviewRequest(string VersionId);
    public sealed record ApplyRequest(string VersionId, string RequestId, string ExpectedRevision, string ExpectedCommunicationRevision,
        string ExpectedContentHash, string ExpectedMapHash, string ConfirmationText, bool ActionsReviewed);

    private static async Task<EngineeringRuntimePreview> Preview(string id, EngineeringStore store, DispatchPipe pipe, CancellationToken ct)
    {
        var version = await store.Version(id, ct);
        var workspace = await pipe.ReadAsync<MapWorkspaceSnapshot>(TrafficControlOperations.GetMapWorkspace, new { MapId = version.Plan.MapId }, ct)
            ?? throw new InvalidDataException("后台没有目标地图，请先导入并发布地图。");
        var parameters = await pipe.ReadAsync<TaskParameterSnapshot>(TrafficControlOperations.GetTaskParameters, new { }, ct)
            ?? throw new InvalidDataException("后台没有任务参数。");
        var communication = await pipe.ReadAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.GetCommunicationSettings, new { }, ct)
            ?? throw new InvalidDataException("后台没有通信配置。");
        var compiled = EngineeringRuntimeCompiler.Compile(version, workspace, parameters, communication);
        if (compiled.Ready && compiled.MapActive)
        {
            var validation = await pipe.ReadAsync<TaskParameterResult>(TrafficControlOperations.ValidateTaskParameters,
                new TaskParameterRequest(Guid.NewGuid().ToString("N"), parameters.Revision, compiled.MapId, compiled.MapVersionId, compiled.Configuration!), ct);
            var errors = compiled.Blockers.ToList();
            if (!parameters.CanSave) errors.Add(parameters.Explanation);
            if (validation is not { Accepted: true }) errors.Add(validation?.Message ?? "真实九步配置校验未返回结果。");
            compiled = compiled with { Blockers = errors };
        }
        return compiled;
    }
    public static void MapEngineeringRuntimeApi(this WebApplication app, bool enabled)
    {
        app.MapGet("/api/engineering-runtime/state", async (DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<TaskParameterSnapshot>(TrafficControlOperations.GetTaskParameters, new { }, ct));
        app.MapPost("/api/engineering-runtime/preview", async (PreviewRequest q, EngineeringStore store, DispatchPipe pipe, CancellationToken ct) =>
            await Preview(q.VersionId, store, pipe, ct));
        app.MapPost("/api/engineering-runtime/apply", async (ApplyRequest q, EngineeringStore store, DispatchPipe pipe, CancellationToken ct) =>
        {
            if (!enabled) return Results.Json(new { error = "工程执行参数应用未启用；模拟实例不允许应用现场参数。" }, statusCode: 403);
            if (!Guid.TryParseExact(q.RequestId, "N", out _) || !q.ActionsReviewed || q.ConfirmationText != "APPLY " + q.VersionId)
                return Results.Ok(new TaskParameterResult(false, "请核对四段车辆动作，并输入准确工程版本确认文字。", []));
            var preview = await Preview(q.VersionId, store, pipe, ct);
            if (!preview.Ready || !preview.MapActive) return Results.Ok(new TaskParameterResult(false, "未应用：" + string.Join("；", preview.Blockers.Append(preview.MapActive ? "" : "请先在地图管理激活绑定地图并完成安全切换，再重新预检。")), []));
            if (preview.CommunicationRevision != q.ExpectedCommunicationRevision || preview.ContentHash != q.ExpectedContentHash || preview.MapDefinitionHash != q.ExpectedMapHash)
                return Results.Ok(new TaskParameterResult(false, "地图、工程或通信配置已经变化，重新预检后再应用。", []));
            return Results.Ok(await pipe.ReadAsync<TaskParameterResult>(TrafficControlOperations.SaveTaskParameters,
                new TaskParameterRequest(q.RequestId, q.ExpectedRevision, preview.MapId, preview.MapVersionId, preview.Configuration!), ct));
        });
    }
}
