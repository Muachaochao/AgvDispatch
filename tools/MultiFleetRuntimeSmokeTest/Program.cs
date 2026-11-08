using System.Reflection;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

// Actual orchestration, outbox, persistent 28-stage handshake and SQLite. Only the
// device boundary is synthetic; this executable opens no network connections.
var checks = 0;
void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; Console.WriteLine("PASS " + reason); }
var evidence = new List<object>();
foreach (var (cars, workstations) in new[] { (3, 7), (5, 11) })
{
    using var f = new RuntimeFixture(cars, workstations);
    await f.Initialize();
    for (var round = 0; round < 2; round++)
    {
        var ids = await f.CreateRound();
        Check(ids.Count == workstations && ids.Distinct().Count() == workstations, $"{cars}车/{workstations}工位第{round + 1}轮独立任务入库");
        var peak = 0; var waiting = false; var restart = false; var offline = false; var healthyProgress = false;
        string? offlineTask = null; string[] offlineCommands = []; var healthyCompletedBeforeOffline = 0;
        for (var tick = 0; tick < 1800; tick++)
        {
            await f.Tick();
            var active = await f.Repository.GetAssignedTasksAsync(default);
            peak = Math.Max(peak, active.Count);
            CheckQuiet(active.Select(t => t.VehicleId).Distinct().Count() == active.Count, "一辆车同时持有冲突业务任务");
            CheckQuiet(active.Count <= cars, "超过配置并发上限");
            var leases = await f.Db.Queryable<TrafficResourceLeaseEntity>().ToListAsync();
            foreach (var group in leases.GroupBy(x => x.ResourceId))
                CheckQuiet(group.Select(x => x.TaskId).Distinct().Count() <= 1, "容量1资源出现不同任务重叠：" + group.Key);
            waiting |= (await f.Control.GetWaitingAsync(new(), default)).Items.Count > 0;
            // Restart the executor while equipment and route ownership are held.
            if (round == 0 && !restart && await f.Db.Queryable<TaskEquipmentClaimEntity>().CountAsync() > 0)
            {
                var before = leases.Select(l => (l.ResourceId, l.TaskId, l.VehicleId)).OrderBy(x => x.ResourceId).ToArray();
                f.Restart();
                var after = (await f.Db.Queryable<TrafficResourceLeaseEntity>().ToListAsync()).Select(l => (l.ResourceId, l.TaskId, l.VehicleId)).OrderBy(x => x.ResourceId).ToArray();
                Check(before.SequenceEqual(after), $"{cars}车重建执行器保留原占用，不重建原任务"); restart = true;
            }
            // One vehicle becomes unavailable after acceptance. Preserve its original
            // command and continue polling the remaining vehicles, then restore it.
            if (round == 0 && tick == 1)
            {
                offlineTask = active.Single(t => t.VehicleId == f.VehicleIds[0]).TaskId;
                offlineCommands = (await f.Db.Queryable<AgvCommandOutboxEntity>().Where(c => c.TaskId == offlineTask).ToListAsync()).Select(c => c.CommandId).ToArray();
                CheckQuiet(offlineCommands.Length > 0, "断线注入前必须已有原命令，不能空测保留行为");
                healthyCompletedBeforeOffline = await f.Db.Queryable<DispatchTaskStepEntity>().Where(s => s.VehicleId != f.VehicleIds[0] && s.Status == 50).CountAsync();
                f.Gateway.Offline = f.VehicleIds[0]; offline = true;
            }
            if (round == 0 && tick > 1 && tick < 8)
            {
                CheckQuiet(active.Any(t => t.TaskId == offlineTask && t.VehicleId == f.VehicleIds[0]), "断线后丢失或替换原任务归属");
                var retainedCommands = await f.Db.Queryable<AgvCommandOutboxEntity>().Where(c => c.TaskId == offlineTask).ToListAsync();
                CheckQuiet(offlineCommands.All(id => retainedCommands.Any(c => c.CommandId == id && c.VehicleId == f.VehicleIds[0])), "断线后替换原命令或车辆归属");
                var steps = await f.Db.Queryable<DispatchTaskStepEntity>().Where(s => s.VehicleId != f.VehicleIds[0] && s.Status == 50).CountAsync();
                healthyProgress |= steps > healthyCompletedBeforeOffline;
            }
            if (round == 0 && tick == 8) f.Gateway.Offline = null;
            var rows = await f.Db.Queryable<DispatchTaskEntity>().Where(t => ids.Contains(t.TaskId)).ToListAsync();
            if (rows.Any(t => (DispatchTaskStage)t.Stage == DispatchTaskStage.Faulted))
                throw new Exception("任务故障：" + JsonSerializer.Serialize(rows));
            if (rows.All(t => (DispatchTaskStage)t.Stage == DispatchTaskStage.Completed)) break;
            if (tick == 1799) throw new Exception("执行预算耗尽：" + await f.Diagnostics(ids));
            await Task.Delay(25);
        }
        var finished = await f.Db.Queryable<DispatchTaskEntity>().Where(t => ids.Contains(t.TaskId)).ToListAsync();
        Check(finished.All(t => (DispatchTaskStage)t.Stage == DispatchTaskStage.Completed), $"{cars}车第{round + 1}轮全部正常完成");
        Check(peak == cars, $"实际调度器达到配置并发{cars}（共享倒料仍容量1排队）");
        Check(waiting, "资源争用产生可查询的等待队列");
        foreach (var id in ids)
        {
            var steps = await f.Writer.GetStepsAsync(id, default);
            CheckQuiet(steps.Count == 9 && steps.All(s => s.Status == DispatchTaskStepStatus.Completed), "九步没有完整闭合");
        }
        Check(await f.Db.Queryable<TrafficResourceLeaseEntity>().CountAsync() == 0 && await f.Db.Queryable<TaskEquipmentClaimEntity>().CountAsync() == 0, "收尾后路径、等待位及倒料设备归属全部闭合");
        Check(f.Signals.Closed, "原工位及共享倒料握手清位，允许下一轮");
        var commands = await f.Db.Queryable<AgvCommandOutboxEntity>().Where(c => ids.Contains(c.TaskId)).ToListAsync();
        if (round == 0) CheckQuiet(offlineCommands.All(id => commands.Any(c => c.CommandId == id && c.TaskId == offlineTask && c.VehicleId == f.VehicleIds[0])), "恢复后原任务/命令/车辆对应关系发生变化");
        Check(commands.Count == workstations * 4 && commands.All(c => c.AttemptCount == 1 && c.Status == (int)AgvCommandStatus.Sent), "四段命令各发送一次，重启和恢复不重复派发");
        if (round == 0) Check(restart && offline && healthyProgress, "单车断线保留原归属，其余车辆继续推进；恢复及组件重启后无重复派发");
        evidence.Add(new { Vehicles = cars, Stations = workstations, Round = round + 1, PeakAssigned = peak, Tasks = ids, Commands = commands.Count });
    }
    Console.WriteLine("Isolated SQLite: " + f.Path);
}
var output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agv-multifleet-" + Guid.NewGuid().ToString("N") + ".json");
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Checks = checks, Runs = evidence, Boundary = "Real service components and SQLite; in-process synthetic PLC/AGV only. No sockets, field devices, kinematic collision or physical acceptance." }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS {checks} multi-fleet runtime checks; evidence: {output}");
void CheckQuiet(bool value, string reason) { if (!value) throw new Exception(reason); }

