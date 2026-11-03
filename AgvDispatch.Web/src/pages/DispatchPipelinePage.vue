<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, shallowRef, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { IconArrowRight, IconDownload, IconPlayerPause, IconPlayerPlay, IconRefresh, IconSearch } from '@tabler/icons-vue';
import { useDispatchStore } from '../stores/dispatch';
import { assignmentModeLabels, type DispatchAssignmentMode } from '../dispatch-optimization';
import {
  pipelineLayerKey, pipelineLayerViews, pipelineTime, pipelineTimestampFresh,
  pipelineEvidenceState, pipelineRecordState, pipelineRecordIsIssue, filterPipelineRecords, pipelineInternalLink,
  retainPipelineObservation, pipelineExecutionSummary,
  type PipelineInspectionSnapshot, type PipelineObservation, type PipelineCheck
} from '../dispatch-pipeline';

const store = useDispatchStore(), route = useRoute(), router = useRouter();
const latest = shallowRef<PipelineInspectionSnapshot>();
const observations = shallowRef<PipelineObservation[]>([]);
const fixed = shallowRef<PipelineObservation | null>(null);
const loading = ref(false), error = ref(''), readAt = ref(0), now = ref(Date.now());
const search = ref(''), onlyIssues = ref(false), detailElement = ref<HTMLElement>();
let alive = true, controller: AbortController | undefined;
let poll: ReturnType<typeof setInterval> | undefined, tick: ReturnType<typeof setInterval> | undefined;
const liveContext = computed(() => !store.demoMode && !store.engineeringSimulationOnly);
const selectedKey = computed(() => pipelineLayerKey(route.query.layer));
const data = computed(() => fixed.value?.snapshot ?? latest.value);
const historyMode = computed(() => fixed.value !== null);
const reference = computed(() => fixed.value ? Date.parse(fixed.value.snapshot.capturedAt) : now.value);
const currentReadFresh = computed(() => liveContext.value && !error.value && readAt.value > 0 && now.value - readAt.value < 15000 && pipelineTimestampFresh(latest.value?.capturedAt, now.value));
const evidenceFresh = computed(() => historyMode.value || currentReadFresh.value);
const layers = computed(() => pipelineLayerViews(data.value, evidenceFresh.value, reference.value));
const selected = computed(() => layers.value.find(layer => layer.key === selectedKey.value) ?? layers.value[0]!);
const layer = computed(() => selected.value.observed);
const records = computed(() => filterPipelineRecords(layer.value?.records ?? [], search.value, onlyIssues.value));
const issueCount = computed(() => layer.value?.records.filter(pipelineRecordIsIssue).length ?? 0);
const attention = computed(() => layers.value.filter(layer => ['blocked', 'waiting', 'stale', 'unknown'].includes(layer.status.tone)));
const executionSummary = computed(() => pipelineExecutionSummary(data.value?.gates ?? [], evidenceFresh.value));
const mode = computed(() => assignmentModeLabels[data.value?.mode as DispatchAssignmentMode] ?? '模式未取得');
const fixedOutsideHistory = computed(() => fixed.value && !observations.value.some(row => row.id === fixed.value!.id));
const state = (check: PipelineCheck) => pipelineEvidenceState(check.state, check.observedAt, evidenceFresh.value, reference.value);
const statusLabel = (label: string) => historyMode.value ? `当时 · ${label}` : label;

