using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;

var path = Path.Combine(Path.GetTempPath(), $"agv-task-center-{Guid.NewGuid():N}.db");
using var database = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = path });
await new DatabaseInitializer(database).InitializeAsync(CancellationToken.None);
var writer = new DispatchTaskRepository(database);
var reader = new TaskCenterReadRepository(database);
var cache = new TestSignalCache();
var creation = new DispatchTaskCreationOptions();
var dispatch = new DispatchOrchestratorOptions { DumperWaitingSite = "WAIT", DumperDumpingSite = "DUMP", StationPickupSites = new() { ["CNC001"] = "PICK" } };
var parameters = new TaskParameterRuntime(dispatch, creation);
var clock = new TestClock();
var service = new TaskCenterQueryService(reader, cache, creation, parameters, clock);
var token = CancellationToken.None;
Require((await reader.QueryAsync(new(), token)).TotalCount == 0, "empty database");
var a = await writer.TryCreateAsync("CNC001", "DUMPER-001", 10, token);
var b = await writer.TryCreateAsync("CNC002", "DUMPER-001", 5, token);
var c = await writer.TryCreateAsync("CNC003", "DUMPER-001", 0, token);
await database.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.Completed)
    .Where(t => t.TaskId == b.Task.TaskId).ExecuteCommandAsync();
await database.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.Faulted)
    .Where(t => t.TaskId == c.Task.TaskId).ExecuteCommandAsync();
foreach (var filter in new[] { TaskListFilter.Waiting, TaskListFilter.Failed, TaskListFilter.Completed })
    Require((await reader.QueryAsync(new(filter), token)).TotalCount == 1, $"filter {filter}");
Require((await reader.QueryAsync(new(TaskListFilter.Running), token)).TotalCount == 0, "running filter");
Require((await reader.QueryAsync(new(Search: "CNC001"), token)).TotalCount == 1, "station search");
Require((await reader.QueryAsync(new(Search: "' OR 1=1 --"), token)).TotalCount == 0, "parameterized search");
var page1 = await reader.QueryAsync(new(PageSize: 1), token);
var page2 = await reader.QueryAsync(new(Page: 2, PageSize: 1), token);
Require(page1.TotalCount == 3 && page1.Items.Single().TaskId != page2.Items.Single().TaskId, "stable paging");
try { await reader.QueryAsync(new(PageSize: 101), token); Require(false, "limit rejected"); }
catch (ArgumentException) { }
Require(await reader.GetDetailAsync("missing", token) is null, "unknown task");
var detail = (await service.GetDetailAsync(a.Task.TaskId, token))!;
Require(detail.Steps.Count == 9 && detail.Steps[0].Sequence < detail.Steps[8].Sequence, "ordered nine steps");
Require(detail.PlcConditions.Single().Quality == SignalQuality.Unknown && !detail.PlcConditions.Single().Satisfied, "missing signal not satisfied");
await database.Updateable<DispatchTaskEntity>().SetColumns(task => task.Stage == (int)DispatchTaskStage.WaitingForVehicle)
    .Where(task => task.TaskId == a.Task.TaskId).ExecuteCommandAsync();
await database.Insertable(new TrafficRequestEntity
{
    StepId = detail.Steps[0].StepId, TaskId = a.Task.TaskId, VehicleId = "AGV-01",
    Source = "LM8", Destination = "PICK", EdgeIds = "[]", ResourceIds = "[]",
    State = "Requested", Reason = "派车前准备失败，未发送：车端地图核验未通过",
    WaitingCategory = "Route", RequestedAt = DateTime.Now, UpdatedAt = DateTime.Now
}).ExecuteCommandAsync();
var diagnosticService = new TaskCenterQueryService(reader, cache, creation, parameters, clock,
    trafficControl: new TrafficResourceControlService(database, new TrafficResourceCatalog([])));
var dispatchDetail = (await diagnosticService.GetDetailAsync(a.Task.TaskId, token))!;
Require(dispatchDetail.WaitingExplanation.Contains("AGV-01") &&
    dispatchDetail.WaitingExplanation.Contains("LM8→PICK") &&
    dispatchDetail.WaitingExplanation.Contains("车端地图核验未通过"),
    "待派车任务显示候选车辆、已规划路线和最近发送前失败原因");
await database.Deleteable<TrafficRequestEntity>().Where(request => request.TaskId == a.Task.TaskId).ExecuteCommandAsync();
await database.Updateable<DispatchTaskEntity>().SetColumns(task => task.Stage == (int)DispatchTaskStage.Created)
    .Where(task => task.TaskId == a.Task.TaskId).ExecuteCommandAsync();
cache.Set("CNC001", "FullBoxRequestAcknowledged", true, SignalQuality.Stale);
Require(!(await service.GetDetailAsync(a.Task.TaskId, token))!.PlcConditions.Single().Satisfied, "stale true not satisfied");
cache.Set("CNC001", "FullBoxRequestAcknowledged", true, SignalQuality.Good);
Require((await service.GetDetailAsync(a.Task.TaskId, token))!.PlcConditions.Single().Satisfied, "good true satisfied");
await writer.MarkAcknowledgedAsync(a.Task.TaskId, token);
var outbox = new AgvCommandOutboxRepository(database);
var first = (await writer.GetStepsAsync(a.Task.TaskId, token))[0];
Require(await outbox.TryPrepareTransportStepAsync(a.Task.TaskId, first.StepId, "AGV-01",
    ["PARK", "CNC001"], null, token), "command preparation");
var commands = await outbox.GetDueCommandsAsync(DateTimeOffset.Now.AddSeconds(1), 10, token);
await outbox.TryClaimAsync(commands.Single().CommandId, DateTimeOffset.Now, token);
await database.Insertable(new TrafficResourceLeaseEntity
{
    LeaseId = Guid.NewGuid().ToString("N"), ResourceId = "NARROW-01", OwnerStepId = first.StepId,
    TaskId = a.Task.TaskId, VehicleId = "AGV-01", AcquiredAt = DateTime.Now.AddMinutes(-2),
    RenewedAt = DateTime.Now.AddMinutes(-2), ExpiresAt = DateTime.Now.AddMinutes(-1)
}).ExecuteCommandAsync();
detail = (await service.GetDetailAsync(a.Task.TaskId, token))!;
Require(detail.Task.VehicleId == "AGV-01" && detail.Commands.Single().AttemptCount == 1, "vehicle and attempts");
Require(detail.Resources.Single().HeartbeatExpired, "expired lease remains visible");
Require(await database.Queryable<TrafficResourceLeaseEntity>().CountAsync() == 1, "read does not release lease");
Require((await service.GetDetailAsync(b.Task.TaskId, token))!.PlcConditions.Count == 0, "terminal task not waiting for plc");
var fixtureNow = DateTime.Now; clock.Now = new DateTimeOffset(fixtureNow);
var secondStep = (await writer.GetStepsAsync(a.Task.TaskId, token))[1];
await database.Updateable<DispatchTaskStepEntity>().SetColumns(step => new DispatchTaskStepEntity
    { Status = (int)DispatchTaskStepStatus.Completed, StartedAt = fixtureNow.AddSeconds(-65), CompletedAt = fixtureNow.AddSeconds(-5) })
    .Where(step => step.StepId == first.StepId).ExecuteCommandAsync();
