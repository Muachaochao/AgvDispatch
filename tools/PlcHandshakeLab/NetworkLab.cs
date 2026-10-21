using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PlcHandshakeLab;

public sealed class PointMapping
{
    public string Station { get; set; } = "CNC-001";
    public int Signal { get; set; }
    public int Db { get; set; } = 1;
    public int Byte { get; set; }
    public int Bit { get; set; }
    public bool Initial { get; set; }
    public string Name => Signal < 10 ? Sequence.Signals[Signal].Name : Signal == 10 ? "工位故障" : Signal == 11 ? "下游在线" : "倒料设备故障";
    public string Owner => Signal < 10 ? Sequence.Signals[Signal].Owner : "P";
    public string Address => $"DB{Db}.DBX{Byte}.{Bit}";
    public PointMapping Copy() => (PointMapping)MemberwiseClone();
}

public sealed class NetworkLabConfig
{
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 10200;
    public string AllowedClient { get; set; } = "127.0.0.1";
    public string DumperStationId { get; set; } = "DUMPER-001";
    public double AutoDoneSeconds { get; set; } = 5;
    public List<PointMapping> Points { get; set; } = [];
    public const string Shared = "共享倒料";

    public static NetworkLabConfig CreateDefault()
    {
        var config = new NetworkLabConfig();
        foreach (var (station, offset) in new[] { ("CNC-001", 0), ("CNC-002", 2) })
        {
            for (var signal = 0; signal < 6; signal++)
                config.Points.Add(new() { Station = station, Signal = signal, Byte = offset, Bit = signal < 2 ? signal : signal + 2, Initial = signal == 1 });
            config.Points.Add(new() { Station = station, Signal = 10, Byte = offset, Bit = 2 });
            config.Points.Add(new() { Station = station, Signal = 11, Byte = offset, Bit = 3, Initial = true });
        }
        for (var signal = 6; signal < 10; signal++)
            config.Points.Add(new() { Station = Shared, Signal = signal, Byte = 610, Bit = signal - 6 });
        config.Points.Add(new() { Station = Shared, Signal = 12, Byte = 610, Bit = 4 });
        return config;
    }

