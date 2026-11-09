using AgvDispatch.Connectors.Agv;
using System.Reflection;
using System.Text.Json;

if (args.Length < 4)
{
    Console.Error.WriteLine(
        "Usage: AgvObstacleDeadlock <vehicle> <ip> <original-task-id> <target-site> [--inspect|--apply|--forward|--translate-back <meters>]");
    return 2;
}

var vehicleId = args[0];
var ipAddress = args[1];
var originalTaskId = args[2];
var retreatSite = args[3];
var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
var forward = args.Contains("--forward", StringComparer.OrdinalIgnoreCase);
var translateIndex = Array.IndexOf(args, "--translate-back");
var inspect = args.Contains("--inspect", StringComparer.OrdinalIgnoreCase);
var cancelOnly = args.Contains("--cancel-only", StringComparer.OrdinalIgnoreCase);
var routeIndex = Array.IndexOf(args, "--route-csv");
var requestedRoute = routeIndex >= 0 &&
                     routeIndex + 1 < args.Length
    ? args[routeIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    : null;
var jackUnload = args.Contains("--jack-unload", StringComparer.OrdinalIgnoreCase);
var translateBack = translateIndex >= 0 &&
                    translateIndex + 1 < args.Length &&
                    double.TryParse(args[translateIndex + 1], out var parsedDistance)
    ? parsedDistance
    : (double?)null;
var options = new AgvOptions
{
    Enabled = true,
    AllowCommands = true,
    NavigationCommandMode = "SpecifiedPath",
    PollIntervalMs = 500,
    ReconnectDelayMs = 3000,
    StaleAfterMs = 5000,
    ConnectTimeoutMs = 3000,
    RequestTimeoutMs = 5000,
    RequestIntervalMs = 100,
    ProtocolVersion = 1,
    Robots =
    [
        new AgvRobotOptions
        {
            Enabled = true,
            VehicleId = vehicleId,
            DisplayName = vehicleId,
            IpAddress = ipAddress,
            Port = 19204,
            NavigationPort = 19206
        }
    ]
};

await using var connections = new AgvConnectionManager(options);
if (!connections.TryGet(vehicleId, out var client))
    throw new InvalidOperationException($"Vehicle {vehicleId} is not configured.");

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var before = await client.PollAsync(timeout.Token);
var robot = before.Telemetry;
if (inspect)
{
    var task = await client.QueryTaskAsync(originalTaskId, timeout.Token);
    var jackRequest = typeof(SeerRobotClient).GetMethod(
        "RequestAsync",
        BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new MissingMethodException(nameof(SeerRobotClient), "RequestAsync");
    var jackTask = jackRequest.Invoke(client, [(ushort)1027, null, timeout.Token])
        as Task<JsonDocument> ?? throw new InvalidOperationException("Unexpected 1027 response type.");
    using var jack = await jackTask;
    var blockTask = jackRequest.Invoke(client, [(ushort)1006, null, timeout.Token])
        as Task<JsonDocument> ?? throw new InvalidOperationException("Unexpected 1006 response type.");
    using var block = await blockTask;
    var jackState = jack.RootElement.TryGetProperty("jack_state", out var jackStateValue) &&
                    jackStateValue.TryGetInt32(out var stateValue)
        ? stateValue
        : (int?)null;
    var jackFull = jack.RootElement.TryGetProperty("jack_isFull", out var jackFullValue) &&
                   jackFullValue.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? jackFullValue.GetBoolean()
        : (bool?)null;
    var jackError = jack.RootElement.TryGetProperty("jack_error_code", out var jackErrorValue) &&
                    jackErrorValue.TryGetInt32(out var errorValue)
        ? errorValue
        : (int?)null;
    var blockReason = block.RootElement.TryGetProperty("block_reason", out var blockReasonValue) &&
                      blockReasonValue.TryGetInt32(out var reasonValue)
        ? reasonValue
        : (int?)null;
    var blockX = block.RootElement.TryGetProperty("block_x", out var blockXValue) &&
                 blockXValue.TryGetDouble(out var xValue)
        ? xValue
        : (double?)null;
    var blockY = block.RootElement.TryGetProperty("block_y", out var blockYValue) &&
                 blockYValue.TryGetDouble(out var yValue)
        ? yValue
        : (double?)null;
    var blockId = block.RootElement.TryGetProperty("block_id", out var blockIdValue) &&
                  blockIdValue.TryGetInt32(out var idValue)
        ? idValue
        : (int?)null;
    var slowed = block.RootElement.TryGetProperty("slowed", out var slowedValue) &&
                 slowedValue.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? slowedValue.GetBoolean()
        : (bool?)null;
    Console.WriteLine(
        $"INSPECT vehicle={vehicleId} station={robot.CurrentStation ?? "-"} x={robot.X} y={robot.Y} " +
        $"blocked={robot.Blocked} navigation={robot.NavigationState} vx={robot.VelocityX} vy={robot.VelocityY} " +
        $"jack={jackState?.ToString() ?? "?"} full={jackFull?.ToString() ?? "?"} jackError={jackError?.ToString() ?? "?"} " +
        $"blockReason={blockReason?.ToString() ?? "?"} blockX={blockX?.ToString() ?? "?"} " +
        $"blockY={blockY?.ToString() ?? "?"} blockId={blockId?.ToString() ?? "?"} slowed={slowed?.ToString() ?? "?"} " +
        $"originalTask={originalTaskId} status={task?.Status.ToString() ?? "missing"} " +
        $"tasks={string.Join(",", robot.Tasks.Select(item => $"{item.TaskId}:{item.Status}"))}");
    return 0;
}
if (cancelOnly)
{
    var task = await client.QueryTaskAsync(originalTaskId, timeout.Token);
    if (task is { Status: 1 or 2 or 3 })
    {
        await client.CancelNavigationAsync(originalTaskId, smartCancel: true, timeout.Token);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(500, timeout.Token);
            task = await client.QueryTaskAsync(originalTaskId, timeout.Token);
            if (task is null || task.Status is 4 or 5 or 6 or 7)
                break;
        }
    }
    var after = await client.PollAsync(timeout.Token);
    Console.WriteLine(
        $"CANCELLED vehicle={vehicleId} task={originalTaskId} status={task?.Status.ToString() ?? "missing"} " +
        $"station={after.Telemetry.CurrentStation ?? "-"} blocked={after.Telemetry.Blocked} " +
        $"vx={after.Telemetry.VelocityX} vy={after.Telemetry.VelocityY}");
    return task is null || task.Status is 4 or 5 or 6 or 7 ? 0 : 4;
}
if (robot.EmergencyStopped != false)
    throw new InvalidOperationException("Emergency stop must be clear.");
if (!forward && translateBack is null && robot.Blocked != true)
    throw new InvalidOperationException("Backward recovery requires a blocked vehicle.");
if (Math.Abs(robot.VelocityX ?? double.NaN) > 0.02 ||
    Math.Abs(robot.VelocityY ?? double.NaN) > 0.02 ||
    Math.Abs(robot.AngularVelocity ?? double.NaN) > 0.02)
    throw new InvalidOperationException("Vehicle is not confirmed stopped.");

var original = await client.QueryTaskAsync(originalTaskId, timeout.Token);
Console.WriteLine(
    $"BEFORE vehicle={vehicleId} station={robot.CurrentStation ?? "-"} x={robot.X} y={robot.Y} " +
    $"blocked={robot.Blocked} originalTask={originalTaskId} status={original?.Status.ToString() ?? "missing"}");
Console.WriteLine(
    "ACTIVE_TASKS " +
    string.Join(",", robot.Tasks.Select(task => $"{task.TaskId}:{task.Status}")));
if (!apply && !forward && translateBack is null)
{
    Console.WriteLine("READ_ONLY");
    return 0;
}
if (original is { Status: 1 or 2 or 3 })
{
    await client.CancelNavigationAsync(originalTaskId, smartCancel: true, timeout.Token);
    var terminalDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
    while (DateTimeOffset.UtcNow < terminalDeadline)
    {
        await Task.Delay(500, timeout.Token);
        original = await client.QueryTaskAsync(originalTaskId, timeout.Token);
        if (original is null || original.Status is 4 or 5 or 6 or 7)
            break;
    }
    if (original?.Status is 1 or 2 or 3)
        throw new InvalidOperationException("Original vendor task did not reach a terminal state after cancellation.");
}

var current = await client.PollAsync(timeout.Token);
if (current.Telemetry.EmergencyStopped != false ||
    Math.Abs(current.Telemetry.VelocityX ?? double.NaN) > 0.02 ||
    Math.Abs(current.Telemetry.VelocityY ?? double.NaN) > 0.02 ||
    Math.Abs(current.Telemetry.AngularVelocity ?? double.NaN) > 0.02)
    throw new InvalidOperationException("Vehicle did not settle after original-task cancellation.");

var retreatTaskId = "DEADLOCK-" + Guid.NewGuid().ToString("N");
if (translateBack is { } distance)
{
    if (distance is <= 0 or > 1.0)
        throw new ArgumentOutOfRangeException(nameof(translateBack), "Translate distance must be 0-1m.");
    var request = typeof(SeerRobotClient).GetMethod(
        "RequestAsync",
        BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new MissingMethodException(nameof(SeerRobotClient), "RequestAsync");
    var responseTask = request.Invoke(
        client,
        [(ushort)3055, new { dist = distance, vx = -0.1, vy = 0.0, mode = 1 }, timeout.Token])
        as Task<JsonDocument> ?? throw new InvalidOperationException("Unexpected 3055 response type.");
    using var response = await responseTask;
    await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, distance / 0.1 + 2)), timeout.Token);
    var stopped = await client.PollAsync(timeout.Token);
    Console.WriteLine(
        $"TRANSLATED_BACK vehicle={vehicleId} distance={distance} " +
        $"station={stopped.Telemetry.CurrentStation ?? "-"} x={stopped.Telemetry.X} y={stopped.Telemetry.Y} " +
        $"blocked={stopped.Telemetry.Blocked} vx={stopped.Telemetry.VelocityX}");
    return stopped.Telemetry.Blocked == false ? 0 : 3;
}
if (forward)
{
    if (string.IsNullOrWhiteSpace(current.Telemetry.CurrentStation))
        throw new InvalidOperationException("Forward move requires a trusted current station.");
    var path = requestedRoute ?? [current.Telemetry.CurrentStation, retreatSite];
    if (path.Length < 2 ||
        !string.Equals(path[0], current.Telemetry.CurrentStation, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(path[^1], retreatSite, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            $"Route must start at {current.Telemetry.CurrentStation} and end at {retreatSite}.");
    await client.GoToTargetAsync(
        retreatTaskId,
        path,
        action: jackUnload ? new AgvDispatch.Domain.Agvs.AgvTransportAction("JackUnload") : null,
        useSpecifiedPath: true,
        timeout.Token);
}
else
{
    await client.GoFromCurrentPoseBackwardAsync(
        retreatTaskId,
        retreatSite,
        maximumSpeedMetersPerSecond: 0.2,
        maximumAccelerationMetersPerSecondSquared: 0.3,
        timeout.Token);
}

var arrivalDeadline = DateTimeOffset.UtcNow.AddSeconds(
    requestedRoute is { Length: > 12 } ? 180 : 90);
while (DateTimeOffset.UtcNow < arrivalDeadline)
{
    await Task.Delay(500, timeout.Token);
    var observed = await client.PollAsync(timeout.Token);
    if (string.Equals(observed.Telemetry.CurrentStation, retreatSite, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs(observed.Telemetry.VelocityX ?? double.NaN) <= 0.02 &&
        Math.Abs(observed.Telemetry.VelocityY ?? double.NaN) <= 0.02)
    {
        var retreat = await client.QueryTaskAsync(retreatTaskId, timeout.Token);
        Console.WriteLine(
            $"RETREATED vehicle={vehicleId} station={observed.Telemetry.CurrentStation} " +
            $"retreatTask={retreatTaskId} status={retreat?.Status.ToString() ?? "missing"}");
        return 0;
    }
}

var last = await client.PollAsync(timeout.Token);
throw new TimeoutException(
    $"Vehicle did not reach retreat site {retreatSite} within 90 seconds. " +
    $"station={last.Telemetry.CurrentStation ?? "-"} blocked={last.Telemetry.Blocked} " +
    $"tasks={string.Join(",", last.Telemetry.Tasks.Select(task => $"{task.TaskId}:{task.Status}"))}");
