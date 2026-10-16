using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Stations;

namespace AgvDispatch.Application.Abstractions;

public interface IPlcGateway
{
    Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken);
    Task<bool?> ReadSignalAsync(string stationId, string signalName, CancellationToken cancellationToken);
    Task<StationSignal?> ReadStationAsync(string stationId, CancellationToken cancellationToken);
    Task WriteSignalAsync(string stationId, string signalName, bool value, CancellationToken cancellationToken);
}
