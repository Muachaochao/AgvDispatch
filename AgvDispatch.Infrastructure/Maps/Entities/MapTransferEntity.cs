using SqlSugar;
namespace AgvDispatch.Infrastructure.Persistence.Entities;
[SugarTable("map_transfer_operation")]
public sealed class MapTransferEntity
{
    [SugarColumn(IsPrimaryKey=true)]public string OperationId{get;set;}="";
    public string RequestId{get;set;}="";
    public string IntentHash{get;set;}="";
    public string MapId{get;set;}="";public string VersionId{get;set;}="";public string VehicleId{get;set;}="";
    public string TargetMapName{get;set;}="";public string SourcePath{get;set;}="";public string SourceSha256{get;set;}="";
    public string DefinitionHash{get;set;}="";public string ExpectedDeviceMd5{get;set;}="";public string Firmware{get;set;}="";
    public string ActiveMapId{get;set;}="";public string ActiveVersionId{get;set;}="";public string ActiveDefinitionHash{get;set;}="";
    public string ConfirmationNote{get;set;}="";public bool Load{get;set;}
    public string OwnerSession{get;set;}="";public string Operator{get;set;}="";
    public string State{get;set;}="";public int AttemptCount{get;set;}
    [SugarColumn(ColumnDataType="TEXT")]public string LastObservation{get;set;}="";
    public DateTime CreatedAt{get;set;}public DateTime UpdatedAt{get;set;}
}
[SugarTable("map_transfer_event")]
public sealed class MapTransferEventEntity
{
    [SugarColumn(IsPrimaryKey=true)]public string Id{get;set;}="";
    public string OperationId{get;set;}="";public string Kind{get;set;}="";public string SessionId{get;set;}="";
    public string Operator{get;set;}="";
    [SugarColumn(ColumnDataType="TEXT")]public string Evidence{get;set;}="";
    public DateTime OccurredAt{get;set;}
}
