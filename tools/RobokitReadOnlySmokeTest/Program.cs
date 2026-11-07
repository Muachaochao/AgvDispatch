using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Service;

var checks=0;
void Check(bool condition,string message) { if(!condition) throw new Exception(message); checks++; Console.WriteLine("PASS "+message); }
await using var robot=new Robot();
var config=new AgvRobotOptions { VehicleId="SIM", IpAddress="127.0.0.1",Port=robot.StatusPort,NavigationPort=robot.NavigationPort };
var options=new AgvOptions { Enabled=true,AllowCommands=true,Robots=[config],ConnectTimeoutMs=500,RequestTimeoutMs=500,RequestIntervalMs=100 };
Check(new AgvRobotOptions().Port==19204 && new AgvRobotOptions().NavigationPort==19206,"默认状态/导航分端口");
var saved=CommunicationSettingsService.FromLegacy(new(),options);
Check(saved.Agvs[0].NavigationPort==robot.NavigationPort,"导航端口进入通信设置");
var restored=new AgvOptions(); CommunicationSettingsService.ApplyAgv(saved,restored);
Check(restored.Robots[0].NavigationPort==robot.NavigationPort && restored.Robots[0].Port==robot.StatusPort,"导航端口保存/加载不丢失");
var legacy=JsonSerializer.Deserialize<AgvRobotOptions>("{\"VehicleId\":\"legacy\",\"Port\":23456}")!;
Check(legacy.Port==23456 && legacy.NavigationPort==19206,"旧Port仅兼容状态端口，不推导导航端口");
config.NavigationPort=0;Check(options.GetConfigurationError()!=null,"无效导航端口拒绝");config.NavigationPort=robot.NavigationPort;
options.RequestIntervalMs=99;Check(options.GetConfigurationError()!=null,"过快请求间隔拒绝");options.RequestIntervalMs=100;
await using var manager=new AgvConnectionManager(options);
var client=manager.Clients.Single();
await client.PollAsync(default);
await client.GoToTargetAsync("TASK",["LM1","LM2"],null,true,default);
Check(robot.Types.Any(t=>t.Port==robot.NavigationPort && t.Type==3066),"3066仅发送导航端口");
Check(robot.Types.Where(t=>t.Type is 1100 or 1110).All(t=>t.Port==robot.StatusPort),"1100/1110仅发送状态端口");
await client.CancelNavigationAsync("TASK",true,default);
Check(robot.Types.Any(t=>t.Port==robot.NavigationPort && t.Type==3003),"3003定向取消走导航端口");
Check(robot.LastCancel=="TASK","取消保留taskId");
var cache=new AgvStateCache(options);
cache.ApplyTelemetry("SIM",new AgvRobotTelemetry(0,0,0,1,"LM1",null,90,false,false,"Idle",null,null,null,[],[],DateTimeOffset.Now),TimeSpan.FromMilliseconds(1));
var gateway=new SeerAgvGateway(cache,manager,options);
robot.TaskStatus=3;
var paused=await gateway.GetTransportTaskStatusAsync("TASK",default);
Check(paused?.State==AgvTransportTaskState.Suspended && paused.Message!.Contains("暂停"),"状态3映射暂停，保持非终态");
robot.TaskStatus=7;
var overtime=await gateway.GetTransportTaskStatusAsync("TASK",default);
Check(overtime?.State==AgvTransportTaskState.Failed && overtime.Message!.Contains("超时"),"状态7失败处置保留超时原因");
robot.TaskStatus=404;Check(await gateway.GetTransportTaskStatusAsync("TASK",default)==null,"404仍为未知不作完成");
robot.TaskStatus=2;
robot.RejectNavigationCode=40020;
var rejectedSends=robot.Types.Count(t=>t.Type==3066);
try{await gateway.CreateTransportTaskAsync("CONTROL-LOCKED","SIM",["LM1","LM2"],null,default);throw new Exception("明确控制抢占仍被当作可重试");}
catch(AgvCommandRejectedException e){Check(e.RequestType==3066&&e.ReturnCode==40020,"明确40020归类为发送前终态拒绝");}
Check(robot.Types.Count(t=>t.Type==3066)==rejectedSends+1,"发送前拒绝不会在同一调用内重发3066");
Check((await client.QueryTaskAsync("CONTROL-LOCKED",default))?.Status==404,"发送前拒绝没有创建车端任务");
robot.RejectNavigationCode=null;
var sendsBeforeDroppedResponse=robot.Types.Count(t=>t.Type==3066);
robot.DropNavigation=true;
await gateway.CreateTransportTaskAsync("UNKNOWN", "SIM",["LM1","LM2"],null,default);
Check((await client.QueryTaskAsync("UNKNOWN",default))?.Status==2,"导航响应丢失后从状态端口确认原任务");
Check(robot.Types.Count(t=>t.Type==3066)==sendsBeforeDroppedResponse+1,"发送未知不重复3066");
robot.DropNavigation=false;
options.RequestTimeoutMs=1500;
robot.HoldNavigation=true;
var pending=client.GoToTargetAsync("HELD",["LM1","LM2"],null,true,default);
await robot.Held.Task.WaitAsync(TimeSpan.FromSeconds(2));
var watch=Stopwatch.StartNew();await client.QueryTaskAsync("TASK",default);
Check(watch.ElapsedMilliseconds<450,"导航等待不占用状态通道锁");
robot.DropStatus=true;
try { await client.PollAsync(default); throw new Exception("状态丢失未识别"); } catch(EndOfStreamException) { checks++; }
Check(!pending.IsCompleted,"状态通道断线不会中断等待中的导航");
robot.DropStatus=false;
Check((await client.QueryTaskAsync("TASK",default))?.Status==2,"状态通道独立重连");
robot.Release.TrySetResult();await pending;robot.HoldNavigation=false;options.RequestTimeoutMs=500;
var local=new ActiveMapRuntime();
local.Replace(new("MAP","V1",new MapDefinition("demo","test","world","m",
    [new MapNode("N1",0,0,MapNodeType.Station,true,[],"LM1"),new MapNode("N2",1,0,MapNodeType.Station,true,[],"LM2")],[],[]),DateTimeOffset.Now));
