using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;

namespace AgvDispatch.Infrastructure.Traffic;

public sealed partial class TrafficSegmentReceiptService
{
    private static TrafficSegmentCommitResult Denied(string reason)=>new(false,false,reason,[]);
    public async Task<TrafficSegmentCommitResult?> ReadCommittedAsync(TrafficSegmentCommitRequest request,CancellationToken ct)
    {
        if(!ValidRequest(request))return Denied("凭据和请求编号无效。");
        var row=await database.Queryable<TrafficSegmentCommitEntity>().Where(x=>x.RequestId==request.RequestId).FirstAsync(ct);
        if(row is null)return null;
        if(row.ReceiptId!=request.ReceiptId)return Denied("请求号已绑定另一凭据。");
        try
        {
            var value=JsonSerializer.Deserialize<StoredResult>(row.ResultJson);
            var receipt=await database.Queryable<TrafficSegmentReceiptEntity>().Where(x=>x.ReceiptId==request.ReceiptId).FirstAsync(ct);
            if(TransportRouteSnapshots.Hash(row.ResultJson)!=row.ResultHash||value is null||value.SchemaVersion!=1||
                value.RequestId!=request.RequestId||value.ReceiptId!=request.ReceiptId||receipt is null||
                receipt.BindingHash!=value.ReceiptHash||receipt.CommandId!=value.CommandId||receipt.RouteRevision!=value.RouteRevision||
                string.IsNullOrWhiteSpace(value.EvidenceJson)||TransportRouteSnapshots.Hash(value.EvidenceJson)!=value.EvidenceHash||value.LeaseIds is not {Length:>0}||
                value.LeaseIds.Any(string.IsNullOrWhiteSpace)||
                value.LeaseIds.Distinct(StringComparer.Ordinal).Count()!=value.LeaseIds.Length)
                return Denied("原提交结果完整性校验失败。");
            var audits=await database.Queryable<TrafficSegmentCommitLeaseEntity>().Where(x=>x.RequestId==request.RequestId).ToListAsync(ct);
            if(audits.Count!=value.LeaseIds.Length||audits.Any(a=>a.ReceiptId!=request.ReceiptId||a.EvidenceHash!=value.EvidenceHash)||
                !audits.Select(a=>a.LeaseId).OrderBy(id=>id,StringComparer.Ordinal).SequenceEqual(value.LeaseIds.OrderBy(id=>id,StringComparer.Ordinal)))
                return Denied("原逐资源审计不完整。");
            return new(true,true,"只返回已提交结果，不执行新资源删除。",value.LeaseIds);
        }
        catch(JsonException){return Denied("原提交结果无法解析。");}
    }

