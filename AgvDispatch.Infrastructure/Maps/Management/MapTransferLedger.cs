using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Maps;
using SqlSugar;
namespace AgvDispatch.Infrastructure.Persistence;

/// <summary>No sender dependency: preparation and observation cannot transmit maps.</summary>
public sealed class MapTransferLedger(ISqlSugarClient db,IMapExecutionGate gate,IMapSwitchService switches,
    IAgvStateCache vehicles,IActiveMapRuntime runtime,IRobokitInspectionService inspector):IMapTransferLedger
{
    private static DateTimeOffset Utc(DateTime value)=>new(DateTime.SpecifyKind(value,DateTimeKind.Utc));
    private static void PageBudget(int page,int size){if(page<1||page>10000||size<1||size>100)throw new ArgumentException("分页超出范围（页码1～10000，每页1～100）。");}
    public async Task<MapTransferPage> ListAsync(TrafficUserContext user,MapTransferListQuery query,CancellationToken ct)
    {
        Demand(user,TrafficPermission.View);PageBudget(query.Page,query.PageSize);
        foreach(var value in new[]{query.VehicleId,query.MapId,query.State})if(value is not null&&(string.IsNullOrWhiteSpace(value)||value.Length>64))throw new ArgumentException("筛选编号或状态无效。");
        if(query.State is not null && query.State is not ("ReadOnlyPrepared" or "Blocked" or "ReviewRequired" or "Sending" or "Uncertain" or "ClosedAfterReview"))throw new ArgumentException("未知地图传输状态。");
        if(query.From is not null && query.To is not null && query.From>query.To)throw new ArgumentException("时间范围倒置。");
        await InitializeAsync(ct);using var control=await gate.EnterControlAsync(ct);
        var rows=db.Queryable<MapTransferEntity>()
            .WhereIF(query.VehicleId is not null,x=>x.VehicleId==query.VehicleId)
            .WhereIF(query.MapId is not null,x=>x.MapId==query.MapId)
            .WhereIF(query.State is not null,x=>x.State==query.State);
        if(query.From is { } from)rows=rows.Where(x=>x.CreatedAt>=from.UtcDateTime);
        if(query.To is { } to)rows=rows.Where(x=>x.CreatedAt<=to.UtcDateTime);
        var total=await rows.CountAsync(ct);
        var page=await rows.OrderBy(x=>x.CreatedAt,OrderByType.Desc).OrderBy(x=>x.OperationId,OrderByType.Desc)
            .Skip((query.Page-1)*query.PageSize).Take(query.PageSize).ToListAsync(ct);
        return new(page.Select(x=>new MapTransferRecord(x.OperationId,x.RequestId,x.MapId,x.VersionId,x.VehicleId,x.TargetMapName,x.State,x.AttemptCount,Utc(x.CreatedAt),Utc(x.UpdatedAt))).ToArray(),total,query.Page,query.PageSize);
    }
    public async Task<MapTransferHistoryPage> HistoryAsync(TrafficUserContext user,MapTransferHistoryQuery query,CancellationToken ct)
    {
        Demand(user,TrafficPermission.View);PageBudget(query.Page,query.PageSize);await InitializeAsync(ct);
        using var control=await gate.EnterControlAsync(ct);await Row(query.OperationId,ct);
        var rows=db.Queryable<MapTransferEventEntity>().Where(x=>x.OperationId==query.OperationId);
        var total=await rows.CountAsync(ct);
        var page=await rows.OrderBy(x=>x.OccurredAt,OrderByType.Desc).OrderBy(x=>x.Id,OrderByType.Desc)
            .Skip((query.Page-1)*query.PageSize).Take(query.PageSize).ToListAsync(ct);
        return new(query.OperationId,page.Select(x=>new MapTransferEventSnapshot(x.Kind,x.Operator,x.Evidence,Utc(x.OccurredAt))).ToArray(),total,query.Page,query.PageSize);
    }
    public async Task<MapTransferReviewPlan> ReviewAsync(TrafficUserContext user,MapTransferQuery query,CancellationToken ct)
    {
        var record=await ReadAsync(user,query,ct);
        return new(record,record.AttemptCount>0||record.State is "Sending" or "Uncertain",
            ["原操作编号、车辆身份、源SHA256、原地图/版本及目标地图身份。",
             "厂家确认的当前固件、上传/载入与任务历史规则、车端MD5算法及其依据。",
             "与原操作关联的厂家应答或完整发送记录；匹配地图可能原本存在，不能替代本次写入证明。",
             "本车最新稳定只读版本/地图/MD5/载入状态及现场人员对车辆停止、定位与坐标系的核对。",
             "后台当前任务、剩余路权、未闭环命令及地图切换门禁的重新核验。",
             "后续独立处置请求须绑定原操作/证据摘要、幂等编号与操作人，事务留档并重核；本批未提供该执行入口。"],
            ["自动补发或建立新操作绕过原不确定结果。","仅凭MD5匹配或口头确认关闭不确定状态。","自动删除原任务/路权、确认地图绑定或放开执行门。"]);
    }
    private readonly string session=Guid.NewGuid().ToString("N");private bool initialized;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new JsonStringEnumConverter()}};
    private static string Hash(string s)=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));
    private static void Demand(TrafficUserContext user,TrafficPermission permission){if(!user.Has(permission))throw new UnauthorizedAccessException("地图传输记录权限不足。");}
    private async Task Transaction(Func<Task> action)
    {
        var result=await db.Ado.UseTranAsync(action);if(!result.IsSuccess)throw new InvalidOperationException("地图传输记录事务失败。",result.ErrorException);
    }
    private Task Event(MapTransferEntity row,string kind,string actor,object evidence,CancellationToken ct)=>db.Insertable(new MapTransferEventEntity{Id=Guid.NewGuid().ToString("N"),OperationId=row.OperationId,Kind=kind,SessionId=session,Operator=actor,Evidence=JsonSerializer.Serialize(evidence,Json),OccurredAt=DateTime.UtcNow}).ExecuteCommandAsync(ct);
    public async Task InitializeAsync(CancellationToken ct)
    {
        using var control=await gate.EnterControlAsync(ct);if(initialized)return;
        var rows=await db.Queryable<MapTransferEntity>().Where(x=>x.OwnerSession!=session && x.State!="ClosedAfterReview").ToListAsync(ct);
        await Transaction(async()=>
        {
            foreach(var r in rows)
            {
                var state=r.AttemptCount>0||r.State=="Sending"?"Uncertain":r.State=="ReadOnlyPrepared"?"ReviewRequired":r.State;
                await db.Updateable<MapTransferEntity>().SetColumns(x=>new MapTransferEntity{State=state,UpdatedAt=DateTime.UtcNow}).Where(x=>x.OperationId==r.OperationId).ExecuteCommandAsync(ct);
                await Event(r,"ServiceRestart","service",new{PreviousState=r.State,RecoveredState=state,NoResend=true},ct);
            }
        });
        if(rows.Any(x=>x.AttemptCount>0||x.State=="Sending"))gate.SetMode(MapExecutionMode.Held);
        initialized=true;
    }
    public async Task<MapTransferSnapshot> PrepareAsync(TrafficUserContext user,PrepareMapTransferRequest request,CancellationToken ct)
    {
        Demand(user,TrafficPermission.ManageMap);
        foreach(var s in new[]{request.RequestId,request.MapId,request.VersionId,request.VehicleId})if(string.IsNullOrWhiteSpace(s)||s.Length>64)throw new ArgumentException("地图传输编号为空或超过预算。");
        if(!Regex.IsMatch(request.ExpectedDeviceMd5??"",@"^[0-9a-fA-F]{32}$")||string.IsNullOrWhiteSpace(request.ConfirmedRobokitVersion)||request.ConfirmedRobokitVersion.Length>128||string.IsNullOrWhiteSpace(request.ConfirmationNote)||request.ConfirmationNote.Length>1500)throw new ArgumentException("需要明确的厂家MD5/固件与核对依据，不能推导源文件MD5。");
        await InitializeAsync(ct);using var control=await gate.EnterControlAsync(ct);
        var fingerprint=Hash(JsonSerializer.Serialize(new{request,user.WindowsIdentity},Json));
        var existing=await db.Queryable<MapTransferEntity>().Where(x=>x.RequestId==request.RequestId).FirstAsync(ct);
        if(existing is not null){if(existing.IntentHash!=fingerprint)throw new InvalidOperationException("请求编号已用于不同的地图/车辆/操作人或参数。");return await Snapshot(existing,ct);}
        var version=await db.Queryable<MapVersionEntity>().Where(x=>x.MapId==request.MapId&&x.VersionId==request.VersionId).FirstAsync(ct)??throw new InvalidOperationException("后台目标地图版本不存在。");
        var definition=JsonSerializer.Deserialize<MapDefinition>(version.DefinitionJson,Json)??throw new InvalidDataException("地图定义无效。");
        var row=new MapTransferEntity{OperationId=Guid.NewGuid().ToString("N"),RequestId=request.RequestId,IntentHash=fingerprint,MapId=request.MapId,VersionId=request.VersionId,VehicleId=request.VehicleId,SourcePath=definition.Source,SourceSha256="",DefinitionHash=Hash(version.DefinitionJson),ExpectedDeviceMd5=request.ExpectedDeviceMd5!,Firmware=request.ConfirmedRobokitVersion,ConfirmationNote=request.ConfirmationNote,Load=request.Load,OwnerSession=session,Operator=user.WindowsIdentity,CreatedAt=DateTime.UtcNow,UpdatedAt=DateTime.UtcNow};
        var sourceErrors=new List<string>();
        var active=runtime.Current;row.ActiveMapId=active?.MapId??"";row.ActiveVersionId=active?.VersionId??"";row.ActiveDefinitionHash=active is null?"":Hash(JsonSerializer.Serialize(active.Definition,Json));
        try
        {
            var bytes=await Source(row.SourcePath,ct);var parsed=new Smap2DImportService().Read(bytes,row.SourcePath);row.SourceSha256=parsed.Sha256;row.TargetMapName=parsed.OriginalMapName;
            if(parsed.Warnings.Any(w=>w.Contains("未转换")||w.Contains("未知")))sourceErrors.Add("源图有未核定几何/扩展，仅保留待核对记录。");
        }
        catch(Exception ex)when(ex is not OperationCanceledException){sourceErrors.Add("源图未核定："+ex.Message);}
        var blockers=await Blockers(row,version,definition,ct);blockers.AddRange(sourceErrors);row.State=blockers.Count==0?"ReadOnlyPrepared":"Blocked";
        await Transaction(async()=>{await db.Insertable(row).ExecuteCommandAsync(ct);await Event(row,"Prepared",user.WindowsIdentity,new{blockers,WritesEnabled=false},ct);});
        return await Snapshot(row,ct);
    }
    private static async Task<byte[]> Source(string path,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(path)||!string.Equals(Path.GetExtension(path),".smap",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("后台版本未引用保留的原生smap。");
        if(new FileInfo(path).Length>64*1024*1024)throw new InvalidDataException("源图超过64MB。");return await File.ReadAllBytesAsync(path,ct);
    }
    private async Task<List<string>> Blockers(MapTransferEntity row,MapVersionEntity? version,MapDefinition? definition,CancellationToken ct)
    {
        var result=new List<string>();
        var active=runtime.Current;var pointers=await db.Queryable<MapProjectEntity>().Where(x=>x.IsActive).ToListAsync(ct);
        if(active is null||pointers.Count!=1||pointers[0].MapId!=active.MapId||pointers[0].ActiveVersionId!=active.VersionId||active.MapId!=row.ActiveMapId||active.VersionId!=row.ActiveVersionId||Hash(JsonSerializer.Serialize(active.Definition,Json))!=row.ActiveDefinitionHash)result.Add("当前运行地图/数据库指针或原活动定义指纹变化。");
        if(gate.Mode!=MapExecutionMode.Running)result.Add("地图执行门处于"+gate.Mode);
        if(version is null||Hash(version.DefinitionJson)!=row.DefinitionHash||definition is null)result.Add("后台目标地图版本/定义已变化。");
        var project=await db.Queryable<MapProjectEntity>().Where(x=>x.MapId==row.MapId).FirstAsync(ct);
        if(project?.PublishedVersionId!=row.VersionId||version?.Status!=(int)MapVersionStatus.Published)result.Add("目标不是后台当前发布版本。");
        if(await db.Queryable<MapSwitchEntity>().Where(x=>x.Phase<90).AnyAsync(ct))result.Add("有未结束的地图切换。");
        if(await db.Queryable<DispatchTaskEntity>().Where(x=>x.Stage!=90&&x.Stage!=110).AnyAsync(ct))result.Add("存在未结束/异常任务（包含等待创建及已分配）。");
        if(definition is not null)result.AddRange(await switches.GetBlockersAsync(definition,ct));
        if(!vehicles.TryGet(row.VehicleId,out var vehicle))result.Add("目标车辆状态缺失。");
        else
        {
            var age=vehicle.LastSuccessfulPollAt is { } poll?DateTimeOffset.UtcNow-poll:TimeSpan.MaxValue;
            if(vehicle.Connection!=ConnectionState.Online||vehicle.Quality!=SignalQuality.Good||age<TimeSpan.Zero||age>TimeSpan.FromSeconds(5))result.Add("目标车辆离线/质量不良/过期/时钟异常。");
            if(vehicle.Blocked!=false||vehicle.EmergencyStopped!=false||vehicle.VelocityX is null||vehicle.VelocityY is null||vehicle.AngularVelocity is null||!double.IsFinite(vehicle.VelocityX.Value)||!double.IsFinite(vehicle.VelocityY.Value)||!double.IsFinite(vehicle.AngularVelocity.Value)||Math.Abs(vehicle.VelocityX.Value)>.01||Math.Abs(vehicle.VelocityY.Value)>.01||Math.Abs(vehicle.AngularVelocity.Value)>.01)result.Add("目标车辆未确认停止或安全状态缺失。");
            if(vehicle.LocalizationConfidence is null||!double.IsFinite(vehicle.LocalizationConfidence.Value)||vehicle.LocalizationConfidence.Value<.5||vehicle.Tasks.Any(t=>t.Status is not (4 or 5 or 6 or 7)))result.Add("目标车辆定位/任务状态未核定。");
        }
        try{if(Convert.ToHexString(SHA256.HashData(await Source(row.SourcePath,ct)))!=row.SourceSha256||row.SourceSha256=="")result.Add("原源文件指纹变化或缺少原指纹。");}
        catch(Exception ex)when(ex is not OperationCanceledException){result.Add("源文件不可核验："+ex.Message);}
        return result.Distinct().ToList();
    }
    private async Task<MapTransferEntity> Row(string id,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(id)||id.Length>64)throw new ArgumentException("地图传输记录编号无效。");
        return await db.Queryable<MapTransferEntity>().Where(x=>x.OperationId==id).FirstAsync(ct)??throw new KeyNotFoundException("地图传输记录不存在。");
    }
    private async Task<MapTransferSnapshot> Snapshot(MapTransferEntity row,CancellationToken ct)
    {
        var version=await db.Queryable<MapVersionEntity>().Where(x=>x.MapId==row.MapId&&x.VersionId==row.VersionId).FirstAsync(ct);
        var definition=version is null?null:JsonSerializer.Deserialize<MapDefinition>(version.DefinitionJson,Json);var blockers=await Blockers(row,version,definition,ct);
        if(row.State!="ReadOnlyPrepared")blockers.Add("本记录不是当前有效准备状态；不构成发送授权。");
        if(row.AttemptCount>0||row.State is "Sending" or "Uncertain")blockers.Add("写入结果不确定，观察不能代替人工处置；不允许补发或自动解除屏障。");
        var events=await db.Queryable<MapTransferEventEntity>().Where(x=>x.OperationId==row.OperationId).OrderBy(x=>x.OccurredAt,OrderByType.Desc).OrderBy(x=>x.Id,OrderByType.Desc).Take(100).ToListAsync(ct);
        return new(row.OperationId,row.RequestId,row.MapId,row.VersionId,row.VehicleId,row.TargetMapName,row.SourceSha256,row.ExpectedDeviceMd5,row.State,row.AttemptCount,row.OwnerSession!=session,blockers.Count==0,blockers,row.LastObservation,new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt,DateTimeKind.Utc)),new DateTimeOffset(DateTime.SpecifyKind(row.UpdatedAt,DateTimeKind.Utc))){Events=events.Select(e=>new MapTransferEventSnapshot(e.Kind,e.Operator,e.Evidence,new DateTimeOffset(DateTime.SpecifyKind(e.OccurredAt,DateTimeKind.Utc)))).ToArray()};
    }
    public async Task<MapTransferSnapshot> ReadAsync(TrafficUserContext user,MapTransferQuery query,CancellationToken ct)
    {
        Demand(user,TrafficPermission.View);await InitializeAsync(ct);using var control=await gate.EnterControlAsync(ct);return await Snapshot(await Row(query.OperationId,ct),ct);
    }
    public async Task<MapTransferSnapshot> ObserveAsync(TrafficUserContext user,MapTransferQuery query,CancellationToken ct)
    {
        Demand(user,TrafficPermission.View);await InitializeAsync(ct);MapTransferEntity pinned;
        using(var control=await gate.EnterControlAsync(ct))pinned=await Row(query.OperationId,ct);
        object observation;
        try
        {
            using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(15));
            var value=await inspector.ReadAsync(new(pinned.VehicleId),budget.Token).WaitAsync(TimeSpan.FromSeconds(15),budget.Token);
            var fresh=DateTimeOffset.UtcNow-value.CheckedAt;
            observation=new{value.CheckedAt,value.Available,value.Stable,value.Reason,value.RobokitVersion,value.RobotMap,value.RobotMapMd5,value.LoadMapStatus,DesiredMapObserved=value.VehicleId==pinned.VehicleId&&fresh>=TimeSpan.Zero&&fresh<=TimeSpan.FromSeconds(5)&&value.Available&&value.Stable&&value.LoadMapStatus==1&&value.RobokitVersion==pinned.Firmware&&value.RobotMap==pinned.TargetMapName&&value.RobotMapMd5==pinned.ExpectedDeviceMd5,ProofOfThisWrite=false};
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){observation=new{Available=false,DesiredMapObserved=false,Reason=ex.Message,ProofOfThisWrite=false};}
        using var final=await gate.EnterControlAsync(ct);var row=await Row(pinned.OperationId,ct);
        var before=await Snapshot(row,ct);var json=JsonSerializer.Serialize(new{Observation=observation,CurrentBlockers=before.Blockers,WritesEnabled=false,MayResend=false},Json);
        await Transaction(async()=>
        {
            await db.Updateable<MapTransferEntity>().SetColumns(x=>new MapTransferEntity{LastObservation=json,UpdatedAt=DateTime.UtcNow}).Where(x=>x.OperationId==row.OperationId).ExecuteCommandAsync(ct);
            await Event(row,"ReadOnlyObservation",user.WindowsIdentity,new{observation,before.Blockers,MayResend=false},ct);
        });
        return await Snapshot(await Row(row.OperationId,ct),ct);
    }
}