var service=new RobokitInspectionService(options,manager,local);
robot.ResetRead();
var good=await service.ReadAsync(new("SIM","N1","N2"),default);
Check(good.Available && good.Stable,"只读完整采集与地图前后核验");
Check(good.RobokitVersion=="3.4.8" && good.ProtocolVersion=="1","1000版本展示");
Check(good.RobotMap=="demo" && good.RobotMapMd5=="abc" && good.LoadMapStatus==1,"1300/1022地图身份和载入状态");
Check(good.Stations.Count==2 && good.Stations[0].X==0 && good.Stations[0].Angle==1.57,"1301站点坐标和rad角度");
Check(good.Mappings.All(m=>m.Found) && good.Mappings[0].SiteId=="LM1","逻辑节点映射真实厂家id");
Check(good.Route.SequenceEqual(new[]{"LM1","LM2"}),"1303用厂家编号查询路线");
Check(good.ControlLocked==true && good.ControlOwner=="sim-owner","1060只读控制权");
Check(good.Issues.Any(i=>i.Contains("不作为调度")),"存在站点不自动认定地图已对齐可放行");
Check(robot.ReadTypes.All(t=>t<2000),"只读核验不调用控制/导航/配置/写入接口");
Check(robot.ZeroPayloadReads,"无参数状态接口发送零数据长度");
robot.Mode="changed";robot.ResetRead();var changed=await service.ReadAsync(new("SIM"),default);
Check(changed.Available && !changed.Stable,"采集期间车端MD5变化拒绝整批核验");
robot.Mode="loading";robot.ResetRead();Check(!(await service.ReadAsync(new("SIM"),default)).Stable,"载入未完成拒绝核验");
robot.Mode="missing";robot.ResetRead();var missing=await service.ReadAsync(new("SIM"),default);
Check(missing.Mappings.All(m=>!m.Found) && missing.Issues.Any(i=>i.Contains("stations")),"缺失站点数组不会显示有效映射");
robot.Mode="duplicate";robot.ResetRead();Check((await service.ReadAsync(new("SIM"),default)).Mappings.Any(m=>!m.Found),"重复车端站点拒绝映射");
robot.Mode="bad-route";robot.ResetRead();Check((await service.ReadAsync(new("SIM","N1","N2"),default)).Issues.Any(i=>i.Contains("路径端点")),"1303端点不符有明确错误");
robot.Mode="error";robot.ResetRead();var failed=await service.ReadAsync(new("SIM"),default);
Check(!failed.Available && failed.Stations.Count==0 && failed.RobotMap==null,"API错误清空健康结果");
robot.Mode="ok";robot.ResetRead();
var oneEnd=await service.ReadAsync(new("SIM","N1"),default);
Check(oneEnd.Issues.Any(i=>i.Contains("同时填写")) && !robot.ReadTypes.Contains(1303),"不完整路径参数不调用1303");
options.Enabled=false;var before=robot.Types.Count;
Check(!(await service.ReadAsync(new("SIM"),default)).Available && robot.Types.Count==before,"关闭采集不连接设备");options.Enabled=true;
robot.Mode="ok";robot.ResetRead();
using(var cancel=new CancellationTokenSource()) { cancel.Cancel(); try { await service.ReadAsync(new("SIM"),cancel.Token);throw new Exception("取消未抛出"); } catch(OperationCanceledException) {checks++;} }
await client.QueryTaskAsync("TASK",default);await client.QueryTaskAsync("TASK",default);
var times=robot.Types.Where(t=>t.Port==robot.StatusPort).Select(t=>t.At).ToArray();
Check(times.Zip(times.Skip(1),(a,b)=>Stopwatch.GetElapsedTime(a,b).TotalMilliseconds).All(ms=>ms>=85),"状态通道请求间隔保持100ms（允许调度测量误差）");
await using(var wrongClient=new SeerRobotClient(new(){VehicleId="WRONG",IpAddress="127.0.0.1",Port=robot.NavigationPort,NavigationPort=robot.StatusPort},options))
{ try {await wrongClient.QueryTaskAsync("TASK",default);throw new Exception("错端口被模拟器接受");} catch(InvalidOperationException) {checks++;} }
// New batch: all segment ids, non-atomic acceptance and real chain aggregation.
robot.Mode="ok";robot.ResetRead();
var chainGateway=new SeerAgvGateway(cache,manager,options,new ChainLookup());
await chainGateway.CreateTransportTaskAsync("CHAIN","SIM",["LM1","LM2","LM3","LM4"],new("JackLoad"),default);
Check(robot.LastMoves.Select(m=>m.GetProperty("task_id").GetString()).SequenceEqual(new[]{"CHAIN-01","CHAIN-02","CHAIN"}),"3066三段编号稳定，末段仍为原步骤号");
Check(robot.LastMoves.Take(2).All(m=>!m.TryGetProperty("operation",out _)) && robot.LastMoves[2].GetProperty("operation").GetString()=="JackLoad","动作仅放在末段");
var sent=robot.Types.Count(t=>t.Type==3066);
await chainGateway.CreateTransportTaskAsync("CHAIN","SIM",["LM1","LM2","LM3","LM4"],null,default);
Check(robot.Types.Count(t=>t.Type==3066)==sent,"完整任务链重试不重复发送");
robot.CascadeEnabled=false;
await chainGateway.CancelTaskAsync("CHAIN",default);
Check(robot.LastCancel=="CHAIN-01","取消从首个未结束原段开始，保留原taskId");
Check((await chainGateway.GetTransportTaskStatusAsync("CHAIN",default))?.State==AgvTransportTaskState.Unknown,"部分取消但后段仍运行不确认结束");
robot.SetTask("CHAIN-01",4);robot.SetTask("CHAIN-02",2);robot.SetTask("CHAIN",2);robot.CascadeEnabled=true;
await chainGateway.CancelTaskAsync("CHAIN",default);
Check(robot.LastCancel=="CHAIN-02" && (await chainGateway.GetTransportTaskStatusAsync("CHAIN",default))?.State==AgvTransportTaskState.Cancelled,"模拟级联取消确认所有原段已结束");
robot.SetTask("CHAIN-01",5);robot.SetTask("CHAIN-02",6);robot.SetTask("CHAIN",6);
Check((await chainGateway.GetTransportTaskStatusAsync("CHAIN",default))?.State==AgvTransportTaskState.Failed,"首段失败和后段级联取消保留失败语义");
robot.SetTask("CHAIN-01",4);robot.SetTask("CHAIN-02",7);robot.SetTask("CHAIN",7);
var mixedTimeout=await chainGateway.GetTransportTaskStatusAsync("CHAIN",default);
Check(mixedTimeout is {State:AgvTransportTaskState.Failed,VendorStatusCode:7},
    "已完成前段加当前状态7仍识别为明确导航超时");
