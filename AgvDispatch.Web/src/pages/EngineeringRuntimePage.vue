<script setup lang="ts">
import {computed,onMounted,onUnmounted,ref,watch} from 'vue';
import {useRoute} from 'vue-router';
import {useDispatchStore} from '../stores/dispatch';
import {taskStepName} from '../task-step-labels';
import {effectiveWaitingSites} from '../waiting-sites';
import {industrialText, vehicleActionLabel} from '../operator-labels';
const props=defineProps<{embedded?:boolean;initialVersion?:string}>();
const store=useDispatchStore(),route=useRoute();
const versions=ref<any[]>([]),version=ref(String(route.query.version??props.initialVersion??'')),preview=ref<any>(null),state=ref<any>(null);
const busy=ref(false),message=ref(''),confirmation=ref(''),reviewed=ref(false),pending=ref<any>(null);
const key='engineering-runtime-request-v1';let epoch=0;
try{const raw=sessionStorage.getItem(key);if(raw)pending.value=JSON.parse(raw);}catch{message.value='原请求记录无法读取，请查询当前应用状态后处理。';}
const context=computed(()=>[store.demoMode,store.adapterIdentity?.sha256,store.snapshot?.map?.id,store.snapshot?.map?.version,store.snapshot?.map?.definitionHash].join('|'));
const connected=computed(()=>!store.engineeringSimulationOnly&&!store.demoMode&&store.fresh);
const canApply=computed(()=>connected.value&&store.engineeringRuntimeApplyEnabled&&preview.value?.ready&&preview.value?.mapActive&&reviewed.value&&confirmation.value==='应用 '+version.value&&!pending.value);
watch([version,context],()=>{++epoch;preview.value=null;reviewed.value=false;confirmation.value='';busy.value=false;});
watch(()=>props.initialVersion,value=>{if(value&&!version.value)version.value=value;});
watch(()=>store.fresh,f=>{if(!f){++epoch;preview.value=null;reviewed.value=false;busy.value=false;}});
onUnmounted(()=>{++epoch;});
function errorText(value:unknown){
  const text=String(value).replace(/^Error:\s*/i,'');
  if(/工程版本摘要不一致/.test(text))return `${text}。先刷新本页和工程版本；仍失败时核对 5199 发布目录、主服务发布批次及入口指针，不能沿用旧页面继续应用。`;
  return text;
}
function yesNo(value:boolean|null|undefined){return value===true?'是':value===false?'否':'未指定';}
async function run(fn:(n:number)=>Promise<void>){if(busy.value)return;const n=++epoch;busy.value=true;message.value='';try{await fn(n);}catch(e){if(n===epoch)message.value=errorText(e);}finally{if(n===epoch)busy.value=false;}}
async function preflight(){if(!connected.value)return;preview.value=null;reviewed.value=false;confirmation.value='';await run(async n=>{const p=await store.api('/api/engineering-runtime/preview',{versionId:version.value});if(n===epoch)preview.value=p;});}
async function query(){if(!connected.value)return;await run(async n=>{const s:any=await store.api('/api/engineering-runtime/state');if(n!==epoch)return;state.value=s;if(pending.value&&s?.savedRequestId===pending.value.requestId){sessionStorage.removeItem(key);pending.value=null;preview.value=null;message.value='原申请已保存并生效；未重复应用，也未启动任务。';}else message.value='已读取当前参数；未观察到原请求回执时保留原编号，可重试同一申请，不生成新请求。';});}
async function send(q:any){await run(async n=>{const r:any=await store.api('/api/engineering-runtime/apply',q);if(n!==epoch)return;message.value=r.message;if(r.accepted){sessionStorage.removeItem(key);pending.value=null;state.value=r.snapshot;preview.value=null;}else{sessionStorage.removeItem(key);pending.value=null;preview.value=null;}});}
async function apply(){if(!canApply.value)return;const p=preview.value;const q={versionId:p.versionId,requestId:crypto.randomUUID().replaceAll('-',''),expectedRevision:p.parameterRevision,expectedCommunicationRevision:p.communicationRevision,expectedContentHash:p.contentHash,expectedMapHash:p.mapDefinitionHash,confirmationText:'APPLY '+version.value,actionsReviewed:reviewed.value};
 try{sessionStorage.setItem(key,JSON.stringify(q));pending.value=q;}catch{message.value='不能保存原请求编号，未发送应用申请。';return;}await send(q);}
