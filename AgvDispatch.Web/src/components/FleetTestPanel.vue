<script setup lang="ts">
import { computed,onMounted,onUnmounted,ref } from 'vue';
import { useDispatchStore } from '../stores/dispatch';
import type { TaskDetail } from '../types';
import { label } from '../state';
type Start={requestId:string;scenario:string;vehicleIds:string[];siteConfirmed:boolean;stationIds?:string[]};
type Run={runId:string;plan:Start;stage:string;message:string;stopRequested:boolean;updatedAt:string;calls:{stationId:string;released:boolean;taskId:string|null}[]};
type Status={enabled:boolean;allowedVehicles:string[];configuredStations:string[];current:Run|null;vehicles:{vehicleId:string;site:string|null;fresh:boolean;message:string}[];tasks:TaskDetail[];observedAt:string};
const store=useDispatchStore(),status=ref<Status|null>(null),error=ref(''),busy=ref(false),reading=ref(false),online=ref(false);
const scenario=ref('Staggered'),confirmed=ref(false),reason=ref('');
const selectedStations=ref<string[]>([]);
const names:Record<string,string>={Staggered:'① 错峰呼叫',Simultaneous:'② 同时呼叫',Handover:'③ 前后交接'};
const current=computed(()=>status.value?.current);
const active=computed(()=>current.value&&current.value.stage!=='Completed');
const fresh=computed(()=>online.value&&store.fresh&&!!status.value&&store.now-Date.parse(status.value.observedAt)<10000&&store.now-Date.parse(status.value.observedAt)>=-1000);
const checks=['两车实际参与，任务、工位和反馈对应正确','同一任务未被重复领取，同车未接冲突任务','后车未提前入倒料区，许可归属正确','前车实际出区且PLC闭合后交接，返箱清位未影响后车','候车不挡出口，相遇按路权等待或通行','逐车数据失效时未借用另一车状态推进'];
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
  status.value=next;online.value=true;
  if(!selectedStations.value.length)selectedStations.value=[...(pending.value?.stationIds??next.configuredStations??[])];
  if(next.current&&pending.value?.requestId===next.current.plan.requestId){sessionStorage.removeItem(startKey);pending.value=null;}
 }catch(e){online.value=false;error.value='双车测试查询失败：'+String(e);}finally{reading.value=false;}
}
async function act(action:string){
 if(busy.value||!fresh.value)return;
 busy.value=true;error.value='';
 try{
  let payload:unknown;
  if(action==='start'){
   pending.value??={requestId:crypto.randomUUID().replaceAll('-',''),scenario:scenario.value,vehicleIds:status.value?.allowedVehicles??[],siteConfirmed:confirmed.value,stationIds:[...selectedStations.value]};
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
function saveReview(){if(current.value)localStorage.setItem('fleet-review-'+current.value.runId,JSON.stringify(results.value));}
function exportEvidence(){
 const blob=new Blob([JSON.stringify({schema:'fleet-test-observation-v1',exportedAt:new Date().toISOString(),observationFresh:fresh.value,status:status.value,
  fieldReview:checks.map((item,i)=>({item,result:results.value[i]})),note:'现场核验为本机人工记录；模拟P反馈不证明机械动作完成。'},null,2)],{type:'application/json'});
 const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download='双车测试-'+(current.value?.runId??'未启动')+'.json';a.click();URL.revokeObjectURL(url);
}
onMounted(()=>{void read();timer=setInterval(()=>void read(),2000);});
onUnmounted(()=>{disposed=true;if(timer)clearInterval(timer);});
</script>
<template>
 <details class="fleet-panel" :open="status?.enabled||!!active">
  <summary>双车多工位 · 竞争与交接测试 <span>{{status?.enabled?'双车启动模式':'需使用双车测试启动脚本'}}</span></summary>
  <p>每次只测试一组、每个工位一次呼叫。由调度按车辆状态、路线及规则分配；不要求一车固定一个工位。</p>
  <p v-if="!fresh" class="inline-warning" role="alert">观察已断开或过期，以下保留值不能作为当前现场状态。</p>
  <div class="fleet-controls">
   <label>本组用例<select v-model="scenario" :disabled="busy||!!active||!!pending"><option v-for="(name,key) in names" :key="key" :value="key">{{name}}</option></select></label>
   <button :disabled="busy||reading" @click="read">查询原记录</button>
   <button :disabled="busy||!fresh||!status?.enabled||!!active||!confirmed||(!pending&&selectedStations.length<2)||!store.submissionEnabled" @click="act('start')">{{pending?'重试原申请':'启动本组测试'}}</button>
   <button :disabled="busy||!fresh||!active||current?.stopRequested" @click="act('stop')">停止新增呼叫并收尾</button>
   <button :disabled="!status" @click="exportEvidence">导出本组观察与核验记录</button>
  </div>
  <div class="fleet-controls"><label v-for="id in status?.configuredStations??[]" :key="id" class="confirm"><input v-model="selectedStations" type="checkbox" :value="id" :disabled="busy||!!active||!!pending"/>{{id}}</label></div>
  <p>{{scenario==='Staggered'?'按所选工位列表顺序，前一工位取箱确认完成后，再呼叫下一工位。':scenario==='Simultaneous'?'所选工位在模拟PLC内同批置呼叫，任务由两车调度分配。':'前一工位任务进入倒料执行步骤后，再呼叫下一工位；后车能否到等待位由路权决定。'}}</p>
  <label class="confirm"><input v-model="confirmed" type="checkbox" :disabled="busy"/>已核对两车停靠位置、无载初态、所选工位箱体、路线及倒料区域；两车初始停车点独立，现场允许执行</label>
  <div class="fleet-vehicles"><article v-for="v in status?.vehicles??[]" :key="v.vehicleId" :class="{stale:!v.fresh||!fresh}"><b>{{v.vehicleId}} · {{v.site||'站点未知'}}</b><p>{{v.message}}</p></article></div>
  <template v-if="current">
   <p role="status"><b>{{names[current.plan.scenario]}} · {{current.stage==='Hold'?'暂停待核对':current.stage==='Completed'?'本组收尾完成':'测试进行中'}}</b> · {{current.message}}</p>
   <small>原运行 {{current.runId}} · {{current.updatedAt}} · 停止新增：{{current.stopRequested?'是':'否'}}</small>
   <div class="fleet-vehicles"><article v-for="(c,index) in current.calls" :key="c.stationId"><b>{{c.stationId}} · {{c.released?'本组已释放呼叫':'尚未触发'}}</b><p>{{callState(index)}}</p><template v-for="t in status?.tasks.filter(t=>t.task.taskId===c.taskId)??[]" :key="t.task.taskId">
    <p>{{t.task.vehicleId||'等待调度分配'}} · {{label(t.task.stage)}}</p><p>{{t.waitingExplanation}}</p>
    <p>出区交接：{{t.handover?.state==='ExitHandoverComplete'?'已闭合':'尚未闭合'}} · {{t.handover?.reason}}</p>
    <p>持有资源：{{t.resources?.map(r=>r.resourceId+' / '+r.vehicleId).join('、')||'未返回持有记录'}}</p>
    <RouterLink :to="'/tasks/'+encodeURIComponent(t.task.taskId)">查看原任务、PLC条件与交接证据</RouterLink>
   </template></article></div>
   <p>交通等待队列中的“阻塞资源 / 车辆”用于核对谁在等谁；心跳过期不能作为物理出区证据。本组结束后不自动把两车驶向同一个待机点。</p>
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
.fleet-panel{border:1px solid #36516c;border-radius:8px;padding:16px;margin:14px 0;color:var(--text-secondary);line-height:1.7}.fleet-panel summary{cursor:pointer;font-weight:600;color:var(--text-primary)}.fleet-panel summary span{font-weight:400;margin-left:12px}.fleet-controls{display:flex;gap:10px;flex-wrap:wrap;align-items:end}.fleet-panel label{display:grid;gap:6px}.fleet-panel select,.fleet-panel input{padding:8px}.fleet-panel .confirm{display:flex;align-items:start;margin:14px 0}.fleet-vehicles{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px;margin:14px 0}.fleet-vehicles article{padding:12px;background:var(--bg-panel,#111d32);border:1px solid #36516c;overflow-wrap:anywhere}.fleet-vehicles article.stale{border-color:#d4a253}.review-item{grid-template-columns:1fr 110px;margin:10px 0}@media(max-width:700px){.fleet-vehicles{grid-template-columns:1fr}}
</style>
