using System.IO.Compression;
using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Infrastructure.Maps;

public sealed class RobokitPointCloudSourceReader
{
    public async Task<PointCloudData> ReadFileAsync(string path,CancellationToken ct=default)
    {
        var extension=Path.GetExtension(path).ToLowerInvariant();
        if(extension==".xyz")return await new XyzPointCloudReader().ReadFileAsync(path,ct);
        if(extension is not (".smap" or ".3dsmap"))throw new NotSupportedException("仅支持XYZ、Robokit 3D.smap包或独立.3dsmap二进制。");
        var cap=extension==".smap"?64*1024*1024:RobokitMap3DReader.MaxBytes;
        await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,64*1024,true);
        if(stream.Length<=0||stream.Length>cap)throw new InvalidDataException("点云输入文件为空或超过大小预算。");
        var bytes=new byte[(int)stream.Length];await stream.ReadExactlyAsync(bytes,ct);if(stream.ReadByte()!=-1)throw new InvalidDataException("读取期间文件长度变化。");
        return await Task.Run(()=>extension==".3dsmap"?new RobokitMap3DReader().Read(bytes,path,ct):ReadPackage(bytes,path,ct),ct);
    }
    public PointCloudData ReadPackage(byte[] bytes,string source,CancellationToken ct=default)
    {
        // Use the existing ZIP length, CRC, hash and entry-path checks before decoding the native payload.
        var package=new Smap3DPackageReader().Read(bytes,source,ct);
        using var input=new MemoryStream(bytes,false);using var zip=new ZipArchive(input,ZipArchiveMode.Read);
        var entry=zip.GetEntry("0.3dsmap")!;var cloud=new byte[checked((int)entry.Length)];using(var stream=entry.Open())
        {
            var offset=0;while(offset<cloud.Length){ct.ThrowIfCancellationRequested();var n=stream.Read(cloud,offset,Math.Min(64*1024,cloud.Length-offset));if(n==0)throw new InvalidDataException("3D条目读取截断。");offset+=n;}
            if(stream.ReadByte()!=-1)throw new InvalidDataException("3D条目超出长度声明。");
        }
        var result=new RobokitMap3DReader().Read(cloud,source,ct);var native=result.NativeMap!;
        if(!string.Equals(package.Plane.OriginalMapName,native.MapName,StringComparison.Ordinal))throw new InvalidDataException("同包二维/三维地图名称不一致，拒绝建立关联。");
        var minX=package.Plane.OffsetX+1;var minY=package.Plane.OffsetY+1;
        var maxX=package.Plane.OffsetX+package.Plane.Width-1;var maxY=package.Plane.OffsetY+package.Plane.Height-1;
        if(maxX<native.HeaderMin.X||minX>native.HeaderMax.X||maxY<native.HeaderMin.Y||minY>native.HeaderMax.Y)throw new InvalidDataException("同包二维/三维XY边界不重叠，不能关联。");
        var nodes=package.Plane.Definition.Nodes;var within=nodes.Count(n=>n.X+package.Plane.OffsetX>=native.HeaderMin.X&&n.X+package.Plane.OffsetX<=native.HeaderMax.X&&n.Y+package.Plane.OffsetY>=native.HeaderMin.Y&&n.Y+package.Plane.OffsetY<=native.HeaderMax.Y);
        return result with{Sha256=package.PackageSha256,NativeMap=native with{PackageMapName=package.Plane.OriginalMapName,AssociationSummary=$"同包地图名一致、XY边界重叠；{within}/{nodes.Count}个二维站点位于三维XY边界。含{package.Plane.Definition.Edges.Count}条已转换二维道路。二维偏移已逆变换核对；坐标系/高度对齐未核定，不自动叠加或绑定。"}};
    }
}
