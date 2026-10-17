<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch, toRef } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { IconMap, IconDeviceFloppy, IconDownload, IconUpload, IconPlus, IconTrash, IconArrowUp, IconArrowDown, IconCopy, IconRefresh, IconZoomIn, IconZoomOut, IconPlugConnected, IconCloudUpload, IconArrowLeft, IconEye } from '@tabler/icons-vue';
import ConsoleDialog from './ConsoleDialog.vue';
import MapEditorPanels from './MapEditorPanels.vue';
import './map-editor.css';
import { useMapEditor } from './use-map-editor';
import * as geometry from './map-editing';
import { useDispatchStore } from './stores/dispatch';
import { useToastStore } from './stores/toast';
import { clone, emptyPlan, parsePlan, validateDefinition, validateVehicles, routePlan, edgePath, type Plan, type Node, type Edge, type Resource, type VehicleConfig, type SceneItem } from './workbench';
import type { Snapshot, MapSummary } from './types';
const props=defineProps<{source?:'demo'|'backend';snapshot?:Snapshot|null;initialTab?:string}>();
const store = useDispatchStore();
const toastStore = useToastStore();
const currentRoute = useRoute();
const mapRouter = useRouter();
const effectiveSource = computed((): 'demo' | 'backend' => props.source ?? (store.fresh && store.snapshot?.source==='backend' && !store.demoMode ? 'backend' : 'demo'));
const effectiveSnapshot = computed((): Snapshot | null => props.snapshot ?? store.snapshot);
const uuid=()=>crypto.randomUUID();
const vehicleMessage=ref('');
const queryTab = computed(() => (currentRoute.query.tab as string) || props.initialTab || 'map');
const queryMapId = computed(() => currentRoute.query.mapId as string | undefined);
const tab=ref(queryTab.value);
watch(queryTab, v => { if (v) tab.value = v; });
watch(()=>props.initialTab,v=>{if(v)tab.value=v;});
const plan=ref<Plan>(emptyPlan()),saved=ref<Plan[]>([]),dirty=ref(false),message=ref(''),busy=ref(false);
const selected=ref(''),kind=ref<'node'|'edge'|'resource'|'scene'>('node');
const nodeForm=ref<Node>(),edgeForm=ref<Edge>(),resourceForm=ref<Resource>(),sceneForm=ref<SceneItem>();
const ed=useMapEditor(plan,tab,message,()=>{dirty.value=true;},inspectObject,saveLocal);
const tool=toRef(ed,'tool'),undoStack=computed(()=>ed.undoStack),redoStack=computed(()=>ed.redoStack);
const pending=ref<Plan|null>(null),importInput=ref<HTMLInputElement>();
const maps=ref<MapSummary[]>([]),mapId=ref(''),workspace=ref<any>(null),versions=ref<any[]>([]),vehicleRevision=ref(''),canManage=ref(false),restart=ref(false);
const vehicleForm=ref<VehicleConfig|null>(null),vehicleOriginal=ref(''),waypoint=ref('');
const svg=toRef(ed,'svg');
const editorPanels=[['properties','属性'],['batch','批量'],['layers','图层'],['history','历史']] as const;
const mapTools=[{id:'select',label:'选择 / 框选'},{id:'pan',label:'平移'},{id:'node',label:'节点'},{id:'edge',label:'直线'},{id:'arc',label:'圆弧'},{id:'bezier',label:'贝塞尔'},{id:'boundary',label:'区域边界'},{id:'brush',label:'关联刷'},{id:'measure',label:'测量'}] as const;
const sceneTools=[{id:'select',label:'选择 / 框选'},{id:'pan',label:'平移'},{id:'scene',label:'放置场景'},{id:'measure',label:'测量'}] as const;
const tabs=[{id:'map',name:'地图编辑'},{id:'scene',name:'场景编辑'},{id:'plans',name:'方案管理'},{id:'vehicles',name:'车辆配置'},{id:'waypoints',name:'航点顺序'}];
const definition=computed(()=>plan.value.definition),issues=computed(()=>validateDefinition(definition.value));
const route=computed(()=>routePlan(definition.value,plan.value.waypoints));
const viewBox=computed(()=>ed.viewBox),scale=computed(()=>ed.scale),nodeRadius=computed(()=>ed.scale*.55);
const counts=computed(()=>`${definition.value.nodes.length} 航点 · ${definition.value.edges.length} 道路 · ${definition.value.resources.length} 共享区域`);
const vehicleErrors=computed(()=>validateVehicles(plan.value.vehicles));
let epoch=0;
watch(effectiveSource,()=>{++epoch;workspace.value=null;maps.value=[];versions.value=[];vehicleRevision.value='';canManage.value=false;message.value='数据源已切换。编辑稿保留，后台关联已解除，请重新读取目标。';});
function edit(fn:()=>void,label='应用属性 / 编辑方案'){return ed.edit(fn,label);}
function history(redo=false){ed.history(redo);}
function resetSelection(){ed.clear();}
function select(type:typeof kind.value,id:string,event?:MouseEvent){ed.pick(type,id,event);}
function inspectObject(type:geometry.ObjectKind|null,id:string){
 selected.value=id;nodeForm.value=undefined;edgeForm.value=undefined;resourceForm.value=undefined;sceneForm.value=undefined;
 if(!type)return;kind.value=type;
 if(type==='node')nodeForm.value=clone(plan.value.definition.nodes.find(n=>n.id===id));
 if(type==='edge')edgeForm.value=clone(plan.value.definition.edges.find(n=>n.id===id));
 if(type==='resource')resourceForm.value=clone(plan.value.definition.resources.find(n=>n.id===id));
 if(type==='scene')sceneForm.value=clone(plan.value.scene.find(n=>n.id===id));
}
const activeForm=computed(()=>kind.value==='node'?nodeForm.value:kind.value==='edge'?edgeForm.value:kind.value==='resource'?resourceForm.value:sceneForm.value);
function unique(prefix:string,ids:string[]){let n=1;while(ids.includes(`${prefix}${n}`))n++;return `${prefix}${n}`;}
function applyInspector(){
 const type=kind.value,old=selected.value,obj=type==='node'?nodeForm.value:type==='edge'?edgeForm.value:type==='resource'?resourceForm.value:sceneForm.value;if(!obj)return;
 const next=clone(plan.value),d=next.definition,list:any[]=type==='node'?d.nodes:type==='edge'?d.edges:type==='resource'?d.resources:next.scene;
 if(!obj.id.trim()||list.some(n=>n.id!==old&&n.id.toLowerCase()===obj.id.toLowerCase())){message.value='编号不能为空或重复。';return;}list[list.findIndex(n=>n.id===old)]=clone(obj);
 if(type==='node'){for(const e of d.edges){if(e.from===old)e.from=obj.id;if(e.to===old)e.to=obj.id;if(e.from===obj.id||e.to===obj.id)e.distance=geometry.edgeLength(d,e);}next.waypoints=next.waypoints.map(id=>id===old?obj.id:id);}
 if(type==='resource')for(const item of [...d.nodes,...d.edges])item.resourceIds=item.resourceIds.map(id=>id===old?obj.id:id);
 try{parsePlan(JSON.stringify(next));if(edit(()=>{const original=geometry.objects(plan.value,type).find(o=>o.id===old);if(original)geometry.requireEditable(definition.value,[original]);if(type==='node')geometry.requireEditable(definition.value,definition.value.edges.filter(e=>e.from===old||e.to===old));if(type==='resource')geometry.requireEditable(definition.value,[...definition.value.nodes,...definition.value.edges].filter(o=>o.resourceIds.includes(old)));geometry.requireLayer(next.definition,geometry.layerId(obj));plan.value=next;},'应用对象属性')){select(type,obj.id);message.value='属性已应用至编辑稿，尚未保存后台。';}}catch(ex){message.value=String(ex);}
}
function removeSelected(){ed.remove();}
function fit(){ed.fit();}
function replace(next:Plan){edit(()=>plan.value=clone(next));resetSelection();workspace.value=null;vehicleRevision.value='';canManage.value=false;fit();}
function saveLocal(){try{parsePlan(JSON.stringify(plan.value));const next={...clone(plan.value),updatedAt:new Date().toISOString()};const list=[next,...saved.value.filter(p=>p.id!==next.id)];if(list.length>20)throw new Error('最多保存20个本机方案，请先导出备份再删除旧方案');localStorage.setItem('agv-web-plans-v1',JSON.stringify(list));saved.value=list;plan.value=next;dirty.value=false;message.value='已保存到此浏览器。本机方案不是后台运行配置，请定期导出文件备份。';}catch(ex){message.value=String(ex);}}
function deleteLocal(id:string){try{const list=saved.value.filter(p=>p.id!==id);localStorage.setItem('agv-web-plans-v1',JSON.stringify(list));saved.value=list;message.value='已移出本机方案列表；当前编辑稿仍保留。';}catch(ex){message.value=String(ex);}}
function download(value:unknown,suffix:string){const blob=new Blob([JSON.stringify(value,null,2)],{type:'application/json'}),url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download=`${plan.value.name.replace(/[<>:"/\\|?*]/g,'_')}${suffix}.json`;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}
function exportPlan(){try{parsePlan(JSON.stringify(plan.value));download({...plan.value,updatedAt:new Date().toISOString()},'-方案');message.value='方案已导出；包含场景、车辆配置及航点顺序，不包含PLC点表、权限或执行开关。';}catch(ex){message.value=String(ex);}}
function exportMap(){if(issues.value.length){message.value=issues.value.join('；');return;}download({...definition.value,source:''},'-规范化地图');}
async function request<T>(path:string,data?:unknown):Promise<T>{const response=await fetch(path,{method:data===undefined?'GET':'POST',headers:data===undefined?undefined:{'Content-Type':'application/json'},body:data===undefined?undefined:JSON.stringify(data),signal:AbortSignal.timeout(35000)});const result=await response.json();if(!response.ok)throw new Error(result.detail??result.error??`HTTP ${response.status}`);return result;}
async function run(fn:(mine:number)=>Promise<void>){if(busy.value)return;busy.value=true;message.value='处理中…';const mine=epoch;try{await fn(mine);}catch(ex){if(mine===epoch)message.value=String(ex);}finally{busy.value=false;}}
async function importFile(e:Event){const input=e.target as HTMLInputElement,file=input.files?.[0];input.value='';if(!file)return;if(file.size>8*1024*1024){message.value='文件超过8MiB；大型原生地图请先在WPF导入。';return;}
 await run(async mine=>{if(file.name.toLowerCase().endsWith('.smap')){if(effectiveSource.value!=='backend')throw new Error('SMAP需要连接后台使用现有厂家解析器；示例模式可导入本工具方案或规范化地图JSON。');const response=await fetch('/api/maps/import?format=smap',{method:'POST',body:file,headers:{'Content-Type':'application/octet-stream'},signal:AbortSignal.timeout(35000)});const result=await response.json();if(!response.ok||!result.accepted||!result.definition)throw new Error(result.detail??result.message??result.error??'地图解析失败');if(mine!==epoch)return;pending.value=parsePlan(JSON.stringify(result.definition));message.value=result.message;}
 else{const next=parsePlan(await file.text());if(mine!==epoch)return;pending.value=next;message.value='文件已解析，核对后点击“加载到编辑稿”。不会切换运行地图。';}});}
