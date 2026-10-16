using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgvDispatch.Infrastructure.Maps;

/// <summary>Validates a Robokit 3D container; the opaque 3D protobuf is retained, never interpreted.</summary>
public sealed class Smap3DPackageReader
{
    public sealed record Limits(long PackageBytes=64L*1024*1024,long InfoBytes=64*1024,
        long PlaneBytes=64L*1024*1024,long CloudBytes=192L*1024*1024,
        long TotalBytes=256L*1024*1024,int CompressionRatio=1000);
    public sealed record EntryReview(string Name,long CompressedBytes,long ExpandedBytes,string Sha256,uint Crc32);
    public sealed record Result(string ZipName,string PackageSha256,byte[] PlaneBytes,
        Smap2DImportService.Result Plane,IReadOnlyList<EntryReview> Entries)
    {
        public bool PointCloudParsed=>false;
        public bool DeviceValidated=>false;
    }
    private readonly Limits limits;
    public Smap3DPackageReader(Limits? limits=null)
    {
        this.limits=limits??new();var cap=new Limits();
        if(this.limits.PackageBytes<1||this.limits.PackageBytes>cap.PackageBytes||this.limits.InfoBytes<1||this.limits.InfoBytes>cap.InfoBytes||this.limits.PlaneBytes<1||this.limits.PlaneBytes>cap.PlaneBytes||this.limits.CloudBytes<1||this.limits.CloudBytes>cap.CloudBytes||this.limits.TotalBytes<1||this.limits.TotalBytes>cap.TotalBytes||this.limits.CompressionRatio<1||this.limits.CompressionRatio>cap.CompressionRatio)throw new ArgumentOutOfRangeException(nameof(limits),"解包预算必须为正且不能高于生产上限。");
    }
    public static bool IsZip(ReadOnlySpan<byte> bytes)=>bytes.Length>=2&&bytes[0]==0x50&&bytes[1]==0x4B;
    public Result Read(byte[] bytes,string source,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(bytes.LongLength==0||bytes.LongLength>limits.PackageBytes)throw new InvalidDataException("3D包为空或超过压缩包大小预算（默认64MiB）。");
        if(!IsZip(bytes))throw new InvalidDataException("不是ZIP格式3D包。");
        using var input=new MemoryStream(bytes,false);using var zip=new ZipArchive(input,ZipArchiveMode.Read,false);
        // Never extract entries to paths from the archive. Only exact root filenames are accepted.
        string[] required=["info.json","0.smap","0.3dsmap"];
        if(zip.Entries.Count!=3)throw new InvalidDataException("3D包根目录必须恰有info.json、0.smap、0.3dsmap三个文件；缺失/重复/扩展项需另行核定。");
        var entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.Ordinal);
        long declared=0;
        foreach(var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if(!required.Contains(entry.FullName,StringComparer.Ordinal)||!entries.TryAdd(entry.FullName,entry))throw new InvalidDataException("3D包有重复、目录路径或未知条目："+entry.FullName);
            var unixType=(entry.ExternalAttributes>>16)&0xF000;
            if(unixType is not (0 or 0x8000))throw new InvalidDataException("3D包不接受链接或特殊条目。");
            var max=entry.FullName switch{"info.json"=>limits.InfoBytes,"0.smap"=>limits.PlaneBytes,_=>limits.CloudBytes};
            if(entry.Length<=0||entry.Length>max)throw new InvalidDataException("3D包条目为空或超过解压大小预算："+entry.FullName);
            declared=checked(declared+entry.Length);
            if(declared>limits.TotalBytes)throw new InvalidDataException("3D包解压总大小超过预算。");
            if(entry.CompressedLength<=0||entry.Length/(double)entry.CompressedLength>limits.CompressionRatio)throw new InvalidDataException("3D包条目压缩比超过预算："+entry.FullName);
        }
        var reviews=new List<EntryReview>();byte[] plane=[];byte[] info=[];long expanded=0;
        foreach(var name in required)
        {
            ct.ThrowIfCancellationRequested();var entry=entries[name];using var stream=entry.Open();
            using var sha=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var capture=name=="0.3dsmap"?null:new MemoryStream();
            var buffer=new byte[64*1024];long count=0;uint crc=uint.MaxValue;int read;
            while((read=stream.Read(buffer))>0)
            {
                ct.ThrowIfCancellationRequested();count+=read;expanded+=read;
                if(count>entry.Length||expanded>limits.TotalBytes)throw new InvalidDataException("3D包实际解压大小超过声明或预算。");
                sha.AppendData(buffer,0,read);capture?.Write(buffer,0,read);
                for(var i=0;i<read;i++)crc=CrcTable[(crc^buffer[i])&255]^(crc>>8);
            }
            crc=~crc;
            if(count!=entry.Length||crc!=entry.Crc32)throw new InvalidDataException("3D包条目长度或CRC校验失败："+name);
            reviews.Add(new(name,entry.CompressedLength,count,Convert.ToHexString(sha.GetHashAndReset()),crc));
            if(name=="info.json")info=capture!.ToArray();else if(name=="0.smap")plane=capture!.ToArray();
        }
        using var doc=JsonDocument.Parse(info.AsMemory(info.AsSpan().StartsWith(new byte[]{0xEF,0xBB,0xBF})?3:0),new(){MaxDepth=16});
        if(doc.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("info.json必须为对象。");
        var properties=doc.RootElement.EnumerateObject().ToArray();
        if(properties.Length!=1||properties[0].Name!="zipName"||properties[0].Value.ValueKind!=JsonValueKind.String)throw new InvalidDataException("info.json只接受已核定的唯一zipName字段；扩展项需核定。");
        var zipName=properties[0].Value.GetString()!;
        if(string.IsNullOrWhiteSpace(zipName)||zipName.Length>256||zipName.Any(char.IsControl)||zipName.IndexOfAny(['/', '\\', ':'])>=0||zipName is "." or "..")throw new InvalidDataException("info.json的zipName无效。");
        var parsed=new Smap2DImportService().Read(plane,source);
        parsed=parsed with{Warnings=new[]{"3D包的二维部分预览：只解析0.smap；0.3dsmap仅校验字节并保留，未解析点云，不代表三维显示或实车格式验收。"}.Concat(parsed.Warnings).ToArray()};
        return new(zipName,Convert.ToHexString(SHA256.HashData(bytes)),plane,parsed,reviews);
    }
    private static readonly uint[] CrcTable=BuildTable();
    private static uint[] BuildTable(){var table=new uint[256];for(uint i=0;i<256;i++){var v=i;for(var bit=0;bit<8;bit++)v=(v&1)!=0?0xEDB88320^(v>>1):v>>1;table[i]=v;}return table;}
}
