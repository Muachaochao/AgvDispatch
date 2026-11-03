<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { useDispatchStore } from '../stores/dispatch';
import {
  assignmentModeLabels, assignmentNumber, assignmentOptionsError, assignmentMappingRowsError, changedAssignmentCount,
  dispatchPipelineLayers, dispatchPipelineLayerName,
  type DispatchAssignmentOptions, type DispatchOptimizationSnapshot, type DispatchZoneRow, type DispatchDeadlineRow
} from '../dispatch-optimization';

const dispatch = useDispatchStore();
const data = ref<DispatchOptimizationSnapshot>();
const draft = ref<DispatchAssignmentOptions>();
const zoneRows = ref<DispatchZoneRow[]>([]), deadlineRows = ref<DispatchDeadlineRow[]>([]);
const draftRevision = ref('');
const loading = ref(false), saving = ref(false), loadError = ref(''), saveError = ref(''), message = ref('');
const readAt = ref(0), now = ref(Date.now()), selectedTaskId = ref(''), search = ref('');
const recent = ref<DispatchOptimizationSnapshot[]>([]);
let alive = true;
let poll: ReturnType<typeof setInterval> | undefined;
let tick: ReturnType<typeof setInterval> | undefined;
let readController: AbortController | undefined;
let saveController: AbortController | undefined;

const liveContext = computed(() => !dispatch.demoMode && !dispatch.engineeringSimulationOnly);
const readFresh = computed(() => !loadError.value && readAt.value > 0 && now.value - readAt.value < 15000);
const configuredDraft = computed(() => draft.value ? {
  ...draft.value,
  siteZones: Object.fromEntries(zoneRows.value.map(row => [row.siteId.trim(), row.zone.trim()])),
  stationDeadlineMinutes: Object.fromEntries(deadlineRows.value.map(row => [row.stationId.trim(), row.minutes]))
} : undefined);
const dirty = computed(() => !!configuredDraft.value && JSON.stringify(configuredDraft.value) !== JSON.stringify(data.value?.options));
const revisionChanged = computed(() => !!data.value && !!draftRevision.value && draftRevision.value !== data.value.revision);
const validation = computed(() => configuredDraft.value
  ? assignmentMappingRowsError(zoneRows.value, deadlineRows.value) || assignmentOptionsError(configuredDraft.value) : '请先读取后台配置。');
const canSave = computed(() => liveContext.value && readFresh.value && dirty.value && !validation.value && !revisionChanged.value && !saving.value && !loading.value);
const tasks = computed(() => (data.value?.tasks ?? []).filter(task =>
  [task.taskId, task.stationId, task.recommendedVehicleId, task.greedyVehicleId, task.dispatchedVehicleId, task.reason]
    .some(value => value?.toLocaleLowerCase().includes(search.value.trim().toLocaleLowerCase()))));
const selected = computed(() => data.value?.tasks.find(task => task.taskId === selectedTaskId.value));
const recommendedCount = computed(() => data.value?.tasks.filter(task => task.recommendedVehicleId).length ?? 0);
const preparedCount = computed(() => data.value?.tasks.filter(task => task.dispatchedVehicleId).length ?? 0);
const differences = computed(() => changedAssignmentCount(data.value?.tasks ?? []));
const pipeline = computed(() => dispatchPipelineLayers(data.value?.layers, readFresh.value && liveContext.value, now.value));
const time = (stamp: string) => {
  const value = Date.parse(stamp);
  return Number.isFinite(value) ? new Date(value).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false }) : '尚无周期';
};
const modeLabel = (mode?: string) => assignmentModeLabels[mode as keyof typeof assignmentModeLabels] ?? '尚未取得';

function resetDraft() {
  if (!data.value) return;
  draft.value = { ...data.value.options };
  zoneRows.value = Object.entries(data.value.options.siteZones ?? {}).map(([siteId, zone]) => ({ siteId, zone }));
  deadlineRows.value = Object.entries(data.value.options.stationDeadlineMinutes ?? {}).map(([stationId, minutes]) => ({ stationId, minutes }));
  draftRevision.value = data.value.revision;
  saveError.value = ''; message.value = '';
}