await database.Updateable<DispatchTaskStepEntity>().SetColumns(step => new DispatchTaskStepEntity
    { Status = (int)DispatchTaskStepStatus.Ready, StartedAt = fixtureNow.AddHours(-25), ErrorMessage = "fixture PLC write timeout" })
    .Where(step => step.StepId == secondStep.StepId).ExecuteCommandAsync();
await database.Updateable<DispatchTaskEntity>().SetColumns(task => task.Stage == (int)DispatchTaskStage.PickingUp)
    .Where(task => task.TaskId == a.Task.TaskId).ExecuteCommandAsync();
cache.Set("CNC001", "BoxPresent", false, SignalQuality.Stale);
cache.Set("CNC001", "FullBoxRemoved", true, SignalQuality.Good);
cache.Set("CNC001", "EmptyBoxTaskAcknowledged", true, SignalQuality.Good);
detail = (await service.GetDetailAsync(a.Task.TaskId, token))!;
Require(detail.Steps.Count(step => step.IsCurrent) == 1 && detail.Steps[1].IsCurrent && !detail.Steps[0].IsCurrent, "only first unfinished step highlighted");
Require(detail.Steps[0].ElapsedText == "00:01:00" && detail.Steps[1].ElapsedText.StartsWith("25:"), "completed duration frozen and active hours exceed 24");
Require(detail.Steps[2].ElapsedText == "未开始", "pending step does not invent elapsed time");
Require(detail.WaitingExplanation.Contains("BoxPresent") && detail.WaitingExplanation.Contains("Stale") &&
    detail.Steps[1].WaitingExplanation.Contains("fixture PLC write timeout"), "waiting reason identifies bad-quality PLC input and latest step error");
Require(detail.DiagnosticsObservedAt == clock.Now && detail.Steps[1].ProgressConditions.Count == 3, "diagnostics use backend clock and current step conditions");
Require(detail.Steps[1].ProgressConditions[0].Result == "Unknown" && detail.Steps[1].ProgressConditions[0].ObservedAt != null, "stale value exposes unknown and source time");
Require(detail.Steps[0].ProgressConditions.Count == 0 && detail.Steps[2].ProgressConditions.Count == 0, "past and future steps never re-evaluated using live PLC");
Require(detail.Steps.All(s => s.CompletionRequirement.Length > 0 && s.EvidenceBoundary.Length > 0), "all nine steps have current behavior and evidence boundary");
Require(detail.Steps[1].ObservedPhase.Contains("Unknown") && detail.Steps[1].EvidenceBoundary.Contains("ACK"), "phase identifies unknown; readback not mislabelled business ACK");
var predicateSnapshot = new PlcSignalSnapshot("TEST", "Input", "输入", "DB1.DBX0.0", "Bool", "PlcToHost", "Read", true, null, SignalQuality.Good, clock.Now, clock.Now, 1);
Require(!TaskSignalPredicate.TryRead(predicateSnapshot, out _) && TaskSignalPredicate.Evaluate("TEST", "Input", false, predicateSnapshot).Result == "Unknown", "Good but null is unknown rather than false");
var eventTime = DateTime.Now;
await database.Insertable(new ExecutionEvidenceEntity { Id = "diagnostic-other-task", TaskId = b.Task.TaskId, StepId = "foreign", Kind = "AgvSendIntent", Evidence = "{\"private\":\"DO-NOT-EXPOSE\"}", OccurredAt = eventTime }).ExecuteCommandAsync();
await database.Insertable(new ExecutionEvidenceEntity { Id = "diagnostic-condition", TaskId = a.Task.TaskId, StepId = secondStep.StepId, Kind = "StepConditionsObserved", Evidence = JsonSerializer.Serialize(new { Conditions = detail.Steps[1].ProgressConditions }), OccurredAt = eventTime }).ExecuteCommandAsync();
await database.Insertable(new ExecutionEvidenceEntity { Id = "diagnostic-bad-json", TaskId = a.Task.TaskId, StepId = secondStep.StepId, Kind = "StepConditionsObserved", Evidence = "{malformed", OccurredAt = eventTime }).ExecuteCommandAsync();
var diagnostics = (await service.GetDetailAsync(new TaskDetailRequest(a.Task.TaskId, false), token))!;
Require(diagnostics.ExecutionTimeline.Any(e => e.Id == "diagnostic-condition" && e.Summary.Contains("BoxPresent")), "persistent conditions available independently of operation history");
Require(diagnostics.ExecutionTimeline.Any(e => e.Kind == "TaskCreated") && diagnostics.ExecutionTimeline.Any(e => e.Kind == "StepCompleted"), "timeline includes real persisted business milestones");
Require(!diagnostics.ExecutionTimeline.Any(e => e.Id == "diagnostic-other-task") && !JsonSerializer.Serialize(diagnostics).Contains("DO-NOT-EXPOSE"), "timeline task isolation and no arbitrary evidence payload leak");
Require(diagnostics.ExecutionTimeline.Single(e => e.Id == "diagnostic-bad-json").Summary.Contains("不可解析"), "malformed historical evidence does not break detail");
Require(diagnostics.ExecutionTimeline.Select(e => e.OccurredAt).SequenceEqual(diagnostics.ExecutionTimeline.Select(e => e.OccurredAt).OrderBy(t => t)), "timeline deterministic chronological order");
var restartRead = await new TaskCenterQueryService(new TaskCenterReadRepository(database), cache, creation, parameters, clock).GetDetailAsync(a.Task.TaskId, token);
Require(restartRead!.ExecutionTimeline.Any(e => e.Id == "diagnostic-condition"), "evidence survives new query service instance");
var config = parameters.Capture() with { StepTimeouts = parameters.Capture().StepTimeouts.Select(row => row with { Seconds = 60 }).ToArray() };
// Populate the required transport actions to reuse the real runtime validation.
config = config with { Actions = config.Actions.Select(row => row with { Operation = "Wait" }).ToArray() };
var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(config))!;
legacyJson.AsObject().Remove("StepTimeouts");
var legacyConfig = System.Text.Json.JsonSerializer.Deserialize<TaskParameterConfiguration>(legacyJson.ToJsonString())!;
Require(legacyConfig.StepTimeouts.Count == 9 && legacyConfig.StepTimeouts.All(row => row.Seconds == 0), "old saved JSON without threshold property defaults to disabled");
parameters.Apply(new(1, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "MAP", "VERSION", config, []));
clock.Now = new DateTimeOffset(fixtureNow.AddHours(-25).AddSeconds(59));
detail = (await service.GetDetailAsync(a.Task.TaskId, token))!;
Require(!detail.Steps[1].IsTimedOut && detail.Steps[1].TimeoutSeconds == 60, "below threshold does not alert");
clock.Now = clock.Now.AddSeconds(1);
detail = (await service.GetDetailAsync(a.Task.TaskId, token))!;
Require(detail.Steps[1].IsTimedOut && detail.Steps[1].TimeoutText.Contains("超出 0 秒") && detail.Steps[1].WaitingExplanation.Contains("Stale"), "exact threshold alerts with waiting reason");
clock.Now = clock.Now.AddSeconds(15);
detail = (await service.GetDetailAsync(a.Task.TaskId, token))!;
Require(detail.Steps.Count(row => row.IsTimedOut) == 1 && detail.Steps[1].TimeoutText.Contains("超出 15 秒"), "only active step alerts and overrun advances");
Require((await reader.GetDetailAsync(a.Task.TaskId, token))!.Task.Stage == DispatchTaskStage.PickingUp && await database.Queryable<TrafficResourceLeaseEntity>().CountAsync() == 1, "timeout query does not cancel task or release traffic");
parameters.Apply(new(1, Guid.NewGuid().ToString("N"), "", "MAP", "VERSION", config with { StepTimeouts = config.StepTimeouts.Select(row => row with { Seconds = 0 }).ToArray() }, []));
Require(!(await service.GetDetailAsync(a.Task.TaskId, token))!.Steps[1].IsTimedOut, "zero threshold disables alert");
parameters.Apply(new(1, Guid.NewGuid().ToString("N"), "", "MAP", "VERSION", config, []));
await database.Updateable<DispatchTaskStepEntity>().SetColumns(row => row.StartedAt == null).Where(row => row.StepId == secondStep.StepId).ExecuteCommandAsync();
Require(!(await service.GetDetailAsync(a.Task.TaskId, token))!.Steps[1].IsTimedOut, "legacy missing timestamp cannot invent timeout");
await database.Updateable<DispatchTaskStepEntity>().SetColumns(row => row.StartedAt == fixtureNow.AddHours(-25)).Where(row => row.StepId == secondStep.StepId).ExecuteCommandAsync();
await database.Updateable<DispatchTaskEntity>().SetColumns(task => new DispatchTaskEntity { Stage = (int)DispatchTaskStage.Cancelled, UpdatedAt = fixtureNow })
    .Where(task => task.TaskId == a.Task.TaskId).ExecuteCommandAsync();
