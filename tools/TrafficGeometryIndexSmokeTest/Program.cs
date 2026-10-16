using System.Diagnostics;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Traffic;
using AgvDispatch.Domain.Maps;

var checks=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
TrafficGeometryRoute Route(string car,List<MapNode> nodes,List<MapEdge> edges)=>new("T"+car,"S"+car,car,"M","V","LocalCartesian",
    nodes.Select(n=>n.Id).ToArray(),edges.Select(e=>e.Id).ToArray(),new("P",car,"LocalCartesian",.5,true,true),0);
(MapDefinition Map,TrafficGeometryRoute A,TrafficGeometryRoute B) Pair(int count,Func<int,(double X,double Y)> first,
    Func<int,(double X,double Y)> second,bool curves=false)
{
    var na=new List<MapNode>();var nb=new List<MapNode>();var ea=new List<MapEdge>();var eb=new List<MapEdge>();
    for(var i=0;i<=count;i++)
    {
        var p=first(i);var q=second(i);
        na.Add(new("A"+i,p.X,p.Y,MapNodeType.Waypoint,true,[]));nb.Add(new("B"+i,q.X,q.Y,MapNodeType.Waypoint,true,[]));
        if(i==0)continue;
        ea.Add(new("EA"+i,"A"+(i-1),"A"+i,1,1,true,true,[]));
        eb.Add(new("EB"+i,"B"+(i-1),"B"+i,1,1,true,true,[]));
        if(curves&&i%3==0)
        {
            ea[^1]=ea[^1] with{GeometryKind=MapEdgeGeometryKind.CubicBezier,ControlPoints=[new(p.X-12,p.Y+30),new(p.X+12,p.Y-30)]};
            eb[^1]=eb[^1] with{GeometryKind=MapEdgeGeometryKind.CubicBezier,ControlPoints=[new(q.X+30,q.Y-12),new(q.X-30,q.Y+12)]};
        }
    }
    return (new("index-test","","LocalCartesian","m",[..na,..nb],[..ea,..eb],[]),Route("1",na,ea),Route("2",nb,eb));
}
TrafficGeometryPreview Eval((MapDefinition Map,TrafficGeometryRoute A,TrafficGeometryRoute B) pair,bool indexed=true,
    int comparisons=1000000,int visits=2000000,int reports=1000)=>TrafficGeometryPreviewEvaluator.Evaluate(pair.Map,"M","V",pair.A,pair.B,
        new(){Enabled=true,UseSpatialIndex=indexed,MaximumEdgesPerRoute=10000,MaximumComparisons=comparisons,MaximumIndexVisits=visits,MaximumReportedConflicts=reports});
void Equal((MapDefinition Map,TrafficGeometryRoute A,TrafficGeometryRoute B) pair,string name)
{
    var naive=Eval(pair,false);var indexed=Eval(pair);
    Check(naive.Available&&indexed.Available&&naive.Complete==indexed.Complete&&
        naive.CanConcludeSeparated==indexed.CanConcludeSeparated&&naive.Conflicts.SequenceEqual(indexed.Conflicts),name);
}
Equal(Pair(25,i=>(i*10,0),i=>(i*10,1.3)),"切触与跨大范围边界不漏冲突，报告顺序一致");
Equal(Pair(25,i=>(i%2==0?-500:500,i*5),i=>(0,i*5)),"长路段横跨索引分区");
Equal(Pair(25,i=>(-100+i*4,-10),i=>(-100+i*4,10),true),"负坐标Bezier控制范围跨分区");
var arcPair=Pair(1,i=>(i*2,0),i=>(i*2,-1.5));
arcPair.Map=arcPair.Map with{Edges=[arcPair.Map.Edges[0] with{GeometryKind=MapEdgeGeometryKind.CircularArc,ControlPoints=[new(1,1)]},arcPair.Map.Edges[1]]};
Equal(arcPair,"圆弧全圆保守范围仍保留候选");
Equal(Pair(20,i=>(0,0),i=>(0,0)),"退化点与重复边界索引一致");
var random=new Random(431);
for(var trial=0;trial<30;trial++)
{
    // Fixed-seed oracle comparison of mixed curves and discontinuous directions, not timing assertions.
    var pa=Enumerable.Range(0,21).Select(_=>(X:random.NextDouble()*300-150,Y:random.NextDouble()*300-150)).ToArray();
    var pb=Enumerable.Range(0,21).Select(_=>(X:random.NextDouble()*300-150,Y:random.NextDouble()*300-150)).ToArray();
    Equal(Pair(20,i=>pa[i],i=>pb[i],trial%2==0),"固定种子混合路线与朴素模型一致 "+trial);
}
var sparse=Pair(5000,i=>(i*2,0),i=>(i*2,100));
var timer=Stopwatch.StartNew();var sparseResult=Eval(sparse);timer.Stop();
Check(sparseResult.CanConcludeSeparated&&sparseResult.Comparisons==0&&sparseResult.IndexVisits==5000,"五千段稀疏双路线排除两千五百万路段配对");
var sparseMs=timer.Elapsed.TotalMilliseconds;
var dense=Pair(1000,i=>(0,0),i=>(0,0));
var reportLimit=Eval(dense,reports:50);
Check(reportLimit.Available&&!reportLimit.Complete&&reportLimit.Conflicts.Count==50&&!reportLimit.CanConcludeSeparated,"千段密集路线达到报告上限不能判定分离");
var densePair=Pair(20,i=>(0,0),i=>(0,0));
var comparisonLimit=Eval(densePair,comparisons:10);
Check(!comparisonLimit.Complete&&comparisonLimit.Comparisons==10,"候选精检预算保持生效");
var indexLimit=Eval(sparse,visits:20);
Check(indexLimit.Available&&!indexLimit.Complete&&indexLimit.IndexVisits==20&&indexLimit.Conflicts.Count==0&&!indexLimit.CanConcludeSeparated,"索引预算耗尽即使无已报告冲突也不能判定分离");
var invalid=TrafficGeometryPreviewEvaluator.Evaluate(sparse.Map,"M","V",sparse.A,sparse.B,new(){Enabled=true,MaximumIndexVisits=0});
Check(!invalid.Available,"索引预算零拒绝");
using(var cts=new CancellationTokenSource())
{
    cts.Cancel();var cancelled=false;
    try{TrafficGeometryPreviewEvaluator.Evaluate(sparse.Map,"M","V",sparse.A,sparse.B,new(){Enabled=true,MaximumEdgesPerRoute=10000},cts.Token);}
    catch(OperationCanceledException){cancelled=true;}
    Check(cancelled,"索引流程取消仍传播");
}
Check(!sparseResult.ResourcesLocked&&!sparseResult.ResourcesReleased,"空间索引仍纯只读");
File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"geometry-index-result.json"),JsonSerializer.Serialize(new{Checks=checks,SparseEdgesPerRoute=5000,
    NaivePairs=25000000,sparseResult.Comparisons,sparseResult.IndexVisits,SparseMilliseconds=sparseMs,
    Scope="synthetic pure spatial preview; no hardware, live geometry locks or release"},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {checks} checks; sparse 5000x5000 {sparseMs:F2} ms.");