async function selectLayer(key: string) {
  await router.replace({ query: { ...route.query, layer: key } });
  await nextTick();
  const viewportTop = detailElement.value?.closest('.dispatch-pipeline-page')?.getBoundingClientRect().top ?? 0;
  if (detailElement.value && detailElement.value.getBoundingClientRect().top < viewportTop) detailElement.value.scrollIntoView({ block: 'start' });
}
function freeze() {
  if (!latest.value) return;
  fixed.value = retainPipelineObservation([], latest.value)[0]!;
}
function chooseObservation(event: Event) {
  const id = (event.target as HTMLSelectElement).value;
  if (!id) { fixed.value = null; return; }
  const found = observations.value.find(row => row.id === id);
  if (found) fixed.value = found;
}
function exportObservation() {
  if (!data.value) return;
  const body = JSON.stringify({ exportedAt: new Date().toISOString(), meaning: '只读观察记录，不授予任何执行许可。', ...data.value }, null, 2);
  const url = URL.createObjectURL(new Blob([body], { type: 'application/json;charset=utf-8' }));
  const anchor = document.createElement('a'); anchor.href = url;
  anchor.download = `dispatch-observation-${data.value.capturedAt.replace(/[^\dTZ]/g, '-')}.json`;
  anchor.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
}
async function load() {
  if (loading.value || !liveContext.value) return;
  loading.value = true;
  const pending = new AbortController(); controller = pending;
  const timeout = setTimeout(() => pending.abort(), 20000);
  try {
    const response = await fetch('/api/dispatch-pipeline', { cache: 'no-store', signal: pending.signal });
    const result = await response.json();
    if (!response.ok) throw new Error(result.detail ?? result.error ?? '无法读取六层监控状态。');
    if (!result || !Array.isArray(result.layers) || !Array.isArray(result.gates) || !pipelineTimestampFresh(result.capturedAt, Date.now(), 30000))
      throw new Error('后台观察结构或采集时间无效，请核对后台版本与时间。');
    if (alive && liveContext.value) {
      latest.value = result; observations.value = retainPipelineObservation(observations.value, result);
      readAt.value = Date.now(); now.value = Date.now(); error.value = '';
    }
  } catch (reason) {
    if (alive) error.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    clearTimeout(timeout); if (controller === pending) controller = undefined;
    if (alive) loading.value = false;
  }
}
watch(liveContext, value => { if (value) void load(); else controller?.abort(); });
onMounted(() => { void load(); poll = setInterval(() => { void load(); }, 5000); tick = setInterval(() => { now.value = Date.now(); }, 1000); });
onUnmounted(() => { alive = false; clearInterval(poll); clearInterval(tick); controller?.abort(); });
</script>

