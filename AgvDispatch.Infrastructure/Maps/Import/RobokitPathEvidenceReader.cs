using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgvDispatch.Domain.Maps;
namespace AgvDispatch.Infrastructure.Maps;

public static class RobokitPathEvidenceReader
{
    public static MapDefinition Attach(byte[] planeBytes,byte[] originalBytes,MapDefinition definition)
    {
        var offset=planeBytes.AsSpan().StartsWith(new byte[]{0xEF,0xBB,0xBF})?3:0;
        using var document=JsonDocument.Parse(planeBytes.AsMemory(offset),new(){MaxDepth=64});var root=document.RootElement;
        var sha=Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
        var planeSha=Convert.ToHexString(SHA256.HashData(planeBytes)).ToLowerInvariant();
        var curves=root.TryGetProperty("advancedCurveList",out var list)?list.EnumerateArray().ToArray():[];
        var result=definition.Edges.ToArray();var next=0;var budget=0;
        for(var index=0;index<curves.Length;index++)
        {
            var curve=curves[index];var kind=Text(curve,"className");
            if(kind is not ("StraightPath" or "BezierPath" or "DegenerateBezier" or "ArcPath"))continue;
            if(next>=result.Length)throw new InvalidDataException("厂家路径证据与已转换道路数量不一致。");
            var edge=result[next];var from=Text(curve.GetProperty("startPos"),"instanceName");var to=Text(curve.GetProperty("endPos"),"instanceName");
            var a=definition.Nodes.Single(n=>n.Id==edge.From);var b=definition.Nodes.Single(n=>n.Id==edge.To);
            if(edge.Id!="SMAP-PATH-"+next||a.AgvSiteId!=from||b.AgvSiteId!=to)
                throw new InvalidDataException("厂家路径证据无法精确对应转换后的方向/道路，不按端点猜测。");
            var present=curve.TryGetProperty("property",out var properties);var raw=present?properties.GetRawText():null;
            budget+=raw is null?0:Encoding.UTF8.GetByteCount(raw);
            if(raw?.Length>65536||budget>2*1024*1024||present&&properties.ValueKind==JsonValueKind.Array&&properties.GetArrayLength()>512)
                throw new InvalidDataException("厂家路径属性超过只读证据预算，拒绝静默截断。");
            result[next]=edge with{VendorPath=new(1,sha,planeSha,index,Text(curve,"instanceName"),kind,from,to,present,raw,
                edge.Id,edge.From,edge.To,a.X,a.Y,b.X,b.Y,edge.GeometryKind,edge.ControlPoints?.ToArray())};
            next++;
        }
        if(next!=result.Length)throw new InvalidDataException("有已转换道路缺少精确原始路径证据。");
        var header=root.GetProperty("header");
        return definition with{Edges=result,RobokitSource=new(sha,planeSha,Text(header,"mapName"),Text(header,"version"),curves.Length,next)};
    }
    private static string Text(JsonElement value,string key)=>value.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()??"":"";
}
