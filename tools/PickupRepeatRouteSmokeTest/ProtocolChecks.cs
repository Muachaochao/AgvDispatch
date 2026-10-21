using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;

static class ProtocolChecks
{
    public static async Task Run(MapDefinition definition, Action<bool,string> check)
    {
        using var device = new Device(definition);
        var runtime = new ActiveMapRuntime(); runtime.Replace(new("MAP", "V1", definition, DateTimeOffset.Now));
        var options = new AgvOptions { Enabled = true, RequestIntervalMs = 100, Robots = [new() { VehicleId = "AGV-01", IpAddress = "127.0.0.1", Port = device.Port, NavigationPort = device.Port }],
            MapBindings = [new() { VehicleId = "AGV-01", LocalMapId = "MAP", LocalMapVersion = "V1", RobotMap = "repeat-map", RobotMapMd5 = new string('a', 32), CoordinatesVerified = true }] };
        await using var connections = new AgvConnectionManager(options);
        var inspection = new Inspection(definition);
        var preflight = new RobokitTaskMapPreflight(options, connections, runtime, inspection);
        var result = await preflight.CheckAsync("AGV-01", "LM-PICK", "PICK", "WAIT", "DUMP", "MAP", "V1", default);
        check(result.Accepted && result.RouteChecks.Where(r=>r.Sequence==1).Select(r=>(r.Source,r.Target)).SequenceEqual(new[]{("LM-PICK","LM-EXIT"),("LM-EXIT","LM-PICK")}), "real Robokit preflight queries both direct edges of the repeat route");
        check(device.Pairs.All(p=>p.Item1!=p.Item2), "no 1303 query invents a same-node edge");
        check(connections.TryGet("AGV-01",out var client), "configured actual protocol client selected");
        await client.GoToTargetAsync("REPEAT-PICKUP", ["LM-PICK","LM-EXIT","LM-PICK"], new AgvTransportAction("JackLoad"), true, default);
        var segments = device.Command!.Value.GetProperty("move_task_list").EnumerateArray().ToArray();
        check(segments.Length==2&&segments[0].GetProperty("source_id").GetString()=="LM-PICK"&&segments[0].GetProperty("id").GetString()=="LM-EXIT"&&segments[1].GetProperty("source_id").GetString()=="LM-EXIT"&&segments[1].GetProperty("id").GetString()=="LM-PICK", "3066 sends real exit and return segments with distinct adjacent stations");
        check(!segments[0].TryGetProperty("operation",out _)&&segments[1].GetProperty("operation").GetString()=="JackLoad", "pickup action is attached only to the final return segment");
        check(segments.Select(s=>s.GetProperty("task_id").GetString()).Distinct().Count()==2, "repeat route retains unique original subtask identities");
        device.RejectReverse=true;
        var refused=await preflight.CheckAsync("AGV-01","LM-PICK","PICK","WAIT","DUMP","MAP","V1",default);
        check(!refused.Accepted&&refused.Message.Contains("禁止跳点"), "manufacturer rejecting the return edge blocks the route");

        var pairCount=device.Pairs.Count;
        check((await preflight.CheckCurrentMapAsync("AGV-01","MAP","V1",default)).Accepted, "fresh current-map inspection accepts matching bound identity for exit evidence");
        check(device.Pairs.Count==pairCount, "current-map exit check performs no route queries or navigation");
        var baseline=inspection.Snapshot;
        var invalidIdentityIndex=0;
        foreach(var changed in new[]{baseline with{Stable=false},baseline with{Available=false},baseline with{VehicleId="OTHER"},baseline with{LocalMapId="OTHER"},baseline with{LocalMapVersion="OLD"},baseline with{RobotMap="OTHER"},baseline with{RobotMapMd5=new string('b',32)},baseline with{LoadMapStatus=0},baseline with{Issues=["1302 hash unavailable"]}})
        {
            inspection.Snapshot=changed;
            check(!(await preflight.CheckCurrentMapAsync("AGV-01","MAP","V1",default)).Accepted,"current-map check refuses invalid identity case "+(++invalidIdentityIndex));
        }
        inspection.Snapshot=baseline;
        inspection.After=()=>runtime.Replace(new("MAP","V1",definition,DateTimeOffset.Now));
        check(!(await preflight.CheckCurrentMapAsync("AGV-01","MAP","V1",default)).Accepted,"replacement runtime snapshot during fresh observation invalidates exit identity");
        inspection.After=null;
        options.MapBindings.Add(options.MapBindings[0]);
        check(!(await preflight.CheckCurrentMapAsync("AGV-01","MAP","V1",default)).Accepted,"ambiguous coordinate binding refuses exit evidence");
        options.MapBindings.RemoveAt(1);options.MapBindings[0].CoordinatesVerified=false;
        check(!(await preflight.CheckCurrentMapAsync("AGV-01","MAP","V1",default)).Accepted,"unverified coordinates refuse exit evidence");
        options.MapBindings[0].CoordinatesVerified=true;
        inspection.After=()=>options.MapBindings[0].RobotMap="changed-during-observation";
        check(!(await preflight.CheckCurrentMapAsync("AGV-01","MAP","V1",default)).Accepted,"coordinate binding mutation during observation refuses exit evidence");
        inspection.After=null;options.MapBindings[0].RobotMap="repeat-map";
        options.MapBindings[0].RobotMapMd5="";
        check(!(await preflight.CheckCurrentMapAsync("AGV-01","MAP","V1",default)).Accepted,"missing MD5 refuses exit evidence");
        options.MapBindings[0].RobotMapMd5=new string('a',32);
        check(!(await preflight.CheckCurrentMapAsync("AGV-01","MAP","OLD",default)).Accepted,"original business map version must match current runtime");
        var actual=new RobokitTaskMapPreflight(options,connections,runtime,new RobokitInspectionService(options,connections,runtime));
        check((await actual.CheckCurrentMapAsync("AGV-01","MAP","V1",default)).Accepted,"current-map gate accepts actual loopback 1300/1022/1302 inspection");
        device.BadHash=true;
        check(!(await actual.CheckCurrentMapAsync("AGV-01","MAP","V1",default)).Accepted,"actual 1302 hash mismatch blocks exit evidence");
        check(device.Pairs.Count==pairCount,"actual exit identity inspection also sends no 1303 or navigation command");
    }
    sealed class Inspection(MapDefinition definition):IRobokitInspectionService
    {
        public RobokitInspectionSnapshot Snapshot=new("AGV-01",DateTimeOffset.Now,true,true,"isolated identity fixture",null,null,"repeat-map",new string('a',32),1,"MAP","V1",false,null,definition.Nodes.Select(n=>new RobokitStation(n.AgvSiteId!,"Station",n.X,n.Y,0,null)).ToArray(),[],[],[]);
        public Action? After;
        public Task<RobokitInspectionSnapshot> ReadAsync(RobokitInspectionQuery query,CancellationToken ct){After?.Invoke();return Task.FromResult(Snapshot);}
    }
    sealed class Device:IDisposable
    {
        readonly TcpListener listener=new(IPAddress.Loopback,0);readonly CancellationTokenSource stop=new();readonly Task server;
        readonly MapDefinition definition;
        public int Port{get;} public bool RejectReverse, BadHash;public JsonElement? Command;public List<(string,string)> Pairs=[];
        public Device(MapDefinition definition){this.definition=definition;listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;server=Task.Run(Serve);}
        async Task Serve()
        {
            try
            {
                using var client=await listener.AcceptTcpClientAsync(stop.Token);using var stream=client.GetStream();
                while(!stop.IsCancellationRequested)
                {
                    var header=new byte[16];await stream.ReadExactlyAsync(header,stop.Token);var size=BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4,4));if(size is <0 or >1048576)throw new Exception("Bad test frame");
                    var data=new byte[size];await stream.ReadExactlyAsync(data,stop.Token);using var request=JsonDocument.Parse(size==0?"{}":System.Text.Encoding.UTF8.GetString(data));
                    var type=BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8,2));object response;
                    switch(type)
                    {
                        case 1000:response=new{version="3.4.fixture",netprotocol_version="1"};break;
                        case 1060:response=new{locked=false,nick_name="fixture"};break;
                        case 1301:response=new{stations=definition.Nodes.Select(n=>new{id=n.AgvSiteId,type="Station",x=n.X,y=n.Y,r=0})};break;
                        case 1302:response=new{map_info=new[]{new{name="repeat-map.smap",md5=new string(BadHash?'b':'a',32)}}};break;
                        case 1303:
                            var a=request.RootElement.GetProperty("source_id").GetString()!;var b=request.RootElement.GetProperty("target_id").GetString()!;Pairs.Add((a,b));
                            response=new{list=RejectReverse&&a=="LM-EXIT"&&b=="LM-PICK"?new[]{a,"OTHER",b}:new[]{a,b}};break;
                        case 1300:response=new{current_map="repeat-map",current_map_md5=new string('a',32)};break;
                        case 1022:response=new{loadmap_status=1};break;
                        case 3066:Command=request.RootElement.Clone();response=new{ret_code=0};break;
                        default:throw new Exception("Unexpected test request "+type);
                    }
                    var output=JsonSerializer.SerializeToUtf8Bytes(response);BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4,4),output.Length);BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8,2),(ushort)(type+10000));
                    await stream.WriteAsync(header,stop.Token);await stream.WriteAsync(output,stop.Token);await stream.FlushAsync(stop.Token);
                }
            }
            catch(Exception ex)when(ex is OperationCanceledException or EndOfStreamException or ObjectDisposedException){}
        }
        public void Dispose(){stop.Cancel();listener.Stop();server.GetAwaiter().GetResult();stop.Dispose();}
    }
}
