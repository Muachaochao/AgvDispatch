using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

/// <summary>
/// Keeps unresolved status-7 recoveries current without sending or releasing
/// anything. A vehicle that clears the obstacle or reaches a trusted station
/// may become eligible for the existing operator-confirmed replacement flow.
/// </summary>
public sealed class TransportTimeoutRecoveryMonitor(
    ITransportTimeoutRecoveryService recovery,
    ILogger<TransportTimeoutRecoveryMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await recovery.ReassessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(error,
                    "运输超时恢复候选刷新失败；原任务、载荷、PLC责任和路权保持不变。");
            }
        }
    }
}
