using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TaskHandshakeStore
{
    public Task<bool> OwnsEquipmentAsync(string taskId,string equipmentId,CancellationToken ct)
        => database.Queryable<TaskEquipmentClaimEntity>().Where(e=>e.TaskId==taskId&&e.EquipmentId==equipmentId).AnyAsync(ct);
    public Task ValidatePhysicalExitAsync(HandshakeCheckpoint checkpoint,TaskDumperExitProof evidence,CancellationToken ct)
        => ValidateExitAsync(checkpoint,evidence,ct);
    public async Task<bool> RecordPhysicalExitAsync(HandshakeCheckpoint checkpoint, TaskDumperExitProof evidence, CancellationToken ct)
    {
        if (checkpoint.Phase != "AwaitPhysicalExit") throw new InvalidOperationException("当前不是28阶段实际出区核对阶段。");
        var moved = false;
        var result = await database.Ado.UseTranAsync(async () =>
        {
            await ValidateExitAsync(checkpoint, evidence, ct);
            var now = DateTime.UtcNow;
            moved = await database.Updateable<TaskHandshakeEntity>().SetColumns(h => new TaskHandshakeEntity
                { Phase="ClearDumpingAfterExit", Revision=checkpoint.Revision+1, UpdatedAtUtc=now, Reason="两次整车出区及原返程不再入区证据已保存；仍持有倒料归属，等待PLC闭合。" })
                .Where(h => h.TaskId==checkpoint.TaskId && h.StepId==checkpoint.StepId && h.Revision==checkpoint.Revision && h.Phase=="AwaitPhysicalExit" && h.ContractVersion==HandshakeExecutionOptions.Version && h.PendingSignal==null).ExecuteCommandAsync(ct)==1;
            if (moved) await database.Insertable(new TaskRuntimeEventEntity { TaskId=checkpoint.TaskId, StepId=checkpoint.StepId,
                Kind="ExitVerified",Phase="ClearDumpingAfterExit", RecordedAtUtc=now, Reason="实际出区有物理位置、车体载荷包络及原发送路线证据；不以返回命令或终态替代。", EvidenceJson=JsonSerializer.Serialize(evidence) }).ExecuteCommandAsync(ct);
        });
        if (!result.IsSuccess)
        {
            if (result.ErrorException is DumperExitPendingException pending) throw pending;
            throw new InvalidOperationException("实际出区证据事务未提交，保持占用。", result.ErrorException);
        }
        return moved;
    }

    public async Task<bool> ReleaseDumperAfterExitAsync(HandshakeCheckpoint checkpoint, string equipmentId, TaskDumperExitProof evidence, CancellationToken ct)
    {
        if (checkpoint.Phase!="ReleaseDumper") throw new InvalidOperationException("PLC退出闭合尚未完成，禁止交接倒料设备。");
        var moved=false;
        var result=await database.Ado.UseTranAsync(async () =>
        {
            await ValidateExitAsync(checkpoint,evidence,ct);
            if (!await database.Queryable<TaskRuntimeEventEntity>().Where(e=>e.TaskId==checkpoint.TaskId && e.StepId==checkpoint.StepId && e.Kind=="ExitVerified" && e.Phase=="ClearDumpingAfterExit").AnyAsync(ct))
                throw new InvalidOperationException("缺少本轮先前持久化的实际出区证据。");
            if (!await database.Queryable<TaskEquipmentClaimEntity>().Where(e=>e.TaskId==checkpoint.TaskId && e.EquipmentId==equipmentId).AnyAsync(ct))
                throw new InvalidOperationException("倒料设备归属已变化，禁止释放其他任务的占用。");
            ValidateDumperClosed(checkpoint,equipmentId,evidence);
            var now=DateTime.UtcNow;
            moved=await database.Updateable<TaskHandshakeEntity>().SetColumns(h=>new TaskHandshakeEntity
                {Phase="ExitHandoverComplete",Revision=checkpoint.Revision+1,UpdatedAtUtc=now,Reason="PLC退出闭合，已原子交接本任务倒料设备及已核定区域；返箱责任继续。"})
                .Where(h=>h.TaskId==checkpoint.TaskId && h.StepId==checkpoint.StepId && h.Revision==checkpoint.Revision && h.Phase=="ReleaseDumper" && h.PendingSignal==null && h.ContractVersion==HandshakeExecutionOptions.Version).ExecuteCommandAsync(ct)==1;
            if(!moved)return;
            var ids=evidence.Policy.ResourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var leases=(await database.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.TaskId==checkpoint.TaskId).ToListAsync(ct)).Where(l=>ids.Contains(l.ResourceId)).ToList();
            if(leases.Any(l=>l.VehicleId!=evidence.VehicleId || l.MapId!=evidence.MapId || l.MapVersionId!=evidence.MapVersionId))
                throw new InvalidOperationException("待交接区域租约与本轮车辆/地图不一致。");
            var leaseIds=leases.Select(l=>l.LeaseId).ToArray();
            if(leaseIds.Length>0) await database.Deleteable<TrafficResourceLeaseEntity>().Where(l=>leaseIds.Contains(l.LeaseId) && l.TaskId==checkpoint.TaskId).ExecuteCommandAsync(ct);
            foreach(var group in leases.GroupBy(l=>l.OwnerStepId))
            {
                var request=await database.Queryable<TrafficRequestEntity>().Where(r=>r.StepId==group.Key && r.TaskId==checkpoint.TaskId).FirstAsync(ct);
                if(request==null)throw new InvalidOperationException("原区域请求记录缺失，事务回滚。");
                var remaining=await database.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.OwnerStepId==group.Key && l.TaskId==checkpoint.TaskId).Select(l=>l.ResourceId).ToListAsync(ct);
                request.ResourceIds=JsonSerializer.Serialize(remaining.OrderBy(x=>x));request.State=remaining.Count==0?"Released":"Held";
                request.Reason="28阶段：两次整车出区、原返程后续不再进入且PLC闭合后交接指定倒料区域，其他区域保持。";request.UpdatedAt=DateTime.Now;
                await database.Updateable(request).ExecuteCommandAsync(ct);
                await database.Insertable(new TrafficLifecycleEventEntity {EventId=Guid.NewGuid().ToString("N"),TaskId=checkpoint.TaskId,StepId=group.Key,VehicleId=evidence.VehicleId,
                    Kind="Released",Reason=request.Reason,ResourceIds=JsonSerializer.Serialize(group.Select(l=>l.ResourceId).Distinct()),OccurredAt=DateTime.Now}).ExecuteCommandAsync(ct);
            }
            if(await database.Deleteable<TaskEquipmentClaimEntity>().Where(e=>e.TaskId==checkpoint.TaskId && e.EquipmentId==equipmentId).ExecuteCommandAsync(ct)!=1)
                throw new InvalidOperationException("倒料设备归属并发变化，事务回滚。");
            await database.Insertable(new TaskRuntimeEventEntity {TaskId=checkpoint.TaskId,StepId=checkpoint.StepId,Kind="EquipmentReleased",Phase="ExitHandoverComplete",StationId=equipmentId,
                RecordedAtUtc=now,Reason="28阶段实际出区、PLC周期闭合后交接倒料设备；返箱任务尚可继续。",EvidenceJson=JsonSerializer.Serialize(new {Proof=evidence,ReleasedLeaseIds=leaseIds})}).ExecuteCommandAsync(ct);
        });
        if(!result.IsSuccess)
        {
            if(result.ErrorException is DumperExitPendingException pending)throw pending;
            throw new InvalidOperationException("倒料归属及已验证区域交接未提交，保持原责任。",result.ErrorException);
        }
        return moved;
    }

    private async Task ValidateExitAsync(HandshakeCheckpoint checkpoint,TaskDumperExitProof proof,CancellationToken ct)
    {
        await EnsureReadyAsync(checkpoint.TaskId,checkpoint.StepId,ct);
        var map=activeMap?.Current??throw new InvalidOperationException("出区交接缺少活动地图。");
        if(checkpoint.ContractVersion!=HandshakeExecutionOptions.Version || proof.TaskId!=checkpoint.TaskId || proof.StepId!=checkpoint.StepId ||
            proof.MapId!=map.MapId || proof.MapVersionId!=map.VersionId ||
            proof.First.MapId!=map.MapId || proof.Second.MapId!=map.MapId || proof.First.MapVersionId!=map.VersionId || proof.Second.MapVersionId!=map.VersionId ||
            proof.First.VehicleId!=proof.VehicleId || proof.Second.VehicleId!=proof.VehicleId || proof.Body.VehicleId!=proof.VehicleId)
            throw new InvalidOperationException("出区证据与原任务、契约、车辆或地图不一致。");
        var now=DateTimeOffset.UtcNow;
        if(proof.CapturedAt>now.AddSeconds(1) || now-proof.CapturedAt>TimeSpan.FromSeconds(proof.Policy.MaximumAgeSeconds) ||
            proof.VendorObservedAt>now.AddSeconds(1) || now-proof.VendorObservedAt>TimeSpan.FromSeconds(proof.Policy.MaximumAgeSeconds))
            throw new InvalidOperationException("出区任务查询或证据已过期。");
        var step=await database.Queryable<DispatchTaskStepEntity>().Where(s=>s.TaskId==proof.TaskId && s.StepId==proof.StepId && s.StepType==70 &&
            (s.Status==40 || s.Status==50) && s.VehicleId==proof.VehicleId && s.VendorTaskId==proof.VendorTaskId).FirstAsync(ct)
            ??throw new InvalidOperationException("没有对应的执行中/已完成原返程。");
        var task=await database.Queryable<DispatchTaskEntity>().Where(t=>t.TaskId==proof.TaskId).FirstAsync(ct);
        if(task==null || task.VehicleId!=proof.VehicleId || task.Stage is not (70 or 80) || task.CancelRequestId!=null)
            throw new InvalidOperationException("返程任务已冻结、取消或归属变化。");
        var commands=await database.Queryable<AgvCommandOutboxEntity>().Where(c=>c.TaskId==proof.TaskId && c.StepId==proof.StepId).ToListAsync(ct);
        var finalCommands=commands.Where(c=>c.Status==(int)AgvCommandStatus.Sent &&
            c.VendorCommandId==proof.VendorTaskId && c.SentAt is not null).ToArray();
        // A return step may legitimately span several committed rolling windows.
        // Every window must be sent, and the proof must identify exactly one final
        // vendor command; requiring the whole step to contain one command breaks
        // valid multi-window returns.
        if(commands.Count==0 || commands.Any(c=>c.Status!=(int)AgvCommandStatus.Sent) || finalCommands.Length!=1)
            throw new InvalidOperationException("返程原命令未唯一确认发送。");
        var originalCommand=finalCommands[0];
        var sent=originalCommand.SentAt!.Value;
        if(proof.First.ObservedAt<new DateTimeOffset(sent) || proof.VendorObservedAt<new DateTimeOffset(sent))
            throw new InvalidOperationException("出区观测早于本轮返程发送。");
        var route=TransportRouteSnapshots.Read(originalCommand);
        var hash=EngineeringRuntimeIdentity.MapHash(map.Definition);
        var legacyHash=TransportRouteSnapshots.Hash(JsonSerializer.Serialize(map.Definition,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        if(route.MapId!=map.MapId || route.MapVersionId!=map.VersionId || route.MapDefinitionHash!=hash || proof.MapDefinitionHash!=hash)
            throw new InvalidOperationException("发送时地图与出区证据不一致，不能重规划代替原路线。");
        using var bound=JsonDocument.Parse(checkpoint.Baseline.Single(s=>s.StationId=="__context"&&s.VariableName=="Binding").Address);
        var context=bound.RootElement;
        var configuration=context.GetProperty("Exit").Deserialize<DumperExitConfiguration>()??throw new InvalidOperationException("原出区配置缺失。");
        var profiles=configuration.BodyProfiles.Where(p=>p.VehicleId==proof.VehicleId).ToArray();
        var bindingHash=context.GetProperty("MapHash").GetString();
        if(context.GetProperty("MapId").GetString()!=proof.MapId||context.GetProperty("VersionId").GetString()!=proof.MapVersionId||bindingHash!=hash&&bindingHash!=legacyHash||context.GetProperty("Vehicle").GetString()!=proof.VehicleId||
            profiles.Length!=1||profiles[0]!=proof.Body||!PolicyEvidenceCompatible(configuration,proof.Policy,proof.MaximumRouteDeviationMeters))
            throw new InvalidOperationException("出区证据的地图、区域、包络或阈值不等于原持久化绑定。");
        var equipment=context.GetProperty("Dumper").GetString();
        if(string.IsNullOrWhiteSpace(equipment)||!await OwnsEquipmentAsync(proof.TaskId,equipment,ct))throw new InvalidOperationException("出区原倒料设备归属缺失，不重新认领。");
        var assessed=DumperExitEvaluator.Evaluate(map.Definition,proof.First,proof.Second,proof.Body,proof.Policy,now);
        if(!assessed.Verified)throw new InvalidOperationException(assessed.Reason);
        var suffix=DumperExitEvaluator.VerifyReturnRouteAfterExit(map.Definition,route.NodeIds,route.EdgeIds,proof.Second,proof.Body,proof.Policy,
            proof.MaximumRouteDeviationMeters,proof.EndpointArrivalConfirmed);
        if(!suffix.Verified)
        {
            if(suffix.WaitingForPhysicalExit)throw new DumperExitPendingException(suffix.Reason);
            throw new InvalidOperationException(suffix.Reason);
        }
    }

    private static bool PolicyEvidenceCompatible(DumperExitConfiguration original,DumperExitPolicy proof,double proofMaximumRouteDeviationMeters)
    {
        // The reviewed commissioning path may lower only the localization-confidence
        // gate. All physical identity and safety geometry values must stay identical,
        // and an existing task may never raise the gate after it has started.
        if(original.MinimumConfidence<proof.MinimumConfidence||
            original.MinimumObservationIntervalSeconds!=proof.MinimumObservationIntervalSeconds||
            original.MaximumAgeSeconds!=proof.MaximumAgeSeconds||
            original.SafetyMarginMeters!=proof.SafetyMarginMeters||
            original.MaximumRouteDeviationMeters<proofMaximumRouteDeviationMeters||
            original.ResourceIds.Length!=proof.ResourceIds.Count)
            return false;
        var originalResources=original.ResourceIds.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var proofResources=proof.ResourceIds.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return originalResources.SequenceEqual(proofResources,StringComparer.OrdinalIgnoreCase);
    }

    private void ValidateDumperClosed(HandshakeCheckpoint checkpoint,string equipmentId,TaskDumperExitProof proof)
    {
        if(liveSignals==null)throw new InvalidOperationException("缺少交接事务中的实时PLC闭合复核，不释放设备。");
        var now=DateTimeOffset.UtcNow;
        foreach(var name in new[]{"Faulted","AgvAtWaitingPosition","AgvAtDumpingPosition","DumperReady","DumpingCompletedMayLeave"})
        {
            var observed=proof.PlcSignals.Where(s=>s.StationId==equipmentId&&s.VariableName==name).ToArray();
            var original=checkpoint.Baseline.SingleOrDefault(s=>s.StationId==equipmentId&&s.VariableName==name);
            if(observed.Length!=1||original==null||observed[0].Address!=original.Address||observed[0].Value!=false||observed[0].Quality!="Good"||
                observed[0].ObservedAt is not {} time||time>now||now-time>TimeSpan.FromSeconds(proof.Policy.MaximumAgeSeconds)||
                !liveSignals.TryGet(equipmentId,name,out var live)||!live.Enabled||live.Address!=original.Address||live.Value is not false||live.Quality!=AgvDispatch.Domain.Devices.SignalQuality.Good||
                live.UpdatedAt is not {} currentTime||currentTime<time||currentTime>now||now-currentTime>TimeSpan.FromSeconds(proof.Policy.MaximumAgeSeconds))
                throw new InvalidOperationException("交接事务PLC闭合证据或最新回读无效："+equipmentId+"/"+name);
        }
    }
}
