using System.Collections.ObjectModel;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
namespace AgvDispatch.Wpf.ViewModels;
public sealed partial class TrafficResourceMonitorViewModel
{
    public ObservableCollection<TrafficMapLine> MapEdges { get; } = [];
    public ObservableCollection<TrafficMapLine> MapRoutes { get; } = [];
    public ObservableCollection<TrafficMapArea> MapAreas { get; } = [];
    public ObservableCollection<TrafficMapDot> MapNodes { get; } = [];
    public ObservableCollection<TrafficMapDot> MapVehicles { get; } = [];
    private double _mapCanvasHeight = 500;
    public double MapCanvasHeight { get => _mapCanvasHeight; private set => SetProperty(ref _mapCanvasHeight,value); }
    private TrafficMapSnapshot? _trafficMap;
    private MapDefinition? _cachedDefinition;
    private string? _cachedMapId, _cachedVersionId;
    private string _trafficMapNotice = "地图联动等待加载。";
    public string TrafficMapNotice { get => _trafficMapNotice; private set => SetProperty(ref _trafficMapNotice,value); }
    private async Task RefreshTrafficMapAsync()
    {
        try
        {
            var map=await _client.GetTrafficMapAsync(new(_cachedMapId,_cachedVersionId),CancellationToken.None);
            if(map.Definition!=null) { _cachedDefinition=map.Definition;_cachedMapId=map.MapId;_cachedVersionId=map.VersionId; }
            else if(map.MapId!=_cachedMapId || map.VersionId!=_cachedVersionId) { _cachedDefinition=null;_cachedMapId=null;_cachedVersionId=null; }
            _trafficMap=map with { Definition=_cachedDefinition }; RebuildTrafficMap();
        }
        catch(Exception e) { ClearTrafficMap("地图联动不可用，已清除旧高亮："+e.Message); }
    }
    private void RebuildTrafficMap()
    {
        var drawing=TrafficMapOverlayBuilder.Build(_trafficMap,SelectedTrafficRequest?.SourceSnapshot);
        MapCanvasHeight = drawing.CanvasHeight;
        Replace(MapEdges,drawing.Edges);Replace(MapRoutes,drawing.Routes);Replace(MapAreas,drawing.Areas);
        Replace(MapNodes,drawing.Nodes);Replace(MapVehicles,drawing.Vehicles);TrafficMapNotice=drawing.Notice;
    }
    private void ClearTrafficMap(string reason)
    {
        InvalidateGeometry(reason);
        _trafficMap=null; _cachedDefinition=null; _cachedMapId=null; _cachedVersionId=null;
        MapEdges.Clear();MapRoutes.Clear();MapAreas.Clear();MapNodes.Clear();MapVehicles.Clear();TrafficMapNotice=reason;
    }
}
