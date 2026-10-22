using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PlcHandshakeLab;

/// <summary>An owned read handle limited to the complete journal prefix captured under the lab lock.</summary>
public sealed class SessionCapture : IDisposable
{
    private readonly FileStream _source;
    public string JournalPath { get; }
    public long Length { get; }
    public string? ConfigJson { get; }
    public DateTimeOffset CapturedAt { get; } = DateTimeOffset.Now;
    public SessionCapture(string journalPath, string? configJson = null)
    {
        JournalPath = Path.GetFullPath(journalPath);
        _source = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Length = _source.Length;
        ConfigJson = configJson;
    }
    public static SessionCapture OpenHistory(string path)
    {
        var config = Path.ChangeExtension(path, ".config.json");
        // Only the explicit adjacent configuration is read, never a path from journal content.
        string? json = null;
        if (File.Exists(config) && new FileInfo(config).Length <= 4 * 1024 * 1024) json = File.ReadAllText(config);
        return new(path, json);
    }
    public Stream OpenPrefix()
    {
        _source.Position = 0;
        return new PrefixStream(_source, Length);
    }
    public void Dispose() => _source.Dispose();

    private sealed class PrefixStream(Stream source, long remaining) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (remaining == 0) return 0;
            var read = source.Read(buffer[..(int)Math.Min(remaining, buffer.Length)]);
            if (read == 0) throw new EndOfStreamException("源日志被截短，请保留原文件并重新选择会话。");
            remaining -= read; return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed record SessionGroup(string Station, int Round)
{
    public string Label => $"{Station} · 第{Round}轮";
}

public sealed class SessionHistory
{
    public NetworkLabConfig? Config { get; private set; }
    public List<StageTestResult> Results { get; } = [];
    public List<NetworkEvent> RecentEvents { get; } = [];
    public List<SessionGroup> Groups { get; } = [];
    public long EventCount { get; private set; }
    public long InvalidLines { get; private set; }
    public string SourceVersion { get; private set; } = "旧会话未记录版本";
    public string Warning { get; private set; } = "";
    public DateTimeOffset? FirstTime { get; private set; }
    public DateTimeOffset? LastTime { get; private set; }
    public static IEnumerable<(NetworkEvent? Event, string Raw)> ReadEvents(SessionCapture capture, CancellationToken token = default)
    {
        using var prefix = capture.OpenPrefix();
        using var reader = new StreamReader(prefix, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            NetworkEvent? item = null;
            try
            {
                item = JsonSerializer.Deserialize<NetworkEvent>(line);
                if (item is null || item.Time == default || item.Kind is null || item.Station is null || item.Point is null ||
                    item.Before is null || item.After is null || item.Detail is null || item.Stage is < 0 or > 28) item = null;
            }
            catch (JsonException) { }
            yield return (item, line);
        }
    }
    public static SessionHistory Read(SessionCapture capture, CancellationToken token = default)
    {
        var result = new SessionHistory();
        try
        {
            result.Config = JsonSerializer.Deserialize<NetworkLabConfig>(capture.ConfigJson ?? "null");
            result.Config?.Validate();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { result.Config = null; }
        if (result.Config is null) result.Warning = "缺少有效的原会话配置；不推测点表，证据包不生成调度点表。";
        var rounds = new Dictionary<string, int>();
        void Group(string station, int round)
        {
            var group = new SessionGroup(station, round);
            if (!result.Groups.Contains(group)) result.Groups.Add(group);
        }
        foreach (var station in result.Config?.Points.Where(p => p.Signal == 0).Select(p => p.Station) ?? []) Group(station, 1);
        foreach (var (item, _) in ReadEvents(capture, token))
        {
            if (item is null) { result.InvalidLines++; continue; }
            result.EventCount++;
            result.FirstTime ??= item.Time; result.LastTime = item.Time;
            result.RecentEvents.Add(item);
            if (result.RecentEvents.Count > 500) result.RecentEvents.RemoveAt(0);
            if (item.Kind == "工具版本") result.SourceVersion = item.Detail[(item.Detail.IndexOf('；') + 1)..];
            if (item.Station.Length > 0 && item.Station != NetworkLabConfig.Shared) Group(item.Station, rounds.GetValueOrDefault(item.Station, 1));
            if (item.Kind == "新一轮测试记录")
            {
                rounds[item.Station] = rounds.GetValueOrDefault(item.Station, 1) + 1;
                Group(item.Station, rounds[item.Station]);
            }
            if (item.Kind != "阶段测试") continue;
            try
            {
                var at = item.Detail.IndexOf('{');
                var test = at < 0 ? null : JsonSerializer.Deserialize<StageTestResult>(item.Detail[at..]);
                if (test is null || test.Time == default || test.Station != item.Station || test.Stage != item.Stage || test.Stage is < 1 or > 28 || test.Round < 1 || string.IsNullOrWhiteSpace(test.Station) ||
                    test.Outcome is not ("通过" or "失败" or "跳过") || test.Note is null || test.Vehicle is null || test.TaskId is null || test.Differences is null || test.Snapshot is null)
                    throw new JsonException("无效阶段结果");
                result.Results.Add(test); Group(test.Station, test.Round);
            }
            catch (JsonException) { result.InvalidLines++; }
        }
        if (result.InvalidLines > 0) result.Warning += $" 有{result.InvalidLines}条损坏/不完整记录，已跳过解析；原文仍保存在证据包，不能视为完整通过。";
        return result;
    }
    public void WriteReport(TextWriter report, CancellationToken token = default)
    {
        static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", "<br>");
        report.WriteLine("# PLC逐阶段测试记录\n\n只读记录；操作者结果不代替实际动作核验，未记录的阶段不算通过。");
        report.WriteLine($"\n原会话工具版本：{Cell(SourceVersion)}\n\n记录提示：{Cell(Warning.Length == 0 ? "无解析异常" : Warning)}\n");
        foreach (var group in Groups)
        {
            token.ThrowIfCancellationRequested();
            report.WriteLine($"## {Cell(group.Station)} · 第{group.Round}轮\n\n|阶段|名称|结果（最后记录）|\n|---|---|---|");
            for (var stage = 1; stage <= 28; stage++)
            {
                var last = Results.LastOrDefault(r => r.Station == group.Station && r.Round == group.Round && r.Stage == stage);
                report.WriteLine($"|{stage}|{Cell(Sequence.Stages[stage - 1].Name)}|{last?.Outcome ?? "未测"}|");
            }
            report.WriteLine();
        }
        report.WriteLine("## 全部记录（含复测）\n");
        foreach (var r in Results)
        { token.ThrowIfCancellationRequested(); report.WriteLine($"- {r.Time:O} {Cell(r.Station)} 第{r.Round}轮 / {r.Stage} / {r.Outcome}；车辆={Cell(r.Vehicle)}；任务={Cell(r.TaskId)}；{Cell(r.Note)}；偏差={Cell(r.Differences)}\n  - {Cell(r.Snapshot)}"); }
    }
}

public static class SessionExports
{
    public static void WriteCsv(SessionCapture capture, TextWriter writer, CancellationToken token = default)
    {
        static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        writer.WriteLine("时间,类型,工位,人工标记阶段,点位,原值,新值,说明");
        foreach (var (e, raw) in SessionHistory.ReadEvents(capture, token))
            writer.WriteLine(string.Join(",", (e is null ? new[] { "", "损坏记录（原文）", "", "0", "", "", "", raw } :
                new[] { e.Time.ToString("O", CultureInfo.InvariantCulture), e.Kind, e.Station, e.Stage.ToString(), e.Point, e.Before, e.After, e.Detail }).Select(Q)));
    }
    public static void Atomic(SessionCapture capture, string destination, Action<Stream> write, CancellationToken token)
    {
        destination = Path.GetFullPath(destination);
        if (string.Equals(destination, capture.JournalPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(destination, Path.ChangeExtension(capture.JournalPath, ".config.json"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("不能用导出文件覆盖原会话日志或配置。");
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { token.ThrowIfCancellationRequested(); write(stream); token.ThrowIfCancellationRequested(); stream.Flush(true); }
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Csv(SessionCapture capture, string destination, CancellationToken token = default) => Atomic(capture, destination, stream =>
    { using var writer = new StreamWriter(stream, new UTF8Encoding(true), 65536, true); WriteCsv(capture, writer, token); }, token);
    public static void Report(SessionCapture capture, string destination, CancellationToken token = default) => Atomic(capture, destination, stream =>
    { var history = SessionHistory.Read(capture, token); using var writer = new StreamWriter(stream, new UTF8Encoding(true), 65536, true); history.WriteReport(writer, token); }, token);

    public static void Bundle(SessionCapture capture, string destination, CancellationToken token = default) => Atomic(capture, destination, stream =>
    {
        var history = SessionHistory.Read(capture, token);
        var manifest = new List<object>();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, true, Encoding.UTF8);
        void Entry(string name, Action<Stream> write)
        {
            token.ThrowIfCancellationRequested();
            using var entry = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
            using var hash = new HashingStream(entry);
            write(hash); hash.Flush();
            manifest.Add(new { path = name, bytes = hash.Bytes, sha256 = hash.Digest() });
        }
        void Text(string name, Action<TextWriter> write) => Entry(name, target =>
        { using var writer = new StreamWriter(target, new UTF8Encoding(true), 65536, true); write(writer); });
        Entry("session.jsonl", target =>
        {
            using var prefix = capture.OpenPrefix(); var buffer = new byte[65536]; int read;
            while ((read = prefix.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); target.Write(buffer, 0, read); }
        });
        if (capture.ConfigJson is not null) Text("session.config.json", w => w.Write(capture.ConfigJson));
        if (history.Config is not null) Text("plc-signals.csv", w => w.Write(history.Config.ExportSignalCsv()));
        Text("session.csv", w => WriteCsv(capture, w, token));
        Text("stages.md", w => history.WriteReport(w, token));
        Entry("summary.json", target => JsonSerializer.Serialize(target, new
        {
            schema = "plc-lab-evidence-v1", capture.CapturedAt, capturedBytes = capture.Length,
            sourceJournal = capture.JournalPath, sourceToolVersion = history.SourceVersion,
            exporterVersion = typeof(SessionExports).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
            history.EventCount, history.InvalidLines, history.Warning, history.FirstTime, history.LastTime,
            boundary = "指定时刻的原日志前缀；P/H电平未恢复，未包含调度数据库、AGV数据或厂家验收结论。"
        }, new JsonSerializerOptions { WriteIndented = true }));
        using var index = zip.CreateEntry("manifest.json").Open();
        JsonSerializer.Serialize(index, new { algorithm = "SHA256", scope = "全部数据条目，不包含清单自身", files = manifest }, new JsonSerializerOptions { WriteIndented = true });
    }, token);

    private sealed class HashingStream(Stream target) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Bytes { get; private set; }
        public string Digest() => Convert.ToHexString(_hash.GetHashAndReset());
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { target.Write(buffer); _hash.AppendData(buffer); Bytes += buffer.Length; }
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
        public override void Flush() => target.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }
}
