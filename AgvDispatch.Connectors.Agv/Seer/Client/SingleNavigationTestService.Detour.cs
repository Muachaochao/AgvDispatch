using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Connectors.Agv;

public sealed partial class SingleNavigationTestService
{
    private static IReadOnlyList<string> CurrentVendorIds(NavigationTestRecord record) =>
        record.VendorTaskIds.Count>0?record.VendorTaskIds:
        record.NavigationApi==3066&&record.Route.Count>=2?AgvTransportSegmentIds.For(record.VendorTaskId,record.Route.Count):[record.VendorTaskId];

    private void SynchronizeSentDetourStatus()
    {
        if(_record?.Detour is not {SendAttemptedAt:not null} detour)return;
        var state=Terminal(_record.State)||_record.State is "Uncertain" or "CancelRequested"?_record.State:"Executing";
        _record=_record with {Detour=detour with {State=state,Message=_record.Message}};
        if(_record.AutoDetour is {} auto)
            _record=_record with {AutoDetour=auto with {State=state,Message=_record.Message,UpdatedAt=_record.UpdatedAt.ToUniversalTime()}};
    }

    private static bool LocalBlocked(AgvRobotTelemetry t) => t.Blocked==true||t.Alarms.Any(AgvObstaclePolicy.IsLocalObstacle);
    private static bool SafeStopped(AgvRobotTelemetry t) => t.EmergencyStopped==false&&t.Charging==false&&
        (t.Blocked is not null||t.Alarms.Any(AgvObstaclePolicy.IsLocalObstacle))&&
        t.LocalizationConfidence is >=0.5&&t.VelocityX is {} vx&&Math.Abs(vx)<0.01&&
        t.VelocityY is {} vy&&Math.Abs(vy)<0.01&&t.AngularVelocity is {} w&&Math.Abs(w)<0.01&&
        !t.Alarms.Any(a=>a.Severity>=AgvAlarmSeverity.Error&&!AgvObstaclePolicy.IsLocalObstacle(a));

    private void ObservePosition(AgvRobotTelemetry t)
    {
        var previous=_record!;
        var continuous=previous.ObservedAt is {} last&&t.ReadAt>last&&t.ReadAt-last<=TimeSpan.FromSeconds(10);
        _record=previous with {CurrentStation=t.CurrentStation,X=t.X,Y=t.Y,ObservedAt=t.ReadAt,
            BlockedSince=LocalBlocked(t)?continuous?previous.BlockedSince??t.ReadAt:t.ReadAt:null};
    }

    private static void RequireKnownOriginal(NavigationTestRecord record)
    {
        if(record.NavigationApi==3066&&record.Route.Count<2)
            throw new InvalidOperationException("历史3066测试缺少完整任务链，不能核定原取消范围；请现场人工核对。");
    }

    private static void RequireNoForeignTasks(AgvRobotTelemetry t,IReadOnlyList<string> own)
    {
        if(t.Tasks.Any(x=>x.Status is <1 or >7||x.Status is 1 or 2 or 3&&!own.Contains(x.TaskId,StringComparer.Ordinal)))
            throw new InvalidOperationException("车端存在其他活动任务或未知状态，拒绝替换当前导航。");
    }

