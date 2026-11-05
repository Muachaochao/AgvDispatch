export type Section = 'stations'|'plcs'|'signals'|'vehicles'|'pools'|'templates'|'dumpers'|'standbys'|'nodes'|'edges'|'resources';
export type EngineeringRow = Record<string,string|number|boolean|string[]>;
export interface EngineeringPlan {schemaVersion:number;name:string;mapId:string;mapVersion:string;nodes:EngineeringRow[];edges:EngineeringRow[];resources:EngineeringRow[];plcs:EngineeringRow[];signals:EngineeringRow[];vehicles:EngineeringRow[];pools:EngineeringRow[];stations:EngineeringRow[];templates:EngineeringRow[];dumpers:EngineeringRow[];standbys:EngineeringRow[]}
export interface Field {key:string;label:string;type?:'number'|'boolean'|'list';options?:string[];ref?:Section;help?:string}
export interface EngineeringTemplatePort {key:string;label:string;type:'string'|'string[]'|'bool';required:boolean;source:string}
export interface EngineeringTemplateVariable {key:string;label:string;type:'string'|'string[]'|'bool'|'int'|'double';producer:string}
export interface EngineeringTemplateNode {id:string;block:string;name:string;inputs:string[];outputs:string[];condition:string;failurePolicy:string;timeoutAction:string;timeoutSeconds:number;autoRetry:boolean}
export interface EngineeringTemplateConnection {fromNodeId:string;output:string;toNodeId:string;input:string;type:string;external:boolean}
export interface EngineeringTemplateGuard {id:string;trigger:string;action:string;targetNodeId:string;description:string}
export interface EngineeringTemplateDefinition {
 id:string;name:string;capability:string;version:number;engine:string;pickupSeconds:number;dumpSeconds:number;returnSeconds:number;
 inputs:EngineeringTemplatePort[];outputs:EngineeringTemplatePort[];variables:EngineeringTemplateVariable[];
 nodes:EngineeringTemplateNode[];connections:EngineeringTemplateConnection[];guards:EngineeringTemplateGuard[];
 sourceTemplateId?:string;sourceVersion?:number;
}
export interface EngineeringTemplateLibraryEntry {key:string;template:EngineeringTemplateDefinition;contentHash:string;savedAt:string}
export interface EngineeringTaskPreviewInput {key:string;label:string;type:string;value:string|null;source:string;required:boolean}
export interface EngineeringTaskPreviewNode {sequence:number;nodeId:string;stepType:string;name:string;sourceSite:string;destinationSite:string;vehicleAction:string;plcCondition:string;inputs:string[];outputs:string[];failurePolicy:string;timeoutAction:string;timeoutSeconds:number;autoRetry:boolean}
export interface EngineeringTaskPreview {
 templateId:string;templateVersion:number;templateHash:string;engine:string;stationId:string;stationName:string;pickupSite:string;
 vehiclePoolId:string;vehicleIds:string[];waitingSites:string[];dumpingSite:string;ready:boolean;
 inputs:EngineeringTaskPreviewInput[];outputs:EngineeringTaskPreviewInput[];nodes:EngineeringTaskPreviewNode[];
 connections:EngineeringTemplateConnection[];guards:EngineeringTemplateGuard[];
 issues:{severity:string;section:string;objectId:string;field:string;message:string}[];
}
export const durableNineStepEngine='DurableNineStepV1';
export function createStandardTemplate(id:string,name:string,capability='Jack',version=1):EngineeringTemplateDefinition{
 const template:EngineeringTemplateDefinition={
  id,name,capability,version,engine:durableNineStepEngine,pickupSeconds:5,dumpSeconds:10,returnSeconds:5,
  inputs:[
   {key:'fromStation',label:'取放箱站点',type:'string',required:true,source:'station.pickupNode'},
   {key:'waitingSite',label:'候车站点列表',type:'string[]',required:true,source:'dumper.waitingNodes'},
   {key:'toStation',label:'倒料站点',type:'string',required:true,source:'dumper.dumpingNode'},
   {key:'vehiclePool',label:'车辆池',type:'string',required:true,source:'station.poolId'},
   {key:'vehicleIds',label:'车辆池成员',type:'string[]',required:true,source:'pool.vehicleIds'},
   {key:'dependsOnTaskIds',label:'前置任务',type:'string[]',required:false,source:'request.dependsOnTaskIds'}
  ],
  outputs:[
   {key:'vehicleId',label:'执行车辆',type:'string',required:false,source:'SelectAgv.vehicleId'},
   {key:'arrival',label:'取货到点',type:'bool',required:false,source:'GoToPickup.arrival'},
   {key:'pickConfirmed',label:'取货确认',type:'bool',required:false,source:'PickupFullBox.pickConfirmed'},
   {key:'dumpConfirmed',label:'倒料确认',type:'bool',required:false,source:'DumpFullBox.dumpConfirmed'},
   {key:'taskCompleted',label:'任务完成',type:'bool',required:false,source:'CompletePlcHandshake.taskCompleted'},
   {key:'error',label:'失败原因',type:'string',required:false,source:'any.error'}
  ],
  variables:[
   {key:'fromStation',label:'取放箱站点',type:'string',producer:'external'},
   {key:'waitingSite',label:'候车站点列表',type:'string[]',producer:'external'},
   {key:'toStation',label:'倒料站点',type:'string',producer:'external'},
   {key:'vehiclePool',label:'车辆池',type:'string',producer:'external'},
   {key:'vehicleIds',label:'车辆池成员',type:'string[]',producer:'external'},
   {key:'dependsOnTaskIds',label:'前置任务',type:'string[]',producer:'external'},
   {key:'vehicleId',label:'执行车辆',type:'string',producer:'select-agv'},
   {key:'arrival',label:'取货到点',type:'bool',producer:'go-to-pickup'},
   {key:'pickConfirmed',label:'取货确认',type:'bool',producer:'pickup-full-box'},
   {key:'waitingArrival',label:'候车到点',type:'bool',producer:'deliver-to-waiting'},
   {key:'dumperReady',label:'倒料机就绪',type:'bool',producer:'wait-dumper-ready'},
   {key:'dumpingArrival',label:'倒料位到点',type:'bool',producer:'enter-dumping'},
   {key:'dumpConfirmed',label:'倒料确认',type:'bool',producer:'dump-full-box'},
   {key:'returned',label:'返箱到点',type:'bool',producer:'return-empty-box'},
   {key:'placeConfirmed',label:'空箱放置确认',type:'bool',producer:'place-empty-box'},
   {key:'taskCompleted',label:'任务完成',type:'bool',producer:'complete-handshake'},
   {key:'error',label:'失败原因',type:'string',producer:'any'}
  ],
  nodes:[
   {id:'select-agv',block:'SelectAgv',name:'选择车辆',inputs:['vehiclePool','vehicleIds'],outputs:['vehicleId'],condition:'车辆在线、定位新鲜、能力匹配，并存在到取货站点的可执行路线',failurePolicy:'HoldUntilCondition',timeoutAction:'NotifyAndHold',timeoutSeconds:0,autoRetry:false},
   {id:'acquire-traffic',block:'AcquireTraffic',name:'取得首段路权',inputs:['vehicleId','fromStation'],outputs:[],condition:'通过现有交通层申请整组路线资源；本适配节点不发送车辆命令',failurePolicy:'HoldUntilCondition',timeoutAction:'NotifyAndHold',timeoutSeconds:0,autoRetry:false},
   {id:'go-to-pickup',block:'GoToPickup',name:'移动至取货点',inputs:['vehicleId','fromStation'],outputs:['arrival'],condition:'持有效交通路权；车辆到点且厂家导航任务完成',failurePolicy:'ManualRecovery',timeoutAction:'ManualRecovery',timeoutSeconds:0,autoRetry:false},
   {id:'pickup-full-box',block:'PickupFullBox',name:'取满箱',inputs:['arrival','fromStation'],outputs:['pickConfirmed'],condition:'PLC质量Good；BoxPresent=false；FullBoxRemoved=true；EmptyBoxTaskAcknowledged=true',failurePolicy:'ManualRecovery',timeoutAction:'ManualRecovery',timeoutSeconds:0,autoRetry:false},
   {id:'deliver-to-waiting',block:'DeliverFullBoxToDumper',name:'送满箱至候车点',inputs:['pickConfirmed','fromStation','waitingSite'],outputs:['waitingArrival'],condition:'候车位按任务持久分配；取得路权后导航完成',failurePolicy:'ManualRecovery',timeoutAction:'ManualRecovery',timeoutSeconds:0,autoRetry:false},
   {id:'wait-dumper-ready',block:'WaitForDumperReady',name:'等待倒料机就绪',inputs:['waitingArrival','waitingSite'],outputs:['dumperReady'],condition:'Faulted=false；AgvAtWaitingPosition=true；DumperReady=true，全部要求Good质量',failurePolicy:'HoldUntilCondition',timeoutAction:'NotifyAndHold',timeoutSeconds:0,autoRetry:false},
   {id:'enter-dumping',block:'EnterDumpingPosition',name:'进入倒料位',inputs:['dumperReady','waitingSite','toStation'],outputs:['dumpingArrival'],condition:'倒料设备独占责任已交接；进入倒料位导航完成',failurePolicy:'ManualRecovery',timeoutAction:'ManualRecovery',timeoutSeconds:0,autoRetry:false},
   {id:'dump-full-box',block:'DumpFullBox',name:'等待倒料完成',inputs:['dumpingArrival','toStation'],outputs:['dumpConfirmed'],condition:'Faulted=false；AgvAtDumpingPosition=true；DumpingCompletedMayLeave=true',failurePolicy:'HoldUntilCondition',timeoutAction:'NotifyAndHold',timeoutSeconds:0,autoRetry:false},
   {id:'return-empty-box',block:'ReturnEmptyBox',name:'返空箱至取放点',inputs:['dumpConfirmed','toStation','fromStation'],outputs:['returned'],condition:'实际出区核验通过后释放已验证区域，保留设备与返箱责任',failurePolicy:'ManualRecovery',timeoutAction:'ManualRecovery',timeoutSeconds:0,autoRetry:false},
   {id:'place-empty-box',block:'PlaceEmptyBox',name:'放置空箱',inputs:['returned','fromStation'],outputs:['placeConfirmed'],condition:'EmptyBoxPlaced=true；BoxPresent=true，全部要求Good质量',failurePolicy:'ManualRecovery',timeoutAction:'ManualRecovery',timeoutSeconds:0,autoRetry:false},
   {id:'complete-handshake',block:'CompletePlcHandshake',name:'结束PLC握手',inputs:['placeConfirmed'],outputs:['taskCompleted'],condition:'按28阶段顺序复位工位PLC信号；只有任务、路权和证据闭合后完成',failurePolicy:'ManualRecovery',timeoutAction:'ManualRecovery',timeoutSeconds:0,autoRetry:false},
   {id:'release-traffic',block:'ReleaseTraffic',name:'释放本任务路权',inputs:['taskCompleted'],outputs:[],condition:'只释放现有交通生命周期已证明可释放的资源；设备、PLC周期和返箱责任保持',failurePolicy:'ManualRecovery',timeoutAction:'ManualRecovery',timeoutSeconds:0,autoRetry:false}
  ],
  connections:[],
  guards:[
   {id:'guard-vehicle',trigger:'VehicleUnavailable',action:'HoldUntilAvailable',targetNodeId:'select-agv',description:'无可用车辆时保留任务，等待车辆恢复，不换单或跳步。'},
   {id:'guard-route',trigger:'RouteUnavailable',action:'HoldAndReplan',targetNodeId:'go-to-pickup',description:'路线不可达时保留任务与责任，等待道路或地图条件恢复。'},
   {id:'guard-plc',trigger:'PlcSignalNotReady',action:'HoldUntilCondition',targetNodeId:'wait-dumper-ready',description:'PLC未满足条件时保持原步骤，不提前进入下一节点。'},
   {id:'guard-traffic',trigger:'TrafficResourceOccupied',action:'HoldForResource',targetNodeId:'deliver-to-waiting',description:'路权或候车位被占用时保留原任务并等待资源释放。'},
   {id:'guard-handshake',trigger:'HandshakeFaulted',action:'EnterManualRecovery',targetNodeId:'complete-handshake',description:'握手异常时保留设备和任务责任，进入人工恢复核对。'}
  ]
 };
 template.connections=buildTemplateConnections(template.nodes,template.variables);
 return template;
}
export function normalizeTemplate(row:EngineeringRow|EngineeringTemplateDefinition):EngineeringTemplateDefinition{
 const r=row as Partial<EngineeringTemplateDefinition>,fallback=createStandardTemplate(String(r.id??''),String(r.name??r.id??''),String(r.capability??'Jack'),Number(r.version??1));
 const variables=Array.isArray(r.variables)&&r.variables.length?r.variables as EngineeringTemplateVariable[]:fallback.variables;
 const sourceNodes=Array.isArray(r.nodes)&&r.nodes.length?r.nodes as EngineeringTemplateNode[]:fallback.nodes;
 const legacyBlocks=['SelectAgv','GoToPickup','PickupFullBox','DeliverFullBoxToDumper','WaitForDumperReady','EnterDumpingPosition','DumpFullBox','ReturnEmptyBox','PlaceEmptyBox','CompletePlcHandshake'];
 const withAdapters=sourceNodes.length===legacyBlocks.length&&sourceNodes.every((node,index)=>node.block===legacyBlocks[index])
  ?[...sourceNodes.slice(0,1),fallback.nodes.find(node=>node.block==='AcquireTraffic')!,...sourceNodes.slice(1),fallback.nodes.find(node=>node.block==='ReleaseTraffic')!]
  :sourceNodes;
 const nodes=withAdapters.map(node=>{
  const base=fallback.nodes.find(item=>item.block===node.block)??node;
  return {...node,failurePolicy:String(node.failurePolicy??base.failurePolicy),timeoutAction:String(node.timeoutAction??base.timeoutAction),timeoutSeconds:Number(node.timeoutSeconds??base.timeoutSeconds),autoRetry:node.autoRetry??base.autoRetry};
 });
 return {
  id:String(r.id??''),name:String(r.name??r.id??''),capability:String(r.capability??'Jack'),version:Number(r.version??1),
  engine:String(r.engine??durableNineStepEngine),pickupSeconds:Number(r.pickupSeconds??5),dumpSeconds:Number(r.dumpSeconds??10),returnSeconds:Number(r.returnSeconds??5),
  inputs:Array.isArray(r.inputs)&&r.inputs.length?r.inputs as EngineeringTemplatePort[]:fallback.inputs,
  outputs:Array.isArray(r.outputs)&&r.outputs.length?r.outputs as EngineeringTemplatePort[]:fallback.outputs,
  variables,nodes,connections:buildTemplateConnections(nodes,variables),
  guards:Array.isArray(r.guards)&&r.guards.length?r.guards as EngineeringTemplateGuard[]:fallback.guards,
  sourceTemplateId:r.sourceTemplateId??'',sourceVersion:r.sourceVersion??0
 };
}
export function buildTemplateConnections(nodes:EngineeringTemplateNode[],variables:EngineeringTemplateVariable[]):EngineeringTemplateConnection[]{
 const producers=new Map(variables.map(item=>[item.key,item])),connections:EngineeringTemplateConnection[]=[];
 for(const node of nodes)for(const input of node.inputs){
  const variable=producers.get(input);if(!variable)continue;
  connections.push({fromNodeId:variable.producer,output:variable.key,toNodeId:node.id,input,type:variable.type,external:variable.producer==='external'||variable.producer==='any'});
 }
 return connections;
}
export function templateExport(template:EngineeringRow|EngineeringTemplateDefinition){return{schemaVersion:1,template:normalizeTemplate(template),contentHash:''};}
export const sections:Record<Section,{label:string;description:string;fields:Field[]}>={
 stations:{label:'工位',description:'工位与业务节点、PLC、任务模板、车辆池及倒料设备分别绑定。新增工位无需修改工位编号枚举。',fields:[{key:'id',label:'工位编号'},{key:'name',label:'名称'},{key:'area',label:'车间／区域'},{key:'pickupNode',label:'取放箱业务节点',ref:'nodes'},{key:'plcId',label:'PLC',ref:'plcs'},{key:'templateId',label:'业务模板',ref:'templates'},{key:'poolId',label:'车辆池',ref:'pools'},{key:'dumperId',label:'倒料设备',ref:'dumpers'},{key:'priority',label:'优先级（0–100）',type:'number'},{key:'enabled',label:'启用',type:'boolean'}]},
 plcs:{label:'PLC连接',description:'本工程连接参数只用于规划存档。模拟不会连接这些地址；实际后台连接请进入现场通信配置。',fields:[{key:'id',label:'PLC编号'},{key:'host',label:'主机地址'},{key:'port',label:'端口',type:'number'},{key:'enabled',label:'启用',type:'boolean'}]},
 signals:{label:'PLC点表',description:'标准取放箱模板使用8个工位信号，倒料设备使用5个信号。逐位检查地址重复、读写方向和缺项；不是PLC程序的电气验收。',fields:[{key:'id',label:'点位编号'},{key:'plcId',label:'所属PLC',ref:'plcs'},{key:'ownerId',label:'所属工位／倒料设备'},{key:'name',label:'信号含义',options:['FullBoxRequested','BoxPresent','Faulted','CommunicationOnline','FullBoxRequestAcknowledged','FullBoxRemoved','EmptyBoxTaskAcknowledged','EmptyBoxPlaced','DumperReady','DumpingCompletedMayLeave','AgvAtWaitingPosition','AgvAtDumpingPosition']},{key:'address',label:'地址，例如DB1.DBX2.0'},{key:'direction',label:'方向',options:['Read','Write']},{key:'dataType',label:'类型',options:['Bool']}]},
 vehicles:{label:'车辆与包络',description:'车辆能力决定可接任务；包络与误差保存依据。示例数值只用于模拟，不会标记为现场已核定。',fields:[{key:'id',label:'车辆编号'},{key:'name',label:'车辆名称'},{key:'host',label:'主机地址'},{key:'startNode',label:'模拟起点',ref:'nodes'},{key:'capability',label:'能力',options:['Jack','Fork']},{key:'radius',label:'包络半径（m）',type:'number'},{key:'localizationError',label:'定位误差上限（m）',type:'number'},{key:'envelopeEvidence',label:'包络依据／适用范围'},{key:'battery',label:'模拟电量（%）',type:'number'},{key:'minimumBattery',label:'派车最低电量（%）',type:'number'},{key:'enabled',label:'启用',type:'boolean'}]},
 pools:{label:'车辆池',description:'工位绑定车辆池，池中车辆按能力、电量、路线和预约空档选择。同一车辆在不同池中仍共用一个忙闲时间线。',fields:[{key:'id',label:'车辆池编号'},{key:'name',label:'名称'},{key:'vehicleIds',label:'车辆编号列表',type:'list',help:'以分号分隔，例如 AGV-01;AGV-02'}]},
 templates:{label:'任务模板',description:'版本化保存输入、输出和九步受控节点；编译目标仍是现有持久化执行器。',fields:[{key:'id',label:'模板编号'},{key:'name',label:'名称'},{key:'capability',label:'车辆所需能力',options:['Jack','Fork']},{key:'version',label:'版本',type:'number'},{key:'pickupSeconds',label:'模拟取箱耗时（秒）',type:'number'},{key:'dumpSeconds',label:'模拟倒料耗时（秒）',type:'number'},{key:'returnSeconds',label:'模拟放箱耗时（秒）',type:'number'}]},
 dumpers:{label:'倒料设备',description:'每台设备绑定一个主等待位、唯一倒料位和容量资源；可增加独立附加等待位，多车排队时不会共用倒料位。展示区域本身不代表资源已接入。',fields:[{key:'id',label:'设备编号'},{key:'name',label:'名称'},{key:'plcId',label:'PLC',ref:'plcs'},{key:'waitingNode',label:'主倒料等待节点',ref:'nodes'},{key:'dumpingNode',label:'倒料节点',ref:'nodes'},{key:'waitingResource',label:'主等待位容量资源',ref:'resources'},{key:'dumpingResource',label:'倒料区容量资源',ref:'resources'},{key:'additionalWaitingNodes',label:'附加独立候车节点',type:'list',ref:'nodes',help:'以分号分隔；节点和资源按下标一一对应，每项必须使用容量1且不与倒料位共用的独立资源'},{key:'additionalWaitingResources',label:'附加候车容量资源',type:'list',ref:'resources',help:'例如 WAIT-LM26；项目数与附加独立候车节点一致'}]},
 standbys:{label:'待机位',description:'待机点按车辆池分配，按优先级和距离选择；检查总容量并避免重复分配物理位置。倒料等待位不能代替待机位。',fields:[{key:'id',label:'待机位编号'},{key:'nodeId',label:'业务节点',ref:'nodes'},{key:'poolId',label:'适用车辆池',ref:'pools'},{key:'resourceId',label:'容量资源',ref:'resources'},{key:'priority',label:'分配优先级',type:'number'}]},
 nodes:{label:'站点映射',description:'业务节点与厂家站点分开管理。坐标以米为单位；导入新地图后检查原有绑定，不根据相似名称自动确认。',fields:[{key:'id',label:'业务节点编号'},{key:'vendorSite',label:'厂家LM编号'},{key:'x',label:'X（m）',type:'number'},{key:'y',label:'Y（m）',type:'number'},{key:'enabled',label:'启用',type:'boolean'}]},
 edges:{label:'道路',description:'有向拓扑与距离用于模拟路线。圆弧等几何在地图编辑器维护；本页不重写原图几何。未绑定交通资源的道路不会自动互斥。',fields:[{key:'id',label:'道路编号'},{key:'from',label:'起点',ref:'nodes'},{key:'to',label:'终点',ref:'nodes'},{key:'distance',label:'道路长度（m）',type:'number'},{key:'speed',label:'模拟速度（m/s）',type:'number'},{key:'resourceIds',label:'交通资源列表',type:'list',help:'以分号分隔；窄道双向应引用同一个资源'},{key:'bidirectional',label:'双向',type:'boolean'},{key:'enabled',label:'启用',type:'boolean'}]},
 resources:{label:'交通资源',description:'这些资源参与模拟预约；地图场景色块只是展示。需要在道路、倒料或待机配置中明确引用资源。',fields:[{key:'id',label:'资源编号'},{key:'name',label:'名称'},{key:'kind',label:'类型',options:['Intersection','NarrowAisle','Station','Waiting','Dumper','Standby']},{key:'capacity',label:'并发容量',type:'number'}]}
};
export const newPlan=():EngineeringPlan=>({schemaVersion:1,name:'新工程',mapId:'',mapVersion:'',nodes:[],edges:[],resources:[],plcs:[],signals:[],vehicles:[],pools:[],stations:[],templates:[],dumpers:[],standbys:[]});
export function newRow(section:Section):EngineeringRow{return Object.fromEntries(sections[section].fields.map(f=>[f.key,f.type==='boolean'?true:f.type==='list'?[]:f.type==='number'?1:f.options?.[0]??'']));}
export function parseCsv(text:string):string[][]{
 const rows:string[][]=[];let row:string[]=[],cell='',quoted=false,closed=false;
 text=text.replace(/^\uFEFF/,'');
 for(let i=0;i<text.length;i++){
  const c=text[i]!;
  if(quoted){if(c==='"'){if(text[i+1]==='"'){cell+='"';i++;}else{quoted=false;closed=true;}}else cell+=c;continue;}
  if(c==='"'){if(cell!==''||closed)throw new Error('CSV引号位置无效');quoted=true;}
  else if(c===','||c==='\n'||c==='\r'){
   row.push(cell);cell='';closed=false;
   if(c!==','){if(c==='\r'&&text[i+1]==='\n')i++;if(row.some(v=>v!==''))rows.push(row);row=[];}
  }else{if(closed)throw new Error('CSV引号后有额外字符');cell+=c;}
 }
 if(quoted)throw new Error('CSV引号未闭合');if(cell!==''||row.length||closed){row.push(cell);rows.push(row);}return rows;
}
export function csvRows(section:Section,text:string):EngineeringRow[]{
 const data=parseCsv(text);const header=data.shift();if(!header?.length)throw new Error('CSV为空');
 const fields=sections[section].fields;if(new Set(header).size!==header.length||header.some(k=>!fields.some(f=>f.key===k))||fields.some(f=>!header.includes(f.key)))throw new Error('列名不符，请下载本页CSV模板后填写');
 if(data.length>12000)throw new Error('单次导入最多12000行');
 const ids=new Set<string>();return data.map((cells,i)=>{
  if(cells.length!==header.length)throw new Error(`第${i+2}行列数不符`);
  const row:EngineeringRow={};fields.forEach(f=>{
   const value=cells[header.indexOf(f.key)]!.trim();
   if(f.type==='number'){if(!value||!Number.isFinite(Number(value)))throw new Error(`第${i+2}行 ${f.label}不是有效数字`);row[f.key]=Number(value);}
   else if(f.type==='boolean'){if(!['true','false'].includes(value))throw new Error(`第${i+2}行 ${f.label}必须是true或false`);row[f.key]=value==='true';}
   else if(f.type==='list')row[f.key]=value.split(';').map(s=>s.trim()).filter(Boolean);
   else row[f.key]=value;
  });
  const id=String(row.id).toLowerCase();if(!id||ids.has(id))throw new Error(`第${i+2}行编号为空或重复`);ids.add(id);return row;
 });
}
export function exportCsv(section:Section,rows:EngineeringRow[]):string{
 const fields=sections[section].fields;const cell=(v:EngineeringRow[string])=>'"'+(Array.isArray(v)?v.join(';'):String(v)).replaceAll('"','""')+'"';
 return '\uFEFF'+[fields.map(f=>f.key).join(','),...rows.map(r=>fields.map(f=>cell(r[f.key]??'')).join(','))].join('\r\n');
}
export function importDiff(before:EngineeringRow[],incoming:EngineeringRow[]){
 const index=new Map(before.map(r=>[String(r.id).toLowerCase(),r]));let added=0,changed=0,unchanged=0;
 const canonical=(r:EngineeringRow)=>JSON.stringify(Object.entries(r).sort(([a],[b])=>a.localeCompare(b)));
 const changes=incoming.map(r=>{const old=index.get(String(r.id).toLowerCase());const status=!old?'新增':canonical(old)===canonical(r)?'未变':'修改';if(status==='新增')added++;else if(status==='修改')changed++;else unchanged++;return{id:String(r.id),status,before:old,after:r};});
 return{added,changed,unchanged,changes,merged:[...before.filter(r=>!incoming.some(x=>String(x.id).toLowerCase()===String(r.id).toLowerCase())),...incoming]};
}