sealed class RuntimeFixture(int carCount, int stationCount) : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fleet-runtime-" + Guid.NewGuid().ToString("N"), "private.db");
    public SqlSugar.ISqlSugarClient Db { get; private set; } = null!;
    public DispatchTaskRepository Writer { get; private set; } = null!;
    public DispatchOrchestrationRepository Repository { get; private set; } = null!;
    public TrafficResourceControlService Control { get; private set; } = null!;
    public SyntheticSignals Signals { get; } = new();
    public SyntheticFleet Gateway { get; } = new();
    public string[] VehicleIds { get; } = Enumerable.Range(0, carCount).Select(i => "TUG-" + (i * 17 + 8)).ToArray();
    private readonly string[] stations = Enumerable.Range(0, stationCount).Select(i => "CELL-" + (i * 13 + 2)).ToArray();
    private readonly ActiveMapRuntime map = new();
    private readonly MapExecutionGate gate = new();
    private readonly DispatchOrchestratorOptions dispatch = new() { Enabled = true, MaxConcurrentTasks = carCount, DumperWaitingSite = "BUFFER", DumperDumpingSite = "TIP" };
    private readonly DispatchTaskCreationOptions creation = new() { DumperStationId = "TIPPER" };
    private readonly HandshakeExecutionOptions handshake = new() { Enabled = true, PlcContractVersion = HandshakeExecutionOptions.Version, StateMaxAgeSeconds = 30,
        DumperExit = new() { ResourceIds = ["TIP-AREA"], MinimumObservationIntervalSeconds = .1, MaximumAgeSeconds = 3,
            MinimumConfidence = .9, SafetyMarginMeters = .15, MaximumRouteDeviationMeters = .25 } };
    private AgvCommandOutboxRepository outbox = null!;
    private TrafficResourceManager traffic = null!;
    private TaskHandshakeStore store = null!;
    private ConfiguredRoutePlanner planner = null!;
    private DispatchTaskOrchestrator orchestrator = null!;
    private DispatchTaskStepCoordinator coordinator = null!;
    private AgvCommandOutboxDispatcher sender = null!;
    public async Task Initialize()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        Db = DatabaseFactory.Create(new() { DatabasePath = Path });
        var resources = new List<TrafficResourceDefinition> {
            new("TIP-AREA", TrafficResourceType.Station, 1, [new(-1,-1),new(1,-1),new(1,1),new(-1,1)]),
            new("BUFFER-BAY", TrafficResourceType.Station, 1) };
        var nodes = new List<MapNode> { new("TIP",0,0,MapNodeType.Station,true,["TIP-AREA"],"TIP"),
            new("OUT",4,0,MapNodeType.Waypoint,true,[],"OUT"), new("BUFFER",-4,0,MapNodeType.WaitingPoint,true,["BUFFER-BAY"],"BUFFER") };
        var edges = new List<MapEdge>();
        void Edge(string a, string b, string[] r) { var first = nodes.Single(n => n.Id == a); var last = nodes.Single(n => n.Id == b);
            edges.Add(new("L-" + edges.Count,a,b,Math.Sqrt(Math.Pow(first.X-last.X,2)+Math.Pow(first.Y-last.Y,2)),1,true,true,r)); }
        for (var i = 0; i < stationCount; i++)
        {
            var site = "LOAD-" + i; var lockId = "DOCK-" + i;
            nodes.Add(new(site,10 + i * 2,10,MapNodeType.Station,true,[lockId],site));
            resources.Add(new(lockId,TrafficResourceType.Station,1));
            dispatch.StationPickupSites[stations[i]] = site;
            // All configured vehicles are eligible, not fixed to one sample station.
            dispatch.StationVehiclePools[stations[i]] = VehicleIds.ToArray();
            Signals.Initialize(stations[i]);
            Edge(site,"BUFFER",[lockId,"BUFFER-BAY"]); Edge("OUT",site,[lockId]);
        }
        for (var i = 0; i < carCount; i++)
        {
            var site = "PARK-" + i; nodes.Add(new(site,-12 - i * 2,10,MapNodeType.Station,true,[],site));
            Gateway.Positions[VehicleIds[i]] = site;
            foreach (var pickup in dispatch.StationPickupSites.Values) Edge(site,pickup,[]);
            handshake.DumperExit.BodyProfiles.Add(new("PROFILE-" + i,VehicleIds[i],.5,.05,true,true));
        }
        Edge("BUFFER","TIP",["BUFFER-BAY","TIP-AREA"]); Edge("TIP","OUT",["TIP-AREA"]);
        var definition = new MapDefinition("synthetic fleet","in-process only","LocalCartesian","m",nodes,edges,resources);
        // The route planner enriches bidirectional/merge geometry with runtime-only
        // single-lane guards. Register the same expanded resource topology with the
        // persistent traffic manager; otherwise the fixture passes planning but fails
        // the first real lease with an unknown AUTO-CORRIDOR-* resource.
        var runtimeResources = MapCorridorResources.AllResources(definition);
        map.Replace(new("SYNTHETIC","R1",definition,DateTimeOffset.UtcNow)); Gateway.Nodes = nodes;
        await new DatabaseInitializer(Db,new TrafficResourceCatalog(runtimeResources)).InitializeAsync(default);
        Writer = new(Db); Repository = new(Db); outbox = new(Db,map); store = new(Db,map,Signals);
        traffic = new(Db,new(runtimeResources),new(TimeSpan.FromMinutes(2))); Control = new(Db,new(runtimeResources));
        foreach (var kind in TaskParameterRuntime.TransportSteps) dispatch.StepActions[kind.ToString()] = new() { Operation = kind == DispatchTaskStepType.GoToPickup ? "JackLoad" : kind == DispatchTaskStepType.ReturnEmptyBox ? "JackUnload" : "Wait" };
        planner = new(new MapDefinitionProvider(new MapOptions(),new MapImportService(),new MapValidationService()),map,dispatch,new NoFlow());
        gate.SetMode(MapExecutionMode.Running); Restart();
    }
    public void Restart()
    {
        orchestrator?.Dispose(); coordinator?.Dispose(); sender?.Dispose();
        var executor = new PersistentHandshakeExecutor(store,Signals,Signals,Repository,Writer,traffic,creation,dispatch,handshake,Gateway,map,Gateway,new SyntheticPreflight());
        orchestrator = new(NullLogger<DispatchTaskOrchestrator>.Instance,dispatch,Repository,outbox,Gateway,Gateway,map,new MapOptions(),planner,traffic,gate,handshakeOptions:handshake,handshake:executor);
        coordinator = new(new DiagnosticLogger<DispatchTaskStepCoordinator>(),new(),dispatch,creation,Repository,outbox,Signals,Signals,planner,traffic,gate,vehicleStates:Gateway,activeMap:map,handshake:executor);
        sender = new(NullLogger<AgvCommandOutboxDispatcher>.Instance,new(),outbox,Gateway,gate,handshakes:store,taskReader:Writer,handshakeOptions:handshake,handshake:executor);
    }
    public async Task<List<string>> CreateRound()
    {
        var result = new List<string>();
        foreach (var station in stations)
        {
            Signals.Set(station,"FullBoxRequested",true); Signals.Set(station,"BoxPresent",true);
            var created = await Writer.TryCreateAsync(station,creation.DumperStationId,50,default);
            if (!created.Created) throw new Exception("Previous cycle not closed");
            Signals.Set(station,"FullBoxRequestAcknowledged",true); Signals.Set(station,"FullBoxRequested",false);
            await Writer.MarkAcknowledgedAsync(created.Task.TaskId,default); result.Add(created.Task.TaskId);
        }
        return result;
    }
    public async Task Tick()
    {
        Gateway.Advance(Signals,dispatch); Signals.Pulse();
        await Cycle(orchestrator,"RunCycleAsync"); await Cycle(coordinator,"RunCycleAsync"); await Cycle(sender,"DispatchDueCommandsAsync");
    }
    private static Task Cycle(object target,string name) => (Task)target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,[CancellationToken.None])!;
    public async Task<string> Diagnostics(List<string> ids) => JsonSerializer.Serialize(new { Positions = Gateway.Positions, Tasks = (await Db.Queryable<DispatchTaskEntity>().Where(t => ids.Contains(t.TaskId)).ToListAsync()).Select(t => new { t.StationId,t.VehicleId,t.Stage }),
        Checkpoints = (await Db.Queryable<TaskHandshakeEntity>().ToListAsync()).Select(c => new {c.TaskId,c.Phase,c.Reason}) });
    public void Dispose() { orchestrator?.Dispose(); coordinator?.Dispose(); sender?.Dispose(); Db?.Dispose(); }
}

