using System.Text.Json;
using PlcHandshakeLab;

internal static class PresentationTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
        var now = DateTimeOffset.Now;
        var config = NetworkLabConfig.CreateDefault();
        var points = config.Points.Where(p => p.Station is "CNC-001" or NetworkLabConfig.Shared)
            .Select(p => new PointState(p, p.Initial, now.AddSeconds(-1), "test", "", now, "S7读")).ToArray();
        var initial = JsonSerializer.Serialize(points);
        var rows = LabPresentation.Compare(0, points, true, now);
        Check(rows.Length == 10 && rows.All(r => !r.Name.Contains("故障")), "对照只含10个核心业务点");
        Check(rows.All(r => r.Status == "符合 · 已交换"), "有效新鲜初态符合阶段1");
        Check(LabPresentation.Compare(0, points, false, now).All(r => r.Actual.EndsWith("旧") && r.Status.Contains("待核对")), "断连不得把保留内存标为有效");
        var expired = points.Select(p => p with { HostAccessedAt = now.AddSeconds(-4) }).ToArray();
        Check(LabPresentation.Compare(0, expired, true, now).All(r => r.Actual.EndsWith("旧")), "总连接新鲜不能掩盖单点过期");
        var changed = points.Select(p => p with { UpdatedAt = now.AddMilliseconds(1) }).ToArray();
        Check(LabPresentation.Compare(0, changed, true, now).All(r => r.Actual.EndsWith("旧")), "变化后未重新交换不能当作有效反馈");
        Check(LabPresentation.Compare(0, null, false, now).All(r => r.Actual == "—" && r.Status == "尚无会话数据"), "未启动不伪造实际值");
        Check(LabPresentation.Compare(0, [], true, now).All(r => r.Actual == "—"), "缺失点位不假设为0");
        Check(LabPresentation.Compare(21, points, true, now)[1].Expected == "0/1", "阶段22保留双值条件");
        Check(LabPresentation.Compare(1, points, true, now)[0].Status.StartsWith("差异"), "实际与期望差异明确");
        Check(initial == JsonSerializer.Serialize(points), "所有查看保持原快照不变");
        NetworkEvent Event(string kind, string detail = "", string point = "", string after = "") => new(now, kind, "CNC-001", 5, point, "", after, detail);
        Check(LabPresentation.MatchesEvent(Event("S7 127.0.0.1:1234"), 2) && !LabPresentation.MatchesEvent(Event("S7 127.0.0.1:1234"), 1), "实际S7来源归入通信");
        Check(LabPresentation.MatchesEvent(Event("拒绝连接"), 2) && LabPresentation.MatchesEvent(Event("拒绝连接"), 3), "连接异常同时可从通信和异常筛选找到");
        Check(LabPresentation.MatchesEvent(Event("人工置位", point: "工位故障", after: "1"), 3) && !LabPresentation.MatchesEvent(Event("人工置位", point: "工位故障", after: "0"), 3), "置故障归异常，撤故障保持业务记录");
        Check(LabPresentation.MatchesEvent(Event("人工置位", point: "下游在线", after: "0"), 3), "下游离线可筛出");
        var result = new StageTestResult(now, "CNC-001", 1, 5, "失败", "V1", "T1", "现场异常", "", "");
        Check(LabPresentation.MatchesEvent(Event("阶段测试", "会话+1s；" + JsonSerializer.Serialize(result)), 3), "转义JSON中的失败结果可筛出");
        Check(!LabPresentation.MatchesEvent(Event("阶段测试", "损坏JSON"), 3) && LabPresentation.MatchesEvent(Event("阶段测试", "损坏JSON"), 0), "异常解析不抛错，原事件在全部中保留");
        Check(LabPresentation.MatchesEvent(Event("阶段标记"), 1) && !LabPresentation.MatchesEvent(Event("阶段标记"), 2), "业务标记不混入通信筛选");
        Console.WriteLine($"PASS: {checks} presentation checks. 点位对照、新鲜度、阶段22双值、只读投影、日志业务/通信/异常分类。");
    }
}
