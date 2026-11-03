using PlcHandshakeLab;

internal static class CallPlanTests
{
    public static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"lab-call-plan-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        using var lab=new NetworkLab(NetworkLabConfig.CreateDefault(),Path.Combine(root,"session.jsonl"));
        bool Value(string station,int signal)=>lab.Snapshot(station).Single(p=>p.Mapping.Station==station&&p.Mapping.Signal==signal).Value;
        void Check(bool condition,string message){if(!condition)throw new Exception(message);}
        void H(string station,int signal,bool value)
        {var p=lab.Config.Points.Single(p=>p.Station==station&&p.Signal==signal);Check(lab.WriteHost(p.Db,p.Byte,p.Bit,[(byte)(value?1:0)],true,"test"),"H write");}
        var callIndex = 0;
        var id=Guid.NewGuid().ToString("N");
        void Reject(Action action) { try { action(); } catch(Exception e) when(e is InvalidOperationException or ArgumentException) { return; } throw new Exception("invalid feedback accepted"); }
        CycleTransportFeedback Feedback(string station, string action) => new(id,callIndex,station,"task-"+callIndex,"AGV-01",action+callIndex,"vendor-"+action+callIndex,action,DateTimeOffset.UtcNow);
        void Close(string station)
        {
            H(station,2,true);lab.TickAutomatic();Check(!Value(station,0),"ack clears call");
            Reject(()=>lab.ApplyTransportFeedback(Feedback(station,"Place")));
            Reject(()=>lab.ApplyTransportFeedback(Feedback(station,"Pickup") with {RunId=Guid.NewGuid().ToString("N")}));
            Reject(()=>lab.ApplyTransportFeedback(Feedback(station,"Pickup") with {CallIndex=callIndex+1}));
            Reject(()=>lab.ApplyTransportFeedback(Feedback(station,"Pickup") with {ObservedAt=DateTimeOffset.UtcNow.AddSeconds(-6)}));
            lab.TickAutomatic();Check(Value(station,1),"ack alone never invents a pickup");
            var pickup=Feedback(station,"Pickup");lab.ApplyTransportFeedback(pickup);lab.ApplyTransportFeedback(pickup);
            Check(!Value(station,1),"original pickup proof breaks P/H mutual wait before removed notification");
            H(station,3,true);H(station,4,true);lab.TickAutomatic();
            Reject(()=>lab.ApplyTransportFeedback(Feedback(station,"Place") with {TaskId="another-task"}));
            Check(!Value(station,1),"return responsibility does not invent box presence");
            var place=Feedback(station,"Place");lab.ApplyTransportFeedback(place);lab.ApplyTransportFeedback(place);
            H(station,5,true);lab.TickAutomatic();Check(Value(station,1),"original place proof simulates empty box returned");
            foreach(var s in new[]{2,3,4,5})H(station,s,false);
            lab.TickAutomatic();callIndex++;
        }
        lab.ConfigureCalls(id,["CNC-001","CNC-002"],2,false);
        lab.TickAutomatic();Check(Value("CNC-001",0)&&!Value("CNC-002",0),"A first");
        for(var i=0;i<3;i++)lab.TickAutomatic();Check(!Value("CNC-002",0),"does not advance before handshake closure");
        Close("CNC-001");lab.TickAutomatic();Check(Value("CNC-002",0),"B only after A closure");
        Close("CNC-002");lab.TickAutomatic();Check(Value("CNC-001",0),"second round starts A");
        Close("CNC-001");lab.TickAutomatic();Check(Value("CNC-002",0),"second round B");
        Close("CNC-002");lab.TickAutomatic();Check(!Value("CNC-001",0)&&lab.CallPlanStatus.Contains("4/4"),"finite rounds stop calls");
        lab.ConfigureCalls(id,["CNC-001","CNC-002"],2,false);lab.TickAutomatic();Check(!Value("CNC-001",0),"start replay cannot restart completed calls");
        var next=Guid.NewGuid().ToString("N");lab.ConfigureCalls(next,["CNC-002","CNC-001"],3,false);
        id=next;callIndex=0;
        lab.TickAutomatic();Check(Value("CNC-002",0),"reverse order");
        lab.ConfigureCalls(next,["CNC-002","CNC-001"],3,true);
        Check(Value("CNC-002",0),"stop retains raised call");Close("CNC-002");lab.TickAutomatic();
        Check(!Value("CNC-001",0)&&lab.CallPlanStatus.Contains("1/6"),"stop prevents next call after draining");
        lab.ConfigureCalls(next,["CNC-002","CNC-001"],3,false);lab.TickAutomatic();
        Check(!Value("CNC-001",0),"ordinary start replay cannot undo the charging pause");
        Reject(()=>lab.ConfigureCalls(Guid.NewGuid().ToString("N"),["CNC-002","CNC-001"],3,false,true));
        Reject(()=>lab.ConfigureCalls(next,["CNC-001","CNC-002"],3,false,true));
        H("CNC-001",2,true);
        Reject(()=>lab.ConfigureCalls(next,["CNC-002","CNC-001"],3,false,true));
        H("CNC-001",2,false);
        lab.ConfigureCalls(next,["CNC-002","CNC-001"],3,false,true);lab.TickAutomatic();
        Check(Value("CNC-001",0)&&!Value("CNC-002",0)&&lab.CallPlanStatus.Contains("1/6"),
            "explicit charging resume preserves the completed count and next configured station");
        lab.ConfigureCalls(next,["CNC-002","CNC-001"],3,true);
        lab.ConfigureCalls(next,["CNC-002","CNC-001"],3,false,true);
        lab.ConfigureCalls(next,["CNC-002","CNC-001"],3,false,true);lab.TickAutomatic();
        Check(Value("CNC-001",0)&&lab.CallPlanStatus.Contains("1/6"),"charging resume retry retains an already raised call without resetting progress");
        Close("CNC-001");lab.TickAutomatic();Check(Value("CNC-002",0)&&lab.CallPlanStatus.Contains("2/6"),"post-charge feedback retains the original run and call index");
        lab.ConfigureCalls(next,["CNC-002","CNC-001"],3,true);Close("CNC-002");lab.TickAutomatic();
        var interrupted=Guid.NewGuid().ToString("N");lab.ConfigureCalls(interrupted,["CNC-001","CNC-002"],2,false);lab.TickAutomatic();
        lab.UpdateCommunication(false);lab.UpdateCommunication(true);lab.TickAutomatic();
        Reject(()=>lab.ApplyTransportFeedback(new(interrupted,0,"CNC-001","t","v","s","vendor","Pickup",DateTimeOffset.UtcNow)));
        Reject(()=>lab.ConfigureCalls(interrupted,["CNC-001","CNC-002"],2,false,true));
        Check(Value("CNC-001",0)&&!Value("CNC-002",0)&&lab.AutomaticStatus.Contains("暂停"),"reconnection never resumes an interrupted call plan");
        using var manual = new NetworkLab(NetworkLabConfig.CreateDefault(), Path.Combine(root,"manual-direct.jsonl"));
        manual.SetAutomatic(true);
        manual.ManualSet("CNC-001",0,true);
        var manualAck=manual.Config.Points.Single(p=>p.Station=="CNC-001"&&p.Signal==2);
        Check(manual.WriteHost(manualAck.Db,manualAck.Byte,manualAck.Bit,[1],true,"test"),"manual call ack");
        manual.TickAutomatic();
        Check(!manual.Snapshot("CNC-001").Single(p=>p.Mapping.Signal==0).Value&&manual.Snapshot("CNC-001").Single(p=>p.Mapping.Signal==1).Value,
            "manual call clears acknowledgement without inventing box pickup");
        var manualRun=Guid.NewGuid().ToString("N");
        var manualPickup=new CycleTransportFeedback(manualRun,0,"CNC-001","manual-task","AGV-01","pickup-step","pickup-vendor","Pickup",DateTimeOffset.UtcNow);
        manual.ApplyTransportFeedback(manualPickup);manual.ApplyTransportFeedback(manualPickup);
        Check(!manual.Snapshot("CNC-001").Single(p=>p.Mapping.Signal==1).Value,
            "manual demand accepts idempotent pickup feedback without a machine-generated call plan");
        var emptyResponsibility=manual.Config.Points.Single(p=>p.Station=="CNC-001"&&p.Signal==4);
        Check(manual.WriteHost(emptyResponsibility.Db,emptyResponsibility.Byte,emptyResponsibility.Bit,[1],true,"test"),"return responsibility");
        var manualPlace=new CycleTransportFeedback(manualRun,0,"CNC-001","manual-task","AGV-01","place-step","place-vendor","Place",DateTimeOffset.UtcNow);
        manual.ApplyTransportFeedback(manualPlace);manual.ApplyTransportFeedback(manualPlace);
        Check(manual.Snapshot("CNC-001").Single(p=>p.Mapping.Signal==1).Value,
            "manual demand accepts idempotent place feedback after return responsibility");
        var configured = NetworkLabConfig.CreateDefault();
        var pattern = configured.Points.Where(p => p.Station == "CNC-001").Select(p => p.Copy()).ToArray();
        configured.Points.RemoveAll(p => p.Station != NetworkLabConfig.Shared);
        var names = Enumerable.Range(1, 300).Select(i => $"WORKCELL-{i:000}").ToArray();
        for (var i = 0; i < names.Length; i++)
            foreach (var source in pattern) { var p = source.Copy(); p.Station = names[i]; p.Byte = i; configured.Points.Add(p); }
        configured.Validate();
        using var generic = new NetworkLab(configured, Path.Combine(root,"generic.jsonl"));
        generic.ConfigureCalls(Guid.NewGuid().ToString("N"), names.Reverse().ToArray(), 2, false);
        generic.TickAutomatic();
        Check(generic.Snapshot(names[^1]).Single(p => p.Mapping.Station == names[^1] && p.Mapping.Signal == 0).Value,
            "300 configured stations accept arbitrary identifiers and reverse order");
        using var unknown = new NetworkLab(configured, Path.Combine(root,"unknown.jsonl"));
        Reject(() => unknown.ConfigureCalls(Guid.NewGuid().ToString("N"), ["UNCONFIGURED"], 1, false));
        Console.WriteLine("CallPlanTests passed: proof-before-H pickup, action-bound box feedback, stale/wrong identity rejection, alternating two rounds, idempotency, stop drains, reconnect pauses.");
    }
}
