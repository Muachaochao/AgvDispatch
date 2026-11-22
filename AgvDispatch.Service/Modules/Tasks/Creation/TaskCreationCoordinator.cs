using System.Collections.Concurrent;
using System.Threading.Channels;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Application.Runtime;

namespace AgvDispatch.Service;

public sealed class TaskCreationCoordinator(
    ILogger<TaskCreationCoordinator> logger,
    DispatchTaskCreationOptions options,
    IStationStateService stationStates,
    IDispatchTaskRepository tasks,
    IPlcGateway plcGateway,
    IMapExecutionGate executionGate,
    TaskParameterRuntime parameters,
    IExecutionEvidenceJournal? journal = null, BusinessRunOwnership? businessRun = null,FleetTestOwnership? fleetOwnership=null,
    IActiveMapRuntime? activeMap = null, DispatchOrchestratorOptions? dispatchOptions = null,
    IDispatchPipelineMonitor? pipeline = null, TimeProvider? clock = null) : IsolatedBackgroundService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private TimeSpan BusinessDelay(int milliseconds) => _clock is SimulationTimeProvider simulation
        ? simulation.ScaleForBusiness(TimeSpan.FromMilliseconds(milliseconds))
        : TimeSpan.FromMilliseconds(milliseconds);
    private readonly Channel<string> _eligibleStations = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, byte> _queuedStations =
        new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            pipeline?.ReportLayer("queue", "Disabled", "PLC 自动接单未启用，持久化原任务继续保留。");
            logger.LogInformation("任务自动创建服务未启用。");
            return;
        }

        if (options.AcknowledgementRetryIntervalMs < 500)
        {
            throw new InvalidOperationException(
                "TaskCreation:AcknowledgementRetryIntervalMs不能小于500ms。");
        }

        if (string.IsNullOrWhiteSpace(options.DumperStationId))
        {
            throw new InvalidOperationException("TaskCreation:DumperStationId不能为空。");
        }
        if (options.DefaultPriority is < 0 or > 100 || options.StationPriorities.Any(pair =>
            string.IsNullOrWhiteSpace(pair.Key) || pair.Value is < 0 or > 100))
            throw new InvalidOperationException("TaskCreation工位优先级须为0至100，且工位编号不能为空。");

        if (options.AutomaticCreationEnabled)
            stationStates.StationStateChanged += OnStationStateChanged;
        try
        {
            foreach (var station in stationStates.GetAll().Where(state => options.AutomaticCreationEnabled && state.CanCreateTask))
            {
                Enqueue(station.StationId);
            }

            logger.LogInformation(
                "任务自动创建服务启动。PLC应答重试周期={RetryIntervalMs}ms",
                options.AcknowledgementRetryIntervalMs);

            await ProcessPendingAcknowledgementsAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var stationTask = _eligibleStations.Reader.WaitToReadAsync(stoppingToken).AsTask();
                var retryTask = Task.Delay(BusinessDelay(options.AcknowledgementRetryIntervalMs), stoppingToken);
                var completed = await Task.WhenAny(stationTask, retryTask);

                if (completed == retryTask)
                {
                    if (options.AutomaticCreationEnabled && executionGate.CanStartNewWork)
                        foreach (var station in stationStates.GetAll().Where(state => state.CanCreateTask)) Enqueue(station.StationId);
                    await ProcessPendingAcknowledgementsAsync(stoppingToken);
                    continue;
                }

                if (!await stationTask)
                {
                    break;
                }

                while (_eligibleStations.Reader.TryRead(out var stationId))
                {
                    try
                    {
                        await CreateAndAcknowledgeAsync(stationId, stoppingToken);
                    }
                    finally
                    {
                        _queuedStations.TryRemove(stationId, out _);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停止。
        }
        finally
        {
            stationStates.StationStateChanged -= OnStationStateChanged;
            _eligibleStations.Writer.TryComplete();
        }
    }

    private async Task CreateAndAcknowledgeAsync(
        string stationId,
        CancellationToken cancellationToken)
    {
        using var execution = await executionGate.EnterAsync(MapExecutionOperation.NewWork, cancellationToken);
        if (execution is null) return;
        if (dispatchOptions?.EngineeringMapError(activeMap?.Current) is not null) return;
        if (businessRun?.Active == true||fleetOwnership?.Active==true) return;
        if (parameters.Revision != "initial" && !parameters.Capture().Pickups.Any(pickup =>
            string.Equals(pickup.StationId, stationId, StringComparison.OrdinalIgnoreCase))) return;
        if (!stationStates.TryGet(stationId, out var state) || !state.CanCreateTask)
        {
            return;
        }

        try
        {
            var result = await tasks.TryCreateAsync(
                stationId,
                options.DumperStationId,
                options.PriorityFor(stationId),
                cancellationToken);
            if (result.Created)
            {
                pipeline?.ReportLayer("queue", "Waiting", $"工位 {stationId} 呼叫已持久化，保留基础优先级与呼叫时间，等待 PLC 应答。", 1);
                logger.LogInformation(
                    "已创建调度任务。TaskId={TaskId}, StationId={StationId}, Priority={Priority}",
                    result.Task.TaskId,
                    stationId,
                    options.PriorityFor(stationId));
            }
            else
            {
                pipeline?.ReportLayer("queue", "Ready", $"工位 {stationId} 已有活动任务，重复呼叫继续使用原任务和呼叫时间。", 1);
                logger.LogInformation(
                    "跳过重复任务创建。StationId={StationId}, ExistingTaskId={TaskId}, Reason={Reason}",
                    stationId,
                    result.Task.TaskId,
                    result.Reason);
            }

            if (result.Task.Stage == DispatchTaskStage.Created)
            {
                await AcknowledgeAsync(result.Task, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "自动创建工位任务失败。StationId={StationId}", stationId);
        }
    }

    private async Task ProcessPendingAcknowledgementsAsync(CancellationToken cancellationToken)
    {
        using var execution = await executionGate.EnterAsync(MapExecutionOperation.ExistingWork, cancellationToken);
        if (execution is null) return;
        IReadOnlyList<ActiveDispatchTask> pending;
        try
        {
            pending = await tasks.GetPendingAcknowledgementsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "读取待PLC应答任务失败。");
            return;
        }

        foreach (var task in pending)
        {
            await AcknowledgeAsync(task, cancellationToken);
        }
    }

    private async Task AcknowledgeAsync(
        ActiveDispatchTask task,
        CancellationToken cancellationToken)
    {
        using var evidenceScope = journal?.BeginScope(task.TaskId, "PLC-REQUEST-ACK");
        try
        {
            await plcGateway.WriteSignalAsync(
                task.StationId,
                "FullBoxRequestAcknowledged",
                true,
                cancellationToken);
            await tasks.MarkAcknowledgedAsync(task.TaskId, cancellationToken);
            pipeline?.ReportLayer("queue", "Ready", $"工位 {task.StationId} PLC 应答完成，原任务进入候选窗口。", 1);
            logger.LogInformation(
                "PLC满箱呼叫已应答，任务进入待派车。TaskId={TaskId}, StationId={StationId}",
                task.TaskId,
                task.StationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                await tasks.RecordAcknowledgementFailureAsync(
                    task.TaskId,
                    exception.Message,
                    cancellationToken);
            }
            catch (Exception persistenceException)
            {
                logger.LogError(
                    persistenceException,
                    "记录PLC应答失败信息时发生异常。TaskId={TaskId}",
                    task.TaskId);
            }

            logger.LogWarning(
                exception,
                "PLC满箱呼叫应答失败，将自动重试。TaskId={TaskId}, StationId={StationId}",
                task.TaskId,
                task.StationId);
        }
    }

    private void OnStationStateChanged(StationStateChanged change)
    {
        if (change.Current.CanCreateTask && change.Previous?.CanCreateTask != true)
        {
            Enqueue(change.StationId);
        }
    }

    private void Enqueue(string stationId)
    {
        if (_queuedStations.TryAdd(stationId, 0) &&
            !_eligibleStations.Writer.TryWrite(stationId))
        {
            _queuedStations.TryRemove(stationId, out _);
        }
    }
}
