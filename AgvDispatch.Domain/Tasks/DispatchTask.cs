namespace AgvDispatch.Domain.Tasks;

public sealed class DispatchTask
{
    public required string TaskId { get; init; }
    public required string StationId { get; init; }
    public string? VehicleId { get; set; }
    public string? VendorTaskId { get; set; }
    public DispatchTaskStage Stage { get; set; } = DispatchTaskStage.Created;
    public int Priority { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? ErrorMessage { get; set; }
}
