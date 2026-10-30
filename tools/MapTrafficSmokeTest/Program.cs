using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;

if (args.Length == 2 && (args[0] == "--cad-inspect" || args[0] == "--cad-render"))
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    Console.WriteLine("CAD read started");
    var document = ACadSharp.IO.DwgReader.Read(args[1]);
    Console.WriteLine($"CAD read {watch.Elapsed}, entities={document.Entities.Count()}, layers={document.Layers.Count()}");
    foreach (var group in document.Entities.GroupBy(entity => entity.GetType().Name).OrderByDescending(group => group.Count()))
        Console.WriteLine(group.Key + ": " + group.Count());
    foreach (var group in document.Entities.OfType<ACadSharp.Entities.Spline>().GroupBy(s => s.Degree))
        Console.WriteLine($"Spline degree={group.Key}, count={group.Count()}, controls max={group.Max(s => s.ControlPoints.Count)}");
    if (args[0] == "--cad-render")
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "cad-diagnostic"); Directory.CreateDirectory(folder);
        var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args[1]));
        var rendered = await new CadUnderlayService().RenderAsync(new CadUnderlayRenderRequest(args[1], folder, [], 2200, 1400), CancellationToken.None);
        Console.WriteLine("PNG + SVG completed " + watch.Elapsed);
        Console.WriteLine(rendered.PreviewPngPath); Console.WriteLine(rendered.PreviewSvgPath);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(rendered.ViewBox));
        foreach (var warning in rendered.Warnings) Console.WriteLine(warning);
        if (!hash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args[1])))) throw new Exception("CAD source changed");
        Console.WriteLine("Source SHA256 unchanged");
    }
    return;
}
await MapGeometrySmokeTests.RunAsync();
if (args.Contains("--geometry-only", StringComparer.OrdinalIgnoreCase)) return;

var arcA=new MapNode("LM1",1.608,1.538,MapNodeType.WaitingPoint,true,[],"LM1");
var arcB=new MapNode("LM4",1.608,-1.521,MapNodeType.WaitingPoint,true,[],"LM4");
var arcForward=new MapEdge("ARC-F","LM1","LM4",3.0941,0.5,false,true,[]){GeometryKind=MapEdgeGeometryKind.CircularArc,ControlPoints=[new(1.4069999999999965,0.009000000000000341)]};
var arcReverse=arcForward with{Id="ARC-R",From="LM4",To="LM1"};
var arcMap=new MapDefinition("enabled arc","","RobotWorld","m",[arcA,arcB],[arcForward,arcReverse],[]);
var arcValidator=new MapValidationService();
Require(arcValidator.Validate(arcMap).IsValid,"启用后的同路反向圆弧不能被折线采样误报为未保护交叉。");
Require(arcValidator.Validate(arcMap with{Edges=[arcForward,arcForward with{Id="DUP"}]}).Issues.Any(i=>i.Code=="EDGE_OVERLAP"),"反向圆弧例外不能放过同向重复道路。");
Require(arcValidator.Validate(arcMap with{Edges=[arcForward with{Bidirectional=true},arcReverse]}).Issues.Any(i=>i.Code=="EDGE_OVERLAP"),"反向圆弧例外不能放过双向加反向重复。");

var resources = new[]
{
    new TrafficResourceDefinition("NARROW-01", TrafficResourceType.NarrowAisle),
    new TrafficResourceDefinition("CROSS-01", TrafficResourceType.Intersection)
};
var graph = new MapGraph(
    [
        new MapNode("A", 0, 0, MapNodeType.Station, true, []),
        new MapNode("X", 1, 0, MapNodeType.Waypoint, true, []),
        new MapNode("Y", 0, 1, MapNodeType.Waypoint, true, ["CROSS-01"]),
        new MapNode("Z", 1, 1, MapNodeType.Waypoint, true, []),
        new MapNode("D", 2, 0, MapNodeType.Station, true, []),
        new MapNode("U", 10, 0, MapNodeType.Waypoint, true, []),
        new MapNode("V", 11, 0, MapNodeType.Waypoint, true, [])
    ],
    [
        new MapEdge("AX", "A", "X", 1, 1, true, true, ["NARROW-01"]),
        new MapEdge("XD", "X", "D", 1, 1, true, true, ["NARROW-01"]),
        new MapEdge("AY", "A", "Y", 2, 1, false, true, []),
        new MapEdge("YZ", "Y", "Z", 2, 1, false, true, []),
        new MapEdge("ZD", "Z", "D", 2, 1, false, true, []),
        new MapEdge("UV", "U", "V", 1, 1, false, true, [])
    ],
    resources);

var shortest = graph.FindShortestRoute("A", "D");
Require(shortest is not null && shortest.Nodes.SequenceEqual(new[] { "A", "X", "D" }),
    "未封锁时应选择窄道最短路径。");
Require(shortest!.ResourceIds.Contains("NARROW-01"), "路径应携带窄道路权。");
var detour = graph.FindShortestRoute(
    "A",
    "D",
    new HashSet<string>(["NARROW-01"], StringComparer.OrdinalIgnoreCase));
Require(detour is not null && detour.Nodes.SequenceEqual(new[] { "A", "Y", "Z", "D" }),
    "窄道占用时应选择可行绕行路径。");
var congestionDetour = graph.FindShortestRoute(
    "A", "D", null,
    new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["AX"] = 100 });
Require(congestionDetour is not null && congestionDetour.Nodes.SequenceEqual(new[] { "A", "Y", "Z", "D" }),
    "路段动态拥堵代价应引导新任务选择可行绕行路径。");
Require(graph.FindShortestRoute("D", "A")?.Nodes.SequenceEqual(new[] { "D", "X", "A" }) == true,
    "双向路段反向规划失败。");
Require(graph.FindShortestRoute("U", "V") is not null && graph.FindShortestRoute("V", "U") is null,
    "单向路段不应允许反向穿越。");
