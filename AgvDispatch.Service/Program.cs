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
var chargingOptions = new ChargingOptions();
builder.Configuration.GetSection("Charging").Bind(chargingOptions);
var chargerMonitoringOptions = new ChargerMonitoringOptions();
builder.Configuration.GetSection("ChargerMonitoring").Bind(chargerMonitoringOptions);
communicationConfig.Chargers ??= [];
communicationConfig.ChargingSites ??= [];
if (communicationConfig.Chargers.Count == 0 && !communicationConfig.ChargerConfigurationInitialized)
{
    communicationConfig.Chargers = chargerMonitoringOptions.Addresses.Distinct(StringComparer.OrdinalIgnoreCase)
        .Select((address,index) => new ChargerConnectionSetting
        {
            Id = "CHARGER-" + address.Replace('.','-'), DisplayName = "充电桩" + (index + 1),
            Enabled = true, IpAddress = address, AckPort = chargerMonitoringOptions.AckPort,
            StatusPort = chargerMonitoringOptions.StatusPort
        }).ToList();
    communicationConfig.ChargerConfigurationInitialized = true;
}
chargerMonitoringOptions.Enabled = communicationConfig.ChargerMonitoringEnabled;
chargerMonitoringOptions.Addresses = [];
chargerMonitoringOptions.Chargers = communicationConfig.Chargers.Where(value => value.Enabled)
    .Select(value => new ChargerEndpoint(value.Id,value.DisplayName,value.IpAddress,value.AckPort,value.StatusPort)).ToArray();
if (communicationConfig.ChargingSites.Count > 0)
{
    chargingOptions.Sites = communicationConfig.ChargingSites.Select(value => new ChargingSite(
        value.Id,value.ChargingSiteId,value.ExitSiteId,value.ResourceId)
        {
            ChargerId=value.ChargerId,
            ChargerAddress=communicationConfig.Chargers.Single(charger=>charger.Id.Equals(value.ChargerId,StringComparison.OrdinalIgnoreCase)).IpAddress,
            Commissioned=value.Commissioned,
            AllowedVehicleIds=value.AllowedVehicleIds.ToArray(), ControlMode=value.ControlMode
        }).ToArray();
    communicationConfig.ChargingSiteConfigurationInitialized = true;
}
else if (!communicationConfig.ChargingSiteConfigurationInitialized && chargingOptions.Sites.Length > 0)
{
    var fallbackCharger = communicationConfig.Chargers.Count == 1 ? communicationConfig.Chargers[0].Id : "";
    chargingOptions.Sites = chargingOptions.Sites.Select(site =>
    {
        var chargerId=string.IsNullOrWhiteSpace(site.ChargerId)?fallbackCharger:site.ChargerId;
        return site with
        {
            ChargerId=chargerId,
            ChargerAddress=communicationConfig.Chargers.FirstOrDefault(value=>value.Id.Equals(chargerId,StringComparison.OrdinalIgnoreCase))?.IpAddress??""
        };
    }).ToArray();
    communicationConfig.ChargingSites = chargingOptions.Sites.Select(site => new ChargingSiteSetting
    {
        Id=site.Id, ChargerId=site.ChargerId, ChargingSiteId=site.ChargingSiteId,
        ExitSiteId=site.ExitSiteId, ResourceId=site.ResourceId, Commissioned=site.Commissioned,
        AllowedVehicleIds=site.AllowedVehicleIds.ToList(), ControlMode=site.ControlMode
    }).ToList();
    communicationConfig.ChargingSiteConfigurationInitialized = true;
}
else if (communicationConfig.ChargingSiteConfigurationInitialized)
    chargingOptions.Sites = [];
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
    provider.GetRequiredService<AgvStateCache>(), agvOptions,provider.GetRequiredService<IChargingDispatchGuard>()));
