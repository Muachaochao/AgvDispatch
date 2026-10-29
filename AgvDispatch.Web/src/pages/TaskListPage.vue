<script setup lang="ts">
import ActionIcon from '../components/ActionIcon.vue';
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { useToastStore } from '../stores/toast';
import { isRunningStage, label } from '../state';
import {taskWait} from '../traffic-display';
import { shortTime } from '../dashboard';
import { newTaskRequestId } from '../task-request';
import type { CreationOptions, RoutePreview, TaskDetail, TaskPage } from '../types';
import { demoTaskPage, emptyTaskHistoryQuery, taskHistoryError, taskHistoryUrl, taskTimeRange, type TaskTimePreset } from '../task-history';
import { IconListDetails, IconArrowLeft, IconPlus, IconSearch, IconChevronLeft, IconChevronRight } from '@tabler/icons-vue';
import ConsoleDialog from '../ConsoleDialog.vue';
import TaskExecutionDetails from '../TaskExecutionDetails.vue';
import BusinessRunPanel from '../components/BusinessRunPanel.vue';

const store = useDispatchStore();
const toast = useToastStore();
const router = useRouter();
const route = useRoute();

const search = ref('');
const taskFilter = ref('all');
const createdFrom = ref('');
const createdTo = ref('');
const pageSize = ref(50);
const appliedQuery = ref(emptyTaskHistoryQuery());
const taskPage = ref(1);
const queriedTasks = ref<TaskPage | null>(null);
const queryBusy = ref(false);
const queryError = ref('');
const queriedAt = ref('');
let queryStarted = false;
let queryEpoch = 0;
const showTaskDialog = ref(false);
const selectedTask = ref('');
const detail = ref<TaskDetail | null>(null);
let detailEpoch = 0;
let detailPending = 0;

const formOpen = ref(false);
const creationMode = ref<'Single'|'Demand'|'Cycle'>('Single');
const station = ref('');
const vehicle = ref('');
const priority = ref(50);
const options = ref<CreationOptions | null>(null);
const routePreview = ref<RoutePreview | null>(null);
const actionBusy = ref(false);
const actionMessage = ref('');
const requestUncertain = ref(false);
try {
  const raw = sessionStorage.getItem('agv-web-pending-request');
  if (raw) {
    const original = JSON.parse(raw);
    if (!original.requestId || !original.stationId || !original.requestedVehicleId) throw new Error('原申请信息不完整');
    requestUncertain.value = true;
    station.value = original.stationId; vehicle.value = original.requestedVehicleId; priority.value = original.priority;
    actionMessage.value = `存在结果未确认的原申请 ${original.requestId}，将沿用原编号核对，不能新建替代任务。`;
  }
} catch { requestUncertain.value = true; actionMessage.value = '原申请存储损坏，禁止新建；请先查询原任务核对。'; }
const optionsBusy = ref(false);
const selectedStation = computed(() => options.value?.stations.find(s => s.stationId === station.value));
const selectedVehicle = computed(() => options.value?.vehicles.find(v => v.vehicleId === vehicle.value));
const pickupReachable = computed(() => !!selectedStation.value?.pickupReachableVehicleIds?.includes(vehicle.value));

const draftQuery = computed(() => ({ search: search.value, filter: taskFilter.value, from: createdFrom.value, to: createdTo.value, pageSize: pageSize.value }));
const queryChanged = computed(() => JSON.stringify(draftQuery.value) !== JSON.stringify(appliedQuery.value));
const visibleTasks = computed(() => queriedTasks.value?.items ?? []);
const pages = computed(() => Math.max(1, Math.ceil((queriedTasks.value?.totalCount ?? 0) / (queriedTasks.value?.pageSize ?? appliedQuery.value.pageSize))));
const tableCount = computed(() => queriedTasks.value?.totalCount ?? 0);
const pageStart = computed(() => tableCount.value ? (taskPage.value - 1) * (queriedTasks.value?.pageSize ?? appliedQuery.value.pageSize) + 1 : 0);
const rangeText = computed(() => appliedQuery.value.from || appliedQuery.value.to ? `${appliedQuery.value.from.replace('T', ' ') || '不限开始'} 至 ${appliedQuery.value.to.replace('T', ' ') || '不限结束'}（不含结束时刻）` : '全部时间');
const canSubmit = computed(() => store.fresh && store.submissionEnabled && !actionBusy.value && !optionsBusy.value && (requestUncertain.value || (!!options.value?.available && !!selectedStation.value?.available && !!selectedVehicle.value?.available && pickupReachable.value)));

