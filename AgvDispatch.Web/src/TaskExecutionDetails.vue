<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import TaskRuntimeHistory from './TaskRuntimeHistory.vue';
import WaitingBayDeparturePanel from './WaitingBayDeparturePanel.vue';
import TaskRouteMap from './TaskRouteMap.vue';
import {useDispatchStore} from './stores/dispatch';
const store=useDispatchStore();
import TaskCancelDialog from './components/TaskCancelDialog.vue';
import TaskSwitchPanel from './components/TaskSwitchPanel.vue';
import { taskStepName } from './task-step-labels';
import type { TaskDetail } from './types';
import { label } from './state';
import { shortTime } from './dashboard';
import { boolText, conditionLabel, diagnosticFresh, stepStatusLabel } from './task-diagnostics';
import {
  actionStateLabel,
  contractLabel,
  failurePolicyLabel,
  handshakePhaseLabel,
  industrialText,
  observedPhaseLabel,
  qualityLabel,
  signalLabel,
  timeoutActionLabel
} from './operator-labels';

const props = defineProps<{ detail: TaskDetail; fresh: boolean; now: number }>();
const selected = ref<number | null>(null);
const pauseReason = ref('');
const pauseBusy = ref(false);
const pauseMessage = ref('');
watch(() => props.detail.task.taskId, () => { selected.value = null; pauseReason.value = ''; pauseMessage.value = ''; });
const step = computed(() => props.detail.steps.find(s => s.sequence === selected.value) ?? props.detail.steps.find(s => s.isCurrent) ?? props.detail.steps.at(-1));
const live = computed(() => diagnosticFresh(props.fresh, props.detail.diagnosticsObservedAt, props.now));
const conditions = computed(() => step.value?.progressConditions ?? []);
const commands = computed(() => props.detail.commands?.filter(c => c.stepId === step.value?.stepId) ?? []);
const checkpoints = computed(() => props.detail.handshakes?.filter(c=>c.stepId===step.value?.stepId) ?? []);
const events = computed(() => props.detail.executionTimeline ?? []);
const paused = computed(() => props.detail.pause?.active === true);
const pausable = computed(() => !paused.value && props.fresh && store.fresh && store.cancellationEnabled &&
  !['Created','Completed','Cancelled','Faulted','CancellationPending','CancellationReview'].includes(props.detail.task.stage) &&
  step.value?.status === 'Ready');
const restoreSignals = computed(() => {
  const rows = conditions.value
    .filter(condition => !condition.satisfied || condition.currentValue === null || condition.quality !== 'Good')
    .map(condition => ({
      key: condition.stationId + condition.variableName,
      label: signalLabel(condition.variableName, condition.name),
      stationId: condition.stationId,
      expected: boolText(condition.expectedValue),
      actual: boolText(condition.currentValue),
      quality: qualityLabel(condition.quality),
      reason: condition.reason
    }));
  for (const checkpoint of checkpoints.value) {
    if (!checkpoint.pendingSignal) continue;
    rows.push({
      key: checkpoint.stepId + checkpoint.pendingSignal,
      label: signalLabel(checkpoint.pendingSignal, checkpoint.pendingSignal),
      stationId: '待确认写入',
      expected: boolText(checkpoint.pendingValue),
      actual: '结果待核对',
      quality: '—',
      reason: '原写入结果未确定，禁止自动重发；现场核对后才能继续。'
    });
  }
  return rows;
});
const currentActionText = computed(() => {
  if (!step.value) return '请选择业务步骤查看当前操作。';
  if (step.value.status === 'Hold') return '任务已暂停并冻结后续执行。先核对车辆、载荷、PLC 和路权，再进入异常恢复。';
  if (['Faulted', 'ManualIntervention'].includes(step.value.status)) return '本步骤需要现场处理。不要重复下发或强制跳步，先按下方证据逐项核对。';
  if (restoreSignals.value.length) return `请先恢复或核对 ${restoreSignals.value.length} 项条件；满足后系统才会继续本步骤。`;
  if (step.value.isCurrent && ['Ready', 'Waiting', 'Pending'].includes(step.value.status)) return '正在等待后台或设备反馈；当前没有未满足的信号条件。';
  if (step.value.status === 'Completed') return '本步骤已完成。下一条信息仅用于回看，不表示现场仍处于同一状态。';
  return '继续观察本步骤的实际执行反馈。';
});

