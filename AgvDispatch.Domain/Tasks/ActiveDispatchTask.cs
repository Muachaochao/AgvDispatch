namespace AgvDispatch.Domain.Tasks;

public sealed record ActiveDispatchTask(
    string TaskId,
    string StationId,
    string? VehicleId,
    string? VendorTaskId,
    DispatchTaskStage Stage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
