using System.IO;
using System.Text;
using System.Text.Json;

namespace PlcHandshakeLab;

public sealed record LivePlcSnapshot(string Station, bool[] Values, DateTimeOffset ObservedAt, bool Faulted, bool DownstreamOnline, int Offset = 0);
public sealed record LiveBatchObservation(string Action, string Message);

// Local device control channel only. The Service continues to read/write the twin through S7.
public sealed class LivePlcConnection
{
    private readonly IReadOnlyDictionary<string, int> _offsets;
    public string Root { get; }
    public IReadOnlyList<string> Stations { get; }
    public LivePlcConnection(string root)
    {
        Root = Path.GetFullPath(root);
        Validate();
        var stations = LoadStations();
        _offsets = stations.ToDictionary(item => item.Station, item => item.Offset, StringComparer.OrdinalIgnoreCase);
        Stations = Array.AsReadOnly(stations.Select(item => item.Station).ToArray());
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
        if (!_offsets.TryGetValue(station.Trim(), out var offset))
            throw new ArgumentException($"工位{station}未包含在当前虚拟PLC点表中。");
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
        return new(station, values, observed, Bit(offset,2) || Bit(610,4), Bit(offset,3), offset);
    }

    public string Call(string station)
    {
        var snapshot = Read(station);
        if (snapshot.Values[0]) return "满箱呼叫已为1，无需重复触发；请在Web刷新条件并提交任务。";
        if (!CanRaiseNewCall(snapshot))
            throw new InvalidOperationException("工位故障、下游离线、无箱、上一轮应答/返箱责任未闭合，或倒料设备仍忙，未触发新呼叫。");
        var canonical = CanonicalStation(station);
        WriteInput(new { sequence = Guid.NewGuid().ToString("N"), call = canonical });
        return "满箱呼叫输入已提交，等待虚拟PLC反馈；车辆任务仍由Web提交。";
    }

    public IReadOnlyDictionary<string, LivePlcSnapshot> ReadMany(IEnumerable<string> stations)
    {
        var snapshots = new Dictionary<string, LivePlcSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var station in NormalizeStations(stations))
            snapshots[station] = Read(station);
        return snapshots;
    }

    public string CallMany(IReadOnlyCollection<string> stations)
    {
        if (stations is null) throw new ArgumentNullException(nameof(stations));
        var normalized = NormalizeStations(stations);
        if (normalized.Count < 2) throw new ArgumentException("同批呼叫至少选择两个工位。");
        var snapshots = ReadMany(normalized);
        var blocked = snapshots.Values.FirstOrDefault(snapshot => !CanRaiseNewCall(snapshot));
        if (blocked is not null)
            throw new InvalidOperationException($"{blocked.Station}当前不满足新呼叫条件：工位故障、下游离线、无箱、上一轮握手未闭合或倒料设备仍忙。");
        WriteInput(new { sequence = Guid.NewGuid().ToString("N"), calls = normalized });
        return $"已在同一次虚拟PLC输入中提交{normalized.Count}个工位呼叫：{string.Join('、', normalized)}；等待设备反馈，任务由正式后台创建和派车。";
    }

    public static bool CanRaiseNewCall(LivePlcSnapshot snapshot) =>
        snapshot.Values.Length == 10 &&
        !snapshot.Faulted && snapshot.DownstreamOnline &&
        snapshot.Values[1] && !snapshot.Values[0] &&
        !snapshot.Values[2] && !snapshot.Values[3] && !snapshot.Values[4] && !snapshot.Values[5] &&
        !snapshot.Values[6] && !snapshot.Values[7] && !snapshot.Values[8] && !snapshot.Values[9];

    private string CanonicalStation(string station) =>
        Stations.FirstOrDefault(item => string.Equals(item, station.Trim(), StringComparison.OrdinalIgnoreCase)) ??
        throw new ArgumentException($"工位{station}未包含在当前虚拟PLC点表中。");

    private IReadOnlyList<string> NormalizeStations(IEnumerable<string> stations)
    {
        var raw = stations
            .Where(station => !string.IsNullOrWhiteSpace(station))
            .Select(station => station.Trim())
            .ToArray();
        var normalized = raw.Select(CanonicalStation).ToArray();
        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new ArgumentException("同批呼叫工位不能重复。");
        if (normalized.Length > Stations.Count)
            throw new ArgumentException("同批呼叫工位数量超过当前点表。");
        return normalized;
    }

    private void WriteInput(object value)
    {
        var input = Path.Combine(Root, "device-input.json");
        var temp = Path.Combine(Root, "device-input-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value), new UTF8Encoding(false));
            File.Move(temp, input, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private IReadOnlyList<(string Station, int Offset)> LoadStations()
    {
        using var lab = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "lab.json")));
        foreach (var path in new[]
        {
            new[] { "simulationTopology", "stations" },
            new[] { "simulationConfiguration", "stations" },
            new[] { "stations" }
        })
        {
            if (!TryProperty(lab.RootElement, out var rows, path) || rows.ValueKind != JsonValueKind.Array) continue;
            var result = new List<(string Station, int Offset)>();
            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("stationId", out var stationId) || stationId.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(stationId.GetString()) ||
                    !row.TryGetProperty("offset", out var offset) || !offset.TryGetInt32(out var value) ||
                    value is < 0 or > 1016 || value % 2 != 0)
                    throw new InvalidOperationException("lab.json中的工位编号或偏移无效。");
                result.Add((stationId.GetString()!.Trim(), value));
            }
            if (result.Count > 0 &&
                result.Select(item => item.Station).Distinct(StringComparer.OrdinalIgnoreCase).Count() == result.Count &&
                result.Select(item => item.Offset).Distinct().Count() == result.Count)
                return result;
        }
        // Compatibility with the original two-station live fixtures.
        return [("CNC-001", 0), ("CNC-002", 2)];
    }

    private static bool TryProperty(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var name in path)
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out value)) return false;
        return true;
    }
}

