using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgvDispatch.Domain.Maps;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AgvDispatch.Infrastructure.Maps;

/// <summary>Conservative offline display conversion; original bytes remain authoritative.</summary>
public sealed class Smap2DImportService
{
    public sealed record Result(MapDefinition Definition,double OffsetX,double OffsetY,double Width,double Height,
        string OriginalMapName,string FormatVersion,string Sha256,string Md5,int OrdinaryPoints,
        IReadOnlyList<string> Warnings,byte[] BackgroundPixels)
    {
        public byte[] ScanPixels {get;init;}=[];
        public byte[] AnnotationPixels {get;init;}=[];
    }
    public Result Read(byte[] bytes,string retainedSource)
    {
        if(bytes.Length is 0 or >64*1024*1024)throw new InvalidDataException("Smap文件为空或超过64MB。");
        if(bytes.Length>1 && bytes[0]==0x50 && bytes[1]==0x4B)throw new NotSupportedException("3D ZIP Smap尚未支持；请选择2D JSON Smap。");
        var start=bytes.AsSpan().StartsWith(new byte[]{0xEF,0xBB,0xBF})?3:0;
        using var doc=JsonDocument.Parse(bytes.AsMemory(start),new(){MaxDepth=64});var root=doc.RootElement;
        var header=root.GetProperty("header");
        if(Text(header,"mapType")!="2D-Map")throw new NotSupportedException("仅支持2D-Map；未猜测其他地图类型。");
        var version=Text(header,"version");
        if(!Regex.IsMatch(version,@"^1\.0\.\d+$"))throw new NotSupportedException("未核定Smap格式版本："+version+"；当前支持已留存的1.0.x格式。");
        var name=Text(header,"mapName");if(string.IsNullOrWhiteSpace(name)||name.Length>256)throw new InvalidDataException("Smap缺少mapName或名称过长。");
        var min=header.GetProperty("minPos");var max=header.GetProperty("maxPos");var minX=Number(min,"x")-1;var minY=Number(min,"y")-1;
        var width=Number(max,"x")-minX+1;var height=Number(max,"y")-minY+1;
        if(!double.IsFinite(width)||!double.IsFinite(height)||width<=2||height<=2 || width>1e6 || height>1e6)throw new InvalidDataException("Smap边界无效或超过显示预算。");
        var resolution=Number(header,"resolution");if(resolution<=0)throw new InvalidDataException("Smap分辨率必须大于0。");
        MapPoint2D Translate(JsonElement p)=>new(Number(p,"x")-minX,Number(p,"y")-minY);
        var warnings=new List<string>{"Smap离线显示转换：节点和路径停用；源属性、方向和动作保留在原图，尚未确认现场坐标绑定。"};
        var nodes=new List<MapNode>();var sites=new Dictionary<string,MapNode>(StringComparer.OrdinalIgnoreCase);
        foreach(var point in Items(root,"advancedPointList",10000))
        {
            var id=Text(point,"instanceName");if(string.IsNullOrWhiteSpace(id)||id.Length>256||sites.ContainsKey(id))throw new InvalidDataException("Smap高级点编号为空、过长或重复（包含大小写冲突）。");
            var pos=Translate(point.GetProperty("pos"));var kind=Text(point,"className");
            var type=kind switch{"LocationMark" or "LandMark"=>MapNodeType.Waypoint,"ActionPoint" or "TransferLocation" or "WorkLocation" or "WorkingLocation"=>MapNodeType.Station,"ParkPoint"=>MapNodeType.WaitingPoint,"ChargePoint"=>MapNodeType.ChargingPoint,_=>MapNodeType.Waypoint};
            if(kind is not ("LocationMark" or "LandMark" or "ActionPoint" or "TransferLocation" or "WorkLocation" or "WorkingLocation" or "ParkPoint" or "ChargePoint"))warnings.Add("未知高级点类型仅作停用显示："+kind+" / "+id);
            var n=new MapNode("BUS-"+id,pos.X,pos.Y,type,false,[],id);nodes.Add(n);sites.Add(id,n);
        }
        var edges=new List<MapEdge>();
        foreach(var curve in Items(root,"advancedCurveList",20000))
        {
            var startPoint=curve.GetProperty("startPos");var endPoint=curve.GetProperty("endPos");
            var s=Text(startPoint,"instanceName");var t=Text(endPoint,"instanceName");
            if(!sites.TryGetValue(s,out var from)||!sites.TryGetValue(t,out var to))throw new InvalidDataException("Smap路径引用不存在的高级点。");
            foreach(var p in new[]{startPoint,endPoint})if(p.TryGetProperty("pos",out var ep))
            {
                var xy=Translate(ep);var n=sites[Text(p,"instanceName")];
                if(Math.Abs(xy.X-n.X)>.001 || Math.Abs(xy.Y-n.Y)>.001)throw new InvalidDataException("Smap路径内嵌端点与站点坐标不一致。");
            }
            var kind=Text(curve,"className");
            if(kind is not ("StraightPath" or "BezierPath" or "DegenerateBezier")){warnings.Add("未转换路径几何，源对象保留："+kind+" / "+Text(curve,"instanceName"));continue;}
            // Keep both directed entries and their actual control points. A degenerate
            // Bezier is still a cubic; do not infer direction or merge reverse paths.
            var controls=kind is "BezierPath" or "DegenerateBezier"?new[]{Translate(curve.GetProperty("controlPos1")),Translate(curve.GetProperty("controlPos2"))}:null;
            var edge=new MapEdge("SMAP-PATH-"+edges.Count,from.Id,to.Id,1,1,false,false,[]){GeometryKind=controls is null?MapEdgeGeometryKind.Line:MapEdgeGeometryKind.CubicBezier,ControlPoints=controls};
            if(!MapEdgeGeometry.TryValidate(edge,from,to,out var error))throw new InvalidDataException("Smap路径几何无效："+error);
            edges.Add(edge with{Distance=MapEdgeGeometry.Length(edge,from,to)});
        }
        var scanPixels=new byte[900*520*4];var annotationPixels=new byte[900*520*4];var activePixels=scanPixels;
        var pixels=new byte[900*520*4];for(var i=0;i<pixels.Length;i+=4){pixels[i]=247;pixels[i+1]=247;pixels[i+2]=247;pixels[i+3]=255;}
        (double X,double Y) Pixel(JsonElement p)=>((Number(p,"x")-minX)/width*900,(1-(Number(p,"y")-minY)/height)*520);
        void Dot(double x,double y,byte red,byte green,byte blue){if(x<0||x>=900||y<0||y>=520)return;var i=((int)y*900+(int)x)*4;pixels[i]=red;pixels[i+1]=green;pixels[i+2]=blue;activePixels[i]=red;activePixels[i+1]=green;activePixels[i+2]=blue;activePixels[i+3]=255;}
        void Line(JsonElement a,JsonElement b,byte red,byte green,byte blue)
        {
            var s=Pixel(a);var e=Pixel(b);var steps=(int)Math.Min(4096,Math.Max(1,Math.Ceiling(Math.Max(Math.Abs(e.X-s.X),Math.Abs(e.Y-s.Y)))));
            for(var j=0;j<=steps;j++)Dot(s.X+(e.X-s.X)*j/steps,s.Y+(e.Y-s.Y)*j/steps,red,green,blue);
        }
        var normal=0;
        foreach(var key in new[]{"normalPosList","rssiPosList","reflectorPosList","tagPosList"})foreach(var p in Items(root,key,2000000)){var q=Pixel(p);Dot(q.X,q.Y,110,110,110);if(key=="normalPosList")normal++;}
        foreach(var line in Items(root,"normalLineList",100000))Line(line.GetProperty("startPos"),line.GetProperty("endPos"),110,110,110);
        activePixels=annotationPixels;
        foreach(var line in Items(root,"advancedLineList",100000)){var geometry=line.GetProperty("line");Line(geometry.GetProperty("startPos"),geometry.GetProperty("endPos"),205,80,80);}
        foreach(var area in Items(root,"advancedAreaList",10000))
        {
            var points=Items(area,"posGroup",10000);if(points.Length<3)throw new InvalidDataException("Smap区域少于3个顶点。");
            for(var i=0;i<points.Length;i++)Line(points[i],points[(i+1)%points.Length],50,120,180);
        }
        if(Items(root,"primitiveList",20000).Length>0)warnings.Add("Primitive图元尚未转换，完整对象保留在源图。");
        var definition=new MapDefinition(name+"（Smap预览）",retainedSource,"SmapWorldTranslated","m",nodes,edges,[]){EditorSettings=new(width,height,Math.Max(1,width/20),true,false,false)};
        return new(definition,minX,minY,width,height,name,version,Convert.ToHexString(SHA256.HashData(bytes)),Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(),normal,warnings,pixels){ScanPixels=scanPixels,AnnotationPixels=annotationPixels};
    }
    public static void SaveBackground(Result result,string path)
    {
        using var image=Image.LoadPixelData<Rgba32>(result.BackgroundPixels,900,520);image.SaveAsPng(path);
    }
    public static void SavePixels(byte[] pixels,string path)
    {
        using var image=Image.LoadPixelData<Rgba32>(pixels,900,520);image.SaveAsPng(path);
    }
    private static string Text(JsonElement p,string key)=>p.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
    private static double Number(JsonElement p,string key)
    {
        if(!p.TryGetProperty(key,out var v))return 0; // Protobuf JSON default numeric fields may be omitted.
        if(v.ValueKind!=JsonValueKind.Number||!v.TryGetDouble(out var n)||!double.IsFinite(n)||Math.Abs(n)>1e6)throw new InvalidDataException("Smap字段不是预算内有限数字："+key);
        return n;
    }
    private static JsonElement[] Items(JsonElement p,string key,int max)
    {
        if(!p.TryGetProperty(key,out var v))return [];
        if(v.ValueKind!=JsonValueKind.Array||v.GetArrayLength()>max)throw new InvalidDataException("Smap数组类型或预算无效："+key);
        return v.EnumerateArray().ToArray();
    }
}