function importRuntime(){const map=effectiveSnapshot.value?.map;if(!map){message.value='没有可复制的运行地图。';return;}if(map.edges.some(e=>e.kind!=='Line')){message.value='运行图包含曲线，请连接后台读取完整草稿，避免丢失曲线控制点。';return;}const next=emptyPlan();next.name=`${map.name} 副本`;next.definition={name:map.name,source:'',coordinateSystem:map.coordinateSystem,unit:map.unit,nodes:map.nodes.map(n=>({...n,type:['Waypoint','Station','Intersection','WaitingPoint','ChargingPoint'].includes(n.type)?n.type:n.id==='WAIT'?'WaitingPoint':'Waypoint',agvSiteId:n.agvSiteId??null,resourceIds:[]})),edges:map.edges.filter(e=>e.kind==='Line').map(e=>({id:e.id,from:e.from,to:e.to,distance:e.points.reduce((sum,p,i,a)=>i?sum+Math.hypot(p.x-a[i-1]!.x,p.y-a[i-1]!.y):0,0),speedLimit:.5,enabled:e.enabled,bidirectional:e.bidirectional,resourceIds:[],geometryKind:'Line'})),resources:map.resources.map(r=>({...r,type:'NarrowAisle'}))};pending.value=next;message.value='运行图仅提供展示投影，副本不含完整道路属性/曲线；后台请优先“读取完整草稿”。';}
async function listMaps(){if(effectiveSource.value!=='backend'){message.value='示例模式没有后台地图目录，可使用本机方案。';return;}await run(async mine=>{const result=await request<MapSummary[]>('/api/maps');if(mine!==epoch)return;maps.value=result;mapId.value||=result[0]?.mapId??'';message.value=`找到 ${result.length} 张后台地图`;});}
function acceptWorkspace(result:any){workspace.value=result;mapId.value=result.project.mapId;plan.value.definition=clone(result.draft.definition);dirty.value=true;resetSelection();fit();}
async function loadWorkspace(){if(effectiveSource.value!=='backend'||!mapId.value)return;await run(async mine=>{const result=await request<any>(`/api/maps/${encodeURIComponent(mapId.value)}/workspace`);if(mine!==epoch)return;if(!result?.draft)throw new Error('没有可编辑草稿');parsePlan(JSON.stringify(result.draft.definition));edit(()=>acceptWorkspace(result));versions.value=await request<any[]>(`/api/maps/${encodeURIComponent(mapId.value)}/versions`);if(mine!==epoch){workspace.value=null;return;}message.value='完整草稿已加载并替换当前地图。场景、车辆及航点顺序请重新核对。';plan.value.waypoints=plan.value.waypoints.filter(id=>definition.value.nodes.some(n=>n.id===id));});}
async function createBackend(){if(effectiveSource.value!=='backend')return;await run(async mine=>{const d=definition.value,result=await request<any>('/api/maps/create',{name:d.name,description:plan.value.name,backgroundFilePath:'',coordinateSystem:d.coordinateSystem,unit:d.unit,coordinateScale:1});if(mine!==epoch)return;if(!result.accepted)throw new Error(result.message);workspace.value=result.workspace;mapId.value=result.workspace.project.mapId;message.value=`${result.message} 请继续保存当前编辑稿。`;});}
async function saveBackend(){if(!workspace.value||issues.value.length||effectiveSource.value!=='backend')return;await run(async mine=>{const w=workspace.value,d=clone(definition.value),result=await request<any>('/api/maps/draft',{mapId:w.project.mapId,versionId:w.draft.versionId,name:d.name,description:w.project.description,backgroundFilePath:w.project.backgroundFilePath,coordinateSystem:d.coordinateSystem,unit:d.unit,coordinateScale:w.project.coordinateScale,definition:d,expectedEditToken:w.draft.editToken});if(mine!==epoch)return;if(!result.accepted)throw new Error(result.message);workspace.value=result.workspace;message.value=`${result.message} 场景/方案仍需单独保存本机；运行地图未切换。`;});}
async function publish(){if(!workspace.value||effectiveSource.value!=='backend')return;const publishMapId=workspace.value.project.mapId;await run(async mine=>{const w=workspace.value,result=await request<any>('/api/maps/publish',{mapId:w.project.mapId,versionId:w.draft.versionId});if(mine!==epoch)return;if(!result.accepted)throw new Error(result.message);workspace.value=null;toastStore.success('草稿已发布');message.value=`${result.message} 已发布，请前往地图管理页激活。`;publishedMapId.value=publishMapId;});}
const publishedMapId=ref('');
function goToActivate(){mapRouter.push({name:'maps'});}
function goToMapManagement(){mapRouter.push({name:'maps'});}
function previewOnDashboard(){store.previewMap=effectiveSnapshot.value?.map??null;mapRouter.push({name:'overview'});}
function openVehicle(v?:VehicleConfig){vehicleMessage.value='';vehicleOriginal.value=v?.vehicleId??'';vehicleForm.value=v?clone(v):{vehicleId:unique('AGV-',plan.value.vehicles.map(v=>v.vehicleId)),displayName:'新增车辆',ipAddress:'',port:19204,navigationPort:19206,enabled:false};}
function applyVehicle(){const v=vehicleForm.value;if(!v)return;const next=clone(plan.value.vehicles),index=next.findIndex(n=>n.vehicleId===vehicleOriginal.value);if(index<0)next.push(v);else next[index]=v;const errors=validateVehicles(next);if(errors.length){vehicleMessage.value=errors.join('；');return;}edit(()=>plan.value.vehicles=clone(next));vehicleForm.value=null;message.value='车辆已加入配置编辑稿；不代表已连接或可派车。';}
async function readVehicles(){if(effectiveSource.value!=='backend'){message.value='示例模式只编辑本机车辆方案，不生成在线状态。';return;}await run(async mine=>{const result=await request<any>('/api/vehicles/configuration');if(mine!==epoch)return;edit(()=>plan.value.vehicles=result.vehicles);vehicleRevision.value=result.revision;restart.value=result.restartRequired;canManage.value=result.canManage;message.value='已读取后台保存的车辆配置，可能与当前活动配置不同。保存后需服务重启生效。';});}
async function saveVehicles(){if(effectiveSource.value!=='backend'||!vehicleRevision.value||vehicleErrors.value.length)return;await run(async mine=>{const result=await request<any>('/api/vehicles/configuration',{expectedRevision:vehicleRevision.value,vehicles:clone(plan.value.vehicles)});if(mine!==epoch)return;vehicleRevision.value=result.revision;restart.value=result.restartRequired;message.value='后台车辆配置已保存，服务重启后生效。PLC、地图绑定及全局通信开关均保留，未在此启动设备通信。';});}
function reorder(index:number,delta:number){if(index+delta<0||index+delta>=plan.value.waypoints.length)return;edit(()=>{const list=plan.value.waypoints;[list[index],list[index+delta]]=[list[index+delta]!,list[index]!];});}
async function pushOfflineToBackend(){if(effectiveSource.value!=='backend'||issues.value.length)return;await run(async mine=>{const d=definition.value;const result=await request<any>('/api/maps/create',{name:d.name,description:plan.value.name,backgroundFilePath:'',coordinateSystem:d.coordinateSystem,unit:d.unit,coordinateScale:1});if(mine!==epoch)return;if(!result.accepted)throw new Error(result.message);workspace.value=result.workspace;mapId.value=result.workspace.project.mapId;toastStore.success('已推送到后台新建地图');message.value=`${result.message} 请继续保存当前编辑稿到后台草稿。`;});}
function beforeUnload(e:BeforeUnloadEvent){if(dirty.value){e.preventDefault();e.returnValue='';}}
onMounted(async()=>{window.addEventListener('beforeunload',beforeUnload);try{const raw=localStorage.getItem('agv-web-plans-v1');if(raw){const data=JSON.parse(raw);if(!Array.isArray(data)||data.length>20)throw new Error('本机方案索引无效');saved.value=data.map(p=>parsePlan(JSON.stringify(p)));}}catch(ex){message.value=`本机方案未加载：${String(ex)}，原内容未覆盖。`;}
if(queryMapId.value&&effectiveSource.value==='backend'){mapId.value=queryMapId.value;message.value=`正在读取后台地图 ${queryMapId.value} 的草稿…`;try{await loadWorkspace();}catch{message.value='自动加载草稿失败，请手动读取。';}}});
onUnmounted(()=>{++epoch;window.removeEventListener('beforeunload',beforeUnload);});
</script>

