<script setup lang="ts">
import { computed,onMounted,ref,watch } from 'vue';
import { shortTime } from './dashboard';
import TimeoutDetourRecoveryPanel from './components/TimeoutDetourRecoveryPanel.vue';
interface RecoveryItem{ticketId:string;taskId:string;stepId:string;stepText:string;state:string;reason:string;verified:boolean;revision:number;vehicleSummary:string;plcSummary:string;resourceSummary:string;identitySummary:string;identityBlocksRecovery:boolean;loadSummary:string}
interface RecoveryStatus{sessionId:string;released:boolean;message:string;recoveryEnabled:boolean;items:RecoveryItem[];audits:{id:string;taskId:string;decisionText:string;reason:string;occurredAt:string}[]}
const props=defineProps<{taskId:string;fresh:boolean;now:number}>();
const status=ref<RecoveryStatus|null>(null),busy=ref(false),message=ref(''),ticket=ref(''),reason=ref(''),material=ref(''),decision=ref('ContinueOriginal');
const locationConfirmed=ref(false),loadConfirmed=ref(false),plcConfirmed=ref(false),pendingConfirmed=ref(false),globalImpact=ref(false),loadedAt=ref(0);let epoch=0;
const item=computed(()=>status.value?.items.find(i=>i.ticketId===ticket.value));
const current=computed(()=>props.fresh&&props.now-loadedAt.value>=-1000&&props.now-loadedAt.value<=30000);
const allowed=computed(()=>current.value&&status.value?.recoveryEnabled&&!busy.value&&reason.value.trim().length>=10);
function clearConfirmations(){locationConfirmed.value=false;loadConfirmed.value=false;plcConfirmed.value=false;pendingConfirmed.value=false;globalImpact.value=false;}
watch(()=>props.taskId,()=>{epoch++;status.value=null;message.value='';ticket.value='';busy.value=false;clearConfirmations();});watch(ticket,clearConfirmations);
async function load(){
  if(busy.value)return;const mine=++epoch;busy.value=true;
  try{const r=await fetch('/api/recovery',{signal:AbortSignal.timeout(35000)}),data=await r.json();if(!r.ok)throw new Error(data.detail??data.error??'读取恢复状态失败');if(mine===epoch){status.value=data;loadedAt.value=Date.now();clearConfirmations();ticket.value=data.items.some((i:RecoveryItem)=>i.ticketId===ticket.value)?ticket.value:(data.items.find((i:RecoveryItem)=>i.taskId===props.taskId)?.ticketId??'');}}
  catch(e){if(mine===epoch){status.value=null;message.value=String(e);}}finally{if(mine===epoch)busy.value=false;}
}
async function act(action:'begin'|'decisions'|'release'){
  if(!allowed.value)return;if(action==='begin'&&!props.taskId.trim())return;if(action!=='decisions'&&!globalImpact.value)return;
  if(action==='decisions'&&(!item.value||!locationConfirmed.value||!loadConfirmed.value||!plcConfirmed.value||!material.value.trim()))return;
  const body=action==='begin'?{taskId:props.taskId,reason:reason.value}:action==='release'?{sessionId:status.value!.sessionId,reason:reason.value}:{sessionId:status.value!.sessionId,ticketId:item.value!.ticketId,revision:item.value!.revision,decision:decision.value,materialReference:material.value,reason:reason.value,vehicleAndLocationConfirmed:locationConfirmed.value,loadAndOwnershipConfirmed:loadConfirmed.value,plcCycleConfirmed:plcConfirmed.value,handshakePendingWriteConfirmed:pendingConfirmed.value};
  const mine=++epoch;busy.value=true;
  try{const r=await fetch('/api/recovery/'+action,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body),signal:AbortSignal.timeout(35000)}),data=await r.json();if(!r.ok)throw new Error(data.detail??data.error??'恢复请求失败');if(mine===epoch)message.value=data.message;}
  catch(e){if(mine===epoch)message.value='请求结果需核对：'+String(e)+'。请刷新批次，勿直接重复提交。';}
  finally{if(mine===epoch){busy.value=false;await load();}}
}
onMounted(()=>{void load();});
</script>
<template><section class="recovery-panel"><h3>现场恢复核对入口</h3><p class="recovery-warning">进入恢复核对会冻结全场后续派单、运输发送和PLC推进；已被车辆接受的动作不会因此自动停止。放行、核对和补记都由原后台验证，本入口不提供强制跳步或直接写PLC位。</p><button :disabled="busy||!fresh" @click="load">读取 / 刷新恢复批次</button><p v-if="message" role="status">{{ message }}</p>
<template v-if="status"><p>{{ status.message }}</p><p>{{ status.recoveryEnabled?'Web恢复操作已配置开启，仍受后台核验。':'Web恢复写操作未启用；当前可只读核对，现场处置可使用WPF。' }} {{ current?'':'当前批次观察超过30秒或已断线，操作前请刷新。' }}</p>
<label>当前批次条目<select v-model="ticket" :disabled="busy"><option value="">选择待核对条目</option><option v-for="i in status.items" :key="i.ticketId" :value="i.ticketId">{{ i.taskId }} · {{ i.stepText }} · {{ i.verified?'已核对':'待核对' }} · r{{ i.revision }}</option></select></label>
<article v-if="item"><b>{{ item.stepText }} · {{ item.state }}</b><p>{{ item.reason }}</p><p>{{ item.vehicleSummary }}</p><p>{{ item.plcSummary }}</p><p>{{ item.resourceSummary }}</p><p>{{ item.loadSummary }}</p><p>{{ item.identitySummary }}</p></article>
<div class="recovery-form"><label>现场依据（至少10字）<textarea v-model="reason" rows="3" maxlength="1000" :disabled="busy" /></label><label>物料/容器编号，或明确空载说明<input v-model="material" maxlength="128" :disabled="busy"></label><label>本条目处置<select v-model="decision" :disabled="busy"><option value="ContinueOriginal">原节点续接</option><option value="RecordPhysicalCompletion">补记原动作完成（后台核验）</option><option value="CloseCancelled">核对后关闭取消</option></select></label>
<label><input v-model="locationConfirmed" type="checkbox" :disabled="busy">已现场核对车辆与位置</label><label><input v-model="loadConfirmed" type="checkbox" :disabled="busy">已现场核对载荷、箱体及任务归属</label><label><input v-model="plcConfirmed" type="checkbox" :disabled="busy">已现场核对本轮PLC周期</label><label><input v-model="pendingConfirmed" type="checkbox" :disabled="busy">若有待确认写入：已核实原写入按期望值执行，允许续接而不重发</label>
<button :disabled="!allowed||!item||item.verified||item.identityBlocksRecovery||!locationConfirmed||!loadConfirmed||!plcConfirmed||!material.trim()" @click="act('decisions')">提交所选条目核对</button>
<label class="recovery-warning"><input v-model="globalImpact" type="checkbox" :disabled="busy">已理解建立核对及放行对全场后续执行的影响；不会自动停止已接受动作</label><div><button :disabled="!allowed||!taskId.trim()||!globalImpact||!status.released" @click="act('begin')">为本任务建立全场恢复核对</button><button :disabled="!allowed||!globalImpact||status.released||!status.items.every(i=>i.verified)" @click="act('release')">请求后台核验并放行本批次</button></div></div>
<details><summary>最近恢复处置记录</summary><p v-for="a in status.audits" :key="a.id">{{ shortTime(a.occurredAt) }} · {{ a.taskId }} · {{ a.decisionText }} · {{ a.reason }}</p></details></template><TimeoutDetourRecoveryPanel :task-id="taskId" :fresh="fresh"/></section></template>
<style scoped>.recovery-panel{margin-top:28px;padding:18px;border:1px solid #524638;background:#1b2334}.recovery-panel h3{color:#e1c99c;font-size:15px}.recovery-panel p{font-size:12px;line-height:1.9;color:#abc1d7;margin:12px 0}.recovery-warning{font-size:12px;line-height:1.9;color:#dfbd83!important}.recovery-panel label{display:flex;gap:8px;align-items:center;font-size:12px;color:#b2c9df;margin:12px 0;flex-wrap:wrap}.recovery-panel select{max-width:100%}.recovery-form>label:first-child{display:block}.recovery-panel textarea{display:block;width:100%;margin-top:8px;background:#0b172a;color:#cee4f7;border:1px solid #3b526c;padding:10px;resize:vertical}.recovery-panel button{margin:5px 10px 5px 0}.recovery-panel article{border-left:2px solid #52708e;padding-left:14px}.recovery-panel details{font-size:12px;line-height:1.9;margin-top:15px}</style>
