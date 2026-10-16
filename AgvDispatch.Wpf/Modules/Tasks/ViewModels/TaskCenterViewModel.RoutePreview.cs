using System.Collections.ObjectModel;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;
using System.Windows.Threading;

namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class TaskCenterViewModel
{
    private readonly DispatcherTimer _routePreviewTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    private CancellationTokenSource? _routePreviewRequest;
    private long _routePreviewGeneration;
    private bool _isRoutePreviewBusy;
    private DateTimeOffset? _routePreviewExpires;
    private string _routePreviewMessage="选择工位和具体车辆后，手动读取当前后台路线核验；不读取离线报告。";
    public bool IsRoutePreviewBusy {get=>_isRoutePreviewBusy;private set{if(SetProperty(ref _isRoutePreviewBusy,value)){RaisePropertyChanged(nameof(CreationInputsEnabled));RefreshCreationCommands();}}}
    public string CreationRoutePreviewMessage {get=>_routePreviewMessage;private set=>SetProperty(ref _routePreviewMessage,value);}
    public ObservableCollection<ManualTaskRouteLeg> CreationRouteLegs {get;}=[];
    public ObservableCollection<ManualTaskRouteSegment> CreationRouteSegments {get;}=[];
    public DelegateCommand PreviewCreationRoutesCommand {get;private set;}=null!;
    public DelegateCommand ClearCreationRoutesCommand {get;private set;}=null!;
    private void InitializeCreationRoutePreview()
    {
        InitializeCreationRouteMap();
        _routePreviewTimer.Tick+=(_,_)=>ExpireCreationRoutePreview();
        PreviewCreationRoutesCommand=new(async()=>await PreviewCreationRoutesAsync(),()=>!IsCreating&&!IsRoutePreviewBusy&&_creationOptions?.MapId is not null&&_creationOptions.MapVersionId is not null&&CreationStation is not null&&CreationVehicle?.Id is not null);
        ClearCreationRoutesCommand=new(()=>ClearCreationRoutePreview("已取消或清空路线预览。"));
    }
    private void ClearCreationRoutePreview(string reason)
    {
        _routePreviewTimer.Stop();_routePreviewGeneration++;_routePreviewRequest?.Cancel();_routePreviewRequest=null;_routePreviewExpires=null;
        ClearCreationRouteMap(reason);
        CreationRouteLegs.Clear();CreationRouteSegments.Clear();CreationRoutePreviewMessage=reason;IsRoutePreviewBusy=false;
    }
    public void ExpireCreationRoutePreview()
    {
        if(_routePreviewExpires is {} expiry&&DateTimeOffset.Now>=expiry)ClearCreationRoutePreview("路线预览已超过15秒观察期限，请重新查询；提交仍会独立核验。");
    }
    public async Task PreviewCreationRoutesAsync()
    {
        if(!PreviewCreationRoutesCommand.CanExecute()||_navigation is null||_navigation.IsCancellationRequested)return;
        var options=_creationOptions!;var request=new ManualTaskRoutePreviewRequest(CreationStation!.StationId,CreationVehicle!.Id!,options.MapId!,options.MapVersionId!,options.ParameterRevision);
        ClearCreationRoutePreview("正在读取后台当前地图与车端只读核验，最多25秒…");var generation=_routePreviewGeneration;
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_navigation.Token);cancellation.CancelAfter(TimeSpan.FromSeconds(25));_routePreviewRequest=cancellation;IsRoutePreviewBusy=true;
        try
        {
            var result=await _client.GetTaskRoutePreviewAsync(request,cancellation.Token);cancellation.Token.ThrowIfCancellationRequested();
            if(generation!=_routePreviewGeneration)return;
            if(result.MapId!=request.MapId||result.MapVersionId!=request.MapVersionId||result.ParameterRevision!=request.ParameterRevision||result.StationId!=request.StationId||result.VehicleId!=request.VehicleId||result.ObservedAt>DateTimeOffset.Now.AddSeconds(2)||result.ObservedAt<DateTimeOffset.Now.AddSeconds(-15)||result.Legs.Count>4||result.Segments.Count>512)
                throw new InvalidOperationException("后台返回的身份、观察时间或显示预算不符，已丢弃。");
            var sequences=new[]{1,3,5,7};
            if(result.Legs.Count>0&&!result.Legs.Select(l=>l.Sequence).SequenceEqual(sequences)||result.Segments.Any(s=>!sequences.Contains(s.Sequence)||s.Segment is <1 or >128)||result.Verified&&(result.Legs.Count!=4||result.Segments.Count==0||result.Segments.Select(s=>s.Sequence).Distinct().Count()!=4||result.Segments.Any(s=>s.Status!="本次通过")))throw new InvalidOperationException("后台路线明细与总体结果矛盾。");
            foreach(var leg in result.Legs)CreationRouteLegs.Add(leg);foreach(var segment in result.Segments)CreationRouteSegments.Add(segment);
            LoadCreationRouteMap(result);
            _routePreviewExpires=result.ObservedAt.AddSeconds(15);_routePreviewTimer.Start();
            CreationRoutePreviewMessage=$"后台观察 {result.ObservedAt:HH:mm:ss} · {result.MapId}/{result.MapVersionId} · {result.VehicleId}@{result.CurrentSite} · {(result.Verified?"本次核验通过":"本次未通过/未完整核验")}\n{result.Message}\n有效展示15秒；不是路权、任务回执或持续放行凭据。";
        }
        catch(OperationCanceledException){if(generation==_routePreviewGeneration)CreationRoutePreviewMessage="路线查询已取消或超时，旧结果已清空。";}
        catch(Exception ex){if(generation==_routePreviewGeneration){CreationRouteLegs.Clear();CreationRouteSegments.Clear();ClearCreationRouteMap("当前路线预览失败，地图已清空。");CreationRoutePreviewMessage="当前路线预览失败："+ex.Message;}}
        finally{if(generation==_routePreviewGeneration){_routePreviewRequest=null;IsRoutePreviewBusy=false;}}
    }
}
