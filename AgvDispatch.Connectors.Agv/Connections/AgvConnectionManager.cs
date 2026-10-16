namespace AgvDispatch.Connectors.Agv;

public sealed class AgvConnectionManager : IAsyncDisposable
{
    private readonly Dictionary<string, SeerRobotClient> _clients;

    public AgvConnectionManager(AgvOptions options)
    {
        _clients = options.Robots
            .Where(robot => robot.Enabled)
            .ToDictionary(
                robot => robot.VehicleId,
                robot => new SeerRobotClient(robot, options),
                StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<SeerRobotClient> Clients => _clients.Values
        .OrderBy(client => client.Robot.VehicleId, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public bool TryGet(string vehicleId, out SeerRobotClient client) =>
        _clients.TryGetValue(vehicleId, out client!);

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync();
        }
    }
}
