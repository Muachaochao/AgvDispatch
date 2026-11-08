using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Domain.Stations;

public sealed record StationStateEvaluation(
    StationOperationalState State,
    bool CanCreateTask,
    string? BlockReason,
    bool? FullBoxRequested,
    bool? BoxPresent,
    bool? Faulted,
    bool? CommunicationOnline,
    SignalQuality SignalQuality,
    string? ActiveTaskId,
    int ActiveTaskCount);

public static class StationStateEvaluator
{
    private static readonly string[] RequiredSignals =
    [
        "FullBoxRequested",
        "BoxPresent",
        "Faulted",
        "CommunicationOnline"
    ];

    public static StationStateEvaluation Evaluate(
        string stationId,
        IReadOnlyList<PlcSignalSnapshot> signals,
        IReadOnlyList<ActiveDispatchTask> activeTasks)
    {
        var byName = signals.ToDictionary(
            signal => signal.VariableName,
            StringComparer.OrdinalIgnoreCase);
        var missing = RequiredSignals.Where(name => !byName.ContainsKey(name)).ToArray();
        if (missing.Length > 0)
        {
            return Result(
                StationOperationalState.Unknown,
                false,
                $"缺少必要PLC信号：{string.Join(", ", missing)}。",
                byName,
                SignalQuality.Unknown,
                activeTasks);
        }

        var required = RequiredSignals.Select(name => byName[name]).ToArray();
        var quality = GetOverallQuality(required);
        if (activeTasks.Count > 1)
        {
            return Result(
                StationOperationalState.SignalConflict,
                false,
                $"工位存在{activeTasks.Count}条未完成任务，禁止继续自动调度。",
                byName,
                quality,
                activeTasks);
        }

        if (quality != SignalQuality.Good)
        {
            var state = quality == SignalQuality.Unknown
                ? StationOperationalState.Unknown
                : StationOperationalState.Offline;
            return Result(
                state,
                false,
                $"PLC信号质量不是Good，当前为{quality}。",
                byName,
                quality,
                activeTasks);
        }

        var fullBoxRequested = byName["FullBoxRequested"].Value!.Value;
        var boxPresent = byName["BoxPresent"].Value!.Value;
        var faulted = byName["Faulted"].Value!.Value;
        var communicationOnline = byName["CommunicationOnline"].Value!.Value;

        if (!communicationOnline)
        {
            return Result(
                StationOperationalState.Offline,
                false,
                "工位通讯状态信号为0。",
                byName,
                quality,
                activeTasks);
        }

        if (faulted)
        {
            return Result(
                StationOperationalState.Faulted,
                false,
                "工位异常信号为1。",
                byName,
                quality,
                activeTasks);
        }

        if (fullBoxRequested && !boxPresent)
        {
            return Result(
                StationOperationalState.SignalConflict,
                false,
                "满箱呼叫为1，但未检测到箱体。",
                byName,
                quality,
                activeTasks);
        }

        if (activeTasks.Count == 1)
        {
            var task = activeTasks[0];
            var state = task.Stage switch
            {
                DispatchTaskStage.Created or
                DispatchTaskStage.WaitingForVehicle or
                DispatchTaskStage.SubmittingToAgv or
                DispatchTaskStage.GoingToPickup => StationOperationalState.FullBoxWaitingForPickup,
                DispatchTaskStage.PickingUp => StationOperationalState.PickupInProgress,
                DispatchTaskStage.GoingToDumper or
                DispatchTaskStage.WaitingForDumper or
                DispatchTaskStage.Dumping => StationOperationalState.WaitingForEmptyBox,
                DispatchTaskStage.ReturningEmptyBox or
                DispatchTaskStage.WaitingForPlcConfirmation =>
                    StationOperationalState.EmptyBoxPlacementInProgress,
                DispatchTaskStage.Completed => StationOperationalState.Completed,
                DispatchTaskStage.Faulted => StationOperationalState.ManualIntervention,
                _ => StationOperationalState.ManualIntervention
            };

            return Result(
                state,
                false,
                $"工位已有未完成任务：{task.TaskId}（{task.Stage}）。",
                byName,
                quality,
                activeTasks);
        }

        if (fullBoxRequested && boxPresent)
        {
            return Result(
                StationOperationalState.FullBoxWaitingForPickup,
                true,
                null,
                byName,
                quality,
                activeTasks);
        }

        if (!boxPresent)
        {
            // An empty station with no active task is a normal waiting-for-box
            // state. It is a conflict only when a dispatcher output from an
            // earlier cycle is still asserted and therefore needs PLC cleanup.
            var residualHandshake = new[]
            {
                "FullBoxRequestAcknowledged",
                "FullBoxRemoved",
                "EmptyBoxTaskAcknowledged",
                "EmptyBoxPlaced"
            }.Any(name => byName.TryGetValue(name, out var signal) &&
                          signal.Quality == SignalQuality.Good && signal.Value == true);
            if (!residualHandshake)
            {
                return Result(
                    StationOperationalState.Idle,
                    false,
                    "未检测到箱体，工位空闲，等待满箱上料。",
                    byName,
                    quality,
                    activeTasks);
            }
            return Result(
                StationOperationalState.SignalConflict,
                false,
                "未检测到箱体，但上一轮PLC握手输出尚未清理。",
                byName,
                quality,
                activeTasks);
        }

        return Result(
            StationOperationalState.Idle,
            false,
            "未收到满箱呼叫。",
            byName,
            quality,
            activeTasks);
    }

    private static StationStateEvaluation Result(
        StationOperationalState state,
        bool canCreateTask,
        string? blockReason,
        IReadOnlyDictionary<string, PlcSignalSnapshot> signals,
        SignalQuality quality,
        IReadOnlyList<ActiveDispatchTask> activeTasks)
    {
        signals.TryGetValue("FullBoxRequested", out var fullBoxRequested);
        signals.TryGetValue("BoxPresent", out var boxPresent);
        signals.TryGetValue("Faulted", out var faulted);
        signals.TryGetValue("CommunicationOnline", out var communicationOnline);

        return new StationStateEvaluation(
            state,
            canCreateTask,
            blockReason,
            fullBoxRequested?.Value,
            boxPresent?.Value,
            faulted?.Value,
            communicationOnline?.Value,
            quality,
            activeTasks.Count == 1 ? activeTasks[0].TaskId : null,
            activeTasks.Count);
    }

    private static SignalQuality GetOverallQuality(IEnumerable<PlcSignalSnapshot> signals)
    {
        var qualities = signals.Select(signal => signal.Quality).ToHashSet();
        if (qualities.Contains(SignalQuality.Bad))
        {
            return SignalQuality.Bad;
        }

        if (qualities.Contains(SignalQuality.Stale))
        {
            return SignalQuality.Stale;
        }

        if (qualities.Contains(SignalQuality.Disabled))
        {
            return SignalQuality.Disabled;
        }

        if (qualities.Contains(SignalQuality.Unknown))
        {
            return SignalQuality.Unknown;
        }

        return SignalQuality.Good;
    }
}
