using System.Reflection;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Service;

internal static class ObservationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        AgvRobotSnapshot Sample(double? confidence = .9) => new("A", "A", "synthetic", 0,
            ConnectionState.Online, SignalQuality.Good, 0, 0, 0, confidence, "PARK", null, 90,
            false, false, "Idle", null, null, null, [], [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, null, 1)
            { VelocityX = 0, VelocityY = 0, AngularVelocity = 0 };
        var current = Sample();
        Action? onVendorQuery = null, onDatabaseQuery = null;
        var cache = Stub<IAgvStateCache>((name, args) =>
        {
            if (name != "TryGet") throw new Exception("unexpected cache operation " + name);
            args[1] = current; return true;
        });
        var signals = Stub<IPlcSignalCache>((name, _) => name == "GetAll" ? new[] {
            "Faulted", "AgvAtWaitingPosition", "AgvAtDumpingPosition", "DumperReady", "DumpingCompletedMayLeave"
        }.Select(id => new PlcSignalSnapshot("DUMPER-001", id, id, "synthetic", "Bool", "P", "R", true,
            false, SignalQuality.Good, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1)).ToArray() : throw new Exception(name));
        var tasks = Stub<ITaskCenterQueryService>((name, _) =>
        {
            if (name != "QueryAsync") throw new Exception("unexpected task operation " + name);
            onDatabaseQuery?.Invoke(); return Task.FromResult(new TaskListPage([], 0, 1, 100));
        });
        var resources = Stub<ITrafficResourceManager>((name, _) => name == "RenewTaskAsync"
            ? Task.CompletedTask : throw new Exception("unexpected resource mutation " + name));
        var traffic = Stub<ITrafficResourceControlService>((name, _) => name == "GetResourcesAsync"
            ? Task.FromResult<IReadOnlyList<TrafficResourceSnapshot>>([]) : throw new Exception(name));
        var gateway = Stub<IAgvGateway>((name, args) =>
        {
            if (name != "GetTransportTaskStatusAsync") throw new Exception("no commands allowed: " + name);
            onVendorQuery?.Invoke();
            return Task.FromResult<AgvTransportTaskStatus?>(new((string)args[0]!, (string)args[0]!,
                AgvTransportTaskState.Completed, "A", DateTimeOffset.UtcNow));
        });
        var map = new ActiveMapRuntime();
        map.Replace(new("M", "V", new("synthetic", "", "RobotWorld", "m",
            [new("PARK", 0, 0, MapNodeType.Station, true, [], "PARK")], [], []), DateTimeOffset.UtcNow));
        var gate = new MapExecutionGate(); gate.SetMode(MapExecutionMode.Running);
        var dispatch = new DispatchOrchestratorOptions { Enabled = true, DumperWaitingSite = "WAIT", DumperDumpingSite = "DUMP" };
        var creation = new DispatchTaskCreationOptions { AutomaticCreationEnabled = false };
        var port = new BusinessRunPort(null!, tasks, signals, cache, map, gate, new(dispatch, creation), creation,
            dispatch, new(), new(), gateway, null!, resources, null!, null!, traffic, null!, true, null!, new());
        var run = new BusinessRunRecord("run", new("request", "Cycle", "A", ["CNC-001"], 1, "PARK", true),
            "M", "V", "initial", "test", "Returning", "", 1, null, Guid.NewGuid().ToString("N"),
            "original", false, DateTimeOffset.UtcNow);
        var observed = await port.ObserveAsync(run, default);
        check(observed.Block is null && observed.Stopped && observed.StandbyCompleted, "fresh original task and latest station prove a completed return");
        onVendorQuery = () => current = Sample(.487);
        observed = await port.ObserveAsync(run, default);
        check(observed.Block is null && !observed.Stopped && !observed.StandbyCompleted,
            "a localization dip during the original query waits without releasing the original return");
        current = Sample(); onVendorQuery = () => current = Sample() with { Connection = ConnectionState.Interrupted };
        observed = await port.ObserveAsync(run, default);
        check(observed.Block is not null && !observed.StandbyCompleted, "a disconnect during status lookup cannot use the earlier good arrival sample");
        current = Sample(); onVendorQuery = null;
        onDatabaseQuery = () => current = Sample() with { EmergencyStopped = true };
        observed = await port.ObserveAsync(run, default);
        check(observed.Block is not null && !observed.Stopped && !observed.StandbyCompleted,
            "a safety stop during database lookup is rechecked before returning arrival evidence");
        onDatabaseQuery = null;
        foreach (var stage in new[] { "Executing", "Waiting", "Returning" })
        {
            current = Sample(.487);
            observed = await port.ObserveAsync(run with { Stage = stage }, default);
            check(observed.Block is null && !observed.Stopped && !observed.StandbyCompleted,
                "transient low localization waits without a new-send or arrival permit in " + stage);
        }
        foreach (var stage in new[] { "Preparing", "Hold" })
        {
            current = Sample(.487);
            check((await port.ObserveAsync(run with { Stage = stage }, default)).Block is not null,
                "startup and reviewed recovery remain strict in " + stage);
        }
        current = Sample() with { X = double.NaN };
        observed = await port.ObserveAsync(run, default);
        check(observed.Block is not null && !observed.Stopped && !observed.StandbyCompleted, "nonfinite coordinates never prove standstill at a station");
    }

    private static T Stub<T>(Func<string, object?[], object?> invoke) where T : class
    {
        var result = DispatchProxy.Create<T, CallbackProxy>();
        ((CallbackProxy)(object)result).Call = invoke;
        return result;
    }
    public class CallbackProxy : DispatchProxy
    {
        public Func<string, object?[], object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!.Name, args!);
    }
}