    private async Task<(AgvRobotTelemetry Telemetry,NavigationTestStation[] Stations)> ObserveDetourSiteAsync(
        SeerRobotClient client,NavigationTestRecord record,CancellationToken ct)
    {
        await RequireJackIdleForNavigationAsync(client,ct);
        await RequireForkIdleForNavigationAsync(client,ct);
        using var before=await client.RequestAsync(1300,null,ct);
        if(MapIdentity(before.RootElement)!=(record.RobotMap,record.RobotMapMd5))
            throw new InvalidOperationException("车端地图身份与原测试不同，请人工核对；不能跨地图改道。");
        using var mapLoad=await client.RequestAsync(1022,null,ct);
        using var owner=await client.RequestAsync(1060,null,ct);
        if(Int(mapLoad.RootElement,"loadmap_status")!=1||!owner.RootElement.TryGetProperty("locked",out var locked)||locked.ValueKind!=System.Text.Json.JsonValueKind.False)
            throw new InvalidOperationException("地图未加载成功或控制权被占用/未知，不能改道。");
        using var stationResponse=await client.RequestAsync(1301,null,ct);
        var stations=ParseStations(stationResponse.RootElement);
        var (telemetry,_)=await client.PollAsync(ct);
        ObservePosition(telemetry);
        using var after=await client.RequestAsync(1300,null,ct);
        using var afterLoad=await client.RequestAsync(1022,null,ct);
        using var afterOwner=await client.RequestAsync(1060,null,ct);
        if(MapIdentity(after.RootElement)!=(record.RobotMap,record.RobotMapMd5)||Int(afterLoad.RootElement,"loadmap_status")!=1||
            !afterOwner.RootElement.TryGetProperty("locked",out var afterLocked)||afterLocked.ValueKind!=System.Text.Json.JsonValueKind.False)
            throw new InvalidOperationException("核验期间地图或控制权变化，请重新核对。");
        if(!SafeStopped(telemetry))throw new InvalidOperationException("车辆未确认停稳、定位不足、急停、充电或严重报警；不能改道。");
        return (telemetry,stations);
    }

    private static NavigationTestStation RequireActualSite(AgvRobotTelemetry t,IReadOnlyList<NavigationTestStation> stations)
    {
        var near=stations.Where(s=>AtStation(t,s.X,s.Y)).ToArray();
        if(near.Length!=1||near[0].Id!=t.CurrentStation)
            throw new InvalidOperationException("车辆已离站或当前站点与实际坐标不唯一匹配，无法可靠接入指定路径；等待人工处理，不能冒用原起点或SELF_POSITION。");
        return near[0];
    }

    private static void RequirePlan(NavigationTestDetourPlan plan,NavigationTestRecord record,string source,
        IReadOnlyList<NavigationTestStation> stations)
    {
        if(!plan.Available)throw new InvalidOperationException(plan.Message);
        if(plan.RobotMap!=record.RobotMap||plan.RobotMapMd5!=record.RobotMapMd5||plan.SourceSite!=source||plan.TargetSite!=record.TargetSite||
            plan.Route.Count is <2 or >2048||plan.Route[0]!=source||plan.Route[^1]!=record.TargetSite||
            plan.Route.Any(x=>!ValidSite(x)||x=="SELF_POSITION"||!stations.Any(s=>s.Id==x))||
            plan.Route.Distinct(StringComparer.Ordinal).Count()!=plan.Route.Count||
            string.IsNullOrWhiteSpace(plan.MapId)||string.IsNullOrWhiteSpace(plan.VersionId)||string.IsNullOrWhiteSpace(plan.DefinitionHash))
            throw new InvalidOperationException("改道候选与实际起点、原终点、地图身份或明确站点链不一致。");
        if(plan.Route.SequenceEqual(record.Route,StringComparer.Ordinal))
            throw new InvalidOperationException("候选与原路线相同，没有避开封锁路段；保持等待。");
    }

