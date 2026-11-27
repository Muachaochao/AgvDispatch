using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Infrastructure.Maps;

public sealed class MapExecutionGate(IRecoveryBarrier? recovery = null,IMapTransferRecoveryBarrier? mapTransfers=null) : IMapExecutionGate
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _readers = new();
    private readonly LinkedList<Waiter> _writers = new();
    private int _activeReaders;
    private bool _writerActive;
    private int _mode = (int)MapExecutionMode.Starting;
    public MapExecutionMode Mode => (MapExecutionMode)Volatile.Read(ref _mode);
    public bool CanStartNewWork => Mode == MapExecutionMode.Running && recovery?.IsOpen != false && mapTransfers?.IsOpen!=false;
    public string? NewWorkBlockReason
    {
        get
        {
            var reasons = new List<string>();
            if (Mode != MapExecutionMode.Running)
                reasons.Add(Mode switch
                {
                    MapExecutionMode.Starting => "后台地图门禁尚未初始化",
                    MapExecutionMode.Draining => "地图切换正在收尾",
                    _ => "地图切换保持暂停，请在地图管理中复核切换记录"
                });
            if (recovery?.IsOpen == false)
                reasons.Add("启动恢复核对尚未放行，请在现场恢复页面核对当前批次；地图切换完成不会解除恢复屏障");
            if (mapTransfers?.IsOpen == false)
                reasons.Add("存在未处置的地图传输记录，须先完成传输结果复核");
            return reasons.Count == 0 ? null : string.Join("；", reasons);
        }
    }

    public async ValueTask<IDisposable?> EnterAsync(MapExecutionOperation operation, CancellationToken cancellationToken)
    {
        if (!Allows(operation)) return null;
        // ExistingWork is a shared execution lease. A slow vendor query may keep a
        // map switch waiting, but it must not serialize dispatch, steps, release and
        // outbox work for every other vehicle. NewWork and control operations take
        // the exclusive side so their local read-modify-write remains atomic.
        var shared = operation == MapExecutionOperation.ExistingWork;
        var waiter = await EnqueueAsync(shared, cancellationToken);
        if (!Allows(operation))
        {
            Release(shared);
            return null;
        }
        return new Lease(this, shared);
    }

    public async ValueTask<IDisposable> EnterControlAsync(CancellationToken cancellationToken)
    {
        await EnqueueAsync(shared: false, cancellationToken);
        return new Lease(this, shared: false);
    }

    public void SetMode(MapExecutionMode mode) => Volatile.Write(ref _mode, (int)mode);

    private bool Allows(MapExecutionOperation operation)
    {
        if (recovery?.IsOpen == false || mapTransfers?.IsOpen == false) return false;
        return Mode == MapExecutionMode.Running ||
            Mode == MapExecutionMode.Draining && operation == MapExecutionOperation.ExistingWork;
    }

    private ValueTask<Waiter> EnqueueAsync(bool shared, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var waiter = new Waiter();
        LinkedListNode<Waiter> node;
        lock (_sync)
        {
            if (!_writerActive && shared && _writers.Count == 0)
            {
                _activeReaders++;
                waiter.Completed = true;
                waiter.Source.SetResult();
                return ValueTask.FromResult(waiter);
            }
            if (!_writerActive && !shared && _activeReaders == 0 && _writers.Count == 0)
            {
                _writerActive = true;
                waiter.Completed = true;
                waiter.Source.SetResult();
                return ValueTask.FromResult(waiter);
            }
            node = (shared ? _readers : _writers).AddLast(waiter)!;
        }
        waiter.Registration = cancellationToken.Register(() => Cancel(waiter, node));
        return WaitAsync(waiter);
    }

    private static async ValueTask<Waiter> WaitAsync(Waiter waiter)
    {
        await waiter.Source.Task.ConfigureAwait(false);
        waiter.Registration.Dispose();
        return waiter;
    }

    private void Cancel(Waiter waiter, LinkedListNode<Waiter> node)
    {
        lock (_sync)
        {
            if (waiter.Completed) return;
            node.List?.Remove(node);
            waiter.Completed = true;
            waiter.Source.TrySetCanceled();
            GrantLocked();
        }
    }

    private void Release(bool shared)
    {
        lock (_sync)
        {
            if (shared)
            {
                if (_activeReaders > 0) _activeReaders--;
            }
            else
            {
                _writerActive = false;
            }
            GrantLocked();
        }
    }

    private void GrantLocked()
    {
        if (_writerActive) return;
        if (_writers.First is { } writer)
        {
            if (_activeReaders != 0) return;
            _writers.RemoveFirst();
            if (writer.Value.Completed) return;
            _writerActive = true;
            writer.Value.Completed = true;
            writer.Value.Source.TrySetResult();
            return;
        }
        // No writer is waiting. Release all readers that queued while the
        // previous exclusive lease was active.
        while (_readers.First is { } reader)
        {
            _readers.RemoveFirst();
            if (reader.Value.Completed) continue;
            _activeReaders++;
            reader.Value.Completed = true;
            reader.Value.Source.TrySetResult();
        }
    }

    private sealed class Waiter
    {
        public readonly TaskCompletionSource Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
        public bool Completed;
    }

    private sealed class Lease(MapExecutionGate gate, bool shared) : IDisposable
    {
        private MapExecutionGate? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release(shared);
    }
}
