using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;
namespace AgvDispatch.Infrastructure.Persistence;
public sealed partial class TrafficResourceControlService
{
    public async Task<TrafficBlockingDetails> GetBlockingAsync(TrafficBlockingQuery query,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(query.TaskId)||query.TaskId.Length>64||string.IsNullOrWhiteSpace(query.StepId)||query.StepId.Length>64)throw new ArgumentException("任务与等待步骤编号无效。");
        TrafficBlockingDetails? result=null;
        var transaction=await database.Ado.UseTranAsync(async()=>{
            var now=(clock??TimeProvider.System).GetUtcNow();
            var root=await database.Queryable<TrafficRequestEntity>().Where(r=>r.StepId==query.StepId&&r.TaskId==query.TaskId).FirstAsync(ct);
            if(root is null){result=new(now,query.TaskId,query.StepId,false,"原等待记录已不存在或归属变化，请刷新队列。",[],[],[],false,false,false);return;}
            // Relationships and their evidence share one read snapshot. Do not build a chain from the capped lifecycle page.
            var leases=await database.Queryable<TrafficResourceLeaseEntity>().ToListAsync(ct);
            var nodes=new List<TrafficBlockingNode>();var links=new List<TrafficBlockingLink>();var seen=new HashSet<string>(StringComparer.Ordinal);
            var queue=new Queue<(string Task,string Step,string Vehicle,int Depth)>();queue.Enqueue((root.TaskId,root.StepId,root.VehicleId,0));seen.Add(root.StepId);var limited=false;
            void Link(TrafficBlockingLink link,int depth){
                if(links.Contains(link))return;
                if(depth>8||links.Count>=128||(!seen.Contains(link.ToStepId)&&seen.Count>=32)){limited=true;return;}
                links.Add(link);if(seen.Add(link.ToStepId))queue.Enqueue((link.ToTaskId,link.ToStepId,link.VehicleId,depth));
            }
            while(queue.TryDequeue(out var item)){
                ct.ThrowIfCancellationRequested();
                var row=await database.Queryable<TrafficRequestEntity>().Where(r=>r.StepId==item.Step&&r.TaskId==item.Task).FirstAsync(ct);
                var task=await database.Queryable<DispatchTaskEntity>().Where(t=>t.TaskId==item.Task).FirstAsync(ct);
                var step=await database.Queryable<DispatchTaskStepEntity>().Where(s=>s.StepId==item.Step&&s.TaskId==item.Task).FirstAsync(ct);
                var commands=await database.Queryable<AgvCommandOutboxEntity>().Where(c=>c.StepId==item.Step&&c.TaskId==item.Task).Take(2).Select(c=>new{c.Status,c.VehicleId}).ToListAsync(ct);
                var projected=row is null?null:ProjectRequest(row,leases,task is null?new Dictionary<string,DispatchTaskEntity>():new Dictionary<string,DispatchTaskEntity>{{task.TaskId,task}},DateTime.Now,now);
                var associated=task is not null&&step is not null&&row is not null&&row.VehicleId==item.Vehicle&&task.VehicleId==item.Vehicle;
                var lease=leases.FirstOrDefault(l=>l.TaskId==item.Task&&l.OwnerStepId==item.Step&&l.VehicleId==item.Vehicle);
                if(projected is {State:"Waiting"}&&(task is null||!Enum.IsDefined(typeof(DispatchTaskStage),task.Stage)||task.Stage is 90 or 100 or 110 or 120 or 130||step is null||step.Status is not (0 or 10 or 60)))projected=projected with{State=lease is null?"Inactive":"Held",Reason="原等待步骤已结束或非待执行状态；不沿用历史阻塞关系，占用仍按实际记录显示。"};
                var stage=task is not null&&Enum.IsDefined(typeof(DispatchTaskStage),task.Stage)?((DispatchTaskStage)task.Stage).ToString():null;
                var stepState=step is not null&&Enum.IsDefined(typeof(DispatchTaskStepStatus),step.Status)?((DispatchTaskStepStatus)step.Status).ToString():null;
                var command=commands.Count==1&&commands[0].VehicleId==item.Vehicle?((AgvCommandStatus)commands[0].Status).ToString():null;
                var retained=stage is null||task!.Stage is 90 or 100 or 110 or 120 or 130;
                var requirements=new TrafficReleaseRequirement[]{
                    new("Ownership",associated?"Observed":"Unknown","原任务、步骤与车辆归属",associated?"数据库归属一致；不证明现场位置。":"缺少匹配的任务/步骤/车辆或原路径记录，不能反推历史。"),
                    new("NormalHandover","NotAssessed","正常交接/收尾释放","须核对适用契约、实际交接证据或完整业务收尾；整单保持的候车位须等原任务闭环。取得新路权、保存命令或当前步骤完成本身都不是释放许可。"),
                    new("SegmentEvidence","NotAssessed","逐段/空间释放","须按配置启用并核验原运输、两帧新鲜定位、车体包络和未来占用；本查询不执行评估或释放。"),
                    new("RetainedReview",retained?"RequiresReview":"NotAssessed","异常或取消保留处置",retained?"异常/取消或任务缺失，原占用保留。车辆结束或租约心跳到期均不足以自动解锁。":"原人工解锁/恢复流程仍需停车、载荷归属、PLC周期和路权处置证据，当前未核验。")};
                nodes.Add(new(item.Task,item.Step,item.Vehicle,row?.MapId??lease?.MapId,row?.MapVersionId??lease?.MapVersionId,projected?.State??"Missing",projected?.Reason??"占用存在但原路径请求缺失，不反推路线。",stage,stepState,command,!associated||stage is null||stepState is null,requirements));
                if(projected is {State:"Waiting",BlockingObservationCurrent:true})foreach(var b in projected.Blockers)
                    Link(new(item.Task,item.Step,b.TaskId,b.StepId,b.VehicleId,"Resource",b.ResourceId,b.HeartbeatExpired,b.MapId,b.MapVersionId),item.Depth+1);
                // A task can retain a prior step while another step waits. These are task dependencies, not resource ownership edges.
                if(lease is not null&&stage is not null&&task!.Stage is not (90 or 100 or 110 or 120 or 130)){
                    var pending=await database.Queryable<TrafficRequestEntity>().Where(r=>r.TaskId==item.Task&&r.State=="Waiting"&&r.StepId!=item.Step).OrderByDescending(r=>r.UpdatedAt).Take(33).ToListAsync(ct);
                    if(pending.Count>32)limited=true;
                    var pendingIds=pending.Select(r=>r.StepId).ToArray();
                    var eligible=pendingIds.Length==0?new List<string>():await database.Queryable<DispatchTaskStepEntity>().Where(s=>pendingIds.Contains(s.StepId)&&s.TaskId==item.Task&&(s.Status==0||s.Status==10||s.Status==60)).Select(s=>s.StepId).ToListAsync(ct);
                    foreach(var wait in pending.Take(32).Where(r=>eligible.Contains(r.StepId))){
                        var p=ProjectRequest(wait,leases,task is null?new Dictionary<string,DispatchTaskEntity>():new Dictionary<string,DispatchTaskEntity>{{task.TaskId,task}},DateTime.Now,now);
                        if(p.State=="Waiting")Link(new(item.Task,item.Step,wait.TaskId,wait.StepId,wait.VehicleId,"TaskWait",null,false,wait.MapId,wait.MapVersionId),item.Depth+1);
                    }
                }
            }
            var stepIds=nodes.Select(n=>n.StepId).ToArray();var pairs=nodes.Select(n=>(n.TaskId,n.StepId)).ToHashSet();
            var events=await database.Queryable<TrafficLifecycleEventEntity>().Where(e=>stepIds.Contains(e.StepId)).OrderByDescending(e=>e.OccurredAt).OrderBy(e=>e.EventId).Take(51).ToListAsync(ct);
            var audits=await database.Queryable<TrafficResourceReleaseAuditEntity>().Where(a=>stepIds.Contains(a.OwnerStepId)).OrderByDescending(a=>a.ReleasedAt).Take(51).ToListAsync(ct);
            var history=events.Where(e=>pairs.Contains((e.TaskId,e.StepId))).Select(e=>new TrafficLifecycleEvent(e.EventId,e.StepId,e.TaskId,e.VehicleId,e.Kind,e.Reason,System.Text.Json.JsonSerializer.Deserialize<string[]>(e.ResourceIds)??[],new(e.OccurredAt)))
                .Concat(audits.Where(a=>pairs.Contains((a.TaskId,a.OwnerStepId))).Select(a=>new TrafficLifecycleEvent("audit:"+a.AuditId,a.OwnerStepId,a.TaskId,a.VehicleId,"ManualReleaseAudit",a.Reason,[a.ResourceId],new(a.ReleasedAt)))).OrderByDescending(e=>e.OccurredAt).ThenBy(e=>e.EventId,StringComparer.Ordinal).ToArray();
            var colors=new Dictionary<string,int>();bool Cycle(string id){if(colors.TryGetValue(id,out var color))return color==1;colors[id]=1;foreach(var link in links.Where(l=>l.FromStepId==id))if(Cycle(link.ToStepId))return true;colors[id]=2;return false;}
            var cycle=nodes.Any(n=>!colors.ContainsKey(n.StepId)&&Cycle(n.StepId));
            var resourceHistory = new List<TrafficLifecycleEventEntity>();
            if (!string.IsNullOrWhiteSpace(root.MapId) && !string.IsNullOrWhiteSpace(root.MapVersionId))
            {
                // Exact JSON membership, scoped to the original request's map version.
                // No LIKE substring matches (e.g. WAIT-01 vs WAIT-010), no cross-map stitching.
                resourceHistory = await database.Ado.SqlQueryAsync<TrafficLifecycleEventEntity>("""
                    SELECT e.* FROM traffic_lifecycle_event e
                    JOIN traffic_request r ON r.StepId=e.StepId AND r.TaskId=e.TaskId AND r.VehicleId=e.VehicleId
                    WHERE r.MapId=@map AND r.MapVersionId=@version
                      AND e.Kind IN ('ResourceAcquired','ResourceReleased','SegmentReleased')
                      AND EXISTS (SELECT 1 FROM json_each(CASE WHEN json_valid(e.ResourceIds) THEN e.ResourceIds ELSE '[]' END) a
                                  JOIN json_each(@resources) b ON a.value=b.value COLLATE NOCASE)
                    ORDER BY e.OccurredAt DESC,e.rowid DESC LIMIT 51
                    """, new SugarParameter("@map",root.MapId), new SugarParameter("@version",root.MapVersionId),
                    new SugarParameter("@resources",root.ResourceIds));
            }
            result=new(now,query.TaskId,query.StepId,true,nodes[0].State=="Waiting"?"只读阻塞关系与规则要求；不是释放许可或死锁自动判定。":"原记录状态已变化，请核对当前状态；不沿用旧等待结论。",nodes,links,history.Take(50).ToArray(),limited,events.Count>50||audits.Count>50||history.Length>50,cycle)
            {
                ResourceHistory=resourceHistory.Take(50).Select(e=>new TrafficLifecycleEvent(e.EventId,e.StepId,e.TaskId,e.VehicleId,
                    e.Kind,e.Reason,System.Text.Json.JsonSerializer.Deserialize<string[]>(e.ResourceIds)??[],new(e.OccurredAt))).ToArray(),
                ResourceHistoryTruncated=resourceHistory.Count>50
            };
        });
        if(!transaction.IsSuccess)throw new InvalidOperationException("读取阻塞链快照失败。",transaction.ErrorException);return result!;
    }
}