    public async Task<NavigationTestDetourPreview> PreviewDetourAsync(TrafficUserContext user,NavigationTestDetourQuery request,CancellationToken ct)
    {
        Authorize(user);await _mutex.WaitAsync(ct);
        try
        {
            _detourPreview=null;
            if(_detourPlanner is null)throw new InvalidOperationException("尚未配置与车端地图绑定的封路规划器，保持原任务等待。");
            var record=_record;
            if(record is null||record.TaskId!=request.TaskId||Terminal(record.State)||record.Detour is {SendAttemptedAt:not null})
                throw new InvalidOperationException("没有可替换的原测试，或改道已尝试发送；请核对现有任务。");
            RequireKnownOriginal(record);
            using var lease=await _execution.EnterControlAsync(ct);
            RequireCommissioningMapRunning();
            var client=await GuardAsync(record.VehicleId,ct);
            var (telemetry,stations)=await ObserveDetourSiteAsync(client,record,ct);
            RequireNoForeignTasks(telemetry,CurrentVendorIds(record));
            var source=RequireActualSite(telemetry,stations);
            var refreshing=record.Detour is not null;
            if(!refreshing&&(!LocalBlocked(telemetry)||_record!.BlockedSince is not {} since||telemetry.ReadAt-since<TimeSpan.FromSeconds(Math.Max(1,_settings.DetourWaitSeconds))))
            {Persist();throw new InvalidOperationException($"尚未连续受阻{Math.Max(1,_settings.DetourWaitSeconds)}秒，请先等待并查询原任务；障碍消失后继续原路线。");}
            var originalIds=record.Detour?.OriginalVendorTaskIds??CurrentVendorIds(record);
            var original=await client.QueryTasksAsync(originalIds,ct);
            if(original.Count!=originalIds.Count||original.Any(x=>x.Status is <1 or >7)||
                (refreshing?original.Any(x=>x.Status is not (4 or 5 or 6 or 7)):!original.Any(x=>x.Status is 1 or 2 or 3)))
                throw new InvalidOperationException(refreshing?"刷新候选必须先核对原所有厂家任务均已终态；缺失或仍活动时保持只读等待。":"原任务链缺失、未知或已全部终态，不能启动受控替换。");
            var sourceIndex=record.Route.Select((site,index)=>(site,index))
                .FirstOrDefault(row=>string.Equals(row.site,source.Id,StringComparison.Ordinal));
            var suggested=sourceIndex.site is not null&&sourceIndex.index+1<record.Route.Count
                ? new[]{new NavigationTestSitePair(source.Id,record.Route[sourceIndex.index+1])}
                : [];
            var plan=await _detourPlanner.PreviewAsync(new(record.TaskId,record.VehicleId,source.Id,record.TargetSite,
                record.RobotMap,record.RobotMapMd5,stations,record.Route){SuggestedBlockedPairs=suggested},ct);
            if(plan.Available)RequirePlan(plan,record,source.Id,stations);
            Persist();
            // A two-minute review window is practical for checking the physical route.
            // Confirmation still re-reads every safety, task, map and closure condition.
            return _detourPreview=new(Guid.NewGuid().ToString("N"),AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow().AddMinutes(2),record.TaskId,record.VehicleId,_record?.BlockedSince,plan);
        }
        finally{_mutex.Release();}
    }

    public async Task<NavigationTestRecord> ConfirmDetourAsync(TrafficUserContext user,NavigationTestDetourConfirm request,CancellationToken ct)
    {
        Authorize(user);await _mutex.WaitAsync(ct);
        try
        {
            var preview=_detourPreview;_detourPreview=null;
            if(!request.CancelOriginalConfirmed||preview is null||preview.PreviewId!=request.PreviewId||preview.TaskId!=request.TaskId||preview.ExpiresAt<AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow())
                throw new InvalidOperationException("候选已失效或未确认取消原导航，请重新只读预览。");
            var record=_record;
            if(record is null||record.TaskId!=request.TaskId||Terminal(record.State)||record.Detour is {SendAttemptedAt:not null}||_detourPlanner is null)
                throw new InvalidOperationException("原测试已变化，不能使用旧候选。");
            using var lease=await _execution.EnterControlAsync(ct);
            RequireCommissioningMapRunning();
            var client=await GuardAsync(record.VehicleId,ct);
            var (t,stations)=await ObserveDetourSiteAsync(client,record,ct);
            var ids=record.Detour?.OriginalVendorTaskIds??CurrentVendorIds(record);
            RequireNoForeignTasks(t,ids);
            RequirePlan(preview.Plan,record,RequireActualSite(t,stations).Id,stations);
            if(record.Detour is null&&!LocalBlocked(t))throw new InvalidOperationException("阻挡已解除，保留原任务继续运行，无需取消改道。");
            await _detourPlanner.ValidateAsync(preview.Plan,ct);
            var rows=await client.QueryTasksAsync(ids,ct);
            if(rows.Count!=ids.Count||rows.Any(x=>x.Status is <1 or >7))throw new InvalidOperationException("原所有厂家任务未全部查到，禁止取消与改道。");
            if(record.Detour is {} previous)
            {
                if(rows.Any(x=>x.Status is not (4 or 5 or 6 or 7)))
                    throw new InvalidOperationException("原任务尚未全部终态，不能刷新候选或重复取消；保持只读核对。");
                var refreshed=previous with {PreviewId=preview.PreviewId,Plan=preview.Plan,State="AwaitingOriginalTerminal",
                    Message="已重新人工确认候选；原任务已终态，不重复取消，等待连续停稳后再确认执行。",StoppedObservedAt=null,
                    ConfirmedAt=AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(),Operator=user.WindowsIdentity};
                _record=_record! with {State="DetourCancelRequested",Detour=refreshed,Message=refreshed.Message,UpdatedAt=AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()};
                Persist();return _record;
            }
            var target=ids.Select(id=>rows.Single(x=>x.TaskId==id)).FirstOrDefault(x=>x.Status is 1 or 2 or 3);
            if(target is null)throw new InvalidOperationException("原任务已经结束，请重新查询。");
            var intent=new NavigationTestDetourRecord(preview.PreviewId,"CancelIntent","已持久化精确取消意图；不代表原任务终态。",preview.Plan,
                ids.ToArray(),record.Route.ToArray(),record.SourceSite,record.NavigationApi,AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(),user.WindowsIdentity)
                {CancelTargetId=target.TaskId,CancelAttemptedAt=AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow()};
            _record=_record! with {State="DetourCancelRequested",Detour=intent,Message=intent.Message,UpdatedAt=AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()};
            Persist();
            try
            {
                await client.CancelNavigationAsync(target.TaskId,false,ct);
                _record=_record with {Detour=intent with {State="CancelRequested",Message="精确取消已应答；等待原所有厂家任务终态及连续停稳，再由人工确认执行候选。"}};
            }
            catch(Exception ex){_record=_record with {Detour=intent with {State="AwaitingOriginalTerminal",Message="取消结果不确定，只查询原所有任务，不重发取消或导航："+ex.Message}};}
            _record=_record with {Message=_record.Detour!.Message};Persist();return _record;
        }
        finally{_mutex.Release();}
    }

