using System.Text.Json;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

public sealed partial class FleetTestService
{
    private sealed record StartRetirement(string RequestId, string Operator, string Reason, DateTimeOffset RetiredAt);
    private StartRetirement[] startRetirements=[];

    private void LoadStartRetirements()
    {
        if(!File.Exists(path+".retired"))return;
        startRetirements=JsonSerializer.Deserialize<StartRetirement[]>(File.ReadAllText(path+".retired"))
            ??throw new InvalidDataException("多车测试原申请注销记录损坏。");
        if(startRetirements.Any(r=>r==null||!Guid.TryParseExact(r.RequestId,"N",out _)||string.IsNullOrWhiteSpace(r.Reason))||
            startRetirements.Select(r=>r.RequestId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=startRetirements.Length)
            throw new InvalidDataException("多车测试原申请注销记录损坏，禁止继续接受申请。");
    }

    // This shares the StartAsync mutex. An in-flight start either commits first and cannot
    // be retired, or observes the durable retirement before it can create a run/send a call.
    public async Task<FleetTestStartRetirementReceipt> RetireStartAsync(TrafficUserContext user,FleetTestStartRetirement request,CancellationToken ct)
    {
        Authorize(user);
        if(!Guid.TryParseExact(request.RequestId,"N",out _)||!request.SiteConfirmed||
            string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Trim().Length<10||request.Reason.Length>1000)
            throw new ArgumentException("须核对原申请、确认现场，并填写10至1000字注销依据。");
        await mutex.WaitAsync(ct);
        try
        {
            var accepted=string.Equals(run?.Plan.RequestId,request.RequestId,StringComparison.OrdinalIgnoreCase)?run:null;
            var directory=Path.GetDirectoryName(Path.GetFullPath(path))!;
            if(accepted==null&&Directory.Exists(directory))
                foreach(var file in Directory.EnumerateFiles(directory,Path.GetFileName(path)+".*.history"))
                {
                    var historic=JsonSerializer.Deserialize<FleetTestRun>(File.ReadAllText(file))
                        ??throw new InvalidDataException("多车测试历史记录损坏，不能证明原申请未受理。");
                    if(string.Equals(historic.Plan.RequestId,request.RequestId,StringComparison.OrdinalIgnoreCase))
                    {accepted=historic;break;}
                }
            if(accepted!=null)
                throw new InvalidOperationException($"原申请已受理，原运行编号：{accepted.RunId}。请查询并核对原运行，不能注销申请或解除占用。");
            var retired=startRetirements.SingleOrDefault(r=>string.Equals(r.RequestId,request.RequestId,StringComparison.OrdinalIgnoreCase));
            if(retired==null)
            {
                retired=new(request.RequestId,user.WindowsIdentity,request.Reason.Trim(),DateTimeOffset.UtcNow);
                var updated=startRetirements.Append(retired).ToArray();
                Directory.CreateDirectory(directory);
                using(var stream=new FileStream(path+".retired.tmp",FileMode.Create,FileAccess.Write,FileShare.None))
                {JsonSerializer.Serialize(stream,updated);stream.Flush(true);}
                File.Move(path+".retired.tmp",path+".retired",true);
                startRetirements=updated;
                logger.LogInformation("多车测试未受理申请已注销。RequestId={RequestId}, Operator={Operator}, Reason={Reason}",retired.RequestId,retired.Operator,retired.Reason);
            }
            return new(request.RequestId,true,"已核对原申请未受理并持久化注销；该编号的迟到请求将被拒绝。未发送设备动作或解除占用。",retired.RetiredAt);
        }
        finally{mutex.Release();}
    }
}