sealed class SyntheticSignals : IPlcSignalCache,IPlcGateway
{
    readonly Dictionary<(string,string),PlcSignalSnapshot> values = []; long version;
    public bool Closed => values.Where(p => p.Key.Item2 is not ("BoxPresent" or "CommunicationOnline" or "Faulted")).All(p => p.Value.Value == false);
    public void Initialize(string station)
    { foreach (var name in TaskHandshakeSignals.Pickup) Set(station,name,name is "BoxPresent" or "CommunicationOnline");
      foreach (var name in TaskHandshakeSignals.Dumper) if (!values.ContainsKey(("TIPPER",name))) Set("TIPPER",name,false); }
    public void Set(string s,string n,bool value) => values[(s,n)] = new(s,n,n,"DB:"+s+":"+n,"BOOL","logical","configured",true,value,SignalQuality.Good,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,++version);
    bool Value(string s,string n) => values[(s,n)].Value == true;
    public void Pulse()
    { Set("TIPPER","DumperReady",Value("TIPPER","AgvAtWaitingPosition")); Set("TIPPER","DumpingCompletedMayLeave",Value("TIPPER","AgvAtDumpingPosition"));
      foreach (var item in values.ToArray()) Set(item.Key.Item1,item.Key.Item2,item.Value.Value == true); }
    public event Action<PlcSignalChanged>? SignalChanged { add { } remove { } }
    public long Version => version;
    public IReadOnlyList<PlcSignalSnapshot> GetAll() => values.Values.ToArray();
    public IReadOnlyList<PlcSignalSnapshot> GetStation(string id) => values.Values.Where(x => x.StationId == id).ToArray();
    public bool TryGet(string s,string n,out PlcSignalSnapshot result) => values.TryGetValue((s,n),out result!);
    public Task WriteSignalAsync(string s,string n,bool v,CancellationToken ct) { Set(s,n,v); return Task.CompletedTask; }
    public Task<bool?> ReadSignalAsync(string s,string n,CancellationToken ct) => Task.FromResult(values[(s,n)].Value);
    public Task<StationSignal?> ReadStationAsync(string id,CancellationToken ct) => throw new NotSupportedException();
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => Task.FromResult(new DeviceConnectionStatus("SYNTHETIC","in-process PLC",ConnectionState.Online,DateTimeOffset.UtcNow));
}
sealed class SyntheticFleet : IAgvGateway,IAgvStateCache
{
    sealed record Command(string Id,string Vehicle,string[] Path,AgvTransportAction? Action,int Ticks = 0,bool Done = false);
    readonly Dictionary<string,Command> commands = []; long version;
    DateTimeOffset observedAt = DateTimeOffset.UtcNow;
    public string? Offline;
    public Dictionary<string,string> Positions { get; } = [];
    public IReadOnlyList<MapNode> Nodes = [];
    public void Advance(SyntheticSignals plc,DispatchOrchestratorOptions dispatch)
    {
        observedAt = DateTimeOffset.UtcNow; version++;
        foreach (var c in commands.Values.Where(c => !c.Done && c.Vehicle != Offline).ToArray())
        {
            var next = c with { Ticks = c.Ticks + 1 }; commands[c.Id] = next;
            if (next.Ticks < 3) continue;
            Positions[c.Vehicle] = c.Path[^1]; commands[c.Id] = next with { Done = true };
            var station = dispatch.StationPickupSites.SingleOrDefault(p => p.Value == c.Path[^1]).Key;
            if (station != null && c.Action?.Operation is "JackLoad" or "JackUnload")
            {
                plc.Set(station,"BoxPresent",c.Action.Operation == "JackUnload");
                if (c.Action.Operation == "JackUnload") plc.Set(station,"EmptyBoxPlaced",true);
            }
        }
    }
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => Task.FromResult(new DeviceConnectionStatus("SYNTHETIC","in-process AGVs",Offline == null ? ConnectionState.Online : ConnectionState.Degraded,DateTimeOffset.UtcNow));
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgvVehicleStatus>>(Positions.Select(p => new AgvVehicleStatus(p.Key,p.Key == Offline ? ConnectionState.Disconnected : ConnectionState.Online,commands.Values.Any(c => c.Vehicle == p.Key && !c.Done) ? "Running" : "Idle",90,p.Value,DateTimeOffset.UtcNow)).ToArray());
    public Task<string> CreateTransportTaskAsync(string id,string? vehicle,IReadOnlyList<string> path,AgvTransportAction? action,CancellationToken ct)
    { if (commands.ContainsKey(id) || vehicle == null || vehicle == Offline) throw new Exception("Duplicate or offline send"); commands.Add(id,new(id,vehicle,path.ToArray(),action)); return Task.FromResult(id); }
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id,CancellationToken ct)
    { if (commands.TryGetValue(id,out var c) && c.Vehicle == Offline) throw new IOException("Synthetic vehicle offline");
      return Task.FromResult<AgvTransportTaskStatus?>(c == null ? null : new(id,id,c.Done ? AgvTransportTaskState.Completed : AgvTransportTaskState.GoingToDestination,c.Vehicle,DateTimeOffset.UtcNow)); }
    public Task CancelTaskAsync(string id,CancellationToken ct) => throw new NotSupportedException();
    public event Action<AgvRobotStateChanged>? StateChanged { add { } remove { } }
    public long Version => version;
    public IReadOnlyList<AgvRobotSnapshot> GetAll() => Positions.Keys.Select(Snapshot).ToArray();
    public bool TryGet(string id,out AgvRobotSnapshot result) { result = Positions.ContainsKey(id) ? Snapshot(id) : null!; return result != null; }
    AgvRobotSnapshot Snapshot(string id)
    { var node = Nodes.Single(n => n.Id == Positions[id]); var at = id == Offline ? observedAt.AddMinutes(-1) : observedAt;
      return new(id,id,"synthetic",0,id == Offline ? ConnectionState.Disconnected : ConnectionState.Online,SignalQuality.Good,node.X,node.Y,0,.99,node.Id,null,90,false,false,"Running",null,null,null,[],[],at,at,0,null,null,version) { VelocityX = 0,VelocityY = 0,AngularVelocity = 0 }; }
}
sealed class SyntheticPreflight : IRobokitTaskMapPreflight
{
    public Task<RobokitTaskMapCheck> CheckCurrentMapAsync(string v,string m,string r,CancellationToken ct) => Task.FromResult(new RobokitTaskMapCheck(m == "SYNTHETIC" && r == "R1","synthetic map evidence only"));
    public Task<RobokitTaskMapCheck> CheckAsync(string v,string s,string p,string w,string d,string m,string r,CancellationToken ct) => CheckCurrentMapAsync(v,m,r,ct);
}
sealed class NoFlow : ITrafficFlowMonitor
{
    public TrafficFlowSnapshot GetSnapshot() => new(DateTimeOffset.UtcNow,"SYNTHETIC","R1",[],[]);
    public IReadOnlyDictionary<string,double> GetEdgePenalties() => new Dictionary<string,double>();
}
sealed class DiagnosticLogger<T> : ILogger<T>
{
    readonly HashSet<string> seen = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
    public void Log<TState>(LogLevel level,EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
    { if(level>=LogLevel.Warning&&exception is not null&&seen.Add(exception.ToString()))Console.WriteLine("DIAGNOSTIC "+exception.GetBaseException().Message); }
}
