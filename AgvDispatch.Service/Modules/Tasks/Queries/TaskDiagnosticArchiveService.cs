using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

public sealed class TaskDiagnosticArchiveService(ITaskCenterQueryService tasks,ITaskHandshakeStore runtime):ITaskDiagnosticArchiveService
{
    public async Task<TaskDiagnosticArchive> ExportAsync(string taskId,CancellationToken ct)
    {
        var detail=await tasks.GetDetailAsync(new TaskDetailRequest(taskId,false),ct)??throw new ArgumentException("任务不存在。");
        var first=await runtime.QueryAsync(new(taskId,PageSize:100),ct);
        if(first.TotalCount>10000)throw new InvalidOperationException("本任务超过单包10000条限制，请按事件筛选分页核查；本次没有生成不完整导出。");
        var events=new List<TaskRuntimeEvent>(first.Items);
        for(var page=2;events.Count<first.TotalCount;page++)
        {
            var batch=await runtime.QueryAsync(new(taskId,Page:page,PageSize:100,ThroughSequence:first.ThroughSequence),ct);
            if(batch.Items.Count==0)throw new InvalidOperationException("导出窗口记录发生异常，未生成成功包。");
            events.AddRange(batch.Items);
        }
        var jsonOptions=new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true};
        jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var files=new Dictionary<string,byte[]> {
            ["task-detail.json"]=JsonSerializer.SerializeToUtf8Bytes(detail,jsonOptions),
            ["runtime-events.json"]=JsonSerializer.SerializeToUtf8Bytes(events.OrderBy(e=>e.Sequence),jsonOptions)
        };
        var csv=new StringBuilder("Sequence,TaskId,StepId,Kind,Phase,StationId,Signal,PreviousValue,Value,Quality,ObservedAt,RecordedAt,Reason,Evidence\r\n");
        foreach(var e in events.OrderBy(e=>e.Sequence))
            csv.AppendLine(string.Join(",",new[]{e.Sequence.ToString(),e.TaskId,e.StepId,e.Kind,e.Phase,e.StationId,e.Signal,e.PreviousValue?.ToString()??"",e.Value?.ToString()??"",e.Quality,e.ObservedAt?.ToString("O")??"",e.RecordedAt.ToString("O"),e.Reason,e.Evidence}.Select(Cell)));
        files["runtime-events.csv"]=new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        files["README.txt"]=Encoding.UTF8.GetBytes("联调诊断包：包含导出时任务观察、握手检查点、资源诊断及固定序号窗口内的新运行事件。\n旧执行时间线只包含最近100项，其总数保留在task-detail.json；本包不宣称包含旧原始审计全文。\n信号记录是首次观察与变化采样，不是PLC扫描级录像。任务详情和事件窗口不是跨设备原子快照。\n软件规划契约不证明现场PLC程序已经匹配；手工确认和写回不等于机械完成。\n");
        var manifest=new {Schema="agv-task-diagnostics",Version=1,TaskId=taskId,CapturedAt=AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(),
            SoftwareVersion=typeof(TaskDiagnosticArchiveService).Assembly.GetName().Version?.ToString(),first.ThroughSequence,EventCount=events.Count,
            LegacyTimelineReturned=detail.ExecutionTimeline.Count,LegacyTimelineTotal=detail.ExecutionTimelineTotal,
            Files=files.Select(f=>new{Name=f.Key,Bytes=f.Value.Length,Sha256=Convert.ToHexString(SHA256.HashData(f.Value))}).ToArray()};
        files["manifest.json"]=JsonSerializer.SerializeToUtf8Bytes(manifest,jsonOptions);
        if(files.Sum(f=>f.Value.Length)>8*1024*1024)throw new InvalidOperationException("诊断包原始数据超过8MiB，未生成不完整导出。");
        using var output=new MemoryStream();
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
            foreach(var file in files){ct.ThrowIfCancellationRequested();await using var entry=zip.CreateEntry(file.Key,CompressionLevel.Fastest).Open();await entry.WriteAsync(file.Value,ct);}
        var bytes=output.ToArray();
        if(bytes.Length>6*1024*1024)throw new InvalidOperationException("诊断包超过传输预算，未生成不完整导出。");
        return new("agv-diagnostics-"+Guid.NewGuid().ToString("N")+".zip",Convert.ToHexString(SHA256.HashData(bytes)),Convert.ToBase64String(bytes),events.Count,first.ThroughSequence);
    }
    private static string Cell(string value)
    {
        if(value.TrimStart().StartsWith('=')||value.TrimStart().StartsWith('+')||value.TrimStart().StartsWith('-')||value.TrimStart().StartsWith('@'))value="'"+value;
        return "\""+value.Replace("\"","\"\"")+"\"";
    }
}