<template>
 <section class="planning-workbench">
  <header class="workbench-title">
    <button @click="goToMapManagement" class="text-button"><IconArrowLeft :size="14"/>地图管理</button>
    <h2><IconMap :size="21"/>调度工程工作台</h2>
    <input v-model="plan.name" aria-label="方案名称" @change="dirty=true"/>
    <span :class="workspace?'green-text':effectiveSource==='backend'?'cyan':'amber-text'">{{workspace?`已关联后台 ${workspace.project.mapId}`:effectiveSource==='backend'?'后台已连接 · 未关联草稿':'离线模式'}}</span>
    <span :class="dirty?'amber-text':'green-text'">{{dirty?'· 未保存':saved.some(p=>p.id===plan.id)?'· 已保存':'· 新方案'}}</span>
    <button title="Ctrl+S · 保存到本机浏览器" @click="saveLocal"><IconDeviceFloppy :size="15"/>保存方案</button>
    <button @click="exportPlan"><IconDownload :size="15"/>导出方案</button>
    <button @click="importInput?.click()"><IconUpload :size="15"/>加载文件</button>
    <button v-if="!workspace&&effectiveSource==='backend'&&!issues.length" @click="pushOfflineToBackend"><IconCloudUpload :size="15"/>推送到后台新建</button>
    <input ref="importInput" class="file-picker" type="file" accept=".json,.smap" aria-label="导入地图或方案文件" @change="importFile"/>
  </header>
  <div v-if="publishedMapId" class="inline-warning" style="display:flex;align-items:center;gap:12px;justify-content:space-between">
    <span>草稿已发布成功！发布不等于激活，请前往地图管理页激活此地图。</span>
    <button class="accent-button" @click="goToActivate();publishedMapId=''">前往激活 →</button>
  </div>
  <div class="workflow-guide">
    <b>操作流程：</b>
    <span :class="definition.nodes.length?'green-text':'amber-text'">① 编辑地图</span> →
    <span :class="workspace?'green-text':'amber-text'">② 关联后台</span> →
    <span :class="workspace&&!dirty?'green-text':'amber-text'">③ 保存草稿</span> →
    <span :class="publishedMapId?'green-text':'amber-text'">④ 发布</span> →
    <span class="amber-text">⑤ 去地图管理激活</span> →
    <span class="amber-text">⑥ 大屏显示</span>
  </div>
  <nav class="workbench-tabs" aria-label="工程模块"><button v-for="item in tabs" :key="item.id" :class="{active:tab===item.id}" @click="tab=item.id">{{item.name}}</button><small>{{counts}}</small></nav>
  <p class="workbench-message" role="status">{{message||'厂家激光扫图在RoboShop完成；此处编辑调度导航和场景。编辑稿不会自动影响运行地图。'}}</p>
  <template v-if="tab==='map'||tab==='scene'">
   <div class="editor-toolbar editor-tool-palette" aria-label="地图绘图工具">
    <button v-for="item in (tab==='map'?mapTools:sceneTools)" :key="item.id" :class="{active:tool===item.id}" :aria-pressed="tool===item.id" @click="ed.changeTool(item.id)">{{item.label}}</button>
    <span class="tool-divider"></span><button :disabled="!undoStack.length" title="Ctrl+Z" @click="history()">撤销</button><button :disabled="!redoStack.length" title="Ctrl+Y / Ctrl+Shift+Z" @click="history(true)">重做</button>
    <button @click="ed.panel='batch'">批量 / 对齐 / 拓扑</button><button @click="ed.panel='layers'">图层</button><button @click="ed.panel='history'">历史</button>
    <button @click="importRuntime">从运行图复制</button><button @click="exportMap">导出地图JSON</button>
   </div>
   <div class="editor-options">
    <label>当前图层<select v-model="ed.currentLayer" @change="ed.settings"><option v-for="layer in ed.layerList" :key="layer.id" :value="layer.id">{{layer.name}}{{layer.isLocked?' · 锁定':''}}</option></select></label>
    <label><input v-model="ed.grid" type="checkbox" @change="ed.settings"/>网格</label><label>间距(m)<input v-model.number="ed.gridStep" type="number" min=".001" step=".1" @change="ed.settings"/></label>
    <label><input v-model="ed.snapGrid" type="checkbox" @change="ed.settings"/>网格吸附</label><label><input v-model="ed.snapNodes" type="checkbox" @change="ed.settings"/>节点吸附</label><label><input v-model="ed.snapAlignment" type="checkbox" @change="ed.settings"/>对齐参考线</label>
    <span>已选 {{ed.count}}</span><button :disabled="!ed.count" @click="ed.remove">删除所选</button>
    <template v-if="tool==='boundary'"><button :disabled="ed.draft.length<3" @click="ed.finishBoundary">完成边界（Enter）</button><button :disabled="!ed.draft.length" @click="ed.draft.pop()">撤回顶点</button></template>
    <template v-if="tool==='brush'"><label>共享区域<select v-model="ed.brushResource"><option value="">选择共享区域</option><option v-for="r in definition.resources" :key="r.id" :value="r.id">{{r.id}}</option></select></label><label><input v-model="ed.brushRemove" type="checkbox"/>解除关联</label></template>
   </div>
   <p class="editor-tool-hint" role="status">{{ed.toolHint}} <strong v-if="ed.measurement!==null">距离 {{ed.measurement.toFixed(3)}} m</strong><span v-if="ed.snapTarget"> · 吸附 {{ed.snapTarget}}</span></p>
   <div class="editor-layout"><aside class="editor-tree"><h3>{{tab==='scene'?'场景对象':'导航对象'}}</h3><template v-if="tab==='map'"><b>航点 {{definition.nodes.length}}</b><button v-for="n in definition.nodes" :key="n.id" :class="{active:ed.isSelected('node',n.id)}" @click="select('node',n.id,$event)">{{n.id}} <small>{{n.agvSiteId||'未绑定厂家站点'}}</small></button><b>道路 {{definition.edges.length}}</b><button v-for="e in definition.edges" :key="e.id" :class="{active:ed.isSelected('edge',e.id)}" @click="select('edge',e.id,$event)">{{e.id}}<small>{{e.from}}{{e.bidirectional?'↔':'→'}}{{e.to}}</small></button><b>共享区域 {{definition.resources.length}}</b><button v-for="r in definition.resources" :key="r.id" @click="select('resource',r.id,$event)">{{r.id}}<small>容量 {{r.capacity}}</small></button></template><template v-else><button @click="ed.addScene({x:0,y:0})"><IconPlus :size="13"/>新增场景对象</button><button v-for="s in plan.scene" :key="s.id" @click="select('scene',s.id,$event)">{{s.name}}<small>{{s.visible?'可见':'已隐藏'}} · {{s.kind}}</small></button><p class="muted-note">场景图形仅用于显示；设备区不会自动成为障碍或交通锁。</p></template></aside>
    <div class="editor-canvas" :class="{'pan-mode':tool==='pan'||ed.space}">
     <svg ref="svg" :viewBox="viewBox" role="application" tabindex="0" aria-label="调度地图编辑画布，支持框选、节点拖动与键盘快捷键" @pointerdown="ed.down" @pointermove="ed.move" @pointerup="ed.up" @pointercancel="ed.cancelGesture" @lostpointercapture="ed.cancelGesture" @contextmenu.prevent @dblclick.prevent="tool==='boundary'&&ed.finishBoundary()" @wheel.prevent="ed.wheel">
      <defs><pattern id="planning-editor-grid" :width="Math.max(.001,Number(ed.gridStep)||1)" :height="Math.max(.001,Number(ed.gridStep)||1)" patternUnits="userSpaceOnUse"><path :d="`M ${Math.max(.001,Number(ed.gridStep)||1)} 0 L 0 0 0 ${Math.max(.001,Number(ed.gridStep)||1)}`" fill="none" stroke="#1a3550" :stroke-width="scale*.04"/></pattern><marker id="planning-editor-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="#72bfd2"/></marker></defs>
      <rect v-if="ed.grid" :x="Number(viewBox.split(' ')[0])" :y="Number(viewBox.split(' ')[1])" :width="Number(viewBox.split(' ')[2])" :height="Number(viewBox.split(' ')[3])" fill="url(#planning-editor-grid)" pointer-events="none"/>
      <g v-for="s in ed.visibleScene" :key="s.id" data-object="scene" data-kind="scene" :data-id="s.id" :style="ed.layerStyle(s)">
       <rect v-if="s.kind!=='text'" :x="s.x" :y="-s.y-s.height" :width="s.width" :height="s.height" :fill="s.color" fill-opacity=".18" :stroke="ed.isSelected('scene',s.id)?'#e8fbff':s.color" :stroke-width="scale*.12"/><text :x="s.x+scale*.2" :y="-s.y-s.height+scale*1.2" :fill="s.color" :font-size="scale*1.1">{{s.name}}</text>
      </g>
      <polygon v-for="r in ed.visibleResources" :key="r.id" data-object="resource" data-kind="resource" :data-id="r.id" :points="r.boundary?.map(p=>`${p.x},${-p.y}`).join(' ')" fill="#e0b25b" :fill-opacity="ed.isSelected('resource',r.id)?.22:.09" :stroke="ed.isSelected('resource',r.id)?'#fff4d0':ed.color(r,'#b79a68')" :stroke-width="ed.lineWidth(r)" :style="ed.layerStyle(r)"><title>{{r.id}} · {{r.type}}</title></polygon>
      <g v-for="e in ed.visibleEdges" :key="e.id" data-kind="edge" :data-id="e.id" data-object="edge" :style="ed.layerStyle(e)">
       <path :d="edgePath(e,definition)" fill="none" stroke="transparent" :stroke-width="scale*1.6" pointer-events="stroke"/>
       <path :d="edgePath(e,definition)" fill="none" :stroke="ed.isSelected('edge',e.id)?'#effaff':ed.color(e,e.enabled?'#40a6c4':'#566175')" :stroke-width="ed.lineWidth(e)*(ed.isSelected('edge',e.id)?1.6:1)" :marker-end="'url(#planning-editor-arrow)'" :marker-start="e.bidirectional?'url(#planning-editor-arrow)':undefined" pointer-events="none"/><title>{{e.id}} · {{e.from}} → {{e.to}} · {{e.distance.toFixed(2)}} m</title>
      </g>
      <g v-for="n in ed.visibleNodes" :key="n.id" data-object="node" data-kind="node" :data-id="n.id" :style="ed.layerStyle(n)">
       <circle :cx="n.x" :cy="-n.y" :r="scale*1.15" fill="transparent"/><circle :cx="n.x" :cy="-n.y" :r="nodeRadius" :fill="ed.isSelected('node',n.id)?'#effaff':ed.color(n,n.enabled?'#0d344a':'#344254')" :stroke="ed.isSelected('node',n.id)?'#70e0f2':ed.color(n,n.enabled?'#59bfd9':'#71829a')" :stroke-width="scale*.15"/><text :x="n.x+scale*.85" :y="-n.y-scale*.65" fill="#bdcfe3" :font-size="scale*1.05">{{n.id}}</text><title>{{n.id}} · {{n.x.toFixed(3)}}, {{n.y.toFixed(3)}} m</title>
      </g>
      <template v-for="e in ed.visibleEdges.filter(e=>ed.isSelected('edge',e.id)&&e.controlPoints?.length)" :key="`handles-${e.id}`">
       <polyline :points="[definition.nodes.find(n=>n.id===e.from),...(e.controlPoints??[]),definition.nodes.find(n=>n.id===e.to)].filter(Boolean).map(p=>`${p!.x},${-p!.y}`).join(' ')" fill="none" stroke="#d9ad63" :stroke-width="scale*.08" :stroke-dasharray="`${scale*.3} ${scale*.3}`" pointer-events="none"/>
       <circle v-for="(p,index) in e.controlPoints" :key="index" :cx="p.x" :cy="-p.y" :r="scale*.5" fill="#ffc767" stroke="#fff0ce" :stroke-width="scale*.1" data-kind="edge" :data-id="e.id" :data-control="index" class="curve-handle"><title>拖动控制点 {{index+1}}</title></circle>
      </template>
      <g pointer-events="none">
       <path v-if="ed.draftPath" :d="ed.draftPath" fill="none" stroke="#ffe29d" :stroke-width="scale*.16" :stroke-dasharray="`${scale*.4} ${scale*.2}`"/>
       <circle v-for="(p,i) in ed.draft" :key="i" :cx="p.x" :cy="-p.y" :r="scale*.32" fill="#ffd075"/>
       <line v-for="(g,i) in ed.guides" :key="i" :x1="g.axis==='x'?g.value:-1e7" :x2="g.axis==='x'?g.value:1e7" :y1="g.axis==='y'?-g.value:-1e7" :y2="g.axis==='y'?-g.value:1e7" stroke="#e4ad55" :stroke-width="scale*.07" :stroke-dasharray="`${scale*.3} ${scale*.3}`"/>
       <rect v-if="ed.box" :x="Math.min(ed.box.a.x,ed.box.b.x)" :y="-Math.max(ed.box.a.y,ed.box.b.y)" :width="Math.abs(ed.box.b.x-ed.box.a.x)" :height="Math.abs(ed.box.b.y-ed.box.a.y)" fill="#4cb7e0" fill-opacity=".12" stroke="#7bd6f4" :stroke-width="scale*.1"/>
       <g v-if="ed.measure.length"><line :x1="ed.measure[0]!.x" :y1="-ed.measure[0]!.y" :x2="(ed.measure[1]??ed.cursor??ed.measure[0])!.x" :y2="-(ed.measure[1]??ed.cursor??ed.measure[0])!.y" stroke="#ecb960" :stroke-width="scale*.15"/><circle v-for="(p,i) in ed.measure" :key="i" :cx="p.x" :cy="-p.y" :r="scale*.35" fill="#ffe2a4"/></g>
      </g>
     </svg>
     <div class="editor-zoom"><button aria-label="编辑地图缩小" @click="ed.zoom=Math.max(.1,ed.zoom*.8)"><IconZoomOut :size="15"/></button><button @click="fit">适配</button><button aria-label="编辑地图放大" @click="ed.zoom=Math.min(40,ed.zoom*1.25)"><IconZoomIn :size="15"/></button></div>
     <div class="editor-coordinate">{{ed.cursor?`X ${ed.cursor.x.toFixed(3)} · Y ${ed.cursor.y.toFixed(3)} m`:'米制坐标'}} · {{Math.round(ed.zoom*100)}}% · 已选 {{ed.count}}{{ed.measurement!==null?` · 测量 ${ed.measurement.toFixed(3)} m`:''}}</div>
    </div>
    <aside class="editor-inspector">
     <nav class="editor-panel-tabs" aria-label="编辑面板"><button v-for="[id,label] in editorPanels" :key="id" :class="{active:ed.panel===id}" @click="ed.panel=id">{{label}}</button></nav>
     <MapEditorPanels v-if="ed.panel!=='properties'" :editor="ed"/>
     <div v-else><h3>{{selected?`属性 · ${selected}`:'地图属性'}}</h3>
     <template v-if="!selected"><label>地图名称<input v-model="definition.name" @change="dirty=true"/></label><label>坐标系<input v-model="definition.coordinateSystem" @change="dirty=true"/></label><p class="muted-note">坐标单位：米。修改坐标系文字不会执行坐标变换。原生SMAP须使用厂家坐标与版本核对。</p></template>
     <template v-if="kind==='node'&&nodeForm"><label>航点编号<input v-model="nodeForm.id"/></label><div class="two-fields"><label>X（米）<input v-model.number="nodeForm.x" type="number" step=".1"/></label><label>Y（米）<input v-model.number="nodeForm.y" type="number" step=".1"/></label></div><label>类型<select v-model="nodeForm.type"><option value="Waypoint">普通航点</option><option value="Station">业务站点</option><option value="Intersection">路口</option><option value="WaitingPoint">等待位</option><option value="ChargingPoint">充电位</option></select></label><label>厂家站点编号<input v-model="nodeForm.agvSiteId" placeholder="例如 LM1，按厂家地图填写"/></label><label class="check-label"><input v-model="nodeForm.enabled" type="checkbox"/>启用航点</label><label>关联共享区域<select v-model="nodeForm.resourceIds" multiple><option v-for="r in definition.resources" :key="r.id" :value="r.id">{{r.id}}</option></select></label></template>
     <template v-if="kind==='edge'&&edgeForm"><label>道路编号<input v-model="edgeForm.id"/></label><label>起点<select v-model="edgeForm.from"><option v-for="n in definition.nodes" :key="n.id">{{n.id}}</option></select></label><label>终点<select v-model="edgeForm.to"><option v-for="n in definition.nodes" :key="n.id">{{n.id}}</option></select></label><div class="two-fields"><label>路径距离（米）<input v-model.number="edgeForm.distance" type="number" min=".001" step=".1"/></label><label>限速（米/秒）<input v-model.number="edgeForm.speedLimit" type="number" min=".001" step=".1"/></label></div><label class="check-label"><input v-model="edgeForm.bidirectional" type="checkbox"/>双向通行</label><label class="check-label"><input v-model="edgeForm.enabled" type="checkbox"/>启用道路</label><p>几何：{{edgeForm.geometryKind}} · 可在画布拖动黄色控制点。</p><div v-for="(p,i) in edgeForm.controlPoints??[]" :key="i" class="two-fields"><label>控制点{{i+1}} X<input v-model.number="p.x" type="number"/></label><label>Y<input v-model.number="p.y" type="number"/></label></div><label>关联共享区域<select v-model="edgeForm.resourceIds" multiple><option v-for="r in definition.resources" :key="r.id" :value="r.id">{{r.id}}</option></select></label></template>
     <template v-if="kind==='resource'&&resourceForm"><label>区域编号<input v-model="resourceForm.id"/></label><label>区域类型<select v-model="resourceForm.type"><option value="NarrowAisle">窄通道</option><option value="Intersection">路口</option><option value="SingleLane">单行区</option><option value="Station">工位</option><option value="ChargingBay">充电区</option></select></label><label>容量<input v-model.number="resourceForm.capacity" type="number" min="1"/></label><p>边界顶点（米）</p><div v-for="(p,i) in resourceForm.boundary??[]" :key="i" class="two-fields"><label>X{{i+1}}<input v-model.number="p.x" type="number"/></label><label>Y{{i+1}}<input v-model.number="p.y" type="number"/></label></div><button @click="resourceForm.boundary??=[];resourceForm.boundary.push({x:0,y:0})">增加顶点</button></template>
     <template v-if="kind==='scene'&&sceneForm"><label>对象编号<input v-model="sceneForm.id"/></label><label>名称 / 标注<input v-model="sceneForm.name"/></label><label>类型<select v-model="sceneForm.kind"><option value="area">车间区域</option><option value="equipment">设备区域</option><option value="text">文字标注</option></select></label><div class="two-fields"><label>X<input v-model.number="sceneForm.x" type="number"/></label><label>Y<input v-model.number="sceneForm.y" type="number"/></label><label>宽<input v-model.number="sceneForm.width" type="number" min=".1"/></label><label>高<input v-model.number="sceneForm.height" type="number" min=".1"/></label></div><label>颜色<input v-model="sceneForm.color" type="color"/></label><label class="check-label"><input v-model="sceneForm.visible" type="checkbox"/>显示对象</label></template>
     <label v-if="activeForm">对象所属图层<select v-model="activeForm.layerId"><option v-for="layer in ed.layerList" :key="layer.id" :value="layer.id">{{layer.name}}</option></select></label>
     <div v-if="selected" class="inspector-actions"><button class="accent-button" @click="applyInspector">应用属性</button><button @click="removeSelected"><IconTrash :size="13"/>删除对象</button></div>
    </div></aside></div>
   <div class="backend-map-tools"><b>后台草稿</b><button :disabled="busy||effectiveSource!=='backend'" @click="listMaps">读取目录</button><select v-model="mapId" aria-label="选择后台地图"><option value="">选择后台地图</option><option v-for="m in maps" :key="m.mapId" :value="m.mapId">{{m.name}}</option></select><button :disabled="busy||!mapId||effectiveSource!=='backend'" @click="loadWorkspace">读取完整草稿并替换</button><button :disabled="busy||effectiveSource!=='backend'" @click="createBackend">新建后台地图</button><button :disabled="busy||!workspace||issues.length>0||effectiveSource!=='backend'" @click="saveBackend">保存地图草稿</button><button :disabled="busy||!workspace||effectiveSource!=='backend'" @click="publish">发布后台已保存草稿</button><small>{{workspace?`${workspace.project.mapId} / V${workspace.draft.versionNumber}`:'尚未关联后台草稿'}}</small></div>
   <div class="validation-strip"><span :class="issues.length?'amber-text':'green-text'">{{issues.length?issues.join('；'):'本地结构检查通过'}}</span><small>本地检查不代替后台几何/安全校验；正式运行切换沿用WPF流程。未应用的属性不参与保存。</small></div><details v-if="versions.length"><summary>后台版本记录 {{versions.length}}</summary><p v-for="v in versions" :key="v.versionId">V{{v.versionNumber}} · {{v.status}} · {{v.nodeCount}}节点 / {{v.edgeCount}}道路 · {{v.updatedAt}}</p></details>
  </template>
  <div v-else-if="tab==='plans'" class="plan-manager"><div class="workbench-guide"><h3>方案内容与生效范围</h3><p>方案包含地图、场景、车辆配置、航点顺序。保存于此浏览器；导出JSON可备份或跨电脑加载。加载方案只替换编辑稿，不下发小车，不切换运行地图。</p><p>地图JSON仅导出地图定义，可由主工程规范化JSON导入；方案JSON是Web工作台格式，不是厂家SMAP文件。SMAP在连接后台后选择文件，复用主工程解析器。</p><button @click="pending=emptyPlan()"><IconPlus :size="14"/>新建空白方案</button><button @click="edit(()=>{plan.id=uuid();plan.name+=' 副本';});saveLocal()"><IconCopy :size="14"/>另存为副本</button><button @click="exportMap">导出地图JSON</button></div><div class="plan-cards"><article v-for="p in saved" :key="p.id"><h3>{{p.name}}</h3><p>{{p.definition.name}}</p><small>{{p.updatedAt}}</small><p>{{p.definition.nodes.length}} 航点 / {{p.vehicles.length}} 车辆 / {{p.scene.length}} 场景</p><button @click="pending=clone(p)">预览并加载</button><button @click="deleteLocal(p.id)">移出列表</button></article><p v-if="!saved.length" class="empty-panel">暂无本机方案。编辑后点击“保存方案”。</p></div></div>
  <div v-else-if="tab==='vehicles'" class="configuration-page"><div class="editor-toolbar"><button class="accent-button" @click="openVehicle()"><IconPlus :size="15"/>新增车辆</button><button :disabled="busy||effectiveSource!=='backend'" @click="readVehicles"><IconRefresh :size="15"/>读取后台车辆配置并替换</button><button :disabled="busy||effectiveSource!=='backend'||!vehicleRevision||!canManage||vehicleErrors.length>0" @click="saveVehicles">保存车辆配置到后台</button><span>{{plan.vehicles.length}}辆 · {{restart?'后台配置待重启':'配置与在线状态分别管理'}}</span></div><p class="muted-note">新增车辆默认不启用。状态端口19204、导航端口19206按现场厂家配置填写；保存后台受未结束任务、配置版本及权限核验，服务重启后生效。此处不会修改PLC、全局通信开关或自动确认地图坐标绑定。</p><div class="management-table"><table><thead><tr><th>编号</th><th>名称</th><th>控制器IP</th><th>状态端口</th><th>导航端口</th><th>启用配置</th><th>当前观察</th><th>操作</th></tr></thead><tbody><tr v-for="v in plan.vehicles" :key="v.vehicleId"><td>{{v.vehicleId}}</td><td>{{v.displayName}}</td><td>{{v.ipAddress}}</td><td>{{v.port}}</td><td>{{v.navigationPort}}</td><td>{{v.enabled?'已勾选':'不启用'}}</td><td>{{effectiveSnapshot?.status?.agvs.find(a=>a.vehicleId===v.vehicleId)?.connection??'无在线数据'}}</td><td><button @click="openVehicle(v)">编辑</button><button @click="edit(()=>plan.vehicles=plan.vehicles.filter(n=>n.vehicleId!==v.vehicleId))">移出配置稿</button></td></tr></tbody></table></div><p v-if="!plan.vehicles.length" class="empty-panel">尚未配置车辆。点击新增，或连接后台读取已保存配置。</p><p class="amber-text">{{vehicleErrors.join('；')}}</p></div>
  <div v-else class="waypoint-editor"><div><h3>航点顺序与路径核对</h3><p class="muted-note">顺序是方案中的导航检查列表。每两个航点之间按启用道路及方向计算最短距离；不包含实时交通预约、厂家可达性或PLC业务条件，不直接生成运动命令。生产任务仍使用现有九步业务流程。</p><div class="editor-toolbar"><select v-model="waypoint" aria-label="选择顺序航点"><option value="">选择航点</option><option v-for="n in definition.nodes.filter(n=>n.enabled)" :key="n.id" :value="n.id">{{n.id}} · {{n.agvSiteId||'未绑定'}}</option></select><button :disabled="!waypoint||plan.waypoints.length>=1000" @click="edit(()=>plan.waypoints.push(waypoint))">追加航点</button><button @click="edit(()=>plan.waypoints.reverse())">反转顺序</button><button @click="edit(()=>plan.waypoints=[])">清空顺序</button></div><ol class="waypoint-list"><li v-for="(id,i) in plan.waypoints" :key="i"><b>{{i+1}}</b><span>{{id}}<small>{{definition.nodes.find(n=>n.id===id)?.agvSiteId||'未绑定厂家站点'}}</small></span><button :disabled="i===0" :aria-label="`上移航点${i+1}`" @click="reorder(i,-1)"><IconArrowUp :size="15"/></button><button :disabled="i===plan.waypoints.length-1" :aria-label="`下移航点${i+1}`" @click="reorder(i,1)"><IconArrowDown :size="15"/></button><button :aria-label="`移除航点${i+1}`" @click="edit(()=>plan.waypoints.splice(i,1))"><IconTrash :size="15"/></button></li></ol></div><aside class="workbench-guide"><h3>核对结果</h3><p v-if="route.error" class="amber-text">{{route.error}}</p><template v-else><p class="green-text">导航图结构可达 · {{route.distance.toFixed(2)}} 米</p><h4>展开航点</h4><p>{{route.nodes.join(' → ')}}</p><h4>道路顺序</h4><p>{{route.edges.join(' → ')||'同一航点，无移动'}}</p><h4>沿途资源</h4><p>{{[...new Set([...route.edges.flatMap(id=>definition.edges.find(e=>e.id===id)?.resourceIds??[]),...route.nodes.flatMap(id=>definition.nodes.find(n=>n.id===id)?.resourceIds??[])])].join('、')||'未关联共享区域'}}</p></template></aside></div>
  <ConsoleDialog v-if="pending" title="核对加载内容" @close="pending=null"><h3>{{pending.name}}</h3><p class="muted-note">{{pending.definition.nodes.length}} 航点 / {{pending.definition.edges.length}} 道路 / {{pending.scene.length}} 场景 / {{pending.vehicles.length}} 车辆 / {{pending.waypoints.length}} 顺序航点</p><p>加载将替换当前编辑稿并解除后台关联。尚未保存的编辑可先取消，再保存或导出；加载不影响后台运行配置。</p><template #footer><button @click="pending=null">取消</button><button class="accent-button" @click="replace(pending!);pending=null;message='已加载到编辑稿，请按模块核对并保存。'">加载到编辑稿</button></template></ConsoleDialog>
  <ConsoleDialog v-if="vehicleForm" :title="vehicleOriginal?'编辑车辆配置':'新增车辆配置'" @close="vehicleForm=null"><div class="creation-grid"><label>车辆编号<input v-model="vehicleForm.vehicleId"/></label><label>显示名称<input v-model="vehicleForm.displayName"/></label><label>控制器IPv4<input v-model="vehicleForm.ipAddress" placeholder="例如 192.168.10.21"/></label><label>状态端口<input v-model.number="vehicleForm.port" type="number" min="1" max="65535"/></label><label>导航端口<input v-model.number="vehicleForm.navigationPort" type="number" min="1" max="65535"/></label><label class="check-label"><input v-model="vehicleForm.enabled" type="checkbox"/>配置为启用（重启后）</label></div><p v-if="vehicleMessage" class="inline-warning" role="alert">{{vehicleMessage}}</p><p class="muted-note">填写小车控制器IP。RoboShop是电脑上的工具，电脑IP与车辆IP分别配置。新车辆还需核定厂家地图和坐标绑定，新增配置不等于可以派车。</p><template #footer><button @click="vehicleForm=null">取消</button><button class="accent-button" @click="applyVehicle">应用到车辆配置稿</button></template></ConsoleDialog>
 </section>
</template>
<style scoped>
.workflow-guide{display:flex;align-items:center;gap:8px;padding:10px 14px;background:#0d1a2e;border:1px solid #25365b;border-radius:4px;font-size:11px;color:#8da3c6;flex-wrap:wrap;margin-bottom:6px}
.workflow-guide b{color:#c8daf0;margin-right:6px}
.workflow-guide span{white-space:nowrap}
</style>