    public async Task<TrafficSegmentCommitResult> CommitAsync(TrafficSegmentCommitRequest request,CancellationToken ct)
    {
        if(!ValidRequest(request))return Denied("凭据和请求编号无效。");
        var prior=await ReadCommittedAsync(request,ct);
        if(prior is not null)return prior;
        if(commitOptions?.Enabled!=true)return Denied("逐资源消费开关关闭。");
        var view=await ReadAsync(request.ReceiptId,ct);
        if(view.State!=TrafficSegmentReceiptState.Pending||!pending.TryGetValue(request.ReceiptId,out var saved))
            return await ReadCommittedAsync(request,ct)??Denied("原凭据不可消费，需重新采集。");
        // Capture outside the transaction/control gate. Both frames must be newer than the receipt.
        var latest = (await inspector.CaptureAsync(saved.Proof.Context.StepId, ct)).Proof;
        if(latest is null||!Compatible(saved.Proof,latest))
        {
            prior=await ReadCommittedAsync(request,ct);
            if(prior is not null)return prior;
            pending.TryRemove(request.ReceiptId,out _);
            return Denied("最新进度/包络不符合原凭据，原凭据撤销且保留路权。");
        }
        using var access=await gate.EnterAsync(MapExecutionOperation.ExistingWork,ct);
        if(access is null||!gate.CanStartNewWork||commitOptions.Enabled!=true)return Denied("消费前执行门禁关闭。");
        TrafficSegmentCommitResult result=Denied("未消费。");
        var transaction=await database.Ado.UseTranAsync(async()=>
        {
            prior=await ReadCommittedAsync(request,ct);
            if(prior is not null){result=prior;return;}
            if(await database.Queryable<TrafficSegmentCommitEntity>().Where(x=>x.ReceiptId==request.ReceiptId).AnyAsync(ct))
            {result=Denied("该凭据已被另一请求消费。");return;}
            var receipt=await database.Queryable<TrafficSegmentReceiptEntity>().Where(x=>x.ReceiptId==request.ReceiptId).FirstAsync(ct);
            if(receipt is null||receipt.SessionId!=sessionId||receipt.BindingHash!=saved.Hash||
                TransportRouteSnapshots.Hash(receipt.BindingJson)!=saved.Hash||Now>=saved.ExpiresAt||Now<saved.Proof.AssessedAt||
                !pending.TryGetValue(request.ReceiptId,out var stillPending)||!ReferenceEquals(stillPending,saved)||
                !await inspector.IsCurrentAsync(saved.Proof,ct)||!await inspector.IsCurrentAsync(latest,ct))
            {result=Denied("事务中原状态/凭据已失效。");return;}
            var candidateIds=saved.Proof.Candidates.Select(c=>c.LeaseId).ToArray();
            var leases=await database.Queryable<TrafficResourceLeaseEntity>().Where(l=>candidateIds.Contains(l.LeaseId)).ToListAsync(ct);
            if(leases.Count!=candidateIds.Length)throw new InvalidOperationException("原候选租约不完整。");
            leases.Sort((a,b)=>StringComparer.Ordinal.Compare(a.LeaseId,b.LeaseId));
            var evidenceJson=JsonSerializer.Serialize(new{latest.Evidence,latest.Context.Body,latest.PolicyJson});
            var evidenceHash=TransportRouteSnapshots.Hash(evidenceJson);
            var releasedAt=Now;
            foreach(var lease in leases)
            {
                var original=latest.Context.Leases.Single(l=>l.LeaseId==lease.LeaseId);
                if(lease.OwnerStepId!=original.StepId||lease.TaskId!=original.TaskId||lease.VehicleId!=original.VehicleId||
                    lease.ResourceId!=original.ResourceId||lease.MapId!=original.MapId||lease.MapVersionId!=original.MapVersionId)
                    throw new InvalidOperationException("原租约归属变化。");
                // IsCurrentAsync verifies the typed acquisition timestamp as part
                // of the fingerprint in this transaction. SQLite stores different
                // textual forms of the same timestamp, so do not rebind it in SQL.
                var deleted=await database.Deleteable<TrafficResourceLeaseEntity>().Where(l=>l.LeaseId==lease.LeaseId&&
                    l.OwnerStepId==lease.OwnerStepId&&l.TaskId==lease.TaskId&&l.VehicleId==lease.VehicleId&&
                    l.ResourceId==lease.ResourceId&&l.MapId==lease.MapId&&l.MapVersionId==lease.MapVersionId)
                    .ExecuteCommandAsync(ct);
                if(deleted!=1)throw new InvalidOperationException("原租约条件删除失败。");
                await database.Insertable(new TrafficSegmentCommitLeaseEntity{AuditId=Guid.NewGuid().ToString("N"),
                    RequestId=request.RequestId,ReceiptId=request.ReceiptId,LeaseId=lease.LeaseId,ResourceId=lease.ResourceId,
                    TaskId=lease.TaskId,StepId=lease.OwnerStepId,VehicleId=lease.VehicleId,MapId=lease.MapId!,
                    MapVersionId=lease.MapVersionId!,AcquiredAt=lease.AcquiredAt,EvidenceHash=evidenceHash,ReleasedAtUtc=releasedAt.UtcDateTime})
                    .ExecuteCommandAsync(ct);
                ct.ThrowIfCancellationRequested();
            }
            var leaseIds=leases.Select(l=>l.LeaseId).OrderBy(id=>id,StringComparer.Ordinal).ToArray();
            var stored=new StoredResult(1,request.RequestId,request.ReceiptId,saved.Hash,latest.Context.CommandId,
                latest.Context.RouteRevision,evidenceJson,evidenceHash,leaseIds,releasedAt);
            var resultJson=JsonSerializer.Serialize(stored);
            await database.Insertable(new TrafficSegmentCommitEntity{RequestId=request.RequestId,ReceiptId=request.ReceiptId,
                ResultJson=resultJson,ResultHash=TransportRouteSnapshots.Hash(resultJson),CommittedAtUtc=releasedAt.UtcDateTime}).ExecuteCommandAsync(ct);
            await database.Insertable(new TrafficSegmentReceiptEventEntity{EventId=Guid.NewGuid().ToString("N"),ReceiptId=request.ReceiptId,
                Kind="Committed",BindingHash=saved.Hash,OccurredAtUtc=releasedAt.UtcDateTime}).ExecuteCommandAsync(ct);
            await database.Insertable(new TrafficLifecycleEventEntity{EventId=Guid.NewGuid().ToString("N"),StepId=latest.Context.StepId,
                TaskId=latest.Context.TaskId,VehicleId=latest.Context.VehicleId,Kind="SegmentReleased",
                Reason="后台凭据与更新两帧证据核验后逐资源释放；原任务仍按步骤执行。",
                ResourceIds=JsonSerializer.Serialize(leases.Select(l=>l.ResourceId)),OccurredAt=releasedAt.LocalDateTime}).ExecuteCommandAsync(ct);
            if(commitOptions.Enabled!=true||!gate.CanStartNewWork||Now>=saved.ExpiresAt||Now<latest.AssessedAt||
                !await inspector.IsCurrentAsync(latest,ct,leases)||!await inspector.IsCurrentAsync(saved.Proof,ct,leases))
                throw new InvalidOperationException("提交末尾门禁/状态/证据失效。");
            ct.ThrowIfCancellationRequested();
            // Keep the diagnostic request consistent in the same transaction, before another owner can acquire.
            var trafficRequest=await database.Queryable<TrafficRequestEntity>().Where(r=>r.StepId==latest.Context.StepId).FirstAsync(ct);
            var remaining=await database.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.OwnerStepId==latest.Context.StepId).ToListAsync(ct);
            var remainingIds=JsonSerializer.Serialize(remaining.Select(l=>l.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id=>id,StringComparer.OrdinalIgnoreCase));
            var afterState=remaining.Count>0?"Held":"Released";
            var afterReason=remaining.Count>0?"已按经过与车体证据释放前序资源；继续保持当前剩余占用。":"本步骤持有资源已按经过与车体证据释放，任务仍按原步骤执行。";
            var transition=new TrafficSegmentReleaseInspector.RequestTransition(latest.Context.StepId,trafficRequest.State,
                trafficRequest.ResourceIds,afterState,remainingIds,afterReason);
            var affected=await database.Updateable<TrafficRequestEntity>().SetColumns(r=>new TrafficRequestEntity{
                State=afterState,ResourceIds=remainingIds,Reason=afterReason,UpdatedAt=releasedAt.LocalDateTime})
                .Where(r=>r.StepId==trafficRequest.StepId&&r.TaskId==trafficRequest.TaskId&&r.VehicleId==trafficRequest.VehicleId&&
                    r.State==trafficRequest.State&&r.ResourceIds==trafficRequest.ResourceIds).ExecuteCommandAsync(ct);
            if(affected!=1||commitOptions.Enabled!=true||!gate.CanStartNewWork||Now>=saved.ExpiresAt||Now<latest.AssessedAt||
                !await inspector.IsCurrentAsync(latest,ct,leases,transition)||!await inspector.IsCurrentAsync(saved.Proof,ct,leases,transition))
                throw new InvalidOperationException("提交末尾剩余占用展示或原状态失效。");
            ct.ThrowIfCancellationRequested();
            result=new(true,false,"原租约删除与逐资源审计、提交结果已同事务保存。",leaseIds);
        });
        if(!transaction.IsSuccess)
        {
            ct.ThrowIfCancellationRequested();return Denied("消费事务回滚，原路权保留。");
        }
        if(result.Accepted)pending.TryRemove(request.ReceiptId,out _);
        return result;
    }

    private static bool Compatible(TrafficSegmentReleaseInspector.CapturedEvidence original,
        TrafficSegmentReleaseInspector.CapturedEvidence latest)
    {
        if(!ReferenceEquals(original.Map,latest.Map)||original.Fingerprint!=latest.Fingerprint||
            original.PolicyJson!=latest.PolicyJson||original.Context.Body!=latest.Context.Body)return false;
        var previous=original.Evidence.Second;var first=latest.Evidence.First;
        // The commit read may begin from the receipt's second frame. That frame
        // is the continuity anchor; the newly captured pair must still advance
        // strictly from it to a later second frame.
        if(first.ControllerEpoch!=previous.ControllerEpoch||first.Sequence<previous.Sequence||
            first.ObservedAt<previous.ObservedAt||first.LastFullyTraversedEdgeIndex<previous.LastFullyTraversedEdgeIndex||
            latest.Evidence.Second.Sequence<=first.Sequence||
            latest.Evidence.Second.ObservedAt<=first.ObservedAt)return false;
        return original.Candidates.All(c=>latest.Candidates.Any(n=>n.LeaseId==c.LeaseId&&n.ResourceId==c.ResourceId&&n.EvidenceVerified));
    }
    private static bool ValidRequest(TrafficSegmentCommitRequest? request)=>request is not null&&
        !string.IsNullOrWhiteSpace(request.ReceiptId)&&request.ReceiptId.Length<=64&&
        !string.IsNullOrWhiteSpace(request.RequestId)&&request.RequestId.Length<=64;
    private sealed record StoredResult(int SchemaVersion,string RequestId,string ReceiptId,string ReceiptHash,
        string CommandId,string RouteRevision,string EvidenceJson,string EvidenceHash,string[] LeaseIds,DateTimeOffset CommittedAt);
}
