using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

var root=Path.Combine(Path.GetTempPath(),"agv-single-lab-check-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var db=DatabaseFactory.Create(new(){DatabasePath=Path.Combine(root,"private.db")});
var initializer=new DatabaseInitializer(db,new TrafficResourceCatalog([new("R1",TrafficResourceType.NarrowAisle)]));
await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>initializer.InitializeAsync(default))));
Check(db.Queryable<TrafficResourceDefinitionEntity>().Count()==1,"concurrent initialization creates one resource");
db.Insertable(new TrafficResourceDefinitionEntity{ResourceId="ACTIVE-MAP",ResourceType=(int)TrafficResourceType.Station,Capacity=1,UpdatedAt=DateTime.Now}).ExecuteCommand();
await initializer.InitializeAsync(default);
Check(db.Queryable<TrafficResourceDefinitionEntity>().Any(r=>r.ResourceId=="ACTIVE-MAP"),"repeat initialization does not erase runtime map resources");
Check(new DispatchTaskCreationOptions().AutomaticCreationEnabled,"existing automatic creation remains enabled by default");

var repository=new DispatchTaskRepository(db);
var states=new EligibleStation();
var plc=new RecordingPlc();
var gate=new MapExecutionGate();gate.SetMode(MapExecutionMode.Running);
var options=new DispatchTaskCreationOptions{Enabled=true,AutomaticCreationEnabled=false,AcknowledgementRetryIntervalMs=500};
var worker=new TaskCreationCoordinator(NullLogger<TaskCreationCoordinator>.Instance,options,states,repository,plc,gate,new TaskParameterRuntime(new(),options));
await worker.StartAsync(default);
try
{
    states.Raise();
    await Task.Delay(1100);
    Check(!db.Queryable<DispatchTaskEntity>().Any(),"manual-only mode ignores initial and subsequent eligible calls");
    var created=await repository.TryCreateAsync("CNC-001","DUMPER-001",50,default);
    for(var i=0;i<30 && (await repository.GetPendingAcknowledgementsAsync(default)).Count>0;i++)await Task.Delay(100);
    var task=db.Queryable<DispatchTaskEntity>().Single(t=>t.TaskId==created.Task.TaskId);
    Check(task.Stage==(int)DispatchTaskStage.WaitingForVehicle,"manual-only mode still acknowledges persisted manual work");
    Check(plc.Writes==1,"manual task acknowledgement is sent once");
    Check(db.Queryable<DispatchTaskEntity>().Count()==1,"no extra automatic task is created");
}
finally{await worker.StopAsync(default);worker.Dispose();}
Console.WriteLine("PASS: 7 database startup/manual-only regression checks. "+root);
void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}

sealed class EligibleStation:IStationStateService
{
    readonly StationStateSnapshot state=new("CNC-001",default,true,null,true,true,false,true,SignalQuality.Good,null,0,DateTimeOffset.Now,DateTimeOffset.Now,1);
    public event Action<StationStateChanged>? StationStateChanged;
    public long Version=>1;
    public IReadOnlyList<StationStateSnapshot> GetAll()=>[state];
    public bool TryGet(string id,out StationStateSnapshot value){value=state;return id==state.StationId;}
    public void Raise()=>StationStateChanged?.Invoke(new(state.StationId,null,state,DateTimeOffset.Now));
}
sealed class RecordingPlc:IPlcGateway
{
    public int Writes;
    public Task WriteSignalAsync(string station,string signal,bool value,CancellationToken ct){Writes++;return Task.CompletedTask;}
    public Task<bool?> ReadSignalAsync(string station,string signal,CancellationToken ct)=>Task.FromResult<bool?>(null);
    public Task<StationSignal?> ReadStationAsync(string station,CancellationToken ct)=>Task.FromResult<StationSignal?>(null);
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct)=>Task.FromResult(new DeviceConnectionStatus("TEST","TEST",ConnectionState.Online,DateTimeOffset.Now));
}
