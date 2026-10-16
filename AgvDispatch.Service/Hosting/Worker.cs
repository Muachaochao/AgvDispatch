using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

public sealed class Worker(
    ILogger<Worker> logger,
    IDatabaseInitializer database,
    IPlcGateway plcGateway,
    IPlcConnectionSupervisor plcSupervisor,
    IAgvGateway agvGateway,
    IStationStateService stationStates) : IsolatedBackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken);
        logger.LogInformation("AGV调度服务启动，SQLite数据库初始化完成。Version={Version}",
            typeof(Worker).Assembly.GetName().Version);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var plc = await plcGateway.GetConnectionStatusAsync(stoppingToken);
            var agv = await agvGateway.GetConnectionStatusAsync(stoppingToken);
            var databaseOnline = await database.CanConnectAsync(stoppingToken);
            var stations = stationStates.GetAll();

            logger.LogDebug(
                "服务心跳 PLC={PlcState}, PLC连续失败={PlcFailures}, PLC响应={PlcResponseMs}ms, AGV={AgvState}, Database={DatabaseState}, 工位={StationCount}, 可创建任务={ReadyCount}, 冲突={ConflictCount}, 故障={FaultCount}, 离线={OfflineCount}",
                plc.State,
                plcSupervisor.Current.ConsecutiveFailures,
                plcSupervisor.Current.LastResponseTimeMs,
                agv.State,
                databaseOnline ? "Online" : "Offline",
                stations.Count,
                stations.Count(station => station.CanCreateTask),
                stations.Count(station => station.State == Domain.Stations.StationOperationalState.SignalConflict),
                stations.Count(station => station.State == Domain.Stations.StationOperationalState.Faulted),
                stations.Count(station => station.State == Domain.Stations.StationOperationalState.Offline));

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
