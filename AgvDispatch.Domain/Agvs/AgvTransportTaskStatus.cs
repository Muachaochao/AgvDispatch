namespace AgvDispatch.Domain.Agvs;

public enum AgvTransportTaskState
{
    Unknown = 0,
    Queued = 10,
    Assigned = 20,
    GoingToSource = 30,
    AtSource = 40,
    Loading = 50,
    GoingToDestination = 60,
    AtDestination = 70,
    Completed = 80,
    Failed = 90,
    Cancelled = 100,
    Suspended = 110
}

public sealed record AgvTransportTaskStatus(
    string VendorTaskId,
    string BusinessTaskId,
    AgvTransportTaskState State,
    string? VehicleId,
    DateTimeOffset UpdatedAt,
    string? Message = null);
