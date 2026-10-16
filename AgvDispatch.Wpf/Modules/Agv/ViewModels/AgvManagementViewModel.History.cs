using System.Collections.ObjectModel;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;

namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class AgvManagementViewModel
{
    private long _historyGeneration;
    private CancellationTokenSource? _historyRequest;
    private string _historyMessage="点击刷新记录，查看所选车辆最近100次手动核验；历史记录不能作当前放行证据。";
    public ObservableCollection<RobokitInspectionRecord> InspectionHistory {get;}=[];
    public DelegateCommand RefreshInspectionHistoryCommand {get;private set;}=null!;
    public string HistoryMessage {get=>_historyMessage;private set=>SetProperty(ref _historyMessage,value);}
    private void InitializeHistory()=>RefreshInspectionHistoryCommand=new DelegateCommand(async()=>await RefreshInspectionHistoryAsync());
    private void InvalidateHistory(){_historyGeneration++;_historyRequest?.Cancel();InspectionHistory.Clear();HistoryMessage="当前记录已清除，请刷新所选车辆的历史。";}
    public async Task RefreshInspectionHistoryAsync()
    {
        if(!_active || SelectedDevice is null)return;
        _historyRequest?.Cancel();
        var generation=++_historyGeneration;var vehicle=SelectedDevice.Id;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));_historyRequest=timeout;
        try
        {
            var records=await _protocol.GetRobokitInspectionHistoryAsync(new(vehicle,100),timeout.Token);
            if(!_active || generation!=_historyGeneration || vehicle!=SelectedDevice?.Id)return;
            if(records.Count>100 || records.Any(r=>r.VehicleId!=vehicle))throw new System.IO.InvalidDataException("核验历史车辆归属或数量不符。");
            InspectionHistory.Clear();foreach(var row in records)InspectionHistory.Add(row);
            HistoryMessage=$"所选车辆最近 {records.Count} 条手动核验记录；新核验后需刷新。历史状态不代表当前车端状态。";
        }
        catch(Exception ex){if(_active&&generation==_historyGeneration){InspectionHistory.Clear();HistoryMessage="核验记录读取失败："+ex.Message;}}
        finally{if(ReferenceEquals(_historyRequest,timeout))_historyRequest=null;}
    }
}
