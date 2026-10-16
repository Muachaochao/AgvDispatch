using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.Connectors.Agv;

var evidence=Path.GetFullPath("docs/commissioning/evidence/20261014-map-transfer");Directory.CreateDirectory(evidence);
var results=new List<object>();var checks=0;
void Check(bool yes,string name){if(!yes)throw new Exception(name);checks++;results.Add(new{Name=name,Status="PASS"});Console.WriteLine("PASS "+name);}
var bytes=File.ReadAllBytes("docs/vendor/robokit/map-format-20261014/smap-master/maps/6.smap");
foreach(var scenario in new[]{"disabled","unconfirmed","firmware","busy","unknownTask","badTaskField","upload","load","drop","rejected","notloaded","hash","changed","identity","missingAck","wrongAck"})
{
    await using var robot=new MockRobot(scenario,bytes);
    var agv=new AgvOptions{Enabled=true,AllowCommands=true,ConnectTimeoutMs=1000,RequestTimeoutMs=500,RequestIntervalMs=100};
    await using var status=new SeerRobotClient(new(){VehicleId="SIM-MAP",IpAddress="127.0.0.1",Port=robot.StatusPort,NavigationPort=robot.StatusPort},agv);
    await using var transfer=new RobokitMapTransferSession(status,agv,new(){WritesEnabled=scenario!="disabled",ConfigurationPort=robot.ConfigPort,ControlPort=robot.ControlPort,ConfirmedRobokitVersion="SIM-FIRMWARE",FirmwareConfirmationNote="模拟版本，非真实厂家核定",Md5ConfirmationNote=scenario=="unconfirmed"?"":"测试端固定MD5；不推导实车算法"});
    var load=scenario is "load" or "notloaded";
    var result=await transfer.TransferAsync(bytes,new string('a',32),load,default);
    if(scenario is "disabled" or "unconfirmed")Check(result.State=="Blocked"&&robot.Requests.IsEmpty,scenario+" 门禁关闭不产生网络请求");
    else if(scenario is "firmware" or "busy" or "unknownTask" or "badTaskField")Check(result.State=="Blocked"&&robot.Writes==0,scenario+" 固件/任务门禁不写地图");
    else if(scenario is "upload" or "load")
    {
        Check(result.DesiredMapObserved&&result.Acknowledged,scenario+" 成功应答后原地图与MD5/载入前后核验通过");
        Check(robot.Requests.Where(x=>x.Type>=2000).Single().Port==(load?robot.ControlPort:robot.ConfigPort),scenario+" 对应类别使用独立端口");
    }
    else if(scenario=="drop")Check(result.DesiredMapObserved&&!result.Acknowledged&&robot.Writes==1,"丢失应答仅得到地图观察，不冒称收到成功回应");
    else if(scenario=="rejected")Check(result.State=="ApiRejected"&&!result.DesiredMapObserved,"非零ret_code不进入成功观察");
    else Check(result.State=="Uncertain"&&!result.DesiredMapObserved,scenario+" 不完整/冲突/变化不放行");
    if(robot.Writes>0){var repeated=await transfer.TransferAsync(bytes,new string('a',32),load,default);Check(repeated.State=="Blocked"&&robot.Writes==1,scenario+" 同会话再次调用禁止补发");}
    Check(robot.Errors.IsEmpty,scenario+" 模拟端完整地图正文/头部/端口校验无失败");
    results.Add(new{Scenario=scenario,Result=result,Writes=robot.Writes,Requests=robot.Requests.Select(r=>new{r.Port,r.Type}).ToArray(),Origin="synthetic_not_device"});
}
File.WriteAllText(Path.Combine(evidence,"results.json"),JsonSerializer.Serialize(new{Passed=checks,Failed=0,Results=results},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Passed "+checks);

sealed class MockRobot:IAsyncDisposable
{
    readonly string mode;readonly JsonElement map;readonly TcpListener status=new(IPAddress.Loopback,0),config=new(IPAddress.Loopback,0),control=new(IPAddress.Loopback,0);readonly CancellationTokenSource stop=new();readonly List<Task> workers=[];readonly object gate=new();int mapReads;
    public int Writes;public ConcurrentQueue<(int Port,ushort Type)> Requests=new();public ConcurrentQueue<string> Errors=new();
    public int StatusPort=>((IPEndPoint)status.LocalEndpoint).Port;public int ConfigPort=>((IPEndPoint)config.LocalEndpoint).Port;public int ControlPort=>((IPEndPoint)control.LocalEndpoint).Port;
    public MockRobot(string scenario,byte[] bytes){mode=scenario;using var d=JsonDocument.Parse(bytes);map=d.RootElement.Clone();foreach(var l in new[]{status,config,control}){l.Start();workers.Add(Accept(l));}}
    async Task Accept(TcpListener l){try{while(!stop.IsCancellationRequested){var c=await l.AcceptTcpClientAsync(stop.Token);lock(gate)workers.Add(Serve(c,((IPEndPoint)l.LocalEndpoint).Port));}}catch(OperationCanceledException){}catch(SocketException)when(stop.IsCancellationRequested){}}
    async Task Serve(TcpClient client,int port)
    {
        using(client)try
        {
            var stream=client.GetStream();while(!stop.IsCancellationRequested)
            {
                var head=new byte[16];await stream.ReadExactlyAsync(head,stop.Token);var type=BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(8));var count=BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(4));
                if(head[0]!=0x5A||head[1]!=1||count<0||count>4*1024*1024)throw new InvalidDataException("头部失败");var bytes=new byte[count];await stream.ReadExactlyAsync(bytes,stop.Token);Requests.Enqueue((port,type));
                if(type<2000&&port!=StatusPort||type==4010&&port!=ConfigPort||type==2025&&port!=ControlPort)throw new InvalidDataException("类别端口错误");
                object response;
                if(type is 4010 or 2025)
                {
                    using var d=JsonDocument.Parse(bytes);if(!JsonElement.DeepEquals(d.RootElement,map))throw new InvalidDataException("请求不是完整原厂家地图");Writes++;
                    if(mode=="drop")return;response=mode=="missingAck"?new{}:mode=="wrongAck"?new{ret_code="0"}:new{ret_code=mode=="rejected"?40002:0};
                }
                else response=type switch
                {
                    1000=>new{version=mode=="firmware"?"OTHER":"SIM-FIRMWARE",ret_code=0},
                    1110=>new{task_status_package=new{task_status_list=mode is "busy" or "unknownTask" or "badTaskField"?new[]{new{task_id="ACTIVE",status=mode=="badTaskField"?(object)"2":mode=="unknownTask"?404:2}}:[]},ret_code=0},
                    1300 when mode=="identity"=>new{ret_code=0},
                    1300=>new{current_map=mode=="changed"&&++mapReads>1?"OTHER":"6",current_map_md5=new string('a',32),ret_code=0},
                    1022=>new{loadmap_status=mode=="notloaded"?0:1,ret_code=0},
                    1302=>new{map_info=new[]{new{name="6.smap",md5=new string(mode=="hash"?'b':'a',32)}},ret_code=0},
                    _=>throw new InvalidDataException("未允许API")
                };
                var body=JsonSerializer.SerializeToUtf8Bytes(response);var h=new byte[16];h[0]=0x5A;h[1]=1;head.AsSpan(2,2).CopyTo(h.AsSpan(2));BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(4),body.Length);BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(8),(ushort)(type+10000));await stream.WriteAsync(h.AsMemory(0,7),stop.Token);await stream.WriteAsync(h.AsMemory(7),stop.Token);await stream.WriteAsync(body,stop.Token);
            }
        }
        catch(OperationCanceledException){}catch(EndOfStreamException){}catch(IOException){}catch(Exception ex){Errors.Enqueue(ex.Message);}
    }
    public async ValueTask DisposeAsync(){stop.Cancel();foreach(var l in new[]{status,config,control})l.Stop();Task[] all;lock(gate)all=workers.ToArray();await Task.WhenAll(all);stop.Dispose();}
}