robot.SetTask("CHAIN-02",5);
Check((await chainGateway.GetTransportTaskStatusAsync("CHAIN",default))?.VendorStatusCode is null,
    "状态4/5/7混合不能被伪装成唯一超时码");
robot.SetTask("CHAIN-01",404);robot.SetTask("CHAIN",4);
Check((await chainGateway.GetTransportTaskStatusAsync("CHAIN",default))?.State==AgvTransportTaskState.Unknown,"历史段缺失不能凭末段完成放行");
robot.AcceptLimit=1;
try{await chainGateway.CreateTransportTaskAsync("PARTIAL","SIM",["LM1","LM2","LM3"],null,default);throw new Exception("部分接收未识别");}catch(AgvPartialAcceptanceException){checks++;}
var partialSends=robot.Types.Count(t=>t.Type==3066);
var rebuilt=new SeerAgvGateway(cache,manager,options,new ChainLookup());
try{await rebuilt.CreateTransportTaskAsync("PARTIAL","SIM",["LM1","LM2","LM3"],null,default);throw new Exception("重建后部分接收重发");}catch(AgvPartialAcceptanceException){checks++;}
Check(robot.Types.Count(t=>t.Type==3066)==partialSends,"服务对象重建后前段部分接收禁止整包重发");
robot.AcceptLimit=int.MaxValue;robot.ResetRead();robot.Md5="0123456789abcdef0123456789abcdef";robot.ExtraStation=true;
var linked=new MapDefinition("demo","test","world","m",
    [new MapNode("N1",0,0,MapNodeType.Station,true,[],"LM1"),new MapNode("N2",1,0,MapNodeType.Station,true,[],"LM2"),new MapNode("N3",2,0,MapNodeType.Station,true,[],"LM3")],
    [new MapEdge("E","N1","N2",1,1,true,true,[]),new MapEdge("E2","N2","N3",1,1,true,true,[]),new MapEdge("E3","N3","N1",2,1,true,true,[])],[]);
