using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Service;
using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

var checks = 0;
void Check(bool value, string description)
{
    if (!value) throw new Exception(description);
    checks++;
    Console.WriteLine("PASS " + description);
}

var now = DateTimeOffset.UtcNow;
var root = Path.Combine(Path.GetTempPath(),
    "AGV-completed-route-release-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var database = DatabaseFactory.Create(new()
{
    DatabasePath = Path.Combine(root, "test.db")
});
await new DatabaseInitializer(database).InitializeAsync(default);

var resources = new[]
{
    new TrafficResourceDefinition("CORRIDOR", TrafficResourceType.NarrowAisle, 1,
        [new(-3, -1), new(-2, -1), new(-2, 1), new(-3, 1)]),
    new TrafficResourceDefinition("INTERSECTION", TrafficResourceType.Intersection, 1,
        [new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)]),
    new TrafficResourceDefinition("FINAL", TrafficResourceType.Station, 1,
        [new(9, -1), new(11, -1), new(11, 1), new(9, 1)])
};
var map = new MapDefinition("completed-route-test", "", "LocalCartesian", "m",
    [
        new("A", -5, 0, MapNodeType.Waypoint, true, []),
        new("B", 0, 0, MapNodeType.Station, true, ["INTERSECTION"]),
        new("C", 10, 0, MapNodeType.Station, true, ["FINAL"])
    ],
    [
        new("AB", "A", "B", 5, 1, false, true, ["CORRIDOR"]),
        new("BC", "B", "C", 10, 1, false, true, ["INTERSECTION"])
    ], resources);
var runtime = new ActiveMapRuntime();
runtime.Replace(new("MAP", "V1", map, now));

var payload = JsonSerializer.Serialize(new AgvTransportCommandPayload(
    "STEP", "AGV", "A", "C", ["A", "B", "C"], null),
    new JsonSerializerOptions(JsonSerializerDefaults.Web));
var request = new TrafficRequestEntity
{
    StepId = "STEP",
    TaskId = "TASK",
    VehicleId = "AGV",
    Source = "A",
    Destination = "C",
    EdgeIds = "[\"AB\",\"BC\"]",
    ResourceIds = "[\"CORRIDOR\",\"FINAL\",\"INTERSECTION\",\"AUTO-CORRIDOR-SYNTHETIC\"]",
    State = "Held",
    MapId = "MAP",
    MapVersionId = "V1"
};
var snapshotJson = TransportRouteSnapshots.Serialize(TransportRouteSnapshots.Capture(
    "TASK", "STEP", "CMD", "AGV", payload, runtime.Current!, request));
var revision = TransportRouteSnapshots.Hash(snapshotJson);

