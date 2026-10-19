using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;

// Interactive UI verification against owned, isolated processes. No device endpoints.
var candidate = Path.GetFullPath(args.Single());
if (!File.Exists(Path.Combine(candidate, "release-manifest.json"))) throw new ArgumentException("Complete candidate required");
var root = Path.Combine(Path.GetTempPath(), "agv-web-layout-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web); options.Converters.Add(new JsonStringEnumConverter());
var definition = new MapDefinition("界面验收合成运行地图", "synthetic only", "TestCartesian", "m",
    [new("取货点", 0, 0, MapNodeType.Station, true, [], "LM1"), new("等待点", 8, 0, MapNodeType.WaitingPoint, true, [], "LM2"), new("倒料点", 8, 6, MapNodeType.Station, true, [], "LM3")],
    [new("取货至等待", "取货点", "等待点", 8, 0.5, true, true, []), new("等待至倒料", "等待点", "倒料点", 6, 0.5, true, true, [])], []);
var path = Path.Combine(root, "private.db");
using (var db = DatabaseFactory.Create(new() { DatabasePath = path }))
{
    await new DatabaseInitializer(db, new TrafficResourceCatalog([])).InitializeAsync(default);
    foreach (var (id, active) in new[] { ("UI-ACTIVE", true), ("UI-DRAFT", false) })
    {
        var version = id + "-V1";
        db.Insertable(new MapProjectEntity { MapId = id, Name = active ? definition.Name : "待发布合成地图", CoordinateSystem = "TestCartesian", Unit = "m", CoordinateScale = 1, IsActive = active, ActiveVersionId = active ? version : null, PublishedVersionId = active ? version : null, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommand();
        if (active) db.Insertable(new MapVersionEntity { MapId = id, VersionId = version, VersionNumber = 1, Status = (int)MapVersionStatus.Published, DefinitionJson = JsonSerializer.Serialize(definition, options), ValidationJson = JsonSerializer.Serialize(new MapValidationService().Validate(definition), options), CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now, CreatedBy = "isolated-layout" }).ExecuteCommand();
        db.Insertable(new MapVersionEntity { MapId = id, VersionId = id + "-DRAFT", VersionNumber = active ? 2 : 1, Status = (int)MapVersionStatus.Draft, EditToken = Guid.NewGuid().ToString("N"), DefinitionJson = JsonSerializer.Serialize(definition, options), ValidationJson = JsonSerializer.Serialize(new MapValidationService().Validate(definition), options), CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now, CreatedBy = "isolated-layout" }).ExecuteCommand();
    }
}
var deploy = Path.Combine(root, "service"); Directory.CreateDirectory(deploy);
foreach (var file in Directory.EnumerateFiles(Path.Combine(candidate, "Service"), "*", SearchOption.AllDirectories))
{
    var relative = Path.GetRelativePath(Path.Combine(candidate, "Service"), file);
    if (relative.Split(Path.DirectorySeparatorChar).Any(x => x is "config" or "data" or "logs") || Path.GetFileName(file).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)) continue;
    var destination = Path.Combine(deploy, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
}
Directory.CreateDirectory(Path.Combine(deploy, "config"));
File.WriteAllText(Path.Combine(deploy, "config/plc-signals.csv"), "address,variableName,chineseName,stationId,deviceType,instanceIndex,dataType,direction,access,addressStatus,enabled,setConditionZh,resetConditionZh,sourceNote\r\n");
var pipe = "AgvWebLayout-" + Guid.NewGuid().ToString("N");
File.WriteAllText(Path.Combine(deploy, "appsettings.json"), JsonSerializer.Serialize(new { Database = new { Path = path }, Security = new { PipeName = pipe }, Plc = new { Enabled = false, AllowWrites = false, SignalCsvPath = "config/plc-signals.csv" }, Agv = new { Enabled = false, AllowCommands = false, Robots = Array.Empty<object>() }, Map = new { Enabled = false }, TaskCreation = new { Enabled = false }, Dispatching = new { Enabled = false }, AgvCommandOutbox = new { Enabled = false }, DispatchTaskSteps = new { Enabled = false }, ExecutionIdentityAcquisition = new { Enabled = false } }, options));
using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
var processes = new List<Process>();
void Start(string executable, string workingDirectory, params string[] arguments)
{
    var info = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    var process = Process.Start(info)!; processes.Add(process);
    process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (process) File.AppendAllText(Path.Combine(root, process.Id + ".log"), e.Data + Environment.NewLine); };
    process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (process) File.AppendAllText(Path.Combine(root, process.Id + ".log"), e.Data + Environment.NewLine); };
    process.BeginOutputReadLine(); process.BeginErrorReadLine();
}
try
{
    Start(Path.Combine(deploy, "AgvDispatch.Service.exe"), deploy);
    Start(Path.Combine(candidate, "Web/host/AgvDispatch.WebHost.exe"), root, "--Web:Port=" + port, "--Web:PipeName=" + pipe, "--Web:StaticPath=" + Path.Combine(candidate, "Web/wwwroot"), "--Web:ProjectStorePath=" + Path.Combine(root, "projects"));
    using var http = new HttpClient();
    var ready = false;
    for (var i = 0; i < 150; i++)
    {
        if (processes.Any(x => x.HasExited)) throw new Exception("Private process exited; inspect " + root);
        try { using var json = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/api/snapshot")); ready = json.RootElement.GetProperty("connected").GetBoolean(); } catch (HttpRequestException) { }
        if (ready) break; await Task.Delay(200);
    }
    if (!ready) throw new Exception("Private backend not ready");
    var metadata = new { root, url = $"http://127.0.0.1:{port}/", database = path, pipe };
    File.WriteAllText(Path.Combine(root, "ready.json"), JsonSerializer.Serialize(metadata, options));
    Console.WriteLine(JsonSerializer.Serialize(metadata, options));
    var deadline = DateTime.UtcNow.AddMinutes(20);
    while (!File.Exists(Path.Combine(root, "stop")) && DateTime.UtcNow < deadline) await Task.Delay(200);
}
finally
{
    foreach (var process in Enumerable.Reverse(processes)) { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } process.Dispose(); }
}
