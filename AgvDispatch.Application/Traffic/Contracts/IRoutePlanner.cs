using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Application.Abstractions;

public interface IRoutePlanner
{
    RoutePlan? Plan(
        string source,
        string destination,
        IReadOnlySet<string> unavailableResourceIds);

    /// <summary>Comparison only: ignores temporary vehicle/lease occupancy but retains
    /// map direction, business no-through rules and official road closures. Never send this route.</summary>
    RoutePlan? PlanWithoutVehicleOccupancyForComparison(string source, string destination) => null;
}
