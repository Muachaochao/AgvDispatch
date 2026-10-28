using AgvDispatch.Service;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Traffic;
using NLog.Extensions.Logging;
using SqlSugar;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "AGV铝屑回收调度服务";
});

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddNLog("NLog.config");

var configuredPath = Environment.GetEnvironmentVariable("AGV_DISPATCH_DATABASE_PATH") ??
    builder.Configuration["Database:Path"] ?? "data/agv-dispatch.db";
var databaseOptions = new DatabaseOptions
{
    DatabasePath = Path.IsPathRooted(configuredPath)
        ? configuredPath
        : Path.Combine(AppContext.BaseDirectory, configuredPath)
};

builder.Services.AddSingleton(databaseOptions);
builder.Services.AddSingleton<ISqlSugarClient>(_ => DatabaseFactory.Create(databaseOptions));
builder.Services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
builder.Services.AddSingleton<IActiveTaskLookup, DispatchTaskReadRepository>();
builder.Services.AddSingleton<ITaskCenterReadRepository, TaskCenterReadRepository>();
builder.Services.AddSingleton<ITaskHistoryExportService, TaskHistoryExportService>();
builder.Services.AddSingleton<ITaskCenterQueryService, TaskCenterQueryService>();
builder.Services.AddSingleton<DispatchTaskRepository>();
builder.Services.AddSingleton<IDispatchTaskRepository>(p => p.GetRequiredService<DispatchTaskRepository>());
builder.Services.AddSingleton<IManualTaskCreationRepository>(p => p.GetRequiredService<DispatchTaskRepository>());
builder.Services.AddSingleton<IManualTaskCreationService, ManualTaskCreationService>();
builder.Services.AddSingleton<IDispatchOrchestrationRepository, DispatchOrchestrationRepository>();
builder.Services.AddSingleton<IAgvCommandOutboxRepository, AgvCommandOutboxRepository>();
builder.Services.AddSingleton<ExecutionIdentityCache>();
builder.Services.AddSingleton<IExecutionIdentityCache>(p => p.GetRequiredService<ExecutionIdentityCache>());
var recoveryIdentityOptions = new RecoveryIdentityOptions();
builder.Configuration.GetSection("RecoveryIdentity").Bind(recoveryIdentityOptions);
builder.Services.AddSingleton(recoveryIdentityOptions);
var identityAcquisitionOptions = new ExecutionIdentityAcquisitionOptions();
builder.Configuration.GetSection("ExecutionIdentityAcquisition").Bind(identityAcquisitionOptions);
builder.Services.AddSingleton(identityAcquisitionOptions);
// Empty reader registrations are intentional until real field schemas and consistency protocols are confirmed.
builder.Services.AddSingleton<ExecutionIdentityAcquisition>();
builder.Services.AddHostedService<ExecutionIdentityPollingService>();
builder.Services.AddSingleton<IExecutionEvidenceJournal, ExecutionEvidenceJournal>();
builder.Services.AddSingleton<ITaskHandshakeStore, TaskHandshakeStore>();
builder.Services.AddSingleton<ITaskDiagnosticArchiveService, TaskDiagnosticArchiveService>();
var handshakeOptions = new HandshakeExecutionOptions();
builder.Configuration.GetSection("HandshakeExecution").Bind(handshakeOptions);
builder.Services.AddSingleton(handshakeOptions);
builder.Services.AddSingleton<PersistentHandshakeExecutor>();
builder.Services.AddSingleton<ICommissioningQueryService, CommissioningQueryService>();
builder.Services.AddHostedService<TaskSignalEvidenceCollector>();
builder.Services.AddSingleton<RecoveryBarrier>();
builder.Services.AddSingleton<IRecoveryBarrier>(p => p.GetRequiredService<RecoveryBarrier>());
builder.Services.AddSingleton<IExecutionRecoveryService, ExecutionRecoveryService>();
builder.Services.AddSingleton<IAgvTrackStore, AgvTrackStore>();

