using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;

internal static class GatewayChecks
{
    public static async Task Run(IRoadClosureService closures, Action<bool, string> check)
    {
        await using var robot = new Robot();
        var options = new AgvOptions { Enabled = true, AllowCommands = true, NavigationCommandMode = "SpecifiedPath",
            RequestTimeoutMs = 1000, ConnectTimeoutMs = 1000, RequestIntervalMs = 100, Robots = [robot.Configuration] };
        var cache = new AgvStateCache(options);
        cache.ApplyTelemetry("SIM", new AgvRobotTelemetry(0, 0, 0, 1, "LM1", null, 90, false, false, "Idle", null, null, null, [], [], DateTimeOffset.Now), TimeSpan.FromMilliseconds(1));
        await using var connections = new AgvConnectionManager(options);
        var gateway = new SeerAgvGateway(cache, connections, options, roadClosures: closures);
        robot.Accepted["previously-accepted"] = 2;
        var known = await gateway.CreateTransportTaskAsync("previously-accepted", "SIM", ["LM1", "LM3"], null, default);
        check(known == "previously-accepted" && robot.Queries == 1 && robot.Sends == 0,
            "gateway reconciles original accepted task on newly sealed route without new navigation");
        var rejected = false;
        try { await gateway.CreateTransportTaskAsync("queued-before-seal", "SIM", ["LM1", "LM3"], null, default); }
        catch (InvalidOperationException) { rejected = true; }
        check(rejected && robot.Queries == 2 && robot.Sends == 0,
            "gateway queries original unknown identity then emits zero 3066 bytes for sealed queued route");
        var alternate = await gateway.CreateTransportTaskAsync("fresh-detour", "SIM", ["LM1", "LM2", "LM3"], null, default);
        check(alternate == "fresh-detour" && robot.Sends == 1 && robot.LastPath.SequenceEqual(["LM1", "LM2", "LM3"]),
            "gateway sends only explicit unsealed LM1 LM2 LM3 chain and reconciles every segment");
        var after = robot.Sends;
        await gateway.CreateTransportTaskAsync("fresh-detour", "SIM", ["LM1", "LM2", "LM3"], null, default);
        check(robot.Sends == after, "accepted alternate chain is reconciled without duplicate send");
        options.NavigationCommandMode = "GotargetCommissioning";
        await gateway.CreateTransportTaskAsync("previously-accepted", "SIM", ["LM1", "LM3"], null, default);
        check(robot.Sends == after, "3051 mode still reconciles an already accepted original identity on a sealed route");
        var singleTargetRejected = false;
        try { await gateway.CreateTransportTaskAsync("unsafe-single-target", "SIM", ["LM1", "LM2", "LM3"], null, default); }
        catch (InvalidOperationException) { singleTargetRejected = true; }
        check(singleTargetRejected && robot.Sends == after, "active closure rejects new 3051 even when supplied preview chain avoids the sealed edge");
    }

    private sealed class Robot : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _clients = [];
        private readonly Task _server;
        private int _queries, _sends;
        public int Queries => Volatile.Read(ref _queries);
        public int Sends => Volatile.Read(ref _sends);
        public ConcurrentDictionary<string, int> Accepted { get; } = new();
        public string[] LastPath { get; private set; } = [];
        public AgvRobotOptions Configuration { get; }
        public Robot()
        {
            _listener.Start(); var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Configuration = new() { VehicleId = "SIM", IpAddress = "127.0.0.1", Port = port, NavigationPort = port };
            _server = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try { while (!_stop.IsCancellationRequested) _clients.Add(HandleAsync(await _listener.AcceptTcpClientAsync(_stop.Token))); }
            catch (OperationCanceledException) { }
        }
        private async Task HandleAsync(TcpClient client)
        {
            using (client) using (var stream = client.GetStream())
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var header = new byte[16]; await stream.ReadExactlyAsync(header, _stop.Token);
                    var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4));
                    if (length is < 0 or > 1048576) throw new InvalidDataException("Invalid loopback frame");
                    var body = new byte[length]; await stream.ReadExactlyAsync(body, _stop.Token);
                    using var request = JsonDocument.Parse(body);
                    var type = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8, 2)); object response;
                    if (type == 1110)
                    {
                        Interlocked.Increment(ref _queries);
                        var ids = request.RootElement.GetProperty("task_ids").EnumerateArray().Select(x => x.GetString()!).ToArray();
                        response = new { task_status_list = ids.Select(id => new { task_id = id, status = Accepted.GetValueOrDefault(id, 404) }).ToArray() };
                    }
                    else if (type == 3066)
                    {
                        Interlocked.Increment(ref _sends);
                        var moves = request.RootElement.GetProperty("move_task_list").EnumerateArray().ToArray();
                        LastPath = new[] { moves[0].GetProperty("source_id").GetString()! }.Concat(moves.Select(m => m.GetProperty("id").GetString()!)).ToArray();
                        foreach (var move in moves) Accepted[move.GetProperty("task_id").GetString()!] = 2;
                        response = new { ret_code = 0 };
                    }
                    else throw new InvalidDataException("Unexpected loopback request " + type);
                    var output = JsonSerializer.SerializeToUtf8Bytes(response);
                    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), output.Length);
                    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), (ushort)(type + 10000));
                    await stream.WriteAsync(header, _stop.Token); await stream.WriteAsync(output, _stop.Token); await stream.FlushAsync(_stop.Token);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or EndOfStreamException or IOException or SocketException) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop(); await _server; await Task.WhenAll(_clients); _stop.Dispose();
        }
    }
}
