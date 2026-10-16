using System.Collections.Concurrent;
using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Infrastructure.Persistence;

// Read-only consumer interface; trusted future PLC/AGV adapters publish complete samples here.
// No external write endpoint and no hardware producer is enabled in this batch.
public sealed class ExecutionIdentityCache : IExecutionIdentityCache
{
    private readonly ConcurrentDictionary<string, PlcExecutionIdentity> _plc = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, VehicleContainerIdentity> _vehicles = new(StringComparer.Ordinal);
    public void Publish(PlcExecutionIdentity sample) => _plc[sample.StationId] = sample;
    public void Publish(VehicleContainerIdentity sample) => _vehicles[sample.VehicleId] = sample;
    public bool TryGetPlc(string id, out PlcExecutionIdentity sample) => _plc.TryGetValue(id, out sample!);
    public bool TryGetVehicle(string id, out VehicleContainerIdentity sample) => _vehicles.TryGetValue(id, out sample!);
    public IReadOnlyList<PlcExecutionIdentity> GetPlc() => _plc.Values.OrderBy(s => s.StationId, StringComparer.Ordinal).ToArray();
    public IReadOnlyList<VehicleContainerIdentity> GetVehicles() => _vehicles.Values.OrderBy(s => s.VehicleId, StringComparer.Ordinal).ToArray();
}
