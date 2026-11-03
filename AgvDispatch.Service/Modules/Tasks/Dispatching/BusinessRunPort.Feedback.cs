using System.Net.Http.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed partial class BusinessRunPort
{
    // This is simulated PLC input, never a claim that a physical box sensor changed.
    // H notifications cannot be used as its trigger: they themselves wait for this P input.
    public async Task SimulatorFeedbackAsync(BusinessRunRecord run, CancellationToken ct)
    {
        if (!simulatorOnly || run.Stage != "Executing" || run.TaskId is null) return;
        var detail = await tasks.GetDetailAsync(run.TaskId, ct)
            ?? throw new InvalidOperationException("模拟箱体反馈找不到原业务任务。");
        await ApplySimulatorFeedbackAsync(run,detail,"cycle-feedback",ct);
    }

    public async Task SimulateDirectFeedbackAsync(CancellationToken ct)
    {
        if (!simulatorOnly) return;
        foreach (var active in await activeTasks.GetActiveTasksAsync(ct))
        {
            if (active.VehicleId is not { Length: > 0 } vehicleId ||
                active.Stage is DispatchTaskStage.Completed or DispatchTaskStage.Cancelled or DispatchTaskStage.CancellationReview)
                continue;
            var detail = await tasks.GetDetailAsync(new TaskDetailRequest(active.TaskId, false), ct);
            if (detail is null) continue;
            var run = new BusinessRunRecord("manual-" + active.TaskId,
                new("manual-" + active.TaskId, "Demand", vehicleId, [active.StationId], 1, "", true),
                map.Current?.MapId ?? "", map.Current?.VersionId ?? "", parameters.Revision,
                "loopback-plc-simulator", "Executing", "", 0, active.TaskId, null, null, false, DateTimeOffset.UtcNow);
            await ApplySimulatorFeedbackAsync(run, detail, "cycle-feedback", ct);
        }
    }
    internal async Task ApplySimulatorFeedbackAsync(BusinessRunRecord run,TaskCenterDetail detail,string endpoint,CancellationToken ct)
    {
        var step = SelectFeedbackStep(run, detail);
        if (step is null) return;
        var pickup = parameters.Capture().Pickups.Single(p => p.StationId == detail.Task.StationId);
        var node = Resolve(pickup.PickupSite);
        if (step.DestinationSite != node.Id && step.DestinationSite != node.AgvSiteId)
            throw new InvalidOperationException("原取放箱步骤目的地与本工位不符。");
        var command = detail.Commands.SingleOrDefault(c => c.StepId == step.StepId && c.Status == AgvCommandStatus.Sent);
        var action = step.StepType == DispatchTaskStepType.GoToPickup ? "JackLoad" : "JackUnload";
        if (command?.Route is not { } route || command.VehicleId != run.Plan.VehicleId ||
            route.TaskId != run.TaskId || route.StepId != step.StepId || route.CommandId != command.CommandId ||
            route.VehicleId != run.Plan.VehicleId || route.MapId != run.MapId || route.MapVersionId != run.MapVersionId ||
            route.Action != action || route.VendorSites.LastOrDefault() != node.AgvSiteId)
            throw new InvalidOperationException("模拟箱体反馈缺少原车端取放动作及路线证据；纯导航不能代替取放箱。");
        bool AtOriginalStation() => vehicles.TryGet(run.Plan.VehicleId, out var vehicle) &&
            Fresh(vehicle.LastSuccessfulPollAt) && vehicle.Connection == ConnectionState.Online &&
            vehicle.Quality == SignalQuality.Good && Stopped(vehicle) && vehicle.CurrentStation == node.AgvSiteId &&
            vehicle.X is { } x && vehicle.Y is { } y && double.IsFinite(x) && double.IsFinite(y) &&
            Math.Sqrt(Math.Pow(x - node.X, 2) + Math.Pow(y - node.Y, 2)) <= .3;
        if (!AtOriginalStation()) return;
        var original = await gateway.GetTransportTaskStatusAsync(step.VendorTaskId!, ct);
        if (original is null || original.VendorTaskId != step.VendorTaskId || original.VehicleId != run.Plan.VehicleId ||
            !Fresh(original.UpdatedAt) || original.State != AgvTransportTaskState.Completed)
            throw new InvalidOperationException("原车端取放动作成功状态或身份未能核对，保留箱体点位。");
        if (!AtOriginalStation()) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { runId = run.RunId, callIndex = run.CompletedJobs,
                station = detail.Task.StationId, taskId = run.TaskId, vehicleId = run.Plan.VehicleId,
                stepId = step.StepId, vendorTaskId = step.VendorTaskId,
                action = action == "JackLoad" ? "Pickup" : "Place", observedAt = original.UpdatedAt })
        };
        request.Headers.Add("X-Agv-Lab-Control", "business-run-v1");
        using var response = await lab.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("模拟箱体反馈未接受，禁止越过本轮：" + await response.Content.ReadAsStringAsync(ct));
    }

    public static TaskStepDetail? SelectFeedbackStep(BusinessRunRecord run, TaskCenterDetail detail)
    {
        if (run.Stage != "Executing" || run.TaskId is null) return null;
        if (detail.Task.TaskId != run.TaskId || detail.Task.VehicleId is { } assigned && assigned != run.Plan.VehicleId ||
            run.Plan.Mode == "Cycle" && detail.Task.StationId != run.Plan.Stations[run.CompletedJobs % run.Plan.Stations.Length] ||
            run.Plan.Mode == "Demand" && !run.Plan.Stations.Contains(detail.Task.StationId, StringComparer.Ordinal))
            throw new InvalidOperationException("模拟箱体反馈与本轮任务/原车/工位身份不符。");
        if (detail.Task.Stage is DispatchTaskStage.Completed or DispatchTaskStage.Cancelled or DispatchTaskStage.Faulted or
            DispatchTaskStage.CancellationPending or DispatchTaskStage.CancellationReview)
            return null;
        foreach (var pair in new[] {
            (DispatchTaskStepType.GoToPickup, DispatchTaskStepType.PickupFullBox),
            (DispatchTaskStepType.ReturnEmptyBox, DispatchTaskStepType.PlaceEmptyBox) })
        {
            var waiting = detail.Steps.SingleOrDefault(s => s.StepType == pair.Item2);
            if (waiting?.Status is not (DispatchTaskStepStatus.Ready or DispatchTaskStepStatus.Executing)) continue;
            if (detail.Task.VehicleId != run.Plan.VehicleId)
                throw new InvalidOperationException("模拟箱体反馈缺少原任务已绑定车辆的证据。");
            if (detail.Steps.OrderBy(s => s.Sequence).FirstOrDefault(s => s.Status != DispatchTaskStepStatus.Completed)?.StepId != waiting.StepId)
                throw new InvalidOperationException("模拟箱体反馈所需前序步骤尚未闭合。");
            var transport = detail.Steps.SingleOrDefault(s => s.StepType == pair.Item1);
            if (transport is not { Status: DispatchTaskStepStatus.Completed, StartedAt: { }, CompletedAt: { } } ||
                transport.VehicleId != run.Plan.VehicleId || string.IsNullOrWhiteSpace(transport.VendorTaskId) ||
                transport.CompletedAt < transport.StartedAt || transport.CompletedAt < detail.Task.CreatedAt ||
                transport.CompletedAt > DateTimeOffset.UtcNow)
                throw new InvalidOperationException("模拟箱体反馈缺少本轮原运输步骤完成证据。");
            return transport;
        }
        return null;
    }
}
