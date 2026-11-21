using System.Text.Json;
using System.Text.Json.Serialization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class MapGeometrySmokeTests
{
    public static async Task RunAsync()
    {
        var start = Node("A", 0, 0);
        var end = Node("D", 4, 0);
        var line = Edge("LINE", "A", "D");
        Check(MapEdgeGeometry.Length(line, start, end) == 4, "legacy line geometry");
        var bezier = Edge("BEZIER", "A", "D") with
        {
            GeometryKind = MapEdgeGeometryKind.CubicBezier,
            ControlPoints = [new(0, 4), new(4, 4)]
        };
        Near(MapEdgeGeometry.PointAt(bezier, start, end, 0.5), new(2, 3), "Bezier midpoint");
        Check(Math.Abs(MapEdgeGeometry.Length(bezier, start, end) - 8) < 0.001, "Bezier arc length");
        Check(MapEdgeGeometry.DistanceTo(bezier, start, end, 2, 3) < 0.001, "on-Bezier distance");
        Check(MapEdgeGeometry.DistanceTo(bezier, start, end, 2, 0) > 1.9, "Bezier distance must not use chord");

        var arcEnd = Node("B", 2, 0);
        var arc = Edge("ARC", "A", "B") with
        {
            GeometryKind = MapEdgeGeometryKind.CircularArc,
            ControlPoints = [new(1, 1)]
        };
        Near(MapEdgeGeometry.PointAt(arc, start, arcEnd, 0.5), new(1, 1), "arc through point");
        Check(Math.Abs(MapEdgeGeometry.Length(arc, start, arcEnd) - Math.PI) < 1e-10, "exact circular arc length");
        Check(MapEdgeGeometry.DistanceTo(arc, start, arcEnd, 1, 1) < 1e-10, "on-arc distance");
        Check(Math.Abs(MapEdgeGeometry.DistanceTo(arc, start, arcEnd, 1, -1) - Math.Sqrt(2)) < 1e-10,
            "point outside sweep must project to endpoint");
        var majorStart = Node("MA", 1, 0);
        var majorEnd = Node("MB", 0, 1);
        var majorArc = arc with { From = "MA", To = "MB", ControlPoints = [new(-1, 0)] };
        Check(Math.Abs(MapEdgeGeometry.Length(majorArc, majorStart, majorEnd) - 1.5 * Math.PI) < 1e-10,
            "major circular arc sweep");
        foreach (var fixture in new[] { (bezier, start, end), (arc, start, arcEnd), (majorArc, majorStart, majorEnd) })
            CheckSplitAndReverse(fixture.Item1, fixture.Item2, fixture.Item3);

        var definition = Definition([start, end], [bezier]);
        var now = DateTimeOffset.Now;
        var onCurve = AgvMapPositionAnalyzer.Analyze(2, 3, 0.99, now, definition, now, TimeSpan.FromSeconds(5), 0.5, 0.1);
        var onChord = AgvMapPositionAnalyzer.Analyze(2, 0, 0.99, now, definition, now, TimeSpan.FromSeconds(5), 0.5, 0.1);
        Check(onCurve.State == AgvMapPositionState.Normal && onChord.State == AgvMapPositionState.OffRoute,
            "AGV localization follows actual curve");
        var published = Definition([start, end, arcEnd],
            [line, line with { Id = "LINE-TIE" }, bezier, arc]);
        var stale = TimeSpan.FromSeconds(5);
        for (var x = -1d; x <= 5; x += 0.25)
        for (var y = -1d; y <= 4; y += 0.25)
        {
            var fullScan = AgvMapPositionAnalyzer.Analyze(x, y, 0.99, now, published, now, stale, 0.5, 0.1);
            var indexed = AgvMapPositionAnalyzer.Analyze(x, y, 0.99, now, published, now, stale, 0.5, 0.1, reuseGeometry: true);
            Check(indexed == fullScan, "published geometry preserves distance, state and map-order ties");
        }
        var replacement = published with { Edges = [published.Edges[1], published.Edges[0], bezier, arc] };
        Check(AgvMapPositionAnalyzer.Analyze(2, 0, 0.99, now, replacement, now, stale, 0.5, 0.1, reuseGeometry: true)
                .NearestEdgeId == "LINE-TIE",
            "a replacement map never inherits the previous geometry index");
        Check(AgvMapPositionAnalyzer.Analyze(2, 0, 0.99, now - stale - stale, published, now, stale, 0.5, 0.1, reuseGeometry: true)
                .State == AgvMapPositionState.Stale,
            "reusing geometry does not reuse vehicle feedback");
        var routeGraph = Definition([start, end, Node("X", 2, -1)],
            [bezier with { Distance = 0.01 }, Edge("AX", "A", "X") with { Distance = Math.Sqrt(5) },
             Edge("XD", "X", "D") with { Distance = Math.Sqrt(5) }]).ToGraph();
        Check(routeGraph.FindShortestRoute("A", "D")?.Nodes.SequenceEqual(new[] { "A", "X", "D" }) == true,
            "routing uses curve length even when persisted distance is stale");

        var validator = new MapValidationService();
        Check(validator.Validate(definition).IsValid, "valid Bezier map");
        var invalidArc = arc with { ControlPoints = [new(1, 0)] };
        Check(validator.Validate(Definition([start, arcEnd], [invalidArc])).Issues.Any(issue => issue.Code == "EDGE_GEOMETRY_INVALID"),
            "collinear circular arc is blocked");
        Check(!MapEdgeGeometry.TryValidate(bezier with { ControlPoints = [new(0, 4)] }, start, end, out _),
            "missing Bezier control point is rejected");
        Check(!MapEdgeGeometry.TryValidate(line with { GeometryKind = (MapEdgeGeometryKind)999 }, start, end, out _),
            "unknown geometry enum is rejected");
        var extremeBezier = bezier with { ControlPoints = [new(-1e308, 1e308), new(1e308, -1e308)] };
        Check(!MapEdgeGeometry.TryValidate(extremeBezier, start, end, out _), "overflowing control points are rejected before sampling");
        var extremeSamplingRejected = false;
        try { _ = MapEdgeGeometry.Sample(extremeBezier, start, end); }
        catch (InvalidOperationException) { extremeSamplingRejected = true; }
        Check(extremeSamplingRejected, "extreme curve sampling reports invalid geometry instead of NaN");

        var crossing = Definition([start, end, Node("C", -1, 2), Node("E", 5, 2)],
            [bezier, Edge("CROSS", "C", "E") with { Distance = 6 }, Edge("CONNECT", "A", "C") with { Distance = Math.Sqrt(5) }]);
        Check(validator.Validate(crossing).Issues.Any(issue => issue.Code == "EDGE_CROSSING_UNPROTECTED" &&
                issue.ObjectIds.Contains("BEZIER") && issue.ObjectIds.Contains("CROSS")),
            "curve crossing outside the endpoint chord is blocked");
        var curvedZone = definition with
        {
            Edges = [bezier with { ResourceIds = ["ZONE"] }],
            Resources = [new("ZONE", TrafficResourceType.SingleLane, 1, [new(1, -1), new(3, -1), new(3, 1), new(1, 1)])]
        };
        Check(validator.Validate(curvedZone).Issues.Any(issue => issue.Code == "RESOURCE_EDGE_OUTSIDE_BOUNDARY"),
            "traffic boundary coverage evaluates the curve midpoint");
        var alternativeCurves = definition with { Edges = [line with { Distance = 4 }, bezier] };
        Check(!validator.Validate(alternativeCurves).Issues.Any(issue => issue.Code == "EDGE_OVERLAP"),
            "different paths between the same stations are retained");
        Check(validator.Validate(alternativeCurves).Issues.Any(issue => issue.Code == "EDGE_PARALLEL_GEOMETRY"),
            "site-sequence navigation blocks same-direction parallel geometry before publication");
        var oppositeLanes = definition with
        {
            Edges = [bezier with { Bidirectional = false },
                MapEdgeGeometry.Reverse(bezier) with { Id = "REVERSE-LANE", Bidirectional = false, ControlPoints = [new(4, -4), new(0, -4)] }]
        };
        Check(validator.Validate(oppositeLanes).IsValid, "distinct opposite-direction single lanes are valid");
        Check(MapVersionDiffer.Compare(definition, definition with
            { Edges = [bezier with { ControlPoints = [new(0, 5), new(4, 4)] }] })
            .Any(item => item.ObjectType == "路段" && item.Kind == MapDifferenceKind.Modified),
            "version diff includes control-point-only edits");

        CheckImports();
        CheckInlineConfiguration();
        CheckExampleMap();
        await CheckMapPersistenceAsync();
        await CheckIndependentCadLayersAsync();
        await CheckSplineCadFrameAsync();
        Console.WriteLine("PASS: Line + CircularArc + CubicBezier geometry, split/reverse, routing, import scaling, inline configuration, SQLite multi-project/publish/reconnect, independent CAD layers, validation and localization");
    }

    private static void CheckInlineConfiguration()
    {
        const string json = """
            {
              "Map": {
                "Enabled": true,
                "Nodes": [
                  { "Id": "A", "X": 0, "Y": 0 },
                  { "Id": "B", "X": 2, "Y": 0 },
                  { "Id": "D", "X": 6, "Y": 0 }
                ],
                "Edges": [
                  { "Id": "ARC", "From": "A", "To": "B", "Distance": 0,
                    "GeometryKind": "CircularArc", "ControlPoints": [{ "X": 1, "Y": 1 }], "Bidirectional": true },
                  { "Id": "BEZIER", "From": "B", "To": "D", "Distance": 0,
                    "GeometryKind": "CubicBezier", "ControlPoints": [{ "X": 2, "Y": 4 }, { "X": 6, "Y": 4 }], "Bidirectional": true },
                  { "Id": "LEGACY", "From": "A", "To": "D", "Distance": 42, "Bidirectional": true }
                ]
              }
            }
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        using var configurationLifetime = configuration as IDisposable;
        var options = new MapOptions();
        configuration.GetSection(MapOptions.SectionName).Bind(options);
        Check(options.GetConfigurationError() is null, "configuration Binder supports plain control-point options");
        var definition = options.BuildDefinition();
        var arc = definition.Edges.Single(edge => edge.Id == "ARC");
        var bezier = definition.Edges.Single(edge => edge.Id == "BEZIER");
        Check(arc.GeometryKind == MapEdgeGeometryKind.CircularArc && Math.Abs(arc.Distance - Math.PI) < 1e-10,
            "inline circular arc definition and actual distance");
        Check(bezier.GeometryKind == MapEdgeGeometryKind.CubicBezier && Math.Abs(bezier.Distance - 8) < 0.001,
            "inline Bezier definition and actual distance");
        Near(bezier.ControlPoints![1], new(6, 4), "inline configuration retains Bezier control points");
        Check(definition.Edges.Single(edge => edge.Id == "LEGACY").Distance == 42,
            "inline legacy line preserves configured routing distance");
    }

    private static void CheckExampleMap()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "curved-map.example.json");
        var example = new MapImportService().Import(new MapImportRequest(path));
        Check(example.Unit == "m" && example.Nodes.Count == 3 && example.Edges.Count == 2,
            "curved map example has three nodes and two meter-based paths");
        Check(example.Edges.Any(edge => edge.GeometryKind == MapEdgeGeometryKind.CircularArc) &&
              example.Edges.Any(edge => edge.GeometryKind == MapEdgeGeometryKind.CubicBezier), "curved example kinds");
        Check(new MapValidationService().Validate(example).IsValid, "curved example has no non-node crossing");
        Check(MapDeploymentPreflightAnalyzer.Analyze(example).Ready, "curved example passes site-mapping and route preflight");
    }

    private static async Task CheckMapPersistenceAsync()
    {
        var fixtureFolder = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(fixtureFolder);
        var databasePath = Path.Combine(fixtureFolder, $"geometry-persistence-{Guid.NewGuid():N}.db");
        var databaseOptions = new DatabaseOptions { DatabasePath = databasePath };
        var user = new TrafficUserContext(@"TEST\geometry-editor", "地图几何测试", TrafficPermission.View | TrafficPermission.ManageMap);
        var example = new MapImportService().Import(new MapImportRequest(Path.Combine(AppContext.BaseDirectory, "curved-map.example.json")));
        var definition = example with
        {
            Layers = [new("navigation", "主导航"), new("work", "装配站点", false)],
            EditorSettings = new(80, 40, 0.25, true, true, true),
            Nodes = [.. example.Nodes, new("LEGACY", 9, 0, MapNodeType.Waypoint, true, []) { LayerId = "work" }],
            Edges = [.. example.Edges, Edge("LEGACY-LINE", "END", "LEGACY") with { Distance = 42 }]
        };
        string mapId, publishedVersionId;
        MapManagementService Manager(SqlSugar.ISqlSugarClient database)
        {
            var runtime = new ActiveMapRuntime();
            var gate = new MapExecutionGate();
            return new(database, new MapValidationService(), new MapImportService(), new CadUnderlayJobQueue(),
                new MapCalibrationService(), runtime, new TrafficResourceCatalog([]),
                new MapAssetStorageOptions { CachePath = fixtureFolder, BackupPath = fixtureFolder }, gate,
                new MapSwitchService(database, gate, runtime, new SwitchVehicleCache(), new SwitchSignalCache(), new MapSwitchSafetySettings()));
        }
        try
        {
            using (var database = DatabaseFactory.Create(databaseOptions))
            {
                await new DatabaseInitializer(database).InitializeAsync(CancellationToken.None);
                var manager = Manager(database);
                var otherProject = await manager.CreateAsync(user,
                    new CreateMapRequest("客户乙 / 二楼", "独立项目", "", "LocalCartesian", "mm", 0.001), CancellationToken.None);
                Check(otherProject.Accepted && otherProject.Workspace!.Project.Name == "客户乙 / 二楼" &&
                    otherProject.Workspace.Draft.Definition.Nodes.Count == 0 && otherProject.Workspace.Project.CadLayers.Count == 0 &&
                    otherProject.Workspace.Project.BackgroundFilePath == "" && otherProject.Workspace.Project.CoordinateScale == 0.001,
                    "arbitrary project name and units create independent empty map without site defaults");
                var replacementCad = Path.Combine(fixtureFolder, "customer-layout.dxf");
                await File.WriteAllTextAsync(replacementCad, "0\nEOF\n");
                try
                {
                    var otherId = otherProject.Workspace!.Project.MapId;
                    await database.Updateable<MapProjectEntity>().SetColumns(row => new MapProjectEntity
                    {
                        BackgroundFilePath = "previous-layout.dxf", PreviewPngPath = "old-preview.png",
                        PreviewSvgPath = "old-preview.svg", CadLayersJson = "[]", CalibrationJson = "{}",
                        CadViewBoxJson = "{}", UnderlayStatus = (int)CadUnderlayJobStatus.Ready
                    }).Where(row => row.MapId == otherId).ExecuteCommandAsync();
                    var changed = await manager.GenerateUnderlayAsync(user,
                        new GenerateMapUnderlayRequest(otherId, replacementCad, ["OLD-SITE-LAYER"]), CancellationToken.None);
                    Check(changed.Accepted && changed.Workspace!.Project.PreviewPngPath == "" &&
                        changed.Workspace.Project.Calibration is null && changed.Workspace.Project.CadViewBox is null,
                        "direct CAD replacement clears stale preview and coordinate calibration");
                    var changedRow = await database.Queryable<MapProjectEntity>().Where(row => row.MapId == otherId).FirstAsync();
                    Check(changedRow.UnderlayIncludedLayersJson == "[]" && changedRow.CadLayersJson == "[]",
                        "new CAD cannot inherit previous file layer selection");
                }
                finally { File.Delete(replacementCad); }
                var created = await manager.CreateAsync(user,
                    new CreateMapRequest("Persisted Curves", "曲线数据库回归", "", "LocalCartesian", "m", 1),
                    CancellationToken.None);
                Check(created.Accepted && created.Workspace is not null, "create isolated SQLite map workspace");
                var workspace = created.Workspace!;
                var saved = await manager.SaveDraftAsync(user,
                    new SaveMapDraftRequest(workspace.Project.MapId, workspace.Draft.VersionId,
                        workspace.Project.Name, workspace.Project.Description, "", "LocalCartesian", "m", 1,
                        definition, workspace.Draft.EditToken), CancellationToken.None);
                Check(saved.Accepted && saved.Workspace!.Draft.Validation.IsValid, "save curve draft to SQLite");
                var published = await manager.PublishAsync(user,
                    new PublishMapRequest(workspace.Project.MapId, workspace.Draft.VersionId), CancellationToken.None);
                Check(published.Accepted && published.Workspace?.Published is not null, "publish persisted curve draft");
                mapId = workspace.Project.MapId;
                publishedVersionId = published.Workspace!.Published!.VersionId;
                var entity = await database.Queryable<MapVersionEntity>()
                    .Where(row => row.VersionId == publishedVersionId).FirstAsync();
                using var serialized = JsonDocument.Parse(entity.DefinitionJson);
                var storedEdges = serialized.RootElement.GetProperty("edges");
                Check(storedEdges[0].GetProperty("geometryKind").GetString() == "CircularArc" &&
                      storedEdges[0].GetProperty("controlPoints").GetArrayLength() == 1 &&
                      storedEdges[1].GetProperty("geometryKind").GetString() == "CubicBezier" &&
                      storedEdges[1].GetProperty("controlPoints").GetArrayLength() == 2,
                    "published MapVersionEntity.DefinitionJson stores both curve kinds and control-point arrays");
            }
            using (var reconnectedDatabase = DatabaseFactory.Create(databaseOptions))
            {
                var reconnectedManager = Manager(reconnectedDatabase);
                var workspace = await reconnectedManager.GetWorkspaceAsync(user, mapId, CancellationToken.None);
                Check(workspace?.Published?.VersionId == publishedVersionId && workspace.Draft.VersionNumber == 2,
                    "fresh manager and database connection read published and next-draft versions");
                var persisted = workspace!.Published!.Definition;
                var layerBackup = Path.Combine(fixtureFolder, "layer-backup-" + Guid.NewGuid().ToString("N") + ".json");
                await File.WriteAllTextAsync(layerBackup, JsonSerializer.Serialize(persisted));
                var layerImported = new MapImportService().Import(new MapImportRequest(layerBackup));
                Check(layerImported.Layers.SequenceEqual(persisted.Layers) && layerImported.EditorSettings == persisted.EditorSettings &&
                    layerImported.Nodes.Single(node => node.Id == "LEGACY").LayerId == "work", "JSON import retains layer metadata and object membership");
                File.Delete(layerBackup);
                Check(persisted.Layers.SequenceEqual(definition.Layers) && persisted.EditorSettings == definition.EditorSettings,
                    "SQLite publish and new connection retain named layers and grid dimensions");
                Check(persisted.Nodes.Single(node => node.Id == "LEGACY").LayerId == "work" &&
                    persisted.ToGraph().FindShortestRoute("END", "LEGACY") is not null, "hidden layer membership persists without removing navigable nodes");
                var byId = persisted.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
                foreach (var edge in definition.Edges)
                {
                    var restored = persisted.Edges.Single(item => item.Id == edge.Id);
                    Check(restored.GeometryKind == edge.GeometryKind &&
                          (restored.ControlPoints ?? []).SequenceEqual(edge.ControlPoints ?? []),
                        "reconnected published map retains ordered curve controls");
                    Check(Math.Abs(restored.Distance - edge.Distance) < 1e-10, "persisted routing distance");
                    Check(Math.Abs(MapEdgeGeometry.Length(restored, byId[restored.From], byId[restored.To]) -
                                   MapEdgeGeometry.Length(edge, byId[edge.From], byId[edge.To])) < 1e-10,
                        "reconnected curve retains geometric length");
                }
                Check(MapVersionDiffer.Compare(definition, persisted).Count == 0 &&
                      MapVersionDiffer.Compare(persisted, workspace.Draft.Definition).Count == 0,
                    "publication and next-draft cloning retain complete geometry fingerprints");
                var changed = persisted with
                {
                    Edges = persisted.Edges.Select(edge => edge.GeometryKind == MapEdgeGeometryKind.CubicBezier
                        ? edge with { ControlPoints = [new(4, 3), new(8, 2)] } : edge).ToArray()
                };
                Check(MapVersionDiffer.Compare(persisted, changed).Any(item =>
                    item.ObjectId == "BEZIER-01" && item.Kind == MapDifferenceKind.Modified),
                    "control-point-only change is detected after SQLite reconnection");
                var route = persisted.ToGraph().FindShortestRoute("START", "LEGACY");
                Check(route?.EdgeIds.SequenceEqual(new[] { "ARC-01", "BEZIER-01", "LEGACY-LINE" }) == true,
                    "reconnected published curves retain complete route topology");
                var expectedCost = definition.Edges.Sum(edge => edge.Distance / edge.SpeedLimit);
                Check(Math.Abs(route!.Cost - expectedCost) < 1e-8, "reconnected route retains curve costs and legacy explicit line distance");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void CheckSplitAndReverse(MapEdge edge, MapNode start, MapNode end)
    {
        var reversed = MapEdgeGeometry.Reverse(edge);
        for (var i = 0; i <= 10; i++)
            Near(MapEdgeGeometry.PointAt(reversed, end, start, i / 10d),
                MapEdgeGeometry.PointAt(edge, start, end, 1 - i / 10d), "reverse preserves shape");
        var split = MapEdgeGeometry.Split(edge, start, end, 0.3);
        var middle = Node("MID", split.SplitPoint.X, split.SplitPoint.Y);
        for (var i = 0; i <= 10; i++)
        {
            Near(MapEdgeGeometry.PointAt(split.First, start, middle, i / 10d),
                MapEdgeGeometry.PointAt(edge, start, end, 0.3 * i / 10d), "first split preserves shape");
            Near(MapEdgeGeometry.PointAt(split.Second, middle, end, i / 10d),
                MapEdgeGeometry.PointAt(edge, start, end, 0.3 + 0.7 * i / 10d), "second split preserves shape");
        }
        Check(Math.Abs(split.First.Distance + split.Second.Distance - MapEdgeGeometry.Length(edge, start, end)) < 0.001,
            "split retains total geometric length");
    }

    private static async Task CheckIndependentCadLayersAsync()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "test-data", "cad-projects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var layer in new[] { "CUSTOMER_A_WALLS", "CUSTOMER_B_EQUIPMENT" })
        {
            var file = Path.Combine(folder, layer + ".dxf");
            await File.WriteAllTextAsync(file, "0\nSECTION\n2\nHEADER\n0\nENDSEC\n0\nSECTION\n2\nTABLES\n0\nTABLE\n2\nLAYER\n70\n1\n0\nLAYER\n2\n" + layer + "\n70\n0\n62\n7\n6\nCONTINUOUS\n0\nENDTAB\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nLINE\n8\n" + layer + "\n10\n0\n20\n0\n30\n0\n11\n1000\n21\n1000\n31\n0\n0\nENDSEC\n0\nEOF\n");
            var result = await new CadUnderlayService().RenderAsync(new CadUnderlayRenderRequest(file, folder, [layer], 640, 480), CancellationToken.None);
            Check(result.AvailableLayers.Contains(layer) && result.IncludedLayers.Contains(layer) &&
                !result.AvailableLayers.Contains(layer == "CUSTOMER_A_WALLS" ? "CUSTOMER_B_EQUIPMENT" : "CUSTOMER_A_WALLS"),
                "CAD layer names and selection come exclusively from each imported file");
            Check(File.Exists(result.PreviewPngPath) && File.Exists(result.PreviewSvgPath), "arbitrary project CAD renders real PNG/SVG assets");
        }
    }

    private static async Task CheckSplineCadFrameAsync()
    {
        var document = new ACadSharp.CadDocument();
        var spline = new ACadSharp.Entities.Spline { Degree = 3 };
        spline.ControlPoints.AddRange([new(10000, 20000, 0), new(10000, 21000, 0), new(11000, 21000, 0), new(11000, 20000, 0)]);
        spline.Knots.AddRange([0, 0, 0, 0, 1, 1, 1, 1]);
        spline.Weights.AddRange([1, 1, 1, 1]);
        document.ModelSpace.Entities.Add(spline);
        var folder = Path.Combine(AppContext.BaseDirectory, "test-data", "cad-spline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "curve.dxf");
        ACadSharp.IO.DxfWriter.Write(file, document, false);
        var source = await File.ReadAllBytesAsync(file);
        var result = await new CadUnderlayService().RenderAsync(new(file, folder, [], 640, 480), CancellationToken.None);
        Check(result.Warnings.Any(warning => warning.Contains("128点")), "sampled CAD spline approximation is disclosed");
        var sourceAfter = await File.ReadAllBytesAsync(file);
        Check(source.SequenceEqual(sourceAfter), "preview conversion never rewrites source CAD");
        Check(result.ViewBox is { } frame && frame.MinX < 10000 && frame.MinY < 20000 &&
            frame.MinX + frame.Width > 11000 && frame.MinY + frame.Height > 20749,
            "CAD raster frame retains nonzero world origin and curve extents with padding");
        using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(result.PreviewPngPath);
        var box = result.ViewBox!;
        var x = (int)Math.Round((10500 - box.MinX) / box.Width * image.Width);
        var y = (int)Math.Round((1 - (20750 - box.MinY) / box.Height) * image.Height);
        Check(Enumerable.Range(-3, 7).Any(dx => Enumerable.Range(-3, 7).Any(dy => image[x + dx, y + dy].R > 80)),
            "nonzero CAD coordinate maps to rendered spline pixel");
    }

    private static void CheckImports()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"agv-geometry-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var paths = new[] { "geometry.json", "nodes.csv", "edges.csv", "resources.csv" }
            .Select(name => Path.Combine(folder, name)).ToArray();
        try
        {
            var start = Node("A", 0, 0); var end = Node("D", 4000, 0);
            var edge = Edge("CURVE", "A", "D") with
            {
                Distance = 0,
                GeometryKind = MapEdgeGeometryKind.CubicBezier,
                ControlPoints = [new(0, 4000), new(4000, 4000)]
            };
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            File.WriteAllText(paths[0], JsonSerializer.Serialize(Definition([start, end], [edge]), options));
            File.WriteAllText(paths[1], "Id,X,Y\nA,0,0\nD,4000,0\n");
            File.WriteAllText(paths[2], "Id,From,To,GeometryKind,ControlPoints,Bidirectional\nCURVE,A,D,CubicBezier,0:4000|4000:4000,true\n");
            File.WriteAllText(paths[3], "Id,Type,Capacity\n");
            var importer = new MapImportService();
            foreach (var request in new[]
                     {
                         new MapImportRequest(paths[0], MapImportFormat.NormalizedJson, 0.001),
                         new MapImportRequest(folder, MapImportFormat.CsvDirectory, 0.001)
                     })
            {
                var imported = importer.Import(request);
                Check(imported.Edges[0].GeometryKind == MapEdgeGeometryKind.CubicBezier, "curve import kind");
                Near(imported.Edges[0].ControlPoints![1], new(4, 4), "curve control-point coordinate scale");
                Check(Math.Abs(imported.Edges[0].Distance - 8) < 0.001, "import derives actual curve distance");
                Check(new MapValidationService().Validate(imported).IsValid, "imported curve validation");
            }
            File.WriteAllText(paths[0], """
                {
                  "name": "LegacyLine", "unit": "m", "resources": [],
                  "nodes": [{ "id": "A", "x": 0, "y": 0 }, { "id": "D", "x": 4, "y": 0 }],
                  "edges": [{ "id": "OLD", "from": "A", "to": "D", "distance": 42, "speedLimit": 1, "bidirectional": true }]
                }
                """);
            var legacy = importer.Import(new MapImportRequest(paths[0]));
            Check(legacy.Edges[0].GeometryKind == MapEdgeGeometryKind.Line && legacy.Edges[0].ControlPoints is null &&
                  new MapValidationService().Validate(legacy).IsValid && legacy.ToGraph().FindShortestRoute("A", "D")?.Cost == 42,
                "legacy JSON without geometry fields retains straight-line validation and configured distance");
        }
        finally
        {
            foreach (var path in paths) if (File.Exists(path)) File.Delete(path);
            Directory.Delete(folder);
        }
    }

    private static MapNode Node(string id, double x, double y) => new(id, x, y, MapNodeType.Waypoint, true, []);
    private static MapEdge Edge(string id, string from, string to) => new(id, from, to, 1, 1, true, true, []);
    private static MapDefinition Definition(IReadOnlyList<MapNode> nodes, IReadOnlyList<MapEdge> edges) =>
        new("Geometry smoke", "test", "LocalCartesian", "m", nodes, edges, []);
    private static void Near(MapPoint2D actual, MapPoint2D expected, string message) =>
        Check(Math.Abs(actual.X - expected.X) < 1e-7 && Math.Abs(actual.Y - expected.Y) < 1e-7, message);
    private static void Check(bool accepted, string message)
    {
        if (!accepted) throw new InvalidOperationException($"Geometry smoke failed: {message}");
    }
}