var mappedDefinition = new MapDefinition(
    "Mapped", "test", "LocalCartesian", "m",
    [
        new MapNode("A", 0, 0, MapNodeType.Station, true, [], "LM-A"),
        new MapNode("X", 1, 0, MapNodeType.Waypoint, true, [], "LM-X"),
        new MapNode("D", 2, 0, MapNodeType.Station, true, [], "LM-D")
    ],
    [
        new MapEdge("AX", "A", "X", 1, 1, true, true, []),
        new MapEdge("XD", "X", "D", 1, 1, true, true, [])
    ],
    []);
var logicalSource = MapRouteSiteMapper.ResolveLogicalNodeId(mappedDefinition, "LM-A");
var logicalPlan = mappedDefinition.ToGraph().FindShortestRoute(logicalSource, "D")!;
var agvPlan = MapRouteSiteMapper.ToAgvSitePlan(mappedDefinition, logicalPlan);
Require(agvPlan.Nodes.First() == "LM-A" && agvPlan.Nodes.Last() == "LM-D",
    "业务节点路径应在下发前转换为Robokit真实站点ID。");
var preflight = MapDeploymentPreflightAnalyzer.Analyze(mappedDefinition);
Require(preflight.Ready && preflight.RoutePairCount == 2 && preflight.UnreachablePairCount == 0,
    "发布前全站点有向路径矩阵检查应识别双向可达地图。");
var brokenPreflight = MapDeploymentPreflightAnalyzer.Analyze(mappedDefinition with
{
    Edges = [new MapEdge("AX", "A", "X", 1, 1, false, true, [])]
});
Require(!brokenPreflight.Ready && brokenPreflight.UnreachablePairCount == 2,
    "发布前检查必须发现断路导致的不可达站点对。");
var positionNow = DateTimeOffset.Now;
var normalPosition = AgvMapPositionAnalyzer.Analyze(
    0.5, 0.1, 0.95, positionNow, mappedDefinition, positionNow,
    TimeSpan.FromSeconds(5), 0.5, 0.75);
Require(normalPosition.State == AgvMapPositionState.Normal && normalPosition.NearestEdgeId == "AX",
    "路网内实时AGV坐标应判定为正常并返回最近路段。");
var offRoutePosition = AgvMapPositionAnalyzer.Analyze(
    0.5, 2, 0.95, positionNow, mappedDefinition, positionNow,
    TimeSpan.FromSeconds(5), 0.5, 0.75);
Require(offRoutePosition.State == AgvMapPositionState.OffRoute && offRoutePosition.DistanceToRouteMeters > 1.9,
    "偏离路网的AGV坐标必须被识别。");
var stalePosition = AgvMapPositionAnalyzer.Analyze(
    0.5, 0, 0.95, positionNow.AddSeconds(-10), mappedDefinition, positionNow,
    TimeSpan.FromSeconds(5), 0.5, 0.75);
Require(stalePosition.State == AgvMapPositionState.Stale,
    "超过定位时效阈值的AGV坐标必须标记为陈旧。");
var changedDefinition = mappedDefinition with
{
    Nodes = mappedDefinition.Nodes.Select(node => node.Id == "A" ? node with { X = 0.5 } : node).ToArray(),
    Resources = [new TrafficResourceDefinition("NARROW-DIFF", TrafficResourceType.NarrowAisle)]
};
var differences = MapVersionDiffer.Compare(mappedDefinition, changedDefinition);
Require(differences.Any(item => item.ObjectType == "节点" && item.ObjectId == "A" && item.Kind == MapDifferenceKind.Modified) &&
        differences.Any(item => item.ObjectType == "交通资源" && item.Kind == MapDifferenceKind.Added),
    "地图版本差异应识别节点修改和交通资源新增。");

