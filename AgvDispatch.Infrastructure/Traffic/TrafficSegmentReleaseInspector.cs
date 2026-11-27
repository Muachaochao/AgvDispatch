using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Traffic;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Traffic;

// Read-only: no resource manager or write API is reachable from this inspector.
public sealed class TrafficSegmentReleaseInspector(ISqlSugarClient database, IActiveMapRuntime activeMap,
    IMapExecutionGate gate, TrafficSegmentReleasePolicy policy,
    IAuthoritativeTrafficProgressReader? progress = null, ITrafficBodyEnvelopeProvider? envelopes = null,
    TimeProvider? clock = null) : ITrafficSegmentReleaseInspector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static TrafficSegmentReleaseAssessment Refuse(string reason) => new(false, reason, []);

    // Caller holds ExistingWork gate; no progress collection or release policy evaluation.
    internal async Task<GeometryContext> ReadGeometryContextAsync(string stepId,CancellationToken ct)
    {
        var value=await LoadAsync(stepId,ct);
        return new(value.Context,value.Fingerprint,value.Map);
    }
    internal sealed record GeometryContext(TrafficSegmentReleaseContext Context,string Fingerprint,ActiveMapRuntimeSnapshot Map);

    public Task<TrafficSegmentReleaseAssessment> InspectAsync(string stepId, CancellationToken ct)
        => InspectCoreAsync(stepId, null, ct);

    internal async Task<(CapturedEvidence? Proof, TrafficSegmentReleaseAssessment Assessment)> CaptureAsync(
        string stepId, CancellationToken ct, TrafficProgressFrame? anchor = null)
    {
        CapturedEvidence? captured=null;
        var assessment = await InspectCoreAsync(stepId, value=>captured=value, ct, anchor);
        return (captured, assessment);
    }

    // Must be called under the same execution gate. Issuance is not resource release authorization.
    internal async Task<bool> IsCurrentAsync(CapturedEvidence captured, CancellationToken ct,
        IReadOnlyList<TrafficResourceLeaseEntity>? deleted=null,RequestTransition? transition=null)
    {
        try
        {
            var current=await LoadAsync(captured.Context.StepId,ct,deleted,transition);
            var now=(clock??AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow();
            // Capture already evaluated both frames against this exact route, geometry and policy.
            // Only their freshness can change while the transaction verifies the same snapshot.
            return current.Fingerprint==captured.Fingerprint && ReferenceEquals(current.Map,captured.Map) &&
                captured.PolicyJson==JsonSerializer.Serialize(policy) && now>=captured.AssessedAt &&
                now-captured.Evidence.First.ObservedAt<=TimeSpan.FromSeconds(captured.MaximumAgeSeconds);
        }
        catch(Exception ex) when(ex is InvalidDataException or JsonException or ArgumentException){return false;}
    }

    private async Task<TrafficSegmentReleaseAssessment> InspectCoreAsync(string stepId, Action<CapturedEvidence>? capture,
        CancellationToken ct, TrafficProgressFrame? anchor = null)
    {
        if (!policy.Enabled) return Refuse("逐段释放只读评估未启用。");
        if (progress is null || envelopes is null) return Refuse("缺少权威进度源或核定车辆包络提供器。");
        if (string.IsNullOrWhiteSpace(stepId)) return Refuse("缺少原步骤编号。");
        try
        {
            var policyJson=JsonSerializer.Serialize(policy);
            Snapshot before;
            using (var access = await gate.EnterAsync(MapExecutionOperation.ExistingWork, ct))
            {
                if (access is null) return Refuse("地图或恢复门禁关闭。");
                before = await LoadAsync(stepId, ct);
            }
            // Device reads must not hold the dispatch control gate.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var body = await envelopes.ReadAsync(before.Context.VehicleId, timeout.Token).WaitAsync(timeout.Token);
            var read = anchor is null
                ? progress.ReadAsync(before.Context.VehicleId, timeout.Token)
                : progress.ReadAfterAsync(before.Context.VehicleId, anchor, timeout.Token);
            var evidence = await read.WaitAsync(timeout.Token);
            using (var access = await gate.EnterAsync(MapExecutionOperation.ExistingWork, ct))
            {
                if (access is null) return Refuse("采集期间地图或恢复门禁关闭。");
                var after = await LoadAsync(stepId, ct);
                if (before.Fingerprint != after.Fingerprint || !ReferenceEquals(before.Map, after.Map))
                    return Refuse("采集期间原任务、命令、路线、租约或活动地图发生变化，请重新采集。");
                if(policyJson!=JsonSerializer.Serialize(policy))return Refuse("采集期间评估配置变化，请重新采集。");
                var context=after.Context with { Body = body };
                var assessedAt=(clock??AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow();
                var assessment=TrafficSegmentReleaseEvaluator.Evaluate(context,evidence,policy,assessedAt);
                if(assessment.Available && assessment.Candidates.Any(c=>c.EvidenceVerified) && evidence is not null)
                    capture?.Invoke(new(after.Fingerprint,after.Map,context,evidence,policyJson,assessedAt,
                        policy.MaximumAgeSeconds,assessment.Candidates.Where(c=>c.EvidenceVerified).ToArray(),
                        assessment.Progress));
                return assessment;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return Refuse("证据读取超时，本次检查不可用。"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or ArgumentException or TimeoutException)
        { return Refuse(ex.Message); }
    }

    private async Task<Snapshot> LoadAsync(string stepId, CancellationToken ct,IReadOnlyList<TrafficResourceLeaseEntity>? deleted=null,
        RequestTransition? transition=null)
    {
        var map = activeMap.Current ?? throw new InvalidDataException("无活动地图。");
        var step = await database.Queryable<DispatchTaskStepEntity>().Where(x => x.StepId == stepId).FirstAsync(ct)
            ?? throw new InvalidDataException("原步骤不存在。");
        var task = await database.Queryable<DispatchTaskEntity>().Where(x => x.TaskId == step.TaskId).FirstAsync(ct)
            ?? throw new InvalidDataException("原任务不存在。");
        var commands = await database.Queryable<AgvCommandOutboxEntity>()
            .Where(x => x.StepId == stepId && x.CommandType == (int)AgvCommandType.Transport).ToListAsync(ct);
        var request = await database.Queryable<TrafficRequestEntity>().Where(x => x.StepId == stepId).FirstAsync(ct)
            ?? throw new InvalidDataException("没有原路径请求记录，不能重新规划代替。");
        if(transition is not null)
        {
            if(deleted is not {Count:>0}||transition.StepId!=stepId||request.State!=transition.AfterState||
                request.ResourceIds!=transition.AfterResourceIds||request.Reason!=transition.AfterReason)
                throw new InvalidDataException("本事务路权展示更新不符合预期。");
            // Reconstruct only this transaction's known diagnostic change; all other fields remain authoritative.
            request.State=transition.BeforeState;request.ResourceIds=transition.BeforeResourceIds;
        }
        var leases = await database.Queryable<TrafficResourceLeaseEntity>().Where(x => x.OwnerStepId == stepId).ToListAsync(ct);
        // Transaction-end verification reconstructs only rows this transaction actually deleted.
        if(deleted is {Count:>0})
        {
            var ids=deleted.Select(l=>l.LeaseId).ToArray();
            if(await database.Queryable<TrafficResourceLeaseEntity>().Where(l=>ids.Contains(l.LeaseId)).AnyAsync(ct))
                throw new InvalidDataException("已删原租约仍存在或被重建。");
            leases.AddRange(deleted);
        }
        var matchingCommands = commands.Where(item => item.VendorCommandId == step.VendorTaskId).ToArray();
        if (matchingCommands.Length != 1) throw new InvalidDataException("当前运输窗口命令缺失或不唯一。");
        var command = matchingCommands[0];
        var expectedStage = (DispatchTaskStepType)step.StepType switch
        {
            DispatchTaskStepType.GoToPickup => DispatchTaskStage.GoingToPickup,
            DispatchTaskStepType.DeliverFullBoxToDumper => DispatchTaskStage.GoingToDumper,
            DispatchTaskStepType.EnterDumpingPosition => DispatchTaskStage.Dumping,
            DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStage.ReturningEmptyBox,
            _ => (DispatchTaskStage)(-1)
        };
        if (task.Stage != (int)expectedStage || expectedStage == (DispatchTaskStage)(-1) ||
            step.Status != (int)DispatchTaskStepStatus.Executing || task.CancelRequestId is not null ||
            command.Status != (int)AgvCommandStatus.Sent || command.SentAt is null)
            throw new InvalidDataException("原运输未确认执行或处于取消/异常状态。");
        var vehicle = step.VehicleId;
        if (string.IsNullOrWhiteSpace(vehicle) || task.VehicleId != vehicle || command.VehicleId != vehicle ||
            request.VehicleId != vehicle || command.TaskId != task.TaskId || request.TaskId != task.TaskId ||
            string.IsNullOrWhiteSpace(step.VendorTaskId) || command.VendorCommandId != step.VendorTaskId ||
            task.VendorTaskId != step.VendorTaskId || request.State is not ("Acquired" or "Held"))
            throw new InvalidDataException("原运输编号、任务、车辆或占用状态不一致。");
        if (command.ExecutionMapId != map.MapId || command.ExecutionMapVersionId != map.VersionId ||
            request.MapId != map.MapId || request.MapVersionId != map.VersionId)
            throw new InvalidDataException("原命令/路线的地图版本缺失或不是活动版本。");
        var payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(command.PayloadJson, JsonOptions)
            ?? throw new InvalidDataException("原运输载荷无效。");
        var frozen = TransportRouteSnapshots.Read(command);
        var recaptured = TransportRouteSnapshots.Capture(task.TaskId,stepId,command.CommandId,vehicle,command.PayloadJson,map,request);
        if (TransportRouteSnapshots.Serialize(frozen) != TransportRouteSnapshots.Serialize(recaptured))
            throw new InvalidDataException("活动地图/路径记录与发送时路线快照不一致。");
        var edges = frozen.EdgeIds;
        var businessStepId = string.IsNullOrWhiteSpace(payload.BusinessStepId)
            ? payload.BusinessTaskId
            : payload.BusinessStepId;
        // A replacement command from obstacle recovery carries its own business identity.
        if ((businessStepId != stepId && !ObstacleRecoveryTaskIds.IsRecoveryTask(businessStepId)) ||
            payload.VehicleId != vehicle || payload.Path is null ||
            payload.Path.Count < 2 ||
            payload.Source != payload.Path[0] || payload.Destination != payload.Path[^1] ||
            step.SourceSite is null || step.DestinationSite is null)
            throw new InvalidDataException("原命令路径和步骤不一致。");
        var nodes = frozen.AgvSites.Select(site =>
        {
            var matches = map.Definition.Nodes.Where(n =>
                string.Equals(string.IsNullOrWhiteSpace(n.AgvSiteId) ? n.Id : n.AgvSiteId, site, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("原路径站点无法唯一映射到活动地图。");
            return matches[0].Id;
        }).ToArray();
        if (leases.Count == 0 || leases.Any(l => l.TaskId != task.TaskId || l.VehicleId != vehicle ||
            l.MapId != map.MapId || l.MapVersionId != map.VersionId))
            throw new InvalidDataException("原租约缺失或归属/地图版本不一致。");
        if (!nodes.SequenceEqual(frozen.NodeIds) || frozen.CoordinateFrameId != map.Definition.CoordinateSystem)
            throw new InvalidDataException("发送时逻辑节点/坐标系发生变化。");
        var revision = command.ExecutionRouteSnapshotHash!;
        // SentAt is a conservative lower bound: frames before acceptance are deliberately refused.
        var context = new TrafficSegmentReleaseContext(task.TaskId, stepId, vehicle, step.VendorTaskId,
            command.CommandId, revision, map.MapId, map.VersionId, map.Definition.CoordinateSystem,
            new DateTimeOffset(command.SentAt.Value), true, true, true, map.Definition, nodes, edges,
            leases.Select(l => new TrafficSegmentLease(l.LeaseId,l.ResourceId,l.TaskId,l.OwnerStepId,l.VehicleId,l.MapId!,l.MapVersionId!)).ToArray(), null);
        // Ignore lease heartbeat renewal timestamps, but bind every ownership identity and acquired epoch.
        var fingerprint = Digest(new { task.Stage,task.VehicleId,task.VendorTaskId,task.CancelRequestId,
            StepStatus=step.Status,step.StepType,step.SourceSite,step.DestinationSite,StepVendorTaskId=step.VendorTaskId,
            command.CommandId,CommandStatus=command.Status,command.SentAt,revision,request.State,request.ResourceIds,
            Leases = leases.OrderBy(l => l.LeaseId, StringComparer.Ordinal).Select(l => new {
                l.LeaseId,l.ResourceId,l.TaskId,l.OwnerStepId,l.VehicleId,l.MapId,l.MapVersionId,l.AcquiredAt }) });
        return new(context, fingerprint, map);
    }

    private static string Digest(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private sealed record Snapshot(TrafficSegmentReleaseContext Context, string Fingerprint, ActiveMapRuntimeSnapshot Map);
    internal sealed record CapturedEvidence(string Fingerprint,ActiveMapRuntimeSnapshot Map,
        TrafficSegmentReleaseContext Context,TrafficProgressPair Evidence,string PolicyJson,
        DateTimeOffset AssessedAt,double MaximumAgeSeconds,
        IReadOnlyList<TrafficSegmentReleaseCandidate> Candidates,
        TrafficSegmentProgressSummary? Progress);
    internal sealed record RequestTransition(string StepId,string BeforeState,string BeforeResourceIds,
        string AfterState,string AfterResourceIds,string AfterReason);
}