detail = (await service.GetDetailAsync(a.Task.TaskId, token))!;
Require(detail.Steps.All(step => !step.IsCurrent && !step.IsTimedOut) && detail.Steps[1].ElapsedText == "25:00:00（截至任务结束）", "ended task has no current step and stopped elapsed counter");
// Read durable audits through task ownership and recovery batch tickets.
var historyAt = new DateTime(2026, 1, 1, 12, 0, 0);
await database.Insertable(new[] {
 new ExecutionRecoveryTicketEntity { TicketId="ha", SessionId="session-a", TaskId=a.Task.TaskId },
 new ExecutionRecoveryTicketEntity { TicketId="hb", SessionId="session-b", TaskId=b.Task.TaskId }
}).ExecuteCommandAsync();
ExecutionEvidenceEntity HistoryEvent(string id, string task, string kind, int seconds, string session="", string evidence="{}") =>
 new() { Id=id, TaskId=task, Kind=kind, SessionId=session, OccurredAt=historyAt.AddSeconds(seconds), Operator="历史测试员", Reason="现场核对依据", Evidence=evidence };
await database.Insertable(new[] {
 HistoryEvent("h01", a.Task.TaskId, "TaskCancellationRequested", 0),
 HistoryEvent("h02", a.Task.TaskId, "TaskCancellationVehicleConfirmed", 1),
 HistoryEvent("h03", "__SITE__", "ContinueOriginal", 2, "session-a"),
 HistoryEvent("h04", "", "Release", 4, "session-a", new string('证',10000)),
 HistoryEvent("excluded-global", "", "Release", 5, "session-b"),
 HistoryEvent("excluded-other", b.Task.TaskId, "CloseCancelled", 5),
 HistoryEvent("excluded-poll", a.Task.TaskId, "RecoveryWaiting", 5)
}).ExecuteCommandAsync();
await database.Insertable(new[] {
 new TrafficResourceReleaseAuditEntity { AuditId="release-a", TaskId=a.Task.TaskId, ResourceId="NARROW", ReleasedAt=historyAt.AddSeconds(3), Operator="原操作员", RequestedBy="现场确认员", Reason="车辆已离开区域" },
 new TrafficResourceReleaseAuditEntity { AuditId="release-b", TaskId=b.Task.TaskId, ReleasedAt=historyAt.AddSeconds(3) }
}).ExecuteCommandAsync();
var evidenceBeforeHistoryReads = await database.Queryable<ExecutionEvidenceEntity>().CountAsync();
var history=(await reader.GetDetailAsync(a.Task.TaskId, token))!;
Require(history.OperationHistoryTotal==5 && history.OperationHistory.Count==5, "exclude other tasks and unrelated batches, polling without multiplying batch records");
Require(history.OperationHistory.Select(e=>e.Id).SequenceEqual(new[]{"E:h01","E:h02","E:h03","R:release-a","E:h04"}), "mixed history chronological ordering");
Require(history.OperationHistory[2].Scope.Contains("全场") && history.OperationHistory[3].Operator=="现场确认员" && history.OperationHistory[3].ResourceId=="NARROW", "scope and release attribution explicit");
var large=history.OperationHistory.Last();
Require(large.EvidenceTruncated && large.EvidenceCharacters==10000 && large.Evidence.Length==4096 && large.EvidenceDisplay.Contains("完整原文"), "large evidence preview explicitly bounded");
Require((await database.Queryable<ExecutionEvidenceEntity>().Where(e=>e.Id=="h04").FirstAsync()).Evidence.Length==10000, "full evidence retained");
await database.Insertable(Enumerable.Range(0,205).Select(n=>HistoryEvent($"window-{n:D3}",a.Task.TaskId,"CloseCancelled",10)).ToArray()).ExecuteCommandAsync();
history=(await reader.GetDetailAsync(a.Task.TaskId,token))!;
Require(history.OperationHistoryTotal==210 && history.OperationHistory.Count==200 && history.OperationHistory.First().Id=="E:window-005" && history.OperationHistory.Last().Id=="E:window-204", "latest 200 stable timestamp ties and full count");
Require(await database.Queryable<ExecutionEvidenceEntity>().CountAsync()==evidenceBeforeHistoryReads+205 && await database.Queryable<TrafficResourceLeaseEntity>().CountAsync()==1 && history.Task.Stage==DispatchTaskStage.Cancelled, "reads preserve audits leases and task state");
var cappedTimeline = (await service.GetDetailAsync(new TaskDetailRequest(a.Task.TaskId, false), token))!;
Require(cappedTimeline.ExecutionTimeline.Count == 100 && cappedTimeline.ExecutionTimelineTotal > 100, "execution timeline bounded with full count and history disabled");
Require(cappedTimeline.Steps.All(s => !s.IsCurrent && s.ProgressConditions.Count == 0), "cancelled task never shows live PLC gates");
// Real SQL pagination, including records outside the former 200-row window.
var firstHistoryPage = await reader.QueryHistoryAsync(new(a.Task.TaskId), token);
Require(firstHistoryPage.TotalCount == 210 && firstHistoryPage.Items.Count == 50 && firstHistoryPage.Items.First().Id == "E:window-204" && firstHistoryPage.Items.Last().Id == "E:window-155", "history page one queries newest records descending");
var oldHistoryPage = await reader.QueryHistoryAsync(new(a.Task.TaskId, Page:5), token);
Require(oldHistoryPage.Items.Count == 10 && oldHistoryPage.Items.Last().Id == "E:h01" && !oldHistoryPage.Items.Any(e => firstHistoryPage.Items.Any(f => f.Id == e.Id)), "older-than-200 records reachable without page overlap");
Require((await reader.QueryHistoryAsync(new(a.Task.TaskId, Page:100000),token)).Page == 5, "out of range history page clamps to last page");
Require((await service.QueryHistoryAsync(new(a.Task.TaskId, TaskHistoryCategory.Cancellation),token)).TotalCount == 2 &&
    (await reader.QueryHistoryAsync(new(a.Task.TaskId, TaskHistoryCategory.Recovery),token)).TotalCount == 207 &&
    (await reader.QueryHistoryAsync(new(a.Task.TaskId, TaskHistoryCategory.TrafficRelease),token)).Items.Single().ResourceId == "NARROW", "all history category filters execute in backend");
