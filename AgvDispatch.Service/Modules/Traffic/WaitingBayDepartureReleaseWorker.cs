using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;

namespace AgvDispatch.Service;

/// <summary>
/// Releases only a physically vacated independent waiting bay. The vehicle must
/// already be stopped at the configured dumping station with fresh healthy
/// telemetry, and its return route must not re-enter any waiting-bay resource.
/// </summary>
public sealed class WaitingBayDepartureReleaseWorker(
    ILogger<WaitingBayDepartureReleaseWorker> logger,
    IActiveTaskLookup tasks,
    IAgvStateCache vehicles,
    IActiveMapRuntime map,
    IRoutePlanner routePlanner,
    DispatchOrchestratorOptions dispatch,
    HandshakeExecutionOptions handshake,
    MapOptions mapOptions,
    TrafficResourceManager resources,
    IMapExecutionGate gate) : IsolatedBackgroundService
{
    private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!dispatch.Enabled || !handshake.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var execution = await gate.EnterAsync(MapExecutionOperation.ExistingWork, stoppingToken);
                if (execution is null || map.Current is not { } current) continue;
                var waiting = DumperWaitingBaySafety.ExclusiveResourceIds(current.Definition, dispatch);
                if (waiting.Count == 0) continue;
                var atDump = (await tasks.GetActiveTasksAsync(stoppingToken))
                    .Where(task => task.Stage == DispatchTaskStage.Dumping)
                    .ToArray();
                foreach (var task in atDump)
                {
                    if (CanRelease(task, current, waiting, out var reason) is false)
                    {
                        if (_blocked.Add(task.TaskId))
                            logger.LogInformation("候车位暂不释放。TaskId={TaskId}, Reason={Reason}", task.TaskId, reason);
                        continue;
                    }
                    var released = await resources.ReleaseDepartedWaitingBaysAsync(
                        task.TaskId,
                        waiting,
                        "车辆已确认静止到达倒料点，返程路线不再进入候车位；仅释放已离位的独立候车资源。",
                        stoppingToken);
                    if (released > 0)
                    {
                        _blocked.Remove(task.TaskId);
                        logger.LogInformation(
                            "车辆已离开候车位，释放独立候车资源。TaskId={TaskId}, VehicleId={VehicleId}, ReleasedLeases={Released}",
                            task.TaskId,
                            task.VehicleId,
                            released);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "候车位离位释放检查失败，原资源保持占用。");
            }
        }
    }

    private bool CanRelease(ActiveDispatchTask task, ActiveMapRuntimeSnapshot current,
        IReadOnlySet<string> waitingResources, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(task.VehicleId)) return Fail("任务缺少车辆", out reason);
        if (!vehicles.TryGet(task.VehicleId, out var vehicle)) return Fail("缺少车辆实时状态", out reason);
        if (!Fresh(vehicle) || vehicle.Connection != ConnectionState.Online || vehicle.Quality != SignalQuality.Good)
            return Fail("车辆通信或数据质量无效", out reason);
        if (vehicle.Blocked != false || vehicle.EmergencyStopped != false ||
            vehicle.Alarms.Any(alarm => alarm.Severity is AgvAlarmSeverity.Error or AgvAlarmSeverity.Fatal))
            return Fail("车辆阻挡、急停或存在活动报警", out reason);
        if (vehicle.CurrentStation is null ||
            MapRouteSiteMapper.ResolveLogicalNodeId(current.Definition, vehicle.CurrentStation) !=
            MapRouteSiteMapper.ResolveLogicalNodeId(current.Definition, dispatch.DumperDumpingSite))
            return Fail("车辆尚未停到倒料点", out reason);
        if (vehicle.X is not { } x || vehicle.Y is not { } y ||
            vehicle.VelocityX is not { } vx || vehicle.VelocityY is not { } vy ||
            vehicle.AngularVelocity is not { } va ||
            Math.Abs(vx) > .02 || Math.Abs(vy) > .02 || Math.Abs(va) > .02)
            return Fail("车辆位置或静止状态无效", out reason);
        var dumping = Resolve(current.Definition, dispatch.DumperDumpingSite);
        if (dumping is null || Math.Sqrt(Math.Pow(x - dumping.X, 2) + Math.Pow(y - dumping.Y, 2)) > .3)
            return Fail("车辆坐标未落在倒料点核验范围", out reason);
        var pickup = dispatch.GetPickupSite(task.StationId);
        var returnRoute = pickup is null ? null : routePlanner.Plan(
            dispatch.DumperDumpingSite,
            pickup,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (returnRoute is null || returnRoute.ResourceIds.Any(waitingResources.Contains))
            return Fail("返程不可规划或后续路线会重新进入候车位", out reason);
        var profiles = handshake.DumperExit.BodyProfiles.Where(profile =>
            profile.VehicleId.Equals(task.VehicleId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (profiles.Length != 1 || !profiles[0].CalibrationVerified ||
            !profiles[0].IncludesMaximumPermittedLoad)
            return Fail("缺少唯一有效的整车及最大载箱包络", out reason);
        return true;
    }

    private bool Fresh(AgvRobotSnapshot vehicle) =>
        vehicle.LastSuccessfulPollAt is { } at &&
        at <= DateTimeOffset.UtcNow.AddSeconds(1) &&
        DateTimeOffset.UtcNow - at <= TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds);

    private static MapNode? Resolve(MapDefinition definition, string site) =>
        definition.Nodes.Where(node => node.Enabled &&
            (node.Id.Equals(site, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.AgvSiteId, site, StringComparison.OrdinalIgnoreCase))).SingleOrDefault();

    private static bool Fail(string value, out string reason)
    {
        reason = value;
        return false;
    }
}
