<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import ConsoleDialog from './ConsoleDialog.vue';
import { useDispatchStore } from './stores/dispatch';
import { shortTime } from './dashboard';
import { closureMapMatches, closurePageCurrent, closureRouteCurrent, closureSites,
  type RoadClosure, type RoadClosurePage, type RoadRoutePreview } from './road-closures';

const store = useDispatchStore();
const open = ref(false), page = ref<RoadClosurePage | null>(null), candidate = ref<RoadRoutePreview | null>(null);
const loading = ref(false), saving = ref(false), error = ref(''), message = ref('');
const selectedEdgeId = ref(''), bidirectional = ref(true), reason = ref(''), locationConfirmed = ref(false);
const reverseDirection = ref(false);
const source = ref(''), destination = ref('');
const releasing = ref<RoadClosure | null>(null), releaseReason = ref(''), clearConfirmed = ref(false);
const createRequestId = ref(''), releaseRequestId = ref('');
let epoch = 0, timer: ReturnType<typeof setInterval> | undefined;
const map = computed(() => store.snapshot?.map);
const context = computed(() => [store.demoMode, store.adapterIdentity?.sha256,
  map.value?.id, map.value?.version, map.value?.definitionHash].join('|'));
const backend = computed(() => !store.demoMode && !store.engineeringSimulationOnly && store.fresh && store.snapshot?.source === 'backend');
const current = computed(() => backend.value && closurePageCurrent(page.value, map.value, store.fresh, store.now));
const items = computed(() => page.value?.closures ?? []);
const active = computed(() => items.value.filter(item => item.active && closureMapMatches(item, map.value)));
const sites = computed(() => closureSites(map.value));
const roads = computed(() => (map.value?.edges ?? []).filter(edge => edge.enabled));
const selectedEdge = computed(() => roads.value.find(edge => edge.id === selectedEdgeId.value));
const canCreate = computed(() => current.value && !saving.value && !!selectedEdge.value && reason.value.trim().length >= 5 && locationConfirmed.value);
const candidateCurrent = computed(() => backend.value && closureRouteCurrent(candidate.value, page.value, map.value,
  source.value, destination.value, store.fresh, store.now));

