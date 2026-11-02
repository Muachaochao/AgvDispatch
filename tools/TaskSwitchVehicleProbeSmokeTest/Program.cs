using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgvDispatch.Connectors.Agv;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
    Console.WriteLine("PASS " + name);
}

const string lowered = "{\"ret_code\":0,\"jack_enable\":true,\"jack_emc\":false,\"jack_error_code\":0,\"jack_state\":3,\"jack_isFull\":false}";
var cases = new (string Name, string? Response, bool Ready, string? Blocker)[]
{
    ("explicit-empty-lowered", lowered, true, null),
    ("optional-ret-code-omitted", lowered.Replace("\"ret_code\":0,", ""), true, null),
    ("loaded", lowered.Replace("\"jack_isFull\":false", "\"jack_isFull\":true"), false, "有载荷"),
    ("raising", lowered.Replace("\"jack_state\":3", "\"jack_state\":0"), false, "上升"),
    ("raised", lowered.Replace("\"jack_state\":3", "\"jack_state\":1"), false, "上升到位"),
    ("lowering", lowered.Replace("\"jack_state\":3", "\"jack_state\":2"), false, "正在下降"),
    ("stopped-not-lowered", lowered.Replace("\"jack_state\":3", "\"jack_state\":4"), false, "未确认下降"),
    ("failed", lowered.Replace("\"jack_state\":3", "\"jack_state\":255"), false, "执行失败"),
    ("unknown-state", lowered.Replace("\"jack_state\":3", "\"jack_state\":8"), false, "未知"),
    ("disabled-jack", lowered.Replace("\"jack_enable\":true", "\"jack_enable\":false"), false, "未启用"),
    ("emergency", lowered.Replace("\"jack_emc\":false", "\"jack_emc\":true"), false, "急停"),
    ("jack-error", lowered.Replace("\"jack_error_code\":0", "\"jack_error_code\":4"), false, "错误码"),
    ("missing-load", lowered.Replace(",\"jack_isFull\":false", ""), false, "jack_isFull"),
    ("missing-enable", lowered.Replace("\"jack_enable\":true,", ""), false, "jack_enable"),
    ("missing-emergency", lowered.Replace("\"jack_emc\":false,", ""), false, "jack_emc"),
    ("missing-state", lowered.Replace("\"jack_state\":3,", ""), false, "jack_state"),
    ("missing-error", lowered.Replace("\"jack_error_code\":0,", ""), false, "jack_error_code"),
    ("string-load", lowered.Replace("\"jack_isFull\":false", "\"jack_isFull\":\"false\""), false, "布尔"),
    ("null-load", lowered.Replace("\"jack_isFull\":false", "\"jack_isFull\":null"), false, "布尔"),
    ("string-state", lowered.Replace("\"jack_state\":3", "\"jack_state\":\"3\""), false, "整数"),
    ("fraction-state", lowered.Replace("\"jack_state\":3", "\"jack_state\":3.5"), false, "整数"),
    ("bad-ret-code", lowered.Replace("\"ret_code\":0", "\"ret_code\":7"), false, "错误"),
    ("malformed-ret-code", lowered.Replace("\"ret_code\":0", "\"ret_code\":\"0\""), false, "不可用"),
    ("duplicate-load", lowered.Replace("\"jack_isFull\":false", "\"jack_isFull\":true,\"jack_isFull\":false"), false, "重复字段"),
    ("duplicate-case", lowered.Replace("\"jack_isFull\":false", "\"JACK_ISFULL\":true,\"jack_isFull\":false"), false, "重复字段"),
    ("not-object", "[]", false, "不是"),
    ("bad-json", "{", false, "不可用"),
    ("wrong-frame", lowered, false, "响应编号"),
    ("connection-lost", null, false, "不可用")
};

foreach (var item in cases)
{
    await using var robot = new ProbeRobot(item.Response, item.Name == "wrong-frame");
    var options = new AgvOptions
    {
        Enabled = true,
        // The probe works with commands disabled. It must never enable them.
        AllowCommands = false,
        RequestTimeoutMs = 1000,
        ConnectTimeoutMs = 1000,
        Robots = [robot.Configuration]
    };
    await using var connections = new AgvConnectionManager(options);
    var started = DateTimeOffset.UtcNow;
    var result = await new TaskSwitchVehicleProbe(options, connections).ReadAsync("PROBE-VEHICLE", default);
    Check(result.VehicleId == "PROBE-VEHICLE" && result.ObservedAt >= started, item.Name + " vehicle/time identity");
    Check(result.EmptyAndLowered == item.Ready && result.Blockers.Count == (item.Ready ? 0 : 1), item.Name + " readiness");
    if (item.Blocker is not null) Check(result.Blockers.Any(b => b.Contains(item.Blocker)), item.Name + " diagnostic");
    Check(robot.Requests.FirstOrDefault() == 1027 && robot.PayloadLengths.FirstOrDefault() == 0 &&
          robot.Requests.All(type => type is 1027 or 1110) && robot.Requests.Count(type => type == 1110) <= 1,
        item.Name + " read-only status/task queries, no motion command");
    if (item.Ready) Check(result.TasksKnown && result.NoActiveTasks, item.Name + " complete task-list closure");
    Check(!options.AllowCommands, item.Name + " commands remain disabled");
}

