using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;

// Coordinator-level harness: real database, real state cache, real coordinator
// and store. Only the vendor gateway, the PLC/handshake precondition and the
// body envelope are synthetic.
sealed record ObstacleHarness(
    ISqlSugarClient Db,
    AgvStateCache States,
    ObstacleRecoveryCoordinator Coordinator,
    ObstacleRecoveryStore Store,
    RecoveryFake Gateway,
    ManualClock Clock,
    TrafficResourceManager Traffic,
    IRoadClosureService Closures);

static class ObstacleHarnessFactory
{
    public static MapDefinition StationGraph(bool withDetour) => new("MAP-O", "obstacle-r7", "RobotWorld", "m",
        [
            new("S", 0, 0, MapNodeType.Station, true, [], "S"),
            new("H", 1, 0, MapNodeType.Waypoint, true, [], "H"),
            new("D", 0, 1, MapNodeType.Waypoint, true, [], "D"),
            new("BUS-X", 1, 1, MapNodeType.Station, true, [], "X")
        ],
        withDetour
            ? [
                new("E-SH", "S", "H", 1, 1, false, true, []),
                new("E-HX", "H", "BUS-X", 1, 1, false, true, []),
                new("E-SD", "S", "D", 1, 1, false, true, []),
                new("E-DX", "D", "BUS-X", 1, 1, false, true, [])
            ]
            : [
                new("E-SH", "S", "H", 1, 1, false, true, []),
                new("E-HX", "H", "BUS-X", 1, 1, false, true, [])
            ], []);

    public static async Task<ObstacleHarness> BuildAsync(string root, string name, MapDefinition definition,
        DateTimeOffset start, string taskId, string stepId, ObstacleRecoveryOptions? options = null,
        bool corruptSnapshot = false, IRoutePlanner? routePlanner = null)
    {
        var clock = new ManualClock(start);
        var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, name + ".db") });
        var catalog = new TrafficResourceCatalog(MapCorridorResources.AllResources(definition));
        await new DatabaseInitializer(db, catalog).InitializeAsync(default);
        var map = new ActiveMapRuntime();
        map.Replace(new("MAP-O", "V1", definition, start));
        var gate = new MapExecutionGate();
        gate.SetMode(MapExecutionMode.Running);
        var closures = new RoadClosureService(db, map, gate);
        await closures.InitializeAsync(default);
        var traffic = new TrafficResourceManager(db, catalog, new(TimeSpan.FromMinutes(1)), clock);
        var agvOptions = new AgvOptions
        {
            Enabled = true,
            AllowCommands = true,
            StaleAfterMs = 10000,
            Robots =
            [
                new AgvRobotOptions { VehicleId = "A", IpAddress = "127.0.0.1", Enabled = true },
                new AgvRobotOptions { VehicleId = "B", IpAddress = "127.0.0.2", Enabled = true }
            ]
        };
        var states = new AgvStateCache(agvOptions, clock);
        var gateway = new RecoveryFake();
        var recoveryOptions = options ?? new ObstacleRecoveryOptions
        {
            Enabled = true,
            AutomaticDetourEnabled = true,
            DetourAfterSeconds = 30,
            CancelAfterSeconds = 120,
            WarningAfterSeconds = 60,
            AlarmAfterSeconds = 120,
            ClosureSuggestionAfterSeconds = 300,
            StableStopObservationMs = 500
        };
        var mapOptions = new MapOptions { PositionStaleSeconds = 30, MinimumLocalizationConfidence = 0.5 };
        var dispatch = new DispatchOrchestratorOptions
        {
            StandbySites = ["S"],
            DumperWaitingSite = "X",
            DumperDumpingSite = "X"
        };
        var store = new ObstacleRecoveryStore(db, recoveryOptions, clock);
        var coordinator = new ObstacleRecoveryCoordinator(db, states, map,
            new DispatchOrchestrationRepository(db), new AgvTransportChainLookup(db), closures,
            traffic, new Envelope(), gate, gateway, new Preconditions(), store, recoveryOptions,
            mapOptions, dispatch, clock, NullLogger<ObstacleRecoveryCoordinator>.Instance,
            routePlanner: routePlanner);
        await SeedTransportAsync(db, taskId, stepId, "A", start, definition, corruptSnapshot);
        return new(db, states, coordinator, store, gateway, clock, traffic, closures);
    }

    public static AgvRobotTelemetry Telemetry(DateTimeOffset at, bool blocked,
        double x = 0, double y = 0, string? current = "S") =>
        new(x, y, 0, .99, current, null, 80, blocked, false, "Running",
            null, null, null, [], [], at)
        {
            VelocityX = 0,
            VelocityY = 0,
            AngularVelocity = 0,
            Charging = false
        };

    public static string[] Decode(string json) =>
        JsonSerializer.Deserialize<string[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];

    // Executing transport S→H→X with its immutable send snapshot. A corrupt snapshot
    // makes every recovery observation of this vehicle throw, as an unreadable route does.
    public static async Task SeedTransportAsync(ISqlSugarClient db, string taskId, string stepId,
        string vehicleId, DateTimeOffset start, MapDefinition definition, bool corruptSnapshot = false)
    {
        var now = start.UtcDateTime;
        await db.Insertable(new DispatchTaskEntity
        {
            TaskId = taskId, StationId = "CNC-" + taskId, VehicleId = vehicleId, VendorTaskId = stepId,
            Stage = (int)DispatchTaskStage.GoingToPickup, Priority = 0, CreatedAt = now, UpdatedAt = now
        }).ExecuteCommandAsync();
        await db.Insertable(new DispatchTaskStepEntity
        {
            StepId = stepId, TaskId = taskId, Sequence = 1,
            StepType = (int)DispatchTaskStepType.GoToPickup,
            Status = (int)DispatchTaskStepStatus.Executing,
            SourceSite = "S", DestinationSite = "BUS-X", VehicleId = vehicleId, VendorTaskId = stepId,
            CreatedAt = now, UpdatedAt = now, StartedAt = now
        }).ExecuteCommandAsync();
        var payload = new AgvTransportCommandPayload(stepId, vehicleId, "S", "H", ["S", "H"], null);
        var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var snapshotJson = corruptSnapshot ? "{broken" : JsonSerializer.Serialize(new TransportRouteSnapshot(
            1, taskId, stepId, "CMD-" + stepId, vehicleId, "MAP-O", "V1", "RobotWorld", EngineeringRuntimeIdentity.MapHash(definition),
            TransportRouteSnapshots.Hash(payloadJson), ["S", "H", "X"], ["E-SH", "E-HX"], ["S", "H", "X"]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.Insertable(new AgvCommandOutboxEntity
        {
            CommandId = "CMD-" + stepId, IdempotencyKey = "TEST:" + stepId, TaskId = taskId, StepId = stepId,
            VehicleId = vehicleId, CommandType = (int)AgvCommandType.Transport,
            Status = (int)AgvCommandStatus.Sent, PayloadJson = payloadJson,
            ExecutionMapId = "MAP-O", ExecutionMapVersionId = "V1",
            ExecutionRouteSnapshotJson = snapshotJson,
            ExecutionRouteSnapshotHash = corruptSnapshot ? "BROKEN" : TransportRouteSnapshots.Hash(snapshotJson),
            NextAttemptAt = now, CreatedAt = now, UpdatedAt = now, SentAt = now, VendorCommandId = stepId
        }).ExecuteCommandAsync();
    }
}

sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset current = start;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan span) => current += span;
    public void SetTo(DateTimeOffset value) => current = value;
}