    private async Task ObserveDetourCancellationAsync(SeerRobotClient client,CancellationToken ct)
    {
        var record=_record!;var detour=record.Detour!;
        var rows=await client.QueryTasksAsync(detour.OriginalVendorTaskIds,ct);
        var (t,stations)=await ObserveDetourSiteAsync(client,record,ct);
        var allTerminal=rows.Count==detour.OriginalVendorTaskIds.Count&&rows.All(x=>x.Status is 4 or 5 or 6 or 7);
        RequireNoForeignTasks(t,detour.OriginalVendorTaskIds);
        var atSource=RequireActualSite(t,stations).Id==detour.Plan.SourceSite;
        var stopped=allTerminal&&atSource;
        var continuous=record.ObservedAt is {} previous&&t.ReadAt>previous&&t.ReadAt-previous<=TimeSpan.FromSeconds(10);
        var first=stopped?continuous?detour.StoppedObservedAt??t.ReadAt:t.ReadAt:(DateTimeOffset?)null;
        var ready=stopped&&first is {} observed&&t.ReadAt-observed>=TimeSpan.FromMilliseconds(500);
        var held=detour.State=="ReviewHold";
        var state=held?"ReviewHold":ready?"Ready":"AwaitingOriginalTerminal";
        var message=held?"后台重启后的改道保持人工复核；只读核对原厂家任务，不自动续发。":
            ready?"原所有厂家任务均已终态，车辆在候选起点连续停稳；请再次确认执行候选。":
            !allTerminal?"原厂家任务尚未全部确认终态或历史缺失；保持等待，不发送新导航。":
            !atSource?"车辆不在候选起点，等待人工核对，不能从旧起点发车。":"等待第二次停稳与定位核验。";
        _record=_record! with {State=held?"ReviewHold":ready?"DetourReady":"DetourCancelRequested",Message=message,
            Detour=detour with {State=state,Message=message,StoppedObservedAt=first},UpdatedAt=AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()};Persist();
    }

