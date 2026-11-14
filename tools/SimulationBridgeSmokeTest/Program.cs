using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.WebHost;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
var temporary = Path.Combine(Path.GetTempPath(), "simulation-bridge-" + Guid.NewGuid().ToString("N"));
var controller = Path.Combine(temporary, "tools", "SimulationLab", "controller.py");
var root = Path.Combine(temporary, "artifacts", "web-single-vehicle-test");
Directory.CreateDirectory(Path.GetDirectoryName(controller)!);
Directory.CreateDirectory(root);
await File.WriteAllTextAsync(controller, """
import argparse, json, os, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--root');p.add_argument('--port',type=int);a=p.parse_args()
token=os.environ['AGV_SIMULATION_TOKEN'];root=Path(a.root)
class Handler(BaseHTTPRequestHandler):
    def log_message(self,*args):pass
    def do_GET(self):
        if self.headers.get('X-Simulation-Token') != token:
            self.send_response(403);self.end_headers();return
        if self.path.startswith('/archive?id='):
            if self.path.startswith('/archive?id=' + 'e'*32):
                body=json.dumps(dict(error='evidence not found')).encode()
                self.send_response(404);self.send_header('Content-Type','application/json')
                self.end_headers();self.wfile.write(body);return
            body=b'PK\\x03\\x04simulation-evidence'
            self.send_response(200);self.send_header('Content-Type','application/zip')
            self.send_header('Content-Disposition','attachment; filename="evidence.zip"')
            self.end_headers();self.wfile.write(body);return
        body=json.dumps(dict(enabled=True,pid=os.getpid(),path=self.path)).encode()
        self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(body)
    def do_POST(self):
        if self.headers.get('X-Simulation-Token') != token:
            self.send_response(403);self.end_headers();return
        payload=json.loads(self.rfile.read(int(self.headers.get('Content-Length','0'))))
        with (root/'requests.jsonl').open('a',encoding='utf-8') as f:f.write(json.dumps(dict(path=self.path,payload=payload))+'\n')
        body=json.dumps(dict(enabled=True,path=self.path,payload=payload)).encode()
        self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(body)
        if self.path=='/shutdown':threading.Thread(target=self.server.shutdown).start()
s=ThreadingHTTPServer(('127.0.0.1',a.port),Handler);s.serve_forever();s.server_close()
""");
var pythonStart = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
pythonStart.ArgumentList.Add("-c"); pythonStart.ArgumentList.Add("import sys;print(sys.executable)");
using var resolver = Process.Start(pythonStart)!;
var python = (await resolver.StandardOutput.ReadToEndAsync()).Trim();
await resolver.WaitForExitAsync();
Check(resolver.ExitCode == 0 && Path.IsPathFullyQualified(python), "resolve absolute Python interpreter");
int Port() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
Dictionary<string, string?> Values(int port) => new()
{
    ["Web:Simulation:Enabled"] = "true", ["Web:Simulation:Root"] = root,
    ["Web:Simulation:ControllerPath"] = controller, ["Web:Simulation:PythonPath"] = python,
    ["Web:Simulation:Port"] = port.ToString()
};
IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
async Task<JsonElement> Execute(IResult result)
{
    using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
    var context = new DefaultHttpContext { RequestServices = services };
    await using var stream = new MemoryStream(); context.Response.Body = stream;
    await result.ExecuteAsync(context);
    stream.Position = 0;
    using var parsed = await JsonDocument.ParseAsync(stream);
    return parsed.RootElement.Clone();
}
async Task<(byte[] Body, int Status, string? ContentType, string? Disposition)> ExecuteRaw(IResult result)
{
    using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
    var context = new DefaultHttpContext { RequestServices = services };
    await using var stream = new MemoryStream(); context.Response.Body = stream;
    await result.ExecuteAsync(context);
    return (stream.ToArray(), context.Response.StatusCode, context.Response.ContentType, context.Response.Headers.ContentDisposition.ToString());
}
async Task Reject(Dictionary<string, string?> values, string message)
{
    try { await using var unused = new SimulationBridge(Config(values)); }
    catch (ArgumentException) { Check(true, message); return; }
    throw new Exception("Expected rejection: " + message);
}
await using (var disabled = new SimulationBridge(new ConfigurationBuilder().Build()))
    Check(!disabled.Enabled && (await Execute(await disabled.ForwardAsync(HttpMethod.Post, "/start", "{}", default))).GetProperty("error").GetString()!.Contains("未启用"),
        "disabled bridge cannot launch or operate devices");
