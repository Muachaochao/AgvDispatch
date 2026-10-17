using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class TaskCenterViewModel
{
    private void SetCreationRouteActionDetails(ManualTaskRouteSegment segment)
    {
        // Use only metadata attached to this observation; never borrow a different options snapshot.
        var steps=_creationMapSteps;
        var expected=new[]{"GoToPickup","PickupFullBox","DeliverFullBoxToDumper","WaitForDumperReady","EnterDumpingPosition","DumpFullBox","ReturnEmptyBox","PlaceEmptyBox","CompletePlcHandshake"};
        if(steps.Count!=9||steps.Any(s=>s is null)||steps.Select(s=>s.Sequence).Distinct().Count()!=9||!Enumerable.Range(1,9).All(n=>steps.Count(s=>s.Sequence==n&&s.StepType.ToString()==expected[n-1])==1)||steps.Any(s=>s.ValidationErrors is null||s.ValidationErrors.Count>100||s.ValidationErrors.Any(e=>e is null||e.Length>8192)||new[]{s.Name,s.SourceSite,s.DestinationSite,s.PlcStationId,s.VehicleAction,s.WaitingCondition,s.PlcHandshake,s.ConfigurationNote}.Any(t=>t is null||t.Length>8192)))
        {CreationRouteActionDetails="本次后台未提供完整且一致的九步动作/PLC配置，未关联其他批次数据。请更新后台并重新查询。";return;}
        var step=steps.Single(s=>s.Sequence==segment.Sequence);
        var parts=CreationRouteSegments.Where(s=>s.Sequence==segment.Sequence).OrderBy(s=>s.Segment).ToArray();
        if(parts.Length==0||!parts.Select(s=>s.Segment).SequenceEqual(Enumerable.Range(1,parts.Length))||(parts[0].LogicalFrom!=step.SourceSite&&parts[0].VendorFrom!=step.SourceSite&&segment.Sequence!=1)||(parts[^1].LogicalTo!=step.DestinationSite&&parts[^1].VendorTo!=step.DestinationSite)||parts.Zip(parts.Skip(1)).Any(p=>p.First.LogicalTo!=p.Second.LogicalFrom))
        {CreationRouteActionDetails="本次运输子段与步骤端点不一致，无法可靠关联动作/PLC条件。";return;}
        var terminal=segment.Segment==parts[^1].Segment;
        var lines=new List<string>{"配置要求 · 非实时信号，不表示条件已满足",$"第{step.Sequence}步 {step.Name} · 车辆 {_creationMapVehicle}",$"业务末站 {parts[^1].LogicalTo} / 厂家末站 {parts[^1].VendorTo}",terminal?"当前为末子段：附加动作在整段运输末站执行。":"当前为途经子段：不在此处单独执行末站附加动作。",$"车辆动作：{step.VehicleAction}",$"运输完成条件：{step.WaitingCondition}",$"运输握手：{step.PlcHandshake}"};
        var end=steps.Where(s=>s.Sequence>step.Sequence&&new[]{1,3,5,7}.Contains(s.Sequence)).Select(s=>s.Sequence).DefaultIfEmpty(10).Min();
        foreach(var following in steps.Where(s=>s.Sequence>step.Sequence&&s.Sequence<end).OrderBy(s=>s.Sequence))
        {
            lines.Add($"随后第{following.Sequence}步 {following.Name} · PLC {following.PlcStationId}");
            lines.Add("等待："+following.WaitingCondition);lines.Add("握手："+following.PlcHandshake);
            lines.Add("说明："+following.ConfigurationNote);
            if(following.ValidationErrors.Count>0)lines.Add("配置错误："+string.Join("；",following.ValidationErrors.Take(20)));
        }
        if(step.ValidationErrors.Count>0)lines.Add("运输配置错误："+string.Join("；",step.ValidationErrors.Take(20)));
        CreationRouteActionDetails=string.Join("\n",lines)+"\n实际执行与Good质量由后台逐步判断；本页只读，不发送动作或PLC写入。";
    }
}
