namespace AgvDispatch.Application.Abstractions;

public sealed record BusinessRunStart(string RequestId, string Mode, string VehicleId,
    string[] Stations, int Rounds, string StandbySite, bool SiteConfirmed);
public sealed record BusinessRunStop(string RunId);
public sealed record BusinessRunRecord(string RunId, BusinessRunStart Plan, string MapId, string MapVersionId,
    string ParameterRevision, string Operator, string Stage, string Message, int CompletedJobs,
    string? TaskId, string? PendingRequestId, string? VendorTaskId, bool StopRequested, DateTimeOffset UpdatedAt);
public sealed record BusinessRunStatus(BusinessRunRecord? Current, string Message);
public sealed record BusinessRunObservation(string MapId, string MapVersionId, string ParameterRevision,
    string? Block, string[] Calls, bool AllClosed, bool AnyActiveTasks, string? CurrentSite,
    bool Stopped, string? CurrentTaskState, bool StandbyCompleted);

// The coordinator owns scheduling; the PLC tool alone generates simulated P calls.
public interface IBusinessRunPort
{
    Task ValidateAsync(BusinessRunStart plan, CancellationToken ct);
    Task<BusinessRunObservation> ObserveAsync(BusinessRunRecord run, CancellationToken ct);
    Task<string> CreateAsync(BusinessRunRecord run, string station, string requestId, CancellationToken ct);
    Task<string> ReturnAsync(BusinessRunRecord run, CancellationToken ct);
    Task SimulatorAsync(BusinessRunRecord run, bool stop, CancellationToken ct);
    Task ReleaseReturnAsync(BusinessRunRecord run, CancellationToken ct);
    Task<BusinessRunRecord> ReconcileAsync(BusinessRunRecord run, CancellationToken ct) => Task.FromResult(run);
    Task<BusinessRunRecord> RetireUnacceptedAsync(BusinessRunRecord run, CancellationToken ct) => Task.FromResult(run);
}
public interface IBusinessRunService
{
    Task<BusinessRunStatus> ReadAsync(CancellationToken ct);
    Task<BusinessRunStatus> StartAsync(TrafficUserContext user, BusinessRunStart plan, CancellationToken ct);
    Task<BusinessRunStatus> StopAsync(TrafficUserContext user, BusinessRunStop request, CancellationToken ct);
    Task<BusinessRunStatus> CloseReviewedAsync(TrafficUserContext user, BusinessRunStop request, CancellationToken ct);
}
public interface IBusinessRunInterlock { bool Active { get; } }
