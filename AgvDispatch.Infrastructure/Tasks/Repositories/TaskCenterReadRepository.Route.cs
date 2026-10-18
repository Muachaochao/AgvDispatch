using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Traffic;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Domain.Tasks;
namespace AgvDispatch.Infrastructure.Persistence;
public sealed partial class TaskCenterReadRepository
{
    private static TaskRouteDetail? ReadRoute(AgvCommandOutboxEntity command)
    {
        try{
            if((command.ExecutionRouteSnapshotJson?.Length??0)>1024*1024)return null;
            var s=TransportRouteSnapshots.Read(command);
            if(s.EdgeIds.Length is <1 or >2048||s.NodeIds.Length!=s.EdgeIds.Length+1||s.AgvSites.Length!=s.NodeIds.Length||s.NodeIds.Any(string.IsNullOrWhiteSpace)||s.EdgeIds.Any(string.IsNullOrWhiteSpace))return null;
            var payload=JsonSerializer.Deserialize<AgvTransportCommandPayload>(command.PayloadJson,new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new(s.TaskId,s.StepId,s.CommandId,s.VehicleId,s.MapId,s.MapVersionId,s.MapDefinitionHash,s.NodeIds,s.EdgeIds,s.AgvSites,payload?.Action?.Operation??"导航，无附加动作");
        }catch(Exception e)when(e is InvalidDataException or JsonException or ArgumentException or NullReferenceException){return null;}
    }
}
