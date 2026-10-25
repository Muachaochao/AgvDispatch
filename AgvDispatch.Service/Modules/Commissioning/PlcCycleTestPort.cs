using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

/// <summary>Read and write boundary for the isolated PLC cycle test. It exposes only
/// cached PLC values, observed vehicle state and one-use route previews.</summary>
public sealed class PlcCycleTestPort(
    PlcCycleTestOptions options,
    IPlcSignalCache signals,
    IAgvStateCache vehicles,
    IActiveMapRuntime map,
    IMapExecutionGate execution,
    IPlcGateway plc,
    ISingleNavigationTestService navigation,
    IRobokitTaskMapPreflight preflight,
    bool simulatorOnly,
    bool businessExecutionDisabled) : IPlcCycleTestPort
{
    private static bool Stopped(AgvRobotSnapshot vehicle) =>
        vehicle.VelocityX is { } vx && Math.Abs(vx) < 0.02 &&
        vehicle.VelocityY is { } vy && Math.Abs(vy) < 0.02 &&
        vehicle.AngularVelocity is { } va && Math.Abs(va) < 0.02 &&
        vehicle.Tasks.All(task => task.Status is 4 or 5 or 6 or 7);

    public async Task<PlcCycleTestEnvironment> ObserveAsync(CancellationToken ct)
    {
        if (!vehicles.TryGet(options.VehicleId, out var vehicle))
            throw new InvalidOperationException($"未取得测试车辆 {options.VehicleId} 的实时状态。");
        var stationMap = await navigation.ReadStationsAsync(ct);
        var jackStatus = await navigation.ReadJackAsync(ct);
        var jack = jackStatus.Telemetry;
        var signalRows = signals.GetAll().Select(signal => new PlcCycleTestSignal(
            signal.StationId, signal.VariableName, signal.Value, signal.Quality.ToString(), signal.UpdatedAt)).ToArray();
        var times = new List<DateTimeOffset>();
        if (vehicle.LastSuccessfulPollAt is { } vehicleTime) times.Add(vehicleTime);
        if (jack?.ObservedAt is { } jackTime) times.Add(jackTime);
        times.AddRange(signalRows.Where(signal => signal.ObservedAt is not null).Select(signal => signal.ObservedAt!.Value));
        var observedAt = times.Count == 0 ? DateTimeOffset.MinValue : times.Min();
        var currentMap = map.Current;
        var healthyAlarms = vehicle.Alarms.All(alarm =>
            alarm.Severity is not (AgvAlarmSeverity.Error or AgvAlarmSeverity.Fatal));
        return new(
            simulatorOnly,
            businessExecutionDisabled,
            execution.Mode == MapExecutionMode.Running,
            currentMap?.MapId ?? "",
            currentMap?.VersionId ?? "",
            vehicle.VehicleId,
            vehicle.CurrentStation,
            vehicle.Connection == ConnectionState.Online && vehicle.Quality == SignalQuality.Good,
            Stopped(vehicle),
            jack is { State: 3, HasLoad: false },
            vehicle.LocalizationConfidence is >= 0.5 &&
                vehicle.LastSuccessfulPollAt is { } localizedAt &&
                DateTimeOffset.UtcNow - localizedAt <= TimeSpan.FromSeconds(options.MaximumAgeSeconds),
            vehicle.Blocked == false && vehicle.EmergencyStopped == false && healthyAlarms,
            observedAt,
            signalRows)
        {
            RobotMap = stationMap.RobotMap,
            RobotMapMd5 = stationMap.RobotMapMd5
        };
    }

    public async Task<PlcCycleTestRouteProof> CheckRoutesAsync(PlcCycleTestRouteRequest request, CancellationToken ct)
    {
        var stations = await navigation.ReadStationsAsync(ct);
        var runtimeMap = map.Current ??
            throw new InvalidOperationException("PLC空载联调缺少运行地图。");
        var mapDefinition = runtimeMap.Definition ??
            throw new InvalidOperationException("PLC空载联调缺少运行地图。");
        var pairs = new[] {
            (request.CurrentStation, request.PickupSite),
            (request.PickupSite, request.WaitingSite),
            (request.WaitingSite, request.DumpingSite),
            (request.DumpingSite, request.PickupSite)
        }.Where(pair => pair.Item1 != pair.Item2)
         .Distinct()
         .ToArray();
        var routes = new List<PlcCycleTestRoute>();
        foreach (var pair in pairs)
        {
            MapNode Resolve(string site)
            {
                var nodes = mapDefinition.Nodes.Where(node => node.Enabled &&
                    (node.Id == site || node.AgvSiteId == site)).ToArray();
                return nodes.Length == 1 ? nodes[0]
                    : throw new InvalidOperationException($"站点 {site} 的启用地图映射不是唯一项。");
            }
            var source = Resolve(pair.Item1);
            var target = Resolve(pair.Item2);
            var path = runtimeMap.Graph.FindShortestRoute(source.Id, target.Id) ??
                throw new InvalidOperationException($"运行地图路线不可达：{pair.Item1} → {pair.Item2}。");
            var vendorPath = path.Nodes.Select(id =>
            {
                var node = mapDefinition.Nodes.Single(item => item.Id == id);
                if (string.IsNullOrWhiteSpace(node.AgvSiteId))
                    throw new InvalidOperationException($"路线节点 {id} 缺少厂家站点映射。");
                return node.AgvSiteId;
            }).ToArray();
            var checkedRoute = await preflight.CheckRouteAsync(request.VehicleId, vendorPath, ct);
            if (!checkedRoute.Accepted)
                throw new InvalidOperationException($"车端路线核验失败：{checkedRoute.Message}");
            routes.Add(new(pair.Item1, pair.Item2, vendorPath));
        }
        return new(stations.RobotMap, stations.RobotMapMd5, routes, DateTimeOffset.UtcNow);
    }

    public Task WriteAsync(string stationId, string signalName, bool value, CancellationToken ct) =>
        plc.WriteSignalAsync(stationId, signalName, value, ct);

    public static bool AllPlcsAreLoopback(IEnumerable<AgvDispatch.Connectors.Plc.PlcOptions> connections)
    {
        var enabled = connections.Where(connection => connection.Enabled).ToArray();
        return enabled.Length == 1 && enabled[0].IpAddress == "127.0.0.1" && enabled[0].Port == 10200;
    }
}
