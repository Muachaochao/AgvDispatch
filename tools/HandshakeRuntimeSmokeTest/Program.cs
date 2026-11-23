using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
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
var dumpResource=new TrafficResourceDefinition("DUMP-AREA",TrafficResourceType.Station,1,[new(8,-2),new(12,-2),new(12,2),new(8,2)]);
map.Replace(new("MAP","V1",new MapDefinition("isolated","","LocalCartesian","m",[new("PICK",0,0,MapNodeType.Station,true,[],"LM1"),new("WAIT",5,0,MapNodeType.WaitingPoint,true,[]),new("DUMP",10,0,MapNodeType.Station,true,[dumpResource.Id],"LM2")],[],[dumpResource]),DateTimeOffset.Now));
var catalog=new TrafficResourceCatalog([dumpResource]);
await new DatabaseInitializer(db,catalog).InitializeAsync(default);
var creation=new DispatchTaskCreationOptions{DumperStationId="DUMPER"};
var dispatch=new DispatchOrchestratorOptions{DumperWaitingSite="WAIT",DumperDumpingSite="DUMP",StationPickupSites=new(){["CNC"]="PICK",["OTHER"]="PICK"}};
var options=new HandshakeExecutionOptions{Enabled=true,PlcContractVersion=HandshakeExecutionOptions.Version,StateMaxAgeSeconds=60,
    DumperExit=new(){ResourceIds=[dumpResource.Id],BodyProfiles=[new DumperExitBodyProfile("isolated-profile","AGV",0.5,0.05,true,true)]}};
var traffic=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(120)));
PersistentHandshakeExecutor Engine()=>new(store,cache,cache,orchestrator,writer,traffic,creation,dispatch,options,vehicle,map,new UnusedGateway(),new UnusedMapPreflight());
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
Check(versionHeld&&cache.Writes==0,"契约版本未匹配时首段绑定不产生设备写入");
options.PlcContractVersion=HandshakeExecutionOptions.LegacyVersion;var legacyHeld=false;try{await engine.BindBeforeTransportAsync((await Current(task)) with{VehicleId="AGV"},default);}catch(InvalidOperationException){legacyHeld=true;}
Check(legacyHeld&&cache.Writes==0,"旧sender-owned版本不能被新28阶段执行器静默迁移");options.PlcContractVersion=HandshakeExecutionOptions.Version;
await engine.BindBeforeTransportAsync((await Current(task)) with{VehicleId="AGV"},default);
Check((await store.GetAsync(task,(await Current(task)).StepId,default))?.Phase=="TransportBound","首段在发送前绑定原周期、地图和车辆");
await SetReady(task,DispatchTaskStepType.PickupFullBox);cache.Set("CNC","BoxPresent",false);
options.Enabled=false;var disabledHeld=false;try{await Tick(task);}catch(InvalidOperationException){disabledHeld=true;}Check(disabledHeld&&cache.Writes==0,"关闭新契约不将已绑定任务降级到旧执行器");options.Enabled=true;