    public void Validate()
    {
        if (!System.Net.IPAddress.TryParse(BindAddress, out var bind) || bind.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !System.Net.IPAddress.TryParse(AllowedClient, out var peer) || peer.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("监听IP和允许调度IP必须是IPv4地址。");
        if (Port is < 1 or > 65535 || !double.IsFinite(AutoDoneSeconds) || AutoDoneSeconds < 0.1 || AutoDoneSeconds > 3600)
            throw new ArgumentException("端口应为1至65535，自动Done延时应为0.1至3600秒。");
        if (Points is null || Points.Count is < 13 or > 1000) throw new ArgumentException("点表必须包含工位和共享倒料点（最多1000项）。");
        if (Points.Any(p => p is null || string.IsNullOrWhiteSpace(p.Station) || p.Signal is < 0 or > 12 || p.Db is < 1 or > 65535 || p.Byte is < 0 or >= 8192 || p.Bit is < 0 or > 7))
            throw new ArgumentException("点表含无效的工位、信号索引、DB、字节（0..8191）或位（0..7）。");
        if (Points.GroupBy(p => (p.Db, p.Byte, p.Bit)).Any(g => g.Count() > 1) || Points.GroupBy(p => (p.Station, p.Signal)).Any(g => g.Count() > 1))
            throw new ArgumentException("点表存在重复地址或同工位重复信号。");
        var stationNames = Points.Select(p => p.Station).Distinct().ToArray();
        if (stationNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != stationNames.Length || Points.Any(p => p.Station != p.Station.Trim()))
            throw new ArgumentException("工位名称不能含首尾空白或仅大小写不同的重复名称。");
        if (string.IsNullOrWhiteSpace(DumperStationId) || DumperStationId != DumperStationId.Trim() || Points.Any(p => string.Equals(p.Station, DumperStationId, StringComparison.OrdinalIgnoreCase)) || Points.Any(p => p.Owner == "H" && p.Initial))
            throw new ArgumentException("倒料设备编号不能为空或与工位重名；H输出启动初值必须为0。");
        var shared = Points.Where(p => p.Station == Shared).Select(p => p.Signal).Order().ToArray();
        if (!shared.SequenceEqual(new[] { 6, 7, 8, 9, 12 })) throw new ArgumentException("共享倒料必须且只能包含6、7、8、9、12号信号。");
        foreach (var group in Points.Where(p => p.Station != Shared).GroupBy(p => p.Station))
            if (!group.Select(p => p.Signal).Order().SequenceEqual(new[] { 0, 1, 2, 3, 4, 5, 10, 11 }))
                throw new ArgumentException($"{group.Key}必须包含0..5、10、11号信号。");
        if (Points.All(p => p.Station == Shared)) throw new ArgumentException("至少配置一个工位。");
    }

    public string ExportSignalCsv()
    {
        Validate();
        string[] names = ["FullBoxRequested", "BoxPresent", "FullBoxRequestAcknowledged", "FullBoxRemoved", "EmptyBoxTaskAcknowledged", "EmptyBoxPlaced", "AgvAtWaitingPosition", "AgvAtDumpingPosition", "DumperReady", "DumpingCompletedMayLeave", "Faulted", "CommunicationOnline", "Faulted"];
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        return "address,variableName,chineseName,stationId,deviceType,instanceIndex,dataType,direction,access,addressStatus,enabled,setConditionZh,resetConditionZh,sourceNote\r\n" +
            string.Join("\r\n", Points.Select(p => string.Join(",", new[] { p.Address, names[p.Signal], p.Name, p.Station == Shared ? DumperStationId : p.Station,
                p.Station == Shared ? "Dumper" : "Station", "1", "Bool", p.Owner == "P" ? "PLC->Host" : "Host->PLC", p.Owner == "P" ? "Read" : "Write",
                "LabOnly", "true", "按联调条件", "按联调条件", "PLC人工联调模拟点表，非现场核定" }.Select(Q))));
    }
}

public sealed record NetworkEvent(DateTimeOffset Time, string Kind, string Station, int Stage, string Point, string Before, string After, string Detail);
public sealed record PointState(PointMapping Mapping, bool Value, DateTimeOffset UpdatedAt, string Source, string Requested);

/// <summary>单一DB内存；UI只写P，网络只写H；所有访问及日志由同一把锁串行。</summary>
public sealed class NetworkLab : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, byte[]> _dbs;
    private readonly Dictionary<(int, int, int), PointMapping> _addresses;
    private readonly Dictionary<(string, int), PointState> _states;
    private readonly List<NetworkEvent> _events = [];
    private readonly StreamWriter _journal;
    private readonly Stopwatch _stageClock = Stopwatch.StartNew();
    private readonly Dictionary<string, int> _stages = new();
    private readonly Dictionary<string, long> _stageSince = new();
    private bool _automatic;
    private bool _permit;
    private long? _dumpSince;
    private int _autoDone;
    public NetworkLabConfig Config { get; }
    public string JournalPath { get; }
    private string? _fault;
    public string? Fault { get { lock (_gate) return _fault; } }
    public bool Automatic { get { lock (_gate) return _automatic; } }

