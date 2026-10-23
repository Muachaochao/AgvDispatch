using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Infrastructure.Maps;

public sealed class MapExecutionGate(IRecoveryBarrier? recovery = null,IMapTransferRecoveryBarrier? mapTransfers=null) : IMapExecutionGate
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
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
