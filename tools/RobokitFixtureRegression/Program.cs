using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Maps;

var root=Path.GetFullPath(args.Length>0?args[0]:"docs/examples/robokit-regression-v1");
var evidence=Path.GetFullPath("docs/commissioning/evidence/20261014-robokit-fixed-fixtures");Directory.CreateDirectory(evidence);
var results=new List<object>();var checks=0;
void Check(bool value,string name){if(!value)throw new Exception(name);checks++;results.Add(new{Name=name,Status="PASS"});Console.WriteLine("PASS "+name);}
var manifest=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"fixture-manifest.json")))!;
Check(manifest["realDeviceReady"]!.GetValue<bool>()==false,"未取得实车资料，夹具明确不具备现场验收资格");
foreach(var file in manifest["files"]!.AsArray())
    Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,file!["path"]!.GetValue<string>())))).Equals(file["sha256"]!.GetValue<string>(),StringComparison.OrdinalIgnoreCase),"固定夹具哈希："+file["path"]);
var original=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"simulated-bundle.json")))!;
Check(original["origin"]!.GetValue<string>()=="explicit_synthetic_project_regression" && !original["deviceValidated"]!.GetValue<bool>(),"完整1100明确标为合成覆盖项目字段，而非本车返回");
await using var robot=new FixtureRobot(original);
var config=new AgvRobotOptions{VehicleId="FIXTURE",IpAddress="127.0.0.1",Port=robot.StatusPort,NavigationPort=robot.NavigationPort};
var options=new AgvOptions{Enabled=true,AllowCommands=true,Robots=[config],ConnectTimeoutMs=1000,RequestTimeoutMs=1500,RequestIntervalMs=100};
await using var manager=new AgvConnectionManager(options);var client=manager.Clients.Single();
robot.Scenario="running";
var (t,_)=await client.PollAsync(default);
Check(t.X==1.25 && t.Y==-2.5 && t.Angle==1.57 && t.LocalizationConfidence==0.98,"1100位置、弧度、定位质量原值解析");
Check(t.CurrentStation=="LM1" && t.LastStation=="LM0","1100厂家站点编号保持独立");
Check(t.BatteryPercent==76m && t.BatteryVoltage==48.2 && t.BatteryCurrent==-1.5 && t.BatteryTemperature==26,"1100电池比例转换与电气字段");
Check(t.Blocked==false && t.EmergencyStopped==false && t.Charging==false,"1100布尔状态解析");
Check(t.VelocityX==0.2 && t.VelocityY==0 && t.AngularVelocity==0.1,"1100速度字段解析");
Check(t.SourceStation=="LM1" && t.TargetStation=="AP1" && t.PathPercentage==0.25 && t.Tasks.Count==2,"1100/1110公共轮询完整组合");
robot.Responses["1100"]=new JsonObject{["ret_code"]=0};
var missing=(await client.PollAsync(default)).Telemetry;
Check(missing.X is null && missing.BatteryPercent is null && missing.EmergencyStopped is null && missing.VelocityX is null,"1100缺字段保持未知，不造健康值");
robot.ResetResponses();
var map=new FixtureMap();map.Replace(new("SIM-LOCAL","SIM-V1",new MapDefinition("sim","test","world","m",
    [new MapNode("BUS-PICK",0,0,MapNodeType.Station,true,[],"LM1"),new MapNode("BUS-WAIT",1,0,MapNodeType.Station,true,[],"LM2"),new MapNode("BUS-DUMP",2,0,MapNodeType.Station,true,[],"AP1")],[],[]),DateTimeOffset.Now));
