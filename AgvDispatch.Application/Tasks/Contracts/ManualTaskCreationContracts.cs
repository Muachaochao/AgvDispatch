namespace AgvDispatch.Application.Abstractions;

public sealed record ManualTaskStationChoice(string StationId, string PickupSite, bool Available, string Explanation)
{
    public IReadOnlyList<ManualTaskStepPreview> PreviewSteps { get; init; } = [];
    public IReadOnlyList<string> PickupReachableVehicleIds { get; init; } = [];
    public string? VehiclePoolId { get; init; }
    public IReadOnlyList<string>? AllowedVehicleIds { get; init; }
    public bool ConfigurationValid => PreviewSteps.Count == 9 && PreviewSteps.All(step => step.ValidationErrors.Count == 0);
}
public sealed record ManualTaskStepPreview(int Sequence, AgvDispatch.Domain.Tasks.DispatchTaskStepType StepType,
    string Name, string SourceSite, string DestinationSite, string PlcStationId,
    string VehicleAction, string WaitingCondition, string PlcHandshake, string ConfigurationNote)
{
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
}
public sealed record ManualTaskVehicleChoice(string VehicleId, string Label, string CurrentSite, bool Available, string Explanation);
public sealed record ManualTaskCreationOptions(string? MapId, string? MapVersionId, bool Available, string Explanation,
    string DumperStationId, string WaitingSite, string DumpingSite, IReadOnlyList<ManualTaskStationChoice> Stations,
    IReadOnlyList<ManualTaskVehicleChoice> Vehicles)
{
    public string MapRuntimeId {get;init;}="";
    public bool RequiresExplicitVehicle { get; init; }
    public string ParameterRevision { get; init; } = "initial";
    public IReadOnlyList<string> WaitingSites { get; init; } = [];
    public IReadOnlyList<TaskStandbyParameter> Standbys { get; init; } = [];
}
public sealed record ManualTaskCreationRequest(string RequestId, string StationId, string? RequestedVehicleId,
    int Priority, string MapId, string MapVersionId)
{
    public string ParameterRevision { get; init; } = "initial";
}
public sealed record ManualTaskCreationResult(bool Accepted, bool Created, string? TaskId, string Message);
public sealed record ManualTaskReceipt(string TaskId, string RequestId, string StationId, string? RequestedVehicleId,
    int Priority, string MapId, string MapVersionId)
{
    public string ParameterRevision { get; init; } = "initial";
}

public interface IManualTaskCreationService
{
    Task<ManualTaskRoutePreview> PreviewRoutesAsync(ManualTaskRoutePreviewRequest request,CancellationToken ct)=>throw new NotSupportedException("后台未支持当前任务路线预览。");
    Task<ManualTaskCreationOptions> GetOptionsAsync(CancellationToken ct);
    Task<ManualTaskCreationResult> CreateAsync(TrafficUserContext user, ManualTaskCreationRequest request, CancellationToken ct);
    // Configuration preparation only; creation/execution separately enforce live write and recovery permission.
    IReadOnlyList<TaskParameterValidationRow> ValidateParameters(TaskParameterConfiguration configuration);
}

public interface IManualTaskCreationRepository
{
    Task<ManualTaskReceipt?> FindRequestAsync(string requestId, CancellationToken ct);
    Task<AgvDispatch.Domain.Tasks.TaskCreationResult> TryCreateManualAsync(ManualTaskCreationRequest request,
        string dumperStationId, string operatorIdentity, CancellationToken ct);
}

public sealed record ManualTaskRoutePreviewRequest(string StationId,string VehicleId,string MapId,string MapVersionId,string ParameterRevision);
public sealed record ManualTaskRouteLeg(int Sequence,string Name,string LogicalPath,string VendorPath,string Message);
public sealed record ManualTaskRouteSegment(int Sequence,int Segment,string EdgeId,string LogicalFrom,string LogicalTo,string VendorFrom,string VendorTo,string Status,string Message)
{
    [System.Text.Json.Serialization.JsonIgnore] public string LogicalPair=>$"{LogicalFrom} → {LogicalTo}";
    [System.Text.Json.Serialization.JsonIgnore] public string VendorPair=>$"{(string.IsNullOrWhiteSpace(VendorFrom)?"[未映射]":VendorFrom)} → {(string.IsNullOrWhiteSpace(VendorTo)?"[未映射]":VendorTo)}";
}
public sealed record ManualTaskRoutePreview(string MapId,string MapVersionId,string ParameterRevision,string StationId,string VehicleId,string CurrentSite,DateTimeOffset ObservedAt,bool Verified,string Message,IReadOnlyList<ManualTaskRouteLeg> Legs,IReadOnlyList<ManualTaskRouteSegment> Segments)
{
    public IReadOnlyList<ManualTaskStepPreview> Steps {get;init;}=[];
    public string ObservationId {get;init;}="";
    public ManualTaskRouteMap? Map {get;init;}
    public string MapIssue {get;init;}="后台未提供同批导航几何。";
}
public sealed record ManualTaskRouteMapNode(string Id,string VendorId,double X,double Y);
public sealed record ManualTaskRouteMapEdge(string Id,string From,string To,bool Bidirectional,string Kind,IReadOnlyList<AgvDispatch.Domain.Maps.MapPoint2D> Points);
public sealed record ManualTaskRouteMap(string MapId,string VersionId,string ObservationId,string Unit,int TotalNodes,int TotalEdges,IReadOnlyList<ManualTaskRouteMapNode> Nodes,IReadOnlyList<ManualTaskRouteMapEdge> Edges)
{
    public string RuntimeId {get;init;}="";
    public bool SourceOptionsAvailable {get;init;}
    public bool SourceVehicleAvailable {get;init;}
    public bool SourceStationAvailable {get;init;}
    public bool SourceStationConfigurationValid {get;init;}
}