database.Insertable(new DispatchTaskEntity
{
    TaskId = "TASK",
    StationId = "CNC-001",
    VehicleId = "AGV",
    Stage = (int)DispatchTaskStage.PickingUp,
    CreatedAt = DateTime.Now,
    UpdatedAt = DateTime.Now
}).ExecuteCommand();
database.Insertable(new DispatchTaskStepEntity
{
    StepId = "STEP",
    TaskId = "TASK",
    Sequence = 1,
    StepType = (int)DispatchTaskStepType.GoToPickup,
    Status = (int)DispatchTaskStepStatus.Completed,
    SourceSite = "A",
    DestinationSite = "C",
    VehicleId = "AGV",
    VendorTaskId = "STEP",
    CreatedAt = DateTime.Now,
    UpdatedAt = DateTime.Now,
    CompletedAt = DateTime.Now
}).ExecuteCommand();
database.Insertable(new AgvCommandOutboxEntity
{
    CommandId = "CMD",
    IdempotencyKey = "STEP:STEP:TRANSPORT",
    TaskId = "TASK",
    StepId = "STEP",
    VehicleId = "AGV",
    VendorCommandId = "STEP",
    CommandType = (int)AgvCommandType.Transport,
    Status = (int)AgvCommandStatus.Sent,
    PayloadJson = payload,
    ExecutionRouteSnapshotJson = snapshotJson,
    ExecutionRouteSnapshotHash = revision,
    ExecutionMapId = "MAP",
    ExecutionMapVersionId = "V1",
    SentAt = now.AddSeconds(-10).LocalDateTime,
    CreatedAt = now.AddSeconds(-10).LocalDateTime,
    UpdatedAt = now.AddSeconds(-10).LocalDateTime
}).ExecuteCommand();
database.Insertable(request).ExecuteCommand();
foreach (var (leaseId, resourceId) in new[]
{
    ("LEASE-CORRIDOR", "CORRIDOR"),
    ("LEASE-INTERSECTION", "INTERSECTION"),
    ("LEASE-AUTO-CORRIDOR", "AUTO-CORRIDOR-SYNTHETIC"),
    ("LEASE-FINAL", "FINAL")
})
{
    database.Insertable(new TrafficResourceLeaseEntity
    {
        LeaseId = leaseId,
        ResourceId = resourceId,
        OwnerStepId = "STEP",
        TaskId = "TASK",
        VehicleId = "AGV",
        MapId = "MAP",
        MapVersionId = "V1",
        AcquiredAt = now.AddSeconds(-10).LocalDateTime,
        RenewedAt = now.AddSeconds(-1).LocalDateTime,
        ExpiresAt = now.AddMinutes(2).LocalDateTime
    }).ExecuteCommand();
}

var vehicle = new AgvRobotSnapshot("AGV", "AGV", "127.0.0.1", 19204,
    ConnectionState.Online, SignalQuality.Good, 10, 0, 0, .95, "C", "C",
    80, false, false, "Idle", null, null, null, [], [], now,
    now, 0, null, null, 1);
var vehicles = new Vehicles(vehicle);
var provider = new BodyProvider(new("CALIBRATED", "AGV", "LocalCartesian",
    .49, true, true));
var options = new HandshakeExecutionOptions
{
    Enabled = true,
    DumperExit = new()
    {
        BodyProfiles =
        [
            new DumperExitBodyProfile("CALIBRATED", "AGV", .49, .1, true, true)
        ]
    }
};
var manager = new TrafficResourceManager(database,
    new TrafficResourceCatalog(resources), new(TimeSpan.FromMinutes(2)));
var service = new CompletedTransportRouteReleaseService(database, runtime, vehicles,
    provider, options, new MapOptions { MinimumLocalizationConfidence = .5 },
    new TrafficSegmentReleasePolicy { Enabled = true }, manager);

var released = await service.ReleaseAsync("TASK", "STEP", default);
// R3: SYNTHETIC is not produced by the enriched topology, so it has no geometry to verify
// and stays leased; only the two bounded resources the vehicle has cleared are released.
Check(released.ReleasedCount == 2, "完成到站后释放两个有边界资源；无几何核验依据的自动生成路段资源保留");
Check(!database.Queryable<TrafficResourceLeaseEntity>().Any(item =>
    item.LeaseId == "LEASE-CORRIDOR" || item.LeaseId == "LEASE-INTERSECTION") &&
    database.Queryable<TrafficResourceLeaseEntity>().Any(item => item.LeaseId == "LEASE-AUTO-CORRIDOR"),
    "前序有边界路段租约已删除，无几何的自动生成路段租约保留");
Check(database.Queryable<TrafficResourceLeaseEntity>().Any(item =>
    item.LeaseId == "LEASE-FINAL"), "终点资源继续保留");
request = database.Queryable<TrafficRequestEntity>().Single();
Check(request.ResourceIds == "[\"AUTO-CORRIDOR-SYNTHETIC\",\"FINAL\"]" && request.State == "Held",
    "路权查询显示剩余的终点及未核验资源");
var replay = await service.ReleaseAsync("TASK", "STEP", default);
Check(replay.ReleasedCount == 0 &&
    database.Queryable<TrafficResourceLeaseEntity>().Count() == 2,
    "重复扫描幂等且不删除终点");

