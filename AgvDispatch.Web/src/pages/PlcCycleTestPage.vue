<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';

interface Child { kind:string;requestId:string;vendorTaskId:string|null;target:string;state:string;updatedAt:string }
interface Event { occurredAt:string;stage:string;message:string }
interface RecordRow {
  cycleId:string;requestId:string;vehicleId:string;stationId:string;pickupSite:string;waitingSite:string;dumpingSite:string;
  mapId:string;mapVersionId:string;robotMap:string;robotMapMd5:string;stage:string;message:string;operator:string;
  armedAt:string;updatedAt:string;children:Child[];events:Event[];simulationOnly:boolean;physicalExitVerified:boolean;
  pendingWrite:{stationId:string;variableName:string;value:boolean;nextStage:string;intentAt:string;acknowledged:boolean}|null;
}
interface Status {
  enabled:boolean;canArm:boolean;blockReason:string|null;vehicleId:string;stationId:string;pickupSite:string;
  waitingSite:string;dumpingSite:string;message:string;current:RecordRow|null;
}

const status=ref<Status|null>(null);
const error=ref('');
const busy=ref(false);
const reading=ref(false);
const connected=ref(false);
const pendingKey='plc-cycle-arm-request';
let pendingArm:Record<string,unknown>|null=null;
try{pendingArm=JSON.parse(sessionStorage.getItem(pendingKey)??'null');}catch{sessionStorage.removeItem(pendingKey);}
const siteClear=ref(false);
const emptyVehicle=ref(false);
const simulatorOnly=ref(false);
const autoRefresh=ref(true);
let timer:ReturnType<typeof setInterval>|undefined;
let disposed=false;

const stageLabels:Record<string,string>={
  ArmedAwaitCall:'已武装，等待满箱呼叫',
  AwaitCallCleared:'已应答，等待PLC撤呼叫',
  NavigatePickup:'准备前往取料站',
  AwaitNavigationToPickup:'车辆前往取料站',
  JackUpStart:'准备顶升',
  AwaitJackUp:'顶升中',
  MarkReturnResponsibility:'记录取箱与返箱责任',
  NavigateWaiting:'准备前往等待点',
  AwaitNavigationToWaiting:'车辆前往等待点',
  NotifyWaiting:'等待PLC Ready许可',
  AwaitDumperReady:'等待PLC Ready许可',
  AwaitWaitingCleared:'等待许可握手闭合',
  NavigateDumping:'准备进入倒料点',
  AwaitNavigationToDumping:'车辆进入倒料点',
  NotifyDumping:'等待PLC倒料完成Done',
  AwaitDone:'等待PLC倒料完成Done',
  NavigateReturn:'准备返回取料站',
  AwaitNavigationToReturn:'车辆返回取料站',
  ClearDumping:'关闭倒料周期',
  AwaitDoneCleared:'等待Done撤销',
  JackDownStart:'准备下降',
  AwaitJackDown:'下降中',
  ClearPicked:'闭合并清取箱通知',
  ClearPlaced:'闭合放箱通知',
  ClearReturnResponsibility:'清返箱责任',
  ClearCallAck:'清呼叫应答',
  Completed:'本轮完成',
  Disarmed:'已撤销武装',
  Hold:'已冻结，须现场核对'
};
const stageLabel=computed(()=>status.value?.current?stageLabels[status.value.current.stage]??status.value.current.stage:'尚未武装');
const plcHint=computed(()=>({
  ArmedAwaitCall:'当前工位满箱呼叫置1；看到呼叫响应后撤呼叫。信号地址以当前绑定点表为准。',
  AwaitCallCleared:'呼叫已响应：把满箱呼叫 （当前工位绑定信号） 清0，随后车辆开始取料位动作。',
  AwaitDumperReady:'车辆到等待点、等待位申请 （等待位申请）=1：将 Ready （Ready） 置1。',
  AwaitWaitingCleared:'等待位申请 （等待位申请） 已撤销：将 Ready （Ready） 清0，车辆才进入倒料点。',
  AwaitDone:'车辆到倒料点、倒料周期 （倒料周期）=1：现场确认本次模拟完成后，将 Done （Done） 置1。',
  AwaitDoneCleared:'车辆已返回取料站、倒料周期 （倒料周期）=0：将 Done （Done） 清0，随后自动下降。',
  Hold:'已冻结。先查询原任务并核对车辆、机构和PLC；不要通过重复置位尝试继续。',
  Completed:'本轮结束。下一轮需重新现场确认并武装；保持满箱呼叫、Ready、Done为0。'
} as Record<string,string>)[status.value?.current?.stage??'']??'当前等待车辆或后台反馈，请勿提前触发Ready、Done。');
const active=computed(()=>status.value?.current&&!['Completed','Disarmed','Hold'].includes(status.value.current.stage));
const canArm=computed(()=>connected.value&&!!status.value?.canArm&&siteClear.value&&emptyVehicle.value&&simulatorOnly.value&&!busy.value&&!reading.value);
function received(next:Status){
  if(disposed)return;
  status.value=next;connected.value=true;
  if(next.current?.requestId===pendingArm?.requestId){pendingArm=null;sessionStorage.removeItem(pendingKey);}
}

