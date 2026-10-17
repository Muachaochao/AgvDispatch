<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import TaskRuntimeHistory from './TaskRuntimeHistory.vue';
import TaskRecoveryPanel from './TaskRecoveryPanel.vue';
import TaskCancelDialog from './components/TaskCancelDialog.vue';
import type { TaskDetail } from './types';
import { label } from './state';
import { shortTime } from './dashboard';
import { boolText, conditionLabel, diagnosticFresh, stepStatusLabel } from './task-diagnostics';

const props = defineProps<{ detail: TaskDetail; fresh: boolean; now: number }>();
const selected = ref<number | null>(null);
watch(() => props.detail.task.taskId, () => { selected.value = null; });
const step = computed(() => props.detail.steps.find(s => s.sequence === selected.value) ?? props.detail.steps.find(s => s.isCurrent) ?? props.detail.steps.at(-1));
const live = computed(() => diagnosticFresh(props.fresh, props.detail.diagnosticsObservedAt, props.now));
const conditions = computed(() => step.value?.progressConditions ?? []);
const commands = computed(() => props.detail.commands?.filter(c => c.stepId === step.value?.stepId) ?? []);
const checkpoints = computed(() => props.detail.handshakes?.filter(c=>c.stepId===step.value?.stepId) ?? []);
const events = computed(() => props.detail.executionTimeline ?? []);
</script>

