using AgvDispatch.Connectors.Plc;

namespace AgvDispatch.Service;

public sealed class PlcFleetPollingService(PlcFleet fleet, PlcSignalCache cache,
    ILogger<PlcPollingService> logger) : IsolatedBackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = fleet.Connections.Select(c => new PlcPollingService(logger, c.Options, c.Gateway,
            cache, c.Supervisor, c.Catalog.Signals.Select(s => s.StationId).ToHashSet(StringComparer.OrdinalIgnoreCase))).ToArray();
        try { await Task.WhenAll(workers.Select(w => w.RunAsync(stoppingToken))); }
        finally { foreach (var worker in workers) worker.Dispose(); }
    }
}
