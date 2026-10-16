using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class MapManagementViewModel
{
    public string[] UnderlayQualityChoices { get; } = ["标准 · 2200×1400", "高清 · 4096×2608", "超清 · 6144×3909"];
    private int _underlayQualityIndex = 1;
    public int UnderlayQualityIndex { get => _underlayQualityIndex; set => SetProperty(ref _underlayQualityIndex, Math.Clamp(value, 0, 2)); }
    private double _underlayOpacity = 1;
    public double UnderlayOpacity { get => _underlayOpacity; set => SetProperty(ref _underlayOpacity, double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1); }
    private bool _enableCadDetail = true;
    public bool EnableCadDetail { get => _enableCadDetail; set { if (SetProperty(ref _enableCadDetail, value)) ScheduleCadDetail(); } }
    private ImageSource? _cadDetailImage;
    public ImageSource? CadDetailImage { get => _cadDetailImage; private set => SetProperty(ref _cadDetailImage, value); }
    private Rect _cadDetailRect;
    public double CadDetailLeft => _cadDetailRect.Left;
    public double CadDetailTop => _cadDetailRect.Top;
    public double CadDetailWidth => _cadDetailRect.Width;
    public double CadDetailHeight => _cadDetailRect.Height;
    private string _cadDetailStatus = "放大后自动加载局部高清；不改变地图标定。";
    public string CadDetailStatus { get => _cadDetailStatus; private set => SetProperty(ref _cadDetailStatus, value); }
    private readonly DispatcherTimer _cadDetailTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private CancellationTokenSource? _cadDetailCancellation;
    private int _cadDetailRevision;
    private bool _cadDetailActive;
    private Rect _cadVisibleRect = new(0, 0, 900, 520);
    private int _cadPixelWidth = 1800, _cadPixelHeight = 1100;

    private void InitializeCadDetail() => _cadDetailTimer.Tick += async (_, _) =>
    { _cadDetailTimer.Stop(); await RefreshCadDetailAsync(); };

    public void UpdateCadViewport(Rect visibleRect, int pixelWidth, int pixelHeight)
    {
        if (_cadVisibleRect == visibleRect && _cadPixelWidth == pixelWidth && _cadPixelHeight == pixelHeight) return;
        _cadVisibleRect = visibleRect;
        _cadPixelWidth = Math.Clamp(pixelWidth, 320, 2048); _cadPixelHeight = Math.Clamp(pixelHeight, 240, 2048);
        ScheduleCadDetail();
    }

    private void ScheduleCadDetail()
    {
        _cadDetailRevision++;
        _cadDetailTimer.Stop(); _cadDetailCancellation?.Cancel();
        CadDetailImage = null; // Do not display an old overlay during a map/viewport change.
        if(_workspace?.Project.BackgroundFormat is "SMAP" or "SMAP3D-2D"){CadDetailStatus="Smap原图预览；局部CAD高清重绘暂不可用。";return;}
        if (!_cadDetailActive || !EnableCadDetail || !ShowUnderlayLayer || CanvasZoom < 1.35 || (!IsOfflineEditing && _client is not ICadDetailClient)) return;
        _cadDetailTimer.Start();
    }

    private async Task RefreshCadDetailAsync()
    {
        var client = IsOfflineEditing ? LocalStore : _client as ICadDetailClient;
        if (!_cadDetailActive || _workspace?.Project.CadViewBox is not { } frame ||
            _workspace.Project.UnderlayStatus != CadUnderlayJobStatus.Ready || client is null) return;
        var scale = Math.Min(900d / frame.PixelWidth, 520d / frame.PixelHeight);
        var displayW = frame.PixelWidth * scale; var displayH = frame.PixelHeight * scale;
        var dx = (900 - displayW) / 2; var dy = (520 - displayH) / 2;
        var rect = Rect.Intersect(_cadVisibleRect, new Rect(dx, dy, displayW, displayH));
        if (rect.IsEmpty || rect.Width < 1 || rect.Height < 1) return;
        var region = new CadRenderRegion(frame.MinX + (rect.Left - dx) / displayW * frame.Width,
            frame.MinY + (1 - (rect.Bottom - dy) / displayH) * frame.Height,
            rect.Width / displayW * frame.Width, rect.Height / displayH * frame.Height);
        var revision = _cadDetailRevision;
        var mapId = _workspace.Project.MapId; var preview = _workspace.Project.PreviewPngPath;
        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _cadDetailCancellation = cancellation;
        try
        {
            CadDetailStatus = "正在加载局部高清（继续显示全图，不阻塞编辑）…";
            var pixelWidth = Math.Clamp((int)(_cadPixelWidth * rect.Width / Math.Max(1, _cadVisibleRect.Width)), 320, 2048);
            var pixelHeight = Math.Clamp((int)(_cadPixelHeight * rect.Height / Math.Max(1, _cadVisibleRect.Height)), 240, 2048);
            var result = await client.GetCadDetailAsync(new(mapId, preview, region, pixelWidth, pixelHeight), cancellation.Token);
            if (revision != _cadDetailRevision || !_cadDetailActive || _workspace?.Project.PreviewPngPath != preview || _workspace.Project.MapId != mapId) return;
            var box = result.ViewBox;
            _cadDetailRect = new Rect(dx + (box.MinX - frame.MinX) / frame.Width * displayW,
                dy + (1 - (box.MinY + box.Height - frame.MinY) / frame.Height) * displayH,
                box.Width / frame.Width * displayW, box.Height / frame.Height * displayH);
            RaisePropertyChanged(nameof(CadDetailLeft)); RaisePropertyChanged(nameof(CadDetailTop));
            RaisePropertyChanged(nameof(CadDetailWidth)); RaisePropertyChanged(nameof(CadDetailHeight));
            CadDetailImage = LoadImage(result.PreviewPngPath);
            CadDetailStatus = CadDetailImage is null ? "局部图片不可用，已保留全图。" : "局部高清已加载 · 导航点线和CAD坐标保持不变";
        }
        catch (OperationCanceledException) { if (revision == _cadDetailRevision) CadDetailStatus = "局部请求已取消或超时，继续显示全图。"; }
        catch (Exception exception) { if (revision == _cadDetailRevision) CadDetailStatus = $"局部高清暂不可用：{exception.Message}；继续显示全图。"; }
        finally { if (ReferenceEquals(_cadDetailCancellation, cancellation)) _cadDetailCancellation = null; cancellation.Dispose(); }
    }
}