// Reproduce hold-and-wait at a rolling boundary: our old CORRIDOR is clear,
// while another car owns NEXT and needs CORRIDOR to leave the dumper.
using var windowDb=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(root,"window.db")});
await new DatabaseInitializer(windowDb).InitializeAsync(default);
var extended = map with { Nodes = map.Nodes
        .Select(node => node.Id == "C"
            ? node with { ResourceIds = ["FINAL", "STOP-CLEAR", "STOP-KEEP"] }
            : node)
        .Append(new MapNode("D",20,0,MapNodeType.WaitingPoint,true,["NEXT","BAY"])).ToArray(),
    Edges=map.Edges.Append(new MapEdge("CD","C","D",10,1,false,true,["NEXT"])).ToArray(),
    Resources=map.Resources
        .Append(new TrafficResourceDefinition("NEXT",TrafficResourceType.Station))
        .Append(new TrafficResourceDefinition("BAY",TrafficResourceType.Station,1,
            [new(19,-1),new(21,-1),new(21,1),new(19,1)]))
        .Append(new TrafficResourceDefinition("STOP-KEEP",TrafficResourceType.Station))
        .Append(new TrafficResourceDefinition("NEXT-CLEAR",TrafficResourceType.NarrowAisle,1,
            [new(14,-1),new(16,-1),new(16,1),new(14,1)]))
        .Append(new TrafficResourceDefinition("STOP-CLEAR",TrafficResourceType.Intersection,1,
            [new(6,-1),new(8,-1),new(8,1),new(6,1)]))
        .ToArray() };
runtime.Replace(new("MAP","V1",extended,DateTimeOffset.UtcNow));
var currentWindow=new AgvTransportRouteWindow(0,"A","C",["A","B","C"],["AB","BC"],
    ["CORRIDOR","FINAL","INTERSECTION","AUTO-CORRIDOR-SYNTHETIC","NEXT-CLEAR","STOP-CLEAR","STOP-KEEP","BAY"],false);
var nextWindow=new AgvTransportRouteWindow(1,"C","D",["C","D"],["CD"],["FINAL","NEXT"],true);
var nextWindowWithClearCorridor=nextWindow with { ResourceIds=["FINAL","NEXT","NEXT-CLEAR","BAY"] };
payload=JsonSerializer.Serialize(new AgvTransportCommandPayload("STEP","AGV","A","C",["A","B","C"],null)
    {BusinessStepId="STEP",WindowCount=2,Windows=[currentWindow,nextWindowWithClearCorridor]},new JsonSerializerOptions(JsonSerializerDefaults.Web));
request.ResourceIds=JsonSerializer.Serialize(currentWindow.ResourceIds);request.State="Held";
snapshotJson=TransportRouteSnapshots.Serialize(TransportRouteSnapshots.Capture("TASK","STEP","CMD","AGV",payload,runtime.Current!,request));
windowDb.Insertable(request).ExecuteCommand();
var windowTask=database.Queryable<DispatchTaskEntity>().Single();windowTask.Stage=(int)DispatchTaskStage.GoingToDumper;windowTask.VendorTaskId="STEP";windowDb.Insertable(windowTask).ExecuteCommand();
var windowStep=database.Queryable<DispatchTaskStepEntity>().Single();windowStep.Status=(int)DispatchTaskStepStatus.Executing;windowStep.StepType=(int)DispatchTaskStepType.DeliverFullBoxToDumper;windowStep.DestinationSite="D";windowDb.Insertable(windowStep).ExecuteCommand();
var windowCommand=database.Queryable<AgvCommandOutboxEntity>().Single();windowCommand.PayloadJson=payload;windowCommand.ExecutionRouteSnapshotJson=snapshotJson;windowCommand.ExecutionRouteSnapshotHash=TransportRouteSnapshots.Hash(snapshotJson);windowDb.Insertable(windowCommand).ExecuteCommand();
var batchAcquiredAt = new DateTime(2026, 11, 22, 3, 55, 25, 270);
foreach(var resource in new[]{"FINAL","CORRIDOR","INTERSECTION","AUTO-CORRIDOR-SYNTHETIC","NEXT-CLEAR","STOP-CLEAR","STOP-KEEP","BAY"})
 windowDb.Insertable(new TrafficResourceLeaseEntity{LeaseId="BOUNDARY-"+resource,ResourceId=resource,TaskId="TASK",OwnerStepId="STEP",VehicleId="AGV",MapId="MAP",MapVersionId="V1",AcquiredAt=batchAcquiredAt,RenewedAt=DateTime.Now,ExpiresAt=DateTime.Now.AddMinutes(2)}).ExecuteCommand();
