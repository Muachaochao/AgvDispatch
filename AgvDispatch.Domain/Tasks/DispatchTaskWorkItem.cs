namespace AgvDispatch.Domain.Tasks;

public sealed record DispatchTaskWorkItem(
    string TaskId,
    string StationId,
    string? VehicleId,
    string? VendorTaskId,
    DispatchTaskStage Stage,
    int Priority,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string? RequestedVehicleId { get; init; }
}
