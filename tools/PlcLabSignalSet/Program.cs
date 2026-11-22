using AgvDispatch.Connectors.Plc;

if (args.Length != 6 || args[1] != "--apply" || args[2] != "SET-LAB-SIGNAL")
{
    Console.Error.WriteLine(
        "Usage: PlcLabSignalSet <signal-csv> --apply SET-LAB-SIGNAL <station> <signal> <true|false>");
    return 2;
}

var csv = Path.GetFullPath(args[0]);
var station = args[3];
var signalName = args[4];
if (!File.Exists(csv))
    throw new FileNotFoundException("PLC lab signal CSV was not found.", csv);
if (!bool.TryParse(args[5], out var value))
    throw new ArgumentException("Signal value must be true or false.");

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
if (!catalog.TryGet(station, signalName, out var signal))
    throw new InvalidDataException($"Signal {station}/{signalName} is not present.");
if (!signal.Enabled || signal.Access != "Write" ||
    signal.AddressStatus is not ("LabOnly" or "Confirmed" or "EngineeringBundle") ||
    signal.DataType != "Bool")
    throw new InvalidDataException($"Refusing non-lab or non-writable signal: {station}/{signalName}.");

using var gateway = new HslPlcGateway(options, catalog, new PlcConnectionSupervisor(options));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
await gateway.WriteSignalAsync(station, signalName, value, timeout.Token);
var readback = await gateway.ReadSignalAsync(station, signalName, timeout.Token);
if (readback != value)
    throw new InvalidOperationException(
        $"Readback mismatch for {station}/{signalName}: expected {value}, actual {readback}.");

Console.WriteLine($"SET {station}/{signalName}={value.ToString().ToLowerInvariant()}");
return 0;