var plcOptions = new PlcOptions();
builder.Configuration.GetSection(PlcOptions.SectionName).Bind(plcOptions);
if (builder.Configuration.GetValue("PlcCycleTest:Enabled", false)) plcOptions.AllowWrites = false;
var agvOptions = new AgvOptions();
builder.Configuration.GetSection(AgvOptions.SectionName).Bind(agvOptions);
var communicationSettingsPath = Path.Combine(Path.GetDirectoryName(databaseOptions.DatabasePath)!, "communication-settings.json");
var communicationDocument = CommunicationSettingsService.ReadDocument(communicationSettingsPath);
var communicationConfig = communicationDocument?.Configuration ?? CommunicationSettingsService.FromLegacy(plcOptions, agvOptions);
communicationConfig.MapBindings ??= CommunicationSettingsService.FromLegacy(plcOptions,agvOptions).MapBindings;
CommunicationSettingsService.Validate(communicationConfig, AppContext.BaseDirectory);
var plcConnections = communicationConfig.Plcs.Select(p => CommunicationSettingsService.ToPlcOptions(p, plcOptions, AppContext.BaseDirectory)).ToArray();
var plcFleet = new PlcFleet(plcConnections);
builder.Services.AddSingleton(plcOptions);
builder.Services.AddSingleton<PlcFleet>(_ => plcFleet);
builder.Services.AddSingleton<IPlcConfigurationInspector>(provider => provider.GetRequiredService<PlcFleet>());
builder.Services.AddSingleton(plcFleet.Catalog);
builder.Services.AddSingleton<PlcSignalCache>();
builder.Services.AddSingleton<IPlcSignalCache>(provider => provider.GetRequiredService<PlcSignalCache>());
builder.Services.AddSingleton<IPlcConnectionSupervisor>(
    provider => provider.GetRequiredService<PlcFleet>());
builder.Services.AddSingleton<IPlcGateway>(provider => new JournaledPlcGateway(provider.GetRequiredService<PlcFleet>(),
    provider.GetRequiredService<IExecutionEvidenceJournal>(), provider.GetRequiredService<IPlcSignalCache>(), provider.GetRequiredService<IRecoveryBarrier>()));
CommunicationSettingsService.ApplyAgv(communicationConfig, agvOptions);
builder.Services.AddSingleton<ICommunicationSettingsService>(provider => new CommunicationSettingsService(
    communicationSettingsPath, AppContext.BaseDirectory, communicationConfig, plcFleet,
    provider.GetRequiredService<IActiveTaskLookup>(),
    provider.GetRequiredService<ILogger<CommunicationSettingsService>>(), provider.GetRequiredService<IMapExecutionGate>(),
    provider.GetRequiredService<IActiveMapRuntime>(), provider.GetRequiredService<AgvConnectionManager>(),
    provider.GetRequiredService<AgvStateCache>(), agvOptions));
builder.Services.AddSingleton(agvOptions);
builder.Services.AddSingleton<AgvStateCache>();
builder.Services.AddSingleton<IAgvStateCache>(
    provider => provider.GetRequiredService<AgvStateCache>());
builder.Services.AddSingleton<AgvConnectionManager>();
builder.Services.AddSingleton<IRobotMapDownloadService>(p=>new RobotMapDownloadService(agvOptions,p.GetRequiredService<AgvConnectionManager>()));
builder.Services.AddSingleton<ISingleNavigationTestService>(p => new SingleNavigationTestService(
    new SingleNavigationTestOptions {
        Enabled=builder.Configuration.GetValue("NavigationTest:Enabled",false),
        VehicleId=builder.Configuration["NavigationTest:VehicleId"]??"",
        ForkEnabled=builder.Configuration.GetValue("NavigationTest:ForkEnabled",false),
        ForkModelMd5=builder.Configuration["NavigationTest:ForkModelMd5"]??"",
        ForkRobotId=builder.Configuration["NavigationTest:ForkRobotId"]??"",
        BusinessExecutionDisabled=new[]{"Dispatching:Enabled","AgvCommandOutbox:Enabled","DispatchTaskSteps:Enabled","TaskCreation:Enabled","HandshakeExecution:Enabled"}
            .All(key=>builder.Configuration.GetValue<bool?>(key)==false)
    },agvOptions,p.GetRequiredService<AgvConnectionManager>(),p.GetRequiredService<IActiveTaskLookup>(),
    p.GetRequiredService<IMapExecutionGate>(),Path.Combine(Path.GetDirectoryName(databaseOptions.DatabasePath)!,builder.Configuration.GetValue("NavigationTest:ForkEnabled",false)?"navigation-test.fork.json":"navigation-test.json")));