windowDb.Insertable(new TaskWaitingBayEntity{TaskId="TASK",StepId="STEP",VehicleId="AGV",MapId="MAP",MapVersionId="V1",WaitingSite="D",ResourceId="BAY",SelectedAt=DateTime.Now}).ExecuteCommand();
// Reproduce the persisted batch format found in the B07 release refusal,
// independently of how this provider writes a single DateTime parameter.
windowDb.Ado.ExecuteCommand("UPDATE traffic_resource_lease SET AcquiredAt = '2026-11-22 03:55:25.270' WHERE LeaseId = 'BOUNDARY-CORRIDOR'");
windowDb.Insertable(new TrafficResourceLeaseEntity{LeaseId="OTHER-NEXT",ResourceId="NEXT",TaskId="OTHER",OwnerStepId="OTHER-STEP",VehicleId="OTHER-CAR",MapId="MAP",MapVersionId="V1",AcquiredAt=DateTime.Now,RenewedAt=DateTime.Now,ExpiresAt=DateTime.Now.AddMinutes(2)}).ExecuteCommand();
var windowManager=new TrafficResourceManager(windowDb,new TrafficResourceCatalog(extended.Resources),new(TimeSpan.FromMinutes(2)));
var windowService=new CompletedTransportRouteReleaseService(windowDb,runtime,vehicles,provider,options,new MapOptions{MinimumLocalizationConfidence=.5},new TrafficSegmentReleasePolicy{Enabled=true},windowManager);
void Refresh(double speed=0,double offset=0)=>vehicles.Snapshot=vehicle with {X=10+offset,LastSuccessfulPollAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow,VelocityX=speed,VelocityY=0,AngularVelocity=0};
AgvTransportTaskStatus Terminal(AgvTransportTaskState state=AgvTransportTaskState.Completed,string id="STEP")=>new(id,"TASK",state,"AGV",DateTimeOffset.UtcNow);
Refresh();
Check((await windowService.ReleaseWindowAsync("TASK","STEP",Terminal(AgvTransportTaskState.GoingToDestination),default)).ReleasedCount==0,"原厂家窗口未完成时不释放旧窗口");
Check((await windowService.ReleaseWindowAsync("TASK","STEP",Terminal(id:"WRONG"),default)).ReleasedCount==0,"其他命令终态不能作为原窗口释放证据");
Refresh(speed:.1);
Check((await windowService.ReleaseWindowAsync("TASK","STEP",Terminal(),default)).ReleasedCount==0,"仍在移动时不因站点字段相同释放旧资源");
Refresh(offset:1);
Check((await windowService.ReleaseWindowAsync("TASK","STEP",Terminal(),default)).ReleasedCount==0,"坐标未到安全站点时不释放，不能冒用最近站点");
Refresh();
var boundaryReleased=await windowService.ReleaseWindowAsync("TASK","STEP",Terminal(),default);
Check(boundaryReleased.ReleasedCount==4,"下一窗口被占用时，先按完整包络释放已通过的旧路段、路口、复用走廊及已明确离开的当前停点多边形资源");
Check(windowDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.ResourceId=="FINAL")&&windowDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.LeaseId=="OTHER-NEXT")&&windowDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.ResourceId=="AUTO-CORRIDOR-SYNTHETIC"),"保留当前停点、他车下一窗口和无几何依据的未知走廊，未删他车路权");
Check(!windowDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.LeaseId=="BOUNDARY-NEXT-CLEAR"),"下一窗口复用但车体已完全离开的旧资源也先释放，避免先占后等");
Check(windowDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.LeaseId=="BOUNDARY-BAY"),"中途停稳时保留已为原任务预订的下游倒料候车位");
Check(windowDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.LeaseId=="BOUNDARY-STOP-KEEP"),"当前停点无几何依据的资源继续保留，不按站点列表笼统阻塞相邻有边界资源");
Check(!windowDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.LeaseId=="BOUNDARY-STOP-CLEAR"),"当前停点列出的有边界资源只有在完整车体包络已离开时才释放");
Check((await windowManager.TryAcquireAsync("OTHER-STEP","OTHER","OTHER-CAR",["CORRIDOR"],default)).Acquired,"返箱车可取得前车真正释放的路权，不再被已走过路段挡住");
Check(windowDb.Queryable<DispatchTaskStepEntity>().Single(s=>s.StepId=="STEP").Status==(int)DispatchTaskStepStatus.Executing&&windowDb.Queryable<AgvCommandOutboxEntity>().Count()==1,"只释放前序资源，不完成业务步骤、不伪造PLC、不提前发送下一窗口");
var identityAcquiredAt=DateTime.Now.AddMinutes(-1);
var identityLease=new TrafficResourceLeaseEntity
{
    LeaseId="LEASE-IDENTITY",ResourceId="CORRIDOR",TaskId="IDENTITY-TASK",
    OwnerStepId="IDENTITY-STEP",VehicleId="AGV",MapId="MAP",MapVersionId="V1",
    AcquiredAt=identityAcquiredAt,RenewedAt=DateTime.Now,ExpiresAt=DateTime.Now.AddMinutes(2)
};
windowDb.Insertable(identityLease).ExecuteCommand();
TrafficResourceLeaseEntity IdentityExpected(string vehicle="AGV",string resource="CORRIDOR",
    string version="V1",DateTime? acquiredAt=null)=>new()
{
    LeaseId="LEASE-IDENTITY",ResourceId=resource,TaskId="IDENTITY-TASK",
    OwnerStepId="IDENTITY-STEP",VehicleId=vehicle,MapId="MAP",MapVersionId=version,
    AcquiredAt=acquiredAt??identityAcquiredAt
};
Check(await windowManager.ReleasePassedLeasesAsync("IDENTITY-TASK","IDENTITY-STEP",
    [IdentityExpected(vehicle:"OTHER-CAR")],"身份回归",default)==0&&
    windowDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.LeaseId=="LEASE-IDENTITY"),
    "释放事务拒绝车号变化的同名租约");
