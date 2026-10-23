import type {Definition,Edge} from './workbench.ts';
export interface VendorPath {schemaVersion:number;sourceSha256:string;planeSha256:string;curveIndex:number;instanceName:string;className:string;fromSite:string;toSite:string;propertyListPresent:boolean;rawPropertiesJson:string|null;importedEdgeId:string;importedFrom:string;importedTo:string;fromX:number;fromY:number;toX:number;toY:number;geometryKind:string;controlPoints?:{x:number;y:number}[]|null}
export interface VendorSource {sha256:string;planeSha256:string;mapName:string;formatVersion:string;curveCount:number;convertedCurveCount:number}
const groups=[
 ['运动方向与速度',[['direction','正走／倒走'],['movestyle','运动方式'],['mainroad','主干道参数'],['maxspeed','最大速度'],['maxacc','最大加速度'],['maxdec','最大减速度'],['maxrot','最大旋转速度'],['maxrotacc','旋转加速度'],['maxrotdec','旋转减速度']]],
 ['到点与载荷',[['reachangle','到点角度参数'],['relaxGoalReachDist','到点距离放宽参数'],['width','width（厂家参数）'],['length','length（厂家参数）'],['holdDir','方向保持参数'],['loadHoldDir','载货方向保持参数'],['unLoadHoldDir','卸载方向保持参数'],['forbiddenRotAngle','旋转角限制参数'],['StopAcc','停止加速度参数']]],
 ['传感器与避障分组',[['ultrasonic','超声相关参数'],['laser','激光相关参数'],['fallingdown','跌落检测相关参数'],['infrared','红外相关参数'],['virtualLaser','虚拟激光相关参数'],['3DCameraHole','3D相机孔洞相关参数'],['3DCameraObstacle','3D相机障碍相关参数'],['collisionGroupId','碰撞分组'],['forbidLaserGroupId','激光屏蔽分组'],['forbidDNodeGroupId','距离传感器屏蔽分组']]]
] as const;
export const vendorFields=groups.flatMap(([group,fields])=>fields.map(([key,label])=>({key,label,group})));
export interface VendorRow {key:string;label:string;group:string;known:boolean;state:'Unset'|'Explicit'|'Unknown';type:string;value:unknown;raw:string;reason:string}
const hash=(s:unknown)=>typeof s==='string'&&/^[a-fA-F0-9]{64}$/.test(s);
export function evidenceValid(v:VendorPath|undefined|null){return !!v&&v.schemaVersion===1&&hash(v.sourceSha256)&&hash(v.planeSha256)&&Number.isInteger(v.curveIndex)&&v.curveIndex>=0&&typeof v.propertyListPresent==='boolean'&&typeof v.fromSite==='string'&&typeof v.toSite==='string';}
function typed(p:any):{ok:boolean;value:unknown}{
 if(!p||typeof p.type!=='string')return {ok:false,value:null};
 const field=({bool:'boolValue',double:'doubleValue',float:'floatValue',int:'int32Value',int32:'int32Value',int64:'int64Value',string:'stringValue'} as Record<string,string>)[p.type];
 if(!field||!Object.hasOwn(p,field))return {ok:false,value:null};const value=p[field];
 const ok=p.type==='bool'?typeof value==='boolean':p.type==='string'?typeof value==='string':
  p.type==='int64'&&typeof value==='string'?/^-?\d+$/.test(value):typeof value==='number'&&Number.isFinite(value)&&(!p.type.startsWith('int')||Number.isSafeInteger(value));
 return {ok,value};
}
export function vendorRows(v:VendorPath|undefined|null):VendorRow[]{
 let list:any[]=[],unknown='';
 if(!evidenceValid(v))unknown='未取得可识别的原始厂家属性证据';
 else if(!v!.propertyListPresent){if(v!.rawPropertiesJson!==null)unknown='属性存在标记与原文不一致';}
 else try{if(typeof v!.rawPropertiesJson!=='string'||v!.rawPropertiesJson.length>65536)throw Error();const raw=JSON.parse(v!.rawPropertiesJson);if(!Array.isArray(raw)||raw.length>512)throw Error();list=raw;}catch{unknown='原属性不是可识别数组，保留未知，不填默认值';}
 const fields=[...vendorFields.map(f=>({...f,known:true})),...list.filter(p=>typeof p?.key==='string'&&!vendorFields.some(f=>f.key===p.key)).map(p=>({key:p.key,label:p.key,group:'其他原始字段',known:false}))];
 const unique=[...new Map(fields.map(f=>[f.key,f])).values()];
 const rows:VendorRow[]=unique.map(f=>{const hits=list.filter(p=>p?.key===f.key),raw=JSON.stringify(hits),p=hits[0],decoded=typed(p);
  if(unknown)return {...f,state:'Unknown',type:'—',value:null,raw:v?.rawPropertiesJson??'',reason:unknown};
  if(!hits.length)return {...f,state:'Unset',type:'—',value:null,raw:'',reason:'原图未显式设置；车端继承值未读取'};
  if(hits.length!==1||!decoded.ok)return {...f,state:'Unknown',type:typeof p?.type==='string'?p.type:'未知',value:null,raw,reason:hits.length>1?'同名字段重复，不能确定有效值':'缺少对应类型值或类型未支持；不解码猜测value'};
  return {...f,state:'Explicit',type:p.type,value:decoded.value,raw,reason:f.known?'原图显式值；未接入车辆控制':'未支持字段，仅原样保留'};
 });
 for(const [index,p]of list.entries())if(typeof p?.key!=='string')rows.push({key:'原始条目#'+index,label:'缺少字段名的原始条目',group:'其他原始字段',known:false,state:'Unknown',type:typeof p?.type==='string'?p.type:'未知',value:null,raw:JSON.stringify(p),reason:'字段名不可识别，未丢弃原条目'});
 return rows;
}
export function bindingIssues(e:Edge,d:Definition){const v=e.vendorPath,issues:string[]=[];if(!evidenceValid(v))return ['此道路没有原始厂家属性证据；需重新导入原SMAP，不能从调度值补造'];
 const a=d.nodes.find(n=>n.id===e.from),b=d.nodes.find(n=>n.id===e.to);const same=(x:number|undefined,y:number)=>typeof x==='number'&&Number.isFinite(x)&&Number.isFinite(y)&&Math.abs(x-y)<1e-9;
 if(e.id!==v!.importedEdgeId||e.from!==v!.importedFrom||e.to!==v!.importedTo||a?.agvSiteId!==v!.fromSite||b?.agvSiteId!==v!.toSite||
  !same(a?.x,v!.fromX)||!same(a?.y,v!.fromY)||!same(b?.x,v!.toX)||!same(b?.y,v!.toY)||e.geometryKind!==v!.geometryKind||JSON.stringify(e.controlPoints??[])!==JSON.stringify(v!.controlPoints??[]))
  issues.push('道路编号、端点、厂家绑定或几何已变更：以下仅是原图历史属性，不代表当前道路匹配');
 const source=d.robokitSource;if(!source||source.sha256!==v!.sourceSha256||source.planeSha256!==v!.planeSha256)issues.push('整图来源摘要缺失或与道路不一致，来源需复核');
 if(source&&source.curveCount>source.convertedCurveCount)issues.push(`原图有${source.curveCount-source.convertedCurveCount}条曲线未转换，当前调度图不是原图完整副本`);
 if(e.bidirectional)issues.push('调度道路设为双向，不会复制厂家属性至反向；正走／倒走与双向通行是不同设置');
 const speed=vendorRows(v).find(r=>r.key==='maxspeed');if(speed?.state==='Explicit'&&typeof speed.value==='number'&&speed.value!==e.speedLimit)issues.push('调度限速与原图maxspeed数值不同；二者未联动，不能据此认定车端实际速度');
 return issues;
}
export function reverseVendorPaths(e:Edge,d:Definition){const v=e.vendorPath;if(!evidenceValid(v))return [];return d.edges.filter(x=>x.id!==e.id&&evidenceValid(x.vendorPath)&&x.vendorPath!.sourceSha256===v!.sourceSha256&&x.vendorPath!.planeSha256===v!.planeSha256&&x.vendorPath!.fromSite===v!.toSite&&x.vendorPath!.toSite===v!.fromSite);}
export function compareVendorRows(a:VendorRow,b:VendorRow|undefined){if(!b)return '未取得反向记录';if(a.state==='Unknown'||b.state==='Unknown')return '无法比较';return a.state===b.state&&a.type===b.type&&JSON.stringify(a.value)===JSON.stringify(b.value)?'原始设置相同':'正反向设置不同';}
export function pairedVendorRows(a:VendorPath|undefined|null,b:VendorPath|undefined|null){const left=vendorRows(a),right=b?vendorRows(b):[];
 const missing=(sample:VendorRow,side:VendorRow[]):VendorRow=>({...sample,state:side.every(r=>r.state==='Unknown')?'Unknown':'Unset',type:'—',value:null,raw:'',reason:'该方向未显式设置此字段；有效继承值未知'});
 return [...new Set([...left.map(r=>r.key),...right.map(r=>r.key)])].map(key=>{const l=left.find(r=>r.key===key),r=right.find(r=>r.key===key);return {left:l??missing(r!,left),right:b?(r??missing(l!,right)):undefined};});}
export function valueText(r:VendorRow){if(r.state==='Unset')return '未设置';if(r.state==='Unknown')return '未知';if(r.key==='direction'&&(r.value===0||r.value===1))return `${r.value}（${r.value===0?'正走':'倒走'}；按留存格式说明）`;return typeof r.value==='string'?r.value:JSON.stringify(r.value);}
