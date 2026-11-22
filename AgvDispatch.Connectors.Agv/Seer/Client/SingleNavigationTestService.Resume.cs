using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Connectors.Agv;

public sealed partial class SingleNavigationTestService
{
    public async Task<NavigationTestRecord> ResumeAsync(TrafficUserContext user,
        NavigationTestResume request, CancellationToken ct)
    {
        Authorize(user);
        await _mutex.WaitAsync(ct);
        try
        {
            var record = _record;
            if (record is null || record.TaskId != request.TaskId || Terminal(record.State))
                throw new InvalidOperationException("没有匹配的暂停测试任务。");
            if (record.State != "Suspended")
                throw new InvalidOperationException("只有车端明确暂停的原任务可以继续；请先查询原任务。");
            if (record.Detour is not null || record.CancellationAttemptedAt is not null)
                throw new InvalidOperationException("原任务已进入改道或取消流程，不能发送继续命令。");
            if (record.ResumeAttemptedAt is not null)
                throw new InvalidOperationException("当前原任务已经记录继续命令尝试，只能查询原任务，不能重复发送。");
            if (record.PauseAttemptedAt is not null && record.PauseStoppedAt is null)
                throw new InvalidOperationException("暂停命令尚未取得连续停稳证据，不能继续原导航。");
            RequireKnownOriginal(record);
            var ids = CurrentVendorIds(record);
            using var lease = await _execution.EnterControlAsync(ct);
            RequireCommissioningMapRunning();
            var client = await GuardAsync(record.VehicleId, ct);
            var (telemetry, stations) = await ObserveDetourSiteAsync(client, record, ct);
            if (telemetry.Blocked != false || LocalBlocked(telemetry))
                throw new InvalidOperationException("车端仍报告阻挡或52200，不能继续原导航。");
            RequireNoForeignTasks(telemetry, ids);
            var currentSite = RequireActualSite(telemetry, stations);
            if (!record.Route.Contains(currentSite.Id, StringComparer.Ordinal))
                throw new InvalidOperationException("车辆当前站点不属于原路线，不能继续原导航。");
            var rows = await client.QueryTasksAsync(ids, ct);
            if (rows.Count != ids.Count || rows.Any(task => task.Status is < 1 or > 7) ||
                rows.Count(task => task.Status is 1 or 2 or 3) != 1 ||
                rows.Single(task => task.Status is 1 or 2 or 3).Status != 3)
                throw new InvalidOperationException("原厂家任务不是唯一的暂停状态，不能发送3002。");
            using var closureLease = _roadClosures is null
                ? null
                : await _roadClosures.EnterRouteSendAsync(record.Route, ct);
            var vendorTaskId = rows.Single(task => task.Status == 3).TaskId;
            var current = _record ?? throw new InvalidOperationException("原导航记录在继续核验期间丢失。");
            _record = current with
            {
                State = "ResumeIntent",
                Message = "已持久化继续原导航意图；结果未确认前禁止重复发送。",
                ResumeAttemptedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(),
                ResumeVendorTaskId = vendorTaskId,
                UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()
            };
            Persist();
            try
            {
                await client.ResumeNavigationAsync(ct);
                _record = _record with
                {
                    State = "ResumeRequested",
                    Message = "车端已应答3002；请继续查询原任务，应答不代表已恢复运动或完成。",
                    UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()
                };
            }
            catch (Exception ex)
            {
                _record = _record with
                {
                    State = "Uncertain",
                    Message = "继续原导航的结果不确定，禁止重发，只能查询原任务或现场核对：" + ex.Message,
                    UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()
                };
            }
            Persist();
            return _record;
        }
        finally
        {
            _mutex.Release();
        }
    }
}
