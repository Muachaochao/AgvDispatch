<script setup lang="ts">
import { computed,onMounted,onUnmounted,ref } from 'vue';
import { useDispatchStore } from '../stores/dispatch';
import type { TaskDetail } from '../types';
import { label } from '../state';
import { fleetSelectionStatus, moveFleetStation, reconcileFleetSelection, retireUnacceptedFleetStart, selectVisibleFleetOptions } from '../fleet-test-options';
type Start={requestId:string;scenario:string;vehicleIds:string[];siteConfirmed:boolean;stationIds?:string[]};
type Run={runId:string;plan:Start;stage:string;message:string;stopRequested:boolean;updatedAt:string;holds?:{time:string;reason:string}[];calls:{stationId:string;released:boolean;taskId:string|null}[]};
type Status={enabled:boolean;allowedVehicles:string[];configuredStations:string[];maxConcurrentTasks:number;current:Run|null;vehicles:{vehicleId:string;site:string|null;fresh:boolean;message:string}[];tasks:TaskDetail[];observedAt:string};
const store=useDispatchStore(),status=ref<Status|null>(null),error=ref(''),busy=ref(false),reading=ref(false),online=ref(false);
const scenario=ref('Staggered'),confirmed=ref(false),reason=ref('');
const selectedStations=ref<string[]>([]);
const selectedVehicles=ref<string[]>([]),vehicleSearch=ref(''),stationSearch=ref(''),selectionNotice=ref('');
let selectionsInitialized=false;
const names:Record<string,string>={Staggered:'① 错峰呼叫',Simultaneous:'② 同时呼叫',Handover:'③ 前后交接'};
const current=computed(()=>status.value?.current);
const active=computed(()=>current.value&&current.value.stage!=='Completed');
const frozen=computed(()=>busy.value||!!active.value||!!pending.value);
const selection=computed(()=>fleetSelectionStatus(status.value,selectedVehicles.value,selectedStations.value));
const visibleVehicles=computed(()=>(status.value?.allowedVehicles??[]).filter(id=>id.toLowerCase().includes(vehicleSearch.value.trim().toLowerCase())));
const visibleStations=computed(()=>(status.value?.configuredStations??[]).filter(id=>id.toLowerCase().includes(stationSearch.value.trim().toLowerCase())));
const fresh=computed(()=>online.value&&store.fresh&&!!status.value&&store.now-Date.parse(status.value.observedAt)<10000&&store.now-Date.parse(status.value.observedAt)>=-1000);
const checks=['所选车辆实际参与，任务、工位和反馈对应正确','同一任务未被重复领取，同车未接冲突任务','后车未提前入倒料区，许可归属正确','前车实际出区且PLC闭合后交接，返箱清位未影响后车','候车不挡出口，相遇按路权等待或通行','逐车数据失效时未借用另一车状态推进'];
const results=ref<string[]>(checks.map(()=> '待核验'));
function callState(index:number){
 const run=current.value;if(!run)return'';
 const call=run.calls[index];if(!call)return'';
 const task=status.value?.tasks.find(t=>t.task.taskId===call.taskId)?.task;
 if(task?.stage==='Cancelled')return'任务已取消，本组不会自动重发';
 if(task?.stage==='Completed')return'任务已完成';
 if(task)return(task.vehicleId?`${task.vehicleId} 已接单 · ${label(task.stage)}`:`已建单，等待调度分配车辆 · ${label(task.stage)}`);
 if(call.released)return'呼叫已释放，等待模拟PLC反馈或任务创建';
 if(run.stopRequested)return'已停止新增呼叫，本工位不再触发';
 if(run.plan.scenario==='Simultaneous')return'等待同批呼叫';
 if(index>0&&run.calls[index-1].released)return run.plan.scenario==='Staggered'
   ?`等待 ${run.calls[index-1].stationId} 完成取箱`
   :`等待 ${run.calls[index-1].stationId} 进入倒料步骤`;
 return'等待前一工位条件成立';
}
let timer:ReturnType<typeof setInterval>|undefined,disposed=false;
const startKey='fleet-test-start';
const pending=ref<Start|null>(null);
try{pending.value=JSON.parse(sessionStorage.getItem(startKey)??'null');if(pending.value)scenario.value=pending.value.scenario;}catch{error.value='原申请记录无法读取，请先查询后台原会话。';}
async function read(){
 if(reading.value||busy.value||disposed)return;reading.value=true;
 try{
  const next=await store.api<Status>('/api/fleet-test');if(disposed)return;
  if(current.value?.runId!==next.current?.runId){
   try{results.value=JSON.parse(localStorage.getItem('fleet-review-'+next.current?.runId)??'null')??checks.map(()=> '待核验');}catch{results.value=checks.map(()=> '待核验');}
  }
  if(current.value?.updatedAt!==next.current?.updatedAt)confirmed.value=false;
  const original=pending.value??(next.current?.stage!=='Completed'?next.current?.plan:null);
  if(original){selectedVehicles.value=[...original.vehicleIds];selectedStations.value=[...(original.stationIds??[])];scenario.value=original.scenario;}
  const vehicleChoice=reconcileFleetSelection(selectedVehicles.value,next.allowedVehicles,selectionsInitialized||!!original,!!original);
  const stationChoice=reconcileFleetSelection(selectedStations.value,next.configuredStations,selectionsInitialized||!!original,!!original);
  selectedVehicles.value=vehicleChoice.selected;selectedStations.value=stationChoice.selected;selectionsInitialized=true;
  if(vehicleChoice.missing.length||stationChoice.missing.length){
   selectionNotice.value=`配置已变化：${[...vehicleChoice.missing,...stationChoice.missing].join('、')} 已不在当前可选范围。${original?'原申请参数保持不变，请核对原记录。':'已移除失效选择，请重新核对本组范围。'}`;
   confirmed.value=false;
  }
  status.value=next;online.value=true;
  if(next.current&&pending.value?.requestId===next.current.plan.requestId){sessionStorage.removeItem(startKey);pending.value=null;}
 }catch(e){online.value=false;error.value='多车测试查询失败：'+String(e);}finally{reading.value=false;}
}
async function act(action:string){
 if(busy.value||!fresh.value)return;
 busy.value=true;error.value='';
 try{
  let payload:unknown;
  if(action==='start'){
   if(!pending.value&&!selection.value.valid)throw new Error(selection.value.issues.join('；'));
   pending.value??={requestId:crypto.randomUUID().replaceAll('-',''),scenario:scenario.value,vehicleIds:[...selectedVehicles.value],siteConfirmed:confirmed.value,stationIds:[...selectedStations.value]};
   payload=pending.value;
   sessionStorage.setItem(startKey,JSON.stringify(payload));
  }else payload={runId:current.value?.runId,reason:reason.value,siteConfirmed:confirmed.value};
  const previousRun=current.value?.runId;
  status.value=await store.api<Status>('/api/fleet-test/'+action,payload);
  if(current.value?.runId!==previousRun)results.value=checks.map(()=> '待核验');
  if(action==='start'){sessionStorage.removeItem(startKey);pending.value=null;}
  confirmed.value=false;
 }catch(e){error.value=String(e)+'；先查询原记录，不重复建单。';}finally{busy.value=false;}
}
async function retirePending(){
 const original=pending.value;
 if(busy.value||!fresh.value||!original||active.value||!confirmed.value||reason.value.trim().length<10)return;
 busy.value=true;error.value='';
 try{
  await retireUnacceptedFleetStart(original,requestId=>store.api('/api/fleet-test/retire-start',{requestId,reason:reason.value,siteConfirmed:confirmed.value}));
  sessionStorage.removeItem(startKey);pending.value=null;
  selectionNotice.value=`原申请 ${original.requestId} 已由后台核对并持久化注销，可重新选择本组车辆和工位。`;
 }catch(e){error.value=String(e)+'；原申请和参数继续保留，请查询原记录或重试本次注销。';}
 finally{busy.value=false;confirmed.value=false;}
 await read();
}
function chooseVisible(kind:'vehicles'|'stations',checked:boolean){
 if(frozen.value)return;
 if(kind==='vehicles')selectedVehicles.value=selectVisibleFleetOptions(selectedVehicles.value,visibleVehicles.value,checked);
 else selectedStations.value=selectVisibleFleetOptions(selectedStations.value,visibleStations.value,checked);
 confirmed.value=false;selectionNotice.value='';
}
function moveStation(index:number,direction:number){
 if(frozen.value)return;
 selectedStations.value=moveFleetStation(selectedStations.value,index,direction);confirmed.value=false;
}
function saveReview(){if(current.value)localStorage.setItem('fleet-review-'+current.value.runId,JSON.stringify(results.value));}
function exportEvidence(){
 const blob=new Blob([JSON.stringify({schema:'fleet-test-observation-v1',exportedAt:new Date().toISOString(),observationFresh:fresh.value,status:status.value,
  fieldReview:checks.map((item,i)=>({item,result:results.value[i]})),note:'现场核验为本机人工记录；模拟P反馈不证明机械动作完成。'},null,2)],{type:'application/json'});
 const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download='多车测试-'+(current.value?.runId??'未启动')+'.json';a.click();URL.revokeObjectURL(url);
}
onMounted(()=>{void read();timer=setInterval(()=>void read(),2000);});
onUnmounted(()=>{disposed=true;if(timer)clearInterval(timer);});
</script>
<template>
 <details class="fleet-panel" :open="status?.enabled||!!active">
  <summary>多车多工位 · 竞争与交接测试 <span>{{status?.enabled?'多车联调已启用':'需启用多车联调配置'}}</span></summary>
  <p>每次只测试一组、每个工位一次呼叫。由调度按车辆状态、路线及规则分配；不要求一车固定一个工位。</p>
  <p>本组选择 {{selectedVehicles.length}} / {{status?.allowedVehicles.length??0}} 辆车、{{selectedStations.length}} / {{status?.configuredStations.length??0}} 个工位；后台并发任务上限 {{status?.maxConcurrentTasks??'未报告'}}。车辆多于名额时排队，不会提高配置上限。</p>
  <p v-if="!fresh" class="inline-warning" role="alert">观察已断开或过期，以下保留值不能作为当前现场状态。</p>
  <p v-if="selectionNotice" class="inline-warning" role="alert">{{selectionNotice}}</p>
  <div class="fleet-controls">
   <label>本组用例<select v-model="scenario" :disabled="busy||!!active||!!pending" @change="confirmed=false"><option v-for="(name,key) in names" :key="key" :value="key">{{name}}</option></select></label>
   <button :disabled="busy||reading" @click="read">查询原记录</button>
   <button :disabled="busy||!fresh||!status?.enabled||!!active||!confirmed||(!pending&&!selection.valid)||!store.submissionEnabled" @click="act('start')">{{pending?'重试原申请':'启动本组测试'}}</button>
   <button :disabled="busy||!fresh||!active||current?.stopRequested" @click="act('stop')">停止新增呼叫并收尾</button>
   <button :disabled="!status" @click="exportEvidence">导出本组观察与核验记录</button>
  </div>
  <div class="fleet-selection-grid">
   <fieldset>
    <legend>参与车辆 · {{selectedVehicles.length}} 辆</legend>
    <div class="fleet-controls"><label>筛选车辆<input v-model="vehicleSearch" type="search" placeholder="车辆编号"/></label><button :disabled="frozen||!visibleVehicles.length" @click="chooseVisible('vehicles',true)">选中筛选结果</button><button :disabled="frozen||!visibleVehicles.length" @click="chooseVisible('vehicles',false)">取消筛选结果</button></div>
    <div class="fleet-option-list"><label v-for="id in visibleVehicles" :key="id" class="confirm"><input v-model="selectedVehicles" type="checkbox" :value="id" :disabled="frozen" @change="confirmed=false;selectionNotice=''"/>{{id}}</label></div>
    <p v-if="!status?.allowedVehicles.length">后台未配置参与车辆，请先在工程配置中心设置车辆池和联调范围。</p><p v-else-if="!visibleVehicles.length">没有匹配的车辆。</p>
   </fieldset>
   <fieldset>
    <legend>参与工位 · {{selectedStations.length}} 个</legend>
    <div class="fleet-controls"><label>筛选工位<input v-model="stationSearch" type="search" placeholder="工位编号"/></label><button :disabled="frozen||!visibleStations.length" @click="chooseVisible('stations',true)">选中筛选结果</button><button :disabled="frozen||!visibleStations.length" @click="chooseVisible('stations',false)">取消筛选结果</button></div>
    <div class="fleet-option-list"><label v-for="id in visibleStations" :key="id" class="confirm"><input v-model="selectedStations" type="checkbox" :value="id" :disabled="frozen" @change="confirmed=false;selectionNotice=''"/>{{id}}</label></div>
    <p v-if="!status?.configuredStations.length">后台未配置业务工位，请先在工程配置中心绑定工位、站点和 PLC 点表。</p><p v-else-if="!visibleStations.length">没有匹配的工位。</p>
   </fieldset>
  </div>
  <details v-if="selectedStations.length"><summary>呼叫顺序（{{selectedStations.length}} 个工位，可调整）</summary><ol class="fleet-sequence"><li v-for="(id,index) in selectedStations" :key="id"><span>{{id}}</span><button :disabled="frozen||index===0" :aria-label="id+' 前移'" @click="moveStation(index,-1)">前移</button><button :disabled="frozen||index===selectedStations.length-1" :aria-label="id+' 后移'" @click="moveStation(index,1)">后移</button></li></ol></details>
  <p v-if="!active&&!pending&&!selection.valid" class="muted-note">{{selection.issues.join('；')}}。</p>
  <p v-if="pending" class="inline-warning">原申请 {{pending.requestId}} 尚待核对，车辆、工位与顺序已锁定；重试使用同一申请编号和原参数。</p>
  <details v-if="pending&&!active" class="pending-retirement">
   <summary>原申请未受理，需要重新选择？</summary>
   <p>先查询原记录。只有后台确认该申请未创建任何运行，并持久化注销后，才会解除本页参数锁定；已受理的申请需查看原运行。本操作不会取消任务、释放占用或发送车辆动作。</p>
   <label>注销核对依据<input v-model="reason" maxlength="1000" :disabled="busy" placeholder="至少10字，说明原申请核对情况及重新选择原因"/></label>
   <p>勾选下方现场核对后，再注销原申请；通信失败或未取得注销回执时仍保留原参数。</p>
   <button :disabled="busy||!fresh||!confirmed||reason.trim().length<10" @click="retirePending">核对并注销未受理申请</button>
  </details>
  <p>{{scenario==='Staggered'?'按所选工位列表顺序，前一工位取箱确认完成后，再呼叫下一工位。':scenario==='Simultaneous'?'所选工位在模拟PLC内同批置呼叫，任务由所选车辆按配置规则分配。':'前一工位任务进入倒料执行步骤后，再呼叫下一工位；后车能否到等待位由路权决定。'}}</p>
  <p class="muted-note">空箱场地测试不要求每个工位有实物箱：开始呼叫时由模拟PLC注入本轮“箱体在位”假信号，取箱后模拟无箱，返箱后模拟有箱；这些反馈不证明真实机械动作。</p>
  <label class="confirm"><input v-model="confirmed" type="checkbox" :disabled="busy"/>已核对所选车辆停靠位置、空载初态、空箱模拟规则、路线及倒料区域；各车初始停车点独立，现场允许执行</label>
  <div class="fleet-vehicles"><article v-for="v in status?.vehicles??[]" :key="v.vehicleId" :class="{stale:!v.fresh||!fresh}"><b>{{v.vehicleId}} · {{v.site||'站点未知'}} · {{selectedVehicles.includes(v.vehicleId)?'本组参与':'未选入本组'}}</b><p>{{v.message}}</p></article></div>
  <template v-if="current">
   <p role="status"><b>{{names[current.plan.scenario]}} · {{current.stage==='Hold'?'暂停待核对':current.stage==='Completed'?'本组收尾完成':'测试进行中'}}</b> · {{current.message}}</p>
   <p v-if="current.stage==='Hold'" class="inline-warning">上述为暂停触发原因，车辆状态恢复后仍保留；请结合下方当前车辆状态核对，再续接原记录。</p>
   <details v-if="current.holds?.length"><summary>暂停历史（{{current.holds.length}} 次）</summary><p v-for="(h,index) in current.holds" :key="index">{{new Date(h.time).toLocaleString()}} · {{h.reason}}</p></details>
   <small>原运行 {{current.runId}} · {{current.updatedAt}} · 停止新增：{{current.stopRequested?'是':'否'}}</small>
   <div class="fleet-vehicles"><article v-for="(c,index) in current.calls" :key="c.stationId"><b>{{c.stationId}} · {{c.released?'本组已释放呼叫':'尚未触发'}}</b><p>{{callState(index)}}</p><template v-for="t in status?.tasks.filter(t=>t.task.taskId===c.taskId)??[]" :key="t.task.taskId">
    <p>{{t.task.vehicleId||'等待调度分配'}} · {{label(t.task.stage)}}</p><p>{{t.waitingExplanation}}</p>
    <p>出区交接：{{t.handover?.state==='ExitHandoverComplete'?'已闭合':'尚未闭合'}} · {{t.handover?.reason}}</p>
    <p>持有资源：{{t.resources?.map(r=>r.resourceId+' / '+r.vehicleId).join('、')||'未返回持有记录'}}</p>
    <RouterLink :to="'/tasks/'+encodeURIComponent(t.task.taskId)">查看原任务、PLC条件与交接证据</RouterLink>
   </template></article></div>
   <p>交通等待队列中的“阻塞资源 / 车辆”用于核对谁在等谁；心跳过期不能作为物理出区证据。本组结束不自动发送回待机动作，各车需使用独立配置的可用待机位。</p>
   <template v-if="current.stage==='Hold'">
    <label>现场核对依据<input v-model="reason" maxlength="1000" placeholder="至少10字，核对原任务与现场状态"/></label>
    <div class="fleet-controls"><button :disabled="busy||!fresh||!confirmed||reason.trim().length<10||!store.submissionEnabled" @click="act('resume')">核对后续接原用例</button><button :disabled="busy||!fresh||!confirmed||!current.stopRequested||reason.trim().length<10" @click="act('close')">核对闭合并结束记录</button></div>
   </template>
   <details><summary>现场验收项目（人工核验，独立于任务完成）</summary><label v-for="(item,i) in checks" :key="item" class="review-item">{{item}}<select v-model="results[i]" @change="saveReview"><option>待核验</option><option>通过</option><option>失败</option><option>本组未覆盖</option></select></label></details>
  </template>
  <p v-if="error" class="inline-warning" role="alert">{{error}}</p>
 </details>
