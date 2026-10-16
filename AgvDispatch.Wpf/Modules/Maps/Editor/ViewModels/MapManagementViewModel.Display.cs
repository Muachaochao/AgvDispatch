using System.Windows;
using System.Windows.Media;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class MapManagementViewModel
{
    private bool _showLabels=true,_sparseLabels=true,_vendorLabels=true,_directions=true,_onlyCurrentLayer,_scan=true,_annotations=true,_restoringDisplay;
    private ImageSource? _scanImage,_annotationImage;
    public ImageSource? SmapScanImage {get=>_scanImage;private set=>SetProperty(ref _scanImage,value);}
    public ImageSource? SmapAnnotationImage {get=>_annotationImage;private set=>SetProperty(ref _annotationImage,value);}
    public bool HasSmapBackgroundLayers=>SmapScanImage is not null&&SmapAnnotationImage is not null;
    public bool UseCompositeUnderlay=>!HasSmapBackgroundLayers;
    public string BackgroundLayerHint=>HasSmapBackgroundLayers?"扫描点/普通线与厂家禁行线/区域轮廓可分别隐藏；不改变导航数据。":"当前底图是合成图片/CAD。可整体隐藏或调淡；旧Smap重新导入为新草稿后可分项显示，原草稿保留。";
    public bool ShowScanLayer {get=>_scan;set{if(SetProperty(ref _scan,value))RefreshDisplay();}}
    public bool ShowSmapAnnotations {get=>_annotations;set{if(SetProperty(ref _annotations,value))RefreshDisplay();}}
    public bool ShowNodeLabels {get=>_showLabels;set{if(SetProperty(ref _showLabels,value))RefreshDisplay();}}
    public bool SparseNodeLabels {get=>_sparseLabels;set{if(SetProperty(ref _sparseLabels,value))RefreshDisplay();}}
    public bool UseVendorSiteLabels {get=>_vendorLabels;set{if(SetProperty(ref _vendorLabels,value))RefreshDisplay();}}
    public bool ShowDirectionArrows {get=>_directions;set{if(SetProperty(ref _directions,value))RefreshDisplay();}}
    public bool OnlyCurrentLayer {get=>_onlyCurrentLayer;set{if(SetProperty(ref _onlyCurrentLayer,value)){ClearHiddenSelection();RefreshDisplay();}}}
    public string LabelDisplaySummary {get;private set;}="";
    private void RefreshDisplay(){if(!_restoringDisplay)RebuildPreview();}
    private void ClearHiddenSelection()
    {
        CancelWorkbenchTool();UpdateBatchNodeSelection([]);UpdateBatchEdgeSelection([]);SelectedNode=null;SelectedEdge=null;SelectedResource=null;
    }
    private void LoadDisplayImages(MapProjectSummary project)
    {
        SmapScanImage=LoadImage(project.SmapScanPngPath??"");SmapAnnotationImage=LoadImage(project.SmapAnnotationPngPath??"");
        RaisePropertyChanged(nameof(HasSmapBackgroundLayers));RaisePropertyChanged(nameof(UseCompositeUnderlay));RaisePropertyChanged(nameof(BackgroundLayerHint));
    }
    private void RestoreDisplay(MapDisplaySettings? value)
    {
        value??=new();_restoringDisplay=true;
        try{ShowNodeLayer=value.Nodes;ShowEdgeLayer=value.Edges;ShowResourceLayer=value.Resources;ShowVehicleLayer=value.Vehicles;ShowUnderlayLayer=value.Underlay;ShowScanLayer=value.Scan;ShowSmapAnnotations=value.Annotations;UnderlayOpacity=value.UnderlayOpacity;ShowNodeLabels=value.Labels;SparseNodeLabels=value.SparseLabels;UseVendorSiteLabels=value.VendorLabels;ShowDirectionArrows=value.Directions;OnlyCurrentLayer=value.OnlyCurrentLayer;}
        finally{_restoringDisplay=false;}
    }
    private MapDisplaySettings CaptureDisplay()=>new(){Nodes=ShowNodeLayer,Edges=ShowEdgeLayer,Resources=ShowResourceLayer,Vehicles=ShowVehicleLayer,Underlay=ShowUnderlayLayer,Scan=ShowScanLayer,Annotations=ShowSmapAnnotations,UnderlayOpacity=UnderlayOpacity,Labels=ShowNodeLabels,SparseLabels=SparseNodeLabels,VendorLabels=UseVendorSiteLabels,Directions=ShowDirectionArrows,OnlyCurrentLayer=OnlyCurrentLayer};
    private void ApplyDisplayReadability()
    {
        var nodes=Nodes.ToDictionary(n=>n.Id,StringComparer.OrdinalIgnoreCase);var selected=_batchSelectedNodes.Select(n=>n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(SelectedNode is { } current)selected.Add(current.Id);
        var scale=1/Math.Max(.1,CanvasZoom);var occupied=new Dictionary<(int,int),List<Rect>>();var labels=0;
        bool Reserve(Rect rect,bool force)
        {
            var x0=(int)Math.Floor(rect.Left/64);var x1=(int)Math.Floor(rect.Right/64);var y0=(int)Math.Floor(rect.Top/32);var y1=(int)Math.Floor(rect.Bottom/32);
            if(!force)for(int x=x0;x<=x1;x++)for(int y=y0;y<=y1;y++)if(occupied.TryGetValue((x,y),out var existing)&&existing.Any(r=>r.IntersectsWith(rect)))return false;
            for(int x=x0;x<=x1;x++)for(int y=y0;y<=y1;y++){if(!occupied.TryGetValue((x,y),out var bucket))occupied[(x,y)]=bucket=[];bucket.Add(rect);}return true;
        }
        var ordered=PreviewNodes.Select((n,i)=>(n,i)).OrderByDescending(x=>selected.Contains(x.n.Id)).ThenBy(x=>x.n.Id,StringComparer.OrdinalIgnoreCase).ToArray();
        foreach(var item in ordered)
        {
            var n=item.n;var source=nodes[n.Id];var text=UseVendorSiteLabels&&!string.IsNullOrWhiteSpace(source.AgvSiteId)?source.AgvSiteId:n.Id;
            var width=Math.Min(210,text.Length*8+8)*scale;var top=7+9*scale;var left=7+8*scale;
            var rect=new Rect(n.Left+left,n.Top+top,width,16*scale);
            var show=ShowNodeLabels&&ShowNodeLayer&&n.Opacity>0&&(!SparseNodeLabels||Reserve(rect,selected.Contains(n.Id)));
            if(show)labels++;
            PreviewNodes[item.i]=n with{Label=text,ShowLabel=show,GlyphScale=scale*(SparseNodeLabels&&Nodes.Count>80?.75:1),LabelScale=scale,LabelLeft=left,LabelTop=top,Tooltip=$"节点 {n.Id} / 厂家 {source.AgvSiteId}\nX={source.X:G8}m Y={source.Y:G8}m"};
        }
        var arrows=new HashSet<(int,int)>();
        for(int i=0;i<PreviewEdges.Count;i++)
        {
            var e=PreviewEdges[i];var key=((int)(e.MidX/(20*scale)),(int)(e.MidY/(20*scale)));var priority=SelectedEdge?.Id==e.Id;
            var show=ShowDirectionArrows&&ShowEdgeLayer&&e.Opacity>0&&(!SparseNodeLabels||priority||arrows.Add(key));
            PreviewEdges[i]=e with{GlyphScale=scale,ShowDirection=show};
        }
        LabelDisplaySummary=$"显示标签 {labels}/{PreviewNodes.Count}；自动稀疏只影响文字/箭头，放大可显示更多，选中点优先；方向以路段属性为准。";RaisePropertyChanged(nameof(LabelDisplaySummary));
    }
}