    public NetworkLab(NetworkLabConfig config, string journalPath)
    {
        config.Validate();
        // Isolate the live address map from any subsequent editor changes.
        Config = JsonSerializer.Deserialize<NetworkLabConfig>(JsonSerializer.Serialize(config))!;
        _dbs = Config.Points.Select(p => p.Db).Distinct().ToDictionary(db => db, _ => new byte[8192]);
        _addresses = Config.Points.ToDictionary(p => (p.Db, p.Byte, p.Bit));
        _states = Config.Points.ToDictionary(p => (p.Station, p.Signal), p => new PointState(p, p.Initial, DateTimeOffset.Now, "初值", p.Initial ? "1" : "0"));
        foreach (var p in Config.Points) if (p.Initial) _dbs[p.Db][p.Byte] |= (byte)(1 << p.Bit);
        JournalPath = Path.GetFullPath(journalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        _journal = new StreamWriter(new FileStream(JournalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        try { Record("会话", "", "", "", "", "启动：人工PLC模式；内存来自点表初值，不恢复旧H输出。" ); }
        catch { try { _journal.Dispose(); } catch (IOException) { } throw; }
    }

    public PointState[] Snapshot(string station)
    {
        lock (_gate) return _states.Values.Where(s => s.Mapping.Station == station || s.Mapping.Station == NetworkLabConfig.Shared).OrderBy(s => s.Mapping.Signal).ToArray();
    }
    public NetworkEvent[] Events() { lock (_gate) return _events.ToArray(); }
    public int Stage(string station) { lock (_gate) return _stages.GetValueOrDefault(station); }
    public double StageElapsed(string station) { lock (_gate) return _stageSince.TryGetValue(station, out var since) ? Stopwatch.GetElapsedTime(since).TotalSeconds : 0; }

    public void SelectStage(string station, int stage)
    {
        lock (_gate)
        {
            if (stage is < 0 or > 27 || !_states.ContainsKey((station, 0))) throw new ArgumentException("无效工位或阶段。");
            _stages[station] = stage;
            _stageSince[station] = Stopwatch.GetTimestamp();
            Record("阶段标记", station, "", "", "", $"人工选择 {stage + 1:00} {Sequence.Stages[stage].Name}；只标记，不改变电平。");
        }
    }

    public void ManualSet(string station, int signal, bool value)
    {
        lock (_gate)
        {
            var p = _states[(station, signal)].Mapping;
            if (p.Owner != "P") throw new InvalidOperationException("H输出只允许调度经S7写入。");
            if (_automatic && (signal is 8 or 9 || signal == 0 && !value)) throw new InvalidOperationException("自动握手控制撤呼叫/Ready/Done；请切回人工模式后操作。");
            Set(p, value, "人工置位", true);
        }
    }

    public void SetAutomatic(bool automatic)
    {
        lock (_gate)
        {
            if (_automatic == automatic) return;
            if (automatic && (Get(NetworkLabConfig.Shared, 7) || Get(NetworkLabConfig.Shared, 8) || Get(NetworkLabConfig.Shared, 9)))
                throw new InvalidOperationException("存在倒料周期、Ready或Done反馈，请先人工闭合后再切入自动模式。");
            _automatic = automatic; _permit = false; _dumpSince = null; _autoDone = 0;
            Record("模式", "", "", "", "", automatic ? "自动PLC握手：呼叫应答撤销、Ready握手及定时Done；箱体仍由人工确认。非真实动作。" : "切回人工：保留所有电平，停止自动反馈。");
        }
    }

    // Invoked on UI timer; monotonic time, independent of table selection and demo playback.
    public void TickAutomatic()
    {
        lock (_gate)
        {
            if (!_automatic) return;
            foreach (var station in Config.Points.Where(p => p.Signal == 0).Select(p => p.Station))
                if (Get(station, 0) && Get(station, 2) && !Get(station, 10) && Get(station, 11)) Set(Find(station, 0), false, "自动撤呼叫");
            var shared = NetworkLabConfig.Shared;
            if (Get(shared, 12) || Config.Points.Where(p => p.Signal == 0).Any(p => Get(p.Station, 10) || !Get(p.Station, 11)))
            {
                // Fault time must not count as a successful simulated dumping interval.
                if (_autoDone == 0) _dumpSince = null;
                return;
            }
            if (Get(shared, 6) && !Get(shared, 7) && !_permit && !Get(shared, 9))
            { Set(Find(shared, 8), true, "自动授予许可"); _permit = true; }
            if (_permit && !Get(shared, 6)) Set(Find(shared, 8), false, "自动消费许可");
            if (Get(shared, 7) && _permit && !Get(shared, 6))
            {
                _dumpSince ??= Stopwatch.GetTimestamp();
                if (_autoDone == 0 && Stopwatch.GetElapsedTime(_dumpSince.Value).TotalSeconds >= Config.AutoDoneSeconds)
                { Set(Find(shared, 9), true, "定时模拟倒料完成（非物理证据）"); _autoDone = 1; }
            }
            else if (_dumpSince is not null)
            {
                Set(Find(shared, 9), false, "自动退出闭合"); _dumpSince = null; _permit = false; _autoDone = 0;
            }
        }
    }

    public void Observe(string station, string vehicle, string task, string observation)
    {
        lock (_gate)
        {
            var values = string.Join(" ", Snapshot(station).Select(p => $"{p.Mapping.Address}={(p.Value ? 1 : 0)}"));
            Record("现场观察", station, "", "", "", $"车辆={vehicle}；任务={task}；{observation}；快照 {values}（人工记录，不自动改变信号）");
        }
    }
    public void Note(string kind, string detail) { lock (_gate) Record(kind, "", "", "", "", detail); }

    public byte[]? Read(int db, int offset, int count)
    {
        lock (_gate) return _dbs.TryGetValue(db, out var bytes) && count > 0 && offset >= 0 && offset <= bytes.Length - count ? bytes.AsSpan(offset, count).ToArray() : null;
    }
    public bool WriteHost(int db, int offset, int bit, byte[] data, bool bitWrite, string peer)
    {
        lock (_gate)
        {
            if (!_dbs.TryGetValue(db, out var bytes) || offset < 0 || data.Length == 0 || offset > bytes.Length - data.Length || bit is < 0 or > 7) return false;
            if (bitWrite)
            {
                if (data.Length != 1 || data[0] > 1 || !_addresses.TryGetValue((db, offset, bit), out var p) || p.Owner != "H")
                { Record("拒绝写入", "", $"DB{db}.{offset}.{bit}", "", "", peer + " 请求非H位或无效位值"); return false; }
                Set(p, data[0] != 0, "S7 " + peer);
                return true;
            }
            // Accept BYTE read/modify/write only if every altered bit belongs to H.
            // Validate the whole item first so a rejected write has no partial effect.
            for (var i = 0; i < data.Length; i++)
                for (var b = 0; b < 8; b++)
                    if (((bytes[offset + i] ^ data[i]) & (1 << b)) != 0 &&
                        (!_addresses.TryGetValue((db, offset + i, b), out var p) || p.Owner != "H"))
                    { Record("拒绝写入", "", $"DB{db}.{offset + i}.{b}", "", "", peer + " 的BYTE写入会改变P位或未映射位"); return false; }
            for (var i = 0; i < data.Length; i++)
                for (var b = 0; b < 8; b++)
                    if (_addresses.TryGetValue((db, offset + i, b), out var p) && p.Owner == "H") Set(p, (data[i] & (1 << b)) != 0, "S7 " + peer);
            return true;
        }
    }

    private PointMapping Find(string station, int signal) => _states[(station, signal)].Mapping;
    private bool Get(string station, int signal) => _states[(station, signal)].Value;
    private void Set(PointMapping p, bool value, string source, bool force = false)
    {
        var old = _states[(p.Station, p.Signal)];
        if (old.Value == value && !force) return;
        // Journal before publishing a change; a disk failure cannot silently drop evidence.
        Record(source, p.Station, p.Address + " " + p.Name, old.Value ? "1" : "0", value ? "1" : "0", "请求=" + (value ? 1 : 0) + "；应用到模拟DB；非物理反馈");
        if (value) _dbs[p.Db][p.Byte] |= (byte)(1 << p.Bit); else _dbs[p.Db][p.Byte] &= (byte)~(1 << p.Bit);
        _states[(p.Station, p.Signal)] = new(p, value, DateTimeOffset.Now, source, value ? "1" : "0");
    }
    private void Record(string kind, string station, string point, string before, string after, string detail)
    {
        var e = new NetworkEvent(DateTimeOffset.Now, kind, station, _stages.TryGetValue(station, out var marked) ? marked + 1 : 0, point, before, after,
            $"会话+{_stageClock.Elapsed.TotalSeconds:F1}s；{detail}");
        try { _journal.WriteLine(JsonSerializer.Serialize(e)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _fault = "会话日志写入失败：" + ex.Message; throw; }
        _events.Add(e); if (_events.Count > 500) _events.RemoveAt(0);
    }
    public string ExportCsv()
    {
        lock (_gate)
        {
            _journal.Flush();
            static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
            var csv = new StringBuilder("时间,类型,工位,人工标记阶段,点位,原值,新值,说明\r\n");
            // Export the durable full session, not only the UI's last 500 events.
            using var reader = new StreamReader(new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                var e = JsonSerializer.Deserialize<NetworkEvent>(line)!;
                csv.AppendLine(string.Join(",", new[] { e.Time.ToString("O", CultureInfo.InvariantCulture), e.Kind, e.Station, e.Stage.ToString(), e.Point, e.Before, e.After, e.Detail }.Select(Q)));
            }
            return csv.ToString();
        }
    }
    public void Dispose() { lock (_gate) _journal.Dispose(); }
}
