using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Stations;

namespace AgvDispatch.Service;

// A separate connection and catalog: enabling this writer never enables the
// production fleet. Only the six H bits of the local two-station simulator exist here.
public sealed class PlcCycleSimulatorWriter : IPlcGateway, IDisposable
{
    private readonly HslPlcGateway? _gateway;
    private static readonly Dictionary<(string, string), string> Outputs = new()
    {
        [("CNC-001", "FullBoxRequestAcknowledged")] = "DB1.DBX0.4",
        [("CNC-001", "FullBoxRemoved")] = "DB1.DBX0.5",
        [("CNC-001", "EmptyBoxTaskAcknowledged")] = "DB1.DBX0.6",
        [("CNC-001", "EmptyBoxPlaced")] = "DB1.DBX0.7",
        [("DUMPER-001", "AgvAtWaitingPosition")] = "DB1.DBX610.0",
        [("DUMPER-001", "AgvAtDumpingPosition")] = "DB1.DBX610.1"
    };

    public PlcCycleSimulatorWriter(PlcCycleTestOptions mode, PlcFleet fleet)
    {
        if (!mode.Enabled) return;
        if (mode.StationId != "CNC-001" || mode.DumperStationId != "DUMPER-001" ||
            !PlcCycleTestPort.AllPlcsAreLoopback(fleet.Connections.Select(c => c.Options)))
            throw new InvalidOperationException("空载联调仅支持CNC-001及127.0.0.1:10200独立模拟PLC。");
        var node = fleet.Connections.Single(c => c.Options.Enabled);
        var allowed = Outputs.Select(pair =>
        {
            if (!node.Catalog.TryGet(pair.Key.Item1, pair.Key.Item2, out var signal) ||
                !signal.Enabled || signal.Address != pair.Value || signal.DataType != "Bool" ||
                !signal.Access.Contains('W'))
                throw new InvalidOperationException($"模拟PLC输出映射不匹配：{pair.Key}。");
            return signal;
        }).ToArray();
        var options = JsonSerializer.Deserialize<PlcOptions>(JsonSerializer.Serialize(node.Options))!;
        options.AllowWrites = true;
        options.VerifyWrites = true;
        _gateway = new(options, PlcSignalCatalog.FromSignals(allowed), new PlcConnectionSupervisor(options));
    }

    public Task WriteSignalAsync(string stationId, string signalName, bool value, CancellationToken ct)
    {
        if (!Outputs.ContainsKey((stationId, signalName)) || _gateway is null)
            throw new InvalidOperationException("禁止写入联调六个H输出以外的点位。");
        return _gateway.WriteSignalAsync(stationId, signalName, value, ct);
    }
    public Task<bool?> ReadSignalAsync(string stationId, string signalName, CancellationToken ct) => throw new NotSupportedException();
    public Task<StationSignal?> ReadStationAsync(string stationId, CancellationToken ct) => throw new NotSupportedException();
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => throw new NotSupportedException();
    public void Dispose() => _gateway?.Dispose();
}
