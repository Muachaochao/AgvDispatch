<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { replayFrameIndex, type ReplayCoverage, type ReplayEvent, type ReplayTime } from '../replay-evidence';

const props = defineProps<{
  events: readonly ReplayEvent[];
  frames: readonly ReplayTime[];
  coverage?: ReplayCoverage;
}>();
const emit = defineEmits<{ seek: [index: number, event: ReplayEvent] }>();
const vehicle = ref(''), task = ref(''), category = ref(''), page = ref(0);
const selected = ref<ReplayEvent>();
const expanded = ref(true);
const pageSize = 25;
const categories: Record<string, string> = {
  task: '任务', command: '命令', motion: '移动／到站', plc: 'PLC', traffic: '路权', anomaly: '异常／恢复',
};
const vehicleIds = computed(() => [...new Set(props.events.flatMap(event => event.vehicleId ? [event.vehicleId] : []))].sort());
const taskIds = computed(() => [...new Set(props.events.flatMap(event => event.taskId ? [event.taskId] : []))].sort());
const filtered = computed(() => props.events.filter(event =>
  (!vehicle.value || event.vehicleId === vehicle.value || event.holderVehicleIds?.includes(vehicle.value))
  && (!task.value || event.taskId === task.value) && (!category.value || event.category === category.value)));
const visible = computed(() => filtered.value.slice(page.value * pageSize, (page.value + 1) * pageSize));
const markers = computed(() => {
  const start = props.frames[0]?.time ?? filtered.value[0]?.time;
  const end = props.frames.at(-1)?.time ?? filtered.value.at(-1)?.time;
  if (start === undefined || end === undefined) return [];
  const bins = new Map<number, ReplayEvent>();
  for (const event of filtered.value) {
    const bin = Math.max(0, Math.min(49, Math.floor((event.time - start) / Math.max(end - start, 1) * 50)));
    const current = bins.get(bin);
    if (!current || event.category === 'anomaly' || event.kind === 'traffic-wait') bins.set(bin, event);
  }
  return [...bins].map(([bin, event]) => ({ bin, event }));
});
const observation = computed(() => selected.value && props.frames[replayFrameIndex(props.frames, selected.value.time)]);
const attentionCount = computed(() => props.events.filter(event => event.category === 'anomaly').length);
const visibleWarnings = computed(() => props.coverage?.warnings ?? []);
watch([vehicle, task, category, () => props.events], () => { page.value = 0; selected.value = undefined; });

function select(event: ReplayEvent) {
  selected.value = event;
  if (props.frames.length) emit('seek', replayFrameIndex(props.frames, event.time), event);
}
function format(time: number) {
  return new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit',
    fractionalSecondDigits: 3, hour12: false,
  }).format(new Date(time * 1000));
}
function formatClock(time: number) {
  return new Intl.DateTimeFormat('zh-CN', {
    hour: '2-digit', minute: '2-digit', second: '2-digit',
    fractionalSecondDigits: 3, hour12: false,
  }).format(new Date(time * 1000));
}
function categoryLabel(value: string) {
  return categories[value] ?? value;
}
</script>

