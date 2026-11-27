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
    /// <summary>B07-FIX C2: consecutive failed observations (read, telemetry, validation) before a session is held.</summary>
    public int TransientFailureLimit { get; set; } = 5;
    /// <summary>B07-FIX C2: a failing observation is held after this many seconds even when the count is not reached.</summary>
    public int TransientFailureSeconds { get; set; } = 60;
    /// <summary>B07-FIX C9: a bay with no current after the start timer is not offered to other vehicles for this long.</summary>
    public int BayFaultCooldownMinutes { get; set; } = 30;
    /// <summary>B07-FIX C11: the lowest battery the dispatcher accepts (it must match the dispatch assignment minimum). A
    /// vehicle must be charged before it falls below this level, so the start threshold may not be lower.</summary>
    public decimal DispatchMinimumBatteryPercent { get; set; } = 20;
    /// <summary>B07-FIX C15: at or above this level, a current that has naturally tapered is a normal full-charge stop.</summary>
    public decimal NearFullBatteryPercent { get; set; } = 95;
    /// <summary>B07-FIX C15: a charge that gains less than ChargeStallMinimumGainPercent within this many minutes is stopped and held.</summary>
    public int ChargeStallMinutes { get; set; } = 20;
    public decimal ChargeStallMinimumGainPercent { get; set; } = 1;
    /// <summary>B07-FIX C16: vehicles whose firmware does not report battery temperature. Their missing temperature is not a fault;
    /// it is reported as an alarm instead.</summary>
    public string[] TemperatureOptionalVehicleIds { get; set; } = [];
    public string[] WaitingSiteIds { get; set; } = [];
    public double EstimatedGainPercentPerMinute { get; set; }
    public Dictionary<string,double> EstimatedGainPercentPerMinuteByVehicle { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int EstimatedDockExitSeconds { get; set; } = 30;
    public int EstimatedApproachWaitSeconds { get; set; }
    public ChargingSite[] Sites { get; set; } = [];
    /// <summary>Optional dedicated standby point for a site that explicitly requires a return after charging.
    /// It can never be an exit or a charging point.</summary>
    public string DefaultStandbySiteId { get; set; } = "";
    /// <summary>Charging completes as soon as the vehicle is safely outside the bay with charging stopped.
    /// Enable this only when the commissioned workflow requires an extra return to DefaultStandbySiteId
    /// before the vehicle may take new work.</summary>
    public bool ReturnToStandbyAfterCharging { get; set; }
    /// <summary>Minimum fleet size to retain when reserving a normal charging session.
    /// Low-battery vehicles waiting without a reservation are counted outside the simultaneous charging limit.</summary>
    public int MinimumAvailableVehicles { get; set; } = 0;
    /// <summary>Maximum concurrent reservations for normal charging. Zero leaves only the minimum fleet rule.</summary>
    public int MaximumVehiclesOutOfService { get; set; } = 0;
    /// <summary>B07-FIX C4: a charging vehicle above this level (and at least target minus the margin) may be asked to stop
    /// when a critical vehicle waits for the bay.</summary>
    public decimal PreemptionReleaseBatteryPercent { get; set; } = 50;
    public decimal PreemptionTargetMarginPercent { get; set; } = 10;
    /// <summary>B07-R8 business-first release. Zero (default) disables it. When orders wait and no
    /// in-service vehicle is free, the most-charged vehicle at or above this level is asked to stop
    /// through the normal stop and exit flow, so it can take the order instead of topping up to the
    /// target. Must lie above the start level so the released vehicle does not queue again at once.</summary>
    public decimal BusinessDemandReleaseBatteryPercent { get; set; }
    public string? ConfigurationError() => BasicConfigurationError() ?? SiteConfigurationError();
    private string? BasicConfigurationError() =>
        CriticalBatteryPercent <= 0 || CriticalBatteryPercent > StartBatteryPercent || StartBatteryPercent >= TargetBatteryPercent || TargetBatteryPercent > 100
            ? "充电阈值须满足0<严重电量≤启动电量<退出电量≤100。" :
        PollIntervalMs < 100 || NavigationTimeoutSeconds < 1 || CurrentTimeoutSeconds < 1 || ChargingTimeoutSeconds < 1 || StopTimeoutSeconds < 1 || StateMaxAgeSeconds < 1 ||
        TransientFailureLimit < 1 || TransientFailureSeconds < 1 ||
        !double.IsFinite(MinimumChargingCurrent) || MinimumChargingCurrent <= 0 || !double.IsFinite(MaximumTemperature) || MaximumTemperature is <= 0 or > 100
            ? "充电超时、采集时效、电流或温度参数无效。" :
        Sites is null || Sites.Any(s => new[] {s.Id,s.ChargingSiteId,s.ExitSiteId,s.ResourceId}.Any(string.IsNullOrWhiteSpace) || s.ChargingSiteId == s.ExitSiteId || s.AllowedVehicleIds is null) ||
        Sites.Select(s=>s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=Sites.Length || Sites.Select(s=>s.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=Sites.Length ||
        Sites.Select(s=>s.ChargingSiteId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=Sites.Length
            ? "充电位、出口和独占资源配置无效或重复。" : null;
    // B07-FIX C11: a shared exit, a standby that is also an exit or a charging point, or a standby that is not a
    // dedicated point must be rejected before any vehicle is sent.
    private string? SiteConfigurationError()
    {
        if (Sites.Select(s => s.ExitSiteId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Sites.Length)
            return "两个充电位共用同一出口，无法同时安全离位，请先核对配置。";
        if (WaitingSiteIds is null || WaitingSiteIds.Any(string.IsNullOrWhiteSpace) ||
            WaitingSiteIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != WaitingSiteIds.Length ||
            EstimatedGainPercentPerMinute is not (>= 0 and <= 100) ||
            EstimatedGainPercentPerMinuteByVehicle is null || EstimatedGainPercentPerMinuteByVehicle.Values.Any(value=>value is not (>=0 and <=100)) ||
            EstimatedDockExitSeconds is < 0 or > 3600 || EstimatedApproachWaitSeconds is < 0 or > 3600)
            return "等桩位或充电时间估算配置无效。";
        if (BayFaultCooldownMinutes < 0)
            return "充电位故障冷却时间不能为负数。";
        if (TargetBatteryPercent - StartBatteryPercent < 10m)
            return "充电启动电量与目标电量至少须相差10个百分点，避免频繁往返。";
        if (StartBatteryPercent < DispatchMinimumBatteryPercent)
            return "充电启动电量不能低于派单最低电量，否则车辆会在无法排队前即被派单阻挡。";
        if (NearFullBatteryPercent <= StartBatteryPercent || NearFullBatteryPercent > 100 || ChargeStallMinutes < 1 || ChargeStallMinimumGainPercent <= 0)
            return "近满电及充电停滞检测参数无效。";
        if (MinimumAvailableVehicles < 0 || MaximumVehiclesOutOfService < 0)
            return "车队最少可用车数及同时离线车数上限不能为负数。";
        if (PreemptionReleaseBatteryPercent is <= 0 || PreemptionReleaseBatteryPercent <= CriticalBatteryPercent || PreemptionReleaseBatteryPercent >= TargetBatteryPercent ||
            PreemptionTargetMarginPercent < 0 || PreemptionTargetMarginPercent >= 100)
            return "抢占放行电量须位于严重电量与目标电量之间，目标余量须非负。";
        if (BusinessDemandReleaseBatteryPercent != 0 &&
            (BusinessDemandReleaseBatteryPercent <= StartBatteryPercent || BusinessDemandReleaseBatteryPercent >= TargetBatteryPercent))
            return "业务优先离桩电量须为0（关闭）或位于启动电量与目标电量之间。";
        if (DefaultStandbySiteId is not null && DefaultStandbySiteId.Trim().Length > 0 &&
            Sites.Any(s => s.ExitSiteId.Equals(DefaultStandbySiteId.Trim(), StringComparison.OrdinalIgnoreCase) ||
                           s.ChargingSiteId.Equals(DefaultStandbySiteId.Trim(), StringComparison.OrdinalIgnoreCase)))
            return "充电返回待机点不能是充电出口或充电点，须为专用待机位。";
        return null;
    }
}

// Commissioned means the actual dock, relay, current polarity and safe exit have
// been confirmed for this vehicle. A ChargingPoint alone never enables motion.
public sealed record ChargingSite(string Id, string ChargingSiteId, string ExitSiteId, string ResourceId)
{
    public string ChargerId { get; init; } = "";
    public string ChargerAddress { get; init; } = "";
    public bool Commissioned { get; init; }
    public string[] AllowedVehicleIds { get; init; } = [];
    public string ControlMode { get; init; } = "Relay6005";
}

// Append phases: persisted older sessions use the original numeric enum values.
public enum ChargingPhase { Queued, WaitingForBusiness, Reserved, SendingToCharger, GoingToCharger, StartingCharge, AwaitingCurrent, Charging, StoppingCharge, AwaitingStop, SendingExit, Exiting, Hold, Completed,
    SendingDock, Docking, StartingSession, SessionRunning, SendingUndock, Undocking, SendingReturn, Returning, SendingToWaitingSite, GoingToWaitingSite }
public sealed record ChargingRoute(string MapId, string MapVersionId, string[] ToCharger, string[] ExitPath, string[] ResourceIds, double Cost)
{
    public string? OriginSiteId { get; init; }
    public string? ReturnSiteId { get; init; }
    public string[] ReturnPath { get; init; } = [];
    // B07-FIX C5: the road that leads into the bay, plus the bay itself; the only resources reserved at queue time.
    public string[] InboundResourceIds { get; init; } = [];
}
public sealed record ChargingWaitingRoute(string MapId, string MapVersionId, string[] Path,
    string ParkingResourceId, string[] ResourceIds, double Cost);
public sealed record ChargingApproachEstimate(double TravelSeconds, double? TrafficWaitSeconds);
public sealed record ChargingSiteEstimate(string SiteId, double? BayWaitSeconds, double? TrafficWaitSeconds, double? TravelSeconds)
{
    public double? StartInSeconds => BayWaitSeconds is double bay && TrafficWaitSeconds is double traffic && TravelSeconds is double travel
        ? Math.Max(bay, traffic) + travel : null;
}
public sealed record ChargingSession(string SessionId, string VehicleId, ChargingPhase Phase, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Message)
{
    public ChargingWaitingRoute? WaitingRoute { get; init; }
    public ChargingSiteEstimate[] SiteEstimates { get; init; } = [];
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
    // B07-FIX C2: a held observation resumes by itself once the observation is clear; these count the failures so far.
    public bool TransientHold { get; init; }
    // B07-FIX C9: set when the bay gave no current after the start timer; the bay is in cooldown until this + BayFaultCooldownMinutes.
    public DateTimeOffset? BayFaultAt { get; init; }
    // B07-FIX C15: the battery level and time of the last observed gain, used to detect a charge that does not progress.
    public decimal? ChargeProgressPercent { get; init; }
    public DateTimeOffset? ChargeProgressAt { get; init; }
    public int TransientFailures { get; init; }
    public DateTimeOffset? TransientSince { get; init; }
    public string? OriginSiteId => Route?.OriginSiteId;
    public string? ReturnSiteId => Route?.ReturnSiteId;
    public bool HasReservation => Site is not null && Route is not null && Phase != ChargingPhase.Completed;
    public bool HasBayReservation => HasReservation && !(Site!.ControlMode == "Relay6005" &&
        (Phase == ChargingPhase.Returning || Phase == ChargingPhase.SendingReturn ||
            Phase == ChargingPhase.Hold && HeldPhase is ChargingPhase.SendingReturn or ChargingPhase.Returning));
}
public sealed record ChargingStatus(bool Enabled, ChargingOptions Options, string? BlockReason,
    IReadOnlyList<ChargingSession> Sessions, DateTimeOffset CapturedAt)
{
    public IReadOnlyList<ChargerTelemetrySnapshot> Chargers { get; init; } = [];
}
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
    /// <summary>B07-FIX C9/C12: charging alarms for the alarm rule engine (bay fault cooldown, unknown battery).</summary>
    IReadOnlyList<AlarmCandidate> AlarmCandidates(DateTimeOffset now) => [];
    Task<ChargingResult> RequestAsync(TrafficUserContext user, ChargingRequest request, CancellationToken ct) =>
        Task.FromResult(new ChargingResult(false, "当前实现不支持手动充电申请。"));
    Task<ChargingResult> RequestStopAsync(TrafficUserContext user, ChargingStopRequest request, CancellationToken ct);
    Task<ChargingResult> ResolveHoldAsync(TrafficUserContext user, ChargingResolveRequest request, CancellationToken ct);
}

public sealed class ChargerMonitoringOptions
{
    public bool Enabled { get; set; } = true;
    public string[] Addresses { get; set; } = [];
    public ChargerEndpoint[] Chargers { get; set; } = [];
    public int AckPort { get; set; } = 20207;
    public int StatusPort { get; set; } = 20204;
    public int PollIntervalMs { get; set; } = 5000;
    public int ConnectTimeoutMs { get; set; } = 2000;
    public string? ConfigurationError() =>
        Addresses.Any(value => !System.Net.IPAddress.TryParse(value,out _)) ||
        Chargers.Any(value => string.IsNullOrWhiteSpace(value.Id) || string.IsNullOrWhiteSpace(value.Name) ||
            !System.Net.IPAddress.TryParse(value.Address,out _) ||
            value.AckPort is < 1 or > 65535 || value.StatusPort is < 1 or > 65535) ||
        AckPort is < 1 or > 65535 || StatusPort is < 1 or > 65535 ||
        PollIntervalMs is < 1000 or > 60000 || ConnectTimeoutMs is < 250 or > 10000
            ? "充电桩监控地址、端口、周期或超时无效。"
            : null;
}

public sealed record ChargerEndpoint(string Id, string Name, string Address, int AckPort, int StatusPort);

public sealed record ChargerTelemetrySnapshot(string ChargerId, string ChargerName, string Address, bool AckPortReachable,
    bool StatusPortReachable, DateTimeOffset ObservedAt, string Outcome,
    string Message, DateTimeOffset? LastFullSuccessAt = null)
{
    public int AckPort { get; init; } = 20207;
    public int StatusPort { get; init; } = 20204;
    public string Transport => "TCP health only";
}

public interface IChargerTelemetryMonitor
{
    IReadOnlyList<ChargerTelemetrySnapshot> Read();
}

// A narrow port makes the persisted workflow testable without robot connections.
public interface IChargingRuntimePort
{
    IReadOnlyList<AgvRobotSnapshot> Vehicles { get; }
    string? NewWorkBlockReason { get; }
    /// <summary>B07-R8: orders waiting for a vehicle minus free in-service vehicles (zero when unknown).</summary>
    Task<int> UnservedBusinessTasksAsync(IReadOnlySet<string> outOfServiceVehicleIds, CancellationToken ct) => Task.FromResult(0);
    ValueTask<IDisposable?> EnterExecutionAsync(bool newWork, CancellationToken ct);
    ValueTask<IDisposable?> EnterRecoveryControlAsync(CancellationToken ct) => EnterExecutionAsync(false,ct);
    Task<string?> BusyReasonAsync(string vehicleId, string? ownSessionId, CancellationToken ct);
    Task<ChargingRoute?> PlanAsync(string vehicleId, ChargingSite site, CancellationToken ct);
    Task<ChargingRoute?> PlanWithReturnAsync(string vehicleId, ChargingSite site, string? returnSiteId, CancellationToken ct) => PlanAsync(vehicleId,site,ct);
    Task<ChargingRoute?> PlanForSessionAsync(ChargingSession session, ChargingSite site, CancellationToken ct) =>
        PlanWithReturnAsync(session.VehicleId, site, session.RequestedReturnSiteId, ct);
    Task<ChargingApproachEstimate?> EstimateApproachAsync(string vehicleId, ChargingSite site, string sessionId, CancellationToken ct) =>
        Task.FromResult<ChargingApproachEstimate?>(null);
    Task<ChargingWaitingRoute?> PlanWaitingAsync(ChargingSession session, CancellationToken ct) => Task.FromResult<ChargingWaitingRoute?>(null);
    Task<bool> ReserveWaitingAsync(ChargingSession session, CancellationToken ct) => Task.FromResult(false);
    Task<string?> ValidateWaitingAsync(ChargingSession session, CancellationToken ct) => Task.FromResult<string?>(null);
    Task<string?> ValidateWaitingRecoveryCloseAsync(ChargingSession session, CancellationToken ct) =>
        Task.FromResult<string?>("原等桩导航离位核验尚未提供。");
    Task TrimWaitingAsync(ChargingSession session, bool departed, CancellationToken ct) => Task.CompletedTask;
    Task ReleaseInboundAsync(ChargingSession session, CancellationToken ct) => ReleaseAsync(session.SessionId, ct);
    /// <summary>Why the last plan for this vehicle and site returned no route (null when unknown or planned).</summary>
    string? LastPlanFailure(string vehicleId, ChargingSite site) => null;
    /// <summary>After the vehicle is verified on the charging point, keep only the bay and the exit path and release the
    /// inbound/return roads it no longer occupies. Returns the number of released leases.</summary>
    Task<int> ShrinkToBayAsync(ChargingSession session, CancellationToken ct) => Task.FromResult(0);
    // B07-FIX C8: exit roads are not held while charging; they are acquired as ordinary traffic once the
    // stop is verified. False keeps the session in AwaitingStop (no Hold) until the exit route is free.
    Task<bool> AcquireExitAsync(ChargingSession session, CancellationToken ct) => Task.FromResult(true);
    // B07-FIX C1: after the vehicle stands at the exit, the return road is leased and the charging bay released.
    // False keeps the session at the exit (no Hold) until the return road is free.
    Task<bool> AcquireReturnAsync(ChargingSession session, CancellationToken ct) => Task.FromResult(true);
    Task<int> ReleaseBayAtExitAsync(ChargingSession session, CancellationToken ct) => Task.FromResult(0);
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
