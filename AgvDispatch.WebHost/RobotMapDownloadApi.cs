using System.Text.RegularExpressions;
using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.WebHost;
public static class RobotMapDownloadApi
{
    public static void MapRobotMapDownloadApi(this WebApplication app)
    {
        var directory=Path.Combine(app.Environment.ContentRootPath,"data","robot-map-downloads");
        app.MapPost("/api/robots/map/download",async(RobotMapDownloadQuery query,DispatchPipe pipe,VendorMapSourceArchive archive,CancellationToken ct)=>
        {
            var download=await pipe.ReadAsync<RobotMapDownload>("GetCurrentRobotMapDownload",query,ct)
                ?? throw new InvalidDataException("车端未返回地图。");
            if(!Regex.IsMatch(download.ContentSha256,"^[a-f0-9]{64}$"))throw new InvalidDataException("下载校验值无效。");
            Directory.CreateDirectory(directory);
            var path=Path.Combine(directory,download.ContentSha256+".smap");
            await File.WriteAllTextAsync(path,download.JsonContent,new System.Text.UTF8Encoding(false),ct);
            var archived = await archive.SaveAsync(await File.ReadAllBytesAsync(path, ct), ct);
            if (archived != download.ContentSha256) throw new InvalidDataException("机器人地图下载与留存摘要不一致。");
            var preview=await pipe.ReadAsync<MapImportPreviewResult>(TrafficControlOperations.PreviewMapImport,
                new PreviewMapImportRequest(path,MapImportFormat.RobokitSmap),ct);
            return Results.Ok(new{download=download with{JsonContent=""},preview=preview is null?null:preview with
                {Definition=preview.Definition is null?null:preview.Definition with{Source=""}}});
        });
        app.MapGet("/api/robots/map/files/{sha}",(string sha)=>
        {
            if(!Regex.IsMatch(sha,"^[a-f0-9]{64}$"))return Results.BadRequest();
            var path=Path.Combine(directory,sha+".smap");
            return File.Exists(path)?Results.File(path,"application/octet-stream","robot-map-"+sha[..12]+".smap"):Results.NotFound();
        });
    }
}
