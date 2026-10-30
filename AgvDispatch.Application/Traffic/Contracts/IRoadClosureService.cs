using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Application.Abstractions;

public sealed record RoadClosureCreateRequest(string RequestId, string ExpectedMapId, string ExpectedVersionId,
    string ExpectedDefinitionHash, string EdgeId, string FromNodeId, string ToNodeId, bool Bidirectional,
    string Reason, string Source = "Operator");
public sealed record RoadClosureReleaseRequest(string RequestId, string ClosureId, long ExpectedRevision,
    string Reason, bool SiteClearedConfirmed);
public sealed record RoadClosurePreviewRequest(string ExpectedMapId, string ExpectedVersionId,
    string ExpectedDefinitionHash, long ExpectedRevision, string SourceSite, string TargetSite);
public sealed record RoadClosureRecord(string ClosureId, string RequestId, string MapId, string VersionId,
    string DefinitionHash, string EdgeId, string FromNodeId, string ToNodeId, bool Bidirectional,
    IReadOnlyList<MapDirectedTraversal> Traversals, string Source, string Reason, string CreatedBy,
    DateTimeOffset CreatedAt, long CreatedRevision, bool Active, string? ReleasedBy,
    DateTimeOffset? ReleasedAt, string? ReleaseReason, long? ReleasedRevision);
public sealed record RoadClosureSnapshot(bool Ready, long Revision, string? MapId, string? VersionId,
    string? DefinitionHash, IReadOnlyList<RoadClosureRecord> Closures)
{
    public IReadOnlyList<MapDirectedTraversal> BlockedTraversals => Closures.Where(c => c.Active &&
            string.Equals(c.MapId, MapId, StringComparison.Ordinal) &&
            string.Equals(c.VersionId, VersionId, StringComparison.Ordinal) &&
            string.Equals(c.DefinitionHash, DefinitionHash, StringComparison.OrdinalIgnoreCase))
        .SelectMany(c => c.Traversals).Distinct().ToArray();
    public bool IdentityConflict => Closures.Any(c => c.Active && c.MapId == MapId && c.VersionId == VersionId &&
        !string.Equals(c.DefinitionHash, DefinitionHash, StringComparison.OrdinalIgnoreCase));
}
public sealed record RoadClosureResult(bool Accepted, string Message, long Revision, RoadClosureRecord? Closure);
public sealed record RoadClosurePreview(bool Available, string Message, long Revision, string? MapId,
    string? VersionId, string? DefinitionHash, string SourceSite, string TargetSite, RoutePlan? Route,
    IReadOnlyList<MapDirectedTraversal> BlockedTraversals);
public sealed class RoadClosureRoutingOptions
{
    public IReadOnlyList<string> ProtectedTransitSites { get; init; } = [];
}

/// <summary>Explicit map-edge closures; robot coordinates alone never create a closure.</summary>
public interface IRoadClosureService
{
    Task InitializeAsync(CancellationToken cancellationToken);
    RoadClosureSnapshot GetSnapshot();
    Task<RoadClosureSnapshot> ListAsync(CancellationToken cancellationToken);
    Task<RoadClosureResult> CreateAsync(TrafficUserContext user, RoadClosureCreateRequest request, CancellationToken cancellationToken);
    Task<RoadClosureResult> ReleaseAsync(TrafficUserContext user, RoadClosureReleaseRequest request, CancellationToken cancellationToken);
    Task<RoadClosurePreview> PreviewAsync(RoadClosurePreviewRequest request, CancellationToken cancellationToken);
    // Hold until the actual send completes. Creation/release use this same gate, so a
    // previously planned/queued path cannot cross a closure added before its send.
    Task<IDisposable> EnterRouteSendAsync(IReadOnlyList<string> agvSitePath, CancellationToken cancellationToken);
}
