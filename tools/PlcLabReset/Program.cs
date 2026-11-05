using AgvDispatch.Connectors.Plc;

if (args.Length != 3 || args[1] != "--apply" || args[2] != "RESET-LAB-SIGNALS")
{
    Console.Error.WriteLine("Usage: PlcLabReset <release-root> --apply RESET-LAB-SIGNALS");
    return 2;
}

var root = Path.GetFullPath(args[0]);
var csv = Path.Combine(root, "Service", "config", "plc-lab-nine-cnc-signals.csv");
if (!File.Exists(csv))
{
    throw new FileNotFoundException("PLC lab signal CSV was not found.", csv);
}

var options = new PlcOptions
{
    Enabled = true,
    AllowWrites = true,
    VerifyWrites = true,
    DeviceId = "PLC-MAIN",
    DisplayName = "总控PLC",
    SiemensSeries = "S1200",
    IpAddress = "127.0.0.1",
    Port = 10200,
    ConnectTimeoutMs = 3000,
    ReceiveTimeoutMs = 3000,
    PollIntervalMs = 500,
    ReconnectDelayMs = 3000,
    StaleAfterMs = 5000,
    InterruptedFailureThreshold = 3,
    ReadBlockSizeBytes = 200,
    SignalCsvPath = csv
};

var catalog = PlcSignalCatalog.Load(csv);
var targets = Enumerable.Range(1, 4)
    .SelectMany(index => new[]
    {
        $"CNC-{index:000}/FullBoxRequestAcknowledged",
        $"CNC-{index:000}/FullBoxRemoved",
        $"CNC-{index:000}/EmptyBoxTaskAcknowledged",
        $"CNC-{index:000}/EmptyBoxPlaced"
    })
    .Concat(new[]
    {
        "DUMPER-001/AgvAtWaitingPosition",
        "DUMPER-001/AgvAtDumpingPosition"
    })
    .ToArray();

foreach (var target in targets)
{
    var parts = target.Split('/', 2);
    if (parts.Length != 2 || !catalog.TryGet(parts[0], parts[1], out var signal))
    {
        throw new InvalidDataException($"Missing PLC lab output: {target}.");
    }
    if (!signal.Enabled || signal.Access != "Write" || signal.AddressStatus != "LabOnly" || signal.DataType != "Bool")
    {
        throw new InvalidDataException($"Refusing non-lab or non-writable signal: {target}.");
    }
}

using var gateway = new HslPlcGateway(options, catalog, new PlcConnectionSupervisor(options));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
foreach (var target in targets)
{
    var parts = target.Split('/', 2);
    await gateway.WriteSignalAsync(parts[0], parts[1], false, timeout.Token);
    Console.WriteLine($"RESET {target}=false");
}

return 0;
