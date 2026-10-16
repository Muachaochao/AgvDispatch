using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgvDispatch.Wpf.Services;

public sealed record MapReviewStation(string SiteId,string LogicalId,double? SourceX,double? SourceY,double? DeviceX,double? DeviceY,double? XyDeltaM,double? LocalDeltaM,double? AngleDeltaRadians,string Status,string Issues);
public sealed record MapReviewRoute(string Role,int Step,string LogicalFrom,string LogicalTo,string VendorFrom,string VendorTo,string Status,string Issues)
{ public string RoleText=>Role switch{"vehicleToPick"=>"车位 → 取货","pickToWait"=>"取货 → 等待","waitToDump"=>"等待 → 倒料","dumpToPick"=>"倒料 → 取货",_=>Role}; }
public sealed record MapReviewBehavior(string Rule,string Status,string Statement,string Source)
{
    public string RuleText=>Rule switch{"unknownTaskResponse"=>"1110未知任务返回","taskHistoryRetention"=>"任务历史保留","controllerRestartRetention"=>"控制器重启后保留","listTaskAtomicAcceptance"=>"3066接收原子性/重复编号","partialAcceptanceVisibilityDelay"=>"部分接收可见延迟","targetedCancellationCascade"=>"3003定向取消级联","smartCancelBehavior"=>"智能取消阶段规则",_=>Rule};
    public string StatusText=>Status=="missing"?"缺少资料":"已提供，未核定";
}
public sealed record MapReviewReport(string Path,string Sha256,string Mode,bool ComparisonPassed,string VehicleAlias,string CapturedAt,string RobotMap,string RobotMapMd5,string LocalMapId,string LocalMapVersion,IReadOnlyList<MapReviewStation> Stations,IReadOnlyList<MapReviewRoute> Routes,IReadOnlyList<MapReviewBehavior> Behaviors,IReadOnlyList<string> Errors,IReadOnlyList<string> Pending)
{
    public MapReviewSnapshot? Snapshot {get;init;}
    public string SnapshotIssue {get;init;}="";
    public IReadOnlyList<string> DisplayErrors=>Errors.Take(500).ToArray();
    public IReadOnlyList<string> DisplayPending=>Pending.Take(500).ToArray();
    public string DetailLimitMessage=>$"错误{Errors.Count}项，待核定{Pending.Count}项；本页各显示前500项，完整内容保留在原报告。";
}
public interface IMapReviewReportReader { Task<MapReviewReport> ReadAsync(string path,CancellationToken token); }

