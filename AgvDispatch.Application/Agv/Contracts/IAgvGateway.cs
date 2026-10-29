using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Application.Abstractions;

public interface IAgvGateway
{
    Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken cancellationToken);
    Task<string> CreateTransportTaskAsync(
        string businessTaskId,
        string? preferredVehicleId,
        IReadOnlyList<string> path,
        AgvTransportAction? action,
        CancellationToken cancellationToken);
    Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(
        string vendorTaskId,
        CancellationToken cancellationToken);
    // Explicit original identity and path are required when no business-task chain
    // repository owns the command (for example a fleet standby return).
    Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string vendorTaskId,string vehicleId,
        IReadOnlyList<string> originalPath,CancellationToken cancellationToken) =>
        throw new NotSupportedException("后台未提供指定车辆原导航全链查询。");
    Task CancelTaskAsync(string vendorTaskId, CancellationToken cancellationToken);
}