    public async Task<NavigationTestRecord> ContinueDetourAsync(TrafficUserContext user,NavigationTestDetourContinue request,CancellationToken ct)
    {
        Authorize(user);await _mutex.WaitAsync(ct);
        try
        {
            var record=_record;var detour=record?.Detour;
            if(!request.ExecuteDetourConfirmed||record is null||record.TaskId!=request.TaskId||detour is null||detour.PreviewId!=request.PreviewId||
                detour.State!="Ready"||detour.SendAttemptedAt is not null||_detourPlanner is null)
                throw new InvalidOperationException("改道尚未就绪、未明确确认或已经尝试发送；不能重复发送。");
            using var lease=await _execution.EnterControlAsync(ct);
            RequireCommissioningMapRunning();
            var client=await GuardAsync(record.VehicleId,ct);
            await ObserveDetourCancellationAsync(client,ct);
            if(_record!.Detour!.State!="Ready")throw new InvalidOperationException(_record.Message);
            var (t,stations)=await ObserveDetourSiteAsync(client,record,ct);
            RequirePlan(detour.Plan,record,RequireActualSite(t,stations).Id,stations);
            using var tasks=await client.RequestAsync(1110,new{},ct);RequireIdleTaskList(tasks.RootElement);
            // 1303 is queried for each edge only as a read-only direct-connectivity check. Never send
            // 3051 here: it could silently choose a different route through the closed corridor.
            for(var i=1;i<detour.Plan.Route.Count;i++)
            {
                var query=new NavigationTestQuery(record.VehicleId,detour.Plan.Route[i-1],detour.Plan.Route[i]);
                using var edge=await client.RequestAsync(1303,new{source_id=query.SourceSite,target_id=query.TargetSite},ct);
                if(!ParseRoute(edge.RootElement,query,stations).SequenceEqual(new[]{query.SourceSite,query.TargetSite},StringComparer.Ordinal))
                    throw new InvalidOperationException("候选含车端未确认的直接线路；不能跳点发送3066。");
            }
            using var sendGate=await _detourPlanner.EnterSendAsync(detour.Plan,ct);
            var (finalTelemetry,finalStations)=await ObserveDetourSiteAsync(client,record,ct);
            if(RequireActualSite(finalTelemetry,finalStations).Id!=detour.Plan.SourceSite)
                throw new InvalidOperationException("发送前车辆已离开候选起点，拒绝改道。");
            using var finalTasks=await client.RequestAsync(1110,new{},ct);RequireIdleTaskList(finalTasks.RootElement);
            var finalRows=await client.QueryTasksAsync(detour.OriginalVendorTaskIds,ct);
            if(finalRows.Count!=detour.OriginalVendorTaskIds.Count||finalRows.Any(x=>x.Status is not (4 or 5 or 6 or 7)))
                throw new InvalidOperationException("发送前原任务链终态证据变化，保持等待。");
            var vendor="NAVDETOUR-"+Guid.NewGuid().ToString("N");
            var ids=AgvTransportSegmentIds.For(vendor,detour.Plan.Route.Count);
            var newRows=await client.QueryTasksAsync(ids,ct);
            if(newRows.Any(x=>x.Status!=404))throw new InvalidOperationException("新候选任务编号已有车端记录，拒绝发送。");
            var target=finalStations.Single(s=>s.Id==record.TargetSite);
            var send=detour with {State="SendIntent",Message="已持久化新任务链发送意图；最多发送一次，未知结果不重发。",SendAttemptedAt=AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(),NewVendorTaskIds=ids.ToArray()};
            _record=_record! with {VendorTaskId=vendor,VendorTaskIds=ids.ToArray(),NavigationApi=3066,SourceSite=detour.Plan.SourceSite,
                Route=detour.Plan.Route.ToArray(),TargetX=target.X,TargetY=target.Y,Detour=send,State="Uncertain",Message=send.Message,UpdatedAt=AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()};
            Persist();
            try
            {
                // All segments have fresh, distinct task IDs; no actions, speed override or safety override.
                await client.GoToTargetAsync(vendor,detour.Plan.Route,null,true,ct);
                var accepted=await client.QueryTasksAsync(ids,ct);
                var complete=accepted.Count==ids.Count&&accepted.All(x=>x.Status is >=1 and <=4);
                _record=_record with {State=complete?"Accepted":"Uncertain",Detour=send with {State="Executing",Message=complete?
                    "候选3066所有分段均已接受；不代表到点完成。":"候选应答后整条任务链尚未全部确认；只查询原新编号，不重发。"}};
            }
            catch(Exception ex){_record=_record with {State="Uncertain",Detour=send with {State="Executing",Message="候选下发结果不确定；保留原新编号，只读查询，禁止重发："+ex.Message}};}
            _record=_record with {Message=_record.Detour!.Message,UpdatedAt=AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()};
            SynchronizeSentDetourStatus();Persist();return _record;
        }
        finally{_mutex.Release();}
    }

