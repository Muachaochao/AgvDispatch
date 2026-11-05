<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { useDispatchStore } from '../stores/dispatch';
import {
  createStandardTemplate,
  normalizeTemplate,
  templateExport,
  buildTemplateConnections,
  type EngineeringPlan,
  type EngineeringRow,
  type EngineeringTaskPreview,
  type EngineeringTemplateDefinition,
  type EngineeringTemplateLibraryEntry,
  type EngineeringTemplateNode
} from '../engineering-center';
import { engineeringOptionLabel, failurePolicyLabel, industrialText, timeoutActionLabel } from '../operator-labels';

const props=defineProps<{plan:EngineeringPlan}>();
const emit=defineEmits<{message:[text:string]}>();
const store=useDispatchStore();
const busy=ref(false),previewBusy=ref(false),error=ref(''),selectedId=ref(''),library=ref<EngineeringTemplateLibraryEntry[]>([]);
const stationId=ref(''),previewTemplateId=ref(''),preview=ref<EngineeringTaskPreview|null>(null);
const templates=computed(()=>props.plan.templates.map(normalizeTemplate));
const selectedRaw=computed(()=>props.plan.templates.find(item=>String(item.id)===selectedId.value)??null);
const selected=computed(()=>templates.value.find(item=>item.id===selectedId.value)??null);
const enabledStations=computed(()=>props.plan.stations.filter(item=>item.enabled!==false));
const selectedStation=computed(()=>enabledStations.value.find(item=>String(item.id)===stationId.value));
const ready=computed(()=>preview.value?.ready===true);

watch(templates,items=>{if(!items.some(item=>item.id===selectedId.value))selectedId.value=items[0]?.id??'';},{immediate:true});
watch(enabledStations,items=>{if(!items.some(item=>String(item.id)===stationId.value))stationId.value=String(items[0]?.id??'');},{immediate:true});
watch(selectedStation,item=>{const id=String(item?.templateId??'');if(templates.value.some(t=>t.id===id))previewTemplateId.value=id;},{immediate:true});
watch([stationId,previewTemplateId],()=>{preview.value=null;if(stationId.value&&previewTemplateId.value)void loadPreview();},{immediate:true});

