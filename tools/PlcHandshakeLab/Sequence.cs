namespace PlcHandshakeLab;

public sealed record Signal(string Name, string Owner, string Address, string Meaning);
public sealed record Stage(string Name, string Condition, string Pattern);

/// <summary>用户于2026-10-20提供的28步表；?表示两种电平均符合该阶段。</summary>
public static class Sequence
{
    public static readonly Signal[] Signals =
    [
        new("满箱呼叫", "P", "0.0", "PLC锁存本轮满料需求"),
        new("箱体检测", "P", "0.1", "物理箱体传感器"),
        new("呼叫响应", "H", "0.4", "上位机受理本轮任务"),
        new("满箱拉走通知", "H", "0.5", "本轮取箱结果已核实"),
        new("空箱任务响应", "H", "0.6", "保持本轮返箱责任"),
        new("空箱放到位通知", "H", "0.7", "本轮放箱动作已核实"),
        new("等待位申请", "H", "610.0", "到等待点且取得调度侧独占"),
        new("倒料周期通知", "H", "610.1", "保持到实际退出区域"),
        new("Ready入站许可", "P", "610.2", "本轮专属许可，撤位不撤设备保留"),
        new("Done允许离开", "P", "610.3", "本轮倒料完成且允许离开")
    ];

    public static readonly Stage[] Stages =
    [
        new("正常接料", "工位有箱，无未闭合旧任务。", "0100000000"),
        new("排屑机满料呼叫", "PLC锁存本轮满料需求。", "1100000000"),
        new("调度受理任务", "上位机去重、持久化任务后应答。", "1110000000"),
        new("PLC撤销呼叫", "PLC看到响应，清呼叫，内部保持本轮忙碌。", "0110000000"),
        new("AGV前往取箱", "第一段运输，等待实际到站及取箱。", "0110000000"),
        new("取箱结果核实", "AGV取箱动作完成，且传感器确认无箱。", "0010000000"),
        new("取箱后更新信号", "清呼叫响应，置取箱通知及返箱责任。", "0001100000"),
        new("前往倒料等待点", "第二段运输，返箱责任保持。", "0001100000"),
        new("实际到等待点", "上位机核实到站，并取得调度侧设备独占。", "0001101000"),
        new("PLC授予入站许可", "PLC核实联锁，锁存本轮设备保留。", "0001101010"),
        new("上位机消费许可", "持久化许可消费记录后，撤等待申请。", "0001100010"),
        new("PLC结束许可握手", "清Ready许可位；PLC内部设备保留继续。", "0001100000"),
        new("AGV前往倒料点", "第三段运输，许可记录、路线及路权有效。", "0001100000"),
        new("实际到倒料点", "核实本车到位及Done旧低基线。", "0001100100"),
        new("倒料执行中", "PLC执行本轮倒料。", "0001100100"),
        new("倒料完成，允许离开", "PLC确认本轮倒料完成及离开条件。", "0001100101"),
        new("车辆尚未退出", "保持倒料周期及必要占用。", "0001100101"),
        new("发出返回运输", "第四段运输开始；发指令不等于实际离站。", "0001100101"),
        new("核实实际退出区域", "上位机依据有效位置及约定区域判据清周期通知。", "0001100001"),
        new("PLC处理退出闭合", "PLC处理本轮正常退出，撤Done，按条件结束内部保留。", "0001100000"),
        new("AGV返回原工位", "返箱责任仍保持，核实返回正确工位。", "0001100000"),
        new("完成空箱放置动作", "核实本轮放箱动作结果，置放到位通知；箱体检测允许0或1。", "0?01110000"),
        new("确认实际有箱", "动作结果和传感器有箱均成立，先后顺序不限。", "0101110000"),
        new("核实本轮全部闭合", "倒料退出、返箱及其他必要条件均满足。", "0101110000"),
        new("撤销取箱通知", "上位机清本轮满箱拉走保持位。", "0100110000"),
        new("撤销放箱通知", "上位机清本轮空箱放到位保持位。", "0100100000"),
        new("结束返箱责任", "上位机最后清空箱任务响应。", "0100000000"),
        new("本轮结束", "PLC核实闭合，允许按新满料需求再次呼叫。", "0100000000")
    ];

    public static bool Matches(int stage, int signal, bool value) =>
        Stages[stage].Pattern[signal] == '?' || (Stages[stage].Pattern[signal] == '1') == value;

    public static bool[] Apply(int stage, IReadOnlyList<bool> previous) =>
        Stages[stage].Pattern.Select((c, i) => c == '?' ? previous[i] : c == '1').ToArray();

    public static string Edge(int stage, int signal)
    {
        var now = Stages[stage].Pattern[signal];
        if (now == '?') return "0/1";
        if (stage == 0) return now.ToString();
        var before = Stages[stage - 1].Pattern[signal];
        return before == now || before == '?' ? now.ToString() : now == '1' ? "↑" : "↓";
    }
}
