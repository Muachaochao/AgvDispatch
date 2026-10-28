using System.IO;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Tasks;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;

internal static partial class Program
{
    private static void CheckReadOnly28Preparation()
    {
        var root = Path.Combine(Path.GetTempPath(), "agv-readonly-28-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "private.db") });
        Complete(new DatabaseInitializer(db).InitializeAsync(default));
        var map = new ActiveMapRuntime();
        var pickups = Enumerable.Range(1, 7).Select(i => new MapNode("PICK" + i, 5, i * 3,
            MapNodeType.Station, true, [])).ToArray();
        MapNode[] nodes = [new("HOME", 0, 0, MapNodeType.Station, true, []), ..pickups,
            new("LM35", 15, 0, MapNodeType.WaitingPoint, true, ["BAY35"]),
            new("LM36", 15, 3, MapNodeType.WaitingPoint, true, ["BAY36"]),
            new("LM39", 20, 0, MapNodeType.Station, true, ["DUMP"])];
        var edges = pickups.SelectMany(p => new MapEdge[]
        {
            new("HOME-" + p.Id, "HOME", p.Id, 10, 1, false, true, []),
            new(p.Id + "-35", p.Id, "LM35", 10, 1, false, true, []),
            new(p.Id + "-36", p.Id, "LM36", 11, 1, false, true, []),
            new("RETURN-" + p.Id, "LM39", p.Id, 15, 1, false, true, [])
        }).Concat([new("35-DUMP", "LM35", "LM39", 5, 1, false, true, []),
            new("36-DUMP", "LM36", "LM39", 6, 1, false, true, [])]).ToArray();
        var definition = new MapDefinition("isolated seven-station preparation", "", "LocalCartesian", "m", nodes, edges,
            [new("BAY35", TrafficResourceType.Station), new("BAY36", TrafficResourceType.Station),
                new("DUMP", TrafficResourceType.Station, 1, [new(19, -1), new(21, -1), new(21, 1), new(19, 1)])]);
        map.Replace(new("MAP", "VERSION", definition, DateTimeOffset.Now));
        var dispatch = new DispatchOrchestratorOptions { Enabled = false, MaxConcurrentTasks = 3,
            DumperWaitingSite = "LM35", DumperWaitingSites = ["LM35", "LM36"], DumperDumpingSite = "LM39",
            StationPickupSites = Enumerable.Range(1, 7).ToDictionary(i => "CNC00" + i, i => "PICK" + i),
            StepActions = TaskParameterRuntime.TransportSteps.ToDictionary(t => t.ToString(), _ => new AgvStepActionOptions { Operation = "Wait" }) };
        var creation = new DispatchTaskCreationOptions { Enabled = false };
        var steps = new DispatchTaskStepOptions { Enabled = false };
        var handshake = new HandshakeExecutionOptions { Enabled = false, PlcContractVersion = HandshakeExecutionOptions.Version,
            DumperExit = new() { ResourceIds = ["DUMP"], BodyProfiles = [new("BODY", "AGV-01", .5, .05, true, true)] } };
        var plc = new FixturePlcConfiguration();
        var template = plc.Signals.Where(s => s.StationId == "CNC001").ToArray();
        foreach (var i in Enumerable.Range(2, 6))
            foreach (var signal in template)
                plc.Signals.Add(signal with { StationId = "CNC00" + i, Address = "DB1.DBX" + plc.Signals.Count + ".0" });
        plc.Options.AllowWrites = false;
        var gate = new FixtureGate { RecoveryOpen = false };
        var runtime = new TaskParameterRuntime(dispatch, creation);
        var service = new ManualTaskCreationService(new DispatchTaskRepository(db), new FixtureStations(), new FixtureGateway(),
            new FixtureStates(), map, gate, new DispatchOrchestrationRepository(db), creation, dispatch, steps, new(), plc, runtime,
            handshakeOptions: handshake);
        var profile = runtime.Capture();
        var rows = service.ValidateParameters(profile);
        Require(rows.Count == 63 && rows.All(r => r.Step.ValidationErrors.Count == 0),
            "seven stations and two waiting bays validate configured 28-stage contract with all execution switches off");
        Require(rows.All(r => r.Step.ConfigurationNote.Contains("执行未启用")) &&
            rows.Where(r => r.Step.StepType == DispatchTaskStepType.ReturnEmptyBox).All(r => r.Step.PlcHandshake.Contains("完全出区")),
            "preparation preview describes configured 28-stage timing and explicitly reports executor disabled");

        handshake.DumperExit.ResourceIds = [];
        handshake.DumperExit.BodyProfiles = [];
        handshake.DumperExit.MaximumAgeSeconds = 0;
        var invalidExit = service.ValidateParameters(profile).Where(r => r.Step.StepType == DispatchTaskStepType.ReturnEmptyBox).ToArray();
        Require(invalidExit.All(r => r.Step.ValidationErrors.Any(e => e.Contains("ResourceIds")) &&
            r.Step.ValidationErrors.Any(e => e.Contains("BodyProfiles")) && r.Step.ValidationErrors.Any(e => e.Contains("观察间隔"))),
            "read-only preparation still validates 28-stage exit resources, body profiles and observation thresholds");
        handshake.DumperExit = new() { ResourceIds = ["DUMP"], BodyProfiles = [new("BODY", "AGV-01", .5, .05, true, true)] };
        var missing = plc.Signals.Single(s => s.StationId == "DUMPER-001" && s.VariableName == "AgvAtDumpingPosition");
        plc.Signals.Remove(missing);
        var invalidPlc = service.ValidateParameters(profile);
        Require(invalidPlc.Where(r => r.Step.StepType is DispatchTaskStepType.WaitForDumperReady or DispatchTaskStepType.ReturnEmptyBox)
            .All(r => r.Step.ValidationErrors.Any(e => e.Contains("AgvAtDumpingPosition"))),
            "read-only preparation retains 28-stage shared PLC baseline and exit signal checks");
        plc.Signals.Add(missing);
        handshake.PlcContractVersion = "wrong-contract";
        Require(service.ValidateParameters(profile).Any(r => r.Step.ValidationErrors.Any(e => e.Contains("28阶段"))),
            "preparation cannot bypass multi-bay protection with an unrecognized contract version");
        handshake.PlcContractVersion = HandshakeExecutionOptions.Version;

        var settings = new TaskParameterSettingsService(new(Path.Combine(root, "parameters.json")), runtime, service,
            new DispatchTaskReadRepository(db), map, gate);
        var saved = Complete(settings.SaveAsync(TrafficUserContext.LocalOperation("isolated-test"),
            new(Guid.NewGuid().ToString("N"), runtime.Revision, "MAP", "VERSION", profile), default));
        Require(saved.Accepted && saved.Snapshot!.Configuration.WaitingSites.SequenceEqual(["LM35", "LM36"]) &&
            !gate.CanStartNewWork && !handshake.Enabled && !dispatch.Enabled && !creation.Enabled && !steps.Enabled && !plc.Options.AllowWrites,
            "read-only preparation saves both bays without opening recovery, PLC writes or any execution switch");
        gate.RecoveryOpen = true;
        dispatch.Enabled = creation.Enabled = steps.Enabled = plc.Options.AllowWrites = true;
        var live = Complete(service.GetOptionsAsync(default));
        Require(!live.Available && live.Stations.All(s => s.PreviewSteps[2].ValidationErrors.Any(e => e.Contains("启用当前28阶段"))) &&
            live.Stations.All(s => !s.PreviewSteps[2].ConfigurationNote.Contains("契约 " + HandshakeExecutionOptions.Version)),
            "live creation still refuses multi-bay dispatch while handshake executor is disabled and does not claim it enabled");
        Require(db.Queryable<DispatchTaskEntity>().Count() == 0 && db.Queryable<AgvCommandOutboxEntity>().Count() == 0,
            "preparation and options checks persist no business tasks or vehicle commands");
    }
}
