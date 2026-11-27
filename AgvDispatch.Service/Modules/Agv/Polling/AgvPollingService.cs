using AgvDispatch.Connectors.Agv;
using AgvDispatch.Application.Runtime;

namespace AgvDispatch.Service;

public sealed class AgvPollingService(
    ILogger<AgvPollingService> logger,
    AgvOptions options,
    AgvConnectionManager connections,
    AgvStateCache stateCache, TimeProvider? clock = null) : IsolatedBackgroundService
{
    private readonly TimeProvider _clock = clock ?? BusinessClock.Provider;
    private TimeSpan BusinessDelay(int milliseconds) => _clock is SimulationTimeProvider simulation
        ? simulation.ScaleForBusiness(TimeSpan.FromMilliseconds(milliseconds))
        : TimeSpan.FromMilliseconds(milliseconds);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation(
                "AGV采集服务未启用。已配置机器人={RobotCount}",
                options.Robots.Count);
            return;
        }

        var error = options.GetConfigurationError();
        if (error is not null)
        {
            logger.LogError("AGV采集服务配置错误：{Error}", error);
            return;
        }

        logger.LogInformation(
            "AGV采集服务启动。机器人={RobotCount}，周期={PollIntervalMs}ms，协议版本={ProtocolVersion}",
            connections.Clients.Count,
            options.PollIntervalMs,
            options.ProtocolVersion);

        var workers = connections.AllClients
            .Select(client => PollRobotAsync(client, stoppingToken))
            .ToArray();
        await Task.WhenAll(workers);
    }

    private async Task PollRobotAsync(
        SeerRobotClient client,
        CancellationToken cancellationToken)
    {
        var vehicleId = client.Robot.VehicleId;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!client.CommunicationEnabled)
            {
                await Task.Delay(BusinessDelay(250), cancellationToken);
                continue;
            }
            try
            {
                if (!client.IsConnected)
                {
                    stateCache.MarkConnecting(vehicleId);
                }

                var (telemetry, responseTime) = await client.PollAsync(cancellationToken);
                var wasOnline = stateCache.TryGet(vehicleId, out var previous) &&
                    previous.Connection == Domain.Devices.ConnectionState.Online;
                if (client.CommunicationEnabled) stateCache.ApplyTelemetry(vehicleId, telemetry, responseTime);
                if (!wasOnline)
                {
                    logger.LogInformation(
                        "AGV采集恢复正常。VehicleId={VehicleId}, ResponseMs={ResponseMs:F1}",
                        vehicleId,
                        responseTime.TotalMilliseconds);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (!client.CommunicationEnabled) continue;
                // A transport failure resets only its own channel inside SeerTcpChannel.
                // Do not interrupt an independent navigation request when status polling fails.
                stateCache.MarkFailure(
                    vehicleId,
                    exception,
                    TimeSpan.FromMilliseconds(options.StaleAfterMs));
                if (stateCache.TryGet(vehicleId, out var failed) &&
                    (failed.ConsecutiveFailures == 1 || failed.ConsecutiveFailures == 3))
                {
                    logger.LogWarning(
                        exception,
                        "AGV采集失败。VehicleId={VehicleId}, State={State}, ConsecutiveFailures={Failures}",
                        vehicleId,
                        failed.Connection,
                        failed.ConsecutiveFailures);
                }
            }

            var delay = stateCache.TryGet(vehicleId, out var current) &&
                current.Connection == Domain.Devices.ConnectionState.Interrupted
                    ? options.ReconnectDelayMs
                    : options.PollIntervalMs;
            try
            {
                await Task.Delay(BusinessDelay(delay), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
