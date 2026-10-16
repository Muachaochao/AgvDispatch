using System.Text.Json;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Persistence.Entities;
namespace AgvDispatch.Infrastructure.Persistence;
internal static class TrafficBlockageObservation
{
    public static TrafficResourceLeaseEntity[] Find(IEnumerable<string> resources, string task,
        IReadOnlyList<TrafficResourceLeaseEntity> leases, TrafficResourceCatalog catalog) => resources.Distinct(StringComparer.OrdinalIgnoreCase)
        .SelectMany(id =>
        {
            var others = leases.Where(l => string.Equals(l.ResourceId,id,StringComparison.OrdinalIgnoreCase) && !string.Equals(l.TaskId,task,StringComparison.OrdinalIgnoreCase)).ToArray();
            return others.Select(l=>l.TaskId).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= (catalog.TryGet(id,out var resource) ? resource.Capacity : 1) ? others : [];
        }).ToArray();
    public static string Key(IEnumerable<TrafficResourceLeaseEntity> leases) => JsonSerializer.Serialize(leases.Select(l=>
        new[]{l.ResourceId.ToUpperInvariant(),l.TaskId.ToUpperInvariant(),l.VehicleId.ToUpperInvariant(),l.OwnerStepId.ToUpperInvariant(),l.MapId,l.MapVersionId})
        .OrderBy(a=>JsonSerializer.Serialize(a),StringComparer.Ordinal));
}
