using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

public sealed class TaskSwitchWorker(ITaskSwitchService service, ILogger<TaskSwitchWorker> logger) : IsolatedBackgroundService
{
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
                await Task.Delay(2000, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