async function api<T>(suffix='',body?:unknown):Promise<T>{
  const response=await fetch('/api/plc-cycle-test'+suffix,{
    method:body===undefined?'GET':'POST',
    headers:body===undefined?{}:{'Content-Type':'application/json'},
    body:body===undefined?undefined:JSON.stringify(body),
    cache:'no-store',
    signal:AbortSignal.timeout(30000)
  });
  const data=await response.json();
  if(!response.ok)throw new Error(data.detail??data.error??'请求失败');
  return data;
}
async function refresh(){
  if(disposed||busy.value||reading.value)return;
  reading.value=true;
  try{received(await api<Status>());if(!disposed)error.value='';}
  catch(reason){if(!disposed){connected.value=false;error.value=String(reason);}}
  finally{reading.value=false;}
}
async function arm(){
  if(!canArm.value)return;
  busy.value=true;error.value='';
  try{
    pendingArm??={
      requestId:crypto.randomUUID().replaceAll('-',''),
      pickupSite:status.value?.pickupSite||'',
      waitingSite:status.value?.waitingSite||'',
      dumpingSite:status.value?.dumpingSite||'',
      siteClearConfirmed:siteClear.value,
      emptyVehicleConfirmed:emptyVehicle.value,
      simulatorOnlyConfirmed:simulatorOnly.value
    };
    sessionStorage.setItem(pendingKey,JSON.stringify(pendingArm));
    received(await api<Status>('/arm',pendingArm));
    siteClear.value=false;emptyVehicle.value=false;simulatorOnly.value=false;
  }catch(reason){connected.value=false;error.value=String(reason)+'；请先查询原任务。再次提交将沿用同一请求编号。';}
  finally{busy.value=false;}
}
async function stop(){
  const current=status.value?.current;
  if(!current||busy.value)return;
  const reason=window.prompt('请输入冻结原因（至少10字）');
  if(!reason)return;
  if(reason.trim().length<10){error.value='冻结原因至少10字';return;}
  busy.value=true;error.value='';
  try{status.value=await api<Status>('/stop',{cycleId:current.cycleId,reason});}
  catch(reason){error.value=String(reason);}
  finally{busy.value=false;}
}
async function reset(){
  const current=status.value?.current;
  if(!current||busy.value||reading.value||!connected.value)return;
  const reason=window.prompt('确认车辆已返回取料站、停稳下降，满箱呼叫/Ready/Done均为0。请输入核对依据（至少10字）。');
  if(!reason)return;
  if(reason.trim().length<10){error.value='核对依据至少10字';return;}
  busy.value=true;
  try{received(await api<Status>('/reset',{cycleId:current.cycleId,reason}));error.value='';}
  catch(reason){connected.value=false;error.value=String(reason)+'；查询原记录后再核对，不要重复复位。';}
  finally{busy.value=false;}
}
onMounted(()=>{
  void refresh();
  timer=setInterval(()=>{if(autoRefresh.value)void refresh();},1500);
});
onUnmounted(()=>{disposed=true;if(timer)clearInterval(timer);});
</script>

