using System.ComponentModel;
using AgvDispatch.Wpf.Services;
using Microsoft.Win32;
using Prism.Commands;

namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class ConnectionSettingsViewModel
{
    public IMapReviewReportReader MapReviewReader { get; set; } = new MapReviewReportReader();
    private CancellationTokenSource? _reviewCancellation;
    private long _reviewGeneration;
    private bool _isImportingMapReview, _showOnlyMapDifferences;
    private MapReviewReport? _mapReview;
    private string _mapReviewMessage = "导入离线核对工具生成的review.json及同目录inputs，只读查看，不修改绑定。";
    public MapReviewReport? MapReview { get=>_mapReview;private set{SetProperty(ref _mapReview,value);ResetReviewLocator();RaisePropertyChanged(nameof(MapReviewStations));RaisePropertyChanged(nameof(MapReviewContext));} }
    public string MapReviewMessage { get=>_mapReviewMessage;private set=>SetProperty(ref _mapReviewMessage,value); }
    public bool IsImportingMapReview { get=>_isImportingMapReview;private set{SetProperty(ref _isImportingMapReview,value);ImportMapReviewCommand?.RaiseCanExecuteChanged();} }
    public bool ShowOnlyMapDifferences { get=>_showOnlyMapDifferences;set{if(SetProperty(ref _showOnlyMapDifferences,value))RaisePropertyChanged(nameof(MapReviewStations));} }
    public IReadOnlyList<MapReviewStation> MapReviewStations=>MapReview?.Stations.Where(s=>!ShowOnlyMapDifferences||s.Status!="PASS").ToArray()??[];
    public string MapReviewContext=>MapReview is not { } report?"尚无报告。":
        $"报告车辆别名：{report.VehicleAlias}；车端图：{report.RobotMap}；MD5：{report.RobotMapMd5}\n"+
        $"所选绑定：{SelectedMapBinding?.VehicleId??"未选"} / {SelectedMapBinding?.RobotMap??"未填"} / {SelectedMapBinding?.RobotMapMd5??"未填"}\n"+
        $"文本对照：车辆别名{Compare(SelectedMapBinding?.VehicleId,report.VehicleAlias)}；车端地图{Compare(SelectedMapBinding?.RobotMap,report.RobotMap)}；MD5{Compare(SelectedMapBinding?.RobotMapMd5,report.RobotMapMd5)}。别名可能脱敏，文本相同不证明归属。\n"+
        $"报告本地图/版本：{report.LocalMapId} / {report.LocalMapVersion}；所选绑定：{SelectedMapBinding?.LocalMapId??"未填"} / {SelectedMapBinding?.LocalMapVersion??"未填"}。";
    private static string Compare(string? a,string b)=>string.IsNullOrWhiteSpace(a)||string.IsNullOrWhiteSpace(b)?"未提供":a==b?"相同（仅文本）":"不同";
    public DelegateCommand ImportMapReviewCommand { get;private set; } = null!;
    public DelegateCommand ClearMapReviewCommand { get;private set; } = null!;
    private void InitializeMapReview()
    {
        ImportMapReviewCommand=new DelegateCommand(async()=>
        {
            var dialog=new OpenFileDialog{Title="导入离线核对报告（review.json）",Filter="核对报告JSON|*.json",CheckFileExists=true};
            if(dialog.ShowDialog()==true)await ImportMapReviewAsync(dialog.FileName);
        },()=>!IsImportingMapReview);
        ClearMapReviewCommand=new DelegateCommand(()=>ClearMapReview("已取消导入并清空报告。"));
        InitializeReviewLocator();
    }
    private void MapReviewBindingChanged(object? sender,PropertyChangedEventArgs e)=>ClearMapReview("绑定字段已变化，请重新导入核对报告。");
    private void ClearMapReview(string message)
    {
        _reviewGeneration++;_reviewCancellation?.Cancel();_reviewCancellation=null;
        MapReview=null;IsImportingMapReview=false;MapReviewMessage=message;
    }
    public async Task ImportMapReviewAsync(string path)
    {
        ClearMapReview("正在校验报告和四份输入哈希…");var generation=_reviewGeneration;
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(20));_reviewCancellation=cancellation;IsImportingMapReview=true;
        try
        {
            var result=await MapReviewReader.ReadAsync(path,cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();if(generation!=_reviewGeneration)return;
            MapReview=result;MapReviewMessage=$"已导入{(result.Mode=="synthetic"?"合成模拟":"提交者声明采集")}报告；文件一致性通过。离线结果声明：{(result.ComparisonPassed?"无已列比对错误":"存在差异/错误")}；{result.Stations.Count}站/{result.Routes.Count}路段。未重新计算原始地图，真实验收、坐标对齐、控制与上传仍未批准。";
        }
        catch(OperationCanceledException){if(generation==_reviewGeneration)MapReviewMessage="导入已取消或超过20秒预算，旧报告已清空。";}
        catch(Exception ex){if(generation==_reviewGeneration)MapReviewMessage="导入失败，旧报告已清空："+ex.Message;}
        finally{if(generation==_reviewGeneration){_reviewCancellation=null;IsImportingMapReview=false;}}
    }
}
