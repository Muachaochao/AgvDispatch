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
using AgvDispatch.Domain.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static class Program
{
    private static int _checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); _checks++; Console.WriteLine("PASS " + name); }
    [STAThread] static void Main()
    {
        var samples = Backend().GetAwaiter().GetResult();
        var app = new AgvDispatch.Wpf.App(); app.InitializeComponent();
        var client = DispatchProxy.Create<ITrafficControlClient, TrafficClient>(); var fake = (TrafficClient)client;
        fake.Snapshot = samples.Lifecycle; fake.Resources = samples.Resources;
        var vm = new TrafficResourceMonitorViewModel(client);
        ViewModelLocationProvider.SetDefaultViewModelFactory(_ => vm);
        vm.RefreshCommand.Execute();
        Check(vm.TrafficRequests.Count > 0 && vm.TrafficEvents.Count > 0, "交通页面加载路权请求及过程历史");
        vm.SelectedTrafficRequest = vm.TrafficRequests.Single(r => r.StepId == "STEP-B");
        Check(vm.TrafficContext.Contains("TASK-B") && vm.TrafficContext.Contains("AGV-B") && vm.TrafficContext.Contains("E-01") && vm.TrafficContext.Contains("TASK-A/AGV-A/STEP-A"), "选中请求关联申请与阻塞任务车辆路段资源");
        Check(!vm.SubmitReleaseCommand.CanExecute(), "只读交通查看不会启用人工解锁");
        var selected = vm.SelectedTrafficRequest.StepId; vm.RefreshCommand.Execute();
        Check(vm.SelectedTrafficRequest?.StepId == selected, "刷新保持路权请求选择");
        var theme = new ThemeService();
        foreach (var spec in new[] { ("light",1280,800), ("dark",1280,800), ("compact",1100,700) })
        {
            if (spec.Item1 == "dark") theme.Toggle();
            var view = new TrafficResourceMonitorView { DataContext = vm, Width = spec.Item2, Height = spec.Item3, Background = (Brush)app.FindResource("AppBackgroundBrush") };
            ((TabControl)view.FindName("TrafficOccupancyTabs")).SelectedIndex = 1;
            view.Measure(new Size(spec.Item2, spec.Item3)); view.Arrange(new Rect(0,0,spec.Item2,spec.Item3)); view.UpdateLayout();
            Check(((DataGrid)view.FindName("TrafficRequestsGrid")).Items.Count == vm.TrafficRequests.Count, "请求表绑定渲染：" + spec.Item1);
            Check(((TextBlock)view.FindName("TrafficContextText")).Text.Contains("TASK-A/AGV-A/STEP-A"), "关联详情绑定渲染：" + spec.Item1);
            var bitmap = new RenderTargetBitmap(spec.Item2,spec.Item3,96,96,PixelFormats.Pbgra32); bitmap.Render(view);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            var file = Path.Combine(AppContext.BaseDirectory,"traffic-"+spec.Item1+".png"); using var stream = File.Create(file); png.Save(stream); Console.WriteLine(file);
        }
        Console.WriteLine($"Traffic lifecycle: {_checks} checks passed. Isolated database only.");
    }
    static async Task<(TrafficLifecycleSnapshot Lifecycle, IReadOnlyList<TrafficResourceSnapshot> Resources)> Backend()
    {
        using var db = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = Path.Combine(Path.GetTempPath(),"agv-traffic-lifecycle-"+Guid.NewGuid().ToString("N"),"test.db") });
        var catalog = new TrafficResourceCatalog([new("NARROW",TrafficResourceType.NarrowAisle),new("CROSS",TrafficResourceType.Intersection),new("CAP2",TrafficResourceType.Intersection,2)]);
        await new DatabaseInitializer(db,catalog).InitializeAsync(default);
        foreach (var id in new[] { "A","B","C","D","LEGACY" })
            await db.Insertable(new DispatchTaskEntity { TaskId = "TASK-"+id, StationId="STATION-"+id, Stage=10, CreatedAt=DateTime.Now, UpdatedAt=DateTime.Now }).ExecuteCommandAsync();
        var manager = new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(30)));
        var control = new TrafficResourceControlService(db,catalog);
        var routeA = new TrafficRouteObservation("STEP-A","TASK-A","AGV-A","PICK","DUMP",["E-01"],["NARROW"],false,"开始申请运输路径路权。");
        await manager.RecordRouteAsync(routeA,default);
        Check((await manager.TryAcquireAsync("STEP-A","TASK-A","AGV-A",["NARROW"],default)).Acquired, "首任务取得窄道路权");
        Check((await manager.TryAcquireAsync("STEP-A","TASK-A","AGV-A",["NARROW"],default)).Acquired && db.Queryable<TrafficResourceLeaseEntity>().Count()==1, "重复申请复用原步骤占用");
        var eventsBefore = db.Queryable<TrafficLifecycleEventEntity>().Count();
        await manager.RenewTaskAsync("TASK-A",default); await manager.RenewTaskAsync("TASK-A",default);
        Check(db.Queryable<TrafficLifecycleEventEntity>().Count()==eventsBefore+1, "续期仅首次记录保持状态不按轮询刷历史");
        var leased = db.Queryable<TrafficResourceLeaseEntity>().Single();
        await db.Updateable<TrafficResourceLeaseEntity>().SetColumns(l=>l.ExpiresAt==DateTime.Now.AddMinutes(-1)).Where(l=>l.LeaseId==leased.LeaseId).ExecuteCommandAsync();
        Check((await manager.GetUnavailableResourceIdsAsync("TASK-B",default)).Contains("NARROW"), "心跳过期仍保持路权");
        await manager.RecordRouteAsync(routeA with { StepId="STEP-B",TaskId="TASK-B",VehicleId="AGV-B",ResourceIds=["CROSS","NARROW"] },default);
        var blocked = await manager.TryAcquireAsync("STEP-B","TASK-B","AGV-B",["CROSS","NARROW"],default);
        Check(!blocked.Acquired && blocked.BlockingTaskId=="TASK-A", "冲突返回原阻塞任务");
        Check(!db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.TaskId=="TASK-B"), "多资源申请冲突不留下部分占用");
        var snapshot = await control.GetLifecycleAsync(default); var waiting=snapshot.Requests.Single(r=>r.StepId=="STEP-B");
        Check(waiting.State=="Waiting" && waiting.EdgeIds.Contains("E-01") && waiting.Blockers.Single().VehicleId=="AGV-A" && waiting.Blockers.Single().HeartbeatExpired, "等待关联原车辆原步骤及过期仍锁定原因");
        var sampleResources = await control.GetResourcesAsync(default); var sampleLifecycle=snapshot;
        var conflict = false; try { await manager.TryAcquireAsync("STEP-A","TASK-A","OTHER",["NARROW"],default); } catch (InvalidOperationException) { conflict=true; }
        Check(conflict && db.Queryable<TrafficResourceLeaseEntity>().Single().VehicleId=="AGV-A", "步骤复用不能替换占用车辆");
        await manager.TryAcquireAsync("STEP-C","TASK-C","AGV-C",["CAP2"],default);
        await manager.TryAcquireAsync("STEP-D","TASK-D","AGV-D",["CAP2"],default);
        Check((await manager.GetUnavailableResourceIdsAsync("TASK-B",default)).Contains("CAP2"), "容量2资源两任务满额后阻塞");
        await manager.TryAcquireAsync("STEP-B2","TASK-B","AGV-B",["CAP2"],default);
        Check((await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="STEP-B2").Blockers.Count==2, "容量冲突展示全部占用任务车辆");
        await db.Ado.ExecuteCommandAsync("CREATE TRIGGER reject_traffic_release BEFORE INSERT ON traffic_lifecycle_event WHEN NEW.Kind='Released' BEGIN SELECT RAISE(ABORT,'audit-failure'); END;");
        var rolledBack=false; try { await manager.ReleaseStepAsync("STEP-C",default); } catch(InvalidOperationException) { rolledBack=true; }
        Check(rolledBack && db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.OwnerStepId=="STEP-C"), "正常释放记录失败时占用原子回滚");
        await db.Ado.ExecuteCommandAsync("DROP TRIGGER reject_traffic_release;");
        await manager.TryAcquireAsync("STEP-A2","TASK-A","AGV-A",["NARROW"],default);
        await manager.TryAcquireAsync("STEP-A2","TASK-A","AGV-A",["NARROW","CROSS"],default);
        var transitionReleased = await manager.ReleaseStepExceptResourcesAsync("STEP-A2",
            ["NARROW"], "滚动窗口续约，仅释放已通过资源。", default);
        Check(transitionReleased == 1 &&
            db.Queryable<TrafficResourceLeaseEntity>().Any(l => l.OwnerStepId == "STEP-A2" && l.ResourceId == "NARROW") &&
            !db.Queryable<TrafficResourceLeaseEntity>().Any(l => l.OwnerStepId == "STEP-A2" && l.ResourceId == "CROSS"),
            "滚动窗口原子保留下一窗资源并释放已完整通过资源");
        await manager.ReleaseTaskExceptStepAsync("TASK-A","STEP-A2",default);
        Check(!db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.OwnerStepId=="STEP-A") && db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.OwnerStepId=="STEP-A2"), "新步骤取得路权后释放旧步骤并保留新占用");
        Check((await control.GetLifecycleAsync(default)).Events.Any(e=>e.StepId=="STEP-A" && e.Kind=="Released" && e.Reason.Contains("前序")), "正常交接释放保存独立过程依据");
        await db.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==100).Where(t=>t.TaskId=="TASK-A").ExecuteCommandAsync();
        await manager.GetUnavailableResourceIdsAsync("TASK-B",default);
        Check(db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.OwnerStepId=="STEP-A2") && (await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="STEP-A2").State=="Retained", "故障任务保留路权且展示异常保留");
        await db.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==90).Where(t=>t.TaskId=="TASK-A").ExecuteCommandAsync();
        await manager.GetUnavailableResourceIdsAsync("TASK-B",default);
        Check(db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.TaskId=="TASK-A"), "只读候选查询不隐式释放已完成任务路权");
        await manager.PrepareDispatchCycleAsync(default);
        Check(!db.Queryable<TrafficResourceLeaseEntity>().Any(l=>l.TaskId=="TASK-A") && (await control.GetLifecycleAsync(default)).Events.Any(e=>e.StepId=="STEP-A2" && e.Kind=="Released"), "已完成任务正常释放并保存过程记录");
        Check((await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="STEP-B").Reason.Contains("重新规划"), "阻塞方释放后等待下一轮调度不在查询时自动授予");
        Check((await manager.TryAcquireAsync("STEP-B","TASK-B","AGV-B",["CROSS","NARROW"],default)).Acquired, "冲突解除后原请求可重新取得完整资源");
        var beforeRelease = db.Queryable<TrafficLifecycleEventEntity>().Where(e => e.StepId == "STEP-B")
            .Select(e => e.EventId).ToList().ToHashSet();
        await manager.ReleaseStepAsync("STEP-B",default);
        var releaseEvents = db.Queryable<TrafficLifecycleEventEntity>().Where(e => e.StepId == "STEP-B")
            .ToList().Where(e => !beforeRelease.Contains(e.EventId)).ToArray();
        await manager.ReleaseStepAsync("STEP-B",default);
        Check(releaseEvents.Select(e => e.Kind).Order().SequenceEqual(new[] { "Released", "ResourceReleased" }) &&
            db.Queryable<TrafficLifecycleEventEntity>().Where(e => e.StepId == "STEP-B").Count() == beforeRelease.Count + releaseEvents.Length,
            "准备回滚仅记录一次物理资源释放和一次请求终态，重试不增记");
        await manager.RecordRouteAsync(routeA with { StepId="STEP-B",TaskId="TASK-B",VehicleId="AGV-B",EdgeIds=["DETOUR-EDGE"],ResourceIds=["CROSS"],Reason="冲突解除后选择绕行路径。" },default);
        await manager.TryAcquireAsync("STEP-B","TASK-B","AGV-B",["CROSS"],default);
        Check((await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="STEP-B").EdgeIds.SequenceEqual(new[]{"DETOUR-EDGE"}), "新的可行路径替换旧等待诊断路段");
        await manager.RecordRouteAsync(routeA with { StepId="STEP-B",TaskId="TASK-B",VehicleId="AGV-B",ResourceIds=["CROSS","CAP2"],Waiting=true,Reason="扩展路径等待容量资源。" },default);
        var mixed=(await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="STEP-B");
        Check(mixed.State=="Waiting" && mixed.HeldResourceIds.Contains("CROSS") && mixed.Blockers.Count==2, "等待扩展资源时展示既有保持占用");
        await manager.RecordRouteAsync(new("NO-PATH","TASK-B","AGV-B","A","B",[],[],true,"地图路径不可达，核对方向和启用状态。"),default);
        Check((await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="NO-PATH").Reason.Contains("不可达"), "无静态路径保留明确原因不伪造资源阻塞");
        await manager.RecordRouteAsync(new("FREE","TASK-B","AGV-B","A","B",["FREE-EDGE"],[],false,"无资源路径"),default);
        await manager.TryAcquireAsync("FREE","TASK-B","AGV-B",[],default);
        Check((await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="FREE").Source=="A" && (await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="FREE").State=="NotRequired", "无需资源路径保留起终点且无占用");
        await db.Insertable(new TrafficResourceLeaseEntity { LeaseId="LEGACY",ResourceId="NARROW",OwnerStepId="LEGACY-STEP",TaskId="TASK-LEGACY",VehicleId="AGV-L",AcquiredAt=DateTime.Now,RenewedAt=DateTime.Now,ExpiresAt=DateTime.Now.AddMinutes(1) }).ExecuteCommandAsync();
        Check((await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="LEGACY-STEP").Reason.Contains("不反推"), "旧占用可关联展示但不反推历史路径");
        var planner=new TestTrafficPlanner();
        using var coordinator=new DispatchTaskStepCoordinator(NullLogger<DispatchTaskStepCoordinator>.Instance, new(),
            new DispatchOrchestratorOptions { StationPickupSites = new() { ["STATION-B"]="PICK" }, DumperWaitingSite="WAIT" }, new(),
            null!,null!,null!,null!,planner,manager,null!);
        var prepare=typeof(DispatchTaskStepCoordinator).GetMethod("PrepareTransportAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var work=new DispatchTaskStepWorkItem("TASK-B","STATION-B","AGV-B",DispatchTaskStage.GoingToDumper,"SERVICE-WAIT",3,
            DispatchTaskStepType.DeliverFullBoxToDumper,DispatchTaskStepStatus.Ready,"PICK","WAIT",DateTimeOffset.Now);
        await (Task)prepare.Invoke(coordinator,[work,CancellationToken.None])!;
        var actualWait=(await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="SERVICE-WAIT");
        Check(actualWait.State=="Waiting" && actualWait.EdgeIds.Contains("E-PLAN") && actualWait.Blockers.Single().TaskId=="TASK-LEGACY",
            "运输协调器规划被阻塞时记录真实诊断路径及持有方");
        Check(!db.Queryable<AgvCommandOutboxEntity>().Any(c=>c.StepId=="SERVICE-WAIT"), "用于等待说明的诊断路径不生成运输命令");
        planner.NoPath=true; await (Task)prepare.Invoke(coordinator,[work,CancellationToken.None])!;
        Check((await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="SERVICE-WAIT").Reason.Contains("不可达"), "实际协调器区分地图无路径和资源占用");
        var readCount=db.Queryable<TrafficLifecycleEventEntity>().Count(); await control.GetLifecycleAsync(default); await control.GetLifecycleAsync(default);
        Check(db.Queryable<TrafficLifecycleEventEntity>().Count()==readCount, "监控查询只读不授予或释放资源");
        var args=typeof(TrafficControlNamedPipeServer).GetConstructors().Single().GetParameters().Select(p=>p.ParameterType==typeof(ITrafficResourceControlService)?(object)control:null).ToArray();
        var server=(TrafficControlNamedPipeServer)Activator.CreateInstance(typeof(TrafficControlNamedPipeServer),args)!;
        var dispatch=typeof(TrafficControlNamedPipeServer).GetMethod("DispatchAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var request=new TrafficControlPipeRequest(TrafficControlOperations.GetTrafficLifecycle,JsonSerializer.SerializeToElement(new{}));
        var reply=await (Task<TrafficControlPipeResponse>)dispatch.Invoke(server,[new TrafficUserContext("test","观察员",TrafficPermission.View),request,CancellationToken.None])!;
        Check(reply.Success && reply.Payload.GetProperty("requests").GetArrayLength()>0, "后台IPC路由返回身份关联过程数据");
        var denied=false; try { await (Task<TrafficControlPipeResponse>)dispatch.Invoke(server,[new TrafficUserContext("test","未授权",TrafficPermission.None),request,CancellationToken.None])!; } catch(UnauthorizedAccessException) { denied=true; }
        Check(denied, "路权过程只读IPC沿用查看权限");
        var pipeName="AgvTrafficLifecycleTest-"+Guid.NewGuid().ToString("N");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        var host=Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(timeout.Token); var header=new byte[4]; await pipe.ReadExactlyAsync(header,timeout.Token);
            var length=BinaryPrimitives.ReadInt32LittleEndian(header); if(length<1 || length>1024*1024) throw new Exception("invalid test frame");
            var input=new byte[length]; await pipe.ReadExactlyAsync(input,timeout.Token);
            var received=JsonSerializer.Deserialize<TrafficControlPipeRequest>(input,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var response=await (Task<TrafficControlPipeResponse>)dispatch.Invoke(server,[new TrafficUserContext("test","观察员",TrafficPermission.View),received,timeout.Token])!;
            var output=JsonSerializer.SerializeToUtf8Bytes(response,new JsonSerializerOptions(JsonSerializerDefaults.Web));
            BinaryPrimitives.WriteInt32LittleEndian(header,output.Length); await pipe.WriteAsync(header,timeout.Token); await pipe.WriteAsync(output,timeout.Token); await pipe.FlushAsync(timeout.Token);
        });
        using var realClient=new NamedPipeTrafficControlClient(pipeName);
        var roundtrip=await realClient.GetTrafficLifecycleAsync(timeout.Token); await host;
        Check(roundtrip.Requests.Single(r=>r.StepId=="STEP-B").HeldResourceIds.Contains("CROSS") && roundtrip.Events.Any(e=>e.Kind=="Released"),
            "隔离真实命名管道往返保留当前持有资源与正常释放事件");
        return (sampleLifecycle,sampleResources);
    }
}
public class TrafficClient : DispatchProxy
{
    public TrafficLifecycleSnapshot Snapshot = new(DateTimeOffset.Now,[],[]);
    public IReadOnlyList<TrafficResourceSnapshot> Resources=[];
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
    {
        "GetSessionAsync"=>Task.FromResult(new TrafficControlSession("test","监控员",TrafficPermission.View)),
        "GetResourcesAsync"=>Task.FromResult(Resources),
        "GetRecentAuditsAsync"=>Task.FromResult<IReadOnlyList<TrafficResourceReleaseAuditSnapshot>>([]),
        "GetReleaseRequestsAsync"=>Task.FromResult<IReadOnlyList<ManualReleaseWorkflowItem>>([]),
        "GetTrafficFlowAsync"=>Task.FromResult(new TrafficFlowSnapshot(DateTimeOffset.Now,null,null,[],[])),
        "GetTrafficLifecycleAsync"=>Task.FromResult(Snapshot),
        _=>throw new NotSupportedException(method?.Name)
    };
}

sealed class TestTrafficPlanner : IRoutePlanner
{
    public bool NoPath;
    public RoutePlan? Plan(string source,string destination,IReadOnlySet<string> unavailable) => NoPath || unavailable.Contains("NARROW") ? null : new([source,destination],["E-PLAN"],["NARROW"],1);
}