var batchHistory = await reader.QueryHistoryAsync(new(a.Task.TaskId, SessionId:"session-a"),token);
Require(batchHistory.TotalCount == 2 && batchHistory.Items.All(e => e.SessionId == "session-a") && batchHistory.SessionIds.SequenceEqual(new[]{"session-a"}), "batch filters include linked global events and exclude unassigned releases");
Require((await reader.QueryHistoryAsync(new(a.Task.TaskId, SessionId:"session-b"),token)).TotalCount == 0 &&
    (await reader.QueryHistoryAsync(new(a.Task.TaskId, SessionId:"' OR 1=1 --"),token)).TotalCount == 0, "foreign and injected batch filters cannot escape task scope");
var fromHistory = new DateTimeOffset(DateTime.SpecifyKind(historyAt.AddSeconds(1), DateTimeKind.Local));
var toHistory = fromHistory.AddSeconds(2);
var timePage = await reader.QueryHistoryAsync(new(a.Task.TaskId, From:fromHistory.ToOffset(TimeSpan.FromHours(-3)),To:toHistory),token);
Require(timePage.TotalCount == 3, "inclusive time boundaries honor offset-normalized instants");
Require((await reader.QueryHistoryAsync(new(a.Task.TaskId, TaskHistoryCategory.Recovery, fromHistory,toHistory,"session-a"),token)).Items.Single().Id == "E:h03", "category time and batch filters combine");
Require((await reader.QueryHistoryAsync(new(a.Task.TaskId,From:fromHistory.AddTicks(1),To:toHistory.AddTicks(-1)),token)).Items.Single().Id=="E:h03", "sub-millisecond boundaries exclude just-outside stored millisecond timestamps");
Require((await service.GetDetailAsync(new TaskDetailRequest(a.Task.TaskId,false),token))!.OperationHistory.Count==0, "separate history paging skips legacy detail payload");
Require((await reader.QueryHistoryAsync(new("missing"),token)).TotalCount == 0, "unknown task returns empty history");
foreach(var bad in new[] { new TaskHistoryQuery(a.Task.TaskId, Page:0), new TaskHistoryQuery(a.Task.TaskId,PageSize:101),
    new TaskHistoryQuery(a.Task.TaskId,(TaskHistoryCategory)99), new TaskHistoryQuery(a.Task.TaskId,From:toHistory,To:fromHistory),
    new TaskHistoryQuery(a.Task.TaskId,SessionId:new string('x',65)) })
{
    var rejected=false; try { await reader.QueryHistoryAsync(bad,token); } catch(ArgumentException) { rejected=true; }
    Require(rejected,"invalid history query rejected before SQL");
}
await database.Insertable(new[]{HistoryEvent("tie-z",a.Task.TaskId,"CloseCancelled",12), HistoryEvent("tie-a",a.Task.TaskId,"CloseCancelled",12)}).ExecuteCommandAsync();
await database.Insertable(new[]{
    new TrafficResourceReleaseAuditEntity { AuditId="tie-z",TaskId=a.Task.TaskId,ReleasedAt=historyAt.AddSeconds(12) },
    new TrafficResourceReleaseAuditEntity { AuditId="tie-a",TaskId=a.Task.TaskId,ReleasedAt=historyAt.AddSeconds(12) }
}).ExecuteCommandAsync();
var tieIds=new List<string>();
for(var page=1;page<=4;page++) tieIds.Add((await reader.QueryHistoryAsync(new(a.Task.TaskId,Page:page,PageSize:1),token)).Items.Single().Id);
Require(tieIds.SequenceEqual(new[]{"R:tie-z","R:tie-a","E:tie-z","E:tie-a"}),"merged SQL pages preserve deterministic source and ID ties");
// Export all filtered records, not the selected display page, and preserve raw evidence.
var unsafeReason = "=SUM(1,2)\n\"交接\"";
var unsafeEntry = HistoryEvent("export-special", a.Task.TaskId, "CloseCancelled", 13, evidence:"<script>原证据</script>\n\"原文\"");
unsafeEntry.Operator="<script>alert('x')</script>"; unsafeEntry.Reason=unsafeReason;
await database.Insertable(unsafeEntry).ExecuteCommandAsync();
var exports = new TaskHistoryExportService(new DatabaseOptions { DatabasePath=path });
var exportUser = TrafficUserContext.LocalOperation("导出测试员");
var package = await exports.ExportAsync(exportUser, new(new(a.Task.TaskId,Page:5,PageSize:1)), token);
Require(package.Accepted && package.RecordCount==215 && File.Exists(package.FilePath), "export contains all matching records beyond 200 regardless of selected page");
Require(package.SizeBytes==new FileInfo(package.FilePath).Length && package.Sha256==Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package.FilePath))).ToLowerInvariant(), "archive size and SHA256 match complete package");
using(var archive=ZipFile.OpenRead(package.FilePath))
{
    string ZipText(string name) { using var stream=archive.GetEntry(name)!.Open(); using var read=new StreamReader(stream); return read.ReadToEnd(); }
    Require(archive.Entries.Select(e=>e.FullName).Order().SequenceEqual(new[]{"README.txt","evidence.jsonl","history.csv","manifest.json","report.html"}.Order()), "package has report CSV original evidence manifest and instructions");
    var lines=ZipText("evidence.jsonl").Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>JsonDocument.Parse(line)).ToArray();
    Require(lines.Length==215 && lines.Select(doc=>doc.RootElement.GetProperty("id").GetString()).Distinct().Count()==215, "all export records unique across snapshot pages");
    var original=lines.Single(doc=>doc.RootElement.GetProperty("id").GetString()=="E:h04").RootElement;
    Require(original.GetProperty("evidence").GetString()==new string('证',10000) && !original.GetProperty("evidenceTruncated").GetBoolean(), "long evidence exports full original instead of clipped preview");
    var special=lines.Single(doc=>doc.RootElement.GetProperty("id").GetString()=="E:export-special").RootElement;
    Require(special.GetProperty("reason").GetString()==unsafeReason && special.GetProperty("evidence").GetString()==unsafeEntry.Evidence, "JSONL round trips quotes newline and raw values");
    var report=ZipText("report.html"); var csv=ZipText("history.csv");
    Require(!report.Contains("<script>") && report.Contains("&lt;script&gt;") && csv.Contains("\"'=SUM(1,2)\n\"\"交接\"\"\""), "report escapes markup and quoted CSV prevents formula interpretation");
    using(var entryStream=archive.GetEntry("history.csv")!.Open()) { var bom=new byte[3]; entryStream.ReadExactly(bom); Require(bom.SequenceEqual(new byte[]{239,187,191}), "CSV has UTF8 BOM for Chinese Excel text"); }
    using var manifest=JsonDocument.Parse(ZipText("manifest.json"));
    Require(manifest.RootElement.GetProperty("task").GetProperty("taskId").GetString()==a.Task.TaskId && manifest.RootElement.GetProperty("recordCount").GetInt32()==215 && manifest.RootElement.GetProperty("steps").GetArrayLength()==9, "manifest contains task snapshot steps applied filter and full count");
    foreach(var item in manifest.RootElement.GetProperty("files").EnumerateObject())
    {
        using var content=archive.GetEntry(item.Name)!.Open();
        Require(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()==item.Value.GetString(), "manifest file checksum matches "+item.Name);
    }
    foreach(var line in lines) line.Dispose();
}
var deniedExport=false;
try { await exports.ExportAsync(new("guest","无权限测试员",TrafficPermission.None),new(new(a.Task.TaskId)),token); } catch(UnauthorizedAccessException) { deniedExport=true; }
Require(deniedExport,"export respects view permission without generating a file");
var batchPackage=await exports.ExportAsync(exportUser,new(new(a.Task.TaskId,TaskHistoryCategory.Recovery,SessionId:"session-a")),token);
Require(batchPackage.Accepted && batchPackage.RecordCount==2,"export reuses combined category and batch ownership filters");
var emptyPackage=await exports.ExportAsync(exportUser,new(new(a.Task.TaskId,SessionId:"foreign-session")),token);
Require(emptyPackage.Accepted && emptyPackage.RecordCount==0,"empty filter exports explicit zero-record report");
Require(!(await exports.ExportAsync(exportUser,new(new("missing")),token)).Accepted,"missing task never exports a fabricated report");
using(var cancelledExport=new CancellationTokenSource())
{
    cancelledExport.Cancel(); var rejected=false;
    try { await exports.ExportAsync(exportUser,new(new(a.Task.TaskId)),cancelledExport.Token); } catch(OperationCanceledException) { rejected=true; }
    Require(rejected,"cancelled export stops before publication");
}
Require(!Directory.EnumerateDirectories(Path.GetDirectoryName(package.FilePath)!,"*.staging").Any() && !Directory.EnumerateFiles(Path.GetDirectoryName(package.FilePath)!,"*.tmp").Any(), "completed and cancelled exports leave no staging output");
Require(await database.Queryable<TrafficResourceLeaseEntity>().CountAsync()==1 && (await reader.GetDetailAsync(a.Task.TaskId,token))!.Task.Stage==DispatchTaskStage.Cancelled, "export never releases resources or changes task status");
Console.WriteLine("PASS: task-history full filtered export, original evidence, escaped report/CSV and checksums");
Console.WriteLine($"Isolated export package: {package.FilePath}");
// Job progress/cancellation and scoped cache retention use a private fixture directory.
var jobRoot=Path.Combine(Path.GetTempPath(),"agv-export-jobs-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(jobRoot);
var jobDbPath=Path.Combine(jobRoot,"jobs.db"); File.Copy(path,jobDbPath);
using var jobDb=DatabaseFactory.Create(new DatabaseOptions { DatabasePath=jobDbPath });
await jobDb.Insertable(Enumerable.Range(0,600).Select(n=>HistoryEvent($"job-{n:D4}",a.Task.TaskId,"CloseCancelled",20,evidence:new string('证',4000))).ToArray()).ExecuteCommandAsync();
var jobs=new TaskHistoryExportService(new DatabaseOptions { DatabasePath=jobDbPath });
var stoppedId=Guid.NewGuid().ToString("N");
Require((await jobs.CancelAsync(exportUser,new(stoppedId),token)).Accepted && !(await jobs.ExportAsync(exportUser,new(new(a.Task.TaskId),stoppedId),token)).Accepted,"cancel arriving before export registration blocks later generation");
var cancelId=Guid.NewGuid().ToString("N");
var background=Task.Run(()=>jobs.ExportAsync(exportUser,new(new(a.Task.TaskId),cancelId),token));
TaskHistoryExportProgress running;
var deadline=DateTime.UtcNow.AddSeconds(20);
do { running=await jobs.GetProgressAsync(exportUser,new(cancelId),token); if(running.Stage=="Writing" || running.Stage=="Packaging") break; await Task.Delay(1); } while(!background.IsCompleted && DateTime.UtcNow<deadline);
Require(!running.IsTerminal && running.TotalRecords==815 && running.Stage is "Writing" or "Packaging", "background exposes exact record progress during real export");
Require((await jobs.CancelAsync(exportUser,new(cancelId),token)).Accepted,"independent cancellation accepted while export connection runs");
Require(!(await background).Accepted && (await jobs.GetProgressAsync(exportUser,new(cancelId),token)).Stage=="Cancelled", "backend cancellation completes cleanup and terminal confirmation");
var cacheRoot=Path.Combine(jobRoot,"exports","task-history");
Require(!Directory.EnumerateFileSystemEntries(cacheRoot).Any(p=>Path.GetFileName(p).Contains(cancelId)), "cancelled job publishes no archive and removes staging and temporary ZIP");
var readyId=Guid.NewGuid().ToString("N");
var ready=await jobs.ExportAsync(exportUser,new(new(a.Task.TaskId),readyId),token);
var readyProgress=await jobs.GetProgressAsync(exportUser,new(readyId),token);
Require(ready.Accepted && readyProgress.IsTerminal && readyProgress.Percent==100 && readyProgress.CompletedRecords==815 && readyProgress.Stage=="Ready", "ready progress completes after archive publication");
Require((await jobs.ExportAsync(exportUser,new(new(a.Task.TaskId),readyId),token)).FilePath==ready.FilePath,"same job identifier replays completed package without duplicate generation");
Require(!(await jobs.CancelAsync(exportUser,new(readyId),token)).Accepted,"finished backend cannot be falsely cancelled");
var conflict=false; try { await jobs.ExportAsync(exportUser,new(new(a.Task.TaskId,TaskHistoryCategory.Cancellation),readyId),token); } catch(ArgumentException) { conflict=true; }
Require(conflict,"job identifiers cannot be replayed with different filters");
var foreign=new TrafficUserContext("other-operator","其他操作员",TrafficPermission.View);
var ownerRejected=false; try { await jobs.CancelAsync(foreign,new(readyId),token); } catch(UnauthorizedAccessException) { ownerRejected=true; }
Require(ownerRejected,"other operation identity cannot cancel original job");
var savedCopy=Path.Combine(jobRoot,"saved-handover.zip"); File.Copy(ready.FilePath,savedCopy);
void AgeFile(string file) { File.SetCreationTimeUtc(file,DateTime.UtcNow.AddDays(-8)); File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddDays(-8)); }
AgeFile(ready.FilePath);
var freshZip=Path.Combine(cacheRoot,"任务处置历史-"+Guid.NewGuid().ToString("N")+".zip"); File.Copy(savedCopy,freshZip); File.SetLastWriteTimeUtc(freshZip,DateTime.UtcNow);
var oldTmp=Path.Combine(cacheRoot,"任务处置历史-"+Guid.NewGuid().ToString("N")+".zip.tmp"); File.WriteAllText(oldTmp,"interrupted zip"); AgeFile(oldTmp);
var oldStage=Path.Combine(cacheRoot,Guid.NewGuid().ToString("N")+".staging"); Directory.CreateDirectory(oldStage); var stageNote=Path.Combine(oldStage,"README.txt"); File.WriteAllText(stageNote,"interrupted staging"); AgeFile(stageNote); Directory.SetCreationTimeUtc(oldStage,DateTime.UtcNow.AddDays(-8)); Directory.SetLastWriteTimeUtc(oldStage,DateTime.UtcNow.AddDays(-8));
var unknownFile=Path.Combine(cacheRoot,"keep-user-file.zip"); File.WriteAllText(unknownFile,"unrelated"); AgeFile(unknownFile);
var unknownStage=Path.Combine(cacheRoot,Guid.NewGuid().ToString("N")+".staging"); Directory.CreateDirectory(unknownStage); File.WriteAllText(Path.Combine(unknownStage,"user-note.txt"),"preserve unknown content");
var exportCache=await jobs.GetCacheAsync(exportUser,new(7),token);
Require(exportCache.TotalCount==4 && exportCache.EligibleCount==3 && exportCache.Items.Single(i=>i.FileName==Path.GetFileName(freshZip)).CanClean==false, "cache lists exact owned packages temporary ZIP and staging, with age policy");
var deniedClean=false; try { await jobs.CleanupAsync(foreign,new(7),token); } catch(UnauthorizedAccessException) { deniedClean=true; }
Require(deniedClean,"view-only identity cannot clean shared cache");
var cleanup=await jobs.CleanupAsync(exportUser,new(7),token);
Require(cleanup.DeletedCount==3 && cleanup.FailedCount==0 && cleanup.FreedBytes>0 && !File.Exists(ready.FilePath) && !File.Exists(oldTmp) && !Directory.Exists(oldStage), "cleanup deletes only eligible completed and interrupted cache artifacts");
Require(File.Exists(savedCopy) && File.Exists(freshZip) && File.Exists(unknownFile) && Directory.Exists(unknownStage), "cleanup preserves saved copy fresh cache and unknown files/directories");
Require(!(await jobs.ExportAsync(exportUser,new(new(a.Task.TaskId),readyId),token)).Accepted,"cleaned package replay cannot return false success");
var invalidRetention=false; try { await jobs.CleanupAsync(exportUser,new(0),token); } catch(ArgumentException) { invalidRetention=true; }
Require(invalidRetention,"cleanup cannot request immediate purge retention");
if(OperatingSystem.IsWindows())
{
    var outside=Path.Combine(jobRoot,"outside-cache"); Directory.CreateDirectory(outside);
    var outsideArchive=Path.Combine(outside,"任务处置历史-"+Guid.NewGuid().ToString("N")+".zip"); File.Copy(savedCopy,outsideArchive); AgeFile(outsideArchive);
    var junction=Path.Combine(cacheRoot,Guid.NewGuid().ToString("N")+".staging");
    var info=new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
        Arguments="/c mklink /J \""+junction+"\" \""+outside+"\"" };
    using var helper=System.Diagnostics.Process.Start(info)!; await helper.WaitForExitAsync();
    Require(helper.ExitCode==0 && (File.GetAttributes(junction)&FileAttributes.ReparsePoint)!=0,"junction fixture created for cache link isolation");
    var linkedCache=await jobs.GetCacheAsync(exportUser,new(7),token);
    Require(!linkedCache.Items.Any(i=>i.FileName==Path.GetFileName(junction)) && (await jobs.CleanupAsync(exportUser,new(7),token)).DeletedCount==0 && File.Exists(outsideArchive),"cache cleanup never traverses directory links to outside files");
}
// A separate task verifies v2 diagnostics without changing the older fixtures or fabricating transport completion.
clock.Now=DateTimeOffset.Now;
var v2Task=(await writer.TryCreateAsync("CNC28","DUMPER-001",0,token)).Task.TaskId;
var v2Steps=await writer.GetStepsAsync(v2Task,token);
var returning=v2Steps.Single(s=>s.StepType==DispatchTaskStepType.ReturnEmptyBox);
await database.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=50,CompletedAt=DateTime.Now})
    .Where(s=>s.TaskId==v2Task&&s.Sequence<returning.Sequence).ExecuteCommandAsync();
