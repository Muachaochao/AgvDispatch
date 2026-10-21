using System.IO;
using System.Text;
using System.Text.Json;

namespace PlcHandshakeLab;

public sealed record LivePlcSnapshot(string Station, bool[] Values, DateTimeOffset ObservedAt, bool Faulted, bool DownstreamOnline);

// Local device control channel only. The Service continues to read/write the twin through S7.
public sealed class LivePlcConnection
{
    public string Root { get; }
    public LivePlcConnection(string root)
    {
        Root = Path.GetFullPath(root);
        Validate();
    }

    private void Validate()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "service", "appsettings.json")));
        var settings = config.RootElement;
        if (settings.GetProperty("Plc").GetProperty("IpAddress").GetString() != "127.0.0.1" ||
            settings.GetProperty("TaskCreation").GetProperty("AutomaticCreationEnabled").GetBoolean() ||
            settings.GetProperty("Agv").GetProperty("Robots").EnumerateArray().Any(r => r.GetProperty("IpAddress").GetString() != "127.0.0.1"))
            throw new InvalidOperationException("仅支持127.0.0.1独立虚拟设备、关闭自动建单的联调实例。");
        if (!File.Exists(Path.Combine(Root, "lab.json")) || !File.Exists(Path.Combine(Root, "navigation.agvmap.json")))
            throw new InvalidOperationException("请选择虚拟设备实例目录（包含lab.json和导航文件）。");
        if (File.Exists(Path.Combine(Root, "STOP"))) throw new InvalidOperationException("虚拟设备已有停止请求。");
    }

    public LivePlcSnapshot Read(string station)
    {
        var offset = Offset(station);
        Validate();
        var path = Path.Combine(Root, "device-state.json");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var observed = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        if (DateTimeOffset.UtcNow - observed > TimeSpan.FromSeconds(3) || observed - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(1))
            throw new InvalidOperationException("虚拟PLC快照超过3秒或时间异常，当前值未知，禁止输入。");
        using var state = JsonDocument.Parse(stream);
        var memory = Convert.FromHexString(state.RootElement.GetProperty("memory_hex").GetString()!);
        if (memory.Length != 1024) throw new InvalidOperationException("虚拟PLC数据区长度不匹配。");
        bool Bit(int b, int bit) => (memory[b] & (1 << bit)) != 0;
        bool[] values = [Bit(offset,0), Bit(offset,1), Bit(offset,4), Bit(offset,5), Bit(offset,6), Bit(offset,7),
            Bit(610,0), Bit(610,1), Bit(610,2), Bit(610,3)];
        return new(station, values, observed, Bit(offset,2) || Bit(610,4), Bit(offset,3));
    }

    public string Call(string station)
    {
        var snapshot = Read(station);
        if (snapshot.Faulted || !snapshot.DownstreamOnline || !snapshot.Values[1] || snapshot.Values[2] || snapshot.Values[4])
            throw new InvalidOperationException("工位故障、下游离线、无箱或本轮责任未结束，未触发新呼叫。");
        if (snapshot.Values[0]) return "满箱呼叫已为1，无需重复触发；请在Web刷新条件并提交任务。";
        var input = Path.Combine(Root, "device-input.json");
        var temp = Path.Combine(Root, "device-input-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new { sequence = Guid.NewGuid().ToString("N"), call = station }), new UTF8Encoding(false));
            File.Move(temp, input, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return "满箱呼叫输入已提交，等待虚拟PLC反馈；车辆任务仍由Web提交。";
    }

    private static int Offset(string station) => station switch
    { "CNC-001" => 0, "CNC-002" => 2, _ => throw new ArgumentException("仅支持CNC-001/CNC-002。") };
}
