namespace PlcHandshakeLab;

public sealed record FleetLabRequest(string RunId,string Action,string[] Stations,string? TaskId=null,bool RecoveryReviewed=false);
public sealed partial class NetworkLab
{
    private sealed record FleetCall(bool Called=false,string? Task=null,string? Vehicle=null,CycleTransportFeedback? Pickup=null,CycleTransportFeedback? Place=null);
    private string? _fleetRun;
    private bool _fleetStopped;
    private readonly Dictionary<string,FleetCall> _fleet=[];
    public void ControlFleet(FleetLabRequest request)
    {
        lock(_gate)
        {
            if(!Guid.TryParseExact(request.RunId,"N",out _))throw new ArgumentException("双车测试编号无效。");
            if(request.Action=="start"&&_fleetRun!=request.RunId)
            {
                if(_fleetRun!=null&&!_fleetStopped||_callRun!=null&&!_stopCalls&&_callCompleted<_callLimit||
                    !_communicationHealthy||_recoveryReason!=null||_permit||
                    Config.Points.Any(p=>p.Owner=="H"&&Get(p.Station,p.Signal)||p.Signal==0&&Get(p.Station,0))||
                    Enumerable.Range(6,4).Any(s=>Get(NetworkLabConfig.Shared,s)))
                    throw new InvalidOperationException("原运行、通信或握手基线未闭合。");
                _fleet.Clear();_fleetRun=request.RunId;_fleetStopped=false;SetAutomatic(true);
                foreach(var station in Config.Points.Where(p=>p.Signal==0&&p.Station!=NetworkLabConfig.Shared).Select(p=>p.Station))_fleet.Add(station,new());
            }
            if(_fleetRun!=request.RunId)throw new InvalidOperationException("模拟器原双车会话不存在；不得覆盖点位重建。");
            if(request.Action=="stop")_fleetStopped=true;
            else if(request.Action=="call")
            {
                if(_fleetStopped||!_communicationHealthy||_recoveryReason!=null||Get(NetworkLabConfig.Shared,12))
                    throw new InvalidOperationException("已停止新增、通信失效或存在恢复/故障门禁。");
                if(request.Stations is not {Length:>0 and <=1000}||request.Stations.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=request.Stations.Length||request.Stations.Any(s=>!_fleet.ContainsKey(s)))
                    throw new ArgumentException("呼叫工位无效。");
                foreach(var station in request.Stations)
                    if(!_fleet[station].Called&&(Get(station,0)||Get(station,10)||!Get(station,11)||Enumerable.Range(2,4).Any(s=>Get(station,s))))
                        throw new InvalidOperationException(station+"未满足握手基线。");
                // The complete batch is atomic with respect to S7 reads.
                foreach(var station in request.Stations)
                {
                    var entry=_fleet[station];if(entry.Called)continue;
                    if(!Get(station,1))
                        Set(Find(station,1),true,"双车空载模拟：注入本轮满箱在位假信号；非物理箱体证据");
                    _fleet[station]=entry with {Called=true};Set(Find(station,0),true,"双车用例模拟满箱呼叫");
                }
            }
            else if(request.Action=="bind")
            {
                if(request.Stations is not {Length:1}||!_fleet.TryGetValue(request.Stations[0],out var entry)||!entry.Called||string.IsNullOrWhiteSpace(request.TaskId))
                    throw new InvalidOperationException("绑定须对应已发呼叫和原任务。");
                if(entry.Task!=null&&entry.Task!=request.TaskId||_fleet.Any(p=>p.Key!=request.Stations[0]&&p.Value.Task==request.TaskId))
                    throw new InvalidOperationException("原任务/工位绑定冲突。");
                _fleet[request.Stations[0]]=entry with {Task=request.TaskId};
            }
            else if(request.Action=="check"&&request.RecoveryReviewed)
            {
                if(!_communicationHealthy)throw new InvalidOperationException("模拟PLC通信尚未恢复。");
                _recoveryReason=null;
            }
            else if(request.Action is not ("start" or "check"))throw new ArgumentException("未知双车操作。");
            Record("双车用例",string.Join(",",request.Stations),"","","",System.Text.Json.JsonSerializer.Serialize(request));
        }
    }
    public void ApplyFleetFeedback(CycleTransportFeedback feedback)
    {
        lock(_gate)
        {
            if(feedback.RunId!=_fleetRun||!_fleet.TryGetValue(feedback.Station,out var entry)||!entry.Called||entry.Task!=feedback.TaskId||
                !_automatic||!_communicationHealthy||_recoveryReason!=null||Get(feedback.Station,10)||!Get(feedback.Station,11)||Get(NetworkLabConfig.Shared,12))
                throw new InvalidOperationException("双车反馈不属于当前有效任务/工位会话。");
            var now=DateTimeOffset.UtcNow;
            if(feedback.ObservedAt>now||now-feedback.ObservedAt>TimeSpan.FromSeconds(5)||
                new[]{feedback.VehicleId,feedback.StepId,feedback.VendorTaskId}.Any(string.IsNullOrWhiteSpace)||entry.Vehicle!=null&&entry.Vehicle!=feedback.VehicleId)
                throw new InvalidOperationException("车辆身份、动作记录或新鲜度无效。");
            var previous=feedback.Action=="Pickup"?entry.Pickup:entry.Place;
            if(previous!=null)
            {
                if(previous.StepId!=feedback.StepId||previous.VendorTaskId!=feedback.VendorTaskId||previous.VehicleId!=feedback.VehicleId)
                    throw new InvalidOperationException("模拟反馈重试身份改变。");
                return;
            }
            if(feedback.Action=="Pickup")
            {
                if(Get(feedback.Station,0)||!Get(feedback.Station,2)||Get(feedback.Station,3))throw new InvalidOperationException("须先受理并撤呼叫。");
                if(_fleet.Any(p=>p.Key!=feedback.Station&&p.Value.Vehicle==feedback.VehicleId&&p.Value.Place==null))
                    throw new InvalidOperationException("该车辆仍有另一工位的返箱责任。");
                _fleet[feedback.Station]=entry with {Vehicle=feedback.VehicleId,Pickup=feedback};
                Set(Find(feedback.Station,1),false,"双车原取箱动作完成，模拟无箱（非物理证据）");
            }
            else if(feedback.Action=="Place")
            {
                if(entry.Pickup==null||!Get(feedback.Station,4))throw new InvalidOperationException("缺少原取箱及返箱责任。");
                _fleet[feedback.Station]=entry with {Place=feedback};
                Set(Find(feedback.Station,1),true,"双车原返箱动作完成，模拟有箱（非物理证据）");
            }
            else throw new ArgumentException("未知动作。");
            Record("双车动作证据",feedback.Station,"","","",System.Text.Json.JsonSerializer.Serialize(feedback));
        }
    }
}
