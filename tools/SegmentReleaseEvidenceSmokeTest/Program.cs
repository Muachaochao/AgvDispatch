using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Traffic;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;

var checks=0;var results=new List<object>();
void Check(bool condition,string name){if(!condition)throw new Exception(name);checks++;results.Add(new{Name=name,Passed=true});Console.WriteLine("PASS "+name);}
var now=new DateTimeOffset(2026,10,14,4,0,0,TimeSpan.Zero);
var boundary=new[]{new MapPoint2D(0,-1),new(1,-1),new(1,1),new(0,1)};
var nodes=new[]{new MapNode("A",0,0,MapNodeType.Waypoint,true,[]),new("B",2,0,MapNodeType.Waypoint,true,[]),new("C",4,0,MapNodeType.Waypoint,true,[]),new("D",6,0,MapNodeType.Station,true,["FINAL"])};
var edges=new[]{new MapEdge("AB","A","B",2,1,true,true,["R"]),new("BC","B","C",2,1,true,true,[]),new("CD","C","D",2,1,true,true,[])};
var map=new MapDefinition("test","","LocalCartesian","m",nodes,edges,[new("R",TrafficResourceType.NarrowAisle,1,boundary),new("FINAL",TrafficResourceType.Station,1,[new(5,-1),new(7,-1),new(7,1),new(5,1)])]);
var context=new TrafficSegmentReleaseContext("TASK","STEP","AGV","STEP","CMD","ROUTE-HASH","M","V1","ROBOT-FRAME",now.AddSeconds(-3),true,true,true,map,["A","B","C","D"],["AB","BC","CD"],[new("LEASE","R","TASK","STEP","AGV","M","V1"),new("FINAL-LEASE","FINAL","TASK","STEP","AGV","M","V1")],new("BODY","AGV","ROBOT-FRAME",0.5,true,true));
var first=new TrafficProgressFrame("TASK","STEP","AGV","STEP","CMD","ROUTE-HASH","M","V1","ROBOT-FRAME","EPOCH",10,0,3,0,0.95,0.05,SignalQuality.Good,true,true,now.AddSeconds(-2));
var pair=new TrafficProgressPair(first,first with{Sequence=11,ObservedAt=now.AddSeconds(-1)});var policy=new TrafficSegmentReleasePolicy{Enabled=true};
TrafficSegmentReleaseAssessment Evaluate(TrafficSegmentReleaseContext? c=null,TrafficProgressPair? p=null,TrafficSegmentReleasePolicy? o=null)=>TrafficSegmentReleaseEvaluator.Evaluate(c??context,p??pair,o??policy,now);
void Refuse(string name,TrafficSegmentReleaseContext? c=null,TrafficProgressPair? p=null,TrafficSegmentReleasePolicy? o=null){var result=Evaluate(c,p,o);Check(!result.Available||result.Candidates.All(x=>!x.EvidenceVerified),name);}
var valid=Evaluate();Check(valid.Available&&valid.Candidates.Single(c=>c.ResourceId=="R").EvidenceVerified,"两帧同车原运输经过与全车包络离开通过只读评估");
Check(!valid.Candidates.Single(c=>c.ResourceId=="FINAL").EvidenceVerified,"终点资源始终保留至正常交接");Check(!valid.ResourcesReleased,"证据通过不执行任何释放");
Refuse("默认开关关闭",o:new());Refuse("无注册进度源拒绝",c:context with{EvidenceSourceRegistered=false});Refuse("恢复门禁关闭拒绝",c:context with{RecoveryAndMapGateOpen=false});Refuse("任务或步骤非执行中拒绝",c:context with{TaskAndStepExecuting=false});
Refuse("缺车辆包络拒绝",c:context with{Body=null});Refuse("参考点未标定拒绝",c:context with{Body=context.Body! with{CalibrationVerified=false}});Refuse("最大载荷不在包络内拒绝",c:context with{Body=context.Body! with{IncludesMaximumPermittedLoad=false}});
Refuse("包络属于另一辆车拒绝",c:context with{Body=context.Body! with{VehicleId="OTHER"}});Refuse("包络坐标参考不一致拒绝",c:context with{Body=context.Body! with{CoordinateFrameId="OTHER"}});Refuse("非有限包络拒绝",c:context with{Body=context.Body! with{RadiusFromReportedReferenceMeters=double.NaN}});
foreach(var frame in new[]{pair.Second with{TaskId="OTHER"},pair.Second with{StepId="OTHER"},pair.Second with{VehicleId="OTHER"},pair.Second with{VendorTaskId="OTHER"},pair.Second with{CommandId="OTHER"},pair.Second with{RouteRevision="OTHER"},pair.Second with{MapId="OTHER"},pair.Second with{MapVersionId="V2"},pair.Second with{CoordinateFrameId="OTHER"}})
    Refuse("拒绝运输/路线/地图归属不符 "+JsonSerializer.Serialize(frame),p:pair with{Second=frame});
