using AgvDispatch.Application.Abstractions;
using AgvDispatch.Service;
using AgvDispatch.Wpf.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using System.Buffers.Binary;
using System.Text.Json;

var name = "agv-cad-test-" + Guid.NewGuid().ToString("N");
var fake = new FakeDetails();
using var server = new CadDetailNamedPipeServer(new TrafficSecurityOptions { PipeName = name }, fake,
    new FakeDatabase(), new NamedPipeDiagnostics(), new TestLogger());
await server.StartAsync(CancellationToken.None);
try
{
    using var client = new NamedPipeTrafficControlClient(name);
    var request = new CadDetailRequest("slow", "base.png", new(0, 0, 10, 10), 640, 480);
    var slow = client.GetCadDetailAsync(request, CancellationToken.None);
    await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var quick = await client.GetCadDetailAsync(request with { MapId = "quick" }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    if (quick.PreviewPngPath != "detail-quick.png" || slow.IsCompleted) throw new Exception("Slow CAD endpoint blocked another display request.");
    await using var raw = new NamedPipeClientStream(".", name + ".cad-detail", PipeDirection.InOut, PipeOptions.Asynchronous,
        System.Security.Principal.TokenImpersonationLevel.Identification);
    await raw.ConnectAsync(3000);
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var payload = JsonSerializer.SerializeToUtf8Bytes(new TrafficControlPipeRequest("PublishMap", JsonSerializer.SerializeToElement(new { })), jsonOptions);
    var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
    await raw.WriteAsync(header); await raw.WriteAsync(payload); await raw.FlushAsync();
    await raw.ReadExactlyAsync(header);
    var responseBytes = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)]; await raw.ReadExactlyAsync(responseBytes);
    var response = JsonSerializer.Deserialize<TrafficControlPipeResponse>(responseBytes, jsonOptions)!;
    if (response.Success || fake.Calls != 2) throw new Exception("CAD endpoint accepted a write operation.");
    fake.Release.SetResult(); await slow.WaitAsync(TimeSpan.FromSeconds(3));
    Console.WriteLine("PASS: actual Windows CAD pipe round-trip, isolated suffix, concurrent slow/fast requests, write operation rejected; no real devices or production database.");
}
finally { fake.Release.TrySetResult(); await server.StopAsync(CancellationToken.None); }

sealed class FakeDatabase : IDatabaseInitializer
{
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<bool> CanConnectAsync(CancellationToken ct) => Task.FromResult(true);
}
sealed class FakeDetails : ICadDetailService
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Calls;
    public async Task<CadDetailResult> GetAsync(CadDetailRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        Console.WriteLine("Received " + request.MapId);
        if (request.MapId == "slow") { Started.TrySetResult(); await Release.Task.WaitAsync(ct); }
        return new("detail-" + request.MapId + ".png", new(0, 0, 10, 10, 640, 480));
    }
}
sealed class TestLogger : Microsoft.Extensions.Logging.ILogger<CadDetailNamedPipeServer>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Console.WriteLine(formatter(state, exception));
}
