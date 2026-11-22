using System.Diagnostics;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Application.Runtime;

namespace AgvDispatch.Service;

public sealed class PlcPollingService(
    ILogger<PlcPollingService> logger,
    PlcOptions options,
    IPlcBatchReader batchReader,
    PlcSignalCache signalCache,
    PlcConnectionSupervisor connectionSupervisor,
    IReadOnlySet<string>? stationIds = null, TimeProvider? clock = null) : IsolatedBackgroundService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public Task RunAsync(CancellationToken token) => ExecuteAsync(token);
    private void MarkSignals(SignalQuality quality, string? message) =>
        signalCache.MarkEnabledSignals(quality, message, stationIds);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            MarkSignals(SignalQuality.Disabled, "PLC通信未启用。");
            connectionSupervisor.ReportStopped("PLC通信未启用。");
            logger.LogInformation(
                "PLC采集服务未启用。DeviceId={DeviceId}，已加载点位={SignalCount}，已确认点位={EnabledCount}",
                options.DeviceId,
                signalCache.GetAll().Count(signal => stationIds is null || stationIds.Contains(signal.StationId)),
                signalCache.GetAll().Count(signal => signal.Enabled && (stationIds is null || stationIds.Contains(signal.StationId))));
            return;
        }

        if (connectionSupervisor.Current.State == ConnectionState.Faulted)
        {
            MarkSignals(
                SignalQuality.Bad,
                connectionSupervisor.Current.Message ?? "PLC配置异常。");
            logger.LogError("PLC采集服务无法启动。DeviceId={DeviceId}：{Message}", options.DeviceId, connectionSupervisor.Current.Message);
            return;
        }

        logger.LogInformation(
            "PLC采集服务启动。DeviceId={DeviceId}，周期={PollIntervalMs}ms，块大小={BlockSize}字节，中断阈值={FailureThreshold}",
            options.DeviceId,
            options.PollIntervalMs,
            options.ReadBlockSizeBytes,
            options.InterruptedFailureThreshold);

        var stopwatch = new Stopwatch();
        while (!stoppingToken.IsCancellationRequested)
        {
            stopwatch.Restart();
            try
            {
                var readings = await batchReader.ReadEnabledSignalsAsync(stoppingToken);
                stopwatch.Stop();

                var wasOnline = connectionSupervisor.Current.State == ConnectionState.Online;
                signalCache.ApplyReadings(readings);
                connectionSupervisor.ReportSuccess(stopwatch.Elapsed);

                if (!wasOnline)
                {
                    logger.LogInformation(
                        "PLC采集恢复正常。DeviceId={DeviceId}，读取点位={ReadingCount}，耗时={ElapsedMs:F1}ms",
                        options.DeviceId,
                        readings.Count,
                        stopwatch.Elapsed.TotalMilliseconds);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                connectionSupervisor.ReportFailure(exception, stopwatch.Elapsed);
                var current = connectionSupervisor.Current;
                UpdateSignalQualityAfterFailure(current, exception.Message);

                if (current.ConsecutiveFailures == 1 ||
                    current.ConsecutiveFailures == options.InterruptedFailureThreshold)
                {
                    logger.LogWarning(
                        exception,
                        "PLC采集失败。DeviceId={DeviceId}，状态={State}，连续失败={FailureCount}，耗时={ElapsedMs:F1}ms",
                        options.DeviceId,
                        current.State,
                        current.ConsecutiveFailures,
                        stopwatch.Elapsed.TotalMilliseconds);
                }
            }

            var delay = connectionSupervisor.Current.State == ConnectionState.Interrupted
                ? options.ReconnectDelayMs
                : options.PollIntervalMs;
            try
            {
                await Task.Delay(_clock is SimulationTimeProvider simulation
                    ? simulation.ScaleForBusiness(TimeSpan.FromMilliseconds(delay))
                    : TimeSpan.FromMilliseconds(delay), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        var lastState = connectionSupervisor.Current.State;
        if (lastState is ConnectionState.Online or ConnectionState.Degraded)
        {
            MarkSignals(SignalQuality.Stale, "PLC采集服务已停止。");
        }

        connectionSupervisor.ReportStopped("PLC采集服务已停止。");
        logger.LogInformation("PLC采集服务已停止。DeviceId={DeviceId}", options.DeviceId);
    }

    private void UpdateSignalQualityAfterFailure(
        PlcConnectionSnapshot connection,
        string errorMessage)
    {
        if (connection.LastSuccessfulReadAt is null)
        {
            MarkSignals(SignalQuality.Bad, errorMessage);
            return;
        }

        var age = _clock.GetLocalNow() - connection.LastSuccessfulReadAt.Value;
        if (age >= TimeSpan.FromMilliseconds(options.StaleAfterMs))
        {
            MarkSignals(SignalQuality.Stale, errorMessage);
        }
    }
}
