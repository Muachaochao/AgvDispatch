namespace AgvDispatch.Application.Abstractions;

/// <summary>Saved independently of execution permissions. Shadow only recommends; it never sends commands.</summary>
public sealed record DispatchAssignmentOptions
{
    public string Mode { get; init; } = "Shadow";
    public int IntervalMs { get; init; } = 3000;
    public int WindowSize { get; init; } = 80;
    public int CandidatesPerVehicle { get; init; } = 15;
    public int MinimumCandidatesPerTask { get; init; } = 2;
    public double WaitPriorityPerMinute { get; init; } = 1;
    public double MaxWaitPriority { get; init; } = 30;
    public double DeadlineHorizonMinutes { get; init; } = 15;
    public double MaxDeadlinePriority { get; init; } = 30;
    public double MinimumBatteryPercent { get; init; } = 20;
    public double NominalSpeedMetersPerSecond { get; init; } = 1;
    public double StandbyBonusSeconds { get; init; } = 1;
    public double BatteryRiskSeconds { get; init; } = 10;
    public double CrossZoneSeconds { get; init; } = 10;
    public double ReturnCostWeight { get; init; } = .2;
    public Dictionary<string, string> SiteZones { get; init; } = new();
    public Dictionary<string, double> StationDeadlineMinutes { get; init; } = new();

    public string? GetConfigurationError()
    {
        if (!new[] { "Disabled", "Shadow", "Global" }.Contains(Mode, StringComparer.OrdinalIgnoreCase))
            return "调度优化模式必须是 Disabled、Shadow 或 Global。";
        if (IntervalMs is < 2000 or > 5000) return "滚动调度周期必须为 2000～5000ms。";
        if (WindowSize is < 1 or > 1000) return "候选任务窗口必须为 1～1000。";
        if (CandidatesPerVehicle is < 1 or > 100) return "每车候选数必须为 1～100。";
        if (MinimumCandidatesPerTask is < 1 or > 5) return "每任务保留车辆数必须为 1～5。";
        if (!InRange(WaitPriorityPerMinute, 0, 100) || !InRange(MaxWaitPriority, 0, 10000))
            return "等待补偿每分钟必须为 0～100，补偿上限必须为 0～10000。";
        if (!InRange(DeadlineHorizonMinutes, 1, 1440) || !InRange(MaxDeadlinePriority, 0, 10000))
            return "截止时间紧迫窗口必须为 1～1440 分钟，紧迫度上限必须为 0～10000。";
        if (!InRange(MinimumBatteryPercent, 0, 100)) return "业务最低电量必须为 0～100%。";
        if (!InRange(NominalSpeedMetersPerSecond, .05, 10)) return "预计行驶速度必须为 0.05～10 米/秒。";
        if (!InRange(StandbyBonusSeconds, 0, 1)) return "待机加分必须为 0～1 秒，不能覆盖明显更长的空驶。";
        if (!InRange(BatteryRiskSeconds, 0, 3600)) return "低电量风险成本必须为 0～3600 秒。";
        if (!InRange(CrossZoneSeconds, 0, 3600) || !InRange(ReturnCostWeight, 0, 10)) return "跨区成本必须为0～3600秒，返程权重必须为0～10。";
        if (SiteZones is null || SiteZones.Count > 1000 || SiteZones.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 128 || string.IsNullOrWhiteSpace(p.Value) || p.Value.Length > 128))
            return "站点区域配置无效（最多1000项，编号及区域名称不超过128字符）。";
        if (StationDeadlineMinutes is null || StationDeadlineMinutes.Count > 1000 || StationDeadlineMinutes.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 128 || !InRange(p.Value, 1, 10080)))
            return "工位时限配置无效（每站1～10080分钟，最多1000项）。";
        if (SiteZones.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != SiteZones.Count ||
            StationDeadlineMinutes.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != StationDeadlineMinutes.Count)
            return "区域或工位配置存在仅大小写不同的重复编号。";
        return null;
    }

    private static bool InRange(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}

public sealed record DispatchAssignmentTask(string TaskId, string StationId, int BasePriority, DateTimeOffset CalledAt)
{
    public DateTimeOffset? Deadline { get; init; }
}

