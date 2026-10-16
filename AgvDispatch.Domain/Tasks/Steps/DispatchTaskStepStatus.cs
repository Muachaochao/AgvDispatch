namespace AgvDispatch.Domain.Tasks;

public enum DispatchTaskStepStatus
{
    Pending = 0,
    Ready = 10,
    CommandPending = 20,
    Sending = 30,
    Executing = 40,
    Completed = 50,
    RetryScheduled = 60,
    Failed = 90,
    Cancelled = 100,
    ManualIntervention = 110
}
