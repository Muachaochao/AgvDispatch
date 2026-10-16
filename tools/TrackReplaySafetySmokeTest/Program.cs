using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Agv;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Wpf.ViewModels;
internal static class Program
{
    static int checks;static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
    [STAThread] static void Main()
    {
        var now=DateTimeOffset.UtcNow;var query=new AgvTrackQueryRequest("CAR","M",now.AddMinutes(-1),now,100,"V");
        var p=new AgvTrackPointSnapshot(1,"CAR","M","V",0,0,0,.99,AgvMapPositionState.Normal,0,"E",now.AddSeconds(-2));
        var q=p with{Id=2,X=1,SampledAt=now.AddSeconds(-1)};
        Check(AgvTrackReplayValidator.Validate([q,p],query).Points.SequenceEqual(new[]{p,q}),"按采样时间排序历史点");
        void Bad(AgvTrackPointSnapshot bad,string name)=>Check(!AgvTrackReplayValidator.Validate([p,bad],query).Available,name);
        Bad(q with{VehicleId="OTHER"},"混车拒绝");Bad(q with{MapId="OTHER"},"混地图拒绝");Bad(q with{MapVersionId="OLD"},"同地图旧版本拒绝");
        Bad(q with{MapVersionId=""},"旧记录无版本拒绝");Bad(q with{X=double.NaN},"非有限坐标拒绝");Bad(q with{Angle=double.PositiveInfinity},"无效角度拒绝");
        Bad(q with{PositionState=AgvMapPositionState.Stale},"失效定位不拼接");Bad(q with{SampledAt=now.AddSeconds(1)},"查询范围外拒绝");Bad(q with{Id=1},"重复历史ID拒绝");
        Check(!AgvTrackReplayValidator.Validate([p],query with{MapVersionId=null}).Available,"回放不能省略版本");
        Check(!AgvTrackReplayValidator.Validate([p,q,q with{Id=3}],query with{MaximumCount=2}).Available,"超返回预算拒绝");
        Check(AgvTrackReplayValidator.Validate([p,q with{PositionState=AgvMapPositionState.OffRoute}],query).Available,"偏离路线历史仍可显示异常");
        Check(AgvTrackReplayValidator.CanConnect(p,q)&&!AgvTrackReplayValidator.CanConnect(p,q with{SampledAt=p.SampledAt.AddSeconds(31)}),"三十秒以上断档不画连续线");
        Check(!AgvTrackReplayValidator.CanConnect(p,q with{MapVersionId="OTHER"}),"不同版本不能连线");
        var root=Path.Combine(Path.GetTempPath(),"track-version-"+Guid.NewGuid().ToString("N"));
        using(var db=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(root,"test.db")}))
        {
            db.CodeFirst.InitTables<AgvTrackPointEntity>();var store=new AgvTrackStore(db);
            store.AppendAsync(p,default).GetAwaiter().GetResult();store.AppendAsync(q,default).GetAwaiter().GetResult();store.AppendAsync(q with{MapVersionId="OLD",X=50},default).GetAwaiter().GetResult();
            var filtered=store.QueryAsync(query,default).GetAwaiter().GetResult();Check(filtered.Count==2&&filtered.All(x=>x.MapVersionId=="V"),"真实SQLite查询仅指定版本");
            Check(store.QueryAsync(query with{MapVersionId=null},default).GetAwaiter().GetResult().Count==3,"旧查询契约兼容保留全版本查询");
            Check(db.Queryable<AgvTrackPointEntity>().Count()==3,"筛选不删除历史数据");
        }
        var app=new AgvDispatch.Wpf.App();app.InitializeComponent();SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var proxy=DispatchProxy.Create<ITrafficControlClient,ReplayClient>();var fake=(ReplayClient)proxy;
        var vm=new MapManagementViewModel(proxy);
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(MapManagementViewModel).GetField("_isOfflineEditing",flags)!.SetValue(vm,false);
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        void Workspace(string id,string version)=>typeof(MapManagementViewModel).GetField("_workspace",flags)!.SetValue(vm,new MapWorkspaceSnapshot(
            JsonSerializer.Deserialize<MapProjectSummary>($"{{\"mapId\":\"{id}\",\"activeVersionId\":\"{version}\",\"isActive\":true}}",json)!,
            null!,JsonSerializer.Deserialize<MapVersionSnapshot>($"{{\"mapId\":\"{id}\",\"versionId\":\"{version}\"}}",json)));
        AgvDiagnostic Vehicle(string id)=>JsonSerializer.Deserialize<AgvDiagnostic>($"{{\"vehicleId\":\"{id}\",\"displayName\":\"{id}\"}}",json)!;
        Workspace("M","V");vm.SelectedLiveAgv=Vehicle("CAR");fake.Points=[p,q];vm.LoadTrackReplayCommand.Execute();
        Check(fake.Query?.MapVersionId=="V"&&vm.PlayTrackReplayCommand.CanExecute(),"界面显式查询运行版本后允许播放");
        vm.SelectedLiveAgv=Vehicle("OTHER");Check(!vm.PlayTrackReplayCommand.CanExecute(),"换车清除已加载轨迹");
        vm.SelectedLiveAgv=Vehicle("CAR");fake.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);vm.LoadTrackReplayCommand.Execute();vm.SelectedLiveAgv=Vehicle("OTHER");fake.Pending.SetResult([p,q]);Pump();
        Check(!vm.PlayTrackReplayCommand.CanExecute(),"换车后迟到轨迹不覆盖新选择");
        fake.Pending=null;vm.SelectedLiveAgv=Vehicle("CAR");fake.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);vm.LoadTrackReplayCommand.Execute();Workspace("M2","V2");fake.Pending.SetResult([p,q]);Pump();
        Check(!vm.PlayTrackReplayCommand.CanExecute(),"换图后迟到轨迹不覆盖新工作区");
        fake.Pending=null;Workspace("M","V");fake.Points=[p,q with{MapVersionId="OLD"}];vm.LoadTrackReplayCommand.Execute();Check(!vm.PlayTrackReplayCommand.CanExecute()&&vm.ReplaySummary.Contains("混合"),"旧后台返回混合版本明确拒绝");
        vm.DeactivateEditor();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"replay-result.json"),JsonSerializer.Serialize(new{Checks=checks,Database=root,Scope="version filter, replay validation and async selection races; no vehicle controls"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS {checks} replay safety checks.");
    }
    static void Pump(){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
}
public class ReplayClient:DispatchProxy
{
    public IReadOnlyList<AgvTrackPointSnapshot> Points=[];public AgvTrackQueryRequest? Query;public TaskCompletionSource<IReadOnlyList<AgvTrackPointSnapshot>>? Pending;
    protected override object? Invoke(MethodInfo? m,object?[]? args)
    {if(m?.Name!="GetAgvTrackAsync")throw new NotSupportedException();Query=(AgvTrackQueryRequest)args![0]!;return Pending?.Task??Task.FromResult(Points);}
}
