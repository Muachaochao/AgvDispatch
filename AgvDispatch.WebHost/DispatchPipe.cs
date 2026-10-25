using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.WebHost;

// Only the explicit HTTP handlers below can choose an operation. No generic pipe proxy is exposed.
public sealed class DispatchPipe(string pipeName)
{
    public async Task<T?> ReadAsync<T>(string operation, object payload, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough, TokenImpersonationLevel.Identification);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
        {
            // The service may still be finishing database and device initialization. A
            // cancelled half-open connection can occupy the single server pipe instance,
            // so allow enough time for the observed startup path to settle.
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            try { await pipe.ConnectAsync(connect.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("无法连接调度后台，请确认主服务已启动及Named Pipe名称一致。"); }
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new TrafficControlPipeRequest(operation,
            JsonSerializer.SerializeToElement(payload, WebJson.Options)), WebJson.Options);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await pipe.WriteAsync(header, timeout.Token);
        await pipe.WriteAsync(bytes, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        await pipe.ReadExactlyAsync(header, timeout.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException("后台帧大小超出限制。");
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, timeout.Token);
        var result = JsonSerializer.Deserialize<TrafficControlPipeResponse>(body, WebJson.Options)
            ?? throw new InvalidDataException("后台返回空响应。");
        if (!result.Success)
        {
            if (result.ErrorCode == "Unauthorized") throw new UnauthorizedAccessException(result.Error);
            throw new InvalidOperationException(result.Error ?? "后台拒绝请求。");
        }
        return result.Payload.Deserialize<T>(WebJson.Options);
    }
}