var inspector=new RobokitInspectionService(options,manager,map);
var before=robot.Requests.Count;
var good=await inspector.ReadAsync(new("FIXTURE","BUS-PICK","BUS-DUMP"),default);
Check(good.Available && good.Stable,"固定同批地图夹具通过只读采集稳定性观察");
Check(good.RobokitVersion=="SIMULATED-NOT-DEVICE" && good.ProtocolVersion=="SIMULATED-NOT-DEVICE","1000保留合成版本标记，不冒充本车固件");
Check(good.RobotMap=="SIM-MAP" && good.RobotMapMd5=="0123456789abcdef0123456789abcdef","1300/1302一致的固定地图MD5");
Check(good.Stations.Count==3 && good.Stations.Single(x=>x.Id=="AP1").Angle==1.57,"1301固定站点类型、数量与角度");
Check(good.Mappings.All(x=>x.Found) && good.Mappings.Single(x=>x.NodeId=="BUS-PICK").SiteId=="LM1","业务节点与厂家站点使用独立编号");
Check(good.Route.SequenceEqual(new[]{"LM1","LM2","AP1"}),"1303保留中间站点，实际查询映射端点");
Check(good.Issues.Any(x=>x.Contains("不作为调度")),"只读观察结果不自动授权任务发送");
Check(robot.Requests.Skip(before).All(x=>x.Type<2000),"地图核验期间没有导航、写图、控制请求");
robot.Responses["1000"]!.AsObject().Remove("version");
Check((await inspector.ReadAsync(new("FIXTURE"),default)).Issues.Any(x=>x.Contains("1000缺少")),"缺失1000版本明确提示");robot.ResetResponses();
robot.Responses["1301"]!["stations"]![1]!["id"]="LM1";
var duplicate=await inspector.ReadAsync(new("FIXTURE"),default);
Check(duplicate.Issues.Any(x=>x.Contains("重复站点")) && !duplicate.Mappings.Single(x=>x.NodeId=="BUS-PICK").Found,"重复1301站点不能通过映射观察");robot.ResetResponses();
robot.Responses["1302"]!["map_info"]![0]!["md5"]=new string('f',32);
Check((await inspector.ReadAsync(new("FIXTURE"),default)).Issues.Any(x=>x.Contains("1302地图MD5")),"1302地图身份冲突明确提示");robot.ResetResponses();
robot.Responses["1303"]!["list"]=new JsonArray("AP1","LM2","LM1");
Check((await inspector.ReadAsync(new("FIXTURE","BUS-PICK","BUS-DUMP"),default)).Issues.Any(x=>x.Contains("路径端点")),"1303反向端点不能冒充所请求线路");robot.ResetResponses();
robot.MapReadCount=0;robot.ChangeMapAfterFirstRead=true;
Check(!(await inspector.ReadAsync(new("FIXTURE"),default)).Stable,"采集前后换图整批不稳定");robot.ChangeMapAfterFirstRead=false;robot.ResetResponses();

