<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { useDispatchStore } from '../stores/dispatch';
import type { CreationOptions } from '../types';
const props=defineProps<{mode:'Demand'|'Cycle';options:CreationOptions|null}>();
const store=useDispatchStore();
type Plan={requestId:string;mode:string;vehicleId:string;stations:string[];rounds:number;standbySite:string;siteConfirmed:boolean};
type Run={runId:string;plan:Plan;stage:string;message:string;completedJobs:number;taskId:string|null;stopRequested:boolean;updatedAt:string};
const current=ref<Run|null>(null), error=ref(''), busy=ref(false), online=ref(false);
const vehicle=ref('AGV-01'), order=ref('CNC-001,CNC-002'), rounds=ref(1), standby=ref('LM7'), confirmed=ref(false);
const pending=ref<Plan|null>(null);
const key='agv-business-run-start';
try{pending.value=JSON.parse(sessionStorage.getItem(key)??'null');}catch{error.value='原申请记录无法读取，请先查询后台原运行。';}
const active=computed(()=>current.value&&current.value.stage!=='Completed');
const locked=computed(()=>busy.value||!!active.value||!!pending.value);
const configurationBlocks=computed(()=>{
  const selected=pending.value?.stations??order.value.split(',');
  return (props.options?.stations??[]).filter(s=>selected.includes(s.stationId)&&s.configurationValid===false);
});
const needsExitConfiguration=computed(()=>configurationBlocks.value.some(s=>s.explanation.includes('DumperExit')));
const selectedVehicleBlock=computed(()=>{
  const id=current.value?.plan.vehicleId??pending.value?.vehicleId??vehicle.value;
  const selected=props.options?.vehicles.find(v=>v.vehicleId===id);
  return selected&&!selected.available?`${id}：${selected.explanation}`:'';
});
const labels:Record<string,string>={Preparing:'准备中',Waiting:'等待呼叫 / 待机',Creating:'受理呼叫中',Executing:'业务执行中',ReturnSending:'准备返回待机',Returning:'返回待机中',Hold:'暂停待核对',Completed:'收尾完成，已回待机'};
let timer:ReturnType<typeof setInterval>|undefined;
let disposed=false, reading=false;
async function read(){
  if(busy.value||reading||disposed)return;
  reading=true;
  try{
    const result=await store.api<{current:Run|null}>('/api/business-run');
    if(disposed)return;
    current.value=result.current;online.value=true;
    if(result.current?.plan.requestId===pending.value?.requestId){sessionStorage.removeItem(key);pending.value=null;}
  }catch(e){if(!disposed){online.value=false;error.value=String(e);}}
  finally{reading=false;}
}
async function start(){
  if(busy.value||active.value||!confirmed.value||!store.submissionEnabled||selectedVehicleBlock.value||configurationBlocks.value.length)return;
  busy.value=true;error.value='';
  try{
    pending.value??={requestId:crypto.randomUUID().replaceAll('-',''),mode:props.mode,vehicleId:vehicle.value,
      stations:order.value.split(','),rounds:rounds.value,standbySite:standby.value,siteConfirmed:true};
    sessionStorage.setItem(key,JSON.stringify(pending.value));
    const result=await store.api<{current:Run|null}>('/api/business-run/start',pending.value);
    current.value=result.current;sessionStorage.removeItem(key);pending.value=null;confirmed.value=false;
  }catch(e){error.value=String(e)+'。请先查询原运行；重试沿用原申请编号。';}
  finally{busy.value=false;}
}
async function stop(){
  if(!current.value||busy.value)return;
  busy.value=true;error.value='';
  try{current.value=(await store.api<{current:Run|null}>('/api/business-run/stop',{runId:current.value.runId})).current;}
  catch(e){error.value=String(e)+'；请查询原运行，停止结果尚未确认。';}
  finally{busy.value=false;}
}
async function closeReviewed(){
  if(!current.value||busy.value||!confirmed.value)return;
  busy.value=true;error.value='';
  try{current.value=(await store.api<{current:Run|null}>('/api/business-run/close-reviewed',{runId:current.value.runId})).current;confirmed.value=false;}
  catch(e){error.value=String(e);}
  finally{busy.value=false;}
}
onMounted(()=>{void read();timer=setInterval(()=>void read(),2000);});
onUnmounted(()=>{disposed=true;if(timer)clearInterval(timer);});
</script>
<template>
  <section class="business-run">
    <p>{{mode==='Demand'?'按真实PLC呼叫接续工位，不主动产生呼叫。':'由本机PLC人工联调台按工位顺序产生模拟呼叫；一轮包含每个所选工位各一次任务。'}}</p>
    <div class="fields">
      <label>车辆<select v-model="vehicle" :disabled="locked"><option v-for="v in options?.vehicles??[]" :key="v.vehicleId" :value="v.vehicleId">{{v.label}}</option></select></label>
      <label>参与工位 / 接续顺序<select v-model="order" :disabled="locked"><option value="CNC-001,CNC-002">A → B（LM2 → LM4）</option><option value="CNC-002,CNC-001">B → A（LM4 → LM2）</option><option value="CNC-001">仅 A / LM2</option><option value="CNC-002">仅 B / LM4</option></select></label>
      <label v-if="mode==='Cycle'">测试轮数<input v-model.number="rounds" type="number" min="1" max="100" :disabled="locked"/></label>
      <label>倒料等待位<input :value="options?.waitingSite||'LM5'" readonly/></label>
      <label>倒料位<input :value="options?.dumpingSite||'LM6'" readonly/></label>
      <label>待机点<input v-model="standby" :disabled="locked"/></label>
    </div>
    <p>无待处理呼叫、返箱责任和倒料占用全部闭合后返回待机点。返途中新增呼叫，到待机点后接续；停止收尾不会取消当前车辆动作。</p>
    <p v-if="mode==='Cycle'">请启动新版 PLC 人工联调台 S7 服务（127.0.0.1:10200）。工具模拟呼叫、箱体和倒料反馈，真实业务动作仍由调度执行，模拟反馈不作为物理验收证据。</p>
    <p v-if="!store.submissionEnabled" class="inline-warning">Web任务提交未启用，请使用业务启动配置。</p>
    <section v-if="configurationBlocks.length" class="inline-warning" role="status">
      <strong>启动前需要补齐的配置</strong>
      <ul><li v-for="s in configurationBlocks" :key="s.stationId"><b>{{s.stationId}}</b><ul><li v-for="(issue,i) in s.explanation.split('；')" :key="i">{{issue}}</li></ul></li></ul>
      <p v-if="needsExitConfiguration">配置位置：当前发布包 Service/appsettings.json → HandshakeExecution → DumperExit。ResourceIds 对应地图编辑中的共享区域资源ID及已核定边界；BodyProfiles 为实测车辆和最大载箱包络。当前网页暂无这两项编辑表单，修改后需受控重启并重新核对本次启动恢复。</p>
      <p>补齐后点击弹窗底部“刷新条件”，再重试原申请。</p>
    </section>
    <p v-if="selectedVehicleBlock" class="inline-warning" role="status">车辆不可派发的具体原因：{{selectedVehicleBlock}}</p>
    <label class="confirm"><input v-model="confirmed" type="checkbox" :disabled="(!!active&&current?.stage!=='Hold')||busy"/>{{current?.stage==='Hold'?'已现场核对原运行，申请结束记录；后台仍检查任务、握手及待机停靠':'已核对当前车辆、路线和现场条件，确认启动所选运行模式'}}</label>
    <div class="buttons">
      <button :disabled="busy" @click="read">查询原运行</button>
      <button class="accent-button" :disabled="busy||!!active||!confirmed||!online||!store.submissionEnabled||!!selectedVehicleBlock||configurationBlocks.length>0" @click="start">{{pending?'重试原申请':mode==='Demand'?'启动按需运行':'启动循环测试'}}</button>
      <button :disabled="busy||!active||(!!current?.stopRequested&&current?.stage!=='Hold')||!online" @click="stop">停止新增呼叫并收尾</button>
      <button v-if="current?.stage==='Hold'" :disabled="busy||!confirmed||!online" @click="closeReviewed">核对原运行并结束记录</button>
    </div>
    <p v-if="error" class="inline-warning" role="alert" style="white-space:pre-line;overflow-wrap:anywhere">{{error}}</p>
    <div v-if="current" class="run-status" role="status">
      <strong>{{labels[current.stage]??current.stage}} {{current.stopRequested?' · 已请求停止新增':''}}</strong>
      <p>{{current.message}}</p>
      <p>{{current.plan.mode==='Cycle'?'循环测试':'按需运行'}} · {{current.plan.vehicleId}} · 已完成 {{current.completedJobs}} 次工位任务<span v-if="current.plan.mode==='Cycle'"> / {{current.plan.rounds*current.plan.stations.length}}（{{Math.floor(current.completedJobs/current.plan.stations.length)}} / {{current.plan.rounds}} 轮）</span></p>
      <p>工位顺序：{{current.plan.stations.join(' → ')}} · 待机 {{current.plan.standbySite}}</p>
      <p v-if="current.taskId">当前任务：{{current.taskId}}</p>
      <small>运行编号：{{current.runId}} · 更新 {{current.updatedAt}}</small>
    </div>
  </section>
</template>
<style scoped>
.business-run{display:grid;gap:14px;color:var(--text-secondary,#b9c9df)}.business-run p{margin:0;line-height:1.7}.fields{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px}.fields label{display:grid;gap:7px}.fields input,.fields select{width:100%;min-width:0}.confirm{display:flex;gap:9px;align-items:flex-start}.confirm input{width:auto;margin-top:5px}.buttons{display:flex;gap:10px;flex-wrap:wrap}.run-status{border:1px solid #34536c;border-radius:8px;padding:16px;display:grid;gap:8px;overflow-wrap:anywhere}@media(max-width:600px){.fields{grid-template-columns:1fr}}
</style>
