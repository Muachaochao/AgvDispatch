using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence;
namespace AgvDispatch.Service;

public sealed class ExecutionIdentityPollingService(ExecutionIdentityAcquisition acquisition,
    ExecutionIdentityAcquisitionOptions options, ILogger<ExecutionIdentityPollingService> logger) : IsolatedBackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) { logger.LogInformation("身份只读采集未启用；现场字段映射尚未接入。"); return; }
        var states = new Dictionary<(string, string), string>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var results = await acquisition.PollOnceAsync(stoppingToken);
                foreach (var result in results)
                {
                    var key = (result.SourceKind, result.SourceId);
                    if (!states.TryGetValue(key, out var old) || old != result.State)
                        logger.LogInformation("身份采集 {Kind}/{Id}：{State}，{Message}", result.SourceKind, result.SourceId, result.State, result.Message);
                    states[key] = result.State;
                }
                await Task.Delay(options.PollIntervalMs, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { acquisition.MarkStopped(); }
    }
}
