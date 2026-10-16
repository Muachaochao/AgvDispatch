using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Stations;

namespace AgvDispatch.Connectors.Plc;

public sealed record PlcRuntimeConnection(PlcOptions Options, PlcSignalCatalog Catalog,
    PlcConnectionSupervisor Supervisor, HslPlcGateway Gateway);

// Station IDs are global business identities. A station must belong to exactly one PLC.
public sealed class PlcFleet : IPlcGateway, IPlcConnectionSupervisor, IPlcConfigurationInspector, IDisposable
{
    private readonly Dictionary<string, PlcRuntimeConnection> _stations = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<PlcRuntimeConnection> Connections { get; }
    public PlcSignalCatalog Catalog { get; }
    public bool Enabled => Connections.Any(c => c.Options.Enabled);
    public event Action<PlcConnectionChanged>? ConnectionChanged;

    public PlcFleet(IReadOnlyList<PlcOptions> options)
    {
        if (options.Count == 0) throw new InvalidOperationException("至少保留一个PLC配置（可以禁用）。");
        if (options.Select(o => o.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count)
            throw new InvalidOperationException("PLC设备编号重复。");
        // Validate all ownership before constructing gateways or opening any connection.
        var catalogs = options.Select(o => string.IsNullOrWhiteSpace(o.SignalCsvPath)
            ? PlcSignalCatalog.FromSignals([]) : PlcSignalCatalog.Load(o.SignalCsvPath)).ToArray();
        var ownerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var catalog in catalogs)
            foreach (var station in catalog.Signals.Select(s => s.StationId).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!ownerIds.Add(station)) throw new InvalidOperationException($"工位{station}被多个PLC点表重复绑定。");
        Catalog = PlcSignalCatalog.FromSignals(catalogs.SelectMany(c => c.Signals));
        var nodes = new List<PlcRuntimeConnection>();
        try
        {
            for (var i = 0; i < options.Count; i++)
            {
                var supervisor = new PlcConnectionSupervisor(options[i]);
                var gateway = new HslPlcGateway(options[i], catalogs[i], supervisor);
                var node = new PlcRuntimeConnection(options[i], catalogs[i], supervisor, gateway);
                nodes.Add(node);
                supervisor.ConnectionChanged += change => ConnectionChanged?.Invoke(change);
                foreach (var station in catalogs[i].Signals.Select(s => s.StationId).Distinct(StringComparer.OrdinalIgnoreCase))
                    _stations.Add(station, node);
            }
        }
        catch { foreach (var node in nodes) node.Gateway.Dispose(); throw; }
        Connections = nodes;
    }

    // Backwards compatible aggregate; detailed diagnostics expose each device separately.
    public PlcConnectionSnapshot Current
    {
        get
        {
            var enabled = Connections.Where(c => c.Options.Enabled).Select(c => c.Supervisor.Current).ToArray();
            if (enabled.Length == 0) return Connections[0].Supervisor.Current;
            var worst = enabled.OrderByDescending(c => c.State == ConnectionState.Faulted ? 5 :
                c.State == ConnectionState.Interrupted ? 4 : c.State == ConnectionState.Disconnected ? 3 :
                c.State == ConnectionState.Connecting ? 2 : c.State == ConnectionState.Degraded ? 1 : 0).First();
            return worst with { DisplayName = $"PLC连接（{enabled.Count(c => c.State == ConnectionState.Online)}/{enabled.Length}正常）" };
        }
    }
    private PlcRuntimeConnection Resolve(string stationId) => _stations.TryGetValue(stationId, out var node)
        ? node : throw new InvalidOperationException($"工位{stationId}没有绑定PLC。");
    public string? GetSignalConfigurationError(string stationId, string variableName, bool requiresWrite) =>
        _stations.TryGetValue(stationId, out var node)
            ? PlcTaskConfigurationInspector.Inspect(node.Options, node.Catalog, stationId, variableName, requiresWrite)
            : "工位未绑定 PLC";
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var c = Current;
        return Task.FromResult(new DeviceConnectionStatus(c.DeviceId, c.DisplayName, c.State, c.UpdatedAt, c.Message));
    }
    public Task<bool?> ReadSignalAsync(string stationId, string signalName, CancellationToken cancellationToken) =>
        Resolve(stationId).Gateway.ReadSignalAsync(stationId, signalName, cancellationToken);
    public Task<StationSignal?> ReadStationAsync(string stationId, CancellationToken cancellationToken) =>
        Resolve(stationId).Gateway.ReadStationAsync(stationId, cancellationToken);
    public Task WriteSignalAsync(string stationId, string signalName, bool value, CancellationToken cancellationToken) =>
        Resolve(stationId).Gateway.WriteSignalAsync(stationId, signalName, value, cancellationToken);
    public void Dispose() { foreach (var node in Connections) node.Gateway.Dispose(); }
}