var plcCycleTestOptions = new PlcCycleTestOptions();
builder.Configuration.GetSection("PlcCycleTest").Bind(plcCycleTestOptions);
builder.Services.AddSingleton(plcCycleTestOptions);
builder.Services.AddSingleton(p => new PlcCycleSimulatorWriter(plcCycleTestOptions, plcFleet));
builder.Services.AddSingleton<IPlcCycleTestPort>(p => new PlcCycleTestPort(
    plcCycleTestOptions,
    p.GetRequiredService<IPlcSignalCache>(),
    p.GetRequiredService<IAgvStateCache>(),
    p.GetRequiredService<IActiveMapRuntime>(),
    p.GetRequiredService<IMapExecutionGate>(),
    p.GetRequiredService<PlcCycleSimulatorWriter>(),
    p.GetRequiredService<ISingleNavigationTestService>(),
    p.GetRequiredService<IRobokitTaskMapPreflight>(),
    PlcCycleTestPort.AllPlcsAreLoopback(plcConnections),
    new[] { "Dispatching", "TaskCreation", "AgvCommandOutbox", "DispatchTaskSteps", "HandshakeExecution" }
        .All(section => builder.Configuration.GetSection(section).GetValue<bool>("Enabled") == false)));
builder.Services.AddSingleton(p => new PlcCycleTestService(
    plcCycleTestOptions,
    p.GetRequiredService<IPlcCycleTestPort>(),
    p.GetRequiredService<ISingleNavigationTestService>(),
    Path.Combine(Path.GetDirectoryName(databaseOptions.DatabasePath)!, "plc-cycle-test.json")));
builder.Services.AddSingleton<IPlcCycleTestService>(p => p.GetRequiredService<PlcCycleTestService>());
builder.Services.AddHostedService(p => p.GetRequiredService<PlcCycleTestService>());
builder.Services.AddSingleton<IRobokitInspectionService, RobokitInspectionService>();
builder.Services.AddSingleton<IRobokitInspectionHistory, RobokitInspectionHistory>();
builder.Services.AddSingleton<IRobokitTaskMapPreflight, RobokitTaskMapPreflight>();
builder.Services.AddSingleton<AgvTransportChainLookup>();
builder.Services.AddSingleton(new BusinessStandbyChains(Path.Combine(Path.GetDirectoryName(databaseOptions.DatabasePath)!, "standby-chains")));
builder.Services.AddSingleton<IAgvTransportChainLookup, BusinessTransportChainLookup>();
builder.Services.AddSingleton<SeerAgvGateway>();
builder.Services.AddSingleton<IAgvGateway>(
    provider => provider.GetRequiredService<SeerAgvGateway>());
builder.Services.AddHostedService<PlcFleetPollingService>();
builder.Services.AddHostedService<AgvPollingService>();
builder.Services.AddHostedService<AgvTrackRecorder>();
var stationStateOptions = new StationStateOptions();
builder.Configuration.GetSection(StationStateOptions.SectionName).Bind(stationStateOptions);
builder.Services.AddSingleton(stationStateOptions);
builder.Services.AddSingleton<StationStateService>();
builder.Services.AddSingleton<IStationStateService>(
    provider => provider.GetRequiredService<StationStateService>());
builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton<IHostedService>(
    provider => provider.GetRequiredService<StationStateService>());
var taskCreationOptions = new DispatchTaskCreationOptions();
builder.Configuration.GetSection(DispatchTaskCreationOptions.SectionName).Bind(taskCreationOptions);
builder.Services.AddSingleton(taskCreationOptions);
builder.Services.AddHostedService<TaskCreationCoordinator>();
var dispatchOrchestratorOptions = new DispatchOrchestratorOptions();
builder.Configuration.GetSection(DispatchOrchestratorOptions.SectionName)
    .Bind(dispatchOrchestratorOptions);