function pauseRequestId(){
  return typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID().replaceAll('-','')
    : `${Date.now()}${Math.random().toString(16).slice(2)}`.padEnd(32,'0').slice(0,32);
}

async function pauseTask(){
  if (!pausable.value || pauseBusy.value || pauseReason.value.trim().length < 10) return;
  pauseBusy.value = true; pauseMessage.value = '';
  try {
    const result = await store.api<{accepted:boolean;message:string}>(`/api/tasks/${encodeURIComponent(props.detail.task.taskId)}/pause`, {
      requestId:pauseRequestId(), taskId:props.detail.task.taskId, expectedStage:props.detail.task.stage, reason:pauseReason.value.trim()
    });
    pauseMessage.value = result.message;
    if (result.accepted) pauseReason.value = '';
  } catch (error) { pauseMessage.value = `暂停结果未确认：${String(error)}`; }
  finally { pauseBusy.value = false; }
}

async function resumePausedTask(){
  if (!paused.value || pauseBusy.value || pauseReason.value.trim().length < 10) return;
  pauseBusy.value = true; pauseMessage.value = '';
  try {
    const result = await store.api<{accepted:boolean;message:string}>(`/api/tasks/${encodeURIComponent(props.detail.task.taskId)}/resume-paused`, {
      requestId:pauseRequestId(), taskId:props.detail.task.taskId, reason:pauseReason.value.trim()
    });
    pauseMessage.value = result.message;
    if (result.accepted) pauseReason.value = '';
  } catch (error) { pauseMessage.value = `恢复结果未确认：${String(error)}`; }
  finally { pauseBusy.value = false; }
}
</script>

