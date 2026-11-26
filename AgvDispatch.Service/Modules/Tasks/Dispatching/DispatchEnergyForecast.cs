using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed class DispatchEnergyOptions
{
    public string Mode { get; set; } = "Disabled";
    public double MinimumArrivalBatteryPercent { get; set; } = 15;
    public double PredictionErrorPercent { get; set; } = 3;
    public double ExpectedDumperWaitSeconds { get; set; } = 120;
    public Dictionary<string, DispatchEnergyProfile> Vehicles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Enabled => Mode is "Observe" or "Enforce";
    public bool Enforced => Mode == "Enforce";
    public bool Valid => (Mode is "Disabled" or "Observe" or "Enforce") &&
        MinimumArrivalBatteryPercent is > 0 and <= 100 &&
        PredictionErrorPercent is >= 0 and <= 100 &&
        MinimumArrivalBatteryPercent + PredictionErrorPercent <= 100 &&
        ExpectedDumperWaitSeconds is >= 0 and <= 86_400 &&
        Vehicles.Values.All(profile => profile.TravelCostPercentPerMeter is >= 0 and <= 10 &&
            profile.IdleCostPercentPerHour is >= 0 and <= 100 &&
            profile.OperationSeconds is >= 0 and <= 600);
}

public sealed class DispatchEnergyProfile
{
    public double TravelCostPercentPerMeter { get; set; }
    public double IdleCostPercentPerHour { get; set; }
    public double OperationSeconds { get; set; }
}

public sealed class DispatchEnergyForecast(DispatchEnergyOptions options, ChargingOptions charging)
{
    public bool Enabled => options.Enabled && charging.Enabled;
    public bool Enforced => options.Enforced;

    public DispatchCandidateEnergy? Estimate(string vehicleId, decimal battery, string pickup, string dumper,
        IReadOnlyList<string> waitingSites, RoutePlan toPickup, double stagingWaitSeconds, ActiveMapRuntimeSnapshot? map,
        IRoutePlanner routes, DispatchAssignmentOptions policy)
    {
        if (map is null || !options.Vehicles.TryGetValue(vehicleId, out var profile)) return null;
        var inbound = routes.PlanWithoutVehicleOccupancyForComparison(dumper, pickup);
        var empty = Leg(toPickup);
        var emptyBox = Leg(inbound);
        (double Meters, double Seconds)? fullBox = null;
        foreach (var waiting in waitingSites)
        {
            var approach = Between(pickup, waiting);
            var entry = Between(waiting, dumper);
            if (approach is null || entry is null) continue;
            var viaBay = (Meters: approach.Value.Meters + entry.Value.Meters,
                Seconds: approach.Value.Seconds + entry.Value.Seconds);
            if (fullBox is null || viaBay.Seconds < fullBox.Value.Seconds) fullBox = viaBay;
        }
        if (empty is null || fullBox is null || emptyBox is null) return null;

        var businessMeters = empty.Value.Meters + fullBox.Value.Meters + emptyBox.Value.Meters;
        var businessSeconds = empty.Value.Seconds + fullBox.Value.Seconds + emptyBox.Value.Seconds +
            options.ExpectedDumperWaitSeconds + stagingWaitSeconds + 2 * profile.OperationSeconds;

        DispatchCandidateEnergy? best = null;
        foreach (var site in charging.Sites.Where(site => site.Commissioned &&
            site.AllowedVehicleIds.Contains(vehicleId, StringComparer.OrdinalIgnoreCase)))
        {
            var chargerLeg = Leg(routes.PlanChargingApproachForComparison(pickup, site.ChargingSiteId));
            if (chargerLeg is null) continue;
            var estimate = Calculate(battery, businessMeters, businessSeconds, chargerLeg.Value.Meters,
                chargerLeg.Value.Seconds, site.Id, profile, options);
            if (best is null || estimate.AtChargerBatteryPercent > best.AtChargerBatteryPercent)
                best = estimate;
        }
        return best;

        (double Meters, double Seconds)? Between(string source, string destination) =>
            MapRouteSiteMapper.IsSameNode(map.Definition, source, destination)
                ? (0, 0)
                : Leg(routes.PlanWithoutVehicleOccupancyForComparison(source, destination));

        (double Meters, double Seconds)? Leg(RoutePlan? route)
        {
            if (route is null || route.EdgeIds.Count == 0 ||
                route.EdgeIds.Any(edge => !map.Graph.Edges.ContainsKey(edge))) return null;
            var time = DispatchCandidateFilter.EstimateRoute(route, map, policy);
            return (route.EdgeIds.Sum(edge => map.Graph.Edges[edge].Distance), time.Travel + time.TrafficWait);
        }
    }