builder.Services.AddSingleton(agvOptions);
builder.Services.AddSingleton(chargingOptions);
var chargingScriptOptions = new ChargingScriptOptions();
builder.Configuration.GetSection("ChargingScript").Bind(chargingScriptOptions);
builder.Services.AddSingleton(chargingScriptOptions);
builder.Services.AddSingleton(chargerMonitoringOptions);
builder.Services.AddSingleton<ChargerMonitoringService>();
builder.Services.AddSingleton<IChargerTelemetryMonitor>(p => p.GetRequiredService<ChargerMonitoringService>());
builder.Services.AddHostedService(p => p.GetRequiredService<ChargerMonitoringService>());
var chargingRobotOptions = new RobokitChargingRobotControlOptions();
builder.Configuration.GetSection(RobokitChargingRobotControlOptions.SectionName).Bind(chargingRobotOptions);
builder.Services.AddSingleton(chargingRobotOptions);
builder.Services.AddSingleton<IChargingRobotControl, RobokitChargingRobotControl>();
var chargingPath = Path.Combine(Path.GetDirectoryName(databaseOptions.DatabasePath)!, "charging-sessions.json");
var chargingDataDirectory = Path.GetDirectoryName(databaseOptions.DatabasePath)!;
builder.Services.AddSingleton<IChargingWorkflow>(p => new ScriptChargingWorkflow(chargingScriptOptions,
    chargingOptions,p.GetRequiredService<AgvOptions>(),chargingDataDirectory));
builder.Services.AddSingleton<IChargingRecoveryEvidence>(new ChargingSessionStore(chargingPath));
builder.Services.AddSingleton<IChargingBusinessSafety, ChargingBusinessSafety>();
builder.Services.AddSingleton<IChargingRuntimePort>(p => new ChargingRuntimePort(chargingOptions,
    p.GetRequiredService<IAgvStateCache>(), p.GetRequiredService<IActiveTaskLookup>(), p.GetRequiredService<IActiveMapRuntime>(),
    p.GetRequiredService<IMapExecutionGate>(), p.GetRequiredService<IRoutePlanner>(), p.GetRequiredService<ITrafficResourceManager>(),
    p.GetRequiredService<ITrafficResourceControlService>(), p.GetRequiredService<IAgvGateway>(), p.GetRequiredService<IRobokitTaskMapPreflight>(),
    () => p.GetRequiredService<FleetTestOwnership>().Active
        ? "联调会话尚未闭合，等待原箱体循环安全结束。"
        : builder.Configuration.GetValue("NavigationTest:Enabled",false) || builder.Configuration.GetValue("PlcCycleTest:Enabled",false) || builder.Configuration.GetValue("FleetTest:Enabled",false)
            ? "独立导航或联调启动模式禁止自动充电，请核对原测试并切换正式运行模式。"
            : !chargingRobotOptions.RelayControlConfirmed ? "厂家充电继电器及充电桩对接尚未现场核定，自动充电保持排队。" : null,
    businessSafety:p.GetRequiredService<IChargingBusinessSafety>(),
    vehicleBlock:id => p.GetRequiredService<BusinessRunOwnership>() is {Active:true} owner &&
        string.Equals(owner.Vehicle,id,StringComparison.OrdinalIgnoreCase) ? "当前车辆业务运行尚未安全交接，等待整轮箱体循环结束。" : null,
    returnSite:id => p.GetRequiredService<BusinessRunOwnership>().GetChargingReturnSite(id)));
builder.Services.AddSingleton(p => new ChargingService(chargingOptions, p.GetRequiredService<IChargingRuntimePort>(),
    p.GetRequiredService<IChargingRobotControl>(), chargingPath, p.GetRequiredService<ILogger<ChargingService>>(),
    workflow:p.GetRequiredService<IChargingWorkflow>(),
    chargerMonitor:p.GetRequiredService<IChargerTelemetryMonitor>()));
builder.Services.AddSingleton<IChargingService>(p => p.GetRequiredService<ChargingService>());
builder.Services.AddSingleton<IChargingDispatchGuard>(p => p.GetRequiredService<ChargingService>());
builder.Services.AddHostedService(p => p.GetRequiredService<ChargingService>());
builder.Services.AddSingleton<AgvStateCache>();
builder.Services.AddSingleton<IAgvStateCache>(
    provider => provider.GetRequiredService<AgvStateCache>());