<template>
<main class="cycle-page">
  <header>
    <div>
      <h2>{{status?.stationId||'指定工位'}} 空载 PLC 联调</h2>
      <p>模拟PLC满箱呼叫触发后，由配置车辆完成取料位顶升、等待许可、倒料和返回取料位下降。当前路线见下方配置。</p>
    </div>
    <label class="refresh"><input v-model="autoRefresh" type="checkbox"/>自动刷新</label>
  </header>

  <p class="scope">仅限独立模拟PLC、无箱空载车辆和现场清空路线。一次武装只执行一轮；不创建正式28阶段任务，不证明实际倒料区退出。所有发送均核验最新状态，不确定时冻结且不重发。</p>
  <p v-if="error" class="error" role="alert">{{error}}</p>
  <button :disabled="busy||reading" @click="refresh">查询原记录 / 刷新状态</button>

  <section class="status-card">
    <div class="status-heading">
      <div><small>当前阶段</small><h3>{{stageLabel}}</h3></div>
      <span class="state" :class="{active,halt:status?.current?.stage==='Hold',done:status?.current?.stage==='Completed'}">{{status?.current?.stage||'NotArmed'}}</span>
    </div>
    <p>{{status?.current?.message||status?.message||status?.blockReason||'正在读取后台状态。'}}</p>
    <p v-if="status?.current" class="scope"><b>此时PLC怎么按：</b>{{plcHint}}</p>
    <dl>
      <dt>测试车辆</dt><dd>{{status?.vehicleId||'未配置'}}</dd>
      <dt>PLC工位</dt><dd>{{status?.stationId||'未配置'}}</dd>
      <dt>路线</dt><dd>{{status?.pickupSite||'未配置'}} → {{status?.waitingSite||'未配置'}} → {{status?.dumpingSite||'未配置'}} → {{status?.pickupSite||'未配置'}}</dd>
      <template v-if="status?.current">
        <dt>车端地图</dt><dd>{{status.current.robotMap}} · {{status.current.robotMapMd5}}</dd>
        <dt>联调编号</dt><dd>{{status.current.cycleId}}</dd>
        <dt>最后更新</dt><dd>{{new Date(status.current.updatedAt).toLocaleString()}}</dd>
      </template>
    </dl>
    <p v-if="status?.blockReason" class="warning">{{status.blockReason}}</p>
  </section>

  <section v-if="!active" class="arm-card">
    <h3>武装一轮</h3>
    <p>确认后请到PLC人工联调台，将 {{status?.stationId||'当前工位'}} 的 <b>满箱呼叫（当前点表绑定地址）</b> 从 0 置 1。后台会写回应答；看到应答后请将呼叫撤为 0。</p>
    <label><input v-model="siteClear" type="checkbox"/>现场已确认当前配置的取料位、等待位、倒料位及整条路线空闲，厂家测试速度已设置</label>
    <label><input v-model="emptyVehicle" type="checkbox"/>{{status?.vehicleId||'配置车辆'}} 无箱空载，顶升机构下降到位，现场人员在场</label>
    <label><input v-model="simulatorOnly" type="checkbox"/>当前连接的是独立模拟PLC，不是正式生产PLC</label>
    <button class="primary" :disabled="!canArm" @click="arm">武装本轮并等待PLC呼叫</button>
    <p v-if="!status?.enabled" class="warning">当前不是PLC空载联调启动模式，请按页面下方命令重启。</p>
  </section>

  <button v-if="active" class="stop" :disabled="busy" @click="stop">冻结本轮后续动作</button>
  <button v-if="status?.current&&['Hold','Completed','Disarmed'].includes(status.current.stage)" :disabled="busy||reading||!connected" @click="reset">现场核对后复位本轮</button>

  <section v-if="status?.current?.children?.length" class="timeline">
    <h3>车辆动作记录</h3>
    <article v-for="child in [...status.current.children].reverse()" :key="child.requestId">
      <b>{{child.kind==='Navigation'?'导航':'顶升'}} · {{child.target}}</b>
      <span>{{child.state}}</span>
      <p>{{child.vendorTaskId||child.requestId}} · {{new Date(child.updatedAt).toLocaleTimeString()}}</p>
    </article>
  </section>

  <section v-if="status?.current?.events?.length" class="events">
    <h3>阶段事件</h3>
    <details v-for="event in [...status.current.events].reverse().slice(0,12)" :key="event.occurredAt+event.stage">
      <summary><time>{{new Date(event.occurredAt).toLocaleTimeString()}}</time><b>{{stageLabels[event.stage]??event.stage}}</b><span>{{event.message}}</span></summary>
    </details>
  </section>

  <section class="commands">
    <h3>启动模式</h3>
    <p>停止当前5199实例后，在项目根目录执行。该入口强制关闭正式建单、调度、命令队列和新握手，只开放单车导航与模拟PLC空载联调。</p>
    <pre>&amp; '.\发布包\停止最新版Web.cmd'