await database.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=40,VehicleId="AGV-28",VendorTaskId="RETURN-28",StartedAt=DateTime.Now})
    .Where(s=>s.StepId==returning.StepId).ExecuteCommandAsync();
await database.Updateable<DispatchTaskEntity>().SetColumns(t=>new DispatchTaskEntity{Stage=(int)DispatchTaskStage.ReturningEmptyBox,VehicleId="AGV-28"}).Where(t=>t.TaskId==v2Task).ExecuteCommandAsync();
var exitCheckpoint=new TaskHandshakeEntity{TaskId=v2Task,StepId=returning.StepId,CycleId=v2Task,ContractVersion=HandshakeExecutionOptions.Version,Phase="AwaitPhysicalExit",Revision=1,StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow};
await database.Insertable(exitCheckpoint).ExecuteCommandAsync();
cache.Set("DUMPER-001","Faulted",false,SignalQuality.Good);cache.Set("DUMPER-001","AgvAtDumpingPosition",true,SignalQuality.Good);cache.Set("DUMPER-001","DumpingCompletedMayLeave",true,SignalQuality.Good);
var v2Query=new TaskCenterQueryService(reader,cache,creation,parameters,clock,new(){Enabled=true,PlcContractVersion=HandshakeExecutionOptions.Version});
var v2Detail=(await v2Query.GetDetailAsync(v2Task,token))!;
Require(v2Detail.Steps[6].IsCurrent&&v2Detail.Steps[6].ObservedPhase.Contains("AwaitPhysicalExit")&&v2Detail.PlcConditions.Any(c=>c.VariableName=="AgvAtDumpingPosition"),"executing return exposes its actual exit checkpoint and PLC guards");
Require(v2Detail.Steps[8].CompletionRequirement.Contains("最后清返箱责任")&&v2Detail.Steps[1].CompletionRequirement.Contains("保持到返箱"),"future and completed v2 steps retain the same 28-stage contract description");
Require(!v2Detail.Handover!.ExitPhysicallyVerified&&!v2Detail.Handover.ReturnCommandCompleted,"executing return never fabricates completed return or physical exit");
await database.Insertable(new TaskRuntimeEventEntity{TaskId=v2Task,StepId=returning.StepId,Kind="ExitVerified",Phase="ClearDumpingAfterExit",RecordedAtUtc=DateTime.UtcNow}).ExecuteCommandAsync();
await database.Updateable<TaskHandshakeEntity>().SetColumns(h=>new TaskHandshakeEntity{Phase="ClearDumpingAfterExit",Revision=h.Revision+1}).Where(h=>h.StepId==returning.StepId).ExecuteCommandAsync();
v2Detail=(await v2Query.GetDetailAsync(v2Task,token))!;
Require(v2Detail.Handover!.ExitPhysicallyVerified&&v2Detail.Handover.State=="PhysicalExitVerified","physical exit evidence alone does not claim PLC and equipment handover complete");
// Reproduce the field stall: return is complete, step 8 is ready, but its prerequisite handover is not.
var placing=v2Steps.Single(s=>s.StepType==DispatchTaskStepType.PlaceEmptyBox);
await database.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=50,CompletedAt=DateTime.Now}).Where(s=>s.StepId==returning.StepId).ExecuteCommandAsync();
await database.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=10,StartedAt=DateTime.Now}).Where(s=>s.StepId==placing.StepId).ExecuteCommandAsync();
await database.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)DispatchTaskStage.WaitingForPlcConfirmation).Where(t=>t.TaskId==v2Task).ExecuteCommandAsync();
const string exitHoldReason="PLC已闭合，等待新鲜的车辆出区及原任务证据。";
await database.Updateable<TaskHandshakeEntity>().SetColumns(h=>new TaskHandshakeEntity{Phase="ReleaseDumper",Revision=h.Revision+1,Reason=exitHoldReason}).Where(h=>h.StepId==returning.StepId).ExecuteCommandAsync();
await database.Insertable(new ExecutionEvidenceEntity{Id="handover-blocked",TaskId=v2Task,StepId=returning.StepId,Kind="DumperExitBlocked",OccurredAt=DateTime.Now,
    Evidence=JsonSerializer.Serialize(new{Reason="车辆定位首帧已过期。",Context="PRIVATE-DEVICE-CONTEXT"})}).ExecuteCommandAsync();
