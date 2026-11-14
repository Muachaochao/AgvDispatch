<script setup lang="ts">
import ActionIcon from './components/ActionIcon.vue';
import DisplayAreasEditor from './components/DisplayAreasEditor.vue';
import AreaAnnotations from './components/AreaAnnotations.vue';
import { computed, nextTick, onMounted, onUnmounted, ref, watch, toRef } from 'vue';
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router';
import { IconMap, IconDeviceFloppy, IconDownload, IconUpload, IconPlus, IconTrash, IconArrowUp, IconArrowDown, IconCopy, IconRefresh, IconZoomIn, IconZoomOut, IconPlugConnected, IconCloudUpload, IconArrowLeft, IconEye, IconSearch, IconChevronDown } from '@tabler/icons-vue';
import ConsoleDialog from './ConsoleDialog.vue';
import ServerProjects from './ServerProjects.vue';
import VendorPathPanel from './VendorPathPanel.vue';
import MapEditorPanels from './MapEditorPanels.vue';
import './map-editor.css';
import { useMapEditor } from './use-map-editor';
import * as geometry from './map-editing';
import { useDispatchStore } from './stores/dispatch';
import { useToastStore } from './stores/toast';
import { clone, emptyPlan, parsePlan, validateDefinition, validateVehicles, routePlan, edgePath, type Plan, type Node, type Edge, type Resource, type VehicleConfig, type SceneItem } from './workbench';
import { selectNodeLabels, type MapLabelMode } from './map-render-lod';
import type { Snapshot, MapSummary } from './types';
import { checkDraftDestination, draftSaveBlocker } from './draft-save';
import { label } from './state';
const props=defineProps<{source?:'demo'|'backend';snapshot?:Snapshot|null;initialTab?:string}>();
const store = useDispatchStore();
const toastStore = useToastStore();
const currentRoute = useRoute();
const mapRouter = useRouter();
const effectiveSource = computed((): 'demo' | 'backend' => props.source ?? (store.snapshot?.source==='backend' && !store.demoMode && !store.engineeringSimulationOnly ? 'backend' : 'demo'));
const effectiveSnapshot = computed((): Snapshot | null => props.snapshot ?? store.snapshot);
const uuid=()=>crypto.randomUUID();
const vehicleMessage=ref('');
const mapVersionStatusLabel=(status:string)=>({Draft:'草稿',Published:'已发布',Active:'当前使用',Retired:'已停用',Archived:'已归档'} as Record<string,string>)[status]??'状态待核对';
const queryTab = computed(() => (currentRoute.query.tab as string) || props.initialTab || 'map');
const queryMapId = computed(() => currentRoute.query.mapId as string | undefined);
const tab=ref(queryTab.value);
const areasOpened=ref(tab.value==='areas');
watch(tab,value=>{if(value==='areas')areasOpened.value=true;});
watch(queryTab, v => { if (v) tab.value = v; });
watch(()=>props.initialTab,v=>{if(v)tab.value=v;});
const plan=ref<Plan>(emptyPlan()),saved=ref<Plan[]>([]),dirty=ref(false),message=ref(''),busy=ref(false);
const initializing=ref(false),mapPickerOpen=ref(false),mapPickerSearch=ref(''),objectSearch=ref('');
const treeRef=ref<any>(null);
const selected=ref(''),kind=ref<'node'|'edge'|'resource'|'scene'>('node');
const nodeForm=ref<Node>(),edgeForm=ref<Edge>(),resourceForm=ref<Resource>(),sceneForm=ref<SceneItem>();
const ed=useMapEditor(plan,tab,message,()=>{dirty.value=true;},inspectObject,saveLocal);
const tool=toRef(ed,'tool'),undoStack=computed(()=>ed.undoStack),redoStack=computed(()=>ed.redoStack);
const pending=ref<Plan|null>(null),importInput=ref<HTMLInputElement>();
const robotMapVehicle=ref('');
const robotMapDownload=ref<{mapName:string;robotMapMd5:string;contentSha256:string;downloadedAt:string}|null>(null);
const robotMapVehicles=computed(()=>effectiveSnapshot.value?.status?.agvs??[]);
watch(robotMapVehicles,vehicles=>{if(!vehicles.some(v=>v.vehicleId===robotMapVehicle.value))robotMapVehicle.value=vehicles[0]?.vehicleId??'';},{immediate:true});
function importWarnings(p:Plan|null):string[]{const value=p?.definition.importWarnings;return Array.isArray(value)?value.filter((w):w is string=>typeof w==='string'):[];}
async function pullRobotMap(){await run(async mine=>{
 const result=await request<any>('/api/robots/map/download',{vehicleId:robotMapVehicle.value});
 if(mine!==epoch)return;
 robotMapDownload.value=result.download;
 if(!result.preview?.accepted||!result.preview.definition)throw new Error(result.preview?.message??'地图已下载，但预览未通过；可保存原生SMAP核对。');
 pending.value=parsePlan(JSON.stringify(result.preview.definition));
 message.value=`已从机器人读取 ${result.download.mapName}，请核对预览；加载不会保存、发布或切换运行地图。`;
});}
const maps=ref<MapSummary[]>([]),mapId=ref(''),workspace=ref<any>(null),versions=ref<any[]>([]),vehicleRevision=ref(''),canManage=ref(false),restart=ref(false);
const association=ref<any>(null);
const saveBlocker=computed(()=>draftSaveBlocker(busy.value,effectiveSource.value==='backend',!!workspace.value,issues.value));
const vehicleForm=ref<VehicleConfig|null>(null),vehicleOriginal=ref(''),waypoint=ref('');
const svg=toRef(ed,'svg');
const panelIcons={properties:'properties',batch:'properties',layers:'layers',history:'history'} as const;
const editorPanels=[['properties','属性'],['batch','批量'],['layers','图层'],['history','历史']] as const;
const mapTools=[{id:'select',icon:'select',label:'选择 / 框选'},{id:'pan',icon:'pan',label:'平移'},{id:'node',icon:'locate',label:'节点'},{id:'edge',icon:'line',label:'直线'},{id:'arc',icon:'curve',label:'圆弧'},{id:'bezier',icon:'curve',label:'贝塞尔'},{id:'boundary',icon:'boundary',label:'区域边界'},{id:'brush',icon:'brush',label:'关联刷'},{id:'measure',icon:'measure',label:'测量'}] as const;
const sceneTools=[{id:'select',icon:'select',label:'选择 / 框选'},{id:'pan',icon:'pan',label:'平移'},{id:'scene',icon:'scene',label:'放置场景'},{id:'measure',icon:'measure',label:'测量'}] as const;
const tabs=[{id:'map',icon:'map',name:'地图编辑'},{id:'areas',icon:'boundary',name:'展示区域'},{id:'scene',icon:'scene',name:'场景编辑'},{id:'plans',icon:'folder',name:'方案管理'},{id:'vehicles',icon:'vehicle',name:'车辆配置'},{id:'waypoints',icon:'route',name:'航点顺序'}] as const;
const definition=computed(()=>plan.value.definition),issues=computed(()=>validateDefinition(definition.value));
const route=computed(()=>routePlan(definition.value,plan.value.waypoints));
const viewBox=computed(()=>ed.viewBox),scale=computed(()=>ed.scale),nodeRadius=computed(()=>ed.scale*.55);
const counts=computed(()=>`${definition.value.nodes.length} 航点 · ${definition.value.edges.length} 道路 · ${definition.value.resources.length} 共享区域 · ${definition.value.areaAnnotations?.length??0} 厂家区域标注`);
const labelMode=ref<MapLabelMode>('auto');
const nodeLabelIds=computed(()=>new Set(selectNodeLabels(ed.visibleNodes,{
 mode:labelMode.value,
 zoom:Number(ed.zoom),
 scale:Number(ed.scale),
 view:ed.viewRect,
 selectedIds:ed.selection.node
}).map(node=>node.id)));
const edgePathData=computed(()=>new Map(ed.visibleEdges.map(edge=>[edge.id,edgePath(edge,definition.value)])));
type EditorTreeRow={key:string;label:string;kind?:'node'|'edge'|'resource'|'scene';objectId?:string;children?:EditorTreeRow[]};
const treeData=computed<EditorTreeRow[]>(()=>{
  if(tab.value==='scene')return [{key:'scene:items',label:`场景对象 ${plan.value.scene.length}`,children:plan.value.scene.map(item=>({key:`scene:${item.id}`,label:item.name,kind:'scene',objectId:item.id}))}];
  return [
    {key:'group:nodes',label:`航点 ${definition.value.nodes.length}`,children:definition.value.nodes.map(node=>({key:`node:${node.id}`,label:node.agvSiteId?`${node.id} · ${node.agvSiteId}`:node.id,kind:'node',objectId:node.id}))},
    {key:'group:edges',label:`道路 ${definition.value.edges.length}`,children:definition.value.edges.map(edge=>({key:`edge:${edge.id}`,label:`${edge.id} · ${edge.from}${edge.bidirectional?'↔':'→'}${edge.to}`,kind:'edge',objectId:edge.id}))},
    {key:'group:resources',label:`共享区域 ${definition.value.resources.length}`,children:definition.value.resources.map(resource=>({key:`resource:${resource.id}`,label:`${resource.id} · 容量${resource.capacity}`,kind:'resource',objectId:resource.id}))}
  ];
});
const treeCurrentKey=computed(()=>selected.value?`${kind.value}:${selected.value}`:'');
function filterTreeNode(value:string,data:EditorTreeRow){return !value||`${data.label} ${data.objectId??''}`.toLowerCase().includes(value.toLowerCase());}
function handleTreeNode(data:EditorTreeRow){if(data.kind&&data.objectId)select(data.kind,data.objectId);}
watch(objectSearch,value=>{void nextTick(()=>treeRef.value?.filter(value));});
const filteredMaps=computed(()=>{
 const keyword=mapPickerSearch.value.trim().toLowerCase();
 return [...maps.value]
   .filter(map=>!keyword||`${map.name} ${map.mapId} ${map.coordinateSystem}`.toLowerCase().includes(keyword))
   .sort((left,right)=>Number(right.isActive)-Number(left.isActive)||right.updatedAt.localeCompare(left.updatedAt));
});
function confirmDiscardDraft(action:string){return !dirty.value||window.confirm(`当前地图编辑稿有未保存修改。确定${action}并放弃这些修改？`);}
async function openMapPicker(){mapPickerSearch.value='';mapPickerOpen.value=true;if(!maps.value.length&&effectiveSource.value==='backend')await listMaps();}
async function chooseMapFromPicker(id:string){if(!id||!confirmDiscardDraft('切换地图'))return;mapPickerOpen.value=false;mapId.value=id;await loadWorkspace(true);}
function startBlankMapFromPicker(){if(!confirmDiscardDraft('新建空白地图'))return;mapPickerOpen.value=false;pending.value=emptyPlan();message.value='已准备空白地图；在加载预览中确认后才会替换当前编辑稿。';}
const showVendorAreas=ref(true);
const runtimeOpen=ref(false),runtimeCoordinates=ref(false),runtimeReplace=ref(false),runtimeRoles=ref<Record<string,string>>({});
const runtimeSource=computed(()=>definition.value.robokitSource?.sha256??'');
function openRuntimePreparation(){runtimeCoordinates.value=false;runtimeReplace.value=false;runtimeRoles.value=Object.fromEntries(definition.value.nodes.map(n=>[n.agvSiteId??n.id,'']));runtimeOpen.value=true;}
const runtimeReady=computed(()=>runtimeCoordinates.value&&runtimeReplace.value&&definition.value.nodes.length>0&&definition.value.nodes.every(n=>n.agvSiteId&&runtimeRoles.value[n.agvSiteId]));
async function prepareRuntime(){if(!runtimeReady.value)return;const source=runtimeSource.value,target=workspace.value?.project.mapId;await run(async mine=>{
 const result=await request<any>('/api/maps/prepare-runtime',{sourceSha256:source,roles:runtimeRoles.value,robotCoordinatesConfirmed:true});
 if(mine!==epoch||runtimeSource.value!==source||workspace.value?.project.mapId!==target)return;
 if(!result.accepted||!result.definition)throw new Error(result.message??'运行地图准备失败');
 parsePlan(JSON.stringify(result.definition));
 edit(()=>{plan.value.definition=clone(result.definition);plan.value.name=result.definition.name;plan.value.waypoints=[];},'从原SMAP准备运行图');
 resetSelection();fit();runtimeOpen.value=false;
 message.value='运行图已准备：车端原始坐标、站点用途和道路已应用。请先关联已有后台地图或新建后台地图，再保存草稿并去地图管理发布。尚未激活，也未给机器人发送动作。';
});}
const visibleVendorAreas=computed(()=>showVendorAreas.value?(definition.value.areaAnnotations??[]).filter(a=>geometry.visible(definition.value,a)):[]);
const vendorAreaNames=computed(()=>(definition.value.areaAnnotations??[]).map(area=>area.text));
const vendorAreaPreview=computed(()=>{const names=vendorAreaNames.value;return `${names.slice(0,5).join('、')}${names.length>5?` 等 ${names.length} 项`:''}`;});
const vehicleErrors=computed(()=>validateVehicles(plan.value.vehicles));
let epoch=0,initialLoadRevision=0;
async function loadInitialBackendMap(){
 if(effectiveSource.value!=='backend')return;
 const revision=++initialLoadRevision;
 initializing.value=true;
 try{
  await listMaps();
  if(revision!==initialLoadRevision||effectiveSource.value!=='backend')return;
  const target=queryMapId.value||effectiveSnapshot.value?.map?.id||maps.value.find(map=>map.isActive)?.mapId||maps.value[0]?.mapId||'';
  if(target){
   mapId.value=target;
   message.value=`正在读取后台地图 ${target} 的草稿…`;
   await loadWorkspace(true);
  }else{
   mapPickerOpen.value=true;
   message.value='后台没有可编辑地图，请选择地图或新建地图。';
  }
 }catch(ex){
  if(revision===initialLoadRevision){message.value=`自动加载地图草稿失败：${String(ex)}`;mapPickerOpen.value=true;}
 }finally{
  if(revision===initialLoadRevision)initializing.value=false;
 }
}
watch(effectiveSource,source=>{
 ++epoch;
 ++initialLoadRevision;
 workspace.value=null;
 association.value=null;
 maps.value=[];
 versions.value=[];
 vehicleRevision.value='';
 canManage.value=false;
 if(source==='backend'){void loadInitialBackendMap();}
 else message.value='数据源已切换。编辑稿保留，后台关联已解除，请重新关联目标以保留编辑内容。';
});
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
async function listMaps(){if(effectiveSource.value!=='backend'){message.value='示例模式没有后台地图目录，可使用本机方案。';return;}await run(async mine=>{const result=await request<MapSummary[]>('/api/maps');if(mine!==epoch)return;maps.value=result;mapId.value||=result.find(map=>map.isActive)?.mapId??result[0]?.mapId??'';message.value=`找到 ${result.length} 张后台地图`;});}
async function openAssociation(){if(effectiveSource.value!=='backend'||!mapId.value)return;const target=mapId.value;await run(async mine=>{const result=await request<any>(`/api/maps/${encodeURIComponent(target)}/workspace`);if(mine!==epoch||mapId.value!==target)return;checkDraftDestination(result,target);association.value=result;});}
function confirmAssociation(){if(!association.value||busy.value||effectiveSource.value!=='backend')return;workspace.value=association.value;mapId.value=workspace.value.project.mapId;association.value=null;message.value=`已关联 ${workspace.value.project.name} 的 V${workspace.value.draft.versionNumber} 草稿，当前编辑内容已保留。请点击“保存地图草稿”，再去地图管理发布新版本。`;}
function acceptWorkspace(result:any){workspace.value=result;mapId.value=result.project.mapId;plan.value.name=result.project.name;plan.value.definition=clone(result.draft.definition);dirty.value=true;resetSelection();fit();}
async function loadWorkspace(force=false){if(effectiveSource.value!=='backend'||!mapId.value)return;if(!force&&!confirmDiscardDraft('读取完整草稿并覆盖当前编辑'))return;await run(async mine=>{const result=await request<any>(`/api/maps/${encodeURIComponent(mapId.value)}/workspace`);if(mine!==epoch)return;if(!result?.draft)throw new Error('没有可编辑草稿');parsePlan(JSON.stringify(result.draft.definition));edit(()=>acceptWorkspace(result));dirty.value=false;versions.value=await request<any[]>(`/api/maps/${encodeURIComponent(mapId.value)}/versions`);if(mine!==epoch){workspace.value=null;return;}message.value='完整草稿已加载并替换当前地图。场景、车辆及航点顺序请重新核对。';plan.value.waypoints=plan.value.waypoints.filter(id=>definition.value.nodes.some(n=>n.id===id));});}
async function createBackend(){if(effectiveSource.value!=='backend')return;await run(async mine=>{const d=definition.value,result=await request<any>('/api/maps/create',{name:d.name,description:plan.value.name,backgroundFilePath:'',coordinateSystem:d.coordinateSystem,unit:d.unit,coordinateScale:1});if(mine!==epoch)return;if(!result.accepted)throw new Error(result.message);workspace.value=result.workspace;mapId.value=result.workspace.project.mapId;message.value=`${result.message} 请继续保存当前编辑稿。`;});}
async function saveBackend(){if(!workspace.value||issues.value.length||effectiveSource.value!=='backend')return;await run(async mine=>{const w=workspace.value,d=clone(definition.value),result=await request<any>('/api/maps/draft',{mapId:w.project.mapId,versionId:w.draft.versionId,name:d.name,description:w.project.description,backgroundFilePath:w.project.backgroundFilePath,coordinateSystem:d.coordinateSystem,unit:d.unit,coordinateScale:w.project.coordinateScale,definition:d,expectedEditToken:w.draft.editToken});if(mine!==epoch)return;if(!result.accepted)throw new Error(result.message);workspace.value=result.workspace;dirty.value=false;message.value=`${result.message} 场景/方案仍需单独保存本机；运行地图未切换。`;});}
function goToMapManagement(){mapRouter.push({name:'maps'});}
function previewOnDashboard(){store.previewMap=effectiveSnapshot.value?.map??null;mapRouter.push({name:'overview'});}
function openVehicle(v?:VehicleConfig){vehicleMessage.value='';vehicleOriginal.value=v?.vehicleId??'';vehicleForm.value=v?clone(v):{vehicleId:unique('AGV-',plan.value.vehicles.map(v=>v.vehicleId)),displayName:'新增车辆',ipAddress:'',port:19204,navigationPort:19206,enabled:false};}
function applyVehicle(){const v=vehicleForm.value;if(!v)return;const next=clone(plan.value.vehicles),index=next.findIndex(n=>n.vehicleId===vehicleOriginal.value);if(index<0)next.push(v);else next[index]=v;const errors=validateVehicles(next);if(errors.length){vehicleMessage.value=errors.join('；');return;}edit(()=>plan.value.vehicles=clone(next));vehicleForm.value=null;message.value='车辆已加入配置编辑稿；不代表已连接或可派车。';}
async function readVehicles(){if(effectiveSource.value!=='backend'){message.value='示例模式只编辑本机车辆方案，不生成在线状态。';return;}await run(async mine=>{const result=await request<any>('/api/vehicles/configuration');if(mine!==epoch)return;edit(()=>plan.value.vehicles=result.vehicles);vehicleRevision.value=result.revision;restart.value=result.restartRequired;canManage.value=result.canManage;message.value='已读取后台保存的车辆配置，可能与当前活动配置不同。保存后需服务重启生效。';});}
async function saveVehicles(){if(effectiveSource.value!=='backend'||!vehicleRevision.value||vehicleErrors.value.length)return;await run(async mine=>{const result=await request<any>('/api/vehicles/configuration',{expectedRevision:vehicleRevision.value,vehicles:clone(plan.value.vehicles)});if(mine!==epoch)return;vehicleRevision.value=result.revision;restart.value=result.restartRequired;message.value='后台车辆配置已保存，服务重启后生效。PLC、地图绑定及全局通信开关均保留，未在此启动设备通信。';});}
function reorder(index:number,delta:number){if(index+delta<0||index+delta>=plan.value.waypoints.length)return;edit(()=>{const list=plan.value.waypoints;[list[index],list[index+delta]]=[list[index+delta]!,list[index]!];});}
async function pushOfflineToBackend(){if(effectiveSource.value!=='backend'||issues.value.length)return;await run(async mine=>{const d=definition.value;const result=await request<any>('/api/maps/create',{name:d.name,description:plan.value.name,backgroundFilePath:'',coordinateSystem:d.coordinateSystem,unit:d.unit,coordinateScale:1});if(mine!==epoch)return;if(!result.accepted)throw new Error(result.message);workspace.value=result.workspace;mapId.value=result.workspace.project.mapId;toastStore.success('已推送到后台新建地图');message.value=`${result.message} 请继续保存当前编辑稿到后台草稿。`;});}
function beforeUnload(e:BeforeUnloadEvent){if(dirty.value){e.preventDefault();e.returnValue='';}}
onBeforeRouteLeave(()=>!dirty.value||window.confirm('当前地图编辑稿有未保存修改。确定离开并放弃这些修改？'));
onMounted(async()=>{window.addEventListener('beforeunload',beforeUnload);try{const raw=localStorage.getItem('agv-web-plans-v1');if(raw){const data=JSON.parse(raw);if(!Array.isArray(data)||data.length>20)throw new Error('本机方案索引无效');saved.value=data.map(p=>parsePlan(JSON.stringify(p)));}}catch(ex){message.value=`本机方案未加载：${String(ex)}，原内容未覆盖。`;}
if(effectiveSource.value==='backend')await loadInitialBackendMap();});
onUnmounted(()=>{++epoch;window.removeEventListener('beforeunload',beforeUnload);});
</script>

