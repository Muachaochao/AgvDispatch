using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using Microsoft.Data.Sqlite;

namespace AgvDispatch.Wpf.Management;

public sealed class SiteOperations(SiteEnvironment site)
{
    public async Task<string> LaunchAsync(bool stop, CancellationToken ct = default)
    {
        var release = site.ReadRelease();
        if (!stop) return await RunLauncherAsync(release, false, ct);
        var session = ProcessIdentity.Read(release);
        if (session is null || (!ProcessIdentity.Matches(session.Service) && !ProcessIdentity.Matches(session.Web))) return "当前版本没有正在运行的受管进程。";
        if (!ProcessIdentity.Matches(session.Service)) return await RunLauncherAsync(release, true, ct);

        var client = new LocalBackendClient(release.PipeName);
        var status = await client.ReadStatusAsync(ct);
        ValidateIdleStatus(status, session.Service.Id);
        ValidateCharging(await client.ReadChargingAsync(ct));
        if (!File.Exists(site.DatabasePath)) throw new InvalidOperationException("日常数据库不存在，无法核实任务及路权，未停止程序。");
        await using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = site.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 3 }.ToString());
        await database.OpenAsync(ct);
        // Freeze task/lease/outbox writes between the idle check and process termination.
        // Without this fence, a new PLC call could dispatch after the status observation.
        using var transaction = database.BeginTransaction(deferred: false);
        using var query = database.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT (SELECT COUNT(*) FROM dispatch_task WHERE Stage NOT IN (90,110)) + (SELECT COUNT(*) FROM traffic_resource_lease) + (SELECT COUNT(*) FROM agv_command_outbox WHERE Status IN (0,10,20))";
        if (Convert.ToInt64(await query.ExecuteScalarAsync(ct)) != 0) throw new InvalidOperationException("仍有未结束/待复核任务、路权或待发送命令，请先在 Web 完成处理。未停止程序。");
        ValidateIdleStatus(await client.ReadStatusAsync(ct), session.Service.Id);
        ValidateCharging(await client.ReadChargingAsync(ct));
        return await RunLauncherAsync(release, true, ct);
    }

    public static void ValidateCharging(ChargingStatus status)
    {
        if (DateTimeOffset.UtcNow - status.CapturedAt > TimeSpan.FromSeconds(10) || status.Sessions.Any(s => s.Phase != ChargingPhase.Completed))
            throw new InvalidOperationException("仍有充电会话或充电状态未知，请先在 Web 处理完成。未停止程序。");
    }

    public static void ValidateIdleStatus(SystemStatusSnapshot status, int processId)
    {
        var age = DateTimeOffset.UtcNow - status.GeneratedAt;
        if (status.Service.ProcessId != processId || age < TimeSpan.FromSeconds(-2) || age > TimeSpan.FromSeconds(10))
            throw new InvalidOperationException("后台身份或状态时效不一致，未停止程序。");
        if (status.Tasks.ActiveCount != 0 || status.Tasks.WaitingCount != 0 || status.Tasks.RunningCount != 0 || status.Tasks.FaultedCount != 0)
            throw new InvalidOperationException("还有任务或异常待处理，请先在 Web 完成取消、恢复及清场。");
        if (status.Safety.AgvCommunicationEnabled && status.Agvs.Count == 0)
            throw new InvalidOperationException("车辆状态缺失，无法核实停稳。");
        foreach (var agv in status.Agvs)
        {
            if (!status.Safety.AgvCommunicationEnabled) break;
            if (agv.TaskCount != 0 || agv.Charging == true)
                throw new InvalidOperationException($"{agv.VehicleId} 仍有车辆任务或正在充电，未停止程序。");
            // Closing an idle software service does not assert that an offline vehicle is stopped.
            // Persistent task/lease/outbox checks below fence all dispatch obligations.
            if (agv.Connection is not (ConnectionState.Online or ConnectionState.Degraded)) continue;
            if (agv.Quality != SignalQuality.Good ||
                agv.LastSuccessfulPollAt is not { } at || DateTimeOffset.UtcNow - at > TimeSpan.FromSeconds(5) ||
                agv.TaskCount != 0 || !Stopped(agv.VelocityX) || !Stopped(agv.VelocityY) || !Stopped(agv.AngularVelocity) || agv.Charging == true)
                throw new InvalidOperationException($"{agv.VehicleId} 尚未确认空闲停稳，或正在充电，未停止程序。");
        }
    }

    private static bool Stopped(double? value) => value is { } number && double.IsFinite(number) && Math.Abs(number) <= .02;

    private async Task<string> RunLauncherAsync(ReleaseInfo release, bool stop, CancellationToken ct)
    {
        var info = new ProcessStartInfo(release.Launcher) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(release.Launcher)!, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--launch-local", "--bundle-root", release.Bundle, "--daily-data", "--daily-launch-settings", site.SettingsPath, "--project-store-path", Path.Combine(site.DataRoot, "Web工程方案"), "--no-browser" }) info.ArgumentList.Add(arg);
        if (stop) info.ArgumentList.Add("--stop");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动日常管理入口。");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        // A timeout never authorizes killing a launcher that may already own live services.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token);
        var text = (await output + "\n" + await error).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException(text);
        return stop ? "已停止当前版本记录的后台和 Web。" : "系统已启动，后台与 Web 已通过日常入口就绪检查。";
    }

    public void SaveConcurrency(int maximum)
    {
        if (maximum is < 1 or > 1000) throw new ArgumentException("并发任务数必须为1～1000。");
        var release = site.ReadRelease();
        using var lease = LockStoppedSite(release, site.Root);
        var settings = JsonNode.Parse(File.ReadAllText(site.SettingsPath))!.AsObject();
        settings["maxConcurrentTasks"] = maximum; // Keep production and all execution permissions unchanged.
        Directory.CreateDirectory(site.BackupRoot);
        var before = Path.Combine(site.BackupRoot, $"入口配置-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        File.Copy(site.SettingsPath, before);
        var temporary = site.SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, settings.ToJsonString(SiteEnvironment.Json)); File.Move(temporary, site.SettingsPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static FileStream LockStoppedSite(ReleaseInfo release, string siteRoot)
    {
        Directory.CreateDirectory(Path.Combine(release.Bundle, "Runtime"));
        var lease = new FileStream(Path.Combine(release.Bundle, "Runtime", "launcher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { ProcessIdentity.RequireAllStopped(siteRoot); return lease; }
        catch { lease.Dispose(); throw; }
    }

    public static string ReadNetwork()
    {
        var lines = new List<string>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            foreach (var address in adapter.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                lines.Add($"{adapter.Name}    {address.Address}    掩码 {address.IPv4Mask}");
        return lines.Count == 0 ? "未检测到已连接的 IPv4 网卡。" : string.Join(Environment.NewLine, lines);
    }

    public static async Task<string> ProbeAsync(string address, int port)
    {
        if (!IPAddress.TryParse(address, out var ip) || port is < 1 or > 65535) throw new ArgumentException("请输入有效 IP 地址和1～65535端口。");
        using var socket = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();
        try { await socket.ConnectAsync(ip, port, timeout.Token); return $"{address}:{port} TCP 可达，{watch.ElapsedMilliseconds} ms。仅验证端口连接，不代表协议、点位或业务握手正常。"; }
        catch (Exception e) when (e is SocketException or OperationCanceledException) { return $"{address}:{port} 连接失败：{(e is OperationCanceledException ? "3秒超时" : e.Message)}"; }
    }
}