var handshakeRowsBefore=await database.Queryable<TaskHandshakeEntity>().Where(h=>h.TaskId==v2Task).CountAsync();
v2Detail=(await v2Query.GetDetailAsync(v2Task,token))!;
Require(v2Detail.WaitingExplanation.Contains("ReleaseDumper")&&v2Detail.WaitingExplanation.Contains(exitHoldReason)&&v2Detail.WaitingExplanation.Contains("尚未进入放箱通知"),
    "ready placement identifies the original handover prerequisite and reason instead of blaming unwritten placed signal");
Require(v2Detail.Steps[7].ObservedPhase.Contains("等待原返程倒料区交接 ReleaseDumper")&&v2Detail.PlcConditions.All(c=>c.StationId=="DUMPER-001")&&
    v2Detail.PlcConditions.All(c=>c.VariableName!="EmptyBoxPlaced"&&c.VariableName!="BoxPresent"),
    "placement phase and conditions show the blocking handover rather than unrelated step-8 PLC targets");
Require(v2Detail.Handover!.Reason.Contains("ReleaseDumper")&&v2Detail.Handover.Reason.Contains(exitHoldReason),"handover diagnosis retains its exact persisted phase and reason");
var heldEvent=v2Detail.ExecutionTimeline.Single(e=>e.Id=="handover-blocked");
Require(heldEvent.Title=="倒料区交接核验未通过"&&heldEvent.Summary.Contains("车辆定位首帧已过期")&&!heldEvent.Summary.Contains("PRIVATE-DEVICE-CONTEXT")&&heldEvent.Summary.Contains("记录时"),
    "handover timeline exposes the recorded safe reason without leaking context or presenting history as current evidence");
