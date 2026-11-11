using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;
using System.Text.Json;
var checks=0;void Check(bool value,string text){if(!value)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
await using(var f=await Fixture.Create()){
 await f.Task("A","SA","VA");await f.Task("B","SB","VB");await f.Hold("A","SA","VA","RA");await f.Hold("B","SB","VB","RB");await f.Wait("A","SA","VA","RB");await f.Wait("B","SB","VB","RA");
 var before=f.Db.Queryable<TrafficResourceLeaseEntity>().Count();var d=await f.Control.GetBlockingAsync(new("A","SA"),default);
 Check(d.Nodes.Count==2&&d.Links.Count==2&&d.CycleObserved,"两任务原占用和等待构成循环，重复访问不丢边");
 Check(d.Nodes.All(n=>!n.MissingEvidence)&&d.Nodes.All(n=>n.ReleaseRequirements.All(r=>r.State!="Ready")),"归属已观察也不输出可释放许可");
 Check(d.Events.Any(e=>e.Kind=="Waiting")&&d.Events.All(e=>e.TaskId is "A" or "B"),"只列关联任务步骤路权事件");
 Check(f.Db.Queryable<TrafficResourceLeaseEntity>().Count()==before,"阻塞链和规则查询不释放租约");
 f.Db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>s.Status==(int)DispatchTaskStepStatus.Completed).Where(s=>s.StepId=="SA").ExecuteCommand();var ended=await f.Control.GetBlockingAsync(new("A","SA"),default);Check(ended.Links.Count==0&&ended.Nodes[0].State!="Waiting","已完成历史等待步骤不继续补出当前阻塞链");
 f.Db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>s.Status==(int)DispatchTaskStepStatus.Pending).Where(s=>s.StepId=="SA").ExecuteCommand();

 f.Db.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)DispatchTaskStage.CancellationPending).Where(t=>t.TaskId=="B").ExecuteCommand();d=await f.Control.GetBlockingAsync(new("A","SA"),default);Check(d.Links.Count==1&&!d.CycleObserved&&d.Nodes.Single(n=>n.TaskId=="B").ReleaseRequirements.Any(r=>r.State=="RequiresReview"),"取消等待仍保留占用，但不沿用历史等待制造当前循环");
 f.Db.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)DispatchTaskStage.GoingToPickup).Where(t=>t.TaskId=="B").ExecuteCommand();
 var wrong=await f.Control.GetBlockingAsync(new("B","SA"),default);Check(!wrong.Available&&wrong.Nodes.Count==0,"任务/步骤归属不一致不补链");
 f.Db.Deleteable<DispatchTaskEntity>().Where(t=>t.TaskId=="B").ExecuteCommand();d=await f.Control.GetBlockingAsync(new("A","SA"),default);
 Check(d.Nodes.Single(n=>n.TaskId=="B").MissingEvidence&&d.Nodes.Single(n=>n.TaskId=="B").ReleaseRequirements.Any(r=>r.State=="RequiresReview"),"占用方任务缺失显示未知和需核对，不当作已结束");
}
await using(var f=await Fixture.Create()){
 foreach(var x in new[]{"A","B","C","D"})await f.Task(x,"S"+x,"V"+x);
 await f.Hold("B","SB","VB","RB");await f.Hold("C","SC","VC","RC");await f.Hold("D","SD","VD","RD");
 await f.Wait("A","SA","VA","RB","RC");await f.Wait("B","SB","VB","RD");await f.Wait("C","SC","VC","RD");
 var d=await f.Control.GetBlockingAsync(new("A","SA"),default);Check(d.Nodes.Count==4&&d.Links.Count==4&&!d.CycleObserved,"菱形汇合共享占用方不误报为循环");
 var expired=DateTime.Now.AddDays(-1);f.Db.Updateable<TrafficResourceLeaseEntity>().SetColumns(l=>l.ExpiresAt==expired).Where(l=>l.LeaseId!="").ExecuteCommand();d=await f.Control.GetBlockingAsync(new("A","SA"),default);
 Check(d.Links.All(l=>l.HeartbeatExpired)&&d.Nodes.Count==4,"过期心跳仍保留链路与占用");
 for(var i=0;i<70;i++)f.Db.Insertable(new TrafficLifecycleEventEntity{EventId="E"+i,TaskId="A",StepId="SA",VehicleId="VA",Kind="Observed",Reason="private",ResourceIds="[\"RB\"]",OccurredAt=DateTime.Now.AddSeconds(i)}).ExecuteCommand();
 f.Db.Insertable(new TrafficLifecycleEventEntity{EventId="OTHER",TaskId="UNRELATED",StepId="UNRELATED",VehicleId="OTHER",Kind="Unrelated",Reason="not part of chain",OccurredAt=DateTime.Now.AddDays(1)}).ExecuteCommand();
 f.Db.Insertable(new TrafficLifecycleEventEntity{EventId="LATE-INSERT-OLD",TaskId="A",StepId="SA",VehicleId="VA",Kind="Old",Reason="late historical insert",OccurredAt=DateTime.Now.AddDays(-1)}).ExecuteCommand();
 d=await f.Control.GetBlockingAsync(new("A","SA"),default);Check(d.Events[0].EventId=="E69"&&!d.Events.Any(e=>e.Kind=="Old"),"最近事件按发生时间排序，不受补入旧历史的插入顺序影响");Check(d.Events.Count==50&&d.EventsTruncated&&!d.Events.Any(e=>e.Kind=="Unrelated"),"关联历史50条预算与范围明确，不借全场同名资源事件");
}
await using(var f=await Fixture.Create()){
 for(var i=0;i<12;i++){await f.Task("T"+i,"S"+i,"V"+i);await f.Hold("T"+i,"S"+i,"V"+i,"R"+i);}
 for(var i=0;i<11;i++)await f.Wait("T"+i,"S"+i,"V"+i,"R"+(i+1));
 var d=await f.Control.GetBlockingAsync(new("T0","S0"),default);Check(d.Truncated&&d.Nodes.Count<=32&&d.Links.Count<=128&&!d.CycleObserved,"深链到8层明确截断，不能当作完整无环证明");
}
await using(var f=await Fixture.Create()){
 await f.Task("A","SA","VA");await f.Task("B","SB","VB");await f.Task("B","SB-WAIT","VB",false);await f.Task("C","SC","VC");
 await f.Hold("B","SB","VB","RB");await f.Hold("C","SC","VC","RC");await f.Wait("A","SA","VA","RB");await f.Wait("B","SB-WAIT","VB","RC");
 var d=await f.Control.GetBlockingAsync(new("A","SA"),default);Check(d.Links.Any(l=>l.Kind=="TaskWait"&&l.FromStepId=="SB"&&l.ToStepId=="SB-WAIT")&&d.Nodes.Any(n=>n.TaskId=="C"),"占用前一步时追踪同任务其他等待步骤");
 for(var i=0;i<210;i++)f.Db.Insertable(new TrafficRequestEntity{TaskId="Q"+i,StepId="Q"+i,VehicleId="Q",State="Waiting",RequestedAt=DateTime.Now.AddDays(2),UpdatedAt=DateTime.Now.AddDays(2)}).ExecuteCommand();
 var lifecycle=await f.Control.GetLifecycleAsync(default);d=await f.Control.GetBlockingAsync(new("A","SA"),default);Check(!lifecycle.Requests.Any(r=>r.StepId=="SA")&&d.Available&&d.Links.Count>0,"详情按原步骤查询，不受生命周期最近200条截断限制");
}
await using(var f=await Fixture.Create()){
 await f.Task("OLD","SO","CAR-OLD");await f.Task("NEXT","SN","CAR-NEXT");await f.Task("ASK","SQ","CAR-ASK");
 await f.Hold("OLD","SO","CAR-OLD","RA");await f.Wait("ASK","SQ","CAR-ASK","RA");
 var manager=new TrafficResourceManager(f.Db,new TrafficResourceCatalog([new("RA",TrafficResourceType.NarrowAisle)]),new(TimeSpan.FromMinutes(10)));
 await manager.ReleaseTaskAsync("OLD",default);await f.Hold("NEXT","SN","CAR-NEXT","RA");
 await f.Wait("ASK","SQ","CAR-ASK","RA");
 var d=await f.Control.GetBlockingAsync(new("ASK","SQ"),default);
 Check(d.ResourceHistory.Any(e=>e.VehicleId=="CAR-OLD"&&e.Kind=="ResourceAcquired")&&
       d.ResourceHistory.Any(e=>e.VehicleId=="CAR-OLD"&&e.Kind=="ResourceReleased")&&
       d.ResourceHistory.Any(e=>e.VehicleId=="CAR-NEXT"&&e.Kind=="ResourceAcquired"),
       "当前挡路方变化后仍可看前车实际释放和后车取得，不依赖当前阻塞链");
 Check(d.Links.Single().VehicleId=="CAR-NEXT"&&!d.Links.Any(l=>l.VehicleId=="CAR-OLD"),"历史占用方不会被当作当前挡路车辆");
 await f.Db.Insertable(new TrafficRequestEntity{TaskId="OTHER",StepId="OTHER",VehicleId="OTHER",MapId="M",MapVersionId="OTHER"}).ExecuteCommandAsync();
 await f.Db.Insertable(new TrafficLifecycleEventEntity{EventId="OTHER-MAP",TaskId="OTHER",StepId="OTHER",VehicleId="OTHER",Kind="ResourceAcquired",ResourceIds="[\"RA\"]",OccurredAt=DateTime.Now}).ExecuteCommandAsync();
 await f.Db.Insertable(new TrafficLifecycleEventEntity{EventId="SUBSTRING",TaskId="NEXT",StepId="SN",VehicleId="CAR-NEXT",Kind="ResourceAcquired",ResourceIds="[\"RA-OTHER\"]",OccurredAt=DateTime.Now}).ExecuteCommandAsync();
 d=await f.Control.GetBlockingAsync(new("ASK","SQ"),default);
 Check(!d.ResourceHistory.Any(e=>e.EventId is "OTHER-MAP" or "SUBSTRING"),"交接历史不混用其他地图或仅名称前缀相同的资源");
}
Console.WriteLine($"Traffic blocking: {checks} checks passed; isolated SQLite only.");
sealed class Fixture:IAsyncDisposable{
 public ISqlSugarClient Db=null!;public TrafficResourceControlService Control=null!;TrafficResourceManager manager=null!;
 public static async Task<Fixture>Create(){var f=new Fixture();var path=Path.Combine(Path.GetTempPath(),"agv-blocking-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);f.Db=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(path,"private.db")});var catalog=new TrafficResourceCatalog(Enumerable.Range(0,20).Select(i=>new TrafficResourceDefinition("R"+i,TrafficResourceType.NarrowAisle)).Concat(new[]{"RA","RB","RC","RD"}.Select(id=>new TrafficResourceDefinition(id,TrafficResourceType.NarrowAisle))));await new DatabaseInitializer(f.Db,catalog).InitializeAsync(default);f.Control=new(f.Db,catalog);f.manager=new(f.Db,catalog,new(TimeSpan.FromMinutes(10)));return f;}
 public async Task Task(string task,string step,string vehicle,bool create=true){if(create)await Db.Insertable(new DispatchTaskEntity{TaskId=task,StationId="ST-"+task,VehicleId=vehicle,Stage=(int)DispatchTaskStage.GoingToPickup,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommandAsync();await Db.Insertable(new DispatchTaskStepEntity{TaskId=task,StepId=step,Sequence=create?1:2,StepType=(int)DispatchTaskStepType.GoToPickup,Status=(int)DispatchTaskStepStatus.Pending,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommandAsync();}
 public async Task Hold(string task,string step,string vehicle,string resource){await manager.RecordRouteAsync(new(step,task,vehicle,"A","B",[],[resource],false,"private"){MapId="M",MapVersionId="V"},default);await manager.TryAcquireAsync(step,task,vehicle,[resource],default);}
 public async Task Wait(string task,string step,string vehicle,params string[] resources){await manager.RecordRouteAsync(new(step,task,vehicle,"A","B",[],resources,true,"private"){MapId="M",MapVersionId="V"},default);await manager.TryAcquireAsync(step,task,vehicle,resources,default);}
 public ValueTask DisposeAsync(){Db.Dispose();return ValueTask.CompletedTask;}
}
