using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgvDispatch.WebHost;

/// <summary>Owns one explicitly configured loopback simulation controller; never uses DispatchPipe.</summary>
public sealed class SimulationBridge : IAsyncDisposable
{
    private static readonly Regex InstanceNamePattern = new("^web-single-vehicle-[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HttpClient client;
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string? root, python, controller, artifactsRoot;
    private readonly int port;
    private readonly object bindingCacheGate = new();
    private (long Length, DateTime LastWriteUtc, string? Selected)? bindingSelectionCache;
    private Process? process;
    private bool disposed;
    private readonly StringBuilder diagnostics = new();
    public bool Enabled { get; }

    public SimulationBridge(IConfiguration configuration)
    {
        Enabled = configuration.GetValue("Web:Simulation:Enabled", false);
        client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            { Timeout = Timeout.InfiniteTimeSpan };
        if (!Enabled) return;
        root = RequiredAbsolute(configuration, "Root");
        python = RequiredAbsolute(configuration, "PythonPath");
        controller = RequiredAbsolute(configuration, "ControllerPath");
        port = configuration.GetValue("Web:Simulation:Port", 0);
        if (port is < 1024 or > 65535 || port == configuration.GetValue("Web:Port", 5199))
            throw new ArgumentException("仿真控制端口必须为1024至65535，且不能使用Web日常端口。");
        if (!File.Exists(python) || !File.Exists(controller)) throw new ArgumentException("仿真Python或控制器文件不存在。");
        var tool = Directory.GetParent(controller)!;
        if (!Path.GetFileName(controller).Equals("controller.py", StringComparison.OrdinalIgnoreCase) ||
            !tool.Name.Equals("SimulationLab", StringComparison.OrdinalIgnoreCase) ||
            tool.Parent?.Name.Equals("tools", StringComparison.OrdinalIgnoreCase) != true)
            throw new ArgumentException("仿真控制器必须是工程tools/SimulationLab/controller.py。");
        var artifacts = Path.Combine(tool.Parent.Parent!.FullName, "artifacts");
        var relative = Path.GetRelativePath(artifacts, root);
        if (relative.Contains(Path.DirectorySeparatorChar) || relative.Contains(Path.AltDirectorySeparatorChar) ||
            !relative.StartsWith("web-single-vehicle-", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("仿真目录必须是当前工程artifacts下独立的web-single-vehicle-*实验目录。");
        // Resolve lexical containment above and reject junction/symlink redirection in existing ancestors.
        foreach (var path in new[] { root, controller, python }) RejectReparseAncestors(path);
        client.BaseAddress = new Uri($"http://127.0.0.1:{port}");
        client.DefaultRequestHeaders.Add("X-Simulation-Token", token);
        artifactsRoot = artifacts;
    }

    public async Task<IResult> ReadBindingAsync(CancellationToken ct)
    {
        var binding = DiscoverBinding();
        var owned = process;
        if (owned is not { HasExited: false }) return Results.Json(binding);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var status = await client.GetAsync("/status", timeout.Token);
            status.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await status.Content.ReadAsStringAsync(timeout.Token));
            var active = document.RootElement.TryGetProperty("rootName", out var rootName) ? rootName.GetString() : null;
            if (!string.IsNullOrWhiteSpace(active))
                binding = binding with { SelectedInstance = active, SelectionSource = "running-controller" };
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            binding = binding with { Message = "控制器进程存在，但状态读取失败：" + error.Message };
        }
        return Results.Json(binding);
    }

    public async Task<IResult> StopControllerAsync(CancellationToken ct)
    {
        if (!Enabled) return Results.Json(DiscoverBinding() with { Message = "隔离仿真环境未启用。" });
        await gate.WaitAsync(ct);
        try
        {
            var owned = process;
            if (owned is not { HasExited: false })
            {
                process?.Dispose();
                process = null;
                return Results.Json(DiscoverBinding() with { ControllerRunning = false, Message = "仿真控制器未运行。" });
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                using var response = await client.PostAsync(
                    "/shutdown", new StringContent("{}", Encoding.UTF8, "application/json"), timeout.Token);
                await owned.WaitForExitAsync(timeout.Token);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
            {
                if (!owned.HasExited) owned.Kill();
                if (!owned.HasExited) await owned.WaitForExitAsync(CancellationToken.None);
            }
            owned.Dispose();
            process = null;
            return Results.Json(DiscoverBinding() with { ControllerRunning = false, Message = "仿真控制器及其受管仿真实例已停止。" });
        }
        finally { gate.Release(); }
    }

    public async Task<IResult> ForwardAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        if (!Enabled) return Results.Json(new { error = "隔离仿真环境未启用。" }, statusCode: 403);
        if ((method != HttpMethod.Get || !AllowedReadPath(path)) &&
            (method != HttpMethod.Post || path is not ("/start" or "/stop" or "/run" or "/abort" or "/control" or
                "/new-instance" or "/configure" or "/recover" or "/recover-release" or "/protocol-start" or "/protocol-stop" or
                "/equipment-start" or "/equipment-control" or "/equipment-stop" or "/scheme-save" or "/scheme-select" or
                "/scheme-start" or "/scheme-delete" or "/point-table-validate" or "/timeout-recovery-execute")))
            throw new ArgumentException("不支持的仿真操作。");
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await EnsureStartedAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, timeout.Token);
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            // A controller must not turn this local JSON API into a redirect or HTML bridge.
            using var parsed = JsonDocument.Parse(json);
            return Results.Content(parsed.RootElement.GetRawText(), "application/json", statusCode: (int)response.StatusCode);
        }
        finally { gate.Release(); }
    }

    public async Task<IResult> ForwardArchiveAsync(string path, string fileName, CancellationToken ct)
    {
        if (!Enabled) return Results.Json(new { error = "隔离仿真环境未启用。" }, statusCode: 403);
        if (!AllowedReadPath(path)) throw new ArgumentException("不支持的仿真证据读取路径。");
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await EnsureStartedAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await response.Content.LoadIntoBufferAsync(64 * 1024 * 1024, timeout.Token);
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                using var parsed = JsonDocument.Parse(bytes);
                // JsonElement owns the document lifetime. Materialize its JSON
                // before leaving the using scope or the HTTP result can fail
                // later with "Cannot access a disposed object".
                return Results.Content(parsed.RootElement.GetRawText(), "application/json",
                    statusCode: (int)response.StatusCode);
            }
            return Results.File(bytes, "application/zip", fileDownloadName: fileName);
        }
        finally { gate.Release(); }
    }

    private static bool AllowedReadPath(string path)
    {
        if (path is "/status" or "/live" or "/report" or "/replay" or "/point-table" or "/snapshot" or "/resources" or "/tasks" ||
            Regex.IsMatch(path, @"^/(report|replay|archive)\?id=[a-f0-9]{32}$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(path, @"^/task\?id=[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(path, @"^/timeout-recovery\?id=[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant))
            return true;
        if (!Uri.TryCreate("http://127.0.0.1" + path, UriKind.Absolute, out var uri) ||
            uri.AbsolutePath != "/tasks")
            return false;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (pair.Length != 2) return false;
            string key;
            string value;
            try
            {
                key = Uri.UnescapeDataString(pair[0]);
                value = Uri.UnescapeDataString(pair[1]);
            }
            catch (UriFormatException) { return false; }
            if (key is not ("filter" or "search" or "page" or "pageSize") || !values.TryAdd(key, value))
                return false;
        }

        if (values.TryGetValue("filter", out var filter) && filter is not ("All" or "Active" or "Completed" or "Failed"))
            return false;
        if (values.TryGetValue("search", out var search) && search.Length > 128)
            return false;
        if (values.TryGetValue("page", out var page) && (!int.TryParse(page, out var pageNumber) || pageNumber < 1))
            return false;
        if (values.TryGetValue("pageSize", out var pageSize) &&
            (!int.TryParse(pageSize, out var size) || size is < 1 or > 200))
            return false;
        return true;
    }

    private SimulationBindingSnapshot DiscoverBinding()
    {
        var templateRootName = root is null ? "" : Path.GetFileName(root);
        var selectionSource = "template-root";
        var selectedRoot = root;
        var message = "";
        if (Enabled && artifactsRoot is not null)
        {
            try
            {
                var selected = ReadSelectedRootName();
                if (selected is not null)
                {
                    var candidate = Path.Combine(artifactsRoot, selected);
                    if (ValidInstance(candidate))
                    {
                        selectedRoot = candidate;
                        selectionSource = "scheme-selection";
                    }
                }
                if (selectionSource != "scheme-selection")
                {
                    var latest = Directory.EnumerateDirectories(
                            artifactsRoot, "web-single-vehicle-*", SearchOption.TopDirectoryOnly)
                        .Where(ValidInstance)
                        .OrderByDescending(Directory.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (latest is not null)
                    {
                        selectedRoot = latest;
                        selectionSource = "latest-valid-instance";
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                                              InvalidDataException or ArgumentException)
            {
                message = "最新仿真实例读取失败：" + error.Message;
            }
        }
        var selectedName = selectedRoot is null ? null : Path.GetFileName(selectedRoot);
        var available = selectedRoot is not null && ValidInstance(selectedRoot);
        var metadata = available ? ReadInstanceMetadata(selectedRoot!) : default;
        return new SimulationBindingSnapshot(
            Enabled,
            process is { HasExited: false },
            port,
            templateRootName,
            selectedName,
            selectionSource,
            available,
            metadata.MapName,
            metadata.VehicleCount,
            metadata.StationCount,
            message);
    }

    private string? ReadSelectedRootName()
    {
        if (artifactsRoot is null || root is null) return null;
        var directory = Path.Combine(artifactsRoot, "simulation-center");
        if (!Directory.Exists(directory)) return null;
        RejectReparseAncestors(directory);
        var scope = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..16].ToLowerInvariant();
        var path = Path.Combine(directory, "schemes-" + scope + ".json");
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > 64 * 1024 * 1024)
            throw new InvalidDataException("仿真选择记录大小无效。");
        lock (bindingCacheGate)
        {
            if (bindingSelectionCache is { } cached &&
                cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
                return cached.Selected;
        }
        RejectReparseAncestors(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var selected = document.RootElement.TryGetProperty("selectedRoot", out var value) ? value.GetString() : null;
        var result = selected is not null && InstanceNamePattern.IsMatch(selected) ? selected : null;
        lock (bindingCacheGate)
            bindingSelectionCache = (info.Length, info.LastWriteTimeUtc, result);
        return result;
    }

    private bool ValidInstance(string path)
    {
        if (!Directory.Exists(path) || !InstanceNamePattern.IsMatch(Path.GetFileName(path))) return false;
        try
        {
            RejectReparseAncestors(path);
            return File.Exists(Path.Combine(path, "lab.json")) &&
                   File.Exists(Path.Combine(path, "service", "appsettings.json")) &&
                   File.Exists(Path.Combine(path, "webhost", "appsettings.json"));
        }
        catch (ArgumentException) { return false; }
    }

    private static InstanceMetadata ReadInstanceMetadata(string path)
    {
        try
        {
            var file = Path.Combine(path, "lab.json");
            if (new FileInfo(file).Length > 16 * 1024 * 1024) return default;
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var configuration = root.TryGetProperty("simulationConfiguration", out var config) ? config : default;
            var mapName = configuration.ValueKind == JsonValueKind.Object &&
                          configuration.TryGetProperty("mapName", out var map)
                ? map.GetString()
                : root.TryGetProperty("mapName", out var labMap) ? labMap.GetString() : null;
            var vehicles = configuration.ValueKind == JsonValueKind.Object &&
                           configuration.TryGetProperty("vehicles", out var rows) &&
                           rows.ValueKind == JsonValueKind.Array
                ? rows.GetArrayLength()
                : root.TryGetProperty("initialSites", out var sites) &&
                  sites.ValueKind == JsonValueKind.Object
                    ? sites.EnumerateObject().Count()
                    : 0;
            var stations = configuration.ValueKind == JsonValueKind.Object &&
                           configuration.TryGetProperty("stations", out var stationRows) &&
                           stationRows.ValueKind == JsonValueKind.Array
                ? stationRows.GetArrayLength()
                : 0;
            return new InstanceMetadata(mapName, vehicles, stations);
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException)
        {
            return default;
        }
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (process is { HasExited: false }) return;
        process?.Dispose();
        process = null;
        // Never attach to or stop an existing server, even if it occupies our configured port.
        using (var probe = new TcpListener(IPAddress.Loopback, port))
        {
            try { probe.Start(); }
            catch (SocketException) { throw new InvalidOperationException("仿真控制端口已占用；不会连接或终止已有程序。"); }
        }
        var info = new ProcessStartInfo(python!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(controller!)!
        };
        foreach (var value in new[] { controller!, "--root", root!, "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            info.ArgumentList.Add(value);
        info.Environment["AGV_SIMULATION_TOKEN"] = token;
        diagnostics.Clear();
        process = new Process { StartInfo = info };
        process.OutputDataReceived += CaptureOutput;
        process.ErrorDataReceived += CaptureOutput;
        if (!process.Start()) throw new InvalidOperationException("仿真控制器启动失败。");
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("仿真控制器已退出：" + ReadDiagnostics());
                try
                {
                    using var pingTimeout = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);
                    pingTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                    using var ping = await client.GetAsync("/status", pingTimeout.Token);
                    if (ping.IsSuccessStatusCode) return;
                    if (ping.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                        throw new InvalidOperationException("仿真控制器认证失败；不会接管其他进程。");
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!startup.IsCancellationRequested) { }
                await Task.Delay(100, startup.Token);
            }
        }
        catch
        {
            // This process was created above and has not received any runtime operation.
            if (!process.HasExited) process.Kill();
            process.Dispose(); process = null;
            throw;
        }
    }

    private void CaptureOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        lock (diagnostics)
        {
            diagnostics.AppendLine(e.Data);
            if (diagnostics.Length > 4096) diagnostics.Remove(0, diagnostics.Length - 4096);
        }
    }

    private string ReadDiagnostics() { lock (diagnostics) return diagnostics.ToString(); }

    private static string RequiredAbsolute(IConfiguration configuration, string name)
    {
        var value = configuration[$"Web:Simulation:{name}"];
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new ArgumentException($"Web:Simulation:{name}必须为明确的绝对路径。");
        return Path.GetFullPath(value);
    }

    private static void RejectReparseAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("仿真路径不能经过符号链接或目录联接。");
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            if (process is { HasExited: false })
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    using var response = await client.PostAsync("/shutdown", new StringContent("{}", Encoding.UTF8, "application/json"), timeout.Token);
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException) { }
                // Only this owned controller process. The controller is responsible for its owned runtime children.
                if (!process.HasExited) process.Kill();
            }
            process?.Dispose(); process = null;
            client.Dispose();
        }
        finally { gate.Release(); }
    }
}

public sealed record SimulationBindingSnapshot(
    bool Enabled,
    bool ControllerRunning,
    int ControllerPort,
    string TemplateRootName,
    string? SelectedInstance,
    string SelectionSource,
    bool InstanceAvailable,
    string? MapName,
    int VehicleCount,
    int StationCount,
    string Message);

internal readonly record struct InstanceMetadata(string? MapName, int VehicleCount, int StationCount);