watch(() => route.params.id, id => { if (id && typeof id === 'string') openTask(id); }, { immediate: true });
watch(() => store.fresh, value => { if (value && !queryStarted) void queryTasks(1, true); });
onMounted(() => { if (store.fresh) void queryTasks(1, true); });

async function loadDetail(id: string) {
  const selection = ++detailEpoch;
  detailPending++;
  try {
    const result = await store.api<TaskDetail | null>(`/api/tasks/${encodeURIComponent(id)}`);
    if (selection === detailEpoch && selectedTask.value === id) detail.value = result;
  } catch { if (selection === detailEpoch && selectedTask.value === id) detail.value = null; }
  finally { detailPending--; }
}
function selectTask(id: string) { selectedTask.value = id; detail.value = null; void loadDetail(id); }
function openTask(id: string) { showTaskDialog.value = true; selectTask(id); }

watch(() => store.snapshot?.receivedAt, () => {
  if (!showTaskDialog.value) return;
  if (!store.fresh) { detailEpoch++; detail.value = null; return; }
  if (selectedTask.value && detailPending === 0) void loadDetail(selectedTask.value);
});
watch(showTaskDialog, open => { if (!open) { detailEpoch++; detail.value = null; } });
onUnmounted(() => { detailEpoch++; queryEpoch++; });

async function queryTasks(page = 1, apply = false) {
  if (queryBusy.value) return;
  const query = apply ? { ...draftQuery.value } : { ...appliedQuery.value };
  const invalid = taskHistoryError(query);
  if (invalid) { queryError.value = invalid; return; }
  const mine = ++queryEpoch;
  queryStarted = true; queryError.value = ''; queryBusy.value = true;
  try {
    let result = store.demoMode ? demoTaskPage(store.allTasks, query, page) : await store.api<TaskPage>(taskHistoryUrl(query, page));
    const lastPage = Math.max(1, Math.ceil(result.totalCount / result.pageSize));
    if (page > lastPage && mine === queryEpoch) result = store.demoMode ? demoTaskPage(store.allTasks, query, lastPage) : await store.api<TaskPage>(taskHistoryUrl(query, lastPage));
    if (mine === queryEpoch) { queriedTasks.value = result; appliedQuery.value = query; taskPage.value = result.page; queriedAt.value = new Date().toISOString(); }
  } catch (ex) { if (mine === queryEpoch) queryError.value = String(ex); }
  finally { if (mine === queryEpoch) queryBusy.value = false; }
}

function resetTaskQuery() { if (queryBusy.value) return; search.value = ''; taskFilter.value = 'all'; createdFrom.value = ''; createdTo.value = ''; pageSize.value = 50; void queryTasks(1, true); }
function chooseTimeRange(preset: TaskTimePreset) { if (queryBusy.value) return; const range = taskTimeRange(preset); createdFrom.value = range.from; createdTo.value = range.to; void queryTasks(1, true); }

async function loadOptions(preserveSelection = false) {
  if (optionsBusy.value || actionBusy.value) return;
  if (requestUncertain.value) { formOpen.value = true; return; }
  formOpen.value = true; routePreview.value = null; actionMessage.value = '';
  optionsBusy.value = true;
  try {
    const result = await store.api<CreationOptions>('/api/task-options');
    options.value = result;
    if (!preserveSelection || !result.stations.some(s => s.stationId === station.value)) station.value = result.stations.find(s => s.available)?.stationId ?? result.stations[0]?.stationId ?? '';
    if (!preserveSelection || !result.vehicles.some(v => v.vehicleId === vehicle.value)) vehicle.value = result.vehicles.find(v => v.available)?.vehicleId ?? result.vehicles[0]?.vehicleId ?? '';
  } catch (ex) { options.value = null; actionMessage.value = String(ex); }
  finally { optionsBusy.value = false; }
}

watch([station, vehicle], () => { routePreview.value = null; });

async function previewRoute() {
  if (!options.value || !station.value || !vehicle.value) return;
  actionBusy.value = true; routePreview.value = null;
  try {
    routePreview.value = await store.api<RoutePreview>('/api/route-preview', { stationId: station.value, vehicleId: vehicle.value, mapId: options.value.mapId, mapVersionId: options.value.mapVersionId, parameterRevision: options.value.parameterRevision });
  } catch (ex) { actionMessage.value = String(ex); }
  finally { actionBusy.value = false; }
}

