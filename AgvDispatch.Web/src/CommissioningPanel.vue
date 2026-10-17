<script setup lang="ts">
import { computed,nextTick,onMounted,onUnmounted,ref,watch } from 'vue';
import type { Snapshot,RoutePreview,CreationOptions } from './types';
import { commissioningCurrent,checkLabels,type CommissioningReport } from './commissioning';
import { frontendReleaseId,releaseAgreement,type AdapterIdentity } from './release-state';
import { isFresh } from './state';
const props=defineProps<{snapshot:Snapshot|null;adapter:AdapterIdentity|null}>();
const emit=defineEmits<{navigate:[target:string]}>();
const report=ref<CommissioningReport|null>(null),station=ref(''),vehicle=ref(''),busy=ref(false),error=ref('');
const choices=ref<CreationOptions|null>(null);
const help=ref(''),onlyIssues=ref(false),route=ref<RoutePreview|null>(null),routeTime=ref('');
const now=ref(Date.now());let timer:ReturnType<typeof setInterval>,epoch=0,controller:AbortController|undefined;
const stationInput=ref<HTMLSelectElement>();
const releaseCheck=computed(()=>releaseAgreement(frontendReleaseId,props.adapter,props.snapshot&&isFresh(props.snapshot,now.value)?props.snapshot.status?.execution?.informationalVersion:undefined));
const current=computed(()=>commissioningCurrent(report.value,props.snapshot,now.value)&&report.value?.stationId===station.value&&report.value?.vehicleId===vehicle.value);
const rows=computed(()=>report.value?.checks.filter(c=>!onlyIssues.value||['Blocked','Unknown'].includes(c.status))??[]);
const totals=computed(()=>({blocked:report.value?.checks.filter(c=>c.status==='Blocked').length??0,unknown:report.value?.checks.filter(c=>c.status==='Unknown').length??0}));
const routeCurrent=computed(()=>current.value&&!!routeTime.value&&now.value-Date.parse(routeTime.value)<=15000);
const guidance:Record<string,{title:string;steps:string[]}>= {
  service:{title:'后台运行配置与部署',steps:['确认主Service已部署本次版本；WebHost是接入层，不会启动主服务。','WPF系统设置维护通信配置，保存后按提示受控重启后台；任务创建、调度、步骤、Outbox及设备读写开关仍由主服务配置管理。','重启前核对在途任务与现场车辆；重启后重新检查，并完成原恢复批次。此页不修改开关或重启进程。']},
  'task-settings':{title:'任务参数与站点映射',steps:['在WPF任务中心的任务参数配置中核对取货工位、等待站、倒料站和各段动作。','九步预览会标明缺失点位和动作配置。站点必须能对应到当前运行地图及厂家站点。','保存参数后返回此页刷新；参数修订变化后需重新核对路线。']},
  'map-binding':{title:'车辆地图与坐标绑定',steps:['WPF系统设置 → Robokit地图绑定：选择车辆、本地地图版本，填写已核定的车端地图/MD5及坐标核对依据。','WPF AGV管理 → Robokit车端地图、站点与版本：在通信开启后只读核验所选车辆。','保存绑定后按后台提示重启；核验记录不能替代当前状态，检查页中的绑定通过也不是车端实时一致性证明。']},
  recovery:{title:'现场恢复处理',steps:['WPF现场恢复，或Web任务详情底部“现场恢复核对入口”，查看原批次、原车辆、原命令及现场依据。','Web恢复写操作默认关闭。建立核对会冻结全场后续执行，但不会自动停止车辆已经接受的动作。','按照原节点续接或核对取消流程处理；不要通过删除任务、手工清位或强制解锁来代替现场核对。']},
  contract:{title:'握手契约与启用范围',steps:['当前模式和配置版本见本页。新契约要求PLC发送方复位自己的位，Ready消费、本轮Done基线及返程后撤销倒料通知必须一致。','sender-owned-return-confirmed-v1默认不启用；配置版本相同不代表PLC程序已完成联调。','完整时序见主工程docs/architecture/持久化握手与Web联调闭环-20261017.md；旧任务不自动迁入，新任务也不能静默降级。']}
};
async function request<T>(path:string,payload:unknown,signal:AbortSignal):Promise<T>{
 const response=await fetch(path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload),signal});
 if(!response.ok){const data=await response.json().catch(()=>({}));throw new Error(data.detail??data.error??`请求失败 ${response.status}`);}return response.json();
}
async function refresh(){
 controller?.abort();const mine=++epoch;controller=new AbortController();const timeout=setTimeout(()=>controller?.abort(),35000);
 busy.value=true;error.value='';route.value=null;routeTime.value='';report.value=null;
 try{const result=await request<CommissioningReport>('/api/commissioning',{stationId:station.value,vehicleId:vehicle.value},controller.signal);if(mine===epoch){report.value=result;choices.value=result.options;}}
 catch(ex){if(mine===epoch)error.value=`无法取得当前后台联调检查：${String(ex)}。请核对主服务版本和连接。`;}
 finally{clearTimeout(timeout);if(mine===epoch)busy.value=false;}
}
async function verifyRoute(){
 if(!current.value||!station.value||!vehicle.value||!report.value)return;
 const original=report.value;const mine=++epoch;controller?.abort();controller=new AbortController();const signal=controller.signal;
 busy.value=true;error.value='';route.value=null;routeTime.value='';const timeout=setTimeout(()=>controller?.abort(),35000);
 try{const result=await request<RoutePreview>('/api/route-preview',{stationId:station.value,vehicleId:vehicle.value,mapId:original.mapId,mapVersionId:original.mapVersion,parameterRevision:original.parameterRevision},signal);
   if(mine===epoch&&report.value===original){route.value=result;routeTime.value=new Date().toISOString();}}
 catch(ex){if(mine===epoch)error.value=String(ex);}finally{clearTimeout(timeout);if(mine===epoch)busy.value=false;}
}
function act(target:string){
 if(target==='selection'){stationInput.value?.focus();return;}
 if(target==='signals'){document.getElementById('commissioning-signals')?.scrollIntoView({behavior:'smooth',block:'start'});return;}
 if(target==='route'){void verifyRoute();return;}
 if(target==='map'||target==='vehicles'||target==='vehicle-status'){emit('navigate',target);return;}
 help.value=target;
 void nextTick(()=>document.getElementById('commissioning-help')?.scrollIntoView({behavior:'smooth',block:'start'}));
}
watch([station,vehicle],()=>{route.value=null;routeTime.value='';void refresh();});
watch([()=>props.snapshot?.connected,()=>props.snapshot?.map?.id,()=>props.snapshot?.map?.version,()=>props.snapshot?.status?.service?.startedAt,()=>props.snapshot?.status?.execution?.buildId],()=>{
 ++epoch;controller?.abort();busy.value=false;route.value=null;routeTime.value='';
});
onMounted(()=>{void refresh();timer=setInterval(()=>now.value=Date.now(),1000);});
onUnmounted(()=>{++epoch;controller?.abort();clearInterval(timer);});
</script>

