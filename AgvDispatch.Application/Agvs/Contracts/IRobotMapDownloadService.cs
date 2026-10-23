namespace AgvDispatch.Application.Abstractions;
public sealed record RobotMapDownloadQuery(string VehicleId);
public sealed record RobotMapDownload(string VehicleId,string MapName,string RobotMapMd5,string ContentSha256,
    string JsonContent,DateTimeOffset DownloadedAt);
public interface IRobotMapDownloadService
{
    Task<RobotMapDownload> ReadCurrentAsync(RobotMapDownloadQuery query,CancellationToken ct);
}
