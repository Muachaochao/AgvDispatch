using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

/// <summary>Layer one reads existing durable calls. Nothing in a planning snapshot deletes or claims work.</summary>
public interface IDispatchQueueWindowService
{
    Task<DispatchQueueWindow> LoadAsync(DispatchAssignmentOptions policy,
        IReadOnlyList<DispatchTaskWorkItem> activeTasks, DateTimeOffset now, CancellationToken ct);
}

public sealed record DispatchQueueWindow
{
    public DispatchTaskWorkItem[] Waiting { get; init; } = [];
    public DispatchTaskWorkItem[] Work { get; init; } = [];
    public DispatchAssignmentTask[] Input { get; init; } = [];
    public IReadOnlyList<DispatchAssignmentTaskScore> Window { get; init; } = [];
    public Dictionary<string, DispatchTaskStepWorkItem> Ready { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> OccupiedVehicles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Layer two builds hard-feasible edges and estimates only; grants and sends belong to later layers.</summary>
public interface IDispatchCandidateFilter
{
    string? CheckVehicle(AgvVehicleStatus vehicle, IReadOnlySet<string> occupied, DispatchAssignmentOptions policy);
    Task<DispatchCandidateGraph> BuildAsync(DispatchQueueWindow queue, DispatchAssignmentOptions policy,
        ActiveMapRuntimeSnapshot? map, CancellationToken ct);
}

public sealed record DispatchCandidateGraph
{
    public AgvVehicleStatus[] Vehicles { get; init; } = [];
    public Dictionary<string, string?> GeneralReasons { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DispatchAssignmentCandidate> Edges { get; init; } = [];
    public Dictionary<(string, string), RoutePlan> Plans { get; init; } = [];
    public HashSet<(string, string)> StagedPairs { get; init; } = [];
    public Dictionary<string, List<DispatchOptimizationCandidate>> Diagnostics { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> TaskReasons { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public long RouteMemoHits { get; init; }
    public long RouteMemoMisses { get; init; }
    // Observations are proposals. Layer four decides whether to persist them; Shadow never does.
    public IReadOnlyList<TrafficRouteObservation> WaitingRoutes { get; init; } = [];
}