local.Replace(new("MAP","V2",linked,DateTimeOffset.Now));
var preflight=new RobokitTaskMapPreflight(options,manager,local,service);
Check(!(await preflight.CheckAsync("SIM","N1","N2","N3","N1","MAP","V2",default)).Accepted,"任务前缺少地图版本绑定拒绝");
options.MapBindings=[new(){VehicleId="SIM",LocalMapId="MAP",LocalMapVersion="V2",RobotMap="demo",RobotMapMd5=robot.Md5,CoordinatesVerified=true}];
var bound=await preflight.CheckAsync("SIM","N1","N2","N3","N1","MAP","V2",default);
Check(bound.RouteChecks.Count==4&&bound.RouteChecks.All(r=>r.DirectConfirmed)&&bound.RouteChecks.Select(r=>r.Sequence).SequenceEqual(new[]{1,3,5,7}),"四运输步骤逐段只读观察结构完整");
Check(bound.Accepted,"绑定明确后四运输段与直接厂家路径核验通过："+bound.Message);
options.MapBindings[0].RobotMapMd5=new string('f',32);
Check(!(await preflight.CheckAsync("SIM","N1","N2","N3","N1","MAP","V2",default)).Accepted,"车端MD5不符拒绝提交");options.MapBindings[0].RobotMapMd5=robot.Md5;
options.MapBindings[0].CoordinatesVerified=false;
Check(!(await preflight.CheckAsync("SIM","N1","N2","N3","N1","MAP","V2",default)).Accepted,"坐标未核定拒绝提交");options.MapBindings[0].CoordinatesVerified=true;
robot.Mode="bad-route";robot.ResetRead();
var routeFailure=await preflight.CheckAsync("SIM","N1","N2","N3","N1","MAP","V2",default);
Check(!routeFailure.Accepted,"厂家路线非直接相连拒绝提交");
Check(routeFailure.RouteChecks.Last() is {Sequence:1,DirectConfirmed:false,Source:"LM1",Target:"LM2"},"失败观察定位原步骤与厂家端点");
robot.Mode="missing";robot.ResetRead();
Check(!(await preflight.CheckAsync("SIM","N1","N2","N3","N1","MAP","V2",default)).Accepted,"缺真实站点拒绝提交");
robot.Mode="ok";robot.ResetRead();
local.Replace(new("MAP","V2",linked with{Nodes=[linked.Nodes[0] with{AgvSiteId=null},linked.Nodes[1],linked.Nodes[2]]},DateTimeOffset.Now));
Check(!(await preflight.CheckAsync("SIM","N1","N2","N3","N1","MAP","V2",default)).Accepted,"逻辑Id不自动代替缺失厂家Id");

