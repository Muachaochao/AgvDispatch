<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
interface RecordRow {taskId:string;vendorTaskId:string;vehicleId:string;sourceSite:string;targetSite:string;robotMap:string;robotMapMd5:string;state:string;message:string;updatedAt:string;route:string[];currentStation:string|null;x:number|null;y:number|null;observedAt:string|null}
interface Status {ready:boolean;blockReason:string|null;enabled:boolean;vehicleId:string;current:RecordRow|null;message:string}
interface Preview {previewId:string;expiresAt:string;vehicleId:string;sourceSite:string;targetSite:string;robotMap:string;robotMapMd5:string;route:string[]}
interface Stations {vehicleId:string;robotMap:string;robotMapMd5:string;stations:{id:string;x:number;y:number}[];suggestedSourceSite:string|null;capturedAt:string}
const status=ref<Status|null>(null),catalog=ref<Stations|null>(null),preview=ref<Preview|null>(null);
const source=ref(''),target=ref(''),confirmed=ref(false),busy=ref(false),error=ref(''),readError=ref(''),now=ref(Date.now());
const labels:Record<string,string>={Accepted:'已接受，等待执行',Waiting:'等待中',Running:'运行中',Suspended:'暂停',Completed:'已完成并核对到点',Failed:'失败',Cancelled:'已取消',CancelRequested:'取消待核实',Uncertain:'结果不确定，禁止重发'};
const active=computed(()=>!!status.value?.current&&!['Completed','Failed','Cancelled'].includes(status.value.current.state));
const valid=computed(()=>!!preview.value&&Date.parse(preview.value.expiresAt)>now.value);
const canInspect=computed(()=>!!catalog.value&&!!source.value&&!!target.value&&source.value!==target.value&&!busy.value&&!active.value&&!readError.value&&status.value?.ready);
const canSubmit=computed(()=>valid.value&&confirmed.value&&!busy.value&&!active.value&&!readError.value&&status.value?.ready);
let disposed=false,timer:ReturnType<typeof setInterval>|undefined;
async function api<T>(suffix='',body?:unknown):Promise<T>{const r=await fetch('/api/navigation-test'+suffix,{method:body===undefined?'GET':'POST',headers:body===undefined?{}:{'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(30000)});const data=await r.json();if(!r.ok)throw new Error(data.detail??data.error??'请求失败');return data;}
function invalidate(){preview.value=null;confirmed.value=false;}
async function refresh(){if(busy.value||disposed)return;busy.value=true;try{const s=await api<Status>();if(!disposed){status.value=s;readError.value='';if(!s.ready)invalidate();}}catch(e){if(!disposed){readError.value=String(e);invalidate();}}finally{busy.value=false;}}
async function loadStations(){if(busy.value||active.value||disposed)return;invalidate();busy.value=true;error.value='';const previous=catalog.value;catalog.value=null;try{const c=await api<Stations>('/stations');if(disposed)return;const same=previous?.vehicleId===c.vehicleId&&previous.robotMap===c.robotMap&&previous.robotMapMd5===c.robotMapMd5;catalog.value=c;source.value=c.suggestedSourceSite??(same&&c.stations.some(s=>s.id===source.value)?source.value:'');target.value=same&&c.stations.some(s=>s.id===target.value)?target.value:'';}catch(e){if(!disposed)error.value=String(e);}finally{busy.value=false;}}
async function inspect(){if(!canInspect.value||!catalog.value)return;invalidate();busy.value=true;error.value='';try{preview.value=await api<Preview>('/preview',{vehicleId:catalog.value.vehicleId,sourceSite:source.value,targetSite:target.value,expectedRobotMap:catalog.value.robotMap,expectedRobotMapMd5:catalog.value.robotMapMd5});}catch(e){error.value=String(e);}finally{busy.value=false;}}
async function submit(){if(!canSubmit.value||!preview.value)return;const id=preview.value.previewId;invalidate();busy.value=true;error.value='';try{await api('/submit',{previewId:id,siteClearConfirmed:true});}catch(e){error.value=String(e)+'；请先查询原任务，确认未发送并解除阻塞后再重新核验。';}finally{busy.value=false;await refresh();}}
async function cancel(){const r=status.value?.current;if(!r)return;busy.value=true;error.value='';try{await api('/cancel',{taskId:r.taskId});}catch(e){error.value=String(e);}finally{busy.value=false;await refresh();}}
watch([source,target],invalidate);
onMounted(async()=>{await refresh();if(!disposed&&status.value?.enabled&&!active.value)await loadStations();if(!disposed)timer=setInterval(()=>{now.value=Date.now();void refresh();},3000);});
onUnmounted(()=>{disposed=true;if(timer)clearInterval(timer);});
</script>

<template>
<main class="nav-test">
 <h2>单次导航测试</h2>
 <p>从车端地图选择起点和终点，由车辆规划并沿可通行路线行驶，可经过中间站点。速度沿用车端配置，请由厂家先设置测试速度。</p>
 <p class="notice">本页不依赖PLC业务任务，不修改车端地图、不抢占控制权、不启动自动调度。每次只执行一个导航任务。</p>
 <p v-if="error||readError" role="alert" class="error">{{error||readError}}</p>
 <section>
  <h3>选择车端站点</h3>
  <p>测试车：<strong>{{status?.vehicleId||'尚未指定'}}</strong> · {{status?.enabled?'测试入口已启用':'后台未启用此测试入口'}}</p>
  <p v-if="status?.blockReason" role="alert" class="error">当前不能发送：{{status.blockReason}}</p>
  <button :disabled="busy||active||!status?.enabled" @click="loadStations">重新读取车端站点</button>
  <template v-if="catalog">
   <p>车端地图：{{catalog.robotMap}} · {{catalog.stations.length}} 个站点<br/>读取时间：{{new Date(catalog.capturedAt).toLocaleString()}}</p>
   <div class="fields">
    <label>起点（车辆实际所在站点）<select v-model="source" :disabled="busy||active"><option value="" disabled>请选择起点</option><option v-for="s in catalog.stations" :key="s.id" :value="s.id">{{s.id}}</option></select></label>
    <label>终点<select v-model="target" :disabled="busy||active"><option value="" disabled>请选择终点</option><option v-for="s in catalog.stations" :key="s.id" :value="s.id" :disabled="s.id===source">{{s.id}}</option></select></label>
   </div>
   <p>车辆必须已停在起点。选定终点后先核验路线；本次任务结束后，可重新选择站点继续测试。</p>
  </template>
  <p v-else>读取车端当前地图中的真实站点后，即可选择起点和终点。</p>
  <button :disabled="!canInspect" @click="inspect">核验车端地图、起点和路线</button>
  <div v-if="preview" class="preview">
   <h3>可达路线 · {{preview.route.length-1}} 段</h3><p class="route">{{preview.route.join(' → ')}}</p>
   <p>车端地图：{{preview.robotMap}}<br/>MD5：{{preview.robotMapMd5}}</p>
   <p>以上是核验时车端返回的站点顺序，实际路线由车端规划执行；发送前会再次核验。</p>
   <p>{{valid?'核验有效':'核验已过期，请重新核验'}} · 有效至 {{new Date(preview.expiresAt).toLocaleTimeString()}}</p>
   <label><input v-model="confirmed" type="checkbox" :disabled="!valid||busy"/>已确认整条测试路线区域空闲、厂家在场，允许这台车执行本次导航</label>
   <button class="send" :disabled="!canSubmit" @click="submit">发送单次导航</button>
  </div>
 </section>
 <section>
  <h3>本次任务与执行状态</h3><button :disabled="busy" @click="refresh">查询原任务</button>
  <template v-if="status?.current"><h3>{{labels[status.current.state]||status.current.state}}</h3>
   <p>{{status.current.sourceSite}} → {{status.current.targetSite}} · {{status.current.vehicleId}}</p>
   <p v-if="status.current.route?.length">核验路线：{{status.current.route.join(' → ')}}</p>
   <p>任务编号：{{status.current.vendorTaskId}}</p><p>{{status.current.message}}</p>
   <p v-if="status.current.observedAt">最近车辆观测：{{new Date(status.current.observedAt).toLocaleString()}}<br/>当前站点：{{status.current.currentStation||'站点之间或未知'}} · 坐标：{{status.current.x?.toFixed(3)??'未知'}}，{{status.current.y?.toFixed(3)??'未知'}}</p>
   <p>最近查询：{{new Date(status.current.updatedAt).toLocaleString()}}</p>
   <button :disabled="busy||!active||!status.enabled" @click="cancel">取消本次导航</button>
  </template><p v-else-if="!status||readError">尚未取得可靠的后台任务状态，请先查询原任务。</p><p v-else>后台当前没有已记录的测试任务。</p>
  <p>收到应答不代表到点完成。只有原任务完成、车辆静止且位置核对到终点，才显示完成。取消不是急停；异常时由现场人员使用车辆急停。断线、超时或后台重启后，只查询原任务，不自动重发。</p>
 </section>
</main>
</template>
<style scoped>
.nav-test{max-width:1100px;margin:24px auto;padding:0 20px;color:#d4e5f6}.nav-test section{background:#18263b;border:1px solid #344965;border-radius:8px;padding:24px;margin:20px 0}.nav-test h2{font-size:26px}.nav-test p{line-height:1.7;overflow-wrap:anywhere}.fields{display:flex;gap:24px;flex-wrap:wrap}.fields label{display:grid;gap:8px}.nav-test select{min-width:220px;padding:10px;background:#101b2e;border:1px solid #526d8d;border-radius:5px;color:inherit}.nav-test button{padding:11px 18px;background:#254868;color:#e5f7ff;border:1px solid #5c89ab;border-radius:5px;cursor:pointer;margin:8px 10px 8px 0}.nav-test button:disabled{opacity:.45;cursor:not-allowed}.nav-test .send{display:block;background:#126b8c;margin-top:18px}.preview{border-left:3px solid #53c8df;padding:8px 20px;margin-top:20px}.route{font-size:20px;font-weight:600}.error{color:#ffb2ab;background:#4b252c;padding:16px}.notice{color:#abd0df}
</style>
