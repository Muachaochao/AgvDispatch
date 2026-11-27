using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using SqlSugar;

namespace AgvDispatch.Service;

/// <summary>
/// Converts a Robokit status-7 transport timeout into an explicit, auditable
/// business hold. Route assessment is read-only until an operator explicitly
/// confirms one replacement navigation; it never sends a cancellation.
/// </summary>
public sealed class TransportTimeoutRecoveryService(
    ISqlSugarClient database,
    IAgvStateCache vehicles,
    IActiveMapRuntime activeMap,
    IAgvTransportChainLookup chains,
    ITrafficResourceManager traffic,
    IRoadClosureService? roadClosures,
    MapOptions mapOptions,
    TimeProvider? clock = null,
    IAgvGateway? gateway = null,
    IMapExecutionGate? executionGate = null,
    PersistentHandshakeExecutor? handshake = null,
    ITaskHandshakeStore? handshakes = null,
    HandshakeExecutionOptions? handshakeOptions = null,
    IPlcSignalCache? signals = null,
    DispatchTaskCreationOptions? creation = null,
    DispatchOrchestratorOptions? dispatch = null,
    ILogger<TransportTimeoutRecoveryService>? logger = null,
    IRoutePlanner? routePlanner = null) : ITransportTimeoutRecoveryService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _execute = new(1, 1);

    public async Task<TransportTimeoutRecoveryAssessment?> CaptureAsync(
        DispatchTaskWorkItem task,
        AgvTransportTaskStatus status,
        CancellationToken cancellationToken)
    {
        if (status.State != AgvTransportTaskState.Failed || status.VendorStatusCode != 7 ||
            string.IsNullOrWhiteSpace(task.VehicleId) ||
            !string.Equals(task.VehicleId, status.VehicleId, StringComparison.OrdinalIgnoreCase))
            return null;

        var step = await database.Queryable<DispatchTaskStepEntity>()
            .Where(row => row.TaskId == task.TaskId &&
                          row.VendorTaskId == status.VendorTaskId &&
                          row.Status == (int)DispatchTaskStepStatus.Executing)
            .FirstAsync(cancellationToken);
        if (step is null || !TransportStep(step.StepType)) return null;

        TransportTimeoutRecoveryAssessment assessment;
        try
        {
            var originalPath = await ResolveFullOriginalPathAsync(task.TaskId, step.StepId,
                task.VehicleId, step.DestinationSite ?? "", status.VendorTaskId, cancellationToken);

            assessment = await AssessAsync(task.TaskId, (DispatchTaskStepType)step.StepType,
                task.VehicleId, status.VendorTaskId, step.DestinationSite ?? "", originalPath,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            assessment = new(TransportTimeoutRecoveryOutcome.ManualIntervention,
                (DispatchTaskStepType)step.StepType, task.VehicleId, status.VendorTaskId,
                null, step.DestinationSite ?? "", [], [],
                Prefix((DispatchTaskStepType)step.StepType) +
                "超时已确认，但恢复证据评估失败：" + error.Message + "；任务已冻结，禁止自动改道或重发。");
        }
        var message = assessment.Message;
        var now = (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetLocalNow().DateTime;
        var captured = false;
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var currentStep = await database.Queryable<DispatchTaskStepEntity>()
                .Where(row => row.StepId == step.StepId &&
                              row.TaskId == task.TaskId &&
                              row.VendorTaskId == status.VendorTaskId &&
                              row.Status == (int)DispatchTaskStepStatus.Executing)
                .FirstAsync(cancellationToken);
            var currentTask = await database.Queryable<DispatchTaskEntity>()
                .Where(row => row.TaskId == task.TaskId)
                .FirstAsync(cancellationToken);
            if (currentStep is null || currentTask is null) return;

            currentStep.Status = (int)DispatchTaskStepStatus.ManualIntervention;
            currentStep.ErrorMessage = Truncate(message);
            currentStep.UpdatedAt = now;
            currentTask.Stage = (int)DispatchTaskStage.Faulted;
            currentTask.ErrorMessage = Truncate(message);
            currentTask.UpdatedAt = now;
            await database.Updateable(currentStep).ExecuteCommandAsync(cancellationToken);
            await database.Updateable(currentTask).ExecuteCommandAsync(cancellationToken);

            var map = activeMap.Current;
            var mapId = map?.MapId ?? "";
            var mapVersionId = map?.VersionId ?? "";
            var definitionHash = map is null ? "" : EngineeringRuntimeIdentity.MapHash(map.Definition);
            var previewHash = Hash(new
            {
                assessment.StepType,
                assessment.VehicleId,
                assessment.OriginalVendorTaskId,
                assessment.CurrentStation,
                assessment.DestinationSite,
                assessment.OriginalPath,
                assessment.CandidatePath,
                MapId = mapId,
                MapVersionId = mapVersionId,
                DefinitionHash = definitionHash
            });
            var existing = await database.Queryable<TransportTimeoutRecoveryEntity>()
                .Where(row => row.TaskId == task.TaskId && row.StepId == step.StepId)
                .FirstAsync(cancellationToken);
            if (existing is null)
            {
                await database.Insertable(new TransportTimeoutRecoveryEntity
                {
                    RecoveryId = Guid.NewGuid().ToString("N"),
                    TaskId = task.TaskId,
                    StepId = step.StepId,
                    VehicleId = task.VehicleId,
                    StepType = (int)assessment.StepType,
                    State = assessment.Outcome == TransportTimeoutRecoveryOutcome.ReadyForControlledDetour
                        ? "ReadyForControlledDetour" : "ManualIntervention",
                    Outcome = assessment.Outcome.ToString(),
                    OriginalVendorTaskId = status.VendorTaskId,
                    CurrentStation = assessment.CurrentStation,
                    DestinationSite = assessment.DestinationSite,
                    OriginalPathJson = JsonSerializer.Serialize(assessment.OriginalPath, Json),
                    CandidatePathJson = JsonSerializer.Serialize(assessment.CandidatePath, Json),
                    MapId = mapId,
                    MapVersionId = mapVersionId,
                    DefinitionHash = definitionHash,
                    PreviewHash = previewHash,
                    LastError = Truncate(message),
                    Revision = 1,
                    CreatedAt = now,
                    UpdatedAt = now
                }).ExecuteCommandAsync(cancellationToken);
            }
            else if (existing.SendAttemptedAt is null && string.IsNullOrWhiteSpace(existing.NewVendorTaskId))
            {
                existing.State = assessment.Outcome == TransportTimeoutRecoveryOutcome.ReadyForControlledDetour
                    ? "ReadyForControlledDetour" : "ManualIntervention";
                existing.Outcome = assessment.Outcome.ToString();
                existing.CurrentStation = assessment.CurrentStation;
                existing.OriginalPathJson = JsonSerializer.Serialize(assessment.OriginalPath, Json);
                existing.CandidatePathJson = JsonSerializer.Serialize(assessment.CandidatePath, Json);
                existing.MapId = mapId;
                existing.MapVersionId = mapVersionId;
                existing.DefinitionHash = definitionHash;
                existing.PreviewHash = previewHash;
                existing.LastError = Truncate(message);
                existing.Revision++;
                existing.UpdatedAt = now;
                await database.Updateable(existing).ExecuteCommandAsync(cancellationToken);
            }

            if (!await database.Queryable<ExecutionEvidenceEntity>().AnyAsync(row =>
                    row.TaskId == task.TaskId &&
                    row.StepId == step.StepId &&
                    row.Kind == "TransportTimeoutRecoveryCaptured", cancellationToken))
            {
                await database.Insertable(new ExecutionEvidenceEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SessionId = "",
                    TaskId = task.TaskId,
                    StepId = step.StepId,
                    Operator = "service",
                    Kind = "TransportTimeoutRecoveryCaptured",
                    Reason = "Robokit导航任务状态7：已冻结原任务并保留载荷、路权和PLC责任。",
                    Evidence = JsonSerializer.Serialize(new
                    {
                        status,
                        assessment,
                        MapId = activeMap.Current?.MapId,
                        MapVersionId = activeMap.Current?.VersionId
                    }),
                    OccurredAt = now
                }).ExecuteCommandAsync(cancellationToken);
            }
            captured = true;
        });
        if (!transaction.IsSuccess)
            throw new InvalidOperationException("保存运输超时恢复状态失败，原任务保持未确认。", transaction.ErrorException);
        return captured ? assessment : null;
    }

    private async Task<string[]> ResolveFullOriginalPathAsync(string taskId, string stepId,
        string vehicleId, string destination, string vendorTaskId, CancellationToken cancellationToken)
    {
        var commands = await database.Queryable<AgvCommandOutboxEntity>()
            .Where(item => item.TaskId == taskId && item.StepId == stepId &&
                           item.CommandType == (int)AgvCommandType.Transport)
            .OrderByDescending(item => item.CreatedAt).ToListAsync(cancellationToken);
        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command.ExecutionRouteSnapshotJson)) continue;
            var snapshot = TransportRouteSnapshots.Read(command);
            if (!string.Equals(snapshot.VehicleId, vehicleId, StringComparison.OrdinalIgnoreCase) ||
                snapshot.AgvSites.Length is < 2 or > 2048 || snapshot.AgvSites.Any(string.IsNullOrWhiteSpace) ||
                !MapRouteSiteMapper.IsSameNode(activeMap.Current?.Definition, snapshot.AgvSites[^1], destination))
                throw new InvalidOperationException("完整业务路线快照与原车或目的站不一致，禁止推测恢复路线。");
            return snapshot.AgvSites;
        }
        var chain = await chains.FindAsync(vendorTaskId, cancellationToken);
        return chain is not null && string.Equals(chain.VehicleId, vehicleId, StringComparison.OrdinalIgnoreCase) &&
            chain.Path.Count >= 2 && MapRouteSiteMapper.IsSameNode(activeMap.Current?.Definition, chain.Path[^1], destination)
            ? chain.Path.ToArray() : [];
    }

    public async Task<TransportTimeoutRecoverySnapshot?> GetAsync(string taskId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return null;
        var row = await database.Queryable<TransportTimeoutRecoveryEntity>()
            .Where(item => item.TaskId == taskId)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstAsync(cancellationToken);
        return row is null ? null : Snapshot(row);
    }

    public async Task ReassessPendingAsync(CancellationToken cancellationToken)
    {
        await _execute.WaitAsync(cancellationToken);
        try
        {
            var rows = await database.Queryable<TransportTimeoutRecoveryEntity>()
                .Where(item => item.SendAttemptedAt == null && item.NewVendorTaskId == null)
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var task = await database.Queryable<DispatchTaskEntity>()
                    .Where(item => item.TaskId == row.TaskId).FirstAsync(cancellationToken);
                var step = await database.Queryable<DispatchTaskStepEntity>()
                    .Where(item => item.StepId == row.StepId).FirstAsync(cancellationToken);
                if (task is null || step is null ||
                    task.Stage != (int)DispatchTaskStage.Faulted ||
                    step.Status != (int)DispatchTaskStepStatus.ManualIntervention ||
                    !string.Equals(step.VendorTaskId, row.OriginalVendorTaskId, StringComparison.Ordinal))
                    continue;

                var originalPath = JsonSerializer.Deserialize<string[]>(row.OriginalPathJson, Json) ?? [];
                // Missing route evidence must not stop communication/obstacle/
                // position reassessment. It still prevents sending in AssessAsync.
                if (originalPath.Length < 2)
                {
                    try
                    {
                        originalPath = await ResolveFullOriginalPathAsync(row.TaskId, row.StepId,
                            row.VehicleId, row.DestinationSite, row.OriginalVendorTaskId, cancellationToken);
                    }
                    catch (Exception error) when (error is InvalidOperationException or InvalidDataException or JsonException)
                    {
                        logger?.LogWarning(error, "超时恢复原路线证据无效，保持人工处置：{TaskId}", row.TaskId);
                    }
                    row.OriginalPathJson = JsonSerializer.Serialize(originalPath, Json);
                }
                var assessment = await AssessAsync(row.TaskId, (DispatchTaskStepType)row.StepType,
                    row.VehicleId, row.OriginalVendorTaskId, row.DestinationSite, originalPath,
                    cancellationToken);
                var candidate = JsonSerializer.Serialize(assessment.CandidatePath, Json);
                var nextState = assessment.Outcome == TransportTimeoutRecoveryOutcome.ReadyForControlledDetour
                    ? "ReadyForControlledDetour" : "ManualIntervention";
                if (row.State == nextState &&
                    row.Outcome == assessment.Outcome.ToString() &&
                    MapRouteSiteMapper.IsSameNode(activeMap.Current?.Definition,
                        row.CurrentStation, assessment.CurrentStation) &&
                    row.CandidatePathJson == candidate &&
                    row.LastError == Truncate(assessment.Message))
                    continue;

                var previous = row.State;
                var map = activeMap.Current;
                row.State = nextState;
                row.Outcome = assessment.Outcome.ToString();
                row.CurrentStation = assessment.CurrentStation;
                row.CandidatePathJson = candidate;
                row.MapId = map?.MapId ?? "";
                row.MapVersionId = map?.VersionId ?? "";
                row.DefinitionHash = map is null ? "" : EngineeringRuntimeIdentity.MapHash(map.Definition);
                row.PreviewHash = Hash(new
                {
                    assessment.StepType,
                    assessment.VehicleId,
                    assessment.OriginalVendorTaskId,
                    assessment.CurrentStation,
                    assessment.DestinationSite,
                    assessment.OriginalPath,
                    assessment.CandidatePath,
                    row.MapId,
                    row.MapVersionId,
                    row.DefinitionHash
                });
                row.LastError = Truncate(assessment.Message);
                row.Revision++;
                row.UpdatedAt = NowLocal;
                await database.Updateable(row).ExecuteCommandAsync(cancellationToken);
                await database.Insertable(new ExecutionEvidenceEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SessionId = "",
                    TaskId = row.TaskId,
                    StepId = row.StepId,
                    Operator = "service:transport-timeout-recovery",
                    Kind = "TransportTimeoutRecoveryReassessed",
                    Reason = "根据新鲜车辆位置、障碍和路线证据重新评估运输超时恢复候选。",
                    Evidence = JsonSerializer.Serialize(new
                    {
                        PreviousState = previous,
                        NextState = row.State,
                        row.Outcome,
                        row.CurrentStation,
                        row.CandidatePathJson,
                        row.Revision,
                        Message = assessment.Message
                    }, Json),
                    OccurredAt = row.UpdatedAt
                }).ExecuteCommandAsync(cancellationToken);
                logger?.LogInformation(
                    "运输超时恢复候选已重新评估。TaskId={TaskId}, State={State}, Outcome={Outcome}, Station={Station}",
                    row.TaskId, row.State, row.Outcome, row.CurrentStation);
            }
        }
        finally
        {
            _execute.Release();
        }
    }

    public async Task<TransportTimeoutRecoveryResult> ExecuteAsync(
        TrafficUserContext user,
        TransportTimeoutRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        if (!user.Has(TrafficPermission.RecoverExecution))
            return new(false, "需要恢复处置权限。");
        if (gateway is null || executionGate is null || dispatch is null)
            return new(false, "超时恢复发送依赖未配置，保持原任务。");
        if (string.IsNullOrWhiteSpace(request.TaskId) || !request.ExecuteConfirmed ||
            !request.VehicleAndLocationConfirmed || !request.LoadAndOwnershipConfirmed ||
            !request.PlcConditionsConfirmed || request.Reason.Trim().Length is < 1 or > 1000)
            return new(false, "必须确认车辆位置、载荷归属和PLC条件，填写非空现场依据，并明确执行。");

        await _execute.WaitAsync(cancellationToken);
        try
        {
            if (executionGate.Mode != MapExecutionMode.Running)
                return new(false, "地图切换或启动恢复门禁未处于运行状态，禁止发送恢复导航。");
            // The original navigation is already terminal; this is existing-work
            // recovery. Keep the gate shared so a slow vendor/PLC check for this
            // vehicle cannot serialize every other vehicle's work. Map switching
            // still waits for this lease and the final binding is conditional.
            using var execution = await executionGate.EnterAsync(
                MapExecutionOperation.ExistingWork, cancellationToken);
            if (execution is null)
                return new(false, "地图执行门禁未放行，禁止发送恢复导航。");
            var row = await database.Queryable<TransportTimeoutRecoveryEntity>()
                .Where(item => item.TaskId == request.TaskId)
                .OrderByDescending(item => item.UpdatedAt)
                .FirstAsync(cancellationToken);
            if (row is null) return new(false, "没有已捕获的运输超时恢复记录。");
            if (row.Revision != request.ExpectedRevision || row.State != "ReadyForControlledDetour")
                return new(false, "恢复候选已变化或不是可执行状态，请重新读取。", Snapshot(row));
            if (row.SendAttemptedAt is not null || !string.IsNullOrWhiteSpace(row.NewVendorTaskId))
                return await ReconcileExistingAsync(row, cancellationToken);

            var task = await database.Queryable<DispatchTaskEntity>().Where(item => item.TaskId == row.TaskId).FirstAsync(cancellationToken);
            var step = await database.Queryable<DispatchTaskStepEntity>().Where(item => item.StepId == row.StepId).FirstAsync(cancellationToken);
            if (task is null || step is null || task.Stage != (int)DispatchTaskStage.Faulted ||
                step.Status != (int)DispatchTaskStepStatus.ManualIntervention ||
                step.VendorTaskId != row.OriginalVendorTaskId)
                return new(false, "原任务、步骤或厂家编号已经变化，禁止发送恢复导航。", Snapshot(row));

            var originalPath = JsonSerializer.Deserialize<string[]>(row.OriginalPathJson, Json) ?? [];
            var storedCandidate = JsonSerializer.Deserialize<string[]>(row.CandidatePathJson, Json) ?? [];
            var map = activeMap.Current;
            if (map is null || map.MapId != row.MapId || map.VersionId != row.MapVersionId ||
                !string.Equals(EngineeringRuntimeIdentity.MapHash(map.Definition), row.DefinitionHash, StringComparison.OrdinalIgnoreCase))
                return new(false, "运行地图或版本已变化，恢复候选失效。", Snapshot(row));

            var current = await AssessAsync(row.TaskId, (DispatchTaskStepType)row.StepType,
                row.VehicleId, row.OriginalVendorTaskId, row.DestinationSite, originalPath,
                cancellationToken);
            if (current.Outcome != TransportTimeoutRecoveryOutcome.ReadyForControlledDetour ||
                !MapRouteSiteMapper.IsSameSequence(map.Definition,
                    current.CandidatePath, storedCandidate))
                return new(false, "车辆位置、障碍或道路状态已变化，请重新读取恢复候选：" + current.Message, Snapshot(row));

            var vendorWindow = await chains.FindAsync(row.OriginalVendorTaskId, cancellationToken);
            if (vendorWindow is null || vendorWindow.Path.Count < 2 ||
                !string.Equals(vendorWindow.VehicleId, row.VehicleId, StringComparison.OrdinalIgnoreCase))
                return new(false, "原导航窗口发送证据缺失，禁止按完整业务路线猜造车端任务编号。", Snapshot(row));
            var original = await gateway.GetTransportTaskStatusAsync(row.OriginalVendorTaskId, row.VehicleId,
                vendorWindow.Path, cancellationToken);
            if (original is null || original.State != AgvTransportTaskState.Failed ||
                !string.Equals(original.VehicleId, row.VehicleId, StringComparison.OrdinalIgnoreCase))
                return new(false, "原厂家任务链未确认全部超时终结，禁止发送替代导航。", Snapshot(row));

            if (!await ConfirmStoppedAsync(row.VehicleId, row.CurrentStation, cancellationToken))
                return new(false, "车辆未连续停稳、定位不新鲜或与候选起点不一致。", Snapshot(row));

            var route = await PlanReplacementAsync(map, row, originalPath, cancellationToken);
            if (route is null || !MapRouteSiteMapper.IsSameSequence(map.Definition,
                    route.Nodes, storedCandidate))
                return new(false, "新路线资源、道路或地图复核未通过，请重新读取。", Snapshot(row));
            // PLC and handshake evidence is checked before any route lease is taken.
            // A failed check must not leave this task holding an extra candidate route.
            var stepWork = new DispatchTaskStepWorkItem(task.TaskId, task.StationId, row.VehicleId,
                (DispatchTaskStage)task.Stage, step.StepId, step.Sequence,
                (DispatchTaskStepType)step.StepType, (DispatchTaskStepStatus)step.Status,
                step.SourceSite, step.DestinationSite, new DateTimeOffset(step.UpdatedAt));
            if (await ValidateRecoveryPlcAsync(stepWork, step, cancellationToken) is { } plcError)
                return new(false, plcError, Snapshot(row));

            var reservation = await traffic.TryAcquireAsync(row.StepId, row.TaskId, row.VehicleId,
                route.ResourceIds, cancellationToken);
            if (!reservation.Acquired)
                return new(false, $"新路线等待公共资源 {reservation.BlockingResourceId}，尚未发送。", Snapshot(row));

            var newRoot = "TIMEOUTRECOVERY-" + Guid.NewGuid().ToString("N");
            row.State = "Sending";
            row.NewVendorTaskId = newRoot;
            row.SendAttemptedAt = NowLocal;
            row.LastError = "已持久化替代导航发送意图；最多发送一次。";
            row.Revision++;
            row.UpdatedAt = NowLocal;
            await database.Updateable(row).ExecuteCommandAsync(cancellationToken);
            try
            {
                var sentRoot = await gateway.CreateTransportTaskAsync(newRoot, row.VehicleId,
                    route.Nodes, dispatch.GetAction((DispatchTaskStepType)row.StepType), cancellationToken);
                if (!string.Equals(sentRoot, newRoot, StringComparison.Ordinal))
                    throw new InvalidOperationException("替代导航返回的根编号与持久化意图不一致。");
                return await BindReplacementAsync(row, task, step, route.Nodes, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                row.State = "Uncertain";
                row.LastError = Truncate("替代导航结果不确定；只查询原新编号，禁止重发：" + error.Message);
                row.Revision++;
                row.UpdatedAt = NowLocal;
                await database.Updateable(row).ExecuteCommandAsync(CancellationToken.None);
                logger?.LogWarning(error, "运输超时替代导航结果不确定。TaskId={TaskId}, NewRoot={NewRoot}", row.TaskId, newRoot);
                return new(false, row.LastError, Snapshot(row));
            }
        }
        finally
        {
            _execute.Release();
        }
    }

    public async Task<AgvTransportTaskStatus?> GetReplacementStatusAsync(
        DispatchTaskWorkItem task,
        CancellationToken cancellationToken)
    {
        if (gateway is null || string.IsNullOrWhiteSpace(task.VendorTaskId)) return null;
        var row = await database.Queryable<TransportTimeoutRecoveryEntity>()
            .Where(item => item.TaskId == task.TaskId && item.NewVendorTaskId == task.VendorTaskId)
            .FirstAsync(cancellationToken);
        if (row is null) return null;
        var path = JsonSerializer.Deserialize<string[]>(row.CandidatePathJson, Json) ?? [];
        if (path.Length < 2) return null;
        var status = await gateway.GetTransportTaskStatusAsync(task.VendorTaskId!, row.VehicleId, path, cancellationToken);
        if (status is { State: AgvTransportTaskState.Completed } && row.State != "Completed")
        {
            row.State = "Completed";
            row.LastError = "替代导航已全部完成；继续等待业务步骤及PLC结果推进。";
            row.Revision++;
            row.UpdatedAt = NowLocal;
            await database.Updateable(row).ExecuteCommandAsync(cancellationToken);
        }
        return status;
    }

    public async Task<bool> IsObstacleCancellationPendingAsync(
        string taskId,
        string vendorTaskId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(vendorTaskId)) return false;
        var rows = await database.Queryable<ObstacleRecoveryEntity>()
            .Where(item => item.TaskId == taskId && item.OriginalVendorTaskId == vendorTaskId)
            .ToListAsync(cancellationToken);
        return rows.Any(row => row.CancelRequestedAtUtc is not null &&
                               row.State != ObstacleRecoveryStates.Recovered &&
                               row.State != ObstacleRecoveryStates.Executing);
    }

    public async Task<string?> ValidateReplacementPreconditionsAsync(
        string taskId,
        string stepId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(stepId))
            return "任务或运输步骤编号为空，禁止自动改道。";
        var task = await database.Queryable<DispatchTaskEntity>()
            .Where(row => row.TaskId == taskId)
            .FirstAsync(cancellationToken);
        var step = await database.Queryable<DispatchTaskStepEntity>()
            .Where(row => row.TaskId == taskId && row.StepId == stepId)
            .FirstAsync(cancellationToken);
        if (task is null || step is null || !TransportStep(step.StepType) ||
            step.Status is not ((int)DispatchTaskStepStatus.Executing or
                (int)DispatchTaskStepStatus.ManualIntervention or
                (int)DispatchTaskStepStatus.Failed))
            return "原业务任务或执行中的运输步骤身份不一致，禁止自动改道。";
        var work = new DispatchTaskStepWorkItem(task.TaskId, task.StationId, task.VehicleId,
            (DispatchTaskStage)task.Stage, step.StepId, step.Sequence,
            (DispatchTaskStepType)step.StepType, (DispatchTaskStepStatus)step.Status,
            step.SourceSite, step.DestinationSite, new DateTimeOffset(step.UpdatedAt));
        return await ValidateRecoveryPlcAsync(work, step, cancellationToken);
    }

    private async Task<TransportTimeoutRecoveryResult> ReconcileExistingAsync(
        TransportTimeoutRecoveryEntity row,
        CancellationToken cancellationToken)
    {
        if (gateway is null || string.IsNullOrWhiteSpace(row.NewVendorTaskId))
            return new(false, "原替代导航已尝试发送，不能再次发送。", Snapshot(row));
        var path = JsonSerializer.Deserialize<string[]>(row.CandidatePathJson, Json) ?? [];
        var status = path.Length < 2 ? null : await gateway.GetTransportTaskStatusAsync(
            row.NewVendorTaskId, row.VehicleId, path, cancellationToken);
        if (status is null || status.State == AgvTransportTaskState.Unknown)
        {
            row.State = "Uncertain";
            row.LastError = "原替代导航记录未知；只查询原新编号，禁止重发。";
            row.UpdatedAt = NowLocal;
            await database.Updateable(row).ExecuteCommandAsync(cancellationToken);
            return new(false, row.LastError, Snapshot(row));
        }
        if (status.State is AgvTransportTaskState.Failed or AgvTransportTaskState.Cancelled)
        {
            row.State = "ManualIntervention";
            row.LastError = "替代导航失败或取消；原业务任务保持冻结，由现场核对。";
            row.Revision++;
            row.UpdatedAt = NowLocal;
            await database.Updateable(row).ExecuteCommandAsync(cancellationToken);
            return new(false, row.LastError, Snapshot(row));
        }
        var task = await database.Queryable<DispatchTaskEntity>().Where(item => item.TaskId == row.TaskId).FirstAsync(cancellationToken);
        var step = await database.Queryable<DispatchTaskStepEntity>().Where(item => item.StepId == row.StepId).FirstAsync(cancellationToken);
        if (task is null || step is null) return new(false, "原业务任务或步骤缺失，保持冻结。", Snapshot(row));
        return await BindReplacementAsync(row, task, step, path, cancellationToken);
    }

    private async Task<TransportTimeoutRecoveryResult> BindReplacementAsync(
        TransportTimeoutRecoveryEntity row,
        DispatchTaskEntity task,
        DispatchTaskStepEntity step,
        IReadOnlyList<string> path,
        CancellationToken cancellationToken)
    {
        var now = NowLocal;
        step.Status = (int)DispatchTaskStepStatus.Executing;
        step.VendorTaskId = row.NewVendorTaskId;
        step.VehicleId = row.VehicleId;
        step.SourceSite = path[0];
        step.DestinationSite = path[^1];
        step.ErrorMessage = null;
        step.UpdatedAt = now;
        task.Stage = (int)TargetStage((DispatchTaskStepType)row.StepType);
        task.VehicleId = row.VehicleId;
        task.VendorTaskId = row.NewVendorTaskId;
        task.ErrorMessage = "超时恢复导航已发送；继续沿用原业务任务和步骤。";
        task.UpdatedAt = now;
        row.State = "Executing";
        row.LastError = task.ErrorMessage;
        row.Revision++;
        row.UpdatedAt = now;
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            await database.Updateable(step).ExecuteCommandAsync(cancellationToken);
            await database.Updateable(task).ExecuteCommandAsync(cancellationToken);
            await database.Updateable(row).ExecuteCommandAsync(cancellationToken);
            await database.Insertable(new ExecutionEvidenceEntity
            {
                Id = Guid.NewGuid().ToString("N"),SessionId = "",TaskId = row.TaskId,StepId = row.StepId,
                Operator = "operator",Kind = "TransportTimeoutReplacementSent",
                Reason = "现场确认后发送一次替代3066任务链。",Evidence = JsonSerializer.Serialize(new { row, Path = path }),
                OccurredAt = now
            }).ExecuteCommandAsync(cancellationToken);
        });
        if (!transaction.IsSuccess)
            return new(false, "替代导航已发送，但任务绑定落库失败；禁止重发，须按新编号现场核对。", Snapshot(row));
        return new(true, "替代导航已发送并绑定原业务任务；等待车辆实际完成。", Snapshot(row));
    }

    private async Task<bool> ConfirmStoppedAsync(string vehicleId, string? station, CancellationToken cancellationToken)
    {
        var definition = activeMap.Current?.Definition;
        bool Stopped(AgvRobotSnapshot robot, string? expectedStation) =>
            robot.Connection == ConnectionState.Online && robot.Quality == SignalQuality.Good &&
            robot.EmergencyStopped == false && !AgvObstaclePolicy.IsBlocked(robot) &&
            !AgvObstaclePolicy.HasBlockingAlarm(robot) &&
            MapRouteSiteMapper.IsSameNode(definition, robot.CurrentStation, expectedStation) &&
            robot.VelocityX is { } vx && Math.Abs(vx) < 0.02 &&
            robot.VelocityY is { } vy && Math.Abs(vy) < 0.02 &&
            robot.AngularVelocity is { } w && Math.Abs(w) < 0.02;
        if (!vehicles.TryGet(vehicleId, out var first) || !Stopped(first, station)) return false;
        var firstSample = first.LastSuccessfulPollAt;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(100, cancellationToken);
            if (!vehicles.TryGet(vehicleId, out var second) ||
                second.LastSuccessfulPollAt is not { } secondSample ||
                firstSample is null || secondSample <= firstSample ||
                !Stopped(second, station)) continue;
            return first.X is { } x1 && second.X is { } x2 && Math.Abs(x1 - x2) < 0.02 &&
                   first.Y is { } y1 && second.Y is { } y2 && Math.Abs(y1 - y2) < 0.02;
        }
        return false;
    }

    private async Task<RoutePlan?> PlanReplacementAsync(ActiveMapRuntimeSnapshot map,
        TransportTimeoutRecoveryEntity row, IReadOnlyList<string> originalPath, CancellationToken cancellationToken)
    {
        var source = MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, row.CurrentStation!);
        var destination = MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, row.DestinationSite);
        var index = originalPath.Select((site, idx) => (site, idx))
            .Single(item => MapRouteSiteMapper.IsSameNode(map.Definition,
                item.site, row.CurrentStation)).idx;
        var next = MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition, originalPath[index + 1]);
        var blocked = map.Definition.Edges.Where(edge => edge.Enabled &&
                (edge.From == source && edge.To == next || edge.Bidirectional && edge.From == next && edge.To == source))
            .SelectMany(edge => edge.Bidirectional
                ? new[] { new MapDirectedTraversal(edge.Id, source, next), new MapDirectedTraversal(edge.Id, next, source) }
                : new[] { new MapDirectedTraversal(edge.Id, source, next) }).ToList();
        if (roadClosures is not null) blocked.AddRange((await roadClosures.ListAsync(cancellationToken)).BlockedTraversals);
        var unavailable = await traffic.GetUnavailableResourceIdsAsync(row.TaskId, cancellationToken);
        var traversals = blocked.Distinct().ToArray();
        if (routePlanner is not null)
            return routePlanner.PlanForVehicle(row.CurrentStation!, row.DestinationSite,
                unavailable, row.VehicleId, traversals);
        var logical = map.Graph.FindShortestTransportRoute(source, destination, unavailable, null, null,
            traversals);
        return logical is null ? null : MapRouteSiteMapper.ToAgvSitePlan(map.Definition, logical);
    }

    private async Task<string?> ValidateRecoveryPlcAsync(DispatchTaskStepWorkItem work,
        DispatchTaskStepEntity step, CancellationToken cancellationToken)
    {
        try
        {
            if (handshakeOptions?.Enabled == true)
            {
                if (handshakes is null || signals is null || creation is null || dispatch is null)
                    return "缺少28阶段PLC存储、点表或工位配置，禁止发送恢复导航。";
                if (work.StepType != DispatchTaskStepType.GoToPickup)
                {
                    if (handshake is null) return "缺少28阶段PLC执行器，禁止发送恢复导航。";
                    await handshake.ValidateTransportAsync(work, cancellationToken);
                    return null;
                }
                var checkpoint = await handshakes.GetAsync(work.TaskId, work.StepId, cancellationToken);
                if (checkpoint is null ||
                    checkpoint.ContractVersion != handshakeOptions.PlcContractVersion ||
                    checkpoint.PendingSignal is not null ||
                    checkpoint.Phase is not ("TransportBound" or "ReadyToComplete"))
                    return "首段原28阶段绑定缺失、待确认或阶段不一致，禁止发送恢复导航。";
                var now = (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow();
                foreach (var expected in checkpoint.Baseline.Where(row => row.StationId != "__context"))
                {
                    if (!signals.TryGet(expected.StationId, expected.VariableName, out var sample) ||
                        sample.Address != expected.Address)
                        return $"首段PLC原点表地址已变化：{expected.StationId}/{expected.VariableName}";
                    if (sample.Value is not bool value ||
                        !TaskSignalPredicate.EvaluateFresh(expected.StationId, expected.VariableName,
                            value, sample, now, handshakeOptions.StateMaxAgeSeconds).Satisfied)
                        return $"首段恢复前PLC信号无效或过期：{expected.StationId}/{expected.VariableName}";
                }
                bool Exact(string station, string variable, bool expected)
                {
                    signals.TryGet(station, variable, out var sample);
                    return TaskSignalPredicate.EvaluateFresh(station, variable, expected, sample, now,
                        handshakeOptions.StateMaxAgeSeconds).Satisfied;
                }
                if (!Exact(work.StationId, "Faulted", false) ||
                    !Exact(work.StationId, "CommunicationOnline", true) ||
                    !Exact(creation.DumperStationId, "Faulted", false) ||
                    !Exact(work.StationId, "FullBoxRequested", false) ||
                    !Exact(work.StationId, "FullBoxRequestAcknowledged", true) ||
                    !Exact(work.StationId, "BoxPresent", true) ||
                    new[] { "FullBoxRemoved", "EmptyBoxTaskAcknowledged", "EmptyBoxPlaced" }
                        .Any(variable => !Exact(work.StationId, variable, false)))
                    return "原取箱工位PLC周期、载荷或通知责任已变化，禁止发送恢复导航。";
                return null;
            }
            if (signals is null || creation is null) return "缺少PLC点表缓存或工位配置，禁止发送恢复导航。";
            var all = await database.Queryable<DispatchTaskStepEntity>()
                .Where(item => item.TaskId == step.TaskId).OrderBy(item => item.Sequence).ToListAsync(cancellationToken);
            bool Fresh(string station, string variable, bool expected)
            {
                signals.TryGet(station, variable, out var sample);
                return TaskSignalPredicate.EvaluateFresh(station, variable, expected, sample,
                    (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow(), handshakeOptions?.StateMaxAgeSeconds ?? 5).Satisfied;
            }
            if (!Fresh(work.StationId!, "Faulted", false) ||
                !Fresh(work.StationId!, "CommunicationOnline", true) ||
                !Fresh(creation.DumperStationId, "Faulted", false))
                return "工位或倒料机故障/通讯PLC条件不成立。";
            if (work.StepType == DispatchTaskStepType.GoToPickup)
            {
                if (!Fresh(work.StationId!, "BoxPresent", true) ||
                    !Fresh(work.StationId!, "FullBoxRequestAcknowledged", true))
                    return "取箱工位满箱或呼叫受理条件不成立。";
            }
            else
            {
                var priorType = work.StepType switch
                {
                    DispatchTaskStepType.DeliverFullBoxToDumper => DispatchTaskStepType.PickupFullBox,
                    DispatchTaskStepType.EnterDumpingPosition => DispatchTaskStepType.WaitForDumperReady,
                    DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStepType.DumpFullBox,
                    _ => (DispatchTaskStepType)(-1)
                };
                if (!all.Any(item => item.StepType == (int)priorType && item.Status == (int)DispatchTaskStepStatus.Completed))
                    return "前序业务步骤未完成，禁止恢复后续运输。";
                if (!Fresh(work.StationId!, "EmptyBoxTaskAcknowledged", true)) return "返箱责任PLC条件不成立。";
                if (work.StepType == DispatchTaskStepType.DeliverFullBoxToDumper && !Fresh(work.StationId!, "BoxPresent", false))
                    return "满箱尚未离开取货工位。";
                if (work.StepType == DispatchTaskStepType.EnterDumpingPosition && !Fresh(creation.DumperStationId, "DumperReady", true))
                    return "倒料机Ready条件不成立。";
                if (work.StepType == DispatchTaskStepType.ReturnEmptyBox &&
                    !Fresh(creation.DumperStationId, "DumpingCompletedMayLeave", true))
                    return "倒料完成及离位许可条件不成立。";
            }
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return "PLC恢复条件复核未通过：" + error.Message; }
    }

    private TransportTimeoutRecoverySnapshot Snapshot(TransportTimeoutRecoveryEntity row) =>
        new(row.RecoveryId, row.TaskId, row.StepId, row.VehicleId, (DispatchTaskStepType)row.StepType,
            row.State, row.Outcome, row.OriginalVendorTaskId, row.NewVendorTaskId, row.CurrentStation,
            row.DestinationSite,
            JsonSerializer.Deserialize<string[]>(row.OriginalPathJson, Json) ?? [],
            JsonSerializer.Deserialize<string[]>(row.CandidatePathJson, Json) ?? [],
            row.MapId, row.MapVersionId, row.DefinitionHash, row.PreviewHash,
            row.SendAttemptedAt is not null, row.LastError ?? "", row.Revision,
            new DateTimeOffset(DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Local)));

    private DateTime NowLocal => (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetLocalNow().DateTime;
    private static DispatchTaskStage TargetStage(DispatchTaskStepType stepType) => stepType switch
    {
        DispatchTaskStepType.GoToPickup => DispatchTaskStage.GoingToPickup,
        DispatchTaskStepType.DeliverFullBoxToDumper => DispatchTaskStage.GoingToDumper,
        DispatchTaskStepType.EnterDumpingPosition => DispatchTaskStage.Dumping,
        DispatchTaskStepType.ReturnEmptyBox => DispatchTaskStage.ReturningEmptyBox,
        _ => throw new InvalidOperationException("不是运输恢复步骤。")
    };

    private static string Hash(object value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));

    private async Task<TransportTimeoutRecoveryAssessment> AssessAsync(
        string businessTaskId,
        DispatchTaskStepType stepType,
        string vehicleId,
        string vendorTaskId,
        string destination,
        IReadOnlyList<string> originalPath,
        CancellationToken cancellationToken)
    {
        TransportTimeoutRecoveryAssessment Result(TransportTimeoutRecoveryOutcome outcome,
            string? currentStation, IReadOnlyList<string> candidatePath, string detail) =>
            new(outcome, stepType, vehicleId, vendorTaskId, currentStation, destination,
                originalPath.ToArray(), candidatePath.ToArray(), Prefix(stepType) + detail);

        if (!vehicles.TryGet(vehicleId, out var robot) ||
            robot.Connection != ConnectionState.Online ||
            robot.Quality != SignalQuality.Good)
            return Result(TransportTimeoutRecoveryOutcome.SafetyHold, robot?.CurrentStation, [],
                "车辆通信或遥测无效，任务已冻结；恢复通信并现场核对前禁止改道或重发。");

        var now = (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow();
        if (robot.LastSuccessfulPollAt is not { } observed || observed > now.AddSeconds(1) ||
            now - observed >= TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds))
            return Result(TransportTimeoutRecoveryOutcome.SafetyHold, robot.CurrentStation, [],
                "车辆定位/状态采样已过期，任务已冻结；等待新鲜遥测后重新处置。");

        if (robot.EmergencyStopped != false || AgvObstaclePolicy.HasBlockingAlarm(robot))
            return Result(TransportTimeoutRecoveryOutcome.SafetyHold, robot.CurrentStation, [],
                "车辆急停或存在严重报警，安全处置优先；不得自动改道。");

        if (AgvObstaclePolicy.IsBlocked(robot))
            return Result(TransportTimeoutRecoveryOutcome.AwaitingObstacleClear, robot.CurrentStation, [],
                "车辆仍处于局部避障/阻挡状态，原任务和路权继续保留；障碍清除、车辆停稳并形成可信站点后才能评估改道。");

        var map = activeMap.Current;
        if (map is null)
            return Result(TransportTimeoutRecoveryOutcome.SafetyHold, robot.CurrentStation, [],
                "运行地图不可用，任务已冻结；不得凭旧路线生成替代导航。");
        var position = AgvMapPositionAnalyzer.Analyze(robot.X, robot.Y, robot.LocalizationConfidence,
            robot.LastSuccessfulPollAt, map.Definition, now, TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds),
            mapOptions.MinimumLocalizationConfidence, mapOptions.MaximumRouteDeviationMeters);
        if (position.State != AgvMapPositionState.Normal)
            return Result(TransportTimeoutRecoveryOutcome.ManualRelocationRequired, robot.CurrentStation, [],
                $"车辆位置不可信：{position.Message}；须人工确认或移动到安全站点，不能从最近道路或旧起点冒名发车。");

        if (string.IsNullOrWhiteSpace(robot.CurrentStation))
            return Result(TransportTimeoutRecoveryOutcome.ManualRelocationRequired, null, [],
                "车辆当前站点未知；须现场核实并移动到明确安全站点后再恢复。");

        var matches = map.Definition.Nodes.Where(node => node.Enabled &&
            MapRouteSiteMapper.IsSameNode(map.Definition, node.Id, robot.CurrentStation)).ToArray();
        if (matches.Length != 1)
            return Result(TransportTimeoutRecoveryOutcome.ManualRelocationRequired, robot.CurrentStation, [],
                "当前站点在运行地图中不是唯一有效站点；禁止自动生成改道路线。");
        if (robot.X is not { } stationX || !double.IsFinite(stationX) ||
            robot.Y is not { } stationY || !double.IsFinite(stationY) ||
            Math.Sqrt(Math.Pow(stationX - matches[0].X, 2) + Math.Pow(stationY - matches[0].Y, 2)) > 0.3)
            return Result(TransportTimeoutRecoveryOutcome.ManualRelocationRequired, robot.CurrentStation, [],
                "车辆仍在路段中间或偏离命名站点超过0.3米；禁止把最近站点冒充为实际起点。");

        var destinationMatches = map.Definition.Nodes.Where(node => node.Enabled &&
            MapRouteSiteMapper.IsSameNode(map.Definition, node.Id, destination)).ToArray();
        if (destinationMatches.Length != 1)
            return Result(TransportTimeoutRecoveryOutcome.ManualIntervention, robot.CurrentStation, [],
                "原任务目的站在当前运行地图中不是唯一有效站点；任务保持冻结并等待人工核对。");

        if (originalPath.Count < 2)
            return Result(TransportTimeoutRecoveryOutcome.ManualIntervention, robot.CurrentStation, [],
                "原导航任务链或路线快照缺失，不能可靠判断受影响道路；任务保持冻结，禁止重发。");

        var positions = originalPath.Select((site, index) => (site, index))
            .Where(row => MapRouteSiteMapper.IsSameNode(map.Definition,
                row.site, robot.CurrentStation))
            .ToArray();
        if (positions.Length != 1 || positions[0].index + 1 >= originalPath.Count)
            return Result(TransportTimeoutRecoveryOutcome.ManualRelocationRequired, robot.CurrentStation, [],
                "车辆当前位置不在原路线中段或不能唯一确定下一站；须现场把车辆移动到可信安全站点。");

        RoadClosureSnapshot? closure = null;
        if (roadClosures is not null)
        {
            try { closure = await roadClosures.ListAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                return Result(TransportTimeoutRecoveryOutcome.ManualIntervention, robot.CurrentStation, [],
                    "道路封锁证据读取失败：" + error.Message + "；保持原任务，等待人工核对。");
            }
            if (!closure.Ready || closure.IdentityConflict ||
                closure.MapId != map.MapId || closure.VersionId != map.VersionId ||
                !string.Equals(closure.DefinitionHash, EngineeringRuntimeIdentity.MapHash(map.Definition),
                    StringComparison.OrdinalIgnoreCase))
                return Result(TransportTimeoutRecoveryOutcome.ManualIntervention, robot.CurrentStation, [],
                    "道路封锁证据未就绪、地图身份不一致或冲突；保持原任务，等待人工核对。");
        }

        var sourceNode = matches[0].Id;
        var destinationNode = destinationMatches[0].Id;
        var blockedTo = MapRouteSiteMapper.ResolveLogicalNodeId(map.Definition,
            originalPath[positions[0].index + 1]);
        var inferred = map.Definition.Edges
            .Where(edge => edge.Enabled &&
                (string.Equals(edge.From, sourceNode, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(edge.To, blockedTo, StringComparison.OrdinalIgnoreCase) ||
                 edge.Bidirectional &&
                 string.Equals(edge.From, blockedTo, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(edge.To, sourceNode, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(edge => edge.Bidirectional
                ? new[]
                {
                    new MapDirectedTraversal(edge.Id, sourceNode, blockedTo),
                    new MapDirectedTraversal(edge.Id, blockedTo, sourceNode)
                }
                : new[] { new MapDirectedTraversal(edge.Id, sourceNode, blockedTo) })
            .ToList();
        if (inferred.Count == 0)
            return Result(TransportTimeoutRecoveryOutcome.ManualIntervention, robot.CurrentStation, [],
                "无法从原路线唯一确定车辆当前下一段道路；保持原任务并等待现场核对。");
        if (closure is not null) inferred.AddRange(closure.BlockedTraversals);
        var blocked = inferred.Distinct().ToArray();
        var unavailable = await traffic.GetUnavailableResourceIdsAsync(businessTaskId, cancellationToken);
        var route = map.Graph.FindShortestTransportRoute(sourceNode, destinationNode,
            unavailable, null, null, blocked);
        if (route is null)
            return Result(TransportTimeoutRecoveryOutcome.ManualIntervention, robot.CurrentStation, [],
                $"已推断受阻道路为 {robot.CurrentStation}→{originalPath[positions[0].index + 1]}，但当前没有可达替代路线；原任务保持冻结，禁止重复发送。");
        var candidate = MapRouteSiteMapper.ToAgvSitePlan(map.Definition, route);
        if (candidate.Nodes.Count < 2 ||
            !MapRouteSiteMapper.IsSameNode(map.Definition,
                candidate.Nodes[0], robot.CurrentStation) ||
            !MapRouteSiteMapper.IsSameNode(map.Definition,
                candidate.Nodes[^1], destination))
            return Result(TransportTimeoutRecoveryOutcome.ManualIntervention, robot.CurrentStation, [],
                "规划结果与实际起点或原目的站不一致；保持原任务，禁止发送。");
        return Result(TransportTimeoutRecoveryOutcome.ReadyForControlledDetour, robot.CurrentStation,
            candidate.Nodes,
            $"已形成只读改道候选：{string.Join("→", candidate.Nodes)}。原任务、载荷和路权保留；发送前仍须核验车端任务终态、连续停稳、地图版本、路权和PLC条件。");
    }

    private static bool TransportStep(int stepType) =>
        stepType is (int)DispatchTaskStepType.GoToPickup
            or (int)DispatchTaskStepType.DeliverFullBoxToDumper
            or (int)DispatchTaskStepType.EnterDumpingPosition
            or (int)DispatchTaskStepType.ReturnEmptyBox;

    private static string Prefix(DispatchTaskStepType stepType) => stepType switch
    {
        DispatchTaskStepType.GoToPickup =>
            "取箱导航超时：原取箱任务和工位呼叫保留，尚未取箱。",
        DispatchTaskStepType.DeliverFullBoxToDumper =>
            "送满箱导航超时：满箱和原倒料归属保留，禁止自动换车。",
        DispatchTaskStepType.EnterDumpingPosition =>
            "进入倒料位导航超时：倒料任务和已取得路权保留，禁止误报到位。",
        DispatchTaskStepType.ReturnEmptyBox =>
            "返空箱导航超时：倒料完成和空箱返程责任保留，尚未完成放箱或PLC收尾。",
        _ => "运输导航超时。"
    };

    private static string Truncate(string value) => value.Length <= 1000 ? value : value[..1000];
}
