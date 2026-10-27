using PlcHandshakeLab;

var config = NetworkLabConfig.CreateDefault();
var directory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "AgvDispatch", "PlcHandshakeLab", DateTime.Now.ToString("yyyyMMdd"));
Directory.CreateDirectory(directory);
var session = DateTime.Now.ToString("HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8];

using var lab = new NetworkLab(config, Path.Combine(directory, session + ".jsonl"));
lab.SelectStage("CNC-001", 0);
await using var server = new S7LabServer(lab);
server.Start();
await using var control = new LabControlServer(lab);
control.Start();

Console.WriteLine($"READY {config.BindAddress}:{server.Port} control=http://127.0.0.1:10201 journal={lab.JournalPath}");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    stop.Cancel();
};
var automatic = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
    while (await timer.WaitForNextTickAsync(stop.Token)) lab.TickAutomatic();
}, stop.Token);
try
{
    await automatic;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
}
