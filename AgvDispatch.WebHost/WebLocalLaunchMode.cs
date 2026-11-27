namespace AgvDispatch.WebHost;

// Permissions come from explicit launch selection (or the daily production policy), never the process session file.
internal sealed record WebLocalLaunchMode(string NavigationVehicle, string PlcCycleVehicle, string PlcCycleStation)
{
    public string BusinessVehicle { get; init; } = "";
    public bool ProductionDispatch { get; init; }
    public int ProductionMaxConcurrentTasks { get; init; }
    public bool ForkTest { get; init; }
    public bool AutoNavigationDetour { get; init; }
    public string[] FleetVehicles { get; init; }=[];
    public int FleetMaxConcurrentTasks { get; init; }
    public bool IsBusiness => ProductionDispatch || BusinessVehicle.Length > 0 || FleetVehicles.Length>0;
    public bool IsPlcCycle => PlcCycleVehicle.Length > 0;
    public string Page => IsBusiness ? "/#/tasks" : IsPlcCycle ? "/#/plc-cycle-test" : NavigationVehicle.Length > 0 ? "/#/navigation-test" : "/";

    public static WebLocalLaunchMode Parse(string[] args)
    {
        string Read(string name)
        {
            var positions = args.Select((value, index) => (value, index)).Where(pair => pair.value == name).ToArray();
            if (positions.Length == 0) return "";
            if (positions.Length != 1 || positions[0].index + 1 >= args.Length)
                throw new ArgumentException($"Missing or repeated {name}.");
            var value = args[positions[0].index + 1];
            if (string.IsNullOrWhiteSpace(value) || value.Length > 32 ||
                value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                throw new ArgumentException($"Invalid identifier for {name}.");
            return value;
        }
        var navigation = Read("--navigation-test-vehicle");
        var forklift = Read("--forklift-test-vehicle");
        if(forklift.Length>0&&navigation.Length>0)throw new ArgumentException("Choose one navigation test vehicle option.");
        if(forklift.Length>0)navigation=forklift;
        if(args.Count(value=>value=="--auto-navigation-detour")>1)
            throw new ArgumentException("Repeated --auto-navigation-detour.");
        var autoDetour=args.Contains("--auto-navigation-detour");
        var business = Read("--business-run-vehicle");
        if (args.Count(value => value == "--production-dispatch") > 1)
            throw new ArgumentException("Repeated --production-dispatch.");
        var production = args.Contains("--production-dispatch");
        var productionConcurrency = 0;
        var productionConcurrencyIndex = Array.IndexOf(args, "--production-max-concurrent");
        if (production || productionConcurrencyIndex >= 0)
        {
            if (!production || args.Count(value => value == "--production-max-concurrent") != 1 ||
                productionConcurrencyIndex < 0 || productionConcurrencyIndex + 1 >= args.Length ||
                !int.TryParse(args[productionConcurrencyIndex + 1], out productionConcurrency) ||
                productionConcurrency is < 1 or > 1000)
                throw new ArgumentException("Production dispatch requires --production-max-concurrent between 1 and 1000.");
        }
        var fleetIndex=Array.IndexOf(args,"--fleet-test-vehicles");
        string[] fleet=[];
        if(fleetIndex>=0)
        {
            if(args.Count(a=>a=="--fleet-test-vehicles")!=1||fleetIndex+1>=args.Length)throw new ArgumentException("Specify the configured fleet vehicle list.");
            fleet=args[fleetIndex+1].Split(',');
            if(fleet.Length is <2 or >1000||fleet.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=fleet.Length||fleet.Any(v=>v.Length is <1 or >32||v.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '-' and not '_')))
                throw new ArgumentException("Specify at least two different valid fleet vehicle IDs.");
        }
        var fleetConcurrency=fleet.Length;
        var concurrencyIndex=Array.IndexOf(args,"--fleet-test-max-concurrent");
        if(concurrencyIndex>=0)
        {
            if(fleet.Length==0||args.Count(a=>a=="--fleet-test-max-concurrent")!=1||concurrencyIndex+1>=args.Length||
                !int.TryParse(args[concurrencyIndex+1],out fleetConcurrency)||fleetConcurrency<1||fleetConcurrency>fleet.Length)
                throw new ArgumentException("Fleet concurrency must be between 1 and the configured fleet size.");
        }
        var cycleVehicle = Read("--plc-cycle-test-vehicle");
        var cycleStation = Read("--plc-cycle-test-station");
        // The daily entry can preserve a selected test mode, while the fleet,
        // business and other explicit entries must be able to replace it.
        var defaultNavigation = Read("--default-navigation-test-vehicle");
        if(args.Count(value=>value=="--default-auto-navigation-detour")>1)
            throw new ArgumentException("Repeated --default-auto-navigation-detour.");
        var defaultAutoDetour=args.Contains("--default-auto-navigation-detour");
        if(defaultAutoDetour&&defaultNavigation.Length==0)
            throw new ArgumentException("Default automatic detour requires a default test vehicle.");
        if(!production&&navigation.Length==0&&business.Length==0&&fleet.Length==0&&cycleVehicle.Length==0&&cycleStation.Length==0)
        {
            navigation=defaultNavigation;
            autoDetour|=defaultAutoDetour;
        }
        if ((cycleVehicle.Length == 0) != (cycleStation.Length == 0))
            throw new ArgumentException("PLC cycle testing requires both a vehicle and a station.");
        if (navigation.Length > 0 && cycleVehicle.Length > 0)
            throw new ArgumentException("Choose either manual navigation or PLC cycle testing for this launch.");
        if (business.Length > 0 && (navigation.Length > 0 || cycleVehicle.Length > 0))
            throw new ArgumentException("Business execution cannot share an isolated test launch.");
        if(fleet.Length>0&&(business.Length>0||navigation.Length>0||cycleVehicle.Length>0))throw new ArgumentException("Fleet testing requires an exclusive launch mode.");
        if (production && (business.Length > 0 || fleet.Length > 0 || navigation.Length > 0 || cycleVehicle.Length > 0))
            throw new ArgumentException("Production dispatch cannot share an isolated test or selected-vehicle run mode.");
        if(autoDetour&&navigation.Length==0)
            throw new ArgumentException("Automatic detour requires an explicit single navigation test vehicle.");
        return new(cycleVehicle.Length > 0 ? cycleVehicle : navigation, cycleVehicle, cycleStation)
            { BusinessVehicle=business, ProductionDispatch=production, ProductionMaxConcurrentTasks=productionConcurrency,
              ForkTest=forklift.Length>0, AutoNavigationDetour=autoDetour,
              FleetVehicles=fleet, FleetMaxConcurrentTasks=fleetConcurrency };
    }

