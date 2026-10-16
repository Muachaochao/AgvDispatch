using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Infrastructure.Maps;

/// <summary>Explicit three-column XYZ text contract, not the proprietary .3dsmap protobuf.</summary>
public sealed class XyzPointCloudReader
{
    public const int MaxBytes=64*1024*1024;
    public const int MaxPoints=1_000_000;
    public async Task<PointCloudData> ReadFileAsync(string path,CancellationToken ct=default)
    {
        if(!string.Equals(Path.GetExtension(path),".xyz",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("三维查看器目前读取.xyz文本；Robokit二进制定义尚缺，不猜测解析.smap/.3dsmap。");
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,64*1024,true);
        if(input.Length<=0||input.Length>MaxBytes)throw new InvalidDataException("XYZ文件为空或超过64MiB。");
        using var bytes=new MemoryStream();var buffer=new byte[64*1024];int read;
        while((read=await input.ReadAsync(buffer,ct))>0){if(bytes.Length+read>MaxBytes)throw new InvalidDataException("XYZ读取超过64MiB。");await bytes.WriteAsync(buffer.AsMemory(0,read),ct);}
        return await Task.Run(()=>Read(bytes.ToArray(),path,ct),ct);
    }
    public PointCloudData Read(byte[] bytes,string source,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(bytes.Length==0||bytes.Length>MaxBytes)throw new InvalidDataException("XYZ文件为空或超过64MiB。");
        var text=new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF');
        var points=new List<CloudPoint>();using var input=new StringReader(text);string? line;int lineNo=0;
        double minX=double.PositiveInfinity,minY=minX,minZ=minX,maxX=double.NegativeInfinity,maxY=maxX,maxZ=maxX;
        while((line=input.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();lineNo++;
            if(lineNo>1_100_000||line.Length>4096)throw new InvalidDataException("XYZ行数或行长超过预算。");
            line=line.Trim();if(line.Length==0||line.StartsWith('#'))continue;
            var fields=line.Contains(',')?line.Split(','):line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if(fields.Length!=3)throw new InvalidDataException($"XYZ第{lineNo}行需要恰好三列X Y Z（米），不接受额外强度/颜色列。");
            double Parse(string field){if(!double.TryParse(field,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value)||Math.Abs(value)>1e6)throw new InvalidDataException($"XYZ第{lineNo}行不是预算内有限坐标。");return value;}
            var point=new CloudPoint(Parse(fields[0]),Parse(fields[1]),Parse(fields[2]));
            if(points.Count>=MaxPoints)throw new InvalidDataException("XYZ点数超过100万。");points.Add(point);
            minX=Math.Min(minX,point.X);minY=Math.Min(minY,point.Y);minZ=Math.Min(minZ,point.Z);maxX=Math.Max(maxX,point.X);maxY=Math.Max(maxY,point.Y);maxZ=Math.Max(maxZ,point.Z);
        }
        if(points.Count==0)throw new InvalidDataException("XYZ没有坐标点。");
        return new(source,Convert.ToHexString(SHA256.HashData(bytes)),points.AsReadOnly(),new(minX,minY,minZ),new(maxX,maxY,maxZ));
    }
}
