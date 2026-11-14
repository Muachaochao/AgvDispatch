using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace AgvDispatch.Wpf.Management;

public enum PaiceOwnedRecordState { Missing, Stale, Live, Conflict, Invalid }

public sealed record PaiceSimulationObservation(
    bool Configured,
    bool FrontendReady,
    bool BackendReady,
    PaiceOwnedRecordState RecordState,
    string Message)
{
    public bool Running => FrontendReady && BackendReady;
    public string FrontendState => !Configured ? "未配置" : FrontendReady ? "已就绪 · 127.0.0.1:5270" : "未就绪";
    public string BackendState => !Configured ? "未配置" : BackendReady ? "健康 · 127.0.0.1:5271" : "未就绪";
}

public sealed class PaiceSimulationOperations(SiteEnvironment site)
{
    private sealed record OwnedProcess(int Id, string Path, long StartedUtcTicks);

    public PaiceSimulationInfo? Info => site.ReadPaiceSimulation();

    public async Task<PaiceSimulationObservation> ObserveAsync(CancellationToken ct = default)
    {
        var info = Info;
        if (info is null) return new(false, false, false, PaiceOwnedRecordState.Missing, "尚未配置排策智演。");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var front = ProbeAsync(http, info.Url, ct);
        var back = ProbeAsync(http, "http://127.0.0.1:5271/api/health", ct);
        await Task.WhenAll(front, back);
        var record = ClassifyOwnedRecord(info.ProcessRecordPath);
        var running = front.Result && back.Result;
        var message = running ? "排策智演前端与后端已就绪。"
            : front.Result ? "排策智演前端已就绪，后端未就绪。"
            : back.Result ? "排策智演后端已就绪，前端未就绪。"
            : record == PaiceOwnedRecordState.Stale ? "检测到失效启动记录，可安全清理后启动。"
            : "排策智演未运行。";
        return new(true, front.Result, back.Result, record, message);
    }

    public async Task<PaiceSimulationObservation> StartAsync(CancellationToken ct = default)
    {
        var info = Info ?? throw new InvalidOperationException("尚未配置排策智演。");
        var before = await ObserveAsync(ct);
        if (before.Running) return before;
        if (before.RecordState is PaiceOwnedRecordState.Live or PaiceOwnedRecordState.Conflict)
            throw new InvalidOperationException("排策智演启动记录仍有存活或冲突进程，拒绝重复启动。");
        RemoveStaleOwnedRecord(info.ProcessRecordPath);
        await RunScriptAsync(info.StartScript, TimeSpan.FromMinutes(12), ct);
        var after = await ObserveAsync(ct);
        if (!after.Running) throw new InvalidOperationException("启动脚本已结束，但排策智演前后端未同时就绪。");
        return after;
    }

    public async Task<PaiceSimulationObservation> StopAsync(CancellationToken ct = default)
    {
        var info = Info ?? throw new InvalidOperationException("尚未配置排策智演。");
        var before = await ObserveAsync(ct);
        if (before.RecordState == PaiceOwnedRecordState.Conflict || before.RecordState == PaiceOwnedRecordState.Invalid)
            throw new InvalidOperationException("排策智演启动记录身份异常，保留记录并要求人工核对。");
        if (!before.FrontendReady && !before.BackendReady)
        {
            if (before.RecordState == PaiceOwnedRecordState.Stale) RemoveStaleOwnedRecord(info.ProcessRecordPath);
            return await ObserveAsync(ct);
        }
        if (before.RecordState != PaiceOwnedRecordState.Live)
            throw new InvalidOperationException("排策智演端点在运行，但缺少有效的受管进程记录。");
        await RunScriptAsync(info.StopScript, TimeSpan.FromSeconds(90), ct);
        var after = await ObserveAsync(ct);
        if (after.FrontendReady || after.BackendReady) throw new InvalidOperationException("停止脚本已结束，但仍有排策智演端口在监听。");
        return after;
    }

    public static PaiceOwnedRecordState ClassifyOwnedRecord(string path)
    {
        if (!File.Exists(path)) return PaiceOwnedRecordState.Missing;
        try
        {
            var entries = JsonSerializer.Deserialize<OwnedProcess[]>(File.ReadAllText(path), SiteEnvironment.Json);
            if (entries is null || entries.Length == 0) return PaiceOwnedRecordState.Invalid;
            var live = 0;
            foreach (var entry in entries)
            {
                if (entry.Id <= 0 || string.IsNullOrWhiteSpace(entry.Path) || entry.StartedUtcTicks <= 0)
                    return PaiceOwnedRecordState.Invalid;
                try
                {
                    using var process = Process.GetProcessById(entry.Id);
                    if (process.HasExited) continue;
                    if (process.StartTime.ToUniversalTime().Ticks != entry.StartedUtcTicks ||
                        !string.Equals(process.MainModule?.FileName, entry.Path, StringComparison.OrdinalIgnoreCase))
                        return PaiceOwnedRecordState.Conflict;
                    live++;
                }
                catch (ArgumentException) { }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return PaiceOwnedRecordState.Conflict;
                }
            }
            return live > 0 ? PaiceOwnedRecordState.Live : PaiceOwnedRecordState.Stale;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return PaiceOwnedRecordState.Invalid;
        }
    }

    public static bool RemoveStaleOwnedRecord(string path)
    {
        var state = ClassifyOwnedRecord(path);
        if (state == PaiceOwnedRecordState.Missing) return false;
        if (state != PaiceOwnedRecordState.Stale)
            throw new InvalidOperationException("排策智演启动记录不是可安全清理的失效记录。");
        File.Delete(path);
        return true;
    }

    private static async Task<bool> ProbeAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { return false; }
    }

    private static async Task RunScriptAsync(string script, TimeSpan timeout, CancellationToken ct)
    {
        var powershell = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, "pwsh.exe"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe"))
            .FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("找不到 PowerShell 7（pwsh）；排策智演启停脚本需要 PowerShell 7.0。");
        var info = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(script)!
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(script);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动排策智演脚本。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        await process.WaitForExitAsync(deadline.Token);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"排策智演脚本执行失败，退出码 {process.ExitCode}；请查看 .runtime 日志。");
    }
}