<template>
 <section class="planning-workbench">
  <header v-show="tab!=='areas'" class="workbench-title">
    <div class="workbench-title-main">
      <el-button link :icon="IconArrowLeft" @click="goToMapManagement">地图管理</el-button>
      <div class="workbench-title-copy"><h2><IconMap :size="21"/>调度工程工作台</h2><small>{{workspace ? `${workspace.project.name} · V${workspace.draft.versionNumber}` : '尚未关联后台地图草稿'}}</small></div>
      <el-input v-model="plan.name" class="plan-name-input" aria-label="方案名称" @change="dirty=true"/>
      <el-tag :type="workspace?'success':effectiveSource==='backend'?'info':'warning'" effect="plain">{{workspace?'已关联草稿':effectiveSource==='backend'?'后台已连接':'离线模式'}}</el-tag>
      <el-tag :type="dirty?'warning':'success'" effect="plain">{{dirty?'未保存':'已保存'}}</el-tag>
    </div>
    <div class="workbench-title-actions">
      <el-button :icon="IconMap" @click="openMapPicker">切换地图</el-button>
      <el-button type="primary" :icon="IconDeviceFloppy" :disabled="!!saveBlocker" :title="saveBlocker||'保存到已关联后台草稿'" @click="saveBackend">保存地图草稿</el-button>
      <el-dropdown trigger="click">
        <el-button>方案操作<IconChevronDown :size="13"/></el-button>
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item :icon="IconDeviceFloppy" @click="saveLocal">保存到本机</el-dropdown-item>
            <el-dropdown-item :icon="IconDownload" @click="exportPlan">导出方案</el-dropdown-item>
            <el-dropdown-item :icon="IconUpload" @click="importInput?.click()">加载方案 / SMAP</el-dropdown-item>
            <el-dropdown-item v-if="!workspace&&effectiveSource==='backend'&&!issues.length" :icon="IconCloudUpload" @click="pushOfflineToBackend">推送到后台新建</el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
      <input ref="importInput" class="file-picker" type="file" accept=".json,.smap" aria-label="导入地图或方案文件" @change="importFile"/>
    </div>
  </header>
  <div v-show="tab!=='areas'" class="workflow-guide">
    <b>操作流程：</b>
    <span :class="definition.nodes.length?'green-text':'amber-text'">① 编辑地图</span> →
    <span :class="workspace?'green-text':'amber-text'">② 关联后台</span> →
    <span :class="workspace&&!dirty?'green-text':'amber-text'">③ 保存草稿</span> →
    <span>④ 地图管理发布</span> →
    <span class="amber-text">⑤ 激活与安全切换</span> →
    <span class="amber-text">⑥ 大屏显示</span>
  </div>
  <div v-show="tab!=='areas'" class="workbench-tab-row">
   <el-tabs v-model="tab" class="workbench-tabs" aria-label="工程模块">
    <el-tab-pane v-for="item in tabs" :key="item.id" :name="item.id">
     <template #label><span class="workbench-tab-label"><ActionIcon :name="item.icon"/>{{item.name}}</span></template>
    </el-tab-pane>
   </el-tabs>
   <small>{{counts}}</small>
  </div>
  <DisplayAreasEditor v-if="areasOpened" v-show="tab==='areas'" :initial-map-id="workspace?.project.mapId||mapId"/>
  <p v-if="tab!=='areas'" class="workbench-message" role="status">{{message||'厂家激光扫图在RoboShop完成；此处编辑调度导航和场景。编辑稿不会自动影响运行地图。'}}</p>
  <ServerProjects v-if="tab==='plans'" :plan="plan" :available="effectiveSource==='backend'" :map-id="workspace?.project?.mapId" :map-version="workspace?.draft?.versionId" @preview="pending=$event"/>
  <template v-if="tab==='map'||tab==='scene'">
   <div class="editor-toolbar editor-tool-palette" aria-label="地图绘图工具">
    <el-button-group class="drawing-tool-group">
      <el-button v-for="item in (tab==='map'?mapTools:sceneTools)" :key="item.id" :type="tool===item.id?'primary':'default'" :class="{active:tool===item.id}" :aria-pressed="tool===item.id" @click="ed.changeTool(item.id)"><ActionIcon :name="item.icon"/>{{item.label}}</el-button>
    </el-button-group>
    <el-button-group>
      <el-tooltip content="撤销 Ctrl+Z"><el-button :disabled="!undoStack.length" aria-label="撤销" @click="history()"><ActionIcon name="undo"/></el-button></el-tooltip>
      <el-tooltip content="重做 Ctrl+Y"><el-button :disabled="!redoStack.length" aria-label="重做" @click="history(true)"><ActionIcon name="redo"/></el-button></el-tooltip>
    </el-button-group>
    <el-button-group>
      <el-button @click="ed.panel='batch'"><ActionIcon name="properties"/>批量</el-button>
      <el-button @click="ed.panel='layers'"><ActionIcon name="layers"/>图层</el-button>
      <el-button @click="ed.panel='history'"><ActionIcon name="history"/>历史</el-button>
    </el-button-group>
    <el-select v-model="robotMapVehicle" aria-label="拉取地图的车辆" style="width:110px"><el-option v-for="v in robotMapVehicles" :key="v.vehicleId" :label="v.vehicleId" :value="v.vehicleId"/></el-select>
    <el-button :disabled="busy||effectiveSource!=='backend'||!robotMapVehicle" :loading="busy" @click="pullRobotMap">拉取车端地图</el-button>
    <el-button :disabled="busy||effectiveSource!=='backend'||!runtimeSource||definition.coordinateSystem!=='SmapWorldTranslated'" @click="openRuntimePreparation">准备运行地图</el-button>
    <el-dropdown trigger="click">
      <el-button>更多工具<IconChevronDown :size="13"/></el-button>
      <template #dropdown><el-dropdown-menu><el-dropdown-item :icon="IconCopy" @click="importRuntime">从运行图复制</el-dropdown-item><el-dropdown-item :icon="IconDownload" @click="exportMap">导出地图 JSON</el-dropdown-item></el-dropdown-menu></template>
    </el-dropdown>
   </div>
   <div class="editor-options">
    <label>当前图层<el-select v-model="ed.currentLayer" style="width:150px" @change="ed.settings"><el-option v-for="layer in ed.layerList" :key="layer.id" :label="`${layer.name}${layer.isLocked?' · 锁定':''}`" :value="layer.id"/></el-select></label>
    <label>对象标签<el-select v-model="labelMode" style="width:116px"><el-option label="自动分级" value="auto"/><el-option label="仅关键对象" value="key"/><el-option label="碰撞过滤全部" value="all"/><el-option label="隐藏标签" value="none"/></el-select></label>
    <el-checkbox v-model="ed.grid" @change="ed.settings">网格</el-checkbox><label>间距(m)<el-input-number v-model="ed.gridStep" :min=".001" :step=".1" controls-position="right" @change="ed.settings"/></label>
    <el-checkbox v-model="ed.snapGrid" @change="ed.settings">网格吸附</el-checkbox><el-checkbox v-model="ed.snapNodes" @change="ed.settings">节点吸附</el-checkbox><el-checkbox v-model="ed.snapAlignment" @change="ed.settings">对齐参考线</el-checkbox>
    <span>已选 {{ed.count}}</span><el-button :disabled="!ed.count" type="danger" plain @click="ed.remove"><ActionIcon name="remove"/>删除所选</el-button>
    <template v-if="tool==='boundary'"><el-button :disabled="ed.draft.length<3" @click="ed.finishBoundary"><ActionIcon name="check"/>完成边界（Enter）</el-button><el-button :disabled="!ed.draft.length" @click="ed.draft.pop()"><ActionIcon name="undo"/>撤回顶点</el-button></template>
    <template v-if="tool==='brush'"><label>共享区域<el-select v-model="ed.brushResource" filterable style="width:170px"><el-option label="选择共享区域" value=""/><el-option v-for="r in definition.resources" :key="r.id" :label="r.id" :value="r.id"/></el-select></label><el-checkbox v-model="ed.brushRemove">解除关联</el-checkbox></template>
   </div>
   <div v-if="definition.areaAnnotations?.length" class="editor-toolbar vendor-area-bar">
    <el-checkbox v-model="showVendorAreas">厂家区域标注（{{definition.areaAnnotations.length}}）</el-checkbox>
    <el-tooltip :content="vendorAreaNames.join('、')" placement="bottom-start" :show-after="350"><span class="vendor-area-preview">{{vendorAreaPreview}}</span></el-tooltip>
    <small>仅作显示，未配置为交通锁</small>
   </div>
   <p class="editor-tool-hint" role="status">{{ed.toolHint}} <strong v-if="ed.measurement!==null">距离 {{ed.measurement.toFixed(3)}} m</strong><span v-if="ed.snapTarget"> · 吸附 {{ed.snapTarget}}</span></p>
   <div class="editor-layout"><aside class="editor-tree"><header><h3>{{tab==='scene'?'场景对象':'导航对象'}}</h3><el-button v-if="tab==='scene'" size="small" :icon="IconPlus" @click="ed.addScene({x:0,y:0})">新增</el-button></header><el-input v-model="objectSearch" clearable :prefix-icon="IconSearch" placeholder="搜索编号、站点或区域"/><el-tree ref="treeRef" :data="treeData" node-key="key" :props="{label:'label',children:'children'}" :filter-node-method="filterTreeNode" :current-node-key="treeCurrentKey" :default-expanded-keys="[tab==='scene'?'scene:items':'group:nodes']" highlight-current :expand-on-click-node="false" empty-text="当前没有可编辑对象" @node-click="handleTreeNode"><template #default="{data}"><span class="editor-tree-label">{{data.label}}</span></template></el-tree><p v-if="tab==='scene'" class="muted-note">场景图形仅用于显示；设备区不会自动成为障碍或交通锁。</p></aside>
    <div class="editor-canvas" :class="{'pan-mode':tool==='pan'||ed.space}">
     <svg ref="svg" :viewBox="viewBox" :data-render-nodes="ed.visibleNodes.length" :data-render-edges="ed.visibleEdges.length" :data-visible-labels="nodeLabelIds.size" role="application" tabindex="0" aria-label="调度地图编辑画布，支持框选、节点拖动与键盘快捷键" @pointerdown="ed.down" @pointermove="ed.move" @pointerup="ed.up" @pointercancel="ed.cancelGesture" @lostpointercapture="ed.cancelGesture" @contextmenu.prevent @dblclick.prevent="tool==='boundary'&&ed.finishBoundary()" @wheel.prevent="ed.wheel">
      <defs><pattern id="planning-editor-grid" :width="Math.max(.001,Number(ed.gridStep)||1)" :height="Math.max(.001,Number(ed.gridStep)||1)" patternUnits="userSpaceOnUse"><path :d="`M ${Math.max(.001,Number(ed.gridStep)||1)} 0 L 0 0 0 ${Math.max(.001,Number(ed.gridStep)||1)}`" fill="none" stroke="#1a3550" :stroke-width="scale*.04"/></pattern><marker id="planning-editor-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="#72bfd2"/></marker></defs>
      <rect v-if="ed.grid" :x="Number(viewBox.split(' ')[0])" :y="Number(viewBox.split(' ')[1])" :width="Number(viewBox.split(' ')[2])" :height="Number(viewBox.split(' ')[3])" fill="url(#planning-editor-grid)" pointer-events="none"/>
      <AreaAnnotations :areas="visibleVendorAreas" :scale="scale"/>
      <g v-for="s in ed.visibleScene" :key="s.id" v-memo="[s, ed.selection.scene, ed.layerList, scale]" data-object="scene" data-kind="scene" :data-id="s.id" :style="ed.layerStyle(s)">
       <rect v-if="s.kind!=='text'" :x="s.x" :y="-s.y-s.height" :width="s.width" :height="s.height" :fill="s.color" fill-opacity=".18" :stroke="ed.isSelected('scene',s.id)?'#e8fbff':s.color" :stroke-width="scale*.12"/><text :x="s.x+scale*.2" :y="-s.y-s.height+scale*1.2" :fill="s.color" :font-size="scale*1.1">{{s.name}}</text>
      </g>
      <polygon v-for="r in ed.visibleResources" :key="r.id" v-memo="[r, ed.selection.resource, ed.layerList, scale]" data-object="resource" data-kind="resource" :data-id="r.id" :points="r.boundary?.map(p=>`${p.x},${-p.y}`).join(' ')" fill="#e0b25b" :fill-opacity="ed.isSelected('resource',r.id)?.22:.09" :stroke="ed.isSelected('resource',r.id)?'#fff4d0':ed.color(r,'#b79a68')" :stroke-width="ed.lineWidth(r)" :style="ed.layerStyle(r)"><title>{{r.id}} · {{r.type}}</title></polygon>
      <g v-for="e in ed.visibleEdges" :key="e.id" v-memo="[e, ed.selection.edge, ed.layerList, scale]" data-kind="edge" :data-id="e.id" data-object="edge" :style="ed.layerStyle(e)">
       <path :d="edgePathData.get(e.id)??''" fill="none" stroke="transparent" :stroke-width="scale*1.6" pointer-events="stroke"/>
       <path :d="edgePathData.get(e.id)??''" fill="none" :stroke="ed.isSelected('edge',e.id)?'#effaff':ed.color(e,e.enabled?'#40a6c4':'#566175')" :stroke-width="ed.lineWidth(e)*(ed.isSelected('edge',e.id)?1.6:1)" :marker-end="Number(ed.zoom)>=1.5||ed.isSelected('edge',e.id)?'url(#planning-editor-arrow)':undefined" :marker-start="e.bidirectional&&(Number(ed.zoom)>=1.5||ed.isSelected('edge',e.id))?'url(#planning-editor-arrow)':undefined" pointer-events="none"/><title>{{e.id}} · {{e.from}} → {{e.to}} · {{e.distance.toFixed(2)}} m</title>
      </g>
      <g v-for="n in ed.visibleNodes" :key="n.id" v-memo="[n, ed.selection.node, ed.layerList, scale, nodeLabelIds.has(n.id)]" data-object="node" data-kind="node" :data-id="n.id" :data-node-type="n.type" :style="ed.layerStyle(n)">
       <circle :cx="n.x" :cy="-n.y" :r="scale*1.15" fill="transparent"/><circle :cx="n.x" :cy="-n.y" :r="nodeRadius" :fill="ed.isSelected('node',n.id)?'#effaff':ed.color(n,n.enabled?'#0d344a':'#344254')" :stroke="ed.isSelected('node',n.id)?'#70e0f2':ed.color(n,n.enabled?'#59bfd9':'#71829a')" :stroke-width="scale*.15"/><text v-if="nodeLabelIds.has(n.id)" class="editor-node-label" :x="n.x+scale*.85" :y="-n.y-scale*.65" fill="#bdcfe3" :font-size="scale*1.05">{{n.id}}</text><title>{{n.id}} · {{n.x.toFixed(3)}}, {{n.y.toFixed(3)}} m</title>
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
     <div class="editor-zoom"><button aria-label="编辑地图缩小" @click="ed.zoom=Math.max(.1,ed.zoom*.8)"><IconZoomOut :size="15"/></button><button @click="fit"><ActionIcon name="fit"/>适配</button><button aria-label="编辑地图放大" @click="ed.zoom=Math.min(40,ed.zoom*1.25)"><IconZoomIn :size="15"/></button></div>
     <div class="editor-coordinate">{{ed.cursor?`X ${ed.cursor.x.toFixed(3)} · Y ${ed.cursor.y.toFixed(3)} m`:'米制坐标'}} · {{Math.round(ed.zoom*100)}}% · 标签 {{nodeLabelIds.size}}/{{ed.visibleNodes.length}} · 已选 {{ed.count}}{{ed.measurement!==null?` · 测量 ${ed.measurement.toFixed(3)} m`:''}}</div>
    </div>
    <aside class="editor-inspector">
     <el-radio-group v-model="ed.panel" class="editor-panel-tabs" size="small" aria-label="编辑面板">
      <el-radio-button v-for="[id,label] in editorPanels" :key="id" :value="id"><ActionIcon :name="panelIcons[id]"/>{{label}}</el-radio-button>
     </el-radio-group>
     <MapEditorPanels v-if="ed.panel!=='properties'" :editor="ed"/>
     <div v-else class="inspector-properties">
      <div class="inspector-heading">
       <div><h3>{{selected?`属性 · ${selected}`:'地图属性'}}</h3><small>{{selected?'修改后点击应用，才会写入当前编辑稿。':'查看当前地图的基础信息。'}}</small></div>
       <el-tag v-if="selected" size="small" effect="plain">{{kind==='node'?'航点':kind==='edge'?'道路':kind==='resource'?'共享区域':'场景对象'}}</el-tag>
      </div>
      <el-form class="inspector-form" label-position="top" size="small" @submit.prevent>
       <template v-if="!selected">
        <el-form-item label="地图名称"><el-input v-model="definition.name" @change="dirty=true"/></el-form-item>
        <el-form-item label="坐标系"><el-input v-model="definition.coordinateSystem" @change="dirty=true"/></el-form-item>
        <el-alert type="info" :closable="false" show-icon title="当前坐标单位为米" description="修改坐标系文字不会执行坐标变换。原生 SMAP 须使用厂家坐标并核对版本。"/>
       </template>
       <template v-if="kind==='node'&&nodeForm">
        <el-form-item label="航点编号"><el-input v-model="nodeForm.id"/></el-form-item>
        <div class="inspector-field-grid">
         <el-form-item label="X（米）"><el-input-number v-model="nodeForm.x" :step=".1" controls-position="right"/></el-form-item>
         <el-form-item label="Y（米）"><el-input-number v-model="nodeForm.y" :step=".1" controls-position="right"/></el-form-item>
        </div>
        <el-form-item label="航点类型"><el-select v-model="nodeForm.type"><el-option label="普通航点" value="Waypoint"/><el-option label="业务站点" value="Station"/><el-option label="路口" value="Intersection"/><el-option label="等待位" value="WaitingPoint"/><el-option label="充电位" value="ChargingPoint"/></el-select></el-form-item>
        <el-form-item label="厂家站点编号"><el-input v-model="nodeForm.agvSiteId" placeholder="例如 LM1，按厂家地图填写"/></el-form-item>
        <el-checkbox v-model="nodeForm.enabled">启用航点</el-checkbox>
        <el-form-item label="关联共享区域"><el-select v-model="nodeForm.resourceIds" multiple filterable collapse-tags :max-collapse-tags="2" placeholder="未关联"><el-option v-for="r in definition.resources" :key="r.id" :label="r.id" :value="r.id"/></el-select></el-form-item>
       </template>
       <template v-if="kind==='edge'&&edgeForm">
        <el-form-item label="道路编号"><el-input v-model="edgeForm.id"/></el-form-item>
        <div class="inspector-field-grid">
         <el-form-item label="起点"><el-select v-model="edgeForm.from" filterable><el-option v-for="n in definition.nodes" :key="n.id" :label="n.id" :value="n.id"/></el-select></el-form-item>
         <el-form-item label="终点"><el-select v-model="edgeForm.to" filterable><el-option v-for="n in definition.nodes" :key="n.id" :label="n.id" :value="n.id"/></el-select></el-form-item>
        </div>
        <div class="inspector-field-grid">
         <el-form-item label="路径距离（米）"><el-input-number v-model="edgeForm.distance" :min=".001" :step=".1" controls-position="right"/></el-form-item>
         <el-form-item label="限速（米/秒）"><el-input-number v-model="edgeForm.speedLimit" :min=".001" :step=".1" controls-position="right"/></el-form-item>
        </div>
        <div class="inspector-checks"><el-checkbox v-model="edgeForm.bidirectional">双向通行</el-checkbox><el-checkbox v-model="edgeForm.enabled">启用道路</el-checkbox></div>
        <el-alert type="info" :closable="false" :title="`几何：${edgeForm.geometryKind}`" description="选中道路后可在画布拖动黄色控制点。"/>
        <div v-for="(p,i) in edgeForm.controlPoints??[]" :key="i" class="inspector-field-grid control-point-row">
         <el-form-item :label="`控制点 ${i+1} X`"><el-input-number v-model="p.x" controls-position="right"/></el-form-item>
         <el-form-item label="Y"><el-input-number v-model="p.y" controls-position="right"/></el-form-item>
        </div>
        <el-form-item label="关联共享区域"><el-select v-model="edgeForm.resourceIds" multiple filterable collapse-tags :max-collapse-tags="2" placeholder="未关联"><el-option v-for="r in definition.resources" :key="r.id" :label="r.id" :value="r.id"/></el-select></el-form-item>
        <VendorPathPanel :edge="edgeForm" :definition="definition" :backend="effectiveSource==='backend'"/>
       </template>
       <template v-if="kind==='resource'&&resourceForm">
        <el-form-item label="区域编号"><el-input v-model="resourceForm.id"/></el-form-item>
        <div class="inspector-field-grid">
         <el-form-item label="区域类型"><el-select v-model="resourceForm.type"><el-option label="窄通道" value="NarrowAisle"/><el-option label="路口" value="Intersection"/><el-option label="单行区" value="SingleLane"/><el-option label="工位" value="Station"/><el-option label="充电区" value="ChargingBay"/></el-select></el-form-item>
         <el-form-item label="容量"><el-input-number v-model="resourceForm.capacity" :min="1" :max="1000" controls-position="right"/></el-form-item>
        </div>
        <div class="inspector-subheading"><span>边界顶点（米）</span><el-button size="small" :icon="IconPlus" @click="resourceForm.boundary=[...(resourceForm.boundary??[]),{x:0,y:0}]">增加顶点</el-button></div>
        <div v-for="(p,i) in resourceForm.boundary??[]" :key="i" class="boundary-point-row">
         <span>{{i+1}}</span>
         <el-input-number v-model="p.x" controls-position="right" aria-label="边界点 X"/>
         <el-input-number v-model="p.y" controls-position="right" aria-label="边界点 Y"/>
         <el-button :icon="IconTrash" circle plain type="danger" aria-label="删除边界点" @click="resourceForm.boundary=resourceForm.boundary?.filter((_,index)=>index!==i)??[]"/>
        </div>
       </template>
       <template v-if="kind==='scene'&&sceneForm">
        <div class="inspector-field-grid">
         <el-form-item label="对象编号"><el-input v-model="sceneForm.id"/></el-form-item>
         <el-form-item label="名称 / 标注"><el-input v-model="sceneForm.name"/></el-form-item>
        </div>
        <el-form-item label="对象类型"><el-select v-model="sceneForm.kind"><el-option label="车间区域" value="area"/><el-option label="设备区域" value="equipment"/><el-option label="文字标注" value="text"/></el-select></el-form-item>
        <div class="inspector-field-grid">
         <el-form-item label="X"><el-input-number v-model="sceneForm.x" controls-position="right"/></el-form-item>
         <el-form-item label="Y"><el-input-number v-model="sceneForm.y" controls-position="right"/></el-form-item>
         <el-form-item label="宽"><el-input-number v-model="sceneForm.width" :min=".1" controls-position="right"/></el-form-item>
         <el-form-item label="高"><el-input-number v-model="sceneForm.height" :min=".1" controls-position="right"/></el-form-item>
        </div>
        <el-form-item label="显示颜色"><el-color-picker v-model="sceneForm.color"/></el-form-item>
        <el-checkbox v-model="sceneForm.visible">显示对象</el-checkbox>
       </template>
       <el-form-item v-if="activeForm" label="对象所属图层"><el-select v-model="activeForm.layerId"><el-option v-for="layer in ed.layerList" :key="layer.id" :label="layer.name" :value="layer.id"/></el-select></el-form-item>
      </el-form>
      <div v-if="selected" class="inspector-actions"><el-button type="primary" :icon="IconDeviceFloppy" @click="applyInspector">应用属性</el-button><el-button type="danger" plain :icon="IconTrash" @click="removeSelected">删除对象</el-button></div>
     </div>
    </aside></div>
   <section class="backend-map-tools">
    <div class="backend-tools-heading"><div><b>后台草稿</b><small>{{workspace?`${workspace.project.name} / V${workspace.draft.versionNumber}`:'当前编辑稿尚未关联后台'}}</small></div><el-tag :type="workspace?'success':'info'" size="small" effect="plain">{{workspace?'已关联':'未关联'}}</el-tag></div>
    <div class="backend-tools-row">
     <el-select v-model="mapId" class="backend-map-select" filterable placeholder="选择后台地图" aria-label="选择后台地图"><el-option v-for="m in maps" :key="m.mapId" :label="m.name" :value="m.mapId"><span>{{m.name}}</span><small>{{m.isActive?'当前使用':'可编辑'}}</small></el-option></el-select>
     <el-button :icon="IconRefresh" :disabled="busy||effectiveSource!=='backend'" @click="listMaps">刷新目录</el-button>
     <el-button @click="openMapPicker">浏览地图</el-button>
    </div>
    <div class="backend-tools-row backend-tools-actions">
     <el-button :disabled="busy||!mapId||effectiveSource!=='backend'" @click="openAssociation">关联并保留编辑稿</el-button>
     <el-button :disabled="busy||!mapId||effectiveSource!=='backend'" :icon="IconDownload" @click="loadWorkspace()">完整草稿覆盖当前编辑</el-button>
     <el-button :disabled="busy||effectiveSource!=='backend'" :icon="IconPlus" @click="createBackend">用当前编辑新建后台地图</el-button>
     <el-button type="primary" :disabled="!!saveBlocker" :title="saveBlocker||'保存到已关联后台草稿'" :icon="IconDeviceFloppy" @click="saveBackend">保存地图草稿</el-button>
     <el-button :icon="IconMap" @click="goToMapManagement">发布 / 激活</el-button>
    </div>
    <small class="backend-map-id">{{workspace?workspace.project.mapId:'关联会保留当前编辑；完整草稿读取会覆盖当前编辑，并在执行前再次确认。'}}</small>
   </section>
   <p v-if="saveBlocker" class="amber-text" role="status">暂不能保存：{{saveBlocker}}</p>
   <div class="validation-strip"><span :class="issues.length?'amber-text':'green-text'">{{issues.length?issues.join('；'):'本地结构检查通过'}}</span><small>本地检查不代替后台几何/安全校验；正式运行切换沿用WPF流程。未应用的属性不参与保存。</small></div><details v-if="versions.length"><summary>后台版本记录 {{versions.length}}</summary><p v-for="v in versions" :key="v.versionId">V{{v.versionNumber}} · {{mapVersionStatusLabel(v.status)}} · {{v.nodeCount}}节点 / {{v.edgeCount}}道路 · {{v.updatedAt}}</p></details>
  </template>
  <div v-else-if="tab==='plans'" class="plan-manager"><div class="workbench-guide"><h3>方案内容与生效范围</h3><p>方案包含地图、场景、车辆配置、航点顺序。保存于此浏览器；导出JSON可备份或跨电脑加载。加载方案只替换编辑稿，不下发小车，不切换运行地图。</p><p>地图JSON仅导出地图定义，可由主工程规范化JSON导入；方案JSON是Web工作台格式，不是厂家SMAP文件。SMAP在连接后台后选择文件，复用主工程解析器。</p><button @click="pending=emptyPlan()"><IconPlus :size="14"/>新建空白方案</button><button @click="edit(()=>{plan.id=uuid();plan.name+=' 副本';});saveLocal()"><IconCopy :size="14"/>另存为副本</button><button @click="exportMap"><ActionIcon name="download"/>导出地图JSON</button></div><div class="plan-cards"><article v-for="p in saved" :key="p.id"><h3>{{p.name}}</h3><p>{{p.definition.name}}</p><small>{{p.updatedAt}}</small><p>{{p.definition.nodes.length}} 航点 / {{p.vehicles.length}} 车辆 / {{p.scene.length}} 场景</p><button @click="pending=clone(p)"><ActionIcon name="preview"/>预览并加载</button><button @click="deleteLocal(p.id)"><ActionIcon name="remove"/>移出列表</button></article><p v-if="!saved.length" class="empty-panel">暂无本机方案。编辑后点击“保存方案”。</p></div></div>
  <div v-else-if="tab==='vehicles'" class="configuration-page"><div class="editor-toolbar"><button class="accent-button" @click="openVehicle()"><IconPlus :size="15"/>新增车辆</button><button :disabled="busy||effectiveSource!=='backend'" @click="readVehicles"><IconRefresh :size="15"/>读取后台车辆配置并替换</button><button :disabled="busy||effectiveSource!=='backend'||!vehicleRevision||!canManage||vehicleErrors.length>0" @click="saveVehicles"><ActionIcon name="save"/>保存车辆配置到后台</button><span>{{plan.vehicles.length}}辆 · {{restart?'后台配置待重启':'配置与在线状态分别管理'}}</span></div><p class="muted-note">新增车辆默认不启用。状态端口19204、导航端口19206按现场厂家配置填写；保存后台受未结束任务、配置版本及权限核验，服务重启后生效。此处不会修改PLC、全局通信开关或自动确认地图坐标绑定。</p><div class="management-table"><table><thead><tr><th>编号</th><th>名称</th><th>控制器IP</th><th>状态端口</th><th>导航端口</th><th>启用配置</th><th>当前观察</th><th>操作</th></tr></thead><tbody><tr v-for="v in plan.vehicles" :key="v.vehicleId"><td>{{v.vehicleId}}</td><td>{{v.displayName}}</td><td>{{v.ipAddress}}</td><td>{{v.port}}</td><td>{{v.navigationPort}}</td><td>{{v.enabled?'已勾选':'不启用'}}</td><td>{{effectiveSnapshot?.status?.agvs.find(a=>a.vehicleId===v.vehicleId)?.connection ? label(effectiveSnapshot?.status?.agvs.find(a=>a.vehicleId===v.vehicleId)?.connection) : '无在线数据'}}</td><td><button @click="openVehicle(v)"><ActionIcon name="edit"/>编辑</button><button @click="edit(()=>plan.vehicles=plan.vehicles.filter(n=>n.vehicleId!==v.vehicleId))"><ActionIcon name="remove"/>移出配置稿</button></td></tr></tbody></table></div><p v-if="!plan.vehicles.length" class="empty-panel">尚未配置车辆。点击新增，或连接后台读取已保存配置。</p><p class="amber-text">{{vehicleErrors.join('；')}}</p></div>
  <div v-else-if="tab==='waypoints'" class="waypoint-editor"><div><h3>航点顺序与路径核对</h3><p class="muted-note">顺序是方案中的导航检查列表。每两个航点之间按启用道路及方向计算最短距离；不包含实时交通预约、厂家可达性或PLC业务条件，不直接生成运动命令。生产任务仍使用现有九步业务流程。</p><div class="editor-toolbar"><select v-model="waypoint" aria-label="选择顺序航点"><option value="">选择航点</option><option v-for="n in definition.nodes.filter(n=>n.enabled)" :key="n.id" :value="n.id">{{n.id}} · {{n.agvSiteId||'未绑定'}}</option></select><button :disabled="!waypoint||plan.waypoints.length>=1000" @click="edit(()=>plan.waypoints.push(waypoint))">追加航点</button><button @click="edit(()=>plan.waypoints.reverse())">反转顺序</button><button @click="edit(()=>plan.waypoints=[])">清空顺序</button></div><ol class="waypoint-list"><li v-for="(id,i) in plan.waypoints" :key="i"><b>{{i+1}}</b><span>{{id}}<small>{{definition.nodes.find(n=>n.id===id)?.agvSiteId||'未绑定厂家站点'}}</small></span><button :disabled="i===0" :aria-label="`上移航点${i+1}`" @click="reorder(i,-1)"><IconArrowUp :size="15"/></button><button :disabled="i===plan.waypoints.length-1" :aria-label="`下移航点${i+1}`" @click="reorder(i,1)"><IconArrowDown :size="15"/></button><button :aria-label="`移除航点${i+1}`" @click="edit(()=>plan.waypoints.splice(i,1))"><IconTrash :size="15"/></button></li></ol></div><aside class="workbench-guide"><h3>核对结果</h3><p v-if="route.error" class="amber-text">{{route.error}}</p><template v-else><p class="green-text">导航图结构可达 · {{route.distance.toFixed(2)}} 米</p><h4>展开航点</h4><p>{{route.nodes.join(' → ')}}</p><h4>道路顺序</h4><p>{{route.edges.join(' → ')||'同一航点，无移动'}}</p><h4>沿途资源</h4><p>{{[...new Set([...route.edges.flatMap(id=>definition.edges.find(e=>e.id===id)?.resourceIds??[]),...route.nodes.flatMap(id=>definition.nodes.find(n=>n.id===id)?.resourceIds??[])])].join('、')||'未关联共享区域'}}</p></template></aside></div>
  <details v-if="importWarnings(plan).length" class="muted-note"><summary>导入核对提示（{{importWarnings(plan).length}}）</summary><p v-for="w in importWarnings(plan)" :key="w">{{w}}</p></details>
  <el-dialog v-model="mapPickerOpen" class="map-picker-dialog" width="min(900px, calc(100vw - 32px))" title="打开后台地图" append-to-body>
   <div class="map-picker-toolbar">
    <el-input v-model="mapPickerSearch" clearable :prefix-icon="IconSearch" placeholder="搜索地图名称、编号或坐标系"/>
    <el-button :icon="IconRefresh" :loading="busy" :disabled="effectiveSource!=='backend'" @click="listMaps">刷新目录</el-button>
    <el-button :icon="IconPlus" @click="startBlankMapFromPicker">新建空白地图</el-button>
   </div>
   <el-empty v-if="!filteredMaps.length" :description="maps.length?'没有匹配的地图':'后台地图目录为空'" :image-size="72"><el-button v-if="maps.length" @click="mapPickerSearch=''">清除搜索</el-button><el-button v-else type="primary" :icon="IconPlus" @click="startBlankMapFromPicker">新建空白地图</el-button></el-empty>
   <div v-else class="map-picker-list">
    <article v-for="map in filteredMaps" :key="map.mapId" :class="{active:map.isActive,current:map.mapId===mapId}">
     <div class="map-picker-card-head"><div><el-tag v-if="map.isActive" type="success" size="small" effect="plain">当前使用</el-tag><el-tag v-else size="small" effect="plain">可编辑</el-tag><strong>{{map.name}}</strong></div><el-button type="primary" plain @click="chooseMapFromPicker(map.mapId)">打开</el-button></div>
     <p>{{map.description||'未填写地图说明'}}</p>
     <div class="map-picker-meta"><span>{{map.coordinateSystem}}</span><span>{{map.unit}}</span><span>更新 {{map.updatedAt}}</span></div>
     <small>{{map.mapId}}</small>
    </article>
   </div>
   <template #footer><span class="map-picker-footnote">打开地图会读取该地图的完整后台草稿，并覆盖当前编辑稿。当前未保存修改会先确认。</span><el-button @click="mapPickerOpen=false">取消</el-button></template>
  </el-dialog>
  <ConsoleDialog v-if="association" title="关联已有后台地图 · 保留当前编辑稿" @close="association=null">
   <p>目标：<b>{{association.project.name}}</b> · V{{association.draft.versionNumber}}</p>
   <p>{{association.project.mapId}}</p>
   <p>当前 {{definition.nodes.length}} 个航点、{{definition.edges.length}} 条道路及已配置的坐标、用途会保留。点击保存后，将用当前导航内容更新这个目标的草稿；已有发布版本和运行地图保留。</p>
   <p class="amber-text">请核对目标地图。关联本身不会保存、发布或激活。</p>
   <template #footer><button @click="association=null">取消</button><button class="accent-button" :disabled="busy||effectiveSource!=='backend'" @click="confirmAssociation">确认关联并保留编辑稿</button></template>
  </ConsoleDialog>
  <ConsoleDialog v-if="runtimeOpen" title="准备运行地图 · 确认站点用途" wide @close="runtimeOpen=false">
   <p>从留存的原始SMAP重新生成运行图，自动还原车端米制坐标，同时转换圆弧、贝塞尔控制点和区域标注；保留厂家站点编号及有向连接。</p>
   <p>逐站选择用途。仅做点到点测试的停靠点可选“测试停靠 / 等待位”。区域文字不会自动生成PLC业务或交通锁。</p>
   <div class="management-table"><table><thead><tr><th>厂家站点</th><th>当前预览坐标（米）</th><th>确认用途</th></tr></thead><tbody><tr v-for="n in definition.nodes" :key="n.id"><td>{{n.agvSiteId||n.id}}</td><td>{{n.x.toFixed(3)}} / {{n.y.toFixed(3)}}</td><td><select v-model="runtimeRoles[n.agvSiteId??n.id]" :aria-label="`${n.agvSiteId||n.id}用途`"><option value="">请选择用途</option><option value="WaitingPoint">测试停靠 / 等待位</option><option value="Station">业务停靠站点</option><option value="Waypoint">途经航点</option><option value="Intersection">路口</option><option value="ChargingPoint">充电位</option></select></td></tr></tbody></table></div>
   <label class="check-label"><input v-model="runtimeCoordinates" type="checkbox"/>确认本图属于目标机器人，使用原SMAP的车端坐标。</label>
   <label class="check-label"><input v-model="runtimeReplace" type="checkbox"/>按原图重建当前导航编辑稿并启用全部已转换道路；当前手工修改的导航内容将被替换。</label>
   <p class="muted-note">准备完成 → 保存当前后台草稿（先关联已有地图，首次也可新建后台地图）→ 地图管理发布 → 激活与安全切换 → 核对后放行。原有版本记录保留；本操作不保存、不激活、不发送车辆动作。</p>
   <template #footer><button :disabled="busy" @click="runtimeOpen=false">取消</button><button class="accent-button" :disabled="busy||!runtimeReady" @click="prepareRuntime">还原坐标并应用到编辑稿</button></template>
  </ConsoleDialog>
  <div v-if="robotMapDownload" class="muted-note"><p>最近拉取：{{robotMapDownload.mapName}} · {{robotMapDownload.downloadedAt}} · 车端MD5：{{robotMapDownload.robotMapMd5}}</p><p>保存内容SHA256：{{robotMapDownload.contentSha256}} <a :href="'/api/robots/map/files/'+robotMapDownload.contentSha256" download>保存原生SMAP</a></p><p>读取机器人已保存的当前地图，不含RoboshopPro尚未下发到车端的编辑；不会自动替换运行图。</p></div>
  <ConsoleDialog v-if="pending" title="核对加载内容" @close="pending=null"><h3>{{pending.name}}</h3><p v-for="w in importWarnings(pending)" :key="w" class="amber-text">{{w}}</p><p class="muted-note">{{pending.definition.nodes.length}} 航点 / {{pending.definition.edges.length}} 道路 / {{pending.scene.length}} 场景 / {{pending.vehicles.length}} 车辆 / {{pending.waypoints.length}} 顺序航点</p><p>加载将替换当前编辑稿并解除后台关联。尚未保存的编辑可先取消，再保存或导出；加载不影响后台运行配置。</p><template #footer><button @click="pending=null"><ActionIcon name="close"/>取消</button><button class="accent-button" @click="replace(pending!);pending=null;message='已加载到编辑稿，请按模块核对并保存。'"><ActionIcon name="upload"/>加载到编辑稿</button></template></ConsoleDialog>
  <ConsoleDialog v-if="vehicleForm" :title="vehicleOriginal?'编辑车辆配置':'新增车辆配置'" @close="vehicleForm=null"><div class="creation-grid"><label>车辆编号<input v-model="vehicleForm.vehicleId"/></label><label>显示名称<input v-model="vehicleForm.displayName"/></label><label>控制器IPv4<input v-model="vehicleForm.ipAddress" placeholder="例如 192.168.10.21"/></label><label>状态端口<input v-model.number="vehicleForm.port" type="number" min="1" max="65535"/></label><label>导航端口<input v-model.number="vehicleForm.navigationPort" type="number" min="1" max="65535"/></label><label class="check-label"><input v-model="vehicleForm.enabled" type="checkbox"/>配置为启用（重启后）</label></div><p v-if="vehicleMessage" class="inline-warning" role="alert">{{vehicleMessage}}</p><p class="muted-note">填写小车控制器IP。RoboShop是电脑上的工具，电脑IP与车辆IP分别配置。新车辆还需核定厂家地图和坐标绑定，新增配置不等于可以派车。</p><template #footer><button @click="vehicleForm=null"><ActionIcon name="close"/>取消</button><button class="accent-button" @click="applyVehicle"><ActionIcon name="check"/>应用到车辆配置稿</button></template></ConsoleDialog>
 </section>
</template>
<style scoped>
.workflow-guide{display:flex;align-items:center;gap:8px;padding:8px 11px;background:var(--ui-surface-soft);border:1px solid var(--ui-line);border-radius:var(--ui-radius-sm);font-size:10px;color:var(--ui-text-2);flex-wrap:wrap}
.workflow-guide b{color:var(--ui-text);margin-right:4px}
.workflow-guide span{white-space:nowrap}
</style>
