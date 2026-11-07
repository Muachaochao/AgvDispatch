using System.Collections.Concurrent;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Traffic;

// No automatic caller or public IPC. Consumption requires a separate default-off option.
public sealed partial class TrafficSegmentReceiptService(ISqlSugarClient database,TrafficSegmentReleaseInspector inspector,
    IMapExecutionGate gate,TimeProvider? clock=null,TrafficSegmentCommitOptions? commitOptions=null):ITrafficSegmentReceiptService,ITrafficSegmentCommitService
{
    private readonly string sessionId=Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string,Pending> pending=[];
    private DateTimeOffset Now=>(clock??TimeProvider.System).GetUtcNow();
    private static TrafficSegmentReceiptView Refuse(string reason)=>new(null,TrafficSegmentReceiptState.Unavailable,reason,null,0);

    public async Task<TrafficSegmentReceiptView> IssueAsync(string stepId,CancellationToken ct)
    {
        foreach(var item in pending)if(item.Value.ExpiresAt<=Now)pending.TryRemove(item.Key,out _);
        if(pending.Count>=256)return Refuse("未过期凭据达到上限，请稍后重新采集。");
        var proof=await inspector.CaptureAsync(stepId,ct);
        if(proof is null)return Refuse("没有合格的后台证据，未签发凭据。");
        using var access=await gate.EnterAsync(MapExecutionOperation.ExistingWork,ct);
        if(access is null||!gate.CanStartNewWork||!await inspector.IsCurrentAsync(proof,ct))return Refuse("签发前门禁或原状态失效。");
        if(pending.Count>=256)return Refuse("未过期凭据达到上限，请稍后重新采集。");
        var now=Now;
        if(now<proof.AssessedAt)return Refuse("签发时钟早于证据评估时刻。");
        var expires=new[]{now.AddSeconds(commitOptions?.ReceiptValiditySeconds??2),proof.Evidence.First.ObservedAt.AddSeconds(proof.MaximumAgeSeconds),
            proof.Evidence.Second.ObservedAt.AddSeconds(proof.MaximumAgeSeconds)}.Min();
        if(expires<=now)return Refuse("证据已过期。");
        var id="SR-"+Guid.NewGuid().ToString("N");
        var binding=JsonSerializer.Serialize(new{SchemaVersion=1,ReceiptId=id,SessionId=sessionId,
            IssuedAt=now,ExpiresAt=expires,proof.Context,proof.Fingerprint,proof.Evidence,proof.PolicyJson,proof.Candidates});
        var hash=TransportRouteSnapshots.Hash(binding);
        var row=new TrafficSegmentReceiptEntity{ReceiptId=id,SessionId=sessionId,TaskId=proof.Context.TaskId,
            StepId=stepId,VehicleId=proof.Context.VehicleId,CommandId=proof.Context.CommandId,RouteRevision=proof.Context.RouteRevision,
            BindingJson=binding,BindingHash=hash,CandidateCount=proof.Candidates.Count,IssuedAtUtc=now.UtcDateTime,ExpiresAtUtc=expires.UtcDateTime};
        var transaction=await database.Ado.UseTranAsync(async()=>
        {
            await database.Insertable(row).ExecuteCommandAsync(ct);
            await database.Insertable(new TrafficSegmentReceiptEventEntity{EventId=Guid.NewGuid().ToString("N"),
                ReceiptId=id,Kind="Issued",BindingHash=hash,OccurredAtUtc=now.UtcDateTime}).ExecuteCommandAsync(ct);
            ct.ThrowIfCancellationRequested();
            if(!gate.CanStartNewWork||!await inspector.IsCurrentAsync(proof,ct)||Now>=expires)
                throw new InvalidOperationException("凭据签发期间状态失效");
        });
        if(!transaction.IsSuccess)
        {
            ct.ThrowIfCancellationRequested();return Refuse("凭据和签发审计事务已回滚。");
        }
        pending[id]=new(proof,hash,expires);
        return new(id,TrafficSegmentReceiptState.Pending,"仅保存检查凭据，尚未释放资源。",expires,row.CandidateCount)
        {
            Candidates = proof.Candidates,
            Progress = proof.Progress
        };
    }

    public async Task<TrafficSegmentReceiptView> ReadAsync(string receiptId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(receiptId))return Refuse("缺少凭据编号。");
        var row=await database.Queryable<TrafficSegmentReceiptEntity>().Where(x=>x.ReceiptId==receiptId).FirstAsync(ct);
        if(row is null)return Refuse("凭据不存在。");
        var expires=new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresAtUtc,DateTimeKind.Utc));
        TrafficSegmentReceiptView View(TrafficSegmentReceiptState state,string reason)=>new(receiptId,state,reason,expires,row.CandidateCount);
        if(await database.Queryable<TrafficSegmentCommitEntity>().Where(x=>x.ReceiptId==receiptId).AnyAsync(ct))
            return View(TrafficSegmentReceiptState.Committed,"凭据已消费；查询原请求结果，不执行新解锁。");
        if(row.SessionId!=sessionId)return View(TrafficSegmentReceiptState.PreviousSession,"上次服务实例的未提交凭据已失效。");
        if(Now.UtcDateTime<row.IssuedAtUtc){pending.TryRemove(receiptId,out _);return View(TrafficSegmentReceiptState.Invalidated,"时钟回退，需重新采集。");}
        if(Now>=expires){pending.TryRemove(receiptId,out _);return View(TrafficSegmentReceiptState.Expired,"凭据已过期，需重新采集。");}
        if(!pending.TryGetValue(receiptId,out var saved)||saved.Hash!=row.BindingHash||
            TransportRouteSnapshots.Hash(row.BindingJson)!=saved.Hash||saved.ExpiresAt!=expires)
        {
            pending.TryRemove(receiptId,out _);
            return View(TrafficSegmentReceiptState.Invalidated,"凭据内容或后台缓存不一致。");
        }
        using var access=await gate.EnterAsync(MapExecutionOperation.ExistingWork,ct);
        if(access is null||!gate.CanStartNewWork||!await inspector.IsCurrentAsync(saved.Proof,ct))
        {
            pending.TryRemove(receiptId,out _);
            return View(TrafficSegmentReceiptState.Invalidated,"原状态或门禁已失效，需重新采集。");
        }
        if(Now>=expires){pending.TryRemove(receiptId,out _);return View(TrafficSegmentReceiptState.Expired,"检查期间凭据过期。");}
        return View(TrafficSegmentReceiptState.Pending,"凭据仍可检查；不能据此直接解锁。");
    }
    private sealed record Pending(TrafficSegmentReleaseInspector.CapturedEvidence Proof,string Hash,DateTimeOffset ExpiresAt);
}
