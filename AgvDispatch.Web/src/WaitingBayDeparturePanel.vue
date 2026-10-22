<script setup lang="ts">
import {computed,onUnmounted,ref,watch} from 'vue';
import {useDispatchStore} from './stores/dispatch';
import {departureCurrent,type DeparturePage} from './waiting-bay-departure';
import ActionIcon from './components/ActionIcon.vue';
const props=defineProps<{taskId:string;stepId:string;resourceIds:string[];live:boolean}>(),store=useDispatchStore();
const resource=ref(''),result=ref<DeparturePage|null>(null),error=ref(''),busy=ref(false);let epoch=0;
const choices=computed(()=>[...new Set(props.resourceIds)]);
const context=computed(()=>[props.taskId,props.stepId,resource.value,store.demoMode,store.adapterIdentity?.sha256,
 store.snapshot?.map?.id,store.snapshot?.map?.version,store.snapshot?.map?.definitionHash,store.snapshot?.tasks?.items.find(t=>t.taskId===props.taskId)?.stage,
 props.live,store.fresh].join('|'));
const current=computed(()=>props.live&&departureCurrent(result.value,store.snapshot,props.taskId,props.stepId,resource.value,store.now));
watch(context,()=>{++epoch;busy.value=false;result.value=null;error.value='';});
watch(choices,ids=>{if(!ids.includes(resource.value))resource.value='';});
onUnmounted(()=>{++epoch;});
async function inspect(){
 if(!props.live||!store.fresh||store.demoMode||!resource.value||busy.value)return;
 const mine=++epoch,ctx=context.value,map=store.snapshot?.map;busy.value=true;error.value='';result.value=null;
 try{const value=await store.api<DeparturePage>('/api/traffic/waiting-bay-departure',{stepId:props.stepId,resourceId:resource.value,
 expectedMapId:map?.id,expectedMapVersion:map?.version,expectedMapDefinitionHash:map?.definitionHash});
 if(mine===epoch&&ctx===context.value)result.value=value;
 }catch(e){if(mine===epoch)error.value=String(e);}finally{if(mine===epoch)busy.value=false;}
}
</script>
<template><section class="departure-panel" aria-label="候车位实际离位只读核验"><h4>候车位实际离位 · 只读证据</h4>
<p>核验原入站命令、连续定位、完整车体与载荷、同图租约及后续倒料资源。通过仅表示本次证据满足检查，候车位仍按整单闭环保持。</p>
<label>候车资源 <select v-model="resource" aria-label="离位核验候车资源"><option value="">请选择原持有资源</option><option v-for="id in choices" :key="id" :value="id">{{id}}</option></select></label>
<button :disabled="busy||!live||!store.fresh||store.demoMode||!resource" @click="inspect"><ActionIcon name="preview"/>{{busy?'核验中…':'只读核验实际离位'}}</button>
<p v-if="!live||!store.fresh">当前诊断无效，不能核验。</p><p v-if="store.demoMode">浏览器合成模式不生成后台离位证据。</p>
<p v-if="error" role="status">{{error}}</p>
<div v-if="result" role="status"><b>{{current?(result.assessment.departureEvidenceVerified?'离位证据通过 · 未释放':result.assessment.available?'离位证据未满足 · 占用保持':'资料不足或当前条件不可核验'):'结果已失效，请重新核验'}}</b>
<p>{{current?result.assessment.reason:'旧结论不能作为当前离位依据。'}}</p>
<details v-if="current&&result.assessment.leaseIds.length"><summary>本次核对的候车位原租约</summary><p v-for="id in result.assessment.leaseIds" :key="id">{{id}}</p></details></div>
</section></template>
<style scoped>.departure-panel{padding:14px;border:1px solid #385271;margin:18px 0;font-size:12px;line-height:1.9;overflow-wrap:anywhere}.departure-panel h4{color:#cce1f3}.departure-panel p{margin:8px 0}.departure-panel select{max-width:100%}.departure-panel button{margin:8px}.departure-panel label{display:inline-flex;gap:8px;align-items:center;flex-wrap:wrap}.departure-panel b{color:#dfc69d}</style>
