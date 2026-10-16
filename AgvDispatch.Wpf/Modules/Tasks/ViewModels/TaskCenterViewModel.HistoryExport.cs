using System.IO;
using System.Security.Cryptography;
using AgvDispatch.Application.Abstractions;
using Microsoft.Win32;
using Prism.Commands;

namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class TaskCenterViewModel
{
    private CancellationTokenSource? _historyExportRequest;
    private bool _isHistoryExporting;
    private string _historyExportId = "", _historyExportProgressText = "";
    private double _historyExportProgress;
    public double HistoryExportProgress { get => _historyExportProgress; private set => SetProperty(ref _historyExportProgress, value); }
    public string HistoryExportProgressText { get => _historyExportProgressText; private set => SetProperty(ref _historyExportProgressText, value); }
    public DelegateCommand CancelHistoryExportCommand { get; private set; } = null!;
    private string _historyExportMessage = "", _historyExportFile = "";
    public bool IsHistoryExporting { get => _isHistoryExporting; private set { SetProperty(ref _isHistoryExporting, value); RaisePropertyChanged(nameof(HistoryExportSummary)); UpdateHistoryCommands(); } }
    public string HistoryExportMessage { get => _historyExportMessage; private set { SetProperty(ref _historyExportMessage, value); RaisePropertyChanged(nameof(HasHistoryExportMessage)); RaisePropertyChanged(nameof(HistoryExportSummary)); } }
    public bool HasHistoryExportMessage => !string.IsNullOrEmpty(HistoryExportMessage);
    public string HistoryExportFile { get => _historyExportFile; private set { SetProperty(ref _historyExportFile, value); RaisePropertyChanged(nameof(HistoryExportSummary)); } }
    public string HistoryExportSummary => IsHistoryExporting ? "正在导出…" : HistoryExportFile != "" ? "导出已保存（点击查看路径与校验信息）" :
        HistoryExportMessage.StartsWith("导出失败", StringComparison.Ordinal) ? "导出失败（点击查看原因）" : "导出结果（点击查看详情）";
    public DelegateCommand ExportHistoryCommand { get; private set; } = null!;
    private void InitializeHistoryExport()
    {
        CancelHistoryExportCommand = new(() => { _historyExportRequest?.Cancel(); UpdateHistoryCommands(); }, () => IsHistoryExporting && _historyExportRequest != null && !_historyExportRequest.IsCancellationRequested);
        InitializeExportCache();
        ExportHistoryCommand = new(async () =>
        {
            var picker = new SaveFileDialog { Filter = "任务处置历史导出包 (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true,
                FileName = $"任务处置历史-{DateTime.Now:yyyyMMdd-HHmmss}.zip", OverwritePrompt = true };
            if (picker.ShowDialog() == true) await ExportHistoryToAsync(picker.FileName);
        }, () => Detail != null && !IsHistoryBusy && !IsHistoryExporting);
    }
    // Captures the applied filter, including all pages; editable draft conditions are not used.
    public async Task ExportHistoryToAsync(string destination)
    {
        if (Detail == null || _navigation == null || _navigation.IsCancellationRequested || IsHistoryExporting) return;
        var query = _historyFilter with { TaskId = Detail.Task.TaskId, Page = 1, PageSize = 100 };
        var request = CancellationTokenSource.CreateLinkedTokenSource(_navigation.Token);
        _historyExportRequest = request;
        var exportId = Guid.NewGuid().ToString("N"); _historyExportId = exportId;
        HistoryExportProgress = 0; HistoryExportProgressText = "正在登记后台导出任务";
        IsHistoryExporting = true; HistoryExportFile = ""; HistoryExportMessage = "正在导出已应用筛选的全部记录与完整证据…";
        string? temporary = null;
        try
        {
            TaskHistoryExportResult result;
            using (var polling = CancellationTokenSource.CreateLinkedTokenSource(request.Token))
            {
                var progress = PollExportProgressAsync(exportId, polling.Token);
                try { result = await _client.ExportTaskHistoryAsync(new(query, exportId), request.Token).WaitAsync(request.Token); }
                finally { polling.Cancel(); await progress; }
            }
            request.Token.ThrowIfCancellationRequested();
            if (!result.Accepted) { HistoryExportMessage = result.Message; HistoryExportProgressText = result.Message; return; }
            HistoryExportProgress = 80; HistoryExportProgressText = "后台已完成，正在保存本地副本";
            var target = Path.GetFullPath(destination);
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var input = File.OpenRead(result.FilePath))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[131072]; long copied = 0; int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), request.Token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), request.Token); copied += read;
                    HistoryExportProgress = 80 + Math.Min(15, result.SizeBytes == 0 ? 15 : 15.0 * copied / result.SizeBytes);
                    HistoryExportProgressText = $"正在保存本地副本 {copied:N0} / {result.SizeBytes:N0} 字节";
                }
            }
            HistoryExportProgress = 95; HistoryExportProgressText = "正在校验本地副本";
            await using (var file = File.OpenRead(temporary))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, request.Token));
                if (file.Length != result.SizeBytes || !hash.Equals(result.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("导出包大小或SHA-256校验不一致，未保存到目标文件。");
            }
            request.Token.ThrowIfCancellationRequested();
            File.Move(temporary, target, true); temporary = null;
            HistoryExportFile = target;
            HistoryExportProgress = 100; HistoryExportProgressText = "导出包已校验并保存";
            HistoryExportMessage = $"已保存全部 {result.RecordCount} 条匹配记录与完整证据。请解压后打开 report.html；SHA-256：{result.Sha256}";
        }
        catch (OperationCanceledException)
        {
            HistoryExportProgressText = "本地保存已停止，正在确认后台取消";
            HistoryExportMessage = await ConfirmBackendCancellationAsync(exportId);
            HistoryExportProgressText = HistoryExportMessage;
        }
        catch (Exception error) { HistoryExportMessage = "导出失败，目标文件未替换：" + error.Message; HistoryExportProgressText = "导出失败"; }
        finally
        {
            if (temporary != null && File.Exists(temporary)) File.Delete(temporary);
            if (_historyExportRequest == request) { _historyExportRequest = null; IsHistoryExporting = false; }
            request.Dispose();
        }
    }
    private async Task PollExportProgressAsync(string id, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(300, ct);
                var progress = await _client.GetTaskHistoryExportProgressAsync(new(id), ct);
                if (_historyExportId != id || ct.IsCancellationRequested) return;
                HistoryExportProgress = Math.Max(HistoryExportProgress, progress.Percent * 0.8);
                HistoryExportProgressText = progress.Message;
                if (progress.IsTerminal) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (_historyExportId == id && !ct.IsCancellationRequested) HistoryExportProgressText = "后台进度暂不可用，仍在等待导出结果"; }
    }
    private async Task<string> ConfirmBackendCancellationAsync(string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var reply = await _client.CancelTaskHistoryExportAsync(new(id), timeout.Token);
            if (!reply.Accepted)
            {
                var finished = await _client.GetTaskHistoryExportProgressAsync(new(id), timeout.Token);
                return finished.Stage == "Ready" ? "本地导出已取消，目标文件未替换；后台包已生成并保留在缓存。" :
                    "本地导出已取消，目标文件未替换；" + finished.Message;
            }
            while (true)
            {
                var progress = await _client.GetTaskHistoryExportProgressAsync(new(id), timeout.Token);
                if (progress.IsTerminal) return progress.Stage == "Cancelled" ? "后台导出已取消并清理临时文件；目标文件未替换。" :
                    "本地导出已取消，目标文件未替换；" + progress.Message;
                await Task.Delay(300, timeout.Token);
            }
        }
        catch (Exception error) { return "本地保存已取消，目标文件未替换；后台取消尚未确认：" + error.Message; }
    }

}
