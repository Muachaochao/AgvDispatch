using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Service;
public sealed class ExecutionRecoveryWorker(IExecutionRecoveryService recovery, ILogger<ExecutionRecoveryWorker> logger, IHostApplicationLifetime lifetime) : IsolatedBackgroundService
{
    public override async Task StartAsync(CancellationToken ct)
    {
        await recovery.InitializeAsync(ct);
        await base.StartAsync(ct);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initialization creates the recovery hold before IPC starts. Periodic inspection
        // must not contend for its control gate while later hosted services initialize.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        try { await started.Task.WaitAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await recovery.InspectAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogError(e, "启动恢复核对失败，保留恢复屏障。"); }
            await Task.Delay(2000, stoppingToken);
        }
    }
}