builder.Services.AddSingleton<AgvConnectionManager>();
builder.Services.AddSingleton<IRobotMapDownloadService>(p=>new RobotMapDownloadService(agvOptions,p.GetRequiredService<AgvConnectionManager>()));
var singleNavigationTestOptions = new SingleNavigationTestOptions
{
    Enabled=builder.Configuration.GetValue("NavigationTest:Enabled",false),
    VehicleId=builder.Configuration["NavigationTest:VehicleId"]??"",
    ForkEnabled=builder.Configuration.GetValue("NavigationTest:ForkEnabled",false),
    ForkModelMd5=builder.Configuration["NavigationTest:ForkModelMd5"]??"",
    ForkRobotId=builder.Configuration["NavigationTest:ForkRobotId"]??"",
    DetourWaitSeconds=builder.Configuration.GetValue("NavigationTest:DetourWaitSeconds",5),
    AutoDetourEnabled=builder.Configuration.GetValue("NavigationTest:AutoDetourEnabled",false),
    AutoDetourWaitSeconds=builder.Configuration.GetValue("NavigationTest:AutoDetourWaitSeconds",10),
    BusinessExecutionDisabled=new[]{"Dispatching:Enabled","AgvCommandOutbox:Enabled","DispatchTaskSteps:Enabled","TaskCreation:Enabled","HandshakeExecution:Enabled"}
        .All(key=>builder.Configuration.GetValue<bool?>(key)==false)
};
builder.Services.AddSingleton(singleNavigationTestOptions);
builder.Services.AddSingleton<ISingleNavigationTestService>(p => new SingleNavigationTestService(
    singleNavigationTestOptions,agvOptions,p.GetRequiredService<AgvConnectionManager>(),p.GetRequiredService<IActiveTaskLookup>(),
    p.GetRequiredService<IMapExecutionGate>(),Path.Combine(Path.GetDirectoryName(databaseOptions.DatabasePath)!,builder.Configuration.GetValue("NavigationTest:ForkEnabled",false)?"navigation-test.fork.json":"navigation-test.json"),p.GetRequiredService<IChargingDispatchGuard>(),p.GetRequiredService<INavigationTestDetourPlanner>(),p.GetRequiredService<IRoadClosureService>()));
builder.Services.AddHostedService<NavigationTestAutoDetourWorker>();
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
builder.Services.AddSingleton<IAgvTransportChainLookup>(p => new BusinessTransportChainLookup(
    p.GetRequiredService<AgvTransportChainLookup>(),p.GetRequiredService<BusinessStandbyChains>(),
    id => p.GetRequiredService<ChargingService>().FindChain(id)));
builder.Services.AddSingleton<SeerAgvGateway>();
builder.Services.AddSingleton<IAgvGateway>(
    provider => provider.GetRequiredService<SeerAgvGateway>());
builder.Services.AddSingleton<IAgvObstacleRecoveryGateway>(
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
var assignmentOptions = new DispatchAssignmentOptions();
builder.Configuration.GetSection("DispatchOptimization").Bind(assignmentOptions);
builder.Services.AddSingleton(p => new DispatchOptimizationService(
    Path.Combine(Path.GetDirectoryName(databaseOptions.DatabasePath)!, "dispatch-optimization.json"), assignmentOptions,
    p.GetRequiredService<ILogger<DispatchOptimizationService>>()));
builder.Services.AddSingleton<IDispatchPipelineMonitor>(p => p.GetRequiredService<DispatchOptimizationService>());
builder.Services.AddSingleton<IDispatchOptimizationService, DispatchPipelineSnapshotService>();
builder.Services.AddSingleton<IDispatchPipelineInspectionService, DispatchPipelineInspectionService>();
builder.Services.AddSingleton<IDispatchQueueWindowService, DispatchQueueWindowService>();
builder.Services.AddSingleton<IDispatchCandidateFilter, DispatchCandidateFilter>();
builder.Services.AddSingleton<DispatchTrafficReservationService>();
builder.Services.AddSingleton<DispatchPreSendSafetyService>();
builder.Services.AddSingleton<IDispatchPreSendSafetyService>(p => p.GetRequiredService<DispatchPreSendSafetyService>());
builder.Services.AddSingleton(p => new DispatchRouteCache(p.GetRequiredService<IRoutePlanner>(), p.GetRequiredService<IActiveMapRuntime>(),
    p.GetRequiredService<ITrafficFlowMonitor>(), p.GetRequiredService<IRoadClosureService>(), p.GetRequiredService<IAgvStateCache>(), dispatchOrchestratorOptions));
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
    p.GetRequiredService<IActiveTaskLookup>(),
    p.GetRequiredService<IPlcSignalCache>(), p.GetRequiredService<IAgvStateCache>(), p.GetRequiredService<IActiveMapRuntime>(),
    p.GetRequiredService<IMapExecutionGate>(), taskParameterRuntime, taskCreationOptions, dispatchOrchestratorOptions,
    p.GetRequiredService<DispatchTaskStepOptions>(), plcCycleTestOptions, p.GetRequiredService<IAgvGateway>(),
    p.GetRequiredService<IRoutePlanner>(), p.GetRequiredService<ITrafficResourceManager>(), p.GetRequiredService<IRobokitTaskMapPreflight>(),
    p.GetRequiredService<BusinessStandbyChains>(), p.GetRequiredService<ITrafficResourceControlService>(), p.GetRequiredService<ISingleNavigationTestService>(), PlcCycleTestPort.AllPlcsAreLoopback(plcConnections), p.GetRequiredService<IManualTaskCreationRepository>(), p.GetRequiredService<BusinessRunOwnership>(),fleetTestOptions,p.GetRequiredService<FleetTestOwnership>(),p.GetRequiredService<IChargingDispatchGuard>()));
