using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgvDispatch.Wpf.Management;

/// <summary>
/// What a site test actually depends on: program release, simulation scheme, map, PLC table and the
/// simulation instance that ran it. Instances are run records and change with every clean configuration;
/// the baseline therefore separates "instance renewed" (normal) from real drift of release/map/scheme.
/// </summary>
public sealed record SiteSnapshot(
    string ReleaseId,
    string? SourceCommit,
    string BundleDirectory,
    string? SimulationInstance,
    string? SchemeName,
    string? MapName,
    string? MapHash,
    string? PlcTableSha256,
    double? SimulationRate,
    string? SimulationRelease,
    DateTimeOffset CapturedAt,
    string? Note = null);

/// <summary>Simulation identity as reported live by the 5199 simulation API.</summary>
public sealed record SimulationIdentity(string? Instance, string? SchemeName, string? MapName, string? MapHash, string? MapVersion);

public sealed record BaselineComparison(bool HasBaseline, IReadOnlyList<string> Drift, IReadOnlyList<string> Info)
{
    public string Level => !HasBaseline ? "idle" : Drift.Count > 0 ? "warn" : "ok";
    public string State => !HasBaseline ? "未锁定基线" : Drift.Count > 0 ? $"与基线有 {Drift.Count} 项变化" : "与基线一致";
}

public sealed class SiteBaselineStore(SiteEnvironment site)
{
    public string BaselinePath => Path.Combine(site.DataRoot, "现场测试基线.json");
    public string HistoryRoot => Path.Combine(site.DataRoot, "现场测试基线-历史");

    /// <summary>Reads simulation-center/current-instance.json written by the simulation controller.</summary>
    public JsonObject? ReadSimulationPointer()
    {
        try
        {
            var settings = JsonNode.Parse(File.ReadAllText(site.SettingsPath))?.AsObject();
            var artifacts = settings?["simulation"]?["artifactsPath"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(artifacts) || !Path.IsPathFullyQualified(artifacts)) return null;
            var pointer = Path.Combine(artifacts, "simulation-center", "current-instance.json");
            if (!File.Exists(pointer) || new FileInfo(pointer).Length > 64 * 1024) return null;
            return JsonNode.Parse(File.ReadAllText(pointer))?.AsObject();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public SiteSnapshot Capture(ReleaseInfo release, SimulationIdentity? live)
    {
        var pointer = ReadSimulationPointer();
        string? Text(string name) => pointer?[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
        double? Number(string name) => pointer?[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
        return new(release.ReleaseId, release.SourceCommit, Path.GetRelativePath(site.Root, release.Bundle),
            live?.Instance ?? Text("rootName"), live?.SchemeName ?? Text("selectedSchemeName"),
            live?.MapName ?? Text("mapName"), live?.MapHash ?? Text("mapDefinitionHash"),
            Text("plcTableSha256"), Number("simulationRate"), Text("releaseId"), DateTimeOffset.Now);
    }

    public SiteSnapshot? Read()
    {
        if (!File.Exists(BaselinePath)) return null;
        try { return JsonSerializer.Deserialize<SiteSnapshot>(File.ReadAllText(BaselinePath), SiteEnvironment.Json); }
        catch (JsonException) { return null; }
    }

    public string Lock(SiteSnapshot snapshot, string? note)
    {
        Directory.CreateDirectory(site.DataRoot);
        Directory.CreateDirectory(HistoryRoot);
        if (File.Exists(BaselinePath))
            File.Copy(BaselinePath, Path.Combine(HistoryRoot, $"基线-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.json"));
        var text = JsonSerializer.Serialize(snapshot with { Note = string.IsNullOrWhiteSpace(note) ? snapshot.Note : note.Trim() }, SiteEnvironment.Json);
        var temporary = BaselinePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); File.Move(temporary, BaselinePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return BaselinePath;
    }

    public static BaselineComparison Compare(SiteSnapshot? baseline, SiteSnapshot current)
    {
        if (baseline is null) return new(false, [], ["尚未锁定现场测试基线；确认版本、方案与地图后点「锁定为基线」。"]);
        var drift = new List<string>();
        var info = new List<string>();
        void Check(string label, string? before, string? after)
        {
            if (!string.Equals(before ?? "", after ?? "", StringComparison.OrdinalIgnoreCase))
                drift.Add($"{label}：{Show(before)} → {Show(after)}");
        }
        Check("发布版本", baseline.ReleaseId, current.ReleaseId);
        if (baseline.SourceCommit is not null || current.SourceCommit is not null) Check("源码提交", Short(baseline.SourceCommit), Short(current.SourceCommit));
        Check("仿真方案", baseline.SchemeName, current.SchemeName);
        Check("地图", baseline.MapName, current.MapName);
        Check("地图摘要", Short(baseline.MapHash), Short(current.MapHash));
        Check("PLC 点表", Short(baseline.PlcTableSha256), Short(current.PlcTableSha256));
        if (baseline.SimulationRate != current.SimulationRate && current.SimulationRate is not null)
            drift.Add($"仿真倍率：{baseline.SimulationRate?.ToString() ?? "—"} → {current.SimulationRate}");
        if (!string.Equals(baseline.SimulationInstance, current.SimulationInstance, StringComparison.OrdinalIgnoreCase))
            info.Add(drift.Count == 0
                ? $"仿真实例已更新为 {Show(current.SimulationInstance)}；方案、地图与点表与基线相同，属于新一次运行，不算变化。"
                : $"仿真实例：{Show(baseline.SimulationInstance)} → {Show(current.SimulationInstance)}");
        info.Add($"基线锁定于 {baseline.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm}{(string.IsNullOrWhiteSpace(baseline.Note) ? "" : " · " + baseline.Note)}");
        return new(true, drift, info);
    }

    public string ExportRecord(SiteSnapshot current, SiteSnapshot? baseline, BaselineComparison comparison)
    {
        Directory.CreateDirectory(site.DiagnosticRoot);
        var path = Path.Combine(site.DiagnosticRoot, $"版本档案-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        var text = new StringBuilder();
        text.AppendLine("# 现场版本档案").AppendLine();
        text.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}　状态：**{comparison.State}**").AppendLine();
        text.AppendLine("| 项目 | 当前 | 基线 |").AppendLine("|---|---|---|");
        void Row(string label, string? now, string? then) => text.AppendLine($"| {label} | {Show(now)} | {Show(then)} |");
        Row("发布版本", current.ReleaseId, baseline?.ReleaseId);
        Row("源码提交", current.SourceCommit, baseline?.SourceCommit);
        Row("版本目录", current.BundleDirectory, baseline?.BundleDirectory);
        Row("仿真方案", current.SchemeName, baseline?.SchemeName);
        Row("地图", current.MapName, baseline?.MapName);
        Row("地图摘要", current.MapHash, baseline?.MapHash);
        Row("PLC 点表 SHA256", current.PlcTableSha256, baseline?.PlcTableSha256);
        Row("仿真倍率", current.SimulationRate?.ToString(), baseline?.SimulationRate?.ToString());
        Row("仿真实例", current.SimulationInstance, baseline?.SimulationInstance);
        Row("仿真程序版本", current.SimulationRelease, baseline?.SimulationRelease);
        text.AppendLine();
        foreach (var line in comparison.Drift) text.AppendLine("- 变化：" + line);
        foreach (var line in comparison.Info) text.AppendLine("- " + line);
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string? Short(string? hash) => string.IsNullOrWhiteSpace(hash) ? null : hash.Length > 12 ? hash[..12] : hash;
}
