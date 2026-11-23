using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Application.Runtime;

namespace AgvDispatch.Service;

public sealed class AgvCommandOutboxDispatcher(
    ILogger<AgvCommandOutboxDispatcher> logger,
    AgvCommandOutboxOptions options,
    IAgvCommandOutboxRepository outbox,
    IAgvGateway agvGateway,
    IMapExecutionGate executionGate,
    IExecutionEvidenceJournal? journal = null, ITaskHandshakeStore? handshakes = null, IDispatchTaskRepository? taskReader = null, HandshakeExecutionOptions? handshakeOptions = null,PersistentHandshakeExecutor? handshake = null,
    IRoadClosureService? roadClosures = null, ITrafficResourceManager? trafficResources = null,
    IActiveMapRuntime? activeMap = null, IDispatchPreSendSafetyService? preSendSafety = null,
    IDispatchPipelineMonitor? pipeline = null, TimeProvider? clock = null) : IsolatedBackgroundService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private TimeSpan BusinessDelay(int milliseconds) => _clock is SimulationTimeProvider simulation
        ? simulation.ScaleForBusiness(TimeSpan.FromMilliseconds(milliseconds))
        : TimeSpan.FromMilliseconds(milliseconds);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string,string> _blockedReasons=new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            pipeline?.ReportLayer("execution", "Disabled", "Outbox 发送未启用，原任务和命令保留。");
            logger.LogInformation("AGV命令Outbox发送服务未启用。");
            return;
        }

        var configurationError = options.GetConfigurationError();
        if (configurationError is not null)
        {
            throw new InvalidOperationException(configurationError);
        }

        logger.LogInformation("AGV命令Outbox发送服务启动，等待地图执行控制放行。");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var execution = await executionGate.EnterAsync(MapExecutionOperation.ExistingWork, stoppingToken);
                if (execution is not null)
                {
                    await RecoverExpiredClaimsAsync(stoppingToken);
                    await DispatchDueCommandsAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "AGV命令Outbox轮询失败。");
            }

            await Task.Delay(BusinessDelay(options.PollIntervalMs), stoppingToken);
        }
    }

    private async Task RecoverExpiredClaimsAsync(CancellationToken cancellationToken)
    {
        var count = await outbox.RecoverStaleClaimsAsync(
            _clock.GetLocalNow().AddMilliseconds(-options.SendingLeaseMs),
            cancellationToken);
        if (count > 0)
        {
            pipeline?.ReportLayer("execution", "Waiting", $"回收 {count} 条超时领取，沿用原命令和步骤编号核对厂家状态，不重新生成任务。", count);
            logger.LogWarning("已回收发送租约超时的AGV命令。Count={Count}", count);
        }
    }

    private async Task DispatchDueCommandsAsync(CancellationToken cancellationToken)
    {
        var connection = await agvGateway.GetConnectionStatusAsync(cancellationToken);
        // 车队部分降级时仍允许为健康车辆发送；Gateway会再次执行车辆级在线、
        // 数据质量、急停、报警和路径起点校验。
        if (connection.State is not (ConnectionState.Online or ConnectionState.Degraded))
        {
            return;
        }

        var due = await outbox.GetDueCommandsAsync(
            _clock.GetLocalNow(),
            options.BatchSize,
            cancellationToken);
        if (due.Count == 0) pipeline?.ReportLayer("execution", "Idle", "当前没有到期的持久化命令，等待原任务后续步骤。", 0);
        foreach (var candidate in due)
        {
            if (preSendSafety is not null && await preSendSafety.CheckPendingCommandAsync(candidate, cancellationToken) is { } rejected)
            {
                pipeline?.ReportLayer("execution", "Waiting", "原命令等待安全层放行：" + rejected, due.Count);
                continue;
            }
            // A degraded fleet can still serve healthy cars. Do not claim an
            // offline car's command: claiming records a possible send attempt,
            // which cannot later be treated as proof that nothing was sent.
            var vehicles = await agvGateway.GetVehiclesAsync(cancellationToken);
            var vehicle = vehicles.SingleOrDefault(item => item.VehicleId == candidate.VehicleId);
            if (vehicle?.Connection != ConnectionState.Online)
            {
                continue;
            }
            if (trafficResources is not null)
            {
                try
                {
                    var payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(candidate.PayloadJson, JsonOptions)
                        ?? throw new InvalidOperationException("待发送路径为空。");
                    if (payload.Path.Count < 2) throw new InvalidOperationException("待发送路径不完整。");
                    var map = activeMap?.Current;
                    if (await trafficResources.CheckStepReservationAsync(candidate.StepId, candidate.TaskId, candidate.VehicleId,
                        map?.MapId, map?.VersionId, payload.Path[0], payload.Path[^1], cancellationToken) is { } reason)
                        throw new InvalidOperationException(reason);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (!_blockedReasons.TryGetValue(candidate.CommandId, out var old) || old != ex.Message)
                    {
                        logger.LogWarning("发送前持久化路权核验未通过，保留原Outbox，不领取、不发送。CommandId={CommandId}, Reason={Reason}", candidate.CommandId, ex.Message);
                        _blockedReasons[candidate.CommandId] = ex.Message;
                    }
                    continue;
                }
            }
            if (roadClosures is not null && candidate.AttemptCount == 0)
            {
                try
                {
                    var payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(candidate.PayloadJson, JsonOptions)
                        ?? throw new InvalidOperationException("待发送路径为空。");
                    // Do not consume an attempt for a known closed route. The gateway
                    // independently holds the same road gate during the actual send.
                    using var check = await roadClosures.EnterRouteSendAsync(payload.Path, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (!_blockedReasons.TryGetValue(candidate.CommandId, out var previous) || previous != ex.Message)
                    {
                        logger.LogWarning("道路封锁核验未通过，保留原Outbox，不领取、不发送。CommandId={CommandId}, Reason={Reason}", candidate.CommandId, ex.Message);
                        _blockedReasons[candidate.CommandId] = ex.Message;
                    }
                    continue;
                }
            }
            if(handshakes!=null&&taskReader!=null)
            {
                var root=(await taskReader.GetStepsAsync(candidate.TaskId,cancellationToken)).FirstOrDefault(s=>s.StepType==DispatchTaskStepType.GoToPickup);
                var binding=root==null?null:await handshakes.GetAsync(candidate.TaskId,root.StepId,cancellationToken);
                if(binding!=null&&(handshakeOptions?.Enabled!=true||handshakeOptions.PlcContractVersion!=HandshakeExecutionOptions.Version||binding.ContractVersion!=HandshakeExecutionOptions.Version)||binding==null&&handshakeOptions?.Enabled==true)
                {
                    logger.LogDebug("命令契约与当前握手模式不一致，保留原Outbox，不领取、不发送。TaskId={TaskId}",candidate.TaskId);
                    continue;
                }
            }
            if(preSendSafety is null && handshakeOptions?.Enabled==true)
            {
                try
                {
                    if(handshake==null||handshakes==null||taskReader==null)throw new InvalidOperationException("28阶段发送缺少持久化执行及契约核验依赖。");
                    var payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(candidate.PayloadJson, JsonOptions);
                    await handshake.ValidatePendingTransportAsync(candidate.TaskId,candidate.StepId,candidate.VehicleId,
                        payload?.WindowIndex > 0,cancellationToken);
                    _blockedReasons.Remove(candidate.CommandId);
                }
                catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested){throw;}
                catch(Exception ex)
                {
                    if(!_blockedReasons.TryGetValue(candidate.CommandId,out var previous)||previous!=ex.Message)
                    {logger.LogWarning("28阶段原命令暂不领取/发送，等待当前条件成立。CommandId={CommandId}, Reason={Reason}",candidate.CommandId,ex.Message);_blockedReasons[candidate.CommandId]=ex.Message;}
                    continue;
                }
            }
            var command = await outbox.TryClaimAsync(
                candidate.CommandId,
                _clock.GetLocalNow(),
                cancellationToken);
            if (command is null)
            {
                continue;
            }

            await SendAsync(command, cancellationToken);
        }
    }

    private async Task SendAsync(
        AgvCommandOutboxItem command,
        CancellationToken cancellationToken)
    {
        try
        {
            if (command.CommandType != AgvCommandType.Transport)
            {
                throw new NotSupportedException($"尚未支持AGV命令类型{command.CommandType}。");
            }

            var payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(
                command.PayloadJson,
                JsonOptions) ?? throw new InvalidOperationException("AGV命令负载为空。");

            // StepId是跨重启不变的幂等键。适配器重试创建前必须按该编号查询已有厂家任务。
            if (journal != null) await journal.RecordAsync(command.TaskId, command.StepId, "AgvSendIntent", new { command, payload }, cancellationToken);
            var vendorCommandId = await agvGateway.CreateTransportTaskAsync(
                payload.BusinessTaskId,
                payload.VehicleId,
                payload.Path,
                payload.Action,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(vendorCommandId))
            {
                throw new InvalidOperationException("AGV接口返回了空的厂家任务编号。");
            }

            // The vehicle has accepted the command. Acknowledgement evidence is best effort: a journal
            // failure must not leave an accepted command in RetryScheduled.
            if (journal != null)
            {
                try
                {
                    await journal.RecordAsync(command.TaskId, command.StepId, "AgvSendAcknowledged", new { command.CommandId, vendorCommandId }, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception evidenceError)
                {
                    logger.LogError(evidenceError, "AGV命令已被车端接受，但确认证据写入失败，仍继续落库为已发送。CommandId={CommandId}, StepId={StepId}", command.CommandId, command.StepId);
                }
            }
            await outbox.MarkSentAsync(command.CommandId, vendorCommandId, cancellationToken);
            pipeline?.ReportLayer("execution", "Running", "厂家已确认原命令；执行、反馈、路权交接及恢复继续沿用同一任务和步骤。", 1);
            logger.LogInformation(
                "AGV命令发送成功。CommandId={CommandId}, StepId={StepId}, VendorCommandId={VendorCommandId}, Attempt={Attempt}",
                command.CommandId,
                command.StepId,
                vendorCommandId,
                command.AttemptCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgvPreSendDeferredException exception)
        {
            var delay = GetRetryDelay(command.AttemptCount);
            await outbox.DeferBeforeSendAsync(
                command.CommandId,
                exception.Message,
                _clock.GetLocalNow().Add(delay),
                cancellationToken);
            pipeline?.ReportLayer(
                "execution",
                "Waiting",
                "车辆在本机发送前复核中暂时不可用；未向厂家发送命令，恢复健康后沿用原命令编号重试：" +
                exception.Message,
                1);
            logger.LogWarning(
                exception,
                "AGV命令在本地发送前复核失败，已回滚尝试次数。CommandId={CommandId}, StepId={StepId}, RetryAfterMs={RetryAfterMs}",
                command.CommandId,
                command.StepId,
                delay.TotalMilliseconds);
        }
        catch (Exception exception)
        {
            var delay = GetRetryDelay(command.AttemptCount);
            var terminalSendResult = exception is AgvPartialAcceptanceException or AgvCommandRejectedException;
            await outbox.ScheduleRetryAsync(
                command.CommandId,
                exception.Message,
                _clock.GetLocalNow().Add(delay),
                terminalSendResult ? 1 : options.MaximumAttempts,
                cancellationToken);
            pipeline?.ReportLayer(
                "execution",
                terminalSendResult ? "Failed" : "Waiting",
                (exception is AgvCommandRejectedException
                    ? "车端明确拒绝控制命令，原命令永久停止重试，须排除控制权/现场条件后新建任务："
                    : exception is AgvPartialAcceptanceException
                        ? "发送结果不完整或原任务链不可证明，原命令永久停止自动重试，保留路权等待恢复核对："
                        : "发送回执失败或不确定，保留原命令并持久化重试计划，不重建任务：") +
                exception.Message,
                1);
            logger.LogWarning(
                exception,
                "AGV命令发送失败，已持久化重试计划。CommandId={CommandId}, StepId={StepId}, Attempt={Attempt}, RetryAfterMs={RetryAfterMs}",
                command.CommandId,
                command.StepId,
                command.AttemptCount,
                delay.TotalMilliseconds);
        }
    }

    private TimeSpan GetRetryDelay(int attemptCount)
    {
        var exponent = Math.Min(Math.Max(attemptCount - 1, 0), 20);
        var delay = options.RetryBaseDelayMs * Math.Pow(2, exponent);
        return TimeSpan.FromMilliseconds(Math.Min(delay, options.MaximumRetryDelayMs));
    }
}