<template>
  <section class="replay-evidence" aria-label="回放事件">
    <header class="evidence-heading">
      <div>
        <p class="eyebrow">LOG EVIDENCE</p>
        <h4>日志证据时间轴</h4>
        <p class="evidence-subtitle">事件按业务时间排列，点击任一记录可回到对应现场。</p>
      </div>
      <div class="heading-metrics" aria-label="事件统计">
        <span><strong>{{ events.length }}</strong> 可定位事件</span>
        <span v-if="attentionCount" class="attention"><strong>{{ attentionCount }}</strong> 异常／恢复</span>
        <button type="button" class="expand-button" :aria-expanded="expanded" @click="expanded = !expanded">
          {{ expanded ? '收起日志' : '展开日志' }}
        </button>
      </div>
    </header>
    <ul v-if="visibleWarnings.length" class="coverage" aria-label="证据缺口">
      <li v-for="warning in visibleWarnings" :key="warning">{{ warning }}</li>
    </ul>
    <div v-show="expanded" class="evidence-body">
      <div v-if="events.length" class="event-filters">
        <label><span>车辆</span><select v-model="vehicle" aria-label="按车辆筛选"><option value="">全部车辆</option><option v-for="id in vehicleIds" :key="id">{{ id }}</option></select></label>
        <label><span>任务</span><select v-model="task" aria-label="按任务筛选"><option value="">全部任务</option><option v-for="id in taskIds" :key="id">{{ id }}</option></select></label>
        <label><span>事件</span><select v-model="category" aria-label="按事件筛选"><option value="">全部事件</option><option v-for="(label, key) in categories" :key="key" :value="key">{{ label }}</option></select></label>
        <span class="filter-result">{{ filtered.length }} 条</span>
      </div>
      <p v-if="!filtered.length" class="evidence-empty">{{ events.length ? '没有符合筛选条件的事件。' : '本轮没有可定位的事件记录。' }}</p>
      <ul v-if="filtered.length" class="category-legend" aria-label="事件类型颜色">
        <li v-for="(label, key) in categories" :key="key" :class="'event-' + key"><i />{{ label }}</li>
      </ul>
      <div v-if="markers.length" class="event-track" role="group" aria-label="事件时间轴">
        <i class="track-line" />
        <button v-for="{ bin, event } in markers" :key="bin" type="button"
          :class="'event-' + event.category"
          :style="{ left: 2 + bin * 1.92 + '%' }" :title="format(event.time) + ' · ' + event.title"
          :aria-label="format(event.time) + ' · ' + event.title" @click="select(event)" />
        <span class="track-start">{{ frames[0] ? formatClock(frames[0].time) : '' }}</span>
        <span class="track-end">{{ frames.at(-1) ? formatClock(frames.at(-1)!.time) : '' }}</span>
      </div>
      <article v-if="selected" class="selected-evidence">
        <header>
          <div><span class="evidence-chip">{{ categoryLabel(selected.category) }}</span><h5>{{ selected.title }}</h5></div>
          <time>{{ format(selected.time) }}</time>
        </header>
        <dl>
          <div><dt>来源</dt><dd>{{ selected.source }}</dd></div>
          <div v-if="selected.wallTime !== undefined"><dt>记录时间</dt><dd>{{ format(selected.wallTime) }}</dd></div>
          <div v-if="selected.position"><dt>车辆原始采样时间</dt><dd>{{ format(selected.position.time) }}</dd></div>
          <div v-else-if="observation"><dt>地图采样时间</dt><dd>{{ format(observation.time) }}</dd></div>
          <div v-if="selected.taskId"><dt>任务</dt><dd>{{ selected.taskId }}</dd></div>
          <div v-if="selected.commandId || selected.vendorCommandId"><dt>命令</dt><dd>{{ selected.commandId || selected.vendorCommandId }}</dd></div>
          <div v-if="selected.resourceIds?.length"><dt>资源</dt><dd>{{ selected.resourceIds.join('、') }}</dd></div>
          <div v-if="selected.holderVehicleIds?.length"><dt>当时占用</dt><dd>{{ selected.holderVehicleIds.join('、') }}</dd></div>
        </dl>
        <p v-if="selected.commandId && coverage?.unconfirmedCommands.includes(selected.commandId)" class="unconfirmed">未匹配到设备接收记录</p>
        <details v-if="selected.detail"><summary>查看结构化原记录</summary><pre>{{ JSON.stringify(selected.detail, null, 2) }}</pre></details>
      </article>
      <ol v-if="filtered.length" class="evidence-list">
        <li v-for="(event, index) in visible" :key="page * pageSize + index">
          <button type="button" :class="'event-' + event.category" :aria-pressed="selected === event" @click="select(event)">
            <time>{{ formatClock(event.time) }}</time>
            <span class="event-marker" />
            <strong>{{ event.title }}</strong>
            <span class="event-vehicle">{{ event.vehicleId || event.stationId || '全场' }}</span>
            <small>{{ event.holderVehicleIds?.length ? '占用 ' + event.holderVehicleIds.join('、') : event.source }}</small>
          </button>
        </li>
      </ol>
      <div v-if="filtered.length > pageSize" class="event-pagination">
        <button type="button" :disabled="page === 0" @click="page--">上一页</button>
        <span>{{ page * pageSize + 1 }}–{{ Math.min((page + 1) * pageSize, filtered.length) }} / {{ filtered.length }}</span>
        <button type="button" :disabled="(page + 1) * pageSize >= filtered.length" @click="page++">下一页</button>
      </div>
    </div>
  </section>
</template>

