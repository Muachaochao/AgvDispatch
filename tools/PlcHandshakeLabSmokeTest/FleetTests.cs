using PlcHandshakeLab;
internal static class FleetTests
{
 public static void Run()
 {
  var checks=0;
  void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;}
  void Reject(Action action,string text){try{action();}catch(InvalidOperationException){checks++;return;}throw new Exception(text);}
  using var lab=new NetworkLab(NetworkLabConfig.CreateDefault(),Path.Combine(Path.GetTempPath(),"fleet-lab-"+Guid.NewGuid().ToString("N"),"session.jsonl"));
  var id=Guid.NewGuid().ToString("N");lab.UpdateCommunication(true);lab.ControlFleet(new(id,"start",[]));
  Host("CNC-001",1,false);Host("CNC-002",1,false);
  lab.ControlFleet(new(id,"call",["CNC-001","CNC-002"]));
  bool Value(string station,int signal)=>lab.Snapshot(station).Single(p=>p.Mapping.Station==station&&p.Mapping.Signal==signal).Value;
  void Host(string station,int signal,bool value){var p=lab.Config.Points.Single(p=>p.Station==station&&p.Signal==signal);lab.WriteHost(p.Db,p.Byte,p.Bit,[value?(byte)1:(byte)0],true,"fleet-test");}
  Check(Value("CNC-001",1)&&Value("CNC-002",1)&&Value("CNC-001",0)&&Value("CNC-002",0),"空箱场地由模拟器注入本轮箱体并建立双工位同批呼叫");
  lab.ControlFleet(new(id,"call",["CNC-001","CNC-002"]));
  Reject(()=>lab.ConfigureCalls(Guid.NewGuid().ToString("N"),["CNC-001"],1,false),"阻止同时运行单车循环");
  lab.ControlFleet(new(id,"bind",["CNC-001"],"T1"));lab.ControlFleet(new(id,"bind",["CNC-002"],"T2"));
  Reject(()=>lab.ControlFleet(new(id,"bind",["CNC-002"],"T1")),"不串任务");
  Host("CNC-001",2,true);Host("CNC-002",2,true);lab.TickAutomatic();
  Check(!Value("CNC-001",0)&&!Value("CNC-002",0),"两站各自撤呼叫");
  var f=new CycleTransportFeedback(id,0,"CNC-001","T1","AGV-01","P1","V1","Pickup",DateTimeOffset.UtcNow);
  Reject(()=>lab.ApplyFleetFeedback(f with {TaskId="T2"}),"另一工位任务不能写反馈");
  Reject(()=>lab.ApplyFleetFeedback(f with {ObservedAt=DateTimeOffset.UtcNow.AddSeconds(-10)}),"过期证据拒绝");
  lab.ApplyFleetFeedback(f);Check(!Value("CNC-001",1)&&Value("CNC-002",1),"仅更新原工位箱体");
  lab.ApplyFleetFeedback(f);Reject(()=>lab.ApplyFleetFeedback(f with {VehicleId="AGV-02"}),"重复反馈不能换车");
  var second=f with {Station="CNC-002",TaskId="T2",StepId="P2",VendorTaskId="V2"};
  Reject(()=>lab.ApplyFleetFeedback(second),"同车不能同时持有两工位返箱责任");
  lab.ApplyFleetFeedback(second with {VehicleId="AGV-02"});Check(!Value("CNC-002",1),"第二车独立反馈");
  lab.ControlFleet(new(id,"stop",[]));Host("CNC-001",4,true);
  lab.ApplyFleetFeedback(f with {StepId="R1",VendorTaskId="VR1",Action="Place",ObservedAt=DateTimeOffset.UtcNow});
  Check(Value("CNC-001",1)&&!Value("CNC-002",1),"停止新增仍允许原任务返箱反馈");
  lab.UpdateCommunication(false);
  Reject(()=>lab.ApplyFleetFeedback(second with {VehicleId="AGV-02",Action="Place"}),"通信失效拒绝另一车代推进");
  Console.WriteLine($"PASS {checks} 双工位模拟PLC竞争、身份和反馈检查。");
 }
}
