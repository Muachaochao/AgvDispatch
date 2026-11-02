using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Application.Abstractions;

public sealed class ChargingOptions
{
    public bool Enabled { get; set; }
    public bool AllowTimedTests { get; set; }
    public decimal StartBatteryPercent { get; set; } = 25;
    public decimal CriticalBatteryPercent { get; set; } = 15;
    public decimal TargetBatteryPercent { get; set; } = 80;
    public int PollIntervalMs { get; set; } = 2000;
    public int NavigationTimeoutSeconds { get; set; } = 600;
    public int CurrentTimeoutSeconds { get; set; } = 60;
    public int ChargingTimeoutSeconds { get; set; } = 14400;
    public int StopTimeoutSeconds { get; set; } = 60;
    public int StateMaxAgeSeconds { get; set; } = 5;
    public double MinimumChargingCurrent { get; set; } = 0.1;
    public double MaximumTemperature { get; set; } = 45;
    public ChargingSite[] Sites { get; set; } = [];
    public string? ConfigurationError() =>
        CriticalBatteryPercent <= 0 || CriticalBatteryPercent > StartBatteryPercent || StartBatteryPercent >= TargetBatteryPercent || TargetBatteryPercent > 100
            ? "充电阈值须满足0<严重电量≤启动电量<退出电量≤100。" :
        PollIntervalMs < 100 || NavigationTimeoutSeconds < 1 || CurrentTimeoutSeconds < 1 || ChargingTimeoutSeconds < 1 || StopTimeoutSeconds < 1 || StateMaxAgeSeconds < 1 ||
        !double.IsFinite(MinimumChargingCurrent) || MinimumChargingCurrent <= 0 || !double.IsFinite(MaximumTemperature) || MaximumTemperature is <= 0 or > 100
            ? "充电超时、采集时效、电流或温度参数无效。" :
        Sites is null || Sites.Any(s => new[] {s.Id,s.ChargingSiteId,s.ExitSiteId,s.ResourceId}.Any(string.IsNullOrWhiteSpace) || s.ChargingSiteId == s.ExitSiteId || s.AllowedVehicleIds is null) ||
        Sites.Select(s=>s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=Sites.Length || Sites.Select(s=>s.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=Sites.Length ||
        Sites.Select(s=>s.ChargingSiteId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=Sites.Length
            ? "充电位、出口和独占资源配置无效或重复。" : null;
}

// Commissioned means the actual dock, relay, current polarity and safe exit have
// been confirmed for this vehicle. A ChargingPoint alone never enables motion.
public sealed record ChargingSite(string Id, string ChargingSiteId, string ExitSiteId, string ResourceId)
{
    public bool Commissioned { get; init; }
    public string[] AllowedVehicleIds { get; init; } = [];
    public string ControlMode { get; init; } = "Relay6005";
}

// Append phases: persisted older sessions use the original numeric enum values.
public enum ChargingPhase { Queued, WaitingForBusiness, Reserved, SendingToCharger, GoingToCharger, StartingCharge, AwaitingCurrent, Charging, StoppingCharge, AwaitingStop, SendingExit, Exiting, Hold, Completed,
    SendingDock, Docking, StartingSession, SessionRunning, SendingUndock, Undocking, SendingReturn, Returning }
public sealed record ChargingRoute(string MapId, string MapVersionId, string[] ToCharger, string[] ExitPath, string[] ResourceIds, double Cost)
{
    public string? OriginSiteId { get; init; }
    public string? ReturnSiteId { get; init; }
    public string[] ReturnPath { get; init; } = [];
}
public sealed record ChargingSession(string SessionId, string VehicleId, ChargingPhase Phase, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Message)
{
    public ChargingSite? Site { get; init; }
    public ChargingRoute? Route { get; init; }
    public string? CommandId { get; init; }
    public string? VendorTaskId { get; init; }
    public ChargingPhase? HeldPhase { get; init; }
    public DateTimeOffset PhaseSince { get; init; } = CreatedAt;
    public bool StartAttempted { get; init; }
    public bool StopAttempted { get; init; }
    public bool StopRequested { get; init; }
    public ChargingBatteryReading? Battery { get; init; }
    public string? Operator { get; init; }
    public string? OperatorEvidence { get; init; }
    public string? RequestId { get; init; }
    public decimal? TargetBatteryPercent { get; init; }
    public int? RequiredPositiveSeconds { get; init; }
    public string? RequestedReturnSiteId { get; init; }
    public string? ScriptOperationId { get; init; }
    public string? ChargeOperationId { get; init; }
    public ChargingWorkflowKind? ScriptOperationKind { get; init; }
    public ChargingWorkflowStatus? WorkflowStatus { get; init; }
    public bool ShutdownVerified { get; init; }
    public string? OriginSiteId => Route?.OriginSiteId;
    public string? ReturnSiteId => Route?.ReturnSiteId;
    public bool HasReservation => Site is not null && Route is not null && Phase != ChargingPhase.Completed;
}
public sealed record ChargingStatus(bool Enabled, ChargingOptions Options, string? BlockReason, IReadOnlyList<ChargingSession> Sessions, DateTimeOffset CapturedAt);
public sealed record ChargingStopRequest(string SessionId, string Reason);
public sealed record ChargingRequest(string RequestId, string VehicleId, int? RequiredPositiveSeconds = null, string? ReturnSiteId = null);
public enum ChargingHoldDecision { ContinueOriginal, CloseAfterPhysicalExit }
public sealed record ChargingResolveRequest(string SessionId, ChargingHoldDecision Decision, string Reason, bool VehicleAndLoadConfirmed, bool ChargerAndLocationConfirmed);
public sealed record ChargingResult(bool Accepted, string Message);

public interface IChargingDispatchGuard
{
    bool HasActiveSessions { get; }
    bool HasReservedSessions => HasActiveSessions;
    bool OwnsVehicle(string vehicleId);
    // Lock order for every allocator: charging lease, then map execution lease.
    ValueTask<IDisposable> EnterBusinessAsync(CancellationToken ct);
    bool AllowsNewBusiness(string vehicleId);
}
public interface IChargingService : IChargingDispatchGuard
{
    Task<ChargingStatus> ReadAsync(CancellationToken ct);
    Task<ChargingResult> RequestAsync(TrafficUserContext user, ChargingRequest request, CancellationToken ct) =>
        Task.FromResult(new ChargingResult(false, "当前实现不支持手动充电申请。"));
    Task<ChargingResult> RequestStopAsync(TrafficUserContext user, ChargingStopRequest request, CancellationToken ct);
    Task<ChargingResult> ResolveHoldAsync(TrafficUserContext user, ChargingResolveRequest request, CancellationToken ct);
}

// A narrow port makes the persisted workflow testable without robot connections.
public interface IChargingRuntimePort
{
    IReadOnlyList<AgvRobotSnapshot> Vehicles { get; }
    string? NewWorkBlockReason { get; }
    ValueTask<IDisposable?> EnterExecutionAsync(bool newWork, CancellationToken ct);
    ValueTask<IDisposable?> EnterRecoveryControlAsync(CancellationToken ct) => EnterExecutionAsync(false,ct);
    Task<string?> BusyReasonAsync(string vehicleId, string? ownSessionId, CancellationToken ct);
    Task<ChargingRoute?> PlanAsync(string vehicleId, ChargingSite site, CancellationToken ct);
    Task<ChargingRoute?> PlanWithReturnAsync(string vehicleId, ChargingSite site, string? returnSiteId, CancellationToken ct) => PlanAsync(vehicleId,site,ct);
    Task<string?> ValidateAsync(ChargingSession session, bool requireStopped, CancellationToken ct);
    Task<string?> ValidateRecoveryCloseAsync(ChargingSession session,CancellationToken ct) => ValidateAsync(session,true,ct);
    bool IsAt(string vehicleId, string siteId);
    Task<bool> ReserveAsync(ChargingSession session, CancellationToken ct);
    Task RenewAsync(string sessionId, CancellationToken ct);
    Task ReleaseAsync(string sessionId, CancellationToken ct);
    Task<string> NavigateAsync(string commandId, string vehicleId, IReadOnlyList<string> path, CancellationToken ct);
    Task CancelNavigationAsync(ChargingSession session, CancellationToken ct) => throw new NotSupportedException("未提供原充电导航定向取消。");
    Task<AgvTransportTaskStatus?> ObserveNavigationAsync(string commandId, string vehicleId, IReadOnlyList<string> path, CancellationToken ct);
}
