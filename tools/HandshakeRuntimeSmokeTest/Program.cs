using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;

var count=0;void Check(bool value,string name){if(!value)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
var folder=Path.Combine(Path.GetTempPath(),"agv-handshake-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
using var db=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(folder,"test.db")});
await new DatabaseInitializer(db).InitializeAsync(default);
var writer=new DispatchTaskRepository(db);var orchestrator=new DispatchOrchestrationRepository(db);var store=new TaskHandshakeStore(db);
var cache=new Signals();var vehicle=new Vehicles();var map=new ActiveMapRuntime();
map.Replace(new("MAP","V1",new MapDefinition("isolated","","LocalCartesian","m",[new("PICK",0,0,MapNodeType.Station,true,[],"LM1"),new("DUMP",10,0,MapNodeType.Station,true,[],"LM2")],[],[]),DateTimeOffset.Now));
var creation=new DispatchTaskCreationOptions{DumperStationId="DUMPER"};
var dispatch=new DispatchOrchestratorOptions{DumperWaitingSite="WAIT",DumperDumpingSite="DUMP",StationPickupSites=new(){["CNC"]="PICK",["OTHER"]="PICK"}};
var options=new HandshakeExecutionOptions{Enabled=true,PlcContractVersion=HandshakeExecutionOptions.Version,StateMaxAgeSeconds=60};
var traffic=new TrafficResourceManager(db,new([]),new(TimeSpan.FromSeconds(120)));
PersistentHandshakeExecutor Engine()=>new(store,cache,cache,orchestrator,writer,traffic,creation,dispatch,options,vehicle,map);
var engine=Engine();
void Fresh(){cache.Refresh();vehicle.Refresh();}
async Task<DispatchTaskStepWorkItem> Current(string task){return (await orchestrator.GetReadyStepsAsync(100,default)).Single(s=>s.TaskId==task);}
async Task<string> Phase(string task){var current=await Current(task);return (await store.GetAsync(task,current.StepId,default))?.Phase??"";}
async Task Tick(string task){Fresh();await engine.ProcessAsync(await Current(task),default);}
async Task SetReady(string task,DispatchTaskStepType type)
{
    var steps=await writer.GetStepsAsync(task,default);var index=steps.Single(s=>s.StepType==type).Sequence;
    await db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=50,CompletedAt=DateTime.Now,VendorTaskId="vendor-original",VehicleId="AGV"}).Where(s=>s.TaskId==task&&s.Sequence<index).ExecuteCommandAsync();
    await db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=10,StartedAt=DateTime.Now,CompletedAt=null,VehicleId="AGV"}).Where(s=>s.TaskId==task&&s.Sequence==index).ExecuteCommandAsync();
    await db.Updateable<DispatchTaskEntity>().SetColumns(t=>new DispatchTaskEntity{VehicleId="AGV",Stage=20}).Where(t=>t.TaskId==task).ExecuteCommandAsync();
}
cache.Initialize("CNC","DUMPER");
var created=await writer.TryCreateAsync("CNC","DUMPER",1,default);var task=created.Task.TaskId;
await writer.MarkAcknowledgedAsync(task,default);
options.PlcContractVersion="unknown";var versionHeld=false;try{await engine.BindBeforeTransportAsync((await Current(task)) with{VehicleId="AGV"},default);}catch(InvalidOperationException){versionHeld=true;}
Check(versionHeld&&cache.Writes==0,"契约版本未匹配时首段绑定不产生设备写入");options.PlcContractVersion=HandshakeExecutionOptions.Version;
await engine.BindBeforeTransportAsync((await Current(task)) with{VehicleId="AGV"},default);
Check((await store.GetAsync(task,(await Current(task)).StepId,default))?.Phase=="TransportBound","首段在发送前绑定原周期、地图和车辆");
await SetReady(task,DispatchTaskStepType.PickupFullBox);cache.Set("CNC","BoxPresent",false);
options.Enabled=false;var disabledHeld=false;try{await Tick(task);}catch(InvalidOperationException){disabledHeld=true;}Check(disabledHeld&&cache.Writes==0,"关闭新契约不将已绑定任务降级到旧执行器");options.Enabled=true;

