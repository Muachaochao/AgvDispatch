using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.Connectors.Plc;
using HslCommunication;
using HslCommunication.Profinet.Siemens;
using PlcHandshakeLab;

internal static class NetworkTests
{
    public static async Task RunAsync()
    {
        CallPlanTests.Run();
        FleetTests.Run();
        await ControlTests.RunAsync();
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        void Reject(Action action, string label) { try { action(); } catch (ArgumentException) { checks++; return; } catch (InvalidOperationException) { checks++; return; } throw new Exception(label); }
        var directory = Path.Combine(Path.GetTempPath(), "PlcNetworkLab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = NetworkLabConfig.CreateDefault(); config.Validate();
            var invalid = NetworkLabConfig.CreateDefault(); invalid.Points[0].Bit = 1;
            Reject(invalid.Validate, "重复地址必须拒绝");
            invalid = NetworkLabConfig.CreateDefault(); invalid.Points.First(p => p.Owner == "H").Initial = true;
            Reject(invalid.Validate, "不伪造初始H输出");
            invalid = NetworkLabConfig.CreateDefault(); invalid.Points.RemoveAt(0); Reject(invalid.Validate, "缺失呼叫定义必须拒绝");
            invalid = NetworkLabConfig.CreateDefault(); invalid.AutoDoneSeconds = double.NaN; Reject(invalid.Validate, "延时NaN必须拒绝");
            invalid = NetworkLabConfig.CreateDefault(); invalid.BindAddress = "localhost"; Reject(invalid.Validate, "必须明确IPv4地址");
            var csvPath = Path.Combine(directory, "signals.csv"); File.WriteAllText(csvPath, config.ExportSignalCsv());
            var catalog = PlcSignalCatalog.Load(csvPath);
            Check(catalog.Count == 21 && catalog.EnabledCount == 21 && catalog.TryGet("DUMPER-001", "DumperReady", out var ready) && ready.Address == "DB1.DBX610.2", "导出CSV兼容正式点表加载器");
            Check(catalog.Signals.Where(p => p.StationId is "CNC-001" or "CNC-002").Count() == 16 &&
                catalog.Signals.Where(p => p.StationId is "CNC-001" or "CNC-002").All(p => p.DeviceType == "ChipRemovalStation"),
                "导出CNC点使用正式业务工位类型，不能仅采集成功而缺失业务状态");
            var businessStations = catalog.Signals.Where(p => p.DeviceType == "ChipRemovalStation" && p.VariableName == "FullBoxRequested")
                .Select(p => p.StationId).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            Check(businessStations.SequenceEqual(new[] { "CNC-001", "CNC-002" }), "正式状态服务的分类与呼叫筛选能注册两个CNC工位");
            Check(catalog.Signals.Where(p => p.StationId == "DUMPER-001").Count() == 5 &&
                catalog.Signals.Where(p => p.StationId == "DUMPER-001").All(p => p.DeviceType == "Dumper"), "共享倒料5点不误注册为取箱工位");
            config.DumperStationId = "DUMP-X"; File.WriteAllText(csvPath, config.ExportSignalCsv());
            Check(PlcSignalCatalog.Load(csvPath).ContainsStation("DUMP-X"), "倒料设备编号可配置");
            config = JsonSerializer.Deserialize<NetworkLabConfig>(JsonSerializer.Serialize(config))!; config.Validate();
            config.Port = FreePort(); config.AutoDoneSeconds = .1;
            using var lab = new NetworkLab(config, Path.Combine(directory, "session.jsonl"));
            bool Value(string station, int signal) => lab.Snapshot(station).Single(s => s.Mapping.Signal == signal).Value;
            var shared = NetworkLabConfig.Shared;
            Check(Value("CNC-001", 1) && !Value(shared, 8), "初态有箱，Ready旧低基线");
            lab.SelectStage("CNC-001", 9);
            Check(!Value("CNC-001", 0) && lab.Stage("CNC-001") == 9, "标记阶段不套用电平");
            Reject(() => lab.ManualSet("CNC-001", 2, true), "人工不得写H");
            lab.ManualSet("CNC-001", 0, true);
            Check(lab.Read(1, 0, 1)![0] == 11, "P输入与DB相同内存");
            Check(lab.WriteHost(1, 0, 4, [1], true, "test") && Value("CNC-001", 2), "H位写入");
            for (var i = 0; i < 5; i++) lab.TickAutomatic();
            Check(Value("CNC-001", 0), "人工模式不自动撤呼叫");
            Check(!lab.WriteHost(1, 0, 0, [0], true, "test") && Value("CNC-001", 0), "S7不得写P");
            var before = lab.Read(1, 0, 3)!;
            Check(!lab.WriteHost(1, 0, 0, [0, 0, 0], false, "test") && lab.Read(1, 0, 3)!.SequenceEqual(before), "整字节越权写入完整拒绝");
            Check(lab.WriteHost(1, 0, 0, [(byte)(before[0] | 64)], false, "test") && Value("CNC-001", 4), "BYTE写H保留P");
            Check(!lab.WriteHost(1, 0, 0, [(byte)(before[0] | 64), 1], false, "test") && lab.Read(1, 1, 1)![0] == 0, "禁止未映射位");
            Check(!lab.WriteHost(1, 0, 4, [2], true, "test"), "BOOL非0/1拒绝");
            lab.SetAutomatic(true); lab.TickAutomatic(); Check(!Value("CNC-001", 0), "自动应答后撤呼叫");
            lab.ManualSet("CNC-001", 0, true); Check(Value("CNC-001", 0), "自动模式仍允许手动发起新呼叫");
            lab.TickAutomatic();
            lab.WriteHost(1, 610, 0, [1], true, "test"); lab.TickAutomatic(); Check(Value(shared, 8), "等待申请后自动授予Ready");
            lab.WriteHost(1, 610, 0, [0], true, "test"); lab.TickAutomatic(); Check(!Value(shared, 8), "消费后Ready撤销");
            lab.WriteHost(1, 610, 1, [1], true, "test"); lab.TickAutomatic(); await Task.Delay(150); lab.TickAutomatic(); Check(Value(shared, 9), "定时模拟Done");
            lab.SetAutomatic(false); lab.WriteHost(1, 610, 1, [0], true, "test"); lab.TickAutomatic(); Check(Value(shared, 9), "切人工保留Done，不自动覆盖");
            lab.ManualSet(shared, 9, false);
            lab.WriteHost(1, 610, 1, [1], true, "test");
            Reject(() => lab.SetAutomatic(true), "活动周期不能切入自动模式");
            lab.WriteHost(1, 610, 1, [0], true, "test");
            lab.ConfirmCycleClosed("已人工核对共享低基线");
            lab.SetAutomatic(true);
            lab.WriteHost(1, 610, 1, [1], true, "test"); lab.TickAutomatic(); await Task.Delay(120); lab.TickAutomatic();
            Check(!Value(shared, 9), "未消费本次许可不生成Done");
            lab.WriteHost(1, 610, 1, [0], true, "test");
            lab.SetAutomatic(false); lab.ConfirmCycleClosed("无许可周期已撤销，核对闭合"); lab.SetAutomatic(true);
            lab.WriteHost(1, 610, 0, [1], true, "test"); lab.TickAutomatic();
            lab.WriteHost(1, 610, 0, [0], true, "test"); lab.TickAutomatic();
            lab.WriteHost(1, 610, 1, [1], true, "test"); lab.TickAutomatic();
            lab.ManualSet(shared, 12, true); lab.TickAutomatic(); await Task.Delay(130); lab.TickAutomatic();
            Check(!Value(shared, 9), "故障期间不生成Done");
            lab.ManualSet(shared, 12, false); lab.TickAutomatic();
            Check(!Value(shared, 9), "故障耗时不能计为倒料成功");
            await Task.Delay(130); lab.TickAutomatic(); Check(Value(shared, 9), "恢复后重新等待完整模拟周期");
            lab.WriteHost(1, 610, 1, [0], true, "test"); lab.TickAutomatic();
            Check(!Value(shared, 9), "自动退出闭合撤Done");
            lab.SetAutomatic(false);
            lab.Observe("CNC-001", "AGV-01", "TASK-01", "实际已退出,测试\"引号\"");
            for (var i = 0; i < 520; i++) lab.Note("容量", i.ToString());
            Check(lab.Events().Length == 500 && lab.ExportCsv().Contains("实际已退出,测试\"\"引号\"\"") && lab.ExportCsv().Split('\n').Length > 520, "完整日志导出不受UI500条限制且CSV转义");

            await using var server = new S7LabServer(lab); server.Start();
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, server.Port);
            var stream = client.GetStream();
            var cotp = Convert.FromHexString("0300001611E00000000100C1020100C2020102C0010A");
            var reply = await Exchange(stream, cotp, fragment: true);
            Check(reply[5] == 0xd0, "COTP拆包连接协商");
            reply = await Exchange(stream, Frame([0xf0, 0, 0, 1, 0, 1, 3, 0xc0]));
            Check(reply[19] == 0xf0, "S7协商");
            reply = await Exchange(stream, Variable(4, 1, 0, 1, false));
            Check(reply[21] == 0xff && reply[25] == lab.Read(1, 0, 1)![0], "网络BYTE读取");
            reply = await Exchange(stream, Variable(5, 1, 0, 5, true, [1]));
            Check(reply[21] == 0xff && Value("CNC-001", 3), "网络H写入反映到面板内存");
            reply = await Exchange(stream, Variable(5, 1, 0, 1, true, [0]));
            Check(reply[21] != 0xff && Value("CNC-001", 1), "网络P写入拒绝");
            reply = await Exchange(stream, Variable(4, 99, 0, 1, false)); Check(reply[21] != 0xff, "未知DB返回项目错误");
            reply = await Exchange(stream, Variable(4, 1, 8192, 1, false)); Check(reply[21] != 0xff, "读取越界返回错误");
            // Malformed write tail must be rejected before any write is applied.
            using (var malformed = new TcpClient())
            {
                await malformed.ConnectAsync(IPAddress.Loopback, server.Port);
                var packet = Variable(5, 1, 0, 5, true, [0]);
                Array.Resize(ref packet, packet.Length + 1); Put16(packet, 2, packet.Length); Put16(packet, 15, 6);
                await malformed.GetStream().WriteAsync(packet); await Task.Delay(50);
                Check(Value("CNC-001", 3), "畸形额外数据不能产生部分写入");
            }
            var code = Environment.GetEnvironmentVariable("HSL_AUTHORIZATION_CODE") ?? Environment.GetEnvironmentVariable("HSL_AUTHORIZATION_CODE", EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(code) || !HslCommunication.Authorization.SetAuthorizationCode(code)) throw new InvalidOperationException("缺少有效本地HSL授权，正式驱动兼容验证未完成。");
            using (var hsl = new SiemensS7Net(SiemensPLCS.S1200, "127.0.0.1") { Port = server.Port, ConnectTimeOut = 3000, ReceiveTimeOut = 3000 })
            {
                var connect = await hsl.ConnectServerAsync(); Check(connect.IsSuccess, "正式HSL连接失败：" + connect.Message);
                var read = await hsl.ReadAsync("DB1.0", 200); Check(read.IsSuccess && read.Content.Length == 200, "正式HSL批量读取：" + read.Message);
                var set = await hsl.WriteAsync("DB1.DBX0.4", true); Check(set.IsSuccess && Value("CNC-001", 2), "正式HSL BOOL写入：" + set.Message);
                var readBack = await hsl.ReadBoolAsync("DB1.DBX0.4"); Check(readBack.IsSuccess && readBack.Content, "正式HSL写入回读");
                var denied = await hsl.WriteAsync("DB1.DBX0.1", false); Check(!denied.IsSuccess && Value("CNC-001", 1), "正式HSL越权P写入必须失败");
                hsl.ConnectClose();
            }
            Check(server.LastRequest is not null, "最后请求时间已记录");
            Console.WriteLine($"PASS: {checks} network checks. S7 TCP拆包/协商/读写、正式HSL驱动、P/H隔离、自动模式、配置与全量日志。");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    private static void Put16(byte[] b, int at, int n) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at, 2), (ushort)n);
    private static byte[] Frame(byte[] param, byte[]? data = null)
    {
        data ??= []; var packet = new byte[17 + param.Length + data.Length]; packet[0] = 3; Put16(packet, 2, packet.Length);
        packet[4] = 2; packet[5] = 0xf0; packet[6] = 0x80; packet[7] = 0x32; packet[8] = 1; packet[12] = 1;
        Put16(packet, 13, param.Length); Put16(packet, 15, data.Length); param.CopyTo(packet, 17); data.CopyTo(packet, 17 + param.Length); return packet;
    }
    internal static byte[] Variable(int func, int db, int offset, int bitOrCount, bool bit, byte[]? raw = null)
    {
        byte[] param = [(byte)func, 1, 0x12, 10, 0x10, (byte)(bit ? 1 : 2), 0, 0, 0, 0, 0x84, 0, 0, 0];
        Put16(param, 6, bit ? 1 : bitOrCount); Put16(param, 8, db);
        var address = offset * 8 + (bit ? bitOrCount : 0); param[11] = (byte)(address >> 16); param[12] = (byte)(address >> 8); param[13] = (byte)address;
        byte[] data = [];
        if (func == 5) { raw ??= [0]; data = new byte[4 + raw.Length]; data[1] = bit ? (byte)3 : (byte)4; Put16(data, 2, bit ? 1 : raw.Length * 8); raw.CopyTo(data, 4); }
        return Frame(param, data);
    }
    internal static async Task<byte[]> Exchange(NetworkStream stream, byte[] packet, bool fragment = false)
    {
        if (fragment) { await stream.WriteAsync(packet.AsMemory(0, 2)); await Task.Delay(10); await stream.WriteAsync(packet.AsMemory(2)); }
        else await stream.WriteAsync(packet);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var header = new byte[4]; await stream.ReadExactlyAsync(header, timeout.Token);
        var response = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2))]; header.CopyTo(response, 0);
        await stream.ReadExactlyAsync(response.AsMemory(4), timeout.Token); return response;
    }
}