Require(await database.Queryable<TaskHandshakeEntity>().Where(h=>h.TaskId==v2Task).CountAsync()==handshakeRowsBefore&&
    !v2Detail.Handshakes.Any(h=>h.StepId==placing.StepId),"diagnostic reads do not create a placement checkpoint or advance handover");
await database.Updateable<TaskHandshakeEntity>().SetColumns(h=>new TaskHandshakeEntity{Phase="WritePending",Revision=h.Revision+1,PendingSignal="DUMPER-001/AgvAtDumpingPosition",PendingValue=false,NextPhase="AwaitDumperClosed"})
    .Where(h=>h.StepId==returning.StepId).ExecuteCommandAsync();
v2Detail=(await v2Query.GetDetailAsync(v2Task,token))!;
Require(v2Detail.WaitingExplanation.Contains("WritePending")&&v2Detail.WaitingExplanation.Contains("不自动重发")&&
    v2Detail.PlcConditions.Single().VariableName=="AgvAtDumpingPosition","unknown exit write remains an explicit original handover review prerequisite");
await database.Updateable<TaskHandshakeEntity>().SetColumns(h=>new TaskHandshakeEntity{Phase="ExitHandoverComplete",Revision=h.Revision+1,PendingSignal=null,PendingValue=null,NextPhase=null})
    .Where(h=>h.StepId==returning.StepId).ExecuteCommandAsync();
v2Detail=(await v2Query.GetDetailAsync(v2Task,token))!;
Require(!v2Detail.WaitingExplanation.Contains("等待倒料区交接完成")&&v2Detail.PlcConditions.Any(c=>c.VariableName=="EmptyBoxPlaced"),
    "completed handover no longer overrides actual placement diagnostics");
