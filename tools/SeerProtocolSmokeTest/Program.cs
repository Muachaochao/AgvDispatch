using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
var server = RunMockServerAsync(listener);

var options = new AgvOptions
{
    Enabled = true,
    ProtocolVersion = 1,
    ConnectTimeoutMs = 2000,
    RequestTimeoutMs = 2000
};
var robot = new AgvRobotOptions
{
    VehicleId = "AGV-TEST",
    DisplayName = "协议测试车",
    IpAddress = IPAddress.Loopback.ToString(),
    Port = port, NavigationPort = port
};

await using var client = new SeerRobotClient(robot, options);
var (telemetry, _) = await client.PollAsync(CancellationToken.None);
var missing = await client.QueryTaskAsync("STEP-001", CancellationToken.None);
Require(missing?.Status == 404, "创建前任务不存在状态解析失败");
await client.GoToTargetAsync(
    "STEP-001",
    new[] { "PARK-01", "JUNCTION-01", "CNC-01" },
    new AgvTransportAction("JackLoad", Recognize: true, UsePgv: true),
    useSpecifiedPath: true,
    CancellationToken.None);
var completed = await client.QueryTaskAsync("STEP-001", CancellationToken.None);
Require(completed?.Status == 4, "指定路径任务完成状态解析失败");
var running = await client.QueryTaskAsync("STEP-CANCEL", CancellationToken.None);
Require(running?.Status == 2, "待取消任务状态解析失败");
await client.PauseNavigationAsync(CancellationToken.None);
await client.ResumeNavigationAsync(CancellationToken.None);
await client.CancelNavigationAsync("STEP-CANCEL", smartCancel: true, CancellationToken.None);
await server;

Require(telemetry.X == 12.5, "位置X解析失败");
Require(telemetry.Y == 6.25, "位置Y解析失败");
Require(telemetry.CurrentStation == "LM-CNC-001", "当前站点解析失败");
Require(telemetry.BatteryPercent == 76m, "电量解析失败");
Require(telemetry.VelocityX == 0.3 && telemetry.VelocityY == 0.4 && telemetry.AngularVelocity == 0.2,
    "速度参数解析失败");
Require(telemetry.Charging == false && telemetry.BatteryVoltage == 51.2 && telemetry.BatteryCurrent == -2.1 &&
    telemetry.BatteryTemperature == 32.5, "电池参数解析失败");
Require(telemetry.NavigationState == "2", "task_status导航状态兼容解析失败");
Require(telemetry.Tasks.Count == 1 && telemetry.Tasks[0].Status == 2, "任务状态解析失败");
Require(telemetry.Alarms.Count == 1 && telemetry.Alarms[0].Code == "55001", "报警解析失败");
await VerifyGatewayIdempotencyAsync();
Console.WriteLine("SEER协议冒烟测试通过：状态采集、1110幂等查询、3066指定路径动作、3001暂停、3002继续和3003精准取消正常。");

static async Task VerifyGatewayIdempotencyAsync()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var server = RunGatewayMockServerAsync(listener);
    var robot = new AgvRobotOptions
    {
        VehicleId = "AGV-GATEWAY",
        DisplayName = "网关测试车",
        IpAddress = IPAddress.Loopback.ToString(),
        Port = port, NavigationPort = port
    };
    var options = new AgvOptions
    {
        Enabled = true,
        AllowCommands = true,
        NavigationCommandMode = "SpecifiedPath",
        ConnectTimeoutMs = 2000,
        RequestTimeoutMs = 2000,
        Robots = [robot]
    };
    var cache = new AgvStateCache(options);
    cache.ApplyTelemetry(
        robot.VehicleId,
        new AgvRobotTelemetry(
            0, 0, 0, 1, "PARK-01", null, 90, false, false, "Idle",
            null, null, null, [], [], DateTimeOffset.Now),
        TimeSpan.FromMilliseconds(5));
    await using var manager = new AgvConnectionManager(options);
    var gateway = new SeerAgvGateway(cache, manager, options);

    var first = await gateway.CreateTransportTaskAsync(
        "STEP-GATEWAY",
        robot.VehicleId,
        new[] { "PARK-01", "CNC-01" },
        new AgvTransportAction("JackLoad", Recognize: true),
        CancellationToken.None);
    var retry = await gateway.CreateTransportTaskAsync(
        "STEP-GATEWAY",
        robot.VehicleId,
        new[] { "PARK-01", "CNC-01" },
        new AgvTransportAction("JackLoad", Recognize: true),
        CancellationToken.None);
    Require(first == "STEP-GATEWAY" && retry == first, "Gateway幂等任务号错误");
    await server;
}