builder.Services.AddSingleton(p => new BusinessRunService(p.GetRequiredService<IBusinessRunPort>(), p.GetRequiredService<BusinessRunOwnership>(),
    Path.Combine(Path.GetDirectoryName(databaseOptions.DatabasePath)!, "business-run.json"),p.GetRequiredService<IChargingDispatchGuard>(),
    p.GetRequiredService<ITaskSwitchGuard>()));
builder.Services.AddSingleton<IBusinessRunService>(p => p.GetRequiredService<BusinessRunService>());
builder.Services.AddHostedService(p => p.GetRequiredService<BusinessRunService>());
builder.Services.AddSingleton<IFleetTestPort>(p=>new FleetTestPort(fleetTestOptions,p.GetRequiredService<IBusinessRunService>(),p.GetRequiredService<IBusinessRunPort>(),
    p.GetRequiredService<IManualTaskCreationService>(),p.GetRequiredService<ITaskCenterQueryService>(),p.GetRequiredService<IDispatchTaskRepository>(),
    p.GetRequiredService<IStationStateService>(),p.GetRequiredService<IPlcSignalCache>(),p.GetRequiredService<IPlcGateway>(),p.GetRequiredService<IAgvStateCache>(),
    p.GetRequiredService<IActiveMapRuntime>(),p.GetRequiredService<IMapExecutionGate>(),taskParameterRuntime,dispatchOrchestratorOptions,taskCreationOptions,
    p.GetRequiredService<HandshakeExecutionOptions>(),p.GetRequiredService<IRobokitTaskMapPreflight>(),p.GetRequiredService<ITrafficResourceControlService>(),
    p.GetRequiredService<IRoutePlanner>(),p.GetRequiredService<ITrafficResourceManager>(),p.GetRequiredService<IAgvGateway>(),
    PlcCycleTestPort.AllPlcsAreLoopback(plcConnections),p.GetRequiredService<IChargingDispatchGuard>()));
builder.Services.AddSingleton(p=>new FleetTestService(p.GetRequiredService<IFleetTestPort>(),p.GetRequiredService<FleetTestOwnership>(),
    Path.Combine(Path.GetDirectoryName(databaseOptions.DatabasePath)!,"fleet-test.json"),p.GetRequiredService<ILogger<FleetTestService>>(),fleetTestOptions));
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
    provider.GetRequiredService<MapDefinitionProvider>().Definition is { } definition
        ? MapCorridorResources.AllResources(definition)
        : []));
