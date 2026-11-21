using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed record StagedPickupRoute(RoutePlan Route, IReadOnlyList<AgvTransportRouteWindow> Windows, double DeferredSeconds);

public sealed class GlobalPickupRoutePlanner(
    DispatchTaskStepOptions steps, AgvOptions agv, HandshakeExecutionOptions handshake)
{
    public bool SupportsRollingRoutes => steps.RollingReservationEnabled &&
        agv.NavigationCommandMode.Equals("SpecifiedPath", StringComparison.OrdinalIgnoreCase);

    public StagedPickupRoute? PlanFollowup(IRoutePlanner planner, MapDefinition map, string source, string target,
        IReadOnlySet<string> unavailable, IReadOnlyCollection<string> vehicleIds, string? dumperExit = null)
    {
        var clear = planner.Plan(source, target, unavailable);
        StagedPickupRoute? best = null;
        if (clear is { Nodes.Count: >= 2 } && double.IsFinite(clear.Cost))
        {
            var windows = ProtectExit(TransportRouteWindows.Plan(clear, map, steps, agv.NavigationCommandMode));
            if (!windows[0].ResourceIds.Any(unavailable.Contains)) best = new(clear, windows, 0);
        }
        if (!SupportsRollingRoutes || unavailable.Count == 0) return best;

        var shortest = planner.Plan(source, target, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (shortest is null || !double.IsFinite(shortest.Cost) || shortest.Cost >= best?.Route.Cost)
            return best;
        var staged = TryStage(shortest, map, unavailable, vehicleIds);
        if (staged is null) return best;
        var protectedWindows = ProtectExit(staged.Windows);
        if (protectedWindows[0].ResourceIds.Any(unavailable.Contains) ||
            best is not null && staged.Route.Cost + staged.DeferredSeconds >= best.Route.Cost)
            return best;
        return staged with { Windows = protectedWindows };

        IReadOnlyList<AgvTransportRouteWindow> ProtectExit(IReadOnlyList<AgvTransportRouteWindow> windows) =>
            dumperExit is null ? windows : TransportRouteWindows.IncludeDumperExitClearance(windows, map, dumperExit);
    }

    public static IReadOnlyList<AgvTransportRouteWindow> ReplaceRemainingWindows(
        AgvTransportCommandPayload payload, IReadOnlyList<AgvTransportRouteWindow> remaining)
    {
        var firstIndex = payload.WindowIndex + 1;
        return payload.Windows.Take(firstIndex)
            .Concat(remaining.Select((window, index) => window with { Index = firstIndex + index }))
            .ToArray();
    }

    public static bool IsForwardContinuation(AgvTransportCommandPayload payload, RoutePlan alternative, MapDefinition map)
    {
        var completedStops = payload.Windows.Take(payload.WindowIndex)
            .Select(window => MapRouteSiteMapper.ResolveLogicalNodeId(map, window.Destination))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        completedStops.Add(MapRouteSiteMapper.ResolveLogicalNodeId(map, payload.Windows[0].Source));
        return !alternative.Nodes.Skip(1).Any(site =>
            completedStops.Contains(MapRouteSiteMapper.ResolveLogicalNodeId(map, site)));
    }

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
