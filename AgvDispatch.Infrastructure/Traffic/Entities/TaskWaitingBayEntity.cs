using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("task_waiting_bay")]
public sealed class TaskWaitingBayEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)] public string TaskId { get; set; } = "";
    [SugarColumn(Length = 32)] public string VehicleId { get; set; } = "";
    [SugarColumn(Length = 64)] public string StepId { get; set; } = "";
    [SugarColumn(Length = 64)] public string MapId { get; set; } = "";
    [SugarColumn(Length = 64)] public string MapVersionId { get; set; } = "";
    [SugarColumn(Length = 128)] public string WaitingSite { get; set; } = "";
    [SugarColumn(Length = 128)] public string ResourceId { get; set; } = "";
    public DateTime SelectedAt { get; set; }
}
