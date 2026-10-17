<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { useToastStore } from '../stores/toast';
import { isRunningStage, label } from '../state';
import { shortTime } from '../dashboard';
import { newTaskRequestId } from '../task-request';
import type { CreationOptions, RoutePreview, TaskDetail, TaskRow } from '../types';
import { ElCard, ElTable, ElTableColumn, ElTag, ElButton, ElInput, ElSelect, ElOption, ElSpace, ElEmpty, ElIcon, ElDialog, ElForm, ElFormItem, ElInputNumber, ElAlert, ElPagination, ElResult } from 'element-plus';
import { ArrowLeft, Plus, Search, List } from '@element-plus/icons-vue';
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
  (taskFilter.value === 'all' || (taskFilter.value === 'waiting' ? ['WaitingForVehicle', 'Created'].includes(task.stage) : taskFilter.value === 'failed' ? ['Faulted', 'CancellationReview'].includes(task.stage) : taskFilter.value === 'completed' ? task.stage === 'Completed' : taskFilter.value === 'cancelled' ? task.stage === 'Cancelled' : isRunningStage(task.stage))) &&
  `${task.taskId} ${task.stationId} ${task.vehicleId ?? ''}`.toLowerCase().includes(search.value.toLowerCase())
));
const totalCount = computed(() => queriedTasks.value?.totalCount ?? store.snapshot?.tasks?.totalCount ?? 0);
const canSubmit = computed(() => store.fresh && store.submissionEnabled && !actionBusy.value && (requestUncertain.value || (!!options.value?.available && !!station.value && !!vehicle.value)));

watch([search, taskFilter], () => { queriedTasks.value = null; taskPage.value = 1; });
watch(() => route.params.id, id => { if (id && typeof id === 'string') openTask(id); }, { immediate: true });

async function loadDetail(id: string) {
  const selection = ++detailEpoch;
  try { const r = await store.api<TaskDetail | null>(`/api/tasks/${encodeURIComponent(id)}`); if (selection === detailEpoch && selectedTask.value === id) detail.value = r; }
  catch { detail.value = null; }
}
function selectTask(id: string) { selectedTask.value = id; detail.value = null; void loadDetail(id); }
function openTask(id: string) { showTaskDialog.value = true; selectTask(id); }

async function queryTasks(page = 1) {
  queryError.value = ''; taskPage.value = page; queryBusy.value = true;
  try {
    const f = ({ all:'All', waiting:'Waiting', running:'Running', failed:'Failed', completed:'Completed', cancelled:'Cancelled' } as Record<string,string>)[taskFilter.value];
    queriedTasks.value = await store.api<{ items: TaskRow[]; totalCount: number; page: number; pageSize: number }>(`/api/tasks?filter=${f}&search=${encodeURIComponent(search.value)}&page=${page}`);
  } catch (ex) { queryError.value = String(ex); }
  finally { queryBusy.value = false; }
}

async function loadOptions() {
  formOpen.value = true; routePreview.value = null; actionMessage.value = '';
  try { const r = await store.api<CreationOptions>('/api/task-options'); options.value = r; station.value = r.stations.find(s => s.available)?.stationId ?? ''; vehicle.value = r.vehicles.find(v => v.available)?.vehicleId ?? ''; }
  catch (ex) { actionMessage.value = String(ex); }
}

async function previewRoute() {
  if (!options.value || !station.value || !vehicle.value) return;
  actionBusy.value = true; routePreview.value = null;
  try { routePreview.value = await store.api<RoutePreview>('/api/route-preview', { stationId: station.value, vehicleId: vehicle.value, mapId: options.value.mapId, mapVersionId: options.value.mapVersionId, parameterRevision: options.value.parameterRevision }); }
  catch (ex) { actionMessage.value = String(ex); }
  finally { actionBusy.value = false; }
}

