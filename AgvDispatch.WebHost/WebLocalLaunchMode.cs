namespace AgvDispatch.WebHost;

// Permissions are selected explicitly for this launch and are never restored from the session file.
internal sealed record WebLocalLaunchMode(string NavigationVehicle, string PlcCycleVehicle, string PlcCycleStation)
{
    public string BusinessVehicle { get; init; } = "";
    public bool ForkTest { get; init; }
    public string[] FleetVehicles { get; init; }=[];
    public bool IsBusiness => BusinessVehicle.Length > 0 || FleetVehicles.Length>0;
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
        var business = Read("--business-run-vehicle");
        var fleetIndex=Array.IndexOf(args,"--fleet-test-vehicles");
        string[] fleet=[];
        if(fleetIndex>=0)
        {
            if(args.Count(a=>a=="--fleet-test-vehicles")!=1||fleetIndex+1>=args.Length)throw new ArgumentException("Specify exactly two fleet vehicles.");
            fleet=args[fleetIndex+1].Split(',');
            if(fleet.Length!=2||fleet.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=2||fleet.Any(v=>v.Length is <1 or >32||v.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '-' and not '_')))
                throw new ArgumentException("Specify two different valid fleet vehicle IDs.");
        }
        var cycleVehicle = Read("--plc-cycle-test-vehicle");
        var cycleStation = Read("--plc-cycle-test-station");
        if ((cycleVehicle.Length == 0) != (cycleStation.Length == 0))
            throw new ArgumentException("PLC cycle testing requires both a vehicle and a station.");
        if (navigation.Length > 0 && cycleVehicle.Length > 0)
            throw new ArgumentException("Choose either manual navigation or PLC cycle testing for this launch.");
        if (business.Length > 0 && (navigation.Length > 0 || cycleVehicle.Length > 0))
            throw new ArgumentException("Business execution cannot share an isolated test launch.");
        if(fleet.Length>0&&(business.Length>0||navigation.Length>0||cycleVehicle.Length>0))throw new ArgumentException("Fleet testing requires an exclusive launch mode.");
        return new(cycleVehicle.Length > 0 ? cycleVehicle : navigation, cycleVehicle, cycleStation) { BusinessVehicle = business, ForkTest=forklift.Length>0,FleetVehicles=fleet };
    }

    public string[] ServiceArguments(string pipe) =>
    [
        $"--Security:PipeName={pipe}", "--Plc:Enabled=false", $"--Plc:AllowWrites={IsBusiness}", "--Agv:Enabled=false",
        $"--Agv:AllowCommands={(IsBusiness || NavigationVehicle.Length > 0 ? "true" : "false")}",
        $"--TaskCreation:Enabled={IsBusiness}", $"--Dispatching:Enabled={IsBusiness}", $"--AgvCommandOutbox:Enabled={IsBusiness}",
        $"--DispatchTaskSteps:Enabled={IsBusiness}", $"--HandshakeExecution:Enabled={IsBusiness}",
        "--TaskCreation:AutomaticCreationEnabled=false",
        $"--FleetTest:Enabled={FleetVehicles.Length==2}",
        $"--FleetTest:VehicleIds:0={FleetVehicles.ElementAtOrDefault(0)??""}",
        $"--FleetTest:VehicleIds:1={FleetVehicles.ElementAtOrDefault(1)??""}",
        $"--Dispatching:MaxConcurrentTasks={(FleetVehicles.Length==2?2:1)}",
        $"--NavigationTest:Enabled={(NavigationVehicle.Length > 0 ? "true" : "false")}",
        $"--NavigationTest:VehicleId={(IsBusiness ? BusinessVehicle : NavigationVehicle)}",
        $"--NavigationTest:ForkEnabled={ForkTest}",
        $"--PlcCycleTest:Enabled={(IsPlcCycle ? "true" : "false")}",
        $"--PlcCycleTest:VehicleId={PlcCycleVehicle}", $"--PlcCycleTest:StationId={PlcCycleStation}"
    ];
}
