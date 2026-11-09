<script setup lang="ts">
import { computed,onMounted,ref,watch } from 'vue';
import { shortTime } from './dashboard';
import TimeoutDetourRecoveryPanel from './components/TimeoutDetourRecoveryPanel.vue';
import { industrialText, qualityLabel, signalLabel } from './operator-labels';
interface RecoveryItem{ticketId:string;taskId:string;stepId:string;stepText:string;state:string;reason:string;verified:boolean;revision:number;vehicleSummary:string;plcSummary:string;resourceSummary:string;identitySummary:string;identityBlocksRecovery:boolean;loadSummary:string;requiresPendingWriteConfirmation:boolean}
interface RecoveryStatus{sessionId:string;released:boolean;message:string;recoveryEnabled:boolean;items:RecoveryItem[];audits:{id:string;taskId:string;decisionText:string;reason:string;occurredAt:string}[]}
const props=defineProps<{taskId:string;fresh:boolean;now:number}>();
const status=ref<RecoveryStatus|null>(null),busy=ref(false),message=ref(''),ticket=ref(''),reason=ref('现场已核对'),material=ref(''),decision=ref('ContinueOriginal');
const locationConfirmed=ref(false),loadConfirmed=ref(false),plcConfirmed=ref(false),pendingConfirmed=ref(false),offlineVehicleConfirmed=ref(false),globalImpact=ref(false),loadedAt=ref(0);let epoch=0;
const item=computed(()=>status.value?.items.find(i=>i.ticketId===ticket.value));
const current=computed(()=>props.fresh&&props.now-loadedAt.value>=-1000&&props.now-loadedAt.value<=30000);
const allowed=computed(()=>current.value&&status.value?.recoveryEnabled&&!busy.value&&reason.value.trim().length>0);
const selectedTask=computed(()=>item.value?.taskId??props.taskId);
const remaining=computed(()=>status.value?.items.filter(i=>!i.verified).length??0);
const note=ref('');
const emptyLoad=ref(false);
watch(emptyLoad,value=>{material.value=value?'空载':'';});
watch(ticket,()=>{material.value='';emptyLoad.value=false;decision.value='ContinueOriginal';});
const plcGroups=computed(()=>{
  const groups=new Map<string,{stationId:string;rows:{label:string;value:string;quality:string;raw:string}[]}>();
  const raw=item.value?.plcSummary??'';
  const pattern=/([^；]+)\/([A-Za-z][A-Za-z0-9_]*)=(True|False)（([^）]+)）/g;
  for(const match of raw.matchAll(pattern)){
    const stationId=match[1]!.trim(), variableName=match[2]!, value=match[3]==='True'?'1':'0', quality=match[4]!;
    const group=groups.get(stationId)??{stationId,rows:[]};
    group.rows.push({label:signalLabel(variableName),value,quality:qualityLabel(quality),raw:`${variableName}=${match[3]}（${quality}）`});
    groups.set(stationId,group);
  }
  return [...groups.values()];
});
const plcPointCount=computed(()=>plcGroups.value.reduce((sum,group)=>sum+group.rows.length,0));
const nextText=computed(()=>{
  if(!item.value)return '先选择一个恢复条目，系统会显示对应任务步骤和需要核对的现场条件。';
  if(item.value.verified)return '该条目已经核对。不要重复提交；如需放行，请检查批次内其他条目并完成全场放行确认。';
  if(item.value.identityBlocksRecovery)return '设备或容器身份尚未闭合。先核对原任务、PLC周期和箱体归属，身份不闭合时禁止续接。';
  if(!locationConfirmed.value||!loadConfirmed.value||!plcConfirmed.value)return '按下方顺序核对车辆位置、载荷与箱体归属、本轮PLC周期，再提交本条目。';
  if(!material.value.trim())return '填写物料/容器编号，或明确填写“空载”；缺少现场依据时不会允许提交。';
  if(decision.value==='CloseCancelled'&&offlineVehicleConfirmed.value)return '将按车辆已正常关机离线收尾；后台仍会核验原任务确认、零路权、PLC基线和设备归属，任一不符即拒绝。';
  return '现场条件已填写。提交后等待后台核验；结果不确定时刷新原批次，不能直接重发。';
});
function clearConfirmations(){locationConfirmed.value=false;loadConfirmed.value=false;plcConfirmed.value=false;pendingConfirmed.value=false;offlineVehicleConfirmed.value=false;globalImpact.value=false;}
watch(()=>props.taskId,()=>{epoch++;status.value=null;message.value='';ticket.value='';busy.value=false;clearConfirmations();});watch(ticket,clearConfirmations);
async function load(){
  if(busy.value)return;const mine=++epoch;busy.value=true;
  try{const r=await fetch('/api/recovery',{signal:AbortSignal.timeout(35000)}),data=await r.json();if(!r.ok)throw new Error(data.detail??data.error??'读取恢复状态失败');if(mine===epoch){status.value=data;loadedAt.value=Date.now();clearConfirmations();ticket.value=data.items.some((i:RecoveryItem)=>i.ticketId===ticket.value&&!i.verified)?ticket.value:(data.items.find((i:RecoveryItem)=>i.taskId===props.taskId&&!i.verified)?.ticketId??data.items.find((i:RecoveryItem)=>!i.verified)?.ticketId??'');}}
  catch(e){if(mine===epoch){status.value=null;message.value=String(e);}}finally{if(mine===epoch)busy.value=false;}
}
async function act(action:'begin'|'decisions'|'release'){
  if(!allowed.value)return;if(action==='begin'&&!props.taskId.trim())return;if(action!=='decisions'&&!globalImpact.value)return;
  if(action==='decisions'&&(!item.value||!locationConfirmed.value||!loadConfirmed.value||!plcConfirmed.value||!material.value.trim()))return;
  const body=action==='begin'?{taskId:props.taskId,reason:[reason.value,note.value.trim()].filter(Boolean).join("：")}:action==='release'?{sessionId:status.value!.sessionId,reason:[reason.value,note.value.trim()].filter(Boolean).join("：")}:{sessionId:status.value!.sessionId,ticketId:item.value!.ticketId,revision:item.value!.revision,decision:decision.value,materialReference:material.value,reason:[reason.value,note.value.trim()].filter(Boolean).join("："),vehicleAndLocationConfirmed:locationConfirmed.value,loadAndOwnershipConfirmed:loadConfirmed.value,plcCycleConfirmed:plcConfirmed.value,handshakePendingWriteConfirmed:pendingConfirmed.value,vehicleOfflineConfirmed:(decision.value==='CloseCancelled'||item.value!.taskId==='__SITE__')&&offlineVehicleConfirmed.value};
  const mine=++epoch;busy.value=true;
  try{const r=await fetch('/api/recovery/'+action,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body),signal:AbortSignal.timeout(35000)}),data=await r.json();if(!r.ok||data.accepted===false)throw new Error(data.message??data.detail??data.error??'恢复请求失败');if(mine===epoch){message.value=data.message;if(action==='decisions'&&globalImpact.value){const check=await fetch('/api/recovery',{signal:AbortSignal.timeout(35000)});const latest=await check.json();if(!check.ok)throw new Error('核对已提交，放行状态读取失败，请刷新');if(!latest.released&&latest.sessionId===body.sessionId&&latest.items.every((i:RecoveryItem)=>i.verified)){const release=await fetch('/api/recovery/release',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({sessionId:latest.sessionId,reason:[reason.value,note.value.trim()].filter(Boolean).join("：")}),signal:AbortSignal.timeout(35000)});const result=await release.json();if(!release.ok||!result.accepted)throw new Error(result.message??'放行未通过');message.value=result.message;}else if(!latest.released){message.value+='；还有其他异常待核对，尚未恢复全场运行。';}}}}
  catch(e){if(mine===epoch)message.value='请求结果需核对：'+String(e)+'。请刷新批次，勿直接重复提交。';}
  finally{if(mine===epoch){busy.value=false;await load();}}
}
onMounted(()=>{void load();});
</script>
<template>
<section class="recovery-panel">
<header><h3>待处理异常 <small>{{ remaining }} 项待核对</small></h3><button :disabled="busy||!fresh" @click="load">{{ busy?'处理中…':'刷新状态' }}</button></header>
<p v-if="message" role="status" class="recovery-warning">{{ industrialText(message) }}</p>
<template v-if="status">
<p v-if="!status.recoveryEnabled" class="recovery-warning">当前仅可查看，恢复操作未启用。</p>
<p v-if="!current" class="recovery-warning">状态已过期或连接中断，请刷新后核对。</p>
<p v-if="status.released">全场恢复屏障已放行。需要核对运行中的任务，请展开下方入口。</p>
<div v-if="!status.released" class="exception-list"><button v-for="i in status.items.filter(i=>!i.verified)" :key="i.ticketId" :class="{selected:ticket===i.ticketId}" :disabled="busy" @click="ticket=i.ticketId">{{ i.taskId==='__SITE__'?'全场设备':i.taskId }} · {{ i.stepText }}</button></div>
<article v-if="item&&!item.verified&&!status.released">
<h3>{{ item.stepText }}</h3><p>{{ industrialText(item.reason) }}</p>
<p>{{ industrialText(item.vehicleSummary) }}</p><p>{{ industrialText(item.loadSummary) }}</p>
<p v-if="item.identityBlocksRecovery" class="recovery-warning">{{ industrialText(item.identitySummary) }}</p>
<div class="recovery-form">
<label>处理原因<select v-model="reason" :disabled="busy"><option>现场已核对</option><option>障碍已清除</option><option>定位已恢复</option><option>人工处理完成</option></select></label>
<label><input v-model="locationConfirmed" type="checkbox" :disabled="busy">车辆与实际位置已核对</label>
<label><input v-model="loadConfirmed" type="checkbox" :disabled="busy">载荷、箱体与原任务归属已核对</label>
<label><input v-model="emptyLoad" type="checkbox" :disabled="busy">现场确认空载</label>
<label v-if="!emptyLoad">箱体编号／载荷核对说明<input v-model="material" maxlength="128" :disabled="busy" placeholder="载箱填写实际箱号；未知时先核对" /></label>
<label><input v-model="plcConfirmed" type="checkbox" :disabled="busy">本轮 PLC 周期与现场条件已核对 <a href="#plc-review">查看信号</a></label>
<label v-if="item.requiresPendingWriteConfirmation"><input v-model="pendingConfirmed" type="checkbox" :disabled="busy">原 PLC 写入已按期望值执行，续接时不重发</label>
<details><summary>其他处理与补充说明</summary><label>处置方式<select v-model="decision" :disabled="busy"><option value="ContinueOriginal">继续原任务</option><option value="RecordPhysicalCompletion">现场动作已完成，申请补记</option><option value="CloseCancelled">核对后关闭已取消任务</option></select></label><label v-if="decision==='CloseCancelled'||item.taskId==='__SITE__'"><input v-model="offlineVehicleConfirmed" type="checkbox" :disabled="busy">相关车辆已正常关机且通信离线（仅用于安全位置、载荷和PLC已核对的全场收尾）</label><label>补充说明（选填）<input v-model="note" maxlength="600" :disabled="busy" /></label><p>补记与关闭仍须后台核验；不会跳过步骤或直接释放占用。</p></details>
<label class="recovery-warning"><input v-model="globalImpact" type="checkbox" :disabled="busy">全部异常核对通过后恢复全场后续执行（已接受的动作不会自动停止）</label>
<button :disabled="!allowed||item.identityBlocksRecovery||!locationConfirmed||!loadConfirmed||!plcConfirmed||!material.trim()||(item.requiresPendingWriteConfirmation&&!pendingConfirmed)" @click="act('decisions')">{{ busy?'核验中…':globalImpact?'确认并请求恢复运行':'提交本项核对' }}</button>
</div>
<details id="plc-review"><summary>查看 PLC 信号、路权与身份详情</summary><p>{{ industrialText(item.identitySummary) }}</p><p>{{ industrialText(item.resourceSummary) }}</p><p v-if="!plcPointCount">未取得可识别信号，不能据此认为正常。</p><section v-for="group in plcGroups" :key="group.stationId"><b>{{ group.stationId }}</b><table><tbody><tr v-for="row in group.rows" :key="row.raw"><td>{{ row.label }}</td><td>{{ row.value }}</td><td>{{ row.quality }}</td></tr></tbody></table></section></details>
</article>
<div v-if="!status.released&&remaining===0"><p>所有条目已核对，仍需后台最终核验。</p><label><input v-model="globalImpact" type="checkbox" :disabled="busy">确认恢复全场后续派单、运输发送和 PLC 推进</label><button :disabled="!allowed||!globalImpact" @click="act('release')">确认并恢复全场运行</button></div>
<details v-if="status.released"><summary>核对运行中任务</summary><p>将冻结全场后续派单、发送和 PLC 推进；已接受动作不会自动停止。</p><label><input v-model="globalImpact" type="checkbox" :disabled="busy">确认上述全场影响</label><button :disabled="!allowed||!taskId.trim()||!globalImpact" @click="act('begin')">为所选任务开始核对</button></details>
<details><summary>最近处置记录</summary><p v-for="a in status.audits" :key="a.id">{{ shortTime(a.occurredAt) }} · {{ a.taskId }} · {{ a.decisionText }} · {{ a.reason }}</p></details>
</template>
<details><summary>运输超时：受控改道与专项核对</summary><TimeoutDetourRecoveryPanel :task-id="selectedTask" :fresh="fresh" /></details>
</section>
</template>
<style scoped>
.recovery-panel{padding:20px;border:1px solid #344c64;background:#111e30;border-radius:10px}.recovery-panel header{display:flex;justify-content:space-between;align-items:center}.recovery-panel h3{font-size:16px;color:#d9e7f5}.recovery-panel small{font-size:12px;color:#8fa9c1;margin-left:12px}.recovery-panel p,.recovery-panel label{font-size:13px;line-height:1.8;color:#b2c9df}.recovery-panel label{display:flex;align-items:center;gap:9px;margin:14px 0;flex-wrap:wrap}.recovery-panel button{margin:5px 10px 5px 0}.exception-list{display:flex;flex-wrap:wrap;margin:16px 0}.selected{border-color:#49bed0;background:#184555}.recovery-panel article{padding:16px;border:1px solid #344c64;border-radius:8px}.recovery-panel details{margin-top:18px;font-size:13px;color:#a9bfd6}.recovery-panel summary{cursor:pointer}.recovery-warning{color:#e7be78!important}.recovery-panel table{width:100%;margin:12px 0}.recovery-panel td{padding:7px;border-bottom:1px solid #344c64}
</style>
