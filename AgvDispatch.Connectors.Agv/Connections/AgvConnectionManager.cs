namespace AgvDispatch.Connectors.Agv;

public sealed class AgvConnectionManager : IAsyncDisposable
{
    private readonly Dictionary<string, SeerRobotClient> _clients;

    public AgvConnectionManager(AgvOptions options)
    {
        _clients = options.Robots
            .ToDictionary(
                robot => robot.VehicleId,
                robot => new SeerRobotClient(robot, options),
                StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<SeerRobotClient> AllClients => _clients.Values
        .OrderBy(client => client.Robot.VehicleId, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public IReadOnlyList<SeerRobotClient> Clients => AllClients.Where(c => c.CommunicationEnabled).ToArray();
    public bool TryGetConfigured(string vehicleId, out SeerRobotClient client) => _clients.TryGetValue(vehicleId, out client!);

    public bool TryGet(string vehicleId, out SeerRobotClient client) =>
        _clients.TryGetValue(vehicleId, out client!) && client.CommunicationEnabled;

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisposeAsync();
        }
    }
}
