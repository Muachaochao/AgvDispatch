using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Service;

internal static class FeedbackTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("box feedback evidence selection"); count++; }
        void Reject(Func<TaskStepDetail?> action)
        { try { action(); } catch (InvalidOperationException) { count++; return; } throw new Exception("invalid box evidence accepted"); }
        var now = DateTimeOffset.UtcNow;
        var run = new BusinessRunRecord("run", new("req", "Cycle", "AGV-01", ["CNC-001", "CNC-002"], 2, "LM7", true),
            "map", "version", "revision", "operator", "Executing", "", 0, "task", null, null, false, now.AddMinutes(-1));
        var types = new[] { DispatchTaskStepType.GoToPickup, DispatchTaskStepType.PickupFullBox,
            DispatchTaskStepType.DeliverFullBoxToDumper, DispatchTaskStepType.WaitForDumperReady,
            DispatchTaskStepType.EnterDumpingPosition, DispatchTaskStepType.DumpFullBox,
            DispatchTaskStepType.ReturnEmptyBox, DispatchTaskStepType.PlaceEmptyBox, DispatchTaskStepType.CompletePlcHandshake };
        var steps = types.Select((type, i) => new TaskStepDetail("step-" + i, i + 1, type,
            i == 0 ? DispatchTaskStepStatus.Completed : i == 1 ? DispatchTaskStepStatus.Ready : DispatchTaskStepStatus.Pending,
            "AGV-01", "LM7", "LM2", "vendor-" + i, 0, now.AddSeconds(-10), now.AddSeconds(-1), null)).ToArray();
        var detail = new TaskCenterDetail(new("task", "CNC-001", "AGV-01", DispatchTaskStage.PickingUp, 50, now, now, null), steps, [], [], [], "");
        Check(BusinessRunPort.SelectFeedbackStep(run, detail)?.StepId == "step-0");
        var unassignedSteps = steps.Select((step, i) => step with {
            Status = i == 0 ? DispatchTaskStepStatus.Ready : DispatchTaskStepStatus.Pending,
            VehicleId = null, VendorTaskId = null, StartedAt = null, CompletedAt = null }).ToArray();
        foreach (var stage in new[] { DispatchTaskStage.Created, DispatchTaskStage.WaitingForVehicle, DispatchTaskStage.SubmittingToAgv })
            Check(BusinessRunPort.SelectFeedbackStep(run, detail with {
                Task = detail.Task with { Stage = stage, VehicleId = null }, Steps = unassignedSteps }) is null);
        Reject(() => BusinessRunPort.SelectFeedbackStep(run, detail with { Task = detail.Task with { VehicleId = null } }));
        Check(BusinessRunPort.SelectFeedbackStep(run with { Plan = run.Plan with { Mode = "Demand" } }, detail) is null);
        Check(BusinessRunPort.SelectFeedbackStep(run with { Stage = "Hold" }, detail) is null);
        foreach (var state in new[] { DispatchTaskStage.Completed, DispatchTaskStage.Cancelled, DispatchTaskStage.CancellationPending, DispatchTaskStage.CancellationReview, DispatchTaskStage.Faulted })
            Check(BusinessRunPort.SelectFeedbackStep(run, detail with { Task = detail.Task with { Stage = state } }) is null);
        Reject(() => BusinessRunPort.SelectFeedbackStep(run with { CompletedJobs = 1 }, detail));
        Reject(() => BusinessRunPort.SelectFeedbackStep(run, detail with { Task = detail.Task with { TaskId = "old-task" } }));
        Reject(() => BusinessRunPort.SelectFeedbackStep(run, detail with { Task = detail.Task with { VehicleId = "AGV-02" } }));
        foreach (var bad in new[] { steps[0] with { Status = DispatchTaskStepStatus.Executing }, steps[0] with { VendorTaskId = null },
            steps[0] with { VehicleId = "AGV-02" }, steps[0] with { CompletedAt = now.AddMinutes(-2) }, steps[0] with { CompletedAt = now.AddMinutes(1) } })
        { var copy = steps.ToArray(); copy[0] = bad; Reject(() => BusinessRunPort.SelectFeedbackStep(run, detail with { Steps = copy })); }
        for (var i = 1; i <= 6; i++) steps[i] = steps[i] with { Status = DispatchTaskStepStatus.Completed };
        steps[7] = steps[7] with { Status = DispatchTaskStepStatus.Ready };
        Check(BusinessRunPort.SelectFeedbackStep(run, detail)?.StepId == "step-6");
        steps[4] = steps[4] with { Status = DispatchTaskStepStatus.Executing };
        Reject(() => BusinessRunPort.SelectFeedbackStep(run, detail));
        Console.WriteLine($"FeedbackTests passed {count} evidence selection checks; no site devices.");
        return count;
    }
}
