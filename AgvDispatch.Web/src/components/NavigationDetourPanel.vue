<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useDispatchStore } from '../stores/dispatch';
import { autoDetourStateLabels, detourCanContinue, detourPreviewCurrent, detourStateLabels,
  type NavigationDetourCurrent, type NavigationDetourPreview } from '../navigation-detour';
import { industrialText } from '../operator-labels';

const props = defineProps<{ current: NavigationDetourCurrent; enabled: boolean; readCurrent: boolean; busy: boolean; now: number;
  autoDetourEnabled: boolean; autoDetourWaitSeconds: number }>();
const emit = defineEmits<{ busy: [value: boolean]; changed: [] }>();
const store = useDispatchStore();
const candidate = ref<NavigationDetourPreview | null>(null), error = ref('');
const cancelConfirmed = ref(false), executeConfirmed = ref(false), pending = ref(false);
const context = computed(() => [props.current.taskId, props.current.vehicleId, props.current.robotMap,
  props.current.robotMapMd5, store.snapshot?.map?.id, store.snapshot?.map?.version,
  store.snapshot?.map?.definitionHash, store.demoMode].join('|'));
const available = computed(() => !props.autoDetourEnabled && props.enabled && props.readCurrent && !props.busy && !pending.value &&
  !store.demoMode && !store.engineeringSimulationOnly && store.fresh);
const canRepreview = computed(() => !!props.current.detour && !props.current.detour.sendAttemptedAt &&
  ['ReviewHold', 'Ready'].includes(props.current.detour.state));
const canPreview = computed(() => available.value && (canRepreview.value || (!props.current.detour &&
  ['Accepted', 'Waiting', 'Running', 'Suspended'].includes(props.current.state))));
const previewCurrent = computed(() => props.readCurrent && store.fresh &&
  detourPreviewCurrent(candidate.value, props.current, store.snapshot?.map, props.now));
const canConfirm = computed(() => available.value && previewCurrent.value && candidate.value?.plan.available && cancelConfirmed.value);
const canContinue = computed(() => available.value && !candidate.value && executeConfirmed.value && detourCanContinue(props.current, store.snapshot?.map));
let epoch = 0;
function invalidate() { ++epoch; candidate.value = null; cancelConfirmed.value = false; executeConfirmed.value = false; }
async function run(action: () => Promise<void>) {
  if (pending.value || props.busy) return;
  pending.value = true; emit('busy', true); error.value = '';
  try { await action(); }
  catch (ex) { error.value = `${String(ex)}；保持原记录，先查询任务状态。`; }
  finally { pending.value = false; emit('busy', false); emit('changed'); }
}
async function preview() {
  if (!canPreview.value) return;
  invalidate(); const mine = epoch, taskId = props.current.taskId;
  await run(async () => {
    const result = await store.api<NavigationDetourPreview>('/api/navigation-test/detour/preview', { taskId });
    if (mine === epoch) candidate.value = result;
  });
}
async function confirm() {
  if (!canConfirm.value || !candidate.value) return;
  const payload = { taskId: props.current.taskId, previewId: candidate.value.previewId, cancelOriginalConfirmed: true };
  invalidate();
  await run(async () => { await store.api('/api/navigation-test/detour/confirm', payload); });
}
async function continueDetour() {
  if (!canContinue.value || !props.current.detour) return;
  const payload = { taskId: props.current.taskId, previewId: props.current.detour.previewId, executeDetourConfirmed: true };
  executeConfirmed.value = false;
  await run(async () => { await store.api('/api/navigation-test/detour/continue', payload); });
}
watch(context, invalidate);
watch(() => props.readCurrent, current => { if (!current) invalidate(); });
watch(() => props.current.detour?.state, () => { cancelConfirmed.value = false; executeConfirmed.value = false; if (props.current.detour) candidate.value = null; });
</script>