var databasePath = Path.Combine(Path.GetTempPath(), $"agv-map-traffic-{Guid.NewGuid():N}.db");
var importPath = Path.Combine(Path.GetTempPath(), $"agv-map-import-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(importPath);
    File.WriteAllText(
        Path.Combine(importPath, "nodes.csv"),
        "Id,X,Y,Type,Enabled,ResourceIds,AgvSiteId\n" +
        "S1,0,0,Station,true,,LM-CNC-001\n" +
        "J1,1000,0,Intersection,true,,LM-JUNCTION-001\n" +
        "S2,2000,0,Station,true,,LM-DUMPER-001\n");
    File.WriteAllText(
        Path.Combine(importPath, "edges.csv"),
        "Id,From,To,Distance,SpeedLimit,Direction,Enabled,ResourceIds\n" +
        "E1,S1,J1,1000,1,Bidirectional,true,NARROW-CSV\n" +
        "E2,J1,S2,1000,1,Bidirectional,true,NARROW-CSV\n");
    File.WriteAllText(
        Path.Combine(importPath, "resources.csv"),
        "Id,Type,Capacity,Boundary\nNARROW-CSV,NarrowAisle,1,\"0:-500|2000:-500|2000:500|0:500\"\n");
    var importer = new MapImportService();
    var validator = new MapValidationService();
    var imported = importer.Import(new MapImportRequest(
        importPath,
        MapImportFormat.CsvDirectory,
        0.001));
    Require(imported.Nodes.Single(node => node.Id == "J1").X == 1,
        "CAD毫米坐标缩放到米失败。");
    Require(imported.Nodes.Single(node => node.Id == "S1").AgvSiteId == "LM-CNC-001",
        "Robokit站点ID导入失败。");
    Require(imported.Resources.Single().Boundary is { Count: 4 } boundary && boundary[1].X == 2,
        "交通资源多边形边界导入和毫米转米失败。");
    var importedReport = validator.Validate(imported);
    Require(importedReport.IsValid, "合法CSV地图包不应产生阻断错误。");
    var invalidBoundaryReport = validator.Validate(imported with
    {
        Resources = [imported.Resources.Single() with
        {
            Boundary = [new MapPoint2D(0, 0), new MapPoint2D(1, 0), new MapPoint2D(2, 0)]
        }]
    });
    Require(invalidBoundaryReport.Issues.Any(issue => issue.Code == "RESOURCE_BOUNDARY_ZERO_AREA"),
        "交通资源共线边界必须被发布校验拦截。");
    var selfIntersectingBoundaryReport = validator.Validate(imported with
    {
        Resources = [imported.Resources.Single() with
        {
            Boundary =
            [
                new MapPoint2D(0, 0), new MapPoint2D(4, 0),
                new MapPoint2D(1, 3), new MapPoint2D(3, 3)
            ]
        }]
    });
    Require(selfIntersectingBoundaryReport.Issues.Any(issue => issue.Code == "RESOURCE_BOUNDARY_SELF_INTERSECTION"),
        "交通资源自交多边形必须被发布校验拦截。");
    var outsideCoverageReport = validator.Validate(imported with
    {
        Nodes = imported.Nodes.Select(node => node.Id == "S1"
            ? node with { X = 10, Y = 10, ResourceIds = ["NARROW-CSV"] }
            : node).ToArray()
    });
    Require(outsideCoverageReport.Issues.Any(issue => issue.Code == "RESOURCE_NODE_OUTSIDE_BOUNDARY"),
        "绑定交通资源但位于多边形外的节点必须产生覆盖告警。");

    var invalidJsonPath = Path.Combine(importPath, "invalid-map.json");
    File.WriteAllText(invalidJsonPath, """
    {
      "name": "InvalidRobokitExport",
      "coordinateSystem": "LocalCartesian",
      "unit": "m",
      "resources": [],
      "nodes": [
        { "id": "A", "x": 0, "y": 0, "type": "Station" },
        { "id": "B", "x": 2, "y": 2, "type": "Station" },
        { "id": "C", "x": 0, "y": 2, "type": "Station" },
        { "id": "D", "x": 2, "y": 0, "type": "Station" }
      ],
      "edges": [
        { "id": "AB", "from": "A", "to": "B", "distance": 2.8, "speedLimit": 1, "bidirectional": false },
        { "id": "CD", "from": "C", "to": "D", "distance": 2.8, "speedLimit": 1, "bidirectional": false }
      ]
    }
    """);
    var invalid = importer.Import(new MapImportRequest(invalidJsonPath));
    var invalidReport = validator.Validate(invalid);
    Require(!invalidReport.IsValid, "断路、错误方向和无保护交叉地图必须校验失败。");
    Require(invalidReport.Issues.Any(issue => issue.Code == "MAP_DISCONNECTED"),
        "未检测到地图断路。");
    Require(invalidReport.Issues.Any(issue => issue.Code == "DIRECTION_UNREACHABLE"),
        "未检测到单行方向不可达。");
    Require(invalidReport.Issues.Any(issue => issue.Code == "EDGE_CROSSING_UNPROTECTED"),
        "未检测到无交点资源保护的几何交叉。");

    var database = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = databasePath });
    await new DatabaseInitializer(database).InitializeAsync(CancellationToken.None);

    var mapAssetPath = Path.Combine(importPath, "underlays");
    var calibration = new MapCalibrationService();
    var underlayQueue = new CadUnderlayJobQueue();
    var activeMapRuntime = new ActiveMapRuntime();
    var activeMapResources = new TrafficResourceCatalog([]);
    var mapGate = new MapExecutionGate();
    var mapSwitches = new MapSwitchService(database, mapGate, activeMapRuntime,
        new SwitchVehicleCache(), new SwitchSignalCache(), new MapSwitchSafetySettings());
    await mapSwitches.InitializeAsync(CancellationToken.None);
    var mapManager = new MapManagementService(
        database,
        validator,
        importer,
        underlayQueue,
        calibration,
        activeMapRuntime,
        activeMapResources,
        new MapAssetStorageOptions
        {
            CachePath = mapAssetPath,
            BackupPath = Path.Combine(importPath, "backups")
        }, mapGate, mapSwitches);
    var mapViewer = new TrafficUserContext(@"FACTORY\map-viewer", "地图观察员", TrafficPermission.View);
    var mapEditor = new TrafficUserContext(
        @"FACTORY\map-editor", "地图管理员", TrafficPermission.View | TrafficPermission.ManageMap);
    var importDenied = false;
    try
    {
        await mapManager.PreviewImportAsync(
            mapViewer,
            new PreviewMapImportRequest(importPath, MapImportFormat.CsvDirectory, 0.001),
            CancellationToken.None);
    }
    catch (UnauthorizedAccessException)
    {
        importDenied = true;
    }
    Require(importDenied, "普通查看账号不能要求后台读取并预览地图包。");
    var importPreview = await mapManager.PreviewImportAsync(
        mapEditor,
        new PreviewMapImportRequest(importPath, MapImportFormat.CsvDirectory, 0.001),
        CancellationToken.None);
    Require(importPreview.Accepted && importPreview.Definition?.Nodes.Count == 3 &&
            importPreview.Validation?.IsValid == true,
        "地图管理员应能通过Service执行只读导入预览和校验。");
    var archivedFixture=await mapManager.CreateAsync(mapEditor,new CreateMapRequest("archive-fixture","", "", "LocalCartesian","m",1),default);
    var archivedWorkspace=archivedFixture.Workspace!;
    var archivedId=archivedWorkspace.Project.MapId;
    Require(!(await mapManager.ArchiveAsync(mapEditor,new(archivedId,true,"stale"),default)).Accepted,"过期编辑令牌不能移除地图。");
    Require((await mapManager.ArchiveAsync(mapEditor,new(archivedId,true,archivedWorkspace.Draft.EditToken),default)).Accepted,"闲置地图可移出目录。");
    Require(!(await mapManager.GetMapsAsync(mapViewer,default)).Any(m=>m.MapId==archivedId)&&
        (await mapManager.GetWorkspaceAsync(mapViewer,archivedId,default))!.Draft.VersionId==archivedWorkspace.Draft.VersionId,"移除隐藏目录但完整保留版本。");
    Require((await mapManager.ArchiveAsync(mapEditor,new(archivedId,false,archivedWorkspace.Draft.EditToken),default)).Accepted,"可恢复已移除地图。");
    await database.Updateable<AgvDispatch.Infrastructure.Persistence.Entities.MapProjectEntity>().SetColumns(m=>new AgvDispatch.Infrastructure.Persistence.Entities.MapProjectEntity{IsActive=true}).Where(m=>m.MapId==archivedId).ExecuteCommandAsync();
    Require(!(await mapManager.ArchiveAsync(mapEditor,new(archivedId,true,archivedWorkspace.Draft.EditToken),default)).Accepted,"运行地图禁止移除。");
    await database.Updateable<AgvDispatch.Infrastructure.Persistence.Entities.MapProjectEntity>().SetColumns(m=>new AgvDispatch.Infrastructure.Persistence.Entities.MapProjectEntity{IsActive=false}).Where(m=>m.MapId==archivedId).ExecuteCommandAsync();
    await mapManager.ArchiveAsync(mapEditor,new(archivedId,true,archivedWorkspace.Draft.EditToken),default);
    var dwgPath = Environment.GetEnvironmentVariable("AGV_TEST_DWG_PATH");
    if (string.IsNullOrWhiteSpace(dwgPath))
    {
        dwgPath = Path.Combine(importPath, "synthetic-map.dwg");
        var fixtureDocument = new ACadSharp.CadDocument();
        fixtureDocument.ModelSpace.Entities.Add(new ACadSharp.Entities.Line(new CSMath.XYZ(0, 0, 0), new CSMath.XYZ(10, 10, 0)));
        ACadSharp.IO.DwgWriter.Write(dwgPath, fixtureDocument);
    }
    var createDenied = false;
    try
    {
        await mapManager.CreateAsync(
            mapViewer,
            new CreateMapRequest("无权地图", "权限测试", dwgPath, "LocalCartesian", "mm", 0.001),
            CancellationToken.None);
    }
    catch (UnauthorizedAccessException)
    {
        createDenied = true;
    }
    Require(createDenied, "只有ManageMap权限才可创建地图。");
    var createdMap = await mapManager.CreateAsync(
        mapEditor,
        new CreateMapRequest("B07-1F", "B07一层AGV调度地图", dwgPath, "LocalCartesian", "mm", 0.001),
        CancellationToken.None);
    Require(createdMap.Accepted && createdMap.Workspace is not null,
        "地图管理员应能创建带DWG底图的地图工作区。");
    Require(createdMap.Workspace!.Project.BackgroundFormat == "DWG-AC1032",
        "应识别本项目DWG为AutoCAD AC1032格式。");
    var underlayResult = await mapManager.GenerateUnderlayAsync(
        mapEditor,
        new GenerateMapUnderlayRequest(createdMap.Workspace.Project.MapId, dwgPath, []),
        CancellationToken.None);
    Require(underlayResult.Accepted &&
            underlayResult.Workspace?.Project.UnderlayStatus == CadUnderlayJobStatus.Pending,
        "复杂DWG转换必须进入后台队列，不能阻塞Named Pipe请求。");
    using (var queuedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
    {
        await using var queuedItems = underlayQueue.ReadAllAsync(queuedTimeout.Token)
            .GetAsyncEnumerator(queuedTimeout.Token);
        Require(await queuedItems.MoveNextAsync(), "CAD后台队列没有收到任务。");
        var queuedJob = queuedItems.Current;
        Require(queuedJob.MapId == createdMap.Workspace.Project.MapId && queuedJob.SourcePath == dwgPath,
            "CAD后台队列应保留地图、源文件和图层选择。");
    }
    var sampleDxf = Path.Combine(importPath, "underlay-sample.dxf");
    File.WriteAllText(sampleDxf, "0\nSECTION\n2\nHEADER\n0\nENDSEC\n0\nSECTION\n2\nTABLES\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nLINE\n8\nAGV_ROUTE\n10\n0\n20\n0\n30\n0\n11\n1000\n21\n1000\n31\n0\n0\nENDSEC\n0\nEOF\n");
    var directRender = await new CadUnderlayService().RenderAsync(
        new CadUnderlayRenderRequest(sampleDxf, mapAssetPath, []),
        CancellationToken.None);
    Require(File.Exists(directRender.PreviewPngPath) && File.Exists(directRender.PreviewSvgPath),
        "CAD渲染器应生成PNG和SVG底图文件。");
    var calibrationResult = await mapManager.SaveCalibrationAsync(
        mapEditor,
        new SaveMapCalibrationRequest(
            createdMap.Workspace.Project.MapId,
            [
                new MapCalibrationPoint(1000, 2000, 1, 2),
                new MapCalibrationPoint(5000, 2000, 5, 2),
                new MapCalibrationPoint(1000, 6000, 1, 6)
            ]),
        CancellationToken.None);
    Require(calibrationResult.Accepted &&
            calibrationResult.Workspace?.Project.Calibration is { Scale: > 0.000999 and < 0.001001,
                RootMeanSquareError: < 0.000001 },
        "三个控制点应计算出稳定的CAD毫米到地图米坐标标定参数。");
    var mapWorkspace = calibrationResult.Workspace!;
    var editableDefinition = imported with
    {
        Name = mapWorkspace.Project.Name,
        Source = dwgPath,
        CoordinateSystem = "LocalCartesian",
        Unit = "m"
    };
    var savedMap = await mapManager.SaveDraftAsync(
        mapEditor,
        new SaveMapDraftRequest(
            mapWorkspace.Project.MapId,
            mapWorkspace.Draft.VersionId,
            mapWorkspace.Project.Name,
            mapWorkspace.Project.Description,
            dwgPath,
            "LocalCartesian",
            "m",
            1,
            editableDefinition,
            mapWorkspace.Draft.EditToken),
        CancellationToken.None);
    Require(savedMap.Accepted && savedMap.Workspace!.Draft.Validation.IsValid,
        "合法拓扑应能保存为可发布草稿。");
    var savedWorkspace = savedMap.Workspace ?? throw new InvalidOperationException("保存地图未返回工作区。");
    var staleSave = await mapManager.SaveDraftAsync(
        mapEditor,
        new SaveMapDraftRequest(
            mapWorkspace.Project.MapId, mapWorkspace.Draft.VersionId, mapWorkspace.Project.Name,
            mapWorkspace.Project.Description, dwgPath, "LocalCartesian", "m", 1,
            editableDefinition, mapWorkspace.Draft.EditToken),
        CancellationToken.None);
    Require(!staleSave.Accepted && staleSave.Message.Contains("其他会话"),
        "旧编辑令牌的会话必须被拒绝，不能覆盖较新的地图草稿。");
    var publishedMap = await mapManager.PublishAsync(
        mapEditor,
        new PublishMapRequest(savedWorkspace.Project.MapId, savedWorkspace.Draft.VersionId),
        CancellationToken.None);
    Require(publishedMap.Accepted &&
            publishedMap.Workspace?.Published?.VersionNumber == 1 &&
            publishedMap.Workspace.Draft.VersionNumber == 2,
        "发布V1后必须固化已发布版本并自动创建V2草稿。");
    var versionHistory = await mapManager.GetVersionHistoryAsync(
        mapViewer, publishedMap.Workspace!.Project.MapId, CancellationToken.None);
    Require(versionHistory.Count == 2 &&
            versionHistory.Any(item => item.VersionNumber == 1 && item.Status == MapVersionStatus.Published) &&
            versionHistory.Any(item => item.VersionNumber == 2 && item.Status == MapVersionStatus.Draft),
        "地图历史应同时返回已发布版本和当前草稿的轻量摘要。");
    var historicalVersion = await mapManager.GetVersionAsync(
        mapViewer,
        publishedMap.Workspace.Project.MapId,
        versionHistory.Single(item => item.VersionNumber == 1).VersionId,
        CancellationToken.None);
    Require(historicalVersion?.Definition.Nodes.Count == editableDefinition.Nodes.Count,
        "按版本ID读取历史地图时必须返回不可变的完整版本快照。");
    var exportedMap = await mapManager.ExportAsync(
        mapEditor,
        new ExportMapRequest(publishedMap.Workspace!.Project.MapId, MapExportVersion.Published),
        CancellationToken.None);
    Require(exportedMap.Accepted && File.Exists(exportedMap.FilePath) &&
            File.Exists(exportedMap.FilePath + ".sha256") && exportedMap.Sha256.Length == 64 &&
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(exportedMap.FilePath)))
                .Equals(exportedMap.Sha256, StringComparison.OrdinalIgnoreCase),
        "地图备份必须由Service生成JSON与SHA-256校验文件，且哈希应与文件内容一致。");
    var restoredPreview = await mapManager.PreviewImportAsync(
        mapEditor,
        new PreviewMapImportRequest(exportedMap.FilePath, MapImportFormat.NormalizedJson, 1),
        CancellationToken.None);
    Require(restoredPreview.Accepted &&
            restoredPreview.Definition?.Nodes.Count == publishedMap.Workspace.Published!.Definition.Nodes.Count &&
            restoredPreview.Definition.Edges.Count == publishedMap.Workspace.Published.Definition.Edges.Count,
        "Service生成的地图备份必须能通过安全的导入预览流程重新读取。正式恢复前不得直接覆盖草稿。");
    Require((await mapSwitches.BeginAsync(mapEditor, new(publishedMap.Workspace!.Project.MapId,
        publishedMap.Workspace.Published!.VersionId), CancellationToken.None)).Accepted, "应先停止新增派单。");
    var activatedMap = await mapManager.ActivateAsync(
        mapEditor,
        new ActivateMapRequest(
            publishedMap.Workspace!.Project.MapId,
            publishedMap.Workspace.Published!.VersionId,
            "ACTIVATE B07-1F", true, true, "离线软件地图测试，现场无车辆与设备动作"),
        CancellationToken.None);
    Require(activatedMap.Accepted && activatedMap.Workspace!.Project.IsActive &&
            activeMapRuntime.Current?.MapId == activatedMap.Workspace.Project.MapId &&
            activeMapResources.TryGet("NARROW-CSV", out _),
        "只有已发布版本可激活，并应原子替换运行路径图与交通资源目录。");
    var switchStatus = await mapSwitches.GetAsync(mapEditor, CancellationToken.None);
    Require(mapGate.Mode == MapExecutionMode.Held, "激活后应保持停止派单。");
    Require((await mapSwitches.ResumeAsync(mapEditor, new(switchStatus.Current!.ReleaseId, true, true,
        "离线软件地图测试确认放行"), CancellationToken.None)).Accepted, "空现场核对后应可放行。");
    var activatedWorkspace = activatedMap.Workspace ?? throw new InvalidOperationException("激活地图未返回工作区。");
    var invalidDraftSave = await mapManager.SaveDraftAsync(
        mapEditor,
        new SaveMapDraftRequest(
            publishedMap.Workspace!.Project.MapId,
            publishedMap.Workspace.Draft.VersionId,
            publishedMap.Workspace.Project.Name,
            publishedMap.Workspace.Project.Description,
            dwgPath,
            "LocalCartesian",
            "m",
            1,
            invalid,
            publishedMap.Workspace.Draft.EditToken),
        CancellationToken.None);
    Require(invalidDraftSave.Accepted && !invalidDraftSave.Workspace!.Draft.Validation.IsValid,
        "含错误的拓扑允许保存草稿，但必须保留校验错误。");
    var invalidWorkspace = invalidDraftSave.Workspace ?? throw new InvalidOperationException("保存非法草稿未返回工作区。");
    var invalidPublish = await mapManager.PublishAsync(
        mapEditor,
        new PublishMapRequest(invalidWorkspace.Project.MapId, invalidWorkspace.Draft.VersionId),
        CancellationToken.None);
    Require(!invalidPublish.Accepted && invalidPublish.Message.Contains("不能发布"),
        "存在地图阻断错误时必须拒绝发布。");
    var secondMap = await mapManager.CreateAsync(
        mapEditor,
        new CreateMapRequest("B07-2F", "多地图测试", string.Empty, "LocalCartesian", "m", 1),
        CancellationToken.None);
    Require(secondMap.Accepted && (await mapManager.GetMapsAsync(mapViewer, CancellationToken.None)).Count == 2,
        "地图模块必须支持多个独立地图项目。");

    var manager = new TrafficResourceManager(
        database,
        new TrafficResourceCatalog(resources),
        new TrafficControlSettings(TimeSpan.FromSeconds(30)));

    var first = await manager.TryAcquireAsync(
        "STEP-1", "TASK-1", "AGV-01", ["NARROW-01"], CancellationToken.None);
    Require(first.Acquired, "首个任务应取得窄道路权。");
    var unsafeActivation = await mapManager.ActivateAsync(
        mapEditor,
        new ActivateMapRequest(
            activatedWorkspace.Project.MapId,
            activatedWorkspace.Published!.VersionId,
            "ACTIVATE B07-1F"),
        CancellationToken.None);
    Require(!unsafeActivation.Accepted && unsafeActivation.Message.Contains("安全切换"),
        "存在任何交通资源租约时必须禁止切换运行地图。");
    var blocked = await manager.TryAcquireAsync(
        "STEP-2", "TASK-2", "AGV-02", ["NARROW-01"], CancellationToken.None);
    Require(!blocked.Acquired && blocked.BlockingTaskId == "TASK-1",
        "另一任务不得同时取得容量为1的窄道路权。");
    var unavailable = await manager.GetUnavailableResourceIdsAsync("TASK-2", CancellationToken.None);
    Require(unavailable.Contains("NARROW-01"), "占用资源必须对其他任务不可用。");

    var handover = await manager.TryAcquireAsync(
        "STEP-3", "TASK-1", "AGV-01", ["NARROW-01", "CROSS-01"], CancellationToken.None);
    Require(handover.Acquired, "同一业务任务应能安全交接到下一运输步骤。");
    await manager.ReleaseTaskExceptStepAsync("TASK-1", "STEP-3", CancellationToken.None);
    Require(await database.Queryable<TrafficResourceLeaseEntity>()
            .CountAsync(item => item.TaskId == "TASK-1") == 2,
        "换段后只应保留新步骤的资源租约。");

    await database.Updateable<TrafficResourceLeaseEntity>()
        .SetColumns(item => new TrafficResourceLeaseEntity { ExpiresAt = DateTime.Now.AddMinutes(-1) })
        .Where(item => item.TaskId == "TASK-1")
        .ExecuteCommandAsync();
    var stillBlocked = await manager.TryAcquireAsync(
        "STEP-4", "TASK-2", "AGV-02", ["NARROW-01"], CancellationToken.None);
    Require(!stillBlocked.Acquired, "租约时间过期不能自动释放实际路权。");

    await database.Insertable(new DispatchTaskEntity
    {
        TaskId = "TASK-1",
        StationId = "CNC001",
        Stage = (int)DispatchTaskStage.Completed,
        Priority = 0,
        CreatedAt = DateTime.Now,
        UpdatedAt = DateTime.Now
    }).ExecuteCommandAsync();
    _ = await manager.GetUnavailableResourceIdsAsync("TASK-2", CancellationToken.None);
    var afterCompletion = await manager.TryAcquireAsync(
        "STEP-4", "TASK-2", "AGV-02", ["NARROW-01"], CancellationToken.None);
    Require(afterCompletion.Acquired, "业务任务完整完成后应释放其全部路权。");

    await database.Insertable(new DispatchTaskEntity
    {
        TaskId = "TASK-2",
        StationId = "CNC002",
        VehicleId = "AGV-02",
        Stage = (int)DispatchTaskStage.GoingToPickup,
        Priority = 0,
        CreatedAt = DateTime.Now,
        UpdatedAt = DateTime.Now
    }).ExecuteCommandAsync();
    await database.Updateable<TrafficResourceLeaseEntity>()
        .SetColumns(item => new TrafficResourceLeaseEntity { ExpiresAt = DateTime.Now.AddSeconds(-1) })
        .Where(item => item.TaskId == "TASK-2")
        .ExecuteCommandAsync();
    var control = new TrafficResourceControlService(
        database,
        new TrafficResourceCatalog(resources));
    var monitored = await control.GetResourcesAsync(CancellationToken.None);
    var monitoredLease = monitored.Single(item => item.ResourceId == "NARROW-01").Leases.Single();
    Require(monitoredLease.HeartbeatExpired, "监控层应标识心跳已过期但仍保留的路权。");
    var workflow = new ManualReleaseWorkflowService(
        database,
        new ManualReleaseWorkflowSettings(TimeSpan.FromMinutes(15)));
    var submitter = new TrafficUserContext(
        @"FACTORY\operator-a", "操作员甲",
        TrafficPermission.View | TrafficPermission.SubmitManualRelease);
    var approver = new TrafficUserContext(
        @"FACTORY\safety-b", "安全员乙",
        TrafficPermission.View | TrafficPermission.ApproveManualRelease);
    var viewer = new TrafficUserContext(@"FACTORY\viewer", "观察员", TrafficPermission.View);
    var activeRelease = await workflow.SubmitAsync(
        submitter,
        new SubmitManualReleaseRequest(
            monitoredLease.LeaseId,
            "现场确认后尝试释放",
            true,
            "RELEASE NARROW-01"),
        CancellationToken.None);
    Require(!activeRelease.Accepted && activeRelease.Message.Contains("禁止"),
        "活动任务路权必须拒绝人工释放。");

    await database.Updateable<DispatchTaskEntity>()
        .SetColumns(item => new DispatchTaskEntity
        {
            Stage = (int)DispatchTaskStage.Faulted,
            UpdatedAt = DateTime.Now
        })
        .Where(item => item.TaskId == "TASK-2")
        .ExecuteCommandAsync();
    var missingConfirmation = await workflow.SubmitAsync(
        submitter,
        new SubmitManualReleaseRequest(
            monitoredLease.LeaseId,
            "故障车辆已经人工移出",
            false,
            "RELEASE NARROW-01"),
        CancellationToken.None);
    Require(!missingConfirmation.Accepted, "未确认车辆离开时必须拒绝人工释放。");
    var unauthorizedRejected = false;
    try
    {
        await workflow.SubmitAsync(viewer,
            new SubmitManualReleaseRequest(monitoredLease.LeaseId, "车辆已离开现场确认", true, "RELEASE NARROW-01"),
            CancellationToken.None);
    }
    catch (UnauthorizedAccessException) { unauthorizedRejected = true; }
    Require(unauthorizedRejected, "无解锁权限的账号必须拒绝。");
    var incorrectText = await workflow.SubmitAsync(submitter,
        new SubmitManualReleaseRequest(monitoredLease.LeaseId, "故障车辆已经人工移出", true, "RELEASE WRONG"),
        CancellationToken.None);
    Require(!incorrectText.Accepted, "错误资源确认文本不得执行解锁。");
    // Force a persistence failure after deletion: both deletion and audit must roll back.
    await database.Ado.ExecuteCommandAsync(
        "CREATE TRIGGER block_release_history BEFORE INSERT ON manual_traffic_release_request BEGIN SELECT RAISE(ABORT, 'test rollback'); END;");
    var rolledBack = false;
    try
    {
        await workflow.SubmitAsync(submitter,
            new SubmitManualReleaseRequest(monitoredLease.LeaseId, "故障车辆已经人工移出", true, "RELEASE NARROW-01"),
            CancellationToken.None);
    }
    catch (InvalidOperationException) { rolledBack = true; }
    Require(rolledBack && await database.Queryable<TrafficResourceLeaseEntity>()
        .Where(x => x.LeaseId == monitoredLease.LeaseId).AnyAsync(), "保存失败时路权删除必须回滚。");
    Require(await database.Queryable<TrafficResourceReleaseAuditEntity>().CountAsync() == 0,
        "保存失败时审计也必须回滚。");
    await database.Ado.ExecuteCommandAsync("DROP TRIGGER block_release_history;");
    var submitted = await workflow.SubmitAsync(submitter,
        new SubmitManualReleaseRequest(monitoredLease.LeaseId, "故障车辆已经人工移出", true, "RELEASE NARROW-01"),
        CancellationToken.None);
    Require(submitted.Accepted && submitted.RequestId is not null, "单人授权应立即执行终态任务解锁。");
    Require(!await database.Queryable<TrafficResourceLeaseEntity>().Where(x => x.LeaseId == monitoredLease.LeaseId).AnyAsync(),
        "解锁成功后租约应已删除，无需第二人批准。");
    var duplicate = await workflow.SubmitAsync(submitter,
        new SubmitManualReleaseRequest(monitoredLease.LeaseId, "故障车辆已经人工移出", true, "RELEASE NARROW-01"),
        CancellationToken.None);
    Require(!duplicate.Accepted, "重复解锁不得产生第二条审计。");
    var legacy = await workflow.ReviewAsync(approver,
        new ReviewManualReleaseRequest(submitted.RequestId!, true, "旧审批"), CancellationToken.None);
    Require(!legacy.Accepted && legacy.Message.Contains("停用"), "兼容审批接口不得再执行解锁。");
    var workflowRows = await workflow.GetRequestsAsync(submitter, 10, CancellationToken.None);
    Require(workflowRows.Single(item => item.RequestId == submitted.RequestId).Status == ManualReleaseWorkflowStatus.Executed,
        "单人解锁历史应直接为Executed。");
    var audits = await control.GetRecentAuditsAsync(10, CancellationToken.None);
    Require(audits.Count == 1 && audits[0].Operator == "操作员甲" && audits[0].HeartbeatExpired &&
        audits[0].RequestedBy == submitter.WindowsIdentity && audits[0].ApprovedBy is null,
        "审计应记录实际单人操作员，不应伪造批准人。");
    Require((await manager.TryAcquireAsync(
        "STEP-ORPHAN", "TASK-ORPHAN", "AGV-03", ["CROSS-01"], CancellationToken.None)).Acquired,
        "应能建立用于任务记录缺失校验的测试租约。");
    var orphanLease = (await control.GetResourcesAsync(CancellationToken.None))
        .Single(item => item.ResourceId == "CROSS-01").Leases.Single();
    var orphanRelease = await workflow.SubmitAsync(
        submitter,
        new SubmitManualReleaseRequest(
            orphanLease.LeaseId,
            "任务记录缺失的异常测试",
            true,
            "RELEASE CROSS-01"),
        CancellationToken.None);
    Require(!orphanRelease.Accepted && orphanRelease.Message.Contains("无法证明"),
        "任务记录缺失时必须拒绝人工释放。");

    var alarmService = new AlarmService(database);
    var alarmCandidate = new AlarmCandidate(
        "AGV:AGV-99:CONNECTION",
        "AGV",
        "AGV-99",
        "AGV_CONNECTION",
        "测试AGV连接中断",
        "用于验证报警发生、确认和恢复闭环。",
        AlarmSeverity.Critical);
    var secondAlarmCandidate = new AlarmCandidate(
        "PLC:PLC-TEST:QUALITY",
        "PLC",
        "PLC-TEST",
        "PLC_QUALITY",
        "测试PLC质量异常",
        "用于验证告警查询的级别、状态和时间筛选。",
        AlarmSeverity.Warning);
    var alarmOperator = new TrafficUserContext(
        @"FACTORY\alarm-operator", "报警处置员",
        TrafficPermission.View | TrafficPermission.AcknowledgeAlarm);
    var alarmObservedAt = DateTimeOffset.Now;
    await alarmService.SynchronizeAsync([alarmCandidate, secondAlarmCandidate], alarmObservedAt, CancellationToken.None);
    await alarmService.SynchronizeAsync([alarmCandidate, secondAlarmCandidate], alarmObservedAt, CancellationToken.None);
    var activeAlarms = await alarmService.GetRecentAsync(10, true, CancellationToken.None);
    Require(activeAlarms.Count == 2 && activeAlarms.All(item => item.Status == AlarmLifecycleStatus.Open),
        "不同报警规则应分别存在，同一规则连续命中必须去重为一条活动报警。");
    var alarmId = activeAlarms.Single(item => item.RuleKey == alarmCandidate.RuleKey).AlarmId;
    var alarmQuery = await alarmService.GetPageAsync(
        alarmOperator,
        new AlarmListQuery(
            "AGV-99",
            "all",
            "all",
            alarmObservedAt.AddHours(-1),
            alarmObservedAt.AddHours(1),
            1,
            20),
        CancellationToken.None);
    Require(alarmQuery.TotalCount == 1 &&
            alarmQuery.Items.Single().AlarmId == alarmId &&
            alarmQuery.Summary is { Total: 1, Open: 1, Acknowledged: 0, Recovered: 0, Critical: 1 },
        "报警分页查询必须应用关键词、首次发生时间并返回全量摘要。");
    var criticalOpenQuery = await alarmService.GetPageAsync(
        alarmOperator,
        new AlarmListQuery("AGV", "open", "critical", null, null, 1, 20),
        CancellationToken.None);
    Require(criticalOpenQuery.TotalCount == 1 &&
            criticalOpenQuery.Items.Single().AlarmId == alarmId &&
            criticalOpenQuery.Summary.Total == 1 &&
            criticalOpenQuery.Summary.Open == 1,
        "报警分页查询必须组合状态和严重级别筛选，且摘要不受列表筛选影响。");
    var alarmUnauthorized = false;
    try
    {
        await alarmService.AcknowledgeAsync(
            viewer,
            new AcknowledgeAlarmRequest(alarmId, "无权确认"),
            CancellationToken.None);
    }
    catch (UnauthorizedAccessException)
    {
        alarmUnauthorized = true;
    }
    Require(alarmUnauthorized, "无报警确认权限的账号必须被拒绝。");
    var acknowledged = await alarmService.AcknowledgeAsync(
        alarmOperator,
        new AcknowledgeAlarmRequest(alarmId, "已经联系现场处理"),
        CancellationToken.None);
    Require(acknowledged.Accepted, "有权限的账号应能确认开放报警。");
    await alarmService.SynchronizeAsync([alarmCandidate, secondAlarmCandidate], alarmObservedAt.AddMinutes(1), CancellationToken.None);
    activeAlarms = await alarmService.GetRecentAsync(10, true, CancellationToken.None);
    Require(activeAlarms.Single(item => item.AlarmId == alarmId).Status == AlarmLifecycleStatus.Acknowledged,
        "报警条件仍存在时确认状态必须保持，不能伪装成已恢复。");
    await alarmService.SynchronizeAsync([secondAlarmCandidate], alarmObservedAt.AddMinutes(2), CancellationToken.None);
    Require((await alarmService.GetRecentAsync(10, true, CancellationToken.None)).Count == 1,
        "单个报警条件消失时只应恢复对应规则，其他活动报警保持。");
    var alarmHistory = await alarmService.GetRecentAsync(10, false, CancellationToken.None);
    Require(alarmHistory.Single(item => item.AlarmId == alarmId).Status == AlarmLifecycleStatus.Recovered,
        "报警条件消失后历史记录必须标记为Recovered。");
    var recoveredQuery = await alarmService.GetPageAsync(
        alarmOperator,
        new AlarmListQuery("AGV-99", "recovered", "critical", null, null, 1, 20),
        CancellationToken.None);
    Require(recoveredQuery.TotalCount == 1 &&
            recoveredQuery.Items.Single().AlarmId == alarmId &&
            recoveredQuery.Summary is { Total: 1, Open: 0, Acknowledged: 0, Recovered: 1, Critical: 1 },
        "已恢复报警应可通过组合筛选查询，摘要应反映恢复后的生命周期状态。");

    var trackStore = new AgvTrackStore(database);
    var trackNow = DateTimeOffset.Now;
    var activeMapId = activatedWorkspace.Project.MapId;
    var activeVersionId = activatedWorkspace.Project.ActiveVersionId!;
    await trackStore.AppendAsync(new AgvTrackPointSnapshot(
        0, "AGV-01", activeMapId, activeVersionId, 0, 0, 0, 0.99,
        AgvMapPositionState.Normal, 0, "AX", trackNow.AddDays(-40)), CancellationToken.None);
    await trackStore.AppendAsync(new AgvTrackPointSnapshot(
        0, "AGV-01", activeMapId, activeVersionId, 1, 0, 0, 0.99,
        AgvMapPositionState.Normal, 0, "AX", trackNow.AddSeconds(-2)), CancellationToken.None);
    await trackStore.AppendAsync(new AgvTrackPointSnapshot(
        0, "AGV-01", activeMapId, activeVersionId, 1, 2, 0, 0.99,
        AgvMapPositionState.OffRoute, 2, "AX", trackNow.AddSeconds(-1)), CancellationToken.None);
    var queriedTrack = await trackStore.QueryAsync(new AgvTrackQueryRequest(
        "AGV-01", activeMapId, trackNow.AddMinutes(-1), trackNow, 100), CancellationToken.None);
    Require(queriedTrack.Count == 2 && queriedTrack[0].SampledAt <= queriedTrack[1].SampledAt &&
            queriedTrack[1].PositionState == AgvMapPositionState.OffRoute,
        "轨迹查询必须按时间升序返回并保留定位判定。 ");
    Require(await trackStore.DeleteOlderThanAsync(trackNow.AddDays(-30), CancellationToken.None) == 1,
        "轨迹保留策略必须清理超过期限的数据。");

    var mapAudits = await mapManager.GetAuditsAsync(
        mapViewer, activatedWorkspace.Project.MapId, 100, CancellationToken.None);
    Require(mapAudits.Any(item => item.Operation == "SaveDraft" && !item.Success) &&
            mapAudits.Any(item => item.Operation == "Publish" && item.Success) &&
            mapAudits.Any(item => item.Operation == "Activate" && item.Success) &&
            mapAudits.Any(item => item.Operation == "Export" && item.Success),
        "地图保存冲突、发布、激活和备份导出必须形成可查询的持久化审计记录。");

    database.Ado.Close();
    database.Ado.Connection.Dispose();
    database.Dispose();
    SqliteConnection.ClearAllPools();
    Console.WriteLine("PASS: CadUnderlayQueue + MapCalibration + ActivePublishedMap + EditableMultiMap + MapImportPreview + MapVersionHistory + MapVersionDiff + ResourceBoundary + LiveAgvMapProjection + AgvTrackHistory + DynamicTrafficPenalty + DeploymentPreflight + DraftConcurrency + MapBackupSha256 + MapOperationAudit + MapVersionPublish + MapValidation + RoutePlanner + TrafficResourceMonitor + ManualReleaseWorkflow + AlarmLifecycle");
}
finally
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    foreach (var path in new[] { databasePath, databasePath + "-shm", databasePath + "-wal" })
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
    try { if (Directory.Exists(importPath)) Directory.Delete(importPath, true); }
    catch (IOException) { }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
