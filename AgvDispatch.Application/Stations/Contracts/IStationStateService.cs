using AgvDispatch.Domain.Stations;

namespace AgvDispatch.Application.Abstractions;

public interface IStationStateService
{
    event Action<StationStateChanged>? StationStateChanged;

    long Version { get; }
    IReadOnlyList<StationStateSnapshot> GetAll();
    bool TryGet(string stationId, out StationStateSnapshot snapshot);
}
