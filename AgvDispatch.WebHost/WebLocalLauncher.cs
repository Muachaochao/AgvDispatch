using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgvDispatch.WebHost;

// Starts only a complete local bundle. No discovery by process name and no configuration migration.
internal static class WebLocalLauncher
{
    private sealed record OwnedProcess(int Id,long StartedUtcTicks,string Path);
    private sealed record Session(string ReleaseId,OwnedProcess Service,OwnedProcess Web,string NavigationTestVehicle="",
        string PlcCycleTestVehicle="",string PlcCycleTestStation="",string BusinessRunVehicle="",bool ForkTest=false,
        bool AutoNavigationDetour=false,string FleetVehicles="",int FleetMaxConcurrentTasks=0,
        bool ProductionDispatch=false,int ProductionMaxConcurrentTasks=0);
    public static async Task<int> RunAsync(string[] args)
    {
        var created=new List<Process>();
        try
        {
            var index=Array.IndexOf(args,"--bundle-root");
            if(index<0||index+1>=args.Length)throw new ArgumentException("Missing --bundle-root.");
            var root=System.IO.Path.GetFullPath(args[index+1]);
            var mode=WebLocalLaunchMode.Parse(WebDailyLaunchSettings.Apply(args));
            using var manifest=JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(root,"release-manifest.json")));
            if(manifest.RootElement.GetProperty("schema").GetString()!="agv-commissioning-release")throw new InvalidDataException("Unsupported release manifest.");
            var release=manifest.RootElement.GetProperty("releaseId").GetString()!;
            var port=manifest.RootElement.GetProperty("port").GetInt32();
            var pipe=manifest.RootElement.GetProperty("pipeName").GetString()!;
            var storeArg=Array.IndexOf(args,"--project-store-path");
            var dailyData = args.Contains("--daily-data") ? System.IO.Path.Combine(System.IO.Directory.GetParent(root)!.FullName,"运行数据") : null;
            var storePath=System.IO.Path.GetFullPath(storeArg>=0&&storeArg+1<args.Length?args[storeArg+1]:dailyData is not null ? System.IO.Path.Combine(dailyData,"Web工程方案") : System.IO.Path.Combine(root,"Web","data","projects"));
            var storeId=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(storePath)));
            if(port is <1024 or >65535||string.IsNullOrWhiteSpace(pipe))throw new InvalidDataException("Invalid local endpoint.");
            var servicePath=System.IO.Path.Combine(root,"Service","AgvDispatch.Service.exe");
            var webPath=System.IO.Path.Combine(root,"Web","host","AgvDispatch.WebHost.exe");
            foreach(var pair in new[]{(servicePath,"AgvDispatch.Service.dll"),(webPath,"AgvDispatch.WebHost.dll")})
                if(!File.Exists(pair.Item1)||!AdapterBuildIdentity.MatchesRelease(FileVersionInfo.GetVersionInfo(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(pair.Item1)!,pair.Item2)).ProductVersion,release))
                    throw new InvalidDataException("Release components do not match. Rebuild the complete bundle.");
            var stateDir=System.IO.Path.Combine(root,"Runtime");Directory.CreateDirectory(stateDir);
            using var exclusive=new FileStream(System.IO.Path.Combine(stateDir,"launcher.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            {
                var statePath=System.IO.Path.Combine(stateDir,"launcher-session.json");
                Session? session=File.Exists(statePath)?JsonSerializer.Deserialize<Session>(File.ReadAllText(statePath)):null;
                if(session is not null&&(session.ReleaseId!=release||session.Service.Path!=servicePath||session.Web.Path!=webPath))throw new InvalidDataException("Session identity mismatch. No process was stopped.");
                bool serviceAlive=Matches(session?.Service),webAlive=Matches(session?.Web);
                if(args.Contains("--stop"))
                {
                    // An explicit stop request can affect only processes this launcher recorded and still owns.
                    foreach(var owned in new[]{session?.Web,session?.Service})if(Matches(owned))
                    {using var process=Process.GetProcessById(owned!.Id);process.Kill(false);await process.WaitForExitAsync();}
                    Console.WriteLine("Stopped only the recorded processes for this release. Browser windows are unchanged.");return 0;
                }
                if(serviceAlive&&(session!.NavigationTestVehicle!=mode.NavigationVehicle||session.PlcCycleTestVehicle!=mode.PlcCycleVehicle||session.PlcCycleTestStation!=mode.PlcCycleStation||session.BusinessRunVehicle!=mode.BusinessVehicle||session.ForkTest!=mode.ForkTest||session.FleetVehicles!=string.Join(",",mode.FleetVehicles)||
                    session.ProductionDispatch!=mode.ProductionDispatch||session.ProductionMaxConcurrentTasks!=mode.ProductionMaxConcurrentTasks||
                    session.AutoNavigationDetour!=mode.AutoNavigationDetour||
                    (session.FleetMaxConcurrentTasks>0?session.FleetMaxConcurrentTasks:string.IsNullOrEmpty(session.FleetVehicles)?0:session.FleetVehicles.Split(',').Length)!=mode.FleetMaxConcurrentTasks))
                    throw new InvalidOperationException("Running session uses another test mode/vehicle/station. Stop this bundle before changing mode.");
                if(serviceAlive!=webAlive)throw new InvalidOperationException("Only one recorded component is running. Use Stop-Latest-Web.cmd before restarting; no process was replaced.");
                var address=$"http://127.0.0.1:{port}";
                using var http=new HttpClient{BaseAddress=new Uri(address),Timeout=TimeSpan.FromSeconds(3)};
                if(!serviceAlive)
                {
                    var probe=new TcpListener(IPAddress.Loopback,port);
                    try{probe.Start();}catch(SocketException){throw new InvalidOperationException($"Port {port} is occupied. No existing program was stopped.");}finally{probe.Stop();}
                    var serviceArgs=mode.ServiceArguments(pipe);
                    if(dailyData is not null) serviceArgs=[..serviceArgs,$"--Database:Path={System.IO.Path.Combine(dailyData,"后台5199","agv-dispatch.db")}"];
                    var service=Start(servicePath,serviceArgs);created.Add(service);
                    // Applying an engineering plan only persists validated task parameters.
                    // Dispatch, AGV commands and PLC writes remain independently gated.
                    var web=Start(webPath,[$"--Web:Port={port}",$"--Web:PipeName={pipe}",$"--Web:EnableTaskSubmission={mode.IsBusiness}","--Web:EnableRecoveryActions=true","--Web:EnableTaskCancellation=true","--Web:EnableEngineeringRuntimeApply=true",$"--Web:ProjectStorePath={storePath}",$"--Web:StaticPath={System.IO.Path.Combine(root,"Web","wwwroot")}"]);
                    created.Add(web);session=new(release,Describe(service,servicePath),Describe(web,webPath),mode.NavigationVehicle,mode.PlcCycleVehicle,mode.PlcCycleStation,mode.BusinessVehicle,mode.ForkTest,mode.AutoNavigationDetour,string.Join(",",mode.FleetVehicles),mode.FleetMaxConcurrentTasks,mode.ProductionDispatch,mode.ProductionMaxConcurrentTasks);
                    var pending=statePath+".tmp";File.WriteAllText(pending,JsonSerializer.Serialize(session,new JsonSerializerOptions{WriteIndented=true}));File.Move(pending,statePath,true);
                }
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
                while(true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if(!Matches(session!.Service)||!Matches(session.Web))throw new InvalidOperationException("A release component exited. Check Service/logs and release configuration.");
                    try
                    {
                        using var config=JsonDocument.Parse(await http.GetStringAsync("/api/config",timeout.Token));
                        if(!AdapterBuildIdentity.MatchesRelease(config.RootElement.GetProperty("adapter").GetProperty("informationalVersion").GetString(),release)||config.RootElement.GetProperty("servicePipeName").GetString()!=pipe||config.RootElement.GetProperty("projectStoreId").GetString()!=storeId)
                            throw new InvalidDataException("The web endpoint belongs to a different release.");
                        using var snapshot=JsonDocument.Parse(await http.GetStringAsync("/api/snapshot",timeout.Token));
                        if(snapshot.RootElement.GetProperty("connected").GetBoolean())break;
                    }
                    catch(Exception ex)when(ex is HttpRequestException or TaskCanceledException){if(timeout.IsCancellationRequested)throw;}
                    await Task.Delay(400,timeout.Token);
                }
                foreach(var process in created)process.Dispose();
                created.Clear(); // Started successfully; a later repeated click reuses the exact recorded processes.
                Console.WriteLine($"Ready: {address}/ | Release: {release}");
                if(!args.Contains("--no-browser"))Process.Start(new ProcessStartInfo(address+mode.Page){UseShellExecute=true});
                return 0;
            }
        }
        catch(Exception ex)
        {
            foreach(var process in created)try{if(!process.HasExited){process.Kill(false);await process.WaitForExitAsync();}}catch(InvalidOperationException){}
            Console.Error.WriteLine("Web startup failed: "+ex.Message);return 1;
        }
        finally{foreach(var process in created)process.Dispose();}
    }
    private static Process Start(string path,string[] args)
    {
        var info=new ProcessStartInfo(path){WorkingDirectory=System.IO.Path.GetDirectoryName(path)!,UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden};
        foreach(var arg in args)info.ArgumentList.Add(arg);
        return Process.Start(info)??throw new InvalidOperationException("Could not start release component.");
    }
    private static OwnedProcess Describe(Process p,string path)=>new(p.Id,p.StartTime.ToUniversalTime().Ticks,path);
    private static bool Matches(OwnedProcess? owned)
    {
        if(owned is null)return false;
        try{using var p=Process.GetProcessById(owned.Id);return !p.HasExited&&p.StartTime.ToUniversalTime().Ticks==owned.StartedUtcTicks&&string.Equals(p.MainModule?.FileName,owned.Path,StringComparison.OrdinalIgnoreCase);}
        catch(Exception ex)when(ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception){return false;}
    }
}
