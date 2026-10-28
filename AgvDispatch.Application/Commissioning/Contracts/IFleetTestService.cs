namespace AgvDispatch.Application.Abstractions;

public sealed record FleetTestStart(string RequestId, string Scenario, string[] VehicleIds, bool SiteConfirmed)
{
    public string[] StationIds { get; init; } = [];
}
public sealed record FleetTestControl(string RunId, string Reason, bool SiteConfirmed);
public sealed record FleetTestStartRetirement(string RequestId, string Reason, bool SiteConfirmed);
public sealed record FleetTestStartRetirementReceipt(string RequestId, bool Retired, string Message, DateTimeOffset RetiredAt);
public sealed record FleetTestCall(string StationId, bool Released, string? TaskId);
public static class FleetTestStandbyReturnStates
{
    public const string Assigned = "Assigned";
    public const string Sending = "Sending";
    public const string Returning = "Returning";
    public const string Completed = "Completed";
    public const string Uncertain = "Uncertain";
}
public sealed record FleetTestStandbyReturn(
    string VehicleId,
    string StandbyId,
    string Site,
    string State,
    string Message,
    string? VendorTaskId,
    DateTimeOffset UpdatedAt)
{
    public IReadOnlyList<string> Route { get; init; } = [];
    public string ResourceTaskId { get; init; } = "";
}
public sealed record FleetTestRun(string RunId, FleetTestStart Plan, string MapId, string MapVersionId,
    string ParameterRevision, string Stage, string Message, bool StopRequested, DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt, FleetTestCall[] Calls)
{
    public string Operator { get; init; }="";
    public FleetTestReview[] Reviews { get; init; }=[];
    public FleetTestHold[] Holds { get; init; }=[];
    public FleetTestStandbyReturn[] StandbyReturns { get; init; }=[];
}
public sealed record FleetTestHold(DateTimeOffset Time,string Reason);
public sealed record FleetTestReview(DateTimeOffset Time,string Operation,string Operator,string Reason);
public sealed record FleetTestVehicle(string VehicleId, string? Site, bool Fresh, string Message);
public sealed record FleetTestStatus(bool Enabled, string[] AllowedVehicles, FleetTestRun? Current,
    FleetTestVehicle[] Vehicles, TaskCenterDetail[] Tasks, DateTimeOffset ObservedAt)
{
    public string[] ConfiguredStations { get; init; } = [];
    public int MaxConcurrentTasks { get; init; }
}
public interface IFleetTestService
{
    Task<FleetTestStatus> ReadAsync(CancellationToken ct);
    Task<FleetTestStatus> StartAsync(TrafficUserContext user, FleetTestStart request, CancellationToken ct);
    Task<FleetTestStartRetirementReceipt> RetireStartAsync(TrafficUserContext user, FleetTestStartRetirement request, CancellationToken ct);
    Task<FleetTestStatus> ControlAsync(TrafficUserContext user, string action, FleetTestControl request, CancellationToken ct);
}
