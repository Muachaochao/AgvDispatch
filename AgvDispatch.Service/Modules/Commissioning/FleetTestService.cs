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
    Task<FleetTestStandbyReturn[]> PlanStandbyReturnsAsync(FleetTestRun run,CancellationToken ct);
    Task<FleetTestStandbyReturn?> TrySendStandbyReturnAsync(FleetTestRun run,FleetTestStandbyReturn[] returns,
        FleetTestStandbyReturn assignment,CancellationToken ct);
    Task<FleetTestStandbyReturn> ObserveStandbyReturnAsync(FleetTestRun run,FleetTestStandbyReturn assignment,
        CancellationToken ct);
    Task ReleaseStandbyReturnAsync(FleetTestStandbyReturn assignment,CancellationToken ct);
}

public sealed partial class FleetTestService : IsolatedBackgroundService, IFleetTestService
{
    private readonly IFleetTestPort port;
    private readonly FleetTestOwnership ownership;
    private readonly string path;
    private readonly bool returnToStandby;
    private readonly SemaphoreSlim mutex=new(1,1);
    private FleetTestRun? run;
    private readonly ILogger<FleetTestService> logger;
    public FleetTestService(IFleetTestPort port,FleetTestOwnership ownership,string path,ILogger<FleetTestService>? logger=null,
        FleetTestOptions? options=null)
    {
        this.port=port;this.ownership=ownership;this.path=path;
        returnToStandby=options?.ReturnToStandby??true;
        this.logger=logger??Microsoft.Extensions.Logging.Abstractions.NullLogger<FleetTestService>.Instance;
        LoadStartRetirements();
        if(File.Exists(path))
        {
            run=JsonSerializer.Deserialize<FleetTestRun>(File.ReadAllText(path))??throw new InvalidDataException("多车测试记录损坏。");
            if(run.Stage!="Completed") Hold("重启后保留原任务；须核对后续接，禁止自动发新呼叫。原状态："+run.Message);
        }
        Protect();
    }
    private void Protect()=>ownership.Set(run is {Stage:not "Completed"},run?.Stage=="Running",run?.Calls.Where(c=>c.TaskId!=null).Select(c=>c.TaskId!)??[],run?.Plan.VehicleIds);
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using(var stream=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None))
        {JsonSerializer.Serialize(stream,run);stream.Flush(true);}
        File.Move(path+".tmp",path,true);Protect();
    }
    private void Hold(string message)
    {
        var now=DateTimeOffset.UtcNow;
        run=run! with {Stage="Hold",Message=message,UpdatedAt=now,Holds=[..run.Holds,new(now,message)]};
        ownership.Set(true,false,[]);Save();
        logger.LogWarning("多车测试暂停。RunId={RunId}, Reason={Reason}, TaskIds={TaskIds}",
            run.RunId,message,string.Join(',',run.Calls.Where(c=>c.TaskId!=null).Select(c=>c.TaskId)));
    }
    private void UpdateStandbyReturn(string vehicleId,FleetTestStandbyReturn updated)
    {
        run=run! with
        {
            StandbyReturns=run.StandbyReturns.Select(r=>r.VehicleId==vehicleId?updated:r).ToArray(),
            UpdatedAt=DateTimeOffset.UtcNow
        };
    }
    private async Task BeginStandbyReturnsAsync(CancellationToken ct)
    {
        var returns=await port.PlanStandbyReturnsAsync(run!,ct);
        if(returns.Length==0)
        {
            run=run! with
            {
                Stage="Completed",
                Message="本组任务及PLC闭合；未配置可用的唯一待机位，因此未发送车辆。",
                UpdatedAt=DateTimeOffset.UtcNow
            };
            Save();
            return;
        }
        run=run! with
        {
            Stage="ReturningStandby",
            Message="业务任务及PLC已闭合；正在返回各自独立待机位。",
            StandbyReturns=returns,
            UpdatedAt=DateTimeOffset.UtcNow
        };
        Save();
        await AdvanceStandbyReturnsAsync(ct);
    }
    private async Task AdvanceStandbyReturnsAsync(CancellationToken ct)
    {
        foreach(var assignment in run!.StandbyReturns.Where(r=>r.State==FleetTestStandbyReturnStates.Assigned).ToArray())
        {
            var sending=assignment with
            {
                State=FleetTestStandbyReturnStates.Sending,
                Message="正在核验待机路线并发送车辆。",
                UpdatedAt=DateTimeOffset.UtcNow
            };
            UpdateStandbyReturn(assignment.VehicleId,sending);Save();
            try
            {
                var sent=await port.TrySendStandbyReturnAsync(run,run.StandbyReturns,sending,ct);
                if(sent is null)
                {
                    UpdateStandbyReturn(assignment.VehicleId,assignment with
                    {
                        Message="待机路线或交通资源暂不可用，保持原位等待。",
                        UpdatedAt=DateTimeOffset.UtcNow
                    });
                }
                else
                {
                    UpdateStandbyReturn(assignment.VehicleId,sent);
                }
                Save();
            }
            catch(FleetStandbySendUncertainException e)
            {
                UpdateStandbyReturn(assignment.VehicleId,sending with
                {
                    State=FleetTestStandbyReturnStates.Uncertain,
                    Message=e.Message,
                    UpdatedAt=DateTimeOffset.UtcNow
                });
                Save();
                throw;
            }
            catch
            {
                UpdateStandbyReturn(assignment.VehicleId,assignment with
                {
                    Message="待机发送前核验失败，车辆未发送。",
                    UpdatedAt=DateTimeOffset.UtcNow
                });
                Save();
                throw;
            }
        }
        foreach(var assignment in run.StandbyReturns
            .Where(r=>r.State==FleetTestStandbyReturnStates.Returning).ToArray())
        {
            var observed=await port.ObserveStandbyReturnAsync(run,assignment,ct);
            UpdateStandbyReturn(assignment.VehicleId,observed);
            if(observed.State==FleetTestStandbyReturnStates.Completed)
                await port.ReleaseStandbyReturnAsync(observed,ct);
            Save();
        }
        if(run.StandbyReturns.All(r=>r.State==FleetTestStandbyReturnStates.Completed))
        {
            run=run with
            {
                Stage="Completed",
                Message="本组任务、PLC握手及全部车辆待机停靠均已闭合。",
                UpdatedAt=DateTimeOffset.UtcNow
            };
            Save();
            logger.LogInformation("多车测试及待机收尾完成。RunId={RunId}, Returns={Returns}",
                run.RunId,string.Join(',',run.StandbyReturns.Select(r=>r.VehicleId+"->"+r.Site+"/"+r.State)));
        }
    }
    private static void Authorize(TrafficUserContext user)
    {if(!user.Has(TrafficPermission.Administer))throw new UnauthorizedAccessException("需要管理员权限。");}
    public async Task<FleetTestStatus> ReadAsync(CancellationToken ct)
    {await mutex.WaitAsync(ct);try{return await port.ObserveAsync(run,ct);}finally{mutex.Release();}}
    public async Task<FleetTestStatus> StartAsync(TrafficUserContext user,FleetTestStart request,CancellationToken ct)
    {
        Authorize(user);await mutex.WaitAsync(ct);
        try
        {
            if(startRetirements.Any(r=>string.Equals(r.RequestId,request.RequestId,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("该原申请已核对注销，禁止迟到或重放；请使用新的申请编号。");
            if(run!=null&&string.Equals(run.Plan.RequestId,request.RequestId,StringComparison.OrdinalIgnoreCase))
            {
                if(JsonSerializer.Serialize(run.Plan)!=JsonSerializer.Serialize(request))throw new ArgumentException("同一申请不能修改参数。");
                return await port.ObserveAsync(run,ct);
            }
            if(run is {Stage:not "Completed"})throw new InvalidOperationException("原多车测试未闭合，请核对原记录。");
            if(!Guid.TryParseExact(request.RequestId,"N",out _)||!request.SiteConfirmed||
                request.Scenario is not ("Staggered" or "Simultaneous" or "Handover")||request.VehicleIds is not {Length:>=2 and <=1000}||
                request.VehicleIds.Any(string.IsNullOrWhiteSpace)||request.VehicleIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=request.VehicleIds.Length||
                request.StationIds is not {Length:>=2 and <=1000}||request.StationIds.Any(string.IsNullOrWhiteSpace)||
                request.StationIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=request.StationIds.Length)
                throw new ArgumentException("请选择至少两辆不同车辆、至少两个工位及测试用例，并确认现场。");
            if(Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(path))))
                foreach(var file in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(path))!,Path.GetFileName(path)+".*.history"))
                    if(string.Equals(JsonSerializer.Deserialize<FleetTestRun>(File.ReadAllText(file))?.Plan.RequestId,request.RequestId,StringComparison.OrdinalIgnoreCase))
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
        var executing=false;
        try
        {
            if(run==null||run.RunId!=request.RunId)throw new ArgumentException("原运行编号不符。");
            if(request.Reason?.Length>1000)throw new ArgumentException("核对依据不能超过1000字。");
            // Reject invalid requests before changing the original run or calling the PLC lab.
            if(action is not ("stop" or "resume" or "close"))throw new ArgumentException("未知操作。");
            if(run.Stage=="Completed")throw new InvalidOperationException("原测试已完成，不能再次控制该记录。");
            if(action!="stop"&&(!request.SiteConfirmed||string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Trim().Length<10))
                throw new ArgumentException("须确认现场并填写至少10字核对依据。");
            if(action=="resume"&&run.Stage!="Hold")throw new InvalidOperationException("仅暂停记录可以续接。");
            if(action=="close"&&!run.StopRequested)throw new InvalidOperationException("先停止新增呼叫，再核对结束记录。");
            executing=true;
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
                    if(run.StandbyReturns.Length>0)
                    {
                        if(run.StandbyReturns.Any(r=>r.State is FleetTestStandbyReturnStates.Sending or FleetTestStandbyReturnStates.Uncertain))
                            throw new InvalidOperationException("待机导航发送结果不确定，须先核对原车端任务，不能自动重发。");
                        run=run with {Stage="ReturningStandby",Message="现场核对后续接原待机返回："+request.Reason,UpdatedAt=DateTimeOffset.UtcNow};Save();
                    }
                    else
                    {
                        await port.LabAsync(run,"check",[],ct);
                        var pending=run.Calls.Where(c=>c.Released&&c.TaskId==null).Select(c=>c.StationId).ToArray();
                        if(pending.Length>0&&!run.StopRequested)await port.LabAsync(run,"call",pending,ct);
                        run=run with {Stage="Running",Message="现场核对后续接原任务："+request.Reason,UpdatedAt=DateTimeOffset.UtcNow};Save();
                    }
                }
                else if(action=="close")
                {
                    if(!run.StopRequested)throw new InvalidOperationException("先停止新增呼叫，再核对结束记录。");
                    await port.CheckClosedAsync(run,ct);
                    run=run with {Stage="Completed",Message="现场核对后结束测试记录："+request.Reason,UpdatedAt=DateTimeOffset.UtcNow};Save();
                }
                else throw new ArgumentException("未知操作。");
            }
            logger.LogInformation("多车测试控制完成。RunId={RunId}, Action={Action}, Operator={Operator}, Reason={Reason}, Stage={Stage}",
                run.RunId,action,user.WindowsIdentity,request.Reason,run.Stage);
            return await port.ObserveAsync(run,ct);
        }catch(Exception e){if(executing&&run is {Stage:not "Completed"})Hold("控制结果待核对："+e.Message);throw;}
        finally{mutex.Release();}
    }
    public async Task TickAsync(CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            if(run is null||run.Stage=="Completed")return;
            // Current vehicle health is returned separately by ObserveAsync. Never overwrite
            // the latched trigger: a recovered sample does not explain why the run stopped.
            if(run.Stage=="Hold")return;
            if(run.Stage=="ReturningStandby")
            {
                try
                {
                    await AdvanceStandbyReturnsAsync(ct);
                }
                catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                catch(Exception e)
                {
                    Hold("保留原任务和待机发送记录待核对："+e.Message);
                }
                return;
            }
            if(run.Stage!="Running")return;
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
                    if(returnToStandby)
                    {
                        await BeginStandbyReturnsAsync(ct);
                    }
                    else
                    {
                        run=run with {Stage="Completed",Message="本组任务及PLC闭合；自动回待机已关闭。",UpdatedAt=DateTimeOffset.UtcNow};Save();
                    }
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
