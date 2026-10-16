namespace AgvDispatch.Domain.Tasks;

public enum AgvCommandStatus
{
    Pending = 0,
    Sending = 10,
    RetryScheduled = 20,
    Sent = 30,
    Failed = 90,
    Cancelled = 100
}