async function submitTask() {
  if (!canSubmit.value) return;
  const key = 'agv-web-pending-request';
  let request: Record<string, unknown>;
  try {
    const stored = sessionStorage.getItem(key);
    request = requestUncertain.value && stored ? JSON.parse(stored) : { requestId: newTaskRequestId(), stationId: station.value, requestedVehicleId: vehicle.value, priority: priority.value, mapId: options.value!.mapId, mapVersionId: options.value!.mapVersionId, parameterRevision: options.value!.parameterRevision };
    sessionStorage.setItem(key, JSON.stringify(request));
  } catch { actionMessage.value = '无法保存申请编号'; return; }
  actionBusy.value = true; requestUncertain.value = true;
  try {
    const r = await store.api<{ accepted: boolean; created: boolean; taskId: string | null; message: string }>('/api/tasks', request);
    sessionStorage.removeItem(key); requestUncertain.value = false; actionMessage.value = r.message;
    if (r.accepted && r.taskId) { toast.success('任务已提交'); selectTask(r.taskId); }
  } catch (ex) { actionMessage.value = `结果未确认：${String(ex)}`; }
  finally { actionBusy.value = false; }
}

function stageType(stage: string): 'success' | 'warning' | 'danger' | 'info' | 'primary' {
  if (stage === 'Faulted' || stage === 'CancellationReview') return 'danger';
  if (stage === 'Completed') return 'success';
  if (isRunningStage(stage)) return 'primary';
  if (stage === 'Cancelled') return 'info';
  return 'warning';
}
</script>

