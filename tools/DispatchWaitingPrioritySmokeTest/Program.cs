using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using SqlSugar;

// D7: a long-waiting low-priority station ages past a fresh higher-priority call on the legacy path,
// and the optimiser's default wait ceiling covers the full base priority span.
var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
var root = Path.Combine(Path.GetTempPath(), "agv-waiting-priority-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "isolated.db") });
var catalog = new TrafficResourceCatalog([new("R", TrafficResourceType.NarrowAisle)]);
await new DatabaseInitializer(db, catalog).InitializeAsync(default);
var tasks = new DispatchTaskRepository(db);
var orchestration = new DispatchOrchestrationRepository(db);

Check(new DispatchAssignmentOptions().MaxWaitPriority >= 100,
    "the default wait ceiling is at least the base priority span, so waiting can overtake a priority 80 call");

var longWaiting = (await tasks.TryCreateAsync("ST-LOW", "DUMP", 0, default)).Task.TaskId;
await tasks.MarkAcknowledgedAsync(longWaiting, default);
var highPriority = (await tasks.TryCreateAsync("ST-HIGH", "DUMP", 80, default)).Task.TaskId;
await tasks.MarkAcknowledgedAsync(highPriority, default);
var middle = (await tasks.TryCreateAsync("ST-MID", "DUMP", 50, default)).Task.TaskId;
await tasks.MarkAcknowledgedAsync(middle, default);
await db.Updateable<DispatchTaskEntity>().SetColumns(t => new DispatchTaskEntity { CreatedAt = DateTime.Now.AddHours(-3) })
    .Where(t => t.TaskId == longWaiting).ExecuteCommandAsync();

var first = (await orchestration.GetDispatchCandidatesAsync(1, default)).Single();
Check(first.TaskId == longWaiting, "a three-hour-old priority 0 call is selected ahead of a fresh priority 80 call");
var fresh = (await orchestration.GetDispatchCandidatesAsync(10, default)).Where(t => t.TaskId != longWaiting).Select(t => t.TaskId).ToArray();
Check(fresh.SequenceEqual([highPriority, middle]), "fresh calls keep strict priority order");

Console.WriteLine($"PASS: {checks} waiting priority checks; isolated SQLite only.");
