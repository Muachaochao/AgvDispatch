using AgvDispatch.Application.Abstractions;
using AgvDispatch.Service;

var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception("FAILED: " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}

var now = DateTimeOffset.UtcNow;
TrafficRequestSnapshot Request(string step, TrafficWaitingAlertLevel level, double seconds) => new(
    step, "T-" + step, "V-" + step, "A", "B", [], ["R-" + step], "Waiting", "等待资源", [], now, now)
{
    WaitingSeconds = seconds,
    WaitingAlertLevel = level,
    Blockers = [new TrafficBlocker("R-BLOCK", "T-OTHER", "V-OTHER", "S-OTHER", false)]
};

var cycle = new TrafficCycleRisk("WAIT-CYCLE-TEST", ["T-A", "T-B"], ["V-A", "V-B"], ["R-A", "R-B"], [],
    "观察到两个任务互等。");
var page = new TrafficWaitingPage(now, 4, 4, 1, 200,
    [
        Request("NORMAL", TrafficWaitingAlertLevel.Normal, 5),
        Request("WARN", TrafficWaitingAlertLevel.Warning, 90),
        Request("CRIT", TrafficWaitingAlertLevel.Critical, 400),
        Request("UNKNOWN", TrafficWaitingAlertLevel.Unknown, 0)
    ],
    ["R-BLOCK"]) { CycleRisks = [cycle] };

var alarms = AlarmRuleEngine.BuildWaitCandidates(page);
var cycleAlarm = alarms.SingleOrDefault(a => a.RuleKey == "TRAFFIC_WAIT_CYCLE:WAIT-CYCLE-TEST");
Check(cycleAlarm is not null && cycleAlarm.Code == "TRAFFIC_WAIT_CYCLE" && cycleAlarm.Severity == AlarmSeverity.Critical,
    "闭合等待环产生 TRAFFIC_WAIT_CYCLE 严重报警");
Check(cycleAlarm is not null && cycleAlarm.Message.Contains("V-A") && cycleAlarm.Message.Contains("R-B"),
    "成环报警说明涉及的车辆与资源");
var warn = alarms.SingleOrDefault(a => a.RuleKey == "TRAFFIC_WAIT_TIMEOUT:WARN");
Check(warn is not null && warn.Code == "TRAFFIC_WAIT_TIMEOUT" && warn.Severity == AlarmSeverity.Warning,
    "超过警告时限的路权等待产生 TRAFFIC_WAIT_TIMEOUT 警告");
var crit = alarms.SingleOrDefault(a => a.RuleKey == "TRAFFIC_WAIT_TIMEOUT:CRIT");
Check(crit is not null && crit.Code == "TRAFFIC_WAIT_TIMEOUT" && crit.Severity == AlarmSeverity.Error,
    "超过严重时限的路权等待产生 TRAFFIC_WAIT_TIMEOUT 错误报警");
Check(!alarms.Any(a => a.RuleKey.EndsWith(":NORMAL") || a.RuleKey.EndsWith(":UNKNOWN")),
    "正常及未知时钟的等待不产生超时报警");
Check(alarms.Count == 3, "同一快照只产生一组确定的等待报警");
Console.WriteLine($"Traffic wait alarms: {checks} checks passed.");