var cache=new AgvStateCache(options);cache.ApplyTelemetry("FIXTURE",t with{CurrentStation="LM1",Tasks=[]},TimeSpan.FromMilliseconds(1));
var lookup=new FixtureChain();var gateway=new SeerAgvGateway(cache,manager,options,lookup);
robot.Scenario="unknown";
Check((await client.QueryTasksAsync([],default)).Count==0,"1110显式空task_ids返回空列表，不与省略字段混淆");
var unknown=await gateway.GetTransportTaskStatusAsync("FIXTURE-ROOT",default);
Check(unknown?.State==AgvTransportTaskState.Unknown,"1110全404是未知，不是完成或取消");
lookup.Attempts=2;var sends=robot.Requests.Count(x=>x.Type==3066);
try{await gateway.CreateTransportTaskAsync("FIXTURE-ROOT","FIXTURE",["LM1","LM2","AP1"],null,default);throw new Exception("历史清除后重发");}catch(AgvPartialAcceptanceException){Check(true,"已尝试原发送且全404禁止自动补发");}
Check(robot.Requests.Count(x=>x.Type==3066)==sends,"历史清除夹具不产生新的3066");
lookup.Attempts=0;robot.PartialAfterSend=true;robot.Scenario="unknown";
try{await gateway.CreateTransportTaskAsync("FIXTURE-ROOT","FIXTURE",["LM1","LM2","AP1"],null,default);throw new Exception("部分接收未识别");}catch(AgvPartialAcceptanceException){Check(true,"3066成功应答后仅部分原段可见仍拒绝确认");}
Check(robot.Requests.Count(x=>x.Type==3066)==sends+1,"部分接收不会在一次调用中反复发包");
var rebuilt=new SeerAgvGateway(cache,manager,options,lookup);
try{await rebuilt.CreateTransportTaskAsync("FIXTURE-ROOT","FIXTURE",["LM1","LM2","AP1"],null,default);throw new Exception("重建后部分接收重发");}catch(AgvPartialAcceptanceException){Check(true,"对象重建后固定部分接收记录仍阻止补发");}
Check(robot.Requests.Count(x=>x.Type==3066)==sends+1,"重建后原编号不重复发送");
robot.Scenario="running";await gateway.CancelTaskAsync("FIXTURE-ROOT",default);
Check(robot.LastCancel=="FIXTURE-ROOT-01","3003只定向原首个未结束段");
Check((await gateway.GetTransportTaskStatusAsync("FIXTURE-ROOT",default))?.State==AgvTransportTaskState.GoingToDestination,"3003成功应答不替代1110取消完成证据");
robot.Scenario="nonCascade";
Check((await gateway.GetTransportTaskStatusAsync("FIXTURE-ROOT",default))?.State==AgvTransportTaskState.Unknown,"仅首段取消而后段仍运行，整链不确认结束");
robot.Scenario="cascade";
Check((await gateway.GetTransportTaskStatusAsync("FIXTURE-ROOT",default))?.State==AgvTransportTaskState.Cancelled,"仅所有原段取消记录齐全才确认取消");
robot.Scenario="historyCleared";
Check((await gateway.GetTransportTaskStatusAsync("FIXTURE-ROOT",default))?.State==AgvTransportTaskState.Unknown,"取消后历史清除不能以此前确认补造当前证据");
Check(robot.Errors.IsEmpty,"模拟端请求端口、JSON参数和完整帧检查无失败");
Check(robot.Requests.All(x=>x.Type<2000?x.Port==robot.StatusPort:x.Port==robot.NavigationPort),"固定夹具严格分状态与导航端口");
File.WriteAllText(Path.Combine(evidence,"results.json"),JsonSerializer.Serialize(new{Passed=checks,Failed=0,Origin="synthetic_not_device",RealDeviceReady=false,Results=results,Requests=robot.Requests.Select(r=>new{r.Port,r.Type}).ToArray()},new JsonSerializerOptions{WriteIndented=true}));
File.WriteAllText(Path.Combine(evidence,"exchanges.json"),JsonSerializer.Serialize(robot.Exchanges.ToArray(),new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Passed {checks}; Failed 0; realDeviceReady=false");

sealed class FixtureMap:IActiveMapRuntime
{
    public ActiveMapRuntimeSnapshot? Current{get;private set;}
    public MapGraph? CurrentGraph=>Current?.Graph;
    public void Replace(ActiveMapRuntimeSnapshot? value)=>Current=value;
}
sealed class FixtureChain:IAgvTransportChainLookup
{
    public int Attempts{get;set;}
    public Task<AgvTransportChain?> FindAsync(string id,CancellationToken ct)=>Task.FromResult<AgvTransportChain?>(id=="FIXTURE-ROOT"?new(id,"FIXTURE",new[]{"LM1","LM2","AP1"},Attempts):null);
}
sealed class FixtureRobot:IAsyncDisposable
{
    readonly JsonNode original;readonly TcpListener status=new(IPAddress.Loopback,0),navigation=new(IPAddress.Loopback,0);
    readonly CancellationTokenSource stop=new();readonly List<Task> workers=[];readonly object gate=new();
    public JsonObject Responses{get;private set;}
    public string Scenario="unknown";public bool PartialAfterSend,ChangeMapAfterFirstRead;public int MapReadCount;public string? LastCancel;
    public ConcurrentQueue<(int Port,ushort Type)> Requests=new();public ConcurrentQueue<string> Errors=new();public ConcurrentQueue<object> Exchanges=new();
    public int StatusPort=>((IPEndPoint)status.LocalEndpoint).Port;public int NavigationPort=>((IPEndPoint)navigation.LocalEndpoint).Port;
    public FixtureRobot(JsonNode bundle){original=bundle.DeepClone();Responses=original["responses"]!.DeepClone().AsObject();status.Start();navigation.Start();workers.Add(Accept(status));workers.Add(Accept(navigation));}
    public void ResetResponses()=>Responses=original["responses"]!.DeepClone().AsObject();
    async Task Accept(TcpListener listener)
    {
        try{while(!stop.IsCancellationRequested){var c=await listener.AcceptTcpClientAsync(stop.Token);lock(gate)workers.Add(Serve(c,((IPEndPoint)listener.LocalEndpoint).Port));}}
        catch(OperationCanceledException){}catch(SocketException)when(stop.IsCancellationRequested){}
    }
    async Task Serve(TcpClient socket,int port)
    {
        using(socket)try
        {
            var stream=socket.GetStream();while(!stop.IsCancellationRequested)
            {
                var h=new byte[16];await stream.ReadExactlyAsync(h,stop.Token);var type=BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(8));var length=BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(4));
                if(h[0]!=0x5A || h[1]!=1 || length<0 || length>1024*1024 || (type<2000?port!=StatusPort:port!=NavigationPort))throw new InvalidDataException("请求头／端口不符");
                var bytes=new byte[length];await stream.ReadExactlyAsync(bytes,stop.Token);var payload=length==0?null:JsonNode.Parse(bytes);
                Requests.Enqueue((port,type));
                if(type is 1000 or 1300 or 1022 or 1060 or 1301 && length!=0)throw new InvalidDataException("无参只读请求未零长度");
                if(type==1100 && (payload?["return_laser"]?.GetValue<bool>()!=false || payload?["return_beams3D"]?.GetValue<bool>()!=false))throw new InvalidDataException("轮询请求字段不符");
                if(type==1302 && payload?["map_names"]?[0]?.GetValue<string>()!="SIM-MAP.smap")throw new InvalidDataException("地图名未对应");
                if(type==1303 && (payload?["source_id"]?.GetValue<string>()!="LM1" || payload?["target_id"]?.GetValue<string>()!="AP1"))throw new InvalidDataException("路线端点未映射");
                var reply=Responses[type.ToString()]?.DeepClone() ?? throw new InvalidDataException("未允许API");
                if(type==1300 && ++MapReadCount>1 && ChangeMapAfterFirstRead)reply["current_map"]="SIM-CHANGED";
                if(type==1110)
                {
                    var rows=new JsonArray();var ids=original["taskIds"]!.AsArray();var states=original["taskScenarios"]![Scenario]!.AsArray();
                    var requested=payload?["task_ids"]?.AsArray().Select(x=>x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                    for(var i=0;i<ids.Count;i++)if(requested is null || requested.Contains(ids[i]!.GetValue<string>()))rows.Add(new JsonObject{["task_id"]=ids[i]!.GetValue<string>(),["status"]=states[i]!.GetValue<int>(),["type"]=3});
                    reply["task_status_package"]!["task_status_list"]=rows;
                }
                if(type==3066)
                {
                    var moves=payload?["move_task_list"]?.AsArray();
                    if(moves?.Count!=2 || moves[0]?["task_id"]?.GetValue<string>()!="FIXTURE-ROOT-01" || moves[1]?["task_id"]?.GetValue<string>()!="FIXTURE-ROOT")throw new InvalidDataException("逐段身份不符");
                    if(PartialAfterSend)Scenario="partial";
                }
                if(type==3003){LastCancel=payload?["taskId"]?.GetValue<string>();if(LastCancel!="FIXTURE-ROOT-01" || payload?["smartCancel"] is null)throw new InvalidDataException("取消字段不符");}
                var body=JsonSerializer.SerializeToUtf8Bytes(reply);var head=new byte[16];head[0]=0x5A;head[1]=1;h.AsSpan(2,2).CopyTo(head.AsSpan(2));BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(4),body.Length);BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(8),(ushort)(type+10000));
                Exchanges.Enqueue(new{Origin="synthetic_not_device",Port=port,Type=type,Sequence=BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(2)),RequestBytes=length,Request=payload,Response=reply,RequestHeaderHex=Convert.ToHexString(h),ResponseHeaderHex=Convert.ToHexString(head)});
                await stream.WriteAsync(head.AsMemory(0,5),stop.Token);await stream.WriteAsync(head.AsMemory(5),stop.Token);await stream.WriteAsync(body,stop.Token);
            }
        }
        catch(OperationCanceledException){}catch(EndOfStreamException){}catch(IOException){}catch(Exception ex){Errors.Enqueue(ex.Message);}
    }
    public async ValueTask DisposeAsync(){stop.Cancel();status.Stop();navigation.Stop();Task[] all;lock(gate)all=workers.ToArray();await Task.WhenAll(all);stop.Dispose();}
}
