using System.Collections.ObjectModel;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;
namespace AgvDispatch.Wpf.ViewModels;
public sealed partial class TrafficResourceMonitorViewModel
{
    public ObservableCollection<TrafficRequestRow> WaitingQueue { get; } = [];
    public ObservableCollection<string> WaitingBlockingResources { get; } = [];
    public IReadOnlyList<WaitingSortChoice> WaitingSortChoices { get; } = [new(TrafficWaitingSort.LongestWait,"等待时间优先"),new(TrafficWaitingSort.PriorityThenWait,"任务优先级优先")];
    public DelegateCommand ApplyWaitingFilterCommand { get; private set; } = null!;
    public DelegateCommand ResetWaitingFilterCommand { get; private set; } = null!;
    public DelegateCommand PreviousWaitingPageCommand { get; private set; } = null!;
    public DelegateCommand NextWaitingPageCommand { get; private set; } = null!;
    private TrafficWaitingQuery _appliedWaitingQuery = new();
    private TrafficWaitingPage? _waitingPage;
    private string _waitingResource="",_waitingPriority="",_waitingNotice="等待队列尚未加载。";
    private TrafficWaitingSort _waitingSort;
    private TrafficRequestRow? _selectedWaitingRequest;
    public string WaitingResourceInput { get=>_waitingResource; set=>SetProperty(ref _waitingResource,value); }
    public string WaitingPriorityInput { get=>_waitingPriority; set=>SetProperty(ref _waitingPriority,value); }
    public TrafficWaitingSort WaitingSortInput { get=>_waitingSort; set=>SetProperty(ref _waitingSort,value); }
    public string WaitingNotice { get=>_waitingNotice; private set=>SetProperty(ref _waitingNotice,value); }
    public TrafficRequestRow? SelectedWaitingRequest
    {
        get=>_selectedWaitingRequest;
        set
        {
            if(!SetProperty(ref _selectedWaitingRequest,value) || value==null)return;
            var existing=TrafficRequests.FirstOrDefault(r=>r.StepId==value.StepId);
            if(existing==null)TrafficRequests.Add(value);else TrafficRequests[TrafficRequests.IndexOf(existing)]=value;
            SelectedTrafficRequest=value;
        }
    }
    private void InitializeWaitingCommands()
    {
        ApplyWaitingFilterCommand=new(async()=>await ChangeWaitingAsync(0),()=>!IsBusy);
        ResetWaitingFilterCommand=new(async()=>{WaitingResourceInput="";WaitingPriorityInput="";WaitingSortInput=TrafficWaitingSort.LongestWait;await ChangeWaitingAsync(0);},()=>!IsBusy);
        PreviousWaitingPageCommand=new(async()=>await ChangeWaitingAsync(-1),()=>!IsBusy && _waitingPage?.PageNumber>1);
        NextWaitingPageCommand=new(async()=>await ChangeWaitingAsync(1),()=>!IsBusy && _waitingPage!=null && (long)_waitingPage.PageNumber*_waitingPage.PageSize<_waitingPage.TotalMatched);
    }
    private async Task ChangeWaitingAsync(int pageDelta)
    {
        if(IsBusy)return;
        if(pageDelta==0)
        {
            int? priority=null;
            if(!string.IsNullOrWhiteSpace(WaitingPriorityInput))
            {
                if(!int.TryParse(WaitingPriorityInput,out var number) || number is <0 or >100){WaitingNotice="最低优先级请输入0–100的整数，或留空。";return;}
                priority=number;
            }
            if(WaitingResourceInput.Trim().Length>128){WaitingNotice="阻塞资源编号过长。";return;}
            _appliedWaitingQuery=new(WaitingResourceInput.Trim(),priority,WaitingSortInput);
        }
        else _appliedWaitingQuery=_appliedWaitingQuery with{PageNumber=Math.Max(1,_appliedWaitingQuery.PageNumber+pageDelta)};
        IsBusy=true;
        try{await LoadWaitingQueueAsync();}
        finally{IsBusy=false;}
    }
    private async Task LoadWaitingQueueAsync()
    {
        try
        {
            var page=await _client.GetTrafficWaitingAsync(_appliedWaitingQuery,CancellationToken.None);
            // The queue may shrink between polls; return to the last available page once.
            var last=Math.Max(1,(int)Math.Ceiling(page.TotalMatched/(double)page.PageSize));
            if(page.PageNumber>last){_appliedWaitingQuery=_appliedWaitingQuery with{PageNumber=last};page=await _client.GetTrafficWaitingAsync(_appliedWaitingQuery,CancellationToken.None);}
            var selected=SelectedWaitingRequest?.StepId;
            _waitingPage=page;Replace(WaitingQueue,page.Items.Select(TrafficRequestRow.From));
            SelectedWaitingRequest=WaitingQueue.FirstOrDefault(r=>r.StepId==selected);
            var suggestions = new[]{""}.Concat(page.BlockingResources).ToArray();
            if (!WaitingBlockingResources.SequenceEqual(suggestions))
            { var pendingInput = WaitingResourceInput; Replace(WaitingBlockingResources,suggestions); WaitingResourceInput = pendingInput; }
            WaitingNotice=$"有效等待 {page.TotalWaiting} · 筛选匹配 {page.TotalMatched} · 第{page.PageNumber}/{Math.Max(1,(int)Math.Ceiling(page.TotalMatched/(double)page.PageSize))}页 · 采集 {page.CapturedAt.LocalDateTime:HH:mm:ss}。"+
                $" 已应用：资源 {(_appliedWaitingQuery.BlockingResourceId is null or ""?"全部":_appliedWaitingQuery.BlockingResourceId)}，最低优先级 {_appliedWaitingQuery.MinimumPriority?.ToString()??"不限"}，排序 {(_appliedWaitingQuery.Sort == TrafficWaitingSort.LongestWait ? "等待时间优先" : "任务优先级优先")}。展示排序不改变调度或授予顺序。"+
                $" 等待预警≥{page.Items.FirstOrDefault()?.WaitingWarningSeconds.ToString()??"—"}秒，长时间等待≥{page.Items.FirstOrDefault()?.WaitingCriticalSeconds.ToString()??"—"}秒；仅提示，不自动解锁。阻塞解除时间为调度观察时间。";
        }
        catch(Exception e){_waitingPage=null;WaitingQueue.Clear();SelectedWaitingRequest=null;WaitingNotice="等待队列读取失败："+e.Message;}
        finally{RaiseCommandStates();}
    }
}
public sealed record WaitingSortChoice(TrafficWaitingSort Value,string Label);