<style scoped>
.replay-evidence {
  --evidence-ink:#16283b;
  --evidence-muted:#65778b;
  --evidence-line:#dbe5ef;
  --evidence-blue:#2679ae;
  min-width:0;
  color:var(--evidence-ink);
  background:#fff;
  border:1px solid var(--evidence-line);
  border-radius:16px;
  overflow:hidden;
  box-shadow:0 12px 30px rgb(40 68 94 / 8%);
}
.evidence-heading {
  display:flex;
  align-items:flex-end;
  justify-content:space-between;
  gap:20px;
  padding:18px 20px 16px;
  background:linear-gradient(135deg,#f5f9fc,#fff);
  border-bottom:1px solid var(--evidence-line);
}
.replay-evidence h4,.replay-evidence h5,.replay-evidence p { margin:0; }
.replay-evidence h4 { font-size:19px; line-height:1.25; letter-spacing:.01em; }
.eyebrow { color:var(--evidence-blue); font-size:10px; font-weight:800; letter-spacing:.18em; }
.evidence-subtitle { margin-top:5px!important; color:var(--evidence-muted); font-size:12px; }
.heading-metrics { display:flex; align-items:center; justify-content:flex-end; flex-wrap:wrap; gap:8px; color:var(--evidence-muted); font-size:12px; }
.heading-metrics span { padding:6px 9px; border:1px solid var(--evidence-line); border-radius:999px; background:#fff; }
.heading-metrics strong { color:var(--evidence-ink); font-size:13px; }
.heading-metrics .attention { color:#9a5b13; border-color:#efd5ad; background:#fff9ed; }
.heading-metrics .attention strong { color:#9a5b13; }
.expand-button,.event-pagination button {
  font:inherit;
  color:var(--evidence-ink);
  background:#fff;
  border:1px solid #bccbd9;
  border-radius:8px;
  padding:6px 10px;
  cursor:pointer;
}
.coverage { display:flex; flex-wrap:wrap; gap:6px; margin:0; padding:10px 20px; list-style:none; color:#8c5a14; background:#fff8e9; border-bottom:1px solid #f1dfbd; font-size:12px; }
.coverage li { padding:3px 8px; border-radius:6px; background:#fff; border:1px solid #f0dbb4; }
.evidence-body { display:grid; gap:12px; padding:16px 20px 20px; }
.event-filters { display:flex; flex-wrap:wrap; align-items:flex-end; gap:10px; }
.event-filters label { display:grid; gap:4px; color:var(--evidence-muted); font-size:11px; font-weight:700; }
.replay-evidence select {
  min-width:130px;
  height:34px;
  font:inherit;
  color:var(--evidence-ink);
  background:#f8fbfd;
  border:1px solid #cbd8e4;
  border-radius:8px;
  padding:0 28px 0 10px;
}
.replay-evidence option { color:#182536; background:#f0f5fa; }
.filter-result { margin-left:auto; padding-bottom:8px; color:var(--evidence-muted); font-size:12px; }
.evidence-empty { padding:24px; text-align:center; color:var(--evidence-muted); background:#f8fafc; border-radius:10px; }
.category-legend { display:flex; flex-wrap:wrap; gap:6px 12px; margin:0; padding:0; list-style:none; color:var(--evidence-muted); font-size:9px; }
.category-legend li { display:inline-flex; align-items:center; gap:5px; }
.category-legend i { width:7px; height:7px; background:var(--event-color,#6688a2); border-radius:50%; }
.category-legend .event-task { --event-color:#3273b8; }
.category-legend .event-command { --event-color:#2679ae; }
.category-legend .event-motion { --event-color:#138d75; }
.category-legend .event-plc { --event-color:#7761bd; }
.category-legend .event-traffic { --event-color:#c58b25; }
.category-legend .event-anomaly { --event-color:#c34e68; }
.event-track { height:48px; position:relative; margin:2px 9px 0; }
.event-track .track-line { position:absolute; left:0; right:0; top:19px; height:2px; border-radius:999px; background:linear-gradient(90deg,#b8d7e8,#adc4d8); }
.event-track button {
  position:absolute;
  top:12px;
  width:9px;
  height:16px;
  padding:0;
  transform:translateX(-50%);
  background:var(--event-color,#3f83b2);
  border:2px solid #fff;
  border-radius:4px;
  box-shadow:0 0 0 1px var(--event-color,#3f83b2);
  cursor:pointer;
}
.event-track .event-task { --event-color:#3273b8; }
.event-track .event-command { --event-color:#2679ae; }
.event-track .event-motion { --event-color:#138d75; }
.event-track .event-plc { --event-color:#7761bd; }
.event-track .event-traffic { --event-color:#c58b25; }
.event-track .event-anomaly { --event-color:#c34e68; }
.track-start,.track-end { position:absolute; top:31px; color:var(--evidence-muted); font-size:10px; font-variant-numeric:tabular-nums; }
.track-start { left:0; }
.track-end { right:0; }
.selected-evidence { padding:14px; border:1px solid #bed8e8; border-left:4px solid #2d86b8; border-radius:10px; background:#f7fbfe; overflow-wrap:anywhere; }
.selected-evidence>header { display:flex; align-items:flex-start; justify-content:space-between; gap:16px; }
.selected-evidence>header>div { display:flex; align-items:center; gap:9px; min-width:0; }
.selected-evidence h5 { font-size:15px; }
.selected-evidence time { flex:none; color:var(--evidence-muted); font:700 11px ui-monospace,SFMono-Regular,Consolas,monospace; }
.evidence-chip { flex:none; padding:3px 7px; color:#1d6388; background:#e2f2fa; border-radius:999px; font-size:10px; font-weight:800; }
.selected-evidence dl { display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:8px 16px; margin:12px 0 0; }
.selected-evidence dl div { min-width:0; }
.selected-evidence dt { color:var(--evidence-muted); font-size:10px; }
.selected-evidence dd { margin:2px 0 0; font-size:12px; font-weight:650; word-break:break-all; }
.selected-evidence .unconfirmed { margin-top:9px; color:#a1571a; font-size:12px; font-weight:700; }
.selected-evidence details { margin-top:10px; font-size:12px; }
.selected-evidence summary { cursor:pointer; color:#356d91; }
.selected-evidence pre { white-space:pre-wrap; max-height:220px; overflow:auto; margin:8px 0 0; padding:10px; background:#0f1e2d; color:#d9e8f2; border-radius:8px; font:11px/1.55 ui-monospace,SFMono-Regular,Consolas,monospace; }
.evidence-list { list-style:none; display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:6px; margin:0; padding:0; max-height:360px; overflow:auto; }
.evidence-list button {
  --event-color:#3273b8;
  position:relative;
  width:100%;
  min-height:58px;
  display:grid;
  grid-template-columns:66px 8px minmax(0,1fr) auto;
  grid-template-rows:auto auto;
  align-items:center;
  gap:2px 8px;
  text-align:left;
  color:var(--evidence-ink);
  background:#fff;
  border:1px solid #e0e8ef;
  border-radius:9px;
  padding:8px 10px;
  cursor:pointer;
}
.evidence-list button:hover { border-color:#a9c4d7; background:#f8fbfd; }
.evidence-list button[aria-pressed="true"] { border-color:var(--event-color); box-shadow:inset 3px 0 var(--event-color); background:#f5fafc; }
.evidence-list .event-task { --event-color:#3273b8; }
.evidence-list .event-command { --event-color:#2679ae; }
.evidence-list .event-motion { --event-color:#138d75; }
.evidence-list .event-plc { --event-color:#7761bd; }
.evidence-list .event-traffic { --event-color:#c58b25; }
.evidence-list .event-anomaly { --event-color:#c34e68; }
.evidence-list time { grid-row:1 / span 2; color:#53697d; font:700 10px ui-monospace,SFMono-Regular,Consolas,monospace; font-variant-numeric:tabular-nums; white-space:nowrap; }
.event-marker { grid-row:1 / span 2; width:6px; height:34px; border-radius:999px; background:var(--event-color); }
.evidence-list strong { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-size:12px; }
.event-vehicle { color:#1f668f; font-size:10px; font-weight:800; white-space:nowrap; }
.evidence-list small { grid-column:3 / span 2; overflow:hidden; color:var(--evidence-muted); font-size:10px; text-overflow:ellipsis; white-space:nowrap; }
.event-pagination { display:flex; align-items:center; justify-content:center; gap:12px; color:var(--evidence-muted); font-size:12px; }
.event-pagination button:disabled { opacity:.42; cursor:default; }
@media (max-width:900px) {
  .evidence-heading { align-items:flex-start; flex-direction:column; }
  .heading-metrics { justify-content:flex-start; }
  .evidence-list { grid-template-columns:1fr; }
}
</style>
