using System.Reflection;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

internal static class DepartureReleaseChecks
{
    public static void Run(Action<bool, string> check)
    {
        var definition = new MapDefinition("departure", "synthetic", "local", "m",
            [new("DUMP", 5, 0, MapNodeType.Station, true, [], "LM-DUMP"),
             new("PICKUP", 0, 0, MapNodeType.Station, true, [], "LM-PICKUP")],
            [new("RETURN", "DUMP", "PICKUP", 5, 1, false, true, [])], []);
        var runtime = new ActiveMapRuntimeSnapshot("M", "V", definition, DateTimeOffset.UtcNow);
        var cache = new AgvStateCache(new AgvOptions
        {
            Enabled = true, Robots = [new() { VehicleId = "CAR", Enabled = true }]
        });
        var dispatch = new DispatchOrchestratorOptions
        {
            DumperDumpingSite = "LM-DUMP", StationPickupSites = new() { ["CNC"] = "LM-PICKUP" }
        };
        var handshake = new HandshakeExecutionOptions
        {
            DumperExit = new() { BodyProfiles = [new("BODY", "CAR", .5, .05, true, true)] }
        };
        var worker = new WaitingBayDepartureReleaseWorker(
            NullLogger<WaitingBayDepartureReleaseWorker>.Instance, null!, cache, null!,
            new ReturnPlanner(definition), dispatch, handshake, new() { MinimumLocalizationConfidence = .5 }, null!, null!);
        var method = typeof(WaitingBayDepartureReleaseWorker).GetMethod("CanRelease", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = new ActiveDispatchTask("TASK", "CNC", "CAR", "VENDOR", DispatchTaskStage.Dumping,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var basis = new AgvRobotTelemetry(5, 0, 0, .8, "LM-DUMP", null, 80, false, false,
            null, null, null, null, [], [], DateTimeOffset.UtcNow)
            { VelocityX = 0, VelocityY = 0, AngularVelocity = 0 };
        bool Assess(AgvRobotTelemetry telemetry, out string reason)
        {
            cache.ApplyTelemetry("CAR", telemetry, TimeSpan.FromMilliseconds(1));
            object?[] args = [task, runtime, new HashSet<string> { "WAIT" }, null];
            var result = (bool)method.Invoke(worker, args)!;
            reason = (string)args[3]!;
            return result;
        }
        var accepted = Assess(basis, out var initialReason);
        check(accepted, "静止到达倒料点且定位有效时允许核验候车位离位: " + initialReason);
        foreach (var station in new string?[] { null, "", " ", "\t" })
            check(!Assess(basis with { CurrentStation = station }, out var reason) && reason.Contains("尚未停到倒料点"),
                "行进中站点缺失返回正常等待，不抛异常、不推断离位");
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            check(!Assess(basis with { X = bad }, out _), "非有限坐标不得作为候车位离位证据");
            check(!Assess(basis with { VelocityX = bad }, out _), "非有限速度不得作为停车证据");
            check(!Assess(basis with { AngularVelocity = bad }, out _), "非有限角速度不得作为停车证据");
        }
        foreach (var confidence in new double?[] { null, double.NaN, double.PositiveInfinity, .49, 1.1 })
            check(!Assess(basis with { LocalizationConfidence = confidence }, out var reason) && reason.Contains("置信度"),
                "缺失、异常或低置信度保留候车位占用");
        check(!Assess(basis with { VelocityX = .1 }, out _), "车辆仍在运动时保留候车位占用");
        check(!Assess(basis with { ReadAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, out _), "过期定位不释放候车位");
        check(Assess(basis with { ReadAt = DateTimeOffset.UtcNow }, out _), "恢复有效定位后能正常完成离位核验");
    }

    private sealed class ReturnPlanner(MapDefinition definition) : IRoutePlanner
    {
        public RoutePlan? Plan(string source, string destination, IReadOnlySet<string> unavailable) =>
            definition.ToGraph().FindShortestTransportRoute(
                MapRouteSiteMapper.ResolveLogicalNodeId(definition, source),
                MapRouteSiteMapper.ResolveLogicalNodeId(definition, destination), unavailable);
    }
}
