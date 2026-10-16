using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;

var root=Path.GetFullPath(args.Length>0?args[0]:"docs/examples/robokit-minimal");
var map=new MapImportService().Import(new MapImportRequest(Path.Combine(root,"minimal-map.json"),MapImportFormat.NormalizedJson,1));
var validation=new MapValidationService().Validate(map);
foreach(var issue in validation.Issues)Console.WriteLine($"{issue.Severity}: {issue.Code}: {issue.Message}");
var checks=0;void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
Check(validation.IsValid,"示例标准JSON实际导入及地图校验通过");
Check(map.Nodes.Count==4&&map.Edges.Count==4&&map.Resources.Count==1,"四节点四路段一共享资源");
var graph=map.ToGraph();
var pairs=new[]{("N_HOME","N_PICK","LM0001","LM0101"),("N_PICK","N_WAIT","LM0101","LM0201"),("N_WAIT","N_DUMP","LM0201","LM0202"),("N_DUMP","N_PICK","LM0202","LM0101")};
foreach(var (from,to,source,target) in pairs)
{
    var route=graph.FindShortestRoute(from,to,new HashSet<string>(),new Dictionary<string,double>());
    Check(route is not null&&route.EdgeIds.Count==1,from+"→"+to+" 可达且使用直接示例边");
    var mapped=MapRouteSiteMapper.ToAgvSitePlan(map,route!);
    Check(mapped.Nodes.SequenceEqual(new[]{source,target}),"逻辑路径映射为 "+source+"→"+target);
    Check(mapped.ResourceIds.Contains("R_DEMO"),"路径绑定教学互斥资源 "+from);
}
using var settings=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"backend-settings-fragment.json")));
var s=settings.RootElement;
Check(!s.GetProperty("Agv").GetProperty("Enabled").GetBoolean()&&!s.GetProperty("Agv").GetProperty("AllowCommands").GetBoolean()&&!s.GetProperty("Plc").GetProperty("Enabled").GetBoolean()&&!s.GetProperty("Plc").GetProperty("AllowWrites").GetBoolean()&&!s.GetProperty("Dispatching").GetProperty("Enabled").GetBoolean()&&!s.GetProperty("TaskCreation").GetProperty("Enabled").GetBoolean(),"配置教学片段不启动设备或调度");
Check(s.GetProperty("Dispatching").GetProperty("StationPickupSites").GetProperty("CNC001").GetString()=="LM0101","工位映射填写厂家模拟站点而非PLC地址");
foreach(var (_,_,source,target) in pairs)
{
    var path=s.GetProperty("Dispatching").GetProperty("Routes").GetProperty(source+"->"+target).EnumerateArray().Select(x=>x.GetString()).ToArray();
    Check(path.SequenceEqual(new[]{source,target}),"兼容显式路线示例与实际映射一致 "+source);
}
Check(s.GetProperty("Dispatching").GetProperty("StepActions").EnumerateObject().Count()==4,"四个运输动作配置完整");
File.WriteAllText(Path.Combine(root,"verification.json"),JsonSerializer.Serialize(new{Checks=checks,Scope="sample import and logical-to-simulated site mapping only; no hardware or current firmware API certification",Validation=validation},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {checks} example configuration checks.");