    /// <summary>
    /// Test-only automatic detour driver. The explicit launch flag is the only
    /// authorization for cancellation. Every motion and cancellation still
    /// passes through the same map, task, stop, and one-shot checks as the
    /// operator-confirmed path above.
    /// </summary>
    public async Task AdvanceAutoDetourAsync(CancellationToken ct)
    {
        if(!_settings.AutoDetourEnabled)return;
        var user=TrafficUserContext.LocalOperation("auto-navigation-detour");
        NavigationTestRecord? snapshot=null;
        NavigationTestDetourContinue? continuation=null;
        string? previewTaskId=null;
        string? auditState=null;
        string? auditMessage=null;

        await _mutex.WaitAsync(ct);
        try
        {
            snapshot=_record;
            if(snapshot is null)return;
            if(Terminal(snapshot.State))
            {
                auditState=snapshot.State;
                auditMessage=snapshot.Message;
            }
            else if(snapshot.State=="Uncertain"||snapshot.CancellationAttemptedAt is not null)
            {
                auditState=snapshot.State=="Uncertain"?"Uncertain":"CancelRequested";
                auditMessage=snapshot.Message;
            }
            else if(snapshot.Detour is { } detour)
            {
                if(detour.SendAttemptedAt is not null)
                {
                    auditState="Executing";auditMessage=detour.Message;
                }
                else if(detour.State=="Ready")
                {
                    continuation=new(snapshot.TaskId,detour.PreviewId,true);
                    auditState="ReadyToSend";
                    auditMessage="原厂家任务已全部终态，车辆连续停稳；自动发送一次备选3066任务链。";
                }
                else if(detour.State=="ReviewHold")
                {
                    auditState="ReviewHold";
                    auditMessage="后台重启后的未发送候选保持人工复核，不自动续发。";
                }
                else
                {
                    auditState="AwaitingOriginalTerminal";
                    auditMessage=detour.Message;
                }
            }
            else if(snapshot.State is not ("Accepted" or "Waiting" or "Running" or "Suspended")||
                snapshot.CancellationAttemptedAt is not null||snapshot.ResumeAttemptedAt is not null)
            {
                auditState="Unavailable";
                auditMessage="原任务不是可安全自动改道的活动状态；保持只读核对。";
            }
            else
            {
                var wait=TimeSpan.FromSeconds(Math.Max(1,Math.Max(_settings.AutoDetourWaitSeconds,_settings.DetourWaitSeconds)));
                if(snapshot.BlockedSince is not { } since)
                {
                    auditState="WaitingForBlock";
                    auditMessage="尚未形成连续阻挡证据；继续查询原任务，不取消或改道。";
                }
                else if(AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow()-since<wait)
                {
                    auditState="WaitingForBlock";
                    auditMessage=$"自动改道等待：连续受阻 {Math.Max(0,(AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow()-since).TotalSeconds):0.0}/{wait.TotalSeconds:0} 秒。";
                }
                else
                {
                    previewTaskId=snapshot.TaskId;
                    auditState="Planning";
                    auditMessage="连续受阻达到门槛，正在按当前站点、原任务下一站和车端地图规划备选路线。";
                }
            }
        }
        finally{_mutex.Release();}

        if(continuation is not null)
        {
            await AuditAutoDetourAsync(user,auditState!,auditMessage!,snapshot!);
            try
            {
                var continued=await ContinueDetourAsync(user,continuation,ct);
                await AuditAutoDetourAsync(user,continued.State=="Uncertain"?"Uncertain":"Executing",
                    continued.Message,continued);
            }
            catch(Exception ex)
            {
                await AuditAutoDetourAsync(user,"SendBlocked","备选路线执行前核验未通过，保持原新任务编号并只读查询："+ex.Message,_record);
            }
            return;
        }

        if(previewTaskId is null)
        {
            if(auditState is not null)await AuditAutoDetourAsync(user,auditState,auditMessage!,snapshot);
            return;
        }

        await AuditAutoDetourAsync(user,"Planning",auditMessage!,snapshot);
        NavigationTestDetourPreview preview;
        try
        {
            preview=await PreviewDetourAsync(user,new(previewTaskId),ct);
        }
        catch(Exception ex)
        {
            await AuditAutoDetourAsync(user,"Unavailable","自动改道未启动，原任务保持不变："+ex.Message,_record);
            return;
        }

        var original=_record??snapshot!;
        var sourceIndex=original.Route.Select((site,index)=>(site,index))
            .FirstOrDefault(row=>string.Equals(row.site,preview.Plan.SourceSite,StringComparison.Ordinal));
        var blockedTo=sourceIndex.site is not null&&sourceIndex.index+1<original.Route.Count
            ? original.Route[sourceIndex.index+1]:null;
        var planMessage="已生成并核验候选："+string.Join(" → ",preview.Plan.Route)+"；自动取消前仍会重新核对障碍、地图和原任务。";
        if(!preview.Plan.Available)
        {
            await AuditAutoDetourAsync(user,"Unavailable",preview.Plan.Message,original,preview.Plan,
                preview.PreviewId,preview.Plan.SourceSite,blockedTo);
            return;
        }

        await AuditAutoDetourAsync(user,"CancelIntent",planMessage,original,preview.Plan,
            preview.PreviewId,preview.Plan.SourceSite,blockedTo);
        try
        {
            var cancelled=await ConfirmDetourAsync(user,
                new(preview.TaskId,preview.PreviewId,true),ct);
            await AuditAutoDetourAsync(user,"CancellationRequested",
                cancelled.Detour?.Message??"已提交精确取消意图；等待原厂家任务终态。",cancelled,
                preview.Plan,preview.PreviewId,preview.Plan.SourceSite,blockedTo);
        }
        catch(Exception ex)
        {
            await AuditAutoDetourAsync(user,"CancelBlocked","自动取消前核验未通过或结果不确定；不重发取消："+ex.Message,_record,
                preview.Plan,preview.PreviewId,preview.Plan.SourceSite,blockedTo);
        }
    }

