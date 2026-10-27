export type Section = 'stations'|'plcs'|'signals'|'vehicles'|'pools'|'templates'|'dumpers'|'standbys'|'nodes'|'edges'|'resources';
export type EngineeringRow = Record<string,string|number|boolean|string[]>;
export interface EngineeringPlan {schemaVersion:number;name:string;mapId:string;mapVersion:string;nodes:EngineeringRow[];edges:EngineeringRow[];resources:EngineeringRow[];plcs:EngineeringRow[];signals:EngineeringRow[];vehicles:EngineeringRow[];pools:EngineeringRow[];stations:EngineeringRow[];templates:EngineeringRow[];dumpers:EngineeringRow[];standbys:EngineeringRow[]}
export interface Field {key:string;label:string;type?:'number'|'boolean'|'list';options?:string[];ref?:Section;help?:string}
export const sections:Record<Section,{label:string;description:string;fields:Field[]}>={
 stations:{label:'工位',description:'工位与业务节点、PLC、任务模板、车辆池及倒料设备分别绑定。新增工位无需修改工位编号枚举。',fields:[{key:'id',label:'工位编号'},{key:'name',label:'名称'},{key:'area',label:'车间／区域'},{key:'pickupNode',label:'取放箱业务节点',ref:'nodes'},{key:'plcId',label:'PLC',ref:'plcs'},{key:'templateId',label:'业务模板',ref:'templates'},{key:'poolId',label:'车辆池',ref:'pools'},{key:'dumperId',label:'倒料设备',ref:'dumpers'},{key:'priority',label:'优先级（0–100）',type:'number'},{key:'enabled',label:'启用',type:'boolean'}]},
 plcs:{label:'PLC连接',description:'本工程连接参数只用于规划存档。模拟不会连接这些地址；实际后台连接请进入现场通信配置。',fields:[{key:'id',label:'PLC编号'},{key:'host',label:'主机地址'},{key:'port',label:'端口',type:'number'},{key:'enabled',label:'启用',type:'boolean'}]},
 signals:{label:'PLC点表',description:'标准取放箱模板使用8个工位信号，倒料设备使用5个信号。逐位检查地址重复、读写方向和缺项；不是PLC程序的电气验收。',fields:[{key:'id',label:'点位编号'},{key:'plcId',label:'所属PLC',ref:'plcs'},{key:'ownerId',label:'所属工位／倒料设备'},{key:'name',label:'信号含义',options:['FullBoxRequested','BoxPresent','Faulted','CommunicationOnline','FullBoxRequestAcknowledged','FullBoxRemoved','EmptyBoxTaskAcknowledged','EmptyBoxPlaced','DumperReady','DumpingCompletedMayLeave','AgvAtWaitingPosition','AgvAtDumpingPosition']},{key:'address',label:'地址，例如DB1.DBX2.0'},{key:'direction',label:'方向',options:['Read','Write']},{key:'dataType',label:'类型',options:['Bool']}]},
 vehicles:{label:'车辆与包络',description:'车辆能力决定可接任务；包络与误差保存依据。示例数值只用于模拟，不会标记为现场已核定。',fields:[{key:'id',label:'车辆编号'},{key:'name',label:'车辆名称'},{key:'host',label:'主机地址'},{key:'startNode',label:'模拟起点',ref:'nodes'},{key:'capability',label:'能力',options:['Jack','Fork']},{key:'radius',label:'包络半径（m）',type:'number'},{key:'localizationError',label:'定位误差上限（m）',type:'number'},{key:'envelopeEvidence',label:'包络依据／适用范围'},{key:'battery',label:'模拟电量（%）',type:'number'},{key:'minimumBattery',label:'派车最低电量（%）',type:'number'},{key:'enabled',label:'启用',type:'boolean'}]},
 pools:{label:'车辆池',description:'工位绑定车辆池，池中车辆按能力、电量、路线和预约空档选择。同一车辆在不同池中仍共用一个忙闲时间线。',fields:[{key:'id',label:'车辆池编号'},{key:'name',label:'名称'},{key:'vehicleIds',label:'车辆编号列表',type:'list',help:'以分号分隔，例如 AGV-01;AGV-02'}]},
 templates:{label:'业务模板',description:'复用取箱→倒料→返箱九步语义。这里的耗时仅为模拟参数，不改变实车动作和PLC握手契约。',fields:[{key:'id',label:'模板编号'},{key:'name',label:'名称'},{key:'capability',label:'车辆所需能力',options:['Jack','Fork']},{key:'pickupSeconds',label:'模拟取箱耗时（秒）',type:'number'},{key:'dumpSeconds',label:'模拟倒料耗时（秒）',type:'number'},{key:'returnSeconds',label:'模拟放箱耗时（秒）',type:'number'}]},
 dumpers:{label:'倒料设备',description:'支持多个倒料设备，每个设备独立绑定等待点、倒料点和资源。展示区域本身不代表资源已接入。',fields:[{key:'id',label:'设备编号'},{key:'name',label:'名称'},{key:'plcId',label:'PLC',ref:'plcs'},{key:'waitingNode',label:'倒料等待节点',ref:'nodes'},{key:'dumpingNode',label:'倒料节点',ref:'nodes'},{key:'waitingResource',label:'等待位容量资源',ref:'resources'},{key:'dumpingResource',label:'倒料区容量资源',ref:'resources'}]},
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
