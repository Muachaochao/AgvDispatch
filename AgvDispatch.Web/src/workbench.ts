import type {VendorPath,VendorSource} from './vendor-path.ts';
export interface Node { id: string; x: number; y: number; type: string; enabled: boolean; resourceIds: string[]; agvSiteId: string | null; allowTrafficStop?: boolean | null; allowTransitThrough?: boolean | null; allowDumperWaiting?: boolean | null; allowChargingWaiting?: boolean | null; layerId?: string }
export interface Edge { vendorPath?:VendorPath|null; id: string; from: string; to: string; distance: number; speedLimit: number; enabled: boolean; bidirectional: boolean; resourceIds: string[]; geometryKind: string; controlPoints?: {x:number;y:number}[] | null; layerId?: string }
export interface Resource { id: string; type: string; capacity: number; boundary?: {x:number;y:number}[] | null; layerId?: string }
export interface AreaAnnotation {id:string;sourceType:string;text:string;boundary:{x:number;y:number}[];strokeColor:string;fillColor:string;fillOpacity:number;layerId?:string;sourceJson?:string}
export interface Definition { areaAnnotations?:AreaAnnotation[]; robokitSource?:VendorSource|null; name: string; source: string; coordinateSystem: string; unit: string; nodes: Node[]; edges: Edge[]; resources: Resource[]; [key:string]:unknown }
export interface SceneItem { id: string; name: string; kind: 'area'|'equipment'|'text'; x:number; y:number; width:number; height:number; color:string; visible:boolean; layerId?:string }
export interface VehicleConfig { vehicleId:string; displayName:string; ipAddress:string; port:number; navigationPort:number; enabled:boolean }
export interface Plan { schema:'agv-web-plan'; version:1; id:string; name:string; updatedAt:string; definition:Definition; scene:SceneItem[]; vehicles:VehicleConfig[]; waypoints:string[] }
export const clone = <T>(value:T):T => value === undefined ? value : JSON.parse(JSON.stringify(value));
export function emptyPlan():Plan { return {schema:'agv-web-plan',version:1,id:crypto.randomUUID(),name:'新调度方案',updatedAt:new Date().toISOString(),definition:{name:'新建导航图',source:'',coordinateSystem:'LocalCartesian',unit:'m',nodes:[],edges:[],resources:[]},scene:[],vehicles:[],waypoints:[]}; }
const finite = (n:unknown) => typeof n==='number' && Number.isFinite(n) && Math.abs(n)<=1e7;
const name = (s:unknown):s is string => typeof s==='string' && s.trim().length>0 && s.length<=128;
export function validateDefinition(d:Definition):string[] {
  const errors:string[]=[];
  if(!d || !Array.isArray(d.nodes)||!Array.isArray(d.edges)||!Array.isArray(d.resources))return ['缺少nodes、edges或resources数组'];
  if(!name(d.name)||!name(d.coordinateSystem)||d.unit!=='m')errors.push('地图名称、坐标系必填；当前Web编辑使用米制');
  if(d.nodes.length>5000||d.edges.length>10000||d.resources.length>2000)return ['地图超出Web编辑预算'];
  if(d.areaAnnotations!==undefined){
    if(!Array.isArray(d.areaAnnotations)||d.areaAnnotations.length>2000)errors.push('厂家区域标注列表无效');
    else for(const a of d.areaAnnotations)if(!a||!name(a.id)||typeof a.text!=='string'||a.text.length>2048||!Array.isArray(a.boundary)||a.boundary.length<3||a.boundary.length>10000||a.boundary.some(p=>!p||!finite(p.x)||!finite(p.y))||!/^#[0-9a-f]{6}$/i.test(a.strokeColor)||!/^#[0-9a-f]{6}$/i.test(a.fillColor)||!finite(a.fillOpacity)||a.fillOpacity<0||a.fillOpacity>1)errors.push('厂家区域标注边界或显示属性无效');
  }
  if(d.layers!==undefined){
    if(!Array.isArray(d.layers)||d.layers.length>200)errors.push('图层列表无效或超过200层');
    else {const ids=new Set<string>();for(const layer of d.layers){if(!layer||!name(layer.id)||!name(layer.name)||ids.has(layer.id.toLowerCase())||['visible','isLocked','isAllowSelect'].some(key=>layer[key]!==undefined&&typeof layer[key]!=='boolean')||(layer.alpha!==undefined&&(!finite(layer.alpha)||layer.alpha<0||layer.alpha>1))||(layer.strokeWidth!==undefined&&(!finite(layer.strokeWidth)||layer.strokeWidth<=0)))errors.push('图层编号、名称或显示设置无效');else ids.add(layer.id.toLowerCase());}}
  }
  for(const item of [...d.nodes,...d.edges,...d.resources])if(item?.layerId!==undefined&&item.layerId!==null&&!name(item.layerId))errors.push('对象图层编号无效');
  for(const [list,label] of [[d.nodes,'航点'],[d.edges,'道路'],[d.resources,'区域']] as const){const ids=new Set<string>();for(const item of list){if(!item||!name(item.id)||ids.has(item.id.toLowerCase()))errors.push(`${label}编号为空或重复`);else ids.add(item.id.toLowerCase());}}
  const nodes=new Set(d.nodes.map(n=>n?.id));const resources=new Set(d.resources.map(r=>r?.id));
  for(const n of d.nodes){if(!n||!finite(n.x)||!finite(n.y)||!['Waypoint','Station','Intersection','WaitingPoint','ChargingPoint'].includes(n.type)||typeof n.enabled!=='boolean')errors.push(`航点 ${n?.id} 坐标或类型无效`);
    if((['allowTrafficStop','allowTransitThrough','allowDumperWaiting','allowChargingWaiting'] as const).some(key=>n[key]!==undefined&&n[key]!==null&&typeof n[key]!=='boolean'))errors.push(`航点 ${n?.id} 的通行或等待角色属性无效`);}
  for(const e of d.edges){if(!e||!nodes.has(e.from)||!nodes.has(e.to)||e.from===e.to||!finite(e.distance)||e.distance<=0||!finite(e.speedLimit)||e.speedLimit<=0||typeof e.enabled!=='boolean'||typeof e.bidirectional!=='boolean')errors.push(`道路 ${e?.id} 端点、距离或速度无效`);
    if(!e||!['Line','CircularArc','CubicBezier'].includes(e.geometryKind)||(e.geometryKind==='CircularArc'&&e.controlPoints?.length!==1)||(e.geometryKind==='CubicBezier'&&e.controlPoints?.length!==2)||e.controlPoints?.some(p=>!finite(p.x)||!finite(p.y)))errors.push(`道路 ${e?.id} 曲线控制点无效`);}
  for(const r of d.resources){if(!r||!['Intersection','NarrowAisle','SingleLane','Station','ChargingBay'].includes(r.type)||!Number.isInteger(r.capacity)||r.capacity<1||r.capacity>1000||r.boundary?.some(p=>!finite(p.x)||!finite(p.y)))errors.push(`区域 ${r?.id} 类型、容量或边界无效`);}
  for(const item of [...d.nodes,...d.edges])if(!item||!Array.isArray(item.resourceIds)||item.resourceIds.some(id=>!resources.has(id)))errors.push(`对象 ${item?.id} 引用了不存在的共享区域`);
  return [...new Set(errors)];
}
export function validateVehicles(vehicles:VehicleConfig[]):string[]{
  const errors:string[]=[];const ids=new Set<string>(),endpoints=new Set<string>();
  if(vehicles.length>100)return ['车辆超出后台100辆限制'];
  for(const v of vehicles){const key=v.vehicleId?.toLowerCase();if(!name(v.vehicleId)||ids.has(key))errors.push('车辆编号为空或重复');ids.add(key);
    if(!/^[A-Za-z0-9_-]{1,32}$/.test(v.vehicleId)||!name(v.displayName)||v.displayName.length>80||typeof v.enabled!=='boolean'||!/^\d{1,3}(\.\d{1,3}){3}$/.test(v.ipAddress)||v.ipAddress.split('.').some(n=>Number(n)>255)||[v.port,v.navigationPort].some(n=>!Number.isInteger(n)||n<1||n>65535))errors.push(`车辆 ${v.vehicleId} 编号（英文数字-_，最多32位）、名称（最多80字）、IPv4或端口无效`);
    for(const p of [v.port,v.navigationPort]){const endpoint=`${v.ipAddress}:${p}`;if(endpoints.has(endpoint))errors.push(`重复通信端点 ${endpoint}`);endpoints.add(endpoint);}}
  return errors;
}
export function parsePlan(text:string):Plan {
  if(text.length>8*1024*1024)throw new Error('文件超过8MiB');
  const value=JSON.parse(text);
  let plan:Plan;
  if(value.schema==='agv-web-plan'){if(value.version!==1)throw new Error('方案版本不支持');plan=value;}
  else {plan=emptyPlan();plan.definition=value.definition??value.map??value.version?.definition??value;plan.name=plan.definition.name;}
  const errors=validateDefinition(plan.definition);
  if(!Array.isArray(plan.scene)||!Array.isArray(plan.vehicles)||!Array.isArray(plan.waypoints)||!name(plan.name)||!name(plan.id))errors.push('方案元数据或数组无效');
  if(errors.length)throw new Error(errors.join('；'));
  if(plan.scene.length>1000||plan.scene.some(s=>!name(s.id)||!name(s.name)||!['area','equipment','text'].includes(s.kind)||![s.x,s.y,s.width,s.height].every(finite)||s.width<=0||s.height<=0||!/^#[0-9a-f]{6}$/i.test(s.color)||typeof s.visible!=='boolean'))throw new Error('场景对象数据无效');
  if(new Set(plan.scene.map(s=>s.id)).size!==plan.scene.length)throw new Error('场景对象编号重复');
  if(plan.waypoints.length>1000||plan.waypoints.some(id=>!plan.definition.nodes.some(n=>n.id===id)))throw new Error('航点顺序包含不存在的航点或超出限制');
  const vehicleErrors=validateVehicles(plan.vehicles);if(vehicleErrors.length)throw new Error(vehicleErrors.join('；'));
  return clone(plan);
}
export function routePlan(d:Definition,waypoints:string[]):{nodes:string[];edges:string[];distance:number;error:string}{
  const result={nodes:[] as string[],edges:[] as string[],distance:0,error:''};
  if(waypoints.length<2)return {...result,error:'至少选择两个航点'};
  if(d.nodes.length*d.edges.length*waypoints.length>2_000_000)return {...result,error:'超出浏览器路线计算预算，请缩小核对范围或使用后台路线预览'};
  const enabled=new Set(d.nodes.filter(n=>n.enabled).map(n=>n.id));
  if(waypoints.some(n=>!enabled.has(n)))return {...result,error:'顺序包含不存在或已停用航点'};
  for(let i=1;i<waypoints.length;i++){
    const start=waypoints[i-1]!,end=waypoints[i]!;const dist=new Map([[start,0]]),prev=new Map<string,{node:string;edge:Edge}>(),done=new Set<string>();
    while(true){let best:string|undefined;let cost=Infinity;for(const [id,n]of dist)if(!done.has(id)&&n<cost){cost=n;best=id;}if(best===undefined||best===end)break;done.add(best);
      for(const e of d.edges){if(!e.enabled||!enabled.has(e.from)||!enabled.has(e.to)||!(e.distance>0))continue;const next=e.from===best?e.to:e.bidirectional&&e.to===best?e.from:null;if(!next)continue;const n=cost+e.distance;if(n<(dist.get(next)??Infinity)){dist.set(next,n);prev.set(next,{node:best,edge:e});}}}
    if(!dist.has(end))return {...result,error:`${start} → ${end} 不可达，请检查方向及启停`};
    const nodes=[end],edges:string[]=[];let p=end;while(p!==start){const step=prev.get(p)!;edges.unshift(step.edge.id);nodes.unshift(step.node);p=step.node;}
    result.nodes.push(...(i===1?nodes:nodes.slice(1)));result.edges.push(...edges);result.distance+=dist.get(end)!;
  }
  return result;
}
export function edgePath(e:Edge,d:Definition):string {
  const a=d.nodes.find(n=>n.id===e.from),b=d.nodes.find(n=>n.id===e.to);if(!a||!b)return '';
  const p=(n:{x:number;y:number})=>`${n.x} ${-n.y}`;
  if(e.geometryKind==='CubicBezier'&&e.controlPoints?.length===2)return `M ${p(a)} C ${p(e.controlPoints[0]!)} ${p(e.controlPoints[1]!)} ${p(b)}`;
  if(e.geometryKind==='CircularArc'&&e.controlPoints?.length===1){
    const c=e.controlPoints[0]!,den=2*(a.x*(c.y-b.y)+c.x*(b.y-a.y)+b.x*(a.y-c.y));if(Math.abs(den)<1e-9)return `M ${p(a)} L ${p(b)}`;
    const q=(n:{x:number;y:number})=>n.x*n.x+n.y*n.y;
    const x=(q(a)*(c.y-b.y)+q(c)*(b.y-a.y)+q(b)*(a.y-c.y))/den,y=(q(a)*(b.x-c.x)+q(c)*(a.x-b.x)+q(b)*(c.x-a.x))/den;
    const r=Math.hypot(a.x-x,a.y-y),angle=(n:{x:number;y:number})=>Math.atan2(n.y-y,n.x-x),mod=(n:number)=>(n+Math.PI*2)%(Math.PI*2),ab=mod(angle(b)-angle(a)),ac=mod(angle(c)-angle(a)),ccw=ac<=ab,sweep=ccw?ab:Math.PI*2-ab;
    return `M ${p(a)} A ${r} ${r} 0 ${sweep>Math.PI?1:0} ${ccw?0:1} ${p(b)}`;
  }
  return `M ${p(a)} L ${p(b)}`;
}
