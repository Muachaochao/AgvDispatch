namespace AgvDispatch.Domain.Agvs;

public enum AgvAlarmSeverity
{
    Notice = 0,
    Warning = 10,
    Error = 20,
    Fatal = 30
}

public sealed record AgvAlarmSnapshot(
    string Code,
    AgvAlarmSeverity Severity,
    string? Message);
