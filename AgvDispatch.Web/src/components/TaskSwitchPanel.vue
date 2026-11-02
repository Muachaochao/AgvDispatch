<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue';
import { useDispatchStore } from '../stores/dispatch';
import { newTaskRequestId } from '../task-request';
import { shortTime } from '../dashboard';
import { observeTaskSwitchIntent, parseTaskSwitchIntent, retainTaskSwitchIntent, taskSwitchIntentError, taskSwitchProgress,
  taskSwitchStageName, taskSwitchSteps, taskSwitchStorageKey, taskSwitchTerminal,
  type TaskSwitchIntent, type TaskSwitchOptions, type TaskSwitchRecord, type TaskSwitchResult } from '../task-switch';

const props = defineProps<{ taskId: string; stage: string; live: boolean }>();
const store = useDispatchStore();
const expanded = ref(false), options = ref<TaskSwitchOptions | null>(null), record = ref<TaskSwitchRecord | null>(null);
const reading = ref(false), busy = ref(false), error = ref(''), notice = ref(''), storageError = ref('');
const pending = ref<TaskSwitchIntent | null>(null), target = ref(''), priority = ref(50), reason = ref(''), confirmed = ref(false);
const reviewReason = ref(''), reviewConfirmed = ref(false), abandonConfirmed = ref(false);
const readAt = ref(0), inspectedStage = ref('');
let epoch = 0, disposed = false;
const context = computed(() => [props.taskId, store.demoMode, store.adapterIdentity?.sha256,
  store.snapshot?.map?.id, store.snapshot?.map?.version].join('|'));
const fresh = computed(() => !store.demoMode && props.live && store.fresh && readAt.value > 0 &&
  store.now - readAt.value >= -1000 && store.now - readAt.value < 10000);
const active = computed(() => !!record.value && !taskSwitchTerminal(record.value.stage));
const locked = computed(() => busy.value || reading.value || !!pending.value || !!storageError.value || active.value);
const selectedStation = computed(() => options.value?.stations.find(s => s.stationId === target.value));
const canApply = computed(() => fresh.value && options.value?.enabled && options.value.eligible && !busy.value && !reading.value &&
  !storageError.value && !active.value && inspectedStage.value === props.stage && !!selectedStation.value?.available &&
  confirmed.value && reason.value.trim().length >= 10 && reason.value.trim().length <= 1000 &&
  Number.isInteger(priority.value) && priority.value >= 0 && priority.value <= 100);
const canReview = computed(() => fresh.value && options.value?.enabled && !busy.value && !reading.value && !pending.value &&
  !storageError.value && record.value?.stage === 'ReviewHold' && reviewConfirmed.value && reviewReason.value.trim().length >= 10);
const progress = computed(() => taskSwitchProgress(record.value?.stage ?? ''));
const cleanupMode = computed(() => (record.value?.resolvedMode ?? options.value?.modeHint) === 'CompleteBusinessReturn');
const confirmationText = computed(() => cleanupMode.value
  ? '已核对原车辆箱体责任和现场条件；同意继续完成原业务返箱、倒料及 PLC 收尾后再创建新任务'
  : '已核对现场允许受控收尾；空车时取消导航，载箱或顶升到位时由后台自动改为完成原业务返箱');

