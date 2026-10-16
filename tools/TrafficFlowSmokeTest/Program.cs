using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

var now = DateTimeOffset.Now;
var agvOptions = new AgvOptions
{
    Enabled = true,
    Robots =
    [
        new AgvRobotOptions { VehicleId = "AGV-01", DisplayName = "一号车", Enabled = true },
        new AgvRobotOptions { VehicleId = "AGV-02", DisplayName = "二号车", Enabled = true }
    ]
};
var states = new AgvStateCache(agvOptions);
Apply(states, "AGV-01", 0.3, now, true);
Apply(states, "AGV-02", 0.7, now, true);

var definition = new MapDefinition(
    "TrafficTest", "smoke", "LocalCartesian", "m",
    [
        new MapNode("A", 0, 0, MapNodeType.Station, true, []),
        new MapNode("B", 1, 0, MapNodeType.Station, true, [])
    ],
    [new MapEdge("AB", "A", "B", 1, 1, true, true, ["NARROW-01"])],
    [new TrafficResourceDefinition("NARROW-01", TrafficResourceType.NarrowAisle)]);
var runtime = new ActiveMapRuntime();
runtime.Replace(new ActiveMapRuntimeSnapshot("MAP-1", "VER-1", definition, now));
var options = new MapOptions
{
    PositionStaleSeconds = 60,
    MaximumRouteDeviationMeters = 0.75,
    MinimumLocalizationConfidence = 0.5,
    CongestionVehicleThreshold = 2,
    StationaryDistanceMeters = 0.05,
    CongestionStationarySeconds = 1,
    DeadlockDetectionSeconds = 2,
    OccupiedEdgePenalty = 5,
    CongestedEdgePenalty = 30,
    StalledEdgePenalty = 120
};
var monitor = new TrafficFlowMonitor(
    NullLogger<TrafficFlowMonitor>.Instance, options, states, runtime);

var initial = monitor.EvaluateOnce(now);
Require(initial.Edges.Single().State == EdgeTrafficState.Congested,
    "两台刚进入同一路段的车辆应先判为拥堵，而不是立即死锁。");
var stalled = monitor.EvaluateOnce(now.AddSeconds(1.1));
Require(stalled.Edges.Single().State == EdgeTrafficState.Stalled && stalled.Deadlocks.Count == 0,
    "有任务车辆持续不动应进入滞留状态，但尚未达到死锁持续时间。");
var deadlock = monitor.EvaluateOnce(now.AddSeconds(3.2));
Require(deadlock.Deadlocks.Count == 1 &&
        deadlock.Deadlocks[0].VehicleIds.SequenceEqual(new[] { "AGV-01", "AGV-02" }),
    "多车滞留超过阈值应形成稳定的潜在死锁候选。");

Apply(states, "AGV-02", 0.9, now.AddSeconds(3.3), true);
var moved = monitor.EvaluateOnce(now.AddSeconds(3.3));
Require(moved.Deadlocks.Count == 0,
    "任一车辆恢复移动后不得继续保留多车死锁候选。");

Apply(states, "AGV-01", 0.3, now.AddSeconds(4), false);
Apply(states, "AGV-02", 0.9, now.AddSeconds(4), false);
var parked = monitor.EvaluateOnce(now.AddSeconds(10));
Require(parked.Edges.Single().State == EdgeTrafficState.Congested && parked.Deadlocks.Count == 0,
    "没有任务且未上报阻挡的停车车辆不能被误判为滞留死锁。");

Console.WriteLine("PASS: Dynamic edge occupancy + congestion + stalled detection + deadlock suggestion + false-positive guard");

static void Apply(AgvStateCache states, string vehicleId, double x, DateTimeOffset readAt, bool hasTask)
{
    states.ApplyTelemetry(vehicleId, new AgvRobotTelemetry(
        x, 0, 0, 0.99, null, null, 80, false, false,
        hasTask ? "running" : "idle", "A", "B", hasTask ? 0.5 : null,
        hasTask ? [new SeerTaskSnapshot($"TASK-{vehicleId}", 1, null)] : [],
        [], readAt), TimeSpan.FromMilliseconds(5));
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