<template>
  <section class="navigation-detour" aria-label="单车受控改道">
    <h3>受阻后的受控改道</h3>
    <p v-if="autoDetourEnabled" class="detour-auto"><strong>自动改道已启用</strong> · 连续受阻 {{ autoDetourWaitSeconds }} 秒后自动规划；确认障碍仍在、存在不同路线且所有安全条件通过后，自动取消原导航并发送一次备用站点链。</p>
    <p v-else>自动改道未启用。当前入口只做只读预览，需要逐项确认后才会操作车辆。</p>
    <p>短时障碍先等待车端恢复；持续受阻后，系统会按原任务路线和车辆实际站点自动推断受阻路段。无法唯一判断时，再到总览核对并封锁受影响通道。</p>
    <p class="detour-note">按车辆实际停稳的站点接入候选。车辆停在站点之间时，当前入口保持等待，不能冒用最近站点作为新起点。</p>
    <RouterLink to="/">前往总览核对道路封锁</RouterLink>
    <p v-if="error" role="alert" class="detour-error">{{ industrialText(error) }}</p>
    <p v-if="!readCurrent" role="status">原任务状态尚未取得或已失效，请先查询原任务。</p>
    <div v-if="current.autoDetour" class="detour-candidate detour-auto-record">
      <h4>{{ autoDetourStateLabels[current.autoDetour.state] || '自动改道状态待核对' }}</h4>
      <details><summary>查看原始自动改道状态</summary><code>{{current.autoDetour.state}}</code></details>
      <p>{{ industrialText(current.autoDetour.message) }}</p>
      <p v-if="current.autoDetour.sourceSite">实际接入点：{{ current.autoDetour.sourceSite }} · 推断受阻下一站：{{ current.autoDetour.blockedToSite || '待核对' }}</p>
      <p v-if="current.autoDetour.blockedEdgeIds.length">推断受阻道路：{{ current.autoDetour.blockedEdgeIds.join('、') }}</p>
      <p v-if="current.autoDetour.candidateRoute.length" class="detour-route">{{ current.autoDetour.candidateRoute.join(' → ') }}</p>
      <p v-if="current.autoDetour.planMessage">{{ industrialText(current.autoDetour.planMessage) }}</p>
      <p>最近自动判定：{{ new Date(current.autoDetour.updatedAt).toLocaleString() }}</p>
    </div>
    <button v-if="!autoDetourEnabled && (!current.detour || canRepreview)" :disabled="!canPreview" @click="preview">{{ current.detour ? '重新核验实际接入点与候选' : '核验实际接入点并预览改道' }}</button>
      <div v-if="candidate" class="detour-candidate">
        <h4>{{ !previewCurrent ? '候选已失效，请重新核验' : candidate.plan.available ? current.detour ? '已重新核验候选 · 等待明确确认' : '已找到备选路线 · 尚未结束原导航' : '无可执行的备选路线 · 保持原任务等待' }}</h4>
        <p>{{ industrialText(candidate.plan.message) }}</p>
        <p v-if="candidate.plan.route.length" class="detour-route">{{ candidate.plan.route.join(' → ') }}</p>
        <p v-if="candidate.plan.blockedEdgeIds.length">自动推断受阻道路：{{ candidate.plan.blockedEdgeIds.join('、') }}</p>
        <p>实际接入点：{{ candidate.plan.sourceSite }} · 原终点：{{ candidate.plan.targetSite }}<br/>运行地图：{{ candidate.plan.mapId }} / {{ candidate.plan.versionId }} · 封锁修订：{{ candidate.plan.closureRevision }}<br/>车端地图：{{ candidate.plan.robotMap }} · {{ candidate.plan.robotMapMd5 }}</p>
        <p>候选有效至 {{ new Date(candidate.expiresAt).toLocaleTimeString() }}。路线存在不代表已经改道。</p>
        <label class="detour-check"><input v-model="cancelConfirmed" type="checkbox" :disabled="!previewCurrent || !candidate.plan.available || busy"/>{{ current.detour ? '已核对新候选及现场条件，重新确认本次改道；原导航终态由后台核验' : '已核对候选路线及现场条件，允许结束这次原导航并进入改道核验' }}</label>
        <button :disabled="!canConfirm" @click="confirm">{{ current.detour ? '确认更新候选并核验停稳' : '确认结束原导航，等待停稳核验' }}</button>
      </div>
    <div v-if="current.detour" class="detour-candidate">
      <h4>{{ detourStateLabels[current.detour.state] || '改道状态待核对' }}</h4><details><summary>查看原始改道状态</summary><code>{{current.detour.state}}</code></details><p>{{ industrialText(current.detour.message) }}</p>
      <p>原路线：{{ current.detour.originalRoute.join(' → ') }}<br/>备选路线：{{ current.detour.plan.route.join(' → ') }}</p>
      <p>原车端任务：{{ current.detour.originalVendorTaskIds.join('、') }}<br/>新车端任务：{{ current.detour.newVendorTaskIds.join('、') || '尚未下发' }}</p>
      <template v-if="current.detour.state === 'Ready' && !candidate">
        <p>后台已核验原导航终结、车辆在候选起点连续停稳。发送前会再次核对地图、封锁、站点与原任务。</p>
        <label class="detour-check"><input v-model="executeConfirmed" type="checkbox" :disabled="busy || !readCurrent"/>已确认备选路线通行条件和现场情况，允许执行本次改道</label>
        <button :disabled="!canContinue" @click="continueDetour">确认执行备选路线</button>
      </template>
      <p v-else-if="current.detour.state === 'ReviewHold'" class="detour-note">后台重启后保持复核。原导航终结、实际站点和停稳核验通过后，可重新预览并明确确认候选；不会自动续发。</p>
    </div>
    <p class="detour-note">改道沿用本次测试任务，原导航与新导航编号均保留。取消应答不代表原导航结束；无路、位置不明、急停或严重报警时保持等待，不自动倒车或重发。</p>
  </section>
</template>

<style scoped>
.navigation-detour{background:#18263b;border:1px solid #344965;border-radius:8px;padding:24px;margin:20px 0;font-size:13px;line-height:1.8}.navigation-detour h3{font-size:17px;margin-bottom:12px}.navigation-detour p{margin:12px 0;overflow-wrap:anywhere}.navigation-detour a{color:#79d5eb}.navigation-detour button{padding:11px 18px;background:#254868;color:#e5f7ff;border:1px solid #5c89ab;border-radius:5px;cursor:pointer;margin:8px 10px 8px 0;white-space:normal}.navigation-detour button:disabled{opacity:.45;cursor:not-allowed}.detour-note{color:#abd0df}.detour-auto{color:#b8f0d7;background:#15362f;border-left:3px solid #4fd7a8;padding:12px 14px}.detour-error{color:#ffb2ab;background:#4b252c;padding:14px}.detour-candidate{border-left:3px solid #53c8df;padding:8px 18px;margin-top:18px;background:#102238}.detour-auto-record{border-left-color:#4fd7a8}.detour-candidate h4{font-size:14px;color:#b2e6ef}.detour-route{font-size:19px;color:#d4f5f7}.detour-check{display:flex;align-items:flex-start;gap:9px;margin:14px 0}.detour-check input{margin-top:4px}@media(max-width:600px){.navigation-detour{padding:16px}.detour-candidate{padding:8px 12px}}
</style>
