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
                p.Station == Shared ? "Dumper" : "ChipRemovalStation", "1", "Bool", p.Owner == "P" ? "PLC->Host" : "Host->PLC", p.Owner == "P" ? "Read" : "Write",
                "LabOnly", "true", "按联调条件", "按联调条件", "PLC人工联调模拟点表，非现场核定" }.Select(Q))));
    }
}

public sealed record NetworkEvent(DateTimeOffset Time, string Kind, string Station, int Stage, string Point, string Before, string After, string Detail);
public sealed record PointState(PointMapping Mapping, bool Value, DateTimeOffset UpdatedAt, string Source, string Requested,
    DateTimeOffset? HostAccessedAt = null, string HostAccess = "尚未交换");
public sealed record StageTestResult(DateTimeOffset Time, string Station, int Round, int Stage, string Outcome,
    string Vehicle, string TaskId, string Note, string Differences, string Snapshot);
public sealed record CommunicationStatus(bool Fresh, string Label)
{
    public const double MaxAgeSeconds = 3;
    public static CommunicationStatus Evaluate(bool running, int clients, DateTimeOffset? last, DateTimeOffset now) =>
        !running ? new(false, "服务未运行 · 电平仅供回看") : clients == 0 ? new(false, "调度未连接 / 已断开 · H值为保留内存") :
        last is null ? new(false, "已连接，等待S7数据交换") : now - last > TimeSpan.FromSeconds(MaxAgeSeconds) || last > now ?
        new(false, "通信停滞 · 超过3秒无S7数据交换 · H值需重新核对") : new(true, "调度通信正常 · S7数据交换有效");
    public static bool PointFresh(PointState point, DateTimeOffset now) => point.HostAccessedAt is { } at && at >= point.UpdatedAt && at <= now &&
        now - at <= TimeSpan.FromSeconds(MaxAgeSeconds);
}

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
    private string? _callRun;
    private string[] _callStations = [];
    private int _callLimit;
    private int _callCompleted;
    private bool _stopCalls;
    private bool _callOutstanding;
    private bool _sawRemoved;
    private bool _sawPlaced;
    public string CallPlanStatus { get { lock (_gate) return _callRun is null ? "未启动循环呼叫" :
        $"循环呼叫 {_callCompleted}/{_callLimit} 次工位任务 · {(_stopCalls ? "停止新增，等待闭合" : "运行中")}"; } }

    // Only P signals are generated here. The scheduler still owns every motion and H output.
    public void ConfigureCalls(string runId, string[] stations, int rounds, bool stop)
    {
        lock (_gate)
        {
            if (stop)
            {
                if (_callRun is null) return; // A restarted tool has no call producer to stop; never clears P/H.
                if (_callRun != runId) throw new InvalidOperationException("停止编号不属于本次工具会话。");
                _stopCalls = true; Record("停止新增呼叫", "", "", "", "", runId); return;
            }
            if (!Guid.TryParseExact(runId, "N", out _) || stations is null || stations.Length is < 1 or > 2 ||
                stations.Distinct().Count() != stations.Length || rounds is < 1 or > 100 ||
                stations.Any(s => s is not ("CNC-001" or "CNC-002") || !_states.ContainsKey((s, 0))))
                throw new ArgumentException("循环工位、轮数或运行编号无效。");
            if (_callRun == runId)
            {
                if (!_callStations.SequenceEqual(stations) || _callLimit != rounds * stations.Length)
                    throw new InvalidOperationException("同一运行编号不能修改顺序或轮数。");
                return;
            }
            if (_callOutstanding || _callRun is not null && !_stopCalls && _callCompleted < _callLimit)
                throw new InvalidOperationException("原循环仍未结束。");
            if (!_communicationHealthy || _recoveryReason is not null || _permit ||
                Config.Points.Any(p => p.Owner == "H" && Get(p.Station, p.Signal)) ||
                Config.Points.Any(p => p.Signal == 0 && Get(p.Station, p.Signal)) ||
                Enumerable.Range(6, 4).Any(s => Get(NetworkLabConfig.Shared, s)))
                throw new InvalidOperationException("须先恢复S7通信并闭合全部呼叫和握手基线。");
            SetAutomatic(true);
            _callRun = runId; _callStations = stations.ToArray(); _callLimit = rounds * stations.Length;
            _callCompleted = 0; _stopCalls = false; _sawRemoved = _sawPlaced = false;
            Record("启动循环呼叫", "", "", "", "", $"{runId}；{string.Join("→", stations)}；{rounds}轮；P箱体反馈为模拟值，非物理证据。");
        }
    }

    private void TickCalls()
    {
        if (_callRun is null || _recoveryReason is not null || !_communicationHealthy) return;
        var station = _callStations[_callCompleted % _callStations.Length];
        if (Get(station, 10) || !Get(station, 11) || Get(NetworkLabConfig.Shared, 12)) return;
        if (_callOutstanding)
        {
            if (Get(station, 3)) { _sawRemoved = true; Set(Find(station, 1), false, "循环模拟取箱，非物理反馈"); }
            if (_sawRemoved && Get(station, 5)) { _sawPlaced = true; Set(Find(station, 1), true, "循环模拟返箱，非物理反馈"); }
            if (!_sawPlaced || !_sawRemoved || Get(station, 0) || _permit ||
                Config.Points.Any(p => p.Owner == "H" && Get(p.Station, p.Signal)) ||
                Enumerable.Range(6, 4).Any(s => Get(NetworkLabConfig.Shared, s))) return;
            _callCompleted++; _callOutstanding = false; _sawRemoved = _sawPlaced = false;
            Record("循环握手闭合", station, "", "", "", $"已闭合{_callCompleted}/{_callLimit}；仅PLC侧证据，任务完成由调度核验。");
            return;
        }
        if (_stopCalls || _callCompleted >= _callLimit) return;
        if (!Get(station, 1) || Config.Points.Any(p => p.Owner == "H" && Get(p.Station, p.Signal)) ||
            Config.Points.Any(p => p.Signal == 0 && Get(p.Station, p.Signal)) || _permit ||
            Enumerable.Range(6, 4).Any(s => Get(NetworkLabConfig.Shared, s))) return;
        Set(Find(station, 0), true, "循环产生满箱呼叫"); _callOutstanding = true;
    }
    private bool _permit;
    private long? _dumpSince;
    private int _autoDone;
    private bool _cycleStarted;
    private string? _recoveryReason;
    private bool _communicationHealthy = true;
    private readonly List<StageTestResult> _testResults = [];
    private readonly Dictionary<string, int> _rounds = new();
    public NetworkLabConfig Config { get; }
    public string JournalPath { get; }
    private string? _fault;
    public string? Fault { get { lock (_gate) return _fault; } }
    public bool Automatic { get { lock (_gate) return _automatic; } }
    public string AutomaticStatus { get { lock (_gate) return _recoveryReason is { } reason ? "暂停待核对：" + reason :
        !_communicationHealthy ? "等待有效调度通信" : Get(NetworkLabConfig.Shared, 12) ? "倒料故障，自动计时暂停" : !_permit ? "空闲，等待本轮申请" : _autoDone == 1 ? "Done已给出，等待实际退出闭合" :
        _cycleStarted ? "本轮倒料计时中" : Get(NetworkLabConfig.Shared, 8) ? "Ready已授予，等待消费" : "Ready已消费，内部设备保留继续"; } }

    public void UpdateCommunication(bool healthy)
    {
        lock (_gate)
        {
            if (_communicationHealthy == healthy) return;
            Record(healthy ? "通信恢复" : "通信中断", "", "", "", "", healthy ? "收到有效S7数据交换；旧周期仍须按恢复提示核对。" : "暂停自动反馈，保留全部电平。");
            _communicationHealthy = healthy;
            if (!healthy && _callRun is not null && (_callOutstanding || !_stopCalls && _callCompleted < _callLimit))
            {
                _stopCalls = true;
                RequireRecovery("循环期间通信中断，已停止新增呼叫；原电平保留，请人工核对闭合。");
            }
            if (!healthy && (_permit || Enumerable.Range(6, 4).Any(s => Get(NetworkLabConfig.Shared, s))))
                RequireRecovery("活动许可或共享信号期间通信中断；请人工闭合并确认模拟周期。");
        }
    }

    private void RequireRecovery(string reason)
    {
        if (_recoveryReason == reason) return;
        Record("自动恢复门禁", NetworkLabConfig.Shared, "", "", "", reason);
        _recoveryReason = reason; _dumpSince = null;
    }

    public void ConfirmCycleClosed(string note)
    {
        lock (_gate)
        {
            if (_automatic || !_communicationHealthy || Get(NetworkLabConfig.Shared, 12) ||
                Enumerable.Range(6, 4).Any(s => Get(NetworkLabConfig.Shared, s)) || string.IsNullOrWhiteSpace(note))
                throw new InvalidOperationException("请切人工模式，恢复通信、清除倒料故障，核对共享申请/周期/Ready/Done全部为0，并填写闭合依据。此操作只解除模拟器内部保留。");
            Record("确认模拟周期闭合", NetworkLabConfig.Shared, "", "", "", note.Trim());
            _permit = false; _cycleStarted = false; _dumpSince = null; _autoDone = 0; _recoveryReason = null;
        }
    }

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
            if (_permit && signal is 8 or 9) RequireRecovery("人工接管过Ready/Done；需核对并确认模拟周期闭合。");
            if (signal == 12 && value && _autoDone == 0) _dumpSince = null;
        }
    }

    public void SetAutomatic(bool automatic)
    {
        lock (_gate)
        {
            if (_automatic == automatic) return;
            if (automatic && (_recoveryReason is not null || Get(NetworkLabConfig.Shared, 7) || Get(NetworkLabConfig.Shared, 8) || Get(NetworkLabConfig.Shared, 9)))
                throw new InvalidOperationException(_recoveryReason ?? "存在倒料周期、Ready或Done反馈，请先人工闭合后再切入自动模式。");
            // A consumed Ready has no visible high bit in stages12/13. Preserve that reservation across mode changes.
            _automatic = automatic; _dumpSince = null;
            Record("模式", "", "", "", "", automatic ? "自动PLC握手：呼叫应答撤销、Ready握手及定时Done；箱体仍由人工确认。非真实动作。" : "切回人工：保留所有电平，停止自动反馈。");
        }
    }

    // Invoked on UI timer; monotonic time, independent of table selection and demo playback.
    public void TickAutomatic()
    {
        lock (_gate)
        {
            if (!_automatic || !_communicationHealthy) return;
            TickCalls();
            foreach (var station in Config.Points.Where(p => p.Signal == 0).Select(p => p.Station))
                if (Get(station, 0) && Get(station, 2) && !Get(station, 10) && Get(station, 11)) Set(Find(station, 0), false, "自动撤呼叫");
            var shared = NetworkLabConfig.Shared;
            // Station faults inhibit that station's call acknowledgement only. Shared dumping has its own fault input.
            if (Get(shared, 12))
            {
                // Fault time must not count as a successful simulated dumping interval.
                if (_autoDone == 0) _dumpSince = null;
                return;
            }
            if (_recoveryReason is not null) return;
            if (_permit && !_cycleStarted && !Get(shared, 8) && Get(shared, 6))
            { RequireRecovery("许可消费后再次提出等待申请；请核对原周期，不能自动授予第二份许可。"); return; }
            if (_cycleStarted && !Get(shared, 7))
            {
                if (_autoDone == 1)
                {
                    Set(Find(shared, 9), false, "自动退出闭合");
                    _dumpSince = null; _permit = false; _autoDone = 0; _cycleStarted = false;
                }
                else RequireRecovery("倒料完成前周期被撤销；不能把取消当作正常完成。");
                return;
            }
            if (Get(shared, 7) && !_permit)
            { RequireRecovery("未取得本轮许可即出现倒料周期；请人工核对闭合。"); return; }
            if (Get(shared, 6) && !Get(shared, 7) && !_permit && !Get(shared, 9))
            { Set(Find(shared, 8), true, "自动授予许可"); _permit = true; }
            if (_permit && !Get(shared, 6)) Set(Find(shared, 8), false, "自动消费许可");
            if (Get(shared, 7) && _permit && !Get(shared, 6))
            {
                _cycleStarted = true;
                _dumpSince ??= Stopwatch.GetTimestamp();
                if (_autoDone == 0 && Stopwatch.GetElapsedTime(_dumpSince.Value).TotalSeconds >= Config.AutoDoneSeconds)
                { Set(Find(shared, 9), true, "定时模拟倒料完成（非物理证据）"); _autoDone = 1; }
            }
        }
    }

    public int TestRound(string station) { lock (_gate) return _rounds.GetValueOrDefault(station, 1); }
    public void BeginTestRound(string station)
    {
        lock (_gate)
        {
            if (!_states.ContainsKey((station, 0))) throw new ArgumentException("工位不存在。");
            var round = TestRound(station) + 1;
            Record("新一轮测试记录", station, "", "", "", $"第{round}轮；只分组记录，不复位点位或调度任务。");
            _rounds[station] = round;
        }
    }
    public StageTestResult[] TestResults() { lock (_gate) return _testResults.ToArray(); }
    public void RecordStageTest(string station, string outcome, string vehicle, string taskId, string note, bool physicalConfirmed)
    {
        lock (_gate)
        {
            if (!_stages.TryGetValue(station, out var stage) || outcome is not ("通过" or "失败" or "跳过")) throw new ArgumentException("先标记有效阶段并选择结果。");
            var points = Snapshot(station).Where(p => p.Mapping.Signal < 10).ToArray();
            var now = DateTimeOffset.Now;
            var differences = string.Join("；", points.Where(p => !Sequence.Matches(stage, p.Mapping.Signal, p.Value))
                .Select(p => $"{p.Mapping.Name}实际{(p.Value ? 1 : 0)}，期望{Sequence.Stages[stage].Pattern[p.Mapping.Signal]}"));
            if (string.IsNullOrWhiteSpace(note)) throw new InvalidOperationException("请填写实际观察或失败/跳过原因。");
            if (outcome == "通过" && (!physicalConfirmed || !_communicationHealthy || _recoveryReason is not null || differences.Length > 0 ||
                Snapshot(station).Any(p => p.Mapping.Signal is 10 or 12 && p.Value || p.Mapping.Signal == 11 && !p.Value) ||
                points.Any(p => !CommunicationStatus.PointFresh(p, now)) || string.IsNullOrWhiteSpace(vehicle) || string.IsNullOrWhiteSpace(taskId)))
                throw new InvalidOperationException("通过需要：有效通信、10点均有3秒内交换记录、电平符合、无工位/倒料故障且下游在线、无恢复门禁，并填写车辆/任务/观察依据及勾选实际条件已核实。");
            var result = new StageTestResult(now, station, TestRound(station), stage + 1, outcome, vehicle.Trim(), taskId.Trim(), note.Trim(), differences,
                string.Join(" ", points.Select(p => $"{p.Mapping.Address}={(p.Value ? 1 : 0)}[交换={p.HostAccessedAt:O}]")));
            Record("阶段测试", station, "", "", "", JsonSerializer.Serialize(result));
            _testResults.Add(result);
        }
    }
    public SessionCapture CaptureSession()
    {
        lock (_gate)
        {
            _journal.Flush();
            return new SessionCapture(JournalPath, JsonSerializer.Serialize(Config));
        }
    }
    public string ExportStageReport()
    {
        using var capture = CaptureSession();
        var history = SessionHistory.Read(capture);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        history.WriteReport(writer);
        return writer.ToString();
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

    public byte[]? Read(int db, int offset, int count, string? peer = null, int? bit = null)
    {
        lock (_gate)
        {
            if (!_dbs.TryGetValue(db, out var bytes) || count <= 0 || offset < 0 || offset > bytes.Length - count) return null;
            if (peer is not null)
                foreach (var p in Config.Points.Where(p => p.Db == db && p.Byte >= offset && p.Byte < offset + count && (bit is null || p.Bit == bit))) Touch(p, "S7读取 " + peer);
            return bytes.AsSpan(offset, count).ToArray();
        }
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
                Touch(p, "S7写入 " + peer);
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
                    if (_addresses.TryGetValue((db, offset + i, b), out var p) && p.Owner == "H")
                    { Set(p, (data[i] & (1 << b)) != 0, "S7 " + peer); Touch(p, "S7写入 " + peer); }
            return true;
        }
    }

    private PointMapping Find(string station, int signal) => _states[(station, signal)].Mapping;
    private bool Get(string station, int signal) => _states[(station, signal)].Value;
    private void Touch(PointMapping p, string access)
    {
        var key = (p.Station, p.Signal);
        _states[key] = _states[key] with { HostAccessedAt = DateTimeOffset.Now, HostAccess = access };
    }
    private void Set(PointMapping p, bool value, string source, bool force = false)
    {
        var old = _states[(p.Station, p.Signal)];
        if (old.Value == value && !force) return;
        // Journal before publishing a change; a disk failure cannot silently drop evidence.
        Record(source, p.Station, p.Address + " " + p.Name, old.Value ? "1" : "0", value ? "1" : "0", "请求=" + (value ? 1 : 0) + "；应用到模拟DB；非物理反馈");
        if (value) _dbs[p.Db][p.Byte] |= (byte)(1 << p.Bit); else _dbs[p.Db][p.Byte] &= (byte)~(1 << p.Bit);
        _states[(p.Station, p.Signal)] = old with { Value = value, UpdatedAt = DateTimeOffset.Now, Source = source, Requested = value ? "1" : "0" };
        if (p.Station == NetworkLabConfig.Shared && p.Signal == 7)
        {
            if (value && _permit)
            {
                _cycleStarted = true;
                if (Get(NetworkLabConfig.Shared, 6) || Get(NetworkLabConfig.Shared, 8)) RequireRecovery("等待申请/Ready握手未闭合就出现倒料周期。");
            }
            else if (value && _automatic) RequireRecovery("未取得本轮许可即出现倒料周期；请人工核对闭合。");
            else if (!value && _cycleStarted && _autoDone == 0) RequireRecovery("倒料完成前周期被撤销；不能把取消当作正常完成。");
        }
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
        using var capture = CaptureSession();
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        SessionExports.WriteCsv(capture, writer);
        return writer.ToString();
    }
    public void Dispose() { lock (_gate) _journal.Dispose(); }
}