<template>
  <section class="management-page dispatch-pipeline-page" :class="{ 'is-historical': historyMode }">
    <header class="pipeline-page-title">
      <div><p class="pipeline-eyebrow">调度过程 / 只读监控</p><h2>六层调度监控</h2><p>先看运行开关，再逐层核对任务、车辆、资源与原命令。</p></div>
      <div class="pipeline-page-actions"><RouterLink to="/dispatch-optimization">调度策略与成本 <IconArrowRight :size="14"/></RouterLink><button data-testid="pipeline-refresh" :disabled="loading || !liveContext" @click="load"><IconRefresh :size="15"/>{{loading ? '正在采集…' : '刷新观察'}}</button></div>
    </header>

    <p v-if="!liveContext" class="pipeline-alert" role="alert">当前为演示或工程隔离模式，不读取现场调度数据。</p>
    <p v-if="error" class="pipeline-alert" role="alert">实时读取失败：{{error}}。{{historyMode ? '下方保留固定时刻的历史观察，不代表当前状态。' : '下方保留最近成功采集内容，不能据此判断当前已就绪。'}}</p>
    <p v-else-if="latest && !currentReadFresh" class="pipeline-alert" role="status">实时采集已过期，正在等待后台重新提供当前观察。</p>

    <div class="pipeline-banner" :class="{ historical: historyMode, stale: !evidenceFresh }">
      <div class="pipeline-main-status"><span class="pipeline-context-label">{{historyMode ? '已固定历史观察 · 非当前状态' : currentReadFresh ? '当前后台观察' : '等待有效后台观察'}}</span><strong>{{historyMode ? '当时：' : ''}}{{executionSummary}}</strong><p>{{data?.summary || '后台连接后，这里会显示实际执行开关与各层采集结果。'}}</p></div>
      <dl class="pipeline-timestamps"><div><dt>{{historyMode ? '固定采集时间' : '本次采集时间'}}</dt><dd>{{pipelineTime(data?.capturedAt)}}</dd></div><div><dt>最近优化计算</dt><dd>{{data?.optimizationCapturedAt ? pipelineTime(data.optimizationCapturedAt) : '尚无计算记录'}}<small v-if="data?.optimizationCapturedAt">第 {{data.cycleNumber}} 轮 · {{mode}}</small><small v-else>{{mode}} · 配置与实际计算分别核对</small></dd></div></dl>
    </div>

    <section class="pipeline-gates" aria-label="实际运行开关">
      <article v-for="gate in data?.gates ?? []" :key="gate.key" :class="{ 'gate-off': gate.enabled === false }"><div><b>{{gate.label}}</b><span :class="{ 'gate-on': gate.enabled === true && evidenceFresh }">{{!evidenceFresh ? '状态待更新' : gate.enabled === true ? '已开启' : gate.enabled === false ? '未开启' : '未取得'}}</span></div><p>{{gate.detail}}</p></article>
      <p v-if="!data?.gates.length" class="pipeline-empty">尚未取得实际运行开关。</p>
    </section>

    <div class="pipeline-observation-toolbar">
      <div class="pipeline-observation-buttons"><button data-testid="pipeline-freeze" :disabled="!latest || historyMode" @click="freeze"><IconPlayerPause :size="15"/>固定本次观察</button><button v-if="historyMode" data-testid="pipeline-resume" @click="fixed = null"><IconPlayerPlay :size="15"/>恢复实时</button><button data-testid="pipeline-export" :disabled="!data" @click="exportObservation"><IconDownload :size="15"/>导出本次观察</button></div>
      <label>查看观察<select data-testid="pipeline-history" :value="fixed?.id ?? ''" @change="chooseObservation"><option value="">实时更新 · 每 5 秒采集</option><option v-if="fixedOutsideHistory" :value="fixed!.id">{{pipelineTime(fixed!.snapshot.capturedAt)}} · 已固定</option><option v-for="observation in observations" :key="observation.id" :value="observation.id">{{pipelineTime(observation.snapshot.capturedAt)}} · 周期标识 {{observation.snapshot.cycleNumber}}</option></select></label>
      <small>仅保留本页最近 60 次采集；刷新页面后清空。固定观察时，后台仍继续采集。</small>
    </div>
    <p v-if="historyMode" class="pipeline-history-note" role="status">正在查看 {{pipelineTime(data?.capturedAt)}} 的证据，各项判断按该时刻展示。跳转到任务、车辆等页面会查看它们的当前数据。</p>

    <div class="pipeline-workspace">
      <nav class="pipeline-layer-nav" aria-label="六层详情选择">
        <header><h3>逐层查看</h3><span>{{attention.length ? `${attention.length} 层需要关注` : '六层观察已取得'}}</span></header>
        <button v-for="item in layers" :key="item.key" :data-layer="item.key" :aria-pressed="selectedKey === item.key" :class="[item.status.tone, { selected: selectedKey === item.key }]" @click="selectLayer(item.key)"><span class="pipeline-step-number">{{item.number}}</span><span class="pipeline-step-body"><b>{{item.name}}</b><small>{{item.question}}</small><span class="pipeline-state-text">{{statusLabel(item.status.label)}}<em>{{item.observed ? `${item.observed.totalCount} 条记录` : '数量未取得'}}</em></span></span><IconArrowRight :size="14"/></button>
        <p>记录为限量样本，详见每层来源说明。</p>
      </nav>

      <article ref="detailElement" class="pipeline-detail" data-testid="pipeline-detail" :data-selected-layer="selected.key">
        <header class="pipeline-detail-header"><div><p>第 {{selected.number}} 层</p><h3>{{selected.name}}</h3><span>{{selected.question}}</span></div><span class="pipeline-status-badge" :class="selected.status.tone">{{statusLabel(selected.status.label)}}</span></header>
        <p class="pipeline-detail-summary">{{layer?.summary || '后台尚未提供该层观察；不会据此显示已通过。'}}</p>
        <div class="pipeline-layer-boundary"><div><span>输入</span><b>{{selected.input}}</b></div><IconArrowRight :size="18"/><div><span>输出</span><b>{{selected.output}}</b></div><small>该层观察：{{pipelineTime(layer?.observedAt)}}</small></div>
        <dl v-if="layer?.metrics.length" class="pipeline-metrics"><div v-for="metric in layer.metrics" :key="metric.label"><dt>{{metric.label}}</dt><dd>{{metric.value}}<small v-if="metric.unit">{{metric.unit}}</small></dd></div></dl>

        <section class="pipeline-checks-section"><header><h4>判断依据</h4><span>实际值、预期条件与最近观察逐项对照</span></header><div class="pipeline-table-wrap"><table class="pipeline-checks"><thead><tr><th>检查项</th><th>观察结果</th><th>实际 / 预期</th><th>依据与时间</th></tr></thead><tbody><tr v-for="check in layer?.checks ?? []" :key="check.key" data-testid="pipeline-check"><td><strong>{{check.label}}</strong></td><td><span class="pipeline-status-badge" :class="state(check).tone">{{statusLabel(state(check).label)}}</span></td><td><div class="pipeline-comparison"><b>{{check.actual || '未取得实际值'}}</b><small>预期：{{check.expected || '后台未提供'}}</small></div></td><td><p>{{check.reason}}</p><small class="pipeline-record-time">{{pipelineTime(check.observedAt)}}</small></td></tr><tr v-if="!layer?.checks.length"><td colspan="4" class="pipeline-empty">尚未取得逐项检查依据；当前不推断检查已通过。</td></tr></tbody></table></div></section>

        <section class="pipeline-records-section"><header><div><h4>实际记录</h4><p>关联原任务、车辆、资源或命令，展开查看采集依据。</p></div><RouterLink :to="selected.link">{{selected.linkLabel}} <IconArrowRight :size="13"/></RouterLink></header>
          <div class="pipeline-record-tools"><label class="pipeline-search"><IconSearch :size="16"/><input v-model="search" data-testid="pipeline-search" type="search" aria-label="筛选当前层记录" placeholder="任务、工位、车辆、资源、命令或原因"/></label><label class="pipeline-issue-filter"><input v-model="onlyIssues" data-testid="pipeline-only-issues" type="checkbox"/>仅等待或异常<span>{{issueCount}}</span></label><button v-if="search || onlyIssues" @click="search = ''; onlyIssues = false">清除筛选</button></div>
          <p class="pipeline-record-count">{{records.length}} 条符合筛选 · 本次展示 {{layer?.records.length ?? 0}} / {{layer?.totalCount ?? '—'}} 条后台记录<span v-if="layer?.truncated"> · 已达采样上限，请进入相关页面查看其余记录</span></p>
          <div class="pipeline-record-list"><article v-for="record in records" :key="record.id" class="pipeline-record" data-testid="pipeline-record"><header><div><strong>{{record.title || record.id}}</strong><span class="pipeline-record-ids"><span v-if="record.stationId">工位 {{record.stationId}}</span><span v-if="record.vehicleId">车辆 {{record.vehicleId}}</span><span v-if="record.resourceId">资源 {{record.resourceId}}</span></span></div><span class="pipeline-status-badge" :class="pipelineRecordState(record.state).tone">{{pipelineRecordState(record.state).label}}</span></header><p>{{record.reason || '后台未提供额外说明。'}}</p><dl class="pipeline-record-facts"><div v-for="fact in record.facts.slice(0, 4)" :key="fact.label"><dt>{{fact.label}}</dt><dd>{{fact.value || '—'}}</dd></div></dl><details v-if="record.facts.length > 4"><summary>展开全部 {{record.facts.length}} 项依据</summary><dl class="pipeline-record-facts"><div v-for="fact in record.facts.slice(4)" :key="fact.label"><dt>{{fact.label}}</dt><dd>{{fact.value || '—'}}</dd></div></dl></details><footer><span>原记录更新 {{pipelineTime(record.updatedAt)}}</span><button v-if="record.taskId" @click="search = record.taskId; onlyIssues = false" title="保留任务编号，切换其他层继续查看">追踪任务 {{record.taskId}}</button><RouterLink v-if="pipelineInternalLink(record.link)" :to="pipelineInternalLink(record.link)!">查看相关记录 <IconArrowRight :size="12"/></RouterLink><small v-else class="pipeline-raw-id">{{record.id}}</small></footer></article><div v-if="!records.length" class="pipeline-empty"><strong>{{search || onlyIssues ? '当前层没有符合筛选的记录' : layer ? '本次采集未返回该层记录' : '尚未取得该层明细'}}</strong><p>{{search || onlyIssues ? '可清除筛选或切换其他层，按同一编号继续查看。' : '空记录不代表所有检查通过，请同时核对运行开关、判断依据和采样说明。'}}</p></div></div>
          <p class="pipeline-source-note">{{layer?.note || '等待后台提供该层数据来源与采样说明。'}} 原记录更新时间与本次采集时间含义不同；观察结果不授予车辆、PLC 或资源动作许可。</p>
        </section>
      </article>
    </div>
  </section>
