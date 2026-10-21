using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Infrastructure.Persistence.Entities;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class ExecutionRecoveryService
{
    private async Task<string?> HandshakeReviewBlockAsync(string taskId,string stepId,RecoveryDecisionRequest request,CancellationToken ct)
    {
        if(await database.Queryable<TaskHandshakeEntity>().Where(e=>e.TaskId==taskId&&e.ContractVersion!=HandshakeExecutionOptions.Version).AnyAsync(ct))
            return "原任务含其他版本握手记录，当前28阶段不能续接或补记；不迁移、不重置原任务，请专项核对。";
        var state=await database.Queryable<TaskHandshakeEntity>().Where(e=>e.TaskId==taskId&&e.StepId==stepId).FirstAsync(ct);
        if(state==null)return null;
        if(state.ContractVersion!=HandshakeExecutionOptions.Version)return "原任务握手契约不是当前28阶段版本，禁止变更旧检查点或静默迁移；保留原任务并专项核对。";
        if(state.Phase=="ReviewRequired")return "原始握手归属不明：本入口不重建周期或清设备位，需专项核对原现场流程。";
        // A return transport can finish while recovery holds the execution barrier closed.
        // Recording only that mechanical terminal must not depend on the exit monitor,
        // which cannot run until this recovery batch is released. Its PLC/ownership
        // checkpoint remains intact and still gates placement and final closure.
        var returnExit=await database.Queryable<DispatchTaskStepEntity>().Where(s=>s.TaskId==taskId&&s.StepId==stepId&&s.StepType==70).AnyAsync(ct)&&
            (state.Phase is "AwaitPhysicalExit" or "ClearDumpingAfterExit" or "AwaitDumperClosed" or "ReleaseDumper" or "ExitHandoverComplete" ||
             state.Phase=="WritePending"&&state.PendingSignal!=null&&state.NextPhase=="AwaitDumperClosed");
        if(state.Phase is not ("TransportBound" or "ExitHandoverComplete")&&!returnExit&&request.Decision==RecoveryDecision.RecordPhysicalCompletion)return "28阶段PLC/出区交接不能跳过持久化阶段；请核对后从原节点续接。";
        if(state.PendingSignal==null)return null;
        if((request.Decision!=RecoveryDecision.ContinueOriginal&&!(returnExit&&request.Decision==RecoveryDecision.RecordPhysicalCompletion))||!request.HandshakePendingWriteConfirmed)return "待确认PLC写入必须明确勾选现场已核对；仅原返程运输可在同时核实机械终态时补记，PLC步骤不能补记完成。";
        var parts=state.PendingSignal.Split('/');
        if(parts.Length!=2||state.PendingValue==null||string.IsNullOrWhiteSpace(state.NextPhase))return "待确认写入记录不完整，禁止续接。";
        var original=TaskHandshakeStore.Describe(state).Baseline.FirstOrDefault(s=>s.StationId==parts[0]&&s.VariableName==parts[1]);
        if(!signals.TryGet(parts[0],parts[1],out var value)||!value.Enabled||value.Quality!=SignalQuality.Good||value.Value!=state.PendingValue||original==null||original.Address!=value.Address||value.UpdatedAt is not {} time||
            DateTimeOffset.Now-time>settings.StateMaxAge||time>DateTimeOffset.Now.AddSeconds(1)||time<_started)
            return "待确认写入缺少本次恢复中新鲜、同地址期望值回读；不自动补写或重发。";
        return null;
    }

    private async Task ResolveHandshakeWriteAsync(string taskId,string stepId,RecoveryDecisionRequest request,string actor,CancellationToken ct)
    {
        var state=await database.Queryable<TaskHandshakeEntity>().Where(e=>e.TaskId==taskId&&e.StepId==stepId).FirstAsync(ct);
        if(state?.PendingSignal==null)return;
        var block=await HandshakeReviewBlockAsync(taskId,stepId,request,ct);
        if(block!=null)throw new InvalidOperationException(block);
        var now=DateTime.UtcNow;
        var changed=await database.Updateable<TaskHandshakeEntity>().SetColumns(e=>new TaskHandshakeEntity {
            Phase=state.NextPhase!,PendingSignal=null,PendingValue=null,NextPhase=null,Revision=state.Revision+1,UpdatedAtUtc=now,
            Reason="人工核对原写入结果后续接；不等于PLC业务ACK。"}).Where(e=>e.StepId==stepId&&e.TaskId==taskId&&e.Revision==state.Revision).ExecuteCommandAsync(ct);
        if(changed!=1)throw new InvalidOperationException("握手阶段已变化，恢复确认未提交。");
        await database.Insertable(new TaskRuntimeEventEntity {TaskId=taskId,StepId=stepId,Kind="HandshakeReviewed",Phase=state.NextPhase!,RecordedAtUtc=now,
            Reason="现场核对原写入后续接；操作人："+actor+"；依据："+request.Reason}).ExecuteCommandAsync(ct);
    }
}
