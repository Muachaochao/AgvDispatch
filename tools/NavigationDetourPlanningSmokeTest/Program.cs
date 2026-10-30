using System.Text;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Service;

var checks = 0;
void Check(bool valid, string name) { if (!valid) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> operation, string name)
{
    try { await operation(); } catch (InvalidOperationException) { Check(true, name); return; }
    throw new Exception(name);
}
var directory = Path.Combine(Path.GetTempPath(), "agv-detour-planning-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(directory, "isolated.db") });
    await new DatabaseInitializer(db).InitializeAsync(default);
    var robot = new Download();
    var imported = new Smap2DImportService().Read(Encoding.UTF8.GetBytes(robot.Json()), "synthetic robot");
    var definition = Smap2DImportService.PrepareRobotWorldRuntime(imported,
        new Dictionary<string, MapNodeType> { ["LM1"] = MapNodeType.Waypoint, ["LM2"] = MapNodeType.Waypoint, ["LM3"] = MapNodeType.Waypoint });
    var runtime = new ActiveMapRuntime(); runtime.Replace(new("MAP", "V1", definition, DateTimeOffset.UtcNow));
    var gate = new MapExecutionGate(); gate.SetMode(MapExecutionMode.Running);
    var closures = new RoadClosureService(db, runtime, gate); await closures.InitializeAsync(default);
    var user = TrafficUserContext.LocalOperation("isolated-map-test");
    var request = new NavigationTestDetourPlanRequest("TASK", "SIM", "LM1", "LM3", "robot-triangle", new string('a', 32),
        [new("LM1", 0, 0), new("LM2", 1, 1), new("LM3", 2, 0)], ["LM1", "LM3"]);
    var planner = new NavigationTestDetourPlanner(runtime, closures, robot);
    Check(!(await planner.PreviewAsync(request, default)).Available && robot.Reads == 0,
        "without explicit closure preview sends no robot request and invents no blocked road");
    var direct = definition.Edges.Single(e => e.From == "BUS-LM1" && e.To == "BUS-LM3");
    var closed = await closures.CreateAsync(user, new("seal", "MAP", "V1", EngineeringRuntimeIdentity.MapHash(definition),
        direct.Id, direct.From, direct.To, true, "synthetic test obstacle"), default);
    Check(closed.Accepted, "seal exact synthetic direct corridor");
    var plan = await planner.PreviewAsync(request, default);
    Check(plan.Available && plan.Route.SequenceEqual(["LM1", "LM2", "LM3"]) && plan.ClosureRevision == closed.Revision,
        "fresh 4011 coordinate/topology evidence yields LM1 via LM2 to LM3");
    await planner.ValidateAsync(plan, default);
    using (await planner.EnterSendAsync(plan, default)) { Check(true, "validated candidate obtains closure-serialized send lease"); }
    await Reject(() => new NavigationTestDetourPlanner(runtime, closures, robot).ValidateAsync(plan, default),
        "process restart cannot reuse an unverified persisted candidate");
    robot.WrongMd5 = true;
    Check(!(await planner.PreviewAsync(request, default)).Available, "same station names on changed robot map MD5 rejected"); robot.WrongMd5 = false;
    robot.Shift = true;
    Check(!(await planner.PreviewAsync(request, default)).Available, "map export with shifted coordinates rejected"); robot.Shift = false;
    robot.MissingRoad = true;
    Check(!(await planner.PreviewAsync(request, default)).Available, "runtime road absent from current robot export rejected"); robot.MissingRoad = false;
    Check(!(await planner.PreviewAsync(request with { Stations = [new("LM1", .2, 0), new("LM2", 1, 1), new("LM3", 2, 0)] }, default)).Available,
        "live station coordinates must also match exported robot map");
    await Reject(() => planner.ValidateAsync(plan with { Route = ["LM1", "LM3"] }, default), "caller cannot replace the verified route");
    await closures.ReleaseAsync(user, new("clear", closed.Closure!.ClosureId, closed.Revision, "synthetic obstacle removed", true), default);
    await Reject(() => planner.ValidateAsync(plan, default), "any closure revision change invalidates a queued detour");
    runtime.Replace(new("MAP", "V2", definition, DateTimeOffset.UtcNow));
    await Reject(() => planner.ValidateAsync(plan, default), "published map version change invalidates a queued detour");
    Check(robot.Reads >= 5, "map checks use only the supplied read-only downloader");
    Console.WriteLine($"Navigation detour planning: {checks} checks passed. No live connections or motion commands.");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(directory, true);
}

sealed class Download : IRobotMapDownloadService
{
    public int Reads; public bool WrongMd5; public bool Shift; public bool MissingRoad;
    public string Json()
    {
        var points = new[] { (Id: "LM1", X: 0d, Y: 0d), (Id: "LM2", X: 1d, Y: 1d), (Id: "LM3", X: 2d, Y: 0d) };
        object Point(string id) { var p = points.Single(p => p.Id == id); return new { instanceName = id, pos = new { x = p.X + (Shift ? 1 : 0), y = p.Y } }; }
        var pairs = new[] { ("LM1", "LM3"), ("LM3", "LM1"), ("LM1", "LM2"), ("LM2", "LM1"), ("LM2", "LM3"), ("LM3", "LM2") };
        return JsonSerializer.Serialize(new
        {
            header = new { mapType = "2D-Map", mapName = "robot-triangle", version = "1.0.6", minPos = new { x = -1, y = -1 }, maxPos = new { x = 3, y = 2 }, resolution = .05 },
            advancedPointList = points.Select(p => new { instanceName = p.Id, className = "LocationMark", pos = new { x = p.X + (Shift ? 1 : 0), y = p.Y } }),
            advancedCurveList = pairs.Where(p => !MissingRoad || p != ("LM2", "LM3")).Select((p, i) => new
            { instanceName = "path-" + i, className = "StraightPath", startPos = Point(p.Item1), endPos = Point(p.Item2) })
        });
    }
    public Task<RobotMapDownload> ReadCurrentAsync(RobotMapDownloadQuery query, CancellationToken ct)
    {
        Reads++; return Task.FromResult(new RobotMapDownload(query.VehicleId, "robot-triangle", new string(WrongMd5 ? 'b' : 'a', 32),
            new string('c', 64), Json(), DateTimeOffset.UtcNow));
    }
}
