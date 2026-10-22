using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcHandshakeLab;

internal static class SessionEvidenceTests
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        var directory = Path.Combine(Path.GetTempPath(), "plc-evidence-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = NetworkLabConfig.CreateDefault(); config.Port = NetworkTests.FreePort();
            var journal = Path.Combine(directory, "session.jsonl");
            File.WriteAllText(Path.ChangeExtension(journal, ".config.json"), JsonSerializer.Serialize(config));
            var lab = new NetworkLab(config, journal);
            try
            {
                lab.Note("工具版本", "TEST-ORIGINAL-1.2.0");
                lab.Note("CSV转义", "带\"引号\",换行\n下一行");
                lab.SelectStage("CNC-001", 0);
                lab.RecordStageTest("CNC-001", "失败", "CAR-1", "TASK-1", "首次失败,带\"引号\"", false);
                lab.RecordStageTest("CNC-001", "跳过", "CAR-1", "TASK-1", "保留复测", false);
                lab.BeginTestRound("CNC-001");
                lab.SelectStage("CNC-001", 4);
                lab.RecordStageTest("CNC-001", "失败", "CAR-2", "TASK-2", "第二轮", false);
                for (var i = 0; i < 1200; i++) lab.Note("压力记录", "EVENT-" + i);
                using var captured = lab.CaptureSession();
                var snapshotLength = captured.Length;
                lab.Note("边界后", "NOT-IN-SNAPSHOT");

                // Deliberately blocked output: live S7 and P writes must finish
                // while export is held, rather than merely timing a fast file.
                await using (var server = new S7LabServer(lab))
                {
                    server.Start();
                    using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", server.Port);
                    using var blocked = new BlockingWriter();
                    var exporting = Task.Run(() => SessionExports.WriteCsv(captured, blocked));
                    try
                    {
                        Check(blocked.Started.Wait(TimeSpan.FromSeconds(5)), "慢速导出已开始");
                        var reply = await NetworkTests.Exchange(client.GetStream(), NetworkTests.Variable(4, 1, 0, 1, false)).WaitAsync(TimeSpan.FromSeconds(3));
                        Check(reply.Length > 0, "导出被阻塞时真实S7读仍完成");
                        await Task.Run(() => lab.ManualSet("CNC-001", 0, true)).WaitAsync(TimeSpan.FromSeconds(3));
                        Check(lab.Snapshot("CNC-001").Single(p => p.Mapping.Signal == 0).Value, "导出不占通信锁且P仍可操作");
                    }
                    finally { blocked.Release.Set(); await exporting.WaitAsync(TimeSpan.FromSeconds(10)); }
                }
                Check(captured.Length == snapshotLength, "日志继续追加时导出边界不变");
                var csv = Path.Combine(directory, "events.csv"); SessionExports.Csv(captured, csv);
                var csvText = File.ReadAllText(csv);
                Check(csvText.Contains("EVENT-0") && csvText.Contains("EVENT-1199") && !csvText.Contains("NOT-IN-SNAPSHOT"), "全量导出超过500条且不混入边界后的事件");
                Check(csvText.Contains("带\"\"引号\"\""), "流式CSV保留转义");
                var history = SessionHistory.Read(captured);
                Check(history.RecentEvents.Count == 500 && history.EventCount > 1200 && history.Results.Count == 3, "历史只保留500条预览，阶段记录不截断");
                Check(history.SourceVersion == "TEST-ORIGINAL-1.2.0" && history.Groups.Contains(new("CNC-001", 2)), "历史保留原工具版本与两轮");
                using var report = new StringWriter(); history.WriteReport(report);
                Check(report.ToString().Contains("未测") && report.ToString().Contains("首次失败") && report.ToString().Contains("保留复测"), "阶段报告保留未测、失败和复测历史");

                var zipPath = Path.Combine(directory, "evidence.zip"); SessionExports.Bundle(captured, zipPath);
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    Check(zip.GetEntry("session.jsonl")!.Length == snapshotLength, "证据包原始日志等于固定前缀");
                    Check(new[] { "session.config.json", "plc-signals.csv", "session.csv", "stages.md", "summary.json", "manifest.json" }.All(n => zip.GetEntry(n) is not null), "证据包包含配置、点表、日志、报告、版本与清单");
                    using var manifestStream = zip.GetEntry("manifest.json")!.Open();
                    using var manifest = JsonDocument.Parse(manifestStream);
                    foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
                    {
                        var entry = zip.GetEntry(file.GetProperty("path").GetString()!)!;
                        using var contents = entry.Open();
                        Check(entry.Length == file.GetProperty("bytes").GetInt64() && Convert.ToHexString(SHA256.HashData(contents)) == file.GetProperty("sha256").GetString(), "ZIP每个数据条目SHA256正确");
                    }
                    using var summaryStream = zip.GetEntry("summary.json")!.Open();
                    using var summary = JsonDocument.Parse(summaryStream);
                    Check(summary.RootElement.GetProperty("sourceToolVersion").GetString() == "TEST-ORIGINAL-1.2.0", "新工具导出不冒充原会话版本");
                }
                File.WriteAllText(csv, "KEEP-EXISTING");
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                try { SessionExports.Csv(captured, csv, cancel.Token); throw new Exception("应取消"); } catch (OperationCanceledException) { }
                Check(File.ReadAllText(csv) == "KEEP-EXISTING" && !Directory.GetFiles(directory, "*.tmp").Any(), "取消不替换已有文件、不残留临时文件");
                try
                {
                    SessionExports.Atomic(captured, csv, stream => { stream.WriteByte(1); throw new IOException("模拟磁盘失败"); }, default);
                    throw new Exception("应导出失败");
                }
                catch (IOException) { }
                Check(File.ReadAllText(csv) == "KEEP-EXISTING" && !Directory.GetFiles(directory, "*.tmp").Any() && lab.Fault is null, "导出写盘失败保留目标及S7会话");
                try { SessionExports.Csv(captured, journal); throw new Exception("应拒绝源覆盖"); } catch (IOException) { }
                Check(lab.ExportCsv().Contains("NOT-IN-SNAPSHOT"), "不能覆盖源日志");
                // A snapshot remains owned and exportable when the live lab closes.
                lab.Dispose(); SessionExports.Report(captured, Path.Combine(directory, "closed.md"));
                Check(File.Exists(Path.Combine(directory, "closed.md")), "现场会话停止后已捕获快照仍可导出");
            }
            finally { lab.Dispose(); }
            var before = SHA256.HashData(File.ReadAllBytes(journal));
            using (var historical = SessionCapture.OpenHistory(journal))
            {
                var read = SessionHistory.Read(historical);
                Check(read.Results.Count == 3 && read.Config is not null && read.SourceVersion.Contains("ORIGINAL"), "重开历史重建结果及原点表");
                SessionExports.Bundle(historical, Path.Combine(directory, "history.zip"));
            }
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(journal))), "回看与导出不会改原日志");
            File.AppendAllText(journal, "{truncated");
            File.Delete(Path.ChangeExtension(journal, ".config.json"));
            using (var broken = SessionCapture.OpenHistory(journal))
            {
                var read = SessionHistory.Read(broken);
                Check(read.Results.Count == 3 && read.InvalidLines == 1 && read.Config is null && read.Warning.Contains("不完整"), "尾部损坏及缺配置明确提示，不丢原有效结果");
                var brokenZip = Path.Combine(directory, "broken.zip"); SessionExports.Bundle(broken, brokenZip);
                using var zip = ZipFile.OpenRead(brokenZip);
                Check(zip.GetEntry("plc-signals.csv") is null && zip.GetEntry("session.jsonl")!.Length == new FileInfo(journal).Length, "缺配置不伪造点表、损坏原文仍留存");
            }
            Console.WriteLine($"PASS: {checks} session evidence checks. Streaming export/S7 concurrency, frozen prefix, history, ZIP hashes, cancellation and damaged tails.");
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("plc-evidence-test-"))
                throw new IOException("Refused unsafe test cleanup.");
            Directory.Delete(resolved, true);
        }
    }
    private sealed class BlockingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public override void WriteLine(string? value) { Started.Set(); if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(); }
        protected override void Dispose(bool disposing) { Started.Dispose(); Release.Dispose(); base.Dispose(disposing); }
    }
}
