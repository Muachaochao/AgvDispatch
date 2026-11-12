using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Wpf.Management;

// This tool observes the backend. Business mutations stay in the Web engineering flow.
public sealed class LocalBackendClient(string pipeName)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public Task<SystemStatusSnapshot> ReadStatusAsync(CancellationToken ct = default) => ReadAsync<SystemStatusSnapshot>(TrafficControlOperations.GetSystemStatus, ct);
    public Task<CommunicationSettingsSnapshot> ReadConnectionsAsync(CancellationToken ct = default) => ReadAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.GetCommunicationSettings, ct);
    public Task<ChargingStatus> ReadChargingAsync(CancellationToken ct = default) => ReadAsync<ChargingStatus>("GetCharging", ct);

    private async Task<T> ReadAsync<T>(string operation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token);
        var request = JsonSerializer.SerializeToUtf8Bytes(new TrafficControlPipeRequest(operation, JsonSerializer.SerializeToElement(new { }, Json)), Json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, request.Length);
        await pipe.WriteAsync(header, timeout.Token);
        await pipe.WriteAsync(request, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        await pipe.ReadExactlyAsync(header, timeout.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException("后台帧长度无效。");
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, timeout.Token);
        var response = JsonSerializer.Deserialize<TrafficControlPipeResponse>(body, Json) ?? throw new InvalidDataException("后台响应为空。");
        if (!response.Success) throw new InvalidOperationException(response.Error ?? "后台拒绝读取状态。");
        return response.Payload.Deserialize<T>(Json) ?? throw new InvalidDataException("后台状态为空。");
    }
}