Check(await windowManager.ReleasePassedLeasesAsync("IDENTITY-TASK","IDENTITY-STEP",
    [IdentityExpected(resource:"INTERSECTION")],"身份回归",default)==0&&
    windowDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.LeaseId=="LEASE-IDENTITY"),
    "释放事务拒绝资源变化的同名租约");
Check(await windowManager.ReleasePassedLeasesAsync("IDENTITY-TASK","IDENTITY-STEP",
    [IdentityExpected(version:"V2")],"身份回归",default)==0&&
    windowDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.LeaseId=="LEASE-IDENTITY"),
    "释放事务拒绝地图版本变化的同名租约");
Check(await windowManager.ReleasePassedLeasesAsync("IDENTITY-TASK","IDENTITY-STEP",
    [IdentityExpected(acquiredAt:identityAcquiredAt.AddSeconds(1))],"身份回归",default)==0&&
    windowDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.LeaseId=="LEASE-IDENTITY"),
    "释放事务拒绝获取周期变化的同名租约");
Check(await windowManager.ReleasePassedLeasesAsync("IDENTITY-TASK","IDENTITY-STEP",
    [IdentityExpected()],"身份回归",default)==1&&
    !windowDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.LeaseId=="LEASE-IDENTITY"),
    "完整身份一致时才删除目标租约");