<template>
  <div class="execution-detail">
    <div class="detail-summary"><div><span>任务编号</span><b class="cyan mono">{{ detail.task.taskId }}</b></div><div><span>业务状态</span><b>{{ label(detail.task.stage) }}</b></div><div><span>来源工位 / 车辆</span><b>{{ detail.task.stationId }} / {{ detail.task.vehicleId ?? '待分配' }}</b></div></div>
    <section v-if="detail.template" class="execution-template"><header><b>任务模板快照</b><span>已冻结的本次业务编排</span></header><p>输入：{{ detail.template.inputs.map(i=>i.label).join('、') }}</p><p>输出：{{ detail.template.outputs.map(i=>i.label).join('、') }}</p><p>{{ detail.template.nodes.length }}个节点 · {{ detail.template.connections.length }}条类型化连接 · {{ detail.template.guards.length }}条异常分支；机械、交通适配和握手节点禁止默认自动重试。</p><details><summary>查看模板原始编号与变量字段</summary><p>模板 {{ detail.template.id }} · 版本 {{ detail.template.version }} · 执行引擎 {{ detail.template.engine }}</p><p>输入字段：{{ detail.template.inputs.map(i=>i.key).join('、') }}</p><p>输出字段：{{ detail.template.outputs.map(i=>i.key).join('、') }}</p></details></section>
    <section v-if="detail.dependencies?.length" class="execution-template"><header><b>前置任务门禁</b><span>{{ detail.dependencies.every(d=>d.completed) ? '已满足' : '等待完成' }}</span></header><p v-for="dependency in detail.dependencies" :key="dependency.taskId"><code>{{ dependency.taskId }}</code> · {{ dependency.completed ? '已完成' : dependency.exists ? label(dependency.stage) : '任务不存在' }}<span v-if="dependency.updatedAt"> · {{ shortTime(dependency.updatedAt) }}</span></p><p>只有前置任务全部完成后才允许本任务派车；前置任务取消、故障或缺失不会被视为完成。</p></section>
    <section v-if="pausable || paused || detail.pause" class="execution-template pause-panel"><header><b>任务暂停</b><span>{{ paused ? '已冻结' : detail.pause ? '历史暂停记录' : '可暂停就绪步骤' }}</span></header><p v-if="detail.pause">暂停依据：{{ industrialText(detail.pause.reason) }} · {{ shortTime(detail.pause.requestedAt) }} · {{ detail.pause.requestedBy }}</p><p v-if="detail.pause?.resumedAt">已于 {{ shortTime(detail.pause.resumedAt) }} 恢复：{{ industrialText(detail.pause.resumeReason) }}</p><p>暂停只适用于等待协调的步骤；动作已发送、执行中或结果未知时必须走取消与恢复核对。</p><div class="pause-actions"><input v-model="pauseReason" maxlength="1000" :placeholder="paused ? '填写至少10字恢复依据' : '填写至少10字暂停依据'" :disabled="pauseBusy"/><button v-if="!paused" :disabled="!pausable || pauseBusy || pauseReason.trim().length<10" @click="pauseTask">暂停任务</button><button v-else :disabled="pauseBusy || pauseReason.trim().length<10" @click="resumePausedTask">显式恢复</button></div><p v-if="pauseMessage" role="status">{{ industrialText(pauseMessage) }}</p></section>
    <p class="muted-note">握手模式：{{ detail.handshakeMode==='PersistentExecution'?'持久化执行':'原执行器＋变化观测' }} · 配置契约：{{ contractLabel(detail.handshakeContractVersion) }}</p>
    <p class="muted-note">后台观察：{{ detail.diagnosticsObservedAt ? shortTime(detail.diagnosticsObservedAt) : '旧版后台未提供诊断时间' }} · {{ live ? '诊断连接有效' : '非当前有效观察，请重新连接或等待刷新' }}</p>
    <div class="waiting-callout" role="status">{{ live ? detail.waitingExplanation : '当前诊断已过期或接口未提供诊断时间；以下内容仅供回看，不代表现场当前状态。' }}</div>
    <section v-if="step" class="task-current-action" :class="{ attention: restoreSignals.length || ['Hold', 'Faulted', 'ManualIntervention'].includes(step.status) }">
      <header><div><small>{{ step.isCurrent ? '当前所在步骤' : '当前查看步骤' }}</small><h3>第 {{ step.sequence }} 步 · {{ taskStepName(step.stepType) }}</h3></div><span>{{ stepStatusLabel(step.status, label(step.status)) }}</span></header>
      <small class="action-label">下一步操作</small><p class="next-action-text">{{ currentActionText }}</p>
      <div v-if="restoreSignals.length" class="restore-signals">
        <b>需要恢复或核对的信号</b>
        <ul>
          <li v-for="signal in restoreSignals" :key="signal.key">
            <strong>{{ signal.label }}</strong>
            <span>{{ signal.stationId }} · 期望 {{ signal.expected }} / 实际 {{ signal.actual }} · 数据{{ signal.quality }}</span>
            <small>{{ signal.reason }}</small>
          </li>
        </ul>
      </div>
      <p v-else class="restore-clear">当前没有未满足的 PLC 信号条件；仍需以车辆位置、命令结果和现场核对为准。</p>
    </section>
    <div v-if="!store.demoMode" class="execution-actions">
      <TaskCancelDialog :task-id="detail.task.taskId" :stage="detail.task.stage" :fresh="live" />
      <RouterLink :to="{path:'/recovery',query:{taskId:detail.task.taskId}}">核对 / 恢复原任务</RouterLink>
      <a :href="`/api/tasks/${encodeURIComponent(detail.task.taskId)}/diagnostics/export`" download>导出原任务诊断包</a>
      <p>取消受理 → 查询原任务结束状态 → 现场核对载荷、PLC 和占用 → 恢复批次放行。取消不是急停。</p>
    </div>
    <TaskSwitchPanel :task-id="detail.task.taskId" :stage="detail.task.stage" :live="live" />
    <TaskRouteMap v-if="step" :detail="detail" :sequence="step.sequence"/>
    <nav class="execution-steps" aria-label="九步业务流程"><button v-for="s in detail.steps" :key="s.sequence" :class="{ selected: step?.sequence===s.sequence, current: s.isCurrent, done:s.status==='Completed' }" :aria-pressed="step?.sequence===s.sequence" @click="selected=s.sequence"><span>{{ s.sequence }}</span><b>{{ taskStepName(s.stepType) }}</b><small>{{ s.isCurrent ? '当前 · ' : '' }}{{ stepStatusLabel(s.status,label(s.status)) }}</small></button></nav>
    <section v-if="step" class="execution-inspection">
      <h3>第{{ step.sequence }}步 · {{ taskStepName(step.stepType) }}</h3>
      <dl><dt>后台执行阶段</dt><dd>{{ observedPhaseLabel(step.observedPhase) }}</dd><dt>现有执行器完成依据</dt><dd>{{ step.completionRequirement || '后台未提供' }}</dd><dt>任务模板节点</dt><dd>{{ step.templateNodeId ? '已绑定任务模板节点' : '旧任务未保存模板快照' }}<details v-if="step.templateNodeId || step.observedPhase"><summary>查看模板节点与后台原始阶段</summary><p v-if="step.templateNodeId">模板节点：{{step.templateNodeId}}</p><p v-if="step.observedPhase">原始阶段：{{step.observedPhase}}</p></details></dd><dt>失败 / 超时策略</dt><dd>{{ failurePolicyLabel(step.failurePolicy) }} / {{ timeoutActionLabel(step.timeoutAction) }}<small>运行禁止自动重试；冻结时保留路权和设备责任</small></dd><dt>耗时 / 超时</dt><dd>{{ step.elapsedText || '未提供' }} · {{ step.timeoutText || '未提供' }}</dd><dt>原命令状态</dt><dd>{{ step.commandStateExplanation || '未提供' }}</dd></dl>
      <p v-if="step.sourceSite || step.destinationSite" class="muted-note">路线：{{ step.sourceSite || '—' }} → {{ step.destinationSite || '—' }} · 厂家任务：{{ step.vendorTaskId || '未返回' }}</p>
      <p v-if="step.errorMessage" class="inline-warning">最近步骤错误：{{ industrialText(step.errorMessage) }}</p>
      <p v-if="step.evidenceBoundary" class="execution-boundary"><b>证据边界 / 待核定项：</b>{{ step.evidenceBoundary }}</p>
      <h4>持久化握手阶段</h4>
      <article v-for="c in checkpoints" :key="c.stepId" class="execution-boundary"><b>{{ handshakePhaseLabel(c.phase) }}</b><p>周期 {{ c.cycleId }} · 契约 {{ contractLabel(c.contractVersion) }} · 修订 {{ c.revision }}</p><p>{{ industrialText(c.reason) }}</p><p v-if="c.pendingSignal">待核对写入：{{ signalLabel(c.pendingSignal) }} = {{ boolText(c.pendingValue) }}。结果未确定，禁止自动重发。</p><small>首次保存 {{ shortTime(c.startedAt) }} · 最近保存 {{ shortTime(c.updatedAt) }}</small><details><summary>查看本步骤原始基线与契约字段</summary><p>原始契约：{{c.contractVersion}}</p><p v-for="b in c.baseline.filter(b=>b.stationId!=='__context')" :key="b.stationId+b.variableName">{{ signalLabel(b.variableName) }}：{{ boolText(b.value) }} · 数据{{ qualityLabel(b.quality) }} · {{ shortTime(b.observedAt) }}<small>{{ b.stationId }} · {{ b.variableName }}</small></p></details></article>
      <p v-if="!checkpoints.length" class="muted-note">本步骤未返回持久化握手检查点。新契约执行器需匹配的PLC程序及显式启用；不把观测解释当作已执行阶段。</p>
      <h4>当前步骤条件</h4>
      <p class="muted-note">{{ checkpoints.length?'以下为持久化阶段对应的条件或写入目标。':'以下为原执行器的当前观察解释。' }} 满足显示项不等于PLC业务接收或机械完成；位置/命令等其他证据仍由后台核验。</p>
      <div v-if="conditions.length" class="execution-table"><table class="dialog-table"><thead><tr><th>信号</th><th>期望 / 实际</th><th>质量 / 时间</th><th>结果</th><th>解释</th></tr></thead><tbody><tr v-for="c in conditions" :key="c.stationId+c.ruleId"><td><b>{{ signalLabel(c.variableName,c.name) }}</b><small>{{ c.stationId }} · {{ c.address }}</small><details><summary>规则与角色</summary><small>{{ c.ruleId }} · {{ c.role }}</small></details></td><td>{{ boolText(c.expectedValue) }} / {{ boolText(c.currentValue) }}</td><td>{{ qualityLabel(c.quality) }}<small>{{ c.observedAt ? shortTime(c.observedAt) : '采集时间未提供' }} · v{{ c.signalVersion ?? '—' }}</small></td><td :class="live && c.satisfied && !c.role.includes('待人工核对') ? 'green-text' : 'amber-text'">{{ conditionLabel(c, live) }}</td><td>{{ c.reason }}</td></tr></tbody></table></div>
      <p v-else class="muted-note">此步骤没有当前PLC条件。未激活、历史步骤不使用实时PLC值重新判定；运输步骤查看原命令及后台完成依据。</p>
      <h4>本步骤车辆命令</h4>
      <WaitingBayDeparturePanel v-if="step.stepType==='EnterDumpingPosition'&&step.stepId" :task-id="detail.task.taskId" :step-id="step.stepId" :resource-ids="detail.resources?.map(r=>r.resourceId)??[]" :live="live"/>
      <div v-if="commands.length" class="execution-table"><table class="dialog-table"><thead><tr><th>命令</th><th>执行状态</th><th>次数</th><th>厂家身份</th><th>最近错误</th></tr></thead><tbody><tr v-for="c in commands" :key="c.commandId"><td>{{ c.commandId }}</td><td>{{ c.status==='Sent' ? '厂家已确认接收' : actionStateLabel(c.status) }}<details><summary>原始状态</summary><code>{{ c.status }}</code></details></td><td>{{ c.attemptCount }}</td><td>{{ c.vendorCommandId || '—' }}</td><td>{{ c.lastError ? industrialText(c.lastError) : '—' }}</td></tr></tbody></table></div><p v-else class="muted-note">未返回本步骤车端命令；不表示动作已完成。取放确认步骤不额外发机械动作。</p>
    </section>
    <h3 class="subheading">当前保留的资源归属</h3><p v-if="!detail.resources?.length" class="muted-note">未返回资源记录；不据此推断现场区域一定空闲。</p><ul v-else class="execution-resources"><li v-for="r in detail.resources" :key="r.resourceId">{{ r.resourceId }} · {{ r.vehicleId }} · {{ r.heartbeatExpired ? '心跳租期已过，不能推断物理出区' : '后台持有记录' }}</li></ul>
    <section v-if="detail.handover" class="execution-boundary"><b>实际出区及资源交接诊断</b><p>{{ detail.handover.reason }}</p><p>返程命令完成：{{ detail.handover.returnCommandCompleted?'已记录':'未记录' }} · 实际出区证据：{{ detail.handover.exitPhysicallyVerified?'已记录':'尚未记录' }} · 出区交接：{{ detail.handover.state==='ExitHandoverComplete'?'已闭合':'尚未闭合' }}</p><p>当前持有：{{ detail.handover.heldResources.join('、') || '无返回记录' }}</p><details v-if="detail.handover.recordedReleaseReasons.length"><summary>查看后台已记录的释放依据</summary><p v-for="(r,i) in detail.handover.recordedReleaseReasons" :key="i">{{ r }}</p></details></section>
    <h3 class="subheading">关键事件时间线</h3><p class="muted-note">来自数据库时间戳及持久化执行证据，不是动画。显示最近{{ events.length }}项 / 共{{ detail.executionTimelineTotal ?? events.length }}项；同时间排序不证明跨设备因果。未采集的历史不补造。</p>
    <ol v-if="events.length" class="execution-events"><li v-for="e in events" :key="e.id"><time>{{ shortTime(e.occurredAt) }}</time><div><b>{{ industrialText(e.title) }}</b><p>{{ industrialText(e.summary) }}</p><small>{{ industrialText(e.source) }}<span v-if="e.stepId"> · 步骤 {{ detail.steps.find(s=>s.stepId===e.stepId)?.sequence ?? industrialText(e.stepId) }}</span></small><details><summary>查看原始事件字段</summary><p>{{e.kind}}<br/>{{e.title}}<br/>{{e.summary}}<br/>{{e.source}}{{e.stepId?' · '+e.stepId:''}}</p></details></div></li></ol><p v-else class="muted-note">尚无执行事件返回，或旧版后台未提供；不生成示例轨迹。</p>
    <section v-if="detail.cancellation" class="execution-boundary cancellation-progress"><h3>取消进度与原占用</h3><p>原请求 {{detail.cancellation.requestId}} · {{detail.cancellation.reason}}</p><p>请求记录 {{shortTime(detail.cancellation.requestedAt)}} · 发送尝试 {{detail.cancellation.sendAttempts}}</p><p>取消发送记录 {{shortTime(detail.cancellation.sentAt)}} · 原运输结束确认 {{shortTime(detail.cancellation.confirmedAt)}}</p><p>发送/接受不等于车辆已停；原运输结束仍须核对箱体、PLC周期及原路权，不自动完成九步或释放占用。</p></section>
    <p v-if="store.demoMode" class="inline-warning">合成演示详情；不访问真实任务历史或恢复接口。操作流程请使用大屏演练面板。</p>
    <template v-else>
    <TaskRuntimeHistory :task-id="detail.task.taskId" :fresh="live" />
    </template>
  </div>
