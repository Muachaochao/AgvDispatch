import { clone, type Definition, type Edge, type Node, type Plan, type Resource, type SceneItem } from './workbench.ts';

export interface Point { x:number; y:number }
export type ObjectKind = 'node'|'edge'|'resource'|'scene';
export interface Selection { node:string[]; edge:string[]; resource:string[]; scene:string[] }
export interface Layer { id:string; name:string; visible:boolean; isLocked:boolean; isAllowSelect:boolean; alpha:number; color?:string|null; strokeWidth:number }
export interface Fragment { nodes:Node[]; edges:Edge[]; resources:Resource[]; scene:SceneItem[] }
export interface SnapOptions { grid:boolean; step:number; nodes:boolean; alignment:boolean; threshold:number }
export const emptySelection = ():Selection => ({node:[],edge:[],resource:[],scene:[]});
export const selectionCount = (s:Selection) => Object.values(s).reduce((n,ids)=>n+ids.length,0);
export const layerId = (o:{layerId?:string}) => o.layerId || 'navigation';
export function layers(d:Definition):Layer[] {
  const raw=Array.isArray(d.layers)?d.layers as Partial<Layer>[]:[];
  const result:Layer[]=raw.map(l=>({id:l.id!,name:l.name||l.id!,visible:l.visible!==false,isLocked:l.isLocked===true,isAllowSelect:l.isAllowSelect!==false,alpha:l.alpha??1,color:l.color,strokeWidth:l.strokeWidth??2}));
  for(const id of ['navigation',...d.nodes.map(layerId),...d.edges.map(layerId),...d.resources.map(layerId)])
    if(!result.some(l=>l.id===id))result.push({id,name:id==='navigation'?'导航图层':id,visible:true,isLocked:false,isAllowSelect:true,alpha:1,strokeWidth:2});
  return result;
}
const objectLayer=(d:Definition,o:{layerId?:string}):Partial<Layer>|undefined=>(Array.isArray(d.layers)?d.layers as Layer[]:[]).find(l=>l.id===layerId(o));
export function visible(d:Definition,o:{layerId?:string}):boolean { return objectLayer(d,o)?.visible!==false; }
export function editable(d:Definition,o:{layerId?:string}):boolean { const l=objectLayer(d,o);return l?.visible!==false&&!l?.isLocked&&l?.isAllowSelect!==false; }
export function objects(p:Plan,k:ObjectKind):({id:string;layerId?:string})[] { return k==='node'?p.definition.nodes:k==='edge'?p.definition.edges:k==='resource'?p.definition.resources:p.scene; }
export function cleanSelection(p:Plan,s:Selection):Selection { return Object.fromEntries((Object.keys(s) as ObjectKind[]).map(k=>[k,s[k].filter(id=>objects(p,k).some(o=>o.id===id&&editable(p.definition,o)))])) as unknown as Selection; }
export function requireEditable(d:Definition,items:{layerId?:string}[]) { if(items.some(o=>!editable(d,o)))throw new Error('操作涉及隐藏、锁定或禁止选择的图层，请先调整图层状态。'); }
export function requireLayer(d:Definition,id:string) { const l=layers(d).find(l=>l.id===id);if(!l||!editable(d,{layerId:id}))throw new Error('当前图层不可编辑，请选择可见且未锁定的图层。'); }
export function uniqueId(prefix:string,used:Iterable<string>):string { const ids=new Set([...used].map(s=>s.toLowerCase()));let i=1;while(ids.has(`${prefix}${i}`.toLowerCase()))i++;return `${prefix}${i}`; }
const near=(a:Point,b:Point,eps=1e-7)=>Math.hypot(a.x-b.x,a.y-b.y)<=eps;
const lerp=(a:Point,b:Point,t:number):Point=>({x:a.x+(b.x-a.x)*t,y:a.y+(b.y-a.y)*t});
export function arcInfo(a:Point,c:Point,b:Point) {
  const den=2*(a.x*(c.y-b.y)+c.x*(b.y-a.y)+b.x*(a.y-c.y));
  if(Math.abs(den)<1e-10)return null;
  const q=(p:Point)=>p.x*p.x+p.y*p.y;
  const x=(q(a)*(c.y-b.y)+q(c)*(b.y-a.y)+q(b)*(a.y-c.y))/den;
  const y=(q(a)*(b.x-c.x)+q(c)*(a.x-b.x)+q(b)*(c.x-a.x))/den;
  const angle=(p:Point)=>Math.atan2(p.y-y,p.x-x),mod=(n:number)=>(n+2*Math.PI)%(2*Math.PI);
  const ab=mod(angle(b)-angle(a)),ac=mod(angle(c)-angle(a));
  return {x,y,r:Math.hypot(a.x-x,a.y-y),start:angle(a),sweep:ac<=ab?ab:ab-2*Math.PI};
}
export function edgePoint(d:Definition,e:Edge,t:number,lookup?:Map<string,Node>):Point {
  const a=lookup?lookup.get(e.from):d.nodes.find(n=>n.id===e.from),b=lookup?lookup.get(e.to):d.nodes.find(n=>n.id===e.to);
  if(!a||!b)throw new Error(`道路 ${e.id} 端点不存在`);
  const cp=e.controlPoints??[];
  if(e.geometryKind==='CubicBezier'&&cp.length===2) {
    const u=1-t;return{x:u*u*u*a.x+3*u*u*t*cp[0]!.x+3*u*t*t*cp[1]!.x+t*t*t*b.x,y:u*u*u*a.y+3*u*u*t*cp[0]!.y+3*u*t*t*cp[1]!.y+t*t*t*b.y};
  }
  if(e.geometryKind==='CircularArc'&&cp.length===1) { const arc=arcInfo(a,cp[0]!,b);if(arc){const angle=arc.start+arc.sweep*t;return{x:arc.x+arc.r*Math.cos(angle),y:arc.y+arc.r*Math.sin(angle)};} }
  return lerp(a,b,t);
}
export function edgeLength(d:Definition,e:Edge):number {
  if(e.geometryKind==='CircularArc'&&e.controlPoints?.length===1){const a=d.nodes.find(n=>n.id===e.from),b=d.nodes.find(n=>n.id===e.to);if(a&&b){const arc=arcInfo(a,e.controlPoints[0]!,b);if(arc)return Math.abs(arc.sweep)*arc.r;}}
  const count=e.geometryKind==='CubicBezier'?100:1,lookup=new Map(d.nodes.map(n=>[n.id,n]));let length=0,previous=edgePoint(d,e,0,lookup);
  for(let i=1;i<=count;i++){const p=edgePoint(d,e,i/count,lookup);length+=Math.hypot(p.x-previous.x,p.y-previous.y);previous=p;}return length;
}
export function addRoad(d:Definition,from:string,to:string,geometryKind='Line',controlPoints:Point[]=[],layer='navigation'):Edge {
  requireLayer(d,layer);const a=d.nodes.find(n=>n.id===from),b=d.nodes.find(n=>n.id===to);
  if(!a||!b||from===to||near(a,b))throw new Error('请选择两个位置不同的有效端点。');
  if(geometryKind==='CircularArc'&&(controlPoints.length!==1||!arcInfo(a,controlPoints[0]!,b)))throw new Error('圆弧控制点不能与两端点共线。');
  if(geometryKind==='CubicBezier'&&controlPoints.length!==2)throw new Error('贝塞尔道路需要两个控制点。');
  const road:Edge={id:uniqueId('E',d.edges.map(e=>e.id)),from,to,geometryKind,controlPoints:clone(controlPoints),speedLimit:.5,bidirectional:false,enabled:true,resourceIds:[],layerId:layer,distance:1};
  road.distance=edgeLength(d,road);d.edges.push(road);return road;
}
export function transformNodes(d:Definition,ids:string[],transform:(p:Point)=>Point) {
  const chosen=new Set(ids),selected=d.nodes.filter(n=>chosen.has(n.id));requireEditable(d,selected);
  const set=new Set(selected.map(n=>n.id)),affected=d.edges.filter(e=>set.has(e.from)||set.has(e.to));requireEditable(d,affected);
  for(const n of selected)Object.assign(n,transform(n));
  for(const e of affected){if(set.has(e.from)&&set.has(e.to)&&e.controlPoints)e.controlPoints=e.controlPoints.map(transform);e.distance=edgeLength(d,e);}
}
export function translate(p:Plan,s:Selection,dx:number,dy:number) {
  if(!Number.isFinite(dx)||!Number.isFinite(dy))throw new Error('移动量必须是有效数字。');
  const d=p.definition;requireEditable(d,[...d.resources.filter(r=>s.resource.includes(r.id)),...p.scene.filter(o=>s.scene.includes(o.id))]);
  transformNodes(d,s.node,a=>({x:a.x+dx,y:a.y+dy}));
  for(const r of d.resources.filter(r=>s.resource.includes(r.id)))r.boundary=r.boundary?.map(a=>({x:a.x+dx,y:a.y+dy}));
  for(const o of p.scene.filter(o=>s.scene.includes(o.id))){o.x+=dx;o.y+=dy;}
}
export function transformSelection(p:Plan,s:Selection,mode:'rotate'|'rotateCW'|'mirrorX'|'mirrorY') {
  const pts=[...p.definition.nodes.filter(n=>s.node.includes(n.id)),...p.definition.resources.filter(r=>s.resource.includes(r.id)).flatMap(r=>r.boundary??[]),...p.scene.filter(o=>s.scene.includes(o.id)).flatMap(o=>[{x:o.x,y:o.y},{x:o.x+o.width,y:o.y+o.height}])];
  if(!pts.length)throw new Error('请先选择节点、区域或场景对象。');
  const cx=(Math.min(...pts.map(p=>p.x))+Math.max(...pts.map(p=>p.x)))/2,cy=(Math.min(...pts.map(p=>p.y))+Math.max(...pts.map(p=>p.y)))/2;
  const apply=(a:Point):Point=>mode==='rotate'?{x:cx-(a.y-cy),y:cy+(a.x-cx)}:mode==='rotateCW'?{x:cx+(a.y-cy),y:cy-(a.x-cx)}:mode==='mirrorX'?{x:2*cx-a.x,y:a.y}:{x:a.x,y:2*cy-a.y};
  requireEditable(p.definition,[...p.definition.resources.filter(r=>s.resource.includes(r.id)),...p.scene.filter(o=>s.scene.includes(o.id))]);
  transformNodes(p.definition,s.node,apply);
  for(const r of p.definition.resources.filter(r=>s.resource.includes(r.id)))r.boundary=r.boundary?.map(apply);
  for(const o of p.scene.filter(o=>s.scene.includes(o.id))){const corners=[apply(o),apply({x:o.x+o.width,y:o.y+o.height})];o.x=Math.min(...corners.map(p=>p.x));o.y=Math.min(...corners.map(p=>p.y));if(mode==='rotate'||mode==='rotateCW')[o.width,o.height]=[o.height,o.width];}
}
export type Alignment='left'|'right'|'top'|'bottom'|'centerX'|'centerY'|'distributeX'|'distributeY';
export function alignNodes(d:Definition,ids:string[],mode:Alignment) {
  const ns=d.nodes.filter(n=>ids.includes(n.id));if(ns.length<(mode.startsWith('distribute')?3:2))throw new Error(mode.startsWith('distribute')?'等间距分布至少选择三个节点。':'对齐至少选择两个节点。');
  const axis=mode==='left'||mode==='right'||mode==='centerX'||mode==='distributeX'?'x':'y';
  const sorted=[...ns].sort((a,b)=>a[axis]-b[axis]),lo=sorted[0]![axis],hi=sorted.at(-1)![axis];
  const positions=new Map(sorted.map((n,i)=>[n.id,mode.startsWith('distribute')?lo+(hi-lo)*i/(sorted.length-1):mode==='left'||mode==='bottom'?lo:mode==='right'||mode==='top'?hi:(lo+hi)/2]));
  // Control points follow a rigid translation only when both endpoints move together.
  const original=clone(d),set=new Set(ids);requireEditable(d,[...ns,...d.edges.filter(e=>set.has(e.from)||set.has(e.to))]);
  for(const n of ns)n[axis]=positions.get(n.id)!;
  for(const e of d.edges.filter(e=>set.has(e.from)||set.has(e.to))){
    const a0=original.nodes.find(n=>n.id===e.from)!,b0=original.nodes.find(n=>n.id===e.to)!,a=d.nodes.find(n=>n.id===e.from)!,b=d.nodes.find(n=>n.id===e.to)!;
    if(e.controlPoints&&set.has(a.id)&&set.has(b.id)&&Math.abs((a.x-a0.x)-(b.x-b0.x))<1e-8&&Math.abs((a.y-a0.y)-(b.y-b0.y))<1e-8)e.controlPoints=e.controlPoints.map(q=>({x:q.x+a.x-a0.x,y:q.y+a.y-a0.y}));
    e.distance=edgeLength(d,e);
  }
}
export function deleteSelection(p:Plan,s:Selection) {
  const d=p.definition,nodes=new Set(s.node),resources=new Set(s.resource),edges=d.edges.filter(e=>s.edge.includes(e.id)||nodes.has(e.from)||nodes.has(e.to));
  requireEditable(d,[...d.nodes.filter(n=>nodes.has(n.id)),...edges,...d.resources.filter(r=>resources.has(r.id)),...p.scene.filter(o=>s.scene.includes(o.id)),...d.nodes.filter(o=>!nodes.has(o.id)&&o.resourceIds.some(id=>resources.has(id))),...d.edges.filter(o=>!edges.includes(o)&&o.resourceIds.some(id=>resources.has(id)))]);
  d.nodes=d.nodes.filter(n=>!nodes.has(n.id));d.edges=d.edges.filter(e=>!edges.includes(e));d.resources=d.resources.filter(r=>!resources.has(r.id));
  for(const o of [...d.nodes,...d.edges])o.resourceIds=o.resourceIds.filter(id=>!resources.has(id));
  p.waypoints=p.waypoints.filter(id=>!nodes.has(id));p.scene=p.scene.filter(o=>!s.scene.includes(o.id));
}
export function copySelection(p:Plan,s:Selection):Fragment {
  const d=p.definition,ids=new Set(s.node),roads=d.edges.filter(e=>s.edge.includes(e.id)||(ids.has(e.from)&&ids.has(e.to)));
  for(const e of roads){ids.add(e.from);ids.add(e.to);}
  return clone({nodes:d.nodes.filter(n=>ids.has(n.id)),edges:roads,resources:d.resources.filter(r=>s.resource.includes(r.id)),scene:p.scene.filter(o=>s.scene.includes(o.id))});
}
export function paste(p:Plan,f:Fragment,layer:string,dx=2,dy=2):Selection {
  const d=p.definition;requireLayer(d,layer);const selected=emptySelection(),nodeMap=new Map<string,string>(),resourceMap=new Map<string,string>();
  for(const r of f.resources){const next={...clone(r),id:uniqueId('R',d.resources.map(r=>r.id)),layerId:layer,boundary:r.boundary?.map(a=>({x:a.x+dx,y:a.y+dy}))};resourceMap.set(r.id,next.id);d.resources.push(next);selected.resource.push(next.id);}
  const refs=(ids:string[])=>ids.map(id=>resourceMap.get(id)??id).filter(id=>d.resources.some(r=>r.id===id));
  for(const n of f.nodes){const next={...clone(n),id:uniqueId('P',d.nodes.map(n=>n.id)),x:n.x+dx,y:n.y+dy,layerId:layer,agvSiteId:null,resourceIds:refs(n.resourceIds)};nodeMap.set(n.id,next.id);d.nodes.push(next);selected.node.push(next.id);}
  for(const e of f.edges){const next={...clone(e),id:uniqueId('E',d.edges.map(e=>e.id)),from:nodeMap.get(e.from)!,to:nodeMap.get(e.to)!,layerId:layer,resourceIds:refs(e.resourceIds),controlPoints:e.controlPoints?.map(a=>({x:a.x+dx,y:a.y+dy}))};if(!next.from||!next.to)throw new Error('剪贴板道路缺少端点。');d.edges.push(next);selected.edge.push(next.id);}
  for(const o of f.scene){const next={...clone(o),id:uniqueId('S',p.scene.map(o=>o.id)),layerId:layer,x:o.x+dx,y:o.y+dy};p.scene.push(next);selected.scene.push(next.id);}return selected;
}
export function reverseRoads(d:Definition,ids:string[]) {const roads=d.edges.filter(e=>ids.includes(e.id));requireEditable(d,roads);for(const e of roads){[e.from,e.to]=[e.to,e.from];if(e.geometryKind==='CubicBezier')e.controlPoints?.reverse();}}
export function splitRoad(d:Definition,id:string,t=.5):string {
  const e=d.edges.find(e=>e.id===id);if(!e||!(t>1e-5&&t<1-1e-5))throw new Error('请选择道路，在内部位置拆分。');requireEditable(d,[e]);
  const point=edgePoint(d,e,t),node:Node={id:uniqueId('P',d.nodes.map(n=>n.id)),...point,type:'Waypoint',enabled:true,resourceIds:clone(e.resourceIds),agvSiteId:null,layerId:layerId(e)};
  const left=clone(e),right=clone(e);right.id=uniqueId('E',d.edges.map(e=>e.id));left.to=node.id;right.from=node.id;
  if(e.geometryKind==='CubicBezier') { const a=edgePoint(d,e,0),b=edgePoint(d,e,1),[c1,c2]=e.controlPoints!;const q0=lerp(a,c1!,t),q1=lerp(c1!,c2!,t),q2=lerp(c2!,b,t),r0=lerp(q0,q1,t),r1=lerp(q1,q2,t);left.controlPoints=[q0,r0];right.controlPoints=[r1,q2]; }
  else if(e.geometryKind==='CircularArc'){left.controlPoints=[edgePoint(d,e,t/2)];right.controlPoints=[edgePoint(d,e,(1+t)/2)];}
  d.nodes.push(node);left.distance=edgeLength(d,left);right.distance=edgeLength(d,right);d.edges.splice(d.edges.indexOf(e),1,left,right);return node.id;
}
export function mergeNodes(p:Plan,ids:string[]) {
  if(ids.length!==2)throw new Error('合并需要恰好选择两个节点，先选中的节点将保留。');
  const d=p.definition,[keep,remove]=ids,ns=d.nodes.filter(n=>ids.includes(n.id));if(ns.length!==2)throw new Error('所选节点不存在。');
  const affected=d.edges.filter(e=>ids.includes(e.from)||ids.includes(e.to));requireEditable(d,[...ns,...affected]);
  const a=ns.find(n=>n.id===keep)!,b=ns.find(n=>n.id===remove)!;
  if(a.agvSiteId&&b.agvSiteId&&a.agvSiteId!==b.agvSiteId)throw new Error('两个节点有不同的厂家站点绑定，请先核对并解除其中一个绑定。');
  a.agvSiteId??=b.agvSiteId;a.resourceIds=[...new Set([...a.resourceIds,...b.resourceIds])];
  for(const e of affected){if(e.from===remove)e.from=keep!;if(e.to===remove)e.to=keep!;if(e.from!==e.to)e.distance=edgeLength(d,e);}
  d.edges=d.edges.filter(e=>e.from!==e.to);d.nodes=d.nodes.filter(n=>n.id!==remove);p.waypoints=p.waypoints.map(id=>id===remove?keep!:id);
}
export function batchRoads(d:Definition,ids:string[],changes:{speedLimit?:number;enabled?:boolean;bidirectional?:boolean}) {
  if(changes.speedLimit!==undefined&&(!Number.isFinite(changes.speedLimit)||changes.speedLimit<=0))throw new Error('限速必须是正数。');
  const selected=d.edges.filter(e=>ids.includes(e.id));requireEditable(d,selected);for(const e of selected)Object.assign(e,changes);
}
export function connectNodes(d:Definition,ids:string[],mode:'chain'|'orthogonal'|'loop',layer:string) {
  if(ids.length<2||(mode==='loop'&&ids.length<3))throw new Error(mode==='loop'?'闭合环路至少选择三个节点。':'连接至少选择两个节点。');requireLayer(d,layer);
  const ns=ids.map(id=>d.nodes.find(n=>n.id===id)!);if(ns.some(n=>!n))throw new Error('所选节点不存在。');requireEditable(d,ns);
  const pairs=ns.slice(1).map((n,i)=>[ns[i]!,n]);if(mode==='loop')pairs.push([ns.at(-1)!,ns[0]!]);
  const connect=(a:Node,b:Node)=>{if(!near(a,b)&&!d.edges.some(e=>(e.from===a.id&&e.to===b.id)||(e.bidirectional&&e.from===b.id&&e.to===a.id)))addRoad(d,a.id,b.id,'Line',[],layer);};
  for(const [a,b] of pairs as [Node,Node][]){if(mode==='orthogonal'&&Math.abs(a.x-b.x)>1e-7&&Math.abs(a.y-b.y)>1e-7){
    let corner=d.nodes.find(n=>layerId(n)===layer&&near(n,{x:b.x,y:a.y}));if(corner)requireEditable(d,[corner]);else{corner={id:uniqueId('P',d.nodes.map(n=>n.id)),x:b.x,y:a.y,type:'Waypoint',enabled:true,agvSiteId:null,resourceIds:[],layerId:layer};d.nodes.push(corner);}connect(a,corner);connect(corner,b);
  }else connect(a,b);}
}
export function cleanTopology(d:Definition):number {
  const nodes=new Set(d.nodes.map(n=>n.id)),seen=new Set<string>(),remove=new Set<string>();
  for(const e of d.edges){const signature=JSON.stringify([e.from,e.to,e.geometryKind,e.controlPoints??[],e.speedLimit,e.enabled,e.bidirectional,[...e.resourceIds].sort(),layerId(e)]);
    if(!nodes.has(e.from)||!nodes.has(e.to)||e.from===e.to||seen.has(signature)){requireEditable(d,[e]);remove.add(e.id);}else seen.add(signature);}
  d.edges=d.edges.filter(e=>!remove.has(e.id));return remove.size;
}
export function segmentIntersection(a:Point,b:Point,c:Point,d:Point):{point:Point;t:number;u:number}|null {
  const rx=b.x-a.x,ry=b.y-a.y,sx=d.x-c.x,sy=d.y-c.y,cross=rx*sy-ry*sx;if(Math.abs(cross)<1e-10)return null;
  const t=((c.x-a.x)*sy-(c.y-a.y)*sx)/cross,u=((c.x-a.x)*ry-(c.y-a.y)*rx)/cross;
  return t>=-1e-8&&t<=1+1e-8&&u>=-1e-8&&u<=1+1e-8?{point:{x:a.x+t*rx,y:a.y+t*ry},t,u}:null;
}
export function splitIntersections(d:Definition,ids:string[]=[]):number {
  const roads=d.edges.filter(e=>e.geometryKind==='Line'&&editable(d,e)&&(ids.length>=2?ids.includes(e.id):true));
  if(roads.length>800)throw new Error('交叉路口预检最多处理800条直线道路，请先缩小选择范围。');
  const cuts=new Map<string,{t:number;point:Point;nodeId?:string}[]>(),lookup=new Map(d.nodes.map(n=>[n.id,n]));
  const ends=new Map(roads.map(e=>[e.id,{a:edgePoint(d,e,0,lookup),b:edgePoint(d,e,1,lookup)}]));
  for(let i=0;i<roads.length;i++)for(let j=i+1;j<roads.length;j++){
    const a=roads[i]!,b=roads[j]!;if(layerId(a)!==layerId(b))continue;
    const first=ends.get(a.id)!,second=ends.get(b.id)!,hit=segmentIntersection(first.a,first.b,second.a,second.b);if(!hit)continue;
    for(const [e,t] of [[a,hit.t],[b,hit.u]] as [Edge,number][]){if(t<=1e-7||t>=1-1e-7)continue;const list=cuts.get(e.id)??[];if(!list.some(c=>Math.abs(c.t-t)<1e-7))list.push({t,point:hit.point});cuts.set(e.id,list);}
  }
  for(const [id,list] of cuts){const e=d.edges.find(e=>e.id===id)!;for(const cut of list){
    const matches=d.nodes.filter(n=>layerId(n)===layerId(e)&&near(n,cut.point));if(matches.length>1)throw new Error('交点存在多个重合节点，请先手动合并核对。');
    let n=matches[0];if(n)requireEditable(d,[n]);else {n={id:uniqueId('J',d.nodes.map(n=>n.id)),...cut.point,type:'Intersection',enabled:true,agvSiteId:null,resourceIds:[],layerId:layerId(e)};d.nodes.push(n);}cut.nodeId=n.id;
  }}
  for(const [id,list] of cuts){const original=d.edges.find(e=>e.id===id)!,path=[original.from,...list.sort((a,b)=>a.t-b.t).map(c=>c.nodeId!),original.to];d.edges=d.edges.filter(e=>e.id!==id);
    for(let i=1;i<path.length;i++){const e={...clone(original),id:i===1?id:uniqueId('E',d.edges.map(e=>e.id)),from:path[i-1]!,to:path[i]!};e.distance=edgeLength(d,e);d.edges.push(e);}}
  return cuts.size;
}
export function validPolygon(points:Point[]):boolean {
  if(points.length<3||points.some(p=>!Number.isFinite(p.x)||!Number.isFinite(p.y)))return false;
  let area=0;for(let i=0;i<points.length;i++){const a=points[i]!,b=points[(i+1)%points.length]!;if(near(a,b))return false;area+=a.x*b.y-b.x*a.y;
    for(let j=i+1;j<points.length;j++){if(j===i+1||(i===0&&j===points.length-1))continue;const c=points[j]!,d=points[(j+1)%points.length]!;
      const cross=(p:Point,q:Point,r:Point)=>(q.x-p.x)*(r.y-p.y)-(q.y-p.y)*(r.x-p.x);
      const collinear=Math.abs(cross(a,b,c))<1e-8&&Math.abs(cross(a,b,d))<1e-8&&Math.max(Math.min(a.x,b.x),Math.min(c.x,d.x))<=Math.min(Math.max(a.x,b.x),Math.max(c.x,d.x))+1e-8&&Math.max(Math.min(a.y,b.y),Math.min(c.y,d.y))<=Math.min(Math.max(a.y,b.y),Math.max(c.y,d.y))+1e-8;
      if(segmentIntersection(a,b,c,d)||collinear)return false;}}
  return Math.abs(area)>1e-8;
}
export function snapPoint(d:Definition,p:Point,options:SnapOptions,exclude:string[]=[]):{point:Point;guides:{axis:'x'|'y';value:number}[];target?:string} {
  const excluded=new Set(exclude),candidates=d.nodes.filter(n=>!excluded.has(n.id)&&visible(d,n)),threshold=options.threshold;
  if(options.nodes){let best:Node|undefined,dist=threshold;for(const n of candidates){const v=Math.hypot(n.x-p.x,n.y-p.y);if(v<=dist){best=n;dist=v;}}if(best)return{point:{x:best.x,y:best.y},guides:[],target:best.id};}
  const point={...p},guides:{axis:'x'|'y';value:number}[]=[];
  for(const axis of ['x','y'] as const){let value:number|undefined,distance=threshold;if(options.alignment)for(const n of candidates)if(Math.abs(n[axis]-p[axis])<distance){distance=Math.abs(n[axis]-p[axis]);value=n[axis];}
    if(value!==undefined){point[axis]=value;guides.push({axis,value});}else if(options.grid&&Number.isFinite(options.step)&&options.step>0)point[axis]=Math.round(p[axis]/options.step)*options.step;}
  return{point,guides};
}
export function boxSelection(p:Plan,a:Point,b:Point):Selection {
  const s=emptySelection(),loX=Math.min(a.x,b.x),hiX=Math.max(a.x,b.x),loY=Math.min(a.y,b.y),hiY=Math.max(a.y,b.y);
  const contains=(q:Point)=>q.x>=loX&&q.x<=hiX&&q.y>=loY&&q.y<=hiY;
  const meets=(u:Point,v:Point)=>contains(u)||contains(v)||[[{x:loX,y:loY},{x:hiX,y:loY}],[{x:hiX,y:loY},{x:hiX,y:hiY}],[{x:hiX,y:hiY},{x:loX,y:hiY}],[{x:loX,y:hiY},{x:loX,y:loY}]].some(([a,b])=>segmentIntersection(u,v,a!,b!));
  for(const n of p.definition.nodes)if(editable(p.definition,n)&&contains(n))s.node.push(n.id);
  const lookup=new Map(p.definition.nodes.map(n=>[n.id,n]));
  for(const e of p.definition.edges)if(editable(p.definition,e)){const steps=e.geometryKind==='Line'?1:64,points=Array.from({length:steps+1},(_,i)=>edgePoint(p.definition,e,i/steps,lookup));if(points.slice(1).some((v,i)=>meets(points[i]!,v)))s.edge.push(e.id);}
  for(const r of p.definition.resources)if(editable(p.definition,r)&&r.boundary?.some((v,i,points)=>meets(v,points[(i+1)%points.length]!)))s.resource.push(r.id);
  for(const o of p.scene)if(o.visible&&editable(p.definition,o)&&o.x<=hiX&&o.x+o.width>=loX&&o.y<=hiY&&o.y+o.height>=loY)s.scene.push(o.id);
  return s;
}
