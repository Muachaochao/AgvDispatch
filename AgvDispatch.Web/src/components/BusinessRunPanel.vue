<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import OrderedStationPicker from './OrderedStationPicker.vue';
import { businessChoices, standbyChoices } from '../business-run-options';
import { useDispatchStore } from '../stores/dispatch';
import type { CreationOptions } from '../types';
import { businessVehicleBlock, summarizeBusinessBlocks } from '../business-blocks';
import { effectiveWaitingSites } from '../waiting-sites';
const props=defineProps<{mode:'Demand'|'Cycle';options:CreationOptions|null;statusOnly?:boolean}>();
const store=useDispatchStore();
type Plan={requestId:string;mode:string;vehicleId:string;stations:string[];rounds:number;standbySite:string;siteConfirmed:boolean};
type Run={runId:string;plan:Plan;stage:string;message:string;completedJobs:number;taskId:string|null;stopRequested:boolean;updatedAt:string;lastResumeRequestId?:string};
const current=ref<Run|null>(null), error=ref(''), busy=ref(false), online=ref(false);
const readError=ref('');
const observedAt=ref(0);
const fresh=computed(()=>online.value&&store.fresh&&store.now-observedAt.value>=-1000&&store.now-observedAt.value<10000);
const vehicle=ref(''), order=ref<string[]>([]), rounds=ref(1), standby=ref(''), confirmed=ref(false);
const pending=ref<Plan|null>(null);
const resumeReason=ref('');
const resumeKey='agv-business-run-resume';
async function resume(){
  if(!current.value||busy.value||!fresh.value||!confirmed.value||!store.submissionEnabled||resumeReason.value.trim().length<10)return;
  epoch++;busy.value=true;error.value='';
  try{
    const previous=sessionStorage.getItem(resumeKey);
    const saved=previous?JSON.parse(previous):null;
    const request=saved&&saved.expectedUpdatedAt===current.value.updatedAt?saved:{runId:current.value.runId,requestId:crypto.randomUUID().replaceAll('-',''),expectedUpdatedAt:current.value.updatedAt,reason:resumeReason.value.trim(),siteConfirmed:true};
    if(request.runId!==current.value.runId)throw new Error('保留了其他运行的未确认续接申请，请先核对原运行');
    sessionStorage.setItem(resumeKey,JSON.stringify(request));
    current.value=(await store.api<{current:Run|null}>('/api/business-run/resume',request)).current;
    sessionStorage.removeItem(resumeKey);confirmed.value=false;
  }catch(e){error.value=String(e)+'。沿用原申请查询；不得清除原任务。';}
  finally{busy.value=false;}
}
const key='agv-business-run-start';
try{pending.value=JSON.parse(sessionStorage.getItem(key)??'null');}catch{error.value='原申请记录无法读取，请先查询后台原运行。';}
const active=computed(()=>current.value&&current.value.stage!=='Completed');
const locked=computed(()=>busy.value||!!active.value||!!pending.value);
const choices=computed(()=>businessChoices(props.options,order.value));
const standbyOptions=computed(()=>standbyChoices(props.options,vehicle.value));
const stationChoices=computed(()=>(props.options?.stations??[]).map(s=>({id:s.stationId,label:s.stationId+' · '+s.pickupSite+(s.vehiclePoolId?' · '+s.vehiclePoolId:'')})));
const selectionError=computed(()=>!choices.value.valid?'请从当前配置选择参与工位。':
 !choices.value.vehicles.some(v=>v.vehicleId===vehicle.value)?'所选工位没有共同允许的车辆，请调整工位组或车辆池配置。':
 !standby.value?'请配置并选择当前车辆的待机点。':
 props.options?.standbys?.length&&!standbyOptions.value.some(s=>s.site===standby.value)?'所选待机点不适用于当前车辆。':'');