    public DispatchCandidateEnergy? Reassess(string vehicleId, decimal battery, DispatchTaskStepType step,
        string? pickup, string dumper, IReadOnlyList<string> waitingSites, RoutePlan remaining,
        ActiveMapRuntimeSnapshot? map, IRoutePlanner routes, double projectedWaitSeconds = 0)
    {
        if (!Enabled || map is null || pickup is null || battery is < 0 or > 100 ||
            !options.Vehicles.TryGetValue(vehicleId, out var profile) || Measure(remaining) is not {} leg)
            return null;
        var meters = leg.Meters;
        var seconds = leg.Seconds + Math.Max(0, projectedWaitSeconds) + profile.OperationSeconds;
        if (step == DispatchTaskStepType.GoToPickup)
        {
            var staging = waitingSites.Select(waiting => (Approach: Between(pickup, waiting), Entry: Between(waiting, dumper)))
                .Where(x => x.Approach.HasValue && x.Entry.HasValue)
                .OrderBy(x => x.Approach!.Value.Seconds + x.Entry!.Value.Seconds).FirstOrDefault();
            if (staging is not { Approach: {} approach, Entry: {} entry })return null;
            meters += approach.Meters + entry.Meters;
            seconds += approach.Seconds + entry.Seconds + profile.OperationSeconds;
        }
        if (step == DispatchTaskStepType.DeliverFullBoxToDumper)
        {
            if (remaining.Nodes.LastOrDefault() is not {} end || Between(end, dumper) is not {} entry)return null;
            meters += entry.Meters;
            seconds += entry.Seconds;
        }
        if (step != DispatchTaskStepType.ReturnEmptyBox)
        {
            if (Between(dumper, pickup) is not {} returnLeg)return null;
            meters += returnLeg.Meters;
            seconds += returnLeg.Seconds + profile.OperationSeconds;
            if (step != DispatchTaskStepType.EnterDumpingPosition)seconds += options.ExpectedDumperWaitSeconds;
        }
        DispatchCandidateEnergy? best = null;
        foreach (var site in charging.Sites.Where(site => site.Commissioned &&
            site.AllowedVehicleIds.Contains(vehicleId, StringComparer.OrdinalIgnoreCase)))
        {
            var approach = MapRouteSiteMapper.IsSameNode(map.Definition, pickup, site.ChargingSiteId) ? (0d, 0d) :
                Measure(routes.PlanChargingApproachForComparison(pickup, site.ChargingSiteId));
            if (approach is not {} charger)continue;
            var forecast = Calculate(battery, meters, seconds, charger.Item1, charger.Item2, site.Id, profile, options);
            if (best is null || forecast.AtChargerBatteryPercent > best.AtChargerBatteryPercent)best = forecast;
        }
        return best;

        (double Meters, double Seconds)? Between(string source, string destination) =>
            MapRouteSiteMapper.IsSameNode(map.Definition, source, destination) ? (0, 0) :
                Measure(routes.PlanWithoutVehicleOccupancyForComparison(source, destination));

        (double Meters, double Seconds)? Measure(RoutePlan? route)
        {
            if (route is null || route.EdgeIds.Count == 0)return null;
            double distance = 0, time = 0;
            foreach (var id in route.EdgeIds)
            {
                if (!map.Graph.Edges.TryGetValue(id, out var edge) || edge.SpeedLimit <= 0)return null;
                distance += edge.Distance;
                time += edge.Distance / edge.SpeedLimit;
            }
            return (distance, time);
        }
    }

    public static DispatchCandidateEnergy Calculate(decimal battery, double businessMeters, double businessSeconds,
        double chargerMeters, double chargerSeconds, string chargerId, DispatchEnergyProfile profile,
        DispatchEnergyOptions options)
    {
        var businessCost = Cost(businessMeters, businessSeconds);
        var chargerCost = Cost(chargerMeters, chargerSeconds);
        var finish = (double)battery - businessCost;
        var arrival = finish - chargerCost;
        return new((double)battery, businessCost, finish, arrival, chargerId,
            options.PredictionErrorPercent, options.MinimumArrivalBatteryPercent,
            arrival - options.PredictionErrorPercent >= options.MinimumArrivalBatteryPercent);

        double Cost(double meters, double seconds) =>
            meters * profile.TravelCostPercentPerMeter + seconds * profile.IdleCostPercentPerHour / 3600;
    }
}
