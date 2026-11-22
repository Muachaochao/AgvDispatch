using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Tasks;

if (args.Length < 4 || args.Length % 4 != 0)
{
    Console.Error.WriteLine("Usage: VehicleJackMaintenance <vehicleId> <ip> <statusPort> <navigationPort> [...]");
    return 2;
}

var evidenceRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "AgvDispatch",
    "VehicleJackMaintenance",
    DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(evidenceRoot);

var results = new List<object>();
for (var index = 0; index < args.Length; index += 4)
{
    var vehicleId = args[index];
    var ip = args[index + 1];
    var statusPort = int.Parse(args[index + 2]);
    var navigationPort = int.Parse(args[index + 3]);
    var options = new AgvOptions
    {
        Enabled = true,
        AllowCommands = true,
        ConnectTimeoutMs = 3000,
        RequestTimeoutMs = 3000,
        RequestIntervalMs = 150,
        Robots =
        [
            new AgvRobotOptions
            {
                Enabled = true,
                VehicleId = vehicleId,
                DisplayName = vehicleId,
                IpAddress = ip,
                Port = statusPort,
                NavigationPort = navigationPort
            }
        ]
    };
    if (options.GetConfigurationError() is { } configurationError)
        throw new InvalidOperationException(configurationError);

    await using var connections = new AgvConnectionManager(options);
    var service = new SingleNavigationTestService(
        new SingleNavigationTestOptions
        {
            Enabled = true,
            VehicleId = vehicleId,
            BusinessExecutionDisabled = false,
            OtherApiPort = 19210
        },
        options,
        connections,
        new NoActiveTasks(),
        new RunningGate(),
        Path.Combine(evidenceRoot, vehicleId + ".json"));

    var before = await service.ReadJackAsync(CancellationToken.None);
    if (before.Telemetry is not { } initial)
        throw new InvalidOperationException(vehicleId + " 顶升反馈不可用：" + before.BlockReason);

    JackTestRecord record;
    if (initial.State == 3)
    {
        results.Add(new { VehicleId = vehicleId, Before = initial, Action = "AlreadyDown", After = initial });
        continue;
    }
    if (initial.State != 1)
        throw new InvalidOperationException(vehicleId + " 当前不是已到上限、可安全下降的状态：" + initial.State);

    var request = new JackTestSubmit(
        Guid.NewGuid().ToString("N"),
        "Down",
        AreaClearConfirmed: true,
        EmptyLoadConfirmed: true);
    record = await service.SubmitJackAsync(SystemOperator.User, request, CancellationToken.None);

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    JackTestTelemetry? settled = null;
    while (!timeout.IsCancellationRequested)
    {
        await Task.Delay(500, timeout.Token);
        var current = await service.ReadJackAsync(timeout.Token);
        settled = current.Telemetry;
        if (settled is { State: 3, Enabled: true, Emergency: false, ErrorCode: 0 })
            break;
        if (settled is { State: 255 } || settled is { ErrorCode: not 0 })
            throw new InvalidOperationException(vehicleId + " 下降异常：" + current.BlockReason);
    }
    if (settled is not { State: 3 })
        throw new TimeoutException(vehicleId + " 30秒内未确认下降到下限位。");

    results.Add(new { VehicleId = vehicleId, Before = initial, Action = "Down", Record = record, After = settled });
}

var evidencePath = Path.Combine(evidenceRoot, "result.json");
await File.WriteAllTextAsync(
    evidencePath,
    System.Text.Json.JsonSerializer.Serialize(
        new { CapturedAt = DateTimeOffset.Now, Results = results, EvidencePath = evidencePath },
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("EVIDENCE=" + evidencePath);
return 0;

static class SystemOperator
{
    public static readonly TrafficUserContext User = new(
        Environment.UserName,
        Environment.UserName,
        TrafficPermission.View | TrafficPermission.Administer);
}

sealed class NoActiveTasks : IActiveTaskLookup
{
    public Task<IReadOnlyList<ActiveDispatchTask>> GetActiveTasksAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ActiveDispatchTask>>([]);
}

sealed class RunningGate : IMapExecutionGate
{
    public MapExecutionMode Mode => MapExecutionMode.Running;
    public bool CanStartNewWork => true;
    public ValueTask<IDisposable?> EnterAsync(
        MapExecutionOperation operation,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IDisposable?>(EmptyLease.Instance);
    public ValueTask<IDisposable> EnterControlAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IDisposable>(EmptyLease.Instance);
    public void SetMode(MapExecutionMode mode)
    {
        if (mode != MapExecutionMode.Running)
            throw new InvalidOperationException("维护工具不允许切换执行模式。");
    }
}

sealed class EmptyLease : IDisposable
{
    public static readonly EmptyLease Instance = new();
    public void Dispose() { }
}
