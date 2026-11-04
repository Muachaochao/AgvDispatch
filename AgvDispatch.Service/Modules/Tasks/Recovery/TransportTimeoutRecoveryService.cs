using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Service;

/// <summary>
/// Converts a Robokit status-7 transport timeout into an explicit, auditable
/// business hold. Route assessment is read-only; this service never sends a
/// cancellation or a replacement navigation command.
/// </summary>
public sealed class TransportTimeoutRecoveryService(
    ISqlSugarClient database,
    IAgvStateCache vehicles,
    IActiveMapRuntime activeMap,
    IAgvTransportChainLookup chains,
    ITrafficResourceManager traffic,
    IRoadClosureService? roadClosures,
    MapOptions mapOptions,
    TimeProvider? clock = null) : ITransportTimeoutRecoveryService
{
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
            var chain = await chains.FindAsync(status.VendorTaskId, cancellationToken);
            var originalPath = chain?.Path?.ToArray() ?? [];
            if (chain is not null &&
                (!string.Equals(chain.VehicleId, task.VehicleId, StringComparison.OrdinalIgnoreCase) ||
                 originalPath.Length < 2 ||
                 !string.Equals(originalPath[^1], step.DestinationSite, StringComparison.OrdinalIgnoreCase)))
                originalPath = [];

            assessment = await AssessAsync((DispatchTaskStepType)step.StepType, task.VehicleId, status.VendorTaskId,
                step.DestinationSite ?? "", originalPath, cancellationToken);
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
        var now = (clock ?? TimeProvider.System).GetLocalNow().DateTime;
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

    private async Task<TransportTimeoutRecoveryAssessment> AssessAsync(
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

        var now = (clock ?? TimeProvider.System).GetUtcNow();
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
            string.Equals(node.AgvSiteId, robot.CurrentStation, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            return Result(TransportTimeoutRecoveryOutcome.ManualRelocationRequired, robot.CurrentStation, [],
                "当前站点在运行地图中不是唯一有效站点；禁止自动生成改道路线。");

        var destinationMatches = map.Definition.Nodes.Where(node => node.Enabled &&
            string.Equals(node.AgvSiteId, destination, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (destinationMatches.Length != 1)
            return Result(TransportTimeoutRecoveryOutcome.ManualIntervention, robot.CurrentStation, [],
                "原任务目的站在当前运行地图中不是唯一有效站点；任务保持冻结并等待人工核对。");

        if (originalPath.Count < 2)
            return Result(TransportTimeoutRecoveryOutcome.ManualIntervention, robot.CurrentStation, [],
                "原导航任务链或路线快照缺失，不能可靠判断受影响道路；任务保持冻结，禁止重发。");

        var positions = originalPath.Select((site, index) => (site, index))
            .Where(row => string.Equals(row.site, robot.CurrentStation, StringComparison.OrdinalIgnoreCase))
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
        var unavailable = await traffic.GetUnavailableResourceIdsAsync(vendorTaskId, cancellationToken);
        var route = map.Graph.FindShortestTransportRoute(sourceNode, destinationNode,
            unavailable, null, null, blocked);
        if (route is null)
            return Result(TransportTimeoutRecoveryOutcome.ManualIntervention, robot.CurrentStation, [],
                $"已推断受阻道路为 {robot.CurrentStation}→{originalPath[positions[0].index + 1]}，但当前没有可达替代路线；原任务保持冻结，禁止重复发送。");
        var candidate = MapRouteSiteMapper.ToAgvSitePlan(map.Definition, route);
        if (candidate.Nodes.Count < 2 ||
            !string.Equals(candidate.Nodes[0], robot.CurrentStation, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.Nodes[^1], destination, StringComparison.OrdinalIgnoreCase))
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
