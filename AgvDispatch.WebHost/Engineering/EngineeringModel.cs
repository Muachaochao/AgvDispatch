using System.Text.Json.Serialization;

namespace AgvDispatch.WebHost.Engineering;

// Engineering revisions are planning artifacts. No connector or dispatch service consumes them implicitly.
public sealed record EngineeringPlan
{
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = "新工程";
    public string MapId { get; init; } = "";
    public string MapVersion { get; init; } = "";
    public List<EngineeringNode> Nodes { get; init; } = [];
    public List<EngineeringEdge> Edges { get; init; } = [];
    public List<EngineeringResource> Resources { get; init; } = [];
    public List<EngineeringPlc> Plcs { get; init; } = [];
    public List<EngineeringSignal> Signals { get; init; } = [];
    public List<EngineeringVehicle> Vehicles { get; init; } = [];
    public List<EngineeringPool> Pools { get; init; } = [];
    public List<EngineeringStation> Stations { get; init; } = [];
    public List<EngineeringTemplate> Templates { get; init; } = [];
    public List<EngineeringDumper> Dumpers { get; init; } = [];
    public List<EngineeringStandby> Standbys { get; init; } = [];
}
public sealed record EngineeringNode(string Id, string VendorSite, double X, double Y, bool Enabled = true);
public sealed record EngineeringEdge(string Id, string From, string To, double Distance, double Speed, string[] ResourceIds, bool Bidirectional = true, bool Enabled = true);
public sealed record EngineeringResource(string Id, string Name, string Kind, int Capacity = 1);
public sealed record EngineeringPlc(string Id, string Host, int Port = 102, bool Enabled = true);
public sealed record EngineeringSignal(string Id, string PlcId, string OwnerId, string Name, string Address, string Direction, string DataType = "Bool");
public sealed record EngineeringVehicle(string Id, string Name, string Host, string StartNode, string Capability, double Radius, double LocalizationError, string EnvelopeEvidence, double Battery = 100, double MinimumBattery = 20, bool Enabled = true);
public sealed record EngineeringPool(string Id, string Name, string[] VehicleIds);
public sealed record EngineeringStation(string Id, string Name, string Area, string PickupNode, string PlcId, string TemplateId, string PoolId, string DumperId, int Priority = 50, bool Enabled = true);
public sealed record EngineeringTemplate(string Id, string Name, string Capability, double PickupSeconds = 5, double DumpSeconds = 10, double ReturnSeconds = 5)
{
    // Versioned task definitions are planning contracts. They are compiled to the
    // existing durable nine-step executor and never executed as arbitrary code.
    public int Version { get; init; } = 1;
    public string Engine { get; init; } = EngineeringTaskTemplates.DurableNineStepEngine;
    public List<EngineeringTemplatePort> Inputs { get; init; } = [];
    public List<EngineeringTemplatePort> Outputs { get; init; } = [];
    public List<EngineeringTemplateVariable> Variables { get; init; } = [];
    public List<EngineeringTemplateNode> Nodes { get; init; } = [];
    public List<EngineeringTemplateConnection> Connections { get; init; } = [];
    public List<EngineeringTemplateGuard> Guards { get; init; } = [];
    public string SourceTemplateId { get; init; } = "";
    public int SourceVersion { get; init; }
}
public sealed record EngineeringTemplatePort(string Key, string Label, string Type, bool Required = true, string Source = "");
public sealed record EngineeringTemplateVariable(string Key, string Label, string Type, string Producer);
public sealed record EngineeringTemplateConnection(string FromNodeId, string Output, string ToNodeId, string Input, string Type, bool External = false);
public sealed record EngineeringTemplateGuard(string Id, string Trigger, string Action, string TargetNodeId, string Description);
public sealed record EngineeringTemplateNode(string Id, string Block, string Name, string[] Inputs, string[] Outputs, string Condition)
{
    public string FailurePolicy { get; init; } = "HoldUntilCondition";
    public string TimeoutAction { get; init; } = "NotifyAndHold";
    public int TimeoutSeconds { get; init; }
    public bool AutoRetry { get; init; }
}
public sealed record EngineeringDumper(string Id, string Name, string PlcId, string WaitingNode, string DumpingNode, string WaitingResource, string DumpingResource)
{
    // The first waiting node/resource remains the required primary handshake bay.
    // Additional independent capacity-1 bays let the dispatcher queue a second
    // vehicle without ever sharing the dumping position.
    public string[] AdditionalWaitingNodes { get; init; } = [];
    public string[] AdditionalWaitingResources { get; init; } = [];
}
public sealed record EngineeringStandby(string Id, string NodeId, string PoolId, string ResourceId, int Priority = 50);
public sealed record EngineeringIssue(string Severity, string Section, string ObjectId, string Field, string Message)
{
    public string Link => "/engineering?tab=" + Uri.EscapeDataString(Section) + "&object=" + Uri.EscapeDataString(ObjectId);
}
public sealed record EngineeringValidation(IReadOnlyList<EngineeringIssue> Issues)
{
    public bool Valid => Issues.All(x => x.Severity != "Error");
}
public sealed record EngineeringDraft(string Revision, DateTimeOffset SavedAt, EngineeringPlan Plan);
public sealed record EngineeringSave(string ExpectedRevision, EngineeringPlan Plan);
public sealed record EngineeringPublish(string ExpectedRevision, string Note);
public sealed record EngineeringVersion(string Id, string DraftRevision, DateTimeOffset PublishedAt, string Note, EngineeringPlan Plan, string ContentHash)
{
    [JsonIgnore]
    public bool RawPlanHashVerified { get; init; }
}
public sealed record EngineeringTemplateLibraryEntry(string Key, EngineeringTemplate Template, string ContentHash, DateTimeOffset SavedAt);
public sealed record EngineeringTemplateTransfer(int SchemaVersion, EngineeringTemplate Template, string? ContentHash);
public sealed record EngineeringTaskPreviewRequest(EngineeringPlan Plan, string StationId, string? TemplateId);
public sealed record EngineeringTaskPreviewInput(string Key, string Label, string Type, string? Value, string Source, bool Required);
public sealed record EngineeringTaskPreviewNode(int Sequence, string NodeId, string StepType, string Name, string SourceSite,
    string DestinationSite, string VehicleAction, string PlcCondition, string[] Inputs, string[] Outputs)
{
    public string FailurePolicy { get; init; } = "HoldUntilCondition";
    public string TimeoutAction { get; init; } = "NotifyAndHold";
    public int TimeoutSeconds { get; init; }
    public bool AutoRetry { get; init; }
}
public sealed record EngineeringTaskPreview(string TemplateId, int TemplateVersion, string TemplateHash, string Engine,
    string StationId, string StationName, string PickupSite, string VehiclePoolId, string[] VehicleIds, string[] WaitingSites,
    string DumpingSite, bool Ready, IReadOnlyList<EngineeringTaskPreviewInput> Inputs,
    IReadOnlyList<EngineeringTaskPreviewInput> Outputs, IReadOnlyList<EngineeringTaskPreviewNode> Nodes,
    IReadOnlyList<EngineeringIssue> Issues)
{
    public IReadOnlyList<EngineeringTemplateConnection> Connections { get; init; } = [];
    public IReadOnlyList<EngineeringTemplateGuard> Guards { get; init; } = [];
}
public sealed record EngineeringCall(string RequestId, string StationId, double AtSeconds = 0);
public sealed record EngineeringSimulationRequest(string VersionId, List<EngineeringCall> Calls, string[] OfflineVehicles, string[] BlockedResources);
public sealed record EngineeringReservation(string ResourceId, string TaskId, string VehicleId, double Start, double End);
public sealed record EngineeringSimTask(string TaskId, string CallId, string StationId, string VehicleId, double Start, double End, string[] Route, string[] Steps);
public sealed record EngineeringRejectedCall(string CallId, string StationId, string Reason, string Link);
public sealed record EngineeringSimulationReport(string Id, string VersionId, DateTimeOffset CreatedAt, int RequestedCalls, int DuplicateCalls,
    IReadOnlyList<EngineeringSimTask> Tasks, IReadOnlyList<EngineeringRejectedCall> Blocked, IReadOnlyList<EngineeringReservation> Reservations,
    IReadOnlyDictionary<string, string> FinalPositions, double DurationSeconds, int PeakVehicles, string EvidenceBoundary);
