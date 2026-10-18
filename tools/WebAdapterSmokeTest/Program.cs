using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Application.Maps;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.WebHost;

var checks = new List<string>();
void Check(bool pass, string name) { if (!pass) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
var workspace = Path.GetFullPath(args.FirstOrDefault() ?? ".");
var candidateIndex=Array.IndexOf(args,"--candidate-dir");
var candidateRoot=candidateIndex<0?null:Path.GetFullPath(args.ElementAtOrDefault(candidateIndex+1)??throw new ArgumentException("缺少候选发布目录。"));
var root = Path.Combine(Path.GetTempPath(), "agv-web-adapter-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine("Evidence: " + root);
var databasePath = Path.Combine(root, "private.db");
var pipeName = "AgvWebTest-" + Guid.NewGuid().ToString("N");
var nodes = new MapNode[] { new("A", 0, 0, MapNodeType.Station, true, [], "LM1"), new("B", 10, 0, MapNodeType.WaitingPoint, true, [], "LM2") };
var edge = new MapEdge("CURVE", "A", "B", 12, 1, true, true, []) { GeometryKind = MapEdgeGeometryKind.CubicBezier, ControlPoints = [new(2, 4), new(8, 4)] };
var definition = new MapDefinition("隔离测试地图", "synthetic", "TestCartesian", "m", nodes, [edge], []);
var projected = SnapshotReader.ProjectMap(new(DateTimeOffset.UtcNow, "MAP-WEB-TEST", "V1", definition, []))!;
var projectionJson = JsonSerializer.SerializeToElement(projected, WebJson.Options);
Check(projectionJson.GetProperty("edges")[0].GetProperty("points").GetArrayLength() == 33, "曲线使用Domain统一采样33点，不退化为直线");
Check(projectionJson.GetProperty("edges")[0].GetProperty("points")[16].GetProperty("y").GetDouble() > 0, "Bezier中间几何保持弯曲");
Check(!projectionJson.ToString().Contains("synthetic"), "Web地图不暴露后台源文件路径");
Check(SnapshotReader.ProjectMap(new(DateTimeOffset.UtcNow, null, null, null, [])) == null, "没有运行地图就返回空，不生成假图");
Check(SnapshotReader.ProjectMap(new(DateTimeOffset.UtcNow, "M", "V", definition with { CoordinateSystem = "SmapWorldTranslated" }, []))!.Warnings.Length > 0, "平移Smap明确说明车位变换限制");

using (var db = DatabaseFactory.Create(new() { DatabasePath = databasePath }))
{
    await new DatabaseInitializer(db, new TrafficResourceCatalog([])).InitializeAsync(default);
    db.Insertable(new MapProjectEntity { MapId = "MAP-WEB-TEST", Name = definition.Name, CoordinateSystem = definition.CoordinateSystem,
        Unit = "m", CoordinateScale = 1, IsActive = true, ActiveVersionId = "V1", PublishedVersionId = "V1", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommand();
    db.Insertable(new MapVersionEntity { MapId = "MAP-WEB-TEST", VersionId = "V1", VersionNumber = 1, Status = (int)MapVersionStatus.Published,
        DefinitionJson = JsonSerializer.Serialize(definition, WebJson.Options), ValidationJson = JsonSerializer.Serialize(new MapValidationService().Validate(definition),WebJson.Options), CreatedBy = "isolated-test", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommand();
    db.Insertable(new MapVersionEntity { MapId="MAP-WEB-TEST",VersionId="V2-DRAFT",VersionNumber=2,Status=(int)MapVersionStatus.Draft,DefinitionJson=JsonSerializer.Serialize(definition,WebJson.Options),ValidationJson=JsonSerializer.Serialize(new MapValidationService().Validate(definition),WebJson.Options),EditToken=Guid.NewGuid().ToString("N"),CreatedBy="isolated-test",CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now }).ExecuteCommand();
    db.Insertable(new DispatchTaskEntity { TaskId = "WEB-ISOLATED-TASK", StationId = "TEST-STATION", Stage = (int)DispatchTaskStage.Completed,
        Priority = 50, CreatedAt = DateTime.Now.AddMinutes(-12), UpdatedAt = DateTime.Now }).ExecuteCommand();
    var sequence = 0;
    foreach (var type in Enum.GetValues<DispatchTaskStepType>())
    {
        sequence++;
        db.Insertable(new DispatchTaskStepEntity { StepId = "WEB-STEP-" + sequence, TaskId = "WEB-ISOLATED-TASK", Sequence = sequence,
            StepType = (int)type, Status = (int)DispatchTaskStepStatus.Completed, StartedAt = DateTime.Now.AddMinutes(-10).AddSeconds(sequence),
            CompletedAt = DateTime.Now.AddMinutes(-5).AddSeconds(sequence), CreatedAt = DateTime.Now.AddMinutes(-12), UpdatedAt = DateTime.Now.AddMinutes(-5) }).ExecuteCommand();
    }
    await new TaskHandshakeStore(db).RecordSignalsAsync("WEB-ISOLATED-TASK","WEB-STEP-2",[new("TEST-STATION","BoxPresent",false,"Good",DateTimeOffset.UtcNow,1,"isolated-test")],default);
    db.Insertable(new ExecutionEvidenceEntity { Id="WEB-EVIDENCE-1", TaskId="WEB-ISOLATED-TASK", StepId="WEB-STEP-2", Kind="StepConditionsObserved",
        Evidence="{\"Conditions\":[{\"StationId\":\"TEST-STATION\",\"VariableName\":\"BoxPresent\",\"CurrentValue\":false,\"Result\":\"Pass\"}]}", OccurredAt=DateTime.Now.AddMinutes(-7) }).ExecuteCommand();
}
var source = candidateRoot==null?Path.Combine(workspace, "AgvDispatch.Service/bin/Debug/net10.0-windows10.0.19041.0/win-x64"):Path.Combine(candidateRoot,"Service");
var deploy = Path.Combine(root, "service");
Directory.CreateDirectory(deploy);
foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
{
    var relative = Path.GetRelativePath(source, file);
    if (relative.StartsWith("config" + Path.DirectorySeparatorChar) || relative.StartsWith("data" + Path.DirectorySeparatorChar) || relative.StartsWith("logs" + Path.DirectorySeparatorChar) || Path.GetFileName(file).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)) continue;
    var target = Path.Combine(deploy, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
}
Directory.CreateDirectory(Path.Combine(deploy,"config"));
File.WriteAllText(Path.Combine(deploy,"config/plc-signals.csv"),"address,variableName,chineseName,stationId,deviceType,instanceIndex,dataType,direction,access,addressStatus,enabled,setConditionZh,resetConditionZh,sourceNote\r\n");
File.WriteAllText(Path.Combine(deploy, "appsettings.json"), JsonSerializer.Serialize(new {
    Database = new { Path = databasePath }, Security = new { PipeName = pipeName },
    Plc = new { Enabled = false, AllowWrites = false, SignalCsvPath = "config/plc-signals.csv" },
    Agv = new { Enabled = false, AllowCommands = false, Robots = Array.Empty<object>() }, Map = new { Enabled = false },
    TaskCreation = new { Enabled = false }, Dispatching = new { Enabled = false, StationPickupSites = new Dictionary<string,string>{{"TEST-STATION","A"}}, DumperWaitingSite="B", DumperDumpingSite="B" }, AgvCommandOutbox = new { Enabled = false }, DispatchTaskSteps = new { Enabled = false },
    ExecutionIdentityAcquisition = new { Enabled = false }
}));
using var service = Launch(Path.Combine(deploy, "AgvDispatch.Service.exe"), deploy, [
    "--Database:Path=" + databasePath, "--Security:PipeName=" + pipeName, "--Plc:Enabled=false", "--Plc:AllowWrites=false", "--Agv:Enabled=false", "--Agv:AllowCommands=false",
    "--TaskCreation:Enabled=false", "--Dispatching:Enabled=false", "--AgvCommandOutbox:Enabled=false", "--DispatchTaskSteps:Enabled=false", "--ExecutionIdentityAcquisition:Enabled=false", "--Map:Enabled=false"]);
var serviceOut = service.StandardOutput.ReadToEndAsync(); var serviceErr = service.StandardError.ReadToEndAsync();
using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var bridgeArguments=new[]{"--Web:Port=" + port, "--Web:PipeName=" + pipeName,"--Web:EnableTaskSubmission=false","--Web:ProjectStorePath="+Path.Combine(root,"projects"),"--Web:StaticPath="+(candidateRoot==null?Path.Combine(workspace,"AgvDispatch.Web/dist"):Path.Combine(candidateRoot,"Web/wwwroot"))};
using var bridge = candidateRoot==null?Launch("dotnet",workspace,[Path.Combine(workspace,"AgvDispatch.WebHost/bin/Debug/net10.0-windows10.0.19041.0/AgvDispatch.WebHost.dll"),..bridgeArguments]):
    Launch(Path.Combine(candidateRoot,"Web/host/AgvDispatch.WebHost.exe"),Path.Combine(candidateRoot,"Web"),bridgeArguments);
var bridgeOut = bridge.StandardOutput.ReadToEndAsync(); var bridgeErr = bridge.StandardError.ReadToEndAsync();
try
{
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(12) };
    JsonElement snapshot = default;
    var ready = false;
    for (var attempt = 0; attempt < 30; attempt++)
    {
        if (service.HasExited || bridge.HasExited) throw new Exception("隔离测试进程提前退出。");
        try { using var result = await http.GetAsync("/api/snapshot"); if (result.IsSuccessStatusCode) { using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync()); snapshot = json.RootElement.Clone(); ready = true; break; } }
        catch (HttpRequestException) { }
        await Task.Delay(200);
    }
    Check(ready, "真实Service.exe → NamedPipe → WebHost → HTTP可用（私有库/独立管道）");
    using var identityConfig=await http.GetAsync("/api/config");
    var adapterIdentity=await identityConfig.Content.ReadFromJsonAsync<JsonElement>();
    Check(adapterIdentity.GetProperty("adapter").GetProperty("sha256").GetString()!.Length==64,"接入层返回实际程序集构建指纹");
    if(candidateRoot!=null)
    {
        using var releaseManifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(candidateRoot,"release-manifest.json")));
        var releaseId=releaseManifest.RootElement.GetProperty("releaseId").GetString();
        Check(adapterIdentity.GetProperty("adapter").GetProperty("informationalVersion").GetString()==releaseId&&snapshot.GetProperty("status").GetProperty("execution").GetProperty("informationalVersion").GetString()==releaseId,"候选包实际启动的Service与WebHost同批版本一致");
        Check(Directory.EnumerateFiles(Path.Combine(candidateRoot,"Web/wwwroot/assets"),"*.js").Any(f=>File.ReadAllText(f).Contains(releaseId!,StringComparison.Ordinal)),"候选包页面内嵌相同批次而不是复用旧静态页面");
    }
    Check(snapshot.GetProperty("connected").GetBoolean() && snapshot.GetProperty("source").GetString() == "backend", "快照明确来自后台而非浏览器演示");
    Check(snapshot.GetProperty("map").GetProperty("id").GetString() == "MAP-WEB-TEST", "读取真实后台当前活动地图");
    Check(snapshot.GetProperty("tasks").GetProperty("items").EnumerateArray().Any(t => t.GetProperty("taskId").GetString() == "WEB-ISOLATED-TASK"), "读取私有数据库任务");
    var safety = snapshot.GetProperty("status").GetProperty("safety");
    Check(!safety.GetProperty("plcWritesAllowed").GetBoolean() && !safety.GetProperty("agvCommandsAllowed").GetBoolean() && !safety.GetProperty("dispatchingEnabled").GetBoolean(), "PLC/车辆写入及调度均关闭");
    Check(snapshot.GetProperty("positionedVehicleIds").GetArrayLength() == 0, "未核定坐标的车辆不自动绑定");
    using var index = await http.GetAsync("/"); Check(index.IsSuccessStatusCode && (await index.Content.ReadAsStringAsync()).Contains("AGV 调度"), "同源静态前端可访问");
    using var details = await http.GetAsync("/api/tasks/WEB-ISOLATED-TASK"); Check(details.IsSuccessStatusCode && (await details.Content.ReadAsStringAsync()).Contains("WEB-ISOLATED-TASK"), "任务详情复用后台查询");
    using var diagnosticJson = JsonDocument.Parse(await details.Content.ReadAsStringAsync());
    var diagnostic = diagnosticJson.RootElement;
    Check(diagnostic.GetProperty("steps").GetArrayLength() == 9 && diagnostic.GetProperty("steps").EnumerateArray().All(s => s.GetProperty("completionRequirement").GetString()!.Length > 0), "九步完成依据由真实后台传至HTTP");
    Check(diagnostic.GetProperty("diagnosticsObservedAt").ValueKind == JsonValueKind.String && diagnostic.GetProperty("steps").EnumerateArray().All(s => s.GetProperty("progressConditions").GetArrayLength() == 0), "终态不按当前PLC值重判历史步骤");
    Check(diagnostic.GetProperty("executionTimeline").EnumerateArray().Any(e => e.GetProperty("id").GetString() == "WEB-EVIDENCE-1"), "HTTP详情包含持久化执行证据而不依赖操作历史开关");
    using var runtimePage = await http.PostAsJsonAsync("/api/tasks/WEB-ISOLATED-TASK/runtime-events",new TaskRuntimeQuery("WEB-ISOLATED-TASK","Signals"),WebJson.Options);
    var runtimePageJson=await runtimePage.Content.ReadFromJsonAsync<TaskRuntimePage>(WebJson.Options);
    Check(runtimePage.IsSuccessStatusCode&&runtimePageJson?.Items.Any(e=>e.Signal=="BoxPresent")==true,"HTTP按任务分页读取持久化信号变化");
    using var runtimeMismatch=await http.PostAsJsonAsync("/api/tasks/WEB-ISOLATED-TASK/runtime-events",new TaskRuntimeQuery("other"),WebJson.Options);
    Check(runtimeMismatch.StatusCode==HttpStatusCode.BadRequest,"事件查询拒绝路径与请求任务不一致");
    using var runtimeInvalid=await http.PostAsJsonAsync("/api/tasks/WEB-ISOLATED-TASK/runtime-events",new TaskRuntimeQuery("WEB-ISOLATED-TASK",Kind:"invalid"),WebJson.Options);
    Check(runtimeInvalid.StatusCode==HttpStatusCode.BadRequest,"事件查询拒绝未知类型");
    using var archiveResponse=await http.GetAsync("/api/tasks/WEB-ISOLATED-TASK/diagnostics/export");
    var diagnosticBytes=await archiveResponse.Content.ReadAsByteArrayAsync();
    Check(archiveResponse.IsSuccessStatusCode&&archiveResponse.Content.Headers.ContentType?.MediaType=="application/zip"&&Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(diagnosticBytes))==archiveResponse.Headers.GetValues("X-Content-SHA256").Single(),"HTTP下载联调ZIP且SHA256一致");
    using(var archiveZip=new System.IO.Compression.ZipArchive(new MemoryStream(diagnosticBytes)))Check(archiveZip.GetEntry("manifest.json")!=null&&archiveZip.GetEntry("runtime-events.csv")!=null,"联调包含清单和信号/阶段CSV");
    using var recoveryRead=await http.GetAsync("/api/recovery");
    var recoveryJson=await recoveryRead.Content.ReadFromJsonAsync<JsonElement>();
    Check(recoveryRead.IsSuccessStatusCode&&!recoveryJson.GetProperty("recoveryEnabled").GetBoolean()&&recoveryJson.GetProperty("items").ValueKind==JsonValueKind.Array,"恢复入口可只读查询，写操作默认关闭");
    using var recoveryDenied=await http.PostAsJsonAsync("/api/recovery/begin",new TaskRecoveryReviewRequest("WEB-ISOLATED-TASK","隔离测试验证默认禁止恢复写操作"),WebJson.Options);
    Check(recoveryDenied.StatusCode==HttpStatusCode.Forbidden,"Web默认禁止建立全场恢复批次");
    using var options = await http.GetAsync("/api/task-options"); Check(options.IsSuccessStatusCode, "任务创建条件查询接通");
    var executionMode=snapshot.GetProperty("status").GetProperty("execution");
    Check(executionMode.GetProperty("buildId").GetString()!.Length==64&&!executionMode.GetProperty("handshakeEnabled").GetBoolean()&&executionMode.GetProperty("mode").GetString()=="OriginalExecutor","后台快照提供真实构建SHA及未启用的新握手模式");
    using var commissioningResponse=await http.PostAsJsonAsync("/api/commissioning",new CommissioningQuery(),WebJson.Options);
    var commissioning=await commissioningResponse.Content.ReadFromJsonAsync<CommissioningReport>(WebJson.Options);
    Check(commissioningResponse.IsSuccessStatusCode&&commissioning?.Runtime.BuildId==executionMode.GetProperty("buildId").GetString()&&commissioning.MapId=="MAP-WEB-TEST","联调检查与真实后台构建、运行地图一致");
    Check(commissioning!.Checks.Any(c=>c.Id=="creation"&&c.Status=="Blocked")&&commissioning.Checks.Any(c=>c.Id=="route-verification"&&c.Status=="Unknown"),"关闭执行和未核验车端路线不能伪装通过");
    var checkedStation=commissioning.Options.Stations.First().StationId;
    using var selectedChecksResponse=await http.PostAsJsonAsync("/api/commissioning",new CommissioningQuery(checkedStation),WebJson.Options);
    var selectedChecks=await selectedChecksResponse.Content.ReadFromJsonAsync<CommissioningReport>(WebJson.Options);
    Check(selectedChecksResponse.IsSuccessStatusCode&&selectedChecks!.Signals.Count==13&&selectedChecks.Signals.All(s=>s.Value==null&&s.Status!="Pass"),"联调13位缺失采样保持未知且不补造电平");
    Check(selectedChecks!.Checks.Any(c=>c.Target=="task-settings")&&selectedChecks.Checks.Any(c=>c.Target=="vehicles"),"联调缺项提供任务参数与车辆配置导航");
    Check(selectedChecks!.Checks.Where(c=>c.Group=="业务前提").Count()==4&&selectedChecks.Checks.Where(c=>c.Group=="业务前提").All(c=>c.Status=="Unknown"),"故障与通讯业务前提缺失时不把未知反馈按0放行");
    using var invalidChecks=await http.PostAsJsonAsync("/api/commissioning",new {stationId=new string('x',33),vehicleId=""});
    Check(invalidChecks.StatusCode==HttpStatusCode.BadRequest,"联调查询拒绝无效工位编号");
    Check(snapshot.GetProperty("alarms").ValueKind == JsonValueKind.Array, "快照包含后台告警记录");
    using var page = await http.GetAsync("/api/tasks?filter=All&search=WEB-ISOLATED-TASK&page=1");
    using var pageJson = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
    Check(page.IsSuccessStatusCode && pageJson.RootElement.GetProperty("totalCount").GetInt32() == 1, "任务查询搜索与总数来自后台");
    using var invalidFilter = await http.GetAsync("/api/tasks?filter=unexpected&page=1");
    Check(invalidFilter.StatusCode == HttpStatusCode.BadRequest, "拒绝未知任务筛选");
    using var invalidPage = await http.GetAsync("/api/tasks?filter=All&page=0");
    Check(invalidPage.StatusCode == HttpStatusCode.BadRequest, "拒绝无效页码");
    using var maps = await http.GetAsync("/api/maps");
    var mapText = await maps.Content.ReadAsStringAsync();
    Check(maps.IsSuccessStatusCode && mapText.Contains("MAP-WEB-TEST") && !mapText.Contains("backgroundFilePath") && !mapText.Contains("previewPngPath"), "地图目录提供真实版本元数据且不暴露源文件路径");
    using var newMap = await http.PostAsJsonAsync("/api/maps/create", new CreateMapRequest("Web编辑隔离图", "isolated", "", "TestCartesian", "m", 1), WebJson.Options);
    var createdMap = await newMap.Content.ReadFromJsonAsync<MapOperationResult>(WebJson.Options);
    Check(createdMap?.Accepted == true && createdMap.Workspace != null, "Web创建真实后台地图草稿");
    var workspaceMap = createdMap!.Workspace!;
    using var draft = await http.PostAsJsonAsync("/api/maps/draft", new SaveMapDraftRequest(workspaceMap.Project.MapId, workspaceMap.Draft.VersionId, "Web编辑隔离图", "isolated", "", "TestCartesian", "m", 1, definition, workspaceMap.Draft.EditToken), WebJson.Options);
    var draftResult = await draft.Content.ReadFromJsonAsync<MapOperationResult>(WebJson.Options);
    Check(draftResult?.Accepted == true && draftResult.Workspace!.Draft.Definition.Edges[0].ControlPoints!.Count == 2, "草稿保存保留曲线控制点");
    using var conflictDraft = await http.PostAsJsonAsync("/api/maps/draft", new SaveMapDraftRequest(workspaceMap.Project.MapId, workspaceMap.Draft.VersionId, "Web编辑隔离图", "isolated", "", "TestCartesian", "m", 1, definition, workspaceMap.Draft.EditToken), WebJson.Options);
    Check((await conflictDraft.Content.ReadFromJsonAsync<MapOperationResult>(WebJson.Options))?.Accepted == false, "旧编辑令牌拒绝覆盖新草稿");
    using var fullWorkspace = await http.GetAsync($"/api/maps/{workspaceMap.Project.MapId}/workspace");
    Check(fullWorkspace.IsSuccessStatusCode && (await fullWorkspace.Content.ReadAsStringAsync()).Contains("controlPoints"), "完整草稿加载而非运行图投影");
    using var versionHistory = await http.GetAsync($"/api/maps/{workspaceMap.Project.MapId}/versions");
    Check(versionHistory.IsSuccessStatusCode, "后台版本列表可读取");
    using var publishedMap = await http.PostAsJsonAsync("/api/maps/publish", new PublishMapRequest(workspaceMap.Project.MapId, workspaceMap.Draft.VersionId), WebJson.Options);
    Check((await publishedMap.Content.ReadFromJsonAsync<MapOperationResult>(WebJson.Options))?.Accepted == true, "Web可发布后台已保存的有效地图草稿");
    var publishedWorkspace=await new DispatchPipe(pipeName).ReadAsync<MapWorkspaceSnapshot>(TrafficControlOperations.GetMapWorkspace,new{MapId=workspaceMap.Project.MapId},default);
    var activation=new ActivateMapRequest(workspaceMap.Project.MapId,publishedWorkspace!.Project.PublishedVersionId!,"ACTIVATE "+publishedWorkspace.Project.Name,true,true,"isolated web lifecycle test; no devices");
    using var deniedActivation=await http.PostAsJsonAsync("/api/maps/activate",activation with{SiteSafetyConfirmed=false},WebJson.Options);
    Check((await deniedActivation.Content.ReadFromJsonAsync<MapOperationResult>(WebJson.Options))?.Accepted==false,"Web激活仍拒绝未确认的安全条件");
    using var startActivation=await http.PostAsJsonAsync("/api/maps/switch/begin",new BeginMapSwitchRequest(activation.MapId,activation.VersionId),WebJson.Options);
    Check((await startActivation.Content.ReadFromJsonAsync<MapSwitchResult>(WebJson.Options))?.Accepted==true,"Web先开始目标版本安全切换");
    using var activated=await http.PostAsJsonAsync("/api/maps/activate",activation,WebJson.Options);
    var activatedResult=await activated.Content.ReadFromJsonAsync<MapOperationResult>(WebJson.Options);
    Check(activatedResult?.Accepted==true,"隔离环境Web激活已保存发布版本："+activatedResult?.Message);
    var activatedSwitch=await http.GetFromJsonAsync<MapSwitchStatus>("/api/maps/switch",WebJson.Options);
    Check(activatedSwitch?.Current?.Phase==MapSwitchPhase.AwaitingResume,"激活只进入待放行，不自动恢复派发");
    using var releaseActivation=await http.PostAsJsonAsync("/api/maps/switch/resume",new FinishMapSwitchRequest(activatedSwitch!.Current!.ReleaseId,true,true,"isolated activation verification",true),WebJson.Options);
    Check((await releaseActivation.Content.ReadFromJsonAsync<MapSwitchResult>(WebJson.Options))?.Accepted==true,"核对后由原后台完成地图放行");
    using var beginSwitch=await http.PostAsJsonAsync("/api/maps/switch/begin",new BeginMapSwitchRequest("MAP-WEB-TEST","V1"),WebJson.Options);
    Check((await beginSwitch.Content.ReadFromJsonAsync<MapSwitchResult>(WebJson.Options))?.Accepted==true,"Web开始安全切换回原隔离地图");
    var switching=await http.GetFromJsonAsync<MapSwitchStatus>("/api/maps/switch",WebJson.Options);
    Check(switching?.Current?.Phase==MapSwitchPhase.Draining,"Web读取正在收尾的切换身份和阶段");
    var finishSwitch=new FinishMapSwitchRequest(switching!.Current!.ReleaseId,true,true,"isolated no-device workflow and safety evidence",true);
    using var drained=await http.PostAsJsonAsync("/api/maps/switch/drain",finishSwitch,WebJson.Options);
    Check((await drained.Content.ReadFromJsonAsync<MapSwitchResult>(WebJson.Options))?.Accepted==false,"普通收尾不能冒充中断恢复或直接激活");
    using var activateOriginal=await http.PostAsJsonAsync("/api/maps/activate",new ActivateMapRequest("MAP-WEB-TEST","V1","ACTIVATE "+definition.Name,true,true,"isolated return to original map"),WebJson.Options);
    var originalActivation=await activateOriginal.Content.ReadFromJsonAsync<MapOperationResult>(WebJson.Options);
    Check(originalActivation?.Accepted==true,"收尾后通过明确激活请求返回原图："+originalActivation?.Message+(!activateOriginal.IsSuccessStatusCode?await activateOriginal.Content.ReadAsStringAsync():""));
    using var resumed=await http.PostAsJsonAsync("/api/maps/switch/resume",finishSwitch,WebJson.Options);
    Check((await resumed.Content.ReadFromJsonAsync<MapSwitchResult>(WebJson.Options))?.Accepted==true,"Web完成原后台安全切换恢复流程");
    using var importPreview = await http.PostAsJsonAsync("/api/maps/import?format=json", definition, WebJson.Options);
    var imported = await importPreview.Content.ReadFromJsonAsync<MapImportPreviewResult>(WebJson.Options);
    Check(imported?.Accepted == true && imported.Definition?.Source == "", "选择文件的规范化地图导入预览成功且不保留临时路径");
    using var invalidImport = await http.PostAsJsonAsync("/api/maps/import?format=exe", definition, WebJson.Options);
    Check(invalidImport.StatusCode == HttpStatusCode.BadRequest, "拒绝非地图导入格式");
    using var configResponse = await http.GetAsync("/api/vehicles/configuration");
    var vehicleConfig = await configResponse.Content.ReadFromJsonAsync<JsonElement>();
    Check(configResponse.IsSuccessStatusCode && vehicleConfig.GetProperty("vehicles").ValueKind == JsonValueKind.Array, "读取后台保存的车辆配置");
    using var configConflict = await http.PostAsJsonAsync("/api/vehicles/configuration", new { expectedRevision = "stale", vehicles = Array.Empty<object>() });
    Check(configConflict.StatusCode == HttpStatusCode.Conflict, "车辆配置旧版本拒绝覆盖");
    var vehiclePayload = new { expectedRevision = vehicleConfig.GetProperty("revision").GetString(), vehicles = new[] { new AgvConnectionSetting { VehicleId = "WEB-CONFIG-ONLY", DisplayName = "隔离车", IpAddress = "127.0.0.1", Enabled = false } } };
    using (var isolatedDb = DatabaseFactory.Create(new() { DatabasePath = databasePath }))
        isolatedDb.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.Created).Where(t => t.TaskId == "WEB-ISOLATED-TASK").ExecuteCommand();
    using var activeTaskGuard = await http.PostAsJsonAsync("/api/vehicles/configuration", vehiclePayload, WebJson.Options);
    Check(!activeTaskGuard.IsSuccessStatusCode, "有未结束任务时后台拒绝车辆配置修改");
    using (var isolatedDb = DatabaseFactory.Create(new() { DatabasePath = databasePath }))
        isolatedDb.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.Completed).Where(t => t.TaskId == "WEB-ISOLATED-TASK").ExecuteCommand();
    using var configSave = await http.PostAsJsonAsync("/api/vehicles/configuration", vehiclePayload, WebJson.Options);
    var savedConfig = await configSave.Content.ReadFromJsonAsync<JsonElement>();
    Check(configSave.IsSuccessStatusCode && savedConfig.GetProperty("restartRequired").GetBoolean(), "无未结束任务时可保存新增车辆，须服务重启生效");
    var taskParameters=await http.GetFromJsonAsync<TaskParameterSnapshot>("/api/task-parameters",WebJson.Options);
    Check(taskParameters?.Configuration.Actions.Count==4,"Web任务参数返回四段动作及原修订");
    using var parameterValidation=await http.PostAsJsonAsync("/api/task-parameters/validate",new TaskParameterRequest(Guid.NewGuid().ToString("N"),"OLD","MAP-WEB-TEST","V1",taskParameters!.Configuration),WebJson.Options);
    Check((await parameterValidation.Content.ReadFromJsonAsync<TaskParameterResult>(WebJson.Options))?.Accepted==false,"Web参数校验不接受旧修订");
    using var parameterSave=await http.PostAsJsonAsync("/api/task-parameters/save",new TaskParameterRequest(Guid.NewGuid().ToString("N"),"OLD","MAP-WEB-TEST","V1",taskParameters.Configuration),WebJson.Options);
    Check((await parameterSave.Content.ReadFromJsonAsync<TaskParameterResult>(WebJson.Options))?.Accepted==false,"Web参数保存复用后台旧修订门禁");
    var bindingState=await http.GetFromJsonAsync<JsonElement>("/api/map-bindings",WebJson.Options);
    using var staleBinding=await http.PostAsJsonAsync("/api/map-bindings",new{expectedRevision="OLD",bindings=Array.Empty<object>()},WebJson.Options);
    Check(staleBinding.StatusCode==HttpStatusCode.Conflict,"Web绑定旧修订拒绝覆盖");
    using var bindingSave=await http.PostAsJsonAsync("/api/map-bindings",new{expectedRevision=bindingState.GetProperty("revision").GetString(),bindings=Array.Empty<object>()},WebJson.Options);
    Check(bindingSave.IsSuccessStatusCode,"Web绑定保存经原配置服务校验");
    var retainedVehicles=await http.GetFromJsonAsync<JsonElement>("/api/vehicles/configuration",WebJson.Options);
    Check(retainedVehicles.GetProperty("vehicles").GetArrayLength()==1,"保存绑定保留其他车辆配置");
    var planId=Guid.NewGuid().ToString();
    var plan=JsonSerializer.SerializeToElement(new{schema="agv-web-plan",version=1,id=planId,name="server engineering fixture",updatedAt=DateTimeOffset.UtcNow,definition,scene=new[]{new{id="AREA",name="synthetic scene",kind="area",x=0,y=0,width=2,height=2,color="#123456",visible=true}},vehicles=Array.Empty<object>(),waypoints=new[]{"A","B"}},WebJson.Options);
    using var projectSave=await http.PostAsJsonAsync("/api/projects",new ProjectSaveRequest("",plan,"MAP-WEB-TEST","V1"),WebJson.Options);
    var project=await projectSave.Content.ReadFromJsonAsync<SavedProject>(WebJson.Options);
    Check(projectSave.IsSuccessStatusCode&&project?.Plan.GetProperty("scene").GetArrayLength()==1,"服务端方案保存场景/地图/顺序，返回内容修订");
    using var conflictingProject=await http.PostAsJsonAsync("/api/projects",new ProjectSaveRequest("",plan),WebJson.Options);
    Check(conflictingProject.StatusCode==HttpStatusCode.Conflict,"服务端项目并发旧修订不覆盖");
    var changedPlan=System.Text.Json.Nodes.JsonNode.Parse(plan.GetRawText())!;changedPlan["name"]="second version";
    using var updatedProject=await http.PostAsJsonAsync("/api/projects",new ProjectSaveRequest(project!.Revision,JsonSerializer.SerializeToElement(changedPlan),"MAP-WEB-TEST","V1"),WebJson.Options);
    var updated=await updatedProject.Content.ReadFromJsonAsync<SavedProject>(WebJson.Options);
    Check(updatedProject.IsSuccessStatusCode&&updated!.Revision!=project.Revision,"服务端方案生成新版本");
    var versionsProject=await http.GetFromJsonAsync<JsonElement[]>("/api/projects/"+planId+"/history",WebJson.Options);
    Check(versionsProject?.Length==2,"服务端保留两版历史而非覆写备份");
    var originalProject=await http.GetFromJsonAsync<SavedProject>("/api/projects/"+planId+"?revision="+project.Revision,WebJson.Options);
    Check(originalProject!.Plan.GetProperty("name").GetString()=="server engineering fixture","历史恢复预览仍保留原内容");
    var reopened=new WebProjectStore(Path.Combine(root,"projects"));
    Check((await reopened.ReadAsync(planId,null,default))!.Revision==updated!.Revision,"重建服务端库对象仍可读取持久化项目");
    using var restoreProject=await http.PostAsJsonAsync("/api/projects",new ProjectSaveRequest(updated!.Revision,plan,"MAP-WEB-TEST","V1"),WebJson.Options);
    var restoredProject=await restoreProject.Content.ReadFromJsonAsync<SavedProject>(WebJson.Options);
    Check(restoreProject.IsSuccessStatusCode&&restoredProject!.Revision!=project.Revision,"恢复旧内容生成新修订，不改写原历史");
    var verifiedOld=await http.GetFromJsonAsync<SavedProject>("/api/projects/"+planId+"?revision="+project.Revision,WebJson.Options);
    Check(verifiedOld!.SavedAt==project.SavedAt,"恢复不覆盖原存档时间和摘要");
    using var unchangedProject=await http.PostAsJsonAsync("/api/projects",new ProjectSaveRequest(restoredProject!.Revision,plan,"MAP-WEB-TEST","V1"),WebJson.Options);
    Check((await unchangedProject.Content.ReadFromJsonAsync<SavedProject>(WebJson.Options))!.Revision==restoredProject.Revision,"重复保存相同内容不会堆积历史");
    using var invalidProjectPath=await http.GetAsync("/api/projects/not-a-guid");Check(invalidProjectPath.StatusCode==HttpStatusCode.BadRequest,"方案编号不能作为任意路径");
    var badPlan=System.Text.Json.Nodes.JsonNode.Parse(plan.GetRawText())!;badPlan["id"]=Guid.NewGuid().ToString();badPlan["waypoints"]=new System.Text.Json.Nodes.JsonArray("MISSING");
    using var invalidProject=await http.PostAsJsonAsync("/api/projects",new ProjectSaveRequest("",JsonSerializer.SerializeToElement(badPlan)),WebJson.Options);
    Check(invalidProject.StatusCode==HttpStatusCode.BadRequest,"服务端拒绝丢失航点引用");
    using var runtimeAfterDraft = await http.GetAsync("/api/snapshot");
    var runtimeJson = await runtimeAfterDraft.Content.ReadFromJsonAsync<JsonElement>();
    Check(runtimeJson.GetProperty("map").GetProperty("id").GetString() == "MAP-WEB-TEST" && runtimeJson.GetProperty("status").GetProperty("agvs").GetArrayLength() == 0, "编辑地图及车辆保存不切换运行地图或自动启动小车连接");
    var validRequest = await new DispatchPipe(pipeName).ReadAsync<ManualTaskCreationResult>(TrafficControlOperations.CreateTask,
        new ManualTaskCreationRequest(Guid.NewGuid().ToString("N"), "TEST-STATION", null, 50, "MAP-WEB-TEST", "V1"), default);
    Check(validRequest is { Accepted: false, Created: false } && !validRequest.Message.StartsWith("请求编号、工位"),
        "32位申请编号通过后台格式核验，执行条件关闭仍拒绝建任务");
    using var deniedOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/config"); deniedOrigin.Headers.Add("Origin", "https://example.invalid");
    using var originResult = await http.SendAsync(deniedOrigin); Check(originResult.StatusCode == HttpStatusCode.Forbidden, "拒绝跨来源读取/操作");
    using var deniedHost = new HttpRequestMessage(HttpMethod.Get, "/health"); deniedHost.Headers.Host = "example.invalid:" + port;
    using var hostResult = await http.SendAsync(deniedHost); Check(hostResult.StatusCode == HttpStatusCode.Forbidden, "拒绝不匹配的Host");
    using var fetchSite = new HttpRequestMessage(HttpMethod.Get, "/api/config"); fetchSite.Headers.Add("Sec-Fetch-Site", "cross-site");
    using var siteResult = await http.SendAsync(fetchSite); Check(siteResult.StatusCode == HttpStatusCode.Forbidden, "拒绝跨站浏览器请求");
    using var landing = new HttpRequestMessage(HttpMethod.Get, "/"); landing.Headers.Add("Sec-Fetch-Site", "cross-site");
    landing.Headers.Add("Sec-Fetch-Mode", "navigate"); landing.Headers.Add("Sec-Fetch-Dest", "document");
    using var landingResult = await http.SendAsync(landing); Check(landingResult.IsSuccessStatusCode, "允许从聊天链接正常打开静态首页，API仍保持同源");
    using var create = await http.PostAsJsonAsync("/api/tasks", new ManualTaskCreationRequest("WEB-DENIED", "TEST-STATION", "AGV-TEST", 50, "MAP-WEB-TEST", "V1"));
    Check(create.StatusCode == HttpStatusCode.Forbidden, "默认关闭提交，不产生现场写入");
    using var unknown = await http.PostAsJsonAsync("/api/pipe", new { operation = "SaveCommunicationSettings" }); Check(unknown.StatusCode == HttpStatusCode.NotFound, "不存在任意NamedPipe代理入口");
    using var ws = new ClientWebSocket(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/ws"), timeout.Token);
    var received = await ReadMessage(ws, timeout.Token);
    Check(received.GetProperty("connected").GetBoolean() && received.GetProperty("map").GetProperty("id").GetString() == "MAP-WEB-TEST", "WebSocket发送真实后台地图与任务快照");
    using (var diagnosticDb = DatabaseFactory.Create(new() { DatabasePath = databasePath }))
    {
        diagnosticDb.Updateable<DispatchTaskEntity>().SetColumns(t=>t.VehicleId=="AGV-TEST").Where(t=>t.TaskId=="WEB-ISOLATED-TASK").ExecuteCommand();
        var payloadJson=JsonSerializer.Serialize(new AgvTransportCommandPayload("WEB-STEP-1","AGV-TEST","LM1","LM2",["LM1","LM2"],new("JackLoad")),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var originalRoute=TransportRouteSnapshots.Capture("WEB-ISOLATED-TASK","WEB-STEP-1","WEB-TRANSPORT","AGV-TEST",payloadJson,new("MAP-WEB-TEST","V1",definition,DateTimeOffset.UtcNow),new TrafficRequestEntity{TaskId="WEB-ISOLATED-TASK",StepId="WEB-STEP-1",VehicleId="AGV-TEST",MapId="MAP-WEB-TEST",MapVersionId="V1",Source="A",Destination="B",State="Acquired",EdgeIds="[\"CURVE\"]"});
        var originalJson=TransportRouteSnapshots.Serialize(originalRoute);
        diagnosticDb.Insertable(new AgvCommandOutboxEntity{CommandId="WEB-TRANSPORT",IdempotencyKey="WEB-TRANSPORT",TaskId="WEB-ISOLATED-TASK",StepId="WEB-STEP-1",VehicleId="AGV-TEST",PayloadJson=payloadJson,Status=(int)AgvCommandStatus.Sent,AttemptCount=1,ExecutionMapId="MAP-WEB-TEST",ExecutionMapVersionId="V1",ExecutionRouteSnapshotJson=originalJson,ExecutionRouteSnapshotHash=TransportRouteSnapshots.Hash(originalJson),CreatedAt=DateTime.Now,UpdatedAt=DateTime.Now,NextAttemptAt=DateTime.Now}).ExecuteCommand();
        diagnosticDb.Updateable<DispatchTaskEntity>().SetColumns(t => t.Stage == (int)DispatchTaskStage.PickingUp).Where(t => t.TaskId == "WEB-ISOLATED-TASK").ExecuteCommand();
        diagnosticDb.Updateable<DispatchTaskStepEntity>().SetColumns(s => new DispatchTaskStepEntity { Status=(int)DispatchTaskStepStatus.Ready, CompletedAt=null }).Where(s => s.StepId == "WEB-STEP-2").ExecuteCommand();
        diagnosticDb.Updateable<DispatchTaskStepEntity>().SetColumns(s => new DispatchTaskStepEntity { Status=(int)DispatchTaskStepStatus.Pending, StartedAt=null, CompletedAt=null }).Where(s => s.TaskId == "WEB-ISOLATED-TASK" && s.Sequence > 2).ExecuteCommand();
    }
    using var activeDiagnostic = await http.GetAsync("/api/tasks/WEB-ISOLATED-TASK");
    var activeDetail = await activeDiagnostic.Content.ReadFromJsonAsync<TaskCenterDetail>(WebJson.Options);
    Check(activeDetail?.Steps.Single(s => s.IsCurrent).ProgressConditions.Count == 3 && activeDetail.PlcConditions.All(c => c.Result == "Unknown"), "活动步骤输出三项实际条件；关闭PLC时为未知，不生成满足值");
    Check(activeDetail?.Commands.Single().Route is {MapId:"MAP-WEB-TEST",MapVersionId:"V1",Action:"JackLoad"} route&&route.EdgeIds.SequenceEqual(new[]{"CURVE"}),"HTTP任务详情保留已校验原命令曲线、地图及动作，当前PLC步可关联前段");
    if (args.Contains("--inspect"))
    {
        Console.WriteLine($"INSPECT http://127.0.0.1:{port}/ — isolated fixture only; all device execution disabled. Remove inspect-hold to finish.");
        var hold = Path.Combine(root, "inspect-hold"); File.WriteAllText(hold, "isolated UI verification");
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        while (File.Exists(hold) && DateTimeOffset.UtcNow < deadline) await Task.Delay(500);
    }
    service.Kill(); await service.WaitForExitAsync();
    using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var disconnected = false;
    // Inspection leaves older snapshots buffered. Drain them within the time budget, rather than
    // assuming the disconnect frame must be among the first five observations.
    while (!disconnectTimeout.IsCancellationRequested) { var message = await ReadMessage(ws, disconnectTimeout.Token); if (!message.GetProperty("connected").GetBoolean()) { disconnected = true; break; } }
    Check(disconnected, "后台停止后推送明确断连，不切换成演示数据");
    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", disconnectTimeout.Token);
    using var db = DatabaseFactory.Create(new() { DatabasePath = databasePath });
    Check(db.Queryable<DispatchTaskEntity>().Count() == 1 && db.Queryable<DispatchTaskEntity>().First().TaskId == "WEB-ISOLATED-TASK", "查询及被拒绝提交没有创建额外任务");
}
finally
{
    if (!service.HasExited) { service.Kill(); await service.WaitForExitAsync(); }
    if (!bridge.HasExited) { bridge.Kill(); await bridge.WaitForExitAsync(); }
    File.WriteAllText(Path.Combine(root, "service.log"), await serviceOut + "\n" + await serviceErr);
    File.WriteAllText(Path.Combine(root, "web.log"), await bridgeOut + "\n" + await bridgeErr);
    File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { checks, databasePath, pipeName, port }, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine($"PASS: {checks.Count} Web adapter checks; only isolated processes were started/stopped.");

static Process Launch(string exe, string workingDirectory, string[] arguments)
{
    var start = new ProcessStartInfo(exe) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.Environment["DOTNET_ENVIRONMENT"] = "Production";
    foreach (var arg in arguments) start.ArgumentList.Add(arg);
    return Process.Start(start)!;
}
static async Task<JsonElement> ReadMessage(ClientWebSocket ws, CancellationToken ct)
{
    using var stream = new MemoryStream(); var buffer = new byte[16384];
    while (true) { var result = await ws.ReceiveAsync(buffer, ct); if (result.MessageType == WebSocketMessageType.Close) throw new Exception("WebSocket提前关闭");
        stream.Write(buffer, 0, result.Count); if (stream.Length > 8 * 1024 * 1024) throw new Exception("测试快照超预算"); if (result.EndOfMessage) break; }
    using var json = JsonDocument.Parse(stream.ToArray()); return json.RootElement.Clone();
}