& '.\发布包\启动最新版Web.cmd' --plc-cycle-test-vehicle &lt;车辆编号&gt; --plc-cycle-test-station &lt;工位编号&gt; --no-browser</pre>
  </section>
</main>
</template>

<style scoped>
.cycle-page{max-width:1100px;margin:24px auto 90px;padding:0 20px;color:#dceaf7}.cycle-page header{display:flex;justify-content:space-between;gap:24px;align-items:flex-start}.cycle-page h2{margin:0;font-size:26px}.cycle-page h3{margin:0 0 12px}.cycle-page section{padding:20px;margin:18px 0;background:#17263b;border:1px solid #304b69;border-radius:9px}.cycle-page p{line-height:1.7}.scope,.warning{border-left:3px solid #d8a54c;padding:12px 16px;background:#342d21}.error{border-left:3px solid #ee6d75;padding:12px 16px;background:#44262d;color:#ffc2c6}.status-heading{display:flex;justify-content:space-between;align-items:center}.status-heading small{color:#91abc2}.status-heading h3{font-size:23px;margin-top:4px}.state{font-family:ui-monospace,monospace;background:#0d1928;padding:7px 11px;border-radius:4px}.state.active{color:#6ee7f4}.state.halt{color:#ff9b9f}.state.done{color:#55e3b4}.cycle-page dl{display:grid;grid-template-columns:130px minmax(0,1fr);gap:8px}.cycle-page dt{color:#91abc2}.cycle-page dd{margin:0;overflow-wrap:anywhere}.arm-card label{display:flex;gap:10px;align-items:flex-start;padding:10px 0;line-height:1.5}.arm-card input{margin-top:4px}.cycle-page button{padding:11px 18px;border:1px solid #527fa2;border-radius:5px;background:#213f5c;color:#f1f9ff;cursor:pointer}.cycle-page button:disabled{opacity:.42;cursor:not-allowed}.cycle-page .primary{display:block;margin-top:16px;background:#127b91}.cycle-page .stop{background:#6e3039;border-color:#b66670}.timeline article{display:grid;grid-template-columns:1fr auto;gap:8px;border-top:1px solid #294058;padding:12px 0}.timeline article:first-of-type{border-top:0}.timeline p{grid-column:1/-1;margin:0;color:#94abc0}.timeline span{color:#6ee7f4}.events details{border-top:1px solid #294058;padding:9px 0}.events summary{display:grid;grid-template-columns:90px 210px 1fr;gap:10px;cursor:pointer}.events time{color:#90a9bf}.events summary span{color:#bfd0df}.commands pre{white-space:pre-wrap;word-break:break-all;background:#0c1726;border:1px solid #263b52;padding:14px;border-radius:6px;color:#b8dff0}.refresh{white-space:nowrap;color:#a9bfd1}@media(max-width:760px){.cycle-page header{display:block}.cycle-page dl{grid-template-columns:1fr}.events summary{grid-template-columns:1fr}}
</style>