function expectedMap() {
  return { expectedMapId: map.value?.id, expectedVersionId: map.value?.version,
    expectedDefinitionHash: map.value?.definitionHash };
}
function resetCandidate() { candidate.value = null; }
async function load() {
  if (!backend.value || loading.value || saving.value) return;
  const mine = epoch, ctx = context.value;
  loading.value = true;
  try {
    const result = await store.api<Omit<RoadClosurePage, 'receivedAt'>>('/api/road-closures');
    if (mine !== epoch || ctx !== context.value) return;
    if (page.value?.revision !== result.revision) resetCandidate();
    page.value = { ...result, receivedAt: new Date().toISOString() };
    error.value = '';
  } catch (ex) {
    if (mine === epoch) { page.value = null; resetCandidate(); error.value = String(ex); }
  } finally { if (mine === epoch) loading.value = false; }
}
async function mutate(action: () => Promise<unknown>, success: string) {
  if (saving.value) return;
  const mine = epoch;
  saving.value = true; error.value = ''; message.value = ''; resetCandidate();
  try {
    await action();
    if (mine === epoch) { message.value = success; releasing.value = null; reason.value = ''; locationConfirmed.value = false; }
  } catch (ex) {
    if (mine === epoch) error.value = `${String(ex)}；请刷新封锁记录，核对原操作结果后再处理。`;
  } finally { saving.value = false; await load(); }
}
async function create() {
  const edge = selectedEdge.value;
  if (!canCreate.value || !edge) return;
  createRequestId.value ||= crypto.randomUUID();
  const payload = { ...expectedMap(), requestId: createRequestId.value, edgeId: edge.id,
    fromNodeId: reverseDirection.value ? edge.to : edge.from, toNodeId: reverseDirection.value ? edge.from : edge.to,
    bidirectional: bidirectional.value, reason: reason.value.trim(), source: 'Operator' };
  await mutate(async () => {
    const result = await store.api<{ accepted: boolean; message: string }>('/api/road-closures', payload);
    if (!result.accepted) throw new Error(result.message);
    createRequestId.value = '';
  }, '已记录道路封锁；后续路径规划将避开受影响通道。');
}
function selectRelease(item: RoadClosure) {
  if (!current.value || !closureMapMatches(item, map.value) || item.releasedAt) return;
  releasing.value = item; releaseReason.value = ''; clearConfirmed.value = false; releaseRequestId.value = '';
}
async function release() {
  const item = releasing.value;
  if (!item || !current.value || !closureMapMatches(item, map.value) || saving.value || !clearConfirmed.value || releaseReason.value.trim().length < 5) return;
  releaseRequestId.value ||= crypto.randomUUID();
  const payload = { requestId: releaseRequestId.value, closureId: item.closureId, expectedRevision: page.value!.revision,
    reason: releaseReason.value.trim(), siteClearedConfirmed: true };
  await mutate(async () => {
    const result = await store.api<{ accepted: boolean; message: string }>('/api/road-closures/release', payload);
    if (!result.accepted) throw new Error(result.message);
    releaseRequestId.value = '';
  }, '已记录人工解封；车辆执行状态与路权仍由原流程核对。');
}
async function preview() {
  if (!current.value || saving.value || !source.value || !destination.value || source.value === destination.value) return;
  const mine = epoch, ctx = context.value, from = source.value, to = destination.value;
  saving.value = true; error.value = ''; message.value = ''; resetCandidate();
  try {
    const result = await store.api<Omit<RoadRoutePreview, 'receivedAt'>>('/api/road-closures/preview', {
      ...expectedMap(), expectedRevision: page.value!.revision, sourceSite: from, targetSite: to });
    if (mine === epoch && ctx === context.value && from === source.value && to === destination.value)
      candidate.value = { ...result, receivedAt: new Date().toISOString() };
  } catch (ex) { if (mine === epoch) error.value = String(ex); }
  finally { saving.value = false; }
}
watch([source, destination], resetCandidate);
watch(context, () => {
  ++epoch; loading.value = false; page.value = null; resetCandidate(); selectedEdgeId.value = '';
  source.value = ''; destination.value = ''; releasing.value = null; locationConfirmed.value = false;
  reason.value = ''; releaseReason.value = ''; clearConfirmed.value = false; message.value = '';
  void load();
});
watch(backend, available => { if (available) void load(); else { ++epoch; loading.value = false; page.value = null; resetCandidate(); releasing.value = null; } });
watch(selectedEdgeId, () => { locationConfirmed.value = false; reverseDirection.value = false; });
watch([selectedEdgeId, reverseDirection, bidirectional], () => { locationConfirmed.value = false; });
watch([selectedEdgeId, reverseDirection, bidirectional, reason], () => { if (!saving.value) createRequestId.value = ''; });
watch(releaseReason, () => { if (!saving.value) releaseRequestId.value = ''; });
onMounted(() => { void load(); timer = setInterval(() => void load(), 5000); });
onUnmounted(() => { ++epoch; clearInterval(timer); });
</script>

