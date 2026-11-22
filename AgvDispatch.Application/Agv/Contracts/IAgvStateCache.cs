using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Application.Abstractions;

public interface IAgvStateCache
{
    event Action<AgvRobotStateChanged>? StateChanged;

    long Version { get; }
    IReadOnlyList<AgvRobotSnapshot> GetAll();
    bool TryGet(string vehicleId, out AgvRobotSnapshot snapshot);

    bool TryGetConsecutiveTelemetry(string vehicleId, out AgvRobotSnapshot previous, out AgvRobotSnapshot latest)
    {
        previous = latest = null!;
        return false;
    }
}
