using System.Security.Cryptography;
using System.Text.Json;
namespace AgvDispatch.WebHost;
public sealed class ProjectConflictException(string message):Exception(message);
public sealed record ProjectSaveRequest(string ExpectedRevision,JsonElement Plan,string? SourceMapId=null,string? SourceMapVersion=null);
public sealed record SavedProject(string Id,string Revision,DateTimeOffset SavedAt,JsonElement Plan,string? SourceMapId,string? SourceMapVersion);
public sealed class WebProjectStore(string root)
{
    private readonly SemaphoreSlim gate=new(1,1);
    private string Folder(string id)=>Path.Combine(root,Guid.Parse(id).ToString("N"));
    public static void Validate(JsonElement p)
    {
        if(p.ValueKind!=JsonValueKind.Object||JsonSerializer.SerializeToUtf8Bytes(p,WebJson.Options).Length>4*1024*1024||p.GetProperty("schema").GetString()!="agv-web-plan"||p.GetProperty("version").GetInt32()!=1||!Guid.TryParse(p.GetProperty("id").GetString(),out _))throw new ArgumentException("方案格式/ID/版本或4MiB预算不符");
        static string Name(JsonElement x,string k){var s=x.GetProperty(k).GetString();if(string.IsNullOrWhiteSpace(s)||s.Length>128)throw new ArgumentException("方案字段为空或过长："+k);return s;}
        Name(p,"name");var d=p.GetProperty("definition");Name(d,"name");Name(d,"coordinateSystem");if(d.GetProperty("unit").GetString()!="m")throw new ArgumentException("只接受明确米制方案");
        foreach(var (o,k,max) in new[]{(d,"nodes",5000),(d,"edges",10000),(d,"resources",2000),(p,"scene",1000),(p,"vehicles",100),(p,"waypoints",1000)})
            if(o.GetProperty(k).ValueKind!=JsonValueKind.Array||o.GetProperty(k).GetArrayLength()>max)throw new ArgumentException("数组无效或超预算："+k);
        static HashSet<string> Ids(JsonElement a){var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var x in a.EnumerateArray())if(!ids.Add(Name(x,"id")))throw new ArgumentException("重复对象编号");return new HashSet<string>(a.EnumerateArray().Select(x=>x.GetProperty("id").GetString()!),StringComparer.Ordinal);}
        static void Coordinate(JsonElement v){if(!v.TryGetDouble(out var n)||!double.IsFinite(n)||Math.Abs(n)>1e7)throw new ArgumentException("坐标无效");}
        var nodes=Ids(d.GetProperty("nodes"));Ids(d.GetProperty("edges"));var resources=Ids(d.GetProperty("resources"));Ids(p.GetProperty("scene"));
        foreach(var n in d.GetProperty("nodes").EnumerateArray()){Coordinate(n.GetProperty("x"));Coordinate(n.GetProperty("y"));if(n.GetProperty("enabled").ValueKind is not(JsonValueKind.True or JsonValueKind.False)||!new[]{"Waypoint","Station","Intersection","WaitingPoint","ChargingPoint"}.Contains(Name(n,"type")))throw new ArgumentException("节点类型/启用状态无效");}
        foreach(var edge in d.GetProperty("edges").EnumerateArray())if(!nodes.Contains(Name(edge,"from"))||!nodes.Contains(Name(edge,"to")))throw new ArgumentException("道路端点不存在");
        foreach(var x in d.GetProperty("nodes").EnumerateArray().Concat(d.GetProperty("edges").EnumerateArray()))foreach(var resource in x.GetProperty("resourceIds").EnumerateArray())if(!resources.Contains(resource.GetString()!))throw new ArgumentException("资源引用不存在");
        foreach(var x in p.GetProperty("waypoints").EnumerateArray())if(!nodes.Contains(x.GetString()!))throw new ArgumentException("顺序引用不存在");
        foreach(var e in d.GetProperty("edges").EnumerateArray()){
            var kind=Name(e,"geometryKind");if(!new[]{"Line","CircularArc","CubicBezier"}.Contains(kind)||e.GetProperty("distance").GetDouble()<=0||e.GetProperty("speedLimit").GetDouble()<=0||e.GetProperty("from").GetString()==e.GetProperty("to").GetString())throw new ArgumentException("道路属性无效");
            foreach(var key in new[]{"enabled","bidirectional"})if(e.GetProperty(key).ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new ArgumentException("道路开关无效");
            if(kind!="Line"){var controls=e.GetProperty("controlPoints");if(controls.GetArrayLength()!=(kind=="CircularArc"?1:2))throw new ArgumentException("曲线控制点无效");foreach(var c in controls.EnumerateArray()){Coordinate(c.GetProperty("x"));Coordinate(c.GetProperty("y"));}}
        }
        foreach(var item in d.GetProperty("resources").EnumerateArray()){if(item.GetProperty("capacity").GetInt32() is <1 or >1000||!new[]{"Intersection","NarrowAisle","SingleLane","Station","ChargingBay"}.Contains(Name(item,"type")))throw new ArgumentException("资源容量/类型无效");}
        foreach(var item in p.GetProperty("scene").EnumerateArray()){Name(item,"name");if(!new[]{"area","equipment","text"}.Contains(Name(item,"kind"))||!System.Text.RegularExpressions.Regex.IsMatch(Name(item,"color"),"^#[0-9a-fA-F]{6}$")||item.GetProperty("visible").ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new ArgumentException("场景属性无效");foreach(var k in new[]{"x","y","width","height"})Coordinate(item.GetProperty(k));if(item.GetProperty("width").GetDouble()<=0||item.GetProperty("height").GetDouble()<=0)throw new ArgumentException("场景大小无效");}
        var vehicles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var v in p.GetProperty("vehicles").EnumerateArray()){var id=Name(v,"vehicleId");if(!vehicles.Add(id)||!System.Text.RegularExpressions.Regex.IsMatch(id,"^[A-Za-z0-9_-]{1,32}$")||!System.Net.IPAddress.TryParse(Name(v,"ipAddress"),out var ip)||ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||v.GetProperty("port").GetInt32() is <1 or >65535||v.GetProperty("navigationPort").GetInt32() is <1 or >65535)throw new ArgumentException("车辆稿编号/IP/端口无效");Name(v,"displayName");}
        // This is an engineering archive, not execution approval; authoritative geometry stays in Service.
    }
    private async Task<SavedProject> ReadFile(string path,CancellationToken ct)
    {
        var data=await File.ReadAllBytesAsync(path,ct);if(data.Length>6*1024*1024)throw new InvalidDataException("存档过大");
        var s=JsonSerializer.Deserialize<SavedProject>(data,WebJson.Options)??throw new InvalidDataException("存档无效");Validate(s.Plan);
        if(s.Revision!=Digest(s.Plan,s.SourceMapId,s.SourceMapVersion,s.SavedAt)||Guid.Parse(s.Id)!=Guid.Parse(s.Plan.GetProperty("id").GetString()!))throw new InvalidDataException("存档摘要或归属不一致");return s;
    }
    private static string Digest(JsonElement plan,string? map,string? version,DateTimeOffset savedAt)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{plan,map,version,savedAt},WebJson.Options)));
    public async Task<SavedProject?> ReadAsync(string id,string? revision,CancellationToken ct)
    {
        var folder=Folder(id);if(revision is not null&&(revision.Length!=64||revision.Any(c=>!Uri.IsHexDigit(c))))throw new ArgumentException("版本摘要无效");
        var path=Path.Combine(folder,revision is null?"current.json":revision.ToUpperInvariant()+".json");return File.Exists(path)?await ReadFile(path,ct):null;
    }
    public async Task<object[]> ListAsync(CancellationToken ct)
    {
        if(!Directory.Exists(root))return [];var result=new List<object>();
        foreach(var folder in Directory.EnumerateDirectories(root).Take(101)){if(!Guid.TryParse(Path.GetFileName(folder),out _))continue;var path=Path.Combine(folder,"current.json");if(!File.Exists(path))continue;var p=await ReadFile(path,ct);result.Add(new{p.Id,p.Revision,p.SavedAt,Name=p.Plan.GetProperty("name").GetString(),p.SourceMapId,p.SourceMapVersion});}return result.ToArray();
    }
    public async Task<object[]> HistoryAsync(string id,CancellationToken ct)
    {
        var folder=Folder(id);if(!Directory.Exists(folder))return [];var list=new List<object>();foreach(var path in Directory.EnumerateFiles(folder,"*.json").Where(p=>Path.GetFileName(p)!="current.json").Take(32)){var p=await ReadFile(path,ct);list.Add(new{p.Revision,p.SavedAt});}return list.ToArray();
    }
    public async Task<SavedProject> SaveAsync(ProjectSaveRequest q,CancellationToken ct)
    {
        Validate(q.Plan);var id=q.Plan.GetProperty("id").GetString()!;
        if((q.SourceMapId?.Length??0)>128||(q.SourceMapVersion?.Length??0)>128)throw new ArgumentException("地图引用过长");
        await gate.WaitAsync(ct);try{
            Directory.CreateDirectory(root);await using var fileLock=new FileStream(Path.Combine(root,"write.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var old=await ReadAsync(id,null,ct);if((old?.Revision??"")!=q.ExpectedRevision)throw new ProjectConflictException("方案已被其他页面修改；请重新读取版本并比较");
            if(old is not null&&JsonSerializer.Serialize(old.Plan,WebJson.Options)==JsonSerializer.Serialize(q.Plan,WebJson.Options)&&old.SourceMapId==q.SourceMapId&&old.SourceMapVersion==q.SourceMapVersion)return old;
            var savedAt=DateTimeOffset.UtcNow;var revision=Digest(q.Plan,q.SourceMapId,q.SourceMapVersion,savedAt);
            var folder=Folder(id);if(!Directory.Exists(folder)&&Directory.EnumerateDirectories(root).Count()>=100)throw new ArgumentException("方案数量达到100，请先导出归档");Directory.CreateDirectory(folder);
            if(Directory.EnumerateFiles(folder,"*.json").Count()>=33)throw new ArgumentException("方案历史达到32版，请导出并另存新方案，保留原历史");
            var saved=new SavedProject(Guid.Parse(id).ToString("N"),revision,savedAt,q.Plan.Clone(),q.SourceMapId,q.SourceMapVersion);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(saved,WebJson.Options);var temp=Path.Combine(folder,Guid.NewGuid().ToString("N")+".tmp");
            try{await File.WriteAllBytesAsync(temp,bytes,ct);File.Move(temp,Path.Combine(folder,revision+".json"),false);await File.WriteAllBytesAsync(temp,bytes,ct);File.Move(temp,Path.Combine(folder,"current.json"),true);}finally{if(File.Exists(temp))File.Delete(temp);}return saved;
        }finally{gate.Release();}
    }
}
public static class ProjectApi
{
    public static void MapProjectApi(this WebApplication app)
    {
        app.MapGet("/api/projects",async(WebProjectStore s,CancellationToken ct)=>await s.ListAsync(ct));
        app.MapGet("/api/projects/{id}",async(string id,string? revision,WebProjectStore s,CancellationToken ct)=>await s.ReadAsync(id,revision,ct));
        app.MapGet("/api/projects/{id}/history",async(string id,WebProjectStore s,CancellationToken ct)=>await s.HistoryAsync(id,ct));
        app.MapPost("/api/projects",async(ProjectSaveRequest q,WebProjectStore s,CancellationToken ct)=>await s.SaveAsync(q,ct));
    }
}
