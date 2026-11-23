namespace AgvDispatch.Service;

/// <summary>Coalesces committed resource changes into one dispatch-cycle wakeup.</summary>
public sealed class DispatchTrafficWakeup : IDisposable
{
    private readonly SemaphoreSlim _pending = new(0, 1);
    private readonly SemaphoreSlim _followup = new(0, 1);
    private readonly object _publishGate = new();

    public void Publish()
    {
        lock (_publishGate)
        {
            if (_pending.CurrentCount == 0) _pending.Release();
            if (_followup.CurrentCount == 0) _followup.Release();
        }
    }

    public Task<bool> WaitAsync(int fallbackIntervalMs, CancellationToken ct) =>
        _pending.WaitAsync(fallbackIntervalMs, ct);

    public Task<bool> WaitAsync(TimeSpan fallbackInterval, CancellationToken ct) =>
        _pending.WaitAsync(fallbackInterval, ct);

    public Task<bool> WaitForFollowupAsync(int fallbackIntervalMs, CancellationToken ct) =>
        _followup.WaitAsync(fallbackIntervalMs, ct);

    public void Dispose()
    {
        _pending.Dispose();
        _followup.Dispose();
    }
}