/// <summary>Imports untrusted offline review output; validates shape/hash consistency, never approves or executes it.</summary>
public sealed class MapReviewReportReader:IMapReviewReportReader
{
    public Task<MapReviewReport> ReadAsync(string path,CancellationToken token)=>Task.Run(()=>ReadCoreAsync(path,token),token);
    private static async Task<byte[]> ReadBounded(string path,int max,CancellationToken token)
    {
        await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true);
        if(stream.Length>max)throw new InvalidDataException("文件超过导入预算。");
        using var output=new MemoryStream();var buffer=new byte[65536];int count;
        while((count=await stream.ReadAsync(buffer,token))>0){if(output.Length+count>max)throw new InvalidDataException("文件超过导入预算。");await output.WriteAsync(buffer.AsMemory(0,count),token);}
        return output.ToArray();
    }
    private static JsonDocument Parse(byte[] raw,CancellationToken token)
    {
        var start=raw.AsSpan().StartsWith(new byte[]{239,187,191})?3:0;
        var doc=JsonDocument.Parse(raw.AsMemory(start),new(){MaxDepth=40});
        try{Unique(doc.RootElement,token);return doc;}catch{doc.Dispose();throw;}
    }
    private static void Unique(JsonElement value,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(value.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var item in value.EnumerateObject()){if(!names.Add(item.Name))throw new InvalidDataException("JSON存在重复字段。");Unique(item.Value,token);}}
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())Unique(item,token);
    }
    private static string Text(JsonElement value,string key,bool optional=false)
    {
        if(!value.TryGetProperty(key,out var item)||item.ValueKind==JsonValueKind.Null){if(optional)return "";throw new InvalidDataException("报告缺少字段："+key);}
        if(item.ValueKind!=JsonValueKind.String||item.GetString()!.Length>4096)throw new InvalidDataException("报告文本字段无效："+key);
        return item.GetString()!;
    }
    private static JsonElement[] Array(JsonElement value,string key,int max)
    {
        if(!value.TryGetProperty(key,out var item)||item.ValueKind!=JsonValueKind.Array||item.GetArrayLength()>max)throw new InvalidDataException("报告数组缺失或超限："+key);
        return item.EnumerateArray().ToArray();
    }
    private static bool Bool(JsonElement value,string key)
    {
        if(!value.TryGetProperty(key,out var item)||item.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException("报告布尔字段缺失："+key);
        return item.GetBoolean();
    }
    private static string[] Strings(JsonElement value,string key,int max)=>Array(value,key,max).Select(item=>item.ValueKind==JsonValueKind.String&&item.GetString()!.Length<=4096?item.GetString()!:throw new InvalidDataException("报告提示文本无效。")).ToArray();
    private static double? Number(JsonElement value,string key,bool delta=false)
    {
        if(!value.TryGetProperty(key,out var item)||item.ValueKind==JsonValueKind.Null)return null;
        if(item.ValueKind!=JsonValueKind.Number||!item.TryGetDouble(out var number)||!double.IsFinite(number)||Math.Abs(number)>2e6||(delta&&number<0))throw new InvalidDataException("报告数值无效："+key);
        return number;
    }
    private static string Status(JsonElement row,bool station)
    {
        var status=Text(row,"status");if(status is not ("PASS" or "FAIL")&&!(station&&status=="PENDING"))throw new InvalidDataException("报告状态无效。");return status;
    }
    private static async Task<MapReviewReport> ReadCoreAsync(string path,CancellationToken token)
    {
        path=System.IO.Path.GetFullPath(path);var raw=await ReadBounded(path,8<<20,token);using var doc=Parse(raw,token);var root=doc.RootElement;
        if(root.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("报告根必须为对象。");
        foreach(var key in new[]{"realDeviceValidated","coordinateAlignmentVerified","behaviorVerified","canEnableCommands","canUploadMap"})if(Bool(root,key))throw new InvalidDataException("报告含验收/控制批准声明，不能作为只读未核定报告导入。");
        var mode=Text(root,"mode");if(mode is not ("synthetic" or "declared_capture"))throw new InvalidDataException("报告来源模式无效。");
        var passed=Bool(root,"offlineComparisonPassed");var origin=Text(root,"originDeclaration");
        if(origin!=(mode=="synthetic"?"explicit_synthetic_project_regression":"real_device_read_only_capture"))throw new InvalidDataException("报告模式与来源不一致。");
        var directory=System.IO.Path.GetDirectoryName(path)!;
        var entries=Array(root,"inputs",4);if(entries.Length!=4)throw new InvalidDataException("需要四份原始输入和哈希，不能只导入散落的review.json。");
        var roles=new HashSet<string>();byte[]? captureRaw=null,localRaw=null;
        foreach(var item in entries)
        {
            token.ThrowIfCancellationRequested();var role=Text(item,"role");var max=role switch{"capture" or "plan"=>4<<20,"local_map" or "smap"=>64<<20,_=>throw new InvalidDataException("未知输入角色；不读取任意路径。")};
            if(!roles.Add(role))throw new InvalidDataException("输入角色重复。");var digest=Text(item,"sha256");
            if(digest.Length!=64||!digest.All(Uri.IsHexDigit)||!item.TryGetProperty("bytes",out var size)||!size.TryGetInt32(out var expected)||expected<1||expected>max)throw new InvalidDataException("输入哈希或长度无效。");
            var bytes=await ReadBounded(System.IO.Path.Combine(directory,"inputs",role+".json"),max,token);
            if(bytes.Length!=expected||!Convert.ToHexString(SHA256.HashData(bytes)).Equals(digest,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("输入文件长度/哈希不一致："+role);
            if(role=="capture")captureRaw=bytes;if(role=="local_map")localRaw=bytes;
        }
        using var capture=Parse(captureRaw!,token);var source=capture.RootElement;
        if(Text(source,"origin")!=origin)throw new InvalidDataException("报告与采集输入来源不一致。");
        if(Bool(source,"deviceValidated"))throw new InvalidDataException("原始采集不能自批准设备验收。");
        var alias=Text(source,"vehicleAlias");var captured=Text(source,"capturedAt");
        if(!DateTimeOffset.TryParse(captured,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out _)||!System.Text.RegularExpressions.Regex.IsMatch(captured,@"(?:Z|[+-]\d{2}:\d{2})$"))throw new InvalidDataException("采集时间缺时区或无效。");
        var samples=Array(source,"samples",100);var map=samples.FirstOrDefault(s=>s.TryGetProperty("api",out var api)&&api.TryGetInt32(out var n)&&n==1300);
        if(!map.TryGetProperty("response",out var response))throw new InvalidDataException("原采集缺1300地图身份。");
        var robotMap=Text(response,"current_map");var md5=Text(response,"current_map_md5");if(md5.Length!=32||!md5.All(Uri.IsHexDigit))throw new InvalidDataException("车端MD5格式无效。");
        using var local=Parse(localRaw!,token);var localId="未提供";var version="未提供";
        if(local.RootElement.TryGetProperty("project",out var project))localId=Text(project,"mapId",true);
        if(local.RootElement.TryGetProperty("draft",out var draft))version=Text(draft,"versionId",true);
        var stations=new List<MapReviewStation>();var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in Array(root,"stations",10000))
        {
            token.ThrowIfCancellationRequested();var id=Text(row,"siteId");if(string.IsNullOrWhiteSpace(id)||!ids.Add(id))throw new InvalidDataException("报告站点为空或重复。");
            stations.Add(new(id,Text(row,"logicalId"),Number(row,"sourceX"),Number(row,"sourceY"),Number(row,"deviceX"),Number(row,"deviceY"),Number(row,"xyDeltaM",true),Number(row,"localDeltaM",true),Number(row,"angleDeltaRadians",true),Status(row,true),string.Join("；",Strings(row,"issues",100))));
        }
        var routes=new List<MapReviewRoute>();
        foreach(var row in Array(root,"routeSegments",8192))
        {
            token.ThrowIfCancellationRequested();var role=Text(row,"role");if(role is not ("vehicleToPick" or "pickToWait" or "waitToDump" or "dumpToPick")||!row.TryGetProperty("step",out var step)||!step.TryGetInt32(out var index)||index is <1 or >2048)throw new InvalidDataException("运输角色/步骤序号无效。");
            routes.Add(new(role,index,Text(row,"logicalFrom"),Text(row,"logicalTo"),Text(row,"vendorFrom",true),Text(row,"vendorTo",true),Status(row,false),string.Join("；",Strings(row,"issues",100))));
        }
        var behaviors=new List<MapReviewBehavior>();foreach(var row in Array(root,"behaviorEvidence",20))
        {
            if(Bool(row,"verified"))throw new InvalidDataException("厂家行为不能由导入报告自动批准。");var status=Text(row,"status");if(status is not ("missing" or "provided_unverified"))throw new InvalidDataException("行为核定状态无效。");
            behaviors.Add(new(Text(row,"rule"),status,Text(row,"statement",true),Text(row,"source",true)));
        }
        var errors=Strings(root,"errors",20000);var pending=Strings(root,"pending",20000);
        if(passed&&(errors.Length>0||stations.Any(s=>s.Status=="FAIL")||routes.Any(s=>s.Status=="FAIL")))throw new InvalidDataException("报告总结果与差异明细矛盾。");
        MapReviewSnapshot? snapshot=null;var snapshotIssue="";
        try{snapshot=MapReviewSnapshot.Read(local.RootElement,Convert.ToHexString(SHA256.HashData(localRaw!)),token);}
        catch(Exception ex)when(ex is InvalidDataException or InvalidOperationException or JsonException or ArgumentException or KeyNotFoundException){snapshotIssue="本地快照不能定位："+ex.Message;}
        return new(path,Convert.ToHexString(SHA256.HashData(raw)),mode,passed,alias,captured,robotMap,md5,localId,version,stations,routes,behaviors,errors,pending){Snapshot=snapshot,SnapshotIssue=snapshotIssue};
    }
}