// Vendor gateway: the original chain stays at the configured state until the
// coordinator cancels it; replacement sends are counted and acknowledged.
sealed class RecoveryFake : IAgvGateway, IAgvObstacleRecoveryGateway
{
    public int CancelCalls { get; private set; }
    public int RecoverySendCalls { get; private set; }
    public AgvTransportTaskState OriginalState { get; set; } = AgvTransportTaskState.GoingToDestination;
    // When set, the next replacement send throws this exception instead of accepting.
    public Exception? NextSendFailure { get; set; }
    public IReadOnlyList<string> SentPath { get; private set; } = [];

    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string vendorTaskId,
        CancellationToken cancellationToken) => Status(vendorTaskId);

    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string vendorTaskId, string vehicleId,
        IReadOnlyList<string> originalPath, CancellationToken cancellationToken) => Status(vendorTaskId);

    public Task CancelTaskAsync(string vendorTaskId, CancellationToken cancellationToken)
    {
        CancelCalls++;
        OriginalState = AgvTransportTaskState.Cancelled;
        return Task.CompletedTask;
    }

    public Task<string> CreateObstacleRecoveryTransportTaskAsync(string newVendorTaskId, string vehicleId,
        IReadOnlyList<string> path, AgvTransportAction? action, DateTimeOffset blockedSince,
        string originalVendorTaskId, ObstacleRecoveryPreSendLimits limits, CancellationToken cancellationToken)
    {
        RecoverySendCalls++;
        SentPath = path.ToArray();
        if (NextSendFailure is { } failure)
        {
            NextSendFailure = null;
            throw failure;
        }
        return Task.FromResult(newVendorTaskId);
    }

    public Task<string> CreateTransportTaskAsync(string businessTaskId, string? preferredVehicleId,
        IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("通用发送口在受阻状态必须被拒绝。");

    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    private Task<AgvTransportTaskStatus?> Status(string id) =>
        Task.FromResult<AgvTransportTaskStatus?>(new(id, id, OriginalState, "A", DateTimeOffset.UtcNow, "synthetic"));
}

sealed class Preconditions : ITransportTimeoutRecoveryService
{
    public Task<TransportTimeoutRecoveryAssessment?> CaptureAsync(DispatchTaskWorkItem task,
        AgvTransportTaskStatus status, CancellationToken cancellationToken) =>
        Task.FromResult<TransportTimeoutRecoveryAssessment?>(null);
    public Task<TransportTimeoutRecoverySnapshot?> GetAsync(string taskId, CancellationToken cancellationToken) =>
        Task.FromResult<TransportTimeoutRecoverySnapshot?>(null);
    public Task<TransportTimeoutRecoveryResult> ExecuteAsync(TrafficUserContext user,
        TransportTimeoutRecoveryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new TransportTimeoutRecoveryResult(false, "not used"));
    public Task<AgvTransportTaskStatus?> GetReplacementStatusAsync(DispatchTaskWorkItem task,
        CancellationToken cancellationToken) => Task.FromResult<AgvTransportTaskStatus?>(null);
    public Task<string?> ValidateReplacementPreconditionsAsync(string taskId, string stepId,
        CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

sealed class Envelope : ITrafficBodyEnvelopeProvider
{
    public Task<TrafficBodyEnvelope?> ReadAsync(string vehicleId, CancellationToken ct) =>
        Task.FromResult<TrafficBodyEnvelope?>(new("SIM-A", vehicleId, "RobotWorld", 0.47, true, true));
}