<template>
  <div class="execution-detail">
    <div class="detail-summary"><div><span>任务编号</span><b class="cyan mono">{{ detail.task.taskId }}</b></div><div><span>业务状态</span><b>{{ label(detail.task.stage) }}</b></div><div><span>来源工位 / 车辆</span><b>{{ detail.task.stationId }} / {{ detail.task.vehicleId ?? '待分配' }}</b></div></div>
    <p class="muted-note">握手模式：{{ detail.handshakeMode==='PersistentExecution'?'持久化执行':'原执行器＋变化观测' }} · 配置契约：{{ detail.handshakeContractVersion||'未配置新契约' }}</p>
    <p class="muted-note">后台观察：{{ detail.diagnosticsObservedAt ? shortTime(detail.diagnosticsObservedAt) : '旧版后台未提供诊断时间' }} · {{ live ? '诊断连接有效' : '非当前有效观察，请重新连接或等待刷新' }}</p>
    <div class="waiting-callout" role="status">{{ live ? detail.waitingExplanation : '当前诊断已过期或接口未提供诊断时间；以下内容仅供回看，不代表现场当前状态。' }}</div>
    <nav class="execution-steps" aria-label="九步业务流程"><button v-for="s in detail.steps" :key="s.sequence" :class="{ selected: step?.sequence===s.sequence, current: s.isCurrent, done:s.status==='Completed' }" :aria-pressed="step?.sequence===s.sequence" @click="selected=s.sequence"><span>{{ s.sequence }}</span><b>{{ label(s.stepType) }}</b><small>{{ s.isCurrent ? '当前 · ' : '' }}{{ stepStatusLabel(s.status,label(s.status)) }}</small></button></nav>
    <section v-if="step" class="execution-inspection">
      <h3>第{{ step.sequence }}步 · {{ label(step.stepType) }}</h3>
      <dl><dt>观测阶段</dt><dd>{{ step.observedPhase || '后台未提供；不根据前端动画推断' }}</dd><dt>现有执行器完成依据</dt><dd>{{ step.completionRequirement || '后台未提供' }}</dd><dt>耗时 / 超时</dt><dd>{{ step.elapsedText || '未提供' }} · {{ step.timeoutText || '未提供' }}</dd><dt>原命令状态</dt><dd>{{ step.commandStateExplanation || '未提供' }}</dd></dl>
      <p v-if="step.sourceSite || step.destinationSite" class="muted-note">路线：{{ step.sourceSite || '—' }} → {{ step.destinationSite || '—' }} · 厂家任务：{{ step.vendorTaskId || '未返回' }}</p>
      <p v-if="step.errorMessage" class="inline-warning">最近步骤错误：{{ step.errorMessage }}</p>
      <p v-if="step.evidenceBoundary" class="execution-boundary"><b>证据边界 / 待核定项：</b>{{ step.evidenceBoundary }}</p>
      <h4>持久化握手阶段</h4>
      <article v-for="c in checkpoints" :key="c.stepId" class="execution-boundary"><b>{{ c.phase }}</b><p>周期 {{ c.cycleId }} · 契约 {{ c.contractVersion }} · 修订 {{ c.revision }}</p><p>{{ c.reason }}</p><p v-if="c.pendingSignal">待核对写入：{{ c.pendingSignal }} = {{ boolText(c.pendingValue) }}。结果未确定，禁止自动重发。</p><small>首次保存 {{ shortTime(c.startedAt) }} · 最近保存 {{ shortTime(c.updatedAt) }}</small><details><summary>查看本步骤原始基线</summary><p v-for="b in c.baseline.filter(b=>b.stationId!=='__context')" :key="b.stationId+b.variableName">{{ b.stationId }}/{{ b.variableName }}：{{ boolText(b.value) }} · {{ label(b.quality) }} · {{ shortTime(b.observedAt) }}</p></details></article>
      <p v-if="!checkpoints.length" class="muted-note">本步骤未返回持久化握手检查点。新契约执行器需匹配的PLC程序及显式启用；不把观测解释当作已执行阶段。</p>
      <h4>当前步骤条件</h4>
      <p class="muted-note">{{ checkpoints.length?'以下为持久化阶段对应的条件或写入目标。':'以下为原执行器的当前观察解释。' }} 满足显示项不等于PLC业务接收或机械完成；位置/命令等其他证据仍由后台核验。</p>
      <div v-if="conditions.length" class="execution-table"><table class="dialog-table"><thead><tr><th>规则 / 信号</th><th>期望 / 实际</th><th>质量 / 时间</th><th>结果</th><th>解释</th></tr></thead><tbody><tr v-for="c in conditions" :key="c.stationId+c.ruleId"><td><b>{{ c.name }}</b><small>{{ c.ruleId }}<br>{{ c.stationId }} · {{ c.address }}<br>{{ c.role }}</small></td><td>{{ boolText(c.expectedValue) }} / {{ boolText(c.currentValue) }}</td><td>{{ label(c.quality) }}<small>{{ c.observedAt ? shortTime(c.observedAt) : '采集时间未提供' }} · v{{ c.signalVersion ?? '—' }}</small></td><td :class="live && c.satisfied && !c.role.includes('待人工核对') ? 'green-text' : 'amber-text'">{{ conditionLabel(c, live) }}</td><td>{{ c.reason }}</td></tr></tbody></table></div>
      <p v-else class="muted-note">此步骤没有当前PLC条件。未激活、历史步骤不使用实时PLC值重新判定；运输步骤查看原命令及后台完成依据。</p>
      <h4>本步骤车辆命令</h4>
      <div v-if="commands.length" class="execution-table"><table class="dialog-table"><thead><tr><th>命令</th><th>持久化状态</th><th>次数</th><th>厂家身份</th><th>最近错误</th></tr></thead><tbody><tr v-for="c in commands" :key="c.commandId"><td>{{ c.commandId }}</td><td>{{ c.status==='Sent' ? '已发送并返回身份' : label(c.status) }}</td><td>{{ c.attemptCount }}</td><td>{{ c.vendorCommandId || '—' }}</td><td>{{ c.lastError || '—' }}</td></tr></tbody></table></div><p v-else class="muted-note">未返回本步骤车端命令；不表示动作已完成。取放确认步骤不额外发机械动作。</p>
    </section>
    <h3 class="subheading">当前保留的资源归属</h3><p v-if="!detail.resources?.length" class="muted-note">未返回资源记录；不据此推断现场区域一定空闲。</p><ul v-else class="execution-resources"><li v-for="r in detail.resources" :key="r.resourceId">{{ r.resourceId }} · {{ r.vehicleId }} · {{ r.heartbeatExpired ? '心跳租期已过，不能推断物理出区' : '后台持有记录' }}</li></ul>
    <section v-if="detail.handover" class="execution-boundary"><b>离站及资源交接诊断</b><p>{{ detail.handover.reason }}</p><p>返程命令完成：{{ detail.handover.returnCommandCompleted?'已记录':'未记录' }} · 保守返程定位核对：{{ detail.handover.exitPhysicallyVerified?'已记录':'尚未记录' }}</p><p>当前持有：{{ detail.handover.heldResources.join('、') || '无返回记录' }}</p><details v-if="detail.handover.recordedReleaseReasons.length"><summary>查看后台已记录的释放依据</summary><p v-for="(r,i) in detail.handover.recordedReleaseReasons" :key="i">{{ r }}</p></details></section>
    <h3 class="subheading">关键事件时间线</h3><p class="muted-note">来自数据库时间戳及持久化执行证据，不是动画。显示最近{{ events.length }}项 / 共{{ detail.executionTimelineTotal ?? events.length }}项；同时间排序不证明跨设备因果。未采集的历史不补造。</p>
    <ol v-if="events.length" class="execution-events"><li v-for="e in events" :key="e.id"><time>{{ shortTime(e.occurredAt) }}</time><div><b>{{ e.title }}</b><p>{{ e.summary }}</p><small>{{ e.source }}<span v-if="e.stepId"> · 步骤 {{ detail.steps.find(s=>s.stepId===e.stepId)?.sequence ?? e.stepId }}</span></small></div></li></ol><p v-else class="muted-note">尚无执行事件返回，或旧版后台未提供；不生成示例轨迹。</p>
    <TaskCancelDialog :task-id="detail.task.taskId" :stage="detail.task.stage" />
    <TaskRuntimeHistory :task-id="detail.task.taskId" :fresh="live" />
    <TaskRecoveryPanel :task-id="detail.task.taskId" :fresh="live" :now="now" />
  </div>
