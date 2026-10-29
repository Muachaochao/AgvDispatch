using System.Text.Json;
using System.Text.Json.Serialization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Connectors.Plc;

var evidence=Path.GetFullPath("docs/commissioning/evidence/20261015-map-transfer-history-ledger");Directory.CreateDirectory(evidence);var root=Path.Combine(evidence,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
var results=new List<object>();var checks=0;
void Check(bool yes,string name){if(!yes)throw new Exception(name);checks++;results.Add(new{Name=name,Status="PASS"});Console.WriteLine("PASS "+name);}
using var db=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(root,"isolated.db")});await new DatabaseInitializer(db).InitializeAsync(default);
var source=Path.Combine(root,"source.smap");File.Copy("docs/vendor/robokit/map-format-20261014/smap-master/maps/6.smap",source);
var json=new JsonSerializerOptions(JsonSerializerDefaults.Web){Converters={new JsonStringEnumConverter()}};
var definition=new MapDefinition("test",source,"world","m",[new("N1",0,0,MapNodeType.Station,true,[],"LM1"),new("N2",1,0,MapNodeType.Station,true,[],"LM2")],[new("E","N1","N2",1,1,true,true,[])],[]);
await db.Insertable(new MapProjectEntity{MapId="MAP",Name="test",PublishedVersionId="V1",ActiveVersionId="V1",IsActive=true,CreatedAt=DateTime.UtcNow,UpdatedAt=DateTime.UtcNow}).ExecuteCommandAsync();
await db.Insertable(new MapVersionEntity{VersionId="V1",MapId="MAP",Status=(int)MapVersionStatus.Published,DefinitionJson=JsonSerializer.Serialize(definition,json),ValidationJson="{}",CreatedAt=DateTime.UtcNow,UpdatedAt=DateTime.UtcNow}).ExecuteCommandAsync();
var barrier=new MapTransferRecoveryBarrier(db);var gate=new MapExecutionGate(null,barrier);var runtime=new ActiveMapRuntime();runtime.Replace(new("MAP","V1",definition,DateTimeOffset.Now));
var options=new AgvOptions{Enabled=true,Robots=[new(){VehicleId="SIM",IpAddress="127.0.0.1"}]};var cache=new AgvStateCache(options);
AgvRobotTelemetry Telemetry()=>new(0,0,0,1,"LM1",null,80,false,false,"0",null,null,null,[],[],DateTimeOffset.Now){VelocityX=0,VelocityY=0,AngularVelocity=0};
void Fresh()=>cache.ApplyTelemetry("SIM",Telemetry(),TimeSpan.Zero);Fresh();
var switches=new MapSwitchService(db,gate,runtime,cache,new SwitchSignalCache(),new(),barrier);await switches.InitializeAsync(default);
var inspection=new FakeInspector();var ledger=new MapTransferLedger(db,gate,switches,cache,runtime,inspection);var user=new TrafficUserContext("test-operator","test",TrafficPermission.View|TrafficPermission.ManageMap);
PrepareMapTransferRequest Req(string id)=>new(id,"MAP","V1","SIM",new string('a',32),"SIM-VERSION","模拟资料声明，不是本车核定",true);
var initial=await ledger.PrepareAsync(user,Req("first"),default);
Check(initial.State=="ReadOnlyPrepared"&&initial.GatesClear&&!initial.WritesEnabled&&initial.AttemptCount==0,"可核对准备记录持久化，但从不授权写入");
Check(db.Queryable<MapTransferEntity>().Count()==1&&db.Queryable<MapTransferEventEntity>().Count()==1,"申请与审计同事务落库");
Check((await ledger.PrepareAsync(user,Req("first"),default)).OperationId==initial.OperationId&&db.Queryable<MapTransferEntity>().Count()==1,"同申请编号重放原记录、不产生第二条");
try{await ledger.PrepareAsync(user,Req("first") with{VehicleId="OTHER"},default);throw new Exception("异参数重用未拒绝");}catch(InvalidOperationException){Check(true,"同编号异参数拒绝");}
try{await ledger.PrepareAsync(user with{Permissions=TrafficPermission.View},Req("view"),default);throw new Exception("View可准备");}catch(UnauthorizedAccessException){Check(true,"只读权限不能创建准备申请");}
try{await ledger.ReadAsync(user with{Permissions=TrafficPermission.None},new(initial.OperationId),default);throw new Exception("无权限可读");}catch(UnauthorizedAccessException){Check(true,"无View不能读记录");}
try{await db.Updateable<MapTransferEntity>().SetColumns(x=>x.ExpectedDeviceMd5==new string('b',32)).Where(x=>x.OperationId==initial.OperationId).ExecuteCommandAsync();throw new Exception("身份可改");}catch(Exception ex)when(!ex.Message.Contains("身份可改")){Check(true,"SQL身份字段重写被触发器拒绝");}
db.Ado.ExecuteCommand("CREATE TRIGGER fail_map_event BEFORE INSERT ON map_transfer_event WHEN NEW.Kind='Prepared' BEGIN SELECT RAISE(ABORT,'injected audit failure'); END");
try{await ledger.PrepareAsync(user,Req("rollback"),default);throw new Exception("审计失败未回滚");}catch(InvalidOperationException){Check(db.Queryable<MapTransferEntity>().Count()==1,"审计失败回滚申请、不留下半条记录");}
db.Ado.ExecuteCommand("DROP TRIGGER fail_map_event");
await db.Insertable(new DispatchTaskEntity{TaskId="WAITING",StationId="S",Stage=0,CreatedAt=DateTime.UtcNow,UpdatedAt=DateTime.UtcNow}).ExecuteCommandAsync();
Check(!(await ledger.ReadAsync(user,new(initial.OperationId),default)).GatesClear,"未分配等待任务也阻止地图传输准备");await db.Deleteable<DispatchTaskEntity>().Where(x=>x.TaskId=="WAITING").ExecuteCommandAsync();
await db.Insertable(new TrafficResourceLeaseEntity{LeaseId="LEASE",ResourceId="R",OwnerStepId="STEP",TaskId="T",VehicleId="SIM",AcquiredAt=DateTime.UtcNow.AddMinutes(-10),ExpiresAt=DateTime.UtcNow.AddMinutes(-1),RenewedAt=DateTime.UtcNow.AddMinutes(-2)}).ExecuteCommandAsync();
Check((await ledger.ReadAsync(user,new(initial.OperationId),default)).Blockers.Any(x=>x.Contains("物理资源")),"已过期原路权仍阻止准备，未删除占用");await db.Deleteable<TrafficResourceLeaseEntity>().Where(x=>x.LeaseId=="LEASE").ExecuteCommandAsync();
await db.Insertable(new AgvCommandOutboxEntity{CommandId="CMD",IdempotencyKey="CMD",TaskId="T",StepId="STEP",VehicleId="SIM",Status=90,PayloadJson="{}",CreatedAt=DateTime.UtcNow,UpdatedAt=DateTime.UtcNow,NextAttemptAt=DateTime.UtcNow}).ExecuteCommandAsync();
Check((await ledger.ReadAsync(user,new(initial.OperationId),default)).Blockers.Any(x=>x.Contains("命令")),"失败未闭环Outbox阻止准备");await db.Deleteable<AgvCommandOutboxEntity>().Where(x=>x.CommandId=="CMD").ExecuteCommandAsync();
cache.ApplyTelemetry("SIM",Telemetry() with{VelocityX=double.NaN},TimeSpan.Zero);Check(!(await ledger.ReadAsync(user,new(initial.OperationId),default)).GatesClear,"NaN速度不能当作停止");Fresh();
cache.ApplyTelemetry("SIM",Telemetry() with{ReadAt=DateTimeOffset.Now.AddMinutes(-1)},TimeSpan.Zero);Check(!(await ledger.ReadAsync(user,new(initial.OperationId),default)).GatesClear,"车辆过期拒绝");Fresh();
gate.SetMode(MapExecutionMode.Draining);Check(!(await ledger.ReadAsync(user,new(initial.OperationId),default)).GatesClear,"地图收尾执行门拒绝准备");gate.SetMode(MapExecutionMode.Running);
var original=File.ReadAllBytes(source);await File.AppendAllTextAsync(source," ");Check((await ledger.ReadAsync(user,new(initial.OperationId),default)).Blockers.Any(x=>x.Contains("指纹")),"源文件变化不能自动回填原摘要");File.WriteAllBytes(source,original);
var versionText=db.Queryable<MapVersionEntity>().Where(x=>x.VersionId=="V1").First().DefinitionJson;await db.Updateable<MapVersionEntity>().SetColumns(x=>x.DefinitionJson==versionText+" ").Where(x=>x.VersionId=="V1").ExecuteCommandAsync();Check(!(await ledger.ReadAsync(user,new(initial.OperationId),default)).GatesClear,"后台同版本定义变化拒绝");await db.Updateable<MapVersionEntity>().SetColumns(x=>x.DefinitionJson==versionText).Where(x=>x.VersionId=="V1").ExecuteCommandAsync();
Fresh();var observed=await ledger.ObserveAsync(user with{Permissions=TrafficPermission.View},new(initial.OperationId),default);
runtime.Replace(new("MAP","V2",definition,DateTimeOffset.Now));Check(!(await ledger.ReadAsync(user,new(initial.OperationId),default)).GatesClear,"活动地图版本变化拒绝沿用原准备");runtime.Replace(new("MAP","V1",definition,DateTimeOffset.Now));
await db.Updateable<MapProjectEntity>().SetColumns(x=>x.ActiveVersionId=="OTHER").Where(x=>x.MapId=="MAP").ExecuteCommandAsync();Check(!(await ledger.ReadAsync(user,new(initial.OperationId),default)).GatesClear,"数据库活动指针与运行地图不一致拒绝");await db.Updateable<MapProjectEntity>().SetColumns(x=>x.ActiveVersionId=="V1").Where(x=>x.MapId=="MAP").ExecuteCommandAsync();
Check(observed.Events.Any(e=>e.Kind=="Prepared")&&observed.Events.Any(e=>e.Kind=="ReadOnlyObservation"),"API返回当前记录的持久化事件历史");
Check(inspection.Calls==1&&observed.LastObservation.Contains("desiredMapObserved")&&observed.AttemptCount==0,"只读进度复核调用检查器、保存摘要、写入次数0");Check(db.Queryable<MapTransferEventEntity>().Where(x=>x.Kind=="ReadOnlyObservation").Count()==1,"只读复核审计持久化");
inspection.Match=false;var mismatch=await ledger.ObserveAsync(user,new(initial.OperationId),default);Check(mismatch.LastObservation.Contains("\"desiredMapObserved\":false"),"新失败观察覆盖旧成功，不沿用旧证据");inspection.Match=true;
inspection.Hold=new(TaskCreationOptions.RunContinuationsAsynchronously);var calls=inspection.Calls;var pending=ledger.ObserveAsync(user,new(initial.OperationId),default);while(inspection.Calls==calls)await Task.Delay(10);
await db.Insertable(new DispatchTaskEntity{TaskId="DURING-OBS",StationId="D",Stage=0,CreatedAt=DateTime.UtcNow,UpdatedAt=DateTime.UtcNow}).ExecuteCommandAsync();inspection.Hold.SetResult(true);var afterRace=await pending;
Check(!afterRace.GatesClear&&afterRace.LastObservation.Contains("CurrentBlockers",StringComparison.OrdinalIgnoreCase),"采集期间出现任务，回门重核后保存阻塞而非放行");inspection.Hold=null;await db.Deleteable<DispatchTaskEntity>().Where(x=>x.TaskId=="DURING-OBS").ExecuteCommandAsync();
var old=db.Queryable<MapTransferEntity>().Where(x=>x.OperationId==initial.OperationId).First();var uncertain=JsonSerializer.Deserialize<MapTransferEntity>(JsonSerializer.Serialize(old))!;uncertain.OperationId="UNCERTAIN";uncertain.RequestId="old-write";uncertain.State="Sending";uncertain.AttemptCount=1;uncertain.OwnerSession="old-service";await db.Insertable(uncertain).ExecuteCommandAsync();
Check(!barrier.IsOpen&&!gate.CanStartNewWork,"待定原写入立即关闭新工作门禁");
var restarted=new MapTransferLedger(db,gate,switches,cache,runtime,inspection);await restarted.InitializeAsync(default);var recovered=await restarted.ReadAsync(user,new("UNCERTAIN"),default);
Check(recovered.State=="Uncertain"&&recovered.PreviousSession&&recovered.AttemptCount==1,"重建服务保留原写入次数并恢复不确定状态");
Check(gate.Mode==MapExecutionMode.Held,"不确定记录重建关闭执行门");
gate.SetMode(MapExecutionMode.Running);using(var work=await gate.EnterAsync(MapExecutionOperation.ExistingWork,default))Check(work is null,"即使模式被改Running，不确定屏障也阻止旧任务发送");
await switches.InitializeAsync(default);Check(gate.Mode==MapExecutionMode.Held,"地图加载初始化不能覆盖不确定屏障");
Fresh();var later=await restarted.ObserveAsync(user,new("UNCERTAIN"),default);Check(later.State=="Uncertain"&&!later.GatesClear&&!barrier.IsOpen,"MD5匹配的只读观察不关闭未知写入或放行");
Check((await restarted.ReadAsync(user,new(initial.OperationId),default)).State=="ReviewRequired","旧准备记录重建需重新审核，不变成待发送");
Check(!typeof(IMapTransferLedger).GetMethods().Any(m=>m.Name.Contains("Send")||m.Name.Contains("Execute")),"后台契约没有发送/重发入口");
// New read-only list/history/disposition verification; no real device input.
var beforeCalls=inspection.Calls;var beforeEvents=db.Queryable<MapTransferEventEntity>().Count();
var plan=await restarted.ReviewAsync(user,new("UNCERTAIN"),default);
Check(plan.RequiresDisposition&&!plan.CanClose&&!plan.CanRelease&&!plan.CanResend&&!plan.ObservationProvesThisWrite,"不确定处置契约要求核对，四种授权固定关闭");
Check(plan.RequiredEvidence.Count>=6&&plan.ProhibitedActions.Any(x=>x.Contains("MD5")),"处置契约列原操作/厂家规则/现场证据，禁止MD5代替本次写入证明");
Check(inspection.Calls==beforeCalls&&db.Queryable<MapTransferEventEntity>().Count()==beforeEvents&&!barrier.IsOpen,"读取处置计划无设备采集或审计写入，不解除未知屏障");
var stamp=DateTime.UtcNow.AddDays(-1);
for(var i=0;i<27;i++){
 var row=JsonSerializer.Deserialize<MapTransferEntity>(JsonSerializer.Serialize(old))!;row.OperationId=$"PAGE-{i:D3}";row.RequestId=$"page-{i}";row.VehicleId="PAGE-CAR";row.MapId="PAGE-MAP";row.State="Blocked";row.CreatedAt=stamp;row.UpdatedAt=stamp;await db.Insertable(row).ExecuteCommandAsync();
}
var page1=await restarted.ListAsync(user,new("PAGE-CAR",PageSize:25),default);var page2=await restarted.ListAsync(user,new("PAGE-CAR",Page:2,PageSize:25),default);
Check(page1.Total==27&&page1.Items.Count==25&&page2.Items.Count==2,"后台列表分页25+2，无重核源图或采集设备");
Check(page1.Items.Concat(page2.Items).Select(x=>x.OperationId).Distinct().Count()==27&&page1.Items[0].OperationId=="PAGE-026","同时间记录用原编号次排序，跨页不重复");
Check((await restarted.ListAsync(user,new("PAGE-CAR",MapId:"OTHER"),default)).Total==0,"地图筛选不混淆其他地图");
Check((await restarted.ListAsync(user,new("PAGE-CAR",State:"Uncertain"),default)).Total==0,"状态筛选精确匹配");
Check((await restarted.ListAsync(user,new("PAGE-CAR",From:new DateTimeOffset(stamp,TimeSpan.Zero),To:new DateTimeOffset(stamp,TimeSpan.Zero)),default)).Total==27,"创建时间双闭区间，精确UTC边界");
Check((await restarted.ListAsync(user,new("PAGE-CAR",From:new DateTimeOffset(stamp.AddSeconds(1),TimeSpan.Zero)),default)).Total==0,"起始时间排除较早记录");
Check(page1.Items.All(x=>x.CreatedAt.Offset==TimeSpan.Zero)&&inspection.Calls==beforeCalls,"列表输出UTC且不读取车端");
foreach(var query in new[]{new MapTransferListQuery(Page:0),new MapTransferListQuery(PageSize:101),new MapTransferListQuery(State:"anything"),new MapTransferListQuery(From:DateTimeOffset.Now,To:DateTimeOffset.Now.AddDays(-1)),new MapTransferListQuery(VehicleId:" ")}){
 try{await restarted.ListAsync(user,query,default);throw new Exception("无效筛选放行");}catch(ArgumentException){Check(true,"无效分页/筛选被拒绝");}
}
for(var i=0;i<130;i++)await db.Insertable(new MapTransferEventEntity{Id=$"HISTORY-{i:D3}",OperationId="UNCERTAIN",Kind="SimulatedEvidence",SessionId="sim",Operator="test",Evidence=$"sample-{i}",OccurredAt=stamp}).ExecuteCommandAsync();
var history1=await restarted.HistoryAsync(user,new("UNCERTAIN",PageSize:100),default);var history2=await restarted.HistoryAsync(user,new("UNCERTAIN",Page:2,PageSize:100),default);
Check(history1.Total==132&&history1.Items.Count==100&&history2.Items.Count==32,"历史独立分页超过旧100项上限，132项可查");
Check(history1.Items.Concat(history2.Items).Where(x=>x.Kind=="SimulatedEvidence").Select(x=>x.Evidence).Distinct().Count()==130,"同时间审计按事件ID排序，不丢失或混入其他操作");
try{await restarted.HistoryAsync(user,new("missing"),default);throw new Exception("不存在操作历史成功");}catch(KeyNotFoundException){Check(true,"不存在操作拒绝历史读取");}
try{await restarted.HistoryAsync(user,new("UNCERTAIN",PageSize:101),default);throw new Exception("历史预算未拒绝");}catch(ArgumentException){Check(true,"历史分页预算拒绝");}
try{await restarted.ListAsync(user with{Permissions=TrafficPermission.None},new(),default);throw new Exception("无权列表成功");}catch(UnauthorizedAccessException){Check(true,"列表要求View权限");}
try{await restarted.HistoryAsync(user with{Permissions=TrafficPermission.None},new("UNCERTAIN"),default);throw new Exception("无权历史成功");}catch(UnauthorizedAccessException){Check(true,"历史要求View权限");}
try{await restarted.ReviewAsync(user with{Permissions=TrafficPermission.None},new("UNCERTAIN"),default);throw new Exception("无权处置计划成功");}catch(UnauthorizedAccessException){Check(true,"处置计划要求View权限");}
Check((await restarted.ListAsync(user with{Permissions=TrafficPermission.Administer},new("PAGE-CAR"),default)).Total==27,"管理员沿用Has权限兼容");
Check(db.Queryable<MapTransferEntity>().Where(x=>x.OperationId=="UNCERTAIN").First().State=="Uncertain"&&!barrier.IsOpen&&inspection.Calls==beforeCalls,"全部历史/计划读取不改原状态、次数或门禁");
File.WriteAllText(Path.Combine(evidence,"results.json"),JsonSerializer.Serialize(new{Passed=checks,Failed=0,Database=Path.Combine(root,"isolated.db"),Results=results},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Passed "+checks);

sealed class FakeInspector:IRobokitInspectionService
{
    public int Calls;public bool Match=true;
    public TaskCompletionSource<bool>? Hold;
    public async Task<RobokitInspectionSnapshot> ReadAsync(RobokitInspectionQuery query,CancellationToken ct)
    {
        Calls++;if(Hold is not null)await Hold.Task;
        return new RobokitInspectionSnapshot(query.VehicleId,DateTimeOffset.Now,true,true,"明确模拟，只读", "SIM-VERSION","1","6",new string(Match?'a':'b',32),1,"MAP","V1",false,"sim",[],[],[],[]);
    }
}
