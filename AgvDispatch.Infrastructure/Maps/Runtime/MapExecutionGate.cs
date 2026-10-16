using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Infrastructure.Maps;

public sealed class MapExecutionGate(IRecoveryBarrier? recovery = null,IMapTransferRecoveryBarrier? mapTransfers=null) : IMapExecutionGate
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private int _mode = (int)MapExecutionMode.Starting;
    public MapExecutionMode Mode => (MapExecutionMode)Volatile.Read(ref _mode);
    public bool CanStartNewWork => Mode == MapExecutionMode.Running && recovery?.IsOpen != false && mapTransfers?.IsOpen!=false;

    public async ValueTask<IDisposable?> EnterAsync(MapExecutionOperation operation, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        if (recovery?.IsOpen != false && mapTransfers?.IsOpen!=false && (Mode == MapExecutionMode.Running ||
            (Mode == MapExecutionMode.Draining && operation == MapExecutionOperation.ExistingWork)))
            return new Lease(_mutex);
        _mutex.Release();
        return null;
    }

    public async ValueTask<IDisposable> EnterControlAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        return new Lease(_mutex);
    }

    public void SetMode(MapExecutionMode mode) => Volatile.Write(ref _mode, (int)mode);
    private sealed class Lease(SemaphoreSlim mutex) : IDisposable
    {
        private SemaphoreSlim? _mutex = mutex;
        public void Dispose() => Interlocked.Exchange(ref _mutex, null)?.Release();
    }
}
