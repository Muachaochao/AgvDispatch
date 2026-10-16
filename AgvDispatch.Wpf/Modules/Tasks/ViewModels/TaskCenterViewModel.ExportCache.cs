using System.Collections.ObjectModel;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;
namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class TaskCenterViewModel
{
    private bool _isExportCacheBusy;
    private int _exportCacheRetention = 7;
    private string _exportCacheMessage = "点击刷新查看后台导出缓存；清理不会删除已保存到其他位置的交接文件。";
    public ObservableCollection<TaskHistoryExportCacheItem> ExportCacheItems { get; } = [];
    public IReadOnlyList<int> ExportCacheRetentionChoices { get; } = [7, 30, 90];
    public int ExportCacheRetention { get => _exportCacheRetention; set => SetProperty(ref _exportCacheRetention, value); }
    public string ExportCacheMessage { get => _exportCacheMessage; private set => SetProperty(ref _exportCacheMessage, value); }
    public bool IsExportCacheBusy { get => _isExportCacheBusy; private set { SetProperty(ref _isExportCacheBusy, value); RefreshExportCacheCommand.RaiseCanExecuteChanged(); CleanExportCacheCommand.RaiseCanExecuteChanged(); } }
    public DelegateCommand RefreshExportCacheCommand { get; private set; } = null!;
    public DelegateCommand CleanExportCacheCommand { get; private set; } = null!;
    private void InitializeExportCache()
    {
        RefreshExportCacheCommand = new(async () => await RefreshExportCacheAsync(), () => !IsExportCacheBusy);
        CleanExportCacheCommand = new(async () => await CleanExportCacheAsync(), () => !IsExportCacheBusy && !IsHistoryExporting);
    }
    public async Task RefreshExportCacheAsync()
    {
        if (IsExportCacheBusy) return;
        IsExportCacheBusy = true;
        try
        {
            var result = await _client.GetTaskHistoryExportCacheAsync(new(ExportCacheRetention), _navigation?.Token ?? default);
            Replace(ExportCacheItems, result.Items);
            ExportCacheMessage = $"后台缓存共 {result.TotalCount} 项 / {result.TotalBytes:N0} 字节；{result.RetentionDays} 天前可清理 {result.EligibleCount} 项。列表展示最近200项。";
        }
        catch (Exception error) { ExportCacheMessage = "缓存读取失败：" + error.Message; }
        finally { IsExportCacheBusy = false; }
    }
    public async Task CleanExportCacheAsync()
    {
        if (IsExportCacheBusy || IsHistoryExporting) return;
        IsExportCacheBusy = true;
        try
        {
            var retention = ExportCacheRetention;
            var result = await _client.CleanupTaskHistoryExportCacheAsync(new(retention), _navigation?.Token ?? default);
            var cache = await _client.GetTaskHistoryExportCacheAsync(new(retention), _navigation?.Token ?? default);
            Replace(ExportCacheItems, cache.Items); ExportCacheMessage = result.Message;
        }
        catch (Exception error) { ExportCacheMessage = "缓存清理失败：" + error.Message; }
        finally { IsExportCacheBusy = false; }
    }
}
