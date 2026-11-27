using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Application.Runtime;

namespace AgvDispatch.Service;

public sealed class TrafficFlowMonitor(
    ILogger<TrafficFlowMonitor> logger,
    MapOptions options,
    IAgvStateCache agvStates,
    IActiveMapRuntime activeMap, TimeProvider? clock = null) : IsolatedBackgroundService, ITrafficFlowMonitor
{
    private readonly TimeProvider _clock = clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider;
    private readonly object _sync = new();
    private readonly Dictionary<string, VehicleMotion> _motions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EdgeStateMemory> _edgeStates =
        new(StringComparer.OrdinalIgnoreCase);
    private TrafficFlowSnapshot _snapshot = new(DateTimeOffset.MinValue, null, null, [], []);
    private string? _runtimeVersionId;

    public TrafficFlowSnapshot GetSnapshot()
    {
        lock (_sync) return _snapshot;
    }

    public IReadOnlyDictionary<string, double> GetEdgePenalties()
    {
        lock (_sync)
        {
            return _snapshot.Edges
                .Where(edge => edge.DynamicPenalty > 0)
                .ToDictionary(edge => edge.EdgeId, edge => edge.DynamicPenalty,
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "动态交通监控启动。周期={Interval}ms，拥堵车辆阈值={Threshold}，滞留={Stalled}s，死锁={Deadlock}s",
            options.TrafficMonitorIntervalMs,
            options.CongestionVehicleThreshold,
            options.CongestionStationarySeconds,
            options.DeadlockDetectionSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                EvaluateOnce();
                await Task.Delay(_clock is SimulationTimeProvider simulation
                    ? simulation.ScaleForBusiness(TimeSpan.FromMilliseconds(options.TrafficMonitorIntervalMs))
                    : TimeSpan.FromMilliseconds(options.TrafficMonitorIntervalMs), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "动态交通监控计算失败；保留上一份快照。");
                await Task.Delay(_clock is SimulationTimeProvider simulation
                    ? simulation.ScaleForBusiness(TimeSpan.FromMilliseconds(options.TrafficMonitorIntervalMs))
                    : TimeSpan.FromMilliseconds(options.TrafficMonitorIntervalMs), stoppingToken);
            }
        }
    }

    public TrafficFlowSnapshot EvaluateOnce(DateTimeOffset? evaluatedAt = null)
    {
        var now = evaluatedAt ?? _clock.GetLocalNow();
        var runtime = activeMap.Current;
        if (runtime is null)
        {
            _runtimeVersionId = null;
            _motions.Clear();
            _edgeStates.Clear();
            var empty = new TrafficFlowSnapshot(now, null, null, [], []);
            lock (_sync) _snapshot = empty;
            return empty;
        }
        if (!string.Equals(_runtimeVersionId, runtime.VersionId, StringComparison.OrdinalIgnoreCase))
        {
            _runtimeVersionId = runtime.VersionId;
            _motions.Clear();
            _edgeStates.Clear();
        }

        var occupants = new Dictionary<string, List<AgvRobotSnapshot>>(StringComparer.OrdinalIgnoreCase);
        foreach (var agv in agvStates.GetAll())
        {
            var position = AgvMapPositionAnalyzer.Analyze(
                agv.X, agv.Y, agv.LocalizationConfidence, agv.LastSuccessfulPollAt,
                runtime.Definition, now, TimeSpan.FromSeconds(options.PositionStaleSeconds),
                options.MinimumLocalizationConfidence, options.MaximumRouteDeviationMeters);
            if (position.State != AgvMapPositionState.Normal || position.NearestEdgeId is null)
            {
                _motions.Remove(agv.VehicleId);
                continue;
            }
            UpdateMotion(agv, now);
            if (!occupants.TryGetValue(position.NearestEdgeId, out var vehicles))
                occupants[position.NearestEdgeId] = vehicles = [];
            vehicles.Add(agv);
        }

        var edges = new List<EdgeTrafficSnapshot>();
        var deadlocks = new List<TrafficDeadlockCandidate>();
        foreach (var edge in runtime.Definition.Edges.Where(edge => edge.Enabled))
        {
            var vehicles = occupants.GetValueOrDefault(edge.Id) ?? [];
            var stalled = vehicles.Where(vehicle => IsStalled(vehicle, now)).ToArray();
            var state = stalled.Length > 0
                ? EdgeTrafficState.Stalled
                : vehicles.Count >= options.CongestionVehicleThreshold
                    ? EdgeTrafficState.Congested
                    : vehicles.Count > 0 ? EdgeTrafficState.Occupied : EdgeTrafficState.Free;
            if (!_edgeStates.TryGetValue(edge.Id, out var memory) || memory.State != state)
                _edgeStates[edge.Id] = memory = new EdgeStateMemory(state, now);
            var penalty = state switch
            {
                EdgeTrafficState.Occupied => options.OccupiedEdgePenalty,
                EdgeTrafficState.Congested => options.CongestedEdgePenalty,
                EdgeTrafficState.Stalled => options.StalledEdgePenalty,
                _ => 0
            };
            var vehicleIds = vehicles.Select(vehicle => vehicle.VehicleId)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
            edges.Add(new EdgeTrafficSnapshot(
                edge.Id, edge.From, edge.To, state, vehicleIds, edge.ResourceIds,
                memory.Since, penalty, BuildMessage(state, vehicleIds, stalled)));
            if (state == EdgeTrafficState.Stalled && stalled.Length >= 2 &&
                now - memory.Since >= TimeSpan.FromSeconds(options.DeadlockDetectionSeconds))
            {
                deadlocks.Add(new TrafficDeadlockCandidate(
                    $"DEADLOCK:{runtime.VersionId}:{edge.Id}", edge.Id,
                    stalled.Select(vehicle => vehicle.VehicleId).OrderBy(id => id).ToArray(),
                    edge.ResourceIds, memory.Since,
                    $"同一路段存在{stalled.Length}台有任务或阻挡车辆持续不动。",
                    "保持现有路权；现场确认车辆朝向和间距，优先暂停后进入车辆。任务终止且车辆已离开后，由授权操作员填写原因并确认解锁。"));
            }
        }
        var result = new TrafficFlowSnapshot(
            now, runtime.MapId, runtime.VersionId, edges, deadlocks);
        lock (_sync) _snapshot = result;
        return result;
    }

    private void UpdateMotion(AgvRobotSnapshot agv, DateTimeOffset now)
    {
        if (agv.X is null || agv.Y is null || !double.IsFinite(agv.X.Value) || !double.IsFinite(agv.Y.Value))
            return;
        if (!_motions.TryGetValue(agv.VehicleId, out var motion))
        {
            _motions[agv.VehicleId] = new VehicleMotion(agv.X.Value, agv.Y.Value, now);
            return;
        }
        var distance = Math.Sqrt(Math.Pow(agv.X.Value - motion.X, 2) + Math.Pow(agv.Y.Value - motion.Y, 2));
        if (distance >= options.StationaryDistanceMeters)
            _motions[agv.VehicleId] = new VehicleMotion(agv.X.Value, agv.Y.Value, now);
    }

    private bool IsStalled(AgvRobotSnapshot agv, DateTimeOffset now)
    {
        if (!_motions.TryGetValue(agv.VehicleId, out var motion)) return false;
        var expectedToMove = agv.Blocked == true || agv.Tasks.Count > 0;
        return expectedToMove && now - motion.LastMovedAt >=
            TimeSpan.FromSeconds(options.CongestionStationarySeconds);
    }

    private static string BuildMessage(
        EdgeTrafficState state,
        IReadOnlyList<string> vehicleIds,
        IReadOnlyList<AgvRobotSnapshot> stalled) => state switch
    {
        EdgeTrafficState.Free => "路段空闲。",
        EdgeTrafficState.Occupied => $"车辆{string.Join(',', vehicleIds)}正在占用。",
        EdgeTrafficState.Congested => $"车辆{string.Join(',', vehicleIds)}集中在该路段。",
        EdgeTrafficState.Stalled => $"车辆{string.Join(',', stalled.Select(vehicle => vehicle.VehicleId))}持续不动。",
        _ => state.ToString()
    };

    private sealed record VehicleMotion(double X, double Y, DateTimeOffset LastMovedAt);
    private sealed record EdgeStateMemory(EdgeTrafficState State, DateTimeOffset Since);
}
