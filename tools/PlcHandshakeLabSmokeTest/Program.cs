using PlcHandshakeLab;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

Check(Sequence.Stages.Length == 28 && Sequence.Signals.Length == 10, "表格尺寸");
foreach (var stage in Sequence.Stages)
    Check(stage.Pattern.Length == 10 && stage.Pattern.All(c => c is '0' or '1' or '?'), "合法电平定义");
var session = new LabSession();
Check(session.Current == 0 && session.Values[1] && session.Values.Count(v => v) == 1, "初始箱体在位");
for (var stage = 1; stage < 28; stage++)
{
    Check(session.Advance(false) && session.Current == stage && session.IsAligned, "完整单轮推进");
    if (stage is >= 6 and <= 25) Check(session.Values[4], "返箱责任必须保持至第27步");
    if (stage is >= 6 and <= 23) Check(session.Values[3], "取箱通知保持至第25步");
    if (stage is >= 13 and <= 17) Check(session.Values[7], "到倒料点后，发返程指令不能清倒料周期");
}
Check(!session.Advance(false) && session.Current == 27, "非循环播放在末步停止");
Check(session.Advance(true) && session.Current == 0 && session.Values[1], "循环返回初始");
session.GoTo(9);
Check(session.Values[6] && session.Values[8], "第10步许可授予");
session.GoTo(10);
Check(!session.Values[6] && session.Values[8], "先撤请求，再撤Ready");
session.GoTo(11);
Check(!session.Values[8] && session.Values[4] && Sequence.Stages[11].Condition.Contains("设备保留继续"), "Ready撤销仍保持本轮设备保留语义");
session.GoTo(17);
Check(session.Values[7] && session.Values[9], "发返回指令时保持周期及Done");
session.GoTo(18);
Check(!session.Values[7] && session.Values[9], "核实实际退出后先撤周期");
Check(Sequence.Edge(18, 7) == "↓", "实际退出的下降沿");
session.GoTo(19);
Check(!session.Values[7] && !session.Values[9] && session.Values[4], "PLC闭合后Done回低，返箱责任继续");
session.GoTo(21);
Check(!session.Values[1] && session.IsAligned, "第22步传感器滞后");
session.Toggle(1);
Check(session.Values[1] && session.IsAligned, "第22步传感器先到也有效");
session.GoTo(21);
Check(session.Values[1], "可选值恢复不伪造传感器下降沿");
session.Toggle(4);
Check(!session.IsAligned && !session.Advance(false) && session.Current == 21, "手动偏差阻止自动推进");
session.GoTo(21);
Check(session.IsAligned, "恢复标准值清除偏差");
session.GoTo(24);
Check(!session.Values[3] && session.Values[4] && session.Values[5], "先清取箱通知");
session.GoTo(25);
Check(!session.Values[3] && session.Values[4] && !session.Values[5], "再清放箱通知");
session.GoTo(26);
Check(!session.Values[4], "最后清返箱责任");
session.Note("测试", "含逗号,与\"引号\"");
Check(session.ExportCsv().Contains("\"含逗号,与\"\"引号\"\"\""), "CSV转义");
for (var i = 0; i < 600; i++) session.Toggle(0);
Check(session.Events.Count == 500, "日志容量受控");
session.Reset();
Check(session.Values.Count(v => v) == 1 && session.Values[1], "重置清除手动电平");
try { session.GoTo(28); throw new Exception("越界未拒绝"); }
catch (ArgumentOutOfRangeException) { checks++; }
var root = Path.Combine(Path.GetTempPath(), "plc-lab-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "service"));
try
{
    void Config(string ip = "127.0.0.1", bool automatic = false) => File.WriteAllText(Path.Combine(root,"service","appsettings.json"), System.Text.Json.JsonSerializer.Serialize(new { Plc = new { IpAddress = ip }, TaskCreation = new { AutomaticCreationEnabled = automatic }, Agv = new { Robots = new[] { new { IpAddress = "127.0.0.1" } } } }));
    var memory = new byte[1024]; memory[0] = 10; memory[2] = 10; memory[4] = 10; memory[6] = 10;
    void State() => File.WriteAllText(Path.Combine(root,"device-state.json"), System.Text.Json.JsonSerializer.Serialize(new { memory_hex = Convert.ToHexString(memory) }));
    void Reject(Action operation, string message)
    {
        var rejected = false;
        try { operation(); } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { rejected = true; }
        Check(rejected,message);
    }
    File.WriteAllText(Path.Combine(root,"lab.json"),System.Text.Json.JsonSerializer.Serialize(new
    {
        simulationTopology = new
        {
            stations = new[] { ("CNC-001", 0), ("CNC-002", 2), ("CNC-003", 4), ("CNC-004", 6) }
                .Select(item => new { stationId = item.Item1, offset = item.Item2 }).ToArray()
        }
    }));
    File.WriteAllText(Path.Combine(root,"navigation.agvmap.json"),"{}");
    Config(); State(); var live = new LivePlcConnection(root);
    Check(live.Stations.SequenceEqual(new[] { "CNC-001", "CNC-002", "CNC-003", "CNC-004" }), "从实例点表读取全部工位");
    Check(live.Read("CNC-001").Values[1] && !live.Read("CNC-002").Values[8], "实时映射箱体与共享倒料");
    var original = File.ReadAllText(Path.Combine(root,"device-state.json"));
    live.Call("CNC-002");
    using (var input = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"device-input.json"))))
        Check(input.RootElement.GetProperty("call").GetString()=="CNC-002" && Guid.TryParseExact(input.RootElement.GetProperty("sequence").GetString(),"N",out _),"呼叫仅发布唯一编号输入");
    Check(original==File.ReadAllText(Path.Combine(root,"device-state.json")),"不改写设备快照或H输出");
    memory[2] |= 1; State(); var sent=File.ReadAllText(Path.Combine(root,"device-input.json")); live.Call("CNC-002");
    Check(sent==File.ReadAllText(Path.Combine(root,"device-input.json")),"重复高电平不重新发输入");
    memory[2] &= unchecked((byte)~1); State();
    live.CallMany(["CNC-001", "CNC-003", "CNC-004"]);
    using (var input = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"device-input.json"))))
        Check(input.RootElement.GetProperty("calls").EnumerateArray().Select(item => item.GetString()).SequenceEqual(
            new[] { "CNC-001", "CNC-003", "CNC-004" }), "同批呼叫使用一次输入提交全部工位");
    memory[2] = 10 | 64; State(); Reject(()=>live.Call("CNC-002"),"返箱责任未结束拒绝新呼叫");
    memory[2] = 10; memory[0] |= 16; State(); Check(live.Read("CNC-001").Values[2] && !live.Read("CNC-002").Values[2],"工位应答分别映射");
    memory[610] |= 2 | 8; State(); Check(live.Read("CNC-002").Values[7] && live.Read("CNC-001").Values[9],"倒料输出共享映射");
    memory[2] = 2; State(); Reject(()=>live.Call("CNC-002"),"下游离线拒绝输入");
    memory[2] = 14; State(); Reject(()=>live.Call("CNC-002"),"工位故障拒绝输入");
    memory[2] = 8; State(); Reject(()=>live.Call("CNC-002"),"无箱拒绝输入");
    File.SetLastWriteTimeUtc(Path.Combine(root,"device-state.json"),DateTime.UtcNow.AddSeconds(-10));
    Reject(()=>live.Read("CNC-001"),"过期显示未知"); Reject(()=>live.Call("CNC-001"),"过期禁止写输入");
    State(); Config("192.0.2.1"); Reject(()=>live.Call("CNC-002"),"运行配置改为现场地址立即阻断");
    Config(automatic:true); Reject(()=>new LivePlcConnection(root),"自动建单实例不允许联调输入");
    Config(); File.WriteAllText(Path.Combine(root,"STOP"),""); Reject(()=>live.Read("CNC-001"),"停止实例不沿用快照");
    Reject(()=>live.Read("CNC-005"),"不推算未知工位");

    var planNow = DateTimeOffset.UtcNow;
    LivePlcSnapshot BatchSnapshot(string station, int offset, bool call, bool acknowledgement = false) =>
        new(station, [call, true, acknowledgement, false, false, false, false, false, false, false],
            planNow, false, true, offset);
    static IReadOnlyDictionary<string, LivePlcSnapshot> Snapshots(params LivePlcSnapshot[] values) =>
        values.ToDictionary(value => value.Station, StringComparer.OrdinalIgnoreCase);
    var plan = new LiveBatchCallPlan();
    plan.Start(["CNC-001", "CNC-002", "CNC-003", "CNC-004"], 2, planNow);
    plan.Submitted(planNow);
    var observed = plan.Observe(Snapshots(
        BatchSnapshot("CNC-001", 0, true), BatchSnapshot("CNC-002", 2, true),
        BatchSnapshot("CNC-003", 4, true), BatchSnapshot("CNC-004", 6, true)), planNow.AddMilliseconds(200));
    Check(observed.Action == "CallObserved", "多轮计划确认四工位同一批反馈");
    Check(plan.Observe(Snapshots(
        BatchSnapshot("CNC-001", 0, false, true), BatchSnapshot("CNC-002", 2, false),
        BatchSnapshot("CNC-003", 4, false), BatchSnapshot("CNC-004", 6, false)), planNow.AddSeconds(2)).Action == "",
        "任一工位握手未闭合时不进入下一轮");
    Check(plan.Observe(Snapshots(
        BatchSnapshot("CNC-001", 0, false), BatchSnapshot("CNC-002", 2, false),
        BatchSnapshot("CNC-003", 4, false), BatchSnapshot("CNC-004", 6, false)), planNow.AddSeconds(2)).Action == "NextRound",
        "全部工位闭环后才发起下一轮");
    plan.Submitted(planNow.AddSeconds(2));
    plan.Observe(Snapshots(
        BatchSnapshot("CNC-001", 0, true), BatchSnapshot("CNC-002", 2, true),
        BatchSnapshot("CNC-003", 4, true), BatchSnapshot("CNC-004", 6, true)), planNow.AddSeconds(2.2));
    Check(plan.Observe(Snapshots(
        BatchSnapshot("CNC-001", 0, false), BatchSnapshot("CNC-002", 2, false),
        BatchSnapshot("CNC-003", 4, false), BatchSnapshot("CNC-004", 6, false)), planNow.AddSeconds(4)).Action == "Completed",
        "达到指定轮次后停止自动呼叫");

    var networkSet = typeof(NetworkLab).GetMethod("Set", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("未找到网络联调台点位写入方法");
    void SetNetworkPoint(NetworkLab target, string station, int signal, bool value)
    {
        var mapping = target.Snapshot(station).Single(item => item.Mapping.Signal == signal).Mapping;
        networkSet.Invoke(target, [mapping, value, "同批测试", false]);
    }
    using (var atomic = new NetworkLab(NetworkLabConfig.CreateDefault(), Path.Combine(root, "network-atomic.jsonl")))
    {
        atomic.ManualSet("CNC-001", 1, false);
        Reject(() => atomic.StartBatchCalls(["CNC-001", "CNC-002", "CNC-003", "CNC-004"], 1),
            "同批任一工位不满足条件时整批拒绝");
        Check(new[] { "CNC-001", "CNC-002", "CNC-003", "CNC-004" }
            .All(station => !atomic.Snapshot(station).Single(point => point.Mapping.Signal == 0).Value),
            "同批基线失败时没有部分工位先写呼叫");
    }

    using (var rounds = new NetworkLab(NetworkLabConfig.CreateDefault(), Path.Combine(root, "network-rounds.jsonl")))
    {
        rounds.StartBatchCalls(["CNC-001", "CNC-002", "CNC-003", "CNC-004"], 2);
        Check(new[] { "CNC-001", "CNC-002", "CNC-003", "CNC-004" }
            .All(station => rounds.Snapshot(station).Single(point => point.Mapping.Signal == 0).Value),
            "独立S7联调台在同一个自动扫描中触发四工位");
        Reject(() => rounds.ConfigureCalls(Guid.NewGuid().ToString("N"),
            ["CNC-001", "CNC-002", "CNC-003", "CNC-004"], 1, false),
            "同批运行期间拒绝顺序循环覆盖");
        rounds.TickAutomatic();
        foreach (var station in new[] { "CNC-001", "CNC-002", "CNC-003", "CNC-004" })
        {
            SetNetworkPoint(rounds, station, 0, false);
            SetNetworkPoint(rounds, station, 2, true);
        }
        rounds.TickAutomatic();
        Check(rounds.BatchCallStatus.Contains("等待全员闭环") &&
              !rounds.Snapshot("CNC-001").Single(point => point.Mapping.Signal == 0).Value,
            "任一工位握手残留时不进入下一轮");
        foreach (var station in new[] { "CNC-001", "CNC-002", "CNC-003", "CNC-004" })
            SetNetworkPoint(rounds, station, 2, false);
        rounds.TickAutomatic();
        Check(rounds.BatchCallStatus.Contains("第2/2轮") &&
              new[] { "CNC-001", "CNC-002", "CNC-003", "CNC-004" }
                .All(station => rounds.Snapshot(station).Single(point => point.Mapping.Signal == 0).Value),
            "四工位全部闭环后同一扫描启动下一轮");
        rounds.StopBatchCalls();
        Check(!rounds.BatchCallActive &&
              new[] { "CNC-001", "CNC-002", "CNC-003", "CNC-004" }
                .All(station => rounds.Snapshot(station).Single(point => point.Mapping.Signal == 0).Value),
            "停止后续轮次保留当前P电平");
    }
}
finally { Directory.Delete(root,true); }
Console.WriteLine($"PASS: {checks} checks. 离线28阶段及联调输入、工位映射、H只读、重复呼叫、过期/故障/离线阻断、同批原子呼叫及多轮闭环。");
await NetworkTests.RunAsync();
await RecoveryAndStageTests.RunAsync();
await SessionEvidenceTests.RunAsync();
PresentationTests.Run();