    private async Task AuditAutoDetourAsync(TrafficUserContext user,string state,string message,
        NavigationTestRecord? snapshot,NavigationTestDetourPlan? plan=null,string? previewId=null,
        string? sourceSite=null,string? blockedToSite=null)
    {
        await _mutex.WaitAsync();
        try
        {
            var record=_record;
            if(record is null)return;
            if(record.AutoDetour is { } current&&current.State==state&&current.Message==message&&
                (record.Detour?.Plan is null||current.CandidateRoute.Count>0))return;
            var previous=record.AutoDetour;
            var detourPlan=plan??record.Detour?.Plan;
            var resolvedSource=sourceSite??detourPlan?.SourceSite??previous?.SourceSite;
            var originalRoute=record.Detour?.OriginalRoute??[];
            var sourceIndex=originalRoute.Select((site,index)=>(site,index))
                .FirstOrDefault(row=>string.Equals(row.site,resolvedSource,StringComparison.Ordinal));
            var resolvedBlockedTo=blockedToSite??previous?.BlockedToSite??
                (sourceIndex.site is not null&&sourceIndex.index+1<originalRoute.Count?originalRoute[sourceIndex.index+1]:null);
            var next=new NavigationTestAutoDetourRecord(state,message,AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(),user.WindowsIdentity)
            {
                BlockedSince=record.BlockedSince??snapshot?.BlockedSince??previous?.BlockedSince,
                SourceSite=resolvedSource,
                BlockedToSite=resolvedBlockedTo,
                PreviewId=previewId??record.Detour?.PreviewId??previous?.PreviewId,
                PlanMessage=detourPlan?.Message??previous?.PlanMessage,
                CandidateRoute=detourPlan?.Route.ToArray()??previous?.CandidateRoute??[],
                BlockedEdgeIds=detourPlan?.BlockedEdgeIds.ToArray()??previous?.BlockedEdgeIds??[]
            };
            _record=record with {AutoDetour=next};Persist();
        }
        finally{_mutex.Release();}
    }
}
