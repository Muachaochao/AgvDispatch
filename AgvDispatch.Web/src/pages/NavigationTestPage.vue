<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useDispatchStore } from '../stores/dispatch';
const dispatch=useDispatchStore();
const mapReloadNeeded=ref(false),mapNotice=ref('');
interface RecordRow {taskId:string;vendorTaskId:string;vehicleId:string;sourceSite:string;targetSite:string;robotMap:string;robotMapMd5:string;state:string;message:string;updatedAt:string;route:string[];currentStation:string|null;x:number|null;y:number|null;observedAt:string|null}
interface Status {ready:boolean;blockReason:string|null;enabled:boolean;vehicleId:string;current:RecordRow|null;message:string}
interface Preview {previewId:string;expiresAt:string;vehicleId:string;sourceSite:string;targetSite:string;robotMap:string;robotMapMd5:string;route:string[]}
interface Stations {vehicleId:string;robotMap:string;robotMapMd5:string;stations:{id:string;x:number;y:number}[];suggestedSourceSite:string|null;capturedAt:string}
interface JackStatus {ready:boolean;blockReason:string|null;vehicleId:string;telemetry:{state:number;enabled:boolean;emergency:boolean;errorCode:number;hasLoad:boolean;height:number|null;observedAt:string}|null;current:{requestId:string;action:string;state:string;message:string;updatedAt:string}|null}
interface ForkStatus {supported:boolean;ready:boolean;blockReason:string|null;vehicleId:string;telemetry:{upperLimit:boolean;lowerLimit:boolean;pumpOn:boolean;valveOn:boolean;observedAt:string}|null;current:{requestId:string;action:string;state:string;message:string}|null}
const fork=ref<ForkStatus|null>(null),forkError=ref(''),forkAreaClear=ref(false),forkEmpty=ref(false),forkStopBusy=ref(false);
const forkStateLabels:Record<string,string>={Accepted:'已受理，等待反馈',Completed:'已到限位并关闭输出',ObservedTarget:'已观测目标限位',Stopped:'输出已关闭，请现场确认停稳',Rejected:'已拒绝',Uncertain:'结果不确定，禁止重发'};
const jack=ref<JackStatus|null>(null),jackError=ref('');
const jackLabels:Record<number,string>={0:'上升中',1:'上升到位',2:'下降中',3:'下降到位',4:'停止',255:'执行失败'};
const status=ref<Status|null>(null),catalog=ref<Stations|null>(null),preview=ref<Preview|null>(null);
const source=ref(''),target=ref(''),confirmed=ref(false),busy=ref(false),error=ref(''),readError=ref(''),now=ref(Date.now());
const labels:Record<string,string>={Accepted:'已接受，等待执行',Waiting:'等待中',Running:'运行中',Suspended:'暂停',Completed:'已完成并核对到点',Failed:'失败',Cancelled:'已取消',CancelRequested:'取消待核实',Uncertain:'结果不确定，禁止重发'};
const active=computed(()=>!!status.value?.current&&!['Completed','Failed','Cancelled'].includes(status.value.current.state));
const valid=computed(()=>!!preview.value&&Date.parse(preview.value.expiresAt)>now.value);
const canInspect=computed(()=>!!catalog.value&&!!source.value&&!!target.value&&source.value!==target.value&&!busy.value&&!active.value&&!readError.value&&status.value?.ready);
const canSubmit=computed(()=>valid.value&&confirmed.value&&!busy.value&&!active.value&&!readError.value&&status.value?.ready);
let autoReadAttempted=false;
let disposed=false,timer:ReturnType<typeof setInterval>|undefined;
async function api<T>(suffix='',body?:unknown):Promise<T>{const r=await fetch('/api/navigation-test'+suffix,{method:body===undefined?'GET':'POST',headers:body===undefined?{}:{'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(30000)});const data=await r.json();if(!r.ok)throw new Error(data.detail??data.error??'请求失败');return data;}
function invalidate(){preview.value=null;confirmed.value=false;}
async function refresh(){if(busy.value||disposed)return;busy.value=true;let reload=false;try{const s=await api<Status>();if(!disposed){const wasEnabled=status.value?.enabled;if(s.ready&&!status.value?.ready)autoReadAttempted=false;status.value=s;readError.value='';if(!s.ready)invalidate();if(!s.enabled&&wasEnabled){catalog.value=null;source.value='';target.value='';}reload=s.ready&&!active.value&&!autoReadAttempted&&(!catalog.value||mapReloadNeeded.value);try{fork.value=await api<ForkStatus>('/fork');forkError.value='';}catch(e){forkError.value=String(e);fork.value=null;}if(!fork.value?.supported){try{jack.value=await api<JackStatus>('/jack');jackError.value='';}catch(e){jackError.value=String(e);jack.value=null;}}}}catch(e){if(!disposed){readError.value=String(e);invalidate();}}finally{busy.value=false;}if(reload&&!disposed)await loadStations();}
async function loadStations(){if(busy.value||active.value||disposed)return;autoReadAttempted=true;invalidate();busy.value=true;error.value='';const previous=catalog.value;catalog.value=null;const mapIdentity=runtimeMapIdentity.value;try{const c=await api<Stations>('/stations');if(disposed||mapIdentity!==runtimeMapIdentity.value)return;const same=previous?.vehicleId===c.vehicleId&&previous.robotMap===c.robotMap&&previous.robotMapMd5===c.robotMapMd5;catalog.value=c;source.value=c.suggestedSourceSite??(same&&c.stations.some(s=>s.id===source.value)?source.value:'');target.value=same&&c.stations.some(s=>s.id===target.value)?target.value:'';mapReloadNeeded.value=false;mapNotice.value='已读取当前车端地图，请重新选择终点并核验路线。';}catch(e){if(!disposed)error.value=String(e);}finally{busy.value=false;}}
async function inspect(){if(!canInspect.value||!catalog.value)return;invalidate();busy.value=true;error.value='';try{preview.value=await api<Preview>('/preview',{vehicleId:catalog.value.vehicleId,sourceSite:source.value,targetSite:target.value,expectedRobotMap:catalog.value.robotMap,expectedRobotMapMd5:catalog.value.robotMapMd5});}catch(e){error.value=String(e);}finally{busy.value=false;}}
async function submit(){if(!canSubmit.value||!preview.value)return;const id=preview.value.previewId;invalidate();busy.value=true;error.value='';try{await api('/submit',{previewId:id,siteClearConfirmed:true});}catch(e){error.value=String(e)+'；请先查询原任务，确认未发送并解除阻塞后再重新核验。';}finally{busy.value=false;await refresh();}}
async function submitFork(action:'Up'|'Down'|'Stop'){
 if(action==='Stop'){if(forkStopBusy.value||!fork.value?.current)return;forkStopBusy.value=true;}
 else {if(busy.value||active.value||readError.value||!status.value?.ready||!fork.value?.ready||!forkAreaClear.value||!forkEmpty.value||forkError.value)return;busy.value=true;}
 forkError.value='';invalidate();
 try{await api('/fork',{requestId:crypto.randomUUID(),action,areaClearConfirmed:forkAreaClear.value,emptyLoadConfirmed:forkEmpty.value});}
 catch(e){forkError.value=String(e)+'；请查询原动作，禁止反复发送。';}
 finally{if(action==='Stop')forkStopBusy.value=false;else busy.value=false;await refresh();}
}
async function submitJack(action:'Up'|'Down'){
 if(busy.value||active.value||!status.value?.ready||!jack.value?.ready||jackError.value)return;
 busy.value=true;error.value='';jackError.value='';const requestId=crypto.randomUUID();invalidate();
 try{await api('/jack',{requestId,action,areaClearConfirmed:true,emptyLoadConfirmed:action==='Up'});}
 catch(e){jackError.value=String(e)+'；请先查询顶升状态，不能重复发送。';}
 finally{busy.value=false;await refresh();}
}
async function cancel(){const r=status.value?.current;if(!r)return;busy.value=true;error.value='';try{await api('/cancel',{taskId:r.taskId});}catch(e){error.value=String(e);}finally{busy.value=false;await refresh();}}
watch([source,target],invalidate);
const runtimeMapIdentity=computed(()=>{const m=dispatch.snapshot?.map;return m?`${m.id}/${m.version}`:'';});
watch(runtimeMapIdentity,(next,previous)=>{if(!next||!previous||next===previous)return;invalidate();catalog.value=null;source.value='';target.value='';mapReloadNeeded.value=true;autoReadAttempted=false;mapNotice.value='运行地图已变化，旧起终点和路线核验已清除；切换完成且测试模式就绪后自动读取车端站点。';});
onMounted(async()=>{await refresh();if(!disposed)timer=setInterval(()=>{now.value=Date.now();void refresh();},3000);});
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
  <div v-if="status&&!status.enabled" class="preview">
   <h3>单车测试模式未启用</h3>
   <p>当前后台未开放单车动作测试；这与地图是否发布、激活是两个独立状态。普通启动或升级重启后不会自动恢复发送许可。</p>
   <p>恢复步骤：确认没有执行中的动作 → 使用“停止最新版Web.cmd”停止当前实例 → 使用“启动最新版Web.cmd --navigation-test-vehicle AGV-01”启动测试模式 → 回到本页查询状态。实际车辆不同请替换车辆编号。</p>
   <p>模式恢复后本页自动读取车端站点；每次导航仍需重新选择终点、核验路线并确认发送。</p>
  </div>
  <p v-if="mapNotice" role="status" class="notice">{{mapNotice}}</p>
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
 <section v-if="fork?.supported" class="jack-test">
  <h3>地牛货叉升降 · {{fork.vehicleId}}</h3>
  <p>上升至上限位、下降至下限位，不提供任意高度设置。与单次导航分开执行，发送应答不代表到位。</p>
  <button :disabled="busy" @click="refresh">查询原升降及限位</button>
  <p v-if="forkError" role="alert" class="error">{{forkError}}</p>
  <p v-if="fork.blockReason" class="notice">当前不能升降：{{fork.blockReason}}</p>
  <div v-if="fork.telemetry" class="jack-status">
   <strong>{{fork.telemetry.upperLimit?'上限位到位':fork.telemetry.lowerLimit?'下限位到位':'两个限位均未触发，机构在中间位置'}}</strong>
   <p>上限位 DI6：{{fork.telemetry.upperLimit?'1':'0'}} · 下限位 DI3：{{fork.telemetry.lowerLimit?'1':'0'}}<br/>上升输出：{{fork.telemetry.pumpOn?'开启':'关闭'}} · 下降输出：{{fork.telemetry.valveOn?'开启':'关闭'}}<br/>观测时间：{{new Date(fork.telemetry.observedAt).toLocaleString()}}</p>
  </div>
  <p v-else>限位反馈不可用；请查询并现场核对。</p>
  <label class="jack-confirm"><input v-model="forkAreaClear" type="checkbox"/>已确认货叉升降范围空闲、现场可操作急停</label>
  <label class="jack-confirm"><input v-model="forkEmpty" type="checkbox"/>已确认本次为空载测试</label>
  <button :disabled="busy||active||!!readError||!status?.ready||!fork.ready||!forkAreaClear||!forkEmpty||!!forkError||fork.telemetry?.upperLimit" @click="submitFork('Up')">货叉上升</button>
  <button :disabled="busy||active||!!readError||!status?.ready||!fork.ready||!forkAreaClear||!forkEmpty||!!forkError||fork.telemetry?.lowerLimit" @click="submitFork('Down')">货叉下降</button>
  <button class="fork-stop" style="background:#633524;border-color:#dc9467" :disabled="forkStopBusy||!fork.current" @click="submitFork('Stop')">停止货叉</button>
  <p v-if="fork.current">最近动作：{{fork.current.action==='Up'?'上升':fork.current.action==='Down'?'下降':'停止'}} · {{forkStateLabels[fork.current.state]??fork.current.state}}<br/>{{fork.current.message}}<br/>请求编号：{{fork.current.requestId}}</p>
  <p class="notice">停止按钮只停止货叉，不是车辆急停。停在中间位置时禁止导航，先核对并下降到位。断线、超时或重启后仅查询，不自动重发。</p>
 </section>
 <p v-else-if="forkError" class="notice">货叉测试接口尚不可用：{{forkError}}</p>
 <section v-if="!fork?.supported" class="jack-test">
  <h3>原地顶升动作测试</h3>
  <p>独立控制当前测试车的顶升机构，不行驶到其他站点。上升、下降与导航互斥；点击按钮即发送本次调试动作，不要求恢复批次或载荷条件。</p>
  <button :disabled="busy" @click="refresh">查询顶升状态</button>
  <p v-if="jackError" role="alert" class="error">{{jackError}}</p>
  <p v-if="jack?.blockReason" class="notice">当前不能操作：{{jack.blockReason}}</p>
  <div v-if="jack?.telemetry" class="jack-status">
   <strong>{{jackLabels[jack.telemetry.state]??'未知状态'}} · 原始码 {{jack.telemetry.state}}</strong>
   <p>当前高度：{{jack.telemetry.height==null?'未提供':(jack.telemetry.height*1000).toFixed(1)+' mm'}} · {{jack.telemetry.enabled?'机构已启用':'机构未启用'}} · {{jack.telemetry.hasLoad?'报告有料':'报告无料'}}<br/>故障码：{{jack.telemetry.errorCode}} · {{jack.telemetry.emergency?'顶升急停':'无顶升急停'}}<br/>观测时间：{{new Date(jack.telemetry.observedAt).toLocaleString()}}</p>
  </div>
  <p v-else>尚未取得可靠顶升反馈；未配置或不支持此接口的车辆不能通过此入口操作。</p>
  <div><button :disabled="busy||active||!status?.ready||!jack?.ready||!!jackError||jack?.telemetry?.state===1" @click="submitJack('Up')">顶升上升</button><button :disabled="busy||active||!status?.ready||!jack?.ready||!!jackError||jack?.telemetry?.state===3" @click="submitJack('Down')">顶升下降</button></div>
  <p v-if="jack?.current">最近动作：{{jack.current.action==='Up'?'上升':'下降'}} · {{jack.current.state}}<br/>{{jack.current.message}}<br/>请求编号：{{jack.current.requestId}}</p>
  <p class="notice">发送应答不代表到位；以车端顶升反馈及现场观察核对。连接中断或重启后不自动重发动作。</p>
 </section>
 <section>
  <h3>{{status?.current&&!active?'最近一次导航记录（已结束）':'本次任务与执行状态'}}</h3><button :disabled="busy" @click="refresh">查询原任务</button>
  <template v-if="status?.current"><h3>{{labels[status.current.state]||status.current.state}}</h3>
   <p class="notice">记录所属地图：{{status.current.robotMap}} · {{new Date(status.current.updatedAt).toLocaleString()}}。{{!active?'这是已结束的历史记录，不表示当前地图已经核验，也不代表当前允许发送。':''}}</p>
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
.nav-test{max-width:1100px;margin:24px auto;padding:0 20px;color:#d4e5f6}.nav-test section{background:#18263b;border:1px solid #344965;border-radius:8px;padding:24px;margin:20px 0}.nav-test h2{font-size:26px}.nav-test p{line-height:1.7;overflow-wrap:anywhere}.fields{display:flex;gap:24px;flex-wrap:wrap}.fields label{display:grid;gap:8px}.nav-test select{min-width:220px;padding:10px;background:#101b2e;border:1px solid #526d8d;border-radius:5px;color:inherit}.nav-test button{padding:11px 18px;background:#254868;color:#e5f7ff;border:1px solid #5c89ab;border-radius:5px;cursor:pointer;margin:8px 10px 8px 0}.nav-test button:disabled{opacity:.45;cursor:not-allowed}.nav-test .send{display:block;background:#126b8c;margin-top:18px}.preview{border-left:3px solid #53c8df;padding:8px 20px;margin-top:20px}.route{font-size:20px;font-weight:600}.error{color:#ffb2ab;background:#4b252c;padding:16px}.notice{color:#abd0df}.jack-status{padding:12px 16px;margin:10px 0;background:#102238;border-left:3px solid #53c8df}.jack-confirm{display:flex;gap:8px;align-items:flex-start;line-height:1.8;margin:16px 0}.jack-confirm input{margin-top:5px}
</style>
