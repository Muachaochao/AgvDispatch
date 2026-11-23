using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Connectors.Agv;

public sealed partial class SeerAgvGateway
{
    public async Task<string> CreateObstacleRecoveryTransportTaskAsync(
        string newVendorTaskId,
        string vehicleId,
        IReadOnlyList<string> path,
        AgvTransportAction? action,
        DateTimeOffset blockedSince,
        string originalVendorTaskId,
        ObstacleRecoveryPreSendLimits limits,
        CancellationToken cancellationToken)
    {
        // Everything before the first 3066 write is a pre-send rejection: nothing
        // reached the vehicle, so the coordinator may clear the send intent and retry.
        try
        {
            EnsureCommandsEnabled();
        }
        catch (InvalidOperationException error)
        {
            throw new AgvRecoverySendRejectedException(error.Message);
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(newVendorTaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalVendorTaskId);
        if (path.Count < 2 || path.Count > 2048 || path.Any(string.IsNullOrWhiteSpace) ||
            path.Zip(path.Skip(1)).Any(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase)))
            throw new AgvRecoverySendRejectedException("自动改道路径必须包含至少两个连续且不重复的有效站点。");
        if (blockedSince == default || blockedSince > AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow().AddSeconds(1))
            throw new AgvRecoverySendRejectedException("自动改道缺少可信的连续受阻起始时间。");
        if (path.Count > 2 &&
            !string.Equals(options.NavigationCommandMode, "SpecifiedPath", StringComparison.OrdinalIgnoreCase))
            throw new AgvRecoverySendRejectedException("自动改道只能使用逐段指定路径模式，不能用3051让车端自行选路。");
        if (!connections.TryGet(vehicleId, out var client))
            throw new AgvRecoverySendRejectedException($"未配置AGV车辆{vehicleId}。");

        await EnsureObstacleRecoveryCanStopAsync(vehicleId, path[0], originalVendorTaskId, limits, cancellationToken);
        if (routeGuard is not null)
        {
            var guard = await routeGuard.CheckRouteAsync(vehicleId, path, cancellationToken);
            if (!guard.Accepted) throw new AgvRecoverySendRejectedException(guard.Message);
        }
        using var roadLease = roadClosures is null ? null :
            await roadClosures.EnterRouteSendAsync(path, cancellationToken);
        await EnsureObstacleRecoveryCanStopAsync(vehicleId, path[0], originalVendorTaskId, limits, cancellationToken);

        var ids = string.Equals(options.NavigationCommandMode, "SpecifiedPath", StringComparison.OrdinalIgnoreCase)
            ? AgvTransportSegmentIds.For(newVendorTaskId, path.Count)
            : [newVendorTaskId];
        var existing = await client.QueryTasksAsync(ids, cancellationToken);
        if (existing.Any(row => row.Status != 404))
            throw new AgvPartialAcceptanceException("自动改道新任务编号已有车端记录，禁止重发。");

        // From here on the request may have reached the vehicle. Only an explicit
        // vehicle refusal (AgvCommandRejectedException) is a definite rejection and
        // propagates as such; I/O errors and timeouts stay uncertain.
        await client.GoToTargetAsync(newVendorTaskId, path, action, true, cancellationToken);
        var accepted = await client.QueryTasksAsync(ids, cancellationToken);
        if (accepted.Count != ids.Count || accepted.Any(row => row.Status is not (1 or 2 or 3 or 4)))
            throw new AgvPartialAcceptanceException("自动改道3066应答后任务链未完整确认，禁止重发。");
        return newVendorTaskId;
    }

    private async Task EnsureObstacleRecoveryCanStopAsync(string vehicleId, string sourceSite,
        string originalVendorTaskId, ObstacleRecoveryPreSendLimits limits, CancellationToken cancellationToken)
    {
        if (!stateCache.TryGet(vehicleId, out var robot))
            throw new AgvRecoverySendRejectedException($"AGV状态缓存中不存在车辆{vehicleId}。");
        if (robot.Connection != ConnectionState.Online || robot.Quality != SignalQuality.Good)
            throw new AgvRecoverySendRejectedException($"AGV {vehicleId}通信状态或数据质量不允许改道。");
        if (robot.EmergencyStopped != false || robot.Charging == true ||
            robot.Alarms.Any(alarm => alarm.Severity >= AgvAlarmSeverity.Error && !AgvObstaclePolicy.IsLocalObstacle(alarm)))
            throw new AgvRecoverySendRejectedException($"AGV {vehicleId}存在急停、充电或非局部避障严重报警，禁止自动改道。");
        if (robot.Tasks.Any(task => task.Status is 1 or 2 or 3))
            throw new AgvRecoverySendRejectedException($"AGV {vehicleId}仍有已接收、执行中或暂停的车端任务，禁止发送替代路线。");
        if (!AgvObstaclePolicy.IsBlocked(robot))
        {
            if (robot.Blocked != false ||
                await OriginalChainTerminalAsync(originalVendorTaskId, cancellationToken) is false)
                throw new AgvRecoverySendRejectedException(
                    $"AGV {vehicleId}障碍已清除，但原3066任务未证明全链终态，禁止走障碍恢复发送口。");
        }
        // Same thresholds as the coordinator: confidence must not fall below the
        // configured minimum (NaN fails), and each velocity component must be within
        // the configured stopped tolerance.
        if (!(robot.LocalizationConfidence is { } confidence && confidence >= limits.MinimumLocalizationConfidence) ||
            robot.VelocityX is not { } vx || !double.IsFinite(vx) || Math.Abs(vx) > limits.StoppedVelocityToleranceMetersPerSecond ||
            robot.VelocityY is not { } vy || !double.IsFinite(vy) || Math.Abs(vy) > limits.StoppedVelocityToleranceMetersPerSecond ||
            robot.AngularVelocity is not { } w || !double.IsFinite(w) || Math.Abs(w) > limits.StoppedVelocityToleranceMetersPerSecond)
            throw new AgvRecoverySendRejectedException($"AGV {vehicleId}定位不足或尚未连续停稳，禁止自动改道。");
        if (!string.Equals(robot.CurrentStation, sourceSite, StringComparison.OrdinalIgnoreCase))
            throw new AgvRecoverySendRejectedException(
                $"AGV {vehicleId}当前站点{robot.CurrentStation ?? "<未知>"}与改道起点{sourceSite}不一致。");
    }

    private async Task<bool> OriginalChainTerminalAsync(string originalVendorTaskId,
        CancellationToken cancellationToken)
    {
        var chain = await ReadChainAsync(originalVendorTaskId, cancellationToken);
        if (chain is not { } evidence || evidence.Rows.Count != evidence.Ids.Count ||
            evidence.Rows.Any(row => row.Status is < 1 or > 7))
            return false;
        return evidence.Rows.All(row => row.Status is 4 or 5 or 6 or 7);
    }
}
