using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Application.Abstractions;

public static class ObstacleRecoveryStates
{
    public const string Blocked = "Blocked";
    public const string DetourReady = "DetourReady";
    public const string CancelIntent = "CancelIntent";
    public const string AwaitingOriginalTerminal = "AwaitingOriginalTerminal";
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
