using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgvDispatch.Wpf.Management;

public sealed record ArchiveFile(string Path, long Length, string Sha256);
public sealed record SiteArchiveManifest(string Schema, string ReleaseId, DateTimeOffset CreatedAt, IReadOnlyList<ArchiveFile> Files);

public sealed class SiteArchives(SiteEnvironment site)
{
    public string CreateBackup()
    {
        var release = site.ReadRelease();
        using var lease = SiteOperations.LockStoppedSite(release, site.Root);
        if (!Directory.Exists(site.DataRoot)) throw new DirectoryNotFoundException("还没有日常运行数据，未创建空备份。");
        CheckDatabase(Path.Combine(site.DataRoot, "后台5199", "agv-dispatch.db"));
        Directory.CreateDirectory(site.BackupRoot);
        var destination = Path.Combine(site.BackupRoot, $"运行数据-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        var temporary = destination + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var entries = new List<ArchiveFile>();
                foreach (var file in EnumerateRegularFiles(site.DataRoot))
                {
                    var relative = "data/" + Path.GetRelativePath(site.DataRoot, file).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
                    entries.Add(new(relative, new FileInfo(file).Length, Hash(file)));
                }
                ValidateEntries(new("agv-site-backup-v1", release.ReleaseId, DateTimeOffset.UtcNow, entries), entries.Select(e => e.Path).Append("manifest.json").ToArray());
                var manifest = archive.CreateEntry("manifest.json");
                using var writer = new StreamWriter(manifest.Open());
                writer.Write(JsonSerializer.Serialize(new SiteArchiveManifest("agv-site-backup-v1", release.ReleaseId, DateTimeOffset.UtcNow, entries), SiteEnvironment.Json));
            }
            File.Move(temporary, destination);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string Restore(string archivePath)
    {
        var release = site.ReadRelease();
        using var lease = SiteOperations.LockStoppedSite(release, site.Root);
        // A fresh sibling is fully verified before touching the current runtime data.
        var staging = Path.Combine(site.Root, ".restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var rollback = Path.Combine(site.BackupRoot, $"恢复前运行数据-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var moved = false;
        try
        {
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("不是本工具生成的完整备份。");
                if (manifestEntry.Length > 16 * 1024 * 1024) throw new InvalidDataException("备份清单过大。");
                using var reader = new StreamReader(manifestEntry.Open());
                var manifest = JsonSerializer.Deserialize<SiteArchiveManifest>(reader.ReadToEnd(), SiteEnvironment.Json) ?? throw new InvalidDataException("备份清单为空。");
                if (manifest.Schema != "agv-site-backup-v1" || manifest.ReleaseId != release.ReleaseId)
                    throw new InvalidDataException("备份格式或版本不一致；跨版本恢复需先完成迁移核对。");
                ValidateEntries(manifest, archive.Entries.Select(e => e.FullName).ToArray());
                foreach (var item in manifest.Files)
                {
                    var entry = archive.GetEntry(item.Path)!;
                    if (entry.Length != item.Length) throw new InvalidDataException("备份长度不一致：" + item.Path);
                    var target = SiteEnvironment.ChildPath(staging, item.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target);
                    if (Hash(target) != item.Sha256) throw new InvalidDataException("备份哈希不一致：" + item.Path);
                }
            }
            var replacement = Path.Combine(staging, "data");
            CheckDatabase(Path.Combine(replacement, "后台5199", "agv-dispatch.db"));
            ProcessIdentity.RequireAllStopped(site.Root);
            // Refuse junctions rather than swapping a path that could point outside this site.
            if (Directory.Exists(site.DataRoot)) _ = EnumerateRegularFiles(site.DataRoot).Count();
            Directory.CreateDirectory(site.BackupRoot);
            if (Directory.Exists(site.DataRoot)) { Directory.Move(site.DataRoot, rollback); moved = true; }
            try { Directory.Move(replacement, site.DataRoot); }
            catch { if (moved && !Directory.Exists(site.DataRoot)) Directory.Move(rollback, site.DataRoot); throw; }
            return moved ? $"运行数据已恢复；恢复前数据保留在：{rollback}。未启动服务，请在启动后核对恢复屏障。" : "运行数据已恢复；未启动服务。";
        }
        finally
        {
            // staging is generated directly under the verified site root, never supplied by an archive.
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public static void ValidateEntries(SiteArchiveManifest manifest, IReadOnlyList<string> entryNames)
    {
        if (manifest.Files is null || manifest.Files.Count is < 1 or > 50000 || entryNames.Count != manifest.Files.Count + 1 ||
            entryNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entryNames.Count)
            throw new InvalidDataException("备份文件列表无效、重复或不完整。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(item.Path) || !item.Path.StartsWith("data/", StringComparison.Ordinal) ||
                item.Path.Contains('\\') || item.Path.Contains(':') || item.Path.Split('/').Any(p => p is ".." or "." or "" || p.EndsWith('.') || p.EndsWith(' ')) ||
                !seen.Add(item.Path) || !entryNames.Contains(item.Path, StringComparer.Ordinal) || item.Length < 0 || item.Length > 4L * 1024 * 1024 * 1024 ||
                item.Sha256 is null || item.Sha256.Length != 64 || !item.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("备份包含非法路径或文件描述。");
            total = checked(total + item.Length);
        }
        if (total > 10L * 1024 * 1024 * 1024) throw new InvalidDataException("备份超过10GiB恢复预算。");
    }

    public string ExportDiagnostics(string status, string journal)
    {
        Directory.CreateDirectory(site.DiagnosticRoot);
        var destination = Path.Combine(site.DiagnosticRoot, $"现场诊断-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        var temporary = destination + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                WriteText(archive, "状态.txt", $"采集时间：{DateTimeOffset.Now:O}\n{status}\n\n{SiteOperations.ReadNetwork()}");
                WriteText(archive, "管理操作.txt", journal);
                var release = site.ReadRelease();
                // Include bounded log tails, not raw database/configuration/credentials.
                var roots = new[] { Path.Combine(release.Bundle, "Service", "logs"), Path.Combine(site.DataRoot, "后台5199", "logs") };
                var logFiles = roots.Where(Directory.Exists).SelectMany(EnumerateRegularFiles).Where(p => Path.GetExtension(p).Equals(".log", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc).Take(12).ToArray();
                for (var i = 0; i < logFiles.Length; i++)
                    WriteText(archive, $"日志/{i:D2}-{Path.GetFileName(logFiles[i])}", ReadTail(logFiles[i]));
            }
            File.Move(temporary, destination);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string ReadTail(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        input.Position = Math.Max(0, input.Length - 256 * 1024);
        using var reader = new StreamReader(input);
        if (input.Position > 0) reader.ReadLine();
        return reader.ReadToEnd();
    }

    private static void WriteText(ZipArchive archive, string name, string content) { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(content); }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void CheckDatabase(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException("备份缺少日常数据库。");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA quick_check";
        if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal)) throw new InvalidDataException("数据库完整性检查未通过。");
    }

    private static IEnumerable<string> EnumerateRegularFiles(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("运行数据或日志包含链接目录，需人工核对后维护。");
        foreach (var file in Directory.EnumerateFiles(root))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("拒绝跟随链接文件。");
            yield return file;
        }
        foreach (var directory in Directory.EnumerateDirectories(root))
            foreach (var file in EnumerateRegularFiles(directory)) yield return file;
    }
}