async function run(fn:()=>Promise<void>){if(busy.value)return;busy.value=true;error.value='';try{await fn();}catch(ex){error.value=ex instanceof Error?ex.message:String(ex);}finally{busy.value=false;}}
function say(text:string){emit('message',text);}
function uniqueId(base:string){const cleaned=base.replace(/[^A-Za-z0-9_-]/g,'-').slice(0,48)||'BOX-CYCLE';let id=cleaned,n=2;const used=new Set(templates.value.map(t=>t.id.toLowerCase()));while(used.has(id.toLowerCase()))id=`${cleaned}-${n++}`.slice(0,64);return id;}
function castTemplate(template:EngineeringTemplateDefinition){return template as unknown as EngineeringRow;}
function addStandard(){const id=uniqueId('BOX-CYCLE');const template=createStandardTemplate(id,'标准取箱倒料返箱');props.plan.templates.push(castTemplate(template));selectedId.value=id;say('已新增九步任务模板；请核对能力、版本和工位绑定后保存工程草稿。');}
function copyTemplate(source:EngineeringTemplateDefinition,fromLibrary=false){
 const id=uniqueId(source.id);
 const copy={...normalizeTemplate(source),id,name:`${source.name}（副本）`,version:1,sourceTemplateId:source.id,sourceVersion:source.version};
 props.plan.templates.push(castTemplate(copy));selectedId.value=id;
 say(`${fromLibrary?'已从全局模板库复制':'已复制'}模板到当前工程；原模板版本保持不变。`);
}
function updateText(key:'name'|'capability',event:Event){if(!selectedRaw.value)return;selectedRaw.value[key]=(event.target as HTMLInputElement).value;}
function updateNumber(key:'version'|'pickupSeconds'|'dumpSeconds'|'returnSeconds',event:Event){if(!selectedRaw.value)return;selectedRaw.value[key]=Number((event.target as HTMLInputElement).value);}
function rawTemplate(){if(!selectedRaw.value)return null;const raw=selectedRaw.value as unknown as EngineeringTemplateDefinition;if(!Array.isArray(raw.nodes)||!raw.nodes.length){const normalized=normalizeTemplate(raw);raw.inputs=normalized.inputs;raw.outputs=normalized.outputs;raw.variables=normalized.variables;raw.nodes=normalized.nodes;raw.guards=normalized.guards;}raw.connections=buildTemplateConnections(raw.nodes,raw.variables);return raw;}
function updateNode(node:EngineeringTemplateNode,key:'failurePolicy'|'timeoutAction'|'timeoutSeconds',event:Event){const raw=rawTemplate();if(!raw)return;const target=raw.nodes.find(item=>item.id===node.id);if(!target)return;const input=event.target as HTMLInputElement|HTMLSelectElement;if(key==='timeoutSeconds')target.timeoutSeconds=Number(input.value);else (target as unknown as Record<string,string>)[key]=input.value;raw.connections=buildTemplateConnections(raw.nodes,raw.variables);}
async function loadLibrary(){await run(async()=>{library.value=await store.api<EngineeringTemplateLibraryEntry[]>('/api/engineering/template-library');});}
async function saveLibrary(){if(!selected.value)return;await run(async()=>{const entry=await store.api<EngineeringTemplateLibraryEntry>('/api/engineering/template-library/save',normalizeTemplate(selected.value!));library.value=await store.api<EngineeringTemplateLibraryEntry[]>('/api/engineering/template-library');say(`模板 ${entry.template.id} v${entry.template.version} 已保存到全局模板库，SHA256 ${entry.contentHash.slice(0,16)}…；已保存版本不可覆盖。`);});}
async function importTemplate(event:Event){const input=event.target as HTMLInputElement,file=input.files?.[0];input.value='';if(!file)return;await run(async()=>{if(file.size>4*1024*1024)throw new Error('模板文件最多4MiB。');const parsed=JSON.parse(await file.text());const transfer=parsed.template?parsed:{schemaVersion:1,template:parsed,contentHash:''};const entry=await store.api<EngineeringTemplateLibraryEntry>('/api/engineering/template-library/import',transfer);await loadLibrary();copyTemplate(entry.template,true);say(`模板 ${entry.template.id} v${entry.template.version} 已导入全局模板库并复制到当前工程。`);});}
function exportTemplate(template:EngineeringTemplateDefinition){const body=templateExport(template);download(`${template.id}-v${template.version}.agv-template.json`,JSON.stringify(body,null,2));}
function download(name:string,text:string){const url=URL.createObjectURL(new Blob([text],{type:'application/json'})),a=document.createElement('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),0);}
async function loadPreview(){if(previewBusy.value||!stationId.value||!previewTemplateId.value)return;previewBusy.value=true;try{preview.value=await store.api<EngineeringTaskPreview>('/api/engineering/task-preview',{plan:props.plan,stationId:stationId.value,templateId:previewTemplateId.value});}catch(ex){error.value=ex instanceof Error?ex.message:String(ex);}finally{previewBusy.value=false;}}
function shortHash(value:string){return value?value.slice(0,12)+'…':'—';}
onMounted(loadLibrary);
</script>

<template>
<section class="template-workbench">
 <div class="template-heading"><div><h3>版本化任务模板</h3><p>模板保存输入、输出、九步节点及两个交通适配节点；下发时只编译为现有持久化执行计划，不执行任意脚本。</p></div><button class="accent-button" :disabled="busy" @click="addStandard">新增九步模板</button></div>
 <p v-if="error" class="inline-warning" role="alert">{{error}}</p>
 <div class="template-lists">
  <section><header><h4>当前工程模板</h4><span>{{templates.length}} 个</span></header>
   <button v-for="item in templates" :key="item.id" :class="{active:selectedId===item.id}" @click="selectedId=item.id"><b>{{item.name}}</b><span>{{item.id}} · v{{item.version}} · {{item.capability}}</span></button>
   <p v-if="!templates.length">当前工程还没有任务模板。</p>
  </section>
  <section><header><h4>全局模板库</h4><span>{{library.length}} 个版本</span></header>
   <article v-for="entry in library" :key="entry.key"><div><b>{{entry.template.name}}</b><span>{{entry.template.id}} · v{{entry.template.version}} · {{entry.template.capability}}</span><small>{{new Date(entry.savedAt).toLocaleString('zh-CN',{timeZone:'Asia/Shanghai'})}} · SHA256 {{shortHash(entry.contentHash)}}</small></div><div><button @click="copyTemplate(entry.template,true)">复制到工程</button><button @click="exportTemplate(entry.template)">导出</button></div></article>
   <p v-if="!library.length">全局模板库为空。保存或导入模板后可跨工程复用。</p>
   <div class="library-actions"><label class="file-button">导入模板文件<input type="file" accept=".json,.agv-template.json" :disabled="busy" @change="importTemplate"/></label><button :disabled="busy" @click="loadLibrary">刷新模板库</button></div>
  </section>
 </div>
 <div v-if="selected" class="template-editor">
  <div class="editor-title"><div><h4>{{selected.name}}</h4><p>模板编号不可修改；已保存到全局模板库的版本也不可覆盖。</p></div><div><button :disabled="busy" @click="saveLibrary">保存版本到模板库</button><button @click="copyTemplate(selected)">复制为新模板</button><button @click="exportTemplate(selected)">导出模板</button></div></div>
  <div class="template-form">
   <label>模板编号<input :value="selected.id" readonly/></label>
   <label>模板名称<input :value="selected.name" maxlength="80" @input="updateText('name',$event)"/></label>
   <label>版本<input :value="selected.version" type="number" min="1" max="1000000" @input="updateNumber('version',$event)"/></label>
   <label>车辆能力<select :value="selected.capability" @change="updateText('capability',$event)"><option value="Jack">{{engineeringOptionLabel('Jack')}}</option><option value="Fork">{{engineeringOptionLabel('Fork')}}</option></select></label>
   <label>模拟取箱秒数<input :value="selected.pickupSeconds" type="number" min="0.1" max="3600" step="0.1" @input="updateNumber('pickupSeconds',$event)"/></label>
   <label>模拟倒料秒数<input :value="selected.dumpSeconds" type="number" min="0.1" max="3600" step="0.1" @input="updateNumber('dumpSeconds',$event)"/></label>
   <label>模拟返箱秒数<input :value="selected.returnSeconds" type="number" min="0.1" max="3600" step="0.1" @input="updateNumber('returnSeconds',$event)"/></label>
   <label>执行器<input :value="selected.engine" readonly/></label>
  </div>
  <div class="contract-columns">
   <section><h5>任务输入</h5><div v-for="port in selected.inputs" :key="port.key"><b>{{port.label}}</b><details><summary>查看输入字段</summary><code>{{port.key}}: {{port.type}}</code></details><small>来源 {{industrialText(port.source)}}</small></div></section>
   <section><h5>任务输出</h5><div v-for="port in selected.outputs" :key="port.key"><b>{{port.label}}</b><details><summary>查看输出字段</summary><code>{{port.key}}: {{port.type}}</code></details><small>来源 {{industrialText(port.source)}}</small></div></section>
  </div>
  <section class="policy-panel"><h5>节点超时与失败策略</h5><p>机械、交通适配和握手节点固定禁止自动重试。超时为0表示仅关闭提示，不自动推进或重发动作。</p><div class="policy-table"><table><thead><tr><th>节点</th><th>失败策略</th><th>超时处置</th><th>超时秒数</th><th>自动重试</th></tr></thead><tbody><tr v-for="node in selected.nodes" :key="node.id"><td><b>{{node.name}}</b><details><summary>查看节点类型</summary><code>{{node.block}}</code></details></td><td><select :value="node.failurePolicy" @change="updateNode(node,'failurePolicy',$event)"><option value="HoldUntilCondition">{{failurePolicyLabel('HoldUntilCondition')}}</option><option value="Pause">暂停并等待人工核对</option><option value="ManualRecovery">进入人工恢复流程</option><option value="Terminate">终止本步骤</option></select></td><td><select :value="node.timeoutAction" @change="updateNode(node,'timeoutAction',$event)"><option value="NotifyAndHold">{{timeoutActionLabel('NotifyAndHold')}}</option><option value="Pause">暂停并等待人工核对</option><option value="ManualRecovery">进入人工恢复流程</option><option value="Terminate">终止本步骤</option></select></td><td><input :value="node.timeoutSeconds" type="number" min="0" max="604800" @input="updateNode(node,'timeoutSeconds',$event)"/></td><td>{{node.autoRetry?'允许':'禁止'}}</td></tr></tbody></table></div></section>
  <section class="guard-panel"><h5>异常分支白名单</h5><div v-for="guard in selected.guards" :key="guard.id"><b>{{guard.trigger}}</b><code>{{guard.action}}</code><span>目标节点 {{guard.targetNodeId}}</span><small>{{guard.description}}</small></div></section>
 </div>

 <section class="task-flow-panel">
  <header><div><h4>九步任务链与输入输出绑定</h4><p>选择工程工位后，从当前草稿展开节点、站点、车辆池、PLC条件和输出。此项只读，不创建任务。</p></div><div class="flow-selects"><label>工位<select v-model="stationId"><option v-for="item in enabledStations" :key="String(item.id)" :value="String(item.id)">{{item.name}} · {{item.id}}</option></select></label><label>模板<select v-model="previewTemplateId"><option v-for="item in templates" :key="item.id" :value="item.id">{{item.id}} · v{{item.version}}</option></select></label><button :disabled="previewBusy||!stationId||!previewTemplateId" @click="loadPreview">重新预览</button></div></header>
  <p v-if="previewBusy">正在生成任务链…</p>
  <template v-else-if="preview">
   <div class="preview-summary"><span :class="ready?'ready':'blocked'">{{ready?'静态绑定通过':'存在阻塞'}}</span><b>{{preview.stationId}} → {{preview.dumpingSite}}</b><small>模板 {{preview.templateId}} v{{preview.templateVersion}} · {{preview.engine}} · SHA256 {{shortHash(preview.templateHash)}}</small></div>
   <ul v-if="preview.issues.length" class="preview-issues"><li v-for="issue in preview.issues" :key="issue.field+issue.message">{{issue.objectId}}：{{issue.message}}</li></ul>
   <div class="flow-grid">
    <div class="flow-chain">
     <article v-for="node in preview.nodes" :key="node.nodeId" :class="{muted:!ready}">
      <header><span>{{node.sequence}}</span><div><b>{{node.name}}</b><code>{{node.stepType}}</code></div></header>
      <dl><div v-if="node.sourceSite"><dt>来源</dt><dd>{{node.sourceSite}}</dd></div><div v-if="node.destinationSite"><dt>目标</dt><dd>{{node.destinationSite}}</dd></div><div><dt>车辆</dt><dd>{{node.vehicleAction}}</dd></div><div><dt>条件</dt><dd>{{node.plcCondition}}</dd></div></dl>
      <p><span v-for="input in node.inputs" :key="input">输入 {{input}}</span><span v-for="output in node.outputs" :key="output">输出 {{output}}</span><span>超时 {{node.timeoutSeconds}}s</span><span>{{node.failurePolicy}}</span></p>
     </article>
    </div>
    <aside class="flow-variables">
     <section><h5>输入绑定</h5><div v-for="item in preview.inputs" :key="item.key"><b>{{item.label}}</b><code>{{item.key}}</code><span>{{item.value||'—'}}</span><small>{{item.source}}</small></div></section>
     <section><h5>输出变量</h5><div v-for="item in preview.outputs" :key="item.key"><b>{{item.label}}</b><code>{{item.key}}</code><span>{{item.type}}</span><small>{{item.source}}</small></div></section>
     <section><h5>类型化连接</h5><div v-for="(item,index) in preview.connections" :key="index"><b>{{item.fromNodeId||'外部输入'}}.{{item.output}}</b><code>{{item.type}}</code><span>→ {{item.toNodeId}}.{{item.input}}</span></div></section>
     <section><h5>异常分支</h5><div v-for="guard in preview.guards" :key="guard.id"><b>{{guard.trigger}}</b><code>{{guard.action}}</code><small>{{guard.targetNodeId}}</small></div></section>
     <p>车辆池 <b>{{preview.vehiclePoolId}}</b>：{{preview.vehicleIds.join('、')||'无授权车辆'}}。候车位：{{preview.waitingSites.join('、')||'未配置'}}。</p>
    </aside>
   </div>
  </template>
  <p v-else>请选择启用工位和已定义模板。</p>
 </section>
</section>
</template>

<style scoped>
.template-workbench{display:grid;gap:18px}.template-heading,.editor-title,.task-flow-panel>header{display:flex;justify-content:space-between;align-items:flex-start;gap:18px}.template-heading h3,.template-heading p,.editor-title h4,.editor-title p,.task-flow-panel h4,.task-flow-panel p{margin:0}.template-heading p,.editor-title p,.task-flow-panel>header p{color:#92abc7;line-height:1.7;margin-top:7px}.template-lists{display:grid;grid-template-columns:1fr 1fr;gap:14px}.template-lists>section,.template-editor,.task-flow-panel{border:1px solid #2d4766;background:#0d192d;padding:14px;min-width:0}.template-lists header{display:flex;justify-content:space-between;color:#9ab4cf;margin-bottom:10px}.template-lists section>button{display:grid;gap:5px;width:100%;text-align:left;margin:6px 0;background:#101f35}.template-lists section>button.active{border-color:#3cb9cf;background:#163344}.template-lists span,.template-lists small{display:block;color:#8fa8c3;font-size:11px}.template-lists article{display:flex;justify-content:space-between;gap:10px;padding:9px 0;border-top:1px solid #243b59;overflow-wrap:anywhere}.template-lists article>div:last-child{display:flex;gap:5px;flex-wrap:wrap;align-content:start}.library-actions{display:flex;gap:8px;margin-top:10px}.template-editor{display:grid;gap:14px}.editor-title>div:last-child{display:flex;gap:7px;flex-wrap:wrap}.template-form{display:grid;grid-template-columns:repeat(auto-fit,minmax(155px,1fr));gap:12px}.template-form label{display:grid;gap:6px;color:#a9c1da;font-size:12px}.template-form input,.template-form select{width:100%;min-width:0}.contract-columns{display:grid;grid-template-columns:1fr 1fr;gap:14px}.contract-columns section,.policy-panel,.guard-panel{border:1px solid #263f5d;padding:12px;background:#0a1628}.contract-columns h5,.flow-variables h5,.policy-panel h5,.guard-panel h5{margin:0 0 10px;color:#8bd8e8}.contract-columns div,.flow-variables div,.guard-panel div{display:grid;gap:3px;padding:7px 0;border-top:1px solid #1f3450}.contract-columns code,.flow-variables code,.guard-panel code{color:#65c9df}.contract-columns small,.flow-variables small,.guard-panel small{color:#809ab8}.policy-panel p{font-size:11px;color:#8fa9c4;margin:0 0 10px}.policy-table{overflow:auto}.policy-table table{width:100%;border-collapse:collapse;min-width:650px}.policy-table th,.policy-table td{text-align:left;padding:8px;border-bottom:1px solid #243b59;vertical-align:top}.policy-table th{color:#8fa8c4;font-weight:400;font-size:11px}.policy-table td{font-size:11px}.policy-table td:first-child code{display:block;color:#67c9dd}.policy-table select,.policy-table input{width:100%;min-width:110px}.guard-panel{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px 14px}.guard-panel h5{grid-column:1/-1}.task-flow-panel{display:grid;gap:14px}.flow-selects{display:flex;align-items:end;gap:8px;flex-wrap:wrap}.flow-selects label{display:grid;gap:5px;color:#9eb8d1;font-size:11px}.preview-summary{display:flex;align-items:center;gap:12px;flex-wrap:wrap;padding:10px 12px;background:#101f34}.preview-summary>span{font-size:11px;padding:4px 8px;border:1px solid #39516e}.preview-summary .ready{color:#62dfb4;border-color:#2d745f;background:#102c28}.preview-summary .blocked{color:#f0b273;border-color:#805d3b;background:#30261d}.preview-summary small{color:#87a1be}.preview-issues{margin:0;padding:10px 12px 10px 30px;color:#efb273;background:#30251d;line-height:1.7}.flow-grid{display:grid;grid-template-columns:minmax(0,1.55fr) minmax(260px,.75fr);gap:14px}.flow-chain{display:grid;gap:10px;max-height:780px;overflow:auto;padding-right:5px}.flow-chain article{border:1px solid #2d526e;border-left:4px solid #39bcd2;background:#102137;padding:12px}.flow-chain article.muted{opacity:.72}.flow-chain article>header{display:flex;align-items:center;gap:10px}.flow-chain article>header>span{display:grid;place-items:center;min-width:27px;height:27px;border-radius:50%;background:#164457;color:#a7eff7;font:600 12px Consolas,monospace}.flow-chain article>header>div{display:grid;gap:3px}.flow-chain code{color:#65c8dc;font-size:11px}.flow-chain dl{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:7px 14px;margin:11px 0}.flow-chain dl>div{display:grid;grid-template-columns:42px minmax(0,1fr);gap:7px}.flow-chain dt{color:#7793b1}.flow-chain dd{margin:0;color:#c2d7e8;overflow-wrap:anywhere}.flow-chain article>p{display:flex;gap:5px;flex-wrap:wrap;margin:0}.flow-chain article>p span{font-size:10px;padding:3px 6px;border:1px solid #2a5365;color:#89cddd;background:#0b2635}.flow-variables{display:grid;gap:12px;align-content:start;max-height:780px;overflow:auto}.flow-variables section{border:1px solid #2c4562;background:#0a1729;padding:12px}.flow-variables>p{font-size:12px;line-height:1.7}.file-button{display:inline-flex;align-items:center;padding:8px 11px;border:1px solid #344664;border-radius:5px;cursor:pointer;font-size:12px;color:#bdd0e5}.file-button input{display:none}@media(max-width:1050px){.template-lists,.contract-columns,.flow-grid,.guard-panel{grid-template-columns:1fr}.flow-chain,.flow-variables{max-height:none}.task-flow-panel>header{display:grid}.flow-selects{align-items:stretch}.flow-selects label{min-width:180px}}@media(max-width:650px){.template-heading,.editor-title{display:grid}.template-lists article{display:grid}.flow-chain dl{grid-template-columns:1fr}}
</style>