async function submitTask() {
  if (!canSubmit.value) return;
  const storageKey = 'agv-web-pending-request';
  let request: Record<string, unknown>;
  try {
    const stored = sessionStorage.getItem(storageKey);
    if (requestUncertain.value && !stored) throw new Error('原申请缺失，禁止换编号');
    request = requestUncertain.value && stored ? JSON.parse(stored) : { requestId: newTaskRequestId(), stationId: station.value, requestedVehicleId: vehicle.value, priority: priority.value, mapId: options.value!.mapId, mapVersionId: options.value!.mapVersionId, parameterRevision: options.value!.parameterRevision };
    if (!request.requestId || !request.stationId || !request.requestedVehicleId) throw new Error('原申请信息不完整');
    sessionStorage.setItem(storageKey, JSON.stringify(request));
  } catch { actionMessage.value = '无法持久保存申请编号，本次未提交。'; return; }
  actionBusy.value = true; actionMessage.value = '正在提交，保持原申请编号。'; requestUncertain.value = true;
  try {
    const result = await store.api<{ accepted: boolean; created: boolean; taskId: string | null; message: string }>('/api/tasks', request);
    sessionStorage.removeItem(storageKey); requestUncertain.value = false;
    actionMessage.value = result.message;
    if (result.accepted && result.taskId) { formOpen.value = false; toast.success('任务已提交'); openTask(result.taskId); }
  } catch (ex) { actionMessage.value = `结果未确认：${String(ex)}。保留原申请，重试使用同一个编号。`; }
  finally { actionBusy.value = false; }
}

watch(() => route.query.create, create => {
  if (create !== '1') return;
  void loadOptions();
  void router.replace({ query: { ...route.query, create: undefined } });
}, { immediate: true });
</script>

