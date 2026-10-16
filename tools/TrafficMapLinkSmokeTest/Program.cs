using System.IO;
using System.IO.Pipes;
using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Infrastructure.Maps;
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
    static void Check(bool value,string name) { if(!value) throw new Exception(name);count++;Console.WriteLine("PASS "+name); }
    [STAThread] static void Main()
    {
        var sample=Backend().GetAwaiter().GetResult();var map=sample.Map;var request=sample.Lifecycle.Requests.Single(r=>r.StepId=="SELECT");var now=map.CapturedAt;
        var drawing=TrafficMapOverlayBuilder.Build(map,request,now);
        Check(drawing.Routes.Any(e=>e.Id=="E01") && drawing.Routes.Any(e=>e.Id=="E02"),"任务路线覆盖直线和曲线路段");
        Check(drawing.Routes.Single(e=>e.Id=="E02").Points.Count>2 && drawing.Routes.Single(e=>e.Id=="E02").Points.Max(p=>p.Y)-drawing.Routes.Single(e=>e.Id=="E02").Points.Min(p=>p.Y)>10,"圆弧按实际曲线绘制不是端点直线");
        Check(drawing.Areas.Any(a=>a.Id=="RH" && a.Stroke=="#EBA12C") && drawing.Areas.Any(a=>a.Id=="RB" && a.Stroke=="#EF5350"),"匹配版本的持有资源与阻塞资源分别高亮");
        Check(drawing.Nodes.Any(n=>n.Id=="RP@N0" && n.Hint.Contains("非区域边界")),"无多边形资源按节点关联显示不伪造区域边界");
        Check(drawing.Vehicles.Any(v=>v.Id=="AGV-A" && v.Fill=="#2389F7") && drawing.Vehicles.Any(v=>v.Id=="AGV-B" && v.Fill=="#EF5350"),"申请车辆及阻塞车辆用新鲜定位分别高亮");
        Check(drawing.Routes.Where(e=>e.Id=="E01" || e.Id=="E02").All(e=>e.Dash.Count>0),"等待请求显示候选虚线路线不表示已经授予通行");
        foreach(var wrong in new[]{request with{MapId=null},request with{MapVersionId="OLD"},request with{MapId="OTHER"}})
        {var d=TrafficMapOverlayBuilder.Build(map,wrong,now);Check(d.Routes.Count==0 && d.Areas.Count==0 && d.Vehicles.Count==0,"未知或不匹配版本不绘制请求关联");}
        var expired=TrafficMapOverlayBuilder.Build(map,request,now.AddMinutes(2));
        Check(expired.Routes.Count==0 && expired.Vehicles.Count==0 && expired.Notice.Contains("过期"),"过期地图快照清除关联及车辆");
        foreach(var invalid in new[]{map.Vehicles.Single(v=>v.VehicleId=="AGV-B") with{Quality=SignalQuality.Bad},map.Vehicles.Single(v=>v.VehicleId=="AGV-B") with{Connection=ConnectionState.Interrupted},
            map.Vehicles.Single(v=>v.VehicleId=="AGV-B") with{LastSuccessfulPollAt=now.AddMinutes(-2)},map.Vehicles.Single(v=>v.VehicleId=="AGV-B") with{LastSuccessfulPollAt=now.AddMinutes(2)},
            map.Vehicles.Single(v=>v.VehicleId=="AGV-B") with{LocalizationConfidence=0.1},map.Vehicles.Single(v=>v.VehicleId=="AGV-B") with{LocalizationConfidence=null},
            map.Vehicles.Single(v=>v.VehicleId=="AGV-B") with{X=double.NaN},map.Vehicles.Single(v=>v.VehicleId=="AGV-B") with{X=10000}})
        {var d=TrafficMapOverlayBuilder.Build(map with{Vehicles=map.Vehicles.Where(v=>v.VehicleId!="AGV-B").Append(invalid).ToArray()},request,now);
            Check(!d.Vehicles.Any(v=>v.Id=="AGV-B") && d.Notice.Contains("AGV-B"),"车辆质量或定位异常不画旧位置");}
        var missing=TrafficMapOverlayBuilder.Build(map with{Vehicles=[]},request,now);
        Check(missing.Vehicles.Count==0 && missing.Notice.Contains("位置缺失"),"缺失车辆不会画到站点或原点冒充实际位置");
        var mismatchBlock=TrafficMapOverlayBuilder.Build(map,request with{Blockers=request.Blockers.Select(b=>b with{MapVersionId="OLD"}).ToArray()},now);
        Check(!mismatchBlock.Vehicles.Any(v=>v.Id=="AGV-B") && mismatchBlock.Notice.Contains("阻塞方地图"),"阻塞方旧版本不映射到新地图");
        var mismatchHeld=TrafficMapOverlayBuilder.Build(map,request with{HeldResources=request.HeldResources.Select(h=>h with{MapVersionId="OLD"}).ToArray()},now);
        Check(!mismatchHeld.Areas.Any(a=>a.Id=="RH") && mismatchHeld.Notice.Contains("持有资源地图"),"旧版本持有资源不按新图形绘制");
        var absent=TrafficMapOverlayBuilder.Build(map,request with{EdgeIds=["MISSING"],ResourceIds=["MISSING-R"]},now);
        Check(absent.Notice.Contains("未找到路段") && absent.Notice.Contains("未配置资源"),"同版本缺失对象明确提示");
        Check(TrafficMapOverlayBuilder.Build(null,request).Edges.Count==0,"无运行地图清空拓扑");
        Check(TrafficMapOverlayBuilder.Build(map,null,now).Routes.Count==0,"无选择只展示基础拓扑");
        var app=new AgvDispatch.Wpf.App();app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var client=DispatchProxy.Create<ITrafficControlClient,MapTrafficClient>();var fake=(MapTrafficClient)client;
        fake.Map=map;fake.Lifecycle=sample.Lifecycle;fake.Resources=sample.Resources;
        var vm=new TrafficResourceMonitorViewModel(client);ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);
        vm.RefreshCommand.Execute();vm.SelectedTrafficRequest=vm.TrafficRequests.Single(r=>r.StepId=="SELECT");
        Check(vm.MapVehicles.Count==2 && vm.MapAreas.Count==2,"页面请求选择即时联动地图");
        vm.RefreshCommand.Execute();Check(fake.LastQuery?.CachedVersionId=="V1" && vm.MapEdges.Count==2,"相同版本只读缓存复用拓扑刷新车辆");
        vm.SelectedLease=vm.Leases.First(l=>l.StepId=="BLOCK");
        Check(vm.SelectedTrafficRequest?.StepId=="BLOCK" && vm.MapVehicles.Any(v=>v.Id=="AGV-B"),"选择占用联动对应原任务地图");
        vm.SelectedTrafficRequest=vm.TrafficRequests.Single(r=>r.StepId=="SELECT");
        Check(!vm.SubmitReleaseCommand.CanExecute(),"地图选择不授权释放路权");
        Check(vm.SelectedLease==null,"切换到另一步骤地图清除旧人工解锁选择");
        fake.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously); vm.RefreshCommand.Execute();
        Check(vm.IsBusy,"异步地图读取保持刷新忙状态");
        vm.SelectedTrafficRequest=vm.TrafficRequests.Single(r=>r.StepId=="BLOCK");
        fake.Pending.SetResult(map with{CapturedAt=DateTimeOffset.Now,Vehicles=map.Vehicles.Select(v=>v with{LastSuccessfulPollAt=DateTimeOffset.Now}).ToArray()});
        var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};var deadline=DateTime.UtcNow.AddSeconds(5);
        timer.Tick+=(_,_)=>{if(!vm.IsBusy || DateTime.UtcNow>deadline)frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();fake.Pending=null;
        Check(!vm.IsBusy && vm.SelectedTrafficRequest?.StepId=="BLOCK" && vm.MapVehicles.Count==1 && vm.MapVehicles.Single().Id=="AGV-B",
            "迟到地图响应按当前选择绘制不恢复上个任务高亮");
        vm.SelectedTrafficRequest=vm.TrafficRequests.Single(r=>r.StepId=="SELECT");
        var theme=new ThemeService();
        foreach(var spec in new[]{("light",1280,800),("dark",1280,800),("compact",1100,700)})
        {
            if(spec.Item1=="dark")theme.Toggle();
            var view=new TrafficResourceMonitorView{DataContext=vm,Width=spec.Item2,Height=spec.Item3,Background=(Brush)app.FindResource("AppBackgroundBrush")};
            ((TabItem)view.FindName("TrafficMapTab")).IsSelected=true;
            view.Measure(new Size(spec.Item2,spec.Item3));view.Arrange(new Rect(0,0,spec.Item2,spec.Item3));view.UpdateLayout();
            Check(((Canvas)view.FindName("TrafficMapCanvas")).ActualWidth==900,"地图画布绑定渲染 "+spec.Item1);
            Check(((TextBlock)view.FindName("TrafficMapNoticeText")).Text.Contains("V1"),"地图版本与逻辑/定位说明显示 "+spec.Item1);
            var bitmap=new RenderTargetBitmap(spec.Item2,spec.Item3,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));var file=Path.Combine(AppContext.BaseDirectory,"traffic-map-"+spec.Item1+".png");using var stream=File.Create(file);png.Save(stream);Console.WriteLine(file);
        }
        fake.Map=map with{VersionId="V2"};vm.RefreshCommand.Execute();
        Check(vm.MapRoutes.Count==0 && vm.MapVehicles.Count==0 && vm.TrafficMapNotice.Contains("不同"),"运行地图切换后立即去掉旧请求高亮");
        fake.Fail=true;vm.RefreshCommand.Execute();Check(vm.MapEdges.Count==0 && vm.MapVehicles.Count==0,"地图读取失败清空旧图形");
        fake.Fail=false;fake.Map=map with{Vehicles=map.Vehicles.Select(v=>v with{LastSuccessfulPollAt=DateTimeOffset.Now}).ToArray()};vm.RefreshCommand.Execute();Check(fake.LastQuery?.CachedVersionId==null && vm.MapVehicles.Count==2,"断连重读完整版本与新位置");
        Console.WriteLine($"Traffic map link: {count} checks passed. No production device I/O.");
    }
    static async Task<(TrafficMapSnapshot Map,TrafficLifecycleSnapshot Lifecycle,IReadOnlyList<TrafficResourceSnapshot> Resources)> Backend()
    {
        using var db=DatabaseFactory.Create(new DatabaseOptions{DatabasePath=Path.Combine(Path.GetTempPath(),"agv-traffic-map-"+Guid.NewGuid().ToString("N"),"test.db")});
        var definition=new MapDefinition("fixture","","LocalCartesian","m",
            [new("N0",0,0,MapNodeType.Station,true,["RP"]),new("N1",5,0,MapNodeType.Waypoint,true,[]),new("N2",10,0,MapNodeType.Station,true,[])],
            [new("E01","N0","N1",5,1,true,true,["RH"]),new MapEdge("E02","N1","N2",5,1,true,true,["RB"]){GeometryKind=MapEdgeGeometryKind.CircularArc,ControlPoints=[new(7.5,2)]}],
            [new("RH",TrafficResourceType.NarrowAisle,1,[new(-0.5,-0.8),new(4.5,-0.8),new(4.5,0.8),new(-0.5,0.8)]),
            new("RB",TrafficResourceType.Intersection,1,[new(5.5,-0.8),new(10.5,-0.8),new(10.5,2.5),new(5.5,2.5)]),new("RP",TrafficResourceType.Station)]);
        var catalog=new TrafficResourceCatalog(definition.Resources);await new DatabaseInitializer(db,catalog).InitializeAsync(default);
        foreach(var id in new[]{"A","B","LEGACY"})await db.Insertable(new DispatchTaskEntity{TaskId="TASK-"+id,StationId=id,Stage=10,CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now}).ExecuteCommandAsync();
        var runtime=new ActiveMapRuntime();runtime.Replace(new("MAP","V1",definition,DateTimeOffset.Now));
        var states=new SwitchVehicleCache();states.Items.Add(SwitchVehicleCache.Ready("AGV-A") with{X=1,Y=0});states.Items.Add(SwitchVehicleCache.Ready("AGV-B") with{X=10,Y=0});
        var control=new TrafficResourceControlService(db,catalog,runtime,states,new(){PositionMaxAgeSeconds=30});
        var manager=new TrafficResourceManager(db,catalog,new(TimeSpan.FromSeconds(30)));
        var planned=new TrafficRouteObservation("SELECT","TASK-A","AGV-A","N0","N2",["E01","E02"],["RH","RP","RB"],false,"申请任务路线资源。"){MapId="MAP",MapVersionId="V1"};
        await manager.RecordRouteAsync(planned,default);await manager.TryAcquireAsync("SELECT","TASK-A","AGV-A",["RH","RP"],default);
        await manager.RecordRouteAsync(planned with{StepId="BLOCK",TaskId="TASK-B",VehicleId="AGV-B",ResourceIds=["RB"]},default);
        await manager.TryAcquireAsync("BLOCK","TASK-B","AGV-B",["RB"],default);
        await manager.RecordRouteAsync(planned with{Waiting=true,Reason="等待阻塞车辆原步骤释放资源。"},default);
        var lifecycle=await control.GetLifecycleAsync(default);var resources=await control.GetResourcesAsync(default);
        Check(lifecycle.Requests.Single(r=>r.StepId=="SELECT").MapVersionId=="V1" && lifecycle.Requests.Single(r=>r.StepId=="SELECT").Blockers.Single().MapVersionId=="V1","规划版本与原占用版本进入关联快照");
        Check(db.Queryable<TrafficResourceLeaseEntity>().ToList().All(l=>l.MapId=="MAP" && l.MapVersionId=="V1"),"授予路权时保存原地图版本不由显示端推测");
        await manager.RenewTaskAsync("TASK-A",default);Check(db.Queryable<TrafficRequestEntity>().Where(r=>r.StepId=="SELECT").Single().MapVersionId=="V1","续期保留原规划版本");
        await manager.RecordRouteAsync(new("LEGACY","TASK-LEGACY","AGV-L","N0","N2",["E01"],["RH"],true,"旧记录"),default);
        Check((await control.GetLifecycleAsync(default)).Requests.Single(r=>r.StepId=="LEGACY").MapId==null,"旧记录不从当前运行地图补填版本");
        await manager.RecordRouteAsync(planned with{MapVersionId="V2"},default);
        var mismatch=false;try{await manager.TryAcquireAsync("SELECT","TASK-A","AGV-A",["RH"],default);}catch(InvalidOperationException){mismatch=true;}
        Check(mismatch && db.Queryable<TrafficResourceLeaseEntity>().Where(l=>l.OwnerStepId=="SELECT").ToList().All(l=>l.MapVersionId=="V1"),"旧占用不能续成另一地图版本的路权");
        await manager.RecordRouteAsync(planned with{Waiting=true},default);
        var first=await control.GetMapAsync(new(),default);Check(first.Definition!=null && first.Vehicles.Count==2,"首次读取运行拓扑及车辆缓存不访问设备");
        var cached=await control.GetMapAsync(new("MAP","V1"),default);Check(cached.Definition==null && cached.VersionId=="V1" && cached.Vehicles.Count==2,"同版本快照省略拓扑但仍提供车辆观测");
        runtime.Replace(new("MAP","V2",definition,DateTimeOffset.Now));Check((await control.GetMapAsync(new("MAP","V1"),default)).Definition!=null,"版本切换发送新拓扑");runtime.Replace(new("MAP","V1",definition,DateTimeOffset.Now));
        var args=typeof(TrafficControlNamedPipeServer).GetConstructors().Single().GetParameters().Select(p=>p.ParameterType==typeof(ITrafficResourceControlService)?(object)control:null).ToArray();
        var server=(TrafficControlNamedPipeServer)Activator.CreateInstance(typeof(TrafficControlNamedPipeServer),args)!;
        var dispatch=typeof(TrafficControlNamedPipeServer).GetMethod("DispatchAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var request=new TrafficControlPipeRequest(TrafficControlOperations.GetTrafficMap,JsonSerializer.SerializeToElement(new TrafficMapQuery()));
        var user=new TrafficUserContext("test","查看员",TrafficPermission.View);var response=await(Task<TrafficControlPipeResponse>)dispatch.Invoke(server,[user,request,CancellationToken.None])!;
        Check(response.Success && response.Payload.GetProperty("versionId").GetString()=="V1","交通地图IPC遵循查看权限返回版本快照");
        var denied=false;try{await(Task<TrafficControlPipeResponse>)dispatch.Invoke(server,[user with{Permissions=TrafficPermission.None},request,CancellationToken.None])!;}catch(UnauthorizedAccessException){denied=true;}Check(denied,"未授权不能读取交通地图");
        var name="TrafficMapLinkTest-"+Guid.NewGuid().ToString("N");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        var host=Task.Run(async()=>{await pipe.WaitForConnectionAsync(timeout.Token);var header=new byte[4];await pipe.ReadExactlyAsync(header,timeout.Token);var length=BinaryPrimitives.ReadInt32LittleEndian(header);if(length<1 || length>1048576)throw new Exception("frame");var data=new byte[length];await pipe.ReadExactlyAsync(data,timeout.Token);
            var input=JsonSerializer.Deserialize<TrafficControlPipeRequest>(data,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;var reply=await(Task<TrafficControlPipeResponse>)dispatch.Invoke(server,[user,input,timeout.Token])!;
            var output=JsonSerializer.SerializeToUtf8Bytes(reply,new JsonSerializerOptions(JsonSerializerDefaults.Web));BinaryPrimitives.WriteInt32LittleEndian(header,output.Length);await pipe.WriteAsync(header,timeout.Token);await pipe.WriteAsync(output,timeout.Token);await pipe.FlushAsync(timeout.Token);});
        using var client=new NamedPipeTrafficControlClient(name);var roundtrip=await client.GetTrafficMapAsync(new(),timeout.Token);await host;
        Check(roundtrip.Definition?.Edges.Single(e=>e.Id=="E02").GeometryKind==MapEdgeGeometryKind.CircularArc && roundtrip.Vehicles.Count==2,"隔离真实管道保留曲线与车辆字段");
        Check(db.Queryable<TrafficResourceLeaseEntity>().Count()==3 && db.Queryable<AgvCommandOutboxEntity>().Count()==0,"地图只读查询不改变占用不生成运输命令");
        return(first,lifecycle,resources);
    }
}
public class MapTrafficClient : DispatchProxy
{
    public TrafficMapSnapshot Map=null!;public TrafficLifecycleSnapshot Lifecycle=null!;public IReadOnlyList<TrafficResourceSnapshot> Resources=[];public bool Fail;public TrafficMapQuery? LastQuery;public TaskCompletionSource<TrafficMapSnapshot>? Pending;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
    {
        "GetSessionAsync"=>Task.FromResult(new TrafficControlSession("test","监控员",TrafficPermission.View)),"GetResourcesAsync"=>Task.FromResult(Resources),
        "GetRecentAuditsAsync"=>Task.FromResult<IReadOnlyList<TrafficResourceReleaseAuditSnapshot>>([]),"GetReleaseRequestsAsync"=>Task.FromResult<IReadOnlyList<ManualReleaseWorkflowItem>>([]),
        "GetTrafficFlowAsync"=>Task.FromResult(new TrafficFlowSnapshot(DateTimeOffset.Now,null,null,[],[])),"GetTrafficLifecycleAsync"=>Task.FromResult(Lifecycle),
        "GetTrafficMapAsync"=>Read((TrafficMapQuery)args![0]!),_=>throw new NotSupportedException(method?.Name)
    };
    Task<TrafficMapSnapshot> Read(TrafficMapQuery query){LastQuery=query;if(Pending!=null)return Pending.Task;if(Fail)throw new IOException("模拟地图读取中断");return Task.FromResult(Map with{CapturedAt=DateTimeOffset.Now,Definition=query.CachedMapId==Map.MapId && query.CachedVersionId==Map.VersionId?null:Map.Definition});}
}
