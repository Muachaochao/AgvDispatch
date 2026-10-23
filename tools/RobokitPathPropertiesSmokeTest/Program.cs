using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.WebHost;
var checks=0;var names=new List<string>();void Check(bool pass,string name){if(!pass)throw new Exception(name);checks++;names.Add(name);Console.WriteLine("PASS "+name);}
var root=Path.Combine(Path.GetTempPath(),"AGV-vendor-path-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
var text="""
{"header":{"mapType":"2D-Map","mapName":"合成厂家路径属性验证","minPos":{"x":0,"y":0},"maxPos":{"x":4,"y":2},"resolution":0.02,"version":"1.0.6"},"advancedPointList":[{"className":"LocationMark","instanceName":"LM1","pos":{"x":0,"y":0}},{"className":"LocationMark","instanceName":"LM2","pos":{"x":4,"y":0}}],"advancedCurveList":[
{"className":"FuturePath","instanceName":"UNSUPPORTED","startPos":{"instanceName":"LM1"},"endPos":{"instanceName":"LM2"}},
{"className":"StraightPath","instanceName":"FORWARD","startPos":{"instanceName":"LM1"},"endPos":{"instanceName":"LM2"},"property":[{"key":"direction","type":"int","int32Value":1},{"key":"maxspeed","type":"double","doubleValue":0.3},{"key":"laser","type":"bool","boolValue":false},{"key":"StopAcc","type":"double","doubleValue":0},{"key":"vendorFuture","type":"custom","payload":{"value":"<script>raw</script>"}}]},
{"className":"StraightPath","instanceName":"REVERSE","startPos":{"instanceName":"LM2"},"endPos":{"instanceName":"LM1"},"property":[{"key":"direction","type":"int","int32Value":0},{"key":"maxspeed","type":"double","doubleValue":0.6},{"key":"reverseOnly","type":"string","stringValue":"reverse-only"}]},
{"className":"BezierPath","instanceName":"PARALLEL","controlPos1":{"x":1,"y":1},"controlPos2":{"x":3,"y":-1},"startPos":{"instanceName":"LM1"},"endPos":{"instanceName":"LM2"}}]}
""";
var bytes=Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();var path=Path.Combine(root,"source.smap");File.WriteAllBytes(path,bytes);var importer=new MapImportService();
var definition=importer.Import(new(path,AgvDispatch.Application.Abstractions.MapImportFormat.RobokitSmap));
Check(definition.Edges.Count==3&&definition.RobokitSource is {CurveCount:4,ConvertedCurveCount:3},"未转换曲线明确计数，三条定向/平行记录不合并");
var arcText=text.Replace("\"className\":\"FuturePath\",\"instanceName\":\"UNSUPPORTED\"","\"className\":\"ArcPath\",\"controlPos1\":{\"x\":2,\"y\":1},\"instanceName\":\"ARC\"");
var arcBytes=Encoding.UTF8.GetBytes(arcText);var arcDefinition=RobokitPathEvidenceReader.Attach(arcBytes,arcBytes,new Smap2DImportService().Read(arcBytes,"arc.smap").Definition);
Check(arcDefinition.Edges.Count==4&&arcDefinition.Edges[0].VendorPath is {ClassName:"ArcPath",CurveIndex:0,GeometryKind:MapEdgeGeometryKind.CircularArc,ControlPoints.Count:1}&&arcDefinition.Edges[1].VendorPath!.CurveIndex==1,"原生圆弧证据与新导入几何一致，不错配后续道路");
var forward=definition.Edges[0];var reverse=definition.Edges[1];var missing=definition.Edges[2];
Check(missing.VendorPath is {GeometryKind:MapEdgeGeometryKind.CubicBezier,ControlPoints.Count:2},"平行贝塞尔道路控制点与原始属性证据分别保留");
Check(forward.VendorPath!.CurveIndex==1&&forward.VendorPath.InstanceName=="FORWARD","原始曲线索引不与过滤后的调度索引混淆");
Check(forward.VendorPath.FromSite=="LM1"&&reverse.VendorPath!.FromSite=="LM2"&&reverse.VendorPath.ToSite=="LM1","正反向证据分别绑定厂家站点");
Check(forward.VendorPath.PropertyListPresent&&forward.VendorPath.RawPropertiesJson!.Contains("0.3")&&reverse.VendorPath!.RawPropertiesJson!.Contains("0.6"),"正反向不同速度各自保留，未互相复制");
Check(forward.VendorPath.RawPropertiesJson!.Contains("false")&&forward.VendorPath.RawPropertiesJson.Contains("\"doubleValue\":0"),"显式false和零原字节文本保持");
Check(forward.VendorPath.RawPropertiesJson.Contains("vendorFuture")&&forward.VendorPath.RawPropertiesJson.Contains("payload"),"未知类型与扩展原文不丢弃");
Check(!missing.VendorPath!.PropertyListPresent&&missing.VendorPath.RawPropertiesJson is null,"property不存在保持未设置，未补零/false");
Check(definition.Edges.All(e=>!e.Enabled&&e.SpeedLimit==1&&!e.Bidirectional),"厂家属性只读留存，不改默认停用/调度速度或双向");
Check(definition.RobokitSource!.Sha256==Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),"含BOM原文件SHA精确对应");
Check(forward.VendorPath.FromX==definition.Nodes.Single(n=>n.Id==forward.From).X,"绑定基线采用本次转换的调度坐标");
var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
var normalized=Path.Combine(root,"normalized.json");File.WriteAllText(normalized,JsonSerializer.Serialize(definition,options));var roundtrip=importer.Import(new(normalized,AgvDispatch.Application.Abstractions.MapImportFormat.NormalizedJson));
Check(JsonSerializer.Serialize(roundtrip.Edges.Select(e=>e.VendorPath),options)==JsonSerializer.Serialize(definition.Edges.Select(e=>e.VendorPath),options),"规范化JSON导出/再导入保留全部路径证据");
Check(roundtrip.RobokitSource==definition.RobokitSource,"整图来源跨JSON保存不丢失");
Check(MapVersionDiffer.Compare(definition,definition with{Edges=[forward with{VendorPath=forward.VendorPath with{RawPropertiesJson="[]"}},reverse,missing]}).Any(x=>x.ObjectType=="路段"),"厂家属性原文变化进入版本差异");
Check(MapVersionDiffer.Compare(definition,definition with{RobokitSource=definition.RobokitSource! with{CurveCount=5}}).Any(x=>x.ObjectType=="厂家来源"),"未转换曲线范围变化进入整图来源差异");
var restored=JsonSerializer.Deserialize<MapDefinition>(JsonSerializer.Serialize(definition,options),options)!;
Check(restored.Edges[0].VendorPath!.RawPropertiesJson==forward.VendorPath.RawPropertiesJson,"数据库定义JSON序列化/反序列化属性保持");
var old=definition with{RobokitSource=null,Edges=definition.Edges.Select(e=>e with{VendorPath=null}).ToArray()};var oldJson=JsonSerializer.Serialize(old,options);
Check(!oldJson.Contains("vendorPath")&&!oldJson.Contains("robokitSource"),"旧图空证据不增加序列化字段，避免无故改变原定义摘要");
var nullText=text.Replace("\"property\":[{\"key\":\"direction\",\"type\":\"int\",\"int32Value\":1},{\"key\":\"maxspeed\",\"type\":\"double\",\"doubleValue\":0.3},{\"key\":\"laser\",\"type\":\"bool\",\"boolValue\":false},{\"key\":\"StopAcc\",\"type\":\"double\",\"doubleValue\":0},{\"key\":\"vendorFuture\",\"type\":\"custom\",\"payload\":{\"value\":\"<script>raw</script>\"}}]", "\"property\":null");
var nullBytes=Encoding.UTF8.GetBytes(nullText);var nullResult=RobokitPathEvidenceReader.Attach(nullBytes,nullBytes,new Smap2DImportService().Read(nullBytes,"null.smap").Definition);
Check(nullResult.Edges[0].VendorPath is {PropertyListPresent:true,RawPropertiesJson:"null"},"显式null保持原状供未知告警，不当未设置");
var archive=new VendorMapSourceArchive(Path.Combine(root,"archive"));var sha=await archive.SaveAsync(bytes,default);Check((await archive.ReadAsync(sha,default))!.SequenceEqual(bytes),"原SMAP逐字节留存并可按SHA读回");
Check(await archive.SaveAsync(bytes,default)==sha&&Directory.GetFiles(Path.Combine(root,"archive")).Length==1,"同原图重复导入幂等留存，不覆盖不生成多份");
try{await archive.ReadAsync("../../outside",default);Check(false,"路径应拒绝");}catch(ArgumentException){Check(true,"原图下载只接受SHA而非客户端路径");}
File.WriteAllText(Path.Combine(root,"archive",sha+".smap"),"corrupt");try{await archive.ReadAsync(sha,default);Check(false,"损坏应拒绝");}catch(InvalidDataException){Check(true,"下载前检出留存原图损坏");}
try{await archive.SaveAsync(bytes,default);Check(false,"损坏不得覆盖");}catch(InvalidDataException){Check(true,"同名损坏原图不静默覆盖");}
Check(await archive.ReadAsync(new string('a',64),default) is null,"其他电脑仅有证据JSON而无原图时返回未留存");
var oversized=text.Replace("<script>raw</script>",new string('x',70000));var oversizedBytes=Encoding.UTF8.GetBytes(oversized);
try{RobokitPathEvidenceReader.Attach(oversizedBytes,oversizedBytes,new Smap2DImportService().Read(oversizedBytes,"big.smap").Definition);Check(false,"超限应拒绝");}catch(InvalidDataException){Check(true,"超限属性明确拒绝，不静默截断");}
File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"vendor-path-result.json"),JsonSerializer.Serialize(new{checks,root,scope="synthetic SMAP, normalized roundtrip and private original archive; no vehicle commands",results=names},options));
File.WriteAllBytes(Path.Combine(root,"synthetic-path-properties.smap"),bytes);File.WriteAllText(Path.Combine(root,"synthetic.agvmap.json"),JsonSerializer.Serialize(definition,options));Console.WriteLine($"PASS {checks} checks. Fixtures: {root}");
