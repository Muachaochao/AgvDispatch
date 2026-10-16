using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
namespace AgvDispatch.Infrastructure.Persistence;

public static class ExecutionIdentityVerifier
{
    public static RecoveryIdentityVerification Verify(string taskId, string stationId, string businessStepId, string? vendorTaskId,
        string? vehicleId, PlcExecutionIdentity? plc, VehicleContainerIdentity? vehicle, ExecutionIdentityBaseline? baseline,
        DateTimeOffset started, DateTimeOffset now, TimeSpan maxAge, bool require)
    {
        RecoveryIdentityVerification Missing(string reason) => new("Unavailable", "无法自动核验：" + reason + "；保留现场核对。",
            require ? "身份核验要求已启用，但" + reason : null, plc, vehicle, baseline);
        RecoveryIdentityVerification Block(string reason) => new("Blocked", "身份核验冲突：" + reason, "身份核验冲突：" + reason, plc, vehicle, baseline);
        bool Present(string? id, int size = 128) => !string.IsNullOrWhiteSpace(id) && id.Length <= size;
        bool Fresh(DateTimeOffset at) => at >= started && at <= now.AddSeconds(1) && now - at <= maxAge;
        if (plc == null && vehicle == null) return Missing("设备身份字段尚未接入");
        if (plc != null)
        {
            if (plc.SchemaVersion != 1 || !plc.IsCoherent || plc.Quality != SignalQuality.Good || !Fresh(plc.ObservedAt)) return Block("PLC身份样本缺失质量、一致性或本批次新鲜时间");
            if (!Present(plc.DeviceSessionId,64) || !Present(plc.CycleId,64) || !Present(plc.ContainerId) || !Present(plc.OriginalBusinessStepId,64)) return Block("PLC身份字段不完整或长度无效");
            if (plc.StationId != stationId || plc.TaskId != taskId || plc.OriginalBusinessStepId != businessStepId) return Block("PLC工位、业务任务或原运输业务编号不匹配");
            if (vendorTaskId != null && plc.OriginalVendorTaskId != null && plc.OriginalVendorTaskId != vendorTaskId) return Block("PLC关联的原厂家运输编号不匹配");
        }
        if (vehicle != null)
        {
            if (vehicle.SchemaVersion != 1 || !vehicle.IsCoherent || vehicle.Quality != SignalQuality.Good || !Fresh(vehicle.ObservedAt)) return Block("车辆容器身份样本缺失质量、一致性或本批次新鲜时间");
            if (vehicle.VehicleId != vehicleId || !Enum.IsDefined(vehicle.LoadState)) return Block("车辆归属或容器状态无效");
            if (vehicle.LoadState == ContainerLoadState.Empty && !string.IsNullOrEmpty(vehicle.ContainerId)) return Block("车辆宣称空载但同时返回容器编号");
            if (vehicle.LoadState == ContainerLoadState.Loaded && (!Present(vehicle.ContainerId) || vehicle.OriginalBusinessStepId != businessStepId ||
                vendorTaskId != null && vehicle.OriginalVendorTaskId != vendorTaskId)) return Block("承载容器的编号或原运输归属不匹配");
            if (plc != null && vehicle.LoadState == ContainerLoadState.Loaded && vehicle.ContainerId != plc.ContainerId) return Block("PLC周期容器与车辆承载容器不同");
        }
        if (plc == null || vehicle == null || vehicle.LoadState == ContainerLoadState.Unknown) return Missing("PLC周期或车辆容器状态尚不完整");
        if (vendorTaskId == null || plc.OriginalVendorTaskId == null) return Missing("原厂家运输编号尚未建立可靠关联");
        var original = baseline?.Plc;
        if (original == null) return Missing("原执行意图没有PLC身份基线，不能用当前值反推历史周期");
        if (original.SchemaVersion != 1 || !original.IsCoherent || original.Quality != SignalQuality.Good ||
            baseline!.CapturedAt - original.ObservedAt > maxAge || original.ObservedAt > baseline.CapturedAt.AddSeconds(1)) return Block("原执行身份基线的质量、一致性或采集时间无效");
        if (original.TaskId != taskId || original.StationId != stationId || original.OriginalBusinessStepId != businessStepId ||
            original.OriginalVendorTaskId != null && original.OriginalVendorTaskId != vendorTaskId) return Block("原执行基线不能关联本任务和原运输");
        if (original.DeviceSessionId != plc.DeviceSessionId) return Block("PLC设备启动编号已变化，不能复用重启前周期");
        if (original.CycleId != plc.CycleId) return Block("PLC动作周期号已变化");
        if (original.ContainerId != plc.ContainerId) return Block("PLC周期的容器身份已变化");
        return new("Verified", $"身份字段已匹配：周期 {plc.DeviceSessionId}/{plc.CycleId}；容器 {plc.ContainerId}；原运输 {businessStepId}/{vendorTaskId}；车辆{(vehicle.LoadState == ContainerLoadState.Empty ? "明确空载" : "承载同一容器")}。物理动作仍须按原握手及现场核对。", null, plc, vehicle, baseline);
    }
}
