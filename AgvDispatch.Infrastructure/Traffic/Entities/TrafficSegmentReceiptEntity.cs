using SqlSugar;
namespace AgvDispatch.Infrastructure.Persistence.Entities;

[SugarTable("traffic_segment_receipt")]
public sealed class TrafficSegmentReceiptEntity
{
    [SugarColumn(IsPrimaryKey=true,Length=64)]public string ReceiptId{get;set;}="";
    [SugarColumn(Length=64)]public string SessionId{get;set;}="";
    [SugarColumn(Length=64)]public string TaskId{get;set;}="";
    [SugarColumn(Length=64)]public string StepId{get;set;}="";
    [SugarColumn(Length=32)]public string VehicleId{get;set;}="";
    [SugarColumn(Length=64)]public string CommandId{get;set;}="";
    [SugarColumn(Length=64)]public string RouteRevision{get;set;}="";
    [SugarColumn(ColumnDataType="TEXT")]public string BindingJson{get;set;}="";
    [SugarColumn(Length=64)]public string BindingHash{get;set;}="";
    public int CandidateCount{get;set;}
    public DateTime IssuedAtUtc{get;set;}
    public DateTime ExpiresAtUtc{get;set;}
}
[SugarTable("traffic_segment_receipt_event")]
public sealed class TrafficSegmentReceiptEventEntity
{
    [SugarColumn(IsPrimaryKey=true,Length=64)]public string EventId{get;set;}="";
    [SugarColumn(Length=64)]public string ReceiptId{get;set;}="";
    [SugarColumn(Length=32)]public string Kind{get;set;}="";
    [SugarColumn(Length=64)]public string BindingHash{get;set;}="";
    public DateTime OccurredAtUtc{get;set;}
}
