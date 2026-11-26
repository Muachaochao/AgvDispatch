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

    RoutePlan? PlanChargingApproachForComparison(string source, string destination) =>
        PlanWithoutVehicleOccupancyForComparison(source, destination);

    /// <summary>Plans a future leg for <paramref name="movingVehicleId"/>: that vehicle's own parked body is not an
    /// obstacle on its own route (for example returning to the bay it is parked on before it leaves for charging).
    /// Every other vehicle, lease, closure and no-through rule still applies.</summary>
    RoutePlan? PlanForVehicle(string source, string destination, IReadOnlySet<string> unavailableResourceIds,
        string movingVehicleId) => Plan(source, destination, unavailableResourceIds);

    /// <summary>Roads occupied by other vehicles, independently of traffic leases.</summary>
    IReadOnlySet<string> GetVehicleOccupiedEdgeIds(MapDefinition definition, string source, string vehicleId) =>
        System.Collections.Frozen.FrozenSet<string>.Empty;

    RoutePlan? PlanChargingApproach(string source, string destination, IReadOnlySet<string> unavailableResourceIds,
        string movingVehicleId) => PlanForVehicle(source, destination, unavailableResourceIds, movingVehicleId);
}