await Tick(task);Check(await Phase(task)=="NotifyRemoved","箱体离开推进到待通知，未一次轮询跳完整握手");
cache.FailAfterNextWrite=true;try{await Tick(task);throw new Exception("expected write failure");}catch(IOException){}
var pending=await store.GetAsync(task,(await Current(task)).StepId,default);var writes=cache.Writes;
Check(pending?.PendingSignal=="CNC/FullBoxRemoved"&&pending.Phase=="WritePending","外部写入结果未知保留原意图和下一阶段");
engine=Engine();await Tick(task);
Check(cache.Writes==writes&&await Phase(task)=="NotifyEmptyResponsibility","重建执行器后回读已等于目标值，直接推进且不重发结果未知写入");
Check(!await store.MoveAsync(pending! with{Revision=pending!.Revision-1},"NotifyEmptyResponsibility","stale",null,null,null,default),"旧revision不能覆盖握手阶段");
// Bounded rewrite: every write from here on is dropped by the PLC link. The first two cycles only observe,
// three rewrites follow at the readback interval, and the fourth due cycle escalates instead of looping.
var beforeDrops=cache.Writes;cache.DropWrites=4;
try{await Tick(task);throw new Exception("expected the dropped write to surface");}catch(IOException){}
await Tick(task);
for(var rewrite=0;rewrite<3;rewrite++){await Task.Delay(2100);await Tick(task);}
Check(cache.Writes==beforeDrops+4&&await Phase(task)=="WritePending","未回读确认的写入最多重写三次并保留写入意图");
await Task.Delay(2100);
try{await Tick(task);throw new Exception("expected escalation after bounded rewrites");}catch(InvalidOperationException ex)when(ex.Message.Contains("已重写3次")){}
Check(cache.Writes==beforeDrops+4,"重写用尽后停止自动发送并保留握手阶段");
// Restarting the executor resets the in-memory counter; the PLC link is healthy again, so readback completes it.
cache.DropWrites=0;engine=Engine();await Tick(task);await Task.Delay(2100);await Tick(task);await Tick(task);
Check(cache.Writes==beforeDrops+5&&await Phase(task)=="AwaitPickupReadback","恢复后一次重写经回读确认并推进原阶段");
for(var i=0;i<8&&(await Current(task)).StepType==DispatchTaskStepType.PickupFullBox;i++)await Tick(task);
Check((await Current(task)).StepType==DispatchTaskStepType.DeliverFullBoxToDumper,"取箱阶段跨轮询完成，原九步顺序保留");
Check(cache.Get("CNC","EmptyBoxTaskAcknowledged")&&cache.Get("CNC","FullBoxRemoved"),"28阶段返空箱责任与拉走通知保持到返箱收尾");

await SetReady(task,DispatchTaskStepType.WaitForDumperReady);vehicle.CurrentStation="WAIT";cache.Set("DUMPER","DumperReady",true);
await Tick(task);Check(await Phase(task)=="AwaitReadyBaseline"&&!cache.Get("DUMPER","AgvAtWaitingPosition"),"残留Ready高电平不能当28阶段新周期许可");
cache.Set("DUMPER","DumperReady",false);await Tick(task);await Tick(task);cache.Set("DUMPER","DumperReady",true);await Tick(task);await Tick(task);
Check(await Phase(task)=="AwaitReadyConsumed"&&!cache.Get("DUMPER","AgvAtWaitingPosition"),"建立低基线后消费本轮Ready并撤销等待通知");
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
Check((await Current(task)).StepType==DispatchTaskStepType.ReturnEmptyBox&&cache.Get("DUMPER","AgvAtDumpingPosition"),"完成许可不等于离站，D2保持到实际出区证据");