</template>

<style scoped>
.execution-template{margin:12px 0;padding:12px 14px;border:1px solid #31546e;background:#0d2134;font-size:11px;line-height:1.7}.execution-template header{display:flex;justify-content:space-between;gap:12px;flex-wrap:wrap}.execution-template b{color:#b9e6ed}.execution-template span{color:#7fd9e8}.execution-template p{margin:5px 0;color:#9eb8cf;overflow-wrap:anywhere}.execution-actions{display:flex;flex-wrap:wrap;gap:16px;align-items:center;margin:12px 0}.execution-actions a{color:#66d1e8}.execution-actions p{width:100%;font-size:12px;color:#b4c7da}
.pause-panel{border-color:#70572d;background:#211d16}.pause-actions{display:flex;gap:8px;margin-top:10px}.pause-actions input{flex:1;min-width:180px}.pause-actions button{white-space:nowrap}
.execution-steps{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:8px;margin:18px 0}.execution-steps button{display:grid;grid-template-columns:24px 1fr;gap:5px;text-align:left;white-space:normal;padding:12px;min-width:0}.execution-steps small{grid-column:2;color:#97afca}.execution-steps .current{border-color:#35bdd7}.execution-steps .selected{background:#15394d;outline:1px solid #67cee2}.execution-steps .done>span{color:#31d3aa}.execution-inspection{padding:18px;background:#101e35;border:1px solid #2d4564}.execution-inspection h3,.execution-inspection h4{color:#cce1f3;margin:0 0 14px}.execution-inspection h4{margin-top:24px}.execution-inspection dl{display:grid;grid-template-columns:145px 1fr;gap:10px;font-size:12px;line-height:1.8}.execution-inspection dt{color:#8ba6c5}.execution-inspection dd{margin:0;overflow-wrap:anywhere}.execution-boundary{padding:12px;border-left:3px solid #c79c51;background:#2a2630;color:#dfc69d;font-size:12px;line-height:1.8;margin:16px 0}.execution-table{overflow-x:auto}.execution-table table{min-width:620px}.execution-table small{display:block;color:#8ba6c5;font-size:10px;line-height:1.7;margin-top:5px}.execution-table td{overflow-wrap:anywhere}.execution-events{list-style:none;padding:0}.execution-events li{display:grid;grid-template-columns:130px 1fr;gap:14px;padding:14px 0;border-bottom:1px solid #253957;font-size:12px}.execution-events time,.execution-events small{color:#88a6c6}.execution-events p{line-height:1.8;margin:6px 0}.execution-events div{overflow-wrap:anywhere}.execution-resources{font-size:12px;line-height:2;color:#a9c2dd}@media(max-width:650px){.execution-steps{grid-template-columns:1fr 1fr}.execution-inspection{padding:12px}.execution-inspection dl{grid-template-columns:1fr;gap:4px}.execution-inspection dd{margin-bottom:9px}.execution-events li{grid-template-columns:1fr;gap:7px}}
.task-current-action{margin:14px 0;padding:15px 17px;border:1px solid #2d6170;border-left:4px solid #49cfd9;border-radius:6px;background:#0d2134}.task-current-action.attention{border-color:#7a5a3a;border-left-color:#e3a562;background:#211f1d}.task-current-action header{display:flex;align-items:flex-start;justify-content:space-between;gap:16px}.task-current-action small{color:#7f9eb6}.task-current-action h3{margin:3px 0 0;color:#d7eff5;font-size:16px}.task-current-action header>span{padding:4px 8px;border:1px solid #3e6579;border-radius:10px;color:#7de0eb;font-size:11px}.task-current-action.attention header>span{border-color:#9b7041;color:#f0c483}.task-current-action .action-label{display:block;margin-top:12px}.next-action-text{margin:3px 0 12px;color:#c0d5e5;line-height:1.8}.restore-signals{padding:11px 13px;border:1px solid #7a5531;border-radius:5px;background:#30251b}.restore-signals>b{color:#f0c175}.restore-signals ul{display:grid;gap:8px;margin:9px 0 0;padding:0;list-style:none}.restore-signals li{display:grid;grid-template-columns:minmax(130px,.7fr) minmax(220px,1.3fr);gap:6px 12px;padding-top:8px;border-top:1px solid #5b452e}.restore-signals li:first-child{padding-top:0;border-top:0}.restore-signals strong{color:#f2d7a5}.restore-signals span{color:#c9b79d}.restore-signals small{grid-column:1/-1;margin:0;color:#9f9182}.restore-clear{margin:9px 0 0;color:#78c9b2;font-size:11px}
</style>
