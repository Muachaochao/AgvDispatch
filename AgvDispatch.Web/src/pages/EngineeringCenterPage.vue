<script setup lang="ts">
import { computed, nextTick, onMounted, onBeforeUnmount, ref, watch } from 'vue';
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import OrderedStationPicker from '../components/OrderedStationPicker.vue';
import EngineeringTaskTemplatePanel from '../components/EngineeringTaskTemplatePanel.vue';
import { sections, newPlan, newRow, csvRows, exportCsv, importDiff, type Section, type EngineeringPlan, type EngineeringRow, type Field } from '../engineering-center';
import { engineeringFieldValueLabel, industrialText } from '../operator-labels';
import { taskStepName } from '../task-step-labels';
import EngineeringRuntimePage from './EngineeringRuntimePage.vue';
import EngineeringSettingsPage from './EngineeringSettingsPage.vue';
import TestCenterPage from './TestCenterPage.vue';
import CommissioningPanel from '../CommissioningPanel.vue';
import CommunicationPage from './CommunicationPage.vue';
const props=defineProps<{snapshot?:any;adapter?:any}>();
const store=useDispatchStore(),route=useRoute(),router=useRouter();
const plan=ref<EngineeringPlan>(newPlan()),revision=ref(''),savedText=ref(''),ready=ref(false),busy=ref(false),message=ref(''),error=ref('');
const search=ref(''),page=ref(1),selected=ref<EngineeringRow|null>(null),checked=ref<string[]>([]);
const validation=ref<any>(null),versions=ref<any[]>([]),versionId=ref(String(route.query.version??'')),publishNote=ref(''),report=ref<any>(null);
const runtimeState=ref<any>(null);
const importPreview=ref<ReturnType<typeof importDiff>|null>(null),importSection=ref<Section>('stations');
const sampleStations=ref(300),sampleVehicles=ref(5),callRounds=ref(1),callInterval=ref(60);
const frozenPlan=ref<EngineeringPlan|null>(null),versionLoading=ref(false),callStations=ref<string[]>([]),offline=ref<string[]>([]),blockedResources=ref<string[]>([]);
const simulationStations=computed(()=>(frozenPlan.value?.stations??[]).filter(s=>s.enabled).map(s=>({id:String(s.id),label:`${s.name} · ${s.id} · ${s.pickupNode} · ${s.poolId} · ${s.dumperId}`})));
watch(versionId,async id=>{
 frozenPlan.value=null;callStations.value=[];offline.value=[];blockedResources.value=[];report.value=null;
 if(!id){versionLoading.value=false;return;}versionLoading.value=true;
 try{const v:any=await store.api('/api/engineering/versions/'+id);if(versionId.value!==id)return;frozenPlan.value=v.plan;callStations.value=v.plan.stations.filter((s:any)=>s.enabled).map((s:any)=>s.id);}
 catch(e){if(versionId.value===id)error.value=String(e);}finally{if(versionId.value===id)versionLoading.value=false;}
});
const mapCatalog=ref<any[]>([]),targetMapId=ref('');
const batchField=ref('poolId'),batchValue=ref('');const reportPage=ref(1);
const tabs=[
 {id:'overview',label:'工程总览',group:'配置'},
 {id:'vehicles',label:'车辆与包络',group:'业务与车辆'},
 {id:'pools',label:'车辆池',group:'业务与车辆'},
 {id:'stations',label:'工位映射',group:'业务与车辆'},
 {id:'templates',label:'任务模板',group:'业务与车辆'},
 {id:'dumpers',label:'倒料设备',group:'业务与车辆'},
 {id:'standbys',label:'待机位',group:'业务与车辆'},
 {id:'nodes',label:'地图节点',group:'地图与资源'},
 {id:'edges',label:'道路',group:'地图与资源'},
 {id:'resources',label:'交通资源',group:'地图与资源'},
 {id:'plcs',label:'PLC连接',group:'通信与信号'},
 {id:'signals',label:'PLC点表',group:'通信与信号'},
 {id:'validation',label:'校验与诊断',group:'发布与接入'},
 {id:'versions',label:'版本与发布',group:'发布与接入'},
 {id:'runtime',label:'执行接入',group:'发布与接入'},
 {id:'settings',label:'任务参数与地图绑定',group:'发布与接入'},
 {id:'commissioning',label:'运行核验',group:'发布与接入'},
 {id:'communication',label:'通信与设备',group:'设备与能源'},
 {id:'charging-run',label:'充电运行',group:'设备与能源'},
 {id:'tests',label:'测试中心',group:'测试与验证'},
 {id:'simulation',label:'配置模拟',group:'测试与验证'}
];
const commonTabIds=new Set(['overview','vehicles','pools','stations','templates','dumpers','standbys','validation','versions']);
const engineeringLevel=ref<'common'|'advanced'>('common');
const visibleTabs=computed(()=>tabs.filter(item=>engineeringLevel.value==='common'?commonTabIds.has(item.id):!commonTabIds.has(item.id)));
const tab=computed(()=>tabs.some(t=>t.id===route.query.tab)?String(route.query.tab):'overview');
const section=computed(()=>tab.value in sections?tab.value as Section:null);
const dirty=computed(()=>savedText.value!==JSON.stringify(plan.value));
const rows=computed(()=>section.value?plan.value[section.value]:[]);
const filtered=computed(()=>rows.value.filter(r=>Object.values(r).join(' ').toLowerCase().includes(search.value.toLowerCase())));
const shown=computed(()=>filtered.value.slice((page.value-1)*25,page.value*25));
const columns=computed(()=>section.value?sections[section.value].fields.filter(f=>f.type!=='list').slice(0,5):[]);
const selectedVersion=computed(()=>versions.value.find(v=>v.id===versionId.value));
const appliedVersionId=computed(()=>runtimeState.value?.configuration?.engineeringSource?.versionId??'');
const reportTasks=computed(()=>report.value?.tasks?.slice((reportPage.value-1)*25,reportPage.value*25)??[]);
function navigate(id:string,extra:Record<string,string>={}){void router.push({path:'/engineering',query:{tab:id,...extra}});}
function setEngineeringLevel(level:'common'|'advanced'){
 engineeringLevel.value=level;
 const allowed=level==='common'?commonTabIds.has(tab.value):!commonTabIds.has(tab.value);
 if(!allowed)navigate(level==='common'?'overview':'nodes');
}
function openRuntime(){if(versionId.value)navigate('runtime',{version:versionId.value});}
function commissioningNavigate(target:string){const path=target==='tasks'?'/tasks':target==='map'?'/maps':target==='vehicles'||target==='vehicle-status'?'/vehicles':'/engineering';void router.push(path);}
function openCharging(){void router.push('/charging');}
watch(()=>route.query.tab,value=>{
 const current=tabs.some(t=>t.id===value)?String(value):'overview';
 engineeringLevel.value=commonTabIds.has(current)?'common':'advanced';
 search.value='';page.value=1;selected.value=null;checked.value=[];importPreview.value=null;
},{immediate:true});
watch(()=>route.query.version,value=>{const id=typeof value==='string'?value:'';if(id&&id!==versionId.value)versionId.value=id;});
watch(search,()=>{page.value=1;checked.value=[];});
watch(()=>[route.query.tab,route.query.object],()=>{void nextTick(focusObject);});
function discardAllowed(){return !ready.value||!dirty.value||window.confirm('当前工程有未保存修改。确定放弃这些修改？');}
onBeforeRouteLeave(()=>discardAllowed());
function beforeUnload(e:BeforeUnloadEvent){if(ready.value&&dirty.value){e.preventDefault();e.returnValue='';}}
window.addEventListener('beforeunload',beforeUnload);
onBeforeUnmount(()=>window.removeEventListener('beforeunload',beforeUnload));
function focusObject(){if(section.value&&typeof route.query.object==='string'){search.value=route.query.object;selected.value=rows.value.find(r=>r.id===route.query.object)??null;}}
async function action(fn:()=>Promise<void>){if(busy.value)return;busy.value=true;error.value='';message.value='';try{await fn();}catch(e){error.value=e instanceof Error?e.message:String(e);}finally{busy.value=false;}}
async function load(){if(!discardAllowed())return;await action(async()=>{const d:any=await store.api('/api/engineering/draft');plan.value=d.plan;revision.value=d.revision;savedText.value=JSON.stringify(d.plan);ready.value=true;validation.value=null;selected.value=null;versions.value=await store.api('/api/engineering/versions');try{runtimeState.value=await store.api('/api/engineering-runtime/state');}catch{runtimeState.value=null;}if(!versionId.value||!versions.value.some((v:any)=>v.id===versionId.value))versionId.value=versions.value[0]?.id??'';focusObject();message.value='已读取工程草稿。工程草稿可独立保存，不依赖车辆在线或现场恢复放行。';});}
async function save(){await action(async()=>{const d:any=await store.api('/api/engineering/draft',{expectedRevision:revision.value,plan:plan.value});revision.value=d.revision;savedText.value=JSON.stringify(d.plan);message.value='工程草稿已保存；未改变现场运行配置、未发送设备动作。';});}
async function validate(){await action(async()=>{validation.value=await store.api('/api/engineering/validate',plan.value);navigate('validation');message.value=validation.value.valid?'配置静态校验通过，可发布工程版本。':'发现配置缺项，请点击问题跳转修改。';});}
async function sample(){if(!discardAllowed())return;await action(async()=>{plan.value=await store.api(`/api/engineering/sample?stations=${sampleStations.value}&vehicles=${sampleVehicles.value}`);validation.value=null;selected.value=null;message.value='示例已载入编辑区，尚未保存。所有地址与数值都是模拟数据。';});}
async function publish(){await action(async()=>{if(dirty.value)throw new Error('请先保存当前修改，再发布同一修订。');const v:any=await store.api('/api/engineering/publish',{expectedRevision:revision.value,note:publishNote.value});versions.value=await store.api('/api/engineering/versions');versionId.value=v.id;message.value='工程版本已发布并冻结。下一步可直接在本工作台接入执行器，不会自动创建任务或移动车辆。';});}
async function simulate(){await action(async()=>{
 if(!versionId.value||!frozenPlan.value||versionLoading.value)throw new Error('请先读取已发布版本。');
 if(!callStations.value.length||callStations.value.some(id=>!simulationStations.value.some(s=>s.id===id)))throw new Error('请选择当前版本的启用工位。');
 if(callRounds.value<1||callRounds.value>100||!Number.isInteger(callRounds.value)||callStations.value.length*callRounds.value>3000)throw new Error('轮数需1–100，呼叫总数最多3000。');
 if(!Number.isFinite(callInterval.value)||callInterval.value<0||(callRounds.value-1)*callInterval.value>86400)throw new Error('呼叫间隔必须为非负数，总呼叫时间不得超过86400秒。');
 const calls=Array.from({length:callRounds.value},(_,r)=>callStations.value.map(id=>({requestId:`CALL-${r+1}-${id}`,stationId:id,atSeconds:r*callInterval.value}))).flat();
 report.value=await store.api('/api/engineering/simulations',{versionId:versionId.value,calls,offlineVehicles:offline.value,blockedResources:blockedResources.value});reportPage.value=1;message.value='模拟已完成；结果已保存到后台工程报告。没有连接任何设备。';
});}
const split=(v:string)=>v.split(';').map(x=>x.trim()).filter(Boolean);
function value(row:EngineeringRow,f:Field){const v=row[f.key];return Array.isArray(v)?v.join(';'):v;}
function update(row:EngineeringRow,f:Field,event:Event){const target=event.target as HTMLInputElement;row[f.key]=f.type==='boolean'?target.checked:f.type==='number'?(target.value===''?'':Number(target.value)):f.type==='list'?split(target.value):target.value;validation.value=null;}
function add(){if(!section.value)return;const row=newRow(section.value);row.id='NEW-'+crypto.randomUUID().slice(0,8);rows.value.push(row);search.value='';page.value=Math.ceil(rows.value.length/25);selected.value=row;}
function copy(){if(!section.value||!selected.value)return;const row=structuredClone(JSON.parse(JSON.stringify(selected.value))) as EngineeringRow;row.id=String(row.id)+'-COPY';rows.value.push(row);selected.value=row;search.value='';page.value=Math.ceil(rows.value.length/25);}
function remove(){if(!section.value||!selected.value)return;const current=selected.value;plan.value[section.value]=rows.value.filter(r=>r!==current);selected.value=null;validation.value=null;}
function check(id:string,event:Event){checked.value=(event.target as HTMLInputElement).checked?[...checked.value,id]:checked.value.filter(x=>x!==id);}
function batch(){if(section.value!=='stations'||checked.value.length===0)return;const field=sections.stations.fields.find(f=>f.key===batchField.value)!;
 for(const row of rows.value.filter(r=>checked.value.includes(String(r.id))))row[batchField.value]=field.type==='number'?Number(batchValue.value):batchValue.value;validation.value=null;message.value=`已修改${checked.value.length}个工位，尚未保存。`;
}
function download(name:string,text:string,type='application/json'){const url=URL.createObjectURL(new Blob([text],{type}));const a=document.createElement('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),0);}
async function importFile(event:Event){const input=event.target as HTMLInputElement;const file=input.files?.[0];input.value='';if(!file||!section.value)return;
 await action(async()=>{if(file.size>4*1024*1024)throw new Error('导入文件最多4MiB。');importSection.value=section.value!;importPreview.value=importDiff(rows.value,csvRows(section.value!,await file.text()));});
}
function applyImport(){if(!importPreview.value||section.value!==importSection.value)return;plan.value[importSection.value]=importPreview.value.merged;message.value='已合并到编辑区，未列出的原对象保留。请校验并保存。';importPreview.value=null;selected.value=null;validation.value=null;}
async function importProject(event:Event){const input=event.target as HTMLInputElement;const file=input.files?.[0];input.value='';if(!file||!discardAllowed())return;await action(async()=>{if(file.size>4*1024*1024)throw new Error('工程文件最多4MiB。');const imported=JSON.parse(await file.text());const p=imported.plan??imported;await store.api('/api/engineering/validate',p);plan.value=p;selected.value=null;validation.value=null;message.value='工程已载入编辑区；请检查差异后保存。';});}
async function versionToDraft(){if(!discardAllowed())return;await action(async()=>{const v:any=await store.api('/api/engineering/versions/'+versionId.value);plan.value=v.plan;message.value='版本内容已载入编辑区；保存会创建新的草稿修订，保留原版本。';});}
async function preflightRollback(v:any){
  if(!v?.id)return;
  if(!window.confirm(`预检回滚到工程版本“${v.name}”？\n\n此操作只做兼容性预检，不会立即应用。预检通过后仍需在执行接入中核对动作并输入确认文字。`))return;
  await action(async()=>{
    versionId.value=v.id;
    const preview:any=await store.api('/api/engineering-runtime/preview',{versionId:v.id});
    if(!preview.ready||!preview.mapActive){
      const blockers=[...(preview.blockers??[])];
      if(!preview.mapActive)blockers.push(`该工程绑定地图 ${preview.mapVersionId}，当前运行地图不同；请先将对应地图安全激活，或把该版本载入草稿重新绑定当前地图。`);
      throw new Error('不能直接回滚：'+blockers.join('；'));
    }
    message.value='回滚预检通过；请在执行接入中核对动作，并完成确认文字后再应用。';
    navigate('runtime',{version:v.id,rollback:'1'});
  });
}
function copyMap(id:string,version:string,d:any){
 if(!d?.nodes||!d?.edges||!d?.resources)throw new Error('后台未返回完整发布地图定义。');
 plan.value.mapId=id;plan.value.mapVersion=version;
 plan.value.nodes=d.nodes.map((n:any)=>({id:n.id,vendorSite:n.agvSiteId??'',x:n.x,y:n.y,enabled:n.enabled}));
 plan.value.edges=d.edges.map((e:any)=>({id:e.id,from:e.from,to:e.to,distance:e.distance,speed:e.speedLimit,resourceIds:e.resourceIds??[],bidirectional:e.bidirectional,enabled:e.enabled}));
 plan.value.resources=d.resources.map((r:any)=>({id:r.id,name:r.id,kind:r.type==='NarrowAisle'||r.type==='SingleLane'?'NarrowAisle':r.type==='Intersection'?'Intersection':'Station',capacity:r.capacity}));
 validation.value=null;selected.value=null;
 message.value='已复制准确地图拓扑到工程编辑区；请重新核对工位和资源绑定。没有修改或激活现场地图。';
}
async function loadMapCatalog(){if(store.engineeringSimulationOnly)return;await action(async()=>{mapCatalog.value=await store.api('/api/maps');});}
async function importPublishedMap(){if(!targetMapId.value||store.engineeringSimulationOnly)return;await action(async()=>{
 const w:any=await store.api(`/api/maps/${encodeURIComponent(targetMapId.value)}/workspace`);const v=w.published;
 if(!v||v.status!=='Published')throw new Error('目标地图尚无已发布版本，请先去地图编辑发布。');
 copyMap(w.project.mapId,v.versionId,v.definition);
});}
async function importMap(){if(store.engineeringSimulationOnly)return;await action(async()=>{
 const current:any=await store.api('/api/snapshot');if(!current.connected||!current.map)throw new Error('未读取到当前运行地图。');const m=current.map;
 const workspace:any=await store.api(`/api/maps/${encodeURIComponent(m.id)}/workspace`);
 const source=[workspace.draft,workspace.published].find(v=>v?.versionId===m.version);
 if(!source)throw new Error('当前运行版本不在工作区，请选择明确的已发布地图。');
 copyMap(m.id,m.version,source.definition);
});}
onMounted(load);
</script>

<template>
<section class="engineering-center management-page">
 <header class="management-title engineering-workbench-title"><div><h2>工程工作台</h2><p>配置、校验、发布、接入与运行核验在同一工作区完成</p></div><span class="mode-badge">配置操作不发送设备指令</span></header>
 <div class="engineering-toolbar"><button :disabled="busy" @click="load">重新读取</button><button class="accent-button" :disabled="busy||!ready||!dirty" @click="save">保存工程草稿</button><button :disabled="busy||!ready" @click="validate">统一校验</button><button :disabled="!ready" @click="download('engineering-plan.json',JSON.stringify(plan,null,2))">导出工程</button><label class="file-button">导入工程<input type="file" accept=".json" :disabled="busy" @change="importProject"/></label><span>{{dirty?'有未保存修改':'草稿已保存'}} · {{revision?revision.slice(0,8):'未建稿'}}</span></div>
 <p class="engineering-boundary">左侧先完成资源与业务配置；随后在同一页依次校验、发布、执行接入和运行核验。只有通信、地图编辑和单车调试等独立模块保留单独页面。</p>
 <p v-if="error" class="inline-warning" role="alert">{{industrialText(error)}}</p><p v-if="message" class="engineering-message" role="status">{{industrialText(message)}}</p>
 <div class="engineering-layout">
 <nav class="engineering-sidebar" aria-label="工程工作台分类"><div class="engineering-level-switch" aria-label="工程配置级别"><button :class="{active:engineeringLevel==='common'}" @click="setEngineeringLevel('common')"><span>常用配置</span><small>日常业务维护</small></button><button :class="{active:engineeringLevel==='advanced'}" @click="setEngineeringLevel('advanced')"><span>高级配置</span><small>地图、通信、联调</small></button></div><template v-for="(item,index) in visibleTabs" :key="item.id"><p v-if="index===0||visibleTabs[index-1]?.group!==item.group" class="engineering-nav-group">{{item.group}}</p><button :class="{active:tab===item.id}" :aria-current="tab===item.id?'page':undefined" @click="navigate(item.id)"><span>{{item.label}}</span><small v-if="item.id in sections">{{plan[item.id as Section].length}}</small></button></template></nav>
 <main class="engineering-content" :aria-busy="busy">
 <template v-if="tab==='overview'">
  <h3>工程概况</h3><div class="engineering-form"><label>工程名称<input v-model="plan.name"/></label><label>地图编号<input v-model="plan.mapId"/></label><label>地图版本<input v-model="plan.mapVersion"/></label></div>
  <div class="engineering-stats"><div v-for="key in (['stations','vehicles','pools','dumpers'] as Section[])" :key="key"><b>{{plan[key].length}}</b><span>{{sections[key].label}}</span></div></div>
  <h3>载入隔离示例</h3><p>示例会替换当前编辑区。先导出或保存已有修改。示例使用虚构地址，不读取或连接现场设备。</p><div class="engineering-form"><label>工位数量<input v-model.number="sampleStations" type="number" min="1" max="1000"/></label><label>车辆数量<input v-model.number="sampleVehicles" type="number" min="1" max="20"/></label><button :disabled="busy" @click="sample">载入示例到编辑区</button></div>
<h3>相关工作区</h3><div class="engineering-links"><button @click="navigate('communication')">PLC／AGV／充电桩通信</button><button @click="navigate('settings')">任务参数与地图绑定</button><button @click="navigate('commissioning')">运行条件与通信诊断</button><button @click="navigate('tests')">测试中心</button><button @click="openCharging">充电运行管理</button><RouterLink to="/planning">地图编辑与交通资源</RouterLink><RouterLink to="/maps">地图版本与安全切换</RouterLink><RouterLink to="/recovery">任务取消与启动恢复</RouterLink></div><p class="engineering-boundary">充电运行管理保留独立页面，因为它会发起实时设备操作、展示会话和告警；通信及绑定参数已经并入本工作台。</p><button :disabled="busy||store.engineeringSimulationOnly" @click="importMap">复制当前运行地图到工程草稿</button>
  <div class="engineering-form"><button :disabled="busy||store.engineeringSimulationOnly" @click="loadMapCatalog">读取已发布地图目录</button><label>目标地图<select v-model="targetMapId"><option value="">请选择</option><option v-for="m in mapCatalog" :key="m.mapId" :value="m.mapId">{{m.name}} · {{m.publishedVersionId||'未发布'}}</option></select></label><button :disabled="busy||!targetMapId||store.engineeringSimulationOnly" @click="importPublishedMap">复制所选发布地图到工程草稿</button></div><p>可以先绑定尚未激活的新地图；发布工程后再进入工程执行接入完成地图切换。</p>
 </template>
 <template v-else-if="tab==='templates'">
  <EngineeringTaskTemplatePanel :plan="plan" @message="message=$event" />
 </template>
 <template v-else-if="section">
  <h3>{{sections[section].label}}</h3><p>{{sections[section].description}}</p>
  <div class="engineering-toolbar"><input v-model="search" aria-label="搜索配置对象" placeholder="搜索编号、区域、绑定…"/><button @click="add">新增</button><button @click="download(section+'.csv',exportCsv(section,rows),'text/csv;charset=utf-8')">导出CSV / 模板</button><label class="file-button">导入CSV<input type="file" accept=".csv" :disabled="busy" @change="importFile"/></label></div>
  <div v-if="section==='stations'" class="engineering-toolbar"><button @click="checked=filtered.map(r=>String(r.id))">选择筛选结果（{{filtered.length}}）</button><span>已选{{checked.length}}</span><select v-model="batchField" aria-label="批量修改字段"><option v-for="f in sections.stations.fields.filter(f=>['area','poolId','templateId','dumperId','priority'].includes(f.key))" :key="f.key" :value="f.key">{{f.label}}</option></select><input v-model="batchValue" aria-label="批量修改值" placeholder="填写目标值"/><button :disabled="!checked.length||!batchValue" @click="batch">应用到所选工位</button></div>
  <div v-if="importPreview" class="engineering-import"><h4>导入差异预览</h4><p>新增{{importPreview.added}} · 修改{{importPreview.changed}} · 未变{{importPreview.unchanged}}。仅按编号合并，不删除未列出的原对象。</p><details><summary>查看前50项差异</summary><div v-for="c in importPreview.changes.slice(0,50)" :key="c.id"><b>{{c.id}} · {{c.status}}</b><pre v-if="c.status==='修改'">原：{{c.before}}
新：{{c.after}}</pre></div></details><button @click="applyImport">确认合并到编辑区</button><button @click="importPreview=null">取消导入</button></div>
  <div class="engineering-table-layout"><div><div class="engineering-table-scroll"><table class="engineering-table"><thead><tr><th v-if="section==='stations'">选择</th><th v-for="f in columns" :key="f.key">{{f.label}}</th><th>操作</th></tr></thead><tbody><tr v-for="(row,i) in shown" :key="(page-1)*25+i" :class="{selected:selected===row}"><td v-if="section==='stations'"><input type="checkbox" :aria-label="'选择'+row.id" :checked="checked.includes(String(row.id))" @change="check(String(row.id),$event)"/></td><td v-for="f in columns" :key="f.key">{{engineeringFieldValueLabel(f.key,value(row,f))}}</td><td><button @click="selected=row">编辑</button></td></tr><tr v-if="!shown.length"><td :colspan="columns.length+2">没有匹配对象。可新增或导入CSV。</td></tr></tbody></table></div><div class="engineering-toolbar"><button :disabled="page<=1" @click="page--">上一页</button><span>第{{page}} / {{Math.max(1,Math.ceil(filtered.length/25))}}页 · {{filtered.length}}项</span><button :disabled="page*25>=filtered.length" @click="page++">下一页</button></div></div>
  <fieldset v-if="selected" class="engineering-detail"><legend>编辑 {{selected.id}}</legend><label v-for="f in sections[section].fields" :key="f.key">{{f.label}}
  <input v-if="f.type==='boolean'" type="checkbox" :checked="!!selected[f.key]" @change="update(selected,f,$event)"/>
  <select v-else-if="f.options" :value="selected[f.key]" @change="update(selected,f,$event)"><option v-for="o in f.options" :key="o" :value="o">{{engineeringFieldValueLabel(f.key,o)}}</option></select>
  <input v-else :value="value(selected,f)" :type="f.type==='number'?'number':'text'" :step="f.type==='number'?'any':undefined" :list="f.ref?'engineering-ref-'+f.ref:undefined" @input="update(selected,f,$event)"/>
  <small v-if="f.help">{{f.help}}</small></label><div class="engineering-toolbar"><button @click="copy">复制对象</button><button @click="remove">从草稿删除</button><button @click="selected=null">收起</button></div></fieldset></div>
 </template>
 <template v-else-if="tab==='validation'">
  <h3>统一校验与问题定位</h3><p>校验当前编辑区，包括PLC缺项、地址冲突、地图绑定、车辆能力、待机容量及路线可达性。</p><button :disabled="busy" @click="validate">重新校验</button><p v-if="!validation">尚未校验当前编辑区。</p><template v-else><p :class="validation.valid?'engineering-message':'inline-warning'">{{validation.valid?'静态配置通过':'存在阻塞项'}} · {{validation.issues.length}}项问题</p><ul class="engineering-issues"><li v-for="(i,n) in validation.issues" :key="n"><span>{{i.severity==='Error'?'阻塞':'提示'}} · {{i.objectId}}</span><p>{{industrialText(i.message)}}</p><RouterLink :to="i.link">进入{{sections[i.section as Section]?.label??'工程概况'}}修改</RouterLink></li></ul></template>
  <p>静态通过不证明地图坐标对齐、实车包络核定或PLC现场动作完成。现场任务阻塞请进入 <RouterLink to="/recovery">异常恢复</RouterLink> 或 <RouterLink to="/commissioning">联调检查</RouterLink>。</p>
 </template>
 <template v-else-if="tab==='versions'">
  <h3>发布工程版本</h3><p>版本包含当前地图拓扑、工位、PLC点表、车辆、模板与资源，保存后冻结内容和摘要。现场运行版本保持独立。</p><label class="engineering-note">发布说明<textarea v-model="publishNote" maxlength="1024" placeholder="说明本次新增工位、绑定及资源调整"/></label><button :disabled="busy||dirty||!revision||!publishNote.trim()" @click="publish">校验并发布工程版本</button><p v-if="dirty" class="inline-warning">当前编辑尚未保存，请先点击“保存工程草稿”。</p>
  <p class="engineering-boundary">历史版本不可修改。当前运行版本带“正在运行”标记；“预检回滚”只验证旧版本与当前地图和通信是否兼容，通过后仍需完成动作核对和确认文字。地图版本不同时，系统会阻止直接回滚。</p>
  <ul class="engineering-versions"><li v-for="v in versions" :key="v.id" :class="{current:v.id===appliedVersionId}"><label><input v-model="versionId" type="radio" :value="v.id"/>{{v.name}} · {{v.stations}}工位／{{v.vehicles}}车 <b v-if="v.id===appliedVersionId" class="version-state current">正在运行</b></label><p>{{v.note}} · {{new Date(v.publishedAt).toLocaleString('zh-CN',{timeZone:'Asia/Shanghai'})}}</p><small>{{v.id}} · 地图 {{v.mapVersion??'未记录'}} · SHA256 {{v.contentHash}}</small><div class="engineering-toolbar"><button :disabled="busy||v.id===appliedVersionId" @click="preflightRollback(v)">预检回滚</button><button :disabled="busy" @click="versionId=v.id;versionToDraft()">载入新草稿</button></div></li><li v-if="!versions.length" class="engineering-empty">暂无可读取版本。发布后刷新本页即可接入。</li></ul><div class="engineering-toolbar"><button :disabled="!versionId||busy" @click="versionToDraft">所选版本载入编辑区</button><button :disabled="!versionId" @click="navigate('simulation')">测试所选版本</button><button class="accent-button" :disabled="!versionId" @click="openRuntime">在同一工作台接入执行器</button></div>
 </template>
 <template v-else-if="tab==='runtime'">
  <div class="engineering-embedded"><EngineeringRuntimePage embedded :initial-version="versionId"/></div>
 </template>
 <template v-else-if="tab==='settings'">
  <div class="engineering-embedded"><EngineeringSettingsPage embedded/></div>
 </template>
 <template v-else-if="tab==='communication'">
  <div class="engineering-embedded"><CommunicationPage embedded/></div>
 </template>
 <template v-else-if="tab==='charging-run'">
  <div class="engineering-placeholder"><h3>充电运行管理</h3><p>充电运行会发出实时设备请求并影响车辆，因此保留在独立的运行页面。</p><button class="accent-button" @click="openCharging">打开充电管理</button></div>
 </template>
 <template v-else-if="tab==='commissioning'">
  <div class="engineering-embedded"><CommissioningPanel embedded :snapshot="props.snapshot??store.snapshot" :adapter="props.adapter??store.adapterIdentity" @navigate="commissioningNavigate"/></div>
 </template>
 <template v-else-if="tab==='tests'">
  <div class="engineering-embedded"><TestCenterPage embedded/></div>
 </template>
 <template v-else-if="tab==='simulation'">
  <h3>配置与调度预约模拟</h3><p>工位、车辆池、倒料设备、待机位和交通资源均读取所选版本。配置多少就显示多少；模拟不会访问PLC、机器人或现场后台。</p>
  <div class="engineering-form">
   <label>工程版本<select v-model="versionId" :disabled="busy"><option value="">请选择已发布版本</option><option v-for="v in versions" :key="v.id" :value="v.id">{{v.name}} · {{v.id.slice(0,8)}} · {{v.note}}</option></select></label>
   <label>每工位呼叫轮数<input v-model.number="callRounds" type="number" min="1" max="100" :disabled="busy"/></label>
   <label>轮次呼叫间隔（秒）<input v-model.number="callInterval" type="number" min="0" :disabled="busy"/></label>
   <label>模拟离线车辆（可多选）<select v-model="offline" multiple :disabled="busy||versionLoading"><option v-for="v in frozenPlan?.vehicles.filter(v=>v.enabled)??[]" :key="String(v.id)" :value="v.id">{{v.name}} · {{v.id}}</option></select></label>
   <label>模拟封锁资源（可多选）<select v-model="blockedResources" multiple :disabled="busy||versionLoading"><option v-for="r in frozenPlan?.resources??[]" :key="String(r.id)" :value="r.id">{{r.name}} · {{r.id}} · 容量{{r.capacity}}</option></select></label>
  </div>
  <p v-if="versionLoading">正在读取版本配置…</p>
  <template v-if="frozenPlan">
   <p>所选版本 {{frozenPlan.stations.filter(s=>s.enabled).length}} 个启用工位／{{frozenPlan.vehicles.filter(v=>v.enabled).length}} 台启用车辆／{{frozenPlan.pools.length}} 个车辆池／{{frozenPlan.dumpers.length}} 个倒料设备／{{frozenPlan.standbys.length}} 个待机位；使用冻结版本。</p>
   <p>同轮呼叫同时进入模拟队列，按配置优先级、车辆池及资源可用情况派发；工位列表顺序不代表车辆固定执行顺序。</p>
   <OrderedStationPicker title="参与模拟工位" v-model="callStations" :choices="simulationStations" :disabled="busy||versionLoading"/>
   <details><summary>查看本次配置的车辆池与设备绑定</summary><p v-for="pool in frozenPlan.pools" :key="String(pool.id)">{{pool.name}}（{{pool.id}}）：{{(pool.vehicleIds as string[]).join('、')}} · 参与工位 {{frozenPlan.stations.filter(s=>s.poolId===pool.id&&callStations.includes(String(s.id))).length}} 个</p><p v-for="d in frozenPlan.dumpers" :key="String(d.id)">{{d.name}}（{{d.id}}）：等待 {{[d.waitingNode,...((d.additionalWaitingNodes as string[]|undefined)??[])].join('、')}} → 倒料 {{d.dumpingNode}}</p></details>
  </template>
  <button class="accent-button" :disabled="busy||versionLoading||!frozenPlan||!callStations.length" @click="simulate">运行隔离模拟</button>
  <template v-if="report"><div class="engineering-stats"><div><b>{{report.tasks.length}} / {{report.requestedCalls}}</b><span>建模完成 / 呼叫数</span></div><div><b>{{report.blocked.length}}</b><span>阻塞呼叫</span></div><div><b>{{report.peakVehicles}}</b><span>模型峰值并行车数</span></div><div><b>{{Math.round(report.durationSeconds)}}</b><span>模型完成时间（秒）</span></div></div><p>去重呼叫{{report.duplicateCalls}} · 报告{{report.id}}</p><p class="engineering-boundary">{{report.evidenceBoundary}}</p><button @click="download('simulation-'+report.id+'.json',JSON.stringify(report,null,2))">下载完整模拟报告</button>
  <h4>最终位置</h4><p v-for="(node,id) in report.finalPositions" :key="id">{{id}} → {{node}}</p><ul class="engineering-issues"><li v-for="(b,i) in report.blocked.slice(0,100)" :key="i">{{b.stationId}}：{{b.reason}} <RouterLink :to="b.link">检查配置</RouterLink></li></ul>
  <div class="engineering-table-scroll"><table class="engineering-table"><thead><tr><th>呼叫 / 工位</th><th>车辆</th><th>开始–结束（秒）</th><th>步骤</th><th>路线</th></tr></thead><tbody><tr v-for="t in reportTasks" :key="t.taskId"><td>{{t.callId}}<br/>{{t.stationId}}</td><td>{{t.vehicleId}}</td><td>{{Math.round(t.start)}}–{{Math.round(t.end)}}</td><td>{{t.steps.length}}步</td><td><details><summary>查看路线与阶段</summary><p>{{t.route.join(' → ')}}</p><ol><li v-for="s in t.steps" :key="s">{{taskStepName(s)}}</li></ol></details></td></tr></tbody></table></div><div class="engineering-toolbar"><button :disabled="reportPage<=1" @click="reportPage--">上一页</button><span>第{{reportPage}}页</span><button :disabled="reportPage*25>=report.tasks.length" @click="reportPage++">下一页</button></div>
  </template>
 </template>
 </main></div>
 <datalist v-for="(s,key) in sections" :id="'engineering-ref-'+key" :key="key"><option v-for="row in plan[key as Section]" :key="String(row.id)" :value="String(row.id)">{{row.name??row.vendorSite??row.id}}</option></datalist>
</section>
</template>

<style scoped>
.engineering-center{max-width:1800px;margin:auto}.management-title p{font-size:13px;font-weight:400;color:#93abc8;margin-top:9px}.engineering-center p{line-height:1.8;color:#b5c8df}.mode-badge{font-size:12px;padding:9px 12px;border:1px solid #396177;border-radius:5px;color:#85def0;white-space:nowrap}.engineering-toolbar{display:flex;gap:9px;align-items:center;flex-wrap:wrap;margin:14px 0}.engineering-toolbar input{max-width:260px}.engineering-toolbar span{font-size:12px;color:#a2b8d1}.engineering-layout{display:grid;grid-template-columns:170px minmax(0,1fr);gap:22px;margin-top:22px}.engineering-sidebar{display:flex;flex-direction:column;gap:4px;align-self:start;position:sticky;top:115px}.engineering-sidebar button{justify-content:space-between;text-align:left;width:100%;border-color:transparent;background:transparent}.engineering-sidebar button.active{background:#183346;border:1px solid #348aa5;color:#9de9f4}.engineering-sidebar small{color:#9ab3cb}.engineering-content{min-width:0;padding:22px;background:#101b30;border:1px solid #2b3b57;border-radius:7px}.engineering-content h3{margin:0 0 12px;font-size:18px}.engineering-content h4{margin:22px 0 10px}.engineering-form{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:16px;margin:20px 0;align-items:end}.engineering-form label,.engineering-detail label,.engineering-note{display:grid;gap:8px;font-size:13px;color:#b6cce3}.engineering-form input,.engineering-form select{width:100%;min-width:0}.engineering-stats{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px;margin:24px 0}.engineering-stats div{padding:17px;background:#15243b;border:1px solid #2c4260;border-radius:5px;display:grid;gap:8px}.engineering-stats b{font-size:24px;color:#9fe3ec}.engineering-stats span{font-size:12px;color:#a5bbd3}.engineering-boundary{font-size:12px;padding:12px 16px;background:#17243a;border-left:3px solid #5ba5bc}.engineering-message{font-size:13px;color:#91e3c7!important}.engineering-links{display:flex;gap:12px 22px;flex-wrap:wrap;margin:20px 0}.engineering-links a,.engineering-issues a,.engineering-content a{color:#83dae9}.file-button{display:inline-flex;padding:10px 13px;border:1px solid #344664;border-radius:5px;cursor:pointer;font-size:13px;color:#bdd0e5}.file-button input{display:none}.engineering-table-layout{display:grid;grid-template-columns:minmax(0,1fr);gap:18px}.engineering-table-layout:has(.engineering-detail){grid-template-columns:minmax(0,1fr) 290px}.engineering-table-scroll{overflow:auto}.engineering-table{border-collapse:collapse;width:100%;font-size:12px}.engineering-table th,.engineering-table td{text-align:left;padding:12px 10px;border-bottom:1px solid #2b3c55;vertical-align:top;max-width:300px;overflow-wrap:anywhere}.engineering-table th{color:#9ab2cd;white-space:nowrap;background:#15243b}.engineering-table tr.selected{background:#1c344b}.engineering-table button{padding:5px 8px;white-space:nowrap}.engineering-detail{min-width:0;border:1px solid #33506d;padding:15px;align-self:start}.engineering-detail legend{font-size:13px;color:#a8e7ed;overflow-wrap:anywhere}.engineering-detail label{margin:11px 0}.engineering-detail input:not([type=checkbox]),.engineering-detail select{width:100%;min-width:0}.engineering-detail small{font-size:11px;line-height:1.6;color:#93adc5}.engineering-import{background:#203448;padding:16px;margin:15px 0;border-radius:4px}.engineering-import pre{white-space:pre-wrap;overflow-wrap:anywhere;font-size:12px}.engineering-issues,.engineering-versions{list-style:none;padding:0}.engineering-issues li,.engineering-versions li{padding:14px;margin:12px 0;background:#17283e;border-left:3px solid #b99851;overflow-wrap:anywhere}.engineering-issues span{font-size:13px;color:#e1c886}.engineering-issues p{font-size:13px}.engineering-versions small{color:#90a7c0;word-break:break-all}.engineering-note textarea{min-height:80px;max-width:800px}fieldset:disabled{opacity:.65}@media(min-width:1450px){.engineering-table-layout:has(.engineering-detail){grid-template-columns:minmax(0,1fr) 340px}}@media(max-width:950px){.engineering-layout{grid-template-columns:1fr}.engineering-sidebar{position:static;flex-direction:row;overflow:auto}.engineering-sidebar button{width:auto;white-space:nowrap}.engineering-sidebar small{margin-left:8px}.engineering-table-layout:has(.engineering-detail){grid-template-columns:1fr}.engineering-stats{grid-template-columns:repeat(2,1fr)}.management-title{flex-wrap:wrap}}
.engineering-center{width:100%;max-width:none;margin:0;padding:0 2px;align-self:stretch;scrollbar-color:#3c5578 #091025;scrollbar-width:thin}
.engineering-workbench-title{padding:4px 2px 0;border-bottom:1px solid #243653}
.engineering-workbench-title h2{font-size:21px}
.engineering-toolbar{padding:9px 11px;border:1px solid #293d5e;border-radius:7px;background:#0d1830}
.engineering-toolbar>span{margin-left:auto}
.engineering-versions li.current{border-left-color:#39b8cf;background:#143247}
.version-state{display:inline-block;margin-left:8px;padding:2px 7px;border:1px solid #2e91a8;border-radius:999px;color:#8ee7f2;background:#102b3b;font-size:11px;font-weight:600}
.engineering-layout{grid-template-columns:clamp(190px,14vw,230px) minmax(0,1fr);gap:14px;margin-top:14px;align-items:start}
.engineering-sidebar{top:6px;max-height:calc(100dvh - 238px);overflow-y:auto;padding:6px;gap:3px;border:1px solid #293d5e;border-radius:8px;background:#0b162b;scrollbar-width:thin;scrollbar-color:#385273 #091025}
.engineering-sidebar button{min-height:32px;padding:6px 9px;border-radius:5px;font-size:12px}
.engineering-sidebar button.active{background:#14384b;border-color:#2a8da8;color:#8de7f3;box-shadow:inset 3px 0 #35c3dc}
.engineering-nav-group{margin:12px 8px 4px;color:#5f7fa2;font-size:9px;font-weight:600;letter-spacing:1.4px;text-transform:uppercase}
.engineering-nav-group:first-child{margin-top:3px}
.engineering-content{min-height:calc(100dvh - 242px);padding:18px 20px 24px;background:#0d172b;border-color:#263b5b;border-radius:8px}
.engineering-content h3{font-size:17px;color:#cce3f3}
.engineering-links button{color:#8be1ed}
.engineering-empty{border-left-color:#52718e!important;color:#8ca5bf}
.engineering-versions li:has(input:checked){border-left-color:#43cbe0;background:#153044}
.engineering-placeholder{display:grid;gap:12px;align-content:start;max-width:720px;padding:20px;border:1px solid #2d4563;border-radius:8px;background:#111f35}
.engineering-embedded,.engineering-embedded>*{min-width:0}
.engineering-embedded{display:block;width:100%}
.engineering-table-layout:has(.engineering-detail){grid-template-columns:minmax(0,1fr) minmax(300px,390px)}
@media(max-width:1050px){.engineering-layout{grid-template-columns:1fr}.engineering-sidebar{position:static;max-height:none;flex-direction:row;flex-wrap:wrap}.engineering-nav-group{display:none}.engineering-sidebar button{width:auto}.engineering-table-layout:has(.engineering-detail){grid-template-columns:1fr}.engineering-toolbar>span{margin-left:0}.engineering-content{min-height:0}}
@media(max-width:650px){.engineering-center{padding:0}.engineering-content{padding:14px}.engineering-toolbar button,.engineering-toolbar .file-button{flex:1 1 140px}.engineering-sidebar{gap:5px}.engineering-sidebar button{flex:1 1 140px}}
</style>