await Tick(task);Check(await Phase(task)=="NotifyRemoved","箱体离开推进到待通知，未一次轮询跳完整握手");
cache.FailAfterNextWrite=true;try{await Tick(task);throw new Exception("expected write failure");}catch(IOException){}
var pending=await store.GetAsync(task,(await Current(task)).StepId,default);var writes=cache.Writes;
Check(pending?.PendingSignal=="CNC/FullBoxRemoved"&&pending.Phase=="WritePending","外部写入结果未知保留原意图和下一阶段");
engine=Engine();try{await Tick(task);throw new Exception("expected hold");}catch(InvalidOperationException){}
Check(cache.Writes==writes,"重建执行器不重发结果未知写入");
Check(!await store.MoveAsync(pending! with{Revision=pending!.Revision-1},"NotifyEmptyResponsibility","stale",null,null,null,default),"旧revision不能覆盖握手阶段");
// Test fixture resolves the pending intent explicitly; production uses permissioned recovery review.
Check(await store.MoveAsync(pending!,pending!.NextPhase!,"isolated explicit review",null,null,null,default),"原待确认阶段经明确核对可CAS续接");
for(var i=0;i<8&&(await Current(task)).StepType==DispatchTaskStepType.PickupFullBox;i++)await Tick(task);
Check((await Current(task)).StepType==DispatchTaskStepType.DeliverFullBoxToDumper,"取箱阶段跨轮询完成，原九步顺序保留");
Check(cache.Get("CNC","EmptyBoxTaskAcknowledged")&&!cache.Get("CNC","FullBoxRemoved"),"返空箱责任保持，拉走通知由调度复位");

await SetReady(task,DispatchTaskStepType.WaitForDumperReady);cache.Set("DUMPER","DumperReady",true);
for(var i=0;i<4;i++)await Tick(task);
Check(await Phase(task)=="AwaitReadyConsumed"&&!cache.Get("DUMPER","AgvAtWaitingPosition"),"Ready可提前为1；撤销等待通知后等PLC消费");
cache.Set("DUMPER","DumperReady",false);await Tick(task);await Tick(task);
Check((await Current(task)).StepType==DispatchTaskStepType.EnterDumpingPosition,"Ready消费为0不会造成持久化执行器自锁");
await engine.ValidateTransportAsync(await Current(task),default);
Check(await store.TryClaimEquipmentAsync(task,(await Current(task)).StepId,"DUMPER",default)==null,"同任务可重入原设备归属");
cache.Initialize("OTHER","DUMPER");var other=await writer.TryCreateAsync("OTHER","DUMPER",1,default);await writer.MarkAcknowledgedAsync(other.Task.TaskId,default);
Check(await store.TryClaimEquipmentAsync(other.Task.TaskId,(await Current(other.Task.TaskId)).StepId,"DUMPER",default)==task,"第二任务不能消费同一倒料机握手");

await SetReady(task,DispatchTaskStepType.DumpFullBox);cache.Set("DUMPER","DumpingCompletedMayLeave",true);await Tick(task);
Check(await Phase(task)=="AwaitDoneBaseline","上轮残留Done高电平不能完成本轮");
cache.Set("DUMPER","DumpingCompletedMayLeave",false);await Tick(task);await Tick(task);
Check(await Phase(task)=="AwaitCurrentDone"&&cache.Get("DUMPER","AgvAtDumpingPosition"),"先记录低基线再发送本轮倒料通知");
cache.Set("DUMPER","DumpingCompletedMayLeave",true);await Tick(task);await Tick(task);
Check((await Current(task)).StepType==DispatchTaskStepType.ReturnEmptyBox&&cache.Get("DUMPER","AgvAtDumpingPosition"),"完成许可不等于离站，D2保持到保守返程确认");