<template>
  <section class="management-page">
    <header class="management-title">
      <h2><IconListDetails :size="23" />业务任务</h2>
      <span>{{ queriedTasks ? `查询共 ${tableCount} 条` : '正在读取任务记录' }}</span>
      <button class="accent-button" @click="loadOptions()"><IconPlus :size="15" />创建任务</button>
      <button @click="router.push('/engineering-tests')">工程测试</button>
      <button @click="router.push('/')"><IconArrowLeft :size="15" />返回大屏</button>
    </header>
    <form class="query-bar task-history-query" @submit.prevent="queryTasks(1, true)">
      <div class="history-main-filters">
        <label>任务 / 工位 / 车辆<input v-model="search" maxlength="128" placeholder="输入关键词" aria-label="搜索任务" :disabled="queryBusy" /></label>
        <label>任务状态<select v-model="taskFilter" aria-label="筛选任务状态" :disabled="queryBusy"><option value="all">全部</option><option value="waiting">待派车</option><option value="running">执行中</option><option value="completed">已完成</option><option value="failed">异常</option><option value="cancelled">已取消</option></select></label>
        <button type="submit" class="accent-button" :disabled="queryBusy || !store.fresh"><IconSearch :size="15" />{{ queryBusy ? '查询中' : '查询' }}</button>
        <button type="button" :disabled="queryBusy" @click="resetTaskQuery"><ActionIcon name="refresh"/>重置</button>
      </div>
      <div class="history-time-filters">
        <span class="history-time-title">创建时间</span>
        <label><span class="sr-only">创建时间从</span><input v-model="createdFrom" type="datetime-local" aria-label="创建时间从" :disabled="queryBusy" /></label>
        <span>至</span>
        <label><span class="sr-only">创建时间至</span><input v-model="createdTo" type="datetime-local" aria-label="创建时间至" :disabled="queryBusy" /></label>
        <div class="history-presets" aria-label="快捷时间范围"><button v-for="item in ([['today', '今天'], ['yesterday', '昨天'], ['week', '近7天'], ['all', '全部时间']] as const)" :key="item[0]" type="button" :disabled="queryBusy || !store.fresh" @click="chooseTimeRange(item[0])">{{ item[1] }}</button></div>
      </div>
      <small>按本机时间查询创建记录；结束时刻不计入。可查询任意历史日期。</small>
    </form>
    <div class="history-result-bar" role="status"><span>{{ store.demoMode ? '浏览器示例' : '后台历史记录' }} · {{ rangeText }}<span v-if="queriedAt"> · 查询于 {{ shortTime(queriedAt) }}</span></span><span v-if="queryChanged" class="pending-filter">条件已修改，点击查询应用；翻页沿用当前结果条件。</span></div>
    <p v-if="queryError" class="inline-warning">{{ queryError }}</p>
    <div class="management-table">
      <table>
        <thead><tr><th>操作</th><th>任务编号</th><th>来源工位</th><th>车辆</th><th>状态</th><th>优先级</th><th>创建时间</th><th>更新时间</th><th>异常说明</th></tr></thead>
        <tbody>
          <tr v-for="task in visibleTasks" :key="task.taskId">
            <td><button class="table-button" @click="openTask(task.taskId)"><ActionIcon name="preview"/>详情</button></td>
            <td class="cyan mono" :title="task.taskId">{{ task.taskId }}</td>
            <td>{{ task.stationId }}</td>
            <td>{{ task.vehicleId ?? '待分配' }}</td>
            <td><span class="state-label" :class="['Faulted','CancellationReview'].includes(task.stage) ? 'fault' : task.stage === 'Completed' ? 'completed' : task.stage === 'Cancelled' ? 'cancelled' : taskWait(store.snapshot,task,store.now) ? 'waiting' : 'running'">{{ taskWait(store.snapshot,task,store.now)?.label ?? label(task.stage) }}</span><small v-if="taskWait(store.snapshot,task,store.now)" style="display:block;max-width:300px;white-space:normal">业务阶段：{{label(task.stage)}} · {{taskWait(store.snapshot,task,store.now)?.reason}}</small></td>
            <td>{{ task.priority }}</td>
            <td>{{ shortTime(task.createdAt) }}</td>
            <td>{{ shortTime(task.updatedAt) }}</td>
            <td class="message-cell" :title="task.errorMessage ?? ''">{{ task.errorMessage ?? '—' }}</td>
          </tr>
        </tbody>
      </table>
      <p v-if="!visibleTasks.length" class="empty-panel">{{ queryBusy ? '正在查询任务记录…' : queryError && !queriedTasks ? '读取失败，请重新查询。' : '当前条件下没有任务，可调整创建时间或状态。' }}</p>
    </div>
    <footer class="pagination">
      <span>共 {{ tableCount }} 条 · 当前 {{ pageStart }}–{{ tableCount ? pageStart + visibleTasks.length - 1 : 0 }} 条</span>
      <label>每页<select v-model.number="pageSize" aria-label="每页任务条数" :disabled="queryBusy" @change="queryTasks(1, true)"><option :value="20">20条</option><option :value="50">50条</option><option :value="100">100条</option><option :value="200">200条</option></select></label>
      <button :disabled="taskPage <= 1 || queryBusy" @click="queryTasks(taskPage - 1)"><IconChevronLeft :size="14" />上一页</button>
      <b>{{ taskPage }} / {{ pages }}</b>
      <button :disabled="taskPage >= pages || queryBusy" @click="queryTasks(taskPage + 1)">下一页<IconChevronRight :size="14" /></button>
    </footer>
  </section>

  <ConsoleDialog v-if="showTaskDialog" title="任务运行详情 · 九步、条件与执行证据" wide @close="showTaskDialog = false">
    <TaskExecutionDetails v-if="detail" :detail="detail" :fresh="store.fresh" :now="store.now" />
    <p v-else class="empty-panel">{{ store.error || '正在读取任务详情…' }}</p>
  </ConsoleDialog>

  <ConsoleDialog v-if="formOpen" title="创建调度任务" @close="formOpen = false">
    <nav class="creation-modes" aria-label="任务运行模式">
      <button :class="{'accent-button':creationMode==='Single'}" @click="creationMode='Single'">单次业务任务</button>
      <button :class="{'accent-button':creationMode==='Demand'}" @click="creationMode='Demand'">正常按需运行</button>
      <button :class="{'accent-button':creationMode==='Cycle'}" @click="creationMode='Cycle'">自动循环测试</button>
    </nav>
    <BusinessRunPanel v-if="creationMode!=='Single'" :mode="creationMode" :options="options"/>
    <template v-else>
    <p class="form-explanation">{{ (options?.explanation ?? actionMessage) || '正在查询后台创建条件…' }}</p>
    <p class="form-explanation">本表创建取满箱、倒料、返空箱任务，需要工位满箱呼叫。可选择工位查看条件和核对路线；满足条件后才能提交。</p>
    <div class="creation-grid">
      <label>取料工位<select v-model="station" :disabled="requestUncertain || actionBusy || optionsBusy"><option value="">请选择</option><option v-for="s in options?.stations ?? []" :key="s.stationId" :value="s.stationId">{{ s.stationId }} · {{ s.pickupSite }}{{ s.available ? '' : ' · 条件未满足' }}</option></select></label>
      <label>车辆<select v-model="vehicle" :disabled="requestUncertain || actionBusy || optionsBusy"><option value="">请选择</option><option v-for="v in options?.vehicles ?? []" :key="v.vehicleId" :value="v.vehicleId">{{ v.label }}{{ v.available ? '' : ' · 暂不可用' }}</option></select></label>
      <label>优先级（0–100）<input v-model.number="priority" type="number" min="0" max="100" :disabled="requestUncertain || actionBusy" /></label>
      <label>地图版本<input :value="options?.mapVersionId ?? '—'" readonly /></label>
    </div>
    <details class="form-explanation"><summary>查看工位条件与未满足项</summary><div role="status">
      <p v-for="s in options?.stations ?? []" :key="s.stationId">{{ s.stationId }}：{{ s.explanation }}</p>
      <p v-if="selectedVehicle">车辆：{{ selectedVehicle.explanation }}</p>
      <p v-if="selectedStation && selectedVehicle && !pickupReachable">所选车辆当前站点到取料点没有可执行路径，请检查地图道路和方向。</p>
    </div></details>
    <p class="form-explanation"><RouterLink class="cyan" to="/engineering-settings" @click="formOpen = false">配置工位、等待点、倒料点及车辆动作</RouterLink>；道路与站点位置在地图编辑中定义。当前业务步骤固定为九步。</p>
    <div class="inline-warning">{{ store.submissionEnabled ? '提交后仍须经过后台核验，成功接收不等于执行完成。' : 'Web提交未启用，可核对条件及路线。' }}</div>
    <div v-if="routePreview" class="route-preview"><b>{{ routePreview.message }}</b><p v-for="leg in routePreview.legs" :key="leg.sequence">{{ leg.sequence }}. {{ leg.name }}：{{ leg.vendorPath || leg.logicalPath }}</p></div>
    <p v-if="actionMessage" class="form-feedback" role="status">{{ actionMessage }}</p>
    </template>
    <template #footer>
      <button @click="formOpen = false"><ActionIcon name="close"/>取消</button>
      <button :disabled="optionsBusy || actionBusy || requestUncertain || !store.fresh" @click="loadOptions(true)"><ActionIcon name="refresh"/>{{ optionsBusy ? '刷新中…' : '刷新条件' }}</button>
      <button v-if="creationMode==='Single'" :disabled="!options || !station || !vehicle || optionsBusy || actionBusy || !store.fresh" @click="previewRoute"><ActionIcon name="route"/>核对四段路线</button>
      <button v-if="creationMode==='Single'" class="accent-button" :disabled="!canSubmit" @click="submitTask">{{ actionBusy ? '处理中…' : requestUncertain ? '重试原申请' : '提交后台' }}</button>
    </template>
  </ConsoleDialog>
