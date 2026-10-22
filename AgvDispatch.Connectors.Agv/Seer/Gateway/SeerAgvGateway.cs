using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using System.Runtime.ExceptionServices;

namespace AgvDispatch.Connectors.Agv;

/// <summary>Robokit TCP adapter. The stable StepId is also the final vendor task_id.</summary>
public sealed class SeerAgvGateway(
    IAgvStateCache stateCache,
    AgvConnectionManager connections,
    AgvOptions options, IAgvTransportChainLookup? chains = null, IRobokitTaskMapPreflight? routeGuard = null) : IAgvGateway
{
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var robots = stateCache.GetAll();
        var online = robots.Count(robot => robot.Connection == ConnectionState.Online);
        var state = online == robots.Count && robots.Count > 0
            ? ConnectionState.Online
            : online > 0 ? ConnectionState.Degraded : ConnectionState.Disconnected;
        return Task.FromResult(new DeviceConnectionStatus(
            "AGV-SYSTEM",
            "AGV车队",
            state,
            DateTimeOffset.Now,
            $"在线{online}/{robots.Count}"));
    }

    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var vehicles = stateCache.GetAll().Select(robot => new AgvVehicleStatus(
            robot.VehicleId,
            robot.Connection,
            GetOperatingState(robot),
            robot.BatteryPercent ?? 0m,
            robot.CurrentStation,
            robot.UpdatedAt)).ToArray();
        return Task.FromResult<IReadOnlyList<AgvVehicleStatus>>(vehicles);
    }

    public async Task<string> CreateTransportTaskAsync(
        string businessTaskId,
        string? preferredVehicleId,
        IReadOnlyList<string> path,
        AgvTransportAction? action,
        CancellationToken cancellationToken)
    {
        EnsureCommandsEnabled();
        ArgumentException.ThrowIfNullOrWhiteSpace(businessTaskId);
        if (string.IsNullOrWhiteSpace(preferredVehicleId))
        {
            throw new InvalidOperationException("创建AGV运输任务前必须明确指定车辆。");
        }

        if (!connections.TryGet(preferredVehicleId, out var client))
        {
            throw new InvalidOperationException($"未配置AGV车辆{preferredVehicleId}。");
        }

        var useSpecifiedPath = string.Equals(options.NavigationCommandMode,"SpecifiedPath",StringComparison.OrdinalIgnoreCase);
        var ids = useSpecifiedPath ? AgvTransportSegmentIds.For(businessTaskId,path.Count) : new[] { businessTaskId };
        var persisted=chains is null ? null : await chains.FindAsync(businessTaskId,cancellationToken);
        if(persisted is not null && (persisted.VehicleId!=preferredVehicleId || !persisted.Path.SequenceEqual(path)))
            throw new AgvPartialAcceptanceException("请求与原持久化运输路径/车辆不符，禁止发送。");
        var existing = await ReadAcceptanceBeforeSendAsync(client, ids, cancellationToken);
        if (existing) return businessTaskId;
        if(persisted is { MaySend:false }) throw new AgvPartialAcceptanceException("原Outbox不是发送认领状态，禁止重新发送。");
        if(persisted?.AttemptCount>1)
            throw new AgvPartialAcceptanceException("原发送已经尝试且车端全部404；无法排除执行后历史丢失，停止自动补发并保留路权。");

        EnsureVehicleCanAcceptCommand(preferredVehicleId, path);
        if(routeGuard is not null)
        {
            var check=await routeGuard.CheckRouteAsync(preferredVehicleId,path,cancellationToken);
            if(!check.Accepted)throw new InvalidOperationException(check.Message);
            EnsureVehicleCanAcceptCommand(preferredVehicleId,path);
        }
        try
        {
            await client.GoToTargetAsync(
                businessTaskId,
                path,
                action,
                useSpecifiedPath,
                cancellationToken);
            if (ids.Count > 1 && !await ReadAcceptanceAsync(client,ids,cancellationToken))
                throw new AgvPartialAcceptanceException("3066应答成功但整条任务链未确认；禁止补发，保持路权并核对。");
            return businessTaskId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception sendException)
        {
            // TCP超时可能发生在机器人已接收、但响应未返回的时刻，不能直接重复创建。
            for (var attempt = 0; attempt < options.CommandConfirmationAttempts; attempt++)
            {
                await Task.Delay(options.CommandConfirmationDelayMs, cancellationToken);
                try
                {
                    if (await ReadAcceptanceAsync(client, ids, cancellationToken))
                    {
                        return businessTaskId;
                    }
                }
                catch (AgvPartialAcceptanceException) { throw; }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // 保留最初的发送异常，确认查询失败不能掩盖根因。
                }
            }

            ExceptionDispatchInfo.Capture(sendException).Throw();
            throw;
        }
    }

    private async Task<bool> ReadAcceptanceBeforeSendAsync(SeerRobotClient client, IReadOnlyList<string> ids, CancellationToken ct)
    {
        // Status and navigation use separate sockets. A fresh 1100 does not
        // prove the old navigation socket survived a vehicle disconnection.
        // Only retry this read-only identity query, before entering any send.
        for (var attempt = 0; ; attempt++)
        {
            try { return await ReadAcceptanceAsync(client, ids, ct); }
            catch (Exception error) when (!ct.IsCancellationRequested &&
                attempt < Math.Max(1, options.CommandConfirmationAttempts) &&
                error is IOException or System.Net.Sockets.SocketException or TimeoutException)
            {
                await Task.Delay(options.CommandConfirmationDelayMs, ct);
            }
        }
    }

    private static async Task<bool> ReadAcceptanceAsync(SeerRobotClient client, IReadOnlyList<string> ids, CancellationToken ct)
    {
        // Keep single-segment API compatibility; multi-segment confirmation checks every stable id.
        var rows = ids.Count == 1 ? new[] { await client.QueryTaskAsync(ids[0],ct) }.Where(r=>r is not null).Cast<SeerTaskSnapshot>().ToArray()
            : await client.QueryTasksAsync(ids,ct);
        var found = rows.Where(r=>r.Status!=404).ToArray();
        if(rows.Count!=ids.Count) throw new AgvPartialAcceptanceException("1110未完整返回逐段任务身份；发送结果不确定，禁止补发。");
        if(found.Length==0) return false;
        if(found.Length!=ids.Count || found.Any(r=>r.Status is not (1 or 2 or 3 or 4)))
            throw new AgvPartialAcceptanceException("3066任务链部分接收、已取消/失败或状态未知；禁止整包补发，保留路权并进行恢复核对。");
        return true;
    }

    private async Task<(SeerRobotClient Client, IReadOnlyList<SeerTaskSnapshot> Rows, IReadOnlyList<string> Ids)?> ReadChainAsync(string id,CancellationToken ct)
    {
        if(chains is null || !string.Equals(options.NavigationCommandMode,"SpecifiedPath",StringComparison.OrdinalIgnoreCase))return null;
        var chain=await chains.FindAsync(id,ct);
        if(chain is null)return null;
        if(!connections.TryGet(chain.VehicleId,out var client))throw new InvalidOperationException("原任务链车辆未配置，不能扫描其他车辆替代。");
        var ids=AgvTransportSegmentIds.For(id,chain.Path.Count);
        var rows=ids.Count==1 ? new[] { await client.QueryTaskAsync(id,ct) }.Where(r=>r is not null).Cast<SeerTaskSnapshot>().ToArray() : await client.QueryTasksAsync(ids,ct);
        return (client,rows,ids);
    }

    public async Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(
        string vendorTaskId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vendorTaskId);
        var chain=await ReadChainAsync(vendorTaskId,cancellationToken);
        if(chain is { } evidence)
        {
            var known=evidence.Rows.Where(r=>r.Status!=404).ToArray();
            var complete=known.Length==evidence.Ids.Count;
            var allTerminal=complete && known.All(r=>r.Status is 4 or 5 or 6 or 7);
            var state=!complete || known.Any(r=>r.Status is not (1 or 2 or 3 or 4 or 5 or 6 or 7)) ? AgvTransportTaskState.Unknown :
                allTerminal ? known.Any(r=>r.Status is 5 or 7) ? AgvTransportTaskState.Failed : known.Any(r=>r.Status==6) ? AgvTransportTaskState.Cancelled : AgvTransportTaskState.Completed :
                known.Any(r=>r.Status is 5 or 6 or 7) ? AgvTransportTaskState.Unknown : known.Any(r=>r.Status==3) ? AgvTransportTaskState.Suspended : AgvTransportTaskState.GoingToDestination;
            return new(vendorTaskId,vendorTaskId,state,evidence.Client.Robot.VehicleId,DateTimeOffset.Now,
                $"整条3066任务链：{known.Length}/{evidence.Ids.Count}段有记录；状态 {string.Join(",",evidence.Rows.Select(r=>$"{r.TaskId}={r.Status}"))}；缺失/仍运行时不确认结束。");
        }
        Exception? lastError = null;
        var successfulQueries = 0;
        var onlineVehicles = stateCache.GetAll()
            .Where(robot => robot.Connection == ConnectionState.Online)
            .Select(robot => robot.VehicleId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clients = connections.Clients
            .OrderByDescending(client => onlineVehicles.Contains(client.Robot.VehicleId));
        foreach (var client in clients)
        {
            try
            {
                var task = await client.QueryTaskAsync(vendorTaskId, cancellationToken);
                successfulQueries++;
                if (task is null || task.Status == 404)
                {
                    continue;
                }

                return new AgvTransportTaskStatus(
                    vendorTaskId,
                    vendorTaskId,
                    MapTaskState(task.Status),
                    client.Robot.VehicleId,
                    DateTimeOffset.Now,
                    $"Robokit状态={task.Status}：{(task.Status == 3 ? "暂停" : task.Status == 7 ? "超时" : "任务状态")}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
            }
        }

        if (lastError is not null && successfulQueries == 0)
        {
            ExceptionDispatchInfo.Capture(lastError).Throw();
        }

        return null;
    }

    public async Task CancelTaskAsync(string vendorTaskId, CancellationToken cancellationToken)
    {
        EnsureCommandsEnabled();
        ArgumentException.ThrowIfNullOrWhiteSpace(vendorTaskId);
        var chain=await ReadChainAsync(vendorTaskId,cancellationToken);
        if(chain is { } evidence)
        {
            if(evidence.Rows.Count!=evidence.Ids.Count || evidence.Rows.Any(r=>r.Status is not (1 or 2 or 3 or 4 or 5 or 6 or 7)))
                throw new AgvPartialAcceptanceException("原3066任务链证据缺失或未知，不能推定取消范围；保持冻结并现场核对。");
            var target=evidence.Ids.Select(id=>evidence.Rows.Single(r=>r.TaskId==id)).FirstOrDefault(r=>r.Status is 1 or 2 or 3);
            if(target is not null) await evidence.Client.CancelNavigationAsync(target.TaskId,options.SmartCancel,cancellationToken);
            // Acceptance does not prove cascade completion; cancellation service rechecks the whole chain.
            return;
        }
        Exception? lastError = null;
        var successfulQueries = 0;
        var onlineVehicles = stateCache.GetAll()
            .Where(robot => robot.Connection == ConnectionState.Online)
            .Select(robot => robot.VehicleId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var client in connections.Clients.OrderByDescending(
                     item => onlineVehicles.Contains(item.Robot.VehicleId)))
        {
            SeerTaskSnapshot? task;
            try
            {
                task = await client.QueryTaskAsync(vendorTaskId, cancellationToken);
                successfulQueries++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
                continue;
            }

            if (task is null || task.Status == 404)
            {
                continue;
            }

            if (task.Status is 4 or 5 or 6 or 7)
            {
                return;
            }

            // 找到任务归属车辆后，取消失败必须直接上抛，不能继续扫描其他车辆。
            await client.CancelNavigationAsync(vendorTaskId, options.SmartCancel, cancellationToken);
            return;
        }

        if (lastError is not null && successfulQueries == 0)
        {
            ExceptionDispatchInfo.Capture(lastError).Throw();
        }
    }

    private void EnsureCommandsEnabled()
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("AGV通信未启用，禁止下发命令。");
        }

        if (!options.AllowCommands)
        {
            throw new InvalidOperationException("Agv:AllowCommands=false，禁止向现场AGV下发命令。");
        }

        var error = options.GetConfigurationError();
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
    }

    private void EnsureVehicleCanAcceptCommand(string vehicleId, IReadOnlyList<string> path)
    {
        if (path is null || path.Count < 2)
        {
            throw new InvalidOperationException("AGV运输路径至少需要起点和终点。");
        }

        if (!stateCache.TryGet(vehicleId, out var robot))
        {
            throw new InvalidOperationException($"AGV状态缓存中不存在车辆{vehicleId}。");
        }

        if (robot.Connection != ConnectionState.Online || robot.Quality != SignalQuality.Good)
        {
            throw new InvalidOperationException($"AGV {vehicleId}通信状态或数据质量不允许下发命令。");
        }

        if (robot.EmergencyStopped == true || robot.Alarms.Any(
                alarm => alarm.Severity >= AgvAlarmSeverity.Error))
        {
            throw new InvalidOperationException($"AGV {vehicleId}处于急停或故障状态。");
        }

        if (string.IsNullOrWhiteSpace(robot.CurrentStation) ||
            !string.Equals(robot.CurrentStation, path[0], StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"AGV {vehicleId}当前站点{robot.CurrentStation ?? "<未知>"}与路径起点{path[0]}不一致。");
        }
    }

    private static AgvTransportTaskState MapTaskState(int status) => status switch
    {
        1 => AgvTransportTaskState.Queued,
        2 => AgvTransportTaskState.GoingToDestination,
        3 => AgvTransportTaskState.Suspended,
        4 => AgvTransportTaskState.Completed,
        5 or 7 => AgvTransportTaskState.Failed,
        6 => AgvTransportTaskState.Cancelled,
        _ => AgvTransportTaskState.Unknown
    };

    private static string GetOperatingState(AgvRobotSnapshot robot)
    {
        if (robot.EmergencyStopped == true || robot.Alarms.Any(
                alarm => alarm.Severity >= AgvAlarmSeverity.Error))
        {
            return "Faulted";
        }

        if (robot.Tasks.Any(task => task.Status is 1 or 2 or 3))
        {
            return "Busy";
        }

        return robot.Connection == ConnectionState.Online ? "Idle" : "Offline";
    }
}
