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
    Task CancelTaskAsync(string vendorTaskId, CancellationToken cancellationToken);
}
