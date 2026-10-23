using System.Security.Cryptography;
using System.Text.RegularExpressions;
namespace AgvDispatch.WebHost;

// Original uploaded bytes, never a generated/sanitized replacement for the vendor map.
public sealed class VendorMapSourceArchive(string root)
{
    public async Task<string> SaveAsync(byte[] bytes,CancellationToken ct)
    {
        if(bytes.Length is <2 or >8*1024*1024)throw new InvalidDataException("原始地图超出留存预算。");
        var sha=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();Directory.CreateDirectory(root);
        var path=Path.Combine(root,sha+".smap");
        if(!File.Exists(path))
        {
            var temporary=Path.Combine(root,sha+"."+Guid.NewGuid().ToString("N")+".tmp");
            try{await File.WriteAllBytesAsync(temporary,bytes,ct);try{File.Move(temporary,path);}catch(IOException) when(File.Exists(path)){} }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(await File.ReadAllBytesAsync(path,ct)),SHA256.HashData(bytes)))
            throw new InvalidDataException("原始地图留存摘要不一致，未覆盖原文件。");
        return sha;
    }
    public async Task<byte[]?> ReadAsync(string sha,CancellationToken ct)
    {
        if(!Regex.IsMatch(sha,"^[a-f0-9]{64}$"))throw new ArgumentException("原始地图摘要无效。");
        var path=Path.Combine(root,sha+".smap");if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>8*1024*1024)throw new InvalidDataException("留存原图超过读取预算。");
        var bytes=await File.ReadAllBytesAsync(path,ct);
        if(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()!=sha)throw new InvalidDataException("留存原图摘要校验失败。");
        return bytes;
    }
}
