using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using AgvDispatch.Domain.Tasks;
using System.Text.Json.Serialization;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TaskHistoryExportService(DatabaseOptions options) : ITaskHistoryExportService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All), Converters = { new JsonStringEnumConverter() } };
    private async Task<TaskHistoryExportResult> GenerateAsync(TrafficUserContext user, TaskHistoryExportRequest request, ExportJob job, CancellationToken ct)
    {
        if (!user.Has(TrafficPermission.View)) throw new UnauthorizedAccessException("需要查看任务权限。");
        var filter = request.Filter with { Page = 1, PageSize = 100 };
        TaskCenterReadRepository.ValidateHistoryQuery(filter);
        var root = CacheRoot;
        ValidateCacheRoot();
        var exportId = job.Id;
        var staging = Path.Combine(root, exportId + ".staging");
        var archivePath = Path.Combine(root, "任务处置历史-" + exportId + ".zip");
        var temporary = archivePath + ".tmp";
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(options.DatabasePath)) throw new InvalidOperationException("任务数据库不存在。");
        // A separate connection avoids attaching a long read transaction to live dispatch writers.
        using var snapshot = DatabaseFactory.Create(options);
        await snapshot.Ado.BeginTranAsync();
        var snapshotFinished = false;
        try
        {
            var reader = new TaskCenterReadRepository(snapshot);
            var detail = await reader.GetDetailAsync(new TaskDetailRequest(filter.TaskId, false), ct);
            if (detail == null) return new(false, "任务不存在，无法导出处置历史。");
            job.Update("Preparing", "正在准备一致快照", 0, 0, 2);
            var capturedAt = DateTimeOffset.Now;
            Directory.CreateDirectory(staging);
            var count = 0;
            using (var csv = new StreamWriter(Path.Combine(staging, "history.csv"), false, new UTF8Encoding(true)))
            using (var evidence = new StreamWriter(Path.Combine(staging, "evidence.jsonl"), false, new UTF8Encoding(false)))
            using (var report = new StreamWriter(Path.Combine(staging, "report.html"), false, new UTF8Encoding(false)))
            {
                await csv.WriteLineAsync("记录编号,时间（含时区）,类别,操作,操作人,步骤编号,恢复批次,路权资源,范围,原因,证据字符数,证据SHA256,证据文件".AsMemory(), ct);
                await report.WriteLineAsync(("<!doctype html><html lang=zh-CN><meta charset=utf-8><title>任务处置交接报告</title>" +
                    "<style>body{font:14px sans-serif;margin:28px;color:#172033}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccd3dc;padding:8px;text-align:left;vertical-align:top;overflow-wrap:anywhere}th{background:#eef2f6}code{overflow-wrap:anywhere}</style>" +
                    "<h1>任务处置交接报告</h1><p>任务：" + Html(detail.Task.TaskId) + "；工位：" + Html(detail.Task.StationId) +
                    "；车辆：" + Html(detail.Task.VehicleId ?? detail.Task.RequestedVehicleId ?? "未分配") + "；阶段：" + Html(StageLabel(detail.Task.Stage)) + "</p>" +
                    (detail.Cancellation == null ? "" : "<p>取消依据：" + Html(detail.Cancellation.Reason) + "；申请人：" + Html(detail.Cancellation.Operator) +
                        "；原车辆任务结束确认：" + Html(detail.Cancellation.ConfirmedAt?.ToString("O") ?? "尚无确认记录") + "</p>") +
                    "<p>快照时间：" + capturedAt.ToString("O") + "；导出人：" + Html(user.WindowsIdentity) + "</p><p>" + Html(FilterDescription(filter)) + "</p>" +
                    "<p>全部匹配记录，按时间倒序排列。全场记录仅关联本任务参与的恢复批次。具体批次筛选不包含未记录批次编号的取消与路权操作。</p>" +
                    "<p>报告展示保存的处置依据，不代表导出时的现场状态。<a href=history.csv>历史CSV</a>；完整证据见 <a href=evidence.jsonl>evidence.jsonl</a>，按记录编号定位。请先解压整个导出包。</p>" +
                    "<table><thead><tr><th>记录 / 时间</th><th>操作 / 操作人</th><th>批次 / 步骤 / 路权</th><th>范围 / 原因</th></tr></thead><tbody>").AsMemory(), ct);
                var total = await reader.WriteHistoryAsync(filter, async entry =>
                {
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Evidence))).ToLowerInvariant();
                    var fields = new[] { entry.Id, entry.OccurredAt.ToString("O"), entry.Category, entry.Operation, entry.Operator,
                        entry.StepId, entry.SessionId, entry.ResourceId, entry.Scope, entry.Reason, entry.EvidenceCharacters.ToString(CultureInfo.InvariantCulture), hash, "evidence.jsonl（按记录编号定位）" };
                    await csv.WriteLineAsync(string.Join(",", fields.Select(Csv)).AsMemory(), ct);
                    await evidence.WriteLineAsync(JsonSerializer.Serialize(new { entry.Id, entry.OccurredAt, entry.Category, entry.Operation,
                        entry.Operator, entry.StepId, entry.SessionId, entry.ResourceId, entry.Scope, entry.Reason, entry.Evidence,
                        entry.EvidenceCharacters, EvidenceTruncated = false, EvidenceSha256 = hash }, Json).AsMemory(), ct);
                    await report.WriteLineAsync(("<tr><td><code>" + Html(entry.Id) + "</code><br>" + entry.OccurredAt.ToString("O") +
                        "</td><td>" + Html(entry.Operation) + "<br>" + Html(entry.Operator) + "</td><td>" + Html(entry.SessionId) + "<br>" + Html(entry.StepId) + "<br>" + Html(entry.ResourceId) +
                        "</td><td>" + Html(entry.Scope) + "<br>" + Html(entry.Reason) + "</td></tr>").AsMemory(), ct);
                    count++;
                    job.Update("Writing", $"正在写入处置记录 {count} / {job.Total}", count, job.Total, job.Total == 0 ? 65 : 5 + (int)(60L * count / job.Total));
                }, ct, total => job.Update("Writing", $"正在写入处置记录 0 / {total}", 0, total, 5));
                if (count != total) throw new InvalidOperationException("导出记录数量与数据库快照不一致。");
                await report.WriteLineAsync(("</tbody></table><p>共 " + count + " 条记录。" + (count == 0 ? "当前筛选没有匹配记录。" : "") + "</p></html>").AsMemory(), ct);
            }
            await snapshot.Ado.CommitTranAsync(); snapshotFinished = true;
            var note = $"AGV任务处置历史导出包 v1\n任务：{filter.TaskId}\n快照时间：{capturedAt:O}\n导出人：{user.WindowsIdentity}\n{FilterDescription(filter)}\n记录数：{count}\n\n先解压，再打开 report.html 交接报告或 history.csv（UTF-8 BOM）。\n完整证据保存在 evidence.jsonl，每行一条，按 id 对应CSV；证据未作4096字符预览截取。\nmanifest.json 包含任务、取消请求、步骤、命令、快照时路权及每个文件的SHA256。\nCSV对可能被Excel当作公式的文本增加前导单引号，原始值以JSONL为准。\n具体批次筛选排除未记录批次编号的取消和路权记录。\n导出反映已有审计，不代表新的现场确认或放行许可。\n";
            await File.WriteAllTextAsync(Path.Combine(staging, "README.txt"), note, new UTF8Encoding(true), ct);
            job.Update("Checksums", "正在计算证据文件校验信息", count, count, 70);
            var checksums = new Dictionary<string, string>();
            foreach (var name in new[] { "history.csv", "evidence.jsonl", "report.html", "README.txt" })
            {
                await using var file = File.OpenRead(Path.Combine(staging, name));
                checksums[name] = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
            }
            var manifest = new { Schema = "agv-task-history-export/v1", ExportId = exportId, CapturedAt = capturedAt, ExportedBy = user.WindowsIdentity,
                Filter = filter, RecordCount = count, Sort = "OccurredAt DESC, source DESC, id DESC", Task = detail.Task, detail.Cancellation,
                Steps = detail.Steps.Select(s => new { s.StepId, s.Sequence, s.StepType, s.Status, s.VehicleId, s.SourceSite, s.DestinationSite, s.VendorTaskId, s.StartedAt, s.CompletedAt, s.ErrorMessage }),
                Commands = detail.Commands, Resources = detail.Resources, Files = checksums };
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(Json) { WriteIndented = true }), new UTF8Encoding(false), ct);
            ct.ThrowIfCancellationRequested();
            job.Update("Packaging", "正在生成ZIP导出包", count, count, 80);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                foreach (var name in checksums.Keys.Append("manifest.json"))
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    await using var source = File.OpenRead(Path.Combine(staging, name));
                    await using var target = entry.Open();
                    await source.CopyToAsync(target, ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            job.Update("Verifying", "正在校验导出包", count, count, 95);
            string archiveHash;
            await using (var file = File.OpenRead(temporary)) archiveHash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
            var result = new TaskHistoryExportResult(true, $"已导出全部 {count} 条匹配记录及完整证据。", archivePath, archiveHash, new FileInfo(temporary).Length, count);
            lock (job.Sync)
            {
                ct.ThrowIfCancellationRequested();
                File.Move(temporary, archivePath);
                job.Update("Ready", "后台导出包已生成，等待前端保存", count, count, 100, true);
            }
            return result;
        }
        finally
        {
            if (!snapshotFinished) await snapshot.Ado.RollbackTranAsync();
            if (File.Exists(temporary)) File.Delete(temporary);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
    private static string Html(string value) => WebUtility.HtmlEncode(value).Replace("\n", "<br>");
    private static string Csv(string value)
    {
        var trimmed = value.TrimStart();
        if ((trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) || (value.Length > 0 && "\t\r\n".Contains(value[0]))) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    private static string CategoryLabel(TaskHistoryCategory category) => category switch
    {
        TaskHistoryCategory.All => "全部", TaskHistoryCategory.Cancellation => "取消", TaskHistoryCategory.Recovery => "恢复核对",
        TaskHistoryCategory.TrafficRelease => "路权解锁", _ => category.ToString()
    };
    private static string StageLabel(DispatchTaskStage stage) => stage switch
    {
        DispatchTaskStage.Created => "等待PLC应答", DispatchTaskStage.WaitingForVehicle => "待派车", DispatchTaskStage.SubmittingToAgv => "下发中",
        DispatchTaskStage.GoingToPickup => "前往取箱", DispatchTaskStage.PickingUp => "取满箱", DispatchTaskStage.GoingToDumper => "前往倒料机",
        DispatchTaskStage.WaitingForDumper => "等待倒料机", DispatchTaskStage.Dumping => "倒料中", DispatchTaskStage.ReturningEmptyBox => "空箱返回",
        DispatchTaskStage.WaitingForPlcConfirmation => "等待PLC收尾", DispatchTaskStage.Completed => "已完成", DispatchTaskStage.Faulted => "失败",
        DispatchTaskStage.Cancelled => "已取消", DispatchTaskStage.CancellationPending => "取消待确认", DispatchTaskStage.CancellationReview => "取消待核对", _ => stage.ToString()
    };
    private static string FilterDescription(TaskHistoryQuery filter) => $"类别：{CategoryLabel(filter.Category)}；起始：{filter.From?.ToString("O") ?? "不限"}；结束：{filter.To?.ToString("O") ?? "不限"}；恢复批次：{(filter.SessionId == "" ? "全部" : filter.SessionId)}。页码不限制导出范围。";
}
