using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

/// <summary>
/// B07-R8 "分批拿路权": splits the next, not yet issued rolling window at the farthest safe stop
/// whose resources are free, so a follower moves up behind a vehicle that is still passing
/// through instead of standing at its old stop until the whole window is empty.
/// Pure function: no lease is changed here; the caller still acquires the shortened window
/// through the normal queue and grant-time cycle checks.
/// The split is refused unless every blocking holder is a transport step whose remaining route
/// (sent window, later windows, pending request) is known and never uses the advanced stretch,
/// and none of them will stop on a blocked resource (its step destination).
/// </summary>
public static class PartialWindowAdvance
{
    public sealed record Decision(IReadOnlyList<AgvTransportRouteWindow>? Windows, string Reason, int CutAfterEdges = 0);

    public static Decision Plan(
        IReadOnlyList<AgvTransportRouteWindow> windows,
        int nextIndex,
        Func<string, MapNode?> findNode,
        Func<AgvTransportRouteWindow, IReadOnlyList<string>> resourcesOf,
        IReadOnlySet<string> unavailable,
        IReadOnlyList<TrafficBlockingHolder>? holders,
        Func<string, IReadOnlyCollection<string>> destinationResources,
        IReadOnlyCollection<string>? retained = null,
        int minimumEdges = 1,
        IReadOnlySet<string>? occupiedEdges = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (nextIndex < 1 || nextIndex >= windows.Count) return new(null, "只拆分尚未下发的后续窗口。");
        var next = windows[nextIndex];
        if (next.EdgeIds.Count < 2 || next.Path.Count != next.EdgeIds.Count + 1) return new(null, "窗口过短，无可拆分的中间安全点。");
        var blocked = next.ResourceIds.Where(unavailable.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (blocked.Length == 0) return new(null, "整窗可用，按原窗口申请。");
        if (holders is null) return new(null, "无法读取占用方，保持整窗等待。");
        var covered = holders.SelectMany(holder => holder.BlockedResourceIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (blocked.Any(resource => !covered.Contains(resource))) return new(null, "部分占用资源找不到占用方，保持整窗等待。");
        if (holders.FirstOrDefault(holder => !holder.Known) is { } unknown)
            return new(null, $"占用方{unknown.VehicleId}未来路线不可证明：{unknown.Reason}");
        foreach (var holder in holders)
        {
            if (string.IsNullOrWhiteSpace(holder.FinalDestination)) return new(null, $"占用方{holder.VehicleId}缺少终点。");
            var parking = destinationResources(holder.FinalDestination);
            if (holder.BlockedResourceIds.Any(resource => parking.Contains(resource, StringComparer.OrdinalIgnoreCase)))
                return new(null, $"占用方{holder.VehicleId}将在被占资源处停靠，保持整窗等待。");
        }
        var future = holders.SelectMany(holder => holder.RemainingRouteResourceIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lower = Math.Max(1, minimumEdges);
        for (var cut = next.Path.Count - 2; cut >= lower; cut--)
        {
            if (findNode(next.Path[cut]) is not { } node || !TransportRouteWindows.IsStableStop(node, null)) continue;
            var prefix = new AgvTransportRouteWindow(next.Index, next.Source, next.Path[cut],
                next.Path.Take(cut + 1).ToArray(), next.EdgeIds.Take(cut).ToArray(), [], false);
            if (occupiedEdges?.Overlaps(prefix.EdgeIds) == true) continue;
            var prefixResources = resourcesOf(prefix);
            if (prefixResources.Any(unavailable.Contains)) continue;
            if (prefixResources.Any(future.Contains)) continue;
            var remainder = new AgvTransportRouteWindow(next.Index + 1, next.Path[cut], next.Destination,
                next.Path.Skip(cut).ToArray(), next.EdgeIds.Skip(cut).ToArray(), [], next.Final);
            prefix = prefix with
            {
                ResourceIds = prefixResources.Concat(retained ?? []).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray()
            };
            remainder = remainder with { ResourceIds = resourcesOf(remainder) };
            var result = windows.Take(nextIndex).Append(prefix).Append(remainder).Concat(windows.Skip(nextIndex + 1))
                .Select((window, index) => window with { Index = index }).ToArray();
            return new(result, $"分批路权：先申请至安全点{next.Path[cut]}（{cut}/{next.EdgeIds.Count}段），其余待占用方通过后再申请。", cut);
        }
        return new(null, "前方没有可用且不与占用方剩余路线冲突的安全点，保持整窗等待。");
    }
}
