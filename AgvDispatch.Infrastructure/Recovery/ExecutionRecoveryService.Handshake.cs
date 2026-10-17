using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Infrastructure.Persistence.Entities;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class ExecutionRecoveryService
{
    private async Task<string?> HandshakeReviewBlockAsync(string taskId,string stepId,RecoveryDecisionRequest request,CancellationToken ct)
    {
        var state=await database.Queryable<TaskHandshakeEntity>().Where(e=>e.TaskId==taskId&&e.StepId==stepId).FirstAsync(ct);
        if(state==null)return null;
        if(state.Phase=="ReviewRequired")return "原始握手归属不明：本入口不重建周期或清设备位，需专项核对原现场流程。";
        if(state.Phase!="TransportBound"&&request.Decision==RecoveryDecision.RecordPhysicalCompletion)return "新契约PLC步骤不能跳过持久化阶段；请核对后从原节点续接。";
        if(state.PendingSignal==null)return null;
        if(request.Decision!=RecoveryDecision.ContinueOriginal||!request.HandshakePendingWriteConfirmed)return "待确认PLC写入必须明确勾选现场已核对，并使用原节点续接；不能补记为机械完成。";
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