if(args.Length==1)
{
    // Read-only archive -> private SQLite replay. This exercises actual CAD
    // geometry; timestamps/terminal telemetry are explicit fixture inputs,
    // never live vehicle evidence or permission to release original leases.
    var archive=Path.GetFullPath(args[0]);var replayPath=Path.Combine(root,"cad-replay.db");
    using(var source=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(archive,"data","agv-dispatch.db"),Mode=SqliteOpenMode.ReadOnly}.ToString()))
    using(var target=new SqliteConnection("Data Source="+replayPath)){source.Open();target.Open();source.BackupDatabase(target);}
    using var cadDb=DatabaseFactory.Create(new(){DatabasePath=replayPath});
    var cadTask=cadDb.Queryable<DispatchTaskEntity>().Single(t=>t.VehicleId=="AGV-02"&&t.Stage==(int)DispatchTaskStage.GoingToDumper);
    var cadStep=cadDb.Queryable<DispatchTaskStepEntity>().Single(s=>s.TaskId==cadTask.TaskId&&s.Status==(int)DispatchTaskStepStatus.Executing);
    var cadCommand=cadDb.Queryable<AgvCommandOutboxEntity>().Single(c=>c.StepId==cadStep.StepId&&c.VendorCommandId==cadTask.VendorTaskId);
    var jsonOptions=new JsonSerializerOptions(JsonSerializerDefaults.Web);jsonOptions.Converters.Add(new JsonStringEnumConverter());
    var cadPayload=JsonSerializer.Deserialize<AgvTransportCommandPayload>(cadCommand.PayloadJson,jsonOptions)!;
    var cadVersion=cadDb.Queryable<MapVersionEntity>().Single(v=>v.VersionId==cadCommand.ExecutionMapVersionId);
    var cadMap=JsonSerializer.Deserialize<MapDefinition>(cadVersion.DefinitionJson,jsonOptions)!;
    var cadRuntime=new ActiveMapRuntime();cadRuntime.Replace(new(cadVersion.MapId,cadVersion.VersionId,cadMap,DateTimeOffset.UtcNow));
    var cadRequest=cadDb.Queryable<TrafficRequestEntity>().Single(r=>r.StepId==cadStep.StepId);
    // Reconstruct the ORIGINAL window request before the failed next-window
    // attempt, only in this private fixture. Keep all archived leases intact.
    var originalWindow=cadPayload.Windows[cadPayload.WindowIndex];
    cadRequest.Source=originalWindow.Source;cadRequest.Destination=originalWindow.Destination;
    cadRequest.EdgeIds=JsonSerializer.Serialize(originalWindow.EdgeIds);cadRequest.ResourceIds=JsonSerializer.Serialize(originalWindow.ResourceIds);cadRequest.State="Held";cadDb.Updateable(cadRequest).ExecuteCommand();
    using var device=JsonDocument.Parse(File.ReadAllText(Path.Combine(archive,"multi-state.json")));
    var car=device.RootElement.GetProperty("cars").GetProperty("AGV-02");
    var cadVehicle=vehicle with {VehicleId="AGV-02",CurrentStation=car.GetProperty("station").GetString(),X=car.GetProperty("x").GetDouble(),Y=car.GetProperty("y").GetDouble(),LastSuccessfulPollAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow,VelocityX=0,VelocityY=0,AngularVelocity=0};
    using var settings=JsonDocument.Parse(File.ReadAllText(Path.Combine(archive,"service-settings.json")));
    var cadHandshake=settings.RootElement.GetProperty("HandshakeExecution").Deserialize<HandshakeExecutionOptions>(jsonOptions)!;
    var cadProfile=cadHandshake.DumperExit.BodyProfiles.Single(p=>p.VehicleId=="AGV-02");
    var cadManager=new TrafficResourceManager(cadDb,new TrafficResourceCatalog(cadMap.Resources),new(TimeSpan.FromMinutes(2)));
    var cadService=new CompletedTransportRouteReleaseService(cadDb,cadRuntime,new Vehicles(cadVehicle),new BodyProvider(new(cadProfile.ProfileId,"AGV-02",cadMap.CoordinateSystem,cadProfile.RadiusFromReportedReferenceMeters,cadProfile.CalibrationVerified,cadProfile.IncludesMaximumPermittedLoad)),cadHandshake,new MapOptions(),new TrafficSegmentReleasePolicy{Enabled=true},cadManager);
    var cadReleased=await cadService.ReleaseWindowAsync(cadTask.TaskId,cadStep.StepId,new(cadTask.VendorTaskId!,cadTask.TaskId,AgvTransportTaskState.Completed,"AGV-02",DateTimeOffset.UtcNow),default);
    var cleared=new[]{"SEG-0203","X-CAD2-J0640","X-CAD2-J0641","AUTO-CORRIDOR-7223E762A27394A1B200","AUTO-CORRIDOR-A9035B3BCBCC062F62B8"};
    Check(cadReleased.ReleasedCount>0&&!cadDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.TaskId==cadTask.TaskId&&cleared.Contains(l.ResourceId)),"真实失败档案的CAD回放：完整包络已离开的五项返箱阻塞资源均可核验释放（仅私有副本）");
    Check(cadDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.VehicleId=="AGV-04"&&l.ResourceId=="X-CAD2-J0652")&&cadDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.TaskId==cadTask.TaskId&&l.ResourceId=="SIM-STOP-SIM-SAFE-CAD2-E01832"),"CAD回放仍保留倒料车当前资源及满箱车安全点，不删除另一辆车路权");
}
if(args.Length==2&&args[0]=="--dumper-replay")
{
    // Replay the v2 DUMPER hold in a private SQLite copy. The archived task is
    // momentarily restored to the stage it had when the entry step completed;
    // this only recreates the release-time decision and never edits the archive.
    var archive=Path.GetFullPath(args[1]);var replayPath=Path.Combine(root,"dumper-replay.db");
    using(var source=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(archive,"data","agv-dispatch.db"),Mode=SqliteOpenMode.ReadOnly}.ToString()))
    using(var target=new SqliteConnection("Data Source="+replayPath)){source.Open();target.Open();source.BackupDatabase(target);}
    using var dumperDb=DatabaseFactory.Create(new(){DatabasePath=replayPath});
    var dumperTask=dumperDb.Queryable<DispatchTaskEntity>().Single(t=>t.VehicleId=="AGV-04"&&t.StationId=="CNC-239");
    var dumperStep=dumperDb.Queryable<DispatchTaskStepEntity>().Single(s=>
        s.TaskId==dumperTask.TaskId&&s.StepType==(int)DispatchTaskStepType.EnterDumpingPosition);
    dumperTask.Stage=(int)DispatchTaskStage.Dumping;dumperTask.VendorTaskId=dumperStep.VendorTaskId;
    dumperDb.Updateable(dumperTask).ExecuteCommand();
    var dumperCommand=dumperDb.Queryable<AgvCommandOutboxEntity>().Single(c=>
        c.StepId==dumperStep.StepId&&c.VendorCommandId==dumperStep.VendorTaskId);
    var dumperVersion=dumperDb.Queryable<MapVersionEntity>().Single(v=>v.VersionId==dumperCommand.ExecutionMapVersionId);
    var jsonOptions=new JsonSerializerOptions(JsonSerializerDefaults.Web);jsonOptions.Converters.Add(new JsonStringEnumConverter());
    var dumperMap=JsonSerializer.Deserialize<MapDefinition>(dumperVersion.DefinitionJson,jsonOptions)!;
    var dumperRuntime=new ActiveMapRuntime();dumperRuntime.Replace(new(dumperVersion.MapId,dumperVersion.VersionId,dumperMap,DateTimeOffset.UtcNow));
    using var device=JsonDocument.Parse(File.ReadAllText(Path.Combine(archive,"multi-state.json")));
    var car=device.RootElement.GetProperty("cars").GetProperty("AGV-04");
    var dumperVehicle=vehicle with {VehicleId="AGV-04",CurrentStation=car.GetProperty("station").GetString(),X=car.GetProperty("x").GetDouble(),Y=car.GetProperty("y").GetDouble(),LastSuccessfulPollAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow,VelocityX=0,VelocityY=0,AngularVelocity=0};
    using var settings=JsonDocument.Parse(File.ReadAllText(Path.Combine(archive,"service-settings.json")));
    var dumperHandshake=settings.RootElement.GetProperty("HandshakeExecution").Deserialize<HandshakeExecutionOptions>(jsonOptions)!;
    var dumperProfile=dumperHandshake.DumperExit.BodyProfiles.Single(p=>p.VehicleId=="AGV-04");
    var dumperManager=new TrafficResourceManager(dumperDb,new TrafficResourceCatalog(dumperMap.Resources),new(TimeSpan.FromMinutes(2)));
    var dumperService=new CompletedTransportRouteReleaseService(dumperDb,dumperRuntime,
        new Vehicles(dumperVehicle),new BodyProvider(new(dumperProfile.ProfileId,"AGV-04",
            dumperMap.CoordinateSystem,dumperProfile.RadiusFromReportedReferenceMeters,
            dumperProfile.CalibrationVerified,dumperProfile.IncludesMaximumPermittedLoad)),
        dumperHandshake,new MapOptions(),new TrafficSegmentReleasePolicy{Enabled=true},dumperManager);
    var dumperReleased=await dumperService.ReleaseAsync(dumperTask.TaskId,dumperStep.StepId,default);
    Check(dumperReleased.ReleasedCount>0&&!dumperDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>
            l.TaskId==dumperTask.TaskId&&l.ResourceId=="X-CAD2-J0652"),
        "v2失败档案回放：DUMPER到站后释放完整包络已离开的相邻路口X-CAD2-J0652");
    Check(dumperDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>
            l.TaskId==dumperTask.TaskId&&l.ResourceId=="DUMPER-001"),
        "v2失败档案回放：仍在当前停点的主设备资源DUMPER-001继续保留");
    Check(dumperDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>
            l.TaskId==dumperTask.TaskId&&l.ResourceId=="X-CAD2-J0651"),
        "v2失败档案回放：完整包络仍覆盖的相邻路口X-CAD2-J0651继续保留");
    Check(dumperDb.Queryable<TrafficResourceLeaseEntity>().Any(l=>
            l.VehicleId=="AGV-01"&&l.ResourceId=="AUTO-CORRIDOR-C5312196CB270FFD59E2"),
        "v2失败档案回放：释放另一任务资源时不得删除AGV-01原路权");
}
Console.WriteLine($"PASS {checks} completed-route release checks. Database: {root}");

sealed class Vehicles(AgvRobotSnapshot snapshot) : IAgvStateCache
{
    public AgvRobotSnapshot Snapshot= snapshot;
    public event Action<AgvRobotStateChanged>? StateChanged;
    public long Version => Snapshot.Version;
    public IReadOnlyList<AgvRobotSnapshot> GetAll() => [Snapshot];
    public bool TryGet(string vehicleId, out AgvRobotSnapshot value)
    {
        value = Snapshot;
        return vehicleId == Snapshot.VehicleId;
    }
}

sealed class BodyProvider(TrafficBodyEnvelope body) : ITrafficBodyEnvelopeProvider
{
    public Task<TrafficBodyEnvelope?> ReadAsync(string vehicleId, CancellationToken ct) =>
        Task.FromResult<TrafficBodyEnvelope?>(body.VehicleId == vehicleId ? body : null);
}
