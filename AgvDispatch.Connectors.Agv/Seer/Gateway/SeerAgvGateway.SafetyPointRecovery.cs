using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using System.Net.Sockets;

namespace AgvDispatch.Connectors.Agv;

public sealed partial class SeerAgvGateway
{
    public async Task<string> CreateObstacleSafetyPointRetreatAsync(
        string retreatVendorTaskId,
        string vehicleId,
        string targetSite,
        string blockedSourceSite,
        string blockedTargetSite,
        double maximumSpeedMetersPerSecond,
        double maximumAccelerationMetersPerSecondSquared,
        double remainingDistanceMeters,
        DateTimeOffset blockedSince,
        string originalVendorTaskId,
        CancellationToken cancellationToken)
    {
        EnsureCommandsEnabled();
        ArgumentException.ThrowIfNullOrWhiteSpace(retreatVendorTaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSite);
        ArgumentException.ThrowIfNullOrWhiteSpace(blockedSourceSite);
        ArgumentException.ThrowIfNullOrWhiteSpace(blockedTargetSite);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalVendorTaskId);
        if (!double.IsFinite(remainingDistanceMeters) || remainingDistanceMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(remainingDistanceMeters),
                "安全点退回距离必须是大于0的有限数值。");
        if (blockedSince == default || blockedSince > AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow().AddSeconds(1))
            throw new InvalidOperationException("退回安全站点缺少可信的连续受阻起始时间。");
        if (!connections.TryGet(vehicleId, out var client))
            throw new InvalidOperationException($"未配置AGV车辆{vehicleId}。");

        await EnsureSafetyPointRetreatCanSendAsync(
            client, targetSite, blockedSourceSite, blockedTargetSite,
            originalVendorTaskId, cancellationToken);
        var existing = await client.QueryTaskAsync(retreatVendorTaskId, cancellationToken);
        if (existing is not null && existing.Status != 404)
            throw new AgvPartialAcceptanceException("安全点退回任务编号已有车端记录，禁止重发。");

        try
        {
            if (remainingDistanceMeters <= 1.0)
            {
                await client.TranslateBackwardAsync(
                    remainingDistanceMeters,
                    Math.Min(maximumSpeedMetersPerSecond, 0.1),
                    cancellationToken);
                return retreatVendorTaskId;
            }

            await client.GoFromCurrentPoseBackwardAsync(
                retreatVendorTaskId,
                targetSite,
                maximumSpeedMetersPerSecond,
                maximumAccelerationMetersPerSecondSquared,
                cancellationToken);
        }
        catch (AgvCommandRejectedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
        {
            // The request may have been accepted before the response was lost.
            // The coordinator persists the id before sending and will query it
            // instead of issuing a duplicate recovery move.
            throw new AgvPartialAcceptanceException(
                remainingDistanceMeters <= 1.0
                    ? "3055短距离平动后退结果不确定；只核对车辆实时位置，禁止重发。"
                    : "3051安全点退回请求结果不确定；只查询原任务编号，禁止重发。");
        }

        if (remainingDistanceMeters <= 1.0) return retreatVendorTaskId;
        var accepted = await client.QueryTaskAsync(retreatVendorTaskId, cancellationToken);
        if (accepted is null || accepted.Status is not (1 or 2 or 3 or 4))
            throw new AgvPartialAcceptanceException(
                "3051应答成功但安全点退回任务未确认；禁止重发，等待只读查询。");
        return retreatVendorTaskId;
    }

    private async Task EnsureSafetyPointRetreatCanSendAsync(
        SeerRobotClient client,
        string targetSite,
        string blockedSourceSite,
        string blockedTargetSite,
        string originalVendorTaskId,
        CancellationToken cancellationToken)
    {
        if (!stateCache.TryGet(client.Robot.VehicleId, out var robot))
            throw new InvalidOperationException($"AGV状态缓存中不存在车辆{client.Robot.VehicleId}。");
        if (robot.Connection != ConnectionState.Online || robot.Quality != SignalQuality.Good)
            throw new InvalidOperationException($"AGV {robot.VehicleId}通信状态或数据质量不允许退回安全站点。");
        if (robot.EmergencyStopped != false || robot.Charging == true ||
            robot.Alarms.Any(alarm =>
                alarm.Severity >= AgvAlarmSeverity.Error && !AgvObstaclePolicy.IsLocalObstacle(alarm)))
            throw new InvalidOperationException(
                $"AGV {robot.VehicleId}存在急停、充电或非局部避障严重报警，禁止退回安全站点。");
        if (robot.Tasks.Any(task => task.Status is 1 or 2 or 3))
            throw new InvalidOperationException(
                $"AGV {robot.VehicleId}仍有已接收、执行中或暂停的车端任务，禁止发送安全点退回。");
        if (!await OriginalChainTerminalAsync(originalVendorTaskId, cancellationToken))
            throw new InvalidOperationException(
                $"AGV {robot.VehicleId}原3066任务链未全部终态，禁止发送安全点退回。");
        if (!string.Equals(blockedSourceSite, targetSite, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(blockedTargetSite))
            throw new InvalidOperationException(
                $"AGV {robot.VehicleId}持久化受阻路段起点{blockedSourceSite}与退回目标{targetSite}不一致。");
        // 1110 may clear source_name/target_name after 3003. A non-empty live
        // field must still agree, while absence is covered by the persisted
        // cancellation-time evidence passed by the coordinator.
        if (!string.IsNullOrWhiteSpace(robot.SourceStation) &&
            !string.Equals(robot.SourceStation, blockedSourceSite, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"AGV {robot.VehicleId}实时路段起点{robot.SourceStation}与持久化受阻路段不一致。");
        if (!string.IsNullOrWhiteSpace(robot.TargetStation) &&
            !string.Equals(robot.TargetStation, blockedTargetSite, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"AGV {robot.VehicleId}实时路段终点{robot.TargetStation}与持久化受阻路段不一致。");
        if (robot.LocalizationConfidence is not >= 0.5 ||
            robot.X is not { } x || !double.IsFinite(x) ||
            robot.Y is not { } y || !double.IsFinite(y) ||
            robot.VelocityX is not { } vx || !double.IsFinite(vx) || Math.Abs(vx) >= 0.02 ||
            robot.VelocityY is not { } vy || !double.IsFinite(vy) || Math.Abs(vy) >= 0.02 ||
            robot.AngularVelocity is not { } w || !double.IsFinite(w) || Math.Abs(w) >= 0.02)
            throw new InvalidOperationException(
                $"AGV {robot.VehicleId}定位不足、位置未知或尚未连续停稳，禁止退回安全站点。");
    }
}
