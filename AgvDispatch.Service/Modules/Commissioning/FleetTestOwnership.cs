namespace AgvDispatch.Service;

public sealed class CommissioningInterlock(BusinessRunOwnership single,FleetTestOwnership fleet)
    : AgvDispatch.Application.Abstractions.IBusinessRunInterlock
{ public bool Active=>single.Active||fleet.Active; }

public sealed class FleetTestOptions
{
    public bool Enabled { get; set; }
    public string[] VehicleIds { get; set; } = [];
    public double BlockedHoldSeconds { get; set; } = 3;
    public bool ReturnToStandby { get; set; } = true;

    public string? ConfigurationError(int maxConcurrentTasks) =>
        !Enabled ? "请使用多车联调启动配置。" :
        VehicleIds is not { Length: >= 2 and <= 1000 } || VehicleIds.Any(string.IsNullOrWhiteSpace) ||
        VehicleIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != VehicleIds.Length
            ? "多车联调须配置至少两辆编号不同的车辆。" :
        !double.IsFinite(BlockedHoldSeconds) || BlockedHoldSeconds is < 1 or > 30
            ? "多车阻挡确认时间须为1至30秒。" :
        maxConcurrentTasks < 1 || maxConcurrentTasks > VehicleIds.Length
            ? $"调度并发上限须在1至配置车辆数{VehicleIds.Length}之间。" : null;
}

// The ordinary dispatcher remains the only vehicle allocator and motion executor.
public sealed class FleetTestOwnership(FleetTestOptions options)
{
    private readonly object sync = new();
    private string[] tasks = [];
    private string[] selectedVehicles = [];
    private bool active, dispatch;
    public bool Active { get { lock(sync) return active; } }
    public void Set(bool isActive, bool mayDispatch, IEnumerable<string> taskIds, IEnumerable<string>? vehicleIds = null)
    { lock(sync) { active=isActive; dispatch=mayDispatch; tasks=taskIds.ToArray(); selectedVehicles=(vehicleIds??options.VehicleIds).ToArray(); } }
    public bool Allows(string taskId, string vehicleId)
    {
        lock(sync) return !options.Enabled&&!active || options.Enabled&&active && dispatch && tasks.Contains(taskId,StringComparer.Ordinal) &&
            options.VehicleIds.Contains(vehicleId,StringComparer.OrdinalIgnoreCase) &&
            selectedVehicles.Contains(vehicleId,StringComparer.OrdinalIgnoreCase);
    }
}