</template>

<style scoped>
.execution-steps{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:8px;margin:18px 0}.execution-steps button{display:grid;grid-template-columns:24px 1fr;gap:5px;text-align:left;white-space:normal;padding:12px;min-width:0}.execution-steps small{grid-column:2;color:#97afca}.execution-steps .current{border-color:#35bdd7}.execution-steps .selected{background:#15394d;outline:1px solid #67cee2}.execution-steps .done>span{color:#31d3aa}.execution-inspection{padding:18px;background:#101e35;border:1px solid #2d4564}.execution-inspection h3,.execution-inspection h4{color:#cce1f3;margin:0 0 14px}.execution-inspection h4{margin-top:24px}.execution-inspection dl{display:grid;grid-template-columns:145px 1fr;gap:10px;font-size:12px;line-height:1.8}.execution-inspection dt{color:#8ba6c5}.execution-inspection dd{margin:0;overflow-wrap:anywhere}.execution-boundary{padding:12px;border-left:3px solid #c79c51;background:#2a2630;color:#dfc69d;font-size:12px;line-height:1.8;margin:16px 0}.execution-table{overflow-x:auto}.execution-table table{min-width:620px}.execution-table small{display:block;color:#8ba6c5;font-size:10px;line-height:1.7;margin-top:5px}.execution-table td{overflow-wrap:anywhere}.execution-events{list-style:none;padding:0}.execution-events li{display:grid;grid-template-columns:130px 1fr;gap:14px;padding:14px 0;border-bottom:1px solid #253957;font-size:12px}.execution-events time,.execution-events small{color:#88a6c6}.execution-events p{line-height:1.8;margin:6px 0}.execution-events div{overflow-wrap:anywhere}.execution-resources{font-size:12px;line-height:2;color:#a9c2dd}@media(max-width:650px){.execution-steps{grid-template-columns:1fr 1fr}.execution-inspection{padding:12px}.execution-inspection dl{grid-template-columns:1fr;gap:4px}.execution-inspection dd{margin-bottom:9px}.execution-events li{grid-template-columns:1fr;gap:7px}}
</style>
