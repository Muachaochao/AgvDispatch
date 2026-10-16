using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Application.Abstractions;

public interface IPlcSignalCache
{
    event Action<PlcSignalChanged>? SignalChanged;

    long Version { get; }
    IReadOnlyList<PlcSignalSnapshot> GetAll();
    IReadOnlyList<PlcSignalSnapshot> GetStation(string stationId);
    bool TryGet(string stationId, string variableName, out PlcSignalSnapshot snapshot);
}
