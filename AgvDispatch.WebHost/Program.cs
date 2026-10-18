using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.WebHost;

if(args.Contains("--launch-local")){Environment.ExitCode=await WebLocalLauncher.RunAsync(args);return;}

var builder = WebApplication.CreateBuilder(args);
// This first local adapter deliberately has one loopback listener, independent of ASPNETCORE_URLS.
var port = builder.Configuration.GetValue("Web:Port", 5188);
if (port is < 1024 or > 65535) throw new InvalidOperationException("Web:Port 必须为1024至65535。");
builder.WebHost.UseKestrel(o => { o.Listen(IPAddress.Loopback, port); o.Limits.MaxRequestBodySize = 8 * 1024 * 1024; });
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton(new DispatchPipe(builder.Configuration["Web:PipeName"] ?? "AgvDispatch.Service"));
builder.Services.AddSingleton<SnapshotReader>();
var projectStorePath=Path.GetFullPath(builder.Configuration["Web:ProjectStorePath"]??Path.Combine(builder.Environment.ContentRootPath,"data","projects"));
builder.Services.AddSingleton(new WebProjectStore(projectStorePath));
var app = builder.Build();
var submissions = builder.Configuration.GetValue("Web:EnableTaskSubmission", false);
var webRoot = Path.GetFullPath(builder.Configuration["Web:StaticPath"] ?? Path.Combine(app.Environment.ContentRootPath, "..", "AgvDispatch.Web", "dist"));

app.Use(async (ctx, next) =>
{
    var expected = $"http://127.0.0.1:{port}";
    var host = ctx.Request.Host;
    var origin = ctx.Request.Headers.Origin.ToString();
    var fetchSite = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
    // A link opened from another page may navigate to the static landing page. API access stays same-origin.
    var landingNavigation = HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Path == "/" &&
        ctx.Request.Headers["Sec-Fetch-Mode"] == "navigate" && ctx.Request.Headers["Sec-Fetch-Dest"] == "document";
    if (host.Host != "127.0.0.1" || host.Port != port ||
        ctx.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
        (origin.Length > 0 && origin != expected) || (!landingNavigation && fetchSite.Length > 0 && fetchSite is not ("same-origin" or "none")))
    { ctx.Response.StatusCode = 403; return; }
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'";
    try { await next(); }
    catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested && !ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = ex is ProjectConflictException ? 409 : ex is ArgumentException or InvalidDataException or JsonException or KeyNotFoundException or FormatException ? 400 : ex is UnauthorizedAccessException ? 403 : ex is OperationCanceledException or TimeoutException ? 504 : 503;
        await ctx.Response.WriteAsJsonAsync(new { error = "后台不可用或拒绝请求", detail = ex.Message }, ctx.RequestAborted);
    }
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.MapGet("/api/config", () => new { taskSubmissionEnabled = submissions, source = "backend", loopbackOnly = true,
    projectStoreId=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(projectStorePath))),
    adapter = new { informationalVersion=AdapterBuildIdentity.InformationalVersion, sha256=AdapterBuildIdentity.Sha256 },
    servicePipeName = builder.Configuration["Web:PipeName"] ?? "AgvDispatch.Service" });
app.MapEditorApi();
app.MapEngineeringApi();
app.MapProjectApi();
app.MapDiagnosticsApi(builder.Configuration.GetValue("Web:EnableRecoveryActions",false),submissions);
app.MapGet("/api/snapshot", async (SnapshotReader reader, CancellationToken ct) => await reader.ReadAsync(ct));
app.MapGet("/api/task-options", async (DispatchPipe pipe, CancellationToken ct) =>
    await pipe.ReadAsync<ManualTaskCreationOptions>(TrafficControlOperations.GetTaskCreationOptions, new { }, ct));
app.MapGet("/api/tasks", async (string? filter, string? search, int? page, DispatchPipe pipe, CancellationToken ct) =>
{
    if (!Enum.TryParse<TaskListFilter>(filter ?? "All", true, out var parsed) || !Enum.IsDefined(parsed) ||
        page is < 1 or > 10000 || (search?.Length ?? 0) > 128) return Results.BadRequest(new { error = "任务筛选或页码无效。" });
    return Results.Ok(await pipe.ReadAsync<TaskListPage>(TrafficControlOperations.GetTasks, new TaskListQuery(parsed, search ?? "", page ?? 1, 50), ct));
});
app.MapGet("/api/maps", async (DispatchPipe pipe, CancellationToken ct) =>
{
    var maps = await pipe.ReadAsync<MapProjectSummary[]>(TrafficControlOperations.GetMaps, new { }, ct);
    // Do not expose source/preview filesystem paths to the browser.
    return maps?.Select(m => new { m.MapId, m.Name, m.Description, m.CoordinateSystem, m.Unit, m.IsActive,
        m.ActiveVersionId, m.PublishedVersionId, m.UpdatedAt }).ToArray() ?? [];
});
app.MapGet("/api/tasks/{id}", async (string id, DispatchPipe pipe, CancellationToken ct) =>
    await pipe.ReadAsync<TaskCenterDetail>(TrafficControlOperations.GetTaskDetail, new TaskDetailRequest(id, false), ct));
app.MapPost("/api/route-preview", async (ManualTaskRoutePreviewRequest request, DispatchPipe pipe, CancellationToken ct) =>
    await pipe.ReadAsync<ManualTaskRoutePreview>(TrafficControlOperations.GetTaskRoutePreview, request, ct));
app.MapPost("/api/tasks", async (ManualTaskCreationRequest request, DispatchPipe pipe, CancellationToken ct) =>
{
    if (!submissions) return Results.Json(new { error = "本接入版未开启任务提交，请在WPF中操作或明确配置 Web:EnableTaskSubmission。" }, statusCode: 403);
    // Existing backend authorization, idempotency, map preflight and execution gate remain authoritative.
    return Results.Ok(await pipe.ReadAsync<ManualTaskCreationResult>(TrafficControlOperations.CreateTask, request, ct));
});
app.MapGet("/api/ws", async (HttpContext ctx, SnapshotReader reader) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
    async Task ReceiveCloseAsync()
    {
        try { var buffer = new byte[256]; while (socket.State == WebSocketState.Open) {
            var result = await socket.ReceiveAsync(buffer, lifetime.Token);
            if (result.MessageType == WebSocketMessageType.Close) break;
            // Client messages are not a command channel.
            if (!result.EndOfMessage) break;
        } } catch (Exception) when (lifetime.IsCancellationRequested || socket.State != WebSocketState.Open) { }
        finally { await lifetime.CancelAsync(); }
    }
    var receiver = ReceiveCloseAsync();
    try
    {
        while (!lifetime.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            WebSnapshot snapshot;
            try { snapshot = await reader.ReadAsync(lifetime.Token); }
            catch (Exception ex) when (!lifetime.IsCancellationRequested)
            { snapshot = new(DateTimeOffset.UtcNow, false, "backend", ex.Message, null, null, null, null, null, []); }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, WebJson.Options);
            if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Web 快照超出8MiB预算。");
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, lifetime.Token);
            await Task.Delay(2000, lifetime.Token);
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
    finally { await lifetime.CancelAsync(); try { await receiver; } catch (WebSocketException) { }
        if (socket.State == WebSocketState.CloseReceived) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); }
});
if (Directory.Exists(webRoot))
{
    var provider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
}
app.MapGet("/health", () => new { status = "ok", role = "web-adapter", taskSubmissionEnabled = submissions });
app.Run();

public partial class Program { }
