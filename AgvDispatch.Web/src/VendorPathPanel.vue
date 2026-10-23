<script setup lang="ts">
import {computed,ref,watch} from 'vue';
import ConsoleDialog from './ConsoleDialog.vue';import ActionIcon from './components/ActionIcon.vue';
import type {Edge,Definition} from './workbench';
import {vendorRows,bindingIssues,reverseVendorPaths,compareVendorRows,valueText,evidenceValid,pairedVendorRows} from './vendor-path';
const props=defineProps<{edge:Edge;definition:Definition;backend:boolean}>();
const open=ref(false),reverseId=ref(''),downloadError=ref('');
const rows=computed(()=>vendorRows(props.edge.vendorPath)),issues=computed(()=>bindingIssues(props.edge,props.definition));
const reverseCandidates=computed(()=>reverseVendorPaths(props.edge,props.definition));
const reverse=computed(()=>reverseCandidates.value.find(e=>e.id===reverseId.value));
const pairs=computed(()=>pairedVendorRows(props.edge.vendorPath,reverse.value?.vendorPath));
const groups=computed(()=>[...new Set(pairs.value.map(r=>r.left.group))]);
const common=computed(()=>rows.value.filter(r=>['direction','movestyle','maxspeed'].includes(r.key)));
watch(()=>props.edge.id,()=>{open.value=false;downloadError.value='';});
watch(()=>reverseCandidates.value.map(e=>e.id).join('|'),()=>{reverseId.value=reverseCandidates.value.length===1?reverseCandidates.value[0]!.id:'';},{immediate:true});
async function original(){const sha=props.edge.vendorPath?.sourceSha256;if(!props.backend||!sha)return;downloadError.value='';
 try{const response=await fetch('/api/maps/vendor-source/'+encodeURIComponent(sha),{cache:'no-store'});if(!response.ok){const body=await response.json();throw Error(body.detail??body.error??'原图未取得');}
 const bytes=await response.blob(),url=URL.createObjectURL(bytes),a=document.createElement('a');a.href=url;a.download=sha+'.smap';a.click();setTimeout(()=>URL.revokeObjectURL(url),0);
 }catch(e){downloadError.value=String(e);}}
