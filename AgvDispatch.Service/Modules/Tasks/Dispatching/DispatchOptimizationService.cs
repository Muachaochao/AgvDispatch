using System.Text.Json;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

/// <summary>Immutable, revisioned options are captured once per cycle. Saving never opens an execution gate.</summary>
public sealed class DispatchOptimizationService : IDispatchOptimizationService, IDispatchPipelineMonitor
{
    private readonly string _path;
    private readonly SemaphoreSlim _save = new(1, 1);
    private DispatchOptimizationSnapshot _snapshot;
    private readonly ILogger<DispatchOptimizationService> _logger;
    public DispatchOptimizationService(string path, DispatchAssignmentOptions defaults, ILogger<DispatchOptimizationService> logger)
    {
        _path = path; _logger = logger;
        if (defaults.GetConfigurationError() is { } error) throw new InvalidDataException(error);
        defaults = Canonical(defaults);
        _snapshot = new() { Options = defaults };
        if (!File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("优化配置超过1MB。");
            var saved = JsonSerializer.Deserialize<SavedOptions>(File.ReadAllText(path)) ?? throw new InvalidDataException("优化配置为空。");
            if (saved.Version != 1 || saved.Options is null || saved.Options.GetConfigurationError() is not null ||
                !Guid.TryParseExact(saved.Revision, "N", out _)) throw new InvalidDataException("优化配置无效。");
            _snapshot = _snapshot with { Options = Canonical(saved.Options), Revision = saved.Revision };
        }
        catch (Exception ex)
        {
            // A damaged file must not silently enable a different dispatch policy.
            _snapshot = _snapshot with { Options = defaults with { Mode = "Disabled" }, Message = "优化配置读取失败，保留传统派车：" + ex.Message };
            logger.LogError(ex, "优化配置无法读取，未启用全局匹配。");
        }
    }
    public DispatchAssignmentOptions Options => Volatile.Read(ref _snapshot).Options;
    public void ReportLayer(string key, string state, string summary, int? itemCount = null)
    {
        DispatchOptimizationSnapshot before, after;
        do
        {
            before = Volatile.Read(ref _snapshot);
            if (!before.Layers.Any(layer => layer.Key == key)) throw new ArgumentException("未知派车层：" + key);
            after = before with { Layers = before.Layers.Select(layer => layer.Key == key
                ? layer with { State = state, Summary = summary, ItemCount = itemCount, UpdatedAt = DateTimeOffset.UtcNow } : layer).ToArray() };
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, after, before), before));
    }
    public Task<DispatchOptimizationSnapshot> GetAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(Volatile.Read(ref _snapshot)); }

    public void Publish(DispatchOptimizationSnapshot cycle, bool shadow = false, bool improved = false)
    {
        DispatchOptimizationSnapshot before, after;
        do
        {
            before = Volatile.Read(ref _snapshot);
            after = cycle with { Options = before.Options, Revision = before.Revision, CycleNumber = before.CycleNumber + 1,
                Layers = before.Layers,
                ShadowCycleCount = before.ShadowCycleCount + (shadow ? 1 : 0),
                ImprovedCycleCount = before.ImprovedCycleCount + (improved ? 1 : 0) };
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, after, before), before));
    }
    public async Task<DispatchOptimizationSnapshot> SaveOptionsAsync(TrafficUserContext user, SaveDispatchOptimizationOptionsRequest request, CancellationToken ct)
    {
        if (!user.Has(TrafficPermission.Administer)) throw new UnauthorizedAccessException("保存派车策略需要管理员权限。");
        if (request.Options is null) throw new ArgumentException("优化配置不能为空。");
        request = request with { Options = JsonSerializer.Deserialize<DispatchAssignmentOptions>(JsonSerializer.Serialize(request.Options))! };
        if (request.Options.GetConfigurationError() is { } error) throw new ArgumentException(error);
        request = request with { Options = Canonical(request.Options) };
        await _save.WaitAsync(ct);
        try
        {
            var previous = Volatile.Read(ref _snapshot);
            if (previous.Revision != request.ExpectedRevision) throw new InvalidOperationException("优化参数已被修改，请重新加载后保存。");
            var saved = new SavedOptions(1, Guid.NewGuid().ToString("N"), request.Options, DateTimeOffset.UtcNow, user.WindowsIdentity);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }), ct);
                ct.ThrowIfCancellationRequested(); File.Move(temp, _path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            DispatchOptimizationSnapshot before, after;
            do
            {
                before = Volatile.Read(ref _snapshot);
                after = before with { Options = saved.Options, Revision = saved.Revision,
                    Message = "策略已保存，下个滚动周期生效；既有任务、路权和执行许可继续保留。" };
            } while (!ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, after, before), before));
            _logger.LogInformation("派车优化策略已保存。Identity={Identity}, Revision={Revision}, Mode={Mode}", user.WindowsIdentity, saved.Revision, saved.Options.Mode);
            return after;
        }
        finally { _save.Release(); }
    }
    private sealed record SavedOptions(int Version, string Revision, DispatchAssignmentOptions Options, DateTimeOffset SavedAt, string SavedBy);
    private static DispatchAssignmentOptions Canonical(DispatchAssignmentOptions options) => options with
    { Mode = options.Mode.Equals("Global", StringComparison.OrdinalIgnoreCase) ? "Global" : options.Mode.Equals("Shadow", StringComparison.OrdinalIgnoreCase) ? "Shadow" : "Disabled" };
}
