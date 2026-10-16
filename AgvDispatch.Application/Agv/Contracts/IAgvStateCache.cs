using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Application.Abstractions;

public interface IAgvStateCache
{
    event Action<AgvRobotStateChanged>? StateChanged;

    long Version { get; }
    IReadOnlyList<AgvRobotSnapshot> GetAll();
    bool TryGet(string vehicleId, out AgvRobotSnapshot snapshot);
}