var invalid = Values(Port()); invalid["Web:Simulation:Root"] = Path.GetDirectoryName(root);
await Reject(invalid, "reject artifacts parent as experiment root");
invalid = Values(Port()); invalid["Web:Simulation:Root"] = Path.Combine(root, "nested");
await Reject(invalid, "reject nested path escaping exact experiment selection");
invalid = Values(Port()); invalid["Web:Simulation:Root"] = Path.Combine(temporary, "production");
await Reject(invalid, "reject production or arbitrary external root");
invalid = Values(5199);
await Reject(invalid, "reject daily Web port as simulation controller");
invalid = Values(Port()); invalid["Web:Simulation:PythonPath"] = "python";
await Reject(invalid, "reject relative interpreter command");
invalid = Values(Port()); invalid["Web:Simulation:ControllerPath"] = Path.Combine(temporary, "arbitrary.py");
await Reject(invalid, "reject missing or arbitrary controller file");
using (var listener = new TcpListener(IPAddress.Loopback, 0))
{
    listener.Start();
    await using var occupied = new SimulationBridge(Config(Values(((IPEndPoint)listener.LocalEndpoint).Port)));
    var rejected = false;
    try { await occupied.ForwardAsync(HttpMethod.Get, "/status", null, default); }
    catch (InvalidOperationException error) when (error.Message.Contains("已占用")) { rejected = true; }
    Check(rejected && listener.Server.IsBound, "occupied port rejected without attaching to or terminating its owner");
}
var freePort = Port();
int ownedPid;
await using (var bridge = new SimulationBridge(Config(Values(freePort))))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var status = await Execute(await bridge.ForwardAsync(HttpMethod.Get, "/status", null, timeout.Token));
    ownedPid = status.GetProperty("pid").GetInt32();
    Check(status.GetProperty("enabled").GetBoolean() && ownedPid != Environment.ProcessId, "launch owned hidden controller and authenticate status");
    using var raw = new HttpClient();
    using var unauthenticated = await raw.GetAsync($"http://127.0.0.1:{freePort}/status");
    Check(unauthenticated.StatusCode == HttpStatusCode.Forbidden, "controller rejects requests lacking private token");
    var requests = Enumerable.Range(0, 4).Select(async i => await Execute(await bridge.ForwardAsync(HttpMethod.Post, "/control", "{\"sequence\":" + i + "}", timeout.Token)));
    var receipts = await Task.WhenAll(requests);
    Check(receipts.Select(r => r.GetProperty("payload").GetProperty("sequence").GetInt32()).Distinct().Count() == 4,
        "serialized concurrent mutations preserve each payload");
    var repeated = await Execute(await bridge.ForwardAsync(HttpMethod.Get, "/status", null, timeout.Token));
    Check(repeated.GetProperty("pid").GetInt32() == ownedPid, "status reuses only its already owned controller");
    Check((await Execute(await bridge.ForwardAsync(HttpMethod.Get, "/report", null, timeout.Token))).GetProperty("path").GetString() == "/report", "report proxy uses fixed route");
    foreach (var path in new[] { "/live", "/snapshot", "/resources",
                                 "/tasks?page=1&filter=Active&pageSize=10&search=CNC-002",
                                 "/task?id=TASK-20261107012623978-a8ace5bc40f54fbd89a842be5e939001" })
        Check((await Execute(await bridge.ForwardAsync(HttpMethod.Get, path, null, timeout.Token))).GetProperty("path").GetString() == path,
            "read-only task proxy allowed: " + path);
    foreach (var path in new[] { "/configure", "/recover", "/recover-release", "/protocol-start", "/protocol-stop",
                                 "/equipment-start", "/equipment-control", "/equipment-stop", "/scheme-save", "/scheme-select", "/scheme-start" })
        Check((await Execute(await bridge.ForwardAsync(HttpMethod.Post, path, "{}", timeout.Token))).GetProperty("path").GetString() == path,
            "custom simulation route allowed: " + path);
    foreach (var path in new[] { "/replay", "/report?id=" + new string('a', 32), "/replay?id=" + new string('f', 32) })
        Check((await Execute(await bridge.ForwardAsync(HttpMethod.Get, path, null, timeout.Token))).GetProperty("path").GetString() == path,
            "history read route allowed: " + path);
    var archive = await ExecuteRaw(await bridge.ForwardArchiveAsync(
        "/archive?id=" + new string('b', 32), "simulation-evidence.zip", timeout.Token));
    Check(archive.ContentType == "application/zip" && archive.Disposition?.Contains("simulation-evidence.zip") == true &&
          archive.Body.AsSpan().StartsWith(new byte[] { 0x50, 0x4b }), "archive proxy returns a bounded ZIP attachment");
    var archiveError = await ExecuteRaw(await bridge.ForwardArchiveAsync(
        "/archive?id=" + new string('e', 32), "simulation-evidence.zip", timeout.Token));
    using var archiveErrorJson = JsonDocument.Parse(archiveError.Body);
    Check(archiveError.Status == 404 &&
          archiveErrorJson.RootElement.GetProperty("error").GetString() == "evidence not found",
        "archive proxy materializes controller errors before the JSON document is disposed");
    foreach (var path in new[] { "/report?id=../../field", "/replay?id=" + new string('a', 32) + "&file=secret", "/replay?id=" + new string('A', 32) })
    {
        var invalidHistoryRejected = false;
        try { await bridge.ForwardAsync(HttpMethod.Get, path, null, timeout.Token); }
        catch (ArgumentException) { invalidHistoryRejected = true; }
        Check(invalidHistoryRejected, "history route rejects arbitrary identities and extra query: " + path);
    }
    foreach (var path in new[] { "/tasks?filter=Unknown", "/tasks?page=0&pageSize=10", "/tasks?pageSize=201", "/tasks?file=secret" })
    {
        var invalidTaskQueryRejected = false;
        try { await bridge.ForwardAsync(HttpMethod.Get, path, null, timeout.Token); }
        catch (ArgumentException) { invalidTaskQueryRejected = true; }
        Check(invalidTaskQueryRejected, "task route rejects invalid query: " + path);
    }
    var rejected = false;
    try { await bridge.ForwardAsync(HttpMethod.Get, "http://example.com/", null, timeout.Token); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "bridge forbids arbitrary proxy targets");
}
Check(File.ReadAllText(Path.Combine(root, "requests.jsonl")).Contains("/shutdown"), "dispose requests authenticated controller shutdown");
var gone = false;
try { using var owned = Process.GetProcessById(ownedPid); gone = owned.HasExited; } catch (ArgumentException) { gone = true; }
Check(gone, "owned controller has exited after disposal");
Console.WriteLine($"{checks} checks passed. Evidence: {temporary}");