<template>
 <section class="commissioning-page control-panel">
  <header class="commissioning-heading"><div><h2>联调检查</h2><p>首次配置 → 只读检查 → 四段路线核验 → 单车任务 → 异常核对</p></div><div><button @click="act('service')">后台连接与部署说明</button> <button :disabled="busy" @click="refresh">{{busy?'读取后台中…':'重新检查'}}</button></div></header>
  <p class="commissioning-note">检查结果是后台当前观察，实际提交与执行会重新核验。本页不启用设备、不创建任务、不复位PLC、不自动查询车端。</p>
  <div class="commissioning-selection"><label>检查工位<select ref="stationInput" v-model="station"><option value="">请选择工位</option><option v-for="s in choices?.stations??[]" :key="s.stationId" :value="s.stationId">{{s.stationId}} · {{s.pickupSite}}</option></select></label><label>检查车辆<select v-model="vehicle"><option value="">请选择车辆</option><option v-for="v in choices?.vehicles??[]" :key="v.vehicleId" :value="v.vehicleId">{{v.label}}</option></select></label><button :disabled="busy||!current||!station||!vehicle" @click="verifyRoute">只读核对四段路线</button><button @click="emit('navigate','tasks')">进入任务列表</button></div>
  <p v-if="error" class="commissioning-error" role="alert">{{error}}</p>
  <section class="commissioning-note"><b>发布批次核对 · {{checkLabels[releaseCheck.status]}}</b><p>页面 {{frontendReleaseId}} · WebHost {{adapter?.informationalVersion??'未返回'}} · 主服务 {{snapshot?.status?.execution?.informationalVersion??'未返回'}}</p><p>{{releaseCheck.reason}}</p><p v-if="!current">这是构建身份信息；当前执行条件仍需重新检查。</p></section>
  <template v-if="report">
   <div class="commissioning-runtime"><div><small>主服务版本 / 构建</small><b>{{report.serviceVersion}} · {{report.runtime.buildId.slice(0,12)}}</b><span>启动 {{new Date(report.serviceStartedAt).toLocaleString()}}</span></div><div><small>执行模式</small><b>{{report.runtime.handshakeEnabled?'持久化新握手':'原执行器＋变化观测'}}</b><span>配置契约 {{report.runtime.configuredContract||'未配置'}}</span></div><div><small>运行地图 / 参数修订</small><b>{{report.mapId||'未激活'}} / {{report.mapVersion||'—'}}</b><span>{{report.parameterRevision}}</span></div></div>
   <details class="commissioning-build"><summary>构建身份及软件支持契约</summary><p>{{report.runtime.informationalVersion}}</p><code>{{report.runtime.buildId}}</code><p>支持 {{report.runtime.supportedContract}}；匹配版本不等于现场已验收。</p></details>
   <div class="commissioning-results"><b :class="current?'':'commissioning-stale'">{{current?'当前观察窗口':'历史观察 / 身份已变化，请重新检查'}}</b><span>{{new Date(report.generatedAt).toLocaleString()}} · {{totals.blocked}} 项阻塞 · {{totals.unknown}} 项待核对</span><label><input v-model="onlyIssues" type="checkbox" />只看阻塞和待核对</label></div>
   <div class="commissioning-table"><table><thead><tr><th>分组 / 检查项</th><th>结果</th><th>依据与处理</th><th>入口</th></tr></thead><tbody><tr v-for="row in rows" :key="row.id"><td><small>{{row.group}}</small><b>{{row.title}}</b></td><td><span class="check-status" :class="current?row.status:'Unknown'">{{current?checkLabels[row.status]:'历史观察'}}</span></td><td><template v-if="row.reason.length>220">{{row.reason.slice(0,180)}}…<details><summary>展开全部检查依据</summary><p>{{row.reason}}</p></details></template><template v-else>{{row.reason}}</template></td><td><button :disabled="row.target==='route'&&(busy||!current||!station||!vehicle)" @click="act(row.target)">{{row.action}}</button></td></tr></tbody></table></div>
   <section v-if="route" class="commissioning-route"><h3>{{routeCurrent?route.verified?'本次车端路线观察核验通过':'本次路线核验未通过 / 未完成':'历史四段核验，需重新核对'}}</h3><p>{{route.message}}</p><p v-if="!routeCurrent">后台身份、地图或观察窗口失效，不能沿用通过结果。</p><p v-for="leg in route.legs" :key="leg.sequence">{{leg.sequence}}. {{leg.name}}：{{leg.vendorPath||leg.logicalPath}} · {{leg.message}}</p><p>此核验不申请路权、不发送导航；不代表已完成整趟现场验收。</p></section>
   <section id="commissioning-signals"><h3>本工位13位信号</h3><p>“观察通过”只检查点位及数据有效性，电平是否符合当前业务阶段请看任务详情。调度输出回读不代表PLC业务接收。</p><p v-if="!report.signals.length">选择工位后读取原点表及当前采样；缺失不会补成0。</p><div v-else class="commissioning-table"><table><thead><tr><th>工位 / 信号</th><th>写入方 / 地址</th><th>当前值</th><th>质量 / 采集时间</th><th>结果 / 说明</th></tr></thead><tbody><tr v-for="s in report.signals" :key="s.stationId+'/'+s.variableName"><td><b>{{s.name}}</b><small>{{s.stationId}} / {{s.variableName}}</small></td><td>{{s.writer}}<small>{{s.address}}</small></td><td>{{s.value===null?'未知':s.value?'1':'0'}}</td><td>{{s.quality}}<small>{{s.observedAt?new Date(s.observedAt).toLocaleString():'未采集'}}</small></td><td><span class="check-status" :class="current?s.status:'Unknown'">{{current?checkLabels[s.status]:'历史观察'}}</span><small>{{s.reason}}</small></td></tr></tbody></table></div></section>
  </template>
  <aside v-if="guidance[help]" id="commissioning-help" class="commissioning-help" role="region" :aria-label="guidance[help]!.title"><button class="help-close" @click="help=''">收起</button><h3>{{guidance[help]!.title}}</h3><ol><li v-for="text in guidance[help]!.steps" :key="text">{{text}}</li></ol><button v-if="help==='recovery'" @click="emit('navigate','tasks')">前往任务列表选择原任务</button></aside>
 </section>