builder.Services.AddSingleton(dispatchOrchestratorOptions);
var taskParameterPath = Path.Combine(Path.GetDirectoryName(databaseOptions.DatabasePath)!, "task-parameters.json");
var taskParameterRuntime = new TaskParameterRuntime(dispatchOrchestratorOptions, taskCreationOptions);
if (TaskParameterSettingsService.ReadDocument(taskParameterPath) is { } taskParameterDocument) taskParameterRuntime.Apply(taskParameterDocument);
builder.Services.AddSingleton(taskParameterRuntime);
builder.Services.AddSingleton<BusinessRunOwnership>();
var fleetTestOptions=new FleetTestOptions();
builder.Configuration.GetSection("FleetTest").Bind(fleetTestOptions);
fleetTestOptions.VehicleIds=fleetTestOptions.VehicleIds.Where(id=>!string.IsNullOrWhiteSpace(id)).ToArray();
builder.Services.AddSingleton(fleetTestOptions);
builder.Services.AddSingleton<FleetTestOwnership>();
builder.Services.AddSingleton<IBusinessRunInterlock,CommissioningInterlock>();
builder.Services.AddSingleton<IBusinessRunPort>(p => new BusinessRunPort(
    p.GetRequiredService<IManualTaskCreationService>(), p.GetRequiredService<ITaskCenterQueryService>(),
    p.GetRequiredService<IPlcSignalCache>(), p.GetRequiredService<IAgvStateCache>(), p.GetRequiredService<IActiveMapRuntime>(),
    p.GetRequiredService<IMapExecutionGate>(), taskParameterRuntime, taskCreationOptions, dispatchOrchestratorOptions,
    p.GetRequiredService<DispatchTaskStepOptions>(), plcCycleTestOptions, p.GetRequiredService<IAgvGateway>(),
    p.GetRequiredService<IRoutePlanner>(), p.GetRequiredService<ITrafficResourceManager>(), p.GetRequiredService<IRobokitTaskMapPreflight>(),
    p.GetRequiredService<BusinessStandbyChains>(), p.GetRequiredService<ITrafficResourceControlService>(), p.GetRequiredService<ISingleNavigationTestService>(), PlcCycleTestPort.AllPlcsAreLoopback(plcConnections), p.GetRequiredService<IManualTaskCreationRepository>(), p.GetRequiredService<BusinessRunOwnership>(),fleetTestOptions,p.GetRequiredService<FleetTestOwnership>()));
builder.Services.AddSingleton(p => new BusinessRunService(p.GetRequiredService<IBusinessRunPort>(), p.GetRequiredService<BusinessRunOwnership>(),
    Path.Combine(Path.GetDirectoryName(databaseOptions.DatabasePath)!, "business-run.json")));
builder.Services.AddSingleton<IBusinessRunService>(p => p.GetRequiredService<BusinessRunService>());
builder.Services.AddHostedService(p => p.GetRequiredService<BusinessRunService>());
builder.Services.AddSingleton<IFleetTestPort>(p=>new FleetTestPort(fleetTestOptions,p.GetRequiredService<IBusinessRunService>(),p.GetRequiredService<IBusinessRunPort>(),
    p.GetRequiredService<IManualTaskCreationService>(),p.GetRequiredService<ITaskCenterQueryService>(),p.GetRequiredService<IDispatchTaskRepository>(),
    p.GetRequiredService<IStationStateService>(),p.GetRequiredService<IPlcSignalCache>(),p.GetRequiredService<IPlcGateway>(),p.GetRequiredService<IAgvStateCache>(),
    p.GetRequiredService<IActiveMapRuntime>(),p.GetRequiredService<IMapExecutionGate>(),taskParameterRuntime,dispatchOrchestratorOptions,taskCreationOptions,
    p.GetRequiredService<HandshakeExecutionOptions>(),p.GetRequiredService<IRobokitTaskMapPreflight>(),p.GetRequiredService<ITrafficResourceControlService>(),PlcCycleTestPort.AllPlcsAreLoopback(plcConnections)));
builder.Services.AddSingleton(p=>new FleetTestService(p.GetRequiredService<IFleetTestPort>(),p.GetRequiredService<FleetTestOwnership>(),
    Path.Combine(Path.GetDirectoryName(databaseOptions.DatabasePath)!,"fleet-test.json"),p.GetRequiredService<ILogger<FleetTestService>>()));
builder.Services.AddSingleton<IFleetTestService>(p=>p.GetRequiredService<FleetTestService>());
builder.Services.AddHostedService(p=>p.GetRequiredService<FleetTestService>());
builder.Services.AddSingleton(new TaskParameterStorageOptions(taskParameterPath));
builder.Services.AddSingleton<ITaskParameterService, TaskParameterSettingsService>();
var mapOptions = new MapOptions();
builder.Configuration.GetSection(MapOptions.SectionName).Bind(mapOptions);
if (!string.IsNullOrWhiteSpace(mapOptions.ImportPath) && !Path.IsPathRooted(mapOptions.ImportPath))
{
    mapOptions.ImportPath = Path.Combine(AppContext.BaseDirectory, mapOptions.ImportPath);
}
if (!string.IsNullOrWhiteSpace(mapOptions.ValidationReportPath) &&
    !Path.IsPathRooted(mapOptions.ValidationReportPath))
{
    mapOptions.ValidationReportPath = Path.Combine(
        AppContext.BaseDirectory,
        mapOptions.ValidationReportPath);
}
if (!string.IsNullOrWhiteSpace(mapOptions.UnderlayCachePath) &&
    !Path.IsPathRooted(mapOptions.UnderlayCachePath))
{
    mapOptions.UnderlayCachePath = Path.Combine(AppContext.BaseDirectory, mapOptions.UnderlayCachePath);
}
if (!string.IsNullOrWhiteSpace(mapOptions.BackupPath) && !Path.IsPathRooted(mapOptions.BackupPath))
{
    mapOptions.BackupPath = Path.Combine(AppContext.BaseDirectory, mapOptions.BackupPath);
}
builder.Services.AddSingleton(mapOptions);
builder.Services.AddSingleton(new TrafficMapDisplayOptions { PositionMaxAgeSeconds = mapOptions.PositionStaleSeconds,
    MinimumLocalizationConfidence = mapOptions.MinimumLocalizationConfidence, MaximumRouteDeviationMeters = mapOptions.MaximumRouteDeviationMeters });
