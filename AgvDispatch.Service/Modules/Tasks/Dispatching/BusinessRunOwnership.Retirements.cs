using System.Text.Json;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

public sealed partial class BusinessRunOwnership
{
    public sealed record Retirement(string RequestId, string RunId, string VehicleId,
        string MapId, string MapVersionId, string ParameterRevision, string Operator, DateTimeOffset RetiredAt);
    private readonly object retirementLock = new();
    private Retirement[] retired = [];
    private string? retirementPath;

    public void LoadRetirements(string path)
    {
        lock (retirementLock)
        {
            retirementPath = path;
            retired = File.Exists(path) ? JsonSerializer.Deserialize<Retirement[]>(File.ReadAllText(path))
                ?? throw new InvalidDataException("原申请注销记录损坏，禁止启动。") : [];
            if (retired.Any(r => !Guid.TryParseExact(r.RequestId, "N", out _) || string.IsNullOrWhiteSpace(r.RunId)) ||
                retired.Select(r => r.RequestId).Distinct(StringComparer.Ordinal).Count() != retired.Length)
                throw new InvalidDataException("原申请注销记录无效，禁止启动。");
        }
    }
    public bool IsRetired(string requestId)
    { lock (retirementLock) return retired.Any(r => r.RequestId == requestId); }

    public void Retire(BusinessRunRecord run)
    {
        lock (retirementLock)
        {
            if (retirementPath is null || run.PendingRequestId is null || !Guid.TryParseExact(run.PendingRequestId, "N", out _))
                throw new InvalidOperationException("缺少持久化原申请身份，不能注销。");
            if (IsRetired(run.PendingRequestId)) return;
            var next = retired.Append(new Retirement(run.PendingRequestId, run.RunId, run.Plan.VehicleId,
                run.MapId, run.MapVersionId, run.ParameterRevision, run.Operator, DateTimeOffset.UtcNow)).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(retirementPath))!);
            var tmp = retirementPath + ".tmp";
            using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, next); file.Flush(true); }
            File.Move(tmp, retirementPath, true);
            retired = next;
        }
    }
}
