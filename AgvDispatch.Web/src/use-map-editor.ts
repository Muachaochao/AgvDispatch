import { computed, onMounted, onUnmounted, reactive, ref, watch, type Ref } from 'vue';
import { clone, validateDefinition, type Plan, type Edge } from './workbench';
import * as geometry from './map-editing';
import type { Point, ObjectKind, Selection, Layer, Alignment, Fragment } from './map-editing';

type Tool='select'|'pan'|'node'|'edge'|'arc'|'bezier'|'boundary'|'brush'|'measure'|'scene';
interface Entry {before:Plan;after:Plan;label:string;at:string;bytes:number}
type Gesture={pointer:number;mode:'pan'|'box'|'move'|'control'|'brush';start:Point;client:Point;inverse:DOMMatrix;before:Plan;selection:Selection;pan:Point;kind?:ObjectKind;id?:string;index?:number;moved:boolean};
export function useMapEditor(plan:Ref<Plan>,tab:Ref<string>,message:Ref<string>,changed:()=>void,inspect:(kind:ObjectKind|null,id:string)=>void,save:()=>void) {
  const svg=ref<SVGSVGElement>(),selection=ref(geometry.emptySelection()),tool=ref<Tool>('select'),panel=ref<'properties'|'batch'|'layers'|'history'>('properties');
  const undoStack=ref<Entry[]>([]),redoStack=ref<Entry[]>([]),clipboard=ref<Fragment|null>(null),pasteCount=ref(0);
  const zoom=ref(1),pan=ref<Point>({x:0,y:0}),camera=ref({x:-5,y:-25,w:40,h:30});
  const grid=ref(true),gridStep=ref(1),snapGrid=ref(true),snapNodes=ref(true),snapAlignment=ref(true),currentLayer=ref('navigation');
  const guides=ref<{axis:'x'|'y';value:number}[]>([]),snapTarget=ref(''),cursor=ref<Point|null>(null),box=ref<{a:Point;b:Point}|null>(null);
  const roadStart=ref(''),roadEnd=ref(''),draft=ref<Point[]>([]),measure=ref<Point[]>([]),brushResource=ref(''),brushRemove=ref(false);
  const batch=reactive({dx:1,dy:0,speed:.5,splitAt:50,newLayerName:'新图层'});
  const space=ref(false),viewport=ref({width:800,height:500});let gesture:Gesture|null=null;let brushSeen=new Set<string>();let observer:ResizeObserver|undefined;
  const d=computed(()=>plan.value.definition),layerList=computed(()=>geometry.layers(d.value));
  const count=computed(()=>geometry.selectionCount(selection.value));
  const selectionSets=computed(()=>({node:new Set(selection.value.node),edge:new Set(selection.value.edge),resource:new Set(selection.value.resource),scene:new Set(selection.value.scene)}));
  const isSelected=(kind:ObjectKind,id:string)=>selectionSets.value[kind].has(id);
  const viewBox=computed(()=>{const c=camera.value,w=c.w/zoom.value,h=c.h/zoom.value;return`${c.x+(c.w-w)/2+pan.value.x} ${c.y+(c.h-h)/2+pan.value.y} ${w} ${h}`;});
  const scale=computed(()=>Math.max(camera.value.w/viewport.value.width,camera.value.h/viewport.value.height)*10/zoom.value);
  watch(svg,element=>{observer?.disconnect();if(!element)return;observer=new ResizeObserver(entries=>{const r=entries[0]?.contentRect;if(r&&r.width&&r.height)viewport.value={width:r.width,height:r.height};});observer.observe(element);});
  const visibleNodes=computed(()=>d.value.nodes.filter(n=>geometry.visible(d.value,n)));
  const visibleEdges=computed(()=>d.value.edges.filter(e=>geometry.visible(d.value,e)));
  const visibleResources=computed(()=>d.value.resources.filter(r=>geometry.visible(d.value,r)));
  const visibleScene=computed(()=>plan.value.scene.filter(o=>o.visible&&geometry.visible(d.value,o)));
  const toolHint=computed(()=>({select:'拖动节点 / 区域 / 场景；空白处拖框，Ctrl / Shift 点选可多选；中键或空格拖动平移。',pan:'拖动画布平移；滚轮以鼠标位置缩放。',node:'点击画布放置节点，使用当前图层及吸附设置。',edge:'依次点击起点、终点节点绘制直线。',arc:roadEnd.value?'点击一个经过点完成圆弧；三点不能共线。':'依次点击起点、终点节点，然后点击圆弧经过点。',bezier:roadEnd.value?`点击第 ${draft.value.length+1} 个控制点（共2个）。`:'依次点击起点、终点节点，然后点击两个贝塞尔控制点。',boundary:'逐点绘制区域边界；双击、Enter 或“完成边界”结束，Backspace 撤回顶点。',brush:'选择共享区域后，在节点或道路上点击 / 拖动刷关联；可切换解除模式。',measure:'依次点击两点测量直线距离，不修改地图。',scene:'点击画布放置场景对象。'} as Record<Tool,string>)[tool.value]);
  const measurement=computed(()=>measure.value.length===2?Math.hypot(measure.value[1]!.x-measure.value[0]!.x,measure.value[1]!.y-measure.value[0]!.y):null);
  const draftPath=computed(()=>{
    if(tool.value==='boundary')return draft.value.map((p,i)=>`${i?'L':'M'} ${p.x} ${-p.y}`).join(' ')+(cursor.value&&draft.value.length?` L ${cursor.value.x} ${-cursor.value.y}`:'');
    const a=d.value.nodes.find(n=>n.id===roadStart.value),b=d.value.nodes.find(n=>n.id===roadEnd.value);
    if(!a)return'';if(!b)return cursor.value?`M ${a.x} ${-a.y} L ${cursor.value.x} ${-cursor.value.y}`:'';
    const controls=[...draft.value,...(cursor.value?[cursor.value]:[])];
    if(tool.value==='bezier'&&controls.length){const c1=controls[0]!,c2=controls[1]??c1;return`M ${a.x} ${-a.y} C ${c1.x} ${-c1.y} ${c2.x} ${-c2.y} ${b.x} ${-b.y}`;}
    if(tool.value==='arc'&&controls.length){const c=controls[0]!,arc=geometry.arcInfo(a,c,b);if(arc){const pts=Array.from({length:41},(_,i)=>({x:arc.x+arc.r*Math.cos(arc.start+arc.sweep*i/40),y:arc.y+arc.r*Math.sin(arc.start+arc.sweep*i/40)}));return pts.map((p,i)=>`${i?'L':'M'} ${p.x} ${-p.y}`).join(' ');}}
    return`M ${a.x} ${-a.y} L ${b.x} ${-b.y}`;
  });
  function refreshInspector(){selection.value=geometry.cleanSelection(plan.value,selection.value);for(const k of ['node','edge','resource','scene'] as const){const id=selection.value[k].at(-1);if(id){inspect(k,id);return;}}inspect(null,'');}
  function clear(){selection.value=geometry.emptySelection();inspect(null,'');}
  function setSelection(next:Selection){selection.value=geometry.cleanSelection(plan.value,next);refreshInspector();}
  function pick(kind:ObjectKind,id:string,e?:{ctrlKey?:boolean;metaKey?:boolean;shiftKey?:boolean}) {
    const obj=geometry.objects(plan.value,kind).find(o=>o.id===id);if(!obj||!geometry.editable(d.value,obj)){message.value='该对象所在图层隐藏、锁定或禁止选择。';return;}
    const additive=e?.ctrlKey||e?.metaKey||e?.shiftKey,next=additive?clone(selection.value):geometry.emptySelection();
    next[kind]=additive&&next[kind].includes(id)?next[kind].filter(x=>x!==id):[...next[kind],id];setSelection(next);
  }
  function record(before:Plan,label:string) {
    const previous=JSON.stringify(before),next=JSON.stringify(plan.value);if(previous===next)return false;
    undoStack.value.push({before,after:JSON.parse(next),label,at:new Date().toLocaleTimeString(),bytes:(previous.length+next.length)*2});
    while(undoStack.value.length>1&&(undoStack.value.length>100||undoStack.value.reduce((n,e)=>n+e.bytes,0)>32*1024*1024))undoStack.value.shift();
    redoStack.value=[];changed();return true;
  }
  function edit(fn:()=>void,label='应用属性 / 编辑方案'):boolean {
    if(gesture)cancelGesture();const before=clone(plan.value);
    try{fn();const issues=validateDefinition(d.value);if(issues.length)throw new Error(issues.join('；'));if(plan.value.scene.length>1000)throw new Error('场景对象超过1000个，请缩小编辑范围。');record(before,label);refreshInspector();return true;}catch(ex){plan.value=before;message.value=String(ex instanceof Error?ex.message:ex);refreshInspector();return false;}
  }
  function history(redo=false){if(gesture)cancelGesture();cancelDrawing();const from=redo?redoStack:undoStack,to=redo?undoStack:redoStack,entry=from.value.pop();if(!entry)return;to.value.push(entry);plan.value=clone(redo?entry.after:entry.before);changed();clear();message.value=`${redo?'重做':'撤销'}：${entry.label}`;}
  function restore(index:number){while(undoStack.value.length>index+1)history();}
  function fit(){const pts=[...d.value.nodes,...d.value.edges.flatMap(e=>e.controlPoints??[]),...d.value.resources.flatMap(r=>r.boundary??[]),...(d.value.areaAnnotations??[]).flatMap(a=>a.boundary),...plan.value.scene.flatMap(o=>[{x:o.x,y:o.y},{x:o.x+o.width,y:o.y+o.height}])];
    if(pts.length){const xs=pts.map(p=>p.x),ys=pts.map(p=>-p.y),x=Math.min(...xs)-3,y=Math.min(...ys)-3;camera.value={x,y,w:Math.max(15,Math.max(...xs)-x+3),h:Math.max(12,Math.max(...ys)-y+3)};}else camera.value={x:-5,y:-25,w:40,h:30};zoom.value=1;pan.value={x:0,y:0};}
  function point(e:{clientX:number;clientY:number},matrix?:DOMMatrix):Point|null {const m=matrix??svg.value?.getScreenCTM()?.inverse();if(!m)return null;const p=new DOMPoint(e.clientX,e.clientY).matrixTransform(m);return{x:p.x,y:-p.y};}
  function snapped(p:Point,exclude:string[]=[]){const m=svg.value?.getScreenCTM(),threshold=m?9/Math.max(.01,Math.hypot(m.a,m.b)):.2;const result=geometry.snapPoint(d.value,p,{grid:snapGrid.value,step:gridStep.value,nodes:snapNodes.value,alignment:snapAlignment.value,threshold},exclude);guides.value=result.guides;snapTarget.value=result.target??'';return result.point;}
  function cancelDrawing(){roadStart.value='';roadEnd.value='';draft.value=[];guides.value=[];snapTarget.value='';}
  function release(pointer:number){if(svg.value?.hasPointerCapture(pointer))svg.value.releasePointerCapture(pointer);}
  function cancelGesture(){if(!gesture)return;const g=gesture;gesture=null;if(g.mode==='move'||g.mode==='control'||g.mode==='brush')plan.value=g.before;release(g.pointer);box.value=null;guides.value=[];refreshInspector();}
  function cancel(){cancelGesture();cancelDrawing();tool.value='select';message.value='已取消当前绘制 / 拖动。';}
  function changeTool(next:Tool){cancelGesture();cancelDrawing();tool.value=next;message.value='';}
  watch(tool,()=>cancelDrawing());watch(tab,()=>{cancel();clear();});
  function addNode(p:Point){let id='';if(edit(()=>{geometry.requireLayer(d.value,currentLayer.value);id=geometry.uniqueId('P',d.value.nodes.map(n=>n.id));d.value.nodes.push({id,...p,type:'Waypoint',enabled:true,resourceIds:[],agvSiteId:null,layerId:currentLayer.value});},'新增节点'))pick('node',id);}
  function addScene(p:Point){let id='';if(edit(()=>{geometry.requireLayer(d.value,currentLayer.value);id=geometry.uniqueId('S',plan.value.scene.map(o=>o.id));plan.value.scene.push({id,name:'设备区',kind:'equipment',...p,width:4,height:3,color:'#3a7897',visible:true,layerId:currentLayer.value});},'新增场景'))pick('scene',id);}
  function finishRoad(){const type=tool.value==='arc'?'CircularArc':tool.value==='bezier'?'CubicBezier':'Line';let id='';if(edit(()=>{id=geometry.addRoad(d.value,roadStart.value,roadEnd.value,type,draft.value,currentLayer.value).id;},`绘制${type==='Line'?'直线':type==='CircularArc'?'圆弧':'贝塞尔道路'}`)){pick('edge',id);cancelDrawing();}else draft.value=[];}
  function toolPoint(p:Point,nodeId?:string){const t=tool.value;if(t==='node'){addNode(snapped(p));return;}
    if(t==='scene'){addScene(snapped(p));return;}
    if(t==='boundary'){const next=snapped(p);if(!draft.value.length||Math.hypot(next.x-draft.value.at(-1)!.x,next.y-draft.value.at(-1)!.y)>1e-8)draft.value.push(next);return;}
    if(t==='measure'){if(measure.value.length===2)measure.value=[];measure.value.push(snapped(p));return;}
    if(t==='edge'||t==='arc'||t==='bezier'){
      if(!roadStart.value||!roadEnd.value){if(!nodeId){message.value='请点击已有节点选择道路端点。';return;}if(!roadStart.value){roadStart.value=nodeId;return;}if(nodeId===roadStart.value){message.value='终点必须与起点不同。';return;}roadEnd.value=nodeId;if(t==='edge')finishRoad();}
      else {draft.value.push(snapped(p));if(draft.value.length===(t==='arc'?1:2))finishRoad();}
    }
  }
  function finishBoundary(){if(!geometry.validPolygon(draft.value)){message.value='区域至少需要3个不共线顶点，且边界不能自交。';return;}let id='';if(edit(()=>{geometry.requireLayer(d.value,currentLayer.value);id=geometry.uniqueId('R',d.value.resources.map(r=>r.id));d.value.resources.push({id,type:'NarrowAisle',capacity:1,boundary:clone(draft.value),layerId:currentLayer.value});},'绘制共享区域边界')){pick('resource',id);cancelDrawing();}}
  function brush(kind:ObjectKind,id:string){if(kind!=='node'&&kind!=='edge')return;const key=kind+id;if(brushSeen.has(key))return;const resource=d.value.resources.find(r=>r.id===brushResource.value);if(!resource){message.value='先选择要刷关联的共享区域。';return;}const o=(kind==='node'?d.value.nodes:d.value.edges).find(o=>o.id===id);if(!o||!geometry.editable(d.value,o))return;
    o.resourceIds=brushRemove.value?o.resourceIds.filter(r=>r!==resource.id):[...new Set([...o.resourceIds,resource.id])];brushSeen.add(key);
  }
  function objectAt(target:EventTarget|null){const el=target instanceof Element?target.closest<SVGElement>('[data-kind][data-id]'):null;return el?{kind:el.dataset.kind as ObjectKind,id:el.dataset.id!}:null;}
  function down(e:PointerEvent){if(e.button!==0&&e.button!==1&&e.button!==2)return;const p=point(e),m=svg.value?.getScreenCTM()?.inverse();if(!p||!m)return;e.preventDefault();svg.value?.focus();const obj=objectAt(e.target),panGesture=e.button!==0||tool.value==='pan'||space.value;
    if(!panGesture&&tool.value!=='select'&&tool.value!=='brush'){toolPoint(p,obj?.kind==='node'?obj.id:undefined);return;}
    const base:Gesture={pointer:e.pointerId,mode:'pan',start:p,client:{x:e.clientX,y:e.clientY},inverse:m,before:clone(plan.value),selection:clone(selection.value),pan:clone(pan.value),moved:false};
    if(!panGesture&&tool.value==='brush'){base.mode='brush';brushSeen=new Set();if(obj)brush(obj.kind,obj.id);}
    else if(!panGesture&&obj&&!e.altKey){
      const target=e.target as SVGElement;const control=target.dataset.control;
      const item=geometry.objects(plan.value,obj.kind).find(o=>o.id===obj.id);if(!item||!geometry.editable(d.value,item)){message.value='当前图层不可编辑。';return;}
      if(e.ctrlKey||e.metaKey||e.shiftKey){pick(obj.kind,obj.id,e);return;}
      if(!selection.value[obj.kind].includes(obj.id))pick(obj.kind,obj.id);
      if(control!==undefined&&obj.kind==='edge'){base.mode='control';base.id=obj.id;base.index=Number(control);}
      else if(obj.kind==='node'||obj.kind==='resource'||obj.kind==='scene'){base.mode='move';base.kind=obj.kind;base.id=obj.id;base.selection=clone(selection.value);}
      else return;
    }else if(!panGesture){base.mode='box';if(!e.ctrlKey&&!e.metaKey&&!e.shiftKey)clear();base.selection=clone(selection.value);box.value={a:p,b:p};}
    gesture=base;svg.value?.setPointerCapture(e.pointerId);
  }
  function move(e:PointerEvent){const p=point(e);if(p)cursor.value=gesture?p:snapped(p);const g=gesture;if(!g||e.pointerId!==g.pointer)return;const next=point(e,g.inverse);if(!next)return;
    g.moved ||= Math.hypot(e.clientX-g.client.x,e.clientY-g.client.y)>3;
    if(g.mode==='pan'){pan.value={x:g.pan.x-(next.x-g.start.x),y:g.pan.y+(next.y-g.start.y)};return;}
    if(g.mode==='box'){box.value={a:g.start,b:next};return;}
    if(g.mode==='brush'){const obj=objectAt(document.elementFromPoint(e.clientX,e.clientY));if(obj)brush(obj.kind,obj.id);return;}
    if(!g.moved)return;
    try{if(g.mode==='control'){const road=d.value.edges.find(e=>e.id===g.id)!;geometry.requireEditable(d.value,[road]);road.controlPoints![g.index!]=snapped(next);road.distance=geometry.edgeLength(d.value,road);}
      else {const before=g.before,ids=new Set(g.selection.node),originNodes=new Map(before.definition.nodes.map(n=>[n.id,n])),originEdges=new Map(before.definition.edges.map(e=>[e.id,e]));
        for(const n of d.value.nodes)if(ids.has(n.id)){const original=originNodes.get(n.id)!;n.x=original.x;n.y=original.y;}
        for(const e of d.value.edges)if(ids.has(e.from)||ids.has(e.to)){const original=originEdges.get(e.id)!;e.controlPoints=clone(original.controlPoints);e.distance=original.distance;}
        for(const r of d.value.resources)if(g.selection.resource.includes(r.id))r.boundary=clone(before.definition.resources.find(o=>o.id===r.id)!.boundary);
        for(const o of plan.value.scene)if(g.selection.scene.includes(o.id)){const original=before.scene.find(s=>s.id===o.id)!;o.x=original.x;o.y=original.y;}
        const anchor=g.kind==='node'?d.value.nodes.find(n=>n.id===g.id)!:g.start;
        const desired={x:anchor.x+next.x-g.start.x,y:anchor.y+next.y-g.start.y},snap=snapped(desired,g.selection.node);geometry.translate(plan.value,g.selection,snap.x-anchor.x,snap.y-anchor.y);}
    }catch(ex){plan.value=clone(g.before);message.value=String(ex instanceof Error?ex.message:ex);}
  }
  function up(e:PointerEvent){const g=gesture;if(!g||g.pointer!==e.pointerId)return;gesture=null;release(e.pointerId);
    if(g.mode==='box'){if(g.moved&&box.value){const next=geometry.boxSelection(plan.value,box.value.a,box.value.b);for(const k of Object.keys(next) as ObjectKind[])next[k]=[...new Set([...g.selection[k],...next[k]])];setSelection(next);}box.value=null;}
    else if(g.mode==='move'||g.mode==='control'||g.mode==='brush'){const issues=validateDefinition(d.value);if(issues.length){plan.value=g.before;message.value=issues.join('；');}else record(g.before,g.mode==='move'?'画布拖动':g.mode==='control'?'调整曲线控制点':'共享区域关联刷');refreshInspector();}
    guides.value=[];snapTarget.value='';
  }
  function wheel(e:WheelEvent){if(gesture)return;const before=point(e);if(!before)return;const factor=Math.exp(-e.deltaY*.0015),next=Math.max(.1,Math.min(40,zoom.value*factor)),ratio=zoom.value/next;
    const c=camera.value,w=c.w/zoom.value,h=c.h/zoom.value,left=c.x+(c.w-w)/2+pan.value.x,top=c.y+(c.h-h)/2+pan.value.y;
    zoom.value=next;const nw=c.w/next,nh=c.h/next;pan.value={x:before.x-(before.x-left)*ratio-c.x-(c.w-nw)/2,y:-before.y-(-before.y-top)*ratio-c.y-(c.h-nh)/2};
  }
  function selectedOperation(fn:()=>void,label:string){if(!count.value){message.value='请先选择对象。';return;}edit(fn,label);}
  function remove(){selectedOperation(()=>{geometry.deleteSelection(plan.value,selection.value);clear();},'批量删除（含关联道路 / 引用）');}
  function copy(){if(!count.value){message.value='请先选择对象。';return;}clipboard.value=geometry.copySelection(plan.value,selection.value);pasteCount.value=0;message.value='已复制节点、内部道路及选中对象到编辑器剪贴板；粘贴节点将清空厂家站点绑定。';}
  function pasteObjects(){if(!clipboard.value){message.value='编辑器剪贴板为空，请先复制对象。';return;}let next:Selection|undefined;const offset=++pasteCount.value*Math.max(gridStep.value,1);if(edit(()=>{next=geometry.paste(plan.value,clipboard.value!,currentLayer.value,offset,offset);},'粘贴对象'))setSelection(next!);}
  function duplicate(){copy();if(clipboard.value)pasteObjects();}
  function align(mode:Alignment){selectedOperation(()=>geometry.alignNodes(d.value,selection.value.node,mode),'对齐 / 等间距分布');}
  function transform(mode:'rotate'|'rotateCW'|'mirrorX'|'mirrorY'){selectedOperation(()=>geometry.transformSelection(plan.value,selection.value,mode),'旋转 / 镜像变换');}
  function moveSelected(){selectedOperation(()=>geometry.translate(plan.value,selection.value,Number(batch.dx),Number(batch.dy)),'批量移动');}
  function merge(){selectedOperation(()=>{geometry.mergeNodes(plan.value,selection.value.node);clear();},'合并两个节点');}
  function reverse(){if(!selection.value.edge.length){message.value='请先选择道路。';return;}edit(()=>geometry.reverseRoads(d.value,selection.value.edge),'反转道路方向');}
  function split(){if(selection.value.edge.length!==1){message.value='拆分需要选择一条道路。';return;}let id='';if(edit(()=>{id=geometry.splitRoad(d.value,selection.value.edge[0]!,Number(batch.splitAt)/100);},'拆分道路'))pick('node',id);}
  function roadBatch(changes:{speedLimit?:number;enabled?:boolean;bidirectional?:boolean}){if(!selection.value.edge.length){message.value='请先选择道路。';return;}edit(()=>geometry.batchRoads(d.value,selection.value.edge,changes),'批量道路属性');}
  function enabled(value:boolean){selectedOperation(()=>{geometry.requireEditable(d.value,d.value.nodes.filter(n=>selection.value.node.includes(n.id)));for(const n of d.value.nodes.filter(n=>selection.value.node.includes(n.id)))n.enabled=value;geometry.batchRoads(d.value,selection.value.edge,{enabled:value});},value?'批量启用':'批量停用');}
  function connect(mode:'chain'|'orthogonal'|'loop'){edit(()=>geometry.connectNodes(d.value,selection.value.node,mode,currentLayer.value),({chain:'自动顺序连接',orthogonal:'正交连接',loop:'闭合环路'})[mode]);}
  function cleanup(){let n=0;if(edit(()=>{n=geometry.cleanTopology(d.value);},'拓扑清理'))message.value=`移除 ${n} 条失效、自环或属性完全相同的重复道路；保留独立节点及不同通行属性。`;}
  function intersections(){let n=0;if(edit(()=>{n=geometry.splitIntersections(d.value,selection.value.edge);},'拆分直线交叉路口'))message.value=`拆分 ${n} 条同图层直线道路；曲线和不同图层的交叉保持原样。`;}
  function writeSettings(){d.value.layers=clone(layerList.value);d.value.editorSettings={...(d.value.editorSettings as object??{}),worldWidth:camera.value.w,worldHeight:camera.value.h,gridSize:gridStep.value,showGrid:grid.value,snapToGrid:snapGrid.value,snapToExisting:snapNodes.value,snapToAlignment:snapAlignment.value,currentLayerId:currentLayer.value};}
  function settings(){if(!(Number.isFinite(gridStep.value)&&gridStep.value>0)){message.value='网格间距必须大于0。';gridStep.value=1;return;}edit(writeSettings,'更新网格 / 吸附设置');}
  function updateLayer(id:string,changes:Partial<Layer>){edit(()=>{d.value.layers=layerList.value.map(l=>l.id===id?{...l,...changes}:l);},'更新图层');}
  function addLayer(){const name=batch.newLayerName.trim();if(!name){message.value='请输入图层名称。';return;}edit(()=>{const id=geometry.uniqueId('layer-',layerList.value.map(l=>l.id));d.value.layers=[...clone(layerList.value),{id,name,visible:true,isLocked:false,isAllowSelect:true,alpha:1,strokeWidth:2}];currentLayer.value=id;writeSettings();},'新增图层');}
  function deleteLayer(id:string){if(id==='navigation'){message.value='导航图层是默认图层，不能删除；可以调整显示或锁定。';return;}const target=layerList.value.find(l=>l.id!==id&&l.visible&&!l.isLocked&&l.isAllowSelect);if(!target){message.value='至少保留一个可编辑图层。';return;}if(layerList.value.find(l=>l.id===id)?.isLocked){message.value='请先解锁图层再删除。';return;}
    edit(()=>{for(const item of [...d.value.nodes,...d.value.edges,...d.value.resources,...plan.value.scene])if(geometry.layerId(item)===id)item.layerId=target.id;d.value.layers=layerList.value.filter(l=>l.id!==id);if(currentLayer.value===id)currentLayer.value=target.id;writeSettings();},'删除图层并迁移对象');}
  function moveToLayer(){selectedOperation(()=>{geometry.requireLayer(d.value,currentLayer.value);for(const k of Object.keys(selection.value) as ObjectKind[])for(const o of geometry.objects(plan.value,k).filter(o=>selection.value[k].includes(o.id))){geometry.requireEditable(d.value,[o]);o.layerId=currentLayer.value;}},'移动对象到当前图层');}
  function layerStyle(o:{layerId?:string}){const l=layerList.value.find(l=>l.id===geometry.layerId(o));return{opacity:l?.alpha??1,color:l?.color||undefined};}
  function color(o:{layerId?:string},fallback:string){return layerList.value.find(l=>l.id===geometry.layerId(o))?.color||fallback;}
  function lineWidth(o:{layerId?:string}){return scale.value*(layerList.value.find(l=>l.id===geometry.layerId(o))?.strokeWidth??2)/10;}
  function selectAll(){const next=geometry.emptySelection();for(const k of (tab.value==='scene'?['scene']:['node','edge','resource']) as ObjectKind[])next[k]=geometry.objects(plan.value,k).filter(o=>geometry.editable(d.value,o)).map(o=>o.id);setSelection(next);}
  function keyboard(e:KeyboardEvent){if(tab.value!=='map'&&tab.value!=='scene')return;const target=e.target as HTMLElement;
    if(target?.closest('input,textarea,select,[contenteditable="true"],[role="dialog"],.console-dialog'))return;
    const ctrl=e.ctrlKey||e.metaKey,key=e.key.toLowerCase();
    if(ctrl&&['z','y','s','c','v','a','d'].includes(key)){e.preventDefault();if(key==='z')history(e.shiftKey);else if(key==='y')history(true);else if(key==='s')save();else if(key==='c')copy();else if(key==='v')pasteObjects();else if(key==='a')selectAll();else duplicate();return;}
    if(key==='escape'){e.preventDefault();cancel();}else if(key==='delete'){e.preventDefault();remove();}else if(key==='enter'&&tool.value==='boundary'){e.preventDefault();finishBoundary();}else if(key==='backspace'&&draft.value.length){e.preventDefault();draft.value.pop();}else if(e.code==='Space'){e.preventDefault();space.value=true;}
  }
  const keyup=(e:KeyboardEvent)=>{if(e.code==='Space')space.value=false;};const blur=()=>{space.value=false;cancelGesture();};
  onMounted(()=>{window.addEventListener('keydown',keyboard);window.addEventListener('keyup',keyup);window.addEventListener('blur',blur);});
  onUnmounted(()=>{cancelGesture();observer?.disconnect();window.removeEventListener('keydown',keyboard);window.removeEventListener('keyup',keyup);window.removeEventListener('blur',blur);});
  watch(()=>d.value.editorSettings,settings=>{if(!settings||typeof settings!=='object')return;const s=settings as any;gridStep.value=Number.isFinite(s.gridSize)&&s.gridSize>0?s.gridSize:1;grid.value=s.showGrid!==false;snapGrid.value=s.snapToGrid!==false;snapNodes.value=s.snapToExisting!==false;snapAlignment.value=s.snapToAlignment!==false;currentLayer.value=s.currentLayerId||'navigation';},{immediate:true});
  return reactive({svg,selection,count,tool,panel,undoStack,redoStack,clipboard,zoom,pan,camera,viewBox,scale,grid,gridStep,snapGrid,snapNodes,snapAlignment,currentLayer,layerList,guides,snapTarget,cursor,box,draft,measure,measurement,roadStart,roadEnd,draftPath,toolHint,batch,brushResource,brushRemove,visibleNodes,visibleEdges,visibleResources,visibleScene,space,
    edit,history,restore,pick,clear,setSelection,isSelected,fit,down,move,up,wheel,cancel,cancelGesture,changeTool,finishBoundary,remove,copy,pasteObjects,duplicate,align,transform,moveSelected,merge,reverse,split,roadBatch,enabled,connect,cleanup,intersections,settings,updateLayer,addLayer,deleteLayer,moveToLayer,layerStyle,color,lineWidth,selectAll,addNode,addScene});
}
