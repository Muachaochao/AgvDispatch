using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Service;
public sealed class TaskCancellationWorker(ITaskCancellationService service, ILogger<TaskCancellationWorker> logger) : IsolatedBackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await service.ProcessAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "任务取消确认轮询失败。"); }
            await Task.Delay(3000, stoppingToken);
        }
    }
}
