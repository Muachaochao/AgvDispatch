using System.IO;
using System.IO.Pipes;
using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;
internal static class Program
{
    static int count;
    static void Check(bool value,string name){if(!value)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
    [STAThread]static void Main()
    {
        AlertChecks().GetAwaiter().GetResult();var sample=Backend().GetAwaiter().GetResult();var app=new AgvDispatch.Wpf.App();app.InitializeComponent();
        var client=DispatchProxy.Create<ITrafficControlClient,WaitingClient>();var fake=(WaitingClient)client;fake.Data=sample;
        var vm=new TrafficResourceMonitorViewModel(client);ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);vm.RefreshCommand.Execute();
        Check(vm.WaitingQueue.Count==50 && vm.WaitingNotice.Contains("有效等待 210"),"等待队列显示全量计数及第一页");
        Check(vm.WaitingQueue[0].WaitingDurationText.Contains(":"),"持续时长正确格式化");
        vm.WaitingResourceInput="BLOCK";vm.WaitingPriorityInput="80";vm.WaitingSortInput=TrafficWaitingSort.PriorityThenWait;
        vm.RefreshCommand.Execute();Check(fake.LastQuery!.MinimumPriority==null,"未应用筛选不改变周期刷新条件");
        vm.ApplyWaitingFilterCommand.Execute();Check(fake.LastQuery!.MinimumPriority==80 && fake.LastQuery.BlockingResourceId=="BLOCK" && vm.WaitingQueue.All(r=>r.SourceSnapshot!.Priority>=80),"应用资源与最低优先级筛选");
        var selection=vm.WaitingQueue[0];vm.SelectedWaitingRequest=selection;
        Check(vm.SelectedTrafficRequest?.StepId==selection.StepId && vm.TrafficContext.Contains(selection.TaskId),"等待行联动任务详情及地图选择");
        Check(!vm.SubmitReleaseCommand.CanExecute(),"排队展示不启用解锁");
        vm.ResetWaitingFilterCommand.Execute();Check(fake.LastQuery!.MinimumPriority==null && vm.WaitingQueue.Count==50,"重置恢复全部等待");
        vm.NextWaitingPageCommand.Execute();Check(fake.LastQuery!.PageNumber==2 && vm.WaitingNotice.Contains("第2/5页"),"下一页覆盖前200记录以外的队列");
        vm.PreviousWaitingPageCommand.Execute();Check(fake.LastQuery!.PageNumber==1 && !vm.PreviousWaitingPageCommand.CanExecute(),"上一页恢复并限制页边界");
        vm.WaitingPriorityInput="invalid";var calls=fake.Calls;vm.ApplyWaitingFilterCommand.Execute();
        Check(fake.Calls==calls && vm.WaitingNotice.Contains("整数"),"无效优先级在提交前指出不请求后台");
        vm.ResetWaitingFilterCommand.Execute();
        fake.Data=sample.Take(2).ToArray();vm.NextWaitingPageCommand.Execute();Check(fake.LastQuery!.PageNumber==1 && vm.WaitingQueue.Count==2,"队列缩小时自动回到有效页面");fake.Data=sample;vm.RefreshCommand.Execute();
        var theme=new ThemeService();
        foreach(var spec in new[]{("light",1280,800),("dark",1280,800),("compact",1100,700)})
        {
            if(spec.Item1=="dark")theme.Toggle();var view=new TrafficResourceMonitorView{DataContext=vm,Width=spec.Item2,Height=spec.Item3,Background=(Brush)app.FindResource("AppBackgroundBrush")};
            ((TabItem)view.FindName("TrafficWaitingTab")).IsSelected=true;view.Measure(new Size(spec.Item2,spec.Item3));view.Arrange(new Rect(0,0,spec.Item2,spec.Item3));view.UpdateLayout();
            Check(((DataGrid)view.FindName("TrafficWaitingGrid")).Items.Count==50,"队列表渲染 "+spec.Item1);
            Check(((TextBlock)view.FindName("TrafficWaitingNotice")).Text.Contains("不改变调度"),"展示排序语义常驻说明 "+spec.Item1);
            var bitmap=new RenderTargetBitmap(spec.Item2,spec.Item3,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
            var path=Path.Combine(AppContext.BaseDirectory,"traffic-wait-"+spec.Item1+".png");using var file=File.Create(path);png.Save(file);Console.WriteLine(path);
        }
        fake.Fail=true;vm.RefreshCommand.Execute();Check(vm.WaitingQueue.Count==0 && !vm.NextWaitingPageCommand.CanExecute(),"读取失败清空旧队列和分页动作");
        Console.WriteLine($"Traffic waiting: {count} checks passed. Isolated data and fake clock only.");
    }
    static async Task AlertChecks()
    {
        using var db=DatabaseFactory.Create(new DatabaseOptions{DatabasePath=Path.Combine(Path.GetTempPath(),"agv-wait-alert-"+Guid.NewGuid().ToString("N"),"test.db")});
        var catalog=new TrafficResourceCatalog([new("R",TrafficResourceType.NarrowAisle),new("CAP2",TrafficResourceType.Intersection,2)]);
        await new DatabaseInitializer(db,catalog).InitializeAsync(default);var clock=new WaitingClock();var initial=clock.Now;
        var repository=new DispatchTaskRepository(db);
        async Task<(string Task,string Step)> Make(string station)
        {
            var task=await repository.TryCreateAsync(station,"DUMP",20,default);await repository.MarkAcknowledgedAsync(task.Task.TaskId,default);
            return(task.Task.TaskId,(await repository.GetStepsAsync(task.Task.TaskId,default))[0].StepId);
        }
        var owner=await Make("OWNER");var other=await Make("OTHER");var wait=await Make("WAIT");
        var manager=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(30)),clock);var control=new TrafficResourceControlService(db,catalog,clock:clock);
        await manager.TryAcquireAsync(owner.Step,owner.Task,"OWNER-AGV",["R","CAP2"],default);
        var route=new TrafficRouteObservation(wait.Step,wait.Task,"WAIT-AGV","A","B",["E"],["R"],true,"waiting fixture"){MapId="M",MapVersionId="V1"};
        await manager.RecordRouteAsync(route,default);
        async Task<TrafficRequestSnapshot> Read()=> (await control.GetWaitingAsync(new(),default)).Items.Single(r=>r.TaskId==wait.Task);
        var first=await Read();Check(first.BlockingObservationCurrent&&first.BlockingSince==initial&&first.BlockingStartKnown,"首次阻塞观察有已知UTC起点");
        var eventCount=db.Queryable<TrafficLifecycleEventEntity>().Count();
        for(var i=0;i<3;i++)await manager.RecordRouteAsync(route,default);
        Check(db.Queryable<TrafficLifecycleEventEntity>().Count()==eventCount,"连续观察同一阻塞不刷历史");
        clock.Now=initial.AddMilliseconds(59999);Check((await Read()).WaitingAlertLevel==TrafficWaitingAlertLevel.Normal,"60秒前不预警");
        clock.Now=initial.AddSeconds(60);Check((await Read()).WaitingAlertLevel==TrafficWaitingAlertLevel.Warning,"60秒边界进入预警");
        clock.Now=initial.AddMilliseconds(299999);Check((await Read()).WaitingAlertLevel==TrafficWaitingAlertLevel.Warning,"300秒前仍为预警");
        clock.Now=initial.AddSeconds(300);var critical=await Read();Check(critical.WaitingAlertLevel==TrafficWaitingAlertLevel.Critical,"300秒边界长时间等待");
        Check((await manager.GetUnavailableResourceIdsAsync(wait.Task,default)).Contains("R"),"长时间等待不自动解除资源占用");
        var row=TrafficRequestRow.From(critical);Check(row.WaitingCritical&&row.WaitingAlertText=="长时间等待"&&row.BlockingObservationText.Contains("持续阻塞"),"前端长时间等待标记与阻塞观察文字");
        clock.Now=initial.AddSeconds(-1);var anomaly=await Read();Check(anomaly.WaitingAlertLevel==TrafficWaitingAlertLevel.ClockAnomaly,"时钟回退不算零等待");
        Check(!TrafficRequestRow.From(anomaly).WaitingCritical,"时钟异常不显示为长时间等待");clock.Now=initial.AddSeconds(400);
        var custom=new TrafficResourceControlService(db,catalog,clock:clock,waitingAlerts:new(){WarningSeconds=500,CriticalSeconds=600});
        Check((await custom.GetWaitingAsync(new(),default)).Items.Single().WaitingAlertLevel==TrafficWaitingAlertLevel.Normal,"后台使用自定义阈值");
        foreach(var bad in new[]{new TrafficWaitingAlertOptions{WarningSeconds=0},new TrafficWaitingAlertOptions{WarningSeconds=300,CriticalSeconds=300},new TrafficWaitingAlertOptions{CriticalSeconds=604801}})
        {var rejected=false;try{bad.Validate();}catch(ArgumentException){rejected=true;}Check(rejected,"拒绝无效等待阈值");}
        await manager.ReleaseTaskAsync(owner.Task,default);var beforeRead=db.Queryable<TrafficLifecycleEventEntity>().Count();var clearBeforeObserve=await Read();
        Check(clearBeforeObserve.Blockers.Count==0&&!clearBeforeObserve.BlockingObservationCurrent&&clearBeforeObserve.BlockingSince==null,"只读查询不沿用已不存在阻塞的旧起点");
        Check(clearBeforeObserve.LastBlockingClearedAt==null&&db.Queryable<TrafficLifecycleEventEntity>().Count()==beforeRead,"只读查询不伪造解除时间或写历史");
        await manager.RecordRouteAsync(route with{Waiting=false},default);var cleared=await Read();
        Check(cleared.LastBlockingClearedAt==clock.Now&&cleared.BlockingSince==null,"调度再次观察时记录解除时间");
        Check(db.Queryable<TrafficLifecycleEventEntity>().Count(e=>e.StepId==wait.Step&&e.Kind=="BlockingCleared")==1,"阻塞解除历史只记录一次");
        var clearance=db.Queryable<TrafficLifecycleEventEntity>().Where(e=>e.StepId==wait.Step&&e.Kind=="BlockingCleared").Single();Check(clearance.ResourceIds.Contains("R"),"解除证据保留原阻塞资源");
        await manager.RecordRouteAsync(route,default);Check((await Read()).LastBlockingClearedAt==cleared.LastBlockingClearedAt,"重复无阻塞观察保持原解除时间");
        clock.Now=initial.AddSeconds(500);await manager.TryAcquireAsync(owner.Step,owner.Task,"OWNER-AGV",["R"],default);await manager.RecordRouteAsync(route,default);var episode=await Read();
        Check(episode.BlockingSince==clock.Now&&episode.WaitingSince==initial,"再次阻塞建立新观察起点而任务等待起点保持");
        clock.Now=initial.AddSeconds(450);var blockingClock=await Read();Check(!blockingClock.BlockingClockValid&&TrafficRequestRow.From(blockingClock).BlockingObservationText=="阻塞时钟异常","阻塞起点在未来时单独标记异常而不伪造持续阻塞");clock.Now=initial.AddSeconds(500);
        await manager.ReleaseTaskAsync(owner.Task,default);await manager.TryAcquireAsync(other.Step,other.Task,"OTHER-AGV",["R"],default);clock.Now=initial.AddSeconds(510);await manager.RecordRouteAsync(route,default);
        Check((await Read()).BlockingSince==episode.BlockingSince&&db.Queryable<TrafficLifecycleEventEntity>().Any(e=>e.Kind=="BlockersChanged"),"未观察到解除的连续阻塞换占用方时记录变化");
        var restarted=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(30)),clock);await restarted.RecordRouteAsync(route,default);Check((await Read()).BlockingSince==episode.BlockingSince,"管理器重建保留阻塞观察起点");
        clock.Now=initial.AddSeconds(520);await manager.RecordRouteAsync(route with{MapVersionId="V2"},default);Check((await Read()).BlockingSince==clock.Now,"地图上下文变化建立新阻塞片段");
        await db.Updateable<TrafficRequestEntity>().SetColumns(r=>new TrafficRequestEntity{BlockingKey=null,BlockingSinceUtc=null,BlockingStartKnown=null,WaitingSinceUtc=null,WaitingStartKnown=null}).Where(r=>r.StepId==wait.Step).ExecuteCommandAsync();
        Check((await Read()).WaitingAlertLevel==TrafficWaitingAlertLevel.Unknown,"旧记录未知等待起点不估算等级");
        await restarted.RecordRouteAsync(route with{MapVersionId="V2"},default);var legacy=await Read();Check(!legacy.BlockingStartKnown&&!legacy.WaitingStartKnown,"历史请求补记阻塞和等待为观察下界");
        var beforeRollback=db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId==wait.Step).Single();await manager.ReleaseTaskAsync(other.Task,default);
        await db.Ado.ExecuteCommandAsync("CREATE TRIGGER reject_clear BEFORE INSERT ON traffic_lifecycle_event WHEN NEW.Kind='BlockingCleared' BEGIN SELECT RAISE(ABORT,'clear-audit-failure'); END;");
        var rollback=false;try{await manager.RecordRouteAsync(route with{MapVersionId="V2"},default);}catch(InvalidOperationException){rollback=true;}
        var afterRollback=db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId==wait.Step).Single();Check(rollback&&afterRollback.BlockingKey==beforeRollback.BlockingKey&&afterRollback.LastBlockingClearedUtc==beforeRollback.LastBlockingClearedUtc,"解除审计失败时观察字段原子回滚");
        await db.Ado.ExecuteCommandAsync("DROP TRIGGER reject_clear;");
        await manager.TryAcquireAsync(owner.Step,owner.Task,"OWNER-AGV",["CAP2"],default);await manager.RecordRouteAsync(route with{ResourceIds=["CAP2"],MapVersionId="V2"},default);
        Check((await Read()).Blockers.Count==0&&(await Read()).BlockingSince==null,"容量2仅一个占用方不标记阻塞");
    }
    static async Task<TrafficRequestSnapshot[]> Backend()
    {
        using var db=DatabaseFactory.Create(new DatabaseOptions{DatabasePath=Path.Combine(Path.GetTempPath(),"agv-wait-"+Guid.NewGuid().ToString("N"),"test.db")});
        var catalog=new TrafficResourceCatalog([new("BLOCK",TrafficResourceType.NarrowAisle),new("OWN",TrafficResourceType.Station)]);await new DatabaseInitializer(db,catalog).InitializeAsync(default);
        var clock=new WaitingClock();var manager=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(30)),clock);
        var control=new TrafficResourceControlService(db,catalog,clock:clock);
        foreach(var id in new[]{"A","HOLDER"})await db.Insertable(new DispatchTaskEntity{TaskId=id,StationId=id,Stage=10,Priority=5,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommandAsync();
        await db.Insertable(new DispatchTaskStepEntity{StepId="A",TaskId="A",Sequence=1,Status=10,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommandAsync();
        var route=new TrafficRouteObservation("A","A","AGV-A","N0","N1",["E"],["BLOCK"],true,"资源占用等待。");
        await manager.RecordRouteAsync(route,default);var start=db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="A").Single().WaitingSinceUtc;
        Check(start==clock.Now.UtcDateTime,"等待起点记录真实进入等待的UTC时间");
        clock.Now=clock.Now.AddSeconds(30);await manager.RecordRouteAsync(route with{Reason="阻塞方信息变化"},default);
        Check(db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="A").Single().WaitingSinceUtc==start,"原因变化不重置等待起点");
        await manager.RecordRouteAsync(route with{Waiting=false,EdgeIds=["OTHER"],Reason="重试规划"},default);
        Check(db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="A").Single().State=="Waiting" && db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="A").Single().WaitingSinceUtc==start,"连续重规划不结束等待片段");
        var restarted=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(30)),clock);await restarted.RecordRouteAsync(route,default);
        Check(db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="A").Single().WaitingSinceUtc==start,"管理器重建保留持久化等待起点");
        await manager.TryAcquireAsync("A","A","AGV-A",["OWN"],default);
        Check(db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="A").Single().WaitingSinceUtc==null,"授予资源清除本次等待");
        clock.Now=clock.Now.AddSeconds(10);await manager.RecordRouteAsync(route,default);var second=db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="A").Single().WaitingSinceUtc;
        Check(second>start,"重新进入等待建立新的片段");
        clock.Now=clock.Now.AddSeconds(20);await manager.RenewTaskAsync("A",default);
        Check(db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="A").Single().WaitingSinceUtc==second && db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="A").Single().State=="Waiting","保持部分路权的续期不重置等待");
        var page=await control.GetWaitingAsync(new(),default);Check(page.Items.Single().WaitingSeconds==20 && page.Items.Single().Priority==5,"等待时长与业务任务优先级读取正确");
        clock.Now=clock.Now.AddMinutes(-1);page=await control.GetWaitingAsync(new(),default);
        Check(page.Items.Single().WaitingSeconds==null && !page.Items.Single().WaitingClockValid,"主机时间回退不显示负数或伪零等待");clock.Now=clock.Now.AddMinutes(1);
        await manager.RecordRouteAsync(route with{VehicleId="OTHER"},default);
        Check(db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="A").Single().WaitingSinceUtc==clock.Now.UtcDateTime,"候选车辆改变建立新等待上下文");
        await db.Updateable<TrafficRequestEntity>().SetColumns(r=>r.WaitingSinceUtc==(DateTime?)null).Where(r=>r.StepId=="A").ExecuteCommandAsync();
        page=await control.GetWaitingAsync(new(),default);Check(page.Items.Single().WaitingSince==null,"旧数据不从申请时间猜测等待起点");
        await manager.RecordRouteAsync(route with{VehicleId="OTHER"},default);page=await control.GetWaitingAsync(new(),default);
        Check(!page.Items.Single().WaitingStartKnown && TrafficRequestRow.From(page.Items.Single()).WaitingDurationText.StartsWith("至少"),"旧等待首次观察显示持续时长下界");
        var unchanged=db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="A").Single().WaitingSinceUtc;
        var beforeEvents=db.Queryable<TrafficLifecycleEventEntity>().Count();clock.Now=clock.Now.AddSeconds(5);await manager.RecordRouteAsync(route with{VehicleId="OTHER"},default);
        Check(db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="A").Single().WaitingSinceUtc==unchanged && db.Queryable<TrafficLifecycleEventEntity>().Count()==beforeEvents,"重复观察不重置起点也不重复写历史");
        clock.Now=clock.Now.AddSeconds(5);await manager.RecordRouteAsync(route with{VehicleId="OTHER",Source="NEW-SOURCE"},default);
        var episode=db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="A").Single();
        Check(episode.WaitingSinceUtc==clock.Now.UtcDateTime && db.Queryable<TrafficLifecycleEventEntity>().Count()==beforeEvents+1 && db.Queryable<TrafficLifecycleEventEntity>().Where(e=>e.WaitingSinceUtc==episode.WaitingSinceUtc).Any(),"起终点上下文变化重启等待并保存起点证据");
        await manager.ReleaseStepAsync("A",default);Check(db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="A").Single().WaitingSinceUtc==null,"释放清除等待片段");
        await db.Deleteable<TrafficRequestEntity>().ExecuteCommandAsync();
        await manager.TryAcquireAsync("HOLDER","HOLDER","AGV-H",["BLOCK"],default);
        for(var i=0;i<210;i++)
        {
            var id="Q"+i.ToString("D3");await db.Insertable(new DispatchTaskEntity{TaskId=id,StationId=id,Stage=10,Priority=i%101,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommandAsync();
            await db.Insertable(new DispatchTaskStepEntity{StepId=id,TaskId=id,Sequence=1,Status=10,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommandAsync();
            await db.Insertable(new TrafficRequestEntity{StepId=id,TaskId=id,VehicleId="AGV-"+i,State="Waiting",ResourceIds="[\"BLOCK\"]",Reason="等待窄道原任务释放",RequestedAt=DateTime.Now,UpdatedAt=DateTime.Now.AddSeconds(i),WaitingSinceUtc=clock.Now.AddSeconds(-i).UtcDateTime,WaitingStartKnown=true}).ExecuteCommandAsync();
        }
        page=await control.GetWaitingAsync(new(),default);Check(page.TotalWaiting==210 && page.Items[0].StepId=="Q209","最长等待从完整数据集排序不受最近200条限制");
        var next=await control.GetWaitingAsync(new(PageNumber:5),default);Check(next.Items.Count==10 && !page.Items.Select(r=>r.StepId).Intersect(next.Items.Select(r=>r.StepId)).Any(),"后续分页无重复且显示尾页");
        var priority=await control.GetWaitingAsync(new(Sort:TrafficWaitingSort.PriorityThenWait),default);Check(priority.Items[0].Priority==100 && priority.Items[0].StepId=="Q201","优先级相同按等待时长稳定排序");
        var high=await control.GetWaitingAsync(new("block",80),default);Check(high.TotalMatched==42 && high.Items.All(r=>r.Priority>=80),"阻塞资源精确匹配和最低优先级全量筛选");
        Check((await control.GetWaitingAsync(new("BLO"),default)).TotalMatched==0,"阻塞资源不做模糊子串匹配");
        await db.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==100).Where(t=>t.TaskId=="Q209").ExecuteCommandAsync();
        await db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>s.Status==50).Where(s=>s.StepId=="Q208").ExecuteCommandAsync();
        Check((await control.GetWaitingAsync(new(),default)).TotalWaiting==208,"故障任务及已完成步骤不混入有效等待");
        await db.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==10).Where(t=>t.TaskId=="Q209").ExecuteCommandAsync();await db.Updateable<DispatchTaskStepEntity>().SetColumns(s=>s.Status==10).Where(s=>s.StepId=="Q208").ExecuteCommandAsync();
        var leases=db.Queryable<TrafficResourceLeaseEntity>().Count();var events=db.Queryable<TrafficLifecycleEventEntity>().Count();
        await control.GetWaitingAsync(new(),default);await control.GetWaitingAsync(new("BLOCK"),default);
        Check(db.Queryable<TrafficResourceLeaseEntity>().Count()==leases && db.Queryable<TrafficLifecycleEventEntity>().Count()==events,"等待队列查询不修改路权或过程历史");
        foreach(var invalid in new[]{new TrafficWaitingQuery(MinimumPriority:-1),new TrafficWaitingQuery(PageNumber:0),new TrafficWaitingQuery(PageSize:201),new TrafficWaitingQuery(Sort:(TrafficWaitingSort)99)})
        {var failed=false;try{await control.GetWaitingAsync(invalid,default);}catch(ArgumentException){failed=true;}Check(failed,"后端拒绝无效筛选分页参数");}
        var args=typeof(TrafficControlNamedPipeServer).GetConstructors().Single().GetParameters().Select(p=>p.ParameterType==typeof(ITrafficResourceControlService)?(object)control:null).ToArray();
        var server=(TrafficControlNamedPipeServer)Activator.CreateInstance(typeof(TrafficControlNamedPipeServer),args)!;var dispatch=typeof(TrafficControlNamedPipeServer).GetMethod("DispatchAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var user=new TrafficUserContext("test","查看员",TrafficPermission.View);var request=new TrafficControlPipeRequest(TrafficControlOperations.GetTrafficWaiting,JsonSerializer.SerializeToElement(new TrafficWaitingQuery("BLOCK",80)));
        var reply=await(Task<TrafficControlPipeResponse>)dispatch.Invoke(server,[user,request,CancellationToken.None])!;Check(reply.Success && reply.Payload.GetProperty("totalMatched").GetInt32()==42,"等待筛选IPC路由与计数正确");
        var denied=false;try{await(Task<TrafficControlPipeResponse>)dispatch.Invoke(server,[user with{Permissions=TrafficPermission.None},request,CancellationToken.None])!;}catch(UnauthorizedAccessException){denied=true;}Check(denied,"查看队列沿用只读授权");
        var name="TrafficWaitTest-"+Guid.NewGuid().ToString("N");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));using var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        var host=Task.Run(async()=>{await pipe.WaitForConnectionAsync(timeout.Token);var header=new byte[4];await pipe.ReadExactlyAsync(header,timeout.Token);var length=BinaryPrimitives.ReadInt32LittleEndian(header);if(length<1 || length>1048576)throw new Exception("frame");var bytes=new byte[length];await pipe.ReadExactlyAsync(bytes,timeout.Token);var input=JsonSerializer.Deserialize<TrafficControlPipeRequest>(bytes,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var response=await(Task<TrafficControlPipeResponse>)dispatch.Invoke(server,[user,input,timeout.Token])!;var output=JsonSerializer.SerializeToUtf8Bytes(response,new JsonSerializerOptions(JsonSerializerDefaults.Web));BinaryPrimitives.WriteInt32LittleEndian(header,output.Length);await pipe.WriteAsync(header,timeout.Token);await pipe.WriteAsync(output,timeout.Token);await pipe.FlushAsync(timeout.Token);});
        using var realClient=new NamedPipeTrafficControlClient(name);var received=await realClient.GetTrafficWaitingAsync(new("BLOCK",80),timeout.Token);await host;
        Check(received.Items[0].WaitingSince?.Offset==TimeSpan.Zero && received.Items[0].WaitingSeconds!=null && received.Items[0].Priority>=80,"隔离真实管道保留UTC起点时长和优先级");
        return (await control.GetWaitingAsync(new(PageSize:200),default)).Items.Concat((await control.GetWaitingAsync(new(PageNumber:2,PageSize:200),default)).Items).ToArray();
    }
}
sealed class WaitingClock : TimeProvider{public DateTimeOffset Now=new(2026,10,13,6,0,0,TimeSpan.Zero);public override DateTimeOffset GetUtcNow()=>Now;}
public class WaitingClient : DispatchProxy
{
    public TrafficRequestSnapshot[] Data=[];public TrafficWaitingQuery? LastQuery;public int Calls;public bool Fail;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
    {
        "GetSessionAsync"=>Task.FromResult(new TrafficControlSession("test","监控员",TrafficPermission.View)),"GetResourcesAsync"=>Task.FromResult<IReadOnlyList<TrafficResourceSnapshot>>([]),
        "GetRecentAuditsAsync"=>Task.FromResult<IReadOnlyList<TrafficResourceReleaseAuditSnapshot>>([]),"GetReleaseRequestsAsync"=>Task.FromResult<IReadOnlyList<ManualReleaseWorkflowItem>>([]),
        "GetTrafficFlowAsync"=>Task.FromResult(new TrafficFlowSnapshot(DateTimeOffset.Now,null,null,[],[])),"GetTrafficLifecycleAsync"=>Task.FromResult(new TrafficLifecycleSnapshot(DateTimeOffset.Now,Data.Take(200).ToArray(),[])),
        "GetTrafficWaitingAsync"=>Read((TrafficWaitingQuery)args![0]!),_=>throw new NotSupportedException(method?.Name)
    };
    Task<TrafficWaitingPage> Read(TrafficWaitingQuery query)
    {
        LastQuery=query;Calls++;if(Fail)throw new IOException("模拟队列读取失败");
        var filtered=Data.Where(r=>(query.MinimumPriority==null || r.Priority>=query.MinimumPriority) && (string.IsNullOrWhiteSpace(query.BlockingResourceId) || r.Blockers.Any(b=>b.ResourceId==query.BlockingResourceId)));
        var order=query.Sort==TrafficWaitingSort.LongestWait?filtered.OrderByDescending(r=>r.WaitingSeconds):filtered.OrderByDescending(r=>r.Priority).ThenByDescending(r=>r.WaitingSeconds);
        var rows=order.ToArray();return Task.FromResult(new TrafficWaitingPage(DateTimeOffset.Now,Data.Length,rows.Length,query.PageNumber,query.PageSize,rows.Skip((query.PageNumber-1)*query.PageSize).Take(query.PageSize).ToArray(),["BLOCK"]));
    }
}
