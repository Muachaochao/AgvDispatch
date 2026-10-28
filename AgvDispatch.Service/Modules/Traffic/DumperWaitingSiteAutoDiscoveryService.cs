using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

/// <summary>
/// Runs after the active map is available and before task workers start.
/// It only enriches editable configurations; persisted engineering profiles
/// and already bound task checkpoints remain immutable.
/// </summary>
public sealed class DumperWaitingSiteAutoDiscoveryService(
    ILogger<DumperWaitingSiteAutoDiscoveryService> logger,
    IActiveMapRuntime maps,
    TaskParameterRuntime parameters) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (maps.Current is not { } map) return Task.CompletedTask;
        if (parameters.ExpandWaitingSitesFromMap(map.Definition))
            logger.LogInformation(
                "已从活动地图补齐倒料候车位。MapId={MapId}, Version={Version}, WaitingSites={WaitingSites}",
                map.MapId,
                map.VersionId,
                string.Join(',', parameters.Capture().WaitingSites));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
