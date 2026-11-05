using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

public sealed class ConfiguredRoutePlanner : IRoutePlanner
{
    private readonly MapGraph? _configuredGraph;
    private readonly MapDefinition? _configuredDefinition;
    private readonly IActiveMapRuntime _activeMap;
    private readonly DispatchOrchestratorOptions _legacyOptions;
    private readonly ITrafficFlowMonitor _trafficFlow;
    private readonly IRoadClosureService? _roadClosures;
    private readonly IAgvStateCache? _vehicleStates;

    public ConfiguredRoutePlanner(
        MapDefinitionProvider map,
        IActiveMapRuntime activeMap,
        DispatchOrchestratorOptions legacyOptions,
        ITrafficFlowMonitor trafficFlow,
        IRoadClosureService? roadClosures = null,
        IAgvStateCache? vehicleStates = null)
    {
        _legacyOptions = legacyOptions;
        _activeMap = activeMap;
        _trafficFlow = trafficFlow;
        _roadClosures = roadClosures;
        _vehicleStates = vehicleStates;
        if (map.Definition is not null)
        {
            _configuredDefinition = map.Definition;
            _configuredGraph = map.Definition.ToGraph();
        }
    }

    public RoutePlan? Plan(
        string source,
        string destination,
        IReadOnlySet<string> unavailableResourceIds)
    {
        var snapshot = _activeMap.Current;
        var closures = _roadClosures?.GetSnapshot();
        if (closures is { Ready: false } || closures?.IdentityConflict == true) return null;
        var activeGraph = snapshot?.Graph;
        var activeDefinition = snapshot?.Definition;
        if (activeGraph is not null && activeDefinition is not null)
        {
            if (closures is not null && (closures.MapId != snapshot!.MapId || closures.VersionId != snapshot.VersionId ||
                !string.Equals(closures.DefinitionHash, EngineeringRuntimeIdentity.MapHash(activeDefinition), StringComparison.OrdinalIgnoreCase)))
                return null;
            return PlanAndMap(activeGraph, activeDefinition, source, destination,
                unavailableResourceIds, _trafficFlow.GetEdgePenalties(), closures?.BlockedTraversals);
        }
        if (_configuredGraph is not null)
        {
            // A persisted closure is bound to a published map identity. A startup
            // configuration graph cannot silently stand in for that identity.
            if (closures?.Closures.Any(c => c.Active) == true) return null;
            return PlanAndMap(_configuredGraph, _configuredDefinition!, source, destination,
                unavailableResourceIds, _trafficFlow.GetEdgePenalties());
        }

        if (closures?.Closures.Any(c => c.Active) == true) return null;

        // A repeat pickup must be backed by actual enabled graph edges, not a
        // legacy direct-route fallback such as [PICK, PICK].
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return null;
        var route = _legacyOptions.GetRoute(source, destination);
        return route is null
            ? null
            : new RoutePlan(route, [], [], Math.Max(0, route.Count - 1));
    }

    private RoutePlan? PlanAndMap(
        MapGraph graph,
        MapDefinition definition,
        string source,
        string destination,
        IReadOnlySet<string> unavailableResourceIds,
        IReadOnlyDictionary<string, double> edgePenalties,
        IReadOnlyCollection<MapDirectedTraversal>? unavailableTraversals = null)
    {
        var logicalSource = MapRouteSiteMapper.ResolveLogicalNodeId(definition, source);
        var logicalDestination = MapRouteSiteMapper.ResolveLogicalNodeId(definition, destination);
        try
        {
            unavailableTraversals = (unavailableTraversals ?? []).Concat(
                DumperTransitClearance.BlockedTraversals(definition, logicalSource, logicalDestination,
                    _legacyOptions.DumperDumpingSite, _legacyOptions.DumperTransitClearanceMeters)).Distinct().ToArray();
        }
        catch (ArgumentException) { return null; }
        var waitingAndDumping = _legacyOptions.GetDumperWaitingSites().Append(_legacyOptions.DumperDumpingSite)
            .Where(site => !string.IsNullOrWhiteSpace(site))
            .Select(site => MapRouteSiteMapper.ResolveLogicalNodeId(definition, site!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var standbyNodes = _legacyOptions.StandbySites
            .Where(site => !string.IsNullOrWhiteSpace(site))
            .Select(site => MapRouteSiteMapper.ResolveLogicalNodeId(definition, site!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var protectedNodes = waitingAndDumping.Concat(standbyNodes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Most standby bays remain no-through nodes. A corridor point may explicitly
        // allow transit; its capacity-1 resource remains enforced by route resources.
        protectedNodes.RemoveWhere(id => definition.Nodes.Any(node =>
            node.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && node.AllowTransitThrough));
        var plan = graph.FindShortestTransportRoute(
            logicalSource, logicalDestination, unavailableResourceIds, edgePenalties, protectedNodes, unavailableTraversals);
        // A vehicle parked in one standby bay may need to pass another bay in the
        // same cluster to reach the road network. Permit that departure path only
        // when the intermediate standby nodes are known to be empty.
        if (plan is null && _vehicleStates is not null && standbyNodes.Contains(logicalSource))
        {
            var occupiedStandby = _vehicleStates.GetAll()
                .Where(vehicle => !string.IsNullOrWhiteSpace(vehicle.CurrentStation))
                .Select(vehicle => MapRouteSiteMapper.ResolveLogicalNodeId(definition, vehicle.CurrentStation!))
                .Where(id => standbyNodes.Contains(id) && !id.Equals(logicalSource, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var departureProtected = waitingAndDumping.Concat(occupiedStandby).ToHashSet(StringComparer.OrdinalIgnoreCase);
            plan = graph.FindShortestTransportRoute(
                logicalSource, logicalDestination, unavailableResourceIds, edgePenalties, departureProtected, unavailableTraversals);
        }
        return plan is null ? null : MapRouteSiteMapper.ToAgvSitePlan(definition, plan);
    }

}
