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
        var interrupted=Guid.NewGuid().ToString("N");lab.ConfigureCalls(interrupted,["CNC-001","CNC-002"],2,false);lab.TickAutomatic();
        lab.UpdateCommunication(false);lab.UpdateCommunication(true);lab.TickAutomatic();
        Reject(()=>lab.ApplyTransportFeedback(new(interrupted,0,"CNC-001","t","v","s","vendor","Pickup",DateTimeOffset.UtcNow)));
        Check(Value("CNC-001",0)&&!Value("CNC-002",0)&&lab.AutomaticStatus.Contains("暂停"),"reconnection never resumes an interrupted call plan");
        Console.WriteLine("CallPlanTests passed: proof-before-H pickup, action-bound box feedback, stale/wrong identity rejection, alternating two rounds, idempotency, stop drains, reconnect pauses.");
    }
}
