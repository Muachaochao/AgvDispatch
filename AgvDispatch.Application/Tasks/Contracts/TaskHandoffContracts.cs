namespace AgvDispatch.Application.Abstractions;

public sealed record TaskHandoffSignals(
    string AtHandoff, string TransferReady, string BoxTaken, string TransferComplete, string ReceiverLoaded);

public sealed record TaskHandoffSite(
    string HandoffId,
    string MapSiteId,
    string PlcStationId,
    string ResourceId,
    string PlcContractVersion,
    string[] VehicleIds,
    bool RequiresReceiverVehicle,
    string[] ReceiverVehicleIds,
    TaskHandoffSignals Signals,
    bool RouteVerified,
    bool CalibrationVerified,
    bool Commissioned,
    string CommissioningReference);

public sealed class TaskHandoffOptions
{
    public bool Enabled { get; set; }
    public TaskHandoffSite[] Sites { get; set; } = [];

    public string[] Validate(TaskHandoffSite site)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(site.HandoffId) || site.HandoffId.Length > 64) errors.Add("交接位编号无效。");
        if (string.IsNullOrWhiteSpace(site.MapSiteId) || site.MapSiteId.Length > 128) errors.Add("交接地图站点未配置。");
        if (string.IsNullOrWhiteSpace(site.PlcStationId) || site.PlcStationId.Length > 32) errors.Add("交接PLC工位未配置。");
        if (string.IsNullOrWhiteSpace(site.ResourceId) || site.ResourceId.Length > 128) errors.Add("交接位独占资源未配置。");
        if (string.IsNullOrWhiteSpace(site.PlcContractVersion) || site.PlcContractVersion.Length > 64) errors.Add("交接PLC契约版本未配置。");
        if (site.VehicleIds is not { Length: > 0 } || site.VehicleIds.Any(string.IsNullOrWhiteSpace)) errors.Add("交接车辆范围未配置。");
        if (site.RequiresReceiverVehicle && (site.ReceiverVehicleIds is not { Length: > 0 } || site.ReceiverVehicleIds.Any(string.IsNullOrWhiteSpace)))
            errors.Add("需要接收车辆时必须配置接收车辆范围。");
        if (site.Signals is null || new[] { site.Signals.AtHandoff, site.Signals.TransferReady, site.Signals.BoxTaken,
            site.Signals.TransferComplete, site.Signals.ReceiverLoaded }.Any(string.IsNullOrWhiteSpace))
            errors.Add("交接位五个PLC信号必须全部显式配置。");
        if (!site.RouteVerified) errors.Add("交接入口、出口和退避路线尚未现场核定。");
        if (!site.CalibrationVerified) errors.Add("交接位停车、顶升及箱体包络标定尚未完成。");
        if (!site.Commissioned) errors.Add("交接位尚未完成现场联调。");
        if (string.IsNullOrWhiteSpace(site.CommissioningReference) || site.CommissioningReference.Length > 128) errors.Add("现场联调记录编号未配置。");
        return errors.ToArray();
    }

    public TaskHandoffSite[] ReadySites() => Enabled ? Sites.Where(site => Validate(site).Length == 0).ToArray() : [];
}