builder.Services.AddSingleton<ActiveMapRuntime>();
builder.Services.AddSingleton<IActiveMapRuntime>(provider => provider.GetRequiredService<ActiveMapRuntime>());
builder.Services.AddSingleton(new RoadClosureRoutingOptions
{
    ProtectedTransitSites = dispatchOrchestratorOptions.GetDumperWaitingSites()
        .Append(dispatchOrchestratorOptions.DumperDumpingSite).Concat(dispatchOrchestratorOptions.StandbySites)
        .Where(site => !string.IsNullOrWhiteSpace(site)).Select(site => site!).ToArray()
});
builder.Services.AddSingleton<IRoadClosureService, RoadClosureService>();
builder.Services.AddSingleton<INavigationTestDetourPlanner, NavigationTestDetourPlanner>();
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
builder.Services.AddSingleton<CompletedTransportRouteReleaseService>();
builder.Services.AddSingleton<ITransportTimeoutRecoveryService, TransportTimeoutRecoveryService>();
builder.Services.AddHostedService<TransportTimeoutRecoveryMonitor>();
var obstacleRecoveryOptions = new ObstacleRecoveryOptions();
builder.Configuration.GetSection(ObstacleRecoveryOptions.SectionName).Bind(obstacleRecoveryOptions);
obstacleRecoveryOptions.Validate();
builder.Services.AddSingleton(obstacleRecoveryOptions);
builder.Services.AddSingleton<ObstacleRecoveryStore>();
builder.Services.AddSingleton<ObstacleRecoveryCoordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ObstacleRecoveryCoordinator>());
var waitingAlerts = new TrafficWaitingAlertOptions();
builder.Configuration.GetSection("TrafficWaitingAlerts").Bind(waitingAlerts); waitingAlerts.Validate();
builder.Services.AddSingleton(waitingAlerts);
var segmentReleaseWorkerOptions = new TrafficSegmentReleaseWorkerOptions();
builder.Configuration.GetSection("TrafficSegmentRelease").Bind(segmentReleaseWorkerOptions);
segmentReleaseWorkerOptions.Validate();
var segmentReleasePolicy = new TrafficSegmentReleasePolicy();
builder.Configuration.GetSection("TrafficSegmentRelease").Bind(segmentReleasePolicy);
var segmentCommitOptions = new TrafficSegmentCommitOptions
{
    Enabled = builder.Configuration.GetValue("TrafficSegmentRelease:CommitEnabled", false)
};
builder.Services.AddSingleton(segmentReleaseWorkerOptions);
builder.Services.AddSingleton(segmentReleasePolicy);
builder.Services.AddSingleton(new TrafficSegmentReleaseMonitor());
builder.Services.AddSingleton<ITrafficBodyEnvelopeProvider, ConfiguredTrafficBodyEnvelopeProvider>();
builder.Services.AddSingleton<IAuthoritativeTrafficProgressReader, AgvTrafficProgressReader>();
builder.Services.AddSingleton<TrafficSegmentReleaseInspector>();
builder.Services.AddSingleton<ITrafficWaitingBayDepartureService>(p=>new TrafficWaitingBayDepartureInspector(
    p.GetRequiredService<ISqlSugarClient>(),p.GetRequiredService<TrafficSegmentReleaseInspector>(),p.GetRequiredService<IMapExecutionGate>(),
    ()=>new(dispatchOrchestratorOptions.GetDumperWaitingSite()??"",dispatchOrchestratorOptions.DumperDumpingSite??""),
    p.GetService<IAuthoritativeTrafficProgressReader>(),p.GetService<ITrafficGeometryProfileProvider>()));
// Only a read-only diagnostic policy; release/commit remain independently disabled.
builder.Services.AddSingleton(new TrafficGeometryPreviewPolicy { Enabled=true });
builder.Services.AddSingleton<ITrafficGeometryPreviewService,TrafficGeometryPreviewService>();
builder.Services.AddSingleton<ITrafficSegmentReleaseInspector>(p=>p.GetRequiredService<TrafficSegmentReleaseInspector>());
builder.Services.AddSingleton(segmentCommitOptions);
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
builder.Services.AddSingleton<ITaskPauseService, TaskPauseService>();
var taskHandoffOptions = new TaskHandoffOptions();
builder.Configuration.GetSection("TaskHandoff").Bind(taskHandoffOptions);
builder.Services.AddSingleton(taskHandoffOptions);
builder.Services.AddSingleton<ITaskSwitchVehicleProbe, TaskSwitchVehicleProbe>();
builder.Services.AddSingleton<TaskSwitchRuntimePort>();
builder.Services.AddSingleton<ITaskSwitchPort>(p => p.GetRequiredService<TaskSwitchRuntimePort>());
builder.Services.AddSingleton<TaskSwitchService>();
builder.Services.AddSingleton<ITaskSwitchService>(p => p.GetRequiredService<TaskSwitchService>());
builder.Services.AddSingleton<ITaskSwitchGuard, TaskSwitchGuard>();
builder.Services.AddSingleton<ITaskSystemQueryPort, TaskSystemQueryPort>();
builder.Services.AddSingleton<ITaskSwitchQueries, TaskSwitchQueries>();
builder.Services.AddHostedService<TaskSwitchWorker>();
builder.Services.AddHostedService<DispatchTaskOrchestrator>();
var outboxOptions = new AgvCommandOutboxOptions();
builder.Configuration.GetSection(AgvCommandOutboxOptions.SectionName).Bind(outboxOptions);
builder.Services.AddSingleton(outboxOptions);
builder.Services.AddHostedService<AgvCommandOutboxDispatcher>();
builder.Services.AddHostedService<CompletedTransportRouteReleaseWorker>();
builder.Services.AddHostedService<WaitingBayDepartureReleaseWorker>();
builder.Services.AddHostedService<RouteSegmentPassThroughReleaseWorker>();
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
