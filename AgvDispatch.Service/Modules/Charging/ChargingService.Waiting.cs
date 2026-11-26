using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Service;

public sealed partial class ChargingService
{
    private ChargingSession? BayOwner(ChargingSite site) => sessions.FirstOrDefault(other => other.HasBayReservation && other.Site!.ResourceId.Equals(site.ResourceId,StringComparison.OrdinalIgnoreCase));

    private double? EstimateBayWait(ChargingSession? owner)
    {
        if(owner is null)return 0;
        var gain=options.EstimatedGainPercentPerMinuteByVehicle.GetValueOrDefault(owner.VehicleId,options.EstimatedGainPercentPerMinute);
        if(owner.Phase is not (ChargingPhase.Charging or ChargingPhase.SessionRunning) || owner.StopRequested ||
            owner.RequiredPositiveSeconds is not null || gain<=0 ||
            owner.Battery is not { BatteryPercent: {} percent } battery || !Fresh(battery.ObservedAt))return null;
        var remaining=Math.Max(0,(double)((owner.TargetBatteryPercent??options.TargetBatteryPercent)-percent));
        return remaining*60/gain+options.EstimatedDockExitSeconds;
    }

    private void UpdateWaitingReason(ChargingSession row,string message)
    {
        if(row.Message!=message)Save(row with {Message=message,UpdatedAt=Now});
    }

    private async Task TrySendWaitingAsync(ChargingSession row,CancellationToken ct)
    {
        if(await port.PlanWaitingAsync(row,ct) is not {} route)
        {
            UpdateWaitingReason(row,"等空桩：保持原位置，等待可用等桩位和足够续航。");
            return;
        }
        row=Move(row with {WaitingRoute=route,CommandId=row.SessionId+"-WAIT",VendorTaskId=null},
            ChargingPhase.SendingToWaitingSite,"等空桩：保存等桩导航意图并申请停车位和进路。");
        if(await port.ValidateWaitingAsync(row,ct) is not null || !await port.ReserveWaitingAsync(row,ct))
        {
            await port.TrimWaitingAsync(row,true,ct);
            Move(row with {WaitingRoute=null,CommandId=null},ChargingPhase.Queued,"等空桩：安全等桩位进路或停车资源暂不可用。");
            return;
        }
        if(port.NewWorkBlockReason is not null || await port.BusyReasonAsync(row.VehicleId,row.SessionId,ct) is not null ||
            await port.ValidateWaitingAsync(row,ct) is {} )
        {
            await port.TrimWaitingAsync(row,true,ct);
            Move(row with {WaitingRoute=null,CommandId=null},ChargingPhase.Queued,"等桩导航发送前复核未通过，已退回队列。");
            return;
        }
        var vendor=await port.NavigateAsync(row.CommandId!,row.VehicleId,route.Path,ct);
        if(vendor!=row.CommandId)throw new InvalidDataException("等桩导航回执与原命令编号不符。");
        Move(row with {VendorTaskId=vendor},ChargingPhase.GoingToWaitingSite,$"等空桩：前往安全等待位 {route.Path[^1]}。");
    }

    private async Task ObserveWaitingAsync(ChargingSession row,CancellationToken ct)
    {
        var route=row.WaitingRoute!;
        var command=row.VendorTaskId??row.CommandId!;
        var status=await port.ObserveNavigationAsync(command,row.VehicleId,route.Path,ct);
        if(status is not null && Fresh(status.UpdatedAt))
        {
            if(status.VendorTaskId!=command || !string.Equals(status.VehicleId,row.VehicleId,StringComparison.OrdinalIgnoreCase) ||
                status.State is AgvTransportTaskState.Cancelled or AgvTransportTaskState.Failed or AgvTransportTaskState.Suspended)
            {Hold(row,"原等桩导航回执异常，保留停车归属与进路。");return;}
            if(status.State==AgvTransportTaskState.Completed && port.IsAt(row.VehicleId,route.Path[^1]))
            {
                await port.TrimWaitingAsync(row,false,ct);
                Move(row,ChargingPhase.Queued,$"等空桩：已在 {route.Path[^1]} 停稳。");
                return;
            }
        }
        if(TimedOut(row,options.NavigationTimeoutSeconds))Hold(row,"等桩导航超时，保留停车归属与进路。");
    }
}
