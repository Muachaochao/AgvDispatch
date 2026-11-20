using System.Text.Json;

namespace AgvDispatch.WebHost;

public static class SimulationApi
{
    public static void MapSimulationApi(this WebApplication app)
    {
        app.MapGet("/api/simulation", async (SimulationBridge bridge, CancellationToken ct) =>
            bridge.Enabled ? await bridge.ForwardAsync(HttpMethod.Get, "/status", null, ct) :
                Results.Ok(new { enabled = false, message = "尚未配置隔离仿真环境，请先创建仿真实验。现场通信配置不会被替换。" }));
        app.MapGet("/api/simulation/live", async (SimulationBridge bridge, CancellationToken ct) =>
            bridge.Enabled ? await bridge.ForwardAsync(HttpMethod.Get, "/live", null, ct) : Disabled());
        app.MapGet("/api/simulation/report", async (string? id, SimulationBridge bridge, CancellationToken ct) =>
            bridge.Enabled ? await bridge.ForwardAsync(HttpMethod.Get, id is null ? "/report" : "/report?id=" + id, null, ct) : Disabled());
        app.MapGet("/api/simulation/replay", async (string? id, SimulationBridge bridge, CancellationToken ct) =>
            bridge.Enabled ? await bridge.ForwardAsync(HttpMethod.Get, id is null ? "/replay" : "/replay?id=" + id, null, ct) : Disabled());
        app.MapGet("/api/simulation/archive", async (string? id, SimulationBridge bridge, CancellationToken ct) =>
        {
            if (!bridge.Enabled) return Disabled();
            if (string.IsNullOrWhiteSpace(id)) return Results.BadRequest(new { error = "导出证据包需要报告编号。" });
            return await bridge.ForwardArchiveAsync("/archive?id=" + id, "simulation-evidence-" + id + ".zip", ct);
        });
        app.MapGet("/api/simulation/point-table", async (SimulationBridge bridge, CancellationToken ct) =>
            bridge.Enabled ? await bridge.ForwardAsync(HttpMethod.Get, "/point-table", null, ct) : Disabled());
        app.MapGet("/api/simulation/snapshot", async (SimulationBridge bridge, CancellationToken ct) =>
            bridge.Enabled ? await bridge.ForwardAsync(HttpMethod.Get, "/snapshot", null, ct) : Disabled());
        app.MapGet("/api/simulation/dispatch-pipeline", async (SimulationBridge bridge, CancellationToken ct) =>
            bridge.Enabled ? await bridge.ForwardAsync(HttpMethod.Get, "/dispatch-pipeline", null, ct) : Disabled());
        app.MapGet("/api/simulation/dispatch-optimization", async (SimulationBridge bridge, CancellationToken ct) =>
            bridge.Enabled ? await bridge.ForwardAsync(HttpMethod.Get, "/dispatch-optimization", null, ct) : Disabled());
        app.MapPost("/api/simulation/dispatch-optimization/options", async (JsonElement options, SimulationBridge bridge, CancellationToken ct) =>
            bridge.Enabled ? await bridge.ForwardAsync(HttpMethod.Post, "/dispatch-optimization/options", options.GetRawText(), ct) : Disabled());
        app.MapGet("/api/simulation/resources", async (SimulationBridge bridge, CancellationToken ct) =>
            bridge.Enabled ? await bridge.ForwardAsync(HttpMethod.Get, "/resources", null, ct) : Disabled());
        app.MapGet("/api/simulation/tasks", async (string? filter, string? search, int? page, int? pageSize,
            SimulationBridge bridge, CancellationToken ct) =>
        {
            if (!bridge.Enabled) return Disabled();
            filter ??= "All";
            if (filter is not ("All" or "Active" or "Completed" or "Failed"))
                return Results.BadRequest(new { error = "任务筛选无效。" });
            page ??= 1;
            pageSize ??= 200;
            if (page < 1 || pageSize is < 1 or > 200)
                return Results.BadRequest(new { error = "任务分页参数无效。" });
            var query = $"?filter={Uri.EscapeDataString(filter)}";
            if (!string.IsNullOrWhiteSpace(search))
                query += "&search=" + Uri.EscapeDataString(search.Trim());
            query += $"&page={page}&pageSize={pageSize}";
            return await bridge.ForwardAsync(HttpMethod.Get, "/tasks" + query, null, ct);
        });
        app.MapGet("/api/simulation/tasks/{id}", async (string id, SimulationBridge bridge, CancellationToken ct) =>
        {
            if (!bridge.Enabled) return Disabled();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_')))
                return Results.BadRequest(new { error = "任务编号无效。" });
            return await bridge.ForwardAsync(HttpMethod.Get, "/task?id=" + Uri.EscapeDataString(id), null, ct);
        });
        app.MapGet("/api/simulation/timeout-recovery/{id}", async (string id, SimulationBridge bridge, CancellationToken ct) =>
        {
            if (!bridge.Enabled) return Disabled();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_')))
                return Results.BadRequest(new { error = "任务编号无效。" });
            return await bridge.ForwardAsync(HttpMethod.Get, "/timeout-recovery?id=" + Uri.EscapeDataString(id), null, ct);
        });
        app.MapPost("/api/simulation/timeout-recovery/{id}/execute", async (string id, HttpRequest request,
            SimulationBridge bridge, CancellationToken ct) =>
        {
            if (!bridge.Enabled) return Disabled();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_')))
                return Results.BadRequest(new { error = "任务编号无效。" });
            if (request.ContentLength == 0) return Results.BadRequest(new { error = "必须提交人工恢复核对条件。" });
            using var json = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "人工恢复参数必须为JSON对象。" });
            return await bridge.ForwardAsync(HttpMethod.Post, "/timeout-recovery-execute", json.RootElement.GetRawText(), ct);
        });
        app.MapPost("/api/simulation/{action}", async (string action, HttpRequest request, SimulationBridge bridge, CancellationToken ct) =>
        {
            if (!bridge.Enabled) return Disabled();
            if (action is not ("start" or "stop" or "run" or "abort" or "control" or
                "new-instance" or "configure" or "recover" or "recover-release" or "protocol-start" or "protocol-stop" or
                "equipment-start" or "equipment-control" or "equipment-stop" or "scheme-save" or "scheme-select" or "scheme-start" or
                "scheme-delete" or "point-table-validate"))
                return Results.NotFound();
            var body = "{}";
            if (request.ContentLength != 0)
            {
                using var json = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
                if (json.RootElement.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { error = "仿真操作参数必须为JSON对象。" });
                body = json.RootElement.GetRawText();
            }
            return await bridge.ForwardAsync(HttpMethod.Post, "/" + action, body, ct);
        });
    }

    private static IResult Disabled() => Results.Json(new { error = "隔离仿真环境未启用。" }, statusCode: 403);
}