static async Task RunGatewayMockServerAsync(TcpListener listener)
{
    using var client = await listener.AcceptTcpClientAsync();
    await using var stream = client.GetStream();
    var createCount = 0;
    for (var index = 0; index < 3; index++)
    {
        var header = new byte[16];
        await stream.ReadExactlyAsync(header);
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)));
        var requestType = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8, 2));
        var requestBody = new byte[length];
        await stream.ReadExactlyAsync(requestBody);
        using var request = JsonDocument.Parse(requestBody);

        object response;
        if (requestType == 3066)
        {
            createCount++;
            response = new { ret_code = 0 };
        }
        else if (requestType == 1110)
        {
            response = new
            {
                ret_code = 0,
                task_status_list = new object[]
                {
                    new { task_id = "STEP-GATEWAY", status = createCount == 0 ? 404 : 1, type = 3 }
                }
            };
        }
        else
        {
            throw new InvalidOperationException($"Gateway测试收到未知请求编号{requestType}");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(response);
        var responseHeader = new byte[16];
        responseHeader[0] = 0x5A;
        responseHeader[1] = header[1];
        BinaryPrimitives.WriteUInt16BigEndian(responseHeader.AsSpan(2, 2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(responseHeader.AsSpan(4, 4), checked((uint)body.Length));
        BinaryPrimitives.WriteUInt16BigEndian(
            responseHeader.AsSpan(8, 2), checked((ushort)(requestType + 10000)));
        await stream.WriteAsync(responseHeader);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    Require(createCount == 1, "Gateway重试不应重复发送3066创建命令");
    listener.Stop();
}

static async Task RunMockServerAsync(TcpListener listener)
{
    using var client = await listener.AcceptTcpClientAsync();
    await using var stream = client.GetStream();
    for (var index = 0; index < 9; index++)
    {
        var header = new byte[16];
        await stream.ReadExactlyAsync(header);
        Require(header[0] == 0x5A, "请求同步字节错误");
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)));
        var requestType = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8, 2));
        var requestBody = new byte[length];
        await stream.ReadExactlyAsync(requestBody);
        using var requestJson = requestBody.Length == 0
            ? JsonDocument.Parse("{}")
            : JsonDocument.Parse(requestBody);

        if (requestType == 3066)
        {
            var moves = requestJson.RootElement.GetProperty("move_task_list");
            Require(moves.GetArrayLength() == 2, "3066路径段数量错误");
            Require(moves[0].GetProperty("source_id").GetString() == "PARK-01", "3066首段起点错误");
            Require(moves[0].GetProperty("id").GetString() == "JUNCTION-01", "3066首段终点错误");
            Require(moves[0].GetProperty("task_id").GetString() == "STEP-001-01", "3066中间任务ID不稳定");
            Require(moves[1].GetProperty("task_id").GetString() == "STEP-001", "3066最终任务ID必须等于StepId");
            Require(moves[1].GetProperty("operation").GetString() == "JackLoad", "3066最终动作错误");
            Require(moves[1].GetProperty("recognize").GetBoolean(), "JackLoad识别参数错误");
            Require(moves[1].GetProperty("use_pgv").GetBoolean(), "JackLoad PGV参数错误");
        }
        else if (requestType == 3003)
        {
            Require(requestJson.RootElement.GetProperty("taskId").GetString() == "STEP-CANCEL", "取消任务ID错误");
            Require(requestJson.RootElement.GetProperty("smartCancel").GetBoolean(), "智能取消参数错误");
        }
        else if (requestType is 3001 or 3002)
        {
            Require(requestBody.Length == 0, "暂停/继续导航请求必须没有JSON数据区");
        }

        object response = requestType switch
        {
            1100 => new
            {
                ret_code = 0,
                x = 12.5,
                y = 6.25,
                angle = 1.57,
                confidence = 0.98,
                current_station = "LM-CNC-001",
                last_station = "LM-JUNCTION-01",
                battery_level = 0.76,
                vx = 0.3, vy = 0.4, w = 0.2, charging = false,
                voltage = 51.2, current = -2.1, battery_temp = 32.5, task_status = 2,
                blocked = false,
                emergency = false,
                warnings = new[] { new { code = 55001, description = "模拟告警" } },
                errors = Array.Empty<object>(),
                fatals = Array.Empty<object>()
            },
            1110 when requestJson.RootElement.TryGetProperty("task_ids", out var ids) => new
            {
                ret_code = 0,
                task_status_list = ids[0].GetString() switch
                {
                    "STEP-001" when index == 2 => new object[] { new { task_id = "STEP-001", status = 404, type = 3 } },
                    "STEP-001" => new object[] { new { task_id = "STEP-001", status = 4, type = 3 } },
                    "STEP-CANCEL" => new object[] { new { task_id = "STEP-CANCEL", status = 2, type = 3 } },
                    _ => Array.Empty<object>()
                }
            },
            1110 => new
            {
                ret_code = 0,
                task_status_package = new
                {
                    source_name = "LM-CNC-001",
                    target_name = "LM-DUMPER-WAIT",
                    percentage = 0.35,
                    task_status_list = new[] { new { task_id = "TASK-TEST", status = 2, type = 3 } }
                }
            },
            3066 => new { ret_code = 0 },
            3001 => new { ret_code = 0 },
            3002 => new { ret_code = 0 },
            3003 => new { ret_code = 0 },
            _ => throw new InvalidOperationException($"收到未知请求编号{requestType}")
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(response);
        var responseHeader = new byte[16];
        responseHeader[0] = 0x5A;
        responseHeader[1] = header[1];
        BinaryPrimitives.WriteUInt16BigEndian(responseHeader.AsSpan(2, 2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(responseHeader.AsSpan(4, 4), checked((uint)body.Length));
        BinaryPrimitives.WriteUInt16BigEndian(
            responseHeader.AsSpan(8, 2),
            checked((ushort)(requestType + 10000)));
        await stream.WriteAsync(responseHeader);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    listener.Stop();
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