function remember(value: TaskSwitchIntent) {
  sessionStorage.setItem(taskSwitchStorageKey(value.taskId), JSON.stringify(value));
  pending.value = value;
}
function reconcile(value: TaskSwitchRecord | null) {
  if (value && value.sourceTaskId !== props.taskId) throw new Error('换单记录与当前原任务归属不符，请核对后台；本页不继续操作。');
  const original = pending.value;
  if (original) {
    const state = observeTaskSwitchIntent(original, value);
    if (state === 'Mismatch') throw new Error('后台原申请与本机保存参数不一致，请核对原记录；本机申请继续保留。');
    if (state === 'Accepted') {
      sessionStorage.removeItem(taskSwitchStorageKey(original.taskId));
      pending.value = null;
    }
  }
  record.value = value;
  if (value && !taskSwitchTerminal(value.stage)) expanded.value = true;
}
function restore() {
  pending.value = null; storageError.value = '';
  try {
    const original = parseTaskSwitchIntent(sessionStorage.getItem(taskSwitchStorageKey(props.taskId)), props.taskId);
    if (original) {
      pending.value = original; target.value = original.targetStationId; priority.value = original.priority; reason.value = original.reason;
      expanded.value = true;
    }
  } catch (e) { storageError.value = String(e); expanded.value = true; }
}
async function inspect() {
  if (store.demoMode || !store.fresh || !props.live || busy.value || reading.value || disposed) return;
  const mine = epoch, source = props.taskId, stage = props.stage;
  reading.value = true;
  try {
    const next = await store.api<TaskSwitchOptions>(`/api/tasks/${encodeURIComponent(source)}/switch-options`);
    if (mine !== epoch || disposed) return;
    options.value = next; inspectedStage.value = stage;
    if (pending.value) {
      const original = await store.api<TaskSwitchRecord | null>(`/api/task-switches/${encodeURIComponent(pending.value.requestId)}`);
      if (mine !== epoch || disposed) return;
      reconcile(original);
      if (!original) notice.value = '尚未查到原申请；原编号与参数继续保留。可再次查询，或在原条件仍满足时显式重试原申请。';
    } else reconcile(next.current);
    readAt.value = Date.now(); error.value = '';
  } catch (e) { if (mine === epoch && !disposed) { error.value = `换单查询失败：${String(e)}`; readAt.value = 0; } }
  finally { if (mine === epoch) reading.value = false; }
}
async function readCurrent() {
  const id = pending.value?.requestId ?? record.value?.switchId;
  if (!id || !props.live || !store.fresh || store.demoMode || busy.value || reading.value || disposed) return;
  const mine = epoch;
  reading.value = true;
  try {
    const current = await store.api<TaskSwitchRecord | null>(`/api/task-switches/${encodeURIComponent(id)}`);
    if (mine !== epoch || disposed) return;
    if (current && current.switchId !== id) throw new Error('后台返回的申请编号与原编号不符，请核对原会话。');
    if (current) reconcile(current);
    else if (pending.value) notice.value = '尚未查到原申请；不能据此认定从未受理，原编号与参数继续保留。';
    else throw new Error('原会话记录未返回，请核对后台；不新建替代申请。');
    readAt.value = Date.now(); error.value = '';
  } catch (e) { if (mine === epoch && !disposed) { error.value = `原换单记录查询失败：${String(e)}`; readAt.value = 0; } }
  finally { if (mine === epoch) reading.value = false; }
}
async function apply() {
  if (!canApply.value) return;
  const mine = epoch, source = props.taskId;
  let intent: TaskSwitchIntent;
  try {
    intent = retainTaskSwitchIntent(pending.value, pending.value ?? {
      requestId: newTaskRequestId(), taskId: source, expectedStage: props.stage, targetStationId: target.value,
      priority: priority.value, reason: reason.value, siteConfirmed: confirmed.value,
      mode: options.value?.modeHint ?? 'Auto',
    });
    const invalid = taskSwitchIntentError(intent);
    if (invalid) throw new Error(invalid);
    // Persist before POST, including on explicit retries of the original intent.
    remember(intent);
  } catch (e) { error.value = `申请未发送：${String(e)}`; return; }
  busy.value = true; error.value = ''; notice.value = '';
  try {
    const result = await store.api<TaskSwitchResult>(`/api/tasks/${encodeURIComponent(source)}/switch`, intent);
    if (mine !== epoch || disposed) return;
    reconcile(result.current); notice.value = result.message;
    if (!result.accepted && !result.current) {
      sessionStorage.removeItem(taskSwitchStorageKey(source)); pending.value = null;
      error.value = result.message;
    }
  } catch (e) {
    if (mine === epoch && !disposed) error.value = `换单结果待核对：${String(e)}。保留原申请，不自动重发，请查询原编号。`;
  } finally { if (mine === epoch) { busy.value = false; confirmed.value = false; } }
}
async function review(action: 'resume' | 'abandon') {
  if (!record.value || !fresh.value || busy.value || reading.value || !options.value?.enabled || pending.value || storageError.value) return;
  if (action === 'resume' ? !canReview.value : !active.value || !abandonConfirmed.value || !reviewConfirmed.value || reviewReason.value.trim().length < 10) return;
  const mine = epoch, original = record.value;
  busy.value = true; error.value = ''; notice.value = '';
  try {
    const result = await store.api<TaskSwitchResult>(`/api/task-switches/${encodeURIComponent(original.switchId)}/${action}`, {
      switchId: original.switchId, expectedRevision: original.revision, reason: reviewReason.value.trim(), siteConfirmed: reviewConfirmed.value,
    });
    if (mine !== epoch || disposed) return;
    reconcile(result.current); notice.value = result.message;
    if (!result.accepted) error.value = result.message;
  } catch (e) { if (mine === epoch && !disposed) error.value = `操作结果待核对：${String(e)}。请查询原会话，不重复提交。`; }
  finally { if (mine === epoch) { busy.value = false; reviewConfirmed.value = false; abandonConfirmed.value = false; } }
}

