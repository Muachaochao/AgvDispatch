using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Service;
public sealed class ExecutionRecoveryWorker(IExecutionRecoveryService recovery, ILogger<ExecutionRecoveryWorker> logger) : IsolatedBackgroundService
{
    public override async Task StartAsync(CancellationToken ct)
    {
        await recovery.InitializeAsync(ct);
        await base.StartAsync(ct);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await recovery.InspectAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogError(e, "启动恢复核对失败，保留恢复屏障。"); }
            await Task.Delay(2000, stoppingToken);
        }
    }
}
