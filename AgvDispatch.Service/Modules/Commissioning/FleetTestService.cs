using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed class FleetClosurePendingException(string message):InvalidOperationException(message);

public interface IFleetTestPort
{
    Task<(string MapId,string Version,string Revision)> ValidateAsync(FleetTestStart plan,CancellationToken ct);
    Task<FleetTestStatus> ObserveAsync(FleetTestRun? run,CancellationToken ct);
    Task LabAsync(FleetTestRun run,string action,string[] stations,CancellationToken ct);
    Task<string?> CreateAsync(FleetTestRun run,string station,CancellationToken ct);
    Task AcknowledgeAsync(string station,string taskId,CancellationToken ct);
    Task FeedbackAsync(FleetTestRun run,TaskCenterDetail task,CancellationToken ct);
    Task CheckCurrentAsync(FleetTestRun run,CancellationToken ct);
    Task CheckClosedAsync(FleetTestRun run,CancellationToken ct);
}

public sealed class FleetTestService : IsolatedBackgroundService, IFleetTestService
{
    private readonly IFleetTestPort port;
    private readonly FleetTestOwnership ownership;
    private readonly string path;
    private readonly SemaphoreSlim mutex=new(1,1);
    private FleetTestRun? run;
    public FleetTestService(IFleetTestPort port,FleetTestOwnership ownership,string path)
    {
        this.port=port;this.ownership=ownership;this.path=path;
        if(File.Exists(path))
        {
            run=JsonSerializer.Deserialize<FleetTestRun>(File.ReadAllText(path))??throw new InvalidDataException("双车测试记录损坏。");
            if(run.Stage!="Completed") run=run with {Stage="Hold",Message="重启后保留原任务；须核对后续接，禁止自动发新呼叫。"};
        }
        Protect();
    }
    private void Protect()=>ownership.Set(run is {Stage:not "Completed"},run?.Stage=="Running",run?.Calls.Where(c=>c.TaskId!=null).Select(c=>c.TaskId!)??[]);
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using(var stream=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None))
        {JsonSerializer.Serialize(stream,run);stream.Flush(true);}
        File.Move(path+".tmp",path,true);Protect();
    }
    private void Hold(string message)
    {run=run! with {Stage="Hold",Message=message,UpdatedAt=DateTimeOffset.UtcNow};ownership.Set(true,false,[]);Save();}
    private static void Authorize(TrafficUserContext user)
    {if(!user.Has(TrafficPermission.Administer))throw new UnauthorizedAccessException("需要管理员权限。");}
    public async Task<FleetTestStatus> ReadAsync(CancellationToken ct)
    {await mutex.WaitAsync(ct);try{return await port.ObserveAsync(run,ct);}finally{mutex.Release();}}
    public async Task<FleetTestStatus> StartAsync(TrafficUserContext user,FleetTestStart request,CancellationToken ct)
    {
        Authorize(user);await mutex.WaitAsync(ct);
        try
        {
            if(run?.Plan.RequestId==request.RequestId)
            {
                if(JsonSerializer.Serialize(run.Plan)!=JsonSerializer.Serialize(request))throw new ArgumentException("同一申请不能修改参数。");
                return await port.ObserveAsync(run,ct);
            }
            if(run is {Stage:not "Completed"})throw new InvalidOperationException("原双车测试未闭合，请核对原记录。");
            if(!Guid.TryParseExact(request.RequestId,"N",out _)||!request.SiteConfirmed||
                request.Scenario is not ("Staggered" or "Simultaneous" or "Handover")||request.VehicleIds is not {Length:2}||
                request.VehicleIds.Any(string.IsNullOrWhiteSpace)||request.VehicleIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=2||
                request.StationIds is not {Length:>=2 and <=1000}||request.StationIds.Any(string.IsNullOrWhiteSpace)||
                request.StationIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=request.StationIds.Length)
                throw new ArgumentException("请选择两辆不同车辆、测试用例并确认现场。");
            if(Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(path))))
                foreach(var file in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(path))!,Path.GetFileName(path)+".*.history"))
                    if(JsonSerializer.Deserialize<FleetTestRun>(File.ReadAllText(file))?.Plan.RequestId==request.RequestId)
                        throw new InvalidOperationException("原申请已归档，禁止重放。");
            var identity=await port.ValidateAsync(request,ct);
            if(run!=null)File.Copy(path,path+"."+run.RunId+".history",false);
            var now=DateTimeOffset.UtcNow;
            run=new(Guid.NewGuid().ToString("N"),request,identity.MapId,identity.Version,identity.Revision,"Running",
                "准备按用例产生模拟P呼叫；车辆由调度分配。",false,now,now,request.StationIds.Select(id=>new FleetTestCall(id,false,null)).ToArray()){Operator=user.WindowsIdentity};
            Save();
            try{await port.LabAsync(run,"start",[],ct);}catch(Exception e){Hold("模拟器启动结果待核对："+e.Message);throw;}
            return await port.ObserveAsync(run,ct);
        }finally{mutex.Release();}
    }
    public async Task<FleetTestStatus> ControlAsync(TrafficUserContext user,string action,FleetTestControl request,CancellationToken ct)
    {
        Authorize(user);await mutex.WaitAsync(ct);
        try
        {
            if(run==null||run.RunId!=request.RunId)throw new ArgumentException("原运行编号不符。");
            if(request.Reason?.Length>1000)throw new ArgumentException("核对依据不能超过1000字。");
            run=run with {Reviews=[..run.Reviews,new(DateTimeOffset.UtcNow,action,user.WindowsIdentity,request.Reason??"")]};
            if(action=="stop")
            {
                run=run with {StopRequested=true,UpdatedAt=DateTimeOffset.UtcNow,Message="停止新增呼叫；原任务与点位保持，等待闭合。"};Save();
                await port.LabAsync(run,"stop",[],ct);
            }
            else
            {
                if(!request.SiteConfirmed||string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Trim().Length<10)
                    throw new ArgumentException("须确认现场并填写至少10字核对依据。");
                if(action=="resume")
                {
                    if(run.Stage!="Hold")throw new InvalidOperationException("仅暂停记录可以续接。");
                    await port.CheckCurrentAsync(run,ct);
                    await port.LabAsync(run,"check",[],ct);
                    var pending=run.Calls.Where(c=>c.Released&&c.TaskId==null).Select(c=>c.StationId).ToArray();
                    if(pending.Length>0&&!run.StopRequested)await port.LabAsync(run,"call",pending,ct);
                    run=run with {Stage="Running",Message="现场核对后续接原任务："+request.Reason,UpdatedAt=DateTimeOffset.UtcNow};Save();
                }
                else if(action=="close")
                {
                    if(!run.StopRequested)throw new InvalidOperationException("先停止新增呼叫，再核对结束记录。");
                    await port.CheckClosedAsync(run,ct);
                    run=run with {Stage="Completed",Message="现场核对后结束测试记录："+request.Reason,UpdatedAt=DateTimeOffset.UtcNow};Save();
                }
                else throw new ArgumentException("未知操作。");
            }
            return await port.ObserveAsync(run,ct);
        }catch(Exception e){if(run is {Stage:not "Completed"})Hold("控制结果待核对："+e.Message);throw;}
        finally{mutex.Release();}
    }
    public async Task TickAsync(CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            if(run?.Stage!="Running")return;
            try
            {
                await port.CheckCurrentAsync(run,ct);
                var observed=await port.ObserveAsync(run,ct);
                foreach(var detail in observed.Tasks)await port.FeedbackAsync(run,detail,ct);
                bool Due(int index)
                {
                    if(index==0||run.Plan.Scenario=="Simultaneous")return true;
                    var previous=observed.Tasks.SingleOrDefault(t=>t.Task.TaskId==run.Calls[index-1].TaskId);
                    return previous!=null&&(run.Plan.Scenario=="Staggered"
                        ?previous.Steps.Any(s=>s.StepType==DispatchTaskStepType.PickupFullBox&&s.Status==DispatchTaskStepStatus.Completed)
                        :previous.Steps.Any(s=>s.StepType==DispatchTaskStepType.DumpFullBox&&s.Status is DispatchTaskStepStatus.Executing or DispatchTaskStepStatus.Completed));
                }
                var due=run.StopRequested?[]:run.Calls.Where((c,i)=>!c.Released&&Due(i)).Select(c=>c.StationId).ToArray();
                if(due.Length>0)
                {
                    // Persist intent before the batch write. An uncertain write is held, never regenerated as a new run.
                    run=run with {Calls=run.Calls.Select(c=>due.Contains(c.StationId)?c with {Released=true}:c).ToArray(),UpdatedAt=DateTimeOffset.UtcNow};Save();
                    await port.LabAsync(run,"call",due,ct);
                }
                foreach(var call in run.Calls.Where(c=>c.Released&&c.TaskId==null).ToArray())
                {
                    var taskId=await port.CreateAsync(run,call.StationId,ct);
                    if(taskId==null)continue;
                    run=run with {Calls=run.Calls.Select(c=>c.StationId==call.StationId?c with {TaskId=taskId}:c).ToArray(),UpdatedAt=DateTimeOffset.UtcNow};Save();
                    await port.LabAsync(run,"bind",[call.StationId],ct);
                    await port.AcknowledgeAsync(call.StationId,taskId,ct);
                }
                if((run.StopRequested||run.Calls.All(c=>c.TaskId!=null))&&
                    run.Calls.Where(c=>c.TaskId!=null).All(c=>observed.Tasks.Any(t=>t.Task.TaskId==c.TaskId&&t.Task.Stage==DispatchTaskStage.Completed)))
                {
                    try{await port.CheckClosedAsync(run,ct);}
                    catch(FleetClosurePendingException){return;}
                    run=run with {Stage="Completed",Message="本组任务及PLC闭合；竞争、道路通行和现场安全结果仍须核验。未发送统一回待机命令。",UpdatedAt=DateTimeOffset.UtcNow};Save();
                }
            }catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception e){Hold("保留原任务和点位待核对："+e.Message);}
        }finally{mutex.Release();}
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
        while(await timer.WaitForNextTickAsync(stoppingToken))await TickAsync(stoppingToken);
    }
}
