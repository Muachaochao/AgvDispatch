using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;

namespace AgvDispatch.Service;

/// <summary>Launches one fixed, auditable Python worker per persisted operation.
/// Observation renews a lease and reads evidence; it never launches/replays work.</summary>
public sealed class ScriptChargingWorkflow(ChargingScriptOptions settings, ChargingOptions policy,
    AgvOptions agvs, string dataDirectory, TimeProvider? clock = null) : IChargingWorkflow
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    private static readonly Regex Identifier = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$", RegexOptions.CultureInvariant);
    private DateTimeOffset Now => (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow();
    private string Root => Path.GetFullPath(Path.IsPathRooted(settings.EvidenceDirectory)
        ? settings.EvidenceDirectory : Path.Combine(dataDirectory, settings.EvidenceDirectory));

    public bool IsConfigured(ChargingSite site, string vehicleId)
    {
        try
        {
            if (!settings.Enabled || !site.Commissioned || site.ControlMode != "RobokitScript" ||
                settings.LeaseTimeoutSeconds is < 10 or > 60 ||
                string.IsNullOrWhiteSpace(settings.PythonExecutable)) return false;
            var profile = ReadProfile(site, vehicleId);
            using var json = JsonDocument.Parse(profile.Bytes);
            return json.RootElement.TryGetProperty("commissioned", out var commissioned) && commissioned.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
        { return false; }
    }

    public async Task StartAsync(ChargingSession session, string operationId, ChargingWorkflowKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Identifier.IsMatch(operationId) || operationId != session.ScriptOperationId || kind != session.ScriptOperationKind)
            throw new InvalidOperationException("脚本操作必须与已持久化的本次充电意图一致。");
        if (session.Site is not { } site || session.Route is not { } route || !IsConfigured(site, session.VehicleId))
            throw new InvalidOperationException("充电脚本及本车标定未启用或未现场核定。");
        if (!agvs.Enabled || !agvs.AllowCommands || agvs.GetConfigurationError() is { })
            throw new InvalidOperationException("AGV动作尚未启用或通信配置无效。");
        var robot = agvs.Robots.SingleOrDefault(r => r.Enabled && r.VehicleId.Equals(session.VehicleId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("当前车辆未配置启用的Robokit连接。");
        var binding = agvs.MapBindings.SingleOrDefault(b => b.VehicleId.Equals(session.VehicleId, StringComparison.OrdinalIgnoreCase) &&
            b.LocalMapId == route.MapId && b.LocalMapVersion == route.MapVersionId && b.CoordinatesVerified)
            ?? throw new InvalidOperationException("本次后台地图与车端地图没有核定的坐标绑定。");
        var profile = ReadProfile(site, session.VehicleId);
        using var parsed = JsonDocument.Parse(profile.Bytes);
        var p = parsed.RootElement;
        if (Required(p, "robotIp") != robot.IpAddress || Required(p, "robotMap") != binding.RobotMap ||
            !Required(p, "robotMapMd5").Equals(binding.RobotMapMd5, StringComparison.OrdinalIgnoreCase) ||
            Required(p, "approachSite") != site.ChargingSiteId)
            throw new InvalidOperationException("本车标定的IP、地图或接近站点与当前调度绑定不一致。");
        if (string.IsNullOrWhiteSpace(site.ChargerAddress) ||
            Required(p, "chargerIp") != site.ChargerAddress)
            throw new InvalidOperationException("本车标定的充电桩IP与充电位绑定的充电桩不一致。");
        if (kind == ChargingWorkflowKind.Undock && (!session.ShutdownVerified || string.IsNullOrWhiteSpace(session.ChargeOperationId)))
            throw new InvalidOperationException("原充电操作尚无已核验的停充证据，禁止退桩。");
        var tools = Path.GetFullPath(Path.IsPathRooted(settings.ToolsDirectory) ? settings.ToolsDirectory :
            Path.Combine(AppContext.BaseDirectory, settings.ToolsDirectory));
        var worker = Path.Combine(tools, "dispatch_worker.py");
        if (!File.Exists(worker)) throw new FileNotFoundException("未部署固定充电执行器。", worker);
        var directory = OperationDirectory(operationId);
        Directory.CreateDirectory(directory);
        var manifestPath = Path.Combine(directory, "manifest.json");
        // CreateNew is the durable launch barrier. Even a crash immediately
        // before Process.Start leaves an unknown intent which cannot be replayed.
        var manifest = new
        {
            operationId, sessionId = session.SessionId, kind, vehicleId = session.VehicleId,
            siteId = site.Id, robotIp = robot.IpAddress, profilePath = profile.Path,
            profileSha256 = Convert.ToHexString(SHA256.HashData(profile.Bytes)).ToLowerInvariant(),
            approachSite = site.ChargingSiteId, robotMap = binding.RobotMap, robotMapMd5 = binding.RobotMapMd5,
            leaseTimeoutSeconds = settings.LeaseTimeoutSeconds,
            maximumSessionSeconds = policy.ChargingTimeoutSeconds,
            maximumTemperature = policy.MaximumTemperature,
            targetBatteryPercent = session.TargetBatteryPercent ?? policy.TargetBatteryPercent,
            requiredPositiveSeconds = session.RequiredPositiveSeconds,
            chargeOperationId = session.ChargeOperationId, createdAt = Now,
        };
        await WriteNewAsync(manifestPath, manifest, ct);
        await AtomicWriteAsync(Path.Combine(directory, "lease.json"), new { operationId, updatedAt = Now }, ct);
        var start = new ProcessStartInfo(settings.PythonExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = tools,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(worker);
        start.ArgumentList.Add("--manifest");
        start.ArgumentList.Add(manifestPath);
        start.Environment["PYTHONUTF8"] = "1";
        try
        {
            var process = Process.Start(start) ?? throw new InvalidOperationException("充电执行器未能启动。");
            _ = CaptureProcessAsync(process, directory);
        }
        catch (Exception e)
        {
            await WriteNewAsync(Path.Combine(directory, "launch_error.json"), new { operationId, updatedAt = Now, message = e.Message }, CancellationToken.None);
            throw new InvalidOperationException("充电执行器启动未确认；保留原操作，禁止重发。", e);
        }
    }

    public async Task<ChargingWorkflowStatus> ObserveAsync(ChargingSession session, CancellationToken ct)
    {
        var id = session.ScriptOperationId ?? throw new InvalidOperationException("充电脚本操作身份缺失。");
        var kind = session.ScriptOperationKind ?? throw new InvalidOperationException("充电脚本阶段缺失。");
        var directory = OperationDirectory(id);
        ChargingWorkflowStatus Unknown(string text) => new(id, kind, ChargingWorkflowState.Unknown, Now, text) { EvidenceDirectory = directory };
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath)) return Unknown("原执行器发送记录不存在；禁止自动补发。");
        using var manifest = JsonDocument.Parse(await ReadSharedAsync(manifestPath, ct));
        if (Required(manifest.RootElement, "operationId") != id || Required(manifest.RootElement, "sessionId") != session.SessionId ||
            Required(manifest.RootElement, "vehicleId") != session.VehicleId || Required(manifest.RootElement, "kind") != kind.ToString())
            return Unknown("执行器记录与原充电会话不一致。");
        var finalPath = Path.Combine(directory, "result.json");
        var path = File.Exists(finalPath) ? finalPath : Path.Combine(directory, "worker_state.json");
        if (!File.Exists(path))
        {
            if (File.Exists(Path.Combine(directory, "launch_error.json")) || Now - File.GetCreationTimeUtc(manifestPath) > TimeSpan.FromSeconds(settings.LeaseTimeoutSeconds))
                return Unknown("原执行器尚未提供启动或终态证据；禁止重发。");
            await AtomicWriteAsync(Path.Combine(directory, "lease.json"), new { operationId = id, updatedAt = Now }, ct);
            return new(id, kind, ChargingWorkflowState.Running, Now, "等待原执行器启动反馈。") { EvidenceDirectory = directory };
        }
        if (new FileInfo(path).Length > 1024 * 1024) return Unknown("执行器反馈超出允许大小。");
        var status = JsonSerializer.Deserialize<ChargingWorkflowStatus>(await ReadSharedAsync(path, ct), Json);
        if (status is null || status.OperationId != id || status.Kind != kind || !Enum.IsDefined(status.State) ||
            !double.IsFinite(status.ContinuousPositiveSeconds) || status.ContinuousPositiveSeconds < 0 || status.UpdatedAt > Now.AddSeconds(2))
            return Unknown("执行器反馈身份、时间或数值无效。");
        if (!File.Exists(finalPath) && status.State != ChargingWorkflowState.Running)
            return Unknown("尚未取得原执行器持久化终态，继续保留资源。");
        if (status.State == ChargingWorkflowState.Running)
        {
            if (Now - status.UpdatedAt > TimeSpan.FromSeconds(settings.LeaseTimeoutSeconds))
                return Unknown("执行器采样过期；保留原操作及充电占用。");
            await AtomicWriteAsync(Path.Combine(directory, "lease.json"), new { operationId = id, updatedAt = Now }, ct);
        }
        return status;
    }

    public async Task RequestStopAsync(ChargingSession session, CancellationToken ct)
    {
        var id = session.ScriptOperationId ?? throw new InvalidOperationException("没有原脚本操作可停止。");
        var directory = OperationDirectory(id);
        if (!File.Exists(Path.Combine(directory, "manifest.json"))) throw new InvalidOperationException("原执行器记录缺失，须现场核对。");
        var stop = Path.Combine(directory, "stop.json");
        if (File.Exists(stop)) return; // One persisted stop request, no new ON or motion.
        await WriteNewAsync(stop, new { operationId = id, updatedAt = Now, reason = "dispatch_stop" }, ct);
    }

    private (string Path, byte[] Bytes) ReadProfile(ChargingSite site, string vehicleId)
    {
        var match = settings.Profiles.SingleOrDefault(p => p.SiteId.Equals(site.Id, StringComparison.OrdinalIgnoreCase) &&
            p.VehicleId.Equals(vehicleId, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("本车与本桩尚无独立标定配置。");
        var path = Path.GetFullPath(Path.IsPathRooted(match.ProfilePath) ? match.ProfilePath : Path.Combine(dataDirectory, match.ProfilePath));
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("充电标定配置过大。");
        var bytes = File.ReadAllBytes(path);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (Required(root, "schema") != "agv-dispatch-charge-profile-v1" || Required(root, "vehicleId") != vehicleId || Required(root, "siteId") != site.Id)
            throw new InvalidDataException("充电标定格式或车辆/充电位身份不匹配。");
        return (path, bytes);
    }

    private string OperationDirectory(string id) => Identifier.IsMatch(id) ? Path.Combine(Root, id) : throw new InvalidDataException("脚本操作ID无效。");
    private static string Required(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new InvalidDataException("充电记录缺失字段：" + key);

    private static async Task WriteNewAsync(string path, object value, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, value, Json, ct);
        await stream.FlushAsync(ct); stream.Flush(true);
    }
    private static async Task AtomicWriteAsync(string path, object value, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await WriteNewAsync(temporary, value, ct); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<byte[]> ReadSharedAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > 1024 * 1024) throw new InvalidDataException("执行器记录超出允许大小。");
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, ct);
        return memory.ToArray();
    }
    private static async Task CaptureProcessAsync(Process process, string directory)
    {
        try
        {
            // Drain both streams concurrently without keeping unbounded output
            // in memory. The fixed worker normally writes no console output.
            await Task.WhenAll(DrainAsync(process.StandardOutput, Path.Combine(directory, "stdout.log")),
                DrainAsync(process.StandardError, Path.Combine(directory, "stderr.log")), process.WaitForExitAsync());
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
        finally { process.Dispose(); }
    }
    private static async Task DrainAsync(StreamReader reader, string path)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        var buffer = new char[4096]; var kept = 0;
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            var allowed = Math.Min(count, Math.Max(0, 1024 * 1024 - kept));
            if (allowed > 0) await writer.WriteAsync(buffer.AsMemory(0, allowed));
            kept += allowed;
        }
    }
}
