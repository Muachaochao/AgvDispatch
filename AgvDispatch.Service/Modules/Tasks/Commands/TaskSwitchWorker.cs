using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Runtime;

namespace AgvDispatch.Service;

public sealed class TaskSwitchWorker(ITaskSwitchService service, ILogger<TaskSwitchWorker> logger,
    TimeProvider? clock = null) : IsolatedBackgroundService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private TimeSpan BusinessDelay(int milliseconds) => _clock is SimulationTimeProvider simulation
        ? simulation.ScaleForBusiness(TimeSpan.FromMilliseconds(milliseconds))
        : TimeSpan.FromMilliseconds(milliseconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await service.InitializeAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await service.ProcessAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { logger.LogError(error, "受控换单轮询失败；原申请和车辆锁保持。"); }
                await Task.Delay(BusinessDelay(2000), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
