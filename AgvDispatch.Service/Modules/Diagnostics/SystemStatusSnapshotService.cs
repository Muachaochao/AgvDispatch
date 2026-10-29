using System.Diagnostics;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Application.Maps;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed class SystemStatusSnapshotService(
    IDatabaseInitializer database,
    IActiveTaskLookup tasks,
    IPlcConnectionSupervisor plcConnection,
    IPlcSignalCache plcSignals,
    IAgvStateCache agvStates,
    PlcFleet plcFleet,
    AgvOptions agvOptions,
    MapOptions mapOptions,
    IActiveMapRuntime activeMap,
    DispatchOrchestratorOptions dispatchOptions,
    TrafficSecurityOptions securityOptions,
    NamedPipeDiagnostics pipeDiagnostics, HandshakeExecutionOptions? handshake = null,
    DispatchTaskCreationOptions? creation = null, DispatchTaskStepOptions? steps = null,
    AgvCommandOutboxOptions? outbox = null) : ISystemStatusSnapshotService
{
    private static readonly DateTimeOffset ProcessStartedAt =
        new(Process.GetCurrentProcess().StartTime);

    public async Task<SystemStatusSnapshot> GetAsync(
        TrafficUserContext user,
        CancellationToken cancellationToken)
    {
        if (!user.Has(TrafficPermission.View))
        {
            throw new UnauthorizedAccessException("当前软件账号没有系统诊断查看权限。");
        }

        var databaseStopwatch = Stopwatch.StartNew();
        var databaseOnline = await database.CanConnectAsync(cancellationToken);
        databaseStopwatch.Stop();
        var activeTasks = await tasks.GetActiveTasksAsync(cancellationToken);
        var signals = plcSignals.GetAll();
        var connection = plcConnection.Current;
        var agvs = agvStates.GetAll();
        var now = DateTimeOffset.Now;

        return new SystemStatusSnapshot(
            now,
            new ServiceRuntimeDiagnostic(
                "AGV铝屑回收调度服务",
                typeof(SystemStatusSnapshotService).Assembly.GetName().Version?.ToString() ?? "unknown",
                Environment.ProcessId,
                ProcessStartedAt,
                now - ProcessStartedAt),
            new DatabaseDiagnostic(
                databaseOnline,
                databaseStopwatch.Elapsed.TotalMilliseconds,
                databaseOnline ? "SQLite连接正常。" : "SQLite连接失败。"),
            new PlcDiagnostic(
                connection.DeviceId,
                connection.DisplayName,
                connection.State,
                connection.ConsecutiveFailures,
                connection.UpdatedAt,
                connection.LastSuccessfulReadAt,
                connection.LastResponseTimeMs,
                connection.Message,
                plcSignals.Version,
                signals.Count,
                signals.Count(signal => signal.Enabled),
                signals.Count(signal => signal.Quality == SignalQuality.Good),
                signals.Count(signal => signal.Quality == SignalQuality.Stale),
                signals.Count(signal => signal.Quality == SignalQuality.Bad),
                signals.Count(signal => signal.Quality == SignalQuality.Unknown),
                signals.Count(signal => signal.Quality == SignalQuality.Disabled)),
            agvs.Select(agv =>
            {
                var position = AgvMapPositionAnalyzer.Analyze(
                    agv.X,
                    agv.Y,
                    agv.LocalizationConfidence,
                    agv.LastSuccessfulPollAt,
                    activeMap.Current?.Definition,
                    now,
                    TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds),
                    mapOptions.MinimumLocalizationConfidence,
                    mapOptions.MaximumRouteDeviationMeters);
                return new AgvDiagnostic(
                agv.VehicleId,
                agv.DisplayName,
                string.IsNullOrWhiteSpace(agv.IpAddress) ? $"未配置:{agv.Port}" : $"{agv.IpAddress}:{agv.Port}",
                agv.Connection,
                agv.Quality,
                agv.BatteryPercent,
                agv.CurrentStation,
                agv.NavigationState,
                agv.Tasks.Count,
                agv.Alarms.Count,
                agv.Blocked,
                agv.EmergencyStopped,
                agv.X,
                agv.Y,
                agv.Angle,
                agv.LocalizationConfidence,
                position.State,
                position.DistanceToRouteMeters,
                position.NearestEdgeId,
                position.Message,
                agv.LastSuccessfulPollAt,
                agv.UpdatedAt,
                agv.ConsecutiveFailures,
                agv.LastResponseTimeMs,
                agv.ErrorMessage)
                {
                    VelocityX = agv.VelocityX, VelocityY = agv.VelocityY,
                    AngularVelocity = agv.AngularVelocity, Charging = agv.Charging,
                    BatteryVoltage = agv.BatteryVoltage, BatteryCurrent = agv.BatteryCurrent,
                    BatteryTemperature = agv.BatteryTemperature,
                    Obstacle = AgvObstacleDiagnostics.Analyze(agv, now, TimeSpan.FromMilliseconds(agvOptions.StaleAfterMs))
                };
            }).ToArray(),
            BuildTaskDiagnostic(activeTasks),
            pipeDiagnostics.GetSnapshot(securityOptions.PipeName, user),
            new SafetySwitchDiagnostic(
                plcFleet.Enabled,
                plcFleet.Connections.Any(c => c.Options.AllowWrites),
                agvOptions.Enabled,
                agvOptions.AllowCommands,
                mapOptions.Enabled || activeMap.Current is not null,
                dispatchOptions.Enabled,
                activeMap.Current?.MapId,
                activeMap.Current?.VersionId))
        { Execution = BackendBuildIdentity.Read(handshake, creation, steps, outbox) };
    }

    private static TaskDiagnostic BuildTaskDiagnostic(IReadOnlyList<ActiveDispatchTask> tasks)
    {
        var waiting = tasks.Count(task => task.Stage is
            DispatchTaskStage.Created or
            DispatchTaskStage.WaitingForVehicle or
            DispatchTaskStage.SubmittingToAgv);
        var faulted = tasks.Count(task => task.Stage == DispatchTaskStage.Faulted);
        return new TaskDiagnostic(
            tasks.Count,
            waiting,
            tasks.Count - waiting - faulted,
            faulted,
            tasks.Select(task => new DispatchTaskDiagnostic(
                task.TaskId,
                task.StationId,
                task.VehicleId,
                task.VendorTaskId,
                task.Stage,
                task.CreatedAt,
                task.UpdatedAt)).ToArray());
    }
}
