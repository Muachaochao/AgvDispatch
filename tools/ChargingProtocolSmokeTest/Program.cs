using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgvDispatch.Connectors.Agv;

var count = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    count++;
    Console.WriteLine("PASS " + message);
}
async Task Fails<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, message); return; }
    throw new Exception(message);
}

await using var robot = new Robot();
var options = new AgvOptions
{
    Enabled = true, AllowCommands = true, ConnectTimeoutMs = 500, RequestTimeoutMs = 500,
    Robots = [new() { VehicleId = "SIM", IpAddress = "127.0.0.1", Port = robot.StatusPort, NavigationPort = robot.StatusPort }]
};
await using var connections = new AgvConnectionManager(options);
var settings = new RobokitChargingRobotControlOptions { OtherApiPort = robot.OtherPort };
var control = new RobokitChargingRobotControl(options, connections, settings);
await Fails<InvalidOperationException>(() => control.SetChargingRelayAsync("SIM", true, default), "缺少现场确认时禁止6005");
Check(robot.Requests.IsEmpty, "充电控制门禁拒绝时不连接设备");
Check(new RobokitChargingRobotControlOptions().OtherApiPort == 19210, "其他API默认19210");
var battery = await control.ReadBatteryAsync("SIM", default);
Check(battery.BatteryPercent == 24.5m && battery.Charging == true && battery.Current == 5.2 && battery.Temperature == 32,
    "1007完整电池遥测及电量比例转换");
Check(battery.ManualCharge == false && battery.AutoCharge == true && battery.VehicleId == "SIM", "分别保留连接指示和指定车辆身份");
Check(robot.Requests.Single().Type == 1007 && robot.Requests.Single().Port == robot.StatusPort && robot.Requests.Single().Payload == "{\"simple\":false}",
    "完整1007走状态端口，显式simple=false");

robot.BatteryResponse = "{\"battery_level\":0.5,\"auto_charge\":true}";
battery = await control.ReadBatteryAsync("SIM", default);
Check(battery.BatteryPercent == 50 && battery.AutoCharge == true && battery.Charging is null && battery.Current is null,
    "缺失充电状态和电流保持未知，桩连接不当成充电");
robot.BatteryResponse = "{\"battery_level\":1.01}";
await Fails<InvalidDataException>(async () => { await control.ReadBatteryAsync("SIM", default); }, "拒绝越界电量");
robot.BatteryResponse = "{\"current\":\"5\"}";
await Fails<InvalidDataException>(async () => { await control.ReadBatteryAsync("SIM", default); }, "拒绝字符串电流");
robot.BatteryResponse = "{\"charging\":1}";
await Fails<InvalidDataException>(async () => { await control.ReadBatteryAsync("SIM", default); }, "拒绝不符合本地协议boolean定义的充电状态");
robot.BatteryResponse = "{\"battery_temp\":1e999}";
await Fails<InvalidDataException>(async () => { await control.ReadBatteryAsync("SIM", default); }, "拒绝非有限温度");
robot.BatteryResponse = "{\"ret_code\":1}";
await Fails<InvalidOperationException>(async () => { await control.ReadBatteryAsync("SIM", default); }, "1007拒绝错误应答");