watch(()=>JSON.stringify([props.options?.mapId,props.options?.mapVersionId,props.options?.parameterRevision,
 props.options?.stations.map(s=>[s.stationId,s.pickupSite,s.allowedVehicleIds]),props.options?.vehicles.map(v=>v.vehicleId),props.options?.standbys]),()=>{
 if(locked.value)return;confirmed.value=false;order.value=(props.options?.stations??[]).map(s=>s.stationId);
 vehicle.value=choices.value.vehicles.find(v=>v.available)?.vehicleId??choices.value.vehicles[0]?.vehicleId??'';
 standby.value=standbyOptions.value[0]?.site??'';
},{immediate:true});
watch([order,vehicle,standby,rounds],()=>{confirmed.value=false;},{deep:true});
const configurationBlocks=computed(()=>{
  const selected=pending.value?.stations??order.value;
  return (props.options?.stations??[]).filter(s=>selected.includes(s.stationId)&&s.configurationValid===false);
});
const needsExitConfiguration=computed(()=>configurationBlocks.value.some(s=>s.explanation.includes('DumperExit')));
const blockGroups=computed(()=>summarizeBusinessBlocks(configurationBlocks.value));
const onlyPermissions=computed(()=>blockGroups.value.length>0&&blockGroups.value.every(g=>g.permission));
const selectedVehicleBlock=computed(()=>{
  const id=current.value?.plan.vehicleId??pending.value?.vehicleId??vehicle.value;
  const selected=props.options?.vehicles.find(v=>v.vehicleId===id);
  return businessVehicleBlock(selected);
});
const labels:Record<string,string>={Preparing:'准备中',Waiting:'等待呼叫 / 待机',Creating:'受理呼叫中',Executing:'业务执行中',ReturnSending:'准备返回待机',Returning:'返回待机中',Hold:'暂停待核对',Completed:'收尾完成，已回待机'};
let timer:ReturnType<typeof setInterval>|undefined;
let disposed=false, reading=false, epoch=0;
async function read(){
  if(busy.value||reading||disposed)return;
  reading=true;
  const mine=epoch;
  try{
    const result=await store.api<{current:Run|null}>('/api/business-run');
    if(disposed||mine!==epoch)return;
    if(current.value?.updatedAt!==result.current?.updatedAt)confirmed.value=false;
    current.value=result.current;online.value=true;observedAt.value=Date.now();readError.value='';
    const original=current.value&&current.value.stage!=='Completed'?current.value.plan:pending.value;
    if(original){vehicle.value=original.vehicleId;if(JSON.stringify(order.value)!==JSON.stringify(original.stations))order.value=[...original.stations];standby.value=original.standbySite;rounds.value=original.rounds;}
    const resumeRaw=sessionStorage.getItem(resumeKey);
    if(resumeRaw&&JSON.parse(resumeRaw).requestId===result.current?.lastResumeRequestId)sessionStorage.removeItem(resumeKey);
    if(result.current?.plan.requestId===pending.value?.requestId){sessionStorage.removeItem(key);pending.value=null;}
  }catch(e){if(!disposed&&mine===epoch){online.value=false;readError.value=String(e);}}
  finally{reading=false;}
}
async function start(){
  if(props.statusOnly||!fresh.value||busy.value||active.value||!confirmed.value||!store.submissionEnabled||selectedVehicleBlock.value||configurationBlocks.value.length||(!pending.value&&selectionError.value))return;
  epoch++;
  busy.value=true;error.value='';
  try{
    pending.value??={requestId:crypto.randomUUID().replaceAll('-',''),mode:props.mode,vehicleId:vehicle.value,
      stations:[...order.value],rounds:rounds.value,standbySite:standby.value,siteConfirmed:true};
    sessionStorage.setItem(key,JSON.stringify(pending.value));
    const result=await store.api<{current:Run|null}>('/api/business-run/start',pending.value);
    current.value=result.current;sessionStorage.removeItem(key);pending.value=null;confirmed.value=false;
  }catch(e){error.value=String(e)+'。请先查询原运行；重试沿用原申请编号。';}
  finally{busy.value=false;}
}
async function stop(){
  if(!current.value||busy.value||!fresh.value)return;
  epoch++;
  busy.value=true;error.value='';
  try{current.value=(await store.api<{current:Run|null}>('/api/business-run/stop',{runId:current.value.runId})).current;}
  catch(e){error.value=String(e)+'；请查询原运行，停止结果尚未确认。';}
  finally{busy.value=false;}
}
async function closeReviewed(){
  if(!current.value||busy.value||!confirmed.value||!fresh.value)return;
  epoch++;
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
    <h3>业务运行与收尾</h3>
    <template v-if="!statusOnly">
    <p>{{mode==='Demand'?'按真实PLC呼叫接续工位，不主动产生呼叫。':'由本机PLC人工联调台按工位顺序产生模拟呼叫；一轮包含每个所选工位各一次任务。'}}</p>
    <div class="fields">
      <label>本次运行车辆<select v-model="vehicle" :disabled="locked"><option value="" disabled>请选择车辆</option><option v-for="v in choices.vehicles" :key="v.vehicleId" :value="v.vehicleId">{{v.label}}</option></select><small>从所选工位车辆池的共同允许车辆中选择，共 {{choices.vehicles.length}} 台。</small></label>
      <label v-if="mode==='Cycle'">测试轮数<input v-model.number="rounds" type="number" min="1" max="100" :disabled="locked"/></label>
      <label>倒料候车位<input :value="effectiveWaitingSites(options).join('、')||'未配置'" readonly/></label>
      <label>倒料位<input :value="options?.dumpingSite||'未配置'" readonly/></label>
      <label v-if="options?.standbys?.length">待机点<select v-model="standby" :disabled="locked"><option value="" disabled>请选择适用待机位</option><option v-for="s in standbyOptions" :key="s.id" :value="s.site">{{s.id}} · {{s.site}} · {{s.vehiclePoolId}}</option></select></label>
      <label v-else>待机点（旧配置未绑定待机位）<input v-model="standby" :disabled="locked" placeholder="填写已核对的地图站点"/><small>可在工程配置中心设置待机位和适用车辆池后应用。</small></label>
    </div>
    <OrderedStationPicker v-model="order" :choices="stationChoices" :disabled="locked"/>
    <p v-if="selectionError&&!active&&!pending" class="inline-warning">{{selectionError}}</p>
    <RouterLink to="/engineering">配置工位、车辆池、倒料设备及待机位</RouterLink>
    <p>无待处理呼叫、返箱责任和倒料占用全部闭合后返回待机点。返途中新增呼叫，到待机点后接续；停止收尾不会取消当前车辆动作。</p>
    <p v-if="mode==='Cycle'">请按当前后台连接配置启动对应的 PLC 模拟服务。工具模拟呼叫、箱体和倒料反馈，真实业务动作仍由调度执行，模拟反馈不作为物理验收证据。</p>
    <p v-if="!store.submissionEnabled" class="inline-warning">当前未启用业务执行，暂不能提交任务。启动模式限制不会删除已保存的工位和点位配置。</p>
    <section v-if="configurationBlocks.length" class="inline-warning" role="status">
      <strong>{{onlyPermissions?'执行权限尚未开启':'启动条件未通过'}}</strong>
      <ul><li v-for="g in blockGroups" :key="g.reason">{{g.reason}}<small> · 涉及 {{g.stations.join('、')}}</small></li></ul>
      <p v-if="onlyPermissions">这些是同一写入门禁在各业务步骤中的重复检查，不需要逐个重新配置点位。显式切换到业务启动模式后，再刷新条件核对。</p>
      <details><summary>查看技术明细（{{blockGroups.reduce((sum,g)=>sum+g.count,0)}} 项步骤检查）</summary><p v-for="s in configurationBlocks" :key="s.stationId"><b>{{s.stationId}}</b><br>{{s.explanation}}</p></details>
      <p v-if="needsExitConfiguration">配置位置：当前发布包 Service/appsettings.json → HandshakeExecution → DumperExit。ResourceIds 对应地图编辑中的共享区域资源ID及已核定边界；BodyProfiles 为实测车辆和最大载箱包络。当前网页暂无这两项编辑表单，修改后需受控重启并重新核对本次启动恢复。</p>
      <p v-if="!onlyPermissions">处理上述条件后，点击弹窗底部“刷新条件”；结果未确认时沿用原申请。</p>
    </section>
    <p v-if="selectedVehicleBlock" class="inline-warning" role="status">车辆不可派发的具体原因：{{selectedVehicleBlock}}</p>
    </template>
    <p v-if="!fresh" class="inline-warning">当前运行观察无效或已过期；保存状态不代表车辆实时状态，请查询并恢复后台连接。</p>
    <p v-if="current?.stage==='Hold'">Hold 保留了原任务责任。先进入异常恢复核对原任务；记录不能直接清空。核对结束记录仍须通过后台的任务、握手、占用和待机停靠检查。</p>
    <label v-if="current?.stage==='Hold'">续接现场依据（至少10字）<input v-model="resumeReason" maxlength="1000" :disabled="busy"/></label>
    <label v-if="!statusOnly||current?.stage==='Hold'" class="confirm"><input v-model="confirmed" type="checkbox" :disabled="(!!active&&current?.stage!=='Hold')||busy"/>{{current?.stage==='Hold'?'已现场核对原运行；续接将继续原流程，结束记录仍须核验任务、握手及待机停靠':'已核对当前车辆、路线和现场条件，确认启动所选运行模式'}}</label>
    <div class="buttons">
      <button :disabled="busy" @click="read">查询原运行</button>
      <button v-if="!statusOnly" class="accent-button" :disabled="busy||!!active||!confirmed||!fresh||!store.submissionEnabled||!!selectedVehicleBlock||configurationBlocks.length>0||(!pending&&!!selectionError)" @click="start">{{pending?'重试原申请':mode==='Demand'?'启动按需运行':'启动循环测试'}}</button>
      <button :disabled="busy||!active||(!!current?.stopRequested&&current?.stage!=='Hold')||!fresh" @click="stop">停止新增呼叫并收尾</button>
      <button v-if="current?.stage==='Hold'" :disabled="busy||!confirmed||!fresh" @click="closeReviewed">核对原运行并结束记录</button>
      <button v-if="current?.stage==='Hold'" :disabled="busy||!confirmed||!fresh||!store.submissionEnabled||resumeReason.trim().length<10" @click="resume">核对后续接原运行</button>
      <RouterLink :to="{path:'/recovery',query:{taskId:current?.taskId??undefined}}">异常恢复核对</RouterLink>
    </div>
    <p v-if="error" class="inline-warning" role="alert" style="white-space:pre-line;overflow-wrap:anywhere">{{error}}</p>
    <p v-if="readError" class="inline-warning" role="alert">查询原运行失败：{{readError}}</p>
    <div v-if="current" class="run-status" role="status">
      <strong>{{labels[current.stage]??current.stage}} {{current.stopRequested?' · 已请求停止新增':''}}</strong>
      <p>{{current.message}}</p>
      <p>{{current.plan.mode==='Cycle'?'循环测试':'按需运行'}} · {{current.plan.vehicleId}} · 已完成 {{current.completedJobs}} 次工位任务<span v-if="current.plan.mode==='Cycle'"> / {{current.plan.rounds*current.plan.stations.length}}（{{Math.floor(current.completedJobs/current.plan.stations.length)}} / {{current.plan.rounds}} 轮）</span></p>
      <p>工位顺序：{{current.plan.stations.join(' → ')}} · 待机 {{current.plan.standbySite}}</p>
      <p v-if="current.taskId">当前任务：<RouterLink :to="`/tasks/${encodeURIComponent(current.taskId)}`">{{current.taskId}}</RouterLink></p>
      <small>运行编号：{{current.runId}} · 更新 {{current.updatedAt}}</small>
    </div>
  </section>
</template>
<style scoped>
.business-run{display:grid;gap:14px;color:var(--text-secondary,#b9c9df)}.business-run p{margin:0;line-height:1.7}.fields{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px}.fields label{display:grid;gap:7px}.fields input,.fields select{width:100%;min-width:0}.confirm{display:flex;gap:9px;align-items:flex-start}.confirm input{width:auto;margin-top:5px}.buttons{display:flex;gap:10px;flex-wrap:wrap}.run-status{border:1px solid #34536c;border-radius:8px;padding:16px;display:grid;gap:8px;overflow-wrap:anywhere}@media(max-width:600px){.fields{grid-template-columns:1fr}}
</style>
