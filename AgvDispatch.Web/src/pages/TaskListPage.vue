<script setup lang="ts">
import ActionIcon from '../components/ActionIcon.vue';
import { computed, onUnmounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { useToastStore } from '../stores/toast';
import { isRunningStage, label } from '../state';
import { shortTime } from '../dashboard';
import { newTaskRequestId } from '../task-request';
import type { CreationOptions, RoutePreview, TaskDetail, TaskRow } from '../types';
import { IconListDetails, IconArrowLeft, IconPlus, IconSearch, IconChevronLeft, IconChevronRight } from '@tabler/icons-vue';
import ConsoleDialog from '../ConsoleDialog.vue';
import TaskExecutionDetails from '../TaskExecutionDetails.vue';

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
const showTaskDialog = ref(false);
const selectedTask = ref('');
const detail = ref<TaskDetail | null>(null);
let detailEpoch = 0;
let detailPending = 0;

const formOpen = ref(false);
const station = ref('');
const vehicle = ref('');
const priority = ref(50);
const options = ref<CreationOptions | null>(null);
const routePreview = ref<RoutePreview | null>(null);
const actionBusy = ref(false);
const actionMessage = ref('');
const requestUncertain = ref(false);

const visibleTasks = computed(() => queriedTasks.value?.items ?? store.allTasks.filter(task =>
  (taskFilter.value === 'all' || (taskFilter.value === 'waiting' ? task.stage === 'WaitingForVehicle' || task.stage === 'Created' : taskFilter.value === 'failed' ? ['Faulted', 'CancellationReview'].includes(task.stage) : taskFilter.value === 'completed' ? task.stage === 'Completed' : taskFilter.value === 'cancelled' ? task.stage === 'Cancelled' : isRunningStage(task.stage))) &&
  `${task.taskId} ${task.stationId} ${task.vehicleId ?? ''}`.toLowerCase().includes(search.value.toLowerCase())
));
const pages = computed(() => Math.max(1, Math.ceil((queriedTasks.value?.totalCount ?? store.snapshot?.tasks?.totalCount ?? 0) / 50)));
const tableCount = computed(() => queriedTasks.value?.totalCount ?? store.snapshot?.tasks?.totalCount ?? 0);
const canSubmit = computed(() => store.fresh && store.submissionEnabled && !actionBusy.value && (requestUncertain.value || (!!options.value?.available && !!station.value && !!vehicle.value)));

watch([search, taskFilter], () => { queriedTasks.value = null; taskPage.value = 1; });
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
onUnmounted(() => { detailEpoch++; });

async function queryTasks(page = 1) {
  queryError.value = ''; taskPage.value = page; queryBusy.value = true;
  try {
    const filter = ({ all: 'All', waiting: 'Waiting', running: 'Running', failed: 'Failed', completed: 'Completed', cancelled: 'Cancelled' } as Record<string, string>)[taskFilter.value];
    const result = await store.api<{ items: TaskRow[]; totalCount: number; page: number; pageSize: number }>(`/api/tasks?filter=${filter}&search=${encodeURIComponent(search.value)}&page=${page}`);
    queriedTasks.value = result;
  } catch (ex) { queryError.value = String(ex); }
  finally { queryBusy.value = false; }
}

function resetTaskQuery() { search.value = ''; taskFilter.value = 'all'; queriedTasks.value = null; taskPage.value = 1; queryError.value = ''; }

async function loadOptions() {
  formOpen.value = true; routePreview.value = null; actionMessage.value = '';
  try {
    const result = await store.api<CreationOptions>('/api/task-options');
    options.value = result;
    station.value = result.stations.find(s => s.available)?.stationId ?? '';
    vehicle.value = result.vehicles.find(v => v.available)?.vehicleId ?? '';
  } catch (ex) { actionMessage.value = String(ex); }
}

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
    request = requestUncertain.value && stored ? JSON.parse(stored) : { requestId: newTaskRequestId(), stationId: station.value, requestedVehicleId: vehicle.value, priority: priority.value, mapId: options.value!.mapId, mapVersionId: options.value!.mapVersionId, parameterRevision: options.value!.parameterRevision };
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
      <h2><IconListDetails :size="23" />任务列表</h2>
      <span>{{ queriedTasks ? '查询总数' : '快照总数' }} {{ tableCount }} 条</span>
      <button class="accent-button" @click="loadOptions"><IconPlus :size="15" />创建任务</button>
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
            <td><span class="state-label" :class="task.stage === 'Faulted' ? 'fault' : task.stage === 'Completed' ? 'idle' : 'running'">{{ label(task.stage) }}</span></td>
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
    <p class="form-explanation">{{ (options?.explanation ?? actionMessage) || '正在查询后台创建条件…' }}</p>
    <div class="creation-grid">
      <label>取料工位<select v-model="station" :disabled="requestUncertain || actionBusy"><option value="">请选择</option><option v-for="s in options?.stations ?? []" :key="s.stationId" :value="s.stationId" :disabled="!s.available">{{ s.stationId }} · {{ s.pickupSite }}</option></select></label>
      <label>车辆<select v-model="vehicle" :disabled="requestUncertain || actionBusy"><option value="">请选择</option><option v-for="v in options?.vehicles ?? []" :key="v.vehicleId" :value="v.vehicleId" :disabled="!v.available">{{ v.label }}</option></select></label>
      <label>优先级（0–100）<input v-model.number="priority" type="number" min="0" max="100" :disabled="requestUncertain || actionBusy" /></label>
      <label>地图版本<input :value="options?.mapVersionId ?? '—'" readonly /></label>
    </div>
    <div class="inline-warning">{{ store.submissionEnabled ? '提交后仍须经过后台核验，成功接收不等于执行完成。' : 'Web提交未启用，可核对条件及路线。' }}</div>
    <div v-if="routePreview" class="route-preview"><b>{{ routePreview.message }}</b><p v-for="leg in routePreview.legs" :key="leg.sequence">{{ leg.sequence }}. {{ leg.name }}：{{ leg.vendorPath || leg.logicalPath }}</p></div>
    <p v-if="actionMessage" class="form-feedback" role="status">{{ actionMessage }}</p>
    <template #footer>
      <button @click="formOpen = false"><ActionIcon name="close"/>取消</button>
      <button :disabled="!options || !station || !vehicle || actionBusy || !store.fresh" @click="previewRoute"><ActionIcon name="route"/>核对四段路线</button>
      <button class="accent-button" :disabled="!canSubmit" @click="submitTask">{{ actionBusy ? '处理中…' : requestUncertain ? '重试原申请' : '提交后台' }}</button>
    </template>
  </ConsoleDialog>
</template>