</script>
<template><section class="vendor-path-summary"><h4>厂家道路属性 · 只读</h4><p>原图设置与调度参数分别保存；未修改车辆地图或实际行驶参数。</p>
<p v-for="r in common" :key="r.key">{{r.label}}：{{valueText(r)}}</p><p v-if="issues.length" class="amber-text">{{issues[0]}}</p>
<button @click="open=true"><ActionIcon name="properties"/>查看厂家属性／双向核对</button>
<ConsoleDialog v-if="open" title="厂家道路原始属性与调度对照（只读）" wide @close="open=false"><div class="vendor-path-detail">
<p>名称说明用于识别字段，未核定本车固件的单位、继承与生效规则。传感器未设置不等于关闭；本页没有编辑或下发入口。</p>
<div class="vendor-binding"><b>当前调度道路：{{edge.id}} · {{edge.from}} → {{edge.to}}</b><p>调度限速 {{edge.speedLimit}} m/s · {{edge.bidirectional?'双向':'单向'}} · {{edge.enabled?'启用':'停用'}} · 资源 {{edge.resourceIds.join('、')||'未关联'}}</p><p>已提取为调度结构：端点及已支持的曲线几何。厂家运动、到点、载荷、传感器和避障参数均为原始证据，未映射至车端控制。</p></div>
<p v-for="(message,i) in issues" :key="i" class="inline-warning">{{message}}</p>
<template v-if="edge.vendorPath&&evidenceValid(edge.vendorPath)"><p>来源：{{definition.robokitSource?.mapName||'整图信息未取得'}} · 格式 {{definition.robokitSource?.formatVersion||'未知'}} · 原曲线序号 {{edge.vendorPath.curveIndex}}（从0起） · {{edge.vendorPath.className}} · 名称 {{edge.vendorPath.instanceName||'未提供'}}</p>
<p>原厂家方向：{{edge.vendorPath.fromSite}} → {{edge.vendorPath.toSite}}</p><details><summary>来源摘要及留存边界</summary><p>原文件 SHA256：{{edge.vendorPath.sourceSha256}}</p><p>二维部分 SHA256：{{edge.vendorPath.planeSha256}}</p><p>来源为导入原图或随方案携带的证据，未取得车辆当前读回与固件兼容确认；任意JSON中的摘要不能独立证明来源真实。</p></details>
<button :disabled="!backend" @click="original"><ActionIcon name="download"/>下载后台留存的原SMAP</button><p v-if="!backend">需连接留存原图的Web后台下载；跨电脑方案JSON不携带完整原SMAP。</p><p v-if="downloadError" role="status">{{downloadError}}</p></template>
<label>反向原记录 <select v-model="reverseId" aria-label="选择反向厂家道路"><option value="">{{reverseCandidates.length?'请选择一条反向记录':'未取得反向原记录'}}</option><option v-for="r in reverseCandidates" :key="r.id" :value="r.id">{{r.id}} · {{r.vendorPath?.fromSite}} → {{r.vendorPath?.toSite}} · 原序号{{r.vendorPath?.curveIndex}}</option></select></label>
<p>按同源厂家端点列出候选，不自动合并平行或反向曲线。未取得反向记录不代表车辆不能反向通行。</p>
<p v-for="(message,i) in reverse?bindingIssues(reverse,definition):[]" :key="'reverse'+i" class="inline-warning">反向：{{message}}</p>
<section v-for="group in groups" :key="group"><h3>{{group}}</h3><div class="vendor-table"><table class="dialog-table"><thead><tr><th>字段／说明</th><th>当前原方向：状态／类型／值</th><th>所选反向：状态／类型／值</th><th>核对与用途</th></tr></thead><tbody><tr v-for="{left:r,right:other} in pairs.filter(r=>r.left.group===group)" :key="r.key"><td><b>{{r.key}}</b><small>{{r.label}}</small></td><td><b>{{r.state==='Explicit'?'显式设置':r.state==='Unset'?'未设置':'未知'}}</b><small>类型：{{r.type}}</small><p>{{valueText(r)}}</p><details v-if="r.raw&&r.raw!=='[]'"><summary>原始条目</summary><pre>{{r.raw}}</pre></details></td><td><template v-if="other"><b>{{other?.state==='Explicit'?'显式设置':other?.state==='Unset'?'未设置':'未知'}}</b><small>类型：{{other?.type}}</small><p>{{valueText(other)}}</p></template><span v-else>未取得</span></td><td><b>{{compareVendorRows(r,other)}}</b><small>{{r.reason}}</small><small>{{r.known?'已识别字段名；仅只读展示':'未支持字段；原样保留'}}</small></td></tr></tbody></table></div></section>
<details v-if="edge.vendorPath"><summary>当前方向完整原始property内容</summary><pre>{{edge.vendorPath.propertyListPresent?edge.vendorPath.rawPropertiesJson:'原曲线未包含property字段'}}</pre></details>
</div></ConsoleDialog></section></template>
<style scoped>.vendor-path-summary{border-top:1px solid #385271;padding-top:12px;font-size:12px;line-height:1.8}.vendor-path-summary h4{color:#8edee9}.vendor-path-summary p{margin:5px 0;overflow-wrap:anywhere}.vendor-path-detail{font-size:12px;line-height:1.9}.vendor-path-detail p{margin:10px 0;overflow-wrap:anywhere}.vendor-binding{padding:12px;border:1px solid #385271;margin:14px 0}.vendor-path-detail h3{margin:22px 0 10px}.vendor-path-detail small{display:block;color:#9fb8cf;white-space:normal}.vendor-path-detail label{display:flex;flex-wrap:wrap;align-items:center;gap:10px;margin-top:16px}.vendor-path-detail select{max-width:100%}.vendor-table{overflow:auto}.vendor-table table{min-width:650px;table-layout:fixed}.vendor-table td{overflow-wrap:anywhere;vertical-align:top}.vendor-path-detail pre{white-space:pre-wrap;overflow-wrap:anywhere;max-height:240px;overflow:auto;border:1px solid #385271;padding:8px}.vendor-path-detail summary{cursor:pointer}.vendor-path-detail button{margin:8px 0}</style>
