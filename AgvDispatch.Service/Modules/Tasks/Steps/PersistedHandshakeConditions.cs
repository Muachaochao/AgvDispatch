using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public static class PersistedHandshakeConditions
{
    public static IReadOnlyList<TaskPlcCondition> Observe(HandshakeCheckpoint checkpoint,DispatchTaskStepType type,string station,string dumper,IPlcSignalCache cache,DateTimeOffset now,int maxAge)
    {
        var list=new List<(string Station,string Variable,bool Expected)>();
        void Add(string s,string v,bool e)=>list.Add((s,v,e));
        if(type is DispatchTaskStepType.PickupFullBox or DispatchTaskStepType.PlaceEmptyBox or DispatchTaskStepType.CompletePlcHandshake){Add(station,"Faulted",false);Add(station,"CommunicationOnline",true);}
        if(type is DispatchTaskStepType.WaitForDumperReady or DispatchTaskStepType.DumpFullBox or DispatchTaskStepType.CompletePlcHandshake)Add(dumper,"Faulted",false);
        if(checkpoint.PendingSignal is {} pending)
        {
            var parts=pending.Split('/');if(parts.Length==2&&checkpoint.PendingValue is {} value)Add(parts[0],parts[1],value);
        }
        else switch(checkpoint.Phase)
        {
            case "AwaitBoxAbsent":Add(station,"BoxPresent",false);break;
            case "NotifyRemoved":Add(station,"FullBoxRemoved",true);break;
            case "NotifyEmptyResponsibility":Add(station,"EmptyBoxTaskAcknowledged",true);break;
            case "AwaitPickupReadback":Add(station,"BoxPresent",false);Add(station,"FullBoxRemoved",true);Add(station,"EmptyBoxTaskAcknowledged",true);break;
            case "ClearCallResponse":case "ClearRemainingCall":Add(station,"FullBoxRequestAcknowledged",false);break;
            case "ClearRemoved":case "ClearRemainingRemoved":Add(station,"FullBoxRemoved",false);break;
            case "AwaitReadyBaseline":Add(dumper,"AgvAtWaitingPosition",false);Add(dumper,"DumpingCompletedMayLeave",false);break;
            case "NotifyWaiting":Add(dumper,"AgvAtWaitingPosition",true);break;
            case "AwaitReady":Add(dumper,"AgvAtWaitingPosition",true);Add(dumper,"DumperReady",true);break;
            case "ConsumeReady":Add(dumper,"AgvAtWaitingPosition",false);break;
            case "AwaitReadyConsumed":Add(dumper,"AgvAtWaitingPosition",false);Add(dumper,"DumperReady",false);break;
            case "AwaitDoneBaseline":Add(dumper,"AgvAtDumpingPosition",false);Add(dumper,"DumpingCompletedMayLeave",false);break;
            case "NotifyDumping":Add(dumper,"AgvAtDumpingPosition",true);Add(dumper,"DumpingCompletedMayLeave",false);break;
            case "AwaitCurrentDone":Add(dumper,"AgvAtDumpingPosition",true);Add(dumper,"DumpingCompletedMayLeave",true);break;
            case "NotifyPlaced":Add(station,"EmptyBoxPlaced",true);break;
            case "AwaitBoxPresent":Add(station,"EmptyBoxPlaced",true);Add(station,"BoxPresent",true);break;
            case "AwaitReturnEvidence":Add(station,"BoxPresent",true);break;
            case "ClearDumpingAfterReturn":Add(dumper,"AgvAtDumpingPosition",false);break;
            case "AwaitDumperClosed":Add(dumper,"AgvAtDumpingPosition",false);Add(dumper,"AgvAtWaitingPosition",false);Add(dumper,"DumpingCompletedMayLeave",false);break;
            case "ClearEmptyResponsibility":Add(station,"EmptyBoxTaskAcknowledged",false);break;
            case "ClearPlaced":Add(station,"EmptyBoxPlaced",false);break;
            case "ReadyToComplete" when type==DispatchTaskStepType.PickupFullBox:
                Add(station,"BoxPresent",false);Add(station,"FullBoxRemoved",false);Add(station,"EmptyBoxTaskAcknowledged",true);Add(station,"FullBoxRequestAcknowledged",false);break;
            case "ReadyToComplete" when type==DispatchTaskStepType.WaitForDumperReady:
                Add(dumper,"AgvAtWaitingPosition",false);Add(dumper,"DumperReady",false);break;
            case "ReadyToComplete" when type==DispatchTaskStepType.DumpFullBox:
                Add(dumper,"AgvAtDumpingPosition",true);Add(dumper,"DumpingCompletedMayLeave",true);break;
            case "ReadyToComplete" when type==DispatchTaskStepType.PlaceEmptyBox:
                Add(station,"BoxPresent",true);Add(station,"EmptyBoxPlaced",true);break;
            case "ReadyToComplete" when type==DispatchTaskStepType.CompletePlcHandshake:
            case "VerifyClosure":Add(station,"BoxPresent",true);foreach(var v in new[]{"FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced"})Add(station,v,false);Add(dumper,"AgvAtDumpingPosition",false);Add(dumper,"AgvAtWaitingPosition",false);Add(dumper,"DumpingCompletedMayLeave",false);break;
        }
        return list.Distinct().Select(r=>
        {
            cache.TryGet(r.Station,r.Variable,out var snapshot);
            return TaskSignalPredicate.EvaluateFresh(r.Station,r.Variable,r.Expected,snapshot,now,maxAge,checkpoint.PendingSignal!=null?"待人工核对写入；不自动放行":"新契约当前阶段条件/目标");
        }).ToArray();
    }
}
