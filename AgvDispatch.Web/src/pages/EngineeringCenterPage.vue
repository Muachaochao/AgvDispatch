<script setup lang="ts">
import { computed, nextTick, onMounted, onBeforeUnmount, ref, watch } from 'vue';
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { sections, newPlan, newRow, csvRows, exportCsv, importDiff, type Section, type EngineeringPlan, type EngineeringRow, type Field } from '../engineering-center';
const store=useDispatchStore(),route=useRoute(),router=useRouter();
const plan=ref<EngineeringPlan>(newPlan()),revision=ref(''),savedText=ref(''),ready=ref(false),busy=ref(false),message=ref(''),error=ref('');
const search=ref(''),page=ref(1),selected=ref<EngineeringRow|null>(null),checked=ref<string[]>([]);
const validation=ref<any>(null),versions=ref<any[]>([]),versionId=ref(''),publishNote=ref(''),report=ref<any>(null);
const importPreview=ref<ReturnType<typeof importDiff>|null>(null),importSection=ref<Section>('stations');
const sampleStations=ref(300),sampleVehicles=ref(5),callRounds=ref(1),offline=ref(''),blockedResources=ref('');
const batchField=ref('poolId'),batchValue=ref('');const reportPage=ref(1);
const tabs=[{id:'overview',label:'工程总览'},...Object.entries(sections).map(([id,s])=>({id,label:s.label})),{id:'validation',label:'校验与诊断'},{id:'versions',label:'发布与版本'},{id:'simulation',label:'模拟测试'}];
const tab=computed(()=>tabs.some(t=>t.id===route.query.tab)?String(route.query.tab):'overview');
const section=computed(()=>tab.value in sections?tab.value as Section:null);
const dirty=computed(()=>savedText.value!==JSON.stringify(plan.value));
const rows=computed(()=>section.value?plan.value[section.value]:[]);
const filtered=computed(()=>rows.value.filter(r=>Object.values(r).join(' ').toLowerCase().includes(search.value.toLowerCase())));
const shown=computed(()=>filtered.value.slice((page.value-1)*25,page.value*25));
const columns=computed(()=>section.value?sections[section.value].fields.filter(f=>f.type!=='list').slice(0,5):[]);
const selectedVersion=computed(()=>versions.value.find(v=>v.id===versionId.value));
const reportTasks=computed(()=>report.value?.tasks?.slice((reportPage.value-1)*25,reportPage.value*25)??[]);
function navigate(id:string){void router.push({path:'/engineering',query:{tab:id}});}
watch(()=>route.query.tab,()=>{search.value='';page.value=1;selected.value=null;checked.value=[];importPreview.value=null;});
watch(search,()=>{page.value=1;checked.value=[];});
watch(()=>[route.query.tab,route.query.object],()=>{void nextTick(focusObject);});
function discardAllowed(){return !ready.value||!dirty.value||window.confirm('当前工程有未保存修改。确定放弃这些修改？');}
onBeforeRouteLeave(()=>discardAllowed());
function beforeUnload(e:BeforeUnloadEvent){if(ready.value&&dirty.value){e.preventDefault();e.returnValue='';}}
window.addEventListener('beforeunload',beforeUnload);
onBeforeUnmount(()=>window.removeEventListener('beforeunload',beforeUnload));
function focusObject(){if(section.value&&typeof route.query.object==='string'){search.value=route.query.object;selected.value=rows.value.find(r=>r.id===route.query.object)??null;}}
async function action(fn:()=>Promise<void>){if(busy.value)return;busy.value=true;error.value='';message.value='';try{await fn();}catch(e){error.value=e instanceof Error?e.message:String(e);}finally{busy.value=false;}}
async function load(){if(!discardAllowed())return;await action(async()=>{const d:any=await store.api('/api/engineering/draft');plan.value=d.plan;revision.value=d.revision;savedText.value=JSON.stringify(d.plan);ready.value=true;validation.value=null;selected.value=null;versions.value=await store.api('/api/engineering/versions');focusObject();message.value='已读取工程草稿。工程草稿可独立保存，不依赖车辆在线或现场恢复放行。';});}
async function save(){await action(async()=>{const d:any=await store.api('/api/engineering/draft',{expectedRevision:revision.value,plan:plan.value});revision.value=d.revision;savedText.value=JSON.stringify(d.plan);message.value='工程草稿已保存；未改变现场运行配置、未发送设备动作。';});}
async function validate(){await action(async()=>{validation.value=await store.api('/api/engineering/validate',plan.value);navigate('validation');message.value=validation.value.valid?'配置静态校验通过，可发布模拟工程版本。':'发现配置缺项，请点击问题跳转修改。';});}
async function sample(){if(!discardAllowed())return;await action(async()=>{plan.value=await store.api(`/api/engineering/sample?stations=${sampleStations.value}&vehicles=${sampleVehicles.value}`);validation.value=null;selected.value=null;message.value='示例已载入编辑区，尚未保存。所有地址与数值都是模拟数据。';});}
async function publish(){await action(async()=>{if(dirty.value)throw new Error('请先保存当前修改，再发布同一修订。');const v:any=await store.api('/api/engineering/publish',{expectedRevision:revision.value,note:publishNote.value});versions.value=await store.api('/api/engineering/versions');versionId.value=v.id;message.value='模拟工程版本已发布并冻结；不会激活现场地图。可以进入模拟测试。';});}
async function simulate(){await action(async()=>{
 if(!versionId.value)throw new Error('请先选择已发布版本。');
 const v:any=await store.api('/api/engineering/versions/'+versionId.value);const active=v.plan.stations.filter((s:any)=>s.enabled);
 if(callRounds.value<1||callRounds.value>10||!Number.isInteger(callRounds.value)||active.length*callRounds.value>3000)throw new Error('轮数需1–10，呼叫总数最多3000。');
 const calls=Array.from({length:callRounds.value},(_,r)=>active.map((s:any)=>({requestId:`CALL-${r+1}-${s.id}`,stationId:s.id,atSeconds:r*60}))).flat();
 report.value=await store.api('/api/engineering/simulations',{versionId:versionId.value,calls,offlineVehicles:split(offline.value),blockedResources:split(blockedResources.value)});reportPage.value=1;message.value='模拟已完成；结果已保存到后台工程报告。没有连接任何设备。';
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
async function importMap(){await action(async()=>{
 const current:any=await store.api('/api/snapshot');if(!current.connected||!current.map)throw new Error('未读取到当前运行地图。');const m=current.map;
 // Resolve exact lengths and limits from authoritative workspace, never infer them from a screenshot.
 const workspace:any=await store.api(`/api/maps/${encodeURIComponent(m.id)}/workspace`);
 const source=[workspace.draft,workspace.published].find(v=>v?.versionId===m.version);
 const d=source?.definition;
 if(!d?.edges||!d?.resources)throw new Error('后台未返回地图道路定义；请从地图编辑导出后核对导入。');
 plan.value.mapId=m.id;plan.value.mapVersion=m.version;
 plan.value.nodes=m.nodes.map((n:any)=>({id:n.id,vendorSite:n.agvSiteId??'',x:n.x,y:n.y,enabled:n.enabled}));
 plan.value.edges=d.edges.map((e:any)=>({id:e.id,from:e.from,to:e.to,distance:e.distance,speed:e.speedLimit,resourceIds:e.resourceIds??[],bidirectional:e.bidirectional,enabled:e.enabled}));
 plan.value.resources=d.resources.map((r:any)=>({id:r.id,name:r.id,kind:r.type==='NarrowAisle'||r.type==='SingleLane'?'NarrowAisle':r.type==='Intersection'?'Intersection':'Station',capacity:r.capacity}));
 message.value='已复制运行地图拓扑到工程编辑区；请重新核对工位和资源绑定。没有修改现场地图。';
});}
onMounted(load);
</script>

<template>
<section class="engineering-center management-page">
 <header class="management-title"><div><h2>工程配置中心</h2><p>统一维护工位、车辆池与资源 · 配置 → 校验 → 版本 → 模拟</p></div><span class="mode-badge">工程规划 / 无设备动作</span></header>
 <div class="engineering-toolbar"><button :disabled="busy" @click="load">重新读取</button><button class="accent-button" :disabled="busy||!ready||!dirty" @click="save">保存工程草稿</button><button :disabled="busy||!ready" @click="validate">统一校验</button><button :disabled="!ready" @click="download('engineering-plan.json',JSON.stringify(plan,null,2))">导出工程</button><label class="file-button">导入工程<input type="file" accept=".json" :disabled="busy" @change="importProject"/></label><span>{{dirty?'有未保存修改':'草稿已保存'}} · {{revision?revision.slice(0,8):'未建稿'}}</span></div>
 <p class="engineering-boundary">本中心发布的是配置规划与模拟版本，不会替换5199现场运行图或启用PLC／车辆。实际运行配置和恢复仍可通过下方Web入口维护，无需打开WPF。</p>
 <p v-if="error" class="inline-warning" role="alert">{{error}}</p><p v-if="message" class="engineering-message" role="status">{{message}}</p>
 <div class="engineering-layout">
 <nav class="engineering-sidebar" aria-label="工程配置分类"><button v-for="item in tabs" :key="item.id" :class="{active:tab===item.id}" :aria-current="tab===item.id?'page':undefined" @click="navigate(item.id)">{{item.label}}<small v-if="item.id in sections">{{plan[item.id as Section].length}}</small></button></nav>
 <main class="engineering-content" :aria-busy="busy">
 <template v-if="tab==='overview'">
  <h3>工程概况</h3><div class="engineering-form"><label>工程名称<input v-model="plan.name"/></label><label>地图编号<input v-model="plan.mapId"/></label><label>地图版本<input v-model="plan.mapVersion"/></label></div>
  <div class="engineering-stats"><div v-for="key in (['stations','vehicles','pools','dumpers'] as Section[])" :key="key"><b>{{plan[key].length}}</b><span>{{sections[key].label}}</span></div></div>
  <h3>载入隔离示例</h3><p>示例会替换当前编辑区。先导出或保存已有修改。示例使用虚构地址，不读取或连接现场设备。</p><div class="engineering-form"><label>工位数量<input v-model.number="sampleStations" type="number" min="1" max="1000"/></label><label>车辆数量<input v-model.number="sampleVehicles" type="number" min="1" max="20"/></label><button :disabled="busy" @click="sample">载入示例到编辑区</button></div>
  <h3>现场Web入口</h3><div class="engineering-links"><RouterLink to="/communication">PLC／AGV通信配置</RouterLink><RouterLink to="/engineering-settings">现行九步参数与地图绑定</RouterLink><RouterLink to="/planning">地图编辑与交通资源</RouterLink><RouterLink to="/maps">地图版本与安全切换</RouterLink><RouterLink to="/recovery">任务取消与启动恢复</RouterLink><RouterLink to="/commissioning">运行条件与通信诊断</RouterLink></div><button :disabled="busy" @click="importMap">复制当前运行地图到工程草稿</button>
 </template>
 <template v-else-if="section">
  <h3>{{sections[section].label}}</h3><p>{{sections[section].description}}</p>
  <div class="engineering-toolbar"><input v-model="search" aria-label="搜索配置对象" placeholder="搜索编号、区域、绑定…"/><button @click="add">新增</button><button @click="download(section+'.csv',exportCsv(section,rows),'text/csv;charset=utf-8')">导出CSV / 模板</button><label class="file-button">导入CSV<input type="file" accept=".csv" :disabled="busy" @change="importFile"/></label></div>
  <div v-if="section==='stations'" class="engineering-toolbar"><button @click="checked=filtered.map(r=>String(r.id))">选择筛选结果（{{filtered.length}}）</button><span>已选{{checked.length}}</span><select v-model="batchField" aria-label="批量修改字段"><option v-for="f in sections.stations.fields.filter(f=>['area','poolId','templateId','dumperId','priority'].includes(f.key))" :key="f.key" :value="f.key">{{f.label}}</option></select><input v-model="batchValue" aria-label="批量修改值" placeholder="填写目标值"/><button :disabled="!checked.length||!batchValue" @click="batch">应用到所选工位</button></div>
  <div v-if="importPreview" class="engineering-import"><h4>导入差异预览</h4><p>新增{{importPreview.added}} · 修改{{importPreview.changed}} · 未变{{importPreview.unchanged}}。仅按编号合并，不删除未列出的原对象。</p><details><summary>查看前50项差异</summary><div v-for="c in importPreview.changes.slice(0,50)" :key="c.id"><b>{{c.id}} · {{c.status}}</b><pre v-if="c.status==='修改'">原：{{c.before}}
新：{{c.after}}</pre></div></details><button @click="applyImport">确认合并到编辑区</button><button @click="importPreview=null">取消导入</button></div>
  <div class="engineering-table-layout"><div><div class="engineering-table-scroll"><table class="engineering-table"><thead><tr><th v-if="section==='stations'">选择</th><th v-for="f in columns" :key="f.key">{{f.label}}</th><th>操作</th></tr></thead><tbody><tr v-for="(row,i) in shown" :key="(page-1)*25+i" :class="{selected:selected===row}"><td v-if="section==='stations'"><input type="checkbox" :aria-label="'选择'+row.id" :checked="checked.includes(String(row.id))" @change="check(String(row.id),$event)"/></td><td v-for="f in columns" :key="f.key">{{value(row,f)}}</td><td><button @click="selected=row">编辑</button></td></tr><tr v-if="!shown.length"><td :colspan="columns.length+2">没有匹配对象。可新增或导入CSV。</td></tr></tbody></table></div><div class="engineering-toolbar"><button :disabled="page<=1" @click="page--">上一页</button><span>第{{page}} / {{Math.max(1,Math.ceil(filtered.length/25))}}页 · {{filtered.length}}项</span><button :disabled="page*25>=filtered.length" @click="page++">下一页</button></div></div>
  <fieldset v-if="selected" class="engineering-detail"><legend>编辑 {{selected.id}}</legend><label v-for="f in sections[section].fields" :key="f.key">{{f.label}}
  <input v-if="f.type==='boolean'" type="checkbox" :checked="!!selected[f.key]" @change="update(selected,f,$event)"/>
  <select v-else-if="f.options" :value="selected[f.key]" @change="update(selected,f,$event)"><option v-for="o in f.options" :key="o" :value="o">{{o}}</option></select>
  <input v-else :value="value(selected,f)" :type="f.type==='number'?'number':'text'" :step="f.type==='number'?'any':undefined" :list="f.ref?'engineering-ref-'+f.ref:undefined" @input="update(selected,f,$event)"/>
  <small v-if="f.help">{{f.help}}</small></label><div class="engineering-toolbar"><button @click="copy">复制对象</button><button @click="remove">从草稿删除</button><button @click="selected=null">收起</button></div></fieldset></div>
 </template>
 <template v-else-if="tab==='validation'">
  <h3>统一校验与问题定位</h3><p>校验当前编辑区，包括PLC缺项、地址冲突、地图绑定、车辆能力、待机容量及路线可达性。</p><button :disabled="busy" @click="validate">重新校验</button><p v-if="!validation">尚未校验当前编辑区。</p><template v-else><p :class="validation.valid?'engineering-message':'inline-warning'">{{validation.valid?'静态配置通过':'存在阻塞项'}} · {{validation.issues.length}}项问题</p><ul class="engineering-issues"><li v-for="(i,n) in validation.issues" :key="n"><span>{{i.severity==='Error'?'阻塞':'提示'}} · {{i.objectId}}</span><p>{{i.message}}</p><RouterLink :to="i.link">进入{{sections[i.section as Section]?.label??'工程概况'}}修改</RouterLink></li></ul></template>
  <p>静态通过不证明地图坐标对齐、实车包络核定或PLC现场动作完成。现场任务阻塞请进入 <RouterLink to="/recovery">异常恢复</RouterLink> 或 <RouterLink to="/commissioning">联调检查</RouterLink>。</p>
 </template>
 <template v-else-if="tab==='versions'">
  <h3>发布模拟工程版本</h3><p>版本包含当前地图拓扑、工位、PLC点表、车辆、模板与资源，保存后冻结内容和摘要。现场运行版本保持独立。</p><label class="engineering-note">发布说明<textarea v-model="publishNote" maxlength="1024" placeholder="说明本次新增工位、绑定及资源调整"/></label><button :disabled="busy||dirty||!revision||!publishNote.trim()" @click="publish">校验并发布模拟版本</button><p v-if="dirty" class="inline-warning">当前编辑尚未保存，请先点击“保存工程草稿”。</p>
  <ul class="engineering-versions"><li v-for="v in versions" :key="v.id"><label><input v-model="versionId" type="radio" :value="v.id"/>{{v.name}} · {{v.stations}}工位／{{v.vehicles}}车</label><p>{{v.note}} · {{new Date(v.publishedAt).toLocaleString('zh-CN',{timeZone:'Asia/Shanghai'})}}</p><small>{{v.id}} · SHA256 {{v.contentHash}}</small></li></ul><div class="engineering-toolbar"><button :disabled="!versionId||busy" @click="versionToDraft">所选版本载入编辑区</button><button :disabled="!versionId" @click="navigate('simulation')">测试所选版本</button></div>
 </template>
 <template v-else-if="tab==='simulation'">
  <h3>配置与调度预约模拟</h3><p>根据已发布版本生成呼叫，模拟车辆池选车、九步阶段、资源容量预约与返回待机。测试不会访问PLC、机器人或现场后台。</p><div class="engineering-form"><label>工程版本<select v-model="versionId"><option value="">请选择已发布版本</option><option v-for="v in versions" :key="v.id" :value="v.id">{{v.name}} · {{v.id.slice(0,8)}} · {{v.note}}</option></select></label><label>每工位呼叫轮数<input v-model.number="callRounds" type="number" min="1" max="10"/></label><label>模拟离线车辆<input v-model="offline" placeholder="AGV-01;AGV-02"/></label><label>模拟封锁资源<input v-model="blockedResources" placeholder="DUMP-AREA"/></label></div><p v-if="selectedVersion">所选版本{{selectedVersion.stations}}工位／{{selectedVersion.vehicles}}车 · 使用冻结配置，不使用未保存编辑。</p><button class="accent-button" :disabled="busy||!versionId" @click="simulate">运行隔离模拟</button>
  <template v-if="report"><div class="engineering-stats"><div><b>{{report.tasks.length}} / {{report.requestedCalls}}</b><span>建模完成 / 呼叫数</span></div><div><b>{{report.blocked.length}}</b><span>阻塞呼叫</span></div><div><b>{{report.peakVehicles}}</b><span>模型峰值并行车数</span></div><div><b>{{Math.round(report.durationSeconds)}}</b><span>模型完成时间（秒）</span></div></div><p>去重呼叫{{report.duplicateCalls}} · 报告{{report.id}}</p><p class="engineering-boundary">{{report.evidenceBoundary}}</p><button @click="download('simulation-'+report.id+'.json',JSON.stringify(report,null,2))">下载完整模拟报告</button>
  <h4>最终位置</h4><p v-for="(node,id) in report.finalPositions" :key="id">{{id}} → {{node}}</p><ul class="engineering-issues"><li v-for="(b,i) in report.blocked.slice(0,100)" :key="i">{{b.stationId}}：{{b.reason}} <RouterLink :to="b.link">检查配置</RouterLink></li></ul>
  <div class="engineering-table-scroll"><table class="engineering-table"><thead><tr><th>呼叫 / 工位</th><th>车辆</th><th>开始–结束（秒）</th><th>步骤</th><th>路线</th></tr></thead><tbody><tr v-for="t in reportTasks" :key="t.taskId"><td>{{t.callId}}<br/>{{t.stationId}}</td><td>{{t.vehicleId}}</td><td>{{Math.round(t.start)}}–{{Math.round(t.end)}}</td><td>{{t.steps.length}}步</td><td><details><summary>查看路线与阶段</summary><p>{{t.route.join(' → ')}}</p><ol><li v-for="s in t.steps" :key="s">{{s}}</li></ol></details></td></tr></tbody></table></div><div class="engineering-toolbar"><button :disabled="reportPage<=1" @click="reportPage--">上一页</button><span>第{{reportPage}}页</span><button :disabled="reportPage*25>=report.tasks.length" @click="reportPage++">下一页</button></div>
  </template>
 </template>
 </main></div>
 <datalist v-for="(s,key) in sections" :id="'engineering-ref-'+key" :key="key"><option v-for="row in plan[key as Section]" :key="String(row.id)" :value="String(row.id)">{{row.name??row.vendorSite??row.id}}</option></datalist>
</section>
</template>

<style scoped>
.engineering-center{max-width:1800px;margin:auto}.management-title p{font-size:13px;font-weight:400;color:#93abc8;margin-top:9px}.engineering-center p{line-height:1.8;color:#b5c8df}.mode-badge{font-size:12px;padding:9px 12px;border:1px solid #396177;border-radius:5px;color:#85def0;white-space:nowrap}.engineering-toolbar{display:flex;gap:9px;align-items:center;flex-wrap:wrap;margin:14px 0}.engineering-toolbar input{max-width:260px}.engineering-toolbar span{font-size:12px;color:#a2b8d1}.engineering-layout{display:grid;grid-template-columns:170px minmax(0,1fr);gap:22px;margin-top:22px}.engineering-sidebar{display:flex;flex-direction:column;gap:4px;align-self:start;position:sticky;top:115px}.engineering-sidebar button{justify-content:space-between;text-align:left;width:100%;border-color:transparent;background:transparent}.engineering-sidebar button.active{background:#183346;border:1px solid #348aa5;color:#9de9f4}.engineering-sidebar small{color:#9ab3cb}.engineering-content{min-width:0;padding:22px;background:#101b30;border:1px solid #2b3b57;border-radius:7px}.engineering-content h3{margin:0 0 12px;font-size:18px}.engineering-content h4{margin:22px 0 10px}.engineering-form{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:16px;margin:20px 0;align-items:end}.engineering-form label,.engineering-detail label,.engineering-note{display:grid;gap:8px;font-size:13px;color:#b6cce3}.engineering-form input,.engineering-form select{width:100%;min-width:0}.engineering-stats{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px;margin:24px 0}.engineering-stats div{padding:17px;background:#15243b;border:1px solid #2c4260;border-radius:5px;display:grid;gap:8px}.engineering-stats b{font-size:24px;color:#9fe3ec}.engineering-stats span{font-size:12px;color:#a5bbd3}.engineering-boundary{font-size:12px;padding:12px 16px;background:#17243a;border-left:3px solid #5ba5bc}.engineering-message{font-size:13px;color:#91e3c7!important}.engineering-links{display:flex;gap:12px 22px;flex-wrap:wrap;margin:20px 0}.engineering-links a,.engineering-issues a,.engineering-content a{color:#83dae9}.file-button{display:inline-flex;padding:10px 13px;border:1px solid #344664;border-radius:5px;cursor:pointer;font-size:13px;color:#bdd0e5}.file-button input{display:none}.engineering-table-layout{display:grid;grid-template-columns:minmax(0,1fr);gap:18px}.engineering-table-layout:has(.engineering-detail){grid-template-columns:minmax(0,1fr) 290px}.engineering-table-scroll{overflow:auto}.engineering-table{border-collapse:collapse;width:100%;font-size:12px}.engineering-table th,.engineering-table td{text-align:left;padding:12px 10px;border-bottom:1px solid #2b3c55;vertical-align:top;max-width:300px;overflow-wrap:anywhere}.engineering-table th{color:#9ab2cd;white-space:nowrap;background:#15243b}.engineering-table tr.selected{background:#1c344b}.engineering-table button{padding:5px 8px;white-space:nowrap}.engineering-detail{min-width:0;border:1px solid #33506d;padding:15px;align-self:start}.engineering-detail legend{font-size:13px;color:#a8e7ed;overflow-wrap:anywhere}.engineering-detail label{margin:11px 0}.engineering-detail input:not([type=checkbox]),.engineering-detail select{width:100%;min-width:0}.engineering-detail small{font-size:11px;line-height:1.6;color:#93adc5}.engineering-import{background:#203448;padding:16px;margin:15px 0;border-radius:4px}.engineering-import pre{white-space:pre-wrap;overflow-wrap:anywhere;font-size:12px}.engineering-issues,.engineering-versions{list-style:none;padding:0}.engineering-issues li,.engineering-versions li{padding:14px;margin:12px 0;background:#17283e;border-left:3px solid #b99851;overflow-wrap:anywhere}.engineering-issues span{font-size:13px;color:#e1c886}.engineering-issues p{font-size:13px}.engineering-versions small{color:#90a7c0;word-break:break-all}.engineering-note textarea{min-height:80px;max-width:800px}fieldset:disabled{opacity:.65}@media(min-width:1450px){.engineering-table-layout:has(.engineering-detail){grid-template-columns:minmax(0,1fr) 340px}}@media(max-width:950px){.engineering-layout{grid-template-columns:1fr}.engineering-sidebar{position:static;flex-direction:row;overflow:auto}.engineering-sidebar button{width:auto;white-space:nowrap}.engineering-sidebar small{margin-left:8px}.engineering-table-layout:has(.engineering-detail){grid-template-columns:1fr}.engineering-stats{grid-template-columns:repeat(2,1fr)}.management-title{flex-wrap:wrap}}
</style>
