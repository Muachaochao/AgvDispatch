using System.Collections.ObjectModel;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;
namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class TrafficResourceMonitorViewModel
{
    private TrafficRequestRow? _geometryFirst,_geometrySecond;
    private CancellationTokenSource? _geometryCancellation;
    private int _geometryGeneration;
    private bool _geometryBusy;
    private TrafficGeometryPreviewSnapshot? _geometrySnapshot;
    private DispatcherTimer? _geometryExpiry;
    private string _geometryNotice="选择两辆车的已确认原运输进行只读预检；等待申请或旧记录可能无法评估。";
    private string _geometryContext="尚未检查。预检不授予通行、不加锁、不解锁。";
    public DelegateCommand CheckGeometryCommand{get;private set;}=null!;
    public ObservableCollection<TrafficGeometryConflict> GeometryConflicts{get;}=[];
    public TrafficRequestRow? GeometryFirst{get=>_geometryFirst;set{var changed=_geometryFirst?.StepId!=value?.StepId;SetProperty(ref _geometryFirst,value);if(changed)InvalidateGeometry("已更换第一条运输，请重新检查。");CheckGeometryCommand?.RaiseCanExecuteChanged();}}
    public TrafficRequestRow? GeometrySecond{get=>_geometrySecond;set{var changed=_geometrySecond?.StepId!=value?.StepId;SetProperty(ref _geometrySecond,value);if(changed)InvalidateGeometry("已更换第二条运输，请重新检查。");CheckGeometryCommand?.RaiseCanExecuteChanged();}}
    public string GeometryNotice{get=>_geometryNotice;private set=>SetProperty(ref _geometryNotice,value);}
    public string GeometryContext{get=>_geometryContext;private set=>SetProperty(ref _geometryContext,value);}
    private void InitializeGeometryCommands()
    {
        CheckGeometryCommand=new(async()=>await CheckGeometryAsync(),()=>!IsBusy&&!_geometryBusy&&Has(TrafficPermission.View)&&GeometryFirst is not null&&GeometrySecond is not null&&GeometryFirst.StepId!=GeometrySecond.StepId);
        _geometryExpiry=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};
        _geometryExpiry.Tick+=(_,_)=>
        {
            if(_geometrySnapshot is {ValidUntil:{ } until} s&&(DateTimeOffset.UtcNow>=until||DateTimeOffset.UtcNow<s.CheckedAt))
                InvalidateGeometry("本次预检已过期或时钟变化，请重新检查。");
        };
    }
    private void InvalidateGeometry(string reason)
    {
        _geometryGeneration++;_geometryCancellation?.Cancel();_geometrySnapshot=null;_geometryExpiry?.Stop();
        GeometryConflicts.Clear();GeometryNotice=reason;GeometryContext="不授予通行、不加锁、不解锁。";
    }
    private void RefreshGeometrySelection()
    {
        GeometryFirst=TrafficRequests.FirstOrDefault(r=>r.StepId==GeometryFirst?.StepId);
        GeometrySecond=TrafficRequests.FirstOrDefault(r=>r.StepId==GeometrySecond?.StepId);
        if(_geometrySnapshot is {} s&&(s.MapId!=_cachedMapId||s.MapVersionId!=_cachedVersionId||
            GeometryFirst?.SourceSnapshot?.State is not ("Held" or "Acquired")||GeometrySecond?.SourceSnapshot?.State is not ("Held" or "Acquired")))
            InvalidateGeometry("运行地图或原运输占用状态变化，请重新检查。");
    }
    private async Task CheckGeometryAsync()
    {
        if(!CheckGeometryCommand.CanExecute())return;
        var first=GeometryFirst!.StepId;var second=GeometrySecond!.StepId;
        InvalidateGeometry("正在核对后台地图、原路线和核定包络……");
        var generation=_geometryGeneration;
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(15));_geometryCancellation=cancellation;
        _geometryBusy=true;CheckGeometryCommand.RaiseCanExecuteChanged();
        try
        {
            var snapshot=await _client.GetTrafficGeometryPreviewAsync(new(first,second),cancellation.Token);
            if(generation!=_geometryGeneration)return;
            if(snapshot.First is {} a&&a.StepId!=first||snapshot.Second is {} b&&b.StepId!=second)
            {InvalidateGeometry("后台返回的原步骤不匹配，请重新检查。");return;}
            if(snapshot.Available&&(snapshot.ValidUntil is not {} until||DateTimeOffset.UtcNow>=until||DateTimeOffset.UtcNow<snapshot.CheckedAt))
            {InvalidateGeometry("收到的预检已过期或时钟异常，请重新检查。");return;}
            _geometrySnapshot=snapshot;GeometryNotice=(snapshot.Available?(snapshot.Assessment!.Complete?"本次完整预检：":"本次预检未完成："):"无法评估：")+snapshot.Reason;
            GeometryContext=$"地图 {snapshot.MapId??"未知"} / {snapshot.MapVersionId??"未知"} · 检查 {snapshot.CheckedAt.LocalDateTime:HH:mm:ss}\n"+
                $"第一运输 {snapshot.First?.TaskId??"未知"} / {snapshot.First?.VehicleId??"未知"} / {snapshot.First?.EdgeCount??0} 段\n"+
                $"第二运输 {snapshot.Second?.TaskId??"未知"} / {snapshot.Second?.VehicleId??"未知"} / {snapshot.Second?.EdgeCount??0} 段\n"+
                $"精检 {snapshot.Assessment?.Comparisons??0} · 索引访问 {snapshot.Assessment?.IndexVisits??0}；不考虑时间，不授予通行、不加锁、不解锁。";
            Replace(GeometryConflicts,snapshot.Assessment?.Conflicts??[]);
            if(snapshot.ValidUntil is not null)_geometryExpiry?.Start();
        }
        catch(Exception ex){if(generation==_geometryGeneration)InvalidateGeometry("预检不可用："+ex.Message);}
        finally{if(ReferenceEquals(_geometryCancellation,cancellation))_geometryCancellation=null;_geometryBusy=false;CheckGeometryCommand.RaiseCanExecuteChanged();}
    }
}
