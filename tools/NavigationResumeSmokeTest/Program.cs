using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Tasks;

var checks = 0;
void Check(bool valid, string name) { if (!valid) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string name)
{
    try { await action(); } catch (InvalidOperationException) { Check(true, name); return; }
    throw new Exception(name);
}

await using var robot = new Robot();
var options = new AgvOptions { Enabled = true, AllowCommands = true, RequestIntervalMs = 100, RequestTimeoutMs = 800,
    ConnectTimeoutMs = 800, Robots = [new() { VehicleId = "SIM", IpAddress = "127.0.0.1", Port = robot.StatusPort, NavigationPort = robot.NavigationPort }] };
await using var connections = new AgvConnectionManager(options);
var folder = Path.Combine(Path.GetTempPath(), "agv-resume-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var path = Path.Combine(folder, "navigation.json");
var suspended = new NavigationTestRecord("TASK", "VENDOR-1", "SIM", "A", "B", "map", new string('a', 32),
    "Suspended", "车端暂停", DateTimeOffset.Now, "isolated")
    { NavigationApi = 3051, Route = ["A", "B"], VendorTaskIds = ["VENDOR-1"] };
File.WriteAllText(path, JsonSerializer.Serialize(suspended));
try
{
    var service = new SingleNavigationTestService(new SingleNavigationTestOptions
        { Enabled = true, VehicleId = "SIM", BusinessExecutionDisabled = true }, options, connections,
        new Tasks(), new Gate(), path);
    var user = TrafficUserContext.LocalOperation("isolated");
    var status = await service.ReadAsync(default);
    Check(status.Current?.State == "Suspended", "fresh original task is reconciled as suspended");
    robot.Blocked = true;
    await Reject(() => service.ResumeAsync(user, new("TASK"), default), "blocked or 52200 state cannot send resume");
    Check(robot.Resumes == 0, "blocked state sends no 3002");
    robot.Blocked = false;
    var resumed = await service.ResumeAsync(user, new("TASK"), default);
    Check(resumed.State == "ResumeRequested" && robot.Resumes == 1 && robot.LastTaskRunning,
        "clear safe state sends exactly one 3002 for the original task");
    await Reject(() => service.ResumeAsync(user, new("TASK"), default), "repeated resume cannot send 3002 twice");
    Check(robot.Resumes == 1, "one shot resume survives repeated request");
    status = await service.ReadAsync(default);
    Check(status.Current?.State == "Running", "same original task is observed running after resume");
    Console.WriteLine($"PASS {checks} navigation resume checks; loopback only, no real vehicle.");
}
finally
{
    Directory.Delete(folder, true);
}

sealed class Tasks : IActiveTaskLookup
{
    public Task<IReadOnlyList<ActiveDispatchTask>> GetActiveTasksAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ActiveDispatchTask>>([]);
}

sealed class Gate : IMapExecutionGate
{
    public MapExecutionMode Mode => MapExecutionMode.Running;
    public bool CanStartNewWork => true;
    public ValueTask<IDisposable?> EnterAsync(MapExecutionOperation operation, CancellationToken ct) =>
        ValueTask.FromResult<IDisposable?>(new Lease());
    public ValueTask<IDisposable> EnterControlAsync(CancellationToken ct) => ValueTask.FromResult<IDisposable>(new Lease());
    public void SetMode(MapExecutionMode mode) { }
    sealed class Lease : IDisposable { public void Dispose() { } }
}

sealed class Robot : IAsyncDisposable
{
    readonly TcpListener status = new(IPAddress.Loopback, 0), navigation = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new(); readonly ConcurrentBag<Task> workers = [];
    public bool Blocked, LastTaskRunning; public int Resumes;
    public int StatusPort => ((IPEndPoint)status.LocalEndpoint).Port;
    public int NavigationPort => ((IPEndPoint)navigation.LocalEndpoint).Port;
    public Robot() { status.Start(); navigation.Start(); workers.Add(Accept(status)); workers.Add(Accept(navigation)); }
    async Task Accept(TcpListener listener)
    {
        try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); workers.Add(Serve(client)); } }
        catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
    }
    async Task Serve(TcpClient client)
    {
        using (client) try
        {
            var stream = client.GetStream();
            while (!stop.IsCancellationRequested)
            {
                var header = new byte[16]; await stream.ReadExactlyAsync(header, stop.Token);
                var size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
                var api = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8));
                var body = new byte[size]; await stream.ReadExactlyAsync(body, stop.Token);
                object response = api switch
                {
                    1300 => new { current_map = "map", current_map_md5 = new string('a', 32) },
                    1022 => new { loadmap_status = 1 },
                    1060 => new { locked = false },
                    1301 => new { stations = new[] { new { id = "A", x = 0d, y = 0d }, new { id = "B", x = 1d, y = 0d } } },
                    1100 => new { x = 0d, y = 0d, confidence = .9, current_station = "A", emergency = false,
                        blocked = Blocked, charging = false, vx = 0d, vy = 0d, w = 0d },
                    1110 => new { task_status_package = new { task_status_list = new[] { new { task_id = "VENDOR-1", status = LastTaskRunning ? 2 : 3 } } } },
                    3002 => Resume(),
                    _ => new { ret_code = 999 }
                };
                var text = JsonSerializer.SerializeToUtf8Bytes(response); var reply = (byte[])header.Clone();
                BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(4), text.Length);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8), (ushort)(api + 10000));
                await stream.WriteAsync(reply, stop.Token); await stream.WriteAsync(text, stop.Token);
            }
        }
        catch (OperationCanceledException) { } catch (IOException) { }
    }
    object Resume() { Resumes++; LastTaskRunning = true; return new { ret_code = 0 }; }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); status.Stop(); navigation.Stop(); await Task.WhenAll(workers.ToArray());
        stop.Dispose();
    }
}
