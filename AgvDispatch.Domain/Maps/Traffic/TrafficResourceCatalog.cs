namespace AgvDispatch.Domain.Maps;

public sealed class TrafficResourceCatalog(IEnumerable<TrafficResourceDefinition> resources)
{
    private IReadOnlyDictionary<string, TrafficResourceDefinition> _resources = Build(resources);

    public IReadOnlyCollection<TrafficResourceDefinition> All => _resources.Values.ToArray();

    public bool TryGet(string id, out TrafficResourceDefinition resource) =>
        Volatile.Read(ref _resources).TryGetValue(id, out resource!);

    public void Replace(IEnumerable<TrafficResourceDefinition> resources) =>
        Volatile.Write(ref _resources, Build(resources));

    private static IReadOnlyDictionary<string, TrafficResourceDefinition> Build(
        IEnumerable<TrafficResourceDefinition> resources) =>
        resources.ToDictionary(resource => resource.Id, StringComparer.OrdinalIgnoreCase);
}
