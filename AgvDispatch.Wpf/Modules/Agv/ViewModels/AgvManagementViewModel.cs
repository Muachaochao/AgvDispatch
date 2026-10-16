using System.Collections.ObjectModel;
using System.IO;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;
using Prism.Regions;
namespace AgvDispatch.Wpf.ViewModels;
public sealed partial class AgvManagementViewModel : DeviceMonitorViewModel
{
    private readonly ITrafficControlClient _protocol;
    private CancellationTokenSource? _inspection;
    private string? _vehicle;
    private bool _active;
    private long _generation;
    private bool _busy;
    private string _inspectionSummary = "选择车辆后点击只读核验；不会下发导航、抢占控制权或修改地图。";
    private string _sourceNodeId = "", _targetNodeId = "";
    public AgvManagementViewModel(IDeviceMonitoringClient client, ITrafficControlClient protocol) : base(client, true)
    {
        _protocol = protocol;
        InitializeHistory();
        InitializeTransfers();
        InspectRobokitCommand = new DelegateCommand(async () => await InspectRobokitAsync(), () => !_busy && _active && SelectedDevice is not null);
        PropertyChanged += (_, e) =>
        {
            if(e.PropertyName != nameof(SelectedDevice)) return;
            if(_vehicle != SelectedDevice?.Id) { _vehicle = SelectedDevice?.Id; Invalidate(); }
            InspectRobokitCommand.RaiseCanExecuteChanged();
        };
    }
    public DelegateCommand InspectRobokitCommand { get; }
    public ObservableCollection<RobokitStation> RobotStations { get; } = [];
    public ObservableCollection<RobokitNodeMapping> RobotMappings { get; } = [];
    public string InspectionSummary { get => _inspectionSummary; private set => SetProperty(ref _inspectionSummary,value); }
    public string SourceNodeId { get => _sourceNodeId; set { if(SetProperty(ref _sourceNodeId,value)) Invalidate(); } }
    public string TargetNodeId { get => _targetNodeId; set { if(SetProperty(ref _targetNodeId,value)) Invalidate(); } }
    private void Invalidate()
    {
        InvalidateHistory();
        InvalidateTransfers();
        _generation++; _inspection?.Cancel(); RobotStations.Clear(); RobotMappings.Clear();
        InspectionSummary = "只读结果已清除，请对当前车辆重新核验。";
    }
    public async Task InspectRobokitAsync()
    {
        if(_busy || !_active || SelectedDevice is null) return;
        var vehicle = SelectedDevice.Id; var generation = ++_generation;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); _inspection = timeout;
        _busy = true; InspectRobokitCommand.RaiseCanExecuteChanged();
        RobotStations.Clear(); RobotMappings.Clear(); InspectionSummary = "正在只读查询车端版本、地图、站点与控制权…";
        try
        {
            var result = await _protocol.GetRobokitInspectionAsync(new(vehicle,SourceNodeId.Trim(),TargetNodeId.Trim()),timeout.Token);
            if(!_active || generation != _generation || vehicle != SelectedDevice?.Id) return;
            if(result.VehicleId != vehicle) throw new InvalidDataException("后台返回车辆与请求不一致。");
            InspectionSummary = $"{result.CheckedAt.ToLocalTime():yyyy.MM.dd HH:mm:ss} · {result.Reason}\n"+
                $"Robokit {result.RobokitVersion ?? "未知"} / 网络协议 {result.ProtocolVersion ?? "未知"} · 车端地图 {result.RobotMap ?? "未知"} · MD5 {result.RobotMapMd5 ?? "未知"} · 载入状态 {(result.LoadMapStatus switch { 0 => "失败", 1 => "成功", 2 => "载入中", _ => "未知" })}\n"+
                $"后台地图 {result.LocalMapId ?? "无"} / {result.LocalMapVersion ?? "无"} · 控制权 {(result.ControlLocked switch { true => "已被占用", false => "未被占用", _ => "未知" })} / {result.ControlOwner ?? "未知"}\n"+
                $"路径：{string.Join(" → ",result.Route)}\n{string.Join("；",result.Issues)}";
            // Unstable observations are not presented as successful station/mapping evidence.
            if(result.Available && result.Stable)
            {
                foreach(var station in result.Stations) RobotStations.Add(station);
                foreach(var mapping in result.Mappings) RobotMappings.Add(mapping);
            }
        }
        catch(Exception ex)
        {
            if(_active && generation == _generation) { RobotStations.Clear(); RobotMappings.Clear(); InspectionSummary = "只读核验不可用："+ex.Message; }
        }
        finally { if(ReferenceEquals(_inspection,timeout)) _inspection = null; _busy=false; InspectRobokitCommand.RaiseCanExecuteChanged(); }
    }
    public override void OnNavigatedTo(NavigationContext context) { _active=true; base.OnNavigatedTo(context); InspectRobokitCommand.RaiseCanExecuteChanged(); }
    public override void OnNavigatedFrom(NavigationContext context) { _active=false; Invalidate(); base.OnNavigatedFrom(context); InspectRobokitCommand.RaiseCanExecuteChanged(); }
}