function accept(snapshot: DispatchOptimizationSnapshot, reset = false) {
  const keepDraft = !reset && dirty.value;
  data.value = snapshot;
  readAt.value = Date.now(); now.value = Date.now();
  if (!keepDraft || !draft.value) resetDraft();
  if (snapshot.cycleNumber > 0 && recent.value[0]?.cycleNumber !== snapshot.cycleNumber)
    recent.value = [snapshot, ...recent.value].slice(0, 20);
  if (!snapshot.tasks.some(task => task.taskId === selectedTaskId.value))
    selectedTaskId.value = snapshot.tasks[0]?.taskId ?? '';
}

async function load() {
  if (loading.value || saving.value || !liveContext.value) return;
  loading.value = true;
  const controller = new AbortController(); readController = controller;
  const timeout = setTimeout(() => controller.abort(), 30000);
  try {
    const response = await fetch('/api/dispatch-optimization', { cache: 'no-store', signal: controller.signal });
    const result = await response.json();
    if (!response.ok) throw new Error(result.detail ?? result.error ?? '无法读取派车优化状态。');
    if (alive) { accept(result); loadError.value = ''; }
  } catch (error) {
    if (alive) loadError.value = error instanceof Error ? error.message : String(error);
  } finally {
    clearTimeout(timeout); readController = undefined;
    if (alive) loading.value = false;
  }
}

async function save() {
  if (!canSave.value || !draft.value) return;
  saving.value = true; saveError.value = ''; message.value = '';
  const controller = new AbortController(); saveController = controller;
  const timeout = setTimeout(() => controller.abort(), 30000);
  try {
    const response = await fetch('/api/dispatch-optimization/options', {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ options: configuredDraft.value, expectedRevision: draftRevision.value }), signal: controller.signal
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.detail ?? result.error ?? '配置保存未完成，请刷新核对。');
    if (alive) {
      accept(result, true); loadError.value = '';
      message.value = `已保存为「${modeLabel(result.options.mode)}」，从后续调度周期生效。`;
    }
  } catch (error) {
    if (alive) saveError.value = error instanceof Error && error.name !== 'AbortError'
      ? error.message : '配置保存回执未确认，请刷新并核对后台配置后再操作。';
  } finally {
    clearTimeout(timeout); saveController = undefined;
    if (alive) saving.value = false;
  }
}

onMounted(() => {
  void load();
  poll = setInterval(() => { void load(); }, 5000);
  tick = setInterval(() => { now.value = Date.now(); }, 1000);
});
onUnmounted(() => {
  alive = false; clearInterval(poll); clearInterval(tick);
  readController?.abort(); saveController?.abort();
});
</script>

