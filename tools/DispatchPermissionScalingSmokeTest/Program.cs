using System.Reflection;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Runtime;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Service;
using Microsoft.Extensions.Logging.Abstractions;

var checks = 0;
void Check(bool valid, string label)
{
    if (!valid) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS " + label);
}

// D11: cancellation needs the same recovery permission as pause and resume.
var root = Path.Combine(Path.GetTempPath(), "agv-permission-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var db = DatabaseFactory.Create(new() { DatabasePath = Path.Combine(root, "test.db") });
var service = new TaskCancellationService(db, new MapExecutionGate(), null!);
var request = new TaskCancelRequest("not-a-request-id", "TASK", DispatchTaskStage.GoingToPickup, "short");
var viewer = new TrafficUserContext("SIM\\viewer", "viewer", TrafficPermission.View);
var recoverer = new TrafficUserContext("SIM\\recover", "recover", TrafficPermission.RecoverExecution);
var viewerDenied = false;
try { await service.RequestAsync(viewer, request, default); }
catch (UnauthorizedAccessException) { viewerDenied = true; }
Check(viewerDenied, "a view-only operator cannot request task cancellation");
var recoverResult = await service.RequestAsync(recoverer, request, default);
Check(!recoverResult.Accepted, "a recovery operator passes the permission gate and reaches request validation");
Check(!new TrafficUserContext("SIM\\monitor", "m", TrafficPermission.View | TrafficPermission.AcknowledgeAlarm)
    .Has(TrafficPermission.RecoverExecution), "acknowledging alarms does not imply cancellation rights");

// D12: workers scale their polling intervals with the simulated business clock.
var isolatedRoot = Path.Combine(root, "simulation");
Directory.CreateDirectory(isolatedRoot);
Environment.SetEnvironmentVariable("AGV_SIMULATION_RATE", "4");
Environment.SetEnvironmentVariable("AGV_SIMULATION_ROOT", isolatedRoot);
Environment.SetEnvironmentVariable("AGV_DISPATCH_DATABASE_PATH", Path.Combine(isolatedRoot, "isolated.db"));
var simulation = SimulationTimeProvider.FromEnvironment();
Environment.SetEnvironmentVariable("AGV_SIMULATION_RATE", null);
Environment.SetEnvironmentVariable("AGV_SIMULATION_ROOT", null);
Environment.SetEnvironmentVariable("AGV_DISPATCH_DATABASE_PATH", null);
Check(simulation.Rate == 4, "simulation clock runs at 4x for the scaling checks");
TimeSpan Delay<T>(T instance, int milliseconds) => (TimeSpan)typeof(T).GetMethod("BusinessDelay",
    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, [milliseconds])!;
var cancellationWorker = new TaskCancellationWorker(null!, NullLogger<TaskCancellationWorker>.Instance, simulation);
Check(Delay(cancellationWorker, 3000) == TimeSpan.FromMilliseconds(750),
    "task cancellation confirmation polling scales from 3 s to 750 ms at 4x");
var switchWorker = new TaskSwitchWorker(null!, NullLogger<TaskSwitchWorker>.Instance, simulation);
Check(Delay(switchWorker, 2000) == TimeSpan.FromMilliseconds(500),
    "controlled task switch polling scales from 2 s to 500 ms at 4x");
var realTime = new TaskSwitchWorker(null!, NullLogger<TaskSwitchWorker>.Instance);
Check(Delay(realTime, 2000) == TimeSpan.FromSeconds(2), "workers without a simulated clock keep real-time polling");

Console.WriteLine($"PASS: {checks} permission and scaling checks; isolated SQLite only.");
