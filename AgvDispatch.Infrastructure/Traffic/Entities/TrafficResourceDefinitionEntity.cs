using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("traffic_resource_definition")]
public sealed class TrafficResourceDefinitionEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 128)]
    public string ResourceId { get; set; } = string.Empty;

    public int ResourceType { get; set; }
    public int Capacity { get; set; }
    public DateTime UpdatedAt { get; set; }
}
