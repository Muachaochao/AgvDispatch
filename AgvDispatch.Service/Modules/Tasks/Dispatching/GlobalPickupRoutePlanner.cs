using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed record StagedPickupRoute(RoutePlan Route, IReadOnlyList<AgvTransportRouteWindow> Windows, double DeferredSeconds);

public sealed class GlobalPickupRoutePlanner(
    DispatchTaskStepOptions steps, AgvOptions agv, HandshakeExecutionOptions handshake)
{
    public StagedPickupRoute? TryStage(RoutePlan route, MapDefinition map, IReadOnlySet<string> unavailable,
        IReadOnlyCollection<string> vehicleIds)
    {
        if (unavailable.Count == 0 || !route.ResourceIds.Any(unavailable.Contains) ||
            route.Nodes.Count < 3 || route.EdgeIds.Count != route.Nodes.Count - 1 ||
            !map.Unit.Equals("m", StringComparison.OrdinalIgnoreCase))
            return null;
        var clearance = FleetClearance(vehicleIds);
        if (clearance is null) return null;

        var windows = TransportRouteWindows.Plan(route, map, steps, agv.NavigationCommandMode,
            node => HasHoldingClearance(node, map, clearance.Value));
        if (windows.Count < 2 || windows[0].ResourceIds.Any(unavailable.Contains))
            return null;

        var conflicts = windows.Skip(1).SelectMany(window => window.ResourceIds)
            .Where(unavailable.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return conflicts == 0 ? null : new(route, windows, Math.Min(90d, 12d + 3d * conflicts));
    }

    private double? FleetClearance(IReadOnlyCollection<string> vehicleIds)
    {
        var exit = handshake.DumperExit;
        if (vehicleIds.Count == 0 || !double.IsFinite(exit.SafetyMarginMeters) || exit.SafetyMarginMeters < 0)
            return null;
        var largest = 0d;
        foreach (var id in vehicleIds)
        {
            var profiles = exit.BodyProfiles.Where(p => p.VehicleId.Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (profiles.Length != 1 || profiles[0] is not { CalibrationVerified: true, IncludesMaximumPermittedLoad: true } body ||
                !double.IsFinite(body.RadiusFromReportedReferenceMeters) || body.RadiusFromReportedReferenceMeters <= 0 ||
                !double.IsFinite(body.MaximumPositionUncertaintyMeters) || body.MaximumPositionUncertaintyMeters < 0)
                return null;
            largest = Math.Max(largest, body.RadiusFromReportedReferenceMeters + body.MaximumPositionUncertaintyMeters);
        }
        return 2 * largest + exit.SafetyMarginMeters;
    }

    private static bool HasHoldingClearance(MapNode stop, MapDefinition map, double clearance)
    {
        if (stop is not { AllowTrafficStop: true, Type: MapNodeType.WaitingPoint } ||
            !stop.ResourceIds.Any(id => map.Resources.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase) &&
                r is { Type: TrafficResourceType.Station, Capacity: 1 }) &&
                !map.Nodes.Any(n => n.Id != stop.Id && n.ResourceIds.Contains(id, StringComparer.OrdinalIgnoreCase))))
            return false;

        var nodes = map.Nodes.Where(n => n.Enabled).ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var edges = map.Edges.Where(e => e.Enabled && nodes.ContainsKey(e.From) && nodes.ContainsKey(e.To)).ToArray();
        var incoming = edges.Where(e => e.To.Equals(stop.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        var outgoing = edges.Where(e => e.From.Equals(stop.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (incoming.Length != 1 || outgoing.Length != 1 ||
            incoming[0] is not { Bidirectional: false, GeometryKind: MapEdgeGeometryKind.Line } ||
            outgoing[0] is not { Bidirectional: false, GeometryKind: MapEdgeGeometryKind.Line } ||
            incoming[0].From.Equals(outgoing[0].To, StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var node in nodes.Values)
        {
            if (node.Id != stop.Id && Math.Sqrt(Math.Pow(node.X - stop.X, 2) + Math.Pow(node.Y - stop.Y, 2)) <= clearance)
                return false;
        }
        // Exclude only the approved lane's approach and departure. Crossing and
        // adjacent lanes must still clear two maximum-load envelopes at the stop.
        return edges.Where(e => e.Id != incoming[0].Id && e.Id != outgoing[0].Id)
            .All(e => MapEdgeGeometry.DistanceTo(e, nodes[e.From], nodes[e.To], stop.X, stop.Y) > clearance);
    }
}
