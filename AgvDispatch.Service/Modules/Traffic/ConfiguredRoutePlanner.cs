using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

public sealed class ConfiguredRoutePlanner : IRoutePlanner
{
    private static readonly IReadOnlySet<string> B07VerticalInlet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "CAD2-E01834", "CAD2-E01859" };
    private readonly MapGraph? _configuredGraph;
    private readonly MapDefinition? _configuredDefinition;
    private readonly IActiveMapRuntime _activeMap;
    private readonly DispatchOrchestratorOptions _legacyOptions;
    private readonly ITrafficFlowMonitor _trafficFlow;
    private readonly IRoadClosureService? _roadClosures;
    private readonly IAgvStateCache? _vehicleStates;
    private readonly MapOptions? _mapOptions;

    public ConfiguredRoutePlanner(
        MapDefinitionProvider map,
        IActiveMapRuntime activeMap,
        DispatchOrchestratorOptions legacyOptions,
        ITrafficFlowMonitor trafficFlow,
        IRoadClosureService? roadClosures = null,
        IAgvStateCache? vehicleStates = null,
        MapOptions? mapOptions = null)
    {
        _legacyOptions = legacyOptions;
        _activeMap = activeMap;
        _trafficFlow = trafficFlow;
        _roadClosures = roadClosures;
        _vehicleStates = vehicleStates;
        _mapOptions = mapOptions;
        if (map.Definition is not null)
        {
            _configuredDefinition = map.Definition;
            _configuredGraph = map.Definition.ToGraph();
        }
    }

    public RoutePlan? Plan(
        string source,
        string destination,
        IReadOnlySet<string> unavailableResourceIds) => PlanCore(source, destination, unavailableResourceIds, false);

    public RoutePlan? PlanForVehicle(string source, string destination, IReadOnlySet<string> unavailableResourceIds,
        string movingVehicleId) => PlanCore(source, destination, unavailableResourceIds, false, movingVehicleId);

    public RoutePlan? PlanChargingApproach(string source, string destination, IReadOnlySet<string> unavailableResourceIds,
        string movingVehicleId) => PlanCore(source, destination, unavailableResourceIds, false, movingVehicleId, chargingApproach: true);

    public RoutePlan? PlanWithoutVehicleOccupancyForComparison(string source, string destination) =>
        PlanCore(source, destination, new HashSet<string>(), true);

    public RoutePlan? PlanChargingApproachForComparison(string source, string destination) =>
        PlanCore(source, destination, new HashSet<string>(), true, chargingApproach: true);

    private RoutePlan? PlanCore(string source, string destination,
        IReadOnlySet<string> unavailableResourceIds, bool comparisonOnly, string? movingVehicleId = null, bool chargingApproach = false)
    {
        var snapshot = _activeMap.Current;
        var closures = _roadClosures?.GetSnapshot();
        if (closures is { Ready: false } || closures?.IdentityConflict == true) return null;
        var activeGraph = snapshot?.Graph;
        var activeDefinition = snapshot?.Definition;
        if (activeGraph is not null && activeDefinition is not null)
        {
            if (closures is not null && (closures.MapId != snapshot!.MapId || closures.VersionId != snapshot.VersionId ||
                !string.Equals(closures.DefinitionHash, snapshot.DefinitionHash, StringComparison.OrdinalIgnoreCase)))
                return null;
            return PlanAndMap(activeGraph, activeDefinition, source, destination,
                unavailableResourceIds, _trafficFlow.GetEdgePenalties(), closures?.BlockedTraversals, comparisonOnly, movingVehicleId, chargingApproach);
        }
        if (_configuredGraph is not null)
        {
            // A persisted closure is bound to a published map identity. A startup
            // configuration graph cannot silently stand in for that identity.
            if (closures?.Closures.Any(c => c.Active) == true) return null;
            return PlanAndMap(_configuredGraph, _configuredDefinition!, source, destination,
                unavailableResourceIds, _trafficFlow.GetEdgePenalties(), comparisonOnly: comparisonOnly,
                movingVehicleId: movingVehicleId, chargingApproach: chargingApproach);
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
        IReadOnlyCollection<MapDirectedTraversal>? unavailableTraversals = null, bool comparisonOnly = false,
        string? movingVehicleId = null, bool chargingApproach = false)
    {
        var logicalSource = MapRouteSiteMapper.ResolveLogicalNodeId(definition, source);
        var logicalDestination = MapRouteSiteMapper.ResolveLogicalNodeId(definition, destination);
        var guardedInlet = chargingApproach && logicalDestination.Equals("CHARGE-02", StringComparison.OrdinalIgnoreCase) &&
            _legacyOptions.SimulationB07VerticalChargingInlet &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGV_SIMULATION_ROOT"));
        try
        {
            unavailableTraversals = (unavailableTraversals ?? []).Concat(
                DumperTransitClearance.BlockedTraversals(definition, logicalSource, logicalDestination,
                    _legacyOptions.DumperDumpingSite, _legacyOptions.DumperTransitClearanceMeters,
                    guardedInlet ? B07VerticalInlet : null, guardedInlet ? "DUMPER-001" : null))
                .Concat(comparisonOnly ? [] : OccupiedCorridorTraversals(definition, logicalSource, movingVehicleId))
                .Distinct().ToArray();
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
                .Where(vehicle => !string.IsNullOrWhiteSpace(vehicle.CurrentStation) &&
                    !string.Equals(vehicle.VehicleId, movingVehicleId, StringComparison.OrdinalIgnoreCase))
                .Select(vehicle => MapRouteSiteMapper.ResolveLogicalNodeId(definition, vehicle.CurrentStation!))
                .Where(id => physicalBayNodes.Contains(id) && !id.Equals(logicalSource, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var departureProtected = occupiedBays;
            plan = graph.FindShortestTransportRoute(
                logicalSource, logicalDestination, unavailableResourceIds, edgePenalties, departureProtected, unavailableTraversals);
        }
        return plan is null ? null : MapRouteSiteMapper.ToAgvSitePlan(definition, plan);
    }

    public IReadOnlySet<string> GetVehicleOccupiedEdgeIds(MapDefinition definition, string source, string vehicleId) =>
        OccupiedCorridorTraversals(definition, MapRouteSiteMapper.ResolveLogicalNodeId(definition, source), vehicleId)
            .Select(traversal => traversal.EdgeId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<MapDirectedTraversal> OccupiedCorridorTraversals(MapDefinition definition, string logicalSource,
        string? movingVehicleId = null)
    {
        if (_vehicleStates is null || _mapOptions is null) return [];
        var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow();
        var maximumAge = TimeSpan.FromSeconds(_mapOptions.PositionStaleSeconds);
        var blocked = new List<MapDirectedTraversal>();
        var vehicles = _vehicleStates.GetAll();
        var sourceNode = definition.Nodes.SingleOrDefault(n => n.Id.Equals(logicalSource, StringComparison.OrdinalIgnoreCase));
        var departing = vehicles.Where(v => sourceNode is not null &&
            v.X is { } x && v.Y is { } y && Distance(x, y, sourceNode.X, sourceNode.Y) <= .15 &&
            MapRouteSiteMapper.ResolveLogicalNodeId(definition, v.CurrentStation ?? "").Equals(logicalSource, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var departingId = departing.Length == 1 ? departing[0].VehicleId : null;
        foreach (var vehicle in vehicles)
        {
            // The vehicle that will drive this route is never an obstacle on its own future leg.
            if (string.Equals(vehicle.VehicleId, movingVehicleId, StringComparison.OrdinalIgnoreCase)) continue;
            // Completing a business task releases its leases, not its physical
            // body. A parked peer keeps blocking its bay and the explicitly modelled
            // adjacent-road envelope resources until fresh pose evidence shows it has
            // left. Offline or stale telemetry therefore falls back to the last known
            // station, never to "no vehicle here". The unique fresh vehicle at the
            // actual source may depart; a nearest point is never substituted.
            var fresh = vehicle.Connection == ConnectionState.Online && vehicle.Quality == SignalQuality.Good &&
                vehicle.X is not null && vehicle.Y is not null && vehicle.LocalizationConfidence is not null &&
                vehicle.LastSuccessfulPollAt is { } seenAt && seenAt <= now.AddSeconds(1) &&
                now - seenAt <= maximumAge;
            var parked = definition.Nodes.SingleOrDefault(n => n.Enabled &&
                (n.Id.Equals(vehicle.CurrentStation, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(n.AgvSiteId, vehicle.CurrentStation, StringComparison.OrdinalIgnoreCase)) &&
                (!fresh || Distance(vehicle.X!.Value, vehicle.Y!.Value, n.X, n.Y) <= .15));
            if (parked is not null && (!fresh || !string.Equals(vehicle.VehicleId, departingId, StringComparison.OrdinalIgnoreCase)))
            {
                var exclusive = definition.Resources.Where(r => r.Capacity == 1 &&
                    parked.ResourceIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase))
                    .Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var edge in definition.Edges.Where(e => e.Enabled &&
                    (e.From.Equals(parked.Id, StringComparison.OrdinalIgnoreCase) ||
                     e.To.Equals(parked.Id, StringComparison.OrdinalIgnoreCase) ||
                     e.ResourceIds.Any(exclusive.Contains))))
                {
                    blocked.Add(new(edge.Id, edge.From, edge.To));
                    if (edge.Bidirectional) blocked.Add(new(edge.Id, edge.To, edge.From));
                }
            }
            // Route geometry of a moving vehicle requires fresh, localized telemetry.
            if (!fresh) continue;
            var x = vehicle.X!.Value;
            var y = vehicle.Y!.Value;
            var confidence = vehicle.LocalizationConfidence!.Value;
            var observed = vehicle.LastSuccessfulPollAt!.Value;
            var position = AgvMapPositionAnalyzer.Analyze(x, y, confidence, observed, definition, now,
                maximumAge, _mapOptions.MinimumLocalizationConfidence,
                _mapOptions.MaximumRouteDeviationMeters, reuseGeometry: true);
            if (position.State != AgvMapPositionState.Normal ||
                string.IsNullOrWhiteSpace(position.NearestEdgeId))
                continue;
            var occupied = definition.Edges.FirstOrDefault(edge =>
                edge.Enabled && string.Equals(edge.Id, position.NearestEdgeId, StringComparison.OrdinalIgnoreCase));
            if (occupied is null) continue;
            var from = definition.Nodes.FirstOrDefault(node =>
                string.Equals(node.Id, occupied.From, StringComparison.OrdinalIgnoreCase));
            var to = definition.Nodes.FirstOrDefault(node =>
                string.Equals(node.Id, occupied.To, StringComparison.OrdinalIgnoreCase));
            if (from is null || to is null) continue;
            // At a station node the route must be allowed to depart. Only a vehicle
            // occupying the interior of a corridor blocks reciprocal traversals.
            var endpointClearance = 0.1;
            if (Distance(x, y, from.X, from.Y) <= endpointClearance ||
                Distance(x, y, to.X, to.Y) <= endpointClearance)
                continue;
            foreach (var edge in definition.Edges.Where(edge => edge.Enabled &&
                         SameUndirectedCorridor(edge, occupied)))
            {
                blocked.Add(new(edge.Id, edge.From, edge.To));
                if (edge.Bidirectional) blocked.Add(new(edge.Id, edge.To, edge.From));
            }
        }
        return blocked.Distinct().ToArray();
    }

    private static bool SameUndirectedCorridor(MapEdge first, MapEdge second) =>
        string.Equals(first.From, second.From, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.To, second.To, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(first.From, second.To, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.To, second.From, StringComparison.OrdinalIgnoreCase);

    private static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

}
