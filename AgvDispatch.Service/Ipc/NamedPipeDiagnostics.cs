using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

public sealed class NamedPipeDiagnostics
{
    private readonly object _sync = new();
    private int _activeConnections;
    private long _totalRequests;
    private long _failedRequests;
    private string? _lastClientIdentity;
    private string? _lastOperation;
    private DateTimeOffset? _lastRequestAt;

    public void ConnectionOpened() => Interlocked.Increment(ref _activeConnections);
    public void ConnectionClosed() => Interlocked.Decrement(ref _activeConnections);

    public void RecordRequest(string identity, string? operation, bool success)
    {
        Interlocked.Increment(ref _totalRequests);
        if (!success)
        {
            Interlocked.Increment(ref _failedRequests);
        }
        lock (_sync)
        {
            _lastClientIdentity = identity;
            _lastOperation = operation;
            _lastRequestAt = DateTimeOffset.Now;
        }
    }

    public NamedPipeDiagnostic GetSnapshot(
        string pipeName,
        TrafficUserContext currentUser)
    {
        string? identity;
        string? operation;
        DateTimeOffset? requestedAt;
        lock (_sync)
        {
            identity = _lastClientIdentity;
            operation = _lastOperation;
            requestedAt = _lastRequestAt;
        }
        return new NamedPipeDiagnostic(
            pipeName,
            Volatile.Read(ref _activeConnections),
            Interlocked.Read(ref _totalRequests),
            Interlocked.Read(ref _failedRequests),
            currentUser.WindowsIdentity,
            currentUser.DisplayName,
            currentUser.Permissions,
            identity,
            operation,
            requestedAt);
    }
}
