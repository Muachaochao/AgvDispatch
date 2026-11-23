using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using SqlSugar;

namespace AgvDispatch.Service;

public sealed record CompletedTransportRouteReleaseResult(int ReleasedCount, string Reason);

/// <summary>
/// Closes the gap between "the AGV navigation step completed" and "the next
/// transport step acquired its route". The completed step is authoritative
/// arrival evidence. Named non-destination leases are released only when the
/// freshly observed complete vehicle envelope is outside their map boundary.
/// </summary>
public sealed class CompletedTransportRouteReleaseService(
    ISqlSugarClient database,
    IActiveMapRuntime activeMap,
    IAgvStateCache vehicles,
    ITrafficBodyEnvelopeProvider envelopes,
    HandshakeExecutionOptions handshake,
    MapOptions mapOptions,
    TrafficSegmentReleasePolicy releasePolicy,
    TrafficResourceManager resources,
    TimeProvider? clock = null, DispatchTrafficWakeup? trafficWakeup = null,
    ILogger<CompletedTransportRouteReleaseService>? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Vehicles already reported as lacking a verified body envelope (warned once per vehicle).
    private static readonly HashSet<string> EnvelopeWarnings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<DispatchTaskStepType> TransportSteps =
    [
        DispatchTaskStepType.GoToPickup,
        DispatchTaskStepType.DeliverFullBoxToDumper,
        DispatchTaskStepType.EnterDumpingPosition,
        DispatchTaskStepType.ReturnEmptyBox
    ];

    public async Task<CompletedTransportRouteReleaseResult> ReleaseAsync(
        string taskId, string stepId, CancellationToken ct)
        => await ReleaseCoreAsync(taskId, stepId, null, ct);

    // The business step remains Executing. Only the original vendor window has
    // completed; this never marks pickup, dumping, return or PLC work complete.
    public Task<CompletedTransportRouteReleaseResult> ReleaseWindowAsync(
        string taskId, string stepId, AgvTransportTaskStatus terminal, CancellationToken ct)
        => ReleaseCoreAsync(taskId, stepId, terminal, ct);

    private async Task<CompletedTransportRouteReleaseResult> ReleaseCoreAsync(
        string taskId, string stepId, AgvTransportTaskStatus? terminal, CancellationToken ct)
    {
        var windowBoundary = terminal is not null;
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(stepId))
            return new(0, "缺少原任务或已完成运输步骤。");
        var map = activeMap.Current;
        if (map is null) return new(0, "没有活动地图，保留已完成步骤路权。");
        var step = await database.Queryable<DispatchTaskStepEntity>()
            .Where(item => item.TaskId == taskId && item.StepId == stepId).FirstAsync(ct);
        var task = await database.Queryable<DispatchTaskEntity>()
            .Where(item => item.TaskId == taskId).FirstAsync(ct);
        var command = await database.Queryable<AgvCommandOutboxEntity>()
            .Where(item => item.TaskId == taskId && item.StepId == stepId &&
                item.CommandType == (int)AgvCommandType.Transport &&
                item.Status == (int)AgvCommandStatus.Sent)
            .OrderByDescending(item => item.CreatedAt).FirstAsync(ct);
        var request = await database.Queryable<TrafficRequestEntity>()
            .Where(item => item.TaskId == taskId && item.StepId == stepId).FirstAsync(ct);
        var leases = await database.Queryable<TrafficResourceLeaseEntity>()
            .Where(item => item.TaskId == taskId && item.OwnerStepId == stepId).ToListAsync(ct);
        if (step is null || task is null || command is null || request is null || leases.Count == 0)
            return new(0, "已完成步骤没有可释放的原路线租约。");
        if (step.Status != (int)(windowBoundary ? DispatchTaskStepStatus.Executing : DispatchTaskStepStatus.Completed) ||
            !TransportSteps.Contains((DispatchTaskStepType)step.StepType))
            return new(0, "原步骤尚未确认运输完成。");
        var expectedStage = (DispatchTaskStepType)step.StepType switch
        {
            DispatchTaskStepType.GoToPickup => DispatchTaskStage.PickingUp,
            DispatchTaskStepType.DeliverFullBoxToDumper => DispatchTaskStage.WaitingForDumper,
            DispatchTaskStepType.EnterDumpingPosition => DispatchTaskStage.Dumping,
            DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStage.WaitingForPlcConfirmation,
            _ => (DispatchTaskStage)(-1)
        };
        if (windowBoundary) expectedStage = (DispatchTaskStepType)step.StepType switch
        {
            DispatchTaskStepType.GoToPickup => DispatchTaskStage.GoingToPickup,
            DispatchTaskStepType.DeliverFullBoxToDumper => DispatchTaskStage.GoingToDumper,
            DispatchTaskStepType.EnterDumpingPosition => DispatchTaskStage.Dumping,
            DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStage.ReturningEmptyBox,
            _ => (DispatchTaskStage)(-1)
        };
        if (task.Stage != (int)expectedStage || task.CancelRequestId is not null ||
            command.Status != (int)AgvCommandStatus.Sent || command.SentAt is null ||
            string.IsNullOrWhiteSpace(step.VehicleId) || task.VehicleId != step.VehicleId ||
            command.VehicleId != step.VehicleId || request.VehicleId != step.VehicleId ||
            command.ExecutionMapId != map.MapId || command.ExecutionMapVersionId != map.VersionId ||
            request.MapId != map.MapId || request.MapVersionId != map.VersionId)
            return new(0, "原任务、步骤、门禁或地图版本不再是可安全释放的到站状态。");

        var payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(
            command.PayloadJson, JsonOptions);
        if (windowBoundary && (terminal!.State != AgvTransportTaskState.Completed ||
            terminal.VendorTaskId != command.VendorCommandId || terminal.VehicleId != step.VehicleId ||
            task.VendorTaskId != command.VendorCommandId || step.VendorTaskId != command.VendorCommandId ||
            terminal.UpdatedAt > (clock ?? TimeProvider.System).GetUtcNow().AddSeconds(1) ||
            (clock ?? TimeProvider.System).GetUtcNow() - terminal.UpdatedAt > TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds) ||
            payload is null || payload.WindowIndex < 0 || payload.WindowIndex >= payload.Windows.Count - 1))
            return new(0, "当前窗口未取得新鲜的原厂家命令完成证据，保留路权。");
        var businessStepId = payload is null
            ? ""
            : string.IsNullOrWhiteSpace(payload.BusinessStepId) ? payload.BusinessTaskId : payload.BusinessStepId;
        var businessSource = payload?.Windows.Count > 0 ? payload.Windows[0].Source : payload?.Source;
        var businessDestination = payload?.Windows.Count > 0 ? payload.Windows[^1].Destination : payload?.Destination;
        // A replacement command from obstacle recovery carries its own business identity;
        // it is accepted explicitly, while its leases still belong to this step.
        if (payload?.Path is not { Count: >= 2 } ||
            (businessStepId != stepId && !ObstacleRecoveryTaskIds.IsRecoveryTask(businessStepId)) ||
            payload.VehicleId != step.VehicleId ||
            !MapRouteSiteMapper.IsSameNode(map.Definition, step.SourceSite, businessSource) ||
            !MapRouteSiteMapper.IsSameNode(map.Definition, step.DestinationSite, businessDestination))
            return new(0, "原运输载荷与原步骤不一致。");
        var frozen = TransportRouteSnapshots.Read(command);
        // The request describes the newest window asked for, which may already be a waiting next
        // window. The frozen snapshot describes the window that was sent: recapture that window.
        var sentWindow = payload.Windows.Count > 0 && payload.WindowIndex < payload.Windows.Count
            ? payload.Windows[payload.WindowIndex]
            : null;
        var recaptureRequest = sentWindow is null ? request : new TrafficRequestEntity
        {
            StepId = request.StepId, TaskId = request.TaskId, VehicleId = request.VehicleId,
            MapId = request.MapId, MapVersionId = request.MapVersionId, ResourceIds = request.ResourceIds,
            State = request.State is "Acquired" or "Held" or "NotRequired" ? request.State : "Held",
            Source = sentWindow.Source, Destination = sentWindow.Destination,
            EdgeIds = JsonSerializer.Serialize(sentWindow.EdgeIds)
        };
        var recaptured = TransportRouteSnapshots.Capture(taskId, stepId, command.CommandId,
            step.VehicleId, command.PayloadJson, map, recaptureRequest);
        if (TransportRouteSnapshots.Serialize(frozen) != TransportRouteSnapshots.Serialize(recaptured))
            return new(0, "发送时路线快照与当前活动地图不一致。");

        var generatedAt = (clock ?? TimeProvider.System).GetUtcNow();
        if (!vehicles.TryGet(step.VehicleId, out var vehicle) ||
            vehicle.Connection != ConnectionState.Online || vehicle.Quality != SignalQuality.Good ||
            vehicle.LastSuccessfulPollAt is not { } observed ||
            observed > generatedAt.AddSeconds(1) ||
            generatedAt - observed > TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds) ||
            vehicle.Blocked != false || vehicle.EmergencyStopped != false ||
            AgvObstaclePolicy.IsBlocked(vehicle) || AgvObstaclePolicy.HasBlockingAlarm(vehicle) ||
            vehicle.X is not { } x || vehicle.Y is not { } y ||
            vehicle.LocalizationConfidence is not { } confidence ||
            !double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(confidence) ||
            confidence < mapOptions.MinimumLocalizationConfidence || confidence > 1)
            return new(0, "车辆实时状态、定位或安全状态不满足到站释放条件。");

        var destinationMatches = map.Definition.Nodes.Where(node => node.Enabled &&
            MapRouteSiteMapper.IsSameNode(map.Definition, node.Id, windowBoundary ? payload.Destination : frozen.NodeIds[^1]) &&
            MapRouteSiteMapper.IsSameNode(map.Definition,
                node.AgvSiteId ?? node.Id, windowBoundary ? payload.Destination : frozen.AgvSites[^1])).ToArray();
        if (destinationMatches.Length != 1 ||
            !MapRouteSiteMapper.IsSameNode(map.Definition, vehicle.CurrentStation,
                destinationMatches[0].AgvSiteId ?? destinationMatches[0].Id))
            return new(0, "车辆当前站点尚未与已完成运输终点一致。");
        if (windowBoundary && (destinationMatches[0].AllowTrafficStop == false ||
            destinationMatches[0].Type is not (MapNodeType.Station or MapNodeType.WaitingPoint or MapNodeType.ChargingPoint or MapNodeType.Intersection) ||
            double.Hypot(x - destinationMatches[0].X, y - destinationMatches[0].Y) > 0.3 ||
            vehicle.VelocityX is not { } vx || !double.IsFinite(vx) || Math.Abs(vx) > 0.02 ||
            vehicle.VelocityY is not { } vy || !double.IsFinite(vy) || Math.Abs(vy) > 0.02 ||
            vehicle.AngularVelocity is not { } va || !double.IsFinite(va) || Math.Abs(va) > 0.02))
            return new(0, "窗口安全点、实时坐标或停稳证据不满足释放条件。");

        var body = await envelopes.ReadAsync(step.VehicleId, ct);
        var profile = handshake.DumperExit.BodyProfiles.Where(item =>
            item.VehicleId.Equals(step.VehicleId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (body is null || body.VehicleId != step.VehicleId ||
            body.CoordinateFrameId != map.Definition.CoordinateSystem ||
            !body.CalibrationVerified || !body.IncludesMaximumPermittedLoad ||
            !double.IsFinite(body.RadiusFromReportedReferenceMeters) ||
            body.RadiusFromReportedReferenceMeters <= 0 ||
            profile.Length != 1 || !double.IsFinite(profile[0].MaximumPositionUncertaintyMeters) ||
            profile[0].MaximumPositionUncertaintyMeters < 0)
        {
            // Without a verified envelope no geometric release is possible, so automatic corridor
            // guards stay leased until the task completes. Surface that once per vehicle (R10).
            lock (EnvelopeWarnings)
                if (EnvelopeWarnings.Add(step.VehicleId))
                    logger?.LogWarning("车体包络缺失或未核定，已完成运输路段无法几何释放，旧路权将保留到任务结束。VehicleId={VehicleId}",
                        step.VehicleId);
            return new(0, "缺少已核定的完整车体、载荷包络或定位误差界。");
        }

        var destination = destinationMatches[0];
        var destinationResources = destination.ResourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Automatic corridor and node guards exist only in the enriched topology. They are
        // checked against their real polygon or edge geometry and never released as boundaryless.
        var topology = MapCorridorResources.Enrich(map.Definition);
        var definitions = topology.Resources.ToDictionary(item => item.Id,
            StringComparer.OrdinalIgnoreCase);
        var radius = body.RadiusFromReportedReferenceMeters +
            releasePolicy.SafetyMarginMeters + profile[0].MaximumPositionUncertaintyMeters;
        if (!double.IsFinite(radius) || radius <= 0 ||
            !double.IsFinite(releasePolicy.SafetyMarginMeters) || releasePolicy.SafetyMarginMeters < 0.1)
            return new(0, "包络安全余量无效，保留路权。");
        var candidates = new List<TrafficResourceLeaseEntity>();
        var graphNodes = map.Definition.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var lease in leases)
        {
            // Every lease owned by this step is a candidate. A replacement route can leave
            // old-route-only leases that the current request no longer lists.
            if (string.IsNullOrWhiteSpace(lease.LeaseId) ||
                lease.MapId != map.MapId || lease.MapVersionId != map.VersionId ||
                lease.VehicleId != step.VehicleId)
                continue;
            // Polygon resources, including generated AUTO-CORRIDOR-* guards, are released only when
            // the complete body envelope is outside them. Boundaryless resources need edge or node
            // geometry; destination resources always stay protected.
            var hasBoundary = definitions.TryGetValue(lease.ResourceId, out var definition) &&
                definition.Capacity >= 1 && ValidBoundary(definition.Boundary);
            if (hasBoundary)
            {
                if (!Outside(x, y, radius, definition!.Boundary!)) continue;
            }
            else
            {
                // A boundaryless resource listed on the current stop node is the
                // only unconditional protection: without geometry there is no
                // independent proof that the complete body has left it.
                if (destinationResources.Contains(lease.ResourceId)) continue;
                // Generated corridor guards also have no polygon. Use their
                // actual edge geometry and keep unknown geometry conservatively.
                var guarded = topology.Edges.Where(e =>
                    e.ResourceIds.Contains(lease.ResourceId, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (guarded.Length == 0)
                {
                    // B07-FIX K1: node-only stop resources (e.g. SIM-STOP-*) carry no polygon and no edge.
                    // A departed stop is released only when the stopped body is clear of a body-sized
                    // footprint centred on every carrier node (distance > 2 x envelope radius).
                    var carriers = map.Definition.Nodes.Where(n =>
                        n.ResourceIds.Contains(lease.ResourceId, StringComparer.OrdinalIgnoreCase)).ToArray();
                    if (carriers.Length == 0 || carriers.Any(n =>
                    {
                        var d = Math.Sqrt((n.X - x) * (n.X - x) + (n.Y - y) * (n.Y - y));
                        return !double.IsFinite(d) || d <= 2 * radius;
                    })) continue;
                }
                else if (guarded.Any(e =>
                {
                    if (!MapEdgeGeometry.TryValidate(e, graphNodes[e.From], graphNodes[e.To], out _))
                        return true;
                    var distance = MapEdgeGeometry.DistanceTo(e, graphNodes[e.From], graphNodes[e.To], x, y);
                    return !double.IsFinite(distance) || distance <= radius;
                })) continue;
            }
            candidates.Add(lease);
        }
        if (candidates.Count == 0)
            return new(0, "前序路段仍被车体覆盖或只剩终点资源，继续保留。");

        var released = await resources.ReleasePassedLeasesAsync(taskId, stepId, candidates,
            windowBoundary ? "原厂家窗口已完成且车辆在安全点停稳，按完整包络释放已通过的旧窗口资源；当前停点资源及仍被车体覆盖或几何无法核定的下一窗口资源继续保留，原业务步骤未完成。" :
                "车辆已完成原运输并停到明确终点；释放该步骤已走过的全部前序路权，终点资源继续保留。",
            ct);
        if (released > 0) trafficWakeup?.Publish();
        return new(released, released == 0
            ? "并发状态已变化，本轮未删除路权。"
            : $"已释放{released}项已走过的前序路段，终点资源继续保留。");
    }

    private static bool ValidBoundary(IReadOnlyList<MapPoint2D>? polygon) =>
        polygon is { Count: >= 3 } && polygon.All(point =>
            point is not null && double.IsFinite(point.X) && double.IsFinite(point.Y));

    private static bool Outside(double x, double y, double radius,
        IReadOnlyList<MapPoint2D> polygon)
    {
        var point = new MapPoint2D(x, y);
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[j];
            var b = polygon[i];
            if (Distance(point, a, b) <= radius + 1e-8) return false;
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return !inside;
    }

    private static double Distance(MapPoint2D point, MapPoint2D a, MapPoint2D b)
    {
        var x = b.X - a.X;
        var y = b.Y - a.Y;
        var length = x * x + y * y;
        if (length <= 1e-16)
            return Math.Sqrt((point.X - a.X) * (point.X - a.X) +
                (point.Y - a.Y) * (point.Y - a.Y));
        var t = Math.Clamp(((point.X - a.X) * x + (point.Y - a.Y) * y) / length, 0, 1);
        var dx = point.X - a.X - t * x;
        var dy = point.Y - a.Y - t * y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