</template>

<style scoped>
.commissioning-page{margin:16px;padding:24px;line-height:1.7;min-height:75vh;color:#c2d3e7}.commissioning-heading,.commissioning-selection,.commissioning-results{display:flex;align-items:center;gap:16px;flex-wrap:wrap}.commissioning-heading{justify-content:space-between}.commissioning-heading h2{font-size:20px}.commissioning-heading p,.commissioning-note,small{color:#94aac4}.commissioning-note{margin:16px 0;padding:12px 16px;background:#101c30;border-left:3px solid #3fbdd3}.commissioning-selection label{display:grid;gap:6px;min-width:210px}.commissioning-selection{align-items:end}.commissioning-selection select{max-width:340px}.commissioning-runtime{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:14px;margin:22px 0 12px}.commissioning-runtime>div{padding:16px;background:#12213a;border:1px solid #2a4262;overflow-wrap:anywhere}.commissioning-runtime b,.commissioning-runtime span,.commissioning-runtime small{display:block}.commissioning-runtime span{font-size:11px;color:#9eb2cd;margin-top:8px}.commissioning-build{font-size:12px;overflow-wrap:anywhere}.commissioning-results{margin:24px 0 12px}.commissioning-results label{margin-left:auto}.commissioning-stale,.commissioning-error{color:#efb968}.commissioning-error{padding:15px;border:1px solid #9d673b;margin-top:14px}.commissioning-table{overflow-x:auto;margin:14px 0 26px}.commissioning-table table{width:100%;border-collapse:collapse;min-width:780px}.commissioning-table th{text-align:left;color:#89b0ce;font-size:12px;background:#11233c}.commissioning-table td,.commissioning-table th{padding:13px 14px;border-bottom:1px solid #263b56;vertical-align:top}.commissioning-table td{font-size:12px}.commissioning-table td:first-child{width:21%}.commissioning-table small,.commissioning-table b{display:block}.commissioning-table small{font-size:11px;overflow-wrap:anywhere}.commissioning-table button{white-space:nowrap}.check-status{font-size:11px;white-space:nowrap}.Pass{color:#46d5b0}.Blocked{color:#fa9b7d}.Unknown{color:#e7bd6b}.Notice{color:#94b9d7}.commissioning-route,.commissioning-help{border:1px solid #3c5876;background:#13243b;padding:20px;margin:20px 0}.commissioning-help li{margin:12px 0}.commissioning-help{position:relative}.help-close{float:right}.commissioning-route h3,#commissioning-signals h3,.commissioning-help h3{font-size:15px}#commissioning-signals>p{font-size:12px;color:#93a9c3;margin-top:10px}@media(max-width:900px){.commissioning-runtime{grid-template-columns:1fr}.commissioning-page{margin:8px;padding:16px}.commissioning-selection label{min-width:170px}.commissioning-results label{margin-left:0}}
</style>
