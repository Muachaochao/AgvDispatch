namespace AgvDispatch.Service;

public sealed class CommissioningInterlock(BusinessRunOwnership single,FleetTestOwnership fleet)
    : AgvDispatch.Application.Abstractions.IBusinessRunInterlock
{ public bool Active=>single.Active||fleet.Active; }

public sealed class FleetTestOptions
{
    public bool Enabled { get; set; }
    public string[] VehicleIds { get; set; } = [];
}

// The ordinary dispatcher remains the only vehicle allocator and motion executor.
public sealed class FleetTestOwnership(FleetTestOptions options)
{
    private readonly object sync = new();
    private string[] tasks = [];
    private bool active, dispatch;
    public bool Active { get { lock(sync) return active; } }
    public void Set(bool isActive, bool mayDispatch, IEnumerable<string> taskIds)
    { lock(sync) { active=isActive; dispatch=mayDispatch; tasks=taskIds.ToArray(); } }
    public bool Allows(string taskId, string vehicleId)
    {
        lock(sync) return !options.Enabled&&!active || options.Enabled&&active && dispatch && tasks.Contains(taskId,StringComparer.Ordinal) &&
            options.VehicleIds.Contains(vehicleId,StringComparer.Ordinal);
    }
}
