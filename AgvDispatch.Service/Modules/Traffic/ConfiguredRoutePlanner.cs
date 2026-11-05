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
        // Waiting, dumping and standby bays are no-through during ordinary routes.
        // A corridor point may explicitly allow transit; its capacity-1 resource
        // remains enforced by route resources.
        var protectedNodes = waitingAndDumping.Concat(standbyNodes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        protectedNodes.RemoveWhere(id => definition.Nodes.Any(node =>
            node.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && node.AllowTransitThrough));
        var plan = graph.FindShortestTransportRoute(
            logicalSource, logicalDestination, unavailableResourceIds, edgePenalties, protectedNodes, unavailableTraversals);
        // A vehicle leaving a bay cluster may need to pass an empty sibling bay to
        // reach the road network. This applies to standby bays and to the waiting /
        // dumping cluster: the latter became reachable only as a protected
        // intermediate node after the 28-stage return path was introduced. Permit
        // the departure path only through cluster nodes observed empty; occupied
        // siblings remain excluded.
        var physicalBayNodes = standbyNodes.Concat(waitingAndDumping).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (plan is null && _vehicleStates is not null && physicalBayNodes.Contains(logicalSource))
        {
            var occupiedBays = _vehicleStates.GetAll()
                .Where(vehicle => !string.IsNullOrWhiteSpace(vehicle.CurrentStation))
                .Select(vehicle => MapRouteSiteMapper.ResolveLogicalNodeId(definition, vehicle.CurrentStation!))
                .Where(id => physicalBayNodes.Contains(id) && !id.Equals(logicalSource, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var departureProtected = occupiedBays;
            plan = graph.FindShortestTransportRoute(
                logicalSource, logicalDestination, unavailableResourceIds, edgePenalties, departureProtected, unavailableTraversals);
        }
        return plan is null ? null : MapRouteSiteMapper.ToAgvSitePlan(definition, plan);
    }

}
