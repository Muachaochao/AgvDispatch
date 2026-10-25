using System.Text.Json;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

// Persist every segment identity before the first send, so specified-path queries
// inspect the entire chain rather than incorrectly querying only the root ID.
public sealed class BusinessStandbyChains(string directory)
{
    public void Save(AgvTransportChain chain)
    {
        Directory.CreateDirectory(directory);
        var path = PathFor(chain.BusinessTaskId);
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, chain); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    private string PathFor(string id)
    {
        if (!id.StartsWith("STANDBY-", StringComparison.Ordinal) || !Guid.TryParseExact(id[8..], "N", out _))
            throw new ArgumentException("待机任务编号无效。");
        return Path.Combine(directory, id + ".json");
    }
    public AgvTransportChain? Find(string id)
    {
        if (!id.StartsWith("STANDBY-", StringComparison.Ordinal)) return null;
        var path = PathFor(id);
        return File.Exists(path) ? JsonSerializer.Deserialize<AgvTransportChain>(File.ReadAllText(path))
            ?? throw new InvalidDataException("待机任务链记录损坏。") : null;
    }
}
public sealed class BusinessTransportChainLookup(AgvDispatch.Infrastructure.Persistence.AgvTransportChainLookup normal,
    BusinessStandbyChains standby) : IAgvTransportChainLookup
{
    public Task<AgvTransportChain?> FindAsync(string id, CancellationToken ct) =>
        id.StartsWith("STANDBY-", StringComparison.Ordinal) ? Task.FromResult(standby.Find(id)) : normal.FindAsync(id, ct);
}
