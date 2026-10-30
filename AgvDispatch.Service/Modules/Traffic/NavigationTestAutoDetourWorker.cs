using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;

namespace AgvDispatch.Service;

/// <summary>
/// Polls the explicitly selected single navigation test. The service itself
/// enforces the blocked hold, map identity, task terminal, stop, and one-shot
/// send gates; this worker only supplies a steady query cadence while the
/// browser is closed.
/// </summary>
public sealed class NavigationTestAutoDetourWorker(
    SingleNavigationTestOptions options,
    ISingleNavigationTestService navigation,
    ILogger<NavigationTestAutoDetourWorker> logger) : IsolatedBackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !options.AutoDetourEnabled) return;
        logger.LogInformation(
            "单车自动改道已启用。VehicleId={VehicleId}, HoldSeconds={HoldSeconds}",
            options.VehicleId,
            Math.Max(1, Math.Max(options.AutoDetourWaitSeconds, options.DetourWaitSeconds)));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await navigation.ReadAsync(stoppingToken);
                await navigation.AdvanceAutoDetourAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "自动改道查询或安全推进失败；保留原任务，不重发取消或导航。");
            }
        }
    }
}
