using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static partial class Program
{
    static void CheckRoutePreview(AgvDispatch.Wpf.App app)
    {
        var evidence=Path.GetFullPath("docs/commissioning/evidence/20261016-task-route-actions");Directory.CreateDirectory(evidence);
        using var db=DatabaseFactory.Create(new DatabaseOptions{DatabasePath=Path.Combine(Path.GetTempPath(),"agv-route-preview-"+Guid.NewGuid().ToString("N")+".db")});Complete(new DatabaseInitializer(db).InitializeAsync(default));
        var nodes=new[]{"HOME","PICK","WAIT","DUMP"}.Select((id,i)=>new MapNode(id,i*5,0,MapNodeType.Station,true,[],"LM-"+id)).ToArray();
        var definition=new MapDefinition("preview-test","","LocalCartesian","m",nodes,Enumerable.Range(0,3).Select(i=>new MapEdge("EDGE"+i,nodes[i].Id,nodes[i+1].Id,5,1,true,true,[])).ToArray(),[]);
        var map=new ActiveMapRuntime();void Restore()=>map.Replace(new("MAP","VERSION",definition,DateTimeOffset.Now));Restore();
        var gateway=new FixtureGateway();var station=new FixtureStations();var states=new FixtureStates();var gate=new FixtureGate();
        var dispatch=new DispatchOrchestratorOptions{Enabled=true,DumperWaitingSite="WAIT",DumperDumpingSite="DUMP",StationPickupSites=new(){["CNC001"]="PICK"}};
        foreach(var (type,action) in new[]{("GoToPickup","JackLoad"),("DeliverFullBoxToDumper","Wait"),("EnterDumpingPosition","Wait"),("ReturnEmptyBox","JackUnload")})dispatch.StepActions[type]=new(){Operation=action};
        var creation=new DispatchTaskCreationOptions();var parameters=new TaskParameterRuntime(dispatch,creation);var checker=new RoutePreviewChecker();
        var service=new ManualTaskCreationService(new DispatchTaskRepository(db),station,gateway,states,map,gate,new DispatchOrchestrationRepository(db),creation,dispatch,new(),new(),new FixturePlcConfiguration(),parameters,checker);
        var options=Complete(service.GetOptionsAsync(default));var request=new ManualTaskRoutePreviewRequest("CNC001","AGV-01","MAP","VERSION",options.ParameterRevision);
        var preview=Complete(service.PreviewRoutesAsync(request,default));
        Require(preview.Verified&&preview.Legs.Select(l=>l.Sequence).SequenceEqual(new[]{1,3,5,7})&&preview.Segments.Count==5,"backend previews four legs and every adjacent subsegment");
        Require(preview.Legs[0].LogicalPath=="HOME → PICK"&&preview.Legs[0].VendorPath=="LM-HOME → LM-PICK"&&preview.Segments[0].EdgeId=="EDGE0","backend keeps business nodes manufacturer sites and edge IDs distinct");
        Require(preview.Segments.All(s=>s.Status=="本次通过")&&preview.ObservedAt>DateTimeOffset.Now.AddSeconds(-2),"current same-batch observations and timestamp exposed");
        CheckBackendRouteMap(service,request,map,definition,preview,checker);
        var count=checker.Calls;Require(!Complete(service.PreviewRoutesAsync(request with{MapVersionId="OLD"},default)).Verified&&checker.Calls==count,"wrong map version avoids device reads");
        Require(!Complete(service.PreviewRoutesAsync(request with{ParameterRevision="OLD"},default)).Verified&&checker.Calls==count,"wrong parameter revision avoids device reads");
        Require(!Complete(service.PreviewRoutesAsync(request with{VehicleId="OTHER"},default)).Verified&&checker.Calls==count,"unknown vehicle avoids device reads");
        Require(!Complete(service.PreviewRoutesAsync(request with{StationId=new string('s',33)},default)).Verified&&checker.Calls==count,"invalid request rejected without reads");
        station.Allowed=false;Require(!Complete(service.PreviewRoutesAsync(request,default)).Verified&&checker.Calls==count,"unready PLC station preview does not query vehicle");station.Allowed=true;
        gate.Allowed=false;Require(!Complete(service.PreviewRoutesAsync(request,default)).Verified&&checker.Calls==count,"recovery gate prevents fresh vehicle preview");gate.Allowed=true;
        map.Replace(new("MAP","VERSION",definition with{Nodes=nodes.Select((n,i)=>i==1?n with{AgvSiteId=null}:n).ToArray()},DateTimeOffset.Now));
        var missingMap=Complete(service.PreviewRoutesAsync(request,default));Require(!missingMap.Verified&&checker.Calls==count,"missing manufacturer mapping stays local and blocked");
        Require(missingMap.Map is not null&&missingMap.Segments.Any(s=>s.Status=="不符"&&s.Message.Contains("PICK")),"local missing mapping is attributed to its exact segment without factory reads");Restore();
        checker.Result=RoutePreviewChecker.Success with{Accepted=false,Message="模拟第5步直接线路不符",RouteChecks=RoutePreviewChecker.Success.RouteChecks.Take(3).Select((r,i)=>i==2?r with{DirectConfirmed=false,Message="1303不是直接路径"}:r).ToArray()};
        var failed=Complete(service.PreviewRoutesAsync(request,default));Require(!failed.Verified&&failed.Segments[0].Status=="仅已观察"&&failed.Segments[2].Status=="不符"&&failed.Segments[3].Status=="未核验","partial check never promotes prior observation to full pass");
        checker.Result=RoutePreviewChecker.Success;checker.After=()=>gateway.Connection=ConnectionState.Disconnected;
        var changed=Complete(service.PreviewRoutesAsync(request,default));Require(changed.Map is null&&!changed.Verified&&changed.Segments.All(s=>s.Status=="已失效"),"vehicle changes during check invalidate all observations");gateway.Connection=ConnectionState.Online;checker.After=null;
        checker.After=()=>map.Replace(new("MAP","VERSION",definition,DateTimeOffset.Now));Require(!Complete(service.PreviewRoutesAsync(request,default)).Verified,"same map identity with replaced snapshot invalidates preview");checker.After=null;
        checker.After=()=>station.Allowed=false;Require(!Complete(service.PreviewRoutesAsync(request,default)).Verified,"PLC readiness change during check invalidates preview");station.Allowed=true;checker.After=null;
        var config=parameters.Capture();checker.After=()=>parameters.Apply(new(1,"CHANGED","","MAP","VERSION",config,[]));Require(!Complete(service.PreviewRoutesAsync(request,default)).Verified,"parameter revision change during check invalidates preview");checker.After=null;parameters.Apply(new(1,request.ParameterRevision,"","MAP","VERSION",config,[]));
        var longNodes=new[]{"HOME"}.Concat(Enumerable.Range(1,128).Select(i=>"MID"+i)).Concat(new[]{"PICK","WAIT","DUMP"}).Select((id,i)=>new MapNode(id,i*.1,0,MapNodeType.Station,true,[],"LM-"+id)).ToArray();
        var longDefinition=definition with{Nodes=longNodes,Edges=Enumerable.Range(0,longNodes.Length-1).Select(i=>new MapEdge("LONG"+i,longNodes[i].Id,longNodes[i+1].Id,.1,1,true,true,[])).ToArray()};
        var beforeLong=checker.Calls;map.Replace(new("MAP","VERSION",longDefinition,DateTimeOffset.Now));var longResult=Complete(service.PreviewRoutesAsync(request,default));Require(!longResult.Verified&&checker.Calls==beforeLong&&longResult.Legs[0].Message.Contains("128"),"preview display budget refuses without changing execution limits");Restore();
        var beforePending=checker.Calls;checker.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);var pending=service.PreviewRoutesAsync(request,default);
        Pump(()=>checker.Calls>beforePending);var control=gate.EnterControlAsync(default).AsTask();Pump(()=>control.IsCompleted);control.Result.Dispose();Require(!pending.IsCompleted,"network read does not hold map execution mutex");checker.Pending.SetResult(RoutePreviewChecker.Success);Complete(pending);checker.Pending=null;
        Require(Complete(db.Queryable<DispatchTaskEntity>().CountAsync())==0,"read-only preview never persists business task");
        var proxy=DispatchProxy.Create<ITrafficControlClient,RoutePreviewClient>();var client=(RoutePreviewClient)(object)proxy;client.Service=service;
        var vm=new TaskCenterViewModel(proxy);vm.OnNavigatedTo(null!);Pump(()=>!vm.IsBusy);vm.IsCreationOpen=true;Pump(()=>!vm.IsCreating);vm.CreationVehicle=vm.CreationVehicles.Last();
        Complete(vm.PreviewCreationRoutesAsync());Require(vm.CreationRouteLegs.Count==4&&vm.CreationRouteSegments.Count==5&&vm.CreationRoutePreviewMessage.Contains("本次核验通过"),"real viewmodel invokes backend current-route preview");
        CheckRouteMapUi(vm,app,evidence,client,preview,failed);
        Complete(vm.PreviewCreationRoutesAsync());
        RenderRoutePreview(vm,app,evidence,"current-routes-light",1280,900);
        var light=app.Resources.MergedDictionaries.FirstOrDefault(d=>d.Source?.OriginalString.Contains("Themes/Light.xaml")==true);
        if(light is not null){light.Source=new Uri("pack://application:,,,/AgvDispatch.Wpf;component/Themes/Dark.xaml");RenderRoutePreview(vm,app,evidence,"current-routes-dark-small",1100,700);light.Source=new Uri("pack://application:,,,/AgvDispatch.Wpf;component/Themes/Light.xaml");}
        typeof(TaskCenterViewModel).GetField("_routePreviewExpires",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(vm,DateTimeOffset.Now.AddSeconds(-1));vm.ExpireCreationRoutePreview();Require(vm.CreationRouteSegments.Count==0&&vm.CreationRoutePreviewMessage.Contains("15秒"),"expired preview clears independently from submit");
        vm.CreationStation=null;Require(vm.CreationRouteSegments.Count==0,"changing selected station clears preview");vm.CreationStation=vm.CreationStations[0];
        client.Override=_=>Task.FromResult(preview with{ObservedAt=DateTimeOffset.Now.AddSeconds(-30)});Complete(vm.PreviewCreationRoutesAsync());Require(vm.CreationRouteSegments.Count==0&&vm.CreationRoutePreviewMessage.Contains("失败"),"stale backend observation refused");
        client.Override=_=>Task.FromResult(preview with{VehicleId="OTHER"});Complete(vm.PreviewCreationRoutesAsync());Require(vm.CreationRouteSegments.Count==0,"cross-vehicle response refused");
        client.Override=_=>Task.FromResult(preview with{Segments=[],ObservedAt=DateTimeOffset.Now});Complete(vm.PreviewCreationRoutesAsync());Require(vm.CreationRouteSegments.Count==0&&vm.CreationRoutePreviewMessage.Contains("矛盾"),"pass without segment evidence rejected");
        client.Override=_=>Task.FromException<ManualTaskRoutePreview>(new NotSupportedException("旧后台不支持当前路线"));Complete(vm.PreviewCreationRoutesAsync());Require(vm.CreationRouteSegments.Count==0&&vm.CreationRoutePreviewMessage.Contains("旧后台"),"old backend failure never reuses static preview");
        var delayed=new TaskCompletionSource<ManualTaskRoutePreview>(TaskCreationOptions.RunContinuationsAsynchronously);client.Override=_=>delayed.Task;var old=vm.PreviewCreationRoutesAsync();Require(vm.IsRoutePreviewBusy&&!vm.SubmitTaskCommand.CanExecute(),"in-flight readonly preview disables submit overlap");
        vm.CreationVehicle=vm.CreationVehicles[0];delayed.SetResult(preview);Complete(old);Require(vm.CreationRouteSegments.Count==0&&!vm.IsRoutePreviewBusy,"late ignored cancellation cannot restore changed selection");
        vm.CreationVehicle=vm.CreationVehicles.Last();client.Override=null;Complete(vm.PreviewCreationRoutesAsync());vm.ClearCreationRoutesCommand.Execute();Require(vm.CreationRouteSegments.Count==0,"manual clear cancels route preview");
        delayed=new(TaskCreationOptions.RunContinuationsAsynchronously);client.Override=_=>delayed.Task;old=vm.PreviewCreationRoutesAsync();vm.OnNavigatedFrom(null!);delayed.SetResult(preview);Complete(old);Require(vm.CreationRouteSegments.Count==0,"leaving page rejects late preview");
        Require(client.Writes==0&&Complete(db.Queryable<DispatchTaskEntity>().CountAsync())==0,"preview UI never invokes create control or persistence");
        File.WriteAllText(Path.Combine(evidence,"backend-preview.json"),JsonSerializer.Serialize(preview));File.WriteAllText(Path.Combine(evidence,"backend-partial-failure.json"),JsonSerializer.Serialize(failed));
    }
    static void RenderRoutePreview(TaskCenterViewModel vm,AgvDispatch.Wpf.App app,string folder,string name,int width,int height)
    {
        ViewModelLocationProvider.SetDefaultViewModelFactory(_=>vm);var view=new TaskCenterView{DataContext=vm,Width=width,Height=height,Background=(Brush)app.FindResource("AppBackgroundBrush")};view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));view.UpdateLayout();
        ((TabItem)view.FindName("CreationRouteDetailsTab")).IsSelected=true;view.UpdateLayout();
        var grid=(DataGrid)view.FindName("CreationRouteSegmentGrid");((ScrollViewer)view.FindName("CreationFormScroll")).ScrollToVerticalOffset(250);view.UpdateLayout();
        Require(grid.IsReadOnly&&grid.Items.Count==5,name+" formal route grid readonly bindings");
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(folder,name+".png"));encoder.Save(file);
    }
}
public sealed class RoutePreviewChecker:IRobokitTaskMapPreflight
{
    public static RobokitTaskMapCheck Success=>new(true,"模拟只读核验通过"){RouteChecks=[new(1,"LM-HOME","LM-PICK",true,"1303直接路径"),new(3,"LM-PICK","LM-WAIT",true,"1303直接路径"),new(5,"LM-WAIT","LM-DUMP",true,"1303直接路径"),new(7,"LM-DUMP","LM-WAIT",true,"1303直接路径"),new(7,"LM-WAIT","LM-PICK",true,"1303直接路径")]};
    public RobokitTaskMapCheck Result=Success;public int Calls;public Action? After;public TaskCompletionSource<RobokitTaskMapCheck>? Pending;
    public async Task<RobokitTaskMapCheck> CheckAsync(string vehicleId,string currentSite,string pickup,string waiting,string dumping,string mapId,string versionId,CancellationToken ct){Calls++;if(Pending is not null)return await Pending.Task;After?.Invoke();return Result;}
}
public class RoutePreviewClient:DispatchProxy
{
    public IManualTaskCreationService Service=null!;public Func<Task<ManualTaskCreationOptions>>? ReadOptions;public Func<ManualTaskRoutePreviewRequest,Task<ManualTaskRoutePreview>>? Override;public int Writes;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method!.Name switch
    {
        "GetTasksAsync"=>Task.FromResult(new TaskListPage([],0,1,50)),
        "GetTaskCreationOptionsAsync"=>ReadOptions?.Invoke()??Service.GetOptionsAsync((CancellationToken)args![0]!),
        "GetTaskRoutePreviewAsync"=>Override?.Invoke((ManualTaskRoutePreviewRequest)args![0]!)??Service.PreviewRoutesAsync((ManualTaskRoutePreviewRequest)args![0]!, (CancellationToken)args[1]!),
        _=>Unexpected(method.Name)
    };
    private object Unexpected(string name){Writes++;throw new InvalidOperationException("unexpected "+name);}
}

