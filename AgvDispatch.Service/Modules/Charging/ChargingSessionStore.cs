using System.Text.Json;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

public sealed class ChargingSessionStore(string path) : IChargingRecoveryEvidence
{
    public Task<ChargingSession?> ReadRecoverySessionAsync(string sessionId,CancellationToken ct)
    { ct.ThrowIfCancellationRequested();return Task.FromResult(Load().SingleOrDefault(s=>s.SessionId==sessionId)); }
    public ChargingSession[] Load()
    {
        if (!File.Exists(path)) return [];
        // Readers keep a complete old snapshot while the writer atomically swaps
        // the pathname; they must not prevent File.Move on Windows.
        using var input = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var rows = JsonSerializer.Deserialize<ChargingSession[]>(input)
            ?? throw new InvalidDataException("充电记录损坏，禁止自动派车。");
        if (rows.Any(s => !s.SessionId.StartsWith("CHARGE-", StringComparison.Ordinal) || !Guid.TryParseExact(s.SessionId[7..], "N", out _) ||
            string.IsNullOrWhiteSpace(s.VehicleId) || !Enum.IsDefined(s.Phase) ||
            s.Phase is not (ChargingPhase.Completed or ChargingPhase.Queued or ChargingPhase.WaitingForBusiness) && (s.Site is null || s.Route is null) ||
            s.Route is { } r && (r.ToCharger.Length < 2 || r.ExitPath.Length < 2 || r.ResourceIds.Length == 0 || string.IsNullOrWhiteSpace(r.MapId) || string.IsNullOrWhiteSpace(r.MapVersionId))))
            throw new InvalidDataException("充电会话身份、地图或保留路径不完整，禁止自动派车。");
        if (rows.Select(s=>s.SessionId).Distinct(StringComparer.Ordinal).Count()!=rows.Length ||
            rows.Where(s=>s.Phase!=ChargingPhase.Completed).GroupBy(s=>s.VehicleId,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1) ||
            rows.Where(s=>s.HasReservation).GroupBy(s=>s.Site!.ResourceId,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))
            throw new InvalidDataException("充电记录含重复车辆或资源归属，禁止自动派车。");
        return rows;
    }

    public void Save(ChargingSession[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, rows); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
}