local.Replace(new("MAP","V2",linked,DateTimeOffset.Now));robot.ResetRead();
Check((await preflight.CheckRouteAsync("SIM",new[]{"LM1","LM2","LM3"},default)).Accepted,"实际发送的逐段厂家路径再次核验");
Check(!(await preflight.CheckRouteAsync("SIM",new[]{"LM1","NOT-A-SITE"},default)).Accepted,"实际发送含未知厂家站点拒绝");
var sendsBeforeGuard=robot.Types.Count(t=>t.Type==3066);
options.MapBindings.Clear();
var guardedGateway=new SeerAgvGateway(cache,manager,options,null,preflight);
try{await guardedGateway.CreateTransportTaskAsync("BLOCKED","SIM",new[]{"LM1","LM2"},null,default);throw new Exception("无绑定仍发送");}catch(InvalidOperationException e)when(e.Message.Contains("核验")){checks++;}
Check(robot.Types.Count(t=>t.Type==3066)==sendsBeforeGuard,"发送门禁失败没有3066副作用");
var directory=Path.Combine(Path.GetTempPath(),"agv-chain-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
using(var db=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(directory,"test.db")}))
{
    var resource=new TrafficResourceDefinition("R",TrafficResourceType.NarrowAisle);
    var catalog=new TrafficResourceCatalog(new[]{resource});await new DatabaseInitializer(db,catalog).InitializeAsync(default);
    var tasks=new DispatchTaskRepository(db);
    var outbox=new AgvCommandOutboxRepository(db);
    var gate=new MapExecutionGate();gate.SetMode(MapExecutionMode.Running);
    var dispatch=typeof(AgvCommandOutboxDispatcher).GetMethod("DispatchDueCommandsAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
    var rejectedTask=await tasks.TryCreateAsync("CNC-REJECT","DUMPER-REJECT",20,default);
    await tasks.MarkAcknowledgedAsync(rejectedTask.Task.TaskId,default);
    var rejectedStep=(await tasks.GetStepsAsync(rejectedTask.Task.TaskId,default))[0];
    Check(await outbox.TryPrepareTransportStepAsync(rejectedTask.Task.TaskId,rejectedStep.StepId,"SIM",new[]{"LM1","LM2"},null,default),"发送前拒绝测试持久化原命令");
    robot.RejectNavigationCode=40020;
    var rejectedSender=new AgvCommandOutboxDispatcher(NullLogger<AgvCommandOutboxDispatcher>.Instance,new(){MaximumAttempts=3},outbox,new SeerAgvGateway(cache,manager,options),gate);
    var rejectedDispatchCount=robot.Types.Count(t=>t.Type==3066);
    await (Task)dispatch.Invoke(rejectedSender,new object[]{CancellationToken.None})!;
    var rejectedCommand=await db.Queryable<AgvCommandOutboxEntity>().Where(c=>c.StepId==rejectedStep.StepId).FirstAsync();
    var rejectedTaskRow=await db.Queryable<DispatchTaskEntity>().Where(t=>t.TaskId==rejectedTask.Task.TaskId).FirstAsync();
    Check(rejectedCommand.Status==(int)AgvCommandStatus.Failed&&rejectedCommand.AttemptCount==1,"明确40020在Outbox立即失败且只尝试一次");
    Check(rejectedTaskRow.Stage==(int)DispatchTaskStage.Faulted,"明确40020保留任务故障和原路权，等待人工恢复");
    Check(robot.Types.Count(t=>t.Type==3066)==rejectedDispatchCount+1,"Outbox对明确40020不安排第二次3066");
    robot.RejectNavigationCode=null;
    var created=await tasks.TryCreateAsync("SIM-STATION","SIM-DUMPER",20,default);
    await tasks.MarkAcknowledgedAsync(created.Task.TaskId,default);var step=(await tasks.GetStepsAsync(created.Task.TaskId,default))[0];
    var traffic=new TrafficResourceManager(db,catalog,new(TimeSpan.FromMinutes(1)));
    await traffic.RecordRouteAsync(new(step.StepId,created.Task.TaskId,"SIM","LM1","LM3",new[]{"E1","E2"},new[]{"R"},false,"partial acceptance simulation"),default);
    Check((await traffic.TryAcquireAsync(step.StepId,created.Task.TaskId,"SIM",new[]{"R"},default)).Acquired,"部分接收测试先持有真实数据库路权");
    Check(await outbox.TryPrepareTransportStepAsync(created.Task.TaskId,step.StepId,"SIM",new[]{"LM1","LM2","LM3"},null,default),"原多段负载持久化");
    var lookup=new AgvTransportChainLookup(db);
    var recorded=await lookup.FindAsync(step.StepId,default);
    Check(recorded?.Path.SequenceEqual(new[]{"LM1","LM2","LM3"})==true && recorded.VehicleId=="SIM","任务链从原Outbox读取，不依赖内存猜编号");
    var persistentGateway=new SeerAgvGateway(cache,manager,options,lookup);robot.AcceptLimit=1;
    var sender=new AgvCommandOutboxDispatcher(NullLogger<AgvCommandOutboxDispatcher>.Instance,new(){MaximumAttempts=3},outbox,persistentGateway,gate);
    await (Task)dispatch.Invoke(sender,new object[]{CancellationToken.None})!;
    Check((await outbox.GetDueCommandsAsync(DateTimeOffset.Now.AddDays(1),10,default)).Count==0,"部分接收立即停止Outbox自动重试");
    var command=await db.Queryable<AgvCommandOutboxEntity>().Where(c=>c.StepId==step.StepId).FirstAsync();
    Check(command.Status==(int)AgvCommandStatus.Failed && command.AttemptCount==1,"部分接收命令持久化失败及原次数");
    Check(db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.TaskId==created.Task.TaskId),"部分接收异常仍保留原路权");
    Check((await tasks.GetStepsAsync(created.Task.TaskId,default))[0].Status!=DispatchTaskStepStatus.Completed,"部分接收不完成运输步骤");
    var beforeRebuild=robot.Types.Count(t=>t.Type==3066);
    var restarted=new SeerAgvGateway(cache,manager,options,new AgvTransportChainLookup(db));
    try{await restarted.CreateTransportTaskAsync(step.StepId,"SIM",new[]{"LM1","LM2","LM3"},null,default);throw new Exception("重建后整包补发");}catch(AgvPartialAcceptanceException){checks++;}
    Check(robot.Types.Count(t=>t.Type==3066)==beforeRebuild,"重建Gateway后仍不重复发送部分接收命令");
    robot.SetTask(step.StepId+"-01",404);
    try{await restarted.CreateTransportTaskAsync(step.StepId,"SIM",new[]{"LM1","LM2","LM3"},null,default);throw new Exception("失败命令历史全丢后重发");}catch(AgvPartialAcceptanceException){checks++;}
    Check(robot.Types.Count(t=>t.Type==3066)==beforeRebuild,"原命令失败且历史全404仍不重新发送");
    File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new{created.Task.TaskId,step.StepId,command.CommandId,command.Status,command.AttemptCount}));
}
Console.WriteLine("Private SQLite evidence: "+directory);
Console.WriteLine($"RobokitReadOnlySmokeTest: {checks} checks passed");

