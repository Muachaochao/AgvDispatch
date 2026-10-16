using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using AgvDispatch.Wpf.Services;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Prism.Mvvm;

sealed class EmptyFlow : ITrafficFlowMonitor
{
    public TrafficFlowSnapshot GetSnapshot()=>new(DateTimeOffset.Now,"MAP-INTEGRATION","V1",[],[]);
    public IReadOnlyDictionary<string,double> GetEdgePenalties()=>new Dictionary<string,double>();
}
sealed class SimulatedStations(SimulatedPlc plc) : IStationStateService
{
    public event Action<StationStateChanged>? StationStateChanged { add{} remove{} } public long Version=>plc.Version;
    public IReadOnlyList<StationStateSnapshot> GetAll()=>[];
    public bool TryGet(string id,out StationStateSnapshot snapshot)
    { snapshot=new(id,default,true,null,true,true,false,true,SignalQuality.Good,null,0,DateTimeOffset.Now,DateTimeOffset.Now,Version); return true; }
}
sealed class SimulatedPlc : IPlcGateway,IPlcSignalCache,IPlcConfigurationInspector
{
    readonly ConcurrentDictionary<(string,string),PlcSignalSnapshot> signals=new();
    public ConcurrentQueue<(string Station,string Name,bool Value)> Pending=new(); public ConcurrentQueue<(string Station,string Name,bool Value)> Writes=new();
    readonly List<PlcSignalDefinition> definitions=[];
    public SimulatedPlc()
    {
        foreach(var station in new[]{"CNC001","CNC002","DUMPER-001"})
        foreach(var name in station.StartsWith("CNC") ? new[]{"FullBoxRequested","BoxPresent","Faulted","CommunicationOnline","FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced"} : new[]{"Faulted","DumperReady","AgvAtWaitingPosition","AgvAtDumpingPosition","DumpingCompletedMayLeave"})
        {
            var write=new[]{"FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced","AgvAtWaitingPosition","AgvAtDumpingPosition"}.Contains(name);
            var address="DB1.DBX"+definitions.Count+".0";
            definitions.Add(new(address,name,name,station,"simulation",0,"Bool","",write?"Write":"Read","Confirmed",true,"","","isolated test"));
            signals[(station,name)]=new(station,name,name,address,"Bool","",write?"Write":"Read",true,false,SignalQuality.Good,DateTimeOffset.Now,DateTimeOffset.Now,0);
            Set(station,name,name is "BoxPresent" or "CommunicationOnline" or "FullBoxRequested");
        }
    }
    public void Set(string s,string n,bool value,SignalQuality quality=SignalQuality.Good) { signals[(s,n)]=signals[(s,n)] with { Value=value,Quality=quality,Version=Interlocked.Increment(ref version),UpdatedAt=DateTimeOffset.Now }; }
    public bool Value(string s,string n)=>signals[(s,n)].Value==true;
    public void Scan() { while(Pending.TryDequeue(out var p)) Set(p.Station,p.Name,p.Value); }
    public event Action<PlcSignalChanged>? SignalChanged { add{} remove{} } long version; public long Version => Volatile.Read(ref version);
    public IReadOnlyList<PlcSignalSnapshot> GetAll()=>signals.Values.ToArray();
    public IReadOnlyList<PlcSignalSnapshot> GetStation(string s)=>signals.Values.Where(p=>p.StationId==s).ToArray();
    public bool TryGet(string s,string n,out PlcSignalSnapshot p)=>signals.TryGetValue((s,n),out p!);
    public Task WriteSignalAsync(string s,string n,bool v,CancellationToken ct) { ct.ThrowIfCancellationRequested(); if(GetSignalConfigurationError(s,n,true)!=null) throw new Exception("Invalid simulated write"); Pending.Enqueue((s,n,v)); Writes.Enqueue((s,n,v)); return Task.CompletedTask; }
    public Task<bool?> ReadSignalAsync(string s,string n,CancellationToken ct)=>Task.FromResult(signals[(s,n)].Value);
    public Task<StationSignal?> ReadStationAsync(string s,CancellationToken ct)=>Task.FromResult<StationSignal?>(null);
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct)=>Task.FromResult(new DeviceConnectionStatus("SIM-PLC","模拟PLC",ConnectionState.Online,DateTimeOffset.Now));
    public string? GetSignalConfigurationError(string s,string n,bool write)=>PlcTaskConfigurationInspector.Inspect(new(){Enabled=true,AllowWrites=true},PlcSignalCatalog.FromSignals(definitions),s,n,write);
}
sealed class SimulatedRobot : IDisposable
{
    public sealed record Command(string Source,string Destination,string Operation);
    readonly TcpListener listener=new(IPAddress.Loopback,0); readonly CancellationTokenSource stop=new(); readonly Task server;
    readonly IReadOnlyList<MapNode> nodes; readonly Dictionary<string,int> statuses=[];
    public ConcurrentDictionary<string,Command> Commands { get; }=new(); string station;
    public AgvRobotOptions Configuration { get; }
    public SimulatedRobot(string id,string home,IReadOnlyList<MapNode> nodes)
    { this.nodes=nodes; station=home; listener.Start(); Configuration=new(){VehicleId=id,IpAddress="127.0.0.1",Port=((IPEndPoint)listener.LocalEndpoint).Port}; Configuration.NavigationPort=Configuration.Port; server=Task.Run(Serve); }
    public void Complete(string id) { lock(statuses) { statuses[id]=4; station=Commands[id].Destination; } }
    async Task Serve()
    {
        try
        {
            using var client=await listener.AcceptTcpClientAsync(stop.Token); using var stream=client.GetStream();
            while(!stop.IsCancellationRequested)
            {
                var header=new byte[16]; await stream.ReadExactlyAsync(header,stop.Token);
                var length=BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4,4)); if(length<0 || length>1048576) throw new Exception("Mock frame length");
                var data=new byte[length]; await stream.ReadExactlyAsync(data,stop.Token); using var request=JsonDocument.Parse(length==0?"{}":System.Text.Encoding.UTF8.GetString(data));
                var type=BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8,2)); object response;
                lock(statuses)
                {
                    if(type==1100) { var node=nodes.Single(n=>n.Id==station); response=new { x=node.X,y=node.Y,angle=0,confidence=1,current_station=station,battery_level=0.9,blocked=false,emergency=false }; }
                    else if(type==1110)
                    {
                        var ids=request.RootElement.TryGetProperty("task_ids",out var list)?list.EnumerateArray().Select(x=>x.GetString()!).ToArray():statuses.Keys.ToArray();
                        response=new { task_status_list=ids.Select(id=>new {task_id=id,status=statuses.GetValueOrDefault(id,404)}).ToArray() };
                    }
                    else if(type==3066)
                    {
                        var list=request.RootElement.GetProperty("move_task_list").EnumerateArray().ToArray(); var last=list[^1]; var id=last.GetProperty("task_id").GetString()!;
                        if(Commands.ContainsKey(id)) throw new Exception("Duplicate command "+id);
                        Commands[id]=new(list[0].GetProperty("source_id").GetString()!,last.GetProperty("id").GetString()!,last.GetProperty("operation").GetString()!); statuses[id]=2; response=new{ret_code=0};
                    }
                    else throw new Exception("Unexpected mock protocol "+type);
                }
                var payload=JsonSerializer.SerializeToUtf8Bytes(response); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4,4),payload.Length); BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8,2),(ushort)(type+10000));
                await stream.WriteAsync(header,stop.Token); await stream.WriteAsync(payload,stop.Token); await stream.FlushAsync(stop.Token);
            }
        }
        catch(Exception e) when(e is OperationCanceledException or EndOfStreamException or ObjectDisposedException) { }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); try{server.GetAwaiter().GetResult();}finally{stop.Dispose();} }
}

