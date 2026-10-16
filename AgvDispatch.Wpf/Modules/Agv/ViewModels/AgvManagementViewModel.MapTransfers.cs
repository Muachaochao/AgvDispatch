using System.Collections.ObjectModel;
using System.Globalization;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;
namespace AgvDispatch.Wpf.ViewModels;
public sealed partial class AgvManagementViewModel
{
    private long _transferListGeneration,_transferDetailGeneration;
    private CancellationTokenSource? _transferListRequest,_transferDetailRequest;
    private string _transferState="",_transferFrom="",_transferTo="",_transferMessage="选择车辆并刷新操作记录。现场写入关闭；历史不能作当前放行证据。",_transferDetail="请选择记录查看只读历史与处置条件。";
    private int _transferPage=1,_transferTotal,_transferHistoryPage=1,_transferHistoryTotal;
    private MapTransferRecord? _selectedTransfer;
    public ObservableCollection<MapTransferRecord> MapTransfers {get;}=[];
    public ObservableCollection<MapTransferEventSnapshot> MapTransferEvents {get;}=[];
    public DelegateCommand RefreshMapTransfersCommand {get;private set;}=null!;
    public DelegateCommand PreviousMapTransfersCommand {get;private set;}=null!;
    public DelegateCommand NextMapTransfersCommand {get;private set;}=null!;
    public DelegateCommand PreviousMapTransferHistoryCommand {get;private set;}=null!;
    public DelegateCommand NextMapTransferHistoryCommand {get;private set;}=null!;
    public DelegateCommand ObserveMapTransferCommand {get;private set;}=null!;
    public string TransferState {get=>_transferState;set{if(SetProperty(ref _transferState,value))InvalidateTransfers();}}
    public string TransferFrom {get=>_transferFrom;set{if(SetProperty(ref _transferFrom,value))InvalidateTransfers();}}
    public string TransferTo {get=>_transferTo;set{if(SetProperty(ref _transferTo,value))InvalidateTransfers();}}
    public string TransferMessage {get=>_transferMessage;private set=>SetProperty(ref _transferMessage,value);}
    public string TransferDetail {get=>_transferDetail;private set=>SetProperty(ref _transferDetail,value);}
    public MapTransferRecord? SelectedTransfer {get=>_selectedTransfer;set{if(SetProperty(ref _selectedTransfer,value)){ClearTransferDetail();if(value is not null)_=LoadTransferDetailAsync(false);}}}
    private void InitializeTransfers()
    {
        RefreshMapTransfersCommand=new(async()=>await LoadTransfersAsync(1));
        PreviousMapTransfersCommand=new(async()=>{if(_transferPage>1)await LoadTransfersAsync(_transferPage-1);});
        NextMapTransfersCommand=new(async()=>{if(_transferPage*25<_transferTotal)await LoadTransfersAsync(_transferPage+1);});
        PreviousMapTransferHistoryCommand=new(async()=>{if(_transferHistoryPage>1)await LoadTransferDetailAsync(false,_transferHistoryPage-1);});
        NextMapTransferHistoryCommand=new(async()=>{if(_transferHistoryPage*25<_transferHistoryTotal)await LoadTransferDetailAsync(false,_transferHistoryPage+1);});
        ObserveMapTransferCommand=new(async()=>await LoadTransferDetailAsync(true));
    }
    private void ClearTransferDetail()
    {
        _transferDetailGeneration++;_transferDetailRequest?.Cancel();MapTransferEvents.Clear();_transferHistoryPage=1;_transferHistoryTotal=0;
        TransferDetail="请选择记录查看只读历史与处置条件。";
    }
    private void InvalidateTransfers()
    {
        _transferListGeneration++;_transferListRequest?.Cancel();SelectedTransfer=null;ClearTransferDetail();MapTransfers.Clear();_transferPage=1;_transferTotal=0;
        TransferMessage="筛选或车辆已改变，请刷新记录。历史不代表当前车辆状态。";
    }
    private static DateTimeOffset? ParseTransferTime(string text)
    {
        if(string.IsNullOrWhiteSpace(text))return null;
        if(!DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.AllowWhiteSpaces,out var time))throw new ArgumentException("时间格式无效，请用 yyyy-MM-dd HH:mm:ss 或带时区的ISO时间。");
        return time;
    }
    public async Task LoadTransfersAsync(int page=1)
    {
        if(!_active||SelectedDevice is null)return;
        _transferListRequest?.Cancel();var generation=++_transferListGeneration;var vehicle=SelectedDevice.Id;
        SelectedTransfer=null;ClearTransferDetail();MapTransfers.Clear();TransferMessage="正在查询后台记录…";
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));_transferListRequest=timeout;
        try
        {
            var query=new MapTransferListQuery(vehicle,State:string.IsNullOrWhiteSpace(TransferState)?null:TransferState.Trim(),From:ParseTransferTime(TransferFrom),To:ParseTransferTime(TransferTo),Page:page);
            var result=await _protocol.ListMapTransfersAsync(query,timeout.Token).WaitAsync(timeout.Token);
            if(!_active||generation!=_transferListGeneration||vehicle!=SelectedDevice?.Id)return;
            if(result.Page!=page||result.PageSize!=25||result.Total<0||result.Items.Count>25||result.Items.Any(x=>x.VehicleId!=vehicle)||result.Items.Select(x=>x.OperationId).Distinct().Count()!=result.Items.Count)throw new InvalidOperationException("后台记录归属或分页数据异常。");
            _transferPage=page;_transferTotal=result.Total;foreach(var item in result.Items)MapTransfers.Add(item);
            TransferMessage=$"第 {page} 页 · 共 {result.Total} 条 · 每页25条（按创建时间筛选；无时区按本机时间）。新增记录后请刷新。";
        }
        catch(Exception ex){if(_active&&generation==_transferListGeneration){MapTransfers.Clear();_transferTotal=0;TransferMessage="记录查询失败："+ex.Message;}}
        finally{if(ReferenceEquals(_transferListRequest,timeout))_transferListRequest=null;}
    }
    public async Task LoadTransferDetailAsync(bool observe=false,int page=1)
    {
        if(!_active||SelectedTransfer is not { } selected||SelectedDevice?.Id!=selected.VehicleId)return;
        _transferDetailRequest?.Cancel();var generation=++_transferDetailGeneration;MapTransferEvents.Clear();TransferDetail=observe?"正在只读采集复核；不会关闭异常或补发…":"正在读取历史和处置条件…";
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(observe?25:10));_transferDetailRequest=timeout;
        bool Current()=>_active&&generation==_transferDetailGeneration&&SelectedTransfer?.OperationId==selected.OperationId&&SelectedDevice?.Id==selected.VehicleId;
        try
        {
            if(observe)await _protocol.ObserveMapTransferAsync(new(selected.OperationId),timeout.Token).WaitAsync(timeout.Token);
            if(!Current())return;
            var review=await _protocol.GetMapTransferReviewAsync(new(selected.OperationId),timeout.Token).WaitAsync(timeout.Token);
            if(!Current())return;
            var history=await _protocol.GetMapTransferHistoryAsync(new(selected.OperationId,page),timeout.Token).WaitAsync(timeout.Token);
            if(!Current())return;
            if(review.Record.OperationId!=selected.OperationId||review.Record.VehicleId!=selected.VehicleId||history.OperationId!=selected.OperationId||history.Page!=page||history.PageSize!=25||history.Total<0||history.Items.Count>25||review.CanClose||review.CanResend||review.CanRelease||review.Record.WritesEnabled||review.ObservationProvesThisWrite)throw new InvalidOperationException("后台只读处置契约或记录归属不符。");
            _transferHistoryPage=page;_transferHistoryTotal=history.Total;foreach(var item in history.Items)MapTransferEvents.Add(item);
            var record=review.Record;
            TransferDetail=$"操作 {record.OperationId} · {record.State} · 已尝试 {record.AttemptCount} 次 · 历史第 {page} 页 / 共 {history.Total} 项\n源SHA256：{record.SourceSha256}\n目标地图：{record.TargetMapName} / 后台 {record.MapId}:{record.VersionId}\n预期厂家MD5：{record.ExpectedDeviceMd5}\n需处置：{review.RequiresDisposition}；关闭、补发、放行入口均未开放。\n当前复核阻塞：{string.Join("；",record.Blockers)}\n处置所需证据：\n{string.Join("\n",review.RequiredEvidence)}\n禁止：{string.Join("；",review.ProhibitedActions)}\n最近观察（历史，不证明本次写入）：{record.LastObservation}";
        }
        catch(Exception ex){if(Current()){MapTransferEvents.Clear();_transferHistoryTotal=0;TransferDetail="只读历史/复核不可用："+ex.Message;}}
        finally{if(ReferenceEquals(_transferDetailRequest,timeout))_transferDetailRequest=null;}
    }
}
