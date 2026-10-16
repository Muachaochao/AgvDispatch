using ACadSharp;
using ACadSharp.IO;
using ACadSharp.Image;
using AgvDispatch.Application.Abstractions;
using System.Xml.Linq;

namespace AgvDispatch.Infrastructure.Maps;

public sealed class CadUnderlayService : ICadUnderlayService
{
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private string? _preparedKey;
    private CadDocument? _preparedDocument;
    private ImagePage? _preparedFrame;
    private int _preparedCurves;
    private readonly Dictionary<ulong, CSMath.BoundingBox> _entityBounds = [];
    public async Task<CadUnderlayRenderResult> RenderAsync(
        CadUnderlayRenderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(request.SourcePath))
        {
            throw new FileNotFoundException("CAD底图文件不存在。", request.SourcePath);
        }
        var extension = Path.GetExtension(request.SourcePath).ToUpperInvariant();
        if (extension is not (".DWG" or ".DXF"))
        {
            throw new NotSupportedException("CAD底图仅支持DWG和DXF格式。 ");
        }
        if (request.Width is < 320 or > 10000 || request.Height is < 240 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "底图输出尺寸超出允许范围。 ");
        }
        if ((long)request.Width * request.Height > 32000000) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Region is { } region && (!double.IsFinite(region.MinX) || !double.IsFinite(region.MinY) ||
            !double.IsFinite(region.Width) || !double.IsFinite(region.Height) || region.Width <= 0 || region.Height <= 0))
            throw new ArgumentException("局部CAD范围无效。", nameof(request));

        await _renderGate.WaitAsync(cancellationToken);
        try { return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = new FileInfo(request.SourcePath);
            var preparedKey = Path.GetFullPath(request.SourcePath) + "|" + source.Length + "|" + source.LastWriteTimeUtc.Ticks + "|" +
                string.Join("\n", request.IncludedLayers.Order(StringComparer.OrdinalIgnoreCase));
            CadDocument document = _preparedKey == preparedKey && _preparedDocument is not null
                ? _preparedDocument : extension == ".DWG" ? DwgReader.Read(request.SourcePath) : DxfReader.Read(request.SourcePath);
            var layers = document.Layers.Select(layer => layer.Name)
                .Concat(document.Entities.Select(entity => entity.Layer?.Name ?? string.Empty))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var included = request.IncludedLayers
                .Where(layer => layers.Contains(layer, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Directory.CreateDirectory(request.OutputDirectory);
            var stem = $"underlay-{Guid.NewGuid():N}";
            var pngPath = Path.Combine(request.OutputDirectory, stem + ".png");
            var svgPath = Path.Combine(request.OutputDirectory, stem + ".svg");
            var exporter = new ImageExporter();
            exporter.Configuration.Width = request.Width;
            exporter.Configuration.Height = request.Height;
            exporter.Configuration.SetPadding(24);
            exporter.Configuration.BackgroundColor = SixLabors.ImageSharp.Color.Parse("#101820");
            exporter.Configuration.LayerVisibility = LayerVisibilityMode.Screen;
            if (included.Length > 0) exporter.Configuration.IncludeLayers(included);
            // Calculate the world frame from drawable root entities, not hidden/archived schemes.
            // Otherwise a valid lower layout remains tiny inside the extents of abandoned upper copies.
            if (_preparedKey != preparedKey || _preparedFrame is null)
            {
                var curves = AddModelSpaceIndexed(exporter, document, includeEntity: entity =>
                    entity.Layer.IsOn && !entity.Layer.Flags.HasFlag(ACadSharp.Tables.LayerFlags.Frozen) &&
                    (included.Length == 0 || included.Contains(entity.Layer.Name, StringComparer.OrdinalIgnoreCase)));
                var preparedFrame = exporter.Pages[^1];
                if (preparedFrame.Entities.Count == 0) throw new InvalidDataException("所选图层没有可见CAD实体。");
                _preparedFrame = preparedFrame;
                _preparedCurves = curves;
                _preparedDocument = document;
                _preparedKey = preparedKey;
                _entityBounds.Clear();
            }
            var sampledCurves = _preparedCurves;
            var frame = _preparedFrame;
            // Keep the same world frame for PNG and SVG; avoid repeatedly traversing nested blocks for filters.
            var fixedExporter = new ImageExporter();
            fixedExporter.Configuration.Width = request.Width;
            fixedExporter.Configuration.Height = request.Height;
            var padding = request.Region is null ? 24 : 0;
            fixedExporter.Configuration.SetPadding(padding);
            fixedExporter.Configuration.BackgroundColor = exporter.Configuration.BackgroundColor;
            fixedExporter.Configuration.LayerVisibility = LayerVisibilityMode.Screen;
            if (included.Length > 0) fixedExporter.Configuration.IncludeLayers(included);
            fixedExporter.Add(new CadDocument().Layouts.First());
            var fixedPage = fixedExporter.Pages[^1];
            if (request.Region is { } crop)
            {
                fixedPage.Layout!.PaperWidth = crop.Width;
                fixedPage.Layout.PaperHeight = crop.Height;
                fixedPage.Translation = new(-crop.MinX, -crop.MinY);
            }
            else
            {
                fixedPage.Layout = frame.Layout;
                fixedPage.Translation = frame.Translation;
            }
            fixedPage.Document = document;
            foreach (var entity in frame.Entities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.Region is { } area)
                {
                    if (!_entityBounds.TryGetValue(entity.Handle, out var bounds))
                    { bounds = entity.GetBoundingBox(); _entityBounds[entity.Handle] = bounds; }
                    if (double.IsFinite(bounds.Min.X) && double.IsFinite(bounds.Min.Y) && double.IsFinite(bounds.Max.X) && double.IsFinite(bounds.Max.Y) &&
                        (bounds.Max.X < area.MinX || bounds.Min.X > area.MinX + area.Width ||
                         bounds.Max.Y < area.MinY || bounds.Min.Y > area.MinY + area.Height)) continue;
                }
                fixedPage.AddEntity(entity);
            }
            foreach (var viewport in frame.Viewports) fixedPage.AddViewport(viewport);
            var viewBox = RasterWorldViewBox(fixedPage, request.Width, request.Height, padding);
            var warnings = new List<string>();
            if (sampledCurves > 0) warnings.Add($"底图中{sampledCurves}条样条使用128点近似显示；原CAD文件未修改。");
            cancellationToken.ThrowIfCancellationRequested();
            fixedExporter.Save(pngPath, ImageExportFormat.Png);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Region is not null) svgPath = string.Empty; // Detail is a raster overlay, not a replacement CAD asset.
            else if (ExpandedEntityCount(document.ModelSpace, new HashSet<ulong>(), 100000) >= 100000)
            {
                var ns = XNamespace.Get("http://www.w3.org/2000/svg");
                new XDocument(new XElement(ns + "svg",
                    new XAttribute("viewBox", FormattableString.Invariant($"{viewBox.MinX} {viewBox.MinY} {viewBox.Width} {viewBox.Height}")),
                    new XElement(ns + "image", new XAttribute("x", viewBox.MinX), new XAttribute("y", viewBox.MinY),
                        new XAttribute("width", viewBox.Width), new XAttribute("height", viewBox.Height),
                        new XAttribute("href", "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(pngPath))))))
                    .Save(svgPath);
                warnings.Add("复杂嵌套CAD采用栅格底图及SVG图片封装，避免矢量展开耗尽内存。");
            }
            else fixedExporter.Save(svgPath, ImageExportFormat.Svg);
            cancellationToken.ThrowIfCancellationRequested();
            return new CadUnderlayRenderResult(
                pngPath,
                svgPath,
                layers,
                included.Length == 0 ? layers : included,
                viewBox,
                warnings);
        }, cancellationToken); }
        finally { _renderGate.Release(); }
    }

    private static long ExpandedEntityCount(ACadSharp.Tables.BlockRecord block, HashSet<ulong> visiting, long limit)
    {
        if (!visiting.Add(block.Handle)) return limit;
        long count = 0;
        foreach (var entity in block.Entities)
        {
            count += entity is ACadSharp.Entities.Insert insert
                ? ExpandedEntityCount(insert.Block, visiting, limit) : 1;
            if (count >= limit) break;
        }
        visiting.Remove(block.Handle);
        return count;
    }

    private static CadViewBoxSnapshot RasterWorldViewBox(ImagePage page, int width, int height, int padding = 24)
    {
        var paperWidth = Math.Max(1, page.Layout!.PaperWidth);
        var paperHeight = Math.Max(1, page.Layout.PaperHeight);
        // Match the raster renderer's float fit and centered pixel padding.
        var scale = Math.Min((width - padding * 2f) / (float)paperWidth, (height - padding * 2f) / (float)paperHeight);
        var offsetX = padding + ((width - padding * 2f) - (float)paperWidth * scale) / 2f;
        var offsetY = padding + ((height - padding * 2f) - (float)paperHeight * scale) / 2f;
        return new CadViewBoxSnapshot(-page.Translation.X - offsetX / scale,
            -page.Translation.Y - offsetY / scale, width / (double)scale, height / (double)scale, width, height);
    }

    public static int AddModelSpaceIndexed(ImageExporter exporter, CadDocument document, Action<string>? trace = null,
        Func<ACadSharp.Entities.Entity, bool>? includeEntity = null)
    {
        var sampledCurves = 0;
        foreach (var blockRecord in document.BlockRecords)
        {
            trace?.Invoke("Sampling block " + blockRecord.Name + " splines=" + blockRecord.Entities.OfType<ACadSharp.Entities.Spline>().Count());
            var sortHandles = new Dictionary<ulong, ulong>();
            if (blockRecord.SortEntitiesTable is { } sourceOrder)
                foreach (var sorter in sourceOrder) if (sorter.Entity is not null) sortHandles.TryAdd(sorter.Entity.Handle, sorter.SortHandle);
            foreach (var spline in blockRecord.Entities.OfType<ACadSharp.Entities.Spline>().ToArray())
            {
                if (!spline.TryPolygonalVertexes(128, out var vertices) || vertices.Count < 2) continue;
                // Lightweight vertices are values, avoiding hundreds of document handle registrations per curve.
                ACadSharp.Entities.Entity polyline;
                if (vertices.All(vertex => Math.Abs(vertex.Z - vertices[0].Z) < 1e-8))
                {
                    var planar = new ACadSharp.Entities.LwPolyline(vertices.Select(vertex => new CSMath.XY(vertex.X, vertex.Y)))
                    { Elevation = vertices[0].Z, IsClosed = spline.IsClosed || spline.IsPeriodic };
                    polyline = planar;
                }
                else
                {
                    var spatial = new ACadSharp.Entities.Polyline3D();
                    foreach (var vertex in vertices) spatial.Vertices.Add(new ACadSharp.Entities.Vertex3D(vertex));
                    if (spline.IsClosed || spline.IsPeriodic) spatial.Vertices.Add(new ACadSharp.Entities.Vertex3D(vertices[0]));
                    polyline = spatial;
                }
                polyline.Layer = spline.Layer; polyline.Color = spline.Color; polyline.LineType = spline.LineType;
                polyline.LineWeight = spline.LineWeight; polyline.IsInvisible = spline.IsInvisible;
                var drawHandle = sortHandles.GetValueOrDefault(spline.Handle, spline.Handle);
                blockRecord.Entities.Remove(spline); blockRecord.Entities.Add(polyline);
                blockRecord.CreateSortEntitiesTable().Add(polyline, drawHandle);
                sampledCurves++;
            }
        }
        trace?.Invoke("Spline previews cached for all blocks");
        // Index CAD draw-order handles once instead of scanning its sorter table for every entity.
        exporter.AddModelSpace(new CadDocument());
        var page = exporter.Pages[^1];
        page.Document = document;
        var block = document.ModelSpace;
        var order = new Dictionary<ulong, ulong>();
        if (block.SortEntitiesTable is { } table)
            foreach (var sorter in table)
                if (sorter.Entity is not null) order.TryAdd(sorter.Entity.Handle, sorter.SortHandle);
        trace?.Invoke("Draw order indexed");
        foreach (var entity in block.Entities.OrderBy(entity => order.GetValueOrDefault(entity.Handle, entity.Handle)))
        {
            if (includeEntity is not null && !includeEntity(entity)) continue;
            if (entity is ACadSharp.Entities.Viewport viewport)
            { if (!viewport.RepresentsPaper) page.AddViewport(viewport); }
            else page.AddEntity(entity);
        }
        trace?.Invoke("Entities added; calculating bounds");
        page.UpdateLayoutSize();
        trace?.Invoke("Bounds completed");
        return sampledCurves;
    }

}