foreach(var frame in new[]{pair.Second with{Quality=SignalQuality.Bad},pair.Second with{Quality=SignalQuality.Stale},pair.Second with{Online=false},pair.Second with{Coherent=false},pair.Second with{ObservedAt=now.AddSeconds(1)},pair.Second with{ObservedAt=now.AddSeconds(-10)},pair.Second with{LocalizationConfidence=0.1},pair.Second with{X=double.NaN},pair.Second with{X=999},pair.Second with{PositionUncertaintyMeters=0.5}})
    Refuse("拒绝质量或位置不可用 "+frame.Quality+" "+frame.X+" "+frame.ObservedAt,p:pair with{Second=frame});
Refuse("发送意图前证据拒绝",c:context with{SendIntentAt=now.AddSeconds(-1)});Refuse("控制器复位拒绝",p:pair with{Second=pair.Second with{ControllerEpoch="RESTART"}});Refuse("空控制器周期拒绝",p:pair with{Second=pair.Second with{ControllerEpoch=""}});
Refuse("重复序号拒绝",p:pair with{Second=pair.Second with{Sequence=10}});Refuse("反向序号拒绝",p:pair with{Second=pair.Second with{Sequence=9}});Refuse("重复时刻拒绝",p:pair with{Second=pair.Second with{ObservedAt=first.ObservedAt}});
Refuse("观察间隔不足拒绝",p:pair with{Second=pair.Second with{ObservedAt=first.ObservedAt.AddMilliseconds(1)}});
Refuse("进度倒退拒绝",p:pair with{Second=pair.Second with{LastFullyTraversedEdgeIndex=-1,X=1}});Refuse("越界进度拒绝",p:pair with{Second=pair.Second with{LastFullyTraversedEdgeIndex=3}});
Refuse("两帧尚未完全通过资源拒绝",p:pair with{First=first with{LastFullyTraversedEdgeIndex=-1,X=1},Second=pair.Second with{LastFullyTraversedEdgeIndex=-1,X=1}});
Refuse("单点已离开但另一帧车体未离开拒绝",p:pair with{First=first with{X=2,Y=0.4}},c:context with{Body=context.Body! with{RadiusFromReportedReferenceMeters=1.0}});
Refuse("车体加裕量接触资源边界拒绝",c:context with{Body=context.Body! with{RadiusFromReportedReferenceMeters=1.8}});
Refuse("定位误差界扩大包络拒绝",c:context with{Body=context.Body! with{RadiusFromReportedReferenceMeters=1.7}},p:pair with{First=first with{PositionUncertaintyMeters=0.2},Second=pair.Second with{PositionUncertaintyMeters=0.2}});
var future=map with{Edges=[edges[0],edges[1],edges[2] with{ResourceIds=["r"]}]};Refuse("未来重入资源含大小写变体保持",c:context with{Map=future});
Refuse("路线断链拒绝",c:context with{RouteNodeIds=["A","C","B","D"]});Refuse("禁用路段拒绝",c:context with{Map=map with{Edges=[edges[0] with{Enabled=false},edges[1],edges[2]]}});Refuse("单向反向路线拒绝",c:context with{Map=map with{Edges=[edges[0] with{From="B",To="A",Bidirectional=false},edges[1],edges[2]]}});
Refuse("非米制地图拒绝",c:context with{Map=map with{Unit="mm"}});Refuse("非法曲线拒绝",c:context with{Map=map with{Edges=[edges[0] with{GeometryKind=MapEdgeGeometryKind.CircularArc},edges[1],edges[2]]}});
Refuse("重复租约拒绝",c:context with{Leases=[context.Leases[0],context.Leases[0]]});Refuse("旧地图租约拒绝",c:context with{Leases=[context.Leases[0] with{MapVersionId="OLD"}]});Refuse("租约归属另一车拒绝",c:context with{Leases=[context.Leases[0] with{VehicleId="OTHER"}]});
foreach(var polygon in new IReadOnlyList<MapPoint2D>?[]{null,[new(0,0),new(1,0)],[new(0,0),new(1,0),new(2,0)],[new(0,0),new(1,1),new(0,1),new(1,0)],[new(0,0),new(double.NaN,0),new(0,1)],[new(0,0),new(0,0),new(1,1)]})
    Refuse("无效或缺失资源边界拒绝",c:context with{Map=map with{Resources=[map.Resources[0] with{Boundary=polygon},map.Resources[1]]}});
Refuse("重复资源编号大小写拒绝",c:context with{Map=map with{Resources=[map.Resources[0],map.Resources[0] with{Id="r"},map.Resources[1]]}});
foreach(var bad in new[]{new TrafficSegmentReleasePolicy{Enabled=true,SafetyMarginMeters=0},new TrafficSegmentReleasePolicy{Enabled=true,MaximumAgeSeconds=double.NaN},new TrafficSegmentReleasePolicy{Enabled=true,MinimumConfidence=2}})Refuse("非法评估配置拒绝",o:bad);
var noEvidence=TrafficSegmentReleaseEvaluator.Evaluate(context,null,policy,now);Check(!noEvidence.Available,"没有两帧证据不能评估");
var output=Path.Combine(AppContext.BaseDirectory,"segment-evidence-result.json");File.WriteAllText(output,JsonSerializer.Serialize(new{Checks=checks,Scope="read-only evidence evaluator; no release action or firmware adapter",Results=results},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(output);Console.WriteLine($"PASS: {checks} segment release evidence checks. No resources released.");
