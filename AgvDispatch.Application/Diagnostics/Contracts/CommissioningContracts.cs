namespace AgvDispatch.Application.Abstractions;

public sealed record BackendExecutionDiagnostic(string BuildId, string InformationalVersion, string Mode,
    bool HandshakeEnabled, string ConfiguredContract, string SupportedContract,
    bool TaskCreationEnabled, bool StepExecutionEnabled, bool OutboxEnabled);

public sealed record CommissioningQuery(string StationId = "", string VehicleId = "");
public sealed record CommissioningCheck(string Id, string Group, string Status, string Title,
    string Reason, string Target, string Action, DateTimeOffset? ObservedAt = null);
public sealed record CommissioningSignal(string StationId, string VariableName, string Name, string Address,
    string Writer, bool? Value, string Quality, DateTimeOffset? ObservedAt, string Status, string Reason);
public sealed record CommissioningReport(DateTimeOffset GeneratedAt, BackendExecutionDiagnostic Runtime,
    string ServiceVersion, DateTimeOffset ServiceStartedAt, string? MapId, string? MapVersion,
    string ParameterRevision, string StationId, string VehicleId, ManualTaskCreationOptions Options,
    IReadOnlyList<CommissioningCheck> Checks, IReadOnlyList<CommissioningSignal> Signals);

public interface ICommissioningQueryService
{
    Task<CommissioningReport> ReadAsync(TrafficUserContext user, CommissioningQuery query, CancellationToken ct);
}
