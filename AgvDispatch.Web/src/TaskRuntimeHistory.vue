<script setup lang="ts">
import { ref, watch } from 'vue';
import type { RuntimePage } from './types';
import { shortTime } from './dashboard';
import { boolText } from './task-diagnostics';
const props=defineProps<{taskId:string;fresh:boolean}>();
const result=ref<RuntimePage|null>(null),busy=ref(false),error=ref(''),kind=ref(''),signal=ref(''),from=ref(''),to=ref('');
let epoch=0;
watch([kind,signal,from,to],()=>{result.value=null;error.value='';});
watch(()=>props.taskId,()=>{epoch++;result.value=null;error.value='';busy.value=false;kind.value='';signal.value='';from.value='';to.value='';});
async function query(page=1,reset=true){
  if(busy.value||!props.fresh)return;const mine=++epoch;busy.value=true;error.value='';
  try{
    const response=await fetch(`/api/tasks/${encodeURIComponent(props.taskId)}/runtime-events`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({taskId:props.taskId,kind:kind.value,signal:signal.value,from:from.value?new Date(from.value).toISOString():null,to:to.value?new Date(to.value).toISOString():null,page,pageSize:50,throughSequence:reset?null:result.value?.throughSequence}),signal:AbortSignal.timeout(35000)});
    const data=await response.json();if(!response.ok)throw new Error(data.detail??data.error??'事件查询失败');
    if(mine===epoch)result.value=data;
  }catch(e){if(mine===epoch){error.value=String(e);result.value=null;}}finally{if(mine===epoch)busy.value=false;}
}
async function download(){
  if(busy.value||!props.fresh)return;const mine=++epoch;busy.value=true;error.value='';
  try{
    const response=await fetch(`/api/tasks/${encodeURIComponent(props.taskId)}/diagnostics/export`,{signal:AbortSignal.timeout(35000)});
    if(!response.ok){const data=await response.json();throw new Error(data.detail??data.error??'导出失败');}
    const blob=await response.blob();if(mine!==epoch)return;
    const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download='agv-diagnostics.zip';a.click();setTimeout(()=>URL.revokeObjectURL(url),10000);
  }catch(e){if(mine===epoch)error.value=String(e);}finally{if(mine===epoch)busy.value=false;}
}
</script>
<template><section class="runtime-history"><h3>运行事件与信号变化查询</h3><p>首次观察与变化采样按后台序号留存。分页保留同一序号窗口；点击“重新查询”读取新事件。采样记录不保证捕获PLC扫描间的全部变化。</p>
<div class="runtime-tools"><label>类型<select v-model="kind" :disabled="busy"><option value="">全部运行事件</option><option value="Signals">信号观察</option><option value="HandshakeTransition">握手阶段转换</option><option value="HandshakeReviewed">握手人工核对</option><option value="ExitVerified">实际出区核对证据</option><option value="EquipmentClaimed">设备归属申请</option><option value="EquipmentReleased">设备归属释放</option></select></label><label>信号变量<input v-model="signal" placeholder="如 BoxPresent" maxlength="128" :disabled="busy"></label><label>记录起始时间<input v-model="from" type="datetime-local" :disabled="busy"></label><label>记录结束时间<input v-model="to" type="datetime-local" :disabled="busy"></label><button :disabled="busy||!fresh" @click="query()">{{ busy?'处理中…':'重新查询' }}</button><button :disabled="busy||!fresh" @click="download">导出本任务联调包</button></div>
<p v-if="error" role="alert" class="inline-warning">{{ error }}</p><p v-if="!result">尚未查询；不自动填入示例事件。</p>
<template v-else><p>共{{ result.totalCount }}项 · 固定窗口截止序号{{ result.throughSequence }} · 第{{ result.page }}页</p><div class="runtime-table"><table class="dialog-table"><thead><tr><th>序号 / 记录时间</th><th>事件 / 阶段</th><th>工位 / 信号</th><th>前值 → 当前 / 质量</th><th>采集时间 / 说明</th></tr></thead><tbody><tr v-for="e in result.items" :key="e.sequence"><td>{{ e.sequence }}<br>{{ shortTime(e.recordedAt) }}</td><td>{{ e.kind }}<br>{{ e.phase }}</td><td>{{ e.stationId||'—' }}<br>{{ e.signal||'—' }}</td><td>{{ e.kind.startsWith('Signal')?`${boolText(e.previousValue)} → ${boolText(e.value)}`:'—' }}<br>{{ e.quality }}</td><td>{{ shortTime(e.observedAt) }}<br>{{ e.reason }}<details v-if="e.evidence"><summary>结构化核对证据</summary><pre>{{ e.evidence }}</pre></details></td></tr></tbody></table></div><p v-if="!result.items.length">当前筛选没有记录。</p><div class="runtime-tools"><button :disabled="busy||!fresh||result.page<=1" @click="query(result!.page-1,false)">上一页</button><button :disabled="busy||!fresh||result.page*result.pageSize>=result.totalCount" @click="query(result!.page+1,false)">下一页</button></div></template>
<p>联调包包含任务观察、握手检查点、资源诊断、新事件窗口、CSV及SHA256清单。旧执行时间线只含最近100项；包内明确记录范围和限制。超过单包限制会拒绝，不静默截断。</p></section></template>
<style scoped>.runtime-history{margin-top:28px;padding-top:20px;border-top:1px solid #294361}.runtime-history h3{color:#cee2f5;font-size:15px}.runtime-history p{font-size:12px;color:#8faac6;line-height:1.8;margin:12px 0}.runtime-tools{display:flex;flex-wrap:wrap;gap:10px;align-items:flex-end;margin:12px 0}.runtime-tools label{display:flex;flex-direction:column;gap:6px;color:#a1bdd7;font-size:11px}.runtime-tools input,.runtime-tools select{max-width:190px}.runtime-table{overflow:auto}.runtime-table table{min-width:740px}.runtime-table pre{white-space:pre-wrap;max-width:450px;overflow-wrap:anywhere;font-size:11px}.runtime-table td{overflow-wrap:anywhere;line-height:1.8}</style>
