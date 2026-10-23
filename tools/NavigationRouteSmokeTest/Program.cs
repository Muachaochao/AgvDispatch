using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Tasks;

var count=0;
void Check(bool value,string label){if(!value)throw new Exception(label);count++;Console.WriteLine("PASS "+label);}
async Task Reject(Func<Task> run,string label)
{
    try{await run();}catch(Exception ex)when(ex is InvalidOperationException or InvalidDataException or ArgumentException){Check(true,label);return;}
    throw new Exception("Accepted unexpectedly: "+label);
}
await using var robot=new Robot();
var options=new AgvOptions{Enabled=true,AllowCommands=true,RequestIntervalMs=100,RequestTimeoutMs=500,ConnectTimeoutMs=500,
    Robots=[new(){VehicleId="SIM",IpAddress="127.0.0.1",Port=robot.StatusPort,NavigationPort=robot.NavigationPort}]};
await using var connections=new AgvConnectionManager(options);
var gate=new Gate();var tasks=new Tasks();
var folder=Path.Combine(Path.GetTempPath(),"agv-route-test-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var path=Path.Combine(folder,"navigation-test.json");robot.IntentPath=path;
var settings=new SingleNavigationTestOptions{Enabled=true,VehicleId="SIM",BusinessExecutionDisabled=true};
SingleNavigationTestService Create()=>new(settings,options,connections,tasks,gate,path);
var service=Create();var user=TrafficUserContext.LocalOperation("test");
var query=new NavigationTestQuery("SIM","A","C"){ExpectedRobotMap="fixture",ExpectedRobotMapMd5=new string('a',32)};
gate.Open=false;
var catalog=await service.ReadStationsAsync(default);
Check(catalog.Stations.Select(s=>s.Id).SequenceEqual(new[]{"A","B","C","D"})&&catalog.SuggestedSourceSite=="A","Read real catalog and suggest source from robot-world location while gate held");
Check(robot.Sends==0&&robot.Cancels==0&&robot.UnexpectedCommands==0,"Catalog sends only read-only requests");
await Reject(()=>service.PreviewAsync(user,query,default),"Recovery hold blocks preview");gate.Open=true;
robot.DuplicateStation=true;await Reject(()=>service.ReadStationsAsync(default),"Duplicate station ids rejected");robot.DuplicateStation=false;
robot.InvalidCoordinate=true;await Reject(()=>service.ReadStationsAsync(default),"Missing coordinates rejected");robot.InvalidCoordinate=false;
robot.ChangeOnStations=true;await Reject(()=>service.ReadStationsAsync(default),"Map changes during catalog read rejected");robot.ChangeOnStations=false;robot.MapChanged=false;
await Reject(()=>service.PreviewAsync(user,query with{ExpectedRobotMap="stale"},default),"Station catalog map identity bound to preview");
await Reject(()=>service.PreviewAsync(user,query with{TargetSite="MISSING"},default),"Unknown selected destination rejected");
foreach(var mode in new[]{"empty","reverse","unknown","repeat","number"})
{robot.RouteMode=mode;await Reject(()=>service.PreviewAsync(user,query,default),"Invalid route rejected: "+mode);}robot.RouteMode="normal";
robot.ForeignActive=true;await Reject(()=>service.PreviewAsync(user,query,default),"Existing robot task cannot be overwritten by 3051");robot.ForeignActive=false;
robot.X=5;await Reject(()=>service.PreviewAsync(user,query,default),"Source must match physical robot position");robot.X=0;
var preview=await service.PreviewAsync(user,query,default);
Check(preview.Route.SequenceEqual(new[]{"A","B","C"})&&preview.TargetX==2&&robot.Sends==0,"Multi-hop route preview preserves actual intermediary without motion");
robot.RouteMode="alternate";
await Reject(()=>service.SubmitAsync(user,new(preview.PreviewId,true),default),"Route changes after confirmation block send");robot.RouteMode="normal";
Check(!File.Exists(path)&&robot.Sends==0,"Route rejection creates no durable intent or motion");
preview=await service.PreviewAsync(user,query,default);await service.ReadStationsAsync(default);
await Reject(()=>service.SubmitAsync(user,new(preview.PreviewId,true),default),"Reloading station catalog invalidates previous confirmation");
preview=await service.PreviewAsync(user,query,default);gate.Open=false;
await Reject(()=>service.SubmitAsync(user,new(preview.PreviewId,true),default),"Gate closing after preview blocks 3051");gate.Open=true;
preview=await service.PreviewAsync(user,query,default);
var sent=await service.SubmitAsync(user,new(preview.PreviewId,true),default);
Check(sent.State=="Accepted"&&sent.NavigationApi==3051&&sent.Route.SequenceEqual(new[]{"A","B","C"}),"3051 receipt records confirmed full route");
Check(robot.Sends==1&&robot.DurableIntent&&robot.LastKeys.SequenceEqual(new[]{"id","source_id","task_id"}),"Exactly one 3051 with source/target/unique id, durable intent and no action or speed override");
Check(robot.LastSource=="A"&&robot.LastTarget=="C"&&robot.LastTask==sent.VendorTaskId,"3051 receives endpoints, not separate intermediary commands");
await Reject(()=>service.SubmitAsync(user,new(preview.PreviewId,true),default),"Repeated click cannot send another navigation");
await Reject(()=>service.PreviewAsync(user,query,default),"Unfinished navigation cannot be replaced");
robot.X=1;robot.CurrentStation="B";
var status=await service.ReadAsync(default);
Check(status.Current?.State=="Running"&&status.Current.CurrentStation=="B"&&status.Current.X==1&&status.Current.ObservedAt is not null,"Running task displays fresh physical station and coordinates");
robot.TaskStatus=4;
status=await service.ReadAsync(default);Check(status.Current?.State=="Uncertain","Task completed away from destination is not shown as arrived");
robot.X=2;robot.CurrentStation="C";robot.Confidence=0.1;
status=await service.ReadAsync(default);Check(status.Current?.State=="Uncertain","Completed task with poor localization does not confirm arrival");robot.Confidence=0.9;
robot.Moving=true;status=await service.ReadAsync(default);Check(status.Current?.State=="Uncertain","Completed task while moving is not terminal for this test");robot.Moving=false;
robot.MapChanged=true;status=await service.ReadAsync(default);Check(status.Current?.State=="Uncertain","Completed task on changed map does not confirm arrival");robot.MapChanged=false;
status=await service.ReadAsync(default);Check(status.Current?.State=="Completed"&&status.Current.Message.Contains("终点静止"),"Original task complete plus correct map, stopped pose and localization confirms arrival");
service=Create();status=await service.ReadAsync(default);
Check(status.Current?.Route.SequenceEqual(new[]{"A","B","C"})==true&&status.Current.NavigationApi==3051&&robot.Sends==1,"Restart retains terminal task, itinerary and protocol without sending");
robot.Reset();preview=await service.PreviewAsync(user,query,default);robot.DropReply=true;
sent=await service.SubmitAsync(user,new(preview.PreviewId,true),default);robot.DropReply=false;
Check(sent.State=="Uncertain"&&robot.Sends==2,"Lost 3051 reply remains uncertain and never retries");
service=Create();robot.ForeignActive=true;
await Reject(()=>service.PreviewAsync(user,query,default),"Restart preserves unfinished intent even if vehicle state changes");robot.ForeignActive=false;
robot.UnknownOriginal=true;status=await service.ReadAsync(default);
Check(status.Current?.State=="Uncertain"&&robot.Sends==2,"Missing original task is not treated as unsent");robot.UnknownOriginal=false;
robot.WrongOriginal=true;status=await service.ReadAsync(default);
Check(status.Current?.State=="Uncertain","Unexpected task id cannot resolve original navigation");robot.WrongOriginal=false;
status=await service.ReadAsync(default);Check(status.Current?.State=="Running"&&robot.Sends==2,"Restart queries exact persisted task and never resends");
await Reject(()=>service.CancelAsync(user,new("foreign"),default),"Cannot cancel unrelated task");
gate.Open=false;await service.CancelAsync(user,new(sent.TaskId),default);
Check(robot.Cancels==1&&robot.CancelTask==sent.VendorTaskId,"Recovery hold allows scoped cancellation of original task");
status=await service.ReadAsync(default);Check(status.Current?.State=="Cancelled","Cancel receipt is followed by original terminal state and stopped vehicle");gate.Open=true;
robot.Reset();preview=await service.PreviewAsync(user,query,default);robot.OmitRetCode=true;
sent=await service.SubmitAsync(user,new(preview.PreviewId,true),default);robot.OmitRetCode=false;
Check(sent.State=="Uncertain"&&robot.Sends==3,"Missing positive navigation acknowledgment never implies acceptance");
await service.CancelAsync(user,new(sent.TaskId),default);await service.ReadAsync(default);
robot.Reset();preview=await service.PreviewAsync(user,query,default);
sent=await service.SubmitAsync(user,new(preview.PreviewId,true),default);
// Simulate a historical persisted 3066 intent; all reconciliation remains read-only.
var legacy=sent with{NavigationApi=3066,TargetX=null,TargetY=null,Route=[]};
File.WriteAllText(path,JsonSerializer.Serialize(legacy));robot.TaskStatus=4;robot.X=2;robot.CurrentStation="C";
service=Create();status=await service.ReadAsync(default);
Check(status.Current?.State=="Completed"&&robot.Sends==4,"Historical 3066 records resolve destination from verified current map without resending");
Check(robot.UnexpectedCommands==0,"No 3066, PLC, map write or control ownership command was issued");
Console.WriteLine($"PASS {count} checks; loopback simulator only, no real vehicle");

sealed class Tasks:IActiveTaskLookup
{public Task<IReadOnlyList<ActiveDispatchTask>> GetActiveTasksAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ActiveDispatchTask>>([]);}
sealed class Gate:IMapExecutionGate
{
    public bool Open=true;public MapExecutionMode Mode=>MapExecutionMode.Running;public bool CanStartNewWork=>Open;
    public ValueTask<IDisposable?> EnterAsync(MapExecutionOperation operation,CancellationToken ct)=>ValueTask.FromResult<IDisposable?>(Open?new Lease():null);
    public ValueTask<IDisposable> EnterControlAsync(CancellationToken ct)=>ValueTask.FromResult<IDisposable>(new Lease());
    public void SetMode(MapExecutionMode mode){}
    sealed class Lease:IDisposable{public void Dispose(){}}
}
sealed class Robot:IAsyncDisposable
{
    readonly TcpListener status=new(IPAddress.Loopback,0),nav=new(IPAddress.Loopback,0);
    readonly CancellationTokenSource stop=new();readonly ConcurrentBag<Task> workers=[];
    public int StatusPort=>((IPEndPoint)status.LocalEndpoint).Port;public int NavigationPort=>((IPEndPoint)nav.LocalEndpoint).Port;
    public string RouteMode="normal",CurrentStation="A",IntentPath="",LastTask="",LastSource="",LastTarget="",CancelTask="";
    public string[] LastKeys=[];public double X,Confidence=0.9;public int TaskStatus=2,Sends,Cancels,UnexpectedCommands;
    public bool DuplicateStation,InvalidCoordinate,MapChanged,ChangeOnStations,ForeignActive,DropReply,OmitRetCode,UnknownOriginal,WrongOriginal,Moving,DurableIntent;
    public Robot(){status.Start();nav.Start();workers.Add(Accept(status,false));workers.Add(Accept(nav,true));}
    public void Reset(){X=0;CurrentStation="A";TaskStatus=2;LastTask="";}
    async Task Accept(TcpListener listener,bool motion)
    {try{while(!stop.IsCancellationRequested){var client=await listener.AcceptTcpClientAsync(stop.Token);workers.Add(Serve(client,motion));}}catch(OperationCanceledException){}catch(ObjectDisposedException){}}
    async Task Serve(TcpClient client,bool motion)
    {
        using(client)try
        {
            var stream=client.GetStream();
            while(!stop.IsCancellationRequested)
            {
                var header=new byte[16];await stream.ReadExactlyAsync(header,stop.Token);
                var length=BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));var api=BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8));
                var bytes=new byte[length];await stream.ReadExactlyAsync(bytes,stop.Token);
                using var payload=JsonDocument.Parse(length==0?"{}":System.Text.Encoding.UTF8.GetString(bytes));var request=payload.RootElement;
                object response;
                switch(api)
                {
                    case 1300:response=new{current_map="fixture",current_map_md5=new string(MapChanged?'b':'a',32)};break;
                    case 1022:response=new{loadmap_status=1};break;
                    case 1060:response=new{locked=false};break;
                    case 1301:
                        response=new{stations=new object[]{new{id="A",x=0d,y=0d},new{id=DuplicateStation?"A":"B",x=1d,y=0d},new{id="C",x=2d,y=0d},new{id="D",x=3d,y=0d}}};
                        if(InvalidCoordinate)response=new{stations=new[]{new{id="A",x=0d}}};
                        if(ChangeOnStations)MapChanged=true;break;
                    case 1303:response=new{list=RouteMode switch{"empty"=>Array.Empty<object>(),"reverse"=>["C","B","A"],"unknown"=>["A","MISSING","C"],"repeat"=>["A","B","B","C"],"number"=>["A",12,"C"],"alternate"=>["A","D","C"],_=>new object[]{"A","B","C"}}};break;
                    case 1100:response=new{x=X,y=0d,confidence=Confidence,current_station=CurrentStation,emergency=false,blocked=false,charging=false,vx=Moving?0.5:0d,vy=0d,w=0d};break;
                    case 1110:
                        var filtered=request.TryGetProperty("task_ids",out var ids);
                        object[] rows=filtered?UnknownOriginal?[]:[new{task_id=WrongOriginal?"foreign":ids[0].GetString(),status=TaskStatus}]:ForeignActive?[new{task_id="foreign",status=2}]:LastTask.Length==0?[]:[new{task_id=LastTask,status=TaskStatus}];
                        response=new{task_status_package=new{task_status_list=rows}};break;
                    case 3051:
                        if(!motion)throw new InvalidDataException("Wrong motion port");
                        LastSource=request.GetProperty("source_id").GetString()!;LastTarget=request.GetProperty("id").GetString()!;LastTask=request.GetProperty("task_id").GetString()!;
                        LastKeys=request.EnumerateObject().Select(p=>p.Name).Order(StringComparer.Ordinal).ToArray();
                        DurableIntent=File.Exists(IntentPath)&&JsonDocument.Parse(File.ReadAllText(IntentPath)).RootElement.GetProperty("VendorTaskId").GetString()==LastTask;
                        TaskStatus=2;Interlocked.Increment(ref Sends);if(DropReply)return;response=OmitRetCode?new{}:(object)new{ret_code=0};break;
                    case 3003:
                        if(!motion)throw new InvalidDataException("Wrong cancel port");
                        CancelTask=request.GetProperty("taskId").GetString()!;Interlocked.Increment(ref Cancels);TaskStatus=6;response=new{ret_code=0};break;
                    default:Interlocked.Increment(ref UnexpectedCommands);response=new{ret_code=999};break;
                }
                var body=JsonSerializer.SerializeToUtf8Bytes(response);var reply=(byte[])header.Clone();
                BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(4),body.Length);BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8),(ushort)(api+10000));
                await stream.WriteAsync(reply,stop.Token);await stream.WriteAsync(body,stop.Token);
            }
        }
        catch(Exception ex)when(ex is OperationCanceledException or IOException or ObjectDisposedException){}
    }
    public async ValueTask DisposeAsync(){stop.Cancel();status.Stop();nav.Stop();await Task.WhenAll(workers.ToArray());stop.Dispose();}
}
