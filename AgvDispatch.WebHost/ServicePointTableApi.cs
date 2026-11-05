using System.Security.Cryptography;
using System.Text;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.WebHost;

public static class ServicePointTableApi
{
    private const long MaximumBytes = 8 * 1024 * 1024;

    public sealed record Snapshot(string DeviceId, string DeviceName, bool Enabled, string RelativePath,
        string FileName, string Sha256, long Bytes, string CsvText, DateTimeOffset ReadAt,
        IReadOnlyList<AvailableTable> AvailableTables);

    public sealed record AvailableTable(string DeviceId, string DeviceName, bool Enabled, string RelativePath);

    public static void MapServicePointTableApi(this WebApplication app)
    {
        app.MapGet("/api/communication/point-table", async (string? deviceId, DispatchPipe pipe,
            IConfiguration configuration, IWebHostEnvironment environment, CancellationToken ct) =>
        {
            var settings = await pipe.ReadAsync<CommunicationSettingsSnapshot>(
                TrafficControlOperations.GetCommunicationSettings, new { }, ct)
                ?? throw new InvalidDataException("后台没有返回通信配置。");
            var serviceDirectory = ResolveServiceDirectory(configuration, environment);
            var configDirectory = Path.Combine(serviceDirectory.FullName, "config");
            string RelativePath(string value)
            {
                var full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(serviceDirectory.FullName, value));
                var relative = Path.GetRelativePath(configDirectory, full);
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ||
                    !string.Equals(Path.GetExtension(full), ".csv", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("当前PLC点表不在Service/config目录内。");
                return "config/" + relative.Replace('\\', '/');
            }
            var tables = settings.Saved.Plcs
                .Where(plc => !string.IsNullOrWhiteSpace(plc.SignalCsvPath))
                .Select(plc => new AvailableTable(plc.DeviceId, plc.DisplayName, plc.Enabled, RelativePath(plc.SignalCsvPath)))
                .ToArray();
            var selected = string.IsNullOrWhiteSpace(deviceId)
                ? settings.Saved.Plcs.FirstOrDefault(plc => plc.Enabled && !string.IsNullOrWhiteSpace(plc.SignalCsvPath))
                  ?? settings.Saved.Plcs.FirstOrDefault(plc => !string.IsNullOrWhiteSpace(plc.SignalCsvPath))
                : settings.Saved.Plcs.SingleOrDefault(plc => plc.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            if (selected is null || string.IsNullOrWhiteSpace(selected.SignalCsvPath))
                return Results.NotFound(new { error = "当前工程没有已绑定的PLC点表。" });

            var path = Path.GetFullPath(Path.IsPathRooted(selected.SignalCsvPath)
                ? selected.SignalCsvPath
                : Path.Combine(serviceDirectory.FullName, selected.SignalCsvPath));
            var relativePath = RelativePath(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumBytes)
                throw new InvalidDataException("当前PLC点表不存在、为空或超过8 MiB。");
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes).TrimStart('\uFEFF');
            return Results.Ok(new Snapshot(selected.DeviceId, selected.DisplayName, selected.Enabled,
                relativePath, Path.GetFileName(path),
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength, text,
                DateTimeOffset.Now, tables));
        });
    }

    private static DirectoryInfo ResolveServiceDirectory(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configured = configuration["Web:ServiceDirectory"];
        var staticPath = configuration["Web:StaticPath"];
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        if (!string.IsNullOrWhiteSpace(staticPath))
            candidates.Add(Path.Combine(Directory.GetParent(Path.GetFullPath(staticPath))?.Parent?.FullName
                ?? throw new InvalidOperationException("无法从Web静态目录定位发布根目录。"), "Service"));
        candidates.Add(Path.Combine(environment.ContentRootPath, "..", "..", "Service"));
        foreach (var candidate in candidates)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(candidate));
            if (directory.Exists) return directory;
        }
        throw new DirectoryNotFoundException("无法定位当前正式Service目录。");
    }
}
