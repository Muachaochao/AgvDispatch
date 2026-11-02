using System.Net;
using System.Net.Sockets;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

/// <summary>Continuously verifies charger TCP reachability without sending the
/// control-bearing 1001 payload. Protocol state remains available only from a
/// fresh active charging session until the vendor supplies a read-only API.</summary>
public sealed class ChargerMonitoringService(ChargerMonitoringOptions options,
    ILogger<ChargerMonitoringService> logger, TimeProvider? clock = null)
    : BackgroundService, IChargerTelemetryMonitor
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private ChargerTelemetrySnapshot[] snapshots = [];

    public IReadOnlyList<ChargerTelemetrySnapshot> Read() => Volatile.Read(ref snapshots);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            Volatile.Write(ref snapshots, []);
            return;
        }
        if (options.ConfigurationError() is { } error) throw new InvalidOperationException(error);

        var previous = new ChargerTelemetrySnapshot[options.Addresses.Length];
        while (!stoppingToken.IsCancellationRequested)
        {
            var observed = await Task.WhenAll(options.Addresses.Select((address, index) =>
                ProbeAsync(address, previous[index], stoppingToken)));
            Volatile.Write(ref snapshots, observed);
            for (var index = 0; index < observed.Length; index++)
            {
                var row = observed[index];
                if (row.Outcome != "connected" && previous[index]?.Outcome != row.Outcome)
                    logger.LogWarning("充电桩通信检查未通过：{Address}，{Message}", row.Address, row.Message);
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(options.PollIntervalMs), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            previous = observed;
        }
    }

    private async Task<ChargerTelemetrySnapshot> ProbeAsync(string address,
        ChargerTelemetrySnapshot? previous, CancellationToken outer)
    {
        var ack = await CanConnectAsync(address, options.AckPort, outer);
        var status = await CanConnectAsync(address, options.StatusPort, outer);
        var now = clock.GetUtcNow();
        var full = ack.Reachable && status.Reachable;
        var outcome = full ? "connected" : ack.Reachable || status.Reachable ? "degraded" : "offline";
        var message = full
            ? "20207 与 20204 端口均可连接；该检查不发送1001控制报文。"
            : string.Join("；", new[]
            {
                ack.Reachable ? null : "20207不可连接" + (ack.Error is null ? "" : "：" + ack.Error),
                status.Reachable ? null : "20204不可连接" + (status.Error is null ? "" : "：" + status.Error)
            }.Where(value => value is not null));
        return new(address, ack.Reachable, status.Reachable, now, outcome, message,
            full ? now : previous?.LastFullSuccessAt);
    }

    private async Task<(bool Reachable, string? Error)> CanConnectAsync(string address, int port,
        CancellationToken outer)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outer);
        timeout.CancelAfter(options.ConnectTimeoutMs);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Parse(address), port, timeout.Token);
            return (true, null);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            return (false, "连接超时");
        }
        catch (Exception error) when (error is SocketException or IOException or OperationCanceledException)
        {
            return (false, error.Message);
        }
    }
}
