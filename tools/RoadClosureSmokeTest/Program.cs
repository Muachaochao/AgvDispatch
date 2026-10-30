using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;

var checks = 0;
void Check(bool valid, string name) { if (!valid) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
async Task Rejects(Func<Task> action, string name)
{
    try { await action(); } catch (InvalidOperationException) { Check(true, name); return; }
    throw new Exception(name);
}
var directory = Path.Combine(Path.GetTempPath(), "agv-road-closure-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    using var database = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(directory, "isolated.db") });
    await new DatabaseInitializer(database).InitializeAsync(default);
    MapNode Node(string id, double x, double y) => new(id, x, y, MapNodeType.Waypoint, true, [], id);
    MapEdge Edge(string id, string from, string to, double distance, bool both = false) => new(id, from, to, distance, 1, both, true, []);
    var definition = new MapDefinition("isolated-triangle", "", "LocalCartesian", "m",
        [Node("LM1", 0, 0), Node("LM2", 5, 5), Node("LM3", 10, 0)],
        [Edge("direct", "LM1", "LM3", 1, true), Edge("parallel", "LM1", "LM3", 2),
         Edge("reverse", "LM3", "LM1", 1), Edge("via-12", "LM1", "LM2", 5, true), Edge("via-23", "LM2", "LM3", 5, true)], []);
    var runtime = new ActiveMapRuntime();
    runtime.Replace(new("MAP", "V1", definition, DateTimeOffset.UtcNow));
    var gate = new MapExecutionGate(); gate.SetMode(MapExecutionMode.Running);
    var closure = new RoadClosureService(database, runtime, gate);
    var provider = new MapDefinitionProvider(new(), new MapImportService(), new MapValidationService());
    var dispatch = new DispatchOrchestratorOptions();
    var planner = new ConfiguredRoutePlanner(provider, runtime, dispatch, new EmptyFlow(), closure);
    var user = TrafficUserContext.LocalOperation("isolated-test-operator");
    RoadClosureCreateRequest Create(string edge, string from, string to, bool both = true) =>
        new(Guid.NewGuid().ToString("N"), "MAP", "V1", EngineeringRuntimeIdentity.MapHash(definition), edge, from, to, both, "isolated obstacle test");
    RoadClosurePreviewRequest Preview(string from = "LM1", string to = "LM3") =>
        new("MAP", "V1", EngineeringRuntimeIdentity.MapHash(definition), closure.GetSnapshot().Revision, from, to);
    Check(planner.Plan("LM1", "LM3", new HashSet<string>()) is null, "planning waits until durable closures are initialized");
    await Rejects(async () => { using var lease = await closure.EnterRouteSendAsync(["LM1", "LM3"], default); }, "send gate refuses uninitialized closure state");
    await closure.InitializeAsync(default);
    Check(closure.GetSnapshot() is { Ready: true, Revision: 0, Closures.Count: 0 }, "new isolated database starts with ready empty closure state");
    Check(planner.Plan("LM1", "LM3", new HashSet<string>())!.Nodes.SequenceEqual(["LM1", "LM3"]), "triangle initially uses direct LM1 to LM3");
    var denied = await closure.CreateAsync(new("monitor", "monitor", TrafficPermission.View), Create("direct", "LM1", "LM3"), default);
    Check(!denied.Accepted && closure.GetSnapshot().Revision == 0, "monitor cannot mutate road closures");
    Check(!(await closure.CreateAsync(user, Create("direct", "LM1", "LM3") with { ExpectedDefinitionHash = "stale" }, default)).Accepted,
        "stale map fingerprint rejects sealing before persistence");
    Check(!(await closure.CreateAsync(user, Create("parallel", "LM3", "LM1", false), default)).Accepted, "one-way edge cannot select unsupported reverse direction");
    var request = Create("direct", "LM1", "LM3");
    var created = await closure.CreateAsync(user, request, default);
    Check(created.Accepted && created.Revision == 1 && created.Closure is { Active: true, Traversals.Count: 4 },
        "bidirectional seal expands to reverse traversal and every parallel station-pair edge");
    Check(created.Closure!.CreatedBy == "isolated-test-operator" && created.Closure.Source == "Operator", "closure audit uses trusted backend user and explicit source");
    Check((await closure.CreateAsync(user, request, default)).Revision == 1 && database.Queryable<RoadClosureEntity>().Count() == 1,
        "same request is idempotent without duplicated closure or revision");
    Check(!(await closure.CreateAsync(user, request with { Reason = "different intent" }, default)).Accepted, "request ID cannot be reused for a different closure intent");
    Check(planner.Plan("LM1", "LM3", new HashSet<string>())!.Nodes.SequenceEqual(["LM1", "LM2", "LM3"]), "new planning detours LM1 through LM2 to LM3");
    Check(planner.Plan("LM3", "LM1", new HashSet<string>())!.Nodes.SequenceEqual(["LM3", "LM2", "LM1"]), "bidirectional physical channel blocks opposite direction too");
    var preview = await closure.PreviewAsync(Preview(), default);
    Check(preview.Available && preview.Route!.Nodes.SequenceEqual(["LM1", "LM2", "LM3"]) && preview.Revision == created.Revision,
        "candidate preview reports exact closure revision and explicit station chain");
    Check(!(await closure.PreviewAsync(Preview() with { ExpectedRevision = 0 }, default)).Available, "stale preview revision fails closed");
    await Rejects(async () => { using var lease = await closure.EnterRouteSendAsync(["LM1", "LM3"], default); }, "already planned or queued direct route cannot send after sealing");
    await Rejects(async () => { using var lease = await closure.EnterRouteSendAsync(["LM3"], default); }, "active seal rejects target-only chain without actual source");
    await Rejects(async () => { using var lease = await closure.EnterRouteSendAsync(["UNKNOWN", "LM3"], default); }, "active seal rejects unmapped station chain");
    await Rejects(async () => { using var lease = await closure.EnterRouteSendAsync(["LM1", "LM1"], default); }, "active seal rejects nonexistent adjacent movement");
    using (await closure.EnterRouteSendAsync(["LM1", "LM2", "LM3"], default)) { Check(true, "current alternate route can enter final send gate"); }
    var send = await closure.EnterRouteSendAsync(["LM1", "LM2", "LM3"], default);
    var secondCreate = closure.CreateAsync(user, Create("via-12", "LM1", "LM2"), default);
    Check(!secondCreate.IsCompleted, "closure mutation waits while a final send lease is active");
    send.Dispose();
    var isolated = await secondCreate;
    Check(isolated.Accepted && planner.Plan("LM1", "LM3", new HashSet<string>()) is null, "sealing the only alternate produces no route instead of unsafe fallback");
    Check(!(await closure.PreviewAsync(Preview(), default)).Available, "no-path preview reports unavailable without fabricating progress");
    Check(!(await closure.ReleaseAsync(user, new("stale-release", isolated.Closure!.ClosureId, 1, "cleaned", true), default)).Accepted,
        "release requires current closure revision");
    Check(!(await closure.ReleaseAsync(user, new("unconfirmed-release", isolated.Closure!.ClosureId, isolated.Revision, "cleaned", false), default)).Accepted,
        "release requires explicit site-cleared confirmation");
    var releaseRequest = new RoadClosureReleaseRequest("release-isolation", isolated.Closure!.ClosureId, isolated.Revision, "isolated channel cleared", true);
    var released = await closure.ReleaseAsync(user, releaseRequest, default);
    Check(released.Accepted && !released.Closure!.Active && released.Closure.ReleasedBy == user.WindowsIdentity && released.Closure.ReleasedAt is not null,
        "release persists operator, reason and UTC timestamp while retaining creation audit");
    Check((await closure.ReleaseAsync(user, releaseRequest, default)).Revision == released.Revision, "release retry is idempotent even after its expected revision becomes old");
    Check(planner.Plan("LM1", "LM3", new HashSet<string>())!.Nodes.SequenceEqual(["LM1", "LM2", "LM3"]), "release restores alternate while remaining direct seal still applies");
    var directional = await closure.CreateAsync(user, Create("direct", "LM1", "LM3", false), default);
    await closure.ReleaseAsync(user, new("release-both", created.Closure!.ClosureId, directional.Revision, "cleared bilateral obstacle", true), default);
    Check(planner.Plan("LM1", "LM3", new HashSet<string>())!.Nodes.SequenceEqual(["LM1", "LM2", "LM3"]), "overlapping seal remains effective when another is released");
    Check(planner.Plan("LM3", "LM1", new HashSet<string>())!.Nodes.SequenceEqual(["LM3", "LM1"]), "single-direction seal preserves reverse travel");
    var restart = new RoadClosureService(database, runtime, gate); await restart.InitializeAsync(default);
    Check(restart.GetSnapshot().Revision == closure.GetSnapshot().Revision && restart.GetSnapshot().Closures.Count == 3 &&
        restart.GetSnapshot().Closures.Count(c => c.Active) == 1, "restart restores active seals and released history without expiry");
    await Rejects(async () => { using var lease = await restart.EnterRouteSendAsync(["LM1", "LM3"], default); }, "restored closure blocks direct send after process restart");
    await GatewayChecks.Run(restart, Check);
    runtime.Replace(new("MAP", "V2", definition, DateTimeOffset.UtcNow));
    Check(closure.GetSnapshot().BlockedTraversals.Count == 0 && planner.Plan("LM1", "LM3", new HashSet<string>())!.Nodes.Count == 2,
        "closures are bound to map version and cannot silently apply to a different version");
    runtime.Replace(new("MAP", "V1", definition with { Name = "changed-without-version" }, DateTimeOffset.UtcNow));
    Check(closure.GetSnapshot().IdentityConflict && planner.Plan("LM1", "LM3", new HashSet<string>()) is null,
        "same-version definition mutation blocks planning until persistence is reviewed");
    await Rejects(async () => { using var lease = await closure.EnterRouteSendAsync(["LM1", "LM3"], default); }, "same-version identity conflict also blocks final sends");
    runtime.Replace(null);
    Check(planner.Plan("LM1", "LM3", new HashSet<string>()) is null, "missing active map cannot use legacy fallback around durable closures");
    await Rejects(async () => { using var lease = await closure.EnterRouteSendAsync(["LM1", "LM3"], default); }, "missing active map cannot send around persisted closures");
    var auditProtected = false;
    try { database.Ado.ExecuteCommand("UPDATE traffic_road_closure_event SET Reason='tampered'"); } catch { auditProtected = true; }
    Check(auditProtected, "database rejects mutation of closure event audit");
    var identityProtected = false;
    try { database.Ado.ExecuteCommand("UPDATE traffic_road_closure SET FromNodeId='LM2'"); } catch { identityProtected = true; }
    Check(identityProtected, "database rejects edits to immutable closure identity");
    var missingReleaseRevisionProtected = false;
    try { database.Ado.ExecuteCommand("UPDATE traffic_road_closure SET Active=0,ReleasedBy='bad',ReleasedAtUtc='2026-10-30',ReleaseReason='bad' WHERE Active=1"); }
    catch { missingReleaseRevisionProtected = true; }
    Check(missingReleaseRevisionProtected, "database rejects release without a revision instead of treating SQL NULL comparison as valid");
    Check(database.Queryable<RoadClosureEventEntity>().Count() == 5, "create and release each retain one append-only event");
    Check(database.Queryable<DispatchTaskEntity>().Count() == 0 && database.Queryable<AgvCommandOutboxEntity>().Count() == 0,
        "sealing and preview never create tasks, send commands or fabricate completion");
    database.Ado.ExecuteCommand("UPDATE traffic_road_closure SET Active=0,ReleasedBy='external',ReleasedAtUtc='2026-10-30',ReleaseReason='missing event',ReleasedRevision=999 WHERE Active=1");
    var brokenRestart = new RoadClosureService(database, runtime, gate);
    var corruptHeld = false;
    try { await brokenRestart.InitializeAsync(default); } catch (InvalidDataException) { corruptHeld = true; }
    Check(corruptHeld && !brokenRestart.GetSnapshot().Ready, "restart refuses a row release without its matching durable audit event");
    await Rejects(async () => { using var lease = await brokenRestart.EnterRouteSendAsync(["LM1", "LM3"], default); },
        "failed durable recovery keeps final route sends blocked");
    Console.WriteLine($"PASS: {checks} isolated road closure checks");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    // This process owns only the GUID directory it created above.
    if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(directory, true);
}

sealed class EmptyFlow : ITrafficFlowMonitor
{
    public TrafficFlowSnapshot GetSnapshot() => new(DateTimeOffset.UtcNow, null, null, [], []);
    public IReadOnlyDictionary<string, double> GetEdgePenalties() => new Dictionary<string, double>();
}
