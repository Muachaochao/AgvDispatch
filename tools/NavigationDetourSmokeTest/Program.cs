using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Tasks;

var checks=0;
void Check(bool value,string label){if(!value)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
async Task Reject(Func<Task> run,string label)
{try{await run();}catch(Exception ex)when(ex is InvalidOperationException or InvalidDataException or ArgumentException){Check(true,label);return;}throw new Exception("Unexpected acceptance: "+label);}
await using var robot=new Robot();
var options=new AgvOptions{Enabled=true,AllowCommands=true,RequestIntervalMs=100,RequestTimeoutMs=500,ConnectTimeoutMs=500,
    Robots=[new(){VehicleId="SIM",IpAddress="127.0.0.1",Port=robot.StatusPort,NavigationPort=robot.NavigationPort}]};
await using var connections=new AgvConnectionManager(options);
var folder=Path.Combine(Path.GetTempPath(),"agv-detour-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
var path=Path.Combine(folder,"navigation.json");robot.IntentPath=path;
var planner=new Planner();var roads=new Closures();robot.ClosureGateHeld=()=>roads.Held;
var settings=new SingleNavigationTestOptions{Enabled=true,VehicleId="SIM",BusinessExecutionDisabled=true,DetourWaitSeconds=1};
SingleNavigationTestService Create()=>new(settings,options,connections,new Tasks(),new Gate(),path,detourPlanner:planner,roadClosures:roads);
var service=Create();var user=TrafficUserContext.LocalOperation("loopback");
roads.Active=true;
await Reject(()=>service.PreviewAsync(user,new("SIM","A","C"),default),"Initial 3051 preview is denied while any current-map closure is active");roads.Active=false;
var closedAfterPreview=await service.PreviewAsync(user,new("SIM","A","C"),default);roads.Active=true;
await Reject(()=>service.SubmitAsync(user,new(closedAfterPreview.PreviewId,true),default),"A closure added after initial preview blocks 3051 submission");roads.Active=false;
closedAfterPreview=await service.PreviewAsync(user,new("SIM","A","C"),default);roads.CloseOnEnter=true;
await Reject(()=>service.SubmitAsync(user,new(closedAfterPreview.PreviewId,true),default),"An unrelated closure added at the final send gate also blocks 3051");roads.Active=false;roads.CloseOnEnter=false;
Check(robot.DirectNavigations==0&&!File.Exists(path),"All initial closure failures precede durable motion intent and any motion frame");
var normal=await service.PreviewAsync(user,new("SIM","A","C"),default);
var sent=await service.SubmitAsync(user,new(normal.PreviewId,true),default);
Check(robot.InitialSendHeld,"Initial 3051 retains the closure mutation lease through the actual motion frame");
robot.X=1;robot.Station="B";robot.Blocked=true;
await service.ReadAsync(default);
await Reject(()=>service.PreviewDetourAsync(user,new(sent.TaskId),default),"Transient obstacle waits without cancelling or replanning");
Check(robot.Cancels==0&&robot.Detours==0,"Read and short wait issue no motion");
await Task.Delay(1100);
robot.X=0.5;robot.Station="A";
await Reject(()=>service.PreviewDetourAsync(user,new(sent.TaskId),default),"Mid-edge pose cannot borrow the old A/LM1 station");
robot.X=1;robot.Station="B";robot.MapChanged=true;
await Reject(()=>service.PreviewDetourAsync(user,new(sent.TaskId),default),"Robot map change blocks detour planning");robot.MapChanged=false;
robot.Foreign=true;
await Reject(()=>service.PreviewDetourAsync(user,new(sent.TaskId),default),"Foreign robot task cannot be overwritten");robot.Foreign=false;
planner.Available=false;
var unavailable=await service.PreviewDetourAsync(user,new(sent.TaskId),default);
Check(!unavailable.Plan.Available&&robot.Cancels==0,"No alternate route is a read-only wait result");
await Reject(()=>service.ConfirmDetourAsync(user,new(sent.TaskId,unavailable.PreviewId,true),default),"Unavailable route cannot cancel the original task");planner.Available=true;
var preview=await service.PreviewDetourAsync(user,new(sent.TaskId),default);
Check(preview.Plan.SourceSite=="B"&&preview.Plan.Route.SequenceEqual(new[]{"B","D","C"})&&robot.Cancels==0,"Candidate starts at the actual reached station, preserving the original target");
Check(planner.LastRequest?.SuggestedBlockedPairs.SequenceEqual(new[]{new NavigationTestSitePair("B","C")})==true,
    "Current route leg is supplied as an automatically inferred obstruction candidate for exact map validation");
Check(preview.ExpiresAt-DateTimeOffset.UtcNow>TimeSpan.FromSeconds(100),
    "Candidate allows a two-minute physical review window while confirmation revalidates all safety conditions");
planner.Revision++;
await Reject(()=>service.ConfirmDetourAsync(user,new(sent.TaskId,preview.PreviewId,true),default),"Closure change invalidates confirmation before cancellation");
preview=await service.PreviewDetourAsync(user,new(sent.TaskId),default);
robot.DropCancelReply=true;
var cancelled=await service.ConfirmDetourAsync(user,new(sent.TaskId,preview.PreviewId,true),default);robot.DropCancelReply=false;
Check(cancelled.TaskId==sent.TaskId&&cancelled.Detour?.State=="AwaitingOriginalTerminal"&&robot.Cancels==1&&robot.CancelDurable,"Lost cancel acknowledgment preserves a durable exact original task intent");
Check(robot.CancelId==sent.VendorTaskId&&robot.Detours==0,"Scoped 3003 only; no replacement navigation at cancellation");
await Reject(()=>service.ConfirmDetourAsync(user,new(sent.TaskId,preview.PreviewId,true),default),"Repeated confirmation cannot repeat cancellation");
robot.UnknownOriginal=true;
var status=await service.ReadAsync(default);
Check(status.Current?.Detour?.State!="Ready"&&robot.Detours==0,"404 original history cannot authorize replacement");robot.UnknownOriginal=false;
await Reject(()=>service.ContinueDetourAsync(user,new(sent.TaskId,preview.PreviewId,true),default),"Replacement requires all original tasks terminal and two stopped observations");
await service.ReadAsync(default);await Task.Delay(550);status=await service.ReadAsync(default);
Check(status.Current?.Detour?.State=="Ready"&&robot.Detours==0&&robot.Cancels==1,"GET only observes: terminal original plus stable location requires a second explicit motion confirmation");
// Restart while the proposed replacement has not been sent: neither GET nor POST may resume it.
var restarted=Create();await restarted.ReadAsync(default);
await Reject(()=>restarted.ContinueDetourAsync(user,new(sent.TaskId,preview.PreviewId,true),default),"Restart puts unsent replacement into ReviewHold");
Check(robot.Detours==0&&robot.Cancels==1,"Restart does not repeat cancel or issue new navigation");
robot.UnknownOriginal=true;
await Reject(()=>restarted.PreviewDetourAsync(user,new(sent.TaskId),default),"ReviewHold cannot refresh candidates while original task history is unknown");robot.UnknownOriginal=false;
robot.UnknownBlocked=true;
await Reject(()=>restarted.PreviewDetourAsync(user,new(sent.TaskId),default),"Unknown obstacle signal cannot qualify a safe stopped location");robot.UnknownBlocked=false;
planner.Revision++;
preview=await restarted.PreviewDetourAsync(user,new(sent.TaskId),default);
await restarted.ConfirmDetourAsync(user,new(sent.TaskId,preview.PreviewId,true),default);
Check(robot.Cancels==1,"Explicit candidate refresh after restart and closure change does not resend original cancellation");
service=restarted;await service.ReadAsync(default);await Task.Delay(550);status=await service.ReadAsync(default);
Check(status.Current?.Detour?.State=="Ready"&&robot.Detours==0,"A refreshed candidate requires fresh stopped proof and second explicit execute confirmation");
planner.Revision++;
await Reject(()=>service.ContinueDetourAsync(user,new(sent.TaskId,preview.PreviewId,true),default),"Closure revision is checked again at the final send gate");planner.Revision--;
robot.Emergency=true;
await Reject(()=>service.ContinueDetourAsync(user,new(sent.TaskId,preview.PreviewId,true),default),"Emergency stop blocks the confirmed detour");robot.Emergency=false;
// Failed observations deliberately clear the two-sample stopped proof; reacquire it.
await service.ReadAsync(default);await Task.Delay(550);await service.ReadAsync(default);
robot.Indirect=true;
await Reject(()=>service.ContinueDetourAsync(user,new(sent.TaskId,preview.PreviewId,true),default),"Every 3066 pair must be a direct robot-map edge; no implicit 3051 routing");robot.Indirect=false;
var rerouted=await service.ContinueDetourAsync(user,new(sent.TaskId,preview.PreviewId,true),default);
Check(rerouted.TaskId==sent.TaskId&&rerouted.SourceSite=="B"&&rerouted.TargetSite=="C"&&rerouted.NavigationApi==3066,"Replacement retains the test identity and target, uses actual station B and specified path 3066");
Check(robot.Detours==1&&robot.DetourDurable&&robot.Segments.Length==2,"New chain identity and full route are durable before the only 3066 frame");
Check(rerouted.Detour?.OriginalVendorTaskIds.SequenceEqual(new[]{sent.VendorTaskId})==true&&rerouted.Detour.NewVendorTaskIds.Count==2&&rerouted.VendorTaskIds.Count==2,"Old and every new vendor task ID remain auditable");
Check(robot.Segments.All(x=>x.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(new[]{"id","source_id","task_id"})),"Detour has no motion action, reverse command, speed or safety override");
await Reject(()=>service.ContinueDetourAsync(user,new(sent.TaskId,preview.PreviewId,true),default),"Repeated execute cannot send the chain twice");
service=Create();status=await service.ReadAsync(default);
Check(robot.Detours==1&&status.Current?.TaskId==sent.TaskId,"Restart after send only queries the persisted replacement IDs");
robot.OriginalReappears=true;robot.NewTerminal=true;robot.Blocked=false;robot.X=2;robot.Station="C";
status=await service.ReadAsync(default);
Check(status.Current?.State=="Uncertain","Original activity reappearing prevents new completion through global idle check");robot.OriginalReappears=false;
robot.PartialNewHistory=true;status=await service.ReadAsync(default);
Check(status.Current?.State=="Uncertain"&&robot.Detours==1,"Partial replacement history is never completion and never retransmission");robot.PartialNewHistory=false;
robot.Fatal=true;status=await service.ReadAsync(default);
Check(status.Current?.State=="Uncertain", "A terminal replacement chain with Fatal 52200 cannot report completed arrival");robot.Fatal=false;
status=await service.ReadAsync(default);
Check(status.Current?.State=="Completed"&&status.Current.Detour?.State=="Completed"&&robot.Detours==1,"All replacement segments terminal plus verified destination confirms arrival");
Check(robot.Unexpected==0&&robot.DirectNavigations==1,"Only the original 3051, scoped cancel and explicit 3066 were sent; no device safety or PLC commands");
robot.Reset();normal=await service.PreviewAsync(user,new("SIM","A","C"),default);sent=await service.SubmitAsync(user,new(normal.PreviewId,true),default);
robot.X=1;robot.Station="B";robot.Blocked=true;await service.ReadAsync(default);await Task.Delay(1100);
preview=await service.PreviewDetourAsync(user,new(sent.TaskId),default);
await service.ConfirmDetourAsync(user,new(sent.TaskId,preview.PreviewId,true),default);
await service.ReadAsync(default);await Task.Delay(550);await service.ReadAsync(default);
robot.DropDetourReply=true;rerouted=await service.ContinueDetourAsync(user,new(sent.TaskId,preview.PreviewId,true),default);robot.DropDetourReply=false;
Check(rerouted.State=="Uncertain"&&robot.Detours==2,"Lost replacement reply leaves the durable chain uncertain without retry");
service=Create();status=await service.ReadAsync(default);
Check(status.Current?.State=="Running"&&robot.Detours==2&&robot.Cancels==2,"Restart reconciles the exact replacement chain after lost reply without cancel or navigation retransmission");

// Explicit automatic mode: the same planner and one-shot motion gates are
// driven without operator clicks, but only after continuous block evidence.
var autoPath=Path.Combine(folder,"navigation-auto.json");robot.IntentPath=autoPath;robot.Reset();planner.Available=true;
var autoSettings=new SingleNavigationTestOptions{Enabled=true,VehicleId="SIM",BusinessExecutionDisabled=true,
    DetourWaitSeconds=1,AutoDetourEnabled=true,AutoDetourWaitSeconds=1};
SingleNavigationTestService AutoCreate()=>new(autoSettings,options,connections,new Tasks(),new Gate(),autoPath,
    detourPlanner:planner,roadClosures:roads);
var auto=AutoCreate();
normal=await auto.PreviewAsync(user,new("SIM","A","C"),default);sent=await auto.SubmitAsync(user,new(normal.PreviewId,true),default);
var cancelsBefore=robot.Cancels;var detoursBefore=robot.Detours;
robot.X=.5;robot.Station="A";robot.Blocked=true;await auto.ReadAsync(default);await Task.Delay(1100);
await auto.ReadAsync(default);await auto.AdvanceAutoDetourAsync(default);
var autoStatus=await auto.ReadAsync(default);
Check(robot.Cancels==cancelsBefore&&robot.Detours==detoursBefore&&autoStatus.Current?.AutoDetour?.State=="Unavailable",
    "Automatic detour refuses a mid-edge pose without borrowing the old station");
robot.X=1;robot.Station="B";planner.Available=false;await auto.ReadAsync(default);await auto.AdvanceAutoDetourAsync(default);
autoStatus=await auto.ReadAsync(default);
Check(robot.Cancels==cancelsBefore&&autoStatus.Current?.AutoDetour?.State=="Unavailable",
    "Automatic detour keeps the original task when no alternate route exists");
planner.Available=true;await auto.ReadAsync(default);await auto.AdvanceAutoDetourAsync(default);
Check(robot.Cancels==cancelsBefore+1&&robot.CancelId==sent.VendorTaskId&&robot.Detours==detoursBefore,
    "Persistent physical block automatically creates a verified candidate and cancels only the exact original vendor task");
autoStatus=await auto.ReadAsync(default);
Check(autoStatus.Current?.AutoDetour?.CandidateRoute.SequenceEqual(new[]{"B","D","C"})==true,
    "Automatic audit exposes the alternate station chain before replacement execution");
await auto.ReadAsync(default);await Task.Delay(650);await auto.ReadAsync(default);await auto.AdvanceAutoDetourAsync(default);
Check(robot.Detours==detoursBefore+1&&robot.Segments.Length==2,
    "Terminal original task plus two stopped observations automatically sends the replacement chain once");
autoStatus=await auto.ReadAsync(default);
Check(autoStatus.Current?.AutoDetour?.State=="Executing",
    "Automatic execution remains auditable and is not reported complete on send acknowledgement");
Console.WriteLine($"PASS {checks} checks; loopback only, no real vehicle");

sealed class Planner:INavigationTestDetourPlanner
{
    public long Revision=3;public bool Available=true;public NavigationTestDetourPlanRequest? LastRequest;
    public Task<NavigationTestDetourPlan> PreviewAsync(NavigationTestDetourPlanRequest r,CancellationToken ct)
    {LastRequest=r;return Task.FromResult(new NavigationTestDetourPlan(Available,Available?"candidate":"no route",Revision,"map","version","hash",r.RobotMap,r.RobotMapMd5,r.SourceSite,r.TargetSite,[r.SourceSite,"D",r.TargetSite]){BlockedEdgeIds=["B-C"]});}
    public Task ValidateAsync(NavigationTestDetourPlan p,CancellationToken ct)
    {if(p.ClosureRevision!=Revision)throw new InvalidOperationException("stale closure");return Task.CompletedTask;}
    public async ValueTask<IDisposable> EnterSendAsync(NavigationTestDetourPlan p,CancellationToken ct){await ValidateAsync(p,ct);return new Lease();}
    sealed class Lease:IDisposable{public void Dispose(){}}
}
sealed class Tasks:IActiveTaskLookup
{public Task<IReadOnlyList<ActiveDispatchTask>> GetActiveTasksAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ActiveDispatchTask>>([]);}
sealed class Closures:IRoadClosureService
{
    public bool Active,CloseOnEnter,Held;
    public RoadClosureSnapshot GetSnapshot()=>new(true,1,"map","version","hash",Active?[new("closure","request","map","version","hash","B-C","B","C",false,[],"Operator","test","test",DateTimeOffset.UtcNow,1,true,null,null,null,null)]:[]);
    public Task<IDisposable> EnterRouteSendAsync(IReadOnlyList<string> path,CancellationToken ct){if(CloseOnEnter)Active=true;Held=true;return Task.FromResult<IDisposable>(new Lease(this));}
    public Task InitializeAsync(CancellationToken ct)=>Task.CompletedTask;
    public Task<RoadClosureSnapshot> ListAsync(CancellationToken ct)=>Task.FromResult(GetSnapshot());
    public Task<RoadClosureResult> CreateAsync(TrafficUserContext user,RoadClosureCreateRequest request,CancellationToken ct)=>throw new NotSupportedException();
    public Task<RoadClosureResult> ReleaseAsync(TrafficUserContext user,RoadClosureReleaseRequest request,CancellationToken ct)=>throw new NotSupportedException();
    public Task<RoadClosurePreview> PreviewAsync(RoadClosurePreviewRequest request,CancellationToken ct)=>throw new NotSupportedException();
    sealed class Lease(Closures owner):IDisposable{public void Dispose(){owner.Held=false;}}
}
sealed class Gate:IMapExecutionGate
{
    public MapExecutionMode Mode=>MapExecutionMode.Running;public bool CanStartNewWork=>true;
    public ValueTask<IDisposable?> EnterAsync(MapExecutionOperation operation,CancellationToken ct)=>ValueTask.FromResult<IDisposable?>(new Lease());
    public ValueTask<IDisposable> EnterControlAsync(CancellationToken ct)=>ValueTask.FromResult<IDisposable>(new Lease());public void SetMode(MapExecutionMode mode){}
    sealed class Lease:IDisposable{public void Dispose(){}}
}
sealed class Robot:IAsyncDisposable
{
    readonly TcpListener status=new(IPAddress.Loopback,0),navigation=new(IPAddress.Loopback,0);
    readonly CancellationTokenSource stop=new();readonly ConcurrentBag<Task> workers=[];
    readonly ConcurrentDictionary<string,int> tasks=new();
    public int StatusPort=>((IPEndPoint)status.LocalEndpoint).Port;public int NavigationPort=>((IPEndPoint)navigation.LocalEndpoint).Port;
    public string IntentPath="",Station="A",OriginalId="",CancelId="";public double X;
    public bool Blocked,UnknownBlocked,MapChanged,Foreign,Emergency,Fatal,DropCancelReply,DropDetourReply,UnknownOriginal,Indirect,NewTerminal,PartialNewHistory,OriginalReappears;
    public bool CancelDurable,DetourDurable,InitialSendHeld;public Func<bool>? ClosureGateHeld;public int DirectNavigations,Detours,Cancels,Unexpected;public JsonElement[] Segments=[];
    public Robot(){status.Start();navigation.Start();workers.Add(Accept(status,false));workers.Add(Accept(navigation,true));}
    public void Reset(){tasks.Clear();OriginalId="";X=0;Station="A";Blocked=false;NewTerminal=false;}
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
                var size=BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));var api=BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8));
                var bytes=new byte[size];await stream.ReadExactlyAsync(bytes,stop.Token);
                using var document=JsonDocument.Parse(size==0?"{}":System.Text.Encoding.UTF8.GetString(bytes));var request=document.RootElement;
                object response;
                switch(api)
                {
                    case 1300:response=new{current_map="fixture",current_map_md5=new string(MapChanged?'b':'a',32)};break;
                    case 1022:response=new{loadmap_status=1};break;
                    case 1060:response=new{locked=false};break;
                    case 1301:response=new{stations=new[]{new{id="A",x=0d,y=0d},new{id="B",x=1d,y=0d},new{id="C",x=2d,y=0d},new{id="D",x=3d,y=0d}}};break;
                    case 1303:
                        var from=request.GetProperty("source_id").GetString()!;var to=request.GetProperty("target_id").GetString()!;
                        response=new{list=from=="A"&&to=="C"?new[]{"A","B","C"}:Indirect?new[]{from,"A",to}:new[]{from,to}};break;
                    case 1100:response=new{x=X,y=0d,confidence=0.9,current_station=Station,emergency=Emergency,blocked=UnknownBlocked?(bool?)null:Blocked,charging=false,vx=0d,vy=0d,w=0d,
                        fatals=Fatal?new[]{new{code=52200,description="fatal fixture"}}:[]};break;
                    case 1110:
                        var ids=request.TryGetProperty("task_ids",out var filter)?filter.EnumerateArray().Select(x=>x.GetString()!).ToArray():tasks.Keys.ToArray();
                        var rows=ids.Select(id=>new{task_id=id,status=id==OriginalId?(UnknownOriginal?404:OriginalReappears?2:tasks.GetValueOrDefault(id,404)):
                            PartialNewHistory&&id.EndsWith("-01",StringComparison.Ordinal)?404:tasks.TryGetValue(id,out var value)?NewTerminal?4:value:404}).ToList();
                        if(Foreign&&!request.TryGetProperty("task_ids",out _))rows.Add(new{task_id="foreign",status=2});
                        response=new{task_status_package=new{task_status_list=rows}};break;
                    case 3051:
                        if(!motion)throw new InvalidDataException("wrong port");OriginalId=request.GetProperty("task_id").GetString()!;tasks[OriginalId]=2;
                        InitialSendHeld=ClosureGateHeld?.Invoke()==true;Interlocked.Increment(ref DirectNavigations);response=new{ret_code=0};break;
                    case 3003:
                        if(!motion)throw new InvalidDataException("wrong port");CancelId=request.GetProperty("taskId").GetString()!;
                        using(var saved=JsonDocument.Parse(File.ReadAllText(IntentPath)))CancelDurable=saved.RootElement.GetProperty("Detour").GetProperty("CancelTargetId").GetString()==CancelId;
                        tasks[CancelId]=6;Interlocked.Increment(ref Cancels);if(DropCancelReply)return;response=new{ret_code=0};break;
                    case 3066:
                        if(!motion)throw new InvalidDataException("wrong port");Segments=request.GetProperty("move_task_list").EnumerateArray().Select(x=>x.Clone()).ToArray();
                        using(var saved=JsonDocument.Parse(File.ReadAllText(IntentPath)))DetourDurable=saved.RootElement.GetProperty("Detour").GetProperty("NewVendorTaskIds").GetArrayLength()==Segments.Length;
                        foreach(var segment in Segments)tasks[segment.GetProperty("task_id").GetString()!]=2;
                        Interlocked.Increment(ref Detours);if(DropDetourReply)return;response=new{ret_code=0};break;
                    default:Interlocked.Increment(ref Unexpected);response=new{ret_code=999};break;
                }
                var body=JsonSerializer.SerializeToUtf8Bytes(response);var reply=(byte[])header.Clone();
                BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(4),body.Length);BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8),(ushort)(api+10000));
                await stream.WriteAsync(reply,stop.Token);await stream.WriteAsync(body,stop.Token);
            }
        }
        catch(Exception ex)when(ex is OperationCanceledException or IOException or ObjectDisposedException){}
    }
    public async ValueTask DisposeAsync(){stop.Cancel();status.Stop();navigation.Stop();await Task.WhenAll(workers.ToArray());stop.Dispose();}
}