watch(context, () => {
  ++epoch; reading.value = false; busy.value = false; options.value = null; record.value = null;
  readAt.value = 0; inspectedStage.value = ''; error.value = ''; notice.value = '';
  target.value = ''; priority.value = 50; reason.value = ''; confirmed.value = false;
  reviewReason.value = ''; reviewConfirmed.value = false; abandonConfirmed.value = false;
  restore(); void inspect();
}, { immediate: true });
watch(() => props.stage, () => { confirmed.value = false; });
watch(() => record.value?.revision, () => { reviewConfirmed.value = false; abandonConfirmed.value = false; });
watch(() => store.fresh && props.live, value => { if (value && !options.value) void inspect(); });
const timer = setInterval(() => {
  if (!expanded.value && !active.value && !pending.value) return;
  if (pending.value || active.value) void readCurrent(); else void inspect();
}, 3000);
onUnmounted(() => { disposed = true; ++epoch; clearInterval(timer); });
</script>

<template>
  <section class="task-switch-panel" aria-label="原任务受控换单">
    <header>
      <div><h3>受控换单</h3><p>空车导航或载箱返箱 · 先闭合原业务责任，再使用原调度执行器创建新任务</p></div>
      <button type="button" :aria-expanded="expanded" @click="expanded = !expanded">{{ expanded ? '收起' : record ? '查看换单进度' : '查看换单条件' }}</button>
    </header>
    <div v-if="expanded" class="switch-content">
      <p v-if="store.demoMode" class="switch-message">浏览器演示模式不访问现场换单接口；此处不提交申请或续接动作。</p>
      <template v-else>
        <div class="switch-toolbar"><button type="button" :disabled="busy || reading || !live || !store.fresh" @click="inspect">{{ reading ? '查询中…' : '刷新换单检查' }}</button><span v-if="options">{{ options.vehicleId || record?.vehicleId || '车辆待核对' }} · {{ fresh ? '当前查询有效' : '当前查询失效，刷新后操作' }}</span></div>
        <p v-if="!live || !store.fresh" class="switch-warning">诊断已过期或连接中断，写操作已禁用；以下记录仅供回看。</p>
        <p v-if="error" class="switch-warning" role="alert">{{ error }}</p>
        <p v-if="storageError" class="switch-warning" role="alert">{{ storageError }} 本页已锁定新申请，请先在原任务恢复入口核对。</p>
        <p v-if="notice" class="switch-message" role="status">{{ notice }}</p>
        <template v-if="record">
          <div class="switch-status" :class="{ hold: record.stage === 'ReviewHold', complete: record.stage === 'Completed' }" role="status">
            <strong>{{ taskSwitchStageName(record.stage) }}</strong><p>{{ record.message }}</p>
            <small>申请 {{ record.switchId }} · 修订 {{ record.revision }} · {{ shortTime(record.updatedAt) }}</small>
          </div>
          <p v-if="active" class="switch-warning"><b>{{ cleanupMode ? '原业务继续收尾，不中途改派：' : '停止不等于已闭合：' }}</b>{{ cleanupMode ? '车辆会继续原九步返箱、倒料和 PLC 收尾；任一步未完成都会保持车辆锁，旧任务 Completed 前不会下发新单。' : '到站取箱可能包含顶升动作；后台会在停止后重新核对空载、顶升、箱体、PLC 周期和路权，旧任务结束前不会下发新单。' }}</p>
          <ol class="switch-progress" aria-label="换单执行阶段"><li v-for="(item, index) in taskSwitchSteps" :key="item.key" :class="{ done: progress > index, current: progress === index }" :aria-current="progress === index ? 'step' : undefined"><span>{{ index + 1 }}</span>{{ item.name }}</li></ol>
          <dl class="switch-info"><dt>原任务</dt><dd><RouterLink :to="{path:'/tasks/'+record.sourceTaskId}">{{ record.sourceTaskId }}</RouterLink></dd><dt>目标工位 / 车辆</dt><dd>{{ record.targetStationId }} / {{ record.vehicleId }}</dd><dt>执行方式</dt><dd>{{ cleanupMode ? '完成原业务返箱后切换' : '空车导航取消后切换' }}</dd><dt>新任务</dt><dd><RouterLink v-if="record.targetTaskId" :to="{path:'/tasks/'+record.targetTaskId}">{{ record.targetTaskId }}</RouterLink><span v-else>尚未确认创建</span></dd><dt>申请依据</dt><dd>{{ record.request?.reason ?? record.reason }}</dd><dt>配置版本</dt><dd>{{ record.mapId }} / {{ record.mapVersionId }} · {{ record.parameterRevision }}</dd><template v-if="record.stage === 'ReviewHold' && record.resumeStage"><dt>保留执行阶段</dt><dd>{{ taskSwitchStageName(record.resumeStage) }}</dd></template></dl>
          <div v-if="record.blockers.length" class="switch-blockers"><h4>当前阻塞与待处理项</h4><ul><li v-for="(blocker, index) in record.blockers" :key="index">{{ blocker }}</li></ul></div>
          <div class="switch-links"><RouterLink :to="{path:'/recovery',query:{taskId:record.sourceTaskId}}">核对原任务、载荷与 PLC 周期</RouterLink><RouterLink to="/resource-review">查看交通占用</RouterLink><button type="button" :disabled="busy || reading || !live || !store.fresh" @click="readCurrent">查询原换单记录</button></div>
          <form v-if="active" class="switch-review" @submit.prevent="review('resume')">
            <p>原单闭合前不下发新单。停止后仍有载荷、顶升、PLC 或路权缺项时，先完成对应核对，再显式续接。</p>
            <label>本次核对依据<textarea v-model="reviewReason" rows="2" minlength="10" maxlength="1000" :disabled="busy" placeholder="说明原运输终态、车辆与箱体、PLC 周期及原占用核对结果" /></label>
            <label class="switch-check"><input v-model="reviewConfirmed" type="checkbox" :disabled="busy || !fresh" />已核对本次现场状态，原任务责任与车辆状态对应正确</label>
            <div class="switch-review-actions"><button v-if="record.stage === 'ReviewHold'" type="submit" class="accent-button" :disabled="!canReview">核对后续接原换单</button></div>
            <details class="switch-abandon"><summary>停止本次换单</summary><p>停止后不再创建替代任务；原任务取消、载荷、PLC 与路权仍需单独闭合。已创建的新任务需在其详情中处理。</p><label class="switch-check"><input v-model="abandonConfirmed" type="checkbox" :disabled="busy || !fresh" />确认只停止换单流程，继续保留原任务处置责任</label><button type="button" :disabled="!fresh || !options?.enabled || busy || reading || !!pending || !!storageError || !reviewConfirmed || !abandonConfirmed || reviewReason.trim().length < 10" @click="review('abandon')">停止换单并保留原任务处置</button></details>
          </form>
        </template>
        <template v-if="!active">
          <p v-if="options" class="switch-message">{{ options.message }}</p>
          <p v-if="options?.modeDescription" class="switch-message">{{ options.modeDescription }}</p>
          <div v-if="options?.blockers.length" class="switch-blockers"><h4>申请前需处理</h4><ul><li v-for="(blocker, index) in options.blockers" :key="index">{{ blocker }}</li></ul></div>
          <p v-if="pending" class="switch-warning">原申请 {{ pending.requestId }} 尚未确认；目标、优先级和原因已锁定。查询未返回记录也不能更换编号重发。</p>
          <form v-if="options?.enabled || pending" class="switch-form" @submit.prevent="apply">
            <div class="switch-form-row"><label>目标工位<select v-model="target" aria-label="换单目标工位" :disabled="locked"><option value="">请选择配置中的工位</option><option v-if="pending && !options?.stations.some(s => s.stationId === pending?.targetStationId)" :value="pending.targetStationId">{{ pending.targetStationId }}（原申请）</option><option v-for="station in options?.stations ?? []" :key="station.stationId" :value="station.stationId" :disabled="!station.available">{{ station.stationId }} · {{ station.pickupSite }}{{ station.available ? '' : '（不可用）' }}</option></select></label><label>新任务优先级<input v-model.number="priority" type="number" min="0" max="100" step="1" :disabled="locked" /></label></div>
            <p v-if="selectedStation" class="station-explanation">{{ selectedStation.explanation }}</p>
            <label>换单依据<textarea v-model="reason" rows="2" minlength="10" maxlength="1000" :disabled="locked" placeholder="说明本次中断原因与目标任务需求，至少10字" /></label>
            <label class="switch-check"><input v-model="confirmed" type="checkbox" :disabled="busy || reading || !fresh" />{{ confirmationText }}</label>
            <p class="switch-boundary">前往取箱时会根据实时载荷自动选择取消或返箱收尾；已经取箱时继续原九步业务。申请受理不等于原任务已结束，不会通过清零 PLC 或删除路权跳过原业务责任。</p>
            <p v-if="options && inspectedStage !== stage" class="switch-warning">原任务阶段已变化，请刷新换单检查。</p>
            <div class="switch-form-actions"><button type="submit" class="accent-button" :disabled="!canApply">{{ busy ? '提交中…' : pending ? '使用原编号重试申请' : '申请受控停止并换单' }}</button><button v-if="pending" type="button" :disabled="busy || reading || !live || !store.fresh" @click="readCurrent">按原编号查询</button></div>
          </form>
        </template>
      </template>
    </div>
  </section>