async function retry(){if(connected.value&&store.engineeringRuntimeApplyEnabled&&pending.value)await send(pending.value);}
onMounted(()=>run(async n=>{const v:any=await store.api('/api/engineering/versions');if(n===epoch){versions.value=v;if(!version.value&&v.length)version.value=v[0].id;}}));
</script>
<template>
<section class="management-page runtime-page" :class="{embedded:props.embedded}">
 <header v-if="!props.embedded" class="management-title"><h2>工程执行接入</h2><RouterLink to="/engineering?tab=versions">返回工程版本</RouterLink></header>
 <p>将已发布工程的工位、倒料点和车辆池接入现有九步调度执行器。按下面顺序准备，每一步均显示实际结果。</p>
 <p v-if="store.engineeringSimulationOnly" class="inline-warning">当前为隔离模拟实例。现场预检、地图激活和参数应用均禁用；可以在工程中心继续模拟测试。</p>
 <p v-else-if="!connected" class="inline-warning">请连接后台并取得新鲜状态后预检。演示数据不能用于应用。</p>
 <p v-if="message" class="inline-warning" role="status">{{industrialText(message)}}</p>
 <article><h3>1. 选择冻结工程版本</h3><label>工程版本<select v-model="version" :disabled="busy||!!pending"><option value="">请选择</option><option v-for="v in versions" :key="v.id" :value="v.id">{{v.name}} · {{v.id}} · {{v.note}}</option></select></label><button :disabled="busy||!connected||!version" @click="preflight">预检工程、地图与当前通信</button><button :disabled="busy||!connected" @click="query">查询当前应用及原申请</button></article>
 <article><h3>2. 核对并激活准确地图</h3><p>先在地图编辑准备运行图并发布，再把准确地图编号和版本绑定到工程。地图激活仍经过现有安全切换流程。</p>
 <div v-if="preview"><p class="identity">目标 {{preview.mapId}} / {{preview.mapVersionId}}<br/>地图摘要 {{preview.mapDefinitionHash}}<br/>工程摘要 {{preview.contentHash}}</p><p>{{preview.mapActive?'绑定地图已是当前运行版本。':'绑定地图尚未激活：先去地图管理完成切换，返回后重新预检。'}}</p></div>
 <nav><RouterLink :to="{path:'/maps',query:{engineeringVersion:version,mapId:preview?.mapId,mapVersion:preview?.mapVersionId}}">地图管理与安全切换</RouterLink><RouterLink to="/engineering-settings">厂家地图绑定／四段车辆动作</RouterLink><RouterLink to="/communication">通信与PLC点表</RouterLink><RouterLink to="/recovery">现场恢复与取消核对</RouterLink></nav>
 <ul v-if="preview?.blockers.length" role="alert"><li v-for="b in preview.blockers" :key="b">{{industrialText(b)}}</li></ul>
 </article>
 <article><h3>3. 核对执行参数并应用</h3><p>当前支持一组倒料设备和共用四段动作；多倒料设备、多动作模板工程会明确拦截。工程的候选包络、模拟耗时、PLC地址和待机策略不会在此自动替换现场配置。</p>
 <template v-if="preview?.configuration"><p>倒料 {{preview.configuration.dumperStationId}} · 候车 {{effectiveWaitingSites(preview.configuration).join('、')}} · 倒料点 {{preview.configuration.dumpingSite}}</p><details><summary>{{preview.configuration.pickups.length}}个工位及实际授权车辆</summary><table><thead><tr><th>工位</th><th>站点</th><th>车辆池</th><th>授权车辆</th></tr></thead><tbody><tr v-for="p in preview.configuration.pickups" :key="p.stationId"><td>{{p.stationId}}</td><td>{{p.pickupSite}}</td><td>{{p.vehiclePoolId}}</td><td>{{p.allowedVehicleIds.join('、')}}</td></tr></tbody></table></details><ul><li v-for="a in preview.configuration.actions" :key="a.stepType">{{taskStepName(a.stepType)}}：{{vehicleActionLabel(a.operation)}} · 顶升高度 {{a.jackHeight??'未指定'}} · 使用 PGV：{{yesNo(a.usePgv)}}</li></ul></template>
 <ul v-if="preview"><li v-for="n in preview.notes" :key="n">{{industrialText(n)}}</li></ul>
 <p v-if="!store.engineeringRuntimeApplyEnabled" class="inline-warning">当前实例未启用工程执行参数应用。受控部署需启用 Web:EnableEngineeringRuntimeApply；这不等于开放车辆动作许可。</p>
