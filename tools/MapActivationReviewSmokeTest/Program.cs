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
var scenario=args.ElementAtOrDefault(Array.IndexOf(args,"--scenario")+1);if(scenario is not ("committed-crash" or "request-lost"))throw new ArgumentException("--scenario committed-crash|request-lost required");
var root=Path.Combine(Path.GetTempPath(),"agv-activation-review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);Console.WriteLine("Evidence: "+root);
var dbPath=Path.Combine(root,"private.db");var pipe="AgvActivationReview-"+Guid.NewGuid().ToString("N");var checks=new List<string>();
void Check(bool value,string name){if(!value)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS "+name);}
MapDefinition Definition(string name,double offset)=>new(name,"isolated synthetic fixture","TestCartesian","m",[new("P",offset,0,MapNodeType.Station,true,[],"LM1"),new("Q",offset+10,0,MapNodeType.Station,true,[],"LM2")],[new("ROAD","P","Q",10,1,true,true,[])],[new("R1",TrafficResourceType.NarrowAisle,1,[new(offset+1,-1),new(offset+3,-1),new(offset+3,1),new(offset+1,1)]),new("R2",TrafficResourceType.NarrowAisle,1,[new(offset+6,-1),new(offset+8,-1),new(offset+8,1),new(offset+6,1)])]);
var first=Definition("组合地图一",0);var second=Definition("组合地图二",20);
using(var db=DatabaseFactory.Create(new(){DatabasePath=dbPath})){
 await new DatabaseInitializer(db,new TrafficResourceCatalog(first.Resources)).InitializeAsync(default);
 foreach(var (id,version,definition,active) in new[]{("COMBO-M1","COMBO-V1",first,true),("COMBO-M2","COMBO-V2",second,false)}){
  db.Insertable(new MapProjectEntity{MapId=id,Name=definition.Name,CoordinateSystem=definition.CoordinateSystem,Unit="m",CoordinateScale=1,IsActive=active,ActiveVersionId=active?version:null,PublishedVersionId=version,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommand();
  db.Insertable(new MapVersionEntity{MapId=id,VersionId=version,VersionNumber=1,Status=(int)MapVersionStatus.Published,DefinitionJson=JsonSerializer.Serialize(definition,WebJson.Options),ValidationJson=JsonSerializer.Serialize(new MapValidationService().Validate(definition),WebJson.Options),CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now,CreatedBy="isolated-combination"}).ExecuteCommand();
 }
 db.Insertable(new MapVersionEntity{MapId="COMBO-M2",VersionId="COMBO-M2-DRAFT",VersionNumber=2,Status=(int)MapVersionStatus.Draft,EditToken=Guid.NewGuid().ToString("N"),DefinitionJson=JsonSerializer.Serialize(second,WebJson.Options),ValidationJson=JsonSerializer.Serialize(new MapValidationService().Validate(second),WebJson.Options),CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now,CreatedBy="isolated-activation-review"}).ExecuteCommand();

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

string originalId="";int expectedPhase=scenario=="committed-crash"?20:10;var expectedOutcome=scenario=="committed-crash"?"CommittedAwaitingReview":"OriginalMapObserved";
async Task<MapActivationReview> Review(string? id=null,string version="COMBO-V2"){using var r=await http.PostAsJsonAsync("api/maps/activation-review",new MapActivationReviewQuery(id??originalId,"COMBO-M2",version),WebJson.Options);r.EnsureSuccessStatusCode();return (await r.Content.ReadFromJsonAsync<MapActivationReview>(WebJson.Options))!;}
string DurableState(){using var db=DatabaseFactory.Create(new(){DatabasePath=dbPath});return JsonSerializer.Serialize(new{Maps=db.Queryable<MapProjectEntity>().OrderBy(x=>x.MapId).ToList(),Switches=db.Queryable<MapSwitchEntity>().OrderBy(x=>x.ReleaseId).ToList(),Leases=db.Queryable<TrafficResourceLeaseEntity>().ToList(),Commands=db.Queryable<AgvCommandOutboxEntity>().ToList(),Audits=db.Queryable<MapOperationAuditEntity>().ToList()},WebJson.Options);}
try{
 StartService();web=Start(Path.Combine(candidate,"Web/host/AgvDispatch.WebHost.exe"),Path.Combine(candidate,"Web"),["--Web:Port="+port,"--Web:PipeName="+pipe,"--Web:StaticPath="+Path.Combine(candidate,"Web/wwwroot"),"--Web:ProjectStorePath="+Path.Combine(root,"projects"),"--Web:EnableTaskSubmission=false","--Web:EnableRecoveryActions=false"],"web");await Ready();
 browser=Start("node",workspace,["AgvDispatch.Web/scripts/map-activation-review-browser-check.mjs"],"browser",new(){{"EDITOR_TEST_URL",$"http://127.0.0.1:{port}/#/maps"},{"MAP_ACTIVATION_FIXTURE",root},{"MAP_ACTIVATION_SCENARIO",scenario}});
 var handled=new HashSet<string>();var deadline=DateTimeOffset.UtcNow.AddMinutes(4);
 while(!browser.Process.HasExited&&DateTimeOffset.UtcNow<deadline){foreach(var file in Directory.EnumerateFiles(root,"command-*.json")){
  if(handled.Contains(file))continue;using var document=JsonDocument.Parse(File.ReadAllText(file));var action=document.RootElement.GetProperty("action").GetString();handled.Add(file);string? error=null;
  try{switch(action){
   case "inspect-and-interrupt":{
    using(var db=DatabaseFactory.Create(new(){DatabasePath=dbPath})){
     var row=db.Queryable<MapSwitchEntity>().Where(x=>x.Phase<90).First();originalId=row.ReleaseId;
     Check(row.Phase==expectedPhase,"故障注入前原切换阶段确认为"+expectedPhase);
     var active=db.Queryable<MapProjectEntity>().Where(x=>x.IsActive).First();Check(active.MapId==(expectedPhase==20?"COMBO-M2":"COMBO-M1"),"数据库活动指针符合本场景实际提交结果");
     Check(db.Queryable<MapOperationAuditEntity>().Where(x=>x.Operation=="Activate"&&x.Success).Count()==(expectedPhase==20?1:0),"成功激活审计严格一次或零次，没有凭浏览器报错推测");
     for(var i=0;i<35;i++)db.Insertable(new MapSwitchEntity{ReleaseId="CLOSED-"+i,MapId="COMBO-M1",VersionId="COMBO-V1",Phase=90,StartedAt=DateTime.Now.AddMinutes(i+1),UpdatedAt=DateTime.Now.AddMinutes(i+1)}).ExecuteCommand();
    }
    var proof=await Review();Check(proof.Outcome==expectedOutcome&&proof.Record?.ReleaseId==originalId&&proof.ContentConsistent,"HTTP/Named Pipe按原编号核对数据库与运行图，历史截断不丢身份");
    var before=DurableState();await Review();await Review();Check(DurableState()==before,"重复只读复核不写切换/活动图/租约/命令/审计");
    Check((await Review(version:"OTHER")).Outcome=="IdentityMismatch"&&(await Review(id:"MISSING")).Outcome=="Unknown","错目标与缺原编号不解释为未提交");
    using var invalid=await http.PostAsJsonAsync("api/maps/activation-review",new MapActivationReviewQuery("","COMBO-M2","COMBO-V2"),WebJson.Options);Check(invalid.StatusCode==HttpStatusCode.BadRequest,"无效原编号由Web拒绝，不透传无界查询");
    using var repeat=await http.PostAsJsonAsync("api/maps/activate",new ActivateMapRequest("COMBO-M2","COMBO-V2","ACTIVATE 组合地图二",true,true,"isolated replay guard"){ExpectedReleaseId=expectedPhase==20?originalId:"OTHER"},WebJson.Options);var result=await repeat.Content.ReadFromJsonAsync<MapOperationResult>(WebJson.Options);Check(result?.Accepted==false&&DurableState()==before,expectedPhase==20?"已提交后的重复激活被拒绝，不产生第二次提交":"旧切换编号不能将未确认请求应用到原批次");
    await service!.Stop();Check(DurableState()==before,"提交后或发送前中断私有进程，持久化证据保持");break;}
   case "restart":{
    StartService();await Ready();var proof=await Review();Check(proof.Outcome==expectedOutcome&&proof.Mode==MapExecutionMode.Held&&!proof.CanStartNewWork,"重启按原持久化结果复核并保持执行门关闭");
    Check(proof.Record?.Phase==(MapSwitchPhase)expectedPhase,"重启不把Draining改成提交，不把待放行改成完成");break;}
   default:throw new Exception("Unexpected private command "+action);
  }}catch(Exception ex){error=ex.ToString();}
  var reply=Path.Combine(root,"reply-"+Path.GetFileName(file)[8..]);File.WriteAllText(reply+".tmp",JsonSerializer.Serialize(new{action,error}));File.Move(reply+".tmp",reply,true);if(error!=null)throw new Exception(error);
 }await Task.Delay(70);}
 if(!browser.Process.HasExited)throw new TimeoutException("Browser activation review timeout");Check(browser.Process.ExitCode==0,"浏览器完成回执/刷新/断连/重连与人工复核保护");
 using(var db=DatabaseFactory.Create(new(){DatabasePath=dbPath})){Check(db.Queryable<MapSwitchEntity>().Where(x=>x.ReleaseId==originalId).First().Phase==expectedPhase&&db.Queryable<AgvCommandOutboxEntity>().Count()==0&&db.Queryable<TrafficResourceLeaseEntity>().Count()==0,"最终阶段未自动推进，未派发命令或改变资源");}
 var final=await Review();Check(final.Mode==MapExecutionMode.Held&&!final.CanStartNewWork,"结束本地复核也不调用后台放行");
}finally{foreach(var p in Enumerable.Reverse(owned)){await p.Stop();File.WriteAllText(Path.Combine(root,p.Tag+".log"),(await p.Output)+"\n"+(await p.Error));p.Process.Dispose();}File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{scenario,checks,originalId,databasePath=dbPath,pipe,port,scope="Private loopback fixture; no site devices"},new JsonSerializerOptions{WriteIndented=true}));}
Console.WriteLine($"PASS {checks.Count} activation review backend checks ({scenario}); {root}");
sealed class OwnedProcess(Process process,string tag,string directory)
{
 public Process Process=process;
 public string Tag=tag;
 public Task<string> Output=Capture(process.StandardOutput,Path.Combine(directory,tag+"-stdout.log"));
 public Task<string> Error=Capture(process.StandardError,Path.Combine(directory,tag+"-stderr.log"));
 public async Task Stop(){if(!Process.HasExited){Process.Kill(true);await Process.WaitForExitAsync();}}
 static async Task<string> Capture(StreamReader source,string path){var text=new System.Text.StringBuilder();await using var log=new StreamWriter(path){AutoFlush=true};while(await source.ReadLineAsync() is{} line){text.AppendLine(line);await log.WriteLineAsync(line);}return text.ToString();}
}