// Restore the independent execution-time handover fixture for the assertions below.
await database.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=40,CompletedAt=null}).Where(s=>s.StepId==returning.StepId).ExecuteCommandAsync();
await database.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=0,StartedAt=null}).Where(s=>s.StepId==placing.StepId).ExecuteCommandAsync();
await database.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)DispatchTaskStage.ReturningEmptyBox).Where(t=>t.TaskId==v2Task).ExecuteCommandAsync();
await database.Updateable<TaskHandshakeEntity>().SetColumns(h=>new TaskHandshakeEntity{Phase="ExitHandoverComplete",Revision=h.Revision+1}).Where(h=>h.StepId==returning.StepId).ExecuteCommandAsync();
v2Detail=(await v2Query.GetDetailAsync(v2Task,token))!;
Require(v2Detail.Handover!.State=="ExitHandoverComplete"&&!v2Detail.Handover.ReturnCommandCompleted,"handover can be recorded while the original return is still running");
await database.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=50,CompletedAt=DateTime.Now}).Where(s=>s.TaskId==v2Task&&s.Sequence<9).ExecuteCommandAsync();
var closing=v2Steps.Single(s=>s.StepType==DispatchTaskStepType.CompletePlcHandshake);
await database.Updateable<DispatchTaskStepEntity>().SetColumns(s=>new DispatchTaskStepEntity{Status=10,StartedAt=DateTime.Now}).Where(s=>s.StepId==closing.StepId).ExecuteCommandAsync();
await database.Updateable<DispatchTaskEntity>().SetColumns(t=>t.Stage==(int)DispatchTaskStage.WaitingForPlcConfirmation).Where(t=>t.TaskId==v2Task).ExecuteCommandAsync();
await database.Insertable(new TaskHandshakeEntity{TaskId=v2Task,StepId=closing.StepId,CycleId=v2Task,ContractVersion=HandshakeExecutionOptions.Version,Phase="VerifyClosure",Revision=1,StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow}).ExecuteCommandAsync();
foreach(var name in new[]{"FullBoxRequested","FullBoxRequestAcknowledged","FullBoxRemoved","EmptyBoxTaskAcknowledged","EmptyBoxPlaced","Faulted"})cache.Set("CNC28",name,false,SignalQuality.Good);
cache.Set("CNC28","BoxPresent",true,SignalQuality.Good);cache.Set("CNC28","CommunicationOnline",true,SignalQuality.Good);
cache.Set("DUMPER-001","AgvAtWaitingPosition",true,SignalQuality.Good);cache.Set("DUMPER-001","DumperReady",true,SignalQuality.Good);
v2Detail=(await v2Query.GetDetailAsync(v2Task,token))!;
Require(v2Detail.PlcConditions.Count>0&&v2Detail.PlcConditions.All(c=>c.StationId=="CNC28"&&c.Satisfied),"front vehicle final closure does not read or require the following vehicle shared bits low");
var pickupConditions=PersistedHandshakeConditions.Observe(new(v2Task,"pickup",v2Task,HandshakeExecutionOptions.Version,"ReadyToComplete",1,"",null,null,null,clock.Now,clock.Now,[]),DispatchTaskStepType.PickupFullBox,"CNC28","DUMPER-001",cache,clock.Now,5);
Require(pickupConditions.Single(c=>c.VariableName=="FullBoxRemoved").ExpectedValue,"v2 pickup completion explicitly expects the held removed notification high");
cache.Set("CNC28","FullBoxRequested",true,SignalQuality.Good);clock.Now=DateTimeOffset.Now;
v2Detail=(await v2Query.GetDetailAsync(v2Task,token))!;
Require(v2Detail.PlcConditions.All(c=>c.VariableName!="FullBoxRequested")&&v2Detail.PlcConditions.All(c=>c.Satisfied),"next call after responsibility clear remains pending without blocking prior closure in Web diagnostics");
foreach(var phase in new[]{"AwaitReturnEvidence","ClearRemoved","ClearPlaced","ClearEmptyResponsibility","WritePending"})
{
    var condition=PersistedHandshakeConditions.Observe(new(v2Task,"closing",v2Task,HandshakeExecutionOptions.Version,phase,1,"",phase=="WritePending"?"CNC28/FullBoxRemoved":null,phase=="WritePending"?false:null,phase=="WritePending"?"ClearPlaced":null,clock.Now,clock.Now,[]),DispatchTaskStepType.CompletePlcHandshake,"CNC28","DUMPER-001",cache,clock.Now,5);
    Require(condition.Single(c=>c.VariableName=="FullBoxRequested") is {ExpectedValue:false,Satisfied:false}&&condition.Single(c=>c.VariableName=="BoxPresent").ExpectedValue,"every v2 final phase retains origin box and withdrawn-call guard: "+phase);
}
foreach(var phase in new[]{"VerifyClosure","ReadyToComplete"})
{
    var condition=PersistedHandshakeConditions.Observe(new(v2Task,"closing",v2Task,HandshakeExecutionOptions.Version,phase,1,"",null,null,null,clock.Now,clock.Now,[]),DispatchTaskStepType.CompletePlcHandshake,"CNC28","DUMPER-001",cache,clock.Now,5);
    Require(condition.All(c=>c.VariableName!="FullBoxRequested")&&condition.Single(c=>c.VariableName=="BoxPresent").ExpectedValue,"after last responsibility clear a new call does not become an old-cycle condition: "+phase);
}
foreach(var phase in new[]{"AwaitPickupReadback","ClearCallResponse","ReadyToComplete"})
{
    var condition=PersistedHandshakeConditions.Observe(new(v2Task,"pickup",v2Task,HandshakeExecutionOptions.Version,phase,1,"",null,null,null,clock.Now,clock.Now,[]),DispatchTaskStepType.PickupFullBox,"CNC28","DUMPER-001",cache,clock.Now,5);
    Require(condition.Single(c=>c.VariableName=="FullBoxRequested") is {ExpectedValue:false,Satisfied:false},"pickup phase exposes residual-call block: "+phase);
}
var bindingConditions=PersistedHandshakeConditions.Observe(new(v2Task,"first",v2Task,HandshakeExecutionOptions.Version,"TransportBound",1,"",null,null,null,clock.Now,clock.Now,[]),DispatchTaskStepType.GoToPickup,"CNC28","DUMPER-001",cache,clock.Now,5);
Require(bindingConditions.Any(c=>c.VariableName=="FullBoxRequested"&&!c.ExpectedValue&&!c.Satisfied)&&bindingConditions.Any(c=>c.VariableName=="FullBoxRequestAcknowledged"&&c.ExpectedValue),"initial binding explains withdrawn call and acknowledged request before pickup");
var readyBaselineConditions=PersistedHandshakeConditions.Observe(new(v2Task,"waiting",v2Task,HandshakeExecutionOptions.Version,"AwaitReadyBaseline",1,"",null,null,null,clock.Now,clock.Now,[]),DispatchTaskStepType.WaitForDumperReady,"CNC28","DUMPER-001",cache,clock.Now,5);
Require(readyBaselineConditions.Single(c=>c.VariableName=="AgvAtDumpingPosition") is {ExpectedValue:false,Satisfied:false},"waiting baseline exposes residual dumping cycle instead of only Ready/Done");
cache.Set("CNC28","FullBoxRequested",false,SignalQuality.Good);
var oldTask=(await writer.TryCreateAsync("CNCOLD","DUMPER-001",0,token)).Task.TaskId;
var oldStep=(await writer.GetStepsAsync(oldTask,token)).First();
await database.Insertable(new TaskHandshakeEntity{TaskId=oldTask,StepId=oldStep.StepId,CycleId=oldTask,ContractVersion=HandshakeExecutionOptions.LegacyVersion,Phase="TransportBound",Revision=1,StartedAtUtc=DateTime.UtcNow,UpdatedAtUtc=DateTime.UtcNow}).ExecuteCommandAsync();
var oldDetail=(await v2Query.GetDetailAsync(oldTask,token))!;
Require(oldDetail.PlcConditions.Count==0&&oldDetail.WaitingExplanation.Contains("不一致")&&oldDetail.Steps[0].EvidenceBoundary.Contains("旧契约"),"legacy checkpoint is not reinterpreted as 28-stage conditions or silently migrated");
Console.WriteLine("PASS: 28-stage execution-time exit, handover, next-vehicle isolation and legacy diagnostic checks");
Console.WriteLine("PASS: live progress, active cancellation, identity/replay and scoped cache retention checks");
Console.WriteLine("PASS: task queries, PLC quality, commands, leases, current step, waiting reasons and elapsed time");
Console.WriteLine($"Isolated test database: {path}");

static void Require(bool passed, string message)
{
    if (!passed) { Console.Error.WriteLine("FAIL: " + message); Environment.Exit(1); }
}

sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Now;
    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
}
sealed class TestSignalCache : IPlcSignalCache
{
    private readonly Dictionary<string, PlcSignalSnapshot> _signals = [];
    public event Action<PlcSignalChanged>? SignalChanged { add { } remove { } }
    public long Version => 0;
    public IReadOnlyList<PlcSignalSnapshot> GetAll() => _signals.Values.ToArray();
    public IReadOnlyList<PlcSignalSnapshot> GetStation(string id) => _signals.Values.Where(s => s.StationId == id).ToArray();
    public bool TryGet(string id, string variable, out PlcSignalSnapshot snapshot) => _signals.TryGetValue(id + ":" + variable, out snapshot!);
    public void Set(string id, string variable, bool value, SignalQuality quality) => _signals[id + ":" + variable] =
        new(id, variable, variable, "DB1.DBX0.0", "BOOL", "PLC->Host", "Read", true, value, quality, DateTimeOffset.Now, null, 0);
}