</template>
<style scoped>
.fleet-panel{border:1px solid #36516c;border-radius:8px;padding:16px;margin:14px 0;color:var(--text-secondary);line-height:1.7}.fleet-panel summary{cursor:pointer;font-weight:600;color:var(--text-primary)}.fleet-panel summary span{font-weight:400;margin-left:12px}.fleet-controls{display:flex;gap:10px;flex-wrap:wrap;align-items:end}.fleet-panel label{display:grid;gap:6px}.fleet-panel select,.fleet-panel input{padding:8px}.fleet-panel .confirm{display:flex;align-items:start;margin:14px 0}.fleet-selection-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px;margin:16px 0}.fleet-selection-grid fieldset{min-width:0;border:1px solid #36516c;border-radius:6px;padding:12px}.fleet-option-list{display:flex;flex-wrap:wrap;align-content:start;gap:0 18px;max-height:220px;overflow:auto;margin-top:10px}.fleet-option-list .confirm{margin:6px 0;overflow-wrap:anywhere}.fleet-sequence{max-height:220px;overflow:auto;padding-left:30px}.fleet-sequence li{margin:6px 0;padding-left:4px}.fleet-sequence li span{display:inline-block;min-width:130px;margin-right:8px;overflow-wrap:anywhere}.fleet-sequence button{padding:3px 8px;margin-right:6px}.fleet-vehicles{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,270px),1fr));gap:12px;margin:14px 0}.fleet-vehicles article{padding:12px;background:var(--bg-panel,#111d32);border:1px solid #36516c;overflow-wrap:anywhere}.fleet-vehicles article.stale{border-color:#d4a253}.review-item{grid-template-columns:1fr 110px;margin:10px 0}@media(max-width:850px){.fleet-selection-grid{grid-template-columns:1fr}}@media(max-width:700px){.fleet-vehicles{grid-template-columns:1fr}}
</style>