builder.Services.AddSingleton<IMapImportService, MapImportService>();
builder.Services.AddSingleton<IMapValidationService, MapValidationService>();
builder.Services.AddSingleton<ICadUnderlayService, CadUnderlayService>();
builder.Services.AddSingleton<ICadDetailService, CadDetailService>();
builder.Services.AddSingleton<IMapCalibrationService, MapCalibrationService>();
builder.Services.AddSingleton<ICadUnderlayJobQueue, CadUnderlayJobQueue>();
builder.Services.AddSingleton(new MapAssetStorageOptions
{
    CachePath = mapOptions.UnderlayCachePath,
    BackupPath = mapOptions.BackupPath
});
builder.Services.AddSingleton<MapDefinitionProvider>();
builder.Services.AddSingleton(provider => new TrafficResourceCatalog(
    provider.GetRequiredService<MapDefinitionProvider>().Definition?.Resources ?? []));
builder.Services.AddSingleton<ActiveMapRuntime>();
builder.Services.AddSingleton<IActiveMapRuntime>(provider => provider.GetRequiredService<ActiveMapRuntime>());
builder.Services.AddSingleton(new TrafficControlSettings(
    TimeSpan.FromSeconds(mapOptions.LeaseDurationSeconds)));
builder.Services.AddSingleton<TrafficFlowMonitor>();
builder.Services.AddSingleton<ITrafficFlowMonitor>(
    provider => provider.GetRequiredService<TrafficFlowMonitor>());
builder.Services.AddSingleton<IHostedService>(
    provider => provider.GetRequiredService<TrafficFlowMonitor>());
builder.Services.AddSingleton<IRoutePlanner, ConfiguredRoutePlanner>();
builder.Services.AddSingleton<TrafficResourceManager>();
builder.Services.AddSingleton<ITrafficResourceManager>(p=>p.GetRequiredService<TrafficResourceManager>());
builder.Services.AddSingleton<ITaskWaitingBayAllocator>(p=>p.GetRequiredService<TrafficResourceManager>());
var waitingAlerts = new TrafficWaitingAlertOptions();
builder.Configuration.GetSection("TrafficWaitingAlerts").Bind(waitingAlerts); waitingAlerts.Validate();
builder.Services.AddSingleton(waitingAlerts);
// Read-only preparation; no commissioned progress source and no release operation.
builder.Services.AddSingleton(new TrafficSegmentReleasePolicy());
builder.Services.AddSingleton<TrafficSegmentReleaseInspector>();
builder.Services.AddSingleton<ITrafficWaitingBayDepartureService>(p=>new TrafficWaitingBayDepartureInspector(
    p.GetRequiredService<ISqlSugarClient>(),p.GetRequiredService<TrafficSegmentReleaseInspector>(),p.GetRequiredService<IMapExecutionGate>(),
    ()=>new(dispatchOrchestratorOptions.GetDumperWaitingSite()??"",dispatchOrchestratorOptions.DumperDumpingSite??""),
    p.GetService<IAuthoritativeTrafficProgressReader>(),p.GetService<ITrafficGeometryProfileProvider>()));