<template>
  <div class="page-container">
    <div class="page-header">
      <h2><ElIcon :size="22"><List /></ElIcon> 任务列表</h2>
      <ElTag type="info" effect="plain" size="small">{{ queriedTasks ? '查询' : '快照' }} {{ totalCount }} 条</ElTag>
      <ElButton type="primary" size="small" @click="loadOptions"><ElIcon><Plus /></ElIcon>创建任务</ElButton>
      <ElButton size="small" @click="router.push('/')"><ElIcon><ArrowLeft /></ElIcon>返回大屏</ElButton>
    </div>

    <ElCard shadow="never" class="filter-card">
      <ElSpace wrap>
        <ElInput v-model="search" placeholder="任务 / 工位 / 车辆" clearable style="width:240px" size="small" @keydown.enter="queryTasks(1)" />
        <ElSelect v-model="taskFilter" size="small" style="width:140px">
          <ElOption label="全部" value="all" /><ElOption label="等待" value="waiting" /><ElOption label="执行中" value="running" /><ElOption label="已完成" value="completed" /><ElOption label="异常" value="failed" /><ElOption label="已取消" value="cancelled" />
        </ElSelect>
        <ElButton type="primary" size="small" :loading="queryBusy" :disabled="!store.fresh" @click="queryTasks(1)"><ElIcon><Search /></ElIcon>查询</ElButton>
        <ElButton size="small" @click="search='';taskFilter='all';queriedTasks=null;taskPage=1">重置</ElButton>
        <small style="color:#718aad">{{ queriedTasks ? '后台分页' : '快照筛选' }}</small>
      </ElSpace>
    </ElCard>

    <ElAlert v-if="queryError" :title="queryError" type="warning" show-icon :closable="false" style="margin-bottom:12px" />

    <ElCard shadow="never" class="table-card">
      <ElTable :data="visibleTasks" stripe style="width:100%" :header-cell-style="{ background:'#0a1525', color:'#8ea7cc' }" :cell-style="{ color:'#c0d0e8' }">
        <ElTableColumn label="操作" width="80" fixed><template #default="{ row }"><ElButton type="primary" text size="small" @click="openTask(row.taskId)">详情</ElButton></template></ElTableColumn>
        <ElTableColumn prop="taskId" label="任务编号" width="150"><template #default="{ row }"><span class="mono text-cyan">{{ row.taskId }}</span></template></ElTableColumn>
        <ElTableColumn prop="stationId" label="来源工位" width="130" />
        <ElTableColumn label="车辆" width="110"><template #default="{ row }">{{ row.vehicleId ?? '待分配' }}</template></ElTableColumn>
        <ElTableColumn label="状态" width="120"><template #default="{ row }"><ElTag :type="stageType(row.stage)" size="small" effect="dark">{{ label(row.stage) }}</ElTag></template></ElTableColumn>
        <ElTableColumn prop="priority" label="优先级" width="80" />
        <ElTableColumn label="创建时间" width="170"><template #default="{ row }">{{ shortTime(row.createdAt) }}</template></ElTableColumn>
        <ElTableColumn label="更新时间" width="170"><template #default="{ row }">{{ shortTime(row.updatedAt) }}</template></ElTableColumn>
        <ElTableColumn label="异常说明" min-width="200"><template #default="{ row }"><span style="color:#9daecc">{{ row.errorMessage ?? '—' }}</span></template></ElTableColumn>
      </ElTable>
      <ElEmpty v-if="!visibleTasks.length" description="暂无匹配任务" />
      <div class="table-footer">
        <ElPagination v-model:current-page="taskPage" :page-size="50" :total="totalCount" layout="total, prev, pager, next" small :disabled="queryBusy" @current-change="queryTasks" />
      </div>
    </ElCard>
  </div>

  <!-- Task Detail Dialog -->
  <ElDialog v-model="showTaskDialog" title="任务运行详情 · 九步、条件与执行证据" width="1000px" destroy-on-close>
    <TaskExecutionDetails v-if="detail" :detail="detail" :fresh="store.fresh" :now="store.now" />
    <ElEmpty v-else :description="store.error || '正在读取…'" />
  </ElDialog>

  <!-- Create Task Dialog -->
  <ElDialog v-model="formOpen" title="创建调度任务" width="600px">
    <p style="color:#93accc;margin-bottom:16px">{{ (options?.explanation ?? actionMessage) || '正在查询后台创建条件…' }}</p>
    <ElForm label-position="top" size="default">
      <ElFormItem label="取料工位"><ElSelect v-model="station" :disabled="requestUncertain||actionBusy" style="width:100%"><ElOption v-for="s in options?.stations??[]" :key="s.stationId" :value="s.stationId" :label="`${s.stationId} · ${s.pickupSite}`" :disabled="!s.available" /></ElSelect></ElFormItem>
      <ElFormItem label="车辆"><ElSelect v-model="vehicle" :disabled="requestUncertain||actionBusy" style="width:100%"><ElOption v-for="v in options?.vehicles??[]" :key="v.vehicleId" :value="v.vehicleId" :label="v.label" :disabled="!v.available" /></ElSelect></ElFormItem>
      <ElFormItem label="优先级（0–100）"><ElInputNumber v-model="priority" :min="0" :max="100" :disabled="requestUncertain||actionBusy" /></ElFormItem>
      <ElFormItem label="地图版本"><ElInput :model-value="options?.mapVersionId??'—'" readonly /></ElFormItem>
    </ElForm>
    <ElAlert :title="store.submissionEnabled?'提交后仍须后台核验':'Web提交未启用'" type="warning" :closable="false" show-icon style="margin:12px 0" />
    <div v-if="routePreview" style="padding:14px;background:#122b44;border:1px solid #285172;border-radius:4px;margin-top:12px">
      <b style="color:#5fc9e0">{{ routePreview.message }}</b>
      <p v-for="leg in routePreview.legs" :key="leg.sequence" style="margin-top:6px;color:#8eb5d4;font-size:12px">{{ leg.sequence }}. {{ leg.name }}：{{ leg.vendorPath || leg.logicalPath }}</p>
    </div>
    <ElAlert v-if="actionMessage" :title="actionMessage" type="info" :closable="false" style="margin-top:12px" />
    <template #footer>
      <ElButton @click="formOpen=false">取消</ElButton>
      <ElButton :disabled="!options||!station||!vehicle||actionBusy||!store.fresh" @click="previewRoute">核对路线</ElButton>
      <ElButton type="primary" :disabled="!canSubmit" :loading="actionBusy" @click="submitTask">{{ requestUncertain?'重试原申请':'提交后台' }}</ElButton>
    </template>
  </ElDialog>
</template>

<style scoped>
.page-container{display:flex;flex-direction:column;gap:12px}
.page-header{display:flex;align-items:center;gap:14px;padding:4px 0}
.page-header h2{display:flex;align-items:center;gap:8px;font-size:18px;color:#d9eafb;margin:0}
.page-header .el-tag{margin-right:auto}
.filter-card{background:#0f1b30;border-color:#1e3352}.filter-card :deep(.el-card__body){padding:12px 16px}
.table-card{background:#0f1b30;border-color:#1e3352}.table-card :deep(.el-card__body){padding:0}
.table-card :deep(.el-table){background:#0f1b30;--el-table-border-color:#1e3352;--el-table-row-hover-bg-color:#142035;--el-table-header-bg-color:#0a1525}
.table-card :deep(.el-table tr){background:#0f1b30}
.table-card :deep(.el-table--striped .el-table__body tr.el-table__row--striped td.el-table__cell){background:#0b1628}
.table-footer{padding:12px 16px;display:flex;justify-content:flex-end}
.text-cyan{color:#67e8f9}.mono{font-family:Consolas,monospace}
</style>
