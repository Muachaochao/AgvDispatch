using System.Text.Json;

namespace AgvDispatch.WebHost;

public static class SimulationApi
{
    public static void MapSimulationApi(this WebApplication app)
    {
        app.MapGet("/api/simulation", async (SimulationBridge bridge, CancellationToken ct) =>
            bridge.Enabled ? await bridge.ForwardAsync(HttpMethod.Get, "/status", null, ct) :
                Results.Ok(new { enabled = false, message = "尚未配置隔离仿真环境，请先创建仿真实验。现场通信配置不会被替换。" }));
        app.MapGet("/api/simulation/report", async (SimulationBridge bridge, CancellationToken ct) =>
            bridge.Enabled ? await bridge.ForwardAsync(HttpMethod.Get, "/report", null, ct) : Disabled());
        app.MapPost("/api/simulation/{action}", async (string action, HttpRequest request, SimulationBridge bridge, CancellationToken ct) =>
        {
            if (!bridge.Enabled) return Disabled();
            if (action is not ("start" or "stop" or "run" or "abort" or "control" or
                "new-instance" or "configure" or "recover" or "recover-release" or "protocol-start" or "protocol-stop" or
                "equipment-start" or "equipment-control" or "equipment-stop"))
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
