<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
interface RecordRow {taskId:string;vendorTaskId:string;vehicleId:string;sourceSite:string;targetSite:string;robotMap:string;robotMapMd5:string;state:string;message:string;updatedAt:string}
interface Status {ready:boolean;blockReason:string|null;enabled:boolean;vehicleId:string;current:RecordRow|null;message:string}
interface Preview {previewId:string;expiresAt:string;vehicleId:string;sourceSite:string;targetSite:string;robotMap:string;robotMapMd5:string;route:string[]}
const status=ref<Status|null>(null), preview=ref<Preview|null>(null), source=ref('LM1'),target=ref('LM2'),confirmed=ref(false),busy=ref(false),error=ref(''),readError=ref(''),now=ref(Date.now());
const labels:Record<string,string>={Accepted:'已接受，等待执行',Waiting:'等待中',Running:'运行中',Suspended:'暂停',Completed:'已完成',Failed:'失败',Cancelled:'已取消',CancelRequested:'取消待核实',Uncertain:'结果不确定，禁止重发'};
const active=computed(()=>!!status.value?.current&&!['Completed','Failed','Cancelled'].includes(status.value.current.state));
const valid=computed(()=>!!preview.value&&Date.parse(preview.value.expiresAt)>now.value);
const canSubmit=computed(()=>valid.value&&confirmed.value&&!busy.value&&!active.value&&status.value?.ready);
let disposed=false,timer:ReturnType<typeof setInterval>|undefined;
async function api<T>(suffix='',body?:unknown):Promise<T>{const r=await fetch('/api/navigation-test'+suffix,{method:body===undefined?'GET':'POST',headers:body===undefined?{}:{'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(30000)});const data=await r.json();if(!r.ok)throw new Error(data.detail??data.error??'请求失败');return data;}
async function refresh(){if(busy.value||disposed)return;busy.value=true;try{const s=await api<Status>();if(!disposed){status.value=s;readError.value='';if(!s.ready){preview.value=null;confirmed.value=false;}}}catch(e){if(!disposed){readError.value=String(e);preview.value=null;}}finally{busy.value=false;}}
async function inspect(){if(busy.value||!status.value?.ready||active.value)return;preview.value=null;confirmed.value=false;busy.value=true;error.value='';try{preview.value=await api<Preview>('/preview',{vehicleId:status.value?.vehicleId,sourceSite:source.value.trim(),targetSite:target.value.trim()});}catch(e){error.value=String(e);}finally{busy.value=false;}}
async function submit(){if(!canSubmit.value||!preview.value)return;const id=preview.value.previewId;preview.value=null;confirmed.value=false;busy.value=true;error.value='';try{await api('/submit',{previewId:id,siteClearConfirmed:true});}catch(e){error.value=String(e)+'；请先查询原任务，确认未发送并解除阻塞后再重新核验。';}finally{busy.value=false;await refresh();}}
async function cancel(){const r=status.value?.current;if(!r)return;busy.value=true;error.value='';try{await api('/cancel',{taskId:r.taskId});}catch(e){error.value=String(e);}finally{busy.value=false;await refresh();}}
watch([source,target],()=>{preview.value=null;confirmed.value=false;});
onMounted(()=>{void refresh();timer=setInterval(()=>{now.value=Date.now();void refresh();},3000);});
onUnmounted(()=>{disposed=true;if(timer)clearInterval(timer);});
</script>

<template>
<main class="nav-test">
 <h2>单次导航测试</h2>
 <p>在厂家确认的空闲区域，测试一台车沿一条相邻路径行驶。速度沿用车端配置，请由厂家先设置测试速度。</p>
 <p class="notice">本页使用车端当前地图和真实站点，不依赖PLC业务任务。不会修改车端地图、抢占控制权或启动自动调度。</p>
 <p v-if="error||readError" role="alert" class="error">{{error||readError}}</p>
 <section>
  <h3>选择测试路径</h3>
  <p>测试车：<strong>{{status?.vehicleId||'尚未指定'}}</strong> · {{status?.enabled?'测试入口已启用':'后台未启用此测试入口'}}</p>
  <p v-if="status?.blockReason" role="alert" class="error">当前不能发送：{{status.blockReason}}</p>
  <div class="fields"><label>起点（厂家编号）<input v-model="source" :disabled="busy||active" maxlength="128"/></label><label>终点（厂家编号）<input v-model="target" :disabled="busy||active" maxlength="128"/></label></div>
  <p>车辆必须已停在起点。第一次建议 LM1 → LM2；当前任务结束后，可重新核验 LM2 → LM1。</p>
  <button :disabled="busy||active||!status?.ready" @click="inspect">核验车端地图、起点和路线</button>
  <div v-if="preview" class="preview">
   <h3>{{preview.route.join(' → ')}}</h3><p>车端地图：{{preview.robotMap}}</p><p>MD5：{{preview.robotMapMd5}}</p>
   <p>{{valid?'核验有效，发送前后台会再次核验':'核验已过期，请重新核验'}} · 有效至 {{new Date(preview.expiresAt).toLocaleTimeString()}}</p>
   <label><input v-model="confirmed" type="checkbox" :disabled="!valid||busy"/>已确认测试区域空闲、厂家在场，允许这台车执行上述单段导航</label>
   <button class="send" :disabled="!canSubmit" @click="submit">发送单次导航</button>
  </div>
 </section>
 <section>
  <h3>本次任务与执行状态</h3><button :disabled="busy" @click="refresh">查询原任务</button>
  <template v-if="status?.current"><h3>{{labels[status.current.state]||status.current.state}}</h3>
   <p>{{status.current.sourceSite}} → {{status.current.targetSite}} · {{status.current.vehicleId}}</p>
   <p>任务编号：{{status.current.vendorTaskId}}</p><p>{{status.current.message}}</p>
   <p>最近查询：{{new Date(status.current.updatedAt).toLocaleString()}}</p>
   <button :disabled="busy||!active||!status.enabled" @click="cancel">取消本次导航</button>
  </template><p v-else-if="!status||readError">尚未取得可靠的后台任务状态，请先查询原任务。</p><p v-else>后台当前没有已记录的测试任务。</p>
  <p>取消是协议请求，不是急停；异常时由现场人员使用车辆急停。断线、超时或后台重启后，只查询原任务，不自动重发。</p>
 </section>
</main>
</template>
<style scoped>
.nav-test{max-width:1100px;margin:24px auto;padding:0 20px;color:#d4e5f6}.nav-test section{background:#18263b;border:1px solid #344965;border-radius:8px;padding:24px;margin:20px 0}.nav-test h2{font-size:26px}.nav-test p{line-height:1.7;overflow-wrap:anywhere}.fields{display:flex;gap:24px;flex-wrap:wrap}.fields label{display:grid;gap:8px}.nav-test input:not([type=checkbox]){padding:10px;background:#101b2e;border:1px solid #526d8d;border-radius:5px;color:inherit}.nav-test button{padding:11px 18px;background:#254868;color:#e5f7ff;border:1px solid #5c89ab;border-radius:5px;cursor:pointer;margin:8px 10px 8px 0}.nav-test button:disabled{opacity:.45;cursor:not-allowed}.nav-test .send{display:block;background:#126b8c;margin-top:18px}.preview{border-left:3px solid #53c8df;padding:8px 20px;margin-top:20px}.error{color:#ffb2ab;background:#4b252c;padding:16px}.notice{color:#abd0df}
</style>