</template>
<style scoped>
.task-history-query{display:flex;flex-direction:column;align-items:stretch;gap:12px}
.history-main-filters,.history-time-filters{display:flex;flex-wrap:wrap;align-items:center;gap:12px}
.history-main-filters label,.history-time-filters label{display:flex;align-items:center;gap:8px}
.history-main-filters input{width:240px}.history-time-filters input{width:205px;color-scheme:dark}
.history-time-title{min-width:64px;color:var(--text-secondary)}
.history-presets{display:flex;flex-wrap:wrap;gap:6px}.history-presets button{padding:6px 10px;font-size:12px}
.history-result-bar{display:flex;align-items:center;justify-content:space-between;gap:10px;flex-wrap:wrap;padding:0 4px 10px;font-size:12px;color:var(--text-secondary)}
.pending-filter{color:#e9b34f}.pagination{flex-wrap:wrap;gap:12px}.pagination label{display:flex;align-items:center;gap:7px}.pagination select{padding:6px 10px}
.sr-only{position:absolute;width:1px;height:1px;overflow:hidden;clip-path:inset(50%);white-space:nowrap}
@media(max-width:800px){.history-main-filters label{flex:1 1 200px}.history-main-filters input{width:100%;min-width:100px}.history-time-filters{gap:8px}.history-time-filters input{width:180px}.history-presets{width:100%}}
</style>