await using (var robot = new ProbeRobot(lowered))
{
    var options = new AgvOptions { Enabled = true, Robots = [robot.Configuration] };
    await using var connections = new AgvConnectionManager(options);
    var probe = new TaskSwitchVehicleProbe(options, connections);
    Check(!(await probe.ReadAsync("UNKNOWN", default)).EmptyAndLowered && robot.Requests.Length == 0, "unknown vehicle does not fall back to another robot");
    Check(!(await probe.ReadAsync("", default)).EmptyAndLowered && robot.Requests.Length == 0, "empty vehicle is rejected without a request");
    options.Enabled = false;
    Check(!(await probe.ReadAsync("PROBE-VEHICLE", default)).EmptyAndLowered && robot.Requests.Length == 0, "disabled AGV connection sends nothing");
    options.Enabled = true;
    await connections.AllClients.Single().SetCommunicationEnabledAsync(false);
    Check(!(await probe.ReadAsync("PROBE-VEHICLE", default)).EmptyAndLowered && robot.Requests.Length == 0, "operator disconnected vehicle sends nothing");
    await connections.AllClients.Single().SetCommunicationEnabledAsync(true);
    Check((await probe.ReadAsync("probe-vehicle", default)).EmptyAndLowered && robot.Requests.FirstOrDefault() == 1027,
        "case-insensitive lookup preserves configured vehicle identity");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    var propagated = false;
    try { await probe.ReadAsync("PROBE-VEHICLE", cancelled.Token); }
    catch (OperationCanceledException) { propagated = true; }
    Check(propagated && robot.Requests.Length == 2, "caller cancellation propagates without another request");
}

// Bind a second loopback vehicle to prove the requested identity chooses its own connection.
await using (var other = new ProbeRobot(lowered.Replace("\"jack_isFull\":false", "\"jack_isFull\":true"), vehicleId: "OTHER-VEHICLE"))
await using (var requested = new ProbeRobot(lowered))
{
    var options = new AgvOptions { Enabled = true, Robots = [other.Configuration, requested.Configuration] };
    await using var connections = new AgvConnectionManager(options);
    var result = await new TaskSwitchVehicleProbe(options, connections).ReadAsync("PROBE-VEHICLE", default);
    Check(result.EmptyAndLowered && result.VehicleId == "PROBE-VEHICLE" && other.Requests.Length == 0 &&
          requested.Requests.FirstOrDefault() == 1027 && requested.Requests.All(type => type is 1027 or 1110),
        "multi-vehicle probe reads only the explicitly selected vehicle");
}

Console.WriteLine($"PASS: {checks} task switch vehicle probe checks; loopback TCP only, no real vehicles or motion commands.");

sealed class ProbeRobot : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task server;
    private readonly List<Task> clients = [];
    private readonly List<ushort> requests = [];
    private readonly List<int> payloadLengths = [];
    private readonly object sync = new();
    private readonly string? response;
    private readonly bool wrongFrame;
    public AgvRobotOptions Configuration { get; }
    public ushort[] Requests { get { lock (sync) return requests.ToArray(); } }
    public int[] PayloadLengths { get { lock (sync) return payloadLengths.ToArray(); } }

    public ProbeRobot(string? response, bool wrongFrame = false, string vehicleId = "PROBE-VEHICLE")
    {
        this.response = response;
        this.wrongFrame = wrongFrame;
        listener.Start();
        Configuration = new() { VehicleId = vehicleId, IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        Configuration.NavigationPort = Configuration.Port;
        server = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                lock (sync) clients.Add(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var header = new byte[16];
                await stream.ReadExactlyAsync(header, stop.Token);
                var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4));
                if (length is < 0 or > 1024) throw new InvalidDataException("Unexpected fixture request length.");
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, stop.Token);
                var type = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8, 2));
                lock (sync) { requests.Add(type); payloadLengths.Add(length); }
                string? bodyText;
                ushort responseType;
                if (type == 1027)
                {
                    if (response is null) return; // A peer-close is an unknown observation, never an empty vehicle.
                    bodyText = response;
                    responseType = (ushort)(wrongFrame ? 11026 : 11027);
                }
                else if (type == 1110)
                {
                    bodyText = "{\"ret_code\":0,\"task_status_list\":[]}";
                    responseType = 11110;
                }
                else
                {
                    throw new InvalidOperationException("Probe sent an unexpected non-read-only request: " + type);
                }
                var bytes = Encoding.UTF8.GetBytes(bodyText);
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), bytes.Length);
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), responseType);
                await stream.WriteAsync(header, stop.Token);
                await stream.WriteAsync(bytes, stop.Token);
                await stream.FlushAsync(stop.Token);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or EndOfStreamException or IOException or SocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        await server;
        Task[] pending;
        lock (sync) pending = clients.ToArray();
        try { await Task.WhenAll(pending); }
        finally { stop.Dispose(); }
    }
}