await SetReady(task,DispatchTaskStepType.PlaceEmptyBox);await Tick(task);cache.Set("CNC","BoxPresent",true);await Tick(task);await Tick(task);
Check((await Current(task)).StepType==DispatchTaskStepType.CompletePlcHandshake,"原放箱动作之后进入独立确认与收尾");
vehicle.CurrentStation="LM2";await Tick(task);Check(await Phase(task)=="AwaitReturnEvidence","仅返程终态不能替代原站新鲜定位");
vehicle.CurrentStation="LM1";await Tick(task);await Tick(task);
Check(await Phase(task)=="AwaitDumperClosed"&&!cache.Get("DUMPER","AgvAtDumpingPosition"),"返程到位核对后才撤销倒料位通知");
await Tick(task);Check(await Phase(task)=="AwaitDumperClosed","PLC未复位Done时不假报周期结束");
cache.Set("DUMPER","DumpingCompletedMayLeave",false);
for(var i=0;i<12;i++)
{
    var ready=(await orchestrator.GetReadyStepsAsync(100,default)).FirstOrDefault(s=>s.TaskId==task);if(ready==null)break;await Tick(task);
}
Check((await new TaskCenterReadRepository(db).GetDetailAsync(task,default))!.Task.Stage==DispatchTaskStage.Completed,"新鲜信号与返程证据闭合后才完成整趟");
Check(!await db.Queryable<TaskEquipmentClaimEntity>().Where(e=>e.TaskId==task).AnyAsync(),"任务收尾后释放本轮设备归属");
Check(cache.WrittenNames.All(s=>s is not "DumperReady" and not "DumpingCompletedMayLeave" and not "BoxPresent"),"没有向PLC只读反馈位写0");

var snapshots=TaskHandshakeSignals.Capture(cache,"OTHER","DUMPER");await store.RecordSignalsAsync(other.Task.TaskId,"",snapshots,default);
var first=await store.QueryAsync(new(other.Task.TaskId,"Signals",PageSize:5),default);var initialCount=first.TotalCount;
await store.RecordSignalsAsync(other.Task.TaskId,"",snapshots,default);
Check((await store.QueryAsync(new(other.Task.TaskId,"Signals"),default)).TotalCount==initialCount,"相同值质量不会因新轮询时间生成伪变化");
cache.Set("OTHER","BoxPresent",false);await store.RecordSignalsAsync(other.Task.TaskId,"",TaskHandshakeSignals.Capture(cache,"OTHER","DUMPER"),default);
var pinned=await store.QueryAsync(new(other.Task.TaskId,"Signals",PageSize:5,ThroughSequence:first.ThroughSequence),default);
Check(pinned.TotalCount==initialCount&&(await store.QueryAsync(new(other.Task.TaskId,"Signals"),default)).TotalCount==initialCount+1,"固定序号分页不混入后来追加事件");
var second=await store.QueryAsync(new(other.Task.TaskId,"Signals",Page:2,PageSize:5,ThroughSequence:first.ThroughSequence),default);
Check(!second.Items.Any(e=>first.Items.Any(f=>f.Sequence==e.Sequence)),"同窗口分页不重叠");
var filtered=await store.QueryAsync(new(other.Task.TaskId,"Signals",Signal:"BoxPresent"),default);
Check(filtered.Items.All(e=>e.TaskId==other.Task.TaskId&&e.Signal=="BoxPresent"),"查询同时隔离任务和信号");
var invalid=false;try{await store.QueryAsync(new(task,Kind:"unexpected"),default);}catch(ArgumentException){invalid=true;}Check(invalid,"拒绝未知事件筛选");
var immutable=false;try{await db.Updateable<TaskRuntimeEventEntity>().SetColumns(e=>e.Reason=="tamper").Where(e=>e.Sequence==first.Items[0].Sequence).ExecuteCommandAsync();}catch{immutable=true;}Check(immutable,"运行事件更新被数据库拒绝");
var queryService=new TaskCenterQueryService(new TaskCenterReadRepository(db),cache,creation,handshakeOptions:options);
var detail=(await queryService.GetDetailAsync(task,default))!;
Check(detail.Handshakes.Count==6&&detail.Handover!.ExitPhysicallyVerified,"查询返回持久化六个检查点及保守返程核对诊断");
var exitEvents=await store.QueryAsync(new(task,Kind:"ExitVerified"),default);
Check(exitEvents.Items.Count==1&&exitEvents.Items[0].Evidence.Contains("vendor-original")&&exitEvents.Items[0].Evidence.Contains("LM1"),"离站核对原子保存原返程身份与原站定位证据");

