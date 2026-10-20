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
Console.WriteLine($"PASS: {checks} checks. 28阶段、关键保持位、可选传感器先后顺序、偏差阻断、循环、日志导出。");
