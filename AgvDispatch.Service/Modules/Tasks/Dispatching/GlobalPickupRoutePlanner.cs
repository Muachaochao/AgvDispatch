using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed record StagedPickupRoute(RoutePlan Route, IReadOnlyList<AgvTransportRouteWindow> Windows, double DeferredSeconds);

public sealed class GlobalPickupRoutePlanner(
    DispatchTaskStepOptions steps, AgvOptions agv, HandshakeExecutionOptions handshake)
{
    private const int MaximumHoldingCandidates = 6;
    private readonly ConditionalWeakTable<ActiveMapRuntimeSnapshot,
        ConcurrentDictionary<(string NodeId, double Clearance), bool>> _holdingClearance = new();

    public bool SupportsRollingRoutes => steps.RollingReservationEnabled &&
        agv.NavigationCommandMode.Equals("SpecifiedPath", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<AgvTransportRouteWindow> PlanInitialWindows(RoutePlan route, ActiveMapRuntimeSnapshot map) =>
        TransportRouteWindows.NormalizeResources(
            TransportRouteWindows.Plan(route, map, steps, agv.NavigationCommandMode), map);

    public IReadOnlyList<string> SafeHoldingSites(ActiveMapRuntimeSnapshot map,
        IReadOnlyCollection<string> vehicleIds)
    {
        if (!map.Definition.Unit.Equals("m", StringComparison.OrdinalIgnoreCase))
            return [];
        var clearance = FleetClearance(vehicleIds);
        if (clearance is null) return [];
        return map.Definition.Nodes
            .Where(node => node.Enabled && node.AllowDumperWaiting != false && IsHoldingSite(node, map, clearance.Value))
            .Select(node => string.IsNullOrWhiteSpace(node.AgvSiteId) ? node.Id : node.AgvSiteId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public StagedPickupRoute? PlanFollowup(IRoutePlanner planner, ActiveMapRuntimeSnapshot map, string source, string target,
        IReadOnlySet<string> unavailable, IReadOnlyCollection<string> vehicleIds, string? dumperExit = null)
    {
        var clear = planner.Plan(source, target, unavailable);
        StagedPickupRoute? best = null;
        if (clear is { Nodes.Count: >= 2 } && double.IsFinite(clear.Cost))
        {
            var windows = ProtectExit(TransportRouteWindows.NormalizeResources(
                TransportRouteWindows.PlanCandidate(clear, map, steps, agv.NavigationCommandMode), map));
            if (windows.Count != 0 && !windows[0].ResourceIds.Any(unavailable.Contains))
                best = new(clear, windows, 0);
        }
        if (!SupportsRollingRoutes || unavailable.Count == 0) return best;

        var shortest = planner.Plan(source, target, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var staged = shortest is { Nodes.Count: >= 3 } && double.IsFinite(shortest.Cost)
            ? TryStage(shortest, map, unavailable, vehicleIds)
            : null;
        var viaHolding = TryStageViaHolding(planner, map, source, target, unavailable, vehicleIds);
        staged = BetterStage(staged, viaHolding, unavailable);
        if (staged is null) return best;
        var protectedWindows = ProtectExit(staged.Windows);
        if (protectedWindows[0].ResourceIds.Any(unavailable.Contains))
            return best;
        staged = staged with { Windows = protectedWindows };
        return best is not null && staged.Route.Cost + staged.DeferredSeconds >= best.Route.Cost
            ? best : staged;

        IReadOnlyList<AgvTransportRouteWindow> ProtectExit(IReadOnlyList<AgvTransportRouteWindow> windows) =>
            dumperExit is null ? windows : TransportRouteWindows.IncludeDumperExitClearance(windows, map.Definition, dumperExit);
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

    public StagedPickupRoute? TryStage(RoutePlan route, ActiveMapRuntimeSnapshot map, IReadOnlySet<string> unavailable,
        IReadOnlyCollection<string> vehicleIds, bool allowClearRoute = false)
    {
        if ((unavailable.Count == 0 && !allowClearRoute) ||
            (!allowClearRoute && !route.ResourceIds.Any(unavailable.Contains)) ||
            route.Nodes.Count < 3 || route.EdgeIds.Count != route.Nodes.Count - 1 ||
            !map.Definition.Unit.Equals("m", StringComparison.OrdinalIgnoreCase))
            return null;
        var clearance = FleetClearance(vehicleIds);
        if (clearance is null) return null;

        // A waiting bay used to stage a vehicle before a blocked window must
        // clear adjacent lanes. Keep the other map-authored rolling stop roles.
        var windows = TransportRouteWindows.NormalizeResources(
            TransportRouteWindows.PlanCandidate(route, map, steps, agv.NavigationCommandMode,
                canStageAt: node => node.Type != MapNodeType.WaitingPoint ||
                    IsHoldingSite(node, map, clearance.Value)), map);
        if (windows.Count < 2 || windows[0].ResourceIds.Any(unavailable.Contains))
            return null;

        var conflicts = windows.Skip(1).SelectMany(window => window.ResourceIds)
            .Where(unavailable.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return conflicts == 0 && !allowClearRoute ? null :
            new(route, windows, Math.Min(90d, 12d + 3d * conflicts));
    }

    /// <summary>
    /// Finds an actually isolated waiting bay near the logical route when the
    /// shortest path has no safe interior stop. The first leg must be fully
    /// available. The remaining leg still uses the original target and keeps
    /// unavailable resources for later rolling reservation.
    /// </summary>
    public StagedPickupRoute? TryStageViaHolding(IRoutePlanner planner, ActiveMapRuntimeSnapshot map,
        string source, string target, IReadOnlySet<string> unavailable, IReadOnlyCollection<string> vehicleIds,
        bool allowClearRoute = false, IReadOnlySet<string>? excludedHoldingSites = null)
    {
        if ((unavailable.Count == 0 && !allowClearRoute) || !SupportsRollingRoutes ||
            !map.Definition.Unit.Equals("m", StringComparison.OrdinalIgnoreCase))
            return null;
        var clearance = FleetClearance(vehicleIds);
        var sourceNode = map.Graph.FindRouteNode(source);
        var targetNode = map.Graph.FindRouteNode(target);
        if (clearance is null || sourceNode is null || targetNode is null) return null;

        var directDistance = Distance(sourceNode, targetNode);
        var candidates = map.Definition.Nodes
            .Where(node => node.Enabled && IsHoldingSite(node, map, clearance.Value))
            .Where(node => !MapRouteSiteMapper.IsSameNode(map.Definition, node.Id, source) &&
                !MapRouteSiteMapper.IsSameNode(map.Definition, node.Id, target) &&
                excludedHoldingSites?.Contains(node.Id) != true &&
                !node.ResourceIds.Any(unavailable.Contains))
            .Select(node => (Node: node, Detour: Distance(sourceNode, node) + Distance(node, targetNode) - directDistance))
            .OrderBy(candidate => candidate.Detour)
            .ThenBy(candidate => candidate.Node.Id, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumHoldingCandidates)
            .ToArray();

        StagedPickupRoute? best = null;
        foreach (var candidate in candidates)
        {
            var firstLeg = planner.Plan(source, candidate.Node.Id, unavailable);
            if (firstLeg is not { Nodes.Count: >= 2 } || !double.IsFinite(firstLeg.Cost)) continue;
            var remaining = planner.Plan(candidate.Node.Id, target, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (remaining is not { Nodes.Count: >= 2 } || !double.IsFinite(remaining.Cost)) continue;

            var combined = new RoutePlan(
                firstLeg.Nodes.Concat(remaining.Nodes.Skip(1)).ToArray(),
                firstLeg.EdgeIds.Concat(remaining.EdgeIds).ToArray(),
                firstLeg.ResourceIds.Concat(remaining.ResourceIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                firstLeg.Cost + remaining.Cost);
            var staged = TryStage(combined, map, unavailable, vehicleIds, allowClearRoute);
            if (staged is null ||
                !MapRouteSiteMapper.IsSameNode(map.Definition, staged.Windows[0].Destination, candidate.Node.Id) ||
                staged.Windows[0].ResourceIds.Any(unavailable.Contains))
                continue;
            best = allowClearRoute
                ? BetterProactiveStage(best, staged)
                : BetterStage(best, staged, unavailable);
        }
        return best;
    }

    private static StagedPickupRoute? BetterStage(StagedPickupRoute? left, StagedPickupRoute? right,
        IReadOnlySet<string> unavailable)
    {
        if (left is { } checkedLeft && checkedLeft.Windows[0].ResourceIds.Any(unavailable.Contains))
            left = null;
        if (right is { } checkedRight && checkedRight.Windows[0].ResourceIds.Any(unavailable.Contains))
            right = null;
        if (left is null) return right;
        if (right is null) return left;
        return StageScore(left) <= StageScore(right) ? left : right;
    }

    private static double StageScore(StagedPickupRoute staged) =>
        staged.Route.Cost + staged.DeferredSeconds;

    private static StagedPickupRoute? BetterProactiveStage(StagedPickupRoute? left,
        StagedPickupRoute right)
    {
        if (left is null) return right;
        var edgeComparison = left.Windows[0].EdgeIds.Count.CompareTo(right.Windows[0].EdgeIds.Count);
        return edgeComparison <= 0 && (edgeComparison < 0 || StageScore(left) <= StageScore(right))
            ? left : right;
    }

    private static double Distance(MapNode left, MapNode right) =>
        Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));

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

    private bool IsHoldingSite(MapNode stop, ActiveMapRuntimeSnapshot map, double clearance) =>
        stop is { AllowTrafficStop: true, Type: MapNodeType.WaitingPoint } &&
        _holdingClearance.GetOrCreateValue(map).GetOrAdd((stop.Id, clearance),
            static (_, input) => HasHoldingClearance(input.Stop, input.Map, input.Clearance),
            (Stop: stop, Map: map.Definition, Clearance: clearance));

    private static bool HasHoldingClearance(MapNode stop, MapDefinition map, double clearance)
    {
        if (!stop.ResourceIds.Any(id => map.Resources.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase) &&
                r is { Type: TrafficResourceType.Station, Capacity: 1 }) &&
                !map.Nodes.Any(n => n.Id != stop.Id && n.ResourceIds.Contains(id, StringComparer.OrdinalIgnoreCase))))
            return false;

        var nodes = map.Nodes.Where(n => n.Enabled).ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var edges = map.Edges.Where(e => e.Enabled && nodes.ContainsKey(e.From) && nodes.ContainsKey(e.To)).ToArray();
        var incoming = edges.Where(e => e.To.Equals(stop.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        var outgoing = edges.Where(e => e.From.Equals(stop.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (incoming.Length != 1 || outgoing.Length != 1 ||
            incoming[0] is not { Bidirectional: false, GeometryKind: MapEdgeGeometryKind.Line } ||
            outgoing[0] is not { Bidirectional: false, GeometryKind: MapEdgeGeometryKind.Line })
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
