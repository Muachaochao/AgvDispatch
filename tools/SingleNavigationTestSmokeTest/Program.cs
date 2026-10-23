using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;

if(args.Length==3&&args[0]=="--prepare-runtime")
{
 var parsed=new Smap2DImportService().Read(File.ReadAllBytes(args[1]),Path.GetFullPath(args[1]));
 var definition=Smap2DImportService.PrepareRobotWorldRuntime(parsed,
     parsed.Definition.Nodes.ToDictionary(n=>n.AgvSiteId!,_=>MapNodeType.WaitingPoint,StringComparer.Ordinal));
 File.WriteAllText(args[2],JsonSerializer.Serialize(definition,new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true,Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}}));
 Console.WriteLine("Prepared robot-world runtime map; explicit commissioning role: all sites are test waiting points. No runtime activation or device command.");return;
}

var count=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
async Task Reject(Func<Task> run,string name){try{await run();throw new Exception("Did not reject: "+name);}catch(Exception e)when(e is InvalidOperationException or ArgumentException or InvalidDataException){Check(true,name);}}
var fixture=new JsonObject {
 ["header"]=new JsonObject{["mapType"]="2D-Map",["mapName"]="synthetic-test",["version"]="1.0.6",["resolution"]=0.02,["minPos"]=JsonNode.Parse("{\"x\":-2,\"y\":-2}"),["maxPos"]=JsonNode.Parse("{\"x\":5,\"y\":5}")},
 ["advancedPointList"]=JsonNode.Parse("[{\"className\":\"LocationMark\",\"instanceName\":\"LM1\",\"pos\":{\"x\":0,\"y\":0}},{\"className\":\"LocationMark\",\"instanceName\":\"LM2\",\"pos\":{\"x\":1,\"y\":0}}]"),
 ["advancedCurveList"]=new JsonArray()
};
foreach(var pair in new[]{("LM1","LM2",0,1),("LM2","LM1",1,0)})((JsonArray)fixture["advancedCurveList"]!).Add(new JsonObject{
 ["className"]="DegenerateBezier",["instanceName"]=pair.Item1+"-"+pair.Item2,
 ["startPos"]=new JsonObject{["instanceName"]=pair.Item1,["pos"]=new JsonObject{["x"]=pair.Item3,["y"]=0}},
 ["endPos"]=new JsonObject{["instanceName"]=pair.Item2,["pos"]=new JsonObject{["x"]=pair.Item4,["y"]=0}},
 ["controlPos1"]=new JsonObject{["x"]=pair.Item3==0?1.0/3:2.0/3,["y"]=0},
 ["controlPos2"]=new JsonObject{["x"]=pair.Item3==0?2.0/3:1.0/3,["y"]=0}
});
var imported=new Smap2DImportService().Read(JsonSerializer.SerializeToUtf8Bytes(fixture),"synthetic");
Check(imported.Definition.Nodes.Count==2&&imported.Definition.Edges.Count==2,"DegenerateBezier preserves both directed entries");
Check(imported.Definition.Edges.All(e=>e.GeometryKind==MapEdgeGeometryKind.CubicBezier&&!e.Enabled&&!e.Bidirectional),"Controls and conservative disabled state retained");
Check(imported.Definition.Nodes.Single(n=>n.AgvSiteId=="LM1").X+imported.OffsetX==0,"Original robot coordinates recover exactly from display offset");
Check(!imported.Warnings.Any(w=>w.Contains("未转换路径")),"Known degenerate curves no longer skipped");
var prepared=Smap2DImportService.PrepareRobotWorldRuntime(imported,imported.Definition.Nodes.ToDictionary(n=>n.AgvSiteId!,_=>MapNodeType.WaitingPoint));
Check(prepared.CoordinateSystem=="RobotWorld"&&prepared.Nodes.All(n=>n.Enabled&&n.Type==MapNodeType.WaitingPoint)&&prepared.Edges.All(e=>e.Enabled),"Runtime preparation explicitly enables confirmed waiting points and paths");
Check(prepared.Nodes.Single(n=>n.AgvSiteId=="LM1").X==0&&prepared.Nodes.Single(n=>n.AgvSiteId=="LM2").X==1,"Runtime coordinates recover original robot-world positions");
Check(prepared.Edges[0].ControlPoints![0].X==imported.Definition.Edges[0].ControlPoints![0].X+imported.OffsetX,"Runtime conversion translates ordered Bezier controls together with nodes");
try{Smap2DImportService.PrepareRobotWorldRuntime(imported,new Dictionary<string,MapNodeType>());throw new Exception("Missing roles accepted");}catch(InvalidDataException){Check(true,"Missing station purpose prevents runtime preparation");}
Check(new MapValidationService().Validate(imported.Definition).IsValid,"Explicit opposite one-way paths pass backend validation");
var edge=imported.Definition.Edges[0];
Check(new MapValidationService().Validate(imported.Definition with{Edges=[edge,edge with{Id="duplicate"}]}).Issues.Any(i=>i.Code=="EDGE_OVERLAP"),"Same-direction duplicates remain blocked");
Check(new MapValidationService().Validate(imported.Definition with{Edges=[edge with{Bidirectional=true},imported.Definition.Edges[1]]}).Issues.Any(i=>i.Code=="EDGE_OVERLAP"),"Bidirectional plus reverse duplicate remains blocked");
var malformed=fixture.DeepClone();malformed["advancedCurveList"]![0]!["controlPos1"]=null;
try{new Smap2DImportService().Read(JsonSerializer.SerializeToUtf8Bytes(malformed),"synthetic");throw new Exception("Malformed controls accepted");}catch(Exception e)when(e is InvalidOperationException or InvalidDataException){Check(true,"Malformed controls rejected rather than replaced by invented line");}
var arcFixture=fixture.DeepClone();
foreach(var curve in arcFixture["advancedCurveList"]!.AsArray()){curve!["className"]="ArcPath";curve["controlPos1"]=new JsonObject{["x"]=0.5,["y"]=0.2};curve.AsObject().Remove("controlPos2");}
var arcs=new Smap2DImportService().Read(JsonSerializer.SerializeToUtf8Bytes(arcFixture),"synthetic-arc");
Check(arcs.Definition.Edges.Count==2&&arcs.Definition.Edges.All(e=>e.GeometryKind==MapEdgeGeometryKind.CircularArc&&e.ControlPoints!.Count==1),"ArcPath preserves both directions with one point on circle");
Check(arcs.Definition.Edges.All(e=>e.Distance>1),"Arc length follows circle rather than straight chord");
Check(new MapValidationService().Validate(arcs.Definition).IsValid,"Opposite circular arcs pass geometry validation");
var unsupported=fixture.DeepClone();unsupported["advancedCurveList"]![0]!["className"]="FuturePath";
var skipped=new Smap2DImportService().Read(JsonSerializer.SerializeToUtf8Bytes(unsupported),"synthetic-unknown");
Check(skipped.Definition.Edges.Count==1&&skipped.Definition.ImportWarnings.Any(w=>w.Contains("FuturePath")),"Unknown geometry is surfaced in persistent import warnings rather than fabricated");
if(args.Length>1){var field=new Smap2DImportService().Read(File.ReadAllBytes(args[1]),args[1]);
Check(field.Definition.Nodes.Count==4&&field.Definition.Edges.Count==11&&field.Definition.Edges.Count(e=>e.GeometryKind==MapEdgeGeometryKind.CircularArc)==2,"Latest field export preserves four nodes, eleven paths and two arcs");
Check(field.Definition.ImportWarnings.Any(w=>w.Contains("LM3-LM1")),"Duplicate source path name is retained and reported");}
if(args.Length>0)
{
 var real=new Smap2DImportService().Read(File.ReadAllBytes(args[0]),args[0]);
 Check(real.Definition.Nodes.Count==4&&real.Definition.Edges.Count==10,"User supplied four-point file imports four nodes / ten directed paths");
 Check(new MapValidationService().Validate(real.Definition).IsValid,"User map passes backend validation without redrawing paths");
 var pairs=real.Definition.Edges.Select(e=>(e.From,e.To)).ToArray();
 Check(pairs.All(p=>pairs.Contains((p.To,p.From))),"User file reverse connections preserved");
}
await using var robot=new Robot();
var options=new AgvOptions{Enabled=true,AllowCommands=true,RequestIntervalMs=100,RequestTimeoutMs=600,ConnectTimeoutMs=600,
 Robots=[new(){VehicleId="SIM",IpAddress="127.0.0.1",Port=robot.StatusPort,NavigationPort=robot.NavigationPort}]};
