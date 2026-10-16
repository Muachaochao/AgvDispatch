using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Application.Abstractions;

// Read-only, cached observations. No device I/O or command APIs belong here.
public sealed record MonitoringField(string Name, string Value, string Quality, string Address, string UpdatedAt, string? ErrorMessage = null);
public sealed record MonitoredDevice(string Id, string Name, string Category, string Connection,
    string Status, string Quality, string UpdatedAt, string Summary, IReadOnlyList<MonitoringField> Fields);
public sealed record DeviceMonitoringSnapshot(DateTimeOffset GeneratedAt,
    IReadOnlyList<MonitoredDevice> Vehicles, IReadOnlyList<MonitoredDevice> Equipment);
public interface IDeviceMonitoringService
{
    Task<DeviceMonitoringSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
public interface IDeviceMonitoringClient
{
    Task<DeviceMonitoringSnapshot> GetDeviceMonitoringAsync(CancellationToken cancellationToken);
}
