using System.Net;
using System.IO;
using System.Text.Json;

namespace PlcHandshakeLab;

// Fixed loopback endpoint, no CORS and a non-simple header. Browsers cannot silently
// issue a write via forms. It operates only the already running simulator instance.
public sealed class LabControlServer(NetworkLab lab, int port = 10201) : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private Task? worker;
    public void Start()
    {
        if (lab.Config.BindAddress != "127.0.0.1" || lab.Config.Port != 10200 || lab.Config.AllowedClient != "127.0.0.1") return;
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start(); worker = RunAsync();
    }
    private sealed record Plan(string RunId, string[] Stations, int Rounds, bool Stop, bool ResumeAfterCharge = false);
    private async Task RunAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
            try
            {
                var request = context.Request;
                var capabilities = request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/capabilities";
                if ((!capabilities && (request.HttpMethod != "POST" || request.Url?.AbsolutePath is not ("/cycle" or "/cycle-feedback" or "/fleet" or "/fleet-feedback"))) ||
                    request.Headers["X-Agv-Lab-Control"] != "business-run-v1" ||
                    request.Headers["Origin"] is not null || request.RemoteEndPoint is null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address) ||
                    request.ContentLength64 > 4096)
                { context.Response.StatusCode = 403; continue; }
                if (capabilities)
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"boxFeedbackContract\":\"business-box-action-v1\",\"fleetContract\":\"fleet-box-action-v1\",\"chargeResumeContract\":\"business-charge-resume-v1\"}"));
                    continue;
                }
                // JsonContent may use chunked transfer. Bound the decoded body too.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var body = new MemoryStream();
                var buffer = new byte[1024];
                int count;
                while ((count = await request.InputStream.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    if (body.Length + count > 4096) throw new ArgumentException("控制参数超过4096字节。");
                    body.Write(buffer, 0, count);
                }
                if(request.Url!.AbsolutePath=="/fleet")
                    lab.ControlFleet(JsonSerializer.Deserialize<FleetLabRequest>(body.ToArray(),new JsonSerializerOptions(JsonSerializerDefaults.Web))??throw new ArgumentException("双车参数为空。"));
                else if(request.Url.AbsolutePath=="/fleet-feedback")
                    lab.ApplyFleetFeedback(JsonSerializer.Deserialize<CycleTransportFeedback>(body.ToArray(),new JsonSerializerOptions(JsonSerializerDefaults.Web))??throw new ArgumentException("双车反馈为空。"));
                else if (request.Url.AbsolutePath == "/cycle-feedback")
                    lab.ApplyTransportFeedback(JsonSerializer.Deserialize<CycleTransportFeedback>(body.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        ?? throw new ArgumentException("动作反馈为空。"));
                else
                {
                    var plan = JsonSerializer.Deserialize<Plan>(body.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        ?? throw new ArgumentException("控制参数为空。");
                    lab.ConfigureCalls(plan.RunId, plan.Stations, plan.Rounds, plan.Stop, plan.ResumeAfterCharge);
                }
                context.Response.StatusCode = 200;
            }
            catch (Exception e)
            {
                context.Response.StatusCode = 409;
                var bytes = System.Text.Encoding.UTF8.GetBytes(e.Message);
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            finally { context.Response.Close(); }
        }
    }
    public async ValueTask DisposeAsync()
    { listener.Close(); if (worker is not null) await worker; }
}