<template>
  <section class="control-panel closures-panel">
    <header class="section-heading"><h2>道路封锁与绕行 <small>TRAFFIC</small></h2><button @click="open = true">管理封锁 / 预览路线</button></header>
    <p v-if="!backend">连接当前后台后可查询道路封锁。</p>
    <p v-else-if="error" role="status">{{ error }}</p>
    <p v-else-if="page">{{ map?.id }} / {{ map?.version }} · {{ current ? '当前读取' : '记录待刷新' }} · {{ active.length }} 条有效封锁 · 读取 {{ shortTime(page.receivedAt) }}</p>
    <p v-else>正在读取当前道路封锁…</p>
    <div v-if="active.length" class="closure-summary"><span v-for="item in active.slice(0, 6)" :key="item.closureId">{{ item.fromNodeId }} {{ item.bidirectional ? '↔' : '→' }} {{ item.toNodeId }} · {{ item.reason }}</span></div>
    <p class="panel-footnote">短暂受阻保留原任务；持续受阻先核对通道。封路不释放车辆占用，路线预览不发送导航。</p>
    <ConsoleDialog v-if="open" title="道路封锁与绕行预览" wide @close="open = false">
      <div class="closure-management">
        <p>当前运行地图：<b>{{ map?.id || '未知' }} / {{ map?.version || '未知' }}</b><br/>地图内容：{{ map?.definitionHash || '待核对' }}</p>
        <p>按现场确认的受影响通道封路。车辆显示受阻不代表已确定障碍位置；封锁会影响后续路径规划。</p>
        <button :disabled="loading || saving || !backend" @click="load">刷新封锁记录</button>
        <p v-if="!current" class="inline-warning">当前地图或封锁观察尚未核对，暂不能变更封锁或预览路线。</p>
        <p v-if="error" role="alert" class="inline-warning">{{ error }}</p><p v-if="message" role="status">{{ message }}</p>
        <h3>封锁受影响通道</h3>
        <div class="closure-fields">
          <label>地图路段<select v-model="selectedEdgeId" :disabled="saving || !current"><option value="">请选择现场核对的路段</option><option v-for="edge in roads" :key="edge.id" :value="edge.id">{{ edge.id }} · {{ edge.from }} → {{ edge.to }}</option></select></label>
          <label>封锁范围<select v-model="bidirectional" :disabled="saving || !current"><option :value="true">双向通道（含反方向道路）</option><option :value="false">仅所选行驶方向</option></select></label>
          <label v-if="selectedEdge?.bidirectional && !bidirectional">行驶方向<select v-model="reverseDirection" :disabled="saving || !current"><option :value="false">{{ selectedEdge.from }} → {{ selectedEdge.to }}</option><option :value="true">{{ selectedEdge.to }} → {{ selectedEdge.from }}</option></select></label>
          <label class="closure-wide">现场原因<input v-model="reason" :disabled="saving || !current" maxlength="500" placeholder="填写障碍位置及现场核对情况，至少5字"/></label>
        </div>
        <label class="closure-check"><input v-model="locationConfirmed" type="checkbox" :disabled="saving || !current || !selectedEdge"/>已核对地图路段、行驶方向与现场受影响通道一致</label>
        <button :disabled="!canCreate" @click="create">封锁此通道</button>
        <h3>封锁记录</h3>
        <div class="closure-table"><table class="dialog-table"><thead><tr><th>通道 / 地图</th><th>状态 / 时间</th><th>依据</th><th>操作</th></tr></thead><tbody>
          <tr v-for="item in items" :key="item.closureId"><td>{{ item.fromNodeId }} {{ item.bidirectional ? '↔' : '→' }} {{ item.toNodeId }}<small>{{ item.mapId }} / {{ item.versionId }}</small><small v-for="traversal in item.traversals" :key="JSON.stringify(traversal)">{{ traversal.edgeId }} · {{ traversal.fromNodeId }} → {{ traversal.toNodeId }}</small></td><td>{{ item.active ? '封锁中' : '已解除' }}<small>{{ shortTime(item.releasedAt || item.createdAt) }}</small></td><td>{{ item.reason }}<small v-if="item.releaseReason">解封依据：{{ item.releaseReason }}</small></td><td><button v-if="item.active" :disabled="saving || !current || !closureMapMatches(item, map)" @click="selectRelease(item)">核对后解封</button><span v-else>审计保留</span></td></tr>
          <tr v-if="!items.length"><td colspan="4">当前没有返回道路封锁记录。</td></tr>
        </tbody></table></div>
        <fieldset v-if="releasing"><legend>解除 {{ releasing.fromNodeId }} {{ releasing.bidirectional ? '↔' : '→' }} {{ releasing.toNodeId }}</legend>
          <label>清理及复核依据<input v-model="releaseReason" :disabled="saving" maxlength="500" placeholder="填写障碍清理和现场通道检查情况，至少5字"/></label>
          <label class="closure-check"><input v-model="clearConfirmed" type="checkbox" :disabled="saving"/>已现场确认障碍清除，通道允许恢复规划</label>
          <button :disabled="saving || !current || !clearConfirmed || releaseReason.trim().length < 5" @click="release">记录人工解封</button><button :disabled="saving" @click="releasing = null">取消编辑</button>
        </fieldset>
        <h3>备选路线预览</h3>
        <p>用于核对后续任务能否避开封锁。正在行驶的车辆须按实际停车位置重新核验接入点，不能沿用原起点直接重发。</p>
        <div class="closure-fields"><label>规划起点<select v-model="source" :disabled="saving || !current"><option value="">请选择起点</option><option v-for="site in sites" :key="site.id" :value="site.id">{{ site.label }}</option></select></label><label>规划终点<select v-model="destination" :disabled="saving || !current"><option value="">请选择终点</option><option v-for="site in sites" :key="site.id" :value="site.id" :disabled="site.id === source">{{ site.label }}</option></select></label></div>
        <button :disabled="saving || !current || !source || !destination || source === destination" @click="preview">预览避开封锁的路线</button>
        <div v-if="candidate" class="closure-candidate"><b>{{ !candidateCurrent ? '候选已失效，请重新预览' : candidate.available ? '存在候选通路 · 尚未发送' : '没有可用通路 · 保持等待' }}</b><p>{{ candidate.message }}</p><p v-if="candidate.route?.nodes.length">{{ candidate.route.nodes.join(' → ') }}</p><small>地图 {{ candidate.mapId }} / {{ candidate.versionId }} · 读取 {{ shortTime(candidate.receivedAt) }}</small></div>
      </div>
    </ConsoleDialog>
  </section>