sealed class Robot : IAsyncDisposable
{
    readonly TcpListener status=new(IPAddress.Loopback,0),navigation=new(IPAddress.Loopback,0);
    readonly CancellationTokenSource stop=new();readonly List<Task> workers=[];readonly object gate=new();
    public ConcurrentQueue<(int Port,int Type,long At)> Types {get;}=new();
    public List<int> ReadTypes {get;}=[];
    public TaskCompletionSource Held {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int StatusPort=>((IPEndPoint)status.LocalEndpoint).Port;
    public int NavigationPort=>((IPEndPoint)navigation.LocalEndpoint).Port;
    public int TaskStatus=2;public bool DropNavigation,HoldNavigation,DropStatus;public string? LastCancel;public string Mode="ok";
    public bool ExtraStation; public string Md5="abc";public int AcceptLimit=int.MaxValue;public bool CascadeEnabled=true;public int? RejectNavigationCode;
    public JsonElement[] LastMoves=[];
    public void SetTask(string id,int state)=>tasks[id]=state;
    public bool ZeroPayloadReads=true;int mapsRead;readonly ConcurrentDictionary<string,int> tasks=new();
    public Robot(){status.Start();navigation.Start();workers.Add(Accept(status));workers.Add(Accept(navigation));}
    public void ResetRead(){mapsRead=0;ReadTypes.Clear();}
    async Task Accept(TcpListener listener)
    {
        try {while(!stop.IsCancellationRequested){var socket=await listener.AcceptTcpClientAsync(stop.Token);lock(gate)workers.Add(Serve(socket,((IPEndPoint)listener.LocalEndpoint).Port));}}
        catch(OperationCanceledException){}catch(SocketException)when(stop.IsCancellationRequested){}
    }
    async Task Serve(TcpClient socket,int port)
    {
        using(socket)try
        {
            var stream=socket.GetStream();
            while(!stop.IsCancellationRequested)
            {
                var header=new byte[16];await stream.ReadExactlyAsync(header,stop.Token);var frame=(Version:header[1],Sequence:BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)),PayloadLength:BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)),MessageType:BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8)));
                var data=new byte[frame.PayloadLength];await stream.ReadExactlyAsync(data,stop.Token);
                using var json=JsonDocument.Parse(data.Length==0?"{}":System.Text.Encoding.UTF8.GetString(data));
                Types.Enqueue((port,frame.MessageType,Stopwatch.GetTimestamp()));
                if(frame.MessageType==1100 && DropStatus)return;
                object result;
                if((port==StatusPort && frame.MessageType>=2000)||(port==NavigationPort && frame.MessageType<3000)) result=new{ret_code=40001,err_msg="wrong API port"};
                else if(frame.MessageType==3066)
                {
                    LastMoves=json.RootElement.GetProperty("move_task_list").EnumerateArray().Select(i=>i.Clone()).ToArray();
                    if(RejectNavigationCode is {} rejectCode)
                    {
                        result=new{ret_code=rejectCode,err_msg="control is preempted, can't execute any standalone operation"};
                        goto respond;
                    }
                    foreach(var item in LastMoves.Take(AcceptLimit))tasks[item.GetProperty("task_id").GetString()!]=2;
                    if(DropNavigation)return;
                    if(HoldNavigation){Held.TrySetResult();await Release.Task.WaitAsync(stop.Token);}
                    result=new{ret_code=0};
                }
                else if(frame.MessageType==3003){LastCancel=json.RootElement.GetProperty("taskId").GetString();
                    if(LastCancel is not null) tasks[LastCancel]=6;
                    if(CascadeEnabled && LastMoves.Any(m=>m.GetProperty("task_id").GetString()==LastCancel))
                        foreach(var item in LastMoves)tasks[item.GetProperty("task_id").GetString()!]=6;
                    result=new{ret_code=0};}
                else if(frame.MessageType==1110)
                {
                    var ids=json.RootElement.TryGetProperty("task_ids",out var list)?list.EnumerateArray().Select(i=>i.GetString()!).ToArray():new[]{"TASK"};
                    result=new{task_status_package=new{task_status_list=ids.Select(id=>new{task_id=id,status=tasks.TryGetValue(id,out var task)?(id=="TASK"?TaskStatus:task):404}).ToArray()},ret_code=0};
                }
                else if(frame.MessageType==1100)result=new{ret_code=0,x=0,y=0,current_station="LM1"};
                else
                {
                    ReadTypes.Add(frame.MessageType);if(frame.MessageType!=1302 && frame.MessageType!=1303 && data.Length!=0)ZeroPayloadReads=false;
                    if(Mode=="error")result=new{ret_code=40051,err_msg="no map"};
                    else result=frame.MessageType switch
                    {
                        1000=>new{version="3.4.8",netprotocol_version="1",ret_code=0},
                        1300=>new{current_map="demo",current_map_md5=Mode=="changed" && ++mapsRead>1?"def":Md5,ret_code=0},
                        1022=>new{loadmap_status=Mode=="loading"?2:1,ret_code=0},
                        1060=>new{locked=true,nick_name="sim-owner",ret_code=0},
                        1301 when Mode=="missing"=>new{ret_code=0},
                        1301=>new{stations=ExtraStation ? new[]{new{id="LM1",type="LocationMark",x=0,y=0,r=1.57},new{id="LM2",type="ActionPoint",x=1,y=0,r=0.0},new{id="LM3",type="LocationMark",x=2,y=0,r=0.0}} : new[]{new{id="LM1",type="LocationMark",x=0,y=0,r=1.57},new{id=Mode=="duplicate"?"LM1":"LM2",type="ActionPoint",x=1,y=0,r=0.0}},ret_code=0},
                        1302=>new{map_info=new[]{new{name="demo.smap",md5=Md5}},ret_code=0},
                        1303=>new{list=Mode=="bad-route"?new[]{"LM2","LM1"}:new[]{json.RootElement.GetProperty("source_id").GetString()!,json.RootElement.GetProperty("target_id").GetString()!},ret_code=0},
                        _=>new{ret_code=40001}
                    };
                }
                respond:
                var bytes=JsonSerializer.SerializeToUtf8Bytes(result);var response=new byte[16]; response[0]=0x5A;response[1]=frame.Version;BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2),frame.Sequence);BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4),bytes.Length);BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(8),(ushort)(frame.MessageType+10000));
                await stream.WriteAsync(response,stop.Token);await stream.WriteAsync(bytes,stop.Token);
            }
        }
        catch(OperationCanceledException){}catch(EndOfStreamException){}catch(IOException){}
    }
    public async ValueTask DisposeAsync(){stop.Cancel();status.Stop();navigation.Stop();Task[] all;lock(gate)all=workers.ToArray();await Task.WhenAll(all);stop.Dispose();}
}

sealed class ChainLookup : IAgvTransportChainLookup
{
    public Task<AgvTransportChain?> FindAsync(string id,CancellationToken ct)=>Task.FromResult<AgvTransportChain?>(
        id=="CHAIN"?new(id,"SIM",new[]{"LM1","LM2","LM3","LM4"}):null);
}
