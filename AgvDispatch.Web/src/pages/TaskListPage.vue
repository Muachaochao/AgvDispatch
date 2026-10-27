<script setup lang="ts">
import ActionIcon from '../components/ActionIcon.vue';
import { computed, onUnmounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { useToastStore } from '../stores/toast';
import { isRunningStage, label } from '../state';
import {taskWait} from '../traffic-display';
import { shortTime } from '../dashboard';
import { newTaskRequestId } from '../task-request';
import type { CreationOptions, RoutePreview, TaskDetail, TaskRow } from '../types';
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
const taskPage = ref(1);
const queriedTasks = ref<{ items: TaskRow[]; totalCount: number; page: number; pageSize: number } | null>(null);
const queryBusy = ref(false);
const queryError = ref('');
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

const visibleTasks = computed(() => queriedTasks.value?.items ?? store.allTasks.filter(task =>
  (taskFilter.value === 'all' || (taskFilter.value === 'waiting' ? task.stage === 'WaitingForVehicle' || task.stage === 'Created' : taskFilter.value === 'failed' ? ['Faulted', 'CancellationReview'].includes(task.stage) : taskFilter.value === 'completed' ? task.stage === 'Completed' : taskFilter.value === 'cancelled' ? task.stage === 'Cancelled' : isRunningStage(task.stage))) &&
  `${task.taskId} ${task.stationId} ${task.vehicleId ?? ''}`.toLowerCase().includes(search.value.toLowerCase())
));
const pages = computed(() => Math.max(1, Math.ceil((queriedTasks.value?.totalCount ?? store.snapshot?.tasks?.totalCount ?? 0) / 50)));
const tableCount = computed(() => queriedTasks.value?.totalCount ?? store.snapshot?.tasks?.totalCount ?? 0);
const canSubmit = computed(() => store.fresh && store.submissionEnabled && !actionBusy.value && !optionsBusy.value && (requestUncertain.value || (!!options.value?.available && !!selectedStation.value?.available && !!selectedVehicle.value?.available && pickupReachable.value)));

watch([search, taskFilter], () => { queryEpoch++; queryBusy.value = false; queriedTasks.value = null; taskPage.value = 1; });
watch(() => route.params.id, id => { if (id && typeof id === 'string') openTask(id); }, { immediate: true });

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

async function queryTasks(page = 1) {
  const mine = ++queryEpoch;
  queryError.value = ''; taskPage.value = page; queryBusy.value = true;
  try {
    const filter = ({ all: 'All', waiting: 'Waiting', running: 'Running', failed: 'Failed', completed: 'Completed', cancelled: 'Cancelled' } as Record<string, string>)[taskFilter.value];
    const result = await store.api<{ items: TaskRow[]; totalCount: number; page: number; pageSize: number }>(`/api/tasks?filter=${filter}&search=${encodeURIComponent(search.value)}&page=${page}`);
    if (mine === queryEpoch) queriedTasks.value = result;
  } catch (ex) { if (mine === queryEpoch) queryError.value = String(ex); }
  finally { if (mine === queryEpoch) queryBusy.value = false; }
}

function resetTaskQuery() { queryEpoch++; queryBusy.value = false; search.value = ''; taskFilter.value = 'all'; queriedTasks.value = null; taskPage.value = 1; queryError.value = ''; }

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
      <span>{{ queriedTasks ? '查询总数' : '快照总数' }} {{ tableCount }} 条</span>
      <button class="accent-button" @click="loadOptions()"><IconPlus :size="15" />创建任务</button>
      <button @click="router.push('/engineering-tests')">工程测试</button>
      <button @click="router.push('/')"><IconArrowLeft :size="15" />返回大屏</button>
    </header>
    <div class="query-bar">
      <label>任务 / 工位 / 车辆<input v-model="search" placeholder="输入关键词" aria-label="搜索任务" @keydown.enter="queryTasks(1)" /></label>
      <label>任务状态<select v-model="taskFilter" aria-label="筛选任务状态"><option value="all">全部</option><option value="waiting">等待</option><option value="running">执行中</option><option value="completed">已完成</option><option value="failed">异常</option><option value="cancelled">已取消</option></select></label>
      <button class="accent-button" :disabled="queryBusy || !store.fresh" @click="queryTasks(1)"><IconSearch :size="15" />{{ queryBusy ? '查询中' : '查询' }}</button>
      <button @click="resetTaskQuery"><ActionIcon name="refresh"/>重置</button>
      <small>{{ queriedTasks ? '后台分页查询结果' : '当前快照筛选；后台可点击查询读取历史' }}</small>
    </div>
    <p v-if="queryError" class="inline-warning">{{ queryError }}</p>
    <div class="management-table">
      <table>
        <thead><tr><th>操作</th><th>任务编号</th><th>来源工位</th><th>车辆</th><th>状态</th><th>优先级</th><th>创建时间</th><th>更新时间</th><th>异常说明</th></tr></thead>
        <tbody>
          <tr v-for="task in visibleTasks" :key="task.taskId">
            <td><button class="table-button" @click="openTask(task.taskId)"><ActionIcon name="preview"/>详情</button></td>
            <td class="cyan mono">{{ task.taskId }}</td>
            <td>{{ task.stationId }}</td>
            <td>{{ task.vehicleId ?? '待分配' }}</td>
            <td><span class="state-label" :class="task.stage === 'Faulted' ? 'fault' : task.stage === 'Completed' ? 'idle' : taskWait(store.snapshot,task,store.now) ? 'waiting' : 'running'">{{ taskWait(store.snapshot,task,store.now)?.label ?? label(task.stage) }}</span><small v-if="taskWait(store.snapshot,task,store.now)" style="display:block;max-width:300px;white-space:normal">业务阶段：{{label(task.stage)}} · {{taskWait(store.snapshot,task,store.now)?.reason}}</small></td>
            <td>{{ task.priority }}</td>
            <td>{{ shortTime(task.createdAt) }}</td>
            <td>{{ shortTime(task.updatedAt) }}</td>
            <td class="message-cell">{{ task.errorMessage ?? '—' }}</td>
          </tr>
        </tbody>
      </table>
      <p v-if="!visibleTasks.length" class="empty-panel">暂无匹配任务</p>
    </div>
    <footer class="pagination">
      <span>当前页 {{ visibleTasks.length }} 条 · 每页最多50条</span>
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
