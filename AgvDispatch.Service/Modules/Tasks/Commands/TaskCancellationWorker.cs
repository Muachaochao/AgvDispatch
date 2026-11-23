using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Runtime;
namespace AgvDispatch.Service;
public sealed class TaskCancellationWorker(ITaskCancellationService service, ILogger<TaskCancellationWorker> logger,
    TimeProvider? clock = null) : IsolatedBackgroundService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private TimeSpan BusinessDelay(int milliseconds) => _clock is SimulationTimeProvider simulation
        ? simulation.ScaleForBusiness(TimeSpan.FromMilliseconds(milliseconds))
        : TimeSpan.FromMilliseconds(milliseconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await service.ProcessAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "任务取消确认轮询失败。"); }
            await Task.Delay(BusinessDelay(3000), stoppingToken);
        }
    }
}
