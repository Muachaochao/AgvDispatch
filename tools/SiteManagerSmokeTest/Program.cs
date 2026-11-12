using System.Diagnostics;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Wpf.Management;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--inspect-site")
        {
            var actual = new ManagementViewModel(SiteEnvironment.Find(args[1]));
            actual.RefreshAsync().GetAwaiter().GetResult();
            Console.WriteLine($"{actual.Release}\n后台：{actual.ServiceState}\nWeb：{actual.WebState}\n{actual.Runtime}\n{actual.Paths}");
            return actual.ServiceState == "配置检查失败" ? 1 : 0;
        }
        var temporary = Path.Combine(Path.GetTempPath(), "agv-manager-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var site = Fixture(temporary);
            Check(site.ReadRelease().ReleaseId == "1.0.0", "Daily release is resolved and both binary identities verified");
            Reject(() => SiteEnvironment.ChildPath(temporary, "../outside"), "Release/path escape rejected");
            Reject(() => SiteEnvironment.ChildPath(temporary, "C:/outside"), "Absolute release path rejected");
            var archives = new SiteArchives(site);
            var backup = archives.CreateBackup();
            Check(File.Exists(backup), "Stopped-site backup includes a valid SQLite database");
            File.WriteAllText(Path.Combine(site.DataRoot, "Web工程方案", "marker.txt"), "changed");
            archives.Restore(backup);
            Check(File.ReadAllText(Path.Combine(site.DataRoot, "Web工程方案", "marker.txt")) == "original", "Restore swaps a fully verified staging directory");
            Check(Directory.GetDirectories(site.BackupRoot, "恢复前运行数据-*").Length == 1, "Previous runtime data remains available for rollback");
            var poisoned = Path.Combine(temporary, "poisoned.zip"); File.Copy(backup, poisoned);
            using (var zip = ZipFile.Open(poisoned, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("data/Web工程方案/marker.txt")!; entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("data/Web工程方案/marker.txt").Open()); writer.Write("tampered");
            }
            Reject(() => archives.Restore(poisoned), "Tampered backup rejected before replacing runtime data");
            Check(File.ReadAllText(Path.Combine(site.DataRoot, "Web工程方案", "marker.txt")) == "original", "Rejected restore preserves current runtime data");
            var badManifest = new SiteArchiveManifest("agv-site-backup-v1", "1.0.0", DateTimeOffset.UtcNow, [new("data/../../escape", 1, new string('A',64))]);
            Reject(() => SiteArchives.ValidateEntries(badManifest, ["manifest.json", "data/../../escape"]), "ZIP traversal rejected");
            Reject(() => SiteArchives.ValidateEntries(badManifest with { Files = [new("data/a", 1, new string('A',64)),new("data/A", 1,new string('A',64))] }, ["manifest.json","data/a","data/A"]), "Case-insensitive duplicate ZIP paths rejected");
            var originalSettings = JsonNode.Parse(File.ReadAllText(site.SettingsPath))!;
            new SiteOperations(site).SaveConcurrency(7);
            var updatedSettings = JsonNode.Parse(File.ReadAllText(site.SettingsPath))!;
            Check(updatedSettings["maxConcurrentTasks"]!.GetValue<int>() == 7 && updatedSettings["productionDispatch"]!.GetValue<bool>() == originalSettings["productionDispatch"]!.GetValue<bool>(), "Concurrency save preserves execution permission and stores a backup");
            Check(Directory.GetFiles(site.BackupRoot, "入口配置-*.json").Length == 1, "Settings backup created before changing daily configuration");
            Reject(() => new SiteOperations(site).SaveConcurrency(0), "Invalid concurrency rejected");
            var release = site.ReadRelease();
            using (var held = SiteOperations.LockStoppedSite(release, site.Root)) Reject(() => new SiteOperations(site).SaveConcurrency(8), "Launcher lock excludes simultaneous maintenance/start");
            var now = DateTimeOffset.UtcNow;
            var status = new SystemStatusSnapshot(now, new("test","test",123,now,TimeSpan.Zero), new(true,0,"ok"),
                new("PLC","PLC",default,0,now,null,null,null,0,0,0,0,0,0,0,0), [], new(0,0,0,0,[]),
                new("test",0,0,0,"test","test",default,null,null,null), new(false,false,false,false,false,false,null,null));
            SiteOperations.ValidateIdleStatus(status,123); Check(true, "Idle observation accepted when device communication is disabled");
            Reject(() => SiteOperations.ValidateIdleStatus(status with { GeneratedAt = now.AddMinutes(-1) },123), "Stale backend status rejected");
            Reject(() => SiteOperations.ValidateIdleStatus(status,124), "Foreign backend process rejected");
            Reject(() => SiteOperations.ValidateIdleStatus(status with { Tasks = new(0,0,0,1,[]) },123), "Faulted/review work prevents stop");
            Reject(() => SiteOperations.ValidateIdleStatus(status with { Safety = status.Safety with { AgvCommunicationEnabled = true } },123), "Missing live fleet observations prevent stop");
            SiteOperations.ValidateCharging(new(false,new(),null,[],now));
            Reject(() => SiteOperations.ValidateCharging(new(false,new(),null,[new("CHARGE-test","AGV-01",ChargingPhase.Hold,now,now,"review")],now)), "Held charging workflow prevents stop");
            var vehicle=JsonSerializer.Deserialize<AgvDiagnostic>("""{"vehicleId":"AGV-01","connection":"Online","quality":"Good","lastSuccessfulPollAt":"NOW","taskCount":0,"velocityX":null,"velocityY":0,"angularVelocity":0}""".Replace("NOW",now.ToString("O")),new JsonSerializerOptions(JsonSerializerDefaults.Web){Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}})!;
            Reject(() => SiteOperations.ValidateIdleStatus(status with {Agvs=[vehicle],Safety=status.Safety with{AgvCommunicationEnabled=true}},123), "Unknown velocity is never treated as stopped");
            VerifyPipe(status);
            var diagnostics = archives.ExportDiagnostics("offline fixture", "smoke test");
            using (var zip = ZipFile.OpenRead(diagnostics)) Check(zip.Entries.All(e => !e.FullName.EndsWith(".db") && !e.FullName.Contains("appsettings")), "Diagnostics do not include raw database or device configuration");
            var mismatched = JsonNode.Parse(File.ReadAllText(site.SettingsPath))!; mismatched["releaseId"]="other"; File.WriteAllText(site.SettingsPath,mismatched.ToJsonString());
            Reject(() => site.ReadRelease(), "Daily pointer/manifest mismatch rejected");
            mismatched["releaseId"]="1.0.0"; File.WriteAllText(site.SettingsPath,mismatched.ToJsonString());
            Check(typeof(ManagementWindow).Assembly.GetType("AgvDispatch.Wpf.MainWindow") is null, "Historical dispatch shell is absent from production assembly");
            Check(!typeof(ManagementWindow).Assembly.GetReferencedAssemblies().Any(a => a.Name is "Prism.Wpf" or "HandyControl" or "LiveChartsCore" or "AgvDispatch.Infrastructure"), "Production manager has no legacy UI or infrastructure dependency");
            var evidence = args.Length == 0 ? Path.Combine(temporary,"screenshots") : Path.GetFullPath(args[0]); Directory.CreateDirectory(evidence);
            RenderPages(site,evidence);
            Console.WriteLine($"PASS {_checks} checks. Screenshots: {evidence}");
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(temporary)) Directory.Delete(temporary,true); }
    }

    private static SiteEnvironment Fixture(string root)
    {
        var bundle=Path.Combine(root,"fixture"); Directory.CreateDirectory(Path.Combine(bundle,"Service")); Directory.CreateDirectory(Path.Combine(bundle,"Web","host"));
        var assembly=typeof(ManagementWindow).Assembly.Location;
        foreach(var folder in new[]{Path.Combine(bundle,"Service"),Path.Combine(bundle,"Web","host")})
        {
            var stem=folder.EndsWith("Service")?"AgvDispatch.Service":"AgvDispatch.WebHost";
            File.Copy(assembly,Path.Combine(folder,stem+".dll")); File.WriteAllText(Path.Combine(folder,stem+".exe"),"fixture only");
        }
        File.WriteAllText(Path.Combine(bundle,"release-manifest.json"),JsonSerializer.Serialize(new { schema="agv-commissioning-release",releaseId="1.0.0",port=5199,pipeName="AgvManager-Isolated-"+Guid.NewGuid().ToString("N") }));
        File.WriteAllText(Path.Combine(root,"最新版Web.json"),JsonSerializer.Serialize(new{directory="fixture",releaseId="1.0.0",url=SiteEnvironment.WebUrl,productionDispatch=false,maxConcurrentTasks=3}));
        var site=new SiteEnvironment(root); Directory.CreateDirectory(Path.GetDirectoryName(site.DatabasePath)!);
        using(var connection=new SqliteConnection("Pooling=False;Data Source="+site.DatabasePath)) { connection.Open(); using var command=connection.CreateCommand(); command.CommandText="CREATE TABLE marker(Value TEXT); INSERT INTO marker VALUES ('original');"; command.ExecuteNonQuery(); }
        Directory.CreateDirectory(Path.Combine(site.DataRoot,"Web工程方案")); File.WriteAllText(Path.Combine(site.DataRoot,"Web工程方案","marker.txt"),"original"); return site;
    }

    private static void VerifyPipe(SystemStatusSnapshot status)
    {
        var name="AgvManagerPipeTest-"+Guid.NewGuid().ToString("N");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server=Task.Run(async()=>
        {
            await using var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(timeout.Token);var header=new byte[4];await pipe.ReadExactlyAsync(header,timeout.Token);
            var body=new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];await pipe.ReadExactlyAsync(body,timeout.Token);
            var request=JsonSerializer.Deserialize<TrafficControlPipeRequest>(body,SiteEnvironment.Json)!;
            if(request.Operation!=TrafficControlOperations.GetSystemStatus)throw new Exception("Unexpected device mutation");
            var reply=JsonSerializer.SerializeToUtf8Bytes(new TrafficControlPipeResponse(true,null,JsonSerializer.SerializeToElement(status,SiteEnvironment.Json)),SiteEnvironment.Json);
            BinaryPrimitives.WriteInt32LittleEndian(header,reply.Length);await pipe.WriteAsync(header,timeout.Token);await pipe.WriteAsync(reply,timeout.Token);await pipe.FlushAsync(timeout.Token);
        });
        var received=new LocalBackendClient(name).ReadStatusAsync(timeout.Token).GetAwaiter().GetResult();server.GetAwaiter().GetResult();
        Check(received.Service.ProcessId==123,"Local backend framing reads status without sending device commands");
    }

    private static void RenderPages(SiteEnvironment site,string evidence)
    {
        var vm=new ManagementViewModel(site); vm.RefreshAsync().GetAwaiter().GetResult();
        var app=new ManagementApp { ShutdownMode=ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        var window=new ManagementWindow(vm) { Width=1180,Height=800 };
        var root=(FrameworkElement)window.Content;
        // Render the content without showing/starting Loaded: no device, browser or service actions.
        for(var index=0;index<vm.Pages.Length;index++)
        {
            vm.SelectedPage=index; root.Measure(new Size(1180,800)); root.Arrange(new Rect(0,0,1180,800)); root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Render);
            var image=new RenderTargetBitmap(1180,800,96,96,PixelFormats.Pbgra32); image.Render(root);
            var pixels=new byte[1180*800*4];image.CopyPixels(pixels,1180*4,0);if(!pixels.Where((_,i)=>i%4==3).Any(a=>a>0))throw new Exception("Blank rendered page");
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file=File.Create(Path.Combine(evidence,$"page-{index}.png")); encoder.Save(file);
        }
        window.Close(); app.Shutdown(); Check(true,"All software assistant pages load and render without a running backend");
    }
    private static void Check(bool condition,string name) { if(!condition)throw new Exception(name); _checks++; Console.WriteLine("PASS "+name); }
    private static void Reject(Action action,string name) { try{action();}catch(Exception e) when(e is InvalidDataException or ArgumentException or IOException or InvalidOperationException){Check(true,name);return;}throw new Exception("Expected rejection: "+name); }
}
