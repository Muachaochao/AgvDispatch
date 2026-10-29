using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Service;

internal static class StandbySendChecks
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        var state=new State();
        var runtime=new ActiveMapRuntime();
        runtime.Replace(new("M","V",new("standby","","RobotWorld","m",
            [new("P",0,0,MapNodeType.Station,true,[],"LM1"),
             new("B",1,0,MapNodeType.Station,true,[],"LM29")],[],[]),DateTimeOffset.UtcNow));
        var gate=new MapExecutionGate();gate.SetMode(MapExecutionMode.Running);
        var path=Path.Combine(Path.GetTempPath(),"fleet-standby-intent-"+Guid.NewGuid().ToString("N")+".json");
        using var cancellation=new CancellationTokenSource();
        FleetTestStandbyReturn? persisted=null;
        var gateway=new Gateway(()=>
        {
            // Inspect the durable intent while the gateway is inside its only send.
            persisted=JsonSerializer.Deserialize<FleetTestStandbyReturn>(File.ReadAllText(path));
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var port=new FleetTestPort(new(){Enabled=true,VehicleIds=["AGV-01","AGV-02"]},
            null!,null!,null!,null!,null!,null!,null!,null!,state,runtime,gate,
            new(new(),new()),new(),new(),new(),new Preflight(),null!,new Planner(),null!,gateway,true);
        var plan=new FleetTestStart(Guid.NewGuid().ToString("N"),"Simultaneous",["AGV-01","AGV-02"],true)
            {StationIds=["CNC-001","CNC-002"]};
        var run=new FleetTestRun(Guid.NewGuid().ToString("N"),plan,"M","V","initial","ReturningStandby","",false,
            DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,[]);
        var assignment=new FleetTestStandbyReturn("AGV-01","B","LM29",FleetTestStandbyReturnStates.Sending,"",null,DateTimeOffset.UtcNow);
        try
        {
            await port.TrySendStandbyReturnAsync(run,[assignment],assignment,intent=>
                File.WriteAllText(path,JsonSerializer.Serialize(intent)),cancellation.Token);
            throw new Exception("发送后取消必须返回结果不确定");
        }
        catch(FleetStandbySendUncertainException error)
        {
            check(error.InnerException is OperationCanceledException,"发送开始后取消保留原取消异常并归为Uncertain");
        }
        check(gateway.Sends==1&&persisted is {VendorTaskId:not null}&&
            persisted.VendorTaskId=="FLEET-STANDBY-"+run.RunId+"-AGV-01"&&persisted.Route.SequenceEqual(["LM1","LM29"]),
            "在网关发送前已持久化稳定厂家编号和完整原路线");
        try
        {
            await port.TrySendStandbyReturnAsync(run,[assignment],assignment,
                _=>throw new IOException("intent storage failed"),default);
            throw new Exception("意图保存失败必须拒绝发送");
        }
        catch(IOException){check(gateway.Sends==1,"持久化发送意图失败时不调用网关发送");}
        var returning=assignment with {State=FleetTestStandbyReturnStates.Returning,VendorTaskId="original",Route=["LM1","LM29"]};
        state.Alarms=[new("52100",AgvAlarmSeverity.Error,"motor blocked")];
        await RejectObservation("待机途中非52200错误不因blocked文字而被忽略");
        state.Alarms=[new("52200",AgvAlarmSeverity.Fatal,"blocked")];
        await RejectObservation("待机途中Fatal即使编号52200也必须暂停");
        state.Alarms=[new("52200",AgvAlarmSeverity.Error,"blocked")];state.Blocked=true;
        check((await port.ObserveStandbyReturnAsync(run,returning,default)).State==FleetTestStandbyReturnStates.Returning,
            "待机途中确认52200局部避让继续查询原任务不冻结全组");
        check(gateway.Sends==1,"待机健康核验与原任务查询均不重发导航");
        gateway.Status=AgvTransportTaskState.Unknown;
        await RejectObservation("原任务全链未知须暂停核对，不能无限显示正常返回中");
        try
        {
            await port.ReconcileStandbyReturnsAsync(run with {StandbyReturns=[returning]},default);
            throw new Exception("核对不能接受未知全链");
        }
        catch(InvalidOperationException){check(true,"核对待机不能将404或缺段的未知任务标为可继续运行");}
        File.Delete(path);
        async Task RejectObservation(string message)
        {
            try{await port.ObserveStandbyReturnAsync(run,returning,default);throw new Exception(message);}
            catch(InvalidOperationException){check(true,message);}
        }
    }

    private sealed class State:IAgvStateCache
    {
        public IReadOnlyList<AgvAlarmSnapshot> Alarms=[];
        public bool Blocked;
        public event Action<AgvRobotStateChanged>? StateChanged {add{} remove{}}
        public long Version=>1;
        public IReadOnlyList<AgvRobotSnapshot> GetAll()=>[Snapshot()];
        public bool TryGet(string id,out AgvRobotSnapshot snapshot){snapshot=Snapshot();return id=="AGV-01";}
        private AgvRobotSnapshot Snapshot()=>new("AGV-01","AGV-01","synthetic",0,ConnectionState.Online,
            SignalQuality.Good,0,0,0,.99,"LM1",null,90,Blocked,false,"Idle",null,null,null,[],Alarms,
            DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,0,0,null,1){VelocityX=0,VelocityY=0,AngularVelocity=0};
    }
    private sealed class Planner:IRoutePlanner
    {public RoutePlan? Plan(string s,string d,IReadOnlySet<string> unavailable)=>new(["LM1","LM29"],[],[],1);}
    private sealed class Preflight:IRobokitTaskMapPreflight
    {
        public Task<RobokitTaskMapCheck> CheckCurrentMapAsync(string v,string m,string r,CancellationToken ct)=>Task.FromResult(new RobokitTaskMapCheck(true,"synthetic"));
        public Task<RobokitTaskMapCheck> CheckRouteAsync(string v,IReadOnlyList<string> p,CancellationToken ct)=>CheckCurrentMapAsync(v,"","",ct);
        public Task<RobokitTaskMapCheck> CheckAsync(string v,string s,string p,string w,string d,string m,string r,CancellationToken ct)=>CheckCurrentMapAsync(v,m,r,ct);
    }
    private sealed class Gateway(Func<string> send):IAgvGateway
    {
        public int Sends;
        public AgvTransportTaskState Status=AgvTransportTaskState.GoingToDestination;
        public Task<string> CreateTransportTaskAsync(string id,string? vehicle,IReadOnlyList<string> path,AgvTransportAction? action,CancellationToken ct)
        {Sends++;return Task.FromResult(send());}
        public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id,CancellationToken ct)=>
            Task.FromResult<AgvTransportTaskStatus?>(new(id,id,Status,"AGV-01",DateTimeOffset.UtcNow));
        public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id,string vehicle,IReadOnlyList<string> path,CancellationToken ct)=>
            GetTransportTaskStatusAsync(id,ct);
        public Task CancelTaskAsync(string id,CancellationToken ct)=>throw new NotSupportedException();
    }
}
