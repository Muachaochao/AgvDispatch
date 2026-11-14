using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgvDispatch.Wpf.Management;

public sealed class SiteEnvironment
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string Root { get; }
    public string SettingsPath => Path.Combine(Root, "最新版Web.json");
    public string DataRoot => Path.Combine(Root, "运行数据");
    public string DatabasePath => Path.Combine(DataRoot, "后台5199", "agv-dispatch.db");
    public string BackupRoot => Path.Combine(Root, "现场管理备份");
    public string DiagnosticRoot => Path.Combine(Root, "现场诊断");
    public const string WebUrl = "http://127.0.0.1:5199/";

    public SiteEnvironment(string root)
    {
        Root = Path.GetFullPath(root);
        if (!File.Exists(SettingsPath)) throw new FileNotFoundException("未找到最新版Web.json。", SettingsPath);
    }

    public static SiteEnvironment Find(string? explicitRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return new(explicitRoot);
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "最新版Web.json"))) return new(directory.FullName);
                if (File.Exists(Path.Combine(directory.FullName, "发布包", "最新版Web.json"))) return new(Path.Combine(directory.FullName, "发布包"));
            }
        throw new DirectoryNotFoundException("未找到日常发布包入口。");
    }

    public ReleaseInfo ReadRelease()
    {
        var settings = JsonNode.Parse(File.ReadAllText(SettingsPath))?.AsObject() ?? throw new InvalidDataException("日常入口配置为空。");
        var relative = settings["directory"]?.GetValue<string>() ?? throw new InvalidDataException("未配置日常版本目录。");
        var bundle = ChildPath(Root, relative);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundle, "release-manifest.json")));
        var manifest = document.RootElement;
        if (manifest.GetProperty("schema").GetString() != "agv-commissioning-release" || manifest.GetProperty("port").GetInt32() != 5199)
            throw new InvalidDataException("日常版本必须为正式发布清单格式且端口为5199。");
        var release = manifest.GetProperty("releaseId").GetString()!;
        if (settings["releaseId"]?.GetValue<string>() != release || settings["url"]?.GetValue<string>() != WebUrl)
            throw new InvalidDataException("日常入口与发布清单版本或地址不一致。");
        var launcher = Path.Combine(bundle, "Web", "host", "AgvDispatch.WebHost.exe");
        var service = Path.Combine(bundle, "Service", "AgvDispatch.Service.exe");
        foreach (var executable in new[] { launcher, service })
        {
            var dll = Path.ChangeExtension(executable, ".dll");
            if (!File.Exists(executable) || !File.Exists(dll) || FileVersionInfo.GetVersionInfo(dll).ProductVersion?.Split('+')[0] != release)
                throw new InvalidDataException("发布组件缺失或版本不一致：" + executable);
        }
        return new(bundle, release, manifest.GetProperty("pipeName").GetString()!, launcher, service,
            settings["productionDispatch"]?.GetValue<bool>() ?? false, settings["maxConcurrentTasks"]?.GetValue<int>() ?? 1);
    }

    public PaiceSimulationInfo? ReadPaiceSimulation()
    {
        var settings = JsonNode.Parse(File.ReadAllText(SettingsPath))?.AsObject() ?? throw new InvalidDataException("日常入口配置为空。");
        if (settings["paiceSimulation"] is not JsonObject paice) return null;
        var directory = PaicePath(paice["directory"]?.GetValue<string>(), "directory");
        var start = PaicePath(paice["startScript"]?.GetValue<string>(), "startScript");
        var stop = PaicePath(paice["stopScript"]?.GetValue<string>(), "stopScript");
        var url = paice["url"]?.GetValue<string>();
        if (!Directory.Exists(directory)) throw new InvalidDataException("排策智演目录不存在：" + directory);
        if (!File.Exists(start) || !File.Exists(stop)) throw new InvalidDataException("排策智演启动或停止脚本不存在。");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || uri.Host != "127.0.0.1" || uri.Port != 5270)
            throw new InvalidDataException("排策智演地址必须为 http://127.0.0.1:5270/。");
        return new(directory, start, stop, uri.AbsoluteUri);
    }

    private string PaicePath(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"paiceSimulation.{name}未配置。");
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(Root, value));
    }

    public static string ChildPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("路径必须为目录内的相对路径。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径超出指定目录。");
        return full;
    }
}

public sealed record ReleaseInfo(string Bundle, string ReleaseId, string PipeName, string Launcher, string Service, bool ProductionDispatch, int MaxConcurrentTasks)
{
    public string SessionPath => Path.Combine(Bundle, "Runtime", "launcher-session.json");
}

public sealed record PaiceSimulationInfo(string Directory, string StartScript, string StopScript, string Url)
{
    public string RuntimeDirectory => Path.Combine(Directory, ".runtime");
    public string ProcessRecordPath => Path.Combine(RuntimeDirectory, "processes.json");
}

public sealed record OwnedProcess(int Id, long StartedUtcTicks, string Path);
public sealed record ManagedSession(string ReleaseId, OwnedProcess Service, OwnedProcess Web);

public static class ProcessIdentity
{
    public static bool Matches(OwnedProcess? owner)
    {
        if (owner is null) return false;
        try
        {
            using var process = Process.GetProcessById(owner.Id);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == owner.StartedUtcTicks &&
                string.Equals(process.MainModule?.FileName, owner.Path, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public static ManagedSession? Read(ReleaseInfo release)
    {
        if (!File.Exists(release.SessionPath)) return null;
        var session = JsonSerializer.Deserialize<ManagedSession>(File.ReadAllText(release.SessionPath), SiteEnvironment.Json) ?? throw new InvalidDataException("启动会话为空。");
        if (session.ReleaseId != release.ReleaseId || !string.Equals(session.Service.Path, release.Service, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(session.Web.Path, release.Launcher, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("会话进程身份与当前版本不一致，拒绝管理其他版本。");
        return session;
    }

    public static void RequireAllStopped(string siteRoot)
    {
        foreach (var name in new[] { "AgvDispatch.Service", "AgvDispatch.WebHost" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    if (process.HasExited) continue;
                    string? image;
                    try { image = process.MainModule?.FileName; }
                    catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("无法核实后台进程的数据归属，拒绝维护。"); }
                    if (image is null || image.StartsWith(Path.GetFullPath(siteRoot).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("请先停止本发布包下的后台和 Web，再进行配置或数据维护；检测到：" + name);
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
}
