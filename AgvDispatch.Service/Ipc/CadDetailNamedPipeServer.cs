using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

/// <summary>Dedicated read-only display endpoint; slow CAD rendering cannot block business IPC.</summary>
public sealed class CadDetailNamedPipeServer(TrafficSecurityOptions options, ICadDetailService details,
    IDatabaseInitializer database, NamedPipeDiagnostics diagnostics, ILogger<CadDetailNamedPipeServer> logger) : IsolatedBackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public override async Task StartAsync(CancellationToken ct)
    { await database.InitializeAsync(ct); await base.StartAsync(ct); }
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, 3).Select(_ => ListenAsync(stoppingToken)));

    private async Task ListenAsync(CancellationToken ct)
    {
        var reportedFailure = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct);
                reportedFailure = false;
                diagnostics.ConnectionOpened();
                try { await HandleAsync(pipe, ct); }
                finally { diagnostics.ConnectionClosed(); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (IOException e)
            { logger.LogDebug("CAD显示客户端已断开：{Error}", e.Message); await Task.Delay(250, ct); }
            catch (Exception e)
            {
                if (!reportedFailure) logger.LogWarning("CAD显示管道失败：{Error}；后台低频重试。", e.Message);
                reportedFailure = true;
                await Task.Delay(1000, ct);
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        // A non-elevated console host also needs permission to create additional listener instances.
        using var owner = WindowsIdentity.GetCurrent();
        if (owner.User is { } sid) security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(options.PipeName + ".cad-detail", PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough, 0, 0, security);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var identity = "(unresolved)";
        TrafficControlPipeResponse response;
        try
        {
            var header = new byte[4]; await pipe.ReadExactlyAsync(header, ct);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > 65536) throw new InvalidDataException("CAD显示请求大小无效。");
            var bytes = new byte[length]; await pipe.ReadExactlyAsync(bytes, ct);
            identity = pipe.GetImpersonationUserName();
            var request = JsonSerializer.Deserialize<TrafficControlPipeRequest>(bytes, JsonOptions)!;
            if (request.Operation != "GetCadDetail") throw new InvalidOperationException("此管道仅支持只读CAD局部显示。");
            var detail = await details.GetAsync(request.Payload.Deserialize<CadDetailRequest>(JsonOptions)!, ct);
            response = new(true, null, JsonSerializer.SerializeToElement(detail, JsonOptions));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { response = new(false, "视口请求已被更新的请求替代。", JsonSerializer.SerializeToElement(new { })) { ErrorCode = "Superseded" }; }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { throw; }
        catch (Exception e)
        { response = new(false, e.Message, JsonSerializer.SerializeToElement(new { })); }
        diagnostics.RecordRequest(identity, "GetCadDetail", response.Success);
        var body = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        var responseHeader = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(responseHeader, body.Length);
        await pipe.WriteAsync(responseHeader, ct); await pipe.WriteAsync(body, ct); await pipe.FlushAsync(ct);
    }
}