await SetReady(task,DispatchTaskStepType.PlaceEmptyBox);await Tick(task);cache.Set("CNC","BoxPresent",true);await Tick(task);await Tick(task);
Check((await Current(task)).StepType==DispatchTaskStepType.PlaceEmptyBox&&!cache.Get("CNC","EmptyBoxPlaced"),"缺少实际出区交接时不能越过放箱确认或写放到位通知");
// Position the fixture at final closure solely to test its independent negative gates.
// No actual exit proof is provided here; the end-to-end v2 suite covers successful handoff.
await SetReady(task,DispatchTaskStepType.CompletePlcHandshake);
vehicle.CurrentStation="LM2";await Tick(task);Check(await Phase(task)=="AwaitReturnEvidence","仅返程终态不能替代原站新鲜定位");
vehicle.CurrentStation="LM1";await Tick(task);await Tick(task);
Check(await Phase(task)=="AwaitReturnEvidence"&&cache.Get("DUMPER","AgvAtDumpingPosition"),"回原工位不能替代28阶段实际出区交接证据，禁止补造清位");
await Tick(task);Check(await Phase(task)=="AwaitReturnEvidence","缺少实际出区交接时保持原周期与返箱责任");
cache.Set("DUMPER","DumpingCompletedMayLeave",false);
for(var i=0;i<4;i++)await Tick(task);
Check((await new TaskCenterReadRepository(db).GetDetailAsync(task,default))!.Task.Stage!=DispatchTaskStage.Completed&&await Phase(task)=="AwaitReturnEvidence","即便Done回低也不能跳过实际出区证据完成整趟");
Check(await db.Queryable<TaskEquipmentClaimEntity>().Where(e=>e.TaskId==task).AnyAsync()&&cache.Get("CNC","FullBoxRemoved")&&cache.Get("CNC","EmptyBoxTaskAcknowledged"),"出区交接未证实时设备、取箱通知和返箱责任均保留");
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
Check(detail.Handshakes.Count==5&&!detail.Handover!.ExitPhysicallyVerified,"放箱未获交接许可不创建检查点，查询不会把返程到位冒充实际出区");
var exitEvents=await store.QueryAsync(new(task,Kind:"ExitVerified"),default);
Check(exitEvents.Items.Count==0,"未运行真实出区取证流程时不伪造ExitVerified事件");

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
    public void Initialize(string station,string dumper){foreach(var v in TaskHandshakeSignals.Pickup)Set(station,v,v is "BoxPresent" or "CommunicationOnline" or "FullBoxRequestAcknowledged");foreach(var v in TaskHandshakeSignals.Dumper)if(!values.ContainsKey(dumper+"/"+v))Set(dumper,v,false);}
    public void Set(string station,string variable,bool value)=>values[station+"/"+variable]=new(station,variable,variable,"DB:"+station+":"+variable,"BOOL","logical","configured",true,value,SignalQuality.Good,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,++version);
    public void Refresh(){foreach(var key in values.Keys.ToArray())values[key]=values[key] with{UpdatedAt=DateTimeOffset.UtcNow,Version=++version};}
    public bool Get(string station,string variable)=>values[station+"/"+variable].Value==true;
    public IReadOnlyList<PlcSignalSnapshot> GetAll()=>values.Values.ToArray();public IReadOnlyList<PlcSignalSnapshot> GetStation(string station)=>values.Values.Where(v=>v.StationId==station).ToArray();
    public bool TryGet(string station,string variable,out PlcSignalSnapshot snapshot)=>values.TryGetValue(station+"/"+variable,out snapshot!);
    public int DropWrites;
    public Task WriteSignalAsync(string station,string variable,bool value,CancellationToken ct){Writes++;WrittenNames.Add(variable);if(DropWrites>0){DropWrites--;throw new IOException("isolated dropped PLC write");}Set(station,variable,value);if(FailAfterNextWrite){FailAfterNextWrite=false;throw new IOException("isolated lost write response");}return Task.CompletedTask;}
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct)=>throw new NotSupportedException();public Task<bool?> ReadSignalAsync(string station,string variable,CancellationToken ct)=>Task.FromResult<bool?>(Get(station,variable));public Task<StationSignal?> ReadStationAsync(string station,CancellationToken ct)=>throw new NotSupportedException();
}
sealed class Vehicles:IAgvStateCache
{
    public string CurrentStation="LM1";private AgvRobotSnapshot snapshot=new("AGV","isolated","127.0.0.1",0,ConnectionState.Online,SignalQuality.Good,0,0,0,1,"LM1",null,100,false,false,"Idle",null,null,null,[],[],DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,0,null,null,1);
    public void Refresh()=>snapshot=snapshot with{CurrentStation=CurrentStation,LastSuccessfulPollAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow};
    public event Action<AgvRobotStateChanged>? StateChanged{add{}remove{}}public long Version=>1;public IReadOnlyList<AgvRobotSnapshot> GetAll()=>[snapshot];public bool TryGet(string id,out AgvRobotSnapshot result){result=snapshot;return id=="AGV";}
}

// This suite covers persisted write/replay and diagnostic regressions. Actual
// geometric exit, resource handoff and multi-car flow are tested by the v2 fixture.
sealed class UnusedGateway:IAgvGateway
{
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct)=>throw new NotSupportedException();
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct)=>throw new NotSupportedException();
    public Task<string> CreateTransportTaskAsync(string id,string? vehicle,IReadOnlyList<string> path,AgvTransportAction? action,CancellationToken ct)=>throw new Exception("Regression fixture must not navigate");
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id,CancellationToken ct)=>throw new Exception("This fixture cannot provide exit evidence");
    public Task CancelTaskAsync(string id,CancellationToken ct)=>throw new NotSupportedException();
}
sealed class UnusedMapPreflight:IRobokitTaskMapPreflight
{
    public Task<RobokitTaskMapCheck> CheckAsync(string vehicleId,string currentSite,string pickup,string waiting,string dumping,string mapId,string versionId,CancellationToken ct)=>Task.FromResult(new RobokitTaskMapCheck(false,"No actual route evidence in this fixture"));
}
