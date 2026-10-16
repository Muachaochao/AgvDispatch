namespace AgvDispatch.Application.Abstractions;

public sealed record CadLayerSnapshot(string Name, bool Included);

public sealed record CadViewBoxSnapshot(
    double MinX,
    double MinY,
    double Width,
    double Height,
    int PixelWidth,
    int PixelHeight);

public sealed record CadUnderlayRenderRequest(
    string SourcePath,
    string OutputDirectory,
    IReadOnlyList<string> IncludedLayers,
    int Width = 2200,
    int Height = 1400,
    CadRenderRegion? Region = null);

public sealed record CadRenderRegion(double MinX, double MinY, double Width, double Height);
public sealed record CadDetailRequest(string MapId, string BasePreviewPath, CadRenderRegion Region, int Width = 1800, int Height = 1100);
public sealed record CadDetailResult(string PreviewPngPath, CadViewBoxSnapshot ViewBox);
public interface ICadDetailClient
{
    Task<CadDetailResult> GetCadDetailAsync(CadDetailRequest request, CancellationToken cancellationToken);
}
public interface ICadDetailService
{
    Task<CadDetailResult> GetAsync(CadDetailRequest request, CancellationToken cancellationToken);
}

public sealed record CadUnderlayRenderResult(
    string PreviewPngPath,
    string PreviewSvgPath,
    IReadOnlyList<string> AvailableLayers,
    IReadOnlyList<string> IncludedLayers,
    CadViewBoxSnapshot? ViewBox,
    IReadOnlyList<string> Warnings);

public interface ICadUnderlayService
{
    Task<CadUnderlayRenderResult> RenderAsync(
        CadUnderlayRenderRequest request,
        CancellationToken cancellationToken);
}

public enum CadUnderlayJobStatus
{
    None = 0,
    Pending = 10,
    Processing = 20,
    Ready = 30,
    Failed = 40
}

public sealed record CadUnderlayJob(
    string MapId,
    string SourcePath,
    IReadOnlyList<string> IncludedLayers,
    int Width = 4096, int Height = 2608);

public interface ICadUnderlayJobQueue
{
    ValueTask QueueAsync(CadUnderlayJob job, CancellationToken cancellationToken);
    IAsyncEnumerable<CadUnderlayJob> ReadAllAsync(CancellationToken cancellationToken);
}