    public string[] ServiceArguments(string pipe) =>
    [
        $"--Security:PipeName={pipe}", "--Plc:Enabled=false", $"--Plc:AllowWrites={IsBusiness}", "--Agv:Enabled=false",
        $"--Agv:AllowCommands={(IsBusiness || NavigationVehicle.Length > 0 ? "true" : "false")}",
        $"--TaskCreation:Enabled={IsBusiness}", $"--Dispatching:Enabled={IsBusiness}", $"--AgvCommandOutbox:Enabled={IsBusiness}",
        $"--Dispatching:RequireGlobalMatching={ProductionDispatch}",
        $"--DispatchTaskSteps:Enabled={IsBusiness}", $"--HandshakeExecution:Enabled={IsBusiness}",
        $"--TaskCreation:AutomaticCreationEnabled={(ProductionDispatch ? "true" : "false")}",
        $"--FleetTest:Enabled={FleetVehicles.Length>=2}",
        ..(FleetVehicles.Length>0?FleetVehicles:["",""]).Select((id,index)=>$"--FleetTest:VehicleIds:{index}={id}"),
        $"--Dispatching:MaxConcurrentTasks={(ProductionDispatch ? ProductionMaxConcurrentTasks : FleetVehicles.Length>0?FleetMaxConcurrentTasks:1)}",
        $"--NavigationTest:Enabled={(NavigationVehicle.Length > 0 ? "true" : "false")}",
        $"--NavigationTest:VehicleId={(IsBusiness ? BusinessVehicle : NavigationVehicle)}",
        $"--NavigationTest:ForkEnabled={ForkTest}",
        $"--NavigationTest:AutoDetourEnabled={AutoNavigationDetour}",
        $"--PlcCycleTest:Enabled={(IsPlcCycle ? "true" : "false")}",
        $"--PlcCycleTest:VehicleId={PlcCycleVehicle}", $"--PlcCycleTest:StationId={PlcCycleStation}",
        // A local one-click business/test launch can only write under the
        // interactive account that owns this process. The pipe ACL already
        // restricts access to that account; this is not a shared operator role.
        ..(IsBusiness || NavigationVehicle.Length > 0 || IsPlcCycle
            ? new[]
            {
                $"--Security:Users:0:WindowsIdentity={Environment.UserDomainName}\\{Environment.UserName}",
                "--Security:Users:0:DisplayName=本机日常操作",
                "--Security:Users:0:Role=Administrator"
            }
            : [])
    ];
}
