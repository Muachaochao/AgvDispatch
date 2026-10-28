using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

/// <summary>
/// Discovers additional dumper waiting bays already declared by the active map.
/// Only uniquely mapped, capacity-one DUMPER-WAIT-* resources are considered;
/// unrelated waiting points stay opt-in.
/// </summary>
public static class DumperWaitingSiteDiscovery
{
    private const string ResourcePrefix = "DUMPER-WAIT-";

    public static IReadOnlyList<string> Combine(
        IReadOnlyList<string> configured,
        MapDefinition definition)
    {
        var discovered = new List<string>();
        foreach (var resource in definition.Resources.Where(resource =>
                     resource.Capacity == 1 &&
                     resource.Id.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            var nodes = definition.Nodes.Where(node =>
                node.Enabled &&
                node.Type == MapNodeType.WaitingPoint &&
                node.ResourceIds.Contains(resource.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (nodes.Length != 1) continue;
            var site = string.IsNullOrWhiteSpace(nodes[0].AgvSiteId)
                ? nodes[0].Id
                : nodes[0].AgvSiteId!;
            if (!string.IsNullOrWhiteSpace(site)) discovered.Add(site);
        }

        return configured
            .Where(site => !string.IsNullOrWhiteSpace(site))
            .Concat(discovered)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
