using System.Text.Json;
using System.Text.Json.Serialization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Infrastructure.Maps;

public sealed class LocalMapEditorStore : ILocalMapEditorStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly ICadUnderlayService _renderer;
    private readonly SemaphoreSlim _detailGate = new(1);
    private readonly LinkedList<(string Key, CadDetailResult Result)> _detailCache = new();
    public string DirectoryPath { get; }
    public LocalMapEditorStore(string? directory = null, ICadUnderlayService? renderer = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgvDispatch", "OfflineMaps"));
        _renderer = renderer ?? new CadUnderlayService();
    }
    private string FilePath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("无效的本地地图编号。");
        return Path.Combine(DirectoryPath, id + ".agvmap.json");
    }
    public async Task<IReadOnlyList<MapProjectSummary>> ListAsync()
    {
        Directory.CreateDirectory(DirectoryPath);
        var maps = new List<MapProjectSummary>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.agvmap.json"))
            maps.Add((await ReadAsync(file)).Project);
        return maps.OrderByDescending(p => p.UpdatedAt).ToArray();
    }
    public Task<MapWorkspaceSnapshot> LoadAsync(string mapId) => ReadAsync(FilePath(mapId));
    private static async Task<MapWorkspaceSnapshot> ReadAsync(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("地图文件超过64MB。");
        await using var stream = File.OpenRead(path);
        var workspace = await JsonSerializer.DeserializeAsync<MapWorkspaceSnapshot>(stream, Json)
            ?? throw new InvalidDataException("不是有效的离线地图文件。");
        if (workspace.Project is null || workspace.Draft?.Definition is null || workspace.Draft.Definition.Nodes is null || workspace.Draft.Definition.Edges is null || workspace.Draft.Definition.Resources is null)
            throw new InvalidDataException("离线地图缺少项目或拓扑数据。");
        return workspace;
    }
    public MapWorkspaceSnapshot Create(CreateMapRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || !double.IsFinite(request.CoordinateScale) || request.CoordinateScale <= 0)
            throw new ArgumentException("地图名称和坐标换算比例无效。");
        var now = DateTimeOffset.Now; var id = Guid.NewGuid().ToString("N");
        var definition = new MapDefinition(request.Name, request.BackgroundFilePath, request.CoordinateSystem, request.Unit, [], [], []);
        return new(new(id, request.Name, request.Description, request.BackgroundFilePath, Path.GetExtension(request.BackgroundFilePath).TrimStart('.').ToUpperInvariant(), request.CoordinateSystem, request.Unit, request.CoordinateScale, "", "", [], null, null, CadUnderlayJobStatus.None, "尚未生成底图", null, false, null, null, now),
            new(Guid.NewGuid().ToString("N"), id, 1, MapVersionStatus.Draft, definition, Validate(definition), "LocalEditor", now, now, null, Guid.NewGuid().ToString("N")), null);
    }
    public Task SaveAsync(MapWorkspaceSnapshot workspace) => ExportAsync(workspace, FilePath(workspace.Project.MapId));
    public async Task ExportAsync(MapWorkspaceSnapshot workspace, string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                await JsonSerializer.SerializeAsync(stream, workspace, Json);
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task<MapWorkspaceSnapshot> OpenFileAsync(string path)
    {
        if(string.Equals(Path.GetExtension(path),".smap",StringComparison.OrdinalIgnoreCase))return await OpenSmapAsync(path);
        var original = await ReadAsync(path);
        // Imported workspace is always local; never inherit backend active/version identifiers.
        var id = Guid.NewGuid().ToString("N");
        var local = original with { Project = original.Project with { MapId = id, IsActive = false, ActiveVersionId = null, PublishedVersionId = null },
            Draft = original.Draft with { MapId = id, VersionId = Guid.NewGuid().ToString("N"), Status = MapVersionStatus.Draft, PublishedAt = null, EditToken = Guid.NewGuid().ToString("N") }, Published = null };
        await SaveAsync(local); return local;
    }
    private async Task<MapWorkspaceSnapshot> OpenSmapAsync(string path)
    {
        if(new FileInfo(path).Length>64*1024*1024)throw new InvalidDataException("Smap文件超过64MB。");
        var bytes=await File.ReadAllBytesAsync(path);
        // Parse once in a worker before creating any assets; the exact same bytes are retained.
        Smap3DPackageReader.Result? package=null;
        var parsed=await Task.Run(()=>
        {
            if(Smap3DPackageReader.IsZip(bytes)){package=new Smap3DPackageReader().Read(bytes,path);return package.Plane;}
            return new Smap2DImportService().Read(bytes,path);
        });
        var workspace=Create(new(parsed.Definition.Name,string.Join("；",parsed.Warnings),path,"SmapWorldTranslated","m",1));
        var assets=Path.Combine(DirectoryPath,workspace.Project.MapId,"smap-source");Directory.CreateDirectory(assets);
        var source=Path.Combine(assets,"original.smap");var preview=Path.Combine(assets,"background.png");
        await File.WriteAllBytesAsync(source,bytes);
        await Task.Run(()=>Smap2DImportService.SaveBackground(parsed,preview));
        var scan=Path.Combine(assets,"scan.png");var annotation=Path.Combine(assets,"annotations.png");
        await Task.Run(()=>{Smap2DImportService.SavePixels(parsed.ScanPixels,scan);Smap2DImportService.SavePixels(parsed.AnnotationPixels,annotation);});
        var definition=parsed.Definition with{Source=source};
        var report=Validate(definition);
        report=report with{Issues=report.Issues.Concat(parsed.Warnings.Select(w=>new MapValidationIssue(MapValidationSeverity.Warning,"SMAP_PREVIEW_ONLY",w,[]))).ToArray()};
        workspace=workspace with{Project=workspace.Project with{BackgroundFilePath=source,BackgroundFormat=package is null?"SMAP":"SMAP3D-2D",SmapScanPngPath=scan,SmapAnnotationPngPath=annotation,PreviewPngPath=preview,CadViewBox=new(0,0,parsed.Width,parsed.Height,900,520),UnderlayStatus=CadUnderlayJobStatus.Ready,UnderlayMessage=string.Join("；",parsed.Warnings)},Draft=workspace.Draft with{Definition=definition,Validation=report}};
        await File.WriteAllTextAsync(Path.Combine(assets,"source-review.json"),JsonSerializer.Serialize(new{parsed.OriginalMapName,parsed.FormatVersion,Sha256=package?.PackageSha256??parsed.Sha256,Md5=Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes)),PlaneSha256=package is null?null:parsed.Sha256,parsed.OffsetX,parsed.OffsetY,parsed.Width,parsed.Height,parsed.OrdinaryPoints,parsed.Warnings,SourceFile="original.smap",PreviewOnly=true,DeviceValidated=false},Json));
        if(package is not null)
        {
            await File.WriteAllBytesAsync(Path.Combine(assets,"0.smap"),package.PlaneBytes);
            await File.WriteAllTextAsync(Path.Combine(assets,"package-review.json"),JsonSerializer.Serialize(new{SourceFile="original.smap",package.ZipName,package.PackageSha256,package.Entries,PlaneSha256=parsed.Sha256,PlaneOffsetX=parsed.OffsetX,PlaneOffsetY=parsed.OffsetY,Preview="3D包的二维部分",package.PointCloudParsed,package.DeviceValidated,WritesEnabled=false},Json));
        }
        await SaveAsync(workspace);return workspace;
    }
    public MapDefinition Import(MapImportRequest request) => new MapImportService().Import(request);
    public MapValidationReport Validate(MapDefinition definition) => new MapValidationService().Validate(definition);
    public MapCalibrationSnapshot Calibrate(IReadOnlyList<MapCalibrationPoint> points) => new MapCalibrationService().Calculate(points);
    public async Task<MapWorkspaceSnapshot> RenderAsync(MapWorkspaceSnapshot workspace, GenerateMapUnderlayRequest request)
    {
        var output = Path.Combine(DirectoryPath, workspace.Project.MapId, "underlay");
        var rendered = await _renderer.RenderAsync(new(request.SourcePath, output, request.IncludedLayers, request.Width, request.Height), CancellationToken.None);
        var sameSource = string.Equals(workspace.Project.BackgroundFilePath, request.SourcePath, StringComparison.OrdinalIgnoreCase);
        var project = workspace.Project with { BackgroundFilePath = request.SourcePath, BackgroundFormat = Path.GetExtension(request.SourcePath).TrimStart('.').ToUpperInvariant(), PreviewPngPath = rendered.PreviewPngPath, PreviewSvgPath = rendered.PreviewSvgPath,
            SmapScanPngPath = null, SmapAnnotationPngPath = null,
            CadViewBox = rendered.ViewBox, Calibration = sameSource ? workspace.Project.Calibration : null,
            CadLayers = rendered.AvailableLayers.Select(name => new CadLayerSnapshot(name, rendered.IncludedLayers.Contains(name))).ToArray(), UnderlayStatus = CadUnderlayJobStatus.Ready,
            UnderlayGeneratedAt = DateTimeOffset.Now, UnderlayMessage = $"本地底图已生成 {request.Width}×{request.Height}", UpdatedAt = DateTimeOffset.Now };
        var result = workspace with { Project = project }; await SaveAsync(result); return result;
    }
    public async Task<CadDetailResult> GetCadDetailAsync(CadDetailRequest request, CancellationToken cancellationToken)
    {
        await _detailGate.WaitAsync(cancellationToken);
        try
        {
            var workspace = await LoadAsync(request.MapId); var p = workspace.Project; var r = request.Region;
            if(p.BackgroundFormat is "SMAP" or "SMAP3D-2D")throw new NotSupportedException("Smap当前使用原图预览，尚未提供CAD局部重绘。");
            if (p.PreviewPngPath != request.BasePreviewPath || p.CadViewBox is not { } frame || p.UnderlayStatus != CadUnderlayJobStatus.Ready)
                throw new InvalidOperationException("本地底图版本已变化。");
            if (!double.IsFinite(r.MinX) || !double.IsFinite(r.MinY) || !double.IsFinite(r.Width) || !double.IsFinite(r.Height) || r.Width <= 0 || r.Height <= 0 ||
                r.MinX < frame.MinX - .01 || r.MinY < frame.MinY - .01 || r.MinX + r.Width > frame.MinX + frame.Width + .01 || r.MinY + r.Height > frame.MinY + frame.Height + .01 ||
                request.Width is < 320 or > 2048 || request.Height is < 240 or > 2048) throw new ArgumentException("局部范围或分辨率无效。");
            var source = new FileInfo(p.BackgroundFilePath);
            var key = JsonSerializer.Serialize(new { request, source.Length, source.LastWriteTimeUtc, p.CadLayers });
            var hit = _detailCache.FirstOrDefault(item => item.Key == key);
            if (hit.Result is not null && File.Exists(hit.Result.PreviewPngPath)) { _detailCache.Remove(hit); _detailCache.AddFirst(hit); return hit.Result; }
            var output = Path.Combine(DirectoryPath, request.MapId, "detail");
            var render = await _renderer.RenderAsync(new(p.BackgroundFilePath, output, p.CadLayers.Where(layer => layer.Included).Select(layer => layer.Name).ToArray(), request.Width, request.Height, r), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var result = new CadDetailResult(render.PreviewPngPath, render.ViewBox!); _detailCache.AddFirst((key, result));
            while (_detailCache.Count > 24) _detailCache.RemoveLast();
            // Only prune files generated in this map's dedicated local detail directory.
            foreach (var file in new DirectoryInfo(output).EnumerateFiles("underlay-*.png").OrderByDescending(f => f.LastWriteTimeUtc).Skip(32))
                try { file.Delete(); } catch (IOException) { }
            return result;
        }
        finally { _detailGate.Release(); }
    }
}