<label class="check"><input v-model="reviewed" type="checkbox" :disabled="!preview||busy||!!pending"/>已核对本次工位、车辆池及保留的四段车辆动作</label><label>输入“应用 {{version||'工程版本编号'}}”确认<input v-model="confirmation" :disabled="busy||!!pending" autocomplete="off" :placeholder="`应用 ${version||'工程版本编号'}`"/></label><button :disabled="busy||!canApply" @click="apply">应用工程执行参数（不启动任务）</button>
 <div v-if="pending" class="inline-warning"><p class="identity">原申请 {{pending.requestId}} · 工程 {{pending.versionId}}。结果待核对，请先查询原申请。</p><button :disabled="busy||!connected||!store.engineeringRuntimeApplyEnabled" @click="retry">沿用原编号重试应用</button></div>
 <p v-if="state" class="identity">当前参数修订 {{state.revision}} · 工程 {{state.configuration?.engineeringSource?.versionId??'未绑定工程'}}<br/>{{state.explanation}}</p>
 </article>
 <article><h3>4. 检查运行条件，再启动业务</h3><p>应用成功后，仍需完成现场地图核验、PLC点表与写许可、已核定包络及启动恢复核对。应用操作不会生成呼叫或下发车辆动作。</p><nav><RouterLink to="/commissioning">查看运行条件</RouterLink><RouterLink to="/tasks">进入任务列表</RouterLink></nav></article>
</section>
</template>
<style scoped>
.runtime-page{max-width:1300px;margin:auto}.runtime-page.embedded{max-width:none;margin:0;gap:10px}.runtime-page article{border:1px solid #2d4263;border-radius:8px;padding:16px 18px;margin:0;background:#111b32}.runtime-page.embedded>article{display:grid;grid-template-columns:minmax(220px,290px) minmax(0,1fr);gap:8px 22px;align-items:start}.runtime-page.embedded>article>h3{grid-column:1}.runtime-page.embedded>article>p,.runtime-page.embedded>article>nav,.runtime-page.embedded>article>ul,.runtime-page.embedded>article>div,.runtime-page.embedded>article>label{grid-column:1/-1}.runtime-page h3{margin:0 0 8px;color:#c2dfed;font-size:16px}.runtime-page p,.runtime-page li{line-height:1.7;color:#9fb6ca}.runtime-page label{display:grid;gap:7px;margin:12px 0;max-width:900px}.runtime-page .check{display:flex;align-items:center}.runtime-page nav{display:flex;flex-wrap:wrap;gap:10px 18px;margin:12px 0}.runtime-page a{color:#5bd6eb}.runtime-page button{margin:5px 10px 5px 0}.identity{overflow-wrap:anywhere;font-family:Consolas,monospace;font-size:12px}.runtime-page table{width:100%;text-align:left;border-collapse:collapse}.runtime-page td,.runtime-page th{padding:9px;border-bottom:1px solid #30405a}details{max-height:420px;overflow:auto}@media(max-width:900px){.runtime-page.embedded>article{grid-template-columns:1fr}.runtime-page.embedded>article>*{grid-column:1!important}}
</style>
