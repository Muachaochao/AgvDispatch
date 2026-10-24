using AgvDispatch.WebHost;
var root = Path.Combine(Path.GetTempPath(), "agv-display-areas-test-" + Guid.NewGuid().ToString("N"));
var store = new DisplayAreaStore(root);
var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); count++; }
async Task Reject(Func<Task> run, string name) { try { await run(); } catch (Exception e) when (e is ArgumentException or ProjectConflictException) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
var a = new DisplayArea(Guid.NewGuid().ToString(), "上料区", "#42bde0", [new(0,0),new(4,0),new(4,3),new(0,3)], ["LM1","LM2"]);
var q = new DisplayAreaSave("", "basis", [a]);
Check(await store.ReadAsync("MAP-A", default) is null, "new map starts empty");
var doc = await store.SaveAsync("MAP-A", q, ["LM1", "LM2"], default);
Check(doc.Areas[0].StationIds.Length == 2 && doc.Areas[0].Name == "上料区", "saves membership and Unicode name");
var reopened = await new DisplayAreaStore(root).ReadAsync("MAP-A", default);
Check(reopened?.Revision == doc.Revision, "persists across store restart");
Check(await store.ReadAsync("MAP-B", default) is null, "map namespaces stay separate");
await Reject(() => store.SaveAsync("MAP-A", q, ["LM1","LM2"], default), "stale revision rejected");
await Reject(() => store.SaveAsync("MAP-B", q, ["LM1"], default), "missing station rejected");
await Reject(() => store.SaveAsync("MAP-B", q with { Areas=[a with { Boundary=[new(0,0),new(3,3),new(0,3),new(3,0)] }] }, ["LM1","LM2"], default), "self intersection rejected");
await Reject(() => store.SaveAsync("MAP-B", q with { Areas=[a with { Boundary=[new(0,0),new(1,0),new(2,0)] }] }, ["LM1","LM2"], default), "zero area rejected");
await Reject(() => store.SaveAsync("MAP-B", q with { Areas=[a with { Color="url(unsafe)" }] }, ["LM1","LM2"], default), "invalid color rejected");
await Reject(() => store.SaveAsync("MAP-B", q with { Areas=[a,a] }, ["LM1","LM2"], default), "duplicate region ID rejected");
await Reject(() => store.SaveAsync("MAP-B", q with { Areas=[a with { Boundary=[new(double.NaN,0),new(1,0),new(1,1)] }] }, ["LM1","LM2"], default), "nonfinite coordinates rejected");
var hidden = await store.SaveAsync("MAP-A", q with {ExpectedRevision=doc.Revision,Areas=[a with { ShowBoundary=false,ShowName=false }]}, ["LM1","LM2"], default);
Check(!hidden.Areas[0].ShowName && !hidden.Areas[0].ShowBoundary, "display switches preserved");
var cleared = await store.SaveAsync("MAP-A", q with {ExpectedRevision=hidden.Revision,Areas=[]}, ["LM1","LM2"], default);
Check(cleared.Areas.Length == 0, "deletion persists without touching map geometry");
Console.WriteLine($"{count} display-area checks passed. Evidence: {root}");