await using var connections=new AgvConnectionManager(options);
var downloader=new RobotMapDownloadService(options,connections,robot.ConfigPort);
var downloaded=await downloader.ReadCurrentAsync(new("SIM"),default);
Check(downloaded.MapName=="test"&&downloaded.ContentSha256.Length==64&&downloaded.RobotMapMd5==new string('a',32),"4011 download records device MD5 and distinct saved-content SHA256");
Check(robot.Downloads==1&&robot.Sends==0&&robot.CancelCount==0,"Readonly download issues no navigation or cancel command");
robot.DownloadWrong=true;await Reject(()=>downloader.ReadCurrentAsync(new("SIM"),default),"Wrong downloaded map identity rejected");robot.DownloadWrong=false;
robot.DownloadChanged=true;await Reject(()=>downloader.ReadCurrentAsync(new("SIM"),default),"Robot map changing during download rejects stale snapshot");robot.DownloadChanged=false;robot.ChangedMap=false;
await Reject(()=>downloader.ReadCurrentAsync(new("MISSING"),default),"Unconfigured vehicle cannot download");
var active=new Tasks();var gate=new Gate();var settings=new SingleNavigationTestOptions{Enabled=true,VehicleId="SIM",BusinessExecutionDisabled=true};
var folder=Path.Combine(Path.GetTempPath(),"agv-nav-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
var recordPath=Path.Combine(folder,"navigation-test.json");
SingleNavigationTestService Create(SingleNavigationTestOptions? s=null)=>new(s??settings,options,connections,active,gate,recordPath);
gate.Open=false;
Check(!(await Create().ReadAsync(default)).Ready,"Closed execution gate is visible before preview");
var service=Create();var user=TrafficUserContext.LocalOperation("test");var query=new NavigationTestQuery("SIM","LM1","LM2");
await Reject(()=>service.PreviewAsync(user,query,default),"Closed gate rejects preview before robot requests");
Check(robot.Sends==0&&!File.Exists(recordPath),"Blocked preview creates no send intent or navigation command");
gate.Open=true;
Check((await service.ReadAsync(default)).Ready,"Readiness recovers when gate opens");
robot.WrappedTasks=true;robot.EmptyHistoricalId=true;
var nestedPreview=await service.PreviewAsync(user,query,default);
Check(nestedPreview.Route.SequenceEqual(new[]{"LM1","LM2"})&&robot.Sends==0,"Physical 1110 wrapper with completed empty-id history permits read-only preview");
robot.ActiveForeign=true;await Reject(()=>service.PreviewAsync(user,query,default),"Wrapped running task still blocks navigation");robot.ActiveForeign=false;
robot.MissingTasks=true;await Reject(()=>service.PreviewAsync(user,query,default),"Wrapped missing task list is not treated as idle");robot.MissingTasks=false;
robot.UnknownStatus=true;await Reject(()=>service.PreviewAsync(user,query,default),"Wrapped unknown status is not treated as terminal");robot.UnknownStatus=false;
robot.WrappedTasks=false;robot.EmptyHistoricalId=false;
await Reject(()=>Create(new(){VehicleId="SIM",BusinessExecutionDisabled=true}).PreviewAsync(user,query,default),"Default disabled blocks motion preparation");
options.AllowCommands=false;await Reject(()=>service.PreviewAsync(user,query,default),"Global AGV command switch cannot be bypassed");options.AllowCommands=true;
await Reject(()=>service.PreviewAsync(user,query with{VehicleId="OTHER"},default),"Unapproved vehicle rejected");
robot.Locked=true;await Reject(()=>service.PreviewAsync(user,query,default),"Occupied control rejected");robot.Locked=false;
robot.Away=true;await Reject(()=>service.PreviewAsync(user,query,default),"Vehicle away from source rejected");robot.Away=false;
robot.Indirect=true;await Reject(()=>service.PreviewAsync(user,query,default),"Indirect route rejected");robot.Indirect=false;
robot.ActiveForeign=true;await Reject(()=>service.PreviewAsync(user,query,default),"Foreign running task rejected");robot.ActiveForeign=false;
robot.MissingTasks=true;await Reject(()=>service.PreviewAsync(user,query,default),"Missing task list rejected");robot.MissingTasks=false;
active.Busy=true;await Reject(()=>service.PreviewAsync(user,query,default),"Active PLC business task rejected");active.Busy=false;
var preview=await service.PreviewAsync(user,query,default);Check(robot.Sends==0,"Preview sends no navigation command");
robot.WrappedTasks=true; // exercise submit, status, cancel, restart with the real envelope
await Reject(()=>service.SubmitAsync(user,new(preview.PreviewId,false),default),"Explicit site confirmation required");
preview=await service.PreviewAsync(user,query,default);robot.ChangedMap=true;
await Reject(()=>service.SubmitAsync(user,new(preview.PreviewId,true),default),"Map changed after preview blocks send");robot.ChangedMap=false;
preview=await service.PreviewAsync(user,query,default);gate.Open=false;
await Reject(()=>service.SubmitAsync(user,new(preview.PreviewId,true),default),"Execution recovery/map gate respected");
Check(!File.Exists(recordPath)&&robot.Sends==0,"Gate closing after preview rejects submit before persistence/send");gate.Open=true;
preview=await service.PreviewAsync(user,query,default);
robot.IntentPath=recordPath;
var sent=await service.SubmitAsync(user,new(preview.PreviewId,true),default);
Check(sent.State=="Accepted"&&robot.Sends==1&&robot.IntentExisted,"Intent is durable before sending one 3066 command");
Check(robot.LastSource=="LM1"&&robot.LastTarget=="LM2"&&robot.LastTask==sent.VendorTaskId,"Exact vendor pair/task id transmitted without action");
await Reject(()=>service.SubmitAsync(user,new(preview.PreviewId,true),default),"Repeated click cannot resend");
await Reject(()=>service.PreviewAsync(user,query,default),"Only one unresolved navigation allowed");
var read=await service.ReadAsync(default);Check(read.Current?.State=="Running","Status queries original task");
await Reject(()=>service.CancelAsync(user,new("FOREIGN"),default),"Cannot cancel foreign task");
await service.CancelAsync(user,new(sent.TaskId),default);
Check(robot.CancelId==sent.VendorTaskId&&robot.CancelCount==1,"Cancel targets only recorded vendor task");
read=await service.ReadAsync(default);Check(read.Current?.State=="Cancelled","Cancel acknowledgment reconciled to terminal state");
preview=await service.PreviewAsync(user,query,default);robot.DropReply=true;
sent=await service.SubmitAsync(user,new(preview.PreviewId,true),default);
Check(sent.State=="Uncertain"&&robot.Sends==2,"Dropped navigation reply is uncertain and never retried");
service=Create();read=await service.ReadAsync(default);
Check(read.Current?.TaskId==sent.TaskId&&read.Current.State=="Running"&&robot.Sends==2,"Restart reconciles persisted original task without resend");
robot.Status=4;read=await service.ReadAsync(default);Check(read.Current?.State=="Completed","Completion requires terminal original task and stopped vehicle");
var recovery=new Barrier();var transfer=new TransferBarrier();
var realGate=new MapExecutionGate(recovery,transfer);
using(await realGate.EnterControlAsync(default))realGate.SetMode(MapExecutionMode.Running);
Check(!realGate.CanStartNewWork&&realGate.NewWorkBlockReason!.Contains("启动恢复"),"Running map still explains closed startup recovery barrier");
Check(await realGate.EnterAsync(MapExecutionOperation.NewWork,default) is null,"Recovery diagnostics do not bypass recovery hold");
recovery.Open=true;transfer.Open=false;
Check(realGate.NewWorkBlockReason!.Contains("传输"),"Map transfer hold has its own diagnosis");
transfer.Open=true;
using(var lease=await realGate.EnterAsync(MapExecutionOperation.NewWork,default))Check(lease is not null&&realGate.NewWorkBlockReason is null,"All barriers open permits work");
using(await realGate.EnterControlAsync(default))realGate.SetMode(MapExecutionMode.Held);
Check(realGate.NewWorkBlockReason!.Contains("地图切换"),"Held map remains blocked independently of recovery");
Console.WriteLine($"PASS {count} checks; simulation only, no physical motion");

sealed class Tasks:IActiveTaskLookup
{public bool Busy;public Task<IReadOnlyList<ActiveDispatchTask>> GetActiveTasksAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ActiveDispatchTask>>(Busy?[null!]:[]);}
sealed class Gate:IMapExecutionGate
{public bool Open=true;public MapExecutionMode Mode=>MapExecutionMode.Running;public bool CanStartNewWork=>Open;public ValueTask<IDisposable?> EnterAsync(MapExecutionOperation op,CancellationToken ct)=>ValueTask.FromResult<IDisposable?>(Open?new Lease():null);public ValueTask<IDisposable> EnterControlAsync(CancellationToken ct)=>ValueTask.FromResult<IDisposable>(new Lease());public void SetMode(MapExecutionMode m){}sealed class Lease:IDisposable{public void Dispose(){}}}
sealed class Robot:IAsyncDisposable
{
 readonly TcpListener status=new(IPAddress.Loopback,0),nav=new(IPAddress.Loopback,0),config=new(IPAddress.Loopback,0);readonly CancellationTokenSource stop=new();readonly ConcurrentBag<Task> workers=[];
 public int ConfigPort=>((IPEndPoint)config.LocalEndpoint).Port;public bool DownloadChanged,DownloadWrong;public int Downloads;
 public int StatusPort=>((IPEndPoint)status.LocalEndpoint).Port;public int NavigationPort=>((IPEndPoint)nav.LocalEndpoint).Port;
 public bool Locked,Away,Indirect,ActiveForeign,MissingTasks,ChangedMap,DropReply,IntentExisted,WrappedTasks,EmptyHistoricalId,UnknownStatus;public string IntentPath="",LastSource="",LastTarget="",LastTask="",CancelId="";public int Sends,CancelCount,Status=2;
 public Robot(){status.Start();nav.Start();config.Start();workers.Add(Accept(config,false));workers.Add(Accept(status,false));workers.Add(Accept(nav,true));}
 async Task Accept(TcpListener listener,bool motion){try{while(!stop.IsCancellationRequested){var c=await listener.AcceptTcpClientAsync(stop.Token);workers.Add(Serve(c,motion));}}catch(OperationCanceledException){}catch(ObjectDisposedException){}}
 async Task Serve(TcpClient client,bool motion)
 {
  using(client)try{var s=client.GetStream();while(!stop.IsCancellationRequested){var h=new byte[16];await s.ReadExactlyAsync(h,stop.Token);var n=BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(4));var type=BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(8));var bytes=new byte[n];await s.ReadExactlyAsync(bytes,stop.Token);using var req=JsonDocument.Parse(n==0?"{}":System.Text.Encoding.UTF8.GetString(bytes));object result;
   switch(type){
    case 4011:
     Interlocked.Increment(ref Downloads);result=new{header=new{mapType="2D-Map",mapName=DownloadWrong?"wrong":"test"}};
     if(DownloadChanged)ChangedMap=true;break;
    case 1300:result=new{current_map=ChangedMap?"changed":"test",current_map_md5=new string(ChangedMap?'b':'a',32)};break;
    case 1022:result=new{loadmap_status=1};break;
    case 1060:result=new{locked=Locked};break;
    case 1301:result=new{stations=new[]{new{id="LM1",x=0d,y=0d},new{id="LM2",x=1d,y=0d}}};break;
    case 1303:result=new{list=Indirect?new[]{"LM1","VIA","LM2"}:new[]{"LM1","LM2"}};break;
    case 1100:result=new{x=Away?5d:0d,y=0d,angle=0d,confidence=0.9,battery_level=0.95,blocked=false,emergency=false,charging=false,current_station=Away?"":"LM1",vx=0d,vy=0d,w=0d};break;
    case 1110:
     if(MissingTasks){result=new{};break;}
     var filtered=req.RootElement.TryGetProperty("task_ids",out var ids);
     var taskData=new{task_status_list=filtered?new[]{new{task_id=ids[0].GetString()!,status=Status}}:ActiveForeign?new[]{new{task_id="foreign",status=2}}:UnknownStatus?new[]{new{task_id="unknown",status=404}}:LastTask.Length>0?new[]{new{task_id=LastTask,status=Status}}:EmptyHistoricalId?new[]{new{task_id="",status=4}}:Array.Empty<object>()};
     result=WrappedTasks?new{ret_code=0,task_status_package=(object)taskData}:(object)taskData;break;
    case 3066:
     if(!motion)throw new Exception("Wrong port");var item=req.RootElement.GetProperty("move_task_list")[0];if(item.TryGetProperty("operation",out _))throw new Exception("Unexpected action");
     LastSource=item.GetProperty("source_id").GetString()!;LastTarget=item.GetProperty("id").GetString()!;LastTask=item.GetProperty("task_id").GetString()!;IntentExisted=File.Exists(IntentPath);Status=2;Interlocked.Increment(ref Sends);
     if(DropReply)return;result=new{ret_code=0};break;
    case 3003:if(!motion)throw new Exception("Wrong cancel port");CancelId=req.RootElement.GetProperty("taskId").GetString()!;Interlocked.Increment(ref CancelCount);Status=6;result=new{ret_code=0};break;
    default:throw new Exception("Unexpected API "+type);
   }
   var body=JsonSerializer.SerializeToUtf8Bytes(result);var reply=(byte[])h.Clone();BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(4),body.Length);BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8),(ushort)(type+10000));await s.WriteAsync(reply,stop.Token);await s.WriteAsync(body,stop.Token);
  }}catch(Exception e)when(e is OperationCanceledException or EndOfStreamException or IOException or ObjectDisposedException){}
 }
 public async ValueTask DisposeAsync(){stop.Cancel();status.Stop();nav.Stop();config.Stop();await Task.WhenAll(workers.ToArray());stop.Dispose();}
}

sealed class Barrier:IRecoveryBarrier { public bool Open;public bool IsOpen=>Open; }
sealed class TransferBarrier:IMapTransferRecoveryBarrier { public bool Open=true;public bool IsOpen=>Open; }
