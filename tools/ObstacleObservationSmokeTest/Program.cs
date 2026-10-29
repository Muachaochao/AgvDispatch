using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Service;

var checks = 0;
void Check(bool valid, string message) { if (!valid) throw new Exception(message); checks++; }
var now = DateTimeOffset.UtcNow;
var clear = new AgvRobotSnapshot("AGV-01", "一号车", "synthetic", 0, ConnectionState.Online,
    SignalQuality.Good, 0, 0, 0, 1, "LM1", null, 80, false, false, "Idle", null, null, null,
    [], [], now, now, 0, 1, null, 1);
var freshFor = TimeSpan.FromSeconds(5);
var blocked = clear with { Blocked = true, ObstacleSince = now.AddSeconds(-2) };
var result = AgvObstacleDiagnostics.Analyze(blocked, now, freshFor);
Check(result.State == "Blocked" && result.DurationSeconds == 2, "短暂阻挡显示等待及后台观察时长");
Check(result.Source == "Blocked" && result.NavigationPolicy.Contains("保留原任务") && result.LocalDetourEvidence.Contains("未验证"), "显示原任务策略及自主绕行证据边界");
var prolonged = blocked with { ObstacleSince = now.AddSeconds(-30) };
Check(AgvObstacleDiagnostics.Analyze(prolonged, now, freshFor).State == "Prolonged", "30秒持续占道进入长期受阻提示");
Check(AgvObstacleDiagnostics.Analyze(prolonged with { LastSuccessfulPollAt = now.AddSeconds(-6) }, now, freshFor) is
    { State: "Unknown", DurationSeconds: null }, "数据失效不继续累计或宣称恢复");
Check(AgvObstacleDiagnostics.Analyze(prolonged with { Connection = ConnectionState.Interrupted }, now, freshFor).State == "Unknown", "断连拒绝当前受阻结论");
Check(AgvObstacleDiagnostics.Analyze(prolonged with { EmergencyStopped = true }, now, freshFor).State == "SafetyStop", "急停优先于持续受阻");
Check(AgvObstacleDiagnostics.Analyze(clear with { Alarms = [new("52200", AgvAlarmSeverity.Fatal, "blocked")] }, now, freshFor).State == "SafetyStop", "52200 Fatal不能降为普通受阻");
Check(AgvObstacleDiagnostics.Analyze(blocked with { Alarms = [new("52100", AgvAlarmSeverity.Error, "motor blocked")] }, now, freshFor).State == "SafetyStop", "普通受阻叠加严重错误仍停止相关流程");
var alarmOnly = clear with { Alarms = [new("52200", AgvAlarmSeverity.Error, "blocked")], ObstacleSince = now.AddSeconds(-31) };
Check(AgvObstacleDiagnostics.Analyze(alarmOnly, now, freshFor) is { State: "Prolonged", Source: "52200", DurationSeconds: 31 }, "仅52200也能显示持续受阻");
Check(AgvObstacleDiagnostics.Analyze(clear with { Blocked = null }, now, freshFor).State == "Unknown", "阻挡字段未知不能显示已恢复");
Check(AgvObstacleDiagnostics.Analyze(blocked with { EmergencyStopped = null }, now, freshFor).State == "Unknown", "安全字段缺失不能显示正常受阻等待");
Check(AgvObstacleDiagnostics.Analyze(clear with { LastObstacleClearedAt = now }, now, freshFor) is
    { State: "Clear", LastClearedAt: not null, DurationSeconds: null } restored && restored.Message.Contains("后续车端任务反馈"), "解除受阻不代表任务完成");
Check(AgvObstacleDiagnostics.Analyze(clear with { LastSuccessfulPollAt = now.AddSeconds(2) }, now, freshFor).State == "Unknown", "未来采样不能证明当前恢复");
Console.WriteLine($"PASS: obstacle observation {checks} checks; synthetic snapshots only, no device connections.");
