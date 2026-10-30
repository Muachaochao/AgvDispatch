using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Service;

internal static class LocalizationChecks
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        var f=Fixture();
        f.State.Set("AGV-01",.487,f.Clock.GetUtcNow());
        var observed=await f.Port.ObserveAsync(null,default);
        check(observed.Vehicles.Single(v=>v.VehicleId=="AGV-01") is {Fresh:false} low&&low.Message.Contains("正在观察"),
            "多车一次低置信度显示局部观察，不显示车辆已就绪");
        check(observed.Vehicles.Single(v=>v.VehicleId=="AGV-02") is {Fresh:true},"单车定位观察不污染其他车辆状态");
        check((await f.Port.ObserveStandbyReturnAsync(f.Run,f.Return,default)).State==FleetTestStandbyReturnStates.Returning,
            "返待机单次低置信度保留原导航，不抛异常导致全组Hold");
        check(f.Gateway.Reads==1&&f.Gateway.Sends==0,"低置信度观察只查询原导航，不重发");
        var sends=0;
        check(await f.Port.TrySendStandbyReturnAsync(f.Run,[f.Return],f.Return,_=>sends++,default) is null&&sends==0&&f.Gateway.Sends==0,
            "定位低值观察期间不持久化新发送意图或发送待机导航");
        try
        {
            await f.Port.PlanStandbyReturnsAsync(f.Run,default);
            throw new Exception("低值期间不能规划为已完成待机");
        }
        catch(FleetClosurePendingException){check(true,"定位低值期间规划返回正常等待，不返回空数组误报已完成");}
        f.Gateway.Status=AgvTransportTaskState.Completed;
        f.State.Site="LM29";f.State.X=1;
        check((await f.Port.ObserveStandbyReturnAsync(f.Run,f.Return,default)).State==FleetTestStandbyReturnStates.Returning,
            "原导航Completed但当前定位低于门槛时不确认待机完成");
        var other=f.Return with {VehicleId="AGV-02",StandbyId="C",Site="LM30",VendorTaskId="other",Route=["LM1","LM30"]};
        check((await f.Port.ObserveStandbyReturnAsync(f.Run,other,default)).State==FleetTestStandbyReturnStates.Completed,
            "一车短时定位观察期间，另一车仍可核对自己的原导航及停靠完成");
        f.State.Set("AGV-01",.8,f.Clock.GetUtcNow().AddMilliseconds(1));
        check((await f.Port.ObserveStandbyReturnAsync(f.Run,f.Return,default)).State==FleetTestStandbyReturnStates.Completed,
            "定位恢复后查询同一原导航并确认实际停靠，无需人工续接");

        var persistent=Fixture();
        var start=persistent.Clock.GetUtcNow().AddSeconds(-2.8);
        persistent.State.Set("AGV-01",.487,start);
        await persistent.Port.ObserveStandbyReturnAsync(persistent.Run,persistent.Return,default);
        persistent.State.Set("AGV-01",.487,start.AddSeconds(1.5));
        await persistent.Port.ObserveStandbyReturnAsync(persistent.Run,persistent.Return,default);
        persistent.Clock.Advance(TimeSpan.FromSeconds(.2));
        persistent.State.Set("AGV-01",.487,start.AddSeconds(3));
        await Reject(persistent,"递增有效采样连续低于门槛3秒后仍须暂停核对");
        check(persistent.Gateway.Reads==2,"持续低值达到门槛不继续推进原导航完成检查");

        foreach(var confidence in new double?[]{null,double.NaN,double.PositiveInfinity,1.01,-.1})
        {
            var invalid=Fixture();invalid.State.Set("AGV-01",confidence,invalid.Clock.GetUtcNow());
            await Reject(invalid,"缺失、非有限或越界置信度立即拒绝，不进行瞬态容忍");
            check(invalid.Gateway.Reads==0&&invalid.Gateway.Sends==0,"无效定位值既不发送，也不确认原导航完成");
        }
        var stale=Fixture();stale.State.Set("AGV-01",.487,stale.Clock.GetUtcNow().AddSeconds(-4));
        await Reject(stale,"失效采样不能作为瞬态定位观察继续运行");
        var outOfOrder=Fixture();
        outOfOrder.State.Set("AGV-01",.487,outOfOrder.Clock.GetUtcNow().AddMilliseconds(-100));
        await outOfOrder.Port.ObserveStandbyReturnAsync(outOfOrder.Run,outOfOrder.Return,default);
        outOfOrder.State.Set("AGV-01",.487,outOfOrder.Clock.GetUtcNow().AddMilliseconds(-200));
        await Reject(outOfOrder,"乱序采样立即拒绝，不能继续累计低置信度观察");
        var missing=Fixture();var beforeMissing=missing.Clock.GetUtcNow().AddSeconds(-2.8);
        missing.State.Set("AGV-01",.487,beforeMissing);
        await missing.Port.ObserveStandbyReturnAsync(missing.Run,missing.Return,default);
        missing.State.Missing.Add("AGV-01");
        await Reject(missing,"车辆缓存缺失立即拒绝并打断定位连续观察");
        missing.State.Missing.Clear();missing.Clock.Advance(TimeSpan.FromSeconds(.2));
        missing.State.Set("AGV-01",.487,beforeMissing.AddSeconds(3));
        check((await missing.Port.ObserveStandbyReturnAsync(missing.Run,missing.Return,default)).State==FleetTestStandbyReturnStates.Returning,
            "缺车后重新出现的低值从新采样开始计时，不继承缺失前低值时长");
        await StandbyAllocationAsync(check);

        async Task Reject(FixtureState fixture,string message)
        {
            try{await fixture.Port.ObserveStandbyReturnAsync(fixture.Run,fixture.Return,default);throw new Exception(message);}
            catch(InvalidOperationException){check(true,message);}
        }
    }

    private static async Task StandbyAllocationAsync(Action<bool,string> check)
    {
        foreach(var order in new[]{new[]{"AGV-01","AGV-02"},new[]{"AGV-02","AGV-01"}})
        {
            var parked=Fixture(sharedBays:true,vehicleOrder:order);
            parked.State.SetSite("AGV-01","LM30",2);parked.State.SetSite("AGV-02","LM29",1);
            var assignments=await parked.Port.PlanStandbyReturnsAsync(parked.Run,default);
            check(assignments.Single(a=>a.VehicleId=="AGV-01") is {Site:"LM30",State:FleetTestStandbyReturnStates.Completed}&&
                  assignments.Single(a=>a.VehicleId=="AGV-02") is {Site:"LM29",State:FleetTestStandbyReturnStates.Completed},
                "优先保留各车当前合法待机位，改变车辆选择顺序也不互换占位");
            check(parked.Gateway.Sends==0,"已在合法待机位时不发无用换位导航");
        }
        var occupied=Fixture(sharedBays:true);
        occupied.State.SetSite("AGV-02","LM29",1);
        var free=await occupied.Port.PlanStandbyReturnsAsync(occupied.Run,default);
        check(free.Single(a=>a.VehicleId=="AGV-01") is {Site:"LM30",State:FleetTestStandbyReturnStates.Assigned}&&
              free.Single(a=>a.VehicleId=="AGV-02") is {Site:"LM29",State:FleetTestStandbyReturnStates.Completed},
            "后列参与车占据的合法待机位提前保留，前车只分配空位");
        var intentCount=0;
        check(await occupied.Port.TrySendStandbyReturnAsync(occupied.Run,[occupied.Return],occupied.Return,_=>intentCount++,default) is null&&
              occupied.Gateway.Sends==0&&intentCount==0,
            "已分配目标后来被其他参与车占用时，不保存发送意图且不发送");
        FixtureState occupiedDuringCheck=null!;
        occupiedDuringCheck=Fixture(sharedBays:true,beforeRouteCheck:()=>occupiedDuringCheck.State.SetSite("AGV-02","LM29",1));
        check(await occupiedDuringCheck.Port.TrySendStandbyReturnAsync(occupiedDuringCheck.Run,[occupiedDuringCheck.Return],
                  occupiedDuringCheck.Return,_=>intentCount++,default) is null&&occupiedDuringCheck.Gateway.Sends==0&&intentCount==0,
            "车端路线核验期间另一车占入目标时，最后一次核对仍拒绝发送");
        var noFree=Fixture();noFree.State.SetSite("AGV-02","LM29",1);
        try
        {
            await noFree.Port.PlanStandbyReturnsAsync(noFree.Run,default);
            throw new Exception("不能规划驶入其他车的占位目标");
        }
        catch(InvalidOperationException error)
        {
            check(error.Message.Contains("未被其他车辆占用")&&noFree.Gateway.Sends==0,
                "受限车辆池没有安全空位时明确拒绝整组分配，零导航发送且不自动挪车");
        }
        var outside=Fixture(sharedBays:true,extraBay:true);
        outside.State.SetSite("AGV-02","LM2",3);
        outside.State.Set("UNSELECTED",.9,outside.Clock.GetUtcNow());outside.State.SetSite("UNSELECTED","LM29",1);
        var safe=await outside.Port.PlanStandbyReturnsAsync(outside.Run,default);
        check(safe.All(a=>a.Site!="LM29")&&safe.Select(a=>a.Site).Distinct().Count()==2,
            "缓存中未选入本组的其他车辆占位也排除，分配两个不同空位");
        foreach(var scenario in new[]{"position","nonfinite","blocked"})
        {
            var unconfirmed=Fixture(sharedBays:true);
            unconfirmed.State.SetSite("AGV-01","LM29",scenario=="position"?5:scenario=="nonfinite"?double.NaN:1);
            unconfirmed.State.Blocked=scenario=="blocked";
            try
            {
                await unconfirmed.Port.PlanStandbyReturnsAsync(unconfirmed.Run,default);
                throw new Exception("仅报告待机站点不能证明已安全停靠");
            }
            catch(FleetClosurePendingException)
            {
                check(unconfirmed.Gateway.Sends==0,"已在待机位分支也校验实际坐标和无阻挡，不误报Completed、不自动挪车");
            }
        }
    }

    private static FixtureState Fixture(bool sharedBays=false,bool extraBay=false,string[]? vehicleOrder=null,Action? beforeRouteCheck=null)
    {
        var clock=new Clock();var state=new State(clock);var gateway=new Gateway();
        var runtime=new ActiveMapRuntime();
        runtime.Replace(new("M","V",new("localization","","RobotWorld","m",
            [new("P",0,0,MapNodeType.Station,true,[],"LM1"),
             new("B",1,0,MapNodeType.Station,true,[],"LM29"),
             new("C",2,0,MapNodeType.Station,true,[],"LM30"),
             new("P2",3,0,MapNodeType.Station,true,[],"LM2"),
             new("D",4,0,MapNodeType.Station,true,[],"LM31")],[],[]),DateTimeOffset.UtcNow));
        var gate=new MapExecutionGate();gate.SetMode(MapExecutionMode.Running);
        var dispatch=new DispatchOrchestratorOptions();var creation=new DispatchTaskCreationOptions();
        var parameters=new TaskParameterRuntime(dispatch,creation);
        var config=new TaskParameterConfiguration("DUMPER-001","LM35","LM39",[new("CNC-001","LM1")],
            TaskParameterRuntime.TransportSteps.Select(step=>new TaskActionParameter(step,"Wait",null,null,null,null)).ToArray())
        {Standbys=[new("B","LM29","FLEET",sharedBays?["AGV-01","AGV-02"]:["AGV-01"]),
                   new("C","LM30","FLEET",sharedBays?["AGV-01","AGV-02"]:["AGV-02"])]};
        if(extraBay)config=config with {Standbys=[..config.Standbys,new("D","LM31","FLEET",["AGV-01","AGV-02"])]};
        parameters.Apply(new(1,"initial","","M","V",config,[]));
        var port=new FleetTestPort(new(){Enabled=true,VehicleIds=["AGV-01","AGV-02"]},
            null!,null!,null!,null!,null!,null!,null!,null!,state,runtime,gate,parameters,dispatch,creation,new(),
            new Preflight(beforeRouteCheck),null!,new Planner(),null!,gateway,true,null,clock);
        var plan=new FleetTestStart(Guid.NewGuid().ToString("N"),"Simultaneous",vehicleOrder??["AGV-01","AGV-02"],true)
            {StationIds=["CNC-001","CNC-002"]};
        var run=new FleetTestRun(Guid.NewGuid().ToString("N"),plan,"M","V","initial","ReturningStandby","",false,
            DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,[]);
        var returning=new FleetTestStandbyReturn("AGV-01","B","LM29",FleetTestStandbyReturnStates.Returning,"","original",DateTimeOffset.UtcNow)
            {Route=["LM1","LM29"]};
        return new(port,state,gateway,clock,run,returning);
    }
    private sealed record FixtureState(FleetTestPort Port,State State,Gateway Gateway,Clock Clock,FleetTestRun Run,FleetTestStandbyReturn Return);
    private sealed class Clock:TimeProvider
    {
        private DateTimeOffset now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>now;
        public void Advance(TimeSpan by)=>now+=by;
    }
    private sealed class State:IAgvStateCache
    {
        private readonly Dictionary<string,(double? Confidence,DateTimeOffset At)> samples=new();
        private readonly Dictionary<string,(string Site,double X)> positions=new();
        public HashSet<string> Missing=new();
        public bool Blocked;
        public string Site{get=>positions["AGV-01"].Site;set=>SetSite("AGV-01",value,X);}
        public double X{get=>positions["AGV-01"].X;set=>SetSite("AGV-01",Site,value);}
        public State(Clock clock)
        {Set("AGV-01",.9,clock.GetUtcNow());Set("AGV-02",.9,clock.GetUtcNow());SetSite("AGV-01","LM1",0);SetSite("AGV-02","LM30",2);}
        public void Set(string id,double? confidence,DateTimeOffset at)=>samples[id]=(confidence,at);
        public void SetSite(string id,string site,double x)=>positions[id]=(site,x);
        public event Action<AgvRobotStateChanged>? StateChanged{add{}remove{}}
        public long Version=>1;
        public IReadOnlyList<AgvRobotSnapshot> GetAll()=>samples.Keys.Select(Snapshot).ToArray();
        public bool TryGet(string id,out AgvRobotSnapshot snapshot)
        {if(Missing.Contains(id)){snapshot=null!;return false;}snapshot=Snapshot(id);return samples.ContainsKey(id);}
        private AgvRobotSnapshot Snapshot(string id)
        {
            var sample=samples[id];
            return new(id,id,"synthetic",0,ConnectionState.Online,SignalQuality.Good,
                positions[id].X,0,0,sample.Confidence,positions[id].Site,null,90,id=="AGV-01"&&Blocked,false,"Idle",null,null,null,[],[],
                sample.At,sample.At,0,0,null,1){VelocityX=0,VelocityY=0,AngularVelocity=0};
        }
    }
    private sealed class Planner:IRoutePlanner
    {public RoutePlan? Plan(string s,string d,IReadOnlySet<string> unavailable)=>new([s,d],[],[],1);}
    private sealed class Preflight(Action? beforeRouteCheck=null):IRobokitTaskMapPreflight
    {
        public Task<RobokitTaskMapCheck> CheckCurrentMapAsync(string v,string m,string r,CancellationToken ct)=>Task.FromResult(new RobokitTaskMapCheck(true,"synthetic"));
        public Task<RobokitTaskMapCheck> CheckRouteAsync(string v,IReadOnlyList<string> p,CancellationToken ct)
        {beforeRouteCheck?.Invoke();return CheckCurrentMapAsync(v,"","",ct);}
        public Task<RobokitTaskMapCheck> CheckAsync(string v,string s,string p,string w,string d,string m,string r,CancellationToken ct)=>CheckCurrentMapAsync(v,m,r,ct);
    }
    private sealed class Gateway:IAgvGateway
    {
        public int Sends,Reads;
        public AgvTransportTaskState Status=AgvTransportTaskState.GoingToDestination;
        public Task<string> CreateTransportTaskAsync(string id,string? vehicle,IReadOnlyList<string> path,AgvTransportAction? action,CancellationToken ct)
        {Sends++;return Task.FromResult(id);}
        public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct)=>throw new NotSupportedException();
        public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id,CancellationToken ct)=>throw new NotSupportedException();
        public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id,string vehicle,IReadOnlyList<string> path,CancellationToken ct)
        {Reads++;return Task.FromResult<AgvTransportTaskStatus?>(new(id,id,Status,vehicle,DateTimeOffset.UtcNow));}
        public Task CancelTaskAsync(string id,CancellationToken ct)=>throw new NotSupportedException();
    }
}
