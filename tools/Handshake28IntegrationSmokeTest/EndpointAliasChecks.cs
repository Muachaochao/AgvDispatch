using System.Reflection;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Service;

static class EndpointAliasChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var task = await fixture.PrepareReleaseAsync();
        var parent = await fixture.TaskAsync(task);
        var returned = (await fixture.Writer.GetStepsAsync(task, default)).Single(s => s.StepType == DispatchTaskStepType.ReturnEmptyBox);
        var step = new DispatchTaskStepWorkItem(task, parent.StationId, parent.VehicleId, parent.Stage,
            returned.StepId, returned.Sequence, returned.StepType, returned.Status, returned.SourceSite, returned.DestinationSite, returned.UpdatedAt);
        fixture.Agv.State = AgvTransportTaskState.Completed;
        var method = typeof(PersistentHandshakeExecutor).GetMethod("CaptureExitProofCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task<TaskDumperExitProof> Capture(string destination = "PICK-1", double? velocity = 0)
        {
            await Task.Delay(120);
            fixture.Vehicles.Set("AGV-1", 10, 0, "LM-PICK-1");
            fixture.Vehicles.SetVelocity("AGV-1", velocity, 0, 0);
            return (await (Task<TaskDumperExitProof?>)method.Invoke(fixture.Engine,
                [step with { DestinationSite = destination }, returned.VendorTaskId!, CancellationToken.None])!)!;
        }
        check((await Capture()).EndpointArrivalConfirmed,
            "出区返程终点将逻辑PICK-1映射为厂家LM-PICK-1，确认原任务完成且停车");
        check((await Capture("lm-pick-1")).EndpointArrivalConfirmed, "返程目的地的唯一厂家别名也可核验终点");
        check(!(await Capture(velocity: .1)).EndpointArrivalConfirmed && !(await Capture(velocity: null)).EndpointArrivalConfirmed,
            "移动中或缺少速度时不能声称终点停车");
        check(!(await Capture("UNKNOWN")).EndpointArrivalConfirmed, "未知返程终点不能推测站点映射");
        var original = fixture.Map.Current!;
        fixture.Map.Replace(new(original.MapId, original.VersionId,
            original.Definition with { Nodes = original.Definition.Nodes.Select(n => n.Id == "PICK-2" ? n with { AgvSiteId = "LM-PICK-1" } : n).ToArray() }, DateTimeOffset.UtcNow));
        check(!(await Capture()).EndpointArrivalConfirmed, "重复厂家站点映射不能证明返程终点");
        fixture.Map.Replace(new(original.MapId, original.VersionId,
            original.Definition with { Nodes = original.Definition.Nodes.Select(n => n.Id == "PICK-1" ? n with { Enabled = false } : n).ToArray() }, DateTimeOffset.UtcNow));
        check(!(await Capture()).EndpointArrivalConfirmed, "禁用目的节点不能证明返程终点");
        fixture.Map.Replace(original);
        fixture.Agv.State = AgvTransportTaskState.GoingToDestination;
        check(!(await Capture()).EndpointArrivalConfirmed, "返程原任务未完成时不因站点与速度相符而伪造到站");
    }
}
