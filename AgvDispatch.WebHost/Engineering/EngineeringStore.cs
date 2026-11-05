using System.Security.Cryptography;
using System.Text.Json;

namespace AgvDispatch.WebHost.Engineering;

public sealed class EngineeringStore(string root)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(WebJson.Options) { WriteIndented = true };
    public static string Hash(EngineeringPlan plan) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan, WebJson.Options)));
    private static string Hash(JsonElement plan) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan, WebJson.Options)));
    private static string SafeId(string id) => Guid.TryParseExact(id, "N", out var g) ? g.ToString("N") : throw new ArgumentException("版本或报告编号无效。");
    private static string SafeTemplateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-'))
            throw new ArgumentException("模板编号无效。");
        return id;
    }
    private async Task<T?> Read<T>(string path, CancellationToken ct) => File.Exists(path) ? JsonSerializer.Deserialize<T>(await File.ReadAllBytesAsync(path, ct), Json) : default;
    private async Task Write<T>(string path, T data, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(data, Json), ct); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task<EngineeringDraft> Draft(CancellationToken ct) => await Read<EngineeringDraft>(Path.Combine(root, "draft.json"), ct) ?? new("", DateTimeOffset.MinValue, new());
    public async Task<EngineeringDraft> Save(EngineeringSave request, CancellationToken ct)
    {
        EngineeringValidator.CheckShape(request.Plan);
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(root);
            await using var fileLock = new FileStream(Path.Combine(root, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = await Draft(ct);
            if (current.Revision != request.ExpectedRevision) throw new ProjectConflictException("工程草稿已变化，请重新读取并比较；没有覆盖其他页面。");
            var next = new EngineeringDraft(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, request.Plan);
            // Draft history survives replacement, including invalid/incomplete drafts.
            await Write(Path.Combine(root, "drafts", next.Revision + ".json"), next, ct);
            await Write(Path.Combine(root, "draft.json"), next, ct); return next;
        }
        finally { gate.Release(); }
    }
    public async Task<EngineeringVersion> Publish(EngineeringPublish request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(root);
            await using var fileLock = new FileStream(Path.Combine(root, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var draft = await Draft(ct);
            if (draft.Revision == "" || draft.Revision != request.ExpectedRevision) throw new ProjectConflictException("请先保存当前草稿，再按同一修订发布。");
            if (string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 1024) throw new ArgumentException("请填写本次发布说明（1–1024字）。");
            var validation = EngineeringValidator.Validate(draft.Plan);
            if (!validation.Valid) throw new ArgumentException("发布校验未通过：" + string.Join("；", validation.Issues.Take(8).Select(x => x.ObjectId + "：" + x.Message)));
            var version = new EngineeringVersion(Guid.NewGuid().ToString("N"), draft.Revision, DateTimeOffset.UtcNow, request.Note.Trim(), draft.Plan, Hash(draft.Plan));
            await Write(Path.Combine(root, "versions", version.Id + ".json"), version, ct); return version;
        }
        finally { gate.Release(); }
    }
    public async Task<EngineeringVersion> Version(string id, CancellationToken ct)
    {
        var path = Path.Combine(root, "versions", SafeId(id) + ".json");
        var v = await Read<EngineeringVersion>(path, ct) ?? throw new KeyNotFoundException("工程版本不存在。");
        if (v.Id != id) throw new InvalidDataException("工程版本摘要不一致。");
        if (v.ContentHash != Hash(v.Plan))
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, ct));
            if (!document.RootElement.TryGetProperty("plan", out var rawPlan) || v.ContentHash != Hash(rawPlan))
                throw new InvalidDataException("工程版本摘要不一致。");
            v = v with { RawPlanHashVerified = true };
        }
        return v;
    }
    public async Task<object[]> Versions(CancellationToken ct)
    {
        var dir = Path.Combine(root, "versions"); if (!Directory.Exists(dir)) return [];
        var rows = new List<object>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(200))
        {
            try
            {
                var v = await Version(Path.GetFileNameWithoutExtension(file), ct);
                rows.Add(new { v.Id, v.DraftRevision, v.PublishedAt, v.Note, v.ContentHash, v.Plan.Name, Stations = v.Plan.Stations.Count, Vehicles = v.Plan.Vehicles.Count });
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException)
            {
                // One legacy or damaged snapshot must not hide every valid version.
                // Direct Version(id) and runtime apply still reject the damaged entry.
            }
        }
        return rows.ToArray();
    }
    public async Task<EngineeringSimulationReport> Simulate(EngineeringSimulationRequest request, CancellationToken ct)
    {
        var version = await Version(request.VersionId, ct);
        var report = EngineeringSimulator.Run(version, request);
        await Write(Path.Combine(root, "reports", report.Id + ".json"), report, ct); return report;
    }
    public async Task<EngineeringSimulationReport> Report(string id, CancellationToken ct) => await Read<EngineeringSimulationReport>(Path.Combine(root, "reports", SafeId(id) + ".json"), ct) ?? throw new KeyNotFoundException("模拟报告不存在。");
    public async Task<EngineeringTemplateLibraryEntry[]> TemplateLibrary(CancellationToken ct)
    {
        var dir = Path.Combine(root, "template-library");
        if (!Directory.Exists(dir)) return [];
        var rows = new List<EngineeringTemplateLibraryEntry>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).Take(1000))
        {
            var entry = await Read<EngineeringTemplateLibraryEntry>(file, ct);
            if (entry is null) continue;
            var normalized = EngineeringTaskTemplates.Normalize(entry.Template);
            var hash = EngineeringTaskTemplates.Hash(normalized);
            rows.Add(entry.ContentHash == hash ? entry : entry with { Template = normalized, ContentHash = hash });
        }
        return rows.ToArray();
    }
    public async Task<EngineeringTemplateLibraryEntry> SaveTemplate(EngineeringTemplate template, CancellationToken ct)
    {
        var normalized = EngineeringTaskTemplates.Normalize(template);
        var issues = EngineeringTaskTemplates.Validate(normalized);
        var errors = issues.Where(x => x.Severity == "Error").ToArray();
        if (errors.Length > 0) throw new ArgumentException("模板校验未通过：" + string.Join("；", errors.Select(x => x.Message)));
        var id = SafeTemplateId(normalized.Id);
        var hash = EngineeringTaskTemplates.Hash(normalized);
        await gate.WaitAsync(ct);
        try
        {
            var path = Path.Combine(root, "template-library", id + ".v" + normalized.Version + ".json");
            var existing = await Read<EngineeringTemplateLibraryEntry>(path, ct);
            if (existing is not null)
            {
                var normalizedExisting = EngineeringTaskTemplates.Normalize(existing.Template);
                if (EngineeringTaskTemplates.Hash(normalizedExisting) == hash)
                {
                    if (existing.ContentHash != hash)
                    {
                        var migrated = existing with { Template = normalizedExisting, ContentHash = hash };
                        await Write(path, migrated, ct);
                        return migrated;
                    }
                    return existing;
                }
                throw new ProjectConflictException("同一模板编号和版本已保存不同内容；请提升模板版本号，不能覆盖已保存版本。");
            }
            var entry = new EngineeringTemplateLibraryEntry(id + ".v" + normalized.Version, normalized, hash, DateTimeOffset.UtcNow);
            await Write(path, entry, ct);
            return entry;
        }
        finally { gate.Release(); }
    }
    public async Task<EngineeringTemplateLibraryEntry> ImportTemplate(EngineeringTemplateTransfer transfer, CancellationToken ct)
    {
        if (transfer.SchemaVersion != 1 || transfer.Template is null) throw new ArgumentException("模板导入文件格式无效。");
        var normalized = EngineeringTaskTemplates.Normalize(transfer.Template);
        var hash = EngineeringTaskTemplates.Hash(normalized);
        if (!string.IsNullOrWhiteSpace(transfer.ContentHash) &&
            !string.Equals(transfer.ContentHash, hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模板导入文件摘要不一致。");
        return await SaveTemplate(normalized, ct);
    }
}
