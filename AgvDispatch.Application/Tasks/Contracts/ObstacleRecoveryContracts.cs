using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Application.Abstractions;

public static class ObstacleRecoveryStates
{
    public const string Blocked = "Blocked";
    public const string DetourReady = "DetourReady";
    public const string CancelIntent = "CancelIntent";
    public const string AwaitingOriginalTerminal = "AwaitingOriginalTerminal";
    public const string SafetyPointReady = "SafetyPointReady";
    public const string SafetyPointSendIntent = "SafetyPointSendIntent";
    public const string SafetyPointMoving = "SafetyPointMoving";
    public const string SafetyPointReached = "SafetyPointReached";
    public const string SendIntent = "SendIntent";
    public const string Executing = "Executing";
    public const string NoRoute = "NoRoute";
    public const string Warning = "Warning";
    public const string Alarm = "Alarm";
    public const string ClosureSuggested = "ClosureSuggested";
    public const string SafetyHold = "SafetyHold";
    public const string Frozen = "Frozen";
    public const string Uncertain = "Uncertain";
    public const string Recovered = "Recovered";
}

public static class ObstacleRecoveryTaskIds
{
    public const string Prefix = "OBSTACLERECOVERY-";
    public static string NewId() => Prefix + Guid.NewGuid().ToString("N");
    public static bool IsRecoveryTask(string? value) =>
        value?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}

public sealed record ObstacleRecoverySnapshot(
    string RecoveryId,
    string TaskId,
    string StepId,
    string VehicleId,
    string OriginalVendorTaskId,
    string State,
    string Message,
    DateTimeOffset BlockedSince,
    IReadOnlyList<string> OriginalPath,
    IReadOnlyList<string> CandidatePath,
    IReadOnlyList<string> BlockedEdgeIds,
    string? SafeSite,
    string MapId,
    string MapVersionId,
    string DefinitionHash,
    string? NewVendorTaskId,
    bool SendAttempted,
    DateTimeOffset UpdatedAt)
{
    public double DurationSeconds { get; init; }
    public string NextAction { get; init; } = "";
    public bool AutomaticDetourEnabled { get; init; }
    public bool AutomaticSafetyPointRetreatEnabled { get; init; }
    public string? SafetyPointTarget { get; init; }
    public string? SafetyPointBlockedSource { get; init; }
    public string? SafetyPointBlockedTarget { get; init; }
    public string? SafetyPointRetreatTaskId { get; init; }
    public string? SafetyPointRetreatMethod { get; init; }
    public DateTimeOffset? SafetyPointRetreatAttemptedAt { get; init; }
    public DateTimeOffset? SafetyPointReachedAt { get; init; }
}

/// <summary>
/// A narrowly scoped command used only after the formal obstacle coordinator has
/// cancelled the original vendor task and revalidated map, route, PLC, traffic,
/// load ownership and a fresh stopped pose. The general gateway refuses a new
/// command while 52200/Blocked is active; this explicit recovery port does not.
/// </summary>
public interface IAgvObstacleRecoveryGateway
{
    Task<string> CreateObstacleRecoveryTransportTaskAsync(
        string newVendorTaskId,
        string vehicleId,
        IReadOnlyList<string> path,
        AgvTransportAction? action,
        DateTimeOffset blockedSince,
        string originalVendorTaskId,
        CancellationToken cancellationToken);
}

/// <summary>
/// A second, explicitly narrower recovery port for one vehicle stopped between
/// named stations. It uses 3055 straight translation for at most one metre and
/// 3051 SELF_POSITION backward mode for longer edges after the original chain
/// is terminal, traffic is frozen and all safety evidence has been revalidated.
/// It never performs a business task move.
/// </summary>
public interface IAgvSafetyPointRecoveryGateway
{
    Task<string> CreateObstacleSafetyPointRetreatAsync(
        string retreatVendorTaskId,
        string vehicleId,
        string targetSite,
        string blockedSourceSite,
        string blockedTargetSite,
        double maximumSpeedMetersPerSecond,
        double maximumAccelerationMetersPerSecondSquared,
        double remainingDistanceMeters,
        DateTimeOffset blockedSince,
        string originalVendorTaskId,
        CancellationToken cancellationToken);
}