/// <summary>
/// UI-independent multi-round sequencer. A batch is only raised after every
/// station in the previous batch has closed its call, acknowledgement, removed,
/// return-responsibility and placed signals.
/// </summary>
public sealed class LiveBatchCallPlan
{
    private DateTimeOffset _submittedAt;
    private DateTimeOffset _callObservedAt;
    public IReadOnlyList<string> Stations { get; private set; } = [];
    public int Rounds { get; private set; }
    public int Round { get; private set; }
    public bool Active { get; private set; }
    public bool AwaitingCallFeedback { get; private set; }
    public bool AwaitingClosure { get; private set; }
    public string Status { get; private set; } = "未启动多轮呼叫";

    public void Start(IEnumerable<string> stations, int rounds, DateTimeOffset now)
    {
        var values = stations.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToArray();
        if (values.Length < 2) throw new ArgumentException("多轮呼叫至少选择两个工位。");
        if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
            throw new ArgumentException("多轮呼叫工位不能重复。");
        if (rounds is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(rounds), "轮次应为1至100。");
        Stations = Array.AsReadOnly(values);
        Rounds = rounds;
        Round = 1;
        Active = true;
        AwaitingCallFeedback = false;
        AwaitingClosure = false;
        _submittedAt = now;
        _callObservedAt = default;
        Status = $"第1/{Rounds}轮准备呼叫{values.Length}个工位";
    }

    public void Submitted(DateTimeOffset now)
    {
        if (!Active) throw new InvalidOperationException("多轮呼叫已经结束。");
        AwaitingCallFeedback = true;
        AwaitingClosure = false;
        _submittedAt = now;
        Status = $"第{Round}/{Rounds}轮已提交，等待{Stations.Count}个工位同时反馈呼叫";
    }

    public void Stop(string reason = "已停止多轮呼叫")
    {
        Active = false;
        AwaitingCallFeedback = AwaitingClosure = false;
        Status = reason;
    }

    public LiveBatchObservation Observe(IReadOnlyDictionary<string, LivePlcSnapshot> snapshots, DateTimeOffset now)
    {
        if (!Active) return new("", Status);
        if (AwaitingCallFeedback &&
            Stations.Any(station => !snapshots.TryGetValue(station, out var snapshot) || !snapshot.Values[0]))
        {
            if (now - _submittedAt > TimeSpan.FromSeconds(5))
            {
                Active = false;
                AwaitingCallFeedback = false;
                Status = $"第{Round}/{Rounds}轮提交后5秒内未观察到全部工位呼叫反馈，已停止，不自动重发。";
                return new("Failed", Status);
            }
            return new("", Status);
        }
        if (AwaitingCallFeedback)
        {
            AwaitingCallFeedback = false;
            AwaitingClosure = true;
            _callObservedAt = now;
            Status = $"第{Round}/{Rounds}轮呼叫已全部反馈；等待所有工位上一轮握手闭合";
            return new("CallObserved", Status);
        }
        if (!AwaitingClosure || now - _callObservedAt < TimeSpan.FromSeconds(1)) return new("", Status);
        if (Stations.Any(station => !snapshots.ContainsKey(station))) return new("", Status);
        if (Stations.Any(station => !LivePlcConnection.CanRaiseNewCall(snapshots[station])))
            return new("", Status);
        if (Round >= Rounds)
        {
            Active = false;
            AwaitingClosure = false;
            Status = $"多轮呼叫完成：共{Rounds}轮、{Rounds * Stations.Count}个工位任务";
            return new("Completed", Status);
        }
        Round++;
        AwaitingClosure = false;
        Status = $"第{Round}/{Rounds}轮条件已满足，准备提交";
        return new("NextRound", Status);
    }
}
