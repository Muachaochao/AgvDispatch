using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed class TaskHandshakeStore(ISqlSugarClient database) : ITaskHandshakeStore
{
    public async Task<bool> RecordExitAsync(HandshakeCheckpoint checkpoint,TaskExitEvidence evidence,CancellationToken ct)
    {
        if(checkpoint.Phase!="AwaitReturnEvidence"||evidence.TaskId!=checkpoint.TaskId||evidence.StepId!=checkpoint.StepId||evidence.MapId.Length==0||evidence.VendorTaskId.Length==0)
            throw new InvalidOperationException("离站核对身份或阶段不一致。");
        var moved=false;
        var result=await database.Ado.UseTranAsync(async()=>
        {
            await EnsureReadyAsync(checkpoint.TaskId,checkpoint.StepId,ct);
            if(!await database.Queryable<DispatchTaskStepEntity>().Where(s=>s.TaskId==checkpoint.TaskId&&s.StepId==evidence.TransportStepId&&s.StepType==70&&s.Status==50&&s.VendorTaskId==evidence.VendorTaskId&&s.VehicleId==evidence.VehicleId).AnyAsync(ct))
                throw new InvalidOperationException("原返程终态与离站证据不匹配。");
            var now=DateTime.UtcNow;
            moved=await database.Updateable<TaskHandshakeEntity>().SetColumns(h=>new TaskHandshakeEntity{Phase="ClearDumpingAfterReturn",Revision=checkpoint.Revision+1,UpdatedAtUtc=now,Reason="原返程完成及新鲜原站定位证据已持久化；不推断更早的中途出区时刻。"})
                .Where(h=>h.TaskId==checkpoint.TaskId&&h.StepId==checkpoint.StepId&&h.Revision==checkpoint.Revision&&h.Phase=="AwaitReturnEvidence").ExecuteCommandAsync(ct)==1;
            if(moved)await database.Insertable(new TaskRuntimeEventEntity{TaskId=checkpoint.TaskId,StepId=checkpoint.StepId,Kind="ExitVerified",Phase="ClearDumpingAfterReturn",RecordedAtUtc=now,
                Reason="保守返程到位核对，不代表精确中途出区时间；仍等待PLC周期闭合。",EvidenceJson=JsonSerializer.Serialize(evidence)}).ExecuteCommandAsync(ct);
        });
        if(!result.IsSuccess)throw new InvalidOperationException("离站核对及阶段事务未提交。",result.ErrorException);
        return moved;
    }
    public async Task<string?> TryClaimEquipmentAsync(string taskId,string stepId,string equipmentId,CancellationToken ct)
    {
        string? owner=null;
        var result=await database.Ado.UseTranAsync(async()=>
        {
            await EnsureReadyAsync(taskId,stepId,ct);
            var existing=await database.Queryable<TaskEquipmentClaimEntity>().Where(e=>e.EquipmentId==equipmentId).FirstAsync(ct);
            if(existing!=null && existing.TaskId!=taskId)
            {
                // Reconcile only a finished new-contract cycle, never a fault/cancelled or expired claim.
                var prior=await database.Queryable<DispatchTaskEntity>().Where(t=>t.TaskId==existing.TaskId && t.Stage==90).FirstAsync(ct);
                var close=await database.Queryable<DispatchTaskStepEntity>().Where(s=>s.TaskId==existing.TaskId && s.StepType==90 && s.Status==50).FirstAsync(ct);
                var proof=close==null?null:await database.Queryable<TaskHandshakeEntity>().Where(h=>h.TaskId==existing.TaskId && h.StepId==close.StepId && h.Phase=="ReadyToComplete").FirstAsync(ct);
                if(prior!=null&&proof!=null&&proof.ContractVersion==HandshakeExecutionOptions.Version&&proof.PendingSignal==null)
                {
                    await database.Deleteable<TaskEquipmentClaimEntity>().Where(e=>e.EquipmentId==equipmentId && e.TaskId==existing.TaskId).ExecuteCommandAsync(ct);
                    await database.Insertable(new TaskRuntimeEventEntity{TaskId=existing.TaskId,StepId=close!.StepId,Kind="EquipmentReleased",StationId=equipmentId,RecordedAtUtc=DateTime.UtcNow,Reason="后续申请核对到原新契约任务和收尾均已完成，清理其遗留设备归属。"}).ExecuteCommandAsync(ct);
                    existing=null;
                }
            }
            if(existing!=null){if(existing.TaskId!=taskId)owner=existing.TaskId;return;}
            await database.Insertable(new TaskEquipmentClaimEntity{EquipmentId=equipmentId,TaskId=taskId,AcquiredAtUtc=DateTime.UtcNow}).ExecuteCommandAsync(ct);
            await database.Insertable(new TaskRuntimeEventEntity{TaskId=taskId,StepId=stepId,Kind="EquipmentClaimed",StationId=equipmentId,RecordedAtUtc=DateTime.UtcNow,Reason="本轮独占倒料机握手，等待位容量仍由地图交通资源单独管理。"}).ExecuteCommandAsync(ct);
        });
        if(!result.IsSuccess)throw new InvalidOperationException("设备归属申请事务失败，保持等待。",result.ErrorException);
        return owner;
    }

    public async Task ReleaseEquipmentAsync(string taskId,CancellationToken ct)
    {
        var result=await database.Ado.UseTranAsync(async()=>
        {
            if(!await database.Queryable<DispatchTaskEntity>().Where(t=>t.TaskId==taskId && t.Stage==90).AnyAsync(ct))throw new InvalidOperationException("任务尚未完成，不自动释放设备归属。");
            var count=await database.Deleteable<TaskEquipmentClaimEntity>().Where(e=>e.TaskId==taskId).ExecuteCommandAsync(ct);
            if(count>0)await database.Insertable(new TaskRuntimeEventEntity{TaskId=taskId,Kind="EquipmentReleased",RecordedAtUtc=DateTime.UtcNow,Reason="本轮新契约完成收尾后释放设备归属。"}).ExecuteCommandAsync(ct);
        });
        if(!result.IsSuccess)throw new InvalidOperationException("设备归属释放事务失败。",result.ErrorException);
    }
    public static HandshakeCheckpoint Describe(TaskHandshakeEntity e) => new(e.TaskId, e.StepId, e.CycleId, e.ContractVersion,
        e.Phase, e.Revision, e.Reason, e.PendingSignal, e.PendingValue, e.NextPhase, Utc(e.StartedAtUtc), Utc(e.UpdatedAtUtc),
        JsonSerializer.Deserialize<TaskSignalEvidence[]>(e.BaselineJson) ?? []);
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public async Task<HandshakeCheckpoint?> GetAsync(string taskId, string stepId, CancellationToken ct)
    {
        var row = await database.Queryable<TaskHandshakeEntity>().Where(e => e.TaskId == taskId && e.StepId == stepId).FirstAsync(ct);
        return row == null ? null : Describe(row);
    }

    public async Task<HandshakeCheckpoint> BeginAsync(DispatchTaskStepWorkItem step, string contractVersion, string initialPhase,
        IReadOnlyList<TaskSignalEvidence> baseline, CancellationToken ct)
    {
        var existing = await GetAsync(step.TaskId, step.StepId, ct);
        if (existing != null)
        {
            if (existing.ContractVersion != contractVersion) throw new InvalidOperationException("握手契约版本不一致，保留原阶段，禁止迁移重跑。");
            return existing;
        }
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            await EnsureReadyAsync(step.TaskId, step.StepId, ct);
            var row = new TaskHandshakeEntity { TaskId=step.TaskId,StepId=step.StepId,CycleId=step.TaskId,ContractVersion=contractVersion,
                Phase=initialPhase,Revision=1,Reason="原始基线，仅证明采集时观察；不等于PLC业务接收。",BaselineJson=JsonSerializer.Serialize(baseline),StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow };
            await database.Insertable(row).ExecuteCommandAsync(ct);
            await EventAsync(row, "HandshakeStarted", row.Reason, ct);
        });
        if (!transaction.IsSuccess)
        {
            existing = await GetAsync(step.TaskId, step.StepId, ct);
            if (existing != null && existing.ContractVersion == contractVersion) return existing;
            throw new InvalidOperationException("握手初始阶段未能保存，禁止执行。", transaction.ErrorException);
        }
        return (await GetAsync(step.TaskId,step.StepId,ct))!;
    }

    private async Task EnsureReadyAsync(string taskId, string stepId, CancellationToken ct)
    {
        var task = await database.Queryable<DispatchTaskEntity>().Where(t=>t.TaskId==taskId).FirstAsync(ct);
        if (task == null || task.Stage >= 90 || !await database.Queryable<DispatchTaskStepEntity>().Where(s=>s.TaskId==taskId && s.StepId==stepId && s.Status==10).AnyAsync(ct))
            throw new InvalidOperationException("任务已冻结/结束或步骤不再Ready，禁止推进握手。");
    }

    public async Task<bool> MoveAsync(HandshakeCheckpoint expected, string phase, string reason, string? pendingSignal, bool? pendingValue, string? nextPhase, CancellationToken ct)
    {
        var changed = false;
        var result = await database.Ado.UseTranAsync(async () =>
        {
            await EnsureReadyAsync(expected.TaskId,expected.StepId,ct);
            var now=DateTime.UtcNow;
            changed = await database.Updateable<TaskHandshakeEntity>().SetColumns(e=>new TaskHandshakeEntity {
                Phase=phase,Reason=reason,PendingSignal=pendingSignal,PendingValue=pendingValue,NextPhase=nextPhase,Revision=expected.Revision+1,UpdatedAtUtc=now })
                .Where(e=>e.StepId==expected.StepId && e.TaskId==expected.TaskId && e.Revision==expected.Revision && e.ContractVersion==expected.ContractVersion).ExecuteCommandAsync(ct)==1;
            if (changed) await EventAsync(new TaskHandshakeEntity{TaskId=expected.TaskId,StepId=expected.StepId,Phase=phase,PendingSignal=pendingSignal,PendingValue=pendingValue,UpdatedAtUtc=now},"HandshakeTransition",reason,ct);
        });
        if (!result.IsSuccess) throw new InvalidOperationException("握手阶段事务未提交，保持原阶段。",result.ErrorException);
        return changed;
    }

    private Task EventAsync(TaskHandshakeEntity row,string kind,string reason,CancellationToken ct) => database.Insertable(new TaskRuntimeEventEntity {
        TaskId=row.TaskId,StepId=row.StepId,Kind=kind,Phase=row.Phase,RecordedAtUtc=row.UpdatedAtUtc,
        StationId=row.PendingSignal?.Split('/').FirstOrDefault()??"",Signal=row.PendingSignal?.Split('/').LastOrDefault()??"",Value=row.PendingValue,Quality=row.PendingSignal==null?"":"Intent",
        Reason=row.PendingSignal==null?reason:$"{row.PendingSignal} 目标={row.PendingValue}；{reason}" }).ExecuteCommandAsync(ct);

    public async Task RecordSignalsAsync(string taskId,string stepId,IReadOnlyList<TaskSignalEvidence> signals,CancellationToken ct)
    {
        var result=await database.Ado.UseTranAsync(async()=>
        {
            foreach(var signal in signals)
            {
                var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]{taskId,signal.StationId,signal.VariableName}))));
                var old=await database.Queryable<TaskSignalLastEntity>().Where(e=>e.Key==key).FirstAsync(ct);
                if(old!=null && old.Value==signal.Value && old.Quality==signal.Quality && old.Address==signal.Address)continue;
                await database.Insertable(new TaskRuntimeEventEntity{TaskId=taskId,StepId=stepId,Kind=old==null?"SignalInitial":"SignalChanged",StationId=signal.StationId,
                    Signal=signal.VariableName,PreviousValue=old?.Value,Value=signal.Value,Quality=signal.Quality,ObservedAtUtc=signal.ObservedAt?.UtcDateTime,RecordedAtUtc=DateTime.UtcNow,
                    Reason=old==null?"首次观察，不推断此前边沿。":"采样观察到值、质量或地址变化；不保证捕获采样间全部变化。"}).ExecuteCommandAsync(ct);
                var row=new TaskSignalLastEntity{Key=key,TaskId=taskId,StationId=signal.StationId,Signal=signal.VariableName,Value=signal.Value,Quality=signal.Quality,Address=signal.Address};
                if(old==null)await database.Insertable(row).ExecuteCommandAsync(ct);else await database.Updateable(row).ExecuteCommandAsync(ct);
            }
        });
        if(!result.IsSuccess)throw new InvalidOperationException("信号变化记录事务失败。",result.ErrorException);
    }

    public async Task<TaskRuntimePage> QueryAsync(TaskRuntimeQuery q,CancellationToken ct)
    {
        TaskRuntimeQueryValidation.Validate(q);
        if(!await database.Queryable<DispatchTaskEntity>().Where(t=>t.TaskId==q.TaskId).AnyAsync(ct))throw new ArgumentException("任务不存在。");
        var upper=q.ThroughSequence??await database.Queryable<TaskRuntimeEventEntity>().Where(e=>e.TaskId==q.TaskId).MaxAsync(e=>e.Sequence,ct);
        var query=database.Queryable<TaskRuntimeEventEntity>().Where(e=>e.TaskId==q.TaskId && e.Sequence<=upper);
        if(q.Kind=="Signals")query=query.Where(e=>e.Kind=="SignalInitial"||e.Kind=="SignalChanged");
        else if(q.Kind.Length>0)query=query.Where(e=>e.Kind==q.Kind);
        if(q.StepId.Length>0)query=query.Where(e=>e.StepId==q.StepId);
        if(q.Signal.Length>0)query=query.Where(e=>e.Signal==q.Signal);
        if(q.From is {} from)query=query.Where(e=>e.RecordedAtUtc>=from.UtcDateTime);
        if(q.To is {} to)query=query.Where(e=>e.RecordedAtUtc<=to.UtcDateTime);
        var count=await query.Clone().CountAsync(ct);
        var rows=await query.OrderBy(e=>e.Sequence,OrderByType.Desc).Skip((q.Page-1)*q.PageSize).Take(q.PageSize).ToListAsync(ct);
        return new(rows.Select(e=>new TaskRuntimeEvent(e.Sequence,e.TaskId,e.StepId,e.Kind,e.Phase,e.StationId,e.Signal,e.PreviousValue,e.Value,e.Quality,
            e.ObservedAtUtc is {} observed?Utc(observed):null,Utc(e.RecordedAtUtc),e.Reason){Evidence=e.EvidenceJson is {Length:>4096}?"仅显示前4096字符："+e.EvidenceJson[..4096]:e.EvidenceJson??""}).ToArray(),count,q.Page,q.PageSize,upper);
    }
}