<template>
  <section class="management-page dispatch-optimization-page">
    <header class="management-title">
      <h2>滚动派车优化</h2>
      <span class="assignment-mode" :class="{ global: data?.options.mode === 'Global' }">{{modeLabel(data?.options.mode)}}</span>
      <RouterLink class="assignment-monitor-entry" to="/dispatch-pipeline">六层调度监控 · 查看每层明细</RouterLink>
      <RouterLink to="/tasks">业务任务</RouterLink>
      <button :disabled="loading || saving || !liveContext" @click="load">{{loading ? '读取中…' : '刷新状态'}}</button>
    </header>
    <p class="assignment-intro">在当前等待队列中同时匹配任务与车辆。执行中、已取得有效路权及已发车的任务保留原归属；未分配任务继续保存在队列，按等待时间重新评估。</p>
    <p v-if="!liveContext" class="inline-warning" role="status">演示或工程隔离模式不能读取和修改现场调度配置。</p>
    <p v-if="loadError" class="inline-warning" role="alert">后台读取失败：{{loadError}}。以下为最近成功读取的结果，配置保存已禁用。</p>
    <p v-else-if="data && !readFresh" class="inline-warning" role="status">最近读取已超过 15 秒，正在等待当前后台状态；配置保存已禁用。</p>
    <p v-if="saveError" class="inline-warning" role="alert">{{saveError}}</p>
    <p v-if="message" class="assignment-success" role="status">{{message}}</p>

    <section class="assignment-panel assignment-pipeline" aria-label="六层调度运行链路">
      <header><div><h3>六层调度运行链路</h3><p>逐层查看后台观察、等待与阻止原因。匹配推荐、路权取得、命令登记及车端执行分别核对。</p></div><span>{{readFresh ? '后台观察' : '等待当前后台观察'}}</span></header>
      <ol class="pipeline-layers">
        <li v-for="layer in pipeline" :key="layer.key" class="pipeline-layer" :class="[layer.tone, { focused: selected?.blockedLayer === layer.key }]" :data-layer="layer.key">
          <div class="pipeline-layer-title"><span class="pipeline-number">{{layer.number}}</span><h4>{{layer.name}}</h4></div>
          <div class="pipeline-layer-state"><strong>{{layer.label}}</strong><span v-if="layer.itemCount !== null">{{layer.itemCount}} 项</span></div>
          <p class="pipeline-purpose">{{layer.purpose}}</p><p class="pipeline-summary">{{layer.summary}}</p>
          <small>{{layer.updatedAt ? `观察于 ${time(layer.updatedAt)}` : '尚无有效更新时间'}}</small>
          <RouterLink :to="`/dispatch-pipeline?layer=${layer.key}`">查看本层输入、依据与实际记录 →</RouterLink>
          <RouterLink v-if="layer.link" :to="layer.link">{{layer.linkLabel}}</RouterLink><span v-else class="pipeline-detail-note">下方查看本轮匹配组合</span>
        </li>
      </ol>
    </section>

    <template v-if="data">
      <div class="assignment-metrics" aria-label="最新周期统计">
        <article><span>持久化等待任务</span><b>{{data.waitingTaskCount}}</b><small>本轮窗口 {{data.windowTaskCount}} 个</small></article>
        <article><span>可用车辆 / 候选边</span><b>{{data.eligibleVehicleCount}} <em>/ {{data.candidateCount}}</em></b><small>匹配图保留 {{data.retainedCandidateCount ?? data.candidateCount}} 条候选边</small></article>
        <article><span>推荐 / 已登记命令</span><b>{{recommendedCount}} <em>/ {{preparedCount}}</em></b><small>与传统派车不同 {{differences}} 项</small></article>
        <article><span>本轮计算耗时</span><b>{{assignmentNumber(data.durationMs)}} <em>ms</em></b><small>路线缓存：命中 {{data.routeCacheHits}} / 计算 {{data.routeCacheMisses}}</small></article>
      </div>
      <div class="assignment-cycle-status"><strong>第 {{data.cycleNumber}} 轮</strong><span>{{time(data.capturedAt)}}</span><span>{{data.message}}</span></div>
      <p v-if="data.shadowCycleCount" class="assignment-note" title="同一候选图下，依次比较可分配任务数、有效优先级总和、预计行驶时间总和。">影子比对累计 {{data.shadowCycleCount}} 轮 · 全局推荐更优 {{data.improvedCycleCount ?? 0}} 轮。比较顺序：可分配数量、优先级、行驶时间。</p>

      <div class="assignment-workspace">
        <article class="assignment-panel assignment-queue">
          <header><div><h3>任务与车辆匹配</h3><p>推荐仅在取得路权及发车复核通过后登记命令。</p></div><label class="assignment-search"><span>筛选本轮任务</span><input v-model="search" type="search" placeholder="工位、车辆、任务编号或等待原因"/></label></header>
          <div class="assignment-table-wrap">
            <table class="assignment-table"><thead><tr><th>工位 / 呼叫时间</th><th>有效优先级</th><th>已等待</th><th>全局推荐</th><th>传统派车</th><th>实际登记</th><th>本轮结果</th><th>详情</th></tr></thead>
              <tbody>
                <tr v-for="task in tasks" :key="task.taskId" :class="{selected: selectedTaskId === task.taskId}">
                  <td><strong>{{task.stationId}}</strong><small>{{time(task.calledAt)}}</small><RouterLink :to="`/tasks/${encodeURIComponent(task.taskId)}`">{{task.taskId}}</RouterLink></td>
                  <td><strong>{{assignmentNumber(task.effectivePriority)}}</strong><small>基础 {{task.basePriority}}</small></td>
                  <td>{{assignmentNumber(task.waitMinutes)}} 分钟</td>
                  <td :class="{different: task.recommendedVehicleId !== task.greedyVehicleId}">{{task.recommendedVehicleId || '待分配'}}</td>
                  <td>{{task.greedyVehicleId || '待分配'}}</td><td>{{task.dispatchedVehicleId || '未登记'}}</td>
                  <td class="assignment-reason"><span v-if="dispatchPipelineLayerName(task.blockedLayer)" class="assignment-layer-tag">{{dispatchPipelineLayerName(task.blockedLayer)}}</span>{{task.reason}}</td>
                  <td><button :aria-pressed="selectedTaskId === task.taskId" @click="selectedTaskId = task.taskId">候选车辆</button></td>
                </tr>
                <tr v-if="!tasks.length"><td colspan="8" class="assignment-empty">{{search ? '当前窗口没有符合筛选条件的任务。' : data.cycleNumber ? '本轮没有可展示的等待任务。' : '等待后台完成首次调度周期。'}}</td></tr>
              </tbody>
            </table>
          </div>
          <p class="assignment-note">窗口外还有 {{Math.max(0, data.waitingTaskCount - data.windowTaskCount)}} 个任务保存在队列中。相同有效优先级按原始呼叫时间排序。影子比对时实际派车仍使用传统算法。</p>
        </article>

        <article class="assignment-panel assignment-candidates">
          <header><div><h3>候选车辆与排除依据</h3><p>{{selected ? `${selected.stationId} · ${selected.taskId}` : '从左侧任务查看候选'}}</p></div></header>
          <template v-if="selected">
            <p class="assignment-selected-reason"><strong v-if="dispatchPipelineLayerName(selected.blockedLayer)">{{dispatchPipelineLayerName(selected.blockedLayer)}}<br/></strong>{{selected.reason}}</p>
            <div class="assignment-table-wrap"><table class="assignment-table"><thead><tr><th>车辆</th><th>行驶估算</th><th>综合成本</th><th>候选状态与依据</th></tr></thead><tbody>
              <tr v-for="candidate in selected.candidates" :key="candidate.vehicleId" :class="{ excluded: !candidate.eligible }">
                <td><strong>{{candidate.vehicleId}}</strong><small v-if="candidate.vehicleId === selected.recommendedVehicleId">全局推荐</small></td>
                <td>{{candidate.eligible ? assignmentNumber(candidate.travelSeconds) + ' 秒' : '—'}}</td>
                <td>{{candidate.eligible ? assignmentNumber(candidate.totalCost) + ' 秒' : '—'}}<details v-if="candidate.eligible" class="assignment-breakdown"><summary>成本分解</summary><span>拥堵折算 {{assignmentNumber(candidate.trafficWaitSeconds)}} 秒</span><span>返程折算 {{assignmentNumber(candidate.returnSeconds)}} 秒</span><span>电量风险 {{assignmentNumber(candidate.batteryRiskSeconds)}} 秒</span><span>跨区成本 {{assignmentNumber(candidate.crossZoneSeconds)}} 秒</span><span>待机抵扣 {{assignmentNumber(candidate.standbyBonusSeconds)}} 秒</span></details></td>
                <td class="assignment-reason"><b>{{candidate.eligible ? '可参与匹配' : '已排除'}}</b><small>{{candidate.reason}}</small></td>
              </tr><tr v-if="!selected.candidates.length"><td colspan="4" class="assignment-empty">本轮未生成候选路线，请查看任务等待原因。</td></tr>
            </tbody></table></div>
          </template>
          <p v-else class="assignment-empty">没有选中的等待任务。</p>
          <p class="assignment-note">行驶时间依据道路速度及估算巡航上限计算。拥堵成本来自当前道路动态惩罚，不代表公共资源释放倒计时；已占用资源仍按硬约束等待。返程按权重折算，未配置区域的站点不添加跨区成本。</p>
          <RouterLink to="/resource-review">查看当前公共资源占用</RouterLink>
        </article>
      </div>

      <details class="assignment-panel assignment-config" open>
        <summary>调度策略配置 <span>{{dirty ? '有未保存的修改' : '与后台已保存配置一致'}}</span></summary>
        <p>影子比对会同时计算全局推荐和传统派车结果。切换为全局匹配后，后续等待任务按推荐组合申请路权；车辆命令仍通过现有持久化队列和安全复核。</p>
        <p v-if="revisionChanged" class="inline-warning" role="alert">后台配置修订已变化。请点击“载入后台配置”取得当前值后再编辑，避免覆盖其他修改。</p>
        <form v-if="draft" @submit.prevent="save">
          <fieldset :disabled="saving || !liveContext">
            <div class="assignment-config-grid">
              <label>派车模式<select v-model="draft.mode"><option value="Disabled">传统派车 · 关闭优化计算</option><option value="Shadow">影子比对 · 推荐供核对</option><option value="Global">全局匹配 · 用于后续派车</option></select></label>
              <label>滚动周期（毫秒）<input v-model.number="draft.intervalMs" type="number" min="2000" max="5000" step="1" required/><small>2～5 秒，默认 3000</small></label>
              <label>窗口任务数<input v-model.number="draft.windowSize" type="number" min="1" max="1000" step="1" required/><small>建议 50～100，默认 80</small></label>
              <label>每车保留候选数<input v-model.number="draft.candidatesPerVehicle" type="number" min="1" max="100" step="1" required/><small>建议 10～20，默认 15</small></label>
              <label>每任务保留车辆数<input v-model.number="draft.minimumCandidatesPerTask" type="number" min="1" max="5" step="1" required/><small>默认 2；唯一可执行候选须保留</small></label>
              <label>每分钟等待补偿<input v-model.number="draft.waitPriorityPerMinute" type="number" min="0" max="100" step="any" required/><small>只计入有效优先级一次</small></label>
              <label>等待补偿上限<input v-model.number="draft.maxWaitPriority" type="number" min="0" max="10000" step="any" required/><small>默认 30 分</small></label>
              <label>截止时间预警窗口（分钟）<input v-model.number="draft.deadlineHorizonMinutes" type="number" min="1" max="1440" step="any" required/></label>
              <label>截止时间补偿上限<input v-model.number="draft.maxDeadlinePriority" type="number" min="0" max="10000" step="any" required/><small>仅对有截止时间的任务生效</small></label>
              <label>业务最低电量（%）<input v-model.number="draft.minimumBatteryPercent" type="number" min="0" max="100" step="any" required/><small>充电调度的业务限制同时生效</small></label>
              <label>估算巡航上限（米/秒）<input v-model.number="draft.nominalSpeedMetersPerSecond" type="number" min="0.05" max="10" step="any" required/><small>与道路速度取较小值；不改变车速</small></label>
              <label>待机车加分（秒）<input v-model.number="draft.standbyBonusSeconds" type="number" min="0" max="1" step="any" required/><small>最多抵扣 1 秒成本</small></label>
              <label>低电量风险成本（秒）<input v-model.number="draft.batteryRiskSeconds" type="number" min="0" max="3600" step="any" required/><small>不代替最低电量硬约束</small></label>
              <label>跨区成本（秒）<input v-model.number="draft.crossZoneSeconds" type="number" min="0" max="3600" step="any" required/><small>仅已配置且不同的业务区域生效</small></label>
              <label>返程成本权重<input v-model.number="draft.returnCostWeight" type="number" min="0" max="10" step="any" required/><small>默认 0.2；0 表示不计返程成本</small></label>
            </div>
            <div class="assignment-mappings">
              <section><header><h4>站点与业务区域</h4><button type="button" @click="zoneRows.push({siteId: '', zone: ''})">添加区域站点</button></header><p>使用地图中的真实 AGV 站点编号。未配置的站点不推测所属区域。</p>
                <div v-for="(row, index) in zoneRows" :key="index" class="assignment-mapping-row"><label>AGV 站点<input v-model="row.siteId" placeholder="如 LM28" maxlength="128" required/></label><label>业务区域<input v-model="row.zone" placeholder="如 A区" maxlength="128" required/></label><button type="button" :aria-label="`删除第 ${index + 1} 条区域配置`" @click="zoneRows.splice(index, 1)">删除</button></div>
                <p v-if="!zoneRows.length" class="assignment-note">尚未配置业务区域。</p>
              </section>
              <section><header><h4>工位呼叫时限</h4><button type="button" @click="deadlineRows.push({stationId: '', minutes: 30})">添加工位时限</button></header><p>从原始呼叫时间起计算目标时限，用于截止时间紧迫度加分。</p>
                <div v-for="(row, index) in deadlineRows" :key="index" class="assignment-mapping-row"><label>工位编号<input v-model="row.stationId" placeholder="如 CNC-01" maxlength="128" required/></label><label>目标时限（分钟）<input v-model.number="row.minutes" type="number" min="1" max="10080" step="any" required/></label><button type="button" :aria-label="`删除第 ${index + 1} 条工位时限`" @click="deadlineRows.splice(index, 1)">删除</button></div>
                <p v-if="!deadlineRows.length" class="assignment-note">尚未设置工位时限；等待时间补偿仍然生效。</p>
              </section>
            </div>
          </fieldset>
          <p v-if="dirty && validation" class="inline-warning" role="alert">{{validation}}</p>
          <div class="assignment-config-actions"><span>配置修订 {{data.revision}}</span><button type="button" :disabled="saving || !readFresh" @click="resetDraft">载入后台配置</button><button class="accent-button" type="submit" :disabled="!canSave">{{saving ? '保存中…' : '保存调度策略'}}</button></div>
        </form>
      </details>

      <details v-if="recent.length" class="assignment-panel assignment-history">
        <summary>当前页面最近 {{recent.length}} 轮观察</summary>
        <div class="assignment-table-wrap"><table class="assignment-table"><thead><tr><th>周期 / 时间</th><th>等待 / 窗口</th><th>推荐 / 实际登记</th><th>不同分配</th><th>耗时</th><th>本轮说明</th></tr></thead><tbody>
          <tr v-for="cycle in recent" :key="cycle.cycleNumber"><td>{{cycle.cycleNumber}}<small>{{time(cycle.capturedAt)}}</small></td><td>{{cycle.waitingTaskCount}} / {{cycle.windowTaskCount}}</td><td>{{cycle.tasks.filter(task => task.recommendedVehicleId).length}} / {{cycle.tasks.filter(task => task.dispatchedVehicleId).length}}</td><td>{{changedAssignmentCount(cycle.tasks)}}</td><td>{{assignmentNumber(cycle.durationMs)}} ms</td><td class="assignment-reason">{{cycle.message}}</td></tr>
        </tbody></table></div>
        <p class="assignment-note">最多保留本页面已读取的 20 轮观察；刷新页面后重新累计。</p>
      </details>
    </template>
    <p v-else-if="!loadError && liveContext" class="assignment-empty">正在读取后台调度策略与周期结果…</p>
  </section>
