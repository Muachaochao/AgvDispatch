using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed class RobokitInspectionHistory(ISqlSugarClient database) : IRobokitInspectionHistory
{
    private const string Kind="RobokitManualInspection";
    public async Task AppendAsync(string operatorIdentity,RobokitInspectionSnapshot snapshot,CancellationToken ct)
    {
        var id=Guid.NewGuid().ToString("N");
        string Bound(string? text,int length)=>text is null?"":text.Length>length?text[..length]:text;
        var record=new RobokitInspectionRecord(id,snapshot.VehicleId,snapshot.CheckedAt,Bound(operatorIdentity,256),snapshot.Available,
            snapshot.Stable,Bound(snapshot.Reason,2000),snapshot.LocalMapId,snapshot.LocalMapVersion,snapshot.RobotMap,snapshot.RobotMapMd5,
            snapshot.Stations.Count,snapshot.Mappings.Count(m=>!m.Found),Bound(string.Join("；",snapshot.Issues),4000));
        await database.Insertable(new ExecutionEvidenceEntity{Id=id,TaskId="",StepId=record.VehicleId,SessionId="",Kind=Kind,
            Operator=record.Operator,Reason=record.Reason,OccurredAt=record.At.LocalDateTime,Evidence=JsonSerializer.Serialize(record)}).ExecuteCommandAsync(ct);
    }
    public async Task<IReadOnlyList<RobokitInspectionRecord>> ReadAsync(RobokitInspectionHistoryQuery query,CancellationToken ct)
    {
        if(query.MaximumCount is <1 or >200 || query.VehicleId?.Length>32)throw new ArgumentException("核验记录查询数量范围1～200，车辆编号最长32字符。");
        var rows=await database.Queryable<ExecutionEvidenceEntity>().Where(r=>r.Kind==Kind)
            .WhereIF(!string.IsNullOrWhiteSpace(query.VehicleId),r=>r.StepId==query.VehicleId)
            .OrderBy(r=>r.OccurredAt,OrderByType.Desc).OrderBy(r=>r.Id,OrderByType.Desc).Take(query.MaximumCount).ToListAsync(ct);
        return rows.Select(r=>JsonSerializer.Deserialize<RobokitInspectionRecord>(r.Evidence)??throw new InvalidDataException("核验记录损坏。")).ToArray();
    }
}
