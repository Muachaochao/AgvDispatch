using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.WebHost.Engineering;

internal static class RuntimeCompilerChecks
{
    public static void Run(Action<bool,string> check)
    {
        var baseline = EngineeringSample.Create(300,5);
        var plan = baseline with
        {
            Nodes = [..baseline.Nodes, new("WAIT-2", "SIM-WAIT-2", 11, 0)],
            Edges =
            [
                ..baseline.Edges,
                new("MAIN-WAIT-2", "MAIN", "WAIT-2", 11, .5, []),
                new("WAIT-2-DUMP", "WAIT-2", "DUMP", 1, .3, [])
            ],
            Resources = [..baseline.Resources, new("WAIT-BAY-2", "第二倒料等待位", "Waiting")],
            Dumpers =
            [
                baseline.Dumpers.Single() with
                {
                    AdditionalWaitingNodes = ["WAIT-2"],
                    AdditionalWaitingResources = ["WAIT-BAY-2"]
                }
            ]
        };
        var version = new EngineeringVersion(Guid.NewGuid().ToString("N"),"draft",DateTimeOffset.UtcNow,"runtime fixture",plan,EngineeringStore.Hash(plan));
        var map = new MapDefinition("synthetic runtime","synthetic","RobotWorld","m",
            plan.Nodes.Select(n => new MapNode(n.Id,n.X,n.Y,MapNodeType.Station,n.Enabled,[],n.VendorSite)).ToArray(),
            plan.Edges.Select(e=>new MapEdge(e.Id,e.From,e.To,e.Distance,e.Speed,e.Bidirectional,e.Enabled,e.ResourceIds)).ToArray(),
            plan.Resources.Select(r=>new TrafficResourceDefinition(r.Id,TrafficResourceType.Station,r.Capacity)).ToArray());
        map=map with { Edges=map.Edges.Select((e,i)=>i==0?e with { GeometryKind=MapEdgeGeometryKind.CubicBezier,ControlPoints=[new(1,2),new(3,4)] }:e).ToArray() };
        var now=DateTimeOffset.UtcNow;
        var published=new MapVersionSnapshot(plan.MapVersion,plan.MapId,1,MapVersionStatus.Published,map,new([],map.Nodes.Count,map.Edges.Count,map.Resources.Count),"fixture",now,now,now,"token");
        var project=new MapProjectSummary(plan.MapId,"fixture","","","","RobotWorld","m",1,"","",[],null,null,CadUnderlayJobStatus.None,"",null,true,plan.MapVersion,plan.MapVersion,now);
        var workspace=new MapWorkspaceSnapshot(project,published,published);
        var config=new TaskParameterConfiguration("OLD","OLDWAIT","OLDDUMP",[new("OLD","OLDPICK")],
            new[]{DispatchTaskStepType.GoToPickup,DispatchTaskStepType.DeliverFullBoxToDumper,DispatchTaskStepType.EnterDumpingPosition,DispatchTaskStepType.ReturnEmptyBox}
                .Select((s,i)=>new TaskActionParameter(s,i==0?"JackLoad":i==3?"JackUnload":"Wait",null,null,null,null)).ToArray())
        {
            WaitingSites=["OLDWAIT","OLDWAIT-SECOND"],
            EngineeringSource=new("OLD-ENGINEERING","OLD-CONTENT","OLD-MAP-HASH")
        };
        var parameters=new TaskParameterSnapshot("revision",config,plan.MapId,plan.MapVersion,true,"",[]);
        var comm=new CommunicationConfiguration { AgvEnabled=true, Plcs=plan.Plcs.Select(p=>new PlcConnectionSetting{DeviceId=p.Id,Enabled=p.Enabled,IpAddress=p.Host,Port=p.Port}).ToList(),
            Agvs=plan.Vehicles.Select(v=>new AgvConnectionSetting{VehicleId=v.Id,Enabled=v.Enabled,IpAddress=v.Host}).ToList() };
        var communication=new CommunicationSettingsSnapshot("comm-revision",comm,comm,false,true,[],[]);
        EngineeringRuntimePreview Compile(EngineeringVersion? v=null,MapWorkspaceSnapshot? w=null,TaskParameterSnapshot? p=null,CommunicationSettingsSnapshot? c=null)=>
            EngineeringRuntimeCompiler.Compile(v??version,w??workspace,p??parameters,c??communication);
        var result=Compile();
        check(result.Ready&&result.MapActive,"300工位5车工程可编译为真实九步参数");
        check(EngineeringValidator.Validate(plan).Valid,"双倒料候车位工程通过拓扑、容量和资源校验");
        check(result.Configuration!.Pickups.Count==300&&result.Configuration.Pickups.All(p=>p.AllowedVehicleIds!.Count==5),"所有工位保留明确车辆池白名单");
        check(result.Configuration.Standbys.Count==plan.Standbys.Count && result.Configuration.Standbys.All(b=>b.AllowedVehicleIds.Count==5),"待机位及明确车辆池授权随工程版本编译");
        check(result.Configuration.WaitingSite==plan.Dumpers.Single().WaitingNode &&
            result.Configuration.WaitingSites.SequenceEqual(new[]{plan.Dumpers.Single().WaitingNode,"WAIT-2"}),
            "切换工程地图后保留工程配置的多个独立倒料候车位");
        check(config.WaitingSites.SequenceEqual(new[]{"OLDWAIT","OLDWAIT-SECOND"}) &&
            config.EngineeringSource!.MapDefinitionHash=="OLD-MAP-HASH",
            "新地图编译不改写原任务参数及旧工程来源");
        check(result.Configuration.Actions.SequenceEqual(config.Actions)&&result.Configuration.StepTimeouts.SequenceEqual(config.StepTimeouts),"编译保留当前车辆动作和超时，不从模拟耗时推断动作");
        check(result.Configuration.TaskTemplate is { Nodes.Count:12 } runtimeTemplate &&
            runtimeTemplate.Variables.Count==17 && runtimeTemplate.Connections.Count>0 && runtimeTemplate.Guards.Count==5,
            "工程编译同时下发模板输入、类型化连接、十二个节点和异常分支策略");
        check(result.Configuration.TaskTemplate!.Nodes.All(n=>!n.AutoRetry),"运行模板快照禁止机械和握手节点自动重试");
        check(result.Configuration.EngineeringSource?.MapDefinitionHash==EngineeringRuntimeIdentity.MapHash(map),"绑定完整原地图摘要，包含曲线控制点");
        check(workspace.Published!.Definition.Edges[0].GeometryKind==MapEdgeGeometryKind.CubicBezier&&workspace.Published.Definition.Edges[0].ControlPoints![0].Y==2,"编译不覆盖厂家曲线");
        check(!Compile(version with { ContentHash=new string('0',64) }).Ready,"工程内容被修改必须拒绝");
        check(!Compile(w:workspace with {Published=null}).Ready,"未发布地图不得应用");
        check(!Compile(w:workspace with {Published=published with {VersionId="another"}}).Ready,"准确地图版本不匹配必须拒绝");
        check(!Compile(w:workspace with {Published=published with {Definition=map with {CoordinateSystem="SmapWorldTranslated"}}}).Ready,"平移预览图不得作为运行图");
        check(!Compile(w:workspace with {Published=published with {Definition=map with {Nodes=map.Nodes.Select((n,i)=>i==0?n with {X=n.X+1}:n).ToArray()}}}).Ready,"站点坐标不同必须拒绝");
        check(!Compile(w:workspace with {Published=published with {Definition=map with {Edges=map.Edges.Select((e,i)=>i==0?e with {SpeedLimit=e.SpeedLimit+1}:e).ToArray()}}}).Ready,"道路限速不同必须拒绝");
        check(!Compile(w:workspace with {Published=published with {Definition=map with {Resources=map.Resources.Select((r,i)=>i==0?r with {Capacity=99}:r).ToArray()}}}).Ready,"交通容量不同必须拒绝");
        check(!Compile(c:communication with {RestartRequired=true}).Ready,"通信尚未生效不得应用");
        check(!Compile(c:communication with {Active=new CommunicationConfiguration()}).Ready,"未启用现场通信不得用模拟连接代替");
        check(Compile(p:parameters with {MapVersionId="old"}) is {Ready:true,MapActive:false},"预检可以指明尚未激活地图，不能误认为已应用");
        var split=plan with {Stations=plan.Stations.Select((s,i)=>i==0?s with {DumperId="DUMPER-B"}:s).ToList()};
        check(!Compile(version with {Plan=split,ContentHash=EngineeringStore.Hash(split)}).Ready,"当前单倒料执行器不得静默合并多个倒料设备");
        var restricted=plan with {Pools=plan.Pools.Select(p=>p with {VehicleIds=[plan.Vehicles[0].Id]}).ToList()};
        var restrictedResult=Compile(version with {Plan=restricted,ContentHash=EngineeringStore.Hash(restricted)});
        check(restrictedResult.Configuration!.Standbys.All(b=>b.AllowedVehicleIds.SequenceEqual(new[]{plan.Vehicles[0].Id})),"待机授权不越过所配置车辆池");
        // Standby capacity remains sufficient; narrowing a pool must narrow the compiled authorization.
        check(restrictedResult.Ready&&restrictedResult.Configuration!.Pickups.All(p=>p.AllowedVehicleIds!.SequenceEqual(new[]{plan.Vehicles[0].Id})),"池内单车授权不会被展开为全部车辆");
    }
}
