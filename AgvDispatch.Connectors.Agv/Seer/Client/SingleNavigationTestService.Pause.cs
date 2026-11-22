using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Connectors.Agv;

public sealed partial class SingleNavigationTestService
{
    public async Task<NavigationTestRecord> PauseAsync(TrafficUserContext user,
        NavigationTestPause request, CancellationToken ct)
    {
        Authorize(user);
        await _mutex.WaitAsync(ct);
        try
        {
            var record = _record;
            if (record is null || record.TaskId != request.TaskId || Terminal(record.State))
                throw new InvalidOperationException("没有匹配的未结束测试任务。");
            if (record.PauseAttemptedAt is not null)
                throw new InvalidOperationException("当前原任务已经记录暂停命令尝试，只能查询原任务，不能重复发送。");
            if (record.Detour is not null || record.CancellationAttemptedAt is not null ||
                record.ResumeAttemptedAt is not null)
                throw new InvalidOperationException("原任务已进入改道、取消或继续流程，不能另发暂停命令。");

            RequireKnownOriginal(record);
            var ids = CurrentVendorIds(record);
            using var lease = await _execution.EnterControlAsync(ct);
            RequireCommissioningMapRunning();
            var client = await GuardAsync(record.VehicleId, ct);

            using var mapResponse = await client.RequestAsync(1300, null, ct);
            if (MapIdentity(mapResponse.RootElement) != (record.RobotMap, record.RobotMapMd5))
                throw new InvalidOperationException("车端地图身份与原测试不同，不能暂停当前导航。");
            using var mapLoad = await client.RequestAsync(1022, null, ct);
            using var owner = await client.RequestAsync(1060, null, ct);
            if (Int(mapLoad.RootElement, "loadmap_status") != 1 ||
                !owner.RootElement.TryGetProperty("locked", out var locked) ||
                locked.ValueKind != System.Text.Json.JsonValueKind.False)
                throw new InvalidOperationException("地图未加载成功或控制权被占用/未知，不能暂停当前导航。");

            var (telemetry, _) = await client.PollAsync(ct);
            ObservePosition(telemetry);
            if (telemetry.EmergencyStopped != false || telemetry.Charging == true ||
                telemetry.Blocked != false || LocalBlocked(telemetry) ||
                telemetry.LocalizationConfidence is not >= 0.5 ||
                telemetry.Alarms.Any(a => a.Severity is >= AgvDispatch.Domain.Agvs.AgvAlarmSeverity.Error))
                throw new InvalidOperationException("车辆急停、阻挡、充电、定位不足或存在严重报警，不能发送导航暂停。");
            RequireNoForeignTasks(telemetry, ids);

            var rows = await client.QueryTasksAsync(ids, ct);
            if (rows.Count != ids.Count || rows.Any(task => task.Status is < 1 or > 7))
                throw new InvalidOperationException("原任务链证据不完整，不能暂停当前导航。");
            if (rows.Any(task => task.Status == 3))
                throw new InvalidOperationException("原导航已经暂停；请先查询并核对原任务，不重复发送3001。");
            var running = rows.SingleOrDefault(task => task.Status == 2);
            if (running is null)
                throw new InvalidOperationException("原任务当前不是运行状态，不需要发送导航暂停。");

            var current = _record ?? throw new InvalidOperationException("原导航记录在暂停核验期间丢失。");
            _record = current with
            {
                State = "PauseIntent",
                Message = "已持久化暂停当前导航意图；结果确认前禁止重复发送。",
                PauseAttemptedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(),
                PauseVendorTaskId = running.TaskId,
                PauseStoppedSamples = 0,
                PauseLastObservedAt = null,
                PauseStoppedAt = null,
                UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()
            };
            Persist();
            try
            {
                await client.PauseNavigationAsync(ct);
                _record = _record with
                {
                    State = "PauseRequested",
                    Message = "车端已应答3001；等待原任务状态3及连续停稳证据，应答不代表已停车。",
                    UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()
                };
            }
            catch (Exception ex)
            {
                _record = _record with
                {
                    State = "Uncertain",
                    Message = "暂停当前导航的结果不确定，禁止重发，只能查询原任务或现场核对：" + ex.Message,
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

    private void ObservePauseState(string state, AgvRobotTelemetry telemetry)
    {
        if (_record is null || _record.PauseAttemptedAt is null || _record.ResumeAttemptedAt is not null ||
            state != "Suspended")
            return;

        if (!SafeStopped(telemetry))
        {
            if (_record.PauseStoppedAt is null)
            {
                _record = _record with
                {
                    PauseStoppedSamples = 0,
                    PauseLastObservedAt = telemetry.ReadAt,
                    Message = "车端已报告导航暂停，但尚未取得定位有效、无严重报警且速度归零的停稳证据。",
                    UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()
                };
            }
            return;
        }

        if (_record.PauseLastObservedAt is { } previous && telemetry.ReadAt <= previous)
            return;
        var samples = Math.Min(2, _record.PauseStoppedSamples + 1);
        var stoppedAt = samples >= 2 ? _record.PauseStoppedAt ?? telemetry.ReadAt : _record.PauseStoppedAt;
        _record = _record with
        {
            PauseStoppedSamples = samples,
            PauseLastObservedAt = telemetry.ReadAt,
            PauseStoppedAt = stoppedAt,
            Message = samples >= 2
                ? "原导航已暂停，并连续两次观察到车速接近零、定位有效且无严重报警；可继续原导航或取消。"
                : "车端已报告导航暂停，正在等待第二次新鲜停稳观察。",
            UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()
        };
    }
}