var archive=await new TaskDiagnosticArchiveService(queryService,store).ExportAsync(other.Task.TaskId,default);
var bytes=Convert.FromBase64String(archive.Base64);Check(Convert.ToHexString(SHA256.HashData(bytes))==archive.Sha256,"联调ZIP传输SHA256一致");
using(var zip=new ZipArchive(new MemoryStream(bytes)))
{
    var manifest=JsonDocument.Parse(new StreamReader(zip.GetEntry("manifest.json")!.Open()).ReadToEnd());
    foreach(var file in manifest.RootElement.GetProperty("files").EnumerateArray())
    {using var stream=zip.GetEntry(file.GetProperty("name").GetString()!)!.Open();using var data=new MemoryStream();stream.CopyTo(data);Check(Convert.ToHexString(SHA256.HashData(data.ToArray()))==file.GetProperty("sha256").GetString(),"联调包逐文件哈希："+file.GetProperty("name").GetString());}
}
File.WriteAllBytes(Path.Combine(folder,archive.FileName),bytes);
Console.WriteLine($"PASS {count} persistent handshake/runtime checks. Isolated evidence: {folder}");

sealed class Signals:IPlcSignalCache,IPlcGateway
{
    private readonly Dictionary<string,PlcSignalSnapshot> values=[];private long version;
    public bool FailAfterNextWrite;public int Writes;public List<string> WrittenNames=[];
    public event Action<PlcSignalChanged>? SignalChanged{add{}remove{}}
    public long Version=>version;
    public void Initialize(string station,string dumper){foreach(var v in TaskHandshakeSignals.Pickup)Set(station,v,v is "BoxPresent" or "CommunicationOnline");foreach(var v in TaskHandshakeSignals.Dumper)if(!values.ContainsKey(dumper+"/"+v))Set(dumper,v,false);}
    public void Set(string station,string variable,bool value)=>values[station+"/"+variable]=new(station,variable,variable,"DB:"+station+":"+variable,"BOOL","logical","configured",true,value,SignalQuality.Good,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,++version);
    public void Refresh(){foreach(var key in values.Keys.ToArray())values[key]=values[key] with{UpdatedAt=DateTimeOffset.UtcNow,Version=++version};}
    public bool Get(string station,string variable)=>values[station+"/"+variable].Value==true;
    public IReadOnlyList<PlcSignalSnapshot> GetAll()=>values.Values.ToArray();public IReadOnlyList<PlcSignalSnapshot> GetStation(string station)=>values.Values.Where(v=>v.StationId==station).ToArray();
    public bool TryGet(string station,string variable,out PlcSignalSnapshot snapshot)=>values.TryGetValue(station+"/"+variable,out snapshot!);
    public Task WriteSignalAsync(string station,string variable,bool value,CancellationToken ct){Writes++;WrittenNames.Add(variable);Set(station,variable,value);if(FailAfterNextWrite){FailAfterNextWrite=false;throw new IOException("isolated lost write response");}return Task.CompletedTask;}
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct)=>throw new NotSupportedException();public Task<bool?> ReadSignalAsync(string station,string variable,CancellationToken ct)=>Task.FromResult<bool?>(Get(station,variable));public Task<StationSignal?> ReadStationAsync(string station,CancellationToken ct)=>throw new NotSupportedException();
}
sealed class Vehicles:IAgvStateCache
{
    public string CurrentStation="LM1";private AgvRobotSnapshot snapshot=new("AGV","isolated","127.0.0.1",0,ConnectionState.Online,SignalQuality.Good,0,0,0,1,"LM1",null,100,false,false,"Idle",null,null,null,[],[],DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,0,null,null,1);
    public void Refresh()=>snapshot=snapshot with{CurrentStation=CurrentStation,LastSuccessfulPollAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow};
    public event Action<AgvRobotStateChanged>? StateChanged{add{}remove{}}public long Version=>1;public IReadOnlyList<AgvRobotSnapshot> GetAll()=>[snapshot];public bool TryGet(string id,out AgvRobotSnapshot result){result=snapshot;return id=="AGV";}
}