</template>

<style scoped>
.dispatch-optimization-page{gap:16px;padding-bottom:24px}.assignment-intro,.assignment-panel>p{font-size:12px;line-height:1.85;color:#a0b6d0}.assignment-mode{padding:5px 11px;border:1px solid #426383;border-radius:4px;font-size:12px;color:#a4c5e7;background:#152b45}.assignment-mode.global{border-color:#3e9f8f;color:#82e3c6;background:#133830}.management-title>a{margin-left:auto;color:#8ad4e3;font-size:12px}.assignment-success{padding:10px 14px;background:#173b37;color:#93e6ca;font-size:12px}.assignment-metrics{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px}.assignment-metrics article{border:1px solid #2b4264;background:#121f39;padding:16px 18px}.assignment-metrics span,.assignment-metrics small{display:block;color:#91a9c9;font-size:11px}.assignment-metrics b{display:block;font-size:28px;font-weight:500;color:#d5e8f6;font-variant-numeric:tabular-nums;margin:8px 0}.assignment-metrics em{font-size:15px;font-style:normal;color:#799bbc}.assignment-cycle-status{display:flex;flex-wrap:wrap;gap:10px 16px;align-items:baseline;padding:12px 15px;background:#142a43;border-left:2px solid #41aac5;color:#9bbad7;font-size:12px;line-height:1.7}.assignment-cycle-status strong{color:#a5d9e8;font-weight:500}.assignment-workspace{display:grid;grid-template-columns:minmax(0,1.65fr) minmax(330px,1fr);gap:16px}.assignment-panel{border:1px solid #2a4263;background:#101d35;min-width:0;padding:17px}.assignment-panel>header{display:flex;justify-content:space-between;gap:16px;align-items:flex-start;margin-bottom:15px}.assignment-panel h3{font-size:14px;font-weight:500;color:#c6deef;margin:0 0 8px}.assignment-panel header p{font-size:11px;color:#829cbb;line-height:1.6}.assignment-search{display:grid;gap:6px;font-size:11px;color:#94abc8;min-width:220px}.assignment-search input{width:100%;min-height:32px}.assignment-table-wrap{overflow:auto;max-height:460px;border:1px solid #283b5b}.assignment-table th,.assignment-table td{padding:12px 10px;max-width:none;font-size:12px;vertical-align:top}.assignment-table th{color:#8da8c8;font-size:11px}.assignment-table td strong{font-weight:500;color:#c9dfef}.assignment-table small,.assignment-table td a{display:block;line-height:1.6;font-size:10px;color:#8ba8ca;margin-top:4px}.assignment-table td a{max-width:170px;white-space:normal;overflow-wrap:anywhere;color:#7ec4dc}.assignment-table .assignment-reason{min-width:190px;max-width:300px;white-space:normal;overflow-wrap:anywhere;line-height:1.8}.assignment-table .different{color:#73d3de}.assignment-table tr.selected{background:#18354e}.assignment-table tr.excluded td{color:#bb9d81}.assignment-table button{font-size:11px;min-height:27px;padding:3px 8px}.assignment-note{font-size:11px!important;line-height:1.9!important;color:#839fbe!important;margin-top:13px}.assignment-selected-reason{padding:10px 12px;background:#172c47;color:#b3cbe1;font-size:12px;line-height:1.8;margin-bottom:13px}.assignment-candidates>a{display:inline-block;color:#81c8df;font-size:12px;margin-top:10px}.assignment-empty{text-align:center!important;color:#839fbe;padding:25px!important;font-size:12px}.assignment-panel summary{cursor:pointer;font-size:14px;color:#c1d9ef;line-height:1.6}.assignment-panel summary>span{font-size:11px;color:#91afcc;margin-left:12px}.assignment-config>p{margin-top:15px}.assignment-config fieldset{border:0;padding:0;margin:18px 0}.assignment-config-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:18px}.assignment-config-grid label{display:flex;flex-direction:column;gap:8px;font-size:12px;color:#adc4dd}.assignment-config-grid input,.assignment-config-grid select{width:100%;min-height:35px}.assignment-config-grid small{font-size:10px;line-height:1.6;color:#7f9ebd}.assignment-config-actions{display:flex;align-items:center;justify-content:flex-end;flex-wrap:wrap;gap:10px;border-top:1px solid #293f5f;padding-top:15px}.assignment-config-actions>span{margin-right:auto;font:10px Consolas,monospace;color:#7c99b9;overflow-wrap:anywhere}.assignment-history .assignment-table-wrap{margin-top:15px}
.assignment-breakdown{margin-top:7px}.assignment-breakdown summary{font-size:10px;color:#82b6d7}.assignment-breakdown span{display:block;font-size:10px;line-height:1.8;color:#91abc9;white-space:nowrap}.assignment-mappings{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:20px;margin-top:24px;padding-top:20px;border-top:1px solid #293f5f}.assignment-mappings section{min-width:0}.assignment-mappings header{display:flex;align-items:center;justify-content:space-between;gap:10px}.assignment-mappings h4{font-size:13px;font-weight:500;color:#b9d1e6}.assignment-mappings header button{font-size:11px}.assignment-mappings p{font-size:11px;line-height:1.8;color:#849fbd;margin:10px 0}.assignment-mapping-row{display:flex;align-items:flex-end;gap:9px;margin:12px 0}.assignment-mapping-row label{display:flex;flex:1;min-width:0;flex-direction:column;gap:7px;font-size:11px;color:#a0bbd6}.assignment-mapping-row input{min-width:0;width:100%;min-height:33px}.assignment-mapping-row button{font-size:11px;min-height:33px}
.assignment-pipeline>header>span{font-size:11px;color:#89a9c7;white-space:nowrap}.pipeline-layers{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:12px;list-style:none;margin:0;padding:0}.pipeline-layer{position:relative;display:flex;flex-direction:column;gap:11px;min-width:0;padding:14px 12px;border:1px solid #2c4261;background:#111f38}.pipeline-layer:not(:last-child)::after{position:absolute;right:-10px;top:20px;content:'›';color:#5d7c9d;font-size:20px;z-index:1}.pipeline-layer.focused{outline:1px solid #90b6d2;outline-offset:2px}.pipeline-layer-title{display:flex;align-items:center;gap:7px}.pipeline-number{display:inline-flex;align-items:center;justify-content:center;flex:0 0 22px;height:22px;border:1px solid #496784;color:#b2cde4;font:12px Consolas,monospace}.pipeline-layer-title h4{font-size:12px;font-weight:500;color:#bcd4e9;line-height:1.6}.pipeline-layer-state{display:flex;flex-wrap:wrap;align-items:center;gap:6px 9px}.pipeline-layer-state strong{font-size:11px;font-weight:400;padding:3px 6px;background:#243149;color:#a3b4c9}.pipeline-layer-state>span{font-size:11px;color:#a6bed5}.pipeline-layer.ready .pipeline-layer-state strong{background:#173c36;color:#87dbc0}.pipeline-layer.running .pipeline-layer-state strong{background:#15384d;color:#83d0e5}.pipeline-layer.observing .pipeline-layer-state strong{background:#253453;color:#adbfe5}.pipeline-layer.waiting .pipeline-layer-state strong,.pipeline-layer.stale .pipeline-layer-state strong{background:#403723;color:#dcc28b}.pipeline-layer.blocked .pipeline-layer-state strong{background:#462d37;color:#e5a4ad}.pipeline-purpose{font-size:10px!important;color:#7897b7;line-height:1.8;min-height:36px}.pipeline-summary{font-size:11px;line-height:1.8;color:#a5bed6;overflow-wrap:anywhere;flex:1}.pipeline-layer>small{font-size:9px;line-height:1.6;color:#6f8eaf}.pipeline-layer>a,.pipeline-detail-note{font-size:10px;line-height:1.6;color:#83c4da}.pipeline-detail-note{color:#7493b3}.assignment-layer-tag{display:block;color:#dfbe83;font-size:10px;margin-bottom:5px}
@media(max-width:1400px){.assignment-workspace{grid-template-columns:1fr}.assignment-table-wrap{max-height:420px}.assignment-config-grid{grid-template-columns:repeat(3,minmax(0,1fr))}.assignment-candidates .assignment-table .assignment-reason{max-width:none;width:100%}}
@media(max-width:1250px){.pipeline-layers{grid-template-columns:repeat(3,minmax(0,1fr))}.pipeline-layer:nth-child(3)::after{display:none}}
@media(max-width:800px){.assignment-metrics{grid-template-columns:repeat(2,minmax(0,1fr))}.assignment-config-grid{grid-template-columns:repeat(2,minmax(0,1fr))}.assignment-mappings{grid-template-columns:1fr}.assignment-panel>header{flex-wrap:wrap}.assignment-search{width:100%}.assignment-panel{padding:13px}.assignment-metrics article{padding:12px}.management-title>a{margin-left:0}}
@media(max-width:800px){.pipeline-layers{grid-template-columns:repeat(2,minmax(0,1fr))}.pipeline-layer:nth-child(3)::after{display:block}.pipeline-layer:nth-child(even)::after{display:none}}
@media(max-width:480px){.assignment-config-grid{grid-template-columns:1fr}.assignment-metrics b{font-size:24px}.assignment-metrics small{line-height:1.7}}
@media(max-width:480px){.pipeline-layers{grid-template-columns:1fr}.pipeline-layer:not(:last-child)::after{display:block;right:auto;left:22px;top:auto;bottom:-15px;transform:rotate(90deg);background:#101d35;height:16px;line-height:16px}.pipeline-purpose{min-height:0}.pipeline-layer{gap:8px}.pipeline-layer>small{font-size:10px}}
.management-title>.assignment-monitor-entry{margin-left:auto;color:#a5e5ef;border:1px solid #367187;background:#17374a;padding:7px 11px;border-radius:4px;font-size:12px}.management-title>.assignment-monitor-entry~a{margin-left:0}
</style>