// Only a read-only diagnostic policy; release/commit remain independently disabled.
builder.Services.AddSingleton(new TrafficGeometryPreviewPolicy { Enabled=true });
builder.Services.AddSingleton<ITrafficGeometryPreviewService,TrafficGeometryPreviewService>();
builder.Services.AddSingleton<ITrafficSegmentReleaseInspector>(p=>p.GetRequiredService<TrafficSegmentReleaseInspector>());
builder.Services.AddSingleton(new TrafficSegmentCommitOptions());
builder.Services.AddSingleton<TrafficSegmentReceiptService>();
builder.Services.AddSingleton<ITrafficSegmentReceiptService>(p=>p.GetRequiredService<TrafficSegmentReceiptService>());
builder.Services.AddSingleton<ITrafficSegmentCommitService>(p=>p.GetRequiredService<TrafficSegmentReceiptService>());
builder.Services.AddSingleton<ITrafficResourceControlService, TrafficResourceControlService>();
var trafficSecurityOptions = new TrafficSecurityOptions();
builder.Configuration.GetSection(TrafficSecurityOptions.SectionName).Bind(trafficSecurityOptions);
if (string.IsNullOrWhiteSpace(trafficSecurityOptions.PipeName))
{
    throw new InvalidOperationException("Security:PipeName不能为空。");
}
if (trafficSecurityOptions.ApprovalTimeoutMinutes is < 1 or > 1440)
{
    throw new InvalidOperationException("Security:ApprovalTimeoutMinutes必须在1到1440之间。");
}
builder.Services.AddSingleton(trafficSecurityOptions);
builder.Services.AddSingleton<NamedPipeDiagnostics>();
builder.Services.AddSingleton<ISystemStatusSnapshotService, SystemStatusSnapshotService>();
builder.Services.AddSingleton<IDeviceMonitoringService, DeviceMonitoringService>();
builder.Services.AddSingleton(new ManualReleaseWorkflowSettings(
    TimeSpan.FromMinutes(trafficSecurityOptions.ApprovalTimeoutMinutes)));
builder.Services.AddSingleton<IManualReleaseWorkflowService, ManualReleaseWorkflowService>();
builder.Services.AddSingleton<IAlarmService, AlarmService>();
builder.Services.AddSingleton<IMapExecutionGate, MapExecutionGate>();
builder.Services.AddSingleton(new MapSwitchSafetySettings
{
    RequiredVehicleIds = agvOptions.Robots.Where(robot => robot.Enabled && !string.IsNullOrWhiteSpace(robot.IpAddress)).Select(robot => robot.VehicleId).ToArray(),
    CheckPlcSignals = plcConnections.Any(plc => plc.Enabled),
    StateMaxAge = TimeSpan.FromSeconds(mapOptions.PositionStaleSeconds),
    MinimumLocalizationConfidence = mapOptions.MinimumLocalizationConfidence,
    MaximumRouteDeviationMeters = mapOptions.MaximumRouteDeviationMeters
});
builder.Services.AddSingleton<IMapSwitchService, MapSwitchService>();
builder.Services.AddSingleton<IMapTransferRecoveryBarrier,MapTransferRecoveryBarrier>();
builder.Services.AddSingleton<IMapTransferLedger,MapTransferLedger>();
builder.Services.AddSingleton<IMapManagementService, MapManagementService>();
builder.Services.AddHostedService<ActiveMapLoader>();
builder.Services.AddHostedService<DumperWaitingSiteAutoDiscoveryService>();
builder.Services.AddHostedService<ExecutionRecoveryWorker>();
builder.Services.AddHostedService<TrafficControlNamedPipeServer>();
builder.Services.AddHostedService<CadDetailNamedPipeServer>();
builder.Services.AddHostedService<CadUnderlayWorker>();
builder.Services.AddSingleton<ITaskCancellationService, TaskCancellationService>();
builder.Services.AddHostedService<TaskCancellationWorker>();
builder.Services.AddHostedService<DispatchTaskOrchestrator>();
var outboxOptions = new AgvCommandOutboxOptions();
builder.Configuration.GetSection(AgvCommandOutboxOptions.SectionName).Bind(outboxOptions);
builder.Services.AddSingleton(outboxOptions);
builder.Services.AddHostedService<AgvCommandOutboxDispatcher>();
var taskStepOptions = new DispatchTaskStepOptions();
builder.Configuration.GetSection(DispatchTaskStepOptions.SectionName).Bind(taskStepOptions);
builder.Services.AddSingleton(taskStepOptions);
builder.Services.AddHostedService<DispatchTaskStepCoordinator>();
var alarmOptions = new AlarmOptions();
builder.Configuration.GetSection(AlarmOptions.SectionName).Bind(alarmOptions);
builder.Services.AddSingleton(alarmOptions);
builder.Services.AddHostedService<AlarmRuleEngine>();

var host = builder.Build();
// Build the schema before any polling/recording background service can query it.
await host.Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync(CancellationToken.None);
host.Run();
