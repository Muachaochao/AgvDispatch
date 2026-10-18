namespace AgvDispatch.Application.Abstractions;
public sealed record TrafficBlockingQuery(string TaskId,string StepId);
public sealed record TrafficReleaseRequirement(string Id,string State,string Text,string Evidence);
public sealed record TrafficBlockingNode(string TaskId,string StepId,string VehicleId,string? MapId,string? MapVersionId,string State,string Reason,string? TaskStage,string? StepStatus,string? CommandStatus,bool MissingEvidence,IReadOnlyList<TrafficReleaseRequirement> ReleaseRequirements);
public sealed record TrafficBlockingLink(string FromTaskId,string FromStepId,string ToTaskId,string ToStepId,string VehicleId,string Kind,string? ResourceId,bool HeartbeatExpired,string? MapId,string? MapVersionId);
public sealed record TrafficBlockingDetails(DateTimeOffset CapturedAt,string TaskId,string StepId,bool Available,string Message,IReadOnlyList<TrafficBlockingNode> Nodes,IReadOnlyList<TrafficBlockingLink> Links,IReadOnlyList<TrafficLifecycleEvent> Events,bool Truncated,bool EventsTruncated,bool CycleObserved);
