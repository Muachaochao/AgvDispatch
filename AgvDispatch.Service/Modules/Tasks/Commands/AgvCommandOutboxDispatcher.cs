using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed class AgvCommandOutboxDispatcher(
    ILogger<AgvCommandOutboxDispatcher> logger,
    AgvCommandOutboxOptions options,
    IAgvCommandOutboxRepository outbox,
    IAgvGateway agvGateway,
    IMapExecutionGate executionGate,
    IExecutionEvidenceJournal? journal = null, ITaskHandshakeStore? handshakes = null, IDispatchTaskRepository? taskReader = null, HandshakeExecutionOptions? handshakeOptions = null) : IsolatedBackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
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

            await Task.Delay(options.PollIntervalMs, stoppingToken);
        }
    }

    private async Task RecoverExpiredClaimsAsync(CancellationToken cancellationToken)
    {
        var count = await outbox.RecoverStaleClaimsAsync(
            DateTimeOffset.Now.AddMilliseconds(-options.SendingLeaseMs),
            cancellationToken);
        if (count > 0)
        {
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
            DateTimeOffset.Now,
            options.BatchSize,
            cancellationToken);
        foreach (var candidate in due)
        {
            if(handshakes!=null&&taskReader!=null)
            {
                var root=(await taskReader.GetStepsAsync(candidate.TaskId,cancellationToken)).FirstOrDefault(s=>s.StepType==DispatchTaskStepType.GoToPickup);
                var binding=root==null?null:await handshakes.GetAsync(candidate.TaskId,root.StepId,cancellationToken);
                if(binding!=null&&(handshakeOptions?.Enabled!=true||handshakeOptions.PlcContractVersion!=binding.ContractVersion)||binding==null&&handshakeOptions?.Enabled==true)
                {
                    logger.LogDebug("命令契约与当前握手模式不一致，保留原Outbox，不领取、不发送。TaskId={TaskId}",candidate.TaskId);
                    continue;
                }
            }
            var command = await outbox.TryClaimAsync(
                candidate.CommandId,
                DateTimeOffset.Now,
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

            if (journal != null) await journal.RecordAsync(command.TaskId, command.StepId, "AgvSendAcknowledged", new { command.CommandId, vendorCommandId }, cancellationToken);
            await outbox.MarkSentAsync(command.CommandId, vendorCommandId, cancellationToken);
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
        catch (Exception exception)
        {
            var delay = GetRetryDelay(command.AttemptCount);
            await outbox.ScheduleRetryAsync(
                command.CommandId,
                exception.Message,
                DateTimeOffset.Now.Add(delay),
                exception is AgvPartialAcceptanceException ? 1 : options.MaximumAttempts,
                cancellationToken);
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