/// <summary>Only hard-feasible pairs belong here. Seconds must be estimates from available evidence, never invented telemetry.</summary>
public sealed record DispatchAssignmentCandidate(string TaskId, string VehicleId, double TravelSeconds)
{
    public double TrafficWaitSeconds { get; init; }
    public double ReturnSeconds { get; init; }
    public double BatteryRiskSeconds { get; init; }
    public double CrossZoneSeconds { get; init; }
    public double StandbyBonusSeconds { get; init; }
    public double TotalCostSeconds => TravelSeconds + TrafficWaitSeconds + ReturnSeconds + BatteryRiskSeconds + CrossZoneSeconds - StandbyBonusSeconds;
}

public sealed record DispatchAssignmentTaskScore(string TaskId, string StationId, int BasePriority,
    DateTimeOffset CalledAt, double WaitingSeconds, double WaitPriority, double DeadlinePriority, double EffectivePriority);

public sealed record DispatchAssignmentRecommendation(string TaskId, string StationId, string VehicleId,
    double EffectivePriority, double WaitingSeconds, double TravelSeconds, double TrafficWaitSeconds,
    double ReturnSeconds, double BatteryRiskSeconds, double CrossZoneSeconds, double StandbyBonusSeconds,
    double TotalCostSeconds, string Reason);

public sealed record DispatchAssignmentUnassigned(string TaskId, string StationId, double EffectivePriority, string Reason);

public sealed record DispatchAssignmentResult
{
    public IReadOnlyList<DispatchAssignmentTaskScore> TaskScores { get; init; } = [];
    public IReadOnlyList<DispatchAssignmentRecommendation> Assignments { get; init; } = [];
    public IReadOnlyList<DispatchAssignmentUnassigned> Unassigned { get; init; } = [];
    public int InputCandidateCount { get; init; }
    public int RetainedCandidateCount { get; init; }
    public double TotalTravelSeconds { get; init; }
    public double TotalCostSeconds { get; init; }
    public double ElapsedMilliseconds { get; init; }
}

public sealed record DispatchOptimizationCandidate(string VehicleId, double TravelSeconds, double TotalCost, string Reason, bool Eligible)
{
    public double TrafficWaitSeconds { get; init; }
    public double ReturnSeconds { get; init; }
    public double BatteryRiskSeconds { get; init; }
    public double CrossZoneSeconds { get; init; }
    public double StandbyBonusSeconds { get; init; }
}
public sealed record DispatchOptimizationTask(string TaskId, string StationId, DateTimeOffset CalledAt,
    int BasePriority, double EffectivePriority, double WaitMinutes, string? RecommendedVehicleId,
    string? GreedyVehicleId, string? DispatchedVehicleId, string Reason, IReadOnlyList<DispatchOptimizationCandidate> Candidates)
{
    public string? BlockedLayer { get; init; }
}
public sealed record DispatchOptimizationSnapshot
{
    public DispatchAssignmentOptions Options { get; init; } = new();
    public string Revision { get; init; } = "initial";
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public long CycleNumber { get; init; }
    public double DurationMs { get; init; }
    public int WaitingTaskCount { get; init; }
    public int WindowTaskCount { get; init; }
    public int EligibleVehicleCount { get; init; }
    public int CandidateCount { get; init; }
    public int RetainedCandidateCount { get; init; }
    public long RouteCacheHits { get; init; }
    public long RouteCacheMisses { get; init; }
    public long ShadowCycleCount { get; init; }
    public long ImprovedCycleCount { get; init; }
    public string Message { get; init; } = "等待调度周期；影子模式只计算推荐，沿用现有派车。";
    public IReadOnlyList<DispatchOptimizationTask> Tasks { get; init; } = [];
    public IReadOnlyList<DispatchPipelineLayerSnapshot> Layers { get; init; } = DispatchPipelineLayers.Initial();
}
public sealed record SaveDispatchOptimizationOptionsRequest(DispatchAssignmentOptions Options, string ExpectedRevision);
public interface IDispatchOptimizationService
{
    Task<DispatchOptimizationSnapshot> GetAsync(CancellationToken cancellationToken);
    Task<DispatchOptimizationSnapshot> SaveOptionsAsync(TrafficUserContext user, SaveDispatchOptimizationOptionsRequest request, CancellationToken cancellationToken);
}
