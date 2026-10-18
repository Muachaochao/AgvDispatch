using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Application.Abstractions;

public enum MapVersionStatus
{
    Draft = 0,
    Published = 10,
    Archived = 20
}

public sealed record MapProjectSummary(
    string MapId,
    string Name,
    string Description,
    string BackgroundFilePath,
    string BackgroundFormat,
    string CoordinateSystem,
    string Unit,
    double CoordinateScale,
    string PreviewPngPath,
    string PreviewSvgPath,
    IReadOnlyList<CadLayerSnapshot> CadLayers,
    CadViewBoxSnapshot? CadViewBox,
    MapCalibrationSnapshot? Calibration,
    CadUnderlayJobStatus UnderlayStatus,
    string UnderlayMessage,
    DateTimeOffset? UnderlayGeneratedAt,
    bool IsActive,
    string? ActiveVersionId,
    string? PublishedVersionId,
    DateTimeOffset UpdatedAt)
{
    public string? SmapScanPngPath {get;init;}
    public string? SmapAnnotationPngPath {get;init;}
}

public sealed record MapVersionSnapshot(
    string VersionId,
    string MapId,
    int VersionNumber,
    MapVersionStatus Status,
    MapDefinition Definition,
    MapValidationReport Validation,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt,
    string EditToken);

public sealed record MapWorkspaceSnapshot(
    MapProjectSummary Project,
    MapVersionSnapshot Draft,
    MapVersionSnapshot? Published);

public sealed record CreateMapRequest(
    string Name,
    string Description,
    string BackgroundFilePath,
    string CoordinateSystem,
    string Unit,
    double CoordinateScale);

public sealed record SaveMapDraftRequest(
    string MapId,
    string VersionId,
    string Name,
    string Description,
    string BackgroundFilePath,
    string CoordinateSystem,
    string Unit,
    double CoordinateScale,
    MapDefinition Definition,
    string ExpectedEditToken);

public sealed record PublishMapRequest(string MapId, string VersionId);

public sealed record GenerateMapUnderlayRequest(
    string MapId,
    string SourcePath,
    IReadOnlyList<string> IncludedLayers,
    int Width = 4096, int Height = 2608);

public sealed record SaveMapCalibrationRequest(
    string MapId,
    IReadOnlyList<MapCalibrationPoint> ControlPoints);

public sealed record ActivateMapRequest(string MapId, string VersionId, string ConfirmationText,
    bool SiteSafetyConfirmed = false, bool VehicleMapCompatibilityConfirmed = false, string Evidence = "")
{ public string? ExpectedReleaseId { get; init; } }

public sealed record PreviewMapImportRequest(
    string Path,
    MapImportFormat Format = MapImportFormat.Auto,
    double CoordinateScale = 1.0);

public sealed record MapImportPreviewResult(
    bool Accepted,
    string Message,
    MapDefinition? Definition = null,
    MapValidationReport? Validation = null);

public enum MapExportVersion
{
    Draft = 0,
    Published = 10
}

public sealed record ExportMapRequest(string MapId, MapExportVersion Version);

public sealed record MapExportResult(
    bool Accepted,
    string Message,
    string FilePath = "",
    string Sha256 = "",
    long SizeBytes = 0);

public sealed record MapOperationAuditSnapshot(
    string AuditId,
    string MapId,
    string? VersionId,
    string Operation,
    bool Success,
    string OperatorIdentity,
    string Summary,
    DateTimeOffset OccurredAt);

public sealed record MapVersionHistoryItem(
    string VersionId,
    string MapId,
    int VersionNumber,
    MapVersionStatus Status,
    int NodeCount,
    int EdgeCount,
    int ResourceCount,
    int ErrorCount,
    int WarningCount,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt);

public sealed record MapOperationResult(
    bool Accepted,
    string Message,
    MapWorkspaceSnapshot? Workspace = null);

public interface IMapManagementService
{
    Task<IReadOnlyList<MapProjectSummary>> GetMapsAsync(
        TrafficUserContext user,
        CancellationToken cancellationToken);

    Task<MapWorkspaceSnapshot?> GetWorkspaceAsync(
        TrafficUserContext user,
        string mapId,
        CancellationToken cancellationToken);

    Task<MapOperationResult> CreateAsync(
        TrafficUserContext user,
        CreateMapRequest request,
        CancellationToken cancellationToken);

    Task<MapOperationResult> SaveDraftAsync(
        TrafficUserContext user,
        SaveMapDraftRequest request,
        CancellationToken cancellationToken);

    Task<MapOperationResult> PublishAsync(
        TrafficUserContext user,
        PublishMapRequest request,
        CancellationToken cancellationToken);

    Task<MapOperationResult> GenerateUnderlayAsync(
        TrafficUserContext user,
        GenerateMapUnderlayRequest request,
        CancellationToken cancellationToken);

    Task<MapOperationResult> SaveCalibrationAsync(
        TrafficUserContext user,
        SaveMapCalibrationRequest request,
        CancellationToken cancellationToken);

    Task<MapOperationResult> ActivateAsync(
        TrafficUserContext user,
        ActivateMapRequest request,
        CancellationToken cancellationToken);

    Task<MapImportPreviewResult> PreviewImportAsync(
        TrafficUserContext user,
        PreviewMapImportRequest request,
        CancellationToken cancellationToken);

    Task<MapExportResult> ExportAsync(
        TrafficUserContext user,
        ExportMapRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MapOperationAuditSnapshot>> GetAuditsAsync(
        TrafficUserContext user,
        string mapId,
        int maximumCount,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MapVersionHistoryItem>> GetVersionHistoryAsync(
        TrafficUserContext user,
        string mapId,
        CancellationToken cancellationToken);

    Task<MapVersionSnapshot?> GetVersionAsync(
        TrafficUserContext user,
        string mapId,
        string versionId,
        CancellationToken cancellationToken);
}
