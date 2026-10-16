using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Service;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Prism.Mvvm;

internal static class Program
{
    static int checks;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
    [STAThread] static void Main()
    {
        var sample=Backend().GetAwaiter().GetResult();
        var app=new AgvDispatch.Wpf.App();app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var client=DispatchProxy.Create<ITrafficControlClient,UiClient>();var fake=(UiClient)client;
        var vm=new TrafficResourceMonitorViewModel(client);
        typeof(TrafficResourceMonitorViewModel).GetField("_session",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(vm,new TrafficControlSession("test","view",TrafficPermission.View));
        TrafficRequestRow Row(string step,string car)=>new(step,"T"+step,car,"A→B","AB","R","占用保持","","",DateTime.Now,DateTime.Now);
        vm.TrafficRequests.Add(Row("S1","CAR1"));vm.TrafficRequests.Add(Row("S2","CAR2"));
        vm.GeometryFirst=vm.TrafficRequests[0];vm.GeometrySecond=vm.TrafficRequests[1];
        ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);
        fake.Result=sample with{CheckedAt=DateTimeOffset.UtcNow,ValidUntil=DateTimeOffset.UtcNow.AddSeconds(5)};
        vm.CheckGeometryCommand.Execute();
        Check(fake.LastQuery==new TrafficGeometryPreviewQuery("S1","S2")&&vm.GeometryConflicts.Count==1,"界面请求只发送原步骤编号并展示交叠");
        Check(vm.GeometryContext.Contains("CAR1")&&vm.GeometryContext.Contains("不授予"),"展示核对的车辆地图与只读含义");
        foreach(var spec in new[]{("light",1280,800),("dark",1280,800),("compact",1100,700)})
        {
            if(spec.Item1=="dark")new ThemeService().Toggle();
            var view=new TrafficResourceMonitorView{DataContext=vm,Width=spec.Item2,Height=spec.Item3};
            ((TabItem)view.FindName("TrafficGeometryTab")).IsSelected=true;
            view.Measure(new Size(spec.Item2,spec.Item3));view.Arrange(new Rect(0,0,spec.Item2,spec.Item3));view.UpdateLayout();
            var bmp=new RenderTargetBitmap(spec.Item2,spec.Item3,96,96,PixelFormats.Pbgra32);bmp.Render(view);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(Path.Combine(AppContext.BaseDirectory,"geometry-"+spec.Item1+".png"));png.Save(file);
            Check(view.ActualWidth==spec.Item2,"只读几何页签渲染 "+spec.Item1);
            Check(((DataGrid)view.FindName("TrafficGeometryConflictsGrid")).ActualHeight>=85,"冲突列表在当前窗口有足够可见空间 "+spec.Item1);
        }
        fake.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);vm.CheckGeometryCommand.Execute();
        vm.GeometrySecond=vm.TrafficRequests[0];fake.Pending.SetResult(fake.Result);Pump(100);
        Check(vm.GeometryConflicts.Count==0&&!vm.CheckGeometryCommand.CanExecute(),"选项变化忽略迟到结果且同一步骤禁止检查");
        fake.Pending=null;vm.GeometrySecond=vm.TrafficRequests[1];fake.Result=fake.Result with{ValidUntil=DateTimeOffset.UtcNow.AddSeconds(-1)};vm.CheckGeometryCommand.Execute();
        Check(vm.GeometryConflicts.Count==0&&vm.GeometryNotice.Contains("过期"),"界面拒绝已过期返回");
        fake.Result=sample with{CheckedAt=DateTimeOffset.UtcNow,ValidUntil=DateTimeOffset.UtcNow.AddMilliseconds(350)};vm.CheckGeometryCommand.Execute();Pump(600);
        Check(vm.GeometryConflicts.Count==0&&vm.GeometryNotice.Contains("过期"),"到期自动清除旧冲突");
        fake.Result=sample with{Assessment=null,ValidUntil=null,Reason="缺少核定包络"};vm.CheckGeometryCommand.Execute();
        Check(vm.GeometryNotice.Contains("无法评估")&&vm.GeometryConflicts.Count==0,"缺真实源没有伪造安全结果");
        fake.Fail=true;vm.CheckGeometryCommand.Execute();Check(vm.GeometryNotice.Contains("不可用")&&vm.GeometryConflicts.Count==0,"断连清空旧结果");
        fake.Fail=false;fake.Result=sample with{CheckedAt=DateTimeOffset.UtcNow,ValidUntil=DateTimeOffset.UtcNow.AddSeconds(5)};vm.CheckGeometryCommand.Execute();vm.OnNavigatedFrom(null!);
        Check(vm.GeometryConflicts.Count==0,"离开页面撤销本次展示");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"context-result.json"),JsonSerializer.Serialize(new{Checks=checks,Scope="private database, simulated certified profiles, real Named Pipe server/client, WPF render; no field equipment"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS {checks} context and UI checks.");
    }
    static void Pump(int milliseconds){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    static async Task<TrafficGeometryPreviewSnapshot> Backend()
    {
        var now=DateTimeOffset.UtcNow;var root=Path.Combine(Path.GetTempPath(),"geometry-context-"+Guid.NewGuid().ToString("N"));
        using var db=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(root,"private.db")});
        db.CodeFirst.InitTables<DispatchTaskEntity,DispatchTaskStepEntity,AgvCommandOutboxEntity,TrafficRequestEntity,TrafficResourceLeaseEntity>();
        var map=new MapDefinition("map","","LocalCartesian","m",[new("A",0,0,MapNodeType.Waypoint,true,[]),new("B",2,0,MapNodeType.Waypoint,true,[]),new("C",1,-1,MapNodeType.Waypoint,true,[]),new("D",1,1,MapNodeType.Waypoint,true,[])],[new("AB","A","B",2,1,true,true,["R1"]),new("CD","C","D",2,1,true,true,["R2"])],[new("R1",TrafficResourceType.NarrowAisle),new("R2",TrafficResourceType.NarrowAisle)]);
        var runtime=new ActiveMapRuntime();var barrier=new Barrier();var gate=new MapExecutionGate(barrier);
        var profiles=new Profiles{Until=now.AddMinutes(5)};var policy=new TrafficGeometryPreviewPolicy{Enabled=true};
        var inspector=new TrafficSegmentReleaseInspector(db,runtime,gate,new());
        var service=new TrafficGeometryPreviewService(inspector,gate,policy,profiles);
        var query=new TrafficGeometryPreviewQuery("S1","S2");
        void Reset()
        {
            db.Deleteable<TrafficResourceLeaseEntity>().ExecuteCommand();db.Deleteable<TrafficRequestEntity>().ExecuteCommand();db.Deleteable<AgvCommandOutboxEntity>().ExecuteCommand();db.Deleteable<DispatchTaskStepEntity>().ExecuteCommand();db.Deleteable<DispatchTaskEntity>().ExecuteCommand();
            runtime.Replace(new("M","V",map,now));gate.SetMode(MapExecutionMode.Running);barrier.IsOpen=true;profiles.Change=null;profiles.Null=false;profiles.Fail=false;profiles.Hang=false;profiles.Revision="R";profiles.Uncertainty=.05;profiles.Until=DateTimeOffset.UtcNow.AddMinutes(5);profiles.Reads=0;profiles.Verified=true;policy.Enabled=true;
            for(var i=1;i<=2;i++)
            {
                var from=i==1?"A":"C";var to=i==1?"B":"D";var edge=from+to;var step="S"+i;var task="T"+i;var car="CAR"+i;var command="CMD"+i;
                var payload=JsonSerializer.Serialize(new AgvTransportCommandPayload(step,car,from,to,[from,to],null),new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var request=new TrafficRequestEntity{TaskId=task,StepId=step,VehicleId=car,Source=from,Destination=to,EdgeIds=JsonSerializer.Serialize(new[]{edge}),ResourceIds=JsonSerializer.Serialize(new[]{"R"+i}),State="Held",MapId="M",MapVersionId="V"};
                var frozen=TransportRouteSnapshots.Serialize(TransportRouteSnapshots.Capture(task,step,command,car,payload,runtime.Current!,request));
                db.Insertable(new DispatchTaskEntity{TaskId=task,StationId="ST"+i,VehicleId=car,VendorTaskId=step,Stage=(int)DispatchTaskStage.GoingToPickup}).ExecuteCommand();
                db.Insertable(new DispatchTaskStepEntity{TaskId=task,StepId=step,VehicleId=car,VendorTaskId=step,StepType=(int)DispatchTaskStepType.GoToPickup,Status=(int)DispatchTaskStepStatus.Executing,SourceSite=from,DestinationSite=to}).ExecuteCommand();
                db.Insertable(new AgvCommandOutboxEntity{TaskId=task,StepId=step,CommandId=command,IdempotencyKey=step,VehicleId=car,VendorCommandId=step,CommandType=(int)AgvCommandType.Transport,Status=(int)AgvCommandStatus.Sent,SentAt=now.LocalDateTime,PayloadJson=payload,ExecutionMapId="M",ExecutionMapVersionId="V",ExecutionRouteSnapshotJson=frozen,ExecutionRouteSnapshotHash=TransportRouteSnapshots.Hash(frozen)}).ExecuteCommand();
                db.Insertable(request).ExecuteCommand();db.Insertable(new TrafficResourceLeaseEntity{LeaseId="L"+i,ResourceId="R"+i,OwnerStepId=step,TaskId=task,VehicleId=car,MapId="M",MapVersionId="V",AcquiredAt=now.LocalDateTime}).ExecuteCommand();
            }
        }
        string Rows()=>JsonSerializer.Serialize(new{Tasks=db.Queryable<DispatchTaskEntity>().ToList(),Steps=db.Queryable<DispatchTaskStepEntity>().ToList(),Commands=db.Queryable<AgvCommandOutboxEntity>().ToList(),Requests=db.Queryable<TrafficRequestEntity>().ToList(),Leases=db.Queryable<TrafficResourceLeaseEntity>().ToList()});
        async Task Refuse(string name,Action mutation){Reset();mutation();var before=Rows();var r=await service.ReadAsync(query,default);Check(!r.Available,name);Check(Rows()==before&&!r.ResourcesReleased&&!r.ResourcesLocked,name+" 不改变任务命令路权");}
        Reset();var baseline=Rows();var good=await service.ReadAsync(query,default);
        Check(good.Available&&good.Assessment!.Conflicts.Count==1&&good.MapVersionId=="V"&&good.First?.VehicleId=="CAR1","权威地图快照和双车原路线建立几何上下文");
        Check(baseline==Rows()&&!good.ResourcesLocked&&!good.ResourcesReleased,"合格预检只读不创建命令凭据审计或释放");
        var noSource=await new TrafficGeometryPreviewService(inspector,gate,policy).ReadAsync(query,default);
        Check(!noSource.Available&&noSource.First is not null&&noSource.Reason.Contains("缺少"),"生产未注册核定来源仍展示原路线与具体缺口");
        Check(!(await service.ReadAsync(new("S1","S1"),default)).Available,"同一步骤拒绝");
        Check(!(await service.ReadAsync(new("UNKNOWN","S2"),default)).Available,"不存在步骤拒绝");
        await Refuse("旧快照缺失",()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>x.StepId=="S1").SetColumns(x=>x.ExecutionRouteSnapshotJson==null).ExecuteCommand());
        await Refuse("活动地图同版本几何变更",()=>runtime.Replace(new("M","V",map with{Nodes=[map.Nodes[0] with{Y=3},..map.Nodes.Skip(1)]},now)));
        await Refuse("等待请求不是已确认运输",()=>db.Updateable<TrafficRequestEntity>().Where(x=>x.StepId=="S1").SetColumns(x=>x.State=="Waiting").ExecuteCommand());
        await Refuse("命令结果未知",()=>db.Updateable<AgvCommandOutboxEntity>().Where(x=>x.StepId=="S1").SetColumns(x=>x.Status==(int)AgvCommandStatus.Sending).ExecuteCommand());
        await Refuse("旧地图租约",()=>db.Updateable<TrafficResourceLeaseEntity>().Where(x=>x.OwnerStepId=="S1").SetColumns(x=>x.MapVersionId=="OLD").ExecuteCommand());
        await Refuse("任务取消期间",()=>db.Updateable<DispatchTaskEntity>().Where(x=>x.TaskId=="T1").SetColumns(x=>x.CancelRequestId=="C").ExecuteCommand());
        await Refuse("恢复门禁关闭",()=>barrier.IsOpen=false);
        await Refuse("地图门禁关闭",()=>gate.SetMode(MapExecutionMode.Held));
        await Refuse("核定包络过期",()=>profiles.Until=now.AddSeconds(-1));
        await Refuse("核定包络未确认",()=>profiles.Verified=false);
        await Refuse("缺包络",()=>profiles.Null=true);
        await Refuse("误差界未知",()=>profiles.Uncertainty=double.NaN);
        await Refuse("核定来源断连",()=>profiles.Fail=true);
        Reset();profiles.Change=()=>db.Updateable<DispatchTaskEntity>().Where(x=>x.TaskId=="T1").SetColumns(x=>x.CancelRequestId=="C").ExecuteCommand();Check(!(await service.ReadAsync(query,default)).Available,"读取期间取消拒绝迟到结果");
        Reset();profiles.Change=()=>runtime.Replace(new("M","V",map,now));Check(!(await service.ReadAsync(query,default)).Available,"读取期间同版本地图实例替换拒绝");
        Reset();profiles.Change=()=>profiles.Revision="NEW";Check(!(await service.ReadAsync(query,default)).Available,"核定来源读取间版本变化拒绝");
        Reset();profiles.Change=()=>db.Updateable<TrafficResourceLeaseEntity>().Where(x=>x.OwnerStepId=="S1").SetColumns(x=>x.RenewedAt==DateTime.Now).ExecuteCommand();Check((await service.ReadAsync(query,default)).Available,"正常心跳续期不改变路线归属");
        Reset();profiles.Hang=true;Check(!(await service.ReadAsync(query,default)).Available,"不响应取消的来源仍受五秒读取预算限制");
        using(var held=await gate.EnterAsync(MapExecutionOperation.ExistingWork,default))Check(held is not null,"来源失败不持有控制门禁");
        Reset();using(var cts=new CancellationTokenSource()){cts.Cancel();var cancelled=false;try{await service.ReadAsync(query,cts.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"调用方取消传播");}
        Reset();baseline=Rows();
        var name="geometry-pipe-"+Guid.NewGuid().ToString("N");
        var args=typeof(TrafficControlNamedPipeServer).GetConstructors().Single().GetParameters().Select(p=>p.ParameterType switch
        {
            var t when t==typeof(Microsoft.Extensions.Logging.ILogger<TrafficControlNamedPipeServer>)=>(object)NullLogger<TrafficControlNamedPipeServer>.Instance,
            var t when t==typeof(TrafficSecurityOptions)=>new TrafficSecurityOptions{PipeName=name},
            var t when t==typeof(IDatabaseInitializer)=>new Initializer(),var t when t==typeof(NamedPipeDiagnostics)=>new NamedPipeDiagnostics(),
            var t when t==typeof(ITrafficGeometryPreviewService)=>service,_=>null
        }).ToArray();
        using var server=(TrafficControlNamedPipeServer)Activator.CreateInstance(typeof(TrafficControlNamedPipeServer),args)!;
        var dispatch=typeof(TrafficControlNamedPipeServer).GetMethod("DispatchAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var request=new TrafficControlPipeRequest(TrafficControlOperations.GetTrafficGeometryPreview,JsonSerializer.SerializeToElement(query));
        var viewer=new TrafficUserContext("test","viewer",TrafficPermission.View);
        var response=await(Task<TrafficControlPipeResponse>)dispatch.Invoke(server,[viewer,request,CancellationToken.None])!;
        Check(response.Success,"只读View权限可查询几何预检");
        var denied=false;try{await(Task<TrafficControlPipeResponse>)dispatch.Invoke(server,[viewer with{Permissions=TrafficPermission.None},request,CancellationToken.None])!;}catch(UnauthorizedAccessException){denied=true;}Check(denied,"IPC拒绝无查看权限上下文");
        await server.StartAsync(default);using var pipeClient=new NamedPipeTrafficControlClient(name);using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var roundtrip=await pipeClient.GetTrafficGeometryPreviewAsync(query,budget.Token);await server.StopAsync(default);
        Check(roundtrip.Available&&roundtrip.Assessment!.Conflicts.Count==1&&roundtrip.First?.StepId=="S1","真实后台管道与客户端保留权威上下文和冲突");
        Check(Rows()==baseline,"真实管道读取不更改原数据库");
        return roundtrip;
    }
}
sealed class Barrier:IRecoveryBarrier{public bool IsOpen{get;set;}=true;}
sealed class Initializer:IDatabaseInitializer{public Task InitializeAsync(CancellationToken ct)=>Task.CompletedTask;public Task<bool> CanConnectAsync(CancellationToken ct)=>Task.FromResult(true);}
sealed class Profiles:ITrafficGeometryProfileProvider
{
    public DateTimeOffset Until;public double Uncertainty=.05;public string Revision="R";public bool Null,Fail,Hang,Verified=true;public int Reads;public Action? Change;
    public Task<TrafficGeometryCertifiedProfile?> ReadAsync(string car,CancellationToken ct)
    {
        Reads++;if(Fail)throw new IOException("模拟核定源断连");if(Hang)return new TaskCompletionSource<TrafficGeometryCertifiedProfile?>().Task;
        var value=Null?null:new TrafficGeometryCertifiedProfile(new("P",car,"LocalCartesian",.5,Verified,true),Uncertainty,Revision,Until);
        if(Reads==1)Change?.Invoke();return Task.FromResult(value);
    }
}
public class UiClient:DispatchProxy
{
    public TrafficGeometryPreviewSnapshot Result=null!;public TrafficGeometryPreviewQuery? LastQuery;public bool Fail;public TaskCompletionSource<TrafficGeometryPreviewSnapshot>? Pending;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method?.Name!="GetTrafficGeometryPreviewAsync")throw new NotSupportedException();
        LastQuery=(TrafficGeometryPreviewQuery)args![0]!;if(Fail)throw new IOException("模拟断连");return Pending?.Task??Task.FromResult(Result);
    }
}
