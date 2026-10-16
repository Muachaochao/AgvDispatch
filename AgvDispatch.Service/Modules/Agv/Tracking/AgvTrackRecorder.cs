using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;

namespace AgvDispatch.Service;

public sealed class AgvTrackRecorder(
    ILogger<AgvTrackRecorder> logger,
    MapOptions options,
    IAgvStateCache agvStates,
    IActiveMapRuntime activeMap,
    IAgvTrackStore tracks,
    IDatabaseInitializer database) : IsolatedBackgroundService
{
    private readonly Dictionary<string, PersistedPosition> _last =
        new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await database.InitializeAsync(stoppingToken);
        var removed = await tracks.DeleteOlderThanAsync(
            DateTimeOffset.Now.AddDays(-options.TrackRetentionDays), stoppingToken);
        logger.LogInformation(
            "AGV轨迹记录启动。采样={Interval}ms，最小位移={Distance}m，保留={Days}天，启动清理={Removed}条",
            options.TrackSamplingIntervalMs,
            options.TrackMinimumDistanceMeters,
            options.TrackRetentionDays,
            removed);
        var nextCleanup = DateTimeOffset.Now.AddDays(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecordCycleAsync(stoppingToken);
                if (DateTimeOffset.Now >= nextCleanup)
                {
                    await tracks.DeleteOlderThanAsync(
                        DateTimeOffset.Now.AddDays(-options.TrackRetentionDays), stoppingToken);
                    nextCleanup = DateTimeOffset.Now.AddDays(1);
                }
                await Task.Delay(options.TrackSamplingIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "AGV轨迹记录周期失败。下个周期继续重试。");
                await Task.Delay(options.TrackSamplingIntervalMs, stoppingToken);
            }
        }
    }

    private async Task RecordCycleAsync(CancellationToken cancellationToken)
    {
        var runtime = activeMap.Current;
        if (runtime is null) return;
        var now = DateTimeOffset.Now;
        foreach (var agv in agvStates.GetAll())
        {
            if (agv.X is null || agv.Y is null || agv.LastSuccessfulPollAt is null ||
                !double.IsFinite(agv.X.Value) || !double.IsFinite(agv.Y.Value))
                continue;
            if (_last.TryGetValue(agv.VehicleId, out var previous) &&
                previous.SourcePollAt >= agv.LastSuccessfulPollAt.Value)
                continue;
            if (previous is not null)
            {
                var moved = Math.Sqrt(Math.Pow(agv.X.Value - previous.X, 2) + Math.Pow(agv.Y.Value - previous.Y, 2));
                if (moved < options.TrackMinimumDistanceMeters &&
                    now - previous.PersistedAt < TimeSpan.FromSeconds(options.TrackStationaryHeartbeatSeconds))
                    continue;
            }
            var position = AgvMapPositionAnalyzer.Analyze(
                agv.X, agv.Y, agv.LocalizationConfidence, agv.LastSuccessfulPollAt,
                runtime.Definition, now, TimeSpan.FromSeconds(options.PositionStaleSeconds),
                options.MinimumLocalizationConfidence, options.MaximumRouteDeviationMeters);
            await tracks.AppendAsync(new AgvTrackPointSnapshot(
                0, agv.VehicleId, runtime.MapId, runtime.VersionId,
                agv.X.Value, agv.Y.Value, agv.Angle, agv.LocalizationConfidence,
                position.State, position.DistanceToRouteMeters, position.NearestEdgeId,
                agv.LastSuccessfulPollAt.Value), cancellationToken);
            _last[agv.VehicleId] = new PersistedPosition(
                agv.X.Value, agv.Y.Value, now, agv.LastSuccessfulPollAt.Value);
        }
    }

    private sealed record PersistedPosition(
        double X,
        double Y,
        DateTimeOffset PersistedAt,
        DateTimeOffset SourcePollAt);
}