</template>

<style scoped>
.dispatch-pipeline-page{gap:15px;padding:8px 4px 24px}.pipeline-page-title{display:flex;align-items:center;justify-content:space-between;gap:18px;padding:4px 0 3px}.pipeline-page-title h2{font-size:23px;font-weight:600;letter-spacing:.5px;color:#d9eafa}.pipeline-eyebrow{font-size:10px;color:#76a1bd;letter-spacing:1px;margin-bottom:7px}.pipeline-page-title>div>p:last-child{color:#91a7c3;font-size:12px;margin-top:8px;line-height:1.7}.pipeline-page-actions{display:flex;align-items:center;gap:15px;flex-wrap:wrap}.dispatch-pipeline-page a{display:inline-flex;align-items:center;gap:6px;color:#82cee0;text-decoration:none}.dispatch-pipeline-page a:hover{text-decoration:underline}.pipeline-banner{display:flex;gap:30px;justify-content:space-between;background:#122c40;border:1px solid #2d5368;border-left:3px solid #4eb7cd;padding:18px 22px}.pipeline-main-status{min-width:0}.pipeline-context-label{display:block;font-size:11px;color:#8db9cc;margin-bottom:8px}.pipeline-main-status>strong{font-size:18px;font-weight:500;color:#d1e8f4;line-height:1.55}.pipeline-main-status>p{font-size:12px;color:#91b0c7;line-height:1.8;margin-top:8px;max-width:900px}.pipeline-banner.historical{background:#30283a;border-color:#7d647a;border-left-color:#d8b7ce}.pipeline-banner.historical .pipeline-context-label{color:#efc9d8}.pipeline-banner.stale{border-color:#735a3d;background:#282637}.pipeline-timestamps{display:flex;align-items:flex-start;gap:27px;flex-shrink:0;margin:3px 0 0}.pipeline-timestamps dt,.pipeline-metrics dt{font-size:11px;color:#8faac3;margin-bottom:8px}.pipeline-timestamps dd{font-size:12px;color:#c0d6e9;margin:0;line-height:1.6;font-variant-numeric:tabular-nums}.pipeline-timestamps small{display:block;font-size:10px;color:#8da7bf;margin-top:4px}.pipeline-gates{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));border:1px solid #293e5e;background:#0e1b30}.pipeline-gates article{padding:12px 14px;min-width:0;border-right:1px solid #293e5e}.pipeline-gates article:last-child{border-right:0}.pipeline-gates article>div{display:flex;gap:8px;flex-wrap:wrap;justify-content:space-between;align-items:center}.pipeline-gates b{font-size:11px;color:#b3c9dc;font-weight:500}.pipeline-gates article>div>span{font-size:10px;color:#a5afbd;background:#243047;padding:3px 5px}.pipeline-gates .gate-on{color:#83ddbe;background:#193b37}.pipeline-gates .gate-off>div>span{color:#dfb77b;background:#3a3027}.pipeline-gates p{font-size:10px;line-height:1.8;color:#7e98b8;margin-top:9px;overflow-wrap:anywhere}.pipeline-observation-toolbar{display:flex;gap:12px 20px;align-items:center;flex-wrap:wrap;padding:2px 0 0}.pipeline-observation-buttons{display:flex;gap:8px;align-items:center}.pipeline-observation-toolbar>label{display:flex;gap:9px;align-items:center;color:#9bb1ca;font-size:11px}.pipeline-observation-toolbar select{max-width:325px}.pipeline-observation-toolbar>small{color:#7793b2;font-size:10px;line-height:1.7;margin-left:auto}.pipeline-alert,.pipeline-history-note{padding:11px 15px;background:#342d35;border-left:2px solid #cfaa79;color:#e4c69a;font-size:12px;line-height:1.8}.pipeline-history-note{background:#29253a;border-color:#a68cac;color:#cfb9d5}.pipeline-workspace{display:grid;grid-template-columns:263px minmax(0,1fr);gap:16px;align-items:start}.pipeline-layer-nav{background:#101d32;border:1px solid #293f5f;min-width:0;position:sticky;top:0}.pipeline-layer-nav>header{padding:16px 16px 13px;border-bottom:1px solid #293f5f}.pipeline-layer-nav h3{font-size:13px;font-weight:500;color:#bdd5e8;margin:0 0 5px}.pipeline-layer-nav>header>span{font-size:10px;color:#8fa3bd}.pipeline-layer-nav>button{display:flex;align-items:flex-start;justify-content:initial;width:100%;padding:15px 12px;gap:11px;border:0;border-bottom:1px solid #223654;border-radius:0;background:transparent;white-space:normal;text-align:left;min-height:98px}.pipeline-layer-nav>button:hover{background:#173249}.pipeline-layer-nav>button.selected{background:#17374d;box-shadow:inset 3px 0 #63cde2}.pipeline-layer-nav>button>svg{margin-top:4px;color:#658eb0;flex-shrink:0}.pipeline-step-number{display:flex;align-items:center;justify-content:center;flex-shrink:0;width:25px;height:25px;border:1px solid #46607e;color:#b6d2e5;font-family:Consolas,monospace}.pipeline-step-body{min-width:0;flex:1}.pipeline-step-body>b{display:block;font-size:13px;font-weight:500;color:#c1d9eb;line-height:1.5}.pipeline-step-body>small{display:block;font-size:10px;color:#86a3bd;line-height:1.6;margin-top:4px}.pipeline-state-text{display:flex;gap:6px;justify-content:space-between;align-items:baseline;font-size:10px;color:#b0bfce;margin-top:10px;line-height:1.5}.pipeline-state-text em{font-size:10px;font-style:normal;color:#7f9cba}.pipeline-layer-nav>p{padding:13px 15px;color:#758ead;font-size:10px;line-height:1.8}.pipeline-detail{border:1px solid #2c4263;background:#101d32;min-width:0;padding:20px}.pipeline-detail-header{display:flex;align-items:flex-start;justify-content:space-between;gap:15px}.pipeline-detail-header>div>p{color:#80a8c4;font-size:11px;margin-bottom:7px}.pipeline-detail-header h3{font-size:21px;line-height:1.5;font-weight:500;color:#d3e5f3;margin:0}.pipeline-detail-header>div>span{display:block;color:#92acc6;font-size:12px;margin-top:5px}.pipeline-status-badge{display:inline-block;white-space:normal;padding:4px 8px;background:#27364c;color:#b9c7d6;font-size:10px;line-height:1.7;flex-shrink:0}.pipeline-status-badge.ready{background:#173f38;color:#8cdec3}.pipeline-status-badge.running,.pipeline-status-badge.observing{background:#19445c;color:#8dd1e2}.pipeline-status-badge.waiting{background:#41372b;color:#e4c38d}.pipeline-status-badge.blocked{background:#492f40;color:#eba7b9}.pipeline-status-badge.stale,.pipeline-status-badge.unknown{background:#393447;color:#c3b7cf}.pipeline-status-badge.disabled,.pipeline-status-badge.idle{background:#29374a;color:#a6b5c9}.pipeline-detail-summary{font-size:12px;line-height:1.9;color:#b4cbe0;background:#152a41;padding:12px 15px;margin:17px 0 13px;overflow-wrap:anywhere}.pipeline-layer-boundary{display:flex;align-items:center;gap:18px;flex-wrap:wrap;padding:8px 0 15px;border-bottom:1px solid #2b405f}.pipeline-layer-boundary>div{display:flex;gap:9px;align-items:baseline}.pipeline-layer-boundary span{font-size:10px;color:#6f96b6}.pipeline-layer-boundary b{font-size:11px;font-weight:400;color:#abc3d9}.pipeline-layer-boundary>svg{color:#426580}.pipeline-layer-boundary>small{font-size:10px;color:#7895b3;margin-left:auto}.pipeline-metrics{display:flex;flex-wrap:wrap;gap:0;margin:18px 0 20px;border-bottom:1px solid #2b405f}.pipeline-metrics>div{padding:0 24px 15px 0;margin-right:22px}.pipeline-metrics dd{margin:0;color:#d1e5f2;font-size:24px;line-height:1.3;font-variant-numeric:tabular-nums;overflow-wrap:anywhere}.pipeline-metrics dd>small{font-size:11px;color:#86a0b9;margin-left:6px}.pipeline-checks-section>header,.pipeline-records-section>header{display:flex;gap:12px;align-items:baseline;justify-content:space-between;margin:20px 0 12px;flex-wrap:wrap}.pipeline-detail h4{font-size:13px;font-weight:500;margin:0;color:#bfd7e9}.pipeline-checks-section>header>span,.pipeline-records-section>header p{font-size:10px;color:#819ebd;line-height:1.7}.pipeline-table-wrap{overflow:auto;border:1px solid #293f5e}.pipeline-checks{width:100%;border-collapse:collapse;font-size:11px}.pipeline-checks th,.pipeline-checks td{padding:11px 12px;text-align:left;vertical-align:top;border-bottom:1px solid #263d59;color:#afc4db;line-height:1.8;min-width:110px}.pipeline-checks th{background:#14243d;font-size:10px;font-weight:400;color:#91adca}.pipeline-checks td:last-child{width:40%;min-width:230px}.pipeline-checks td:nth-child(3){width:25%;min-width:170px}.pipeline-checks td:first-child{width:16%}.pipeline-checks strong{font-weight:500;color:#bcd2e5}.pipeline-checks tr:last-child td{border-bottom:0}.pipeline-comparison b{font-size:11px;font-weight:400;color:#c4d9e9;display:block;overflow-wrap:anywhere}.pipeline-comparison small,.pipeline-record-time{display:block;color:#7e9abb;font-size:10px;line-height:1.7;margin-top:3px}.pipeline-records-section>header{margin-top:25px}.pipeline-records-section>header p{margin-top:5px}.pipeline-records-section>header>a{font-size:11px}.pipeline-record-tools{display:flex;gap:12px;align-items:center;flex-wrap:wrap}.pipeline-search{display:flex;align-items:center;gap:8px;border:1px solid #304a68;background:#0d192e;padding-left:10px;color:#7fa5bc;flex:1;min-width:240px}.pipeline-search input{border:0;border-radius:0;background:transparent;width:100%;min-width:0;padding-left:0}.pipeline-issue-filter{display:flex;gap:7px;align-items:center;font-size:11px;color:#9bb7ce}.pipeline-issue-filter input{min-height:auto;margin:0}.pipeline-issue-filter span{color:#d7bd94;background:#352e29;padding:2px 6px}.pipeline-record-count{font-size:10px;line-height:1.7;color:#809dbb;margin:10px 0}.pipeline-record-count>span{color:#c6ac82}.pipeline-record-list{border:1px solid #293f5e}.pipeline-record{padding:14px 16px;border-bottom:1px solid #293f5e;background:#112139}.pipeline-record:last-child{border-bottom:0}.pipeline-record>header{display:flex;gap:10px;align-items:flex-start;justify-content:space-between}.pipeline-record>header strong{font-weight:500;font-size:12px;color:#c1d9ec;line-height:1.8;overflow-wrap:anywhere}.pipeline-record-ids{display:flex;flex-wrap:wrap;gap:6px 15px;margin-top:3px;font-size:10px;color:#83a3bf}.pipeline-record>p{color:#a6bfd5;font-size:11px;line-height:1.9;margin:10px 0;overflow-wrap:anywhere}.pipeline-record-facts{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:9px 22px;margin:8px 0}.pipeline-record-facts>div{display:grid;grid-template-columns:minmax(75px,.55fr) minmax(0,1fr);gap:10px;align-items:baseline}.pipeline-record-facts dt{font-size:10px;color:#7e9cb9;line-height:1.7;overflow-wrap:anywhere}.pipeline-record-facts dd{font-size:11px;color:#b3cce0;margin:0;line-height:1.7;overflow-wrap:anywhere;font-variant-numeric:tabular-nums}.pipeline-record details{margin-top:9px}.pipeline-record details summary{font-size:10px;color:#91c7d9;cursor:pointer}.pipeline-record>footer{display:flex;gap:10px;align-items:center;flex-wrap:wrap;padding-top:10px;margin-top:10px;border-top:1px solid #293c58}.pipeline-record>footer>span,.pipeline-record>footer>small{font-size:9px;color:#718faf;overflow-wrap:anywhere}.pipeline-record>footer>button{font-size:10px;padding:3px 7px;min-height:24px;white-space:normal;overflow-wrap:anywhere}.pipeline-record>footer>a{margin-left:auto;font-size:10px}.pipeline-raw-id{margin-left:auto}.pipeline-source-note{font-size:10px;line-height:1.9;color:#7d97b5;margin-top:12px}.pipeline-empty{padding:25px 16px;text-align:center;color:#8ca4c0;font-size:12px;line-height:1.9}.pipeline-empty strong{font-weight:500;display:block}.pipeline-empty p{font-size:11px;color:#7693b2;margin-top:7px}.is-historical .pipeline-detail{border-color:#6b5871}.is-historical .pipeline-layer-nav>button.selected{background:#302940;box-shadow:inset 3px 0 #c9a2be}
@media(min-width:1700px){.pipeline-workspace{grid-template-columns:290px minmax(0,1fr)}.pipeline-record-facts{grid-template-columns:repeat(3,minmax(0,1fr))}.pipeline-records-section .pipeline-record{padding:16px 18px}}
@media(max-width:1200px){.pipeline-banner{flex-direction:column;gap:15px}.pipeline-gates{grid-template-columns:repeat(3,minmax(0,1fr))}.pipeline-gates article:nth-child(3n){border-right:0}.pipeline-gates article:nth-child(-n+3){border-bottom:1px solid #293e5e}.pipeline-observation-toolbar>small{margin-left:0;width:100%}.pipeline-workspace{grid-template-columns:235px minmax(0,1fr)}.pipeline-record-facts{grid-template-columns:1fr}.pipeline-detail{padding:16px}}
@media(max-width:800px){.pipeline-page-title{align-items:flex-start;flex-direction:column}.pipeline-page-title h2{font-size:21px}.pipeline-page-actions{width:100%;justify-content:space-between}.pipeline-workspace{grid-template-columns:1fr}.pipeline-layer-nav{position:static;display:grid;grid-template-columns:repeat(3,minmax(0,1fr))}.pipeline-layer-nav>header{grid-column:1/-1;display:flex;align-items:center;justify-content:space-between}.pipeline-layer-nav h3{margin:0}.pipeline-layer-nav>button{min-height:100px;border-right:1px solid #223654;padding:12px 9px;gap:7px}.pipeline-layer-nav>button:nth-of-type(3n){border-right:0}.pipeline-layer-nav>button>svg{display:none}.pipeline-layer-nav>p{grid-column:1/-1}.pipeline-step-body>b{font-size:11px}.pipeline-step-body>small{font-size:9px}.pipeline-state-text{flex-direction:column;gap:2px;margin-top:7px;font-size:9px}.pipeline-step-number{width:20px;height:20px;font-size:10px}.pipeline-detail-header h3{font-size:19px}.pipeline-checks th,.pipeline-checks td{padding:9px}.pipeline-layer-boundary>small{margin-left:0;width:100%}.pipeline-banner{padding:15px 16px}.pipeline-main-status>strong{font-size:16px}.pipeline-timestamps{flex-wrap:wrap;gap:15px 25px}.pipeline-record-facts{grid-template-columns:repeat(2,minmax(0,1fr))}.pipeline-record-facts>div{grid-template-columns:1fr;gap:3px}.pipeline-metrics{margin-bottom:14px}.pipeline-metrics>div{padding-right:15px;margin-right:12px}.pipeline-metrics dd{font-size:22px}}
@media(max-width:480px){.pipeline-gates{grid-template-columns:repeat(2,minmax(0,1fr))}.pipeline-gates article:nth-child(3n){border-right:1px solid #293e5e}.pipeline-gates article:nth-child(2n){border-right:0}.pipeline-gates article:nth-child(-n+4){border-bottom:1px solid #293e5e}.pipeline-layer-nav{grid-template-columns:repeat(2,minmax(0,1fr))}.pipeline-layer-nav>button:nth-of-type(3n){border-right:1px solid #223654}.pipeline-layer-nav>button:nth-of-type(2n){border-right:0}.pipeline-observation-toolbar>label{width:100%;flex-wrap:wrap}.pipeline-observation-toolbar select{width:100%;max-width:none}.pipeline-observation-buttons{width:100%;flex-wrap:wrap}.pipeline-observation-buttons>button{font-size:10px;flex:1}.pipeline-record-facts{grid-template-columns:1fr}.pipeline-record-facts>div{grid-template-columns:90px minmax(0,1fr)}.pipeline-record-tools .pipeline-search{min-width:100%;width:100%}.pipeline-detail{padding:13px}.pipeline-layer-boundary{gap:10px}.pipeline-layer-boundary>div{width:100%}.pipeline-layer-boundary>svg{display:none}.pipeline-record{padding:12px}.pipeline-record>footer>a{margin-left:0}.pipeline-record>header{flex-direction:column;gap:7px}.pipeline-timestamps{flex-direction:column}.pipeline-gates article{padding:11px}.pipeline-main-status>strong{font-size:15px}}
.pipeline-layer-nav>button{min-height:80px;padding-top:10px;padding-bottom:10px}.pipeline-step-body>small{margin-top:2px}.pipeline-state-text{margin-top:7px}.pipeline-gates p{font-size:11px}.pipeline-checks td,.pipeline-record>p,.pipeline-record-facts dd{font-size:12px}
</style>
