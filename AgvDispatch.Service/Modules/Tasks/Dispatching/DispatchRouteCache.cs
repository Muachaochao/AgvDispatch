using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

/// <summary>
/// Bounded LRU for rolling-dispatch route estimates. This is never a send permit:
/// the outbox still rechecks closures, map identity and resource ownership.
/// </summary>
public sealed class DispatchRouteCache(
    IRoutePlanner planner,
    IActiveMapRuntime maps,
    ITrafficFlowMonitor? traffic = null,
    IRoadClosureService? closures = null,
    IAgvStateCache? vehicles = null,
    DispatchOrchestratorOptions? dispatch = null,
    int capacity = 2048)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private long _hits;
    private long _misses;
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public int EntryCount { get { lock (_sync) return _entries.Count; } }

    public RoutePlan? Plan(string source, string destination, IReadOnlySet<string> unavailableResourceIds)
    {
        ArgumentNullException.ThrowIfNull(unavailableResourceIds);
        var before = Context();
        var key = JsonSerializer.Serialize(new object?[] { before, source, destination,
            unavailableResourceIds.Order(StringComparer.OrdinalIgnoreCase).ToArray() });
        RoutePlan? cached = null;
        var found = false;
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                _lru.Remove(entry);
                _lru.AddFirst(entry);
                cached = entry.Value.Route;
                found = true;
            }
        }
        if (found)
        {
            // Context can change while another thread supplies a cached route.
            if (before != Context()) return null;
            Interlocked.Increment(ref _hits);
            return cached;
        }
        Interlocked.Increment(ref _misses);
        var route = planner.Plan(source, destination, unavailableResourceIds);
        if (before != Context()) return null;
        // Defensive copies prevent an estimate consumer mutating the cached path.
        var frozen = route is null ? null : route with
        {
            Nodes = Array.AsReadOnly(route.Nodes.ToArray()),
            EdgeIds = Array.AsReadOnly(route.EdgeIds.ToArray()),
            ResourceIds = Array.AsReadOnly(route.ResourceIds.ToArray())
        };
        lock (_sync)
        {
            if (_entries.Remove(key, out var old)) _lru.Remove(old);
            var added = _lru.AddFirst(new Entry(key, frozen));
            _entries.Add(key, added);
            while (_entries.Count > Math.Max(1, capacity))
            {
                var last = _lru.Last!;
                _entries.Remove(last.Value.Key);
                _lru.RemoveLast();
            }
        }
        return frozen;
    }

    private string Context()
    {
        var map = maps.Current;
        var road = closures?.GetSnapshot();
        // Do not key on sample timestamps or the telemetry cache's incrementing
        // version: stable road conditions should yield hits across poll cycles.
        var penalties = traffic?.GetEdgePenalties().OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new object[] { p.Key, p.Value }).ToArray();
        var occupied = vehicles?.GetAll().Where(v => !string.IsNullOrWhiteSpace(v.CurrentStation))
            .Select(v => v.CurrentStation!).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return JsonSerializer.Serialize(new object?[]
        {
            map?.RuntimeId, map?.MapId, map?.VersionId,
            road?.Ready, road?.Revision, road?.MapId, road?.VersionId, road?.DefinitionHash, road?.IdentityConflict,
            penalties, occupied,
            dispatch?.GetDumperWaitingSites().Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            dispatch?.DumperDumpingSite, dispatch?.StandbySites.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            dispatch?.AllowUnconfiguredDirectRoutes,
            dispatch?.Routes.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new object[] { p.Key, p.Value }).ToArray()
        });
    }

    private sealed record Entry(string Key, RoutePlan? Route);
}
