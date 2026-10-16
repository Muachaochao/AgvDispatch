using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Traffic;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using SqlSugar;

// Test-only prototype. Not registered in production DI, workers, IPC or release packages.
sealed class SimulationTransaction(ISqlSugarClient db, IActiveMapRuntime runtime, IMapExecutionGate gate,
    TrafficSegmentReleaseInspector inspector, Source source, Envelope envelopes, FixedClock clock,
    TrafficSegmentReleasePolicy policy)
{
    private readonly Dictionary<string,Plan> plans = [];
    public Action? BeforeCommit;
    public Action? AfterDelete;
    public async Task<string?> PrepareAsync(CancellationToken ct=default)
    {
        var map=runtime.Current;var state=await FingerprintAsync(ct);var policyJson=JsonSerializer.Serialize(policy);
        var assessment=await inspector.InspectAsync("STEP",ct);
        if(!assessment.Available||assessment.Candidates.All(c=>!c.EvidenceVerified))return null;
        using var access=await gate.EnterAsync(MapExecutionOperation.ExistingWork,ct);
        if(access is null||map is null||!ReferenceEquals(map,runtime.Current)||state!=await FingerprintAsync(ct)||policyJson!=JsonSerializer.Serialize(policy))return null;
        var command=await db.Queryable<AgvCommandOutboxEntity>().Where(x=>x.StepId=="STEP").FirstAsync(ct);
        var frozen=TransportRouteSnapshots.Read(command);
        var leases=await db.Queryable<TrafficResourceLeaseEntity>().Where(x=>x.OwnerStepId=="STEP").ToListAsync(ct);
        var body=envelopes.LastRead;var pair=source.LastRead;
        if(body is null||pair is null)return null;
        var context=new TrafficSegmentReleaseContext("TASK","STEP","AGV","STEP",command.CommandId,
            command.ExecutionRouteSnapshotHash!,map.MapId,map.VersionId,frozen.CoordinateFrameId,
            new DateTimeOffset(command.SentAt!.Value),true,true,true,map.Definition,frozen.NodeIds,frozen.EdgeIds,
            leases.Select(l=>new TrafficSegmentLease(l.LeaseId,l.ResourceId,l.TaskId,l.OwnerStepId,l.VehicleId,l.MapId!,l.MapVersionId!)).ToArray(),body);
        var id=Guid.NewGuid().ToString("N");
        plans.Add(id,new(state,policyJson,map,context,pair,body,leases));return id;
    }

    public async Task<Outcome> CommitAsync(string planId,string requestId,CancellationToken ct=default)
    {
        // Replays return only the already committed result, and never delete a new lease.
        var prior=await db.Queryable<SimRequest>().Where(x=>x.RequestId==requestId).FirstAsync(ct);
        if(prior is not null)return prior.PlanId==planId?new(true,true,prior.Count,"原提交结果"):new(false,false,0,"请求号已绑定其他计划");
        if(!plans.TryGetValue(planId,out var plan))return new(false,false,0,"未知计划");
        BeforeCommit?.Invoke();
        using var access=await gate.EnterAsync(MapExecutionOperation.ExistingWork,ct);
        if(access is null||!gate.CanStartNewWork)return new(false,false,0,"模拟提交仅允许Running且恢复门禁开放");
        var count=0;var reason="";var replayed=false;
        var transaction=await db.Ado.UseTranAsync(async()=>
        {
            ct.ThrowIfCancellationRequested();
            prior=await db.Queryable<SimRequest>().Where(x=>x.RequestId==requestId).FirstAsync(ct);
            if(prior is not null)
            {
                if(prior.PlanId==planId){count=prior.Count;replayed=true;reason="并发请求读取原结果";}
                else reason="请求号已绑定其他计划";
                return;
            }
            if(!ReferenceEquals(plan.Map,runtime.Current)||plan.State!=await FingerprintAsync(ct)||
                plan.Body!=envelopes.Body||plan.Evidence!=source.Evidence||plan.PolicyJson!=JsonSerializer.Serialize(policy))
            {reason="原状态/最新模拟证据已变化";return;}
            var assessment=TrafficSegmentReleaseEvaluator.Evaluate(plan.Context,plan.Evidence,policy,clock.GetUtcNow());
            var candidates=assessment.Candidates.Where(c=>c.EvidenceVerified).ToArray();
            if(!assessment.Available||candidates.Length==0){reason="重新评估未通过";return;}
            foreach(var candidate in candidates)
            {
                var lease=plan.Leases.Single(l=>l.LeaseId==candidate.LeaseId);
                var removed=await db.Deleteable<TrafficResourceLeaseEntity>().Where(l=>l.LeaseId==lease.LeaseId&&
                    l.ResourceId==lease.ResourceId&&l.TaskId==lease.TaskId&&l.OwnerStepId==lease.OwnerStepId&&
                    l.VehicleId==lease.VehicleId&&l.MapId==lease.MapId&&l.MapVersionId==lease.MapVersionId&&l.AcquiredAt==lease.AcquiredAt)
                    .ExecuteCommandAsync(ct);
                if(removed!=1)throw new InvalidOperationException("租约条件删除失败");
                AfterDelete?.Invoke();ct.ThrowIfCancellationRequested();
                await db.Insertable(new SimAudit{AuditId=TransportRouteSnapshots.Hash(requestId+"/"+lease.LeaseId),
                    RequestId=requestId,LeaseId=lease.LeaseId,AcquiredAt=lease.AcquiredAt,ResourceId=lease.ResourceId,
                    RouteRevision=plan.Context.RouteRevision,EvidenceJson=JsonSerializer.Serialize(plan.Evidence)})
                    .ExecuteCommandAsync(ct);
                count++;
            }
            if(!gate.CanStartNewWork||!policy.Enabled||!ReferenceEquals(plan.Map,runtime.Current)||
                plan.Body!=envelopes.Body||plan.Evidence!=source.Evidence||plan.PolicyJson!=JsonSerializer.Serialize(policy)||
                !TrafficSegmentReleaseEvaluator.Evaluate(plan.Context,plan.Evidence,policy,clock.GetUtcNow()).Available)
                throw new InvalidOperationException("提交末尾门禁/证据已失效");
            await db.Insertable(new SimRequest{RequestId=requestId,PlanId=planId,Count=count}).ExecuteCommandAsync(ct);
        });
        if(!transaction.IsSuccess)
        {
            if(ct.IsCancellationRequested)throw new OperationCanceledException(ct);
            return new(false,false,0,"事务回滚: "+transaction.ErrorException?.Message);
        }
        return new(count>0,replayed,count,reason);
    }

    private async Task<string> FingerprintAsync(CancellationToken ct)
    {
        var tasks=await db.Queryable<DispatchTaskEntity>().Where(x=>x.TaskId=="TASK").ToListAsync(ct);
        var steps=await db.Queryable<DispatchTaskStepEntity>().Where(x=>x.StepId=="STEP").ToListAsync(ct);
        var commands=await db.Queryable<AgvCommandOutboxEntity>().Where(x=>x.StepId=="STEP").ToListAsync(ct);
        var requests=await db.Queryable<TrafficRequestEntity>().Where(x=>x.StepId=="STEP").ToListAsync(ct);
        var leases=await db.Queryable<TrafficResourceLeaseEntity>().Where(x=>x.OwnerStepId=="STEP").ToListAsync(ct);
        return TransportRouteSnapshots.Hash(JsonSerializer.Serialize(new{tasks,steps,commands,requests,
            leases=leases.OrderBy(l=>l.LeaseId,StringComparer.Ordinal).Select(l=>new{l.LeaseId,l.ResourceId,l.TaskId,l.OwnerStepId,l.VehicleId,l.MapId,l.MapVersionId,l.AcquiredAt})}));
    }
    private sealed record Plan(string State,string PolicyJson,ActiveMapRuntimeSnapshot Map,TrafficSegmentReleaseContext Context,
        TrafficProgressPair Evidence,TrafficBodyEnvelope Body,IReadOnlyList<TrafficResourceLeaseEntity> Leases);
}
record Outcome(bool Accepted,bool Replayed,int Count,string Reason);
[SugarTable("simulation_segment_request")]
sealed class SimRequest
{
    [SugarColumn(IsPrimaryKey=true)]public string RequestId{get;set;}="";
    public string PlanId{get;set;}="";
    public int Count{get;set;}
}
[SugarTable("simulation_segment_audit")]
sealed class SimAudit
{
    [SugarColumn(IsPrimaryKey=true)]public string AuditId{get;set;}="";
    public string RequestId{get;set;}="";
    public string LeaseId{get;set;}="";
    public string ResourceId{get;set;}="";
    public DateTime AcquiredAt{get;set;}
    public string RouteRevision{get;set;}="";
    [SugarColumn(ColumnDataType="TEXT")]public string EvidenceJson{get;set;}="";
}