</template>

<style scoped>
.task-switch-panel{margin:20px 0;border:1px solid #34516e;border-radius:5px;background:#101e31;color:#c2d5e6;font-size:12px;line-height:1.8;overflow-wrap:anywhere}.task-switch-panel header{display:flex;align-items:center;justify-content:space-between;gap:18px;padding:16px}.task-switch-panel h3{margin:0;color:#d7edf7;font-size:15px}.task-switch-panel header p{margin:3px 0 0;color:#90abc5}.switch-content{border-top:1px solid #2b425c;padding:16px}.switch-toolbar,.switch-links,.switch-form-actions,.switch-review-actions{display:flex;align-items:center;gap:12px;flex-wrap:wrap}.switch-toolbar>span,.station-explanation{color:#94b2cc}.switch-status{border-left:3px solid #47b8d3;padding:12px 14px;background:#142d40;margin-top:16px}.switch-status strong{font-size:14px;color:#84dbec}.switch-status p{margin:6px 0}.switch-status small{color:#93adc6}.switch-status.hold{border-color:#d1a051;background:#302b25}.switch-status.hold strong{color:#ecc27e}.switch-status.complete{border-color:#4cbd9c}.switch-status.complete strong{color:#80dcbc}.switch-info{display:grid;grid-template-columns:120px minmax(0,1fr);gap:8px 16px;margin:18px 0}.switch-info dt{color:#8daac5}.switch-info dd{margin:0}.task-switch-panel a{color:#77d2e5}.switch-message,.switch-warning{padding:10px 12px;background:#14283b;margin:12px 0}.switch-warning{background:#332b23;color:#e3c28d;border-left:3px solid #c19754}.switch-blockers{background:#2b2630;border:1px solid #584154;padding:12px;margin:16px 0}.switch-blockers h4{color:#e4bf9a;margin:0 0 6px}.switch-blockers ul{padding-left:20px;margin:0;color:#dfc2af}.switch-progress{list-style:none;padding:0;display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:8px;margin:18px 0;color:#8ca5c0}.switch-progress li{padding:8px;border-bottom:2px solid #34485d}.switch-progress span{display:block;font-family:monospace;color:#7189a0}.switch-progress .done{color:#8ecdb9;border-color:#4b917e}.switch-progress .current{color:#80d9ee;border-color:#47bedb}.switch-form,.switch-review{display:grid;gap:14px;margin:18px 0 0;padding-top:16px;border-top:1px solid #2b425c}.switch-form-row{display:grid;grid-template-columns:minmax(0,1fr) 150px;gap:16px}.switch-form label:not(.switch-check),.switch-review label:not(.switch-check){display:grid;gap:6px;color:#a9c2d8}.switch-form select,.switch-form input[type=number],.switch-form textarea,.switch-review textarea{width:100%;min-width:0;background:#0a1727;border:1px solid #3b5571;border-radius:3px;color:#d0e3f1;padding:9px;font:inherit}.switch-form textarea,.switch-review textarea{resize:vertical}.switch-check{display:flex;align-items:flex-start;gap:8px;color:#c2d5e6}.switch-check input{margin-top:5px;flex:none}.switch-boundary,.switch-review>p,.switch-abandon p{margin:0;color:#a5bdd3}.switch-abandon{border-top:1px solid #30465d;padding-top:10px}.switch-abandon summary{cursor:pointer;color:#c6b194}.switch-abandon p,.switch-abandon label,.switch-abandon button{margin-top:10px}.task-switch-panel button{white-space:normal}.task-switch-panel button:disabled{cursor:not-allowed}@media(max-width:850px){.switch-progress{grid-template-columns:repeat(3,minmax(0,1fr))}}@media(max-width:550px){.task-switch-panel header{align-items:flex-start;flex-direction:column}.switch-content{padding:12px}.switch-form-row{grid-template-columns:1fr}.switch-info{grid-template-columns:1fr;gap:2px}.switch-info dd{margin-bottom:9px}.switch-progress{gap:4px}.switch-progress li{padding:6px 2px}}
</style>