</template>

<style scoped>
.closures-panel{margin-top:18px;padding-bottom:12px}.closures-panel>p{padding:0 16px;font-size:12px;line-height:1.8;margin:10px 0}.closure-summary{display:flex;flex-wrap:wrap;gap:8px;padding:0 16px;font-size:12px}.closure-summary span{border:1px solid #906940;background:#302820;padding:5px 9px}.closure-management{font-size:12px;line-height:1.8;overflow-wrap:anywhere}.closure-management p{margin:12px 0}.closure-management h3{font-size:14px;color:#d1e5f5;margin:24px 0 12px}.closure-management button{margin:7px 9px 7px 0}.closure-fields{display:grid;grid-template-columns:1fr 1fr;gap:14px}.closure-fields label,.closure-management fieldset>label{display:grid;gap:6px}.closure-fields input,.closure-fields select,.closure-management fieldset input:not([type=checkbox]){width:100%;min-width:0;min-height:34px}.closure-wide{grid-column:1/-1}.closure-check{display:flex;align-items:center;gap:9px;margin:14px 0;line-height:1.8}.closure-management fieldset>.closure-check{display:flex}.closure-table{overflow:auto;max-height:360px}.closure-table table{width:100%;border-collapse:collapse}.closure-table td,.closure-table th{padding:10px;text-align:left;border-bottom:1px solid #304765;white-space:normal;min-width:110px;max-width:320px;overflow-wrap:anywhere}.closure-table small{display:block;color:#8aa9c9;margin-top:4px}.closure-management fieldset{margin:18px 0;padding:16px;border:1px solid #47617e}.closure-candidate{border-left:3px solid #52b9d4;background:#10263a;margin-top:12px;padding:12px 16px}.closure-candidate b{color:#acdfe9}.closure-candidate small{color:#8da8c8}@media(max-width:600px){.closure-fields{grid-template-columns:1fr}.closure-wide{grid-column:auto}.closures-panel .section-heading{height:auto;min-height:44px;flex-wrap:wrap;padding:10px;gap:8px}}
</style>
