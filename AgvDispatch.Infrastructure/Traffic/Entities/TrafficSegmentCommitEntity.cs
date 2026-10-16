using SqlSugar;
namespace AgvDispatch.Infrastructure.Persistence.Entities;
[SugarTable("traffic_segment_commit")]
public sealed class TrafficSegmentCommitEntity
{
    [SugarColumn(IsPrimaryKey=true,Length=64)]public string RequestId{get;set;}="";
    [SugarColumn(Length=64)]public string ReceiptId{get;set;}="";
    [SugarColumn(ColumnDataType="TEXT")]public string ResultJson{get;set;}="";
    [SugarColumn(Length=64)]public string ResultHash{get;set;}="";
    public DateTime CommittedAtUtc{get;set;}
}
[SugarTable("traffic_segment_commit_lease")]
public sealed class TrafficSegmentCommitLeaseEntity
{
    [SugarColumn(IsPrimaryKey=true,Length=64)]public string AuditId{get;set;}="";
    [SugarColumn(Length=64)]public string RequestId{get;set;}="";
    [SugarColumn(Length=64)]public string ReceiptId{get;set;}="";
    [SugarColumn(Length=64)]public string LeaseId{get;set;}="";
    [SugarColumn(Length=128)]public string ResourceId{get;set;}="";
    [SugarColumn(Length=64)]public string TaskId{get;set;}="";
    [SugarColumn(Length=64)]public string StepId{get;set;}="";
    [SugarColumn(Length=32)]public string VehicleId{get;set;}="";
    [SugarColumn(Length=64)]public string MapId{get;set;}="";
    [SugarColumn(Length=64)]public string MapVersionId{get;set;}="";
    public DateTime AcquiredAt{get;set;}
    [SugarColumn(Length=64)]public string EvidenceHash{get;set;}="";
    public DateTime ReleasedAtUtc{get;set;}
}
