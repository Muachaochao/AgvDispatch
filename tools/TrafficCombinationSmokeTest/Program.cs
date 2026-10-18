using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.WebHost;

// Only fixture-owned processes and a new temporary database may be changed by this harness.
var workspace=Path.GetFullPath(args.FirstOrDefault()??".");
var candidateIndex=Array.IndexOf(args,"--candidate-dir");
var candidate=Path.GetFullPath(args.ElementAtOrDefault(candidateIndex+1)??throw new ArgumentException("--candidate-dir required"));
if(candidateIndex<0||!File.Exists(Path.Combine(candidate,"release-manifest.json")))throw new ArgumentException("A complete local candidate is required.");
var root=Path.Combine(Path.GetTempPath(),"agv-traffic-combination-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);Console.WriteLine("Evidence: "+root);
var dbPath=Path.Combine(root,"private.db");var pipe="AgvCombination-"+Guid.NewGuid().ToString("N");var checks=new List<string>();
void Check(bool value,string name){if(!value)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS "+name);}
MapDefinition Definition(string name,double offset)=>new(name,"isolated synthetic fixture","TestCartesian","m",[new("P",offset,0,MapNodeType.Station,true,[],"LM1"),new("Q",offset+10,0,MapNodeType.Station,true,[],"LM2")],[new("ROAD","P","Q",10,1,true,true,[])],[new("R1",TrafficResourceType.NarrowAisle,1,[new(offset+1,-1),new(offset+3,-1),new(offset+3,1),new(offset+1,1)]),new("R2",TrafficResourceType.NarrowAisle,1,[new(offset+6,-1),new(offset+8,-1),new(offset+8,1),new(offset+6,1)])]);
var first=Definition("组合地图一",0);var second=Definition("组合地图二",20);
using(var db=DatabaseFactory.Create(new(){DatabasePath=dbPath})){
 await new DatabaseInitializer(db,new TrafficResourceCatalog(first.Resources)).InitializeAsync(default);
 foreach(var (id,version,definition,active) in new[]{("COMBO-M1","COMBO-V1",first,true),("COMBO-M2","COMBO-V2",second,false)}){
  db.Insertable(new MapProjectEntity{MapId=id,Name=definition.Name,CoordinateSystem=definition.CoordinateSystem,Unit="m",CoordinateScale=1,IsActive=active,ActiveVersionId=active?version:null,PublishedVersionId=version,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommand();
  db.Insertable(new MapVersionEntity{MapId=id,VersionId=version,VersionNumber=1,Status=(int)MapVersionStatus.Published,DefinitionJson=JsonSerializer.Serialize(definition,WebJson.Options),ValidationJson=JsonSerializer.Serialize(new MapValidationService().Validate(definition),WebJson.Options),CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now,CreatedBy="isolated-combination"}).ExecuteCommand();
 }
 var manager=new TrafficResourceManager(db,new TrafficResourceCatalog(first.Resources),new(TimeSpan.FromMinutes(5)));
 foreach(var id in new[]{"A","B","C","D"})TaskRow(db,id);
 await Request(manager,"A",["R1"],"COMBO-M1","COMBO-V1");await Request(manager,"B",["R2"],"COMBO-M1","COMBO-V1");
 await Request(manager,"A",["R2"],"COMBO-M1","COMBO-V1");await Request(manager,"B",["R1"],"COMBO-M1","COMBO-V1");await Request(manager,"C",["R1"],"COMBO-M1","COMBO-V1");await Request(manager,"D",["R1"],"COMBO-M1","COMBO-V1");
}
var deploy=Path.Combine(root,"service");Directory.CreateDirectory(deploy);
foreach(var file in Directory.EnumerateFiles(Path.Combine(candidate,"Service"),"*",SearchOption.AllDirectories)){
 var relative=Path.GetRelativePath(Path.Combine(candidate,"Service"),file);if(relative.Split(Path.DirectorySeparatorChar).Any(x=>x is "config" or "data" or "logs")||Path.GetFileName(file).StartsWith("appsettings",StringComparison.OrdinalIgnoreCase))continue;var target=Path.Combine(deploy,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);
}
Directory.CreateDirectory(Path.Combine(deploy,"config"));File.WriteAllText(Path.Combine(deploy,"config/plc-signals.csv"),"address,variableName,chineseName,stationId,deviceType,instanceIndex,dataType,direction,access,addressStatus,enabled,setConditionZh,resetConditionZh,sourceNote\r\n");
File.WriteAllText(Path.Combine(deploy,"appsettings.json"),JsonSerializer.Serialize(new{Database=new{Path=dbPath},Security=new{PipeName=pipe},Plc=new{Enabled=false,AllowWrites=false,SignalCsvPath="config/plc-signals.csv"},Agv=new{Enabled=false,AllowCommands=false,Robots=Array.Empty<object>()},Map=new{Enabled=false},TaskCreation=new{Enabled=false},Dispatching=new{Enabled=false},AgvCommandOutbox=new{Enabled=false},DispatchTaskSteps=new{Enabled=false},ExecutionIdentityAcquisition=new{Enabled=false}}));
using var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();var port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
var owned=new List<OwnedProcess>();OwnedProcess? service=null;OwnedProcess? web=null;OwnedProcess? browser=null;var serviceNumber=0;
OwnedProcess StartService(){var p=Start(Path.Combine(deploy,"AgvDispatch.Service.exe"),deploy,[],"service-"+(++serviceNumber));service=p;return p;}
OwnedProcess Start(string exe,string cwd,string[] arguments,string tag,Dictionary<string,string>? env=null){var info=new ProcessStartInfo(exe){WorkingDirectory=cwd,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};info.Environment["DOTNET_ENVIRONMENT"]="Production";foreach(var a in arguments)info.ArgumentList.Add(a);if(env!=null)foreach(var e in env)info.Environment[e.Key]=e.Value;var p=new OwnedProcess(Process.Start(info)!,tag,root);owned.Add(p);return p;}
using var http=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{port}/"),Timeout=TimeSpan.FromSeconds(30)};
async Task<JsonElement> Snapshot(){using var r=await http.GetAsync("api/snapshot");r.EnsureSuccessStatusCode();return await r.Content.ReadFromJsonAsync<JsonElement>();}
async Task Ready(){var limit=DateTimeOffset.UtcNow.AddSeconds(35);while(DateTimeOffset.UtcNow<limit){if(service?.Process.HasExited==true)throw new Exception("Private Service exited: "+await service.Output+"\n"+await service.Error);if(web?.Process.HasExited==true)throw new Exception("Private WebHost exited: "+await web.Output+"\n"+await web.Error);try{if((await Snapshot()).GetProperty("connected").GetBoolean())return;}catch(HttpRequestException){}await Task.Delay(200);}throw new Exception("Private backend readiness timeout.");}
async Task<JsonElement> Blocking(string task,string map="COMBO-M1",string version="COMBO-V1",MapDefinition? def=null){var projected=SnapshotReader.ProjectMap(new(DateTimeOffset.UtcNow,map,version,def??first,[]))!;using var r=await http.PostAsJsonAsync("api/traffic/blocking",new{taskId="COMBO-"+task,stepId="COMBO-STEP-"+task,expectedMapId=map,expectedMapVersion=version,expectedMapDefinitionHash=projected.DefinitionHash});r.EnsureSuccessStatusCode();return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail");}
string LeaseIdentity(){using var db=DatabaseFactory.Create(new(){DatabasePath=dbPath});return JsonSerializer.Serialize(db.Queryable<TrafficResourceLeaseEntity>().OrderBy(l=>l.LeaseId).ToList(),WebJson.Options);}
var beforeDisconnect="";
try{
 StartService();web=Start(Path.Combine(candidate,"Web/host/AgvDispatch.WebHost.exe"),Path.Combine(candidate,"Web"),["--Web:Port="+port,"--Web:PipeName="+pipe,"--Web:StaticPath="+Path.Combine(candidate,"Web/wwwroot"),"--Web:ProjectStorePath="+Path.Combine(root,"projects"),"--Web:EnableTaskSubmission=false","--Web:EnableRecoveryActions=false"],"web");await Ready();
 var initial=await Blocking("C");Check(initial.GetProperty("cycleObserved").GetBoolean()&&initial.GetProperty("nodes").GetArrayLength()==3,"四任务争用中C展开到A/B循环，不按所有车辆两两推测");
 Check((await Snapshot()).GetProperty("map").GetProperty("id").GetString()=="COMBO-M1","初始私有后台使用原图一");
 browser=Start("node",workspace,["AgvDispatch.Web/scripts/traffic-combination-browser-check.mjs"],"browser",new(){{"EDITOR_TEST_URL",$"http://127.0.0.1:{port}/"},{"TRAFFIC_COMBINATION_FIXTURE",root}});
 var deadline=DateTimeOffset.UtcNow.AddMinutes(6);var handled=new HashSet<string>();
 while(!browser.Process.HasExited&&DateTimeOffset.UtcNow<deadline){
  foreach(var file in Directory.EnumerateFiles(root,"command-*.json")){
   if(handled.Contains(file))continue;using var doc=JsonDocument.Parse(File.ReadAllText(file));var action=doc.RootElement.GetProperty("action").GetString();handled.Add(file);string? error=null;
   try{switch(action){
    case "advance":{
     using var db=DatabaseFactory.Create(new(){DatabasePath=dbPath});Complete(db,"A");var manager=new TrafficResourceManager(db,new TrafficResourceCatalog(first.Resources),new(TimeSpan.FromMinutes(5)));await manager.ReleaseTaskAsync("COMBO-A",default);var b=await manager.TryAcquireAsync("COMBO-STEP-B","COMBO-B","CAR-B",["R1"],default);Check(b.Acquired,"A正常模拟收尾后B取得R1且保留R2");
     await Task.WhenAll(new[]{"C","D"}.Select(id=>Task.Run(async()=>{using var retryDb=DatabaseFactory.Create(new(){DatabasePath=dbPath});var retry=new TrafficResourceManager(retryDb,new TrafficResourceCatalog(first.Resources),new(TimeSpan.FromMinutes(5)));for(var i=0;i<5;i++){var r=await retry.TryAcquireAsync("COMBO-STEP-"+id,"COMBO-"+id,"CAR-"+id,["R1"],default);if(r.Acquired)throw new Exception("Conflicting waiter acquired occupied R1");}})));Check(db.Queryable<TrafficResourceLeaseEntity>().Count()==2&&db.Queryable<TrafficResourceLeaseEntity>().ToList().All(l=>l.TaskId=="COMBO-B"),"两等待车辆独立连接连续并发重试不抢占B的两个资源");
     var d=await Blocking("C");Check(!d.GetProperty("cycleObserved").GetBoolean()&&d.GetProperty("links")[0].GetProperty("toTaskId").GetString()=="COMBO-B","新观察阻塞方从A变B，旧循环解除");break;}
    case "fault":{
     using var db=DatabaseFactory.Create(new(){DatabasePath=dbPath});var expired=DateTime.Now.AddDays(-1);db.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)DispatchTaskStage.CancellationPending).Where(t=>t.TaskId=="COMBO-B").ExecuteCommand();db.Updateable<TrafficResourceLeaseEntity>().SetColumns(l=>l.ExpiresAt==expired).Where(l=>l.TaskId=="COMBO-B").ExecuteCommand();var d=await Blocking("C");Check(d.GetProperty("links").EnumerateArray().All(l=>l.GetProperty("heartbeatExpired").GetBoolean())&&d.GetProperty("nodes").EnumerateArray().Single(n=>n.GetProperty("taskId").GetString()=="COMBO-B").GetProperty("releaseRequirements").EnumerateArray().Any(r=>r.GetProperty("state").GetString()=="RequiresReview"),"取消待确认加过期心跳仍保留阻塞并要求核对");
     using var begin=await http.PostAsJsonAsync("api/maps/switch/begin",new BeginMapSwitchRequest("COMBO-M2","COMBO-V2"),WebJson.Options);Check((await begin.Content.ReadFromJsonAsync<MapSwitchResult>(WebJson.Options))?.Accepted==true,"可进入停止新增派单的地图收尾阶段");using var activate=await http.PostAsJsonAsync("api/maps/activate",new ActivateMapRequest("COMBO-M2","COMBO-V2","ACTIVATE 组合地图二",true,true,"isolated rejection test"),WebJson.Options);var result=await activate.Content.ReadFromJsonAsync<MapOperationResult>(WebJson.Options);Check(result?.Accepted==false&&result.Message.Contains("禁止切换")&&db.Queryable<TrafficResourceLeaseEntity>().Count()==2,"旧任务或异常占用存在时原门禁拒绝激活新地图，未解锁");beforeDisconnect=LeaseIdentity();break;}
    case "disconnect":await service!.Stop();Check(LeaseIdentity()==beforeDisconnect,"仅停止本测试Service，不修改持久化异常占用");break;
    case "reconnect":StartService();await Ready();Check(LeaseIdentity()==beforeDisconnect,"同库Service重启后租约ID/归属/时间/地图全字段不变");var resumed=await Blocking("C");Check(resumed.GetProperty("nodes").EnumerateArray().Any(n=>n.GetProperty("taskId").GetString()=="COMBO-B"&&n.GetProperty("taskStage").GetString()=="CancellationPending"),"重连重新读取取消待确认占用方，不补造成正常释放");break;
    case "switch-map":{
     using(var db=DatabaseFactory.Create(new(){DatabasePath=dbPath})){var manager=new TrafficResourceManager(db,new TrafficResourceCatalog(first.Resources),new(TimeSpan.FromMinutes(5)));foreach(var id in new[]{"B","C","D"}){Complete(db,id);await manager.ReleaseTaskAsync("COMBO-"+id,default);}Check(db.Queryable<TrafficResourceLeaseEntity>().Count()==0,"明确模拟业务收尾后调用原正常释放，未以断连或超时解锁");}
     using(var incomplete=await http.PostAsJsonAsync("api/maps/activate",new ActivateMapRequest("COMBO-M2","COMBO-V2","ACTIVATE 组合地图二",true,true,"isolated missing draft check"),WebJson.Options)){
      var rejected=await incomplete.Content.ReadFromJsonAsync<MapOperationResult>(WebJson.Options);Check(incomplete.IsSuccessStatusCode&&rejected?.Accepted==false&&rejected.Message.Contains("工作区不完整"),"缺少草稿在提交前明确拒绝，不返回已激活后的503");
      using var db=DatabaseFactory.Create(new(){DatabasePath=dbPath});Check(db.Queryable<MapProjectEntity>().Where(m=>m.IsActive).First().MapId=="COMBO-M1"&&db.Queryable<MapSwitchEntity>().First().Phase==(int)MapSwitchPhase.Draining,"缺草稿拒绝后原活动指针和切换收尾阶段保持不变");
      db.Insertable(new MapVersionEntity{MapId="COMBO-M2",VersionId="COMBO-M2-DRAFT",VersionNumber=2,Status=(int)MapVersionStatus.Draft,EditToken=Guid.NewGuid().ToString("N"),DefinitionJson=JsonSerializer.Serialize(second,WebJson.Options),ValidationJson=JsonSerializer.Serialize(new MapValidationService().Validate(second),WebJson.Options),CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now,CreatedBy="isolated-combination"}).ExecuteCommand();
     }
     using(var db=DatabaseFactory.Create(new(){DatabasePath=dbPath})){db.Updateable<MapVersionEntity>().SetColumns(v=>v.DefinitionJson=="{").Where(v=>v.VersionId=="COMBO-M2-DRAFT").ExecuteCommand();}
     using(var corrupt=await http.PostAsJsonAsync("api/maps/activate",new ActivateMapRequest("COMBO-M2","COMBO-V2","ACTIVATE 组合地图二",true,true,"isolated corrupt draft check"),WebJson.Options)){
      var rejected=await corrupt.Content.ReadFromJsonAsync<MapOperationResult>(WebJson.Options);Check(corrupt.IsSuccessStatusCode&&rejected?.Accepted==false&&rejected.Message.Contains("工作区不完整"),"损坏草稿也在提交前拒绝，不产生报错后活动图变化");using var db=DatabaseFactory.Create(new(){DatabasePath=dbPath});Check(db.Queryable<MapProjectEntity>().Where(m=>m.IsActive).First().MapId=="COMBO-M1"&&db.Queryable<MapSwitchEntity>().First().Phase==(int)MapSwitchPhase.Draining,"损坏草稿拒绝不改变原图和持久化切换阶段");var validDraft=JsonSerializer.Serialize(second,WebJson.Options);db.Updateable<MapVersionEntity>().SetColumns(v=>v.DefinitionJson==validDraft).Where(v=>v.VersionId=="COMBO-M2-DRAFT").ExecuteCommand();
     }
     using var activate=await http.PostAsJsonAsync("api/maps/activate",new ActivateMapRequest("COMBO-M2","COMBO-V2","ACTIVATE 组合地图二",true,true,"isolated fixture explicit simulated completion; no physical vehicle proof"),WebJson.Options);var result=await activate.Content.ReadFromJsonAsync<MapOperationResult>(WebJson.Options);Check(result?.Accepted==true&&result.Workspace?.Project is{IsActive:true,ActiveVersionId:"COMBO-V2"}&&result.Workspace.Published?.VersionId=="COMBO-V2","全部模拟任务及占用收尾后原激活返回一致的新图工作区："+result?.Message);var status=await http.GetFromJsonAsync<MapSwitchStatus>("api/maps/switch",WebJson.Options);Check(status?.Current?.Phase==MapSwitchPhase.AwaitingResume,"激活后保持待放行，不恢复实际派发");
     var old=SnapshotReader.ProjectMap(new(DateTimeOffset.UtcNow,"COMBO-M1","COMBO-V1",first,[]))!;using var stale=await http.PostAsJsonAsync("api/traffic/blocking",new{taskId="COMBO-C",stepId="COMBO-STEP-C",expectedMapId="COMBO-M1",expectedMapVersion="COMBO-V1",expectedMapDefinitionHash=old.DefinitionHash});Check(stale.StatusCode==HttpStatusCode.Conflict,"真实切图后旧ID/版本/摘要的阻塞查询返回409");
     var historical=await Blocking("C","COMBO-M2","COMBO-V2",second);Check(historical.GetProperty("nodes")[0].GetProperty("mapId").GetString()=="COMBO-M1"&&historical.GetProperty("links").GetArrayLength()==0,"旧任务历史仍属于图一，不自动迁为新图同名资源");
     using(var db=DatabaseFactory.Create(new(){DatabasePath=dbPath})){TaskRow(db,"E");TaskRow(db,"F");var manager=new TrafficResourceManager(db,new TrafficResourceCatalog(second.Resources),new(TimeSpan.FromMinutes(5)));await Request(manager,"E",["R1"],"COMBO-M2","COMBO-V2");await Request(manager,"F",["R1"],"COMBO-M2","COMBO-V2");}var newChain=await Blocking("F","COMBO-M2","COMBO-V2",second);Check(newChain.GetProperty("links")[0].GetProperty("toTaskId").GetString()=="COMBO-E"&&newChain.GetProperty("links")[0].GetProperty("mapId").GetString()=="COMBO-M2","新图同名R1仅关联新图E/F，不复用原图占用");break;}
    default:throw new Exception("Unknown test command "+action);
   }}catch(Exception ex){error=ex.ToString();}
   var response=Path.Combine(root,"reply-"+Path.GetFileName(file)[8..]);var temp=response+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(new{action,error}));File.Move(temp,response,true);if(error!=null)throw new Exception(error);
  }
  await Task.Delay(70);
 }
 if(!browser.Process.HasExited)throw new TimeoutException("Isolated browser combination timeout");await browser.Process.WaitForExitAsync();Check(browser.Process.ExitCode==0,"真实浏览器完成争用变化/断连/重连/切图/迟到响应组合检查");
 using(var db=DatabaseFactory.Create(new(){DatabasePath=dbPath})){Check(db.Queryable<TrafficResourceLeaseEntity>().Count()==1&&db.Queryable<TrafficResourceLeaseEntity>().First().TaskId=="COMBO-E","组合只读操作后最终仅保留新图预期一条租约");Check(db.Queryable<AgvCommandOutboxEntity>().Count()==0,"全程零设备命令入队");}
}finally{
 foreach(var p in Enumerable.Reverse(owned)){await p.Stop();File.WriteAllText(Path.Combine(root,p.Tag+".log"),(await p.Output)+"\n"+(await p.Error));p.Process.Dispose();}
 File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{checks,databasePath=dbPath,pipe,port,scope="Private processes, unique pipe, loopback only; synthetic tasks/maps. Not real AGV/PLC acceptance."},new JsonSerializerOptions{WriteIndented=true}));
}
Console.WriteLine($"PASS {checks.Count} backend combination checks; evidence {root}");
static void TaskRow(SqlSugar.ISqlSugarClient db,string id){db.Insertable(new DispatchTaskEntity{TaskId="COMBO-"+id,StationId="COMBO-STATION-"+id,VehicleId="CAR-"+id,Stage=(int)DispatchTaskStage.GoingToPickup,Priority=10,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommand();db.Insertable(new DispatchTaskStepEntity{TaskId="COMBO-"+id,StepId="COMBO-STEP-"+id,Sequence=1,StepType=(int)DispatchTaskStepType.GoToPickup,Status=(int)DispatchTaskStepStatus.Pending,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommand();}
static void Complete(SqlSugar.ISqlSugarClient db,string id){db.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)DispatchTaskStage.Completed).Where(t=>t.TaskId=="COMBO-"+id).ExecuteCommand();db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>s.Status==(int)DispatchTaskStepStatus.Completed).Where(s=>s.TaskId=="COMBO-"+id).ExecuteCommand();}
static async Task Request(TrafficResourceManager m,string id,string[] resources,string map,string version){await m.RecordRouteAsync(new("COMBO-STEP-"+id,"COMBO-"+id,"CAR-"+id,"P","Q",["ROAD"],resources,true,"isolated combination fixture"){MapId=map,MapVersionId=version},default);await m.TryAcquireAsync("COMBO-STEP-"+id,"COMBO-"+id,"CAR-"+id,resources,default);}
sealed class OwnedProcess(Process process,string tag,string directory)
{
 public Process Process=process;
 public string Tag=tag;
 public Task<string> Output=Capture(process.StandardOutput,Path.Combine(directory,tag+"-stdout.log"));
 public Task<string> Error=Capture(process.StandardError,Path.Combine(directory,tag+"-stderr.log"));
 public async Task Stop(){if(!Process.HasExited){Process.Kill(true);await Process.WaitForExitAsync();}}
 static async Task<string> Capture(StreamReader source,string path){var text=new System.Text.StringBuilder();await using var log=new StreamWriter(path){AutoFlush=true};while(await source.ReadLineAsync() is{} line){text.AppendLine(line);await log.WriteLineAsync(line);}return text.ToString();}
}