settings.RelayControlConfirmed = true;
await control.SetChargingRelayAsync("SIM", true, default);
await control.SetChargingRelayAsync("SIM", false, default);
var writes = robot.Requests.Where(r => r.Type == 6005).ToArray();
Check(writes.Length == 2 && writes.All(r => r.Port == robot.OtherPort), "6005只走其他API端口");
Check(writes[0].Payload == "{\"status\":true}" && writes[1].Payload == "{\"status\":false}", "继电器开关使用明确boolean字段");
robot.RelayResponse = "{}";
await Fails<InvalidDataException>(() => control.SetChargingRelayAsync("SIM", true, default), "6005缺少返回码保持不确定");
robot.RelayResponse = "{\"ret_code\":\"0\"}";
await Fails<InvalidDataException>(() => control.SetChargingRelayAsync("SIM", true, default), "6005拒绝字符串返回码");
robot.RelayResponse = "{\"ret_code\":42}";
await Fails<InvalidOperationException>(() => control.SetChargingRelayAsync("SIM", true, default), "6005厂家拒绝不当成成功");
robot.DropRelayResponse = true;
var beforeDrop = robot.Requests.Count(r => r.Type == 6005);
await Fails<EndOfStreamException>(() => control.SetChargingRelayAsync("SIM", false, default), "6005已收但应答丢失报告异常");
Check(robot.Requests.Count(r => r.Type == 6005) == beforeDrop + 1, "6005发送不确定不自动重发");
robot.DropRelayResponse = false;
robot.RelayResponse = "{\"ret_code\":0}";
robot.WrongSequence = true;
await Fails<InvalidDataException>(() => control.SetChargingRelayAsync("SIM", false, default), "拒绝响应序号不匹配");
robot.WrongSequence = false;

var beforeDisabled = robot.Requests.Count;
options.AllowCommands = false;
await Fails<InvalidOperationException>(() => control.SetChargingRelayAsync("SIM", true, default), "全局动作关闭禁止充电控制");
options.AllowCommands = true;
await connections.Clients.Single().SetCommunicationEnabledAsync(false);
await Fails<InvalidOperationException>(() => control.SetChargingRelayAsync("SIM", true, default), "车辆通信关闭禁止充电控制");
await Fails<InvalidOperationException>(async () => { await control.ReadBatteryAsync("SIM", default); }, "车辆通信关闭不读取旧电池状态");
Check(robot.Requests.Count == beforeDisabled, "控制及通信关闭不发送额外请求");
Console.WriteLine($"Charging protocol smoke passed: {count} checks. Synthetic TCP fixture only; no real robot or charger acceptance.");

sealed class Robot : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TcpListener _status = new(IPAddress.Loopback, 0);
    private readonly TcpListener _other = new(IPAddress.Loopback, 0);
    private readonly List<Task> _loops = [];
    public ConcurrentQueue<(int Port, ushort Type, string Payload)> Requests { get; } = new();
    public string BatteryResponse { get; set; } = "{\"ret_code\":0,\"battery_level\":0.245,\"charging\":true,\"current\":5.2,\"battery_temp\":32,\"manual_charge\":false,\"auto_charge\":true}";
    public string RelayResponse { get; set; } = "{\"ret_code\":0}";
    public bool DropRelayResponse { get; set; }
    public bool WrongSequence { get; set; }
    public int StatusPort => ((IPEndPoint)_status.LocalEndpoint).Port;
    public int OtherPort => ((IPEndPoint)_other.LocalEndpoint).Port;
    public Robot()
    {
        _status.Start(); _other.Start();
        _loops.Add(Loop(_status)); _loops.Add(Loop(_other));
    }
    private async Task Loop(TcpListener listener)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(_stop.Token);
                _ = Serve(client, ((IPEndPoint)listener.LocalEndpoint).Port);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task Serve(TcpClient client, int port)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                while (!_stop.IsCancellationRequested)
                {
                    var header = new byte[16];
                    await stream.ReadExactlyAsync(header, _stop.Token);
                    var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4));
                    var type = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8, 2));
                    var payload = new byte[length];
                    await stream.ReadExactlyAsync(payload, _stop.Token);
                    Requests.Enqueue((port, type, Encoding.UTF8.GetString(payload)));
                    if (type == 6005 && DropRelayResponse) return;
                    var reply = Encoding.UTF8.GetBytes(type == 6005 ? RelayResponse : BatteryResponse);
                    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), reply.Length);
                    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), checked((ushort)(type + 10000)));
                    if (WrongSequence) header[3]++;
                    await stream.WriteAsync(header, _stop.Token);
                    await stream.WriteAsync(reply, _stop.Token);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _status.Stop(); _other.Stop();
        await Task.WhenAll(_loops);
        _stop.Dispose();
    }
}
