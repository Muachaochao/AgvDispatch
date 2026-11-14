<script setup lang="ts">
import ActionIcon from '../components/ActionIcon.vue';
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { useToastStore } from '../stores/toast';
import { isWaitingStage, label } from '../state';
import {taskWait} from '../traffic-display';
import { shortTime } from '../dashboard';
import { newTaskRequestId } from '../task-request';
import type { CreationOptions, RoutePreview, TaskDetail, TaskPage } from '../types';
import { demoTaskPage, emptyTaskHistoryQuery, taskHistoryError, taskHistoryUrl, taskTimeRange, type TaskTimePreset } from '../task-history';
import { IconListDetails, IconArrowLeft, IconPlus, IconSearch } from '@tabler/icons-vue';
import ConsoleDialog from '../ConsoleDialog.vue';
import TaskExecutionDetails from '../TaskExecutionDetails.vue';
import BusinessRunPanel from '../components/BusinessRunPanel.vue';
import { industrialText } from '../operator-labels';

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
let queryStarted = false;
let queryEpoch = 0;
const showTaskDialog = ref(false);
const selectedTask = ref('');
const detail = ref<TaskDetail | null>(null);
let detailEpoch = 0;
let detailPending = 0;

const formOpen = ref(false);
const createStep = ref(1);
const creationMode = ref<'Single'|'Demand'|'Cycle'>('Single');
const station = ref('');
const vehicle = ref('');
const priority = ref(50);
const dependencyTaskIdsInput = ref('');
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
    dependencyTaskIdsInput.value = (original.dependsOnTaskIds ?? []).join(', ');
    actionMessage.value = `存在结果未确认的原申请 ${original.requestId}，将沿用原编号核对，不能新建替代任务。`;
  }
} catch { requestUncertain.value = true; actionMessage.value = '原申请存储损坏，禁止新建；请先查询原任务核对。'; }
const optionsBusy = ref(false);
const selectedStation = computed(() => options.value?.stations.find(s => s.stationId === station.value));
const selectedVehicle = computed(() => options.value?.vehicles.find(v => v.vehicleId === vehicle.value));
const pickupReachable = computed(() => !!selectedStation.value?.pickupReachableVehicleIds?.includes(vehicle.value));
const templateInputs = computed(() => {
  const template = options.value?.template;
  if (!template) return [];
  const waiting = options.value?.waitingSites?.length ? options.value.waitingSites : [options.value?.waitingSite ?? ''];
  const values: Record<string, string> = {
    fromStation: selectedStation.value?.pickupSite ?? '',
    waitingSite: waiting.filter(Boolean).join('、'),
    toStation: options.value?.dumpingSite ?? '',
    vehiclePool: selectedStation.value?.vehiclePoolId ?? '未绑定',
    vehicleIds: selectedStation.value?.allowedVehicleIds?.join('、') ?? '未绑定',
    dependsOnTaskIds: dependencyTaskIdsInput.value || '无前置任务'
  };
  return template.inputs.map(input => ({...input,value:values[input.key]??'由后台确定'}));
});
const templateOutputs = computed(() => options.value?.template?.outputs ?? []);

const draftQuery = computed(() => ({ search: search.value, filter: taskFilter.value, from: createdFrom.value, to: createdTo.value, pageSize: pageSize.value }));
const queryChanged = computed(() => JSON.stringify(draftQuery.value) !== JSON.stringify(appliedQuery.value));
const visibleTasks = computed(() => queriedTasks.value?.items ?? []);
const tableCount = computed(() => queriedTasks.value?.totalCount ?? 0);
const canSubmit = computed(() => store.fresh && store.submissionEnabled && !actionBusy.value && !optionsBusy.value && (requestUncertain.value || (!!options.value?.available && !!selectedStation.value?.available && !!selectedVehicle.value?.available && pickupReachable.value)));
const creationSteps = computed(() => creationMode.value === 'Single'
  ? ['运行方式', '条件与路线', '确认提交']
  : ['运行方式', creationMode.value === 'Demand' ? '按需运行配置' : '循环测试配置']);
const singleSelectionReady = computed(() => !!options.value && !!station.value && !!vehicle.value && store.fresh);
const taskStatusTone=(stage:string,waiting=false)=>stage==='Faulted'?'fault':['Cancelled','CancellationPending','CancellationReview'].includes(stage)?'cancelled':stage==='Completed'?'completed':waiting||isWaitingStage(stage)?'waiting':'running';

watch(() => route.params.id, id => { if (id && typeof id === 'string') openTask(id); }, { immediate: true });
watch(() => store.fresh, value => { if (value && !queryStarted) void queryTasks(1, true); });
watch(formOpen, open => { if (open) { createStep.value = 1; routePreview.value = null; } });
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
    if (mine === queryEpoch) { queriedTasks.value = result; appliedQuery.value = query; taskPage.value = result.page; }
  } catch (ex) { if (mine === queryEpoch) queryError.value = String(ex); }
  finally { if (mine === queryEpoch) queryBusy.value = false; }
}

function resetTaskQuery() { if (queryBusy.value) return; search.value = ''; taskFilter.value = 'all'; createdFrom.value = ''; createdTo.value = ''; pageSize.value = 50; void queryTasks(1, true); }
function chooseTimeRange(preset: TaskTimePreset) { if (queryBusy.value) return; const range = taskTimeRange(preset); createdFrom.value = range.from; createdTo.value = range.to; void queryTasks(1, true); }
function changeTaskPageSize(size: number) { pageSize.value = size; void queryTasks(1, true); }
function selectCreationMode(mode: 'Single'|'Demand'|'Cycle') { creationMode.value = mode; createStep.value = 1; routePreview.value = null; actionMessage.value = ''; }
function nextCreationStep() { createStep.value = Math.min(creationSteps.value.length, createStep.value + 1); }
function previousCreationStep() { createStep.value = Math.max(1, createStep.value - 1); }

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
    const dependsOnTaskIds = [...new Set(dependencyTaskIdsInput.value.split(/[\s,，;；]+/).map(id => id.trim()).filter(Boolean))];
    if (dependsOnTaskIds.length > 8 || dependsOnTaskIds.some(id => id.length > 64)) throw new Error('前置任务最多8个，每个编号不超过64字符');
    request = requestUncertain.value && stored ? JSON.parse(stored) : { requestId: newTaskRequestId(), stationId: station.value, requestedVehicleId: vehicle.value, priority: priority.value, mapId: options.value!.mapId, mapVersionId: options.value!.mapVersionId, parameterRevision: options.value!.parameterRevision, dependsOnTaskIds };
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
  <section class="management-page task-history-page">
    <header class="task-page-header">
      <div class="task-page-title"><IconListDetails :size="22" /><div><h2>业务任务</h2><small>查询、核对并查看九步执行证据</small></div></div>
      <div class="task-page-actions"><button class="accent-button" @click="loadOptions()"><IconPlus :size="15" />创建任务</button><button @click="router.push('/engineering-tests')">工程测试</button><button class="quiet-button" @click="router.push('/')"><IconArrowLeft :size="15" />大屏</button></div>
    </header>
    <form class="task-query-toolbar" @submit.prevent="queryTasks(1, true)">
      <el-input v-model="search" class="query-search" maxlength="128" clearable placeholder="任务、工位或车辆" aria-label="搜索任务" :disabled="queryBusy" :prefix-icon="IconSearch" />
      <el-select v-model="taskFilter" aria-label="筛选任务状态" :disabled="queryBusy"><el-option label="全部状态" value="all" /><el-option label="待派车" value="waiting" /><el-option label="执行中" value="running" /><el-option label="已完成" value="completed" /><el-option label="异常" value="failed" /><el-option label="已取消" value="cancelled" /></el-select>
      <div class="query-date-range"><el-date-picker v-model="createdFrom" type="datetime" value-format="YYYY-MM-DDTHH:mm" format="YYYY-MM-DD HH:mm" placeholder="创建时间从" aria-label="创建时间从" :disabled="queryBusy" /><span>至</span><el-date-picker v-model="createdTo" type="datetime" value-format="YYYY-MM-DDTHH:mm" format="YYYY-MM-DD HH:mm" placeholder="创建时间至" aria-label="创建时间至" :disabled="queryBusy" /></div>
      <div class="history-presets" aria-label="快捷时间范围"><button v-for="item in ([['today', '今天'], ['yesterday', '昨天'], ['week', '近7天'], ['all', '全部时间']] as const)" :key="item[0]" type="button" :disabled="queryBusy || !store.fresh" @click="chooseTimeRange(item[0])">{{ item[1] }}</button></div>
      <el-button native-type="submit" type="primary" class="query-submit" :class="{changed:queryChanged}" :disabled="queryBusy || !store.fresh">{{ queryBusy ? '查询中' : queryChanged ? '应用筛选' : '查询' }}</el-button>
      <el-button class="reset-submit" :disabled="queryBusy" @click="resetTaskQuery">重置</el-button>
    </form>
    <p v-if="queryError" class="inline-warning">{{ queryError }}</p>
    <div class="management-table">
      <el-table :data="visibleTasks" row-key="taskId" stripe height="100%" table-layout="fixed" class="business-table task-table">
        <el-table-column label="操作" width="82" fixed="left"><template #default="{ row }"><el-button link type="primary" @click="openTask(row.taskId)"><ActionIcon name="preview"/>详情</el-button></template></el-table-column>
        <el-table-column label="任务编号" prop="taskId" min-width="360" show-overflow-tooltip><template #default="{ row }"><span class="table-mono">{{ row.taskId }}</span></template></el-table-column>
        <el-table-column label="来源工位" prop="stationId" width="120" />
        <el-table-column label="车辆" width="105"><template #default="{ row }">{{ row.vehicleId ?? '待分配' }}</template></el-table-column>
        <el-table-column label="状态" min-width="180"><template #default="{ row }"><span class="state-label" :class="taskStatusTone(row.stage,!!taskWait(store.snapshot,row,store.now))">{{ taskWait(store.snapshot,row,store.now)?.label ?? label(row.stage) }}</span><small v-if="taskWait(store.snapshot,row,store.now)" class="table-subline">业务阶段：{{label(row.stage)}} · {{taskWait(store.snapshot,row,store.now)?.reason}}</small></template></el-table-column>
        <el-table-column label="优先级" prop="priority" width="86" align="center" />
        <el-table-column label="创建时间" width="155"><template #default="{ row }">{{ shortTime(row.createdAt) }}</template></el-table-column>
        <el-table-column label="更新时间" width="155"><template #default="{ row }">{{ shortTime(row.updatedAt) }}</template></el-table-column>
        <el-table-column label="异常说明" min-width="260" show-overflow-tooltip><template #default="{ row }"><span class="table-secondary">{{ row.errorMessage ? industrialText(row.errorMessage) : '—' }}</span></template></el-table-column>
        <template #empty><div class="table-empty">{{ queryBusy ? '正在查询任务记录…' : queryError && !queriedTasks ? '读取失败，请重新查询。' : '当前条件下没有任务，可调整创建时间或状态。' }}</div></template>
      </el-table>
    </div>
    <footer class="task-result-footer">
      <div class="result-total"><span>任务总数</span><b>{{ tableCount }}</b></div>
      <div class="result-controls">
        <el-pagination background :current-page="taskPage" :page-size="appliedQuery.pageSize" :page-sizes="[20, 50, 100, 200]" :total="tableCount" layout="sizes, prev, pager, next" @current-change="queryTasks" @size-change="changeTaskPageSize" />
      </div>
    </footer>
  </section>

  <ConsoleDialog v-if="showTaskDialog" title="任务运行详情 · 九步、条件与执行证据" wide @close="showTaskDialog = false">
    <TaskExecutionDetails v-if="detail" :detail="detail" :fresh="store.fresh" :now="store.now" />
    <p v-else class="empty-panel">{{ store.error || '正在读取任务详情…' }}</p>
  </ConsoleDialog>

  <el-drawer v-model="formOpen" class="task-create-drawer" title="创建调度任务" size="min(720px, 96vw)" :close-on-click-modal="false" destroy-on-close>
    <div class="creation-drawer-body">
      <el-steps class="creation-el-steps" :active="createStep - 1" finish-status="success" process-status="process" align-center>
        <el-step v-for="(step, index) in creationSteps" :key="step" :title="step" class="creation-el-step" role="button" tabindex="0" :aria-current="createStep === index + 1 ? 'step' : undefined" @click="createStep = index + 1" @keydown.enter.prevent="createStep = index + 1" @keydown.space.prevent="createStep = index + 1" />
      </el-steps>

      <section v-if="createStep === 1" class="creation-step-content">
        <div class="creation-step-heading"><el-tag effect="plain" round>第 1 步</el-tag><h3>选择任务运行方式</h3><p>单次任务适合日常出车；按需运行和循环测试用于持续联调，提交边界更严格。</p></div>
        <nav class="creation-modes" aria-label="任务运行模式">
          <button class="creation-mode-card" :class="{active:creationMode==='Single'}" @click="selectCreationMode('Single')"><b>单次业务任务</b><small>取满箱、倒料、返空箱一次完成</small></button>
          <button class="creation-mode-card" :class="{active:creationMode==='Demand'}" @click="selectCreationMode('Demand')"><b>正常按需运行</b><small>按PLC呼叫接续所选工位</small></button>
          <button class="creation-mode-card" :class="{active:creationMode==='Cycle'}" @click="selectCreationMode('Cycle')"><b>自动循环测试</b><small>按工位顺序执行指定轮数</small></button>
        </nav>
        <el-alert class="creation-el-alert" type="info" :closable="false" show-icon>
          <template #title>执行边界</template>
          {{ store.submissionEnabled ? '提交成功只代表后台受理，不等于车辆动作已完成。' : '当前未开启Web提交，可继续核对条件与路线。' }}
        </el-alert>
      </section>

      <section v-else-if="createStep === 2" class="creation-step-content">
        <div class="creation-step-heading"><el-tag effect="plain" round>第 2 步</el-tag><h3>{{ creationMode === 'Single' ? '核对工位、车辆与路线' : creationMode === 'Demand' ? '配置按需运行' : '配置循环测试' }}</h3><p>{{ creationMode === 'Single' ? '系统只展示后台当前允许的配置，条件未满足时不能提交。' : '运行条件仍由后台核验，网页不会绕过设备与恢复门禁。' }}</p></div>
        <BusinessRunPanel v-if="creationMode!=='Single'" :mode="creationMode" :options="options"/>
        <template v-else>
          <p class="form-explanation">{{ (options?.explanation ?? actionMessage) || '正在查询后台创建条件…' }}</p>
          <el-form class="creation-el-form" label-position="top">
            <div class="creation-grid creation-grid-element">
              <el-form-item label="取料工位"><el-select v-model="station" :disabled="requestUncertain || actionBusy || optionsBusy" placeholder="请选择取料工位"><el-option v-for="s in options?.stations ?? []" :key="s.stationId" :label="`${s.stationId} · ${s.pickupSite}${s.available ? '' : ' · 条件未满足'}`" :value="s.stationId" /></el-select></el-form-item>
              <el-form-item label="车辆"><el-select v-model="vehicle" :disabled="requestUncertain || actionBusy || optionsBusy" placeholder="请选择车辆"><el-option v-for="v in options?.vehicles ?? []" :key="v.vehicleId" :label="`${v.label}${v.available ? '' : ' · 暂不可用'}`" :value="v.vehicleId" /></el-select></el-form-item>
              <el-form-item label="优先级（0–100）"><el-input-number v-model="priority" :min="0" :max="100" :disabled="requestUncertain || actionBusy" /></el-form-item>
              <el-form-item label="前置任务（可选）"><el-input v-model="dependencyTaskIdsInput" maxlength="520" placeholder="多个任务编号用逗号分隔；完成前不派车" :disabled="requestUncertain || actionBusy" /></el-form-item>
              <el-form-item label="地图版本"><el-input :model-value="options?.mapVersionId ?? '—'" readonly /></el-form-item>
            </div>
          </el-form>
          <div class="creation-inline-tools">
            <el-button :disabled="optionsBusy || actionBusy || requestUncertain || !store.fresh" @click="loadOptions(true)"><ActionIcon name="refresh"/>{{ optionsBusy ? '刷新中…' : '刷新条件' }}</el-button>
            <el-button :disabled="!options || !station || !vehicle || optionsBusy || actionBusy || !store.fresh" @click="previewRoute"><ActionIcon name="route"/>核对四段路线</el-button>
            <RouterLink class="creation-help-link" to="/engineering-settings" @click="formOpen = false">配置工位、等待点与倒料点</RouterLink>
          </div>
          <div v-if="routePreview" class="route-preview"><b>{{ routePreview.message }}</b><p v-for="leg in routePreview.legs" :key="leg.sequence">{{ leg.sequence }}. {{ leg.name }}：{{ leg.vendorPath || leg.logicalPath }}</p></div>
          <section v-if="options?.template" class="template-instantiation">
            <header><div><h4>模板输入 · {{ options.template.id }} v{{ options.template.version }}</h4><p>节点通过类型化连接引用输入，不会执行任意脚本。</p></div><details><summary>执行引擎</summary><code>{{ options.template.engine }}</code></details></header>
            <dl><div v-for="input in templateInputs" :key="input.key"><dt>{{ input.label }} <small>{{ input.key }}</small></dt><dd>{{ input.value }}</dd></div></dl>
            <details><summary>输出变量与失败策略</summary><p v-for="output in templateOutputs" :key="output.key">{{ output.label }} · {{ output.key }} · {{ output.type }}</p><p>节点 {{ options.template.nodes.length }} 个，异常分支 {{ options.template.guards.length }} 条。</p></details>
          </section>
          <el-collapse class="creation-condition-collapse">
            <el-collapse-item title="查看工位条件与未满足项" name="conditions">
              <div class="creation-condition-list" role="status">
                <p v-for="s in options?.stations ?? []" :key="s.stationId">{{ s.stationId }}：{{ s.explanation }}</p>
                <p v-if="selectedVehicle">车辆：{{ selectedVehicle.explanation }}</p>
                <p v-if="selectedStation && selectedVehicle && !pickupReachable">所选车辆当前站点到取料点没有可执行路径，请检查地图道路和方向。</p>
              </div>
            </el-collapse-item>
          </el-collapse>
        </template>
        <p v-if="actionMessage" class="form-feedback" role="status">{{ industrialText(actionMessage) }}</p>
      </section>

      <section v-else class="creation-step-content">
        <div class="creation-step-heading"><el-tag effect="plain" round>第 3 步</el-tag><h3>确认并提交</h3><p>提交后仍须由后台核验车辆、地图、PLC握手和路权。</p></div>
        <el-descriptions class="creation-review-descriptions" :column="2" border>
          <el-descriptions-item label="运行方式">单次业务任务</el-descriptions-item>
          <el-descriptions-item label="取料工位">{{ selectedStation?.stationId ?? '未选择' }}</el-descriptions-item>
          <el-descriptions-item label="车辆">{{ selectedVehicle?.vehicleId ?? '未选择' }}</el-descriptions-item>
          <el-descriptions-item label="优先级">{{ priority }}</el-descriptions-item>
          <el-descriptions-item label="前置任务">{{ dependencyTaskIdsInput || '无' }}</el-descriptions-item>
          <el-descriptions-item label="地图版本">{{ options?.mapVersionId ?? '未取得' }}</el-descriptions-item>
          <el-descriptions-item label="路线核对" :span="2">{{ routePreview?.message ?? '尚未执行路线核对；提交前建议先核对四段路线。' }}</el-descriptions-item>
        </el-descriptions>
        <el-alert class="creation-el-alert" :type="canSubmit ? 'success' : 'warning'" :closable="false" show-icon>
          <template #title>{{ canSubmit ? '可以提交' : '当前不可提交' }}</template>
          {{ canSubmit ? '后台将再次校验，成功受理后继续观察任务阶段。' : '请返回上一步检查工位、车辆、路径、执行许可或未确认申请。' }}
        </el-alert>
        <p v-if="actionMessage" class="form-feedback" role="status">{{ industrialText(actionMessage) }}</p>
      </section>
    </div>
    <template #footer>
      <div class="creation-drawer-footer">
        <el-button @click="formOpen = false"><ActionIcon name="close"/>取消</el-button>
        <span class="creation-footer-spacer"/>
        <el-button v-if="createStep > 1" @click="previousCreationStep">上一步</el-button>
        <el-button v-if="createStep < creationSteps.length" type="primary" :disabled="createStep === 2 && creationMode === 'Single' && !singleSelectionReady" @click="nextCreationStep">下一步</el-button>
        <el-button v-if="creationMode === 'Single' && createStep === 3" type="primary" :disabled="!canSubmit" @click="submitTask">{{ actionBusy ? '处理中…' : requestUncertain ? '重试原申请' : '提交后台' }}</el-button>
      </div>
    </template>
  </el-drawer>
</template>
<style scoped>
.task-history-page{gap:8px}.task-page-header{display:flex;align-items:center;justify-content:space-between;gap:18px;min-height:52px;padding:2px 2px 7px;border-bottom:1px solid #243858}.task-page-title{display:flex;align-items:center;gap:10px;color:#54cadf}.task-page-title h2{color:#d9eafb;font-size:19px;letter-spacing:.8px}.task-page-title small{display:block;margin-top:4px;color:#7089aa;font-size:10px}.task-page-actions{display:flex;align-items:center;gap:7px}.task-page-actions>span{margin-right:6px;color:#6f9fba;font-size:11px}.task-query-toolbar{display:grid;grid-template-columns:minmax(170px,1.25fr) minmax(110px,.55fr) minmax(300px,1.3fr) auto auto auto;align-items:center;gap:7px;padding:8px 10px;border:1px solid #233a5d;border-radius:5px;background:#0e1932}.task-query-toolbar input,.task-query-toolbar select,.task-query-toolbar button{min-height:31px;height:31px}.query-search{display:flex;align-items:center;gap:7px;min-width:0;padding-left:9px;border:1px solid #2b4168;border-radius:3px;background:#0a1530;color:#667f9f}.query-search input{width:100%;padding:4px 7px;border:0;background:transparent}.query-date-range{display:flex;align-items:center;gap:6px;min-width:0}.query-date-range input{width:100%;min-width:126px;padding:4px 7px;color-scheme:dark}.query-date-range>span{color:#7088a8;font-size:10px}.history-presets{display:flex;gap:2px}.history-presets button{height:31px;min-height:31px;padding:4px 8px;border-color:transparent;background:transparent;color:#7f99ba;font-size:10px}.history-presets button:hover{border-color:#2f6081;background:#132947}.query-submit,.reset-submit{padding:4px 12px}.quiet-button{border-color:transparent;background:transparent}.task-result-head{display:flex;align-items:center;justify-content:space-between;gap:10px;min-height:26px;padding:2px 4px 0;color:#7890af;font-size:10px}.task-time-note{color:#5e789b}.pending-filter{color:#e9b34f}.management-table{min-height:260px}.task-result-footer{display:flex;align-items:center;justify-content:flex-end;gap:9px;min-height:35px;padding:2px 2px 4px;color:#7890ae;font-size:10px}.task-result-footer>span{margin-right:auto}.task-result-footer label{display:flex;align-items:center;gap:6px}.task-result-footer select{min-height:28px;padding:3px 7px}.task-result-footer button{min-height:28px;padding:4px 8px}.task-result-footer b{font-weight:400;font-size:10px;color:#9cb2cb}.task-history-page>.inline-warning{margin:0}
@media(max-width:1200px){.task-query-toolbar{grid-template-columns:minmax(170px,1fr) 125px minmax(280px,1.3fr) auto auto}.history-presets{grid-column:1/-1;justify-self:end}.reset-submit{display:none}}
@media(max-width:780px){.task-page-header{align-items:flex-start;flex-wrap:wrap}.task-page-actions{width:100%;flex-wrap:wrap}.task-page-actions>span{margin-right:auto}.task-query-toolbar{grid-template-columns:1fr 1fr}.query-search,.query-date-range{grid-column:1/-1}.history-presets{grid-column:1/-1;justify-self:start}.query-submit{grid-column:2}.task-result-head{flex-wrap:wrap}.task-result-footer{flex-wrap:wrap}.task-result-footer>span{width:100%}}
</style>
<style scoped>
.template-instantiation{border:1px solid #315b70;background:#0d2435;padding:13px 15px;margin:14px 0;line-height:1.7}.template-instantiation header{display:flex;justify-content:space-between;gap:12px;align-items:flex-start}.template-instantiation h4{margin:0;color:#bceaf2;font-size:14px}.template-instantiation p{margin:5px 0;color:#a8c5d6;font-size:12px}.template-instantiation code{color:#6ed7e8;font-size:11px}.template-instantiation dl{display:grid;grid-template-columns:155px minmax(0,1fr);gap:5px 12px;margin:12px 0}.template-instantiation dt{color:#8eafc5;font-size:11px}.template-instantiation dt small{display:block;color:#6f95ae}.template-instantiation dd{margin:0;color:#d3e8f2;font-size:12px;overflow-wrap:anywhere}.template-instantiation details{font-size:11px;color:#9ab8ce}.template-instantiation details p{margin:5px 0}@media(max-width:650px){.template-instantiation dl{grid-template-columns:1fr}}
</style>
<style scoped>
.task-page-title h2{
  margin:0;
}

.query-submit.changed{
  color:#f0c46a;
  border-color:#7f6535;
  background:#342b20;
}

.task-result-footer{
  justify-content:space-between;
  min-height:42px;
  padding:5px 2px 3px;
}

.result-total{
  display:flex;
  align-items:baseline;
  gap:7px;
  color:#899fbd;
  font-size:11px;
}

.result-total b{
  color:#eaf6ff;
  font:600 15px Consolas,monospace;
}

.result-controls{
  display:flex;
  align-items:center;
  gap:10px;
}

.page-size{
  display:flex;
  align-items:center;
  gap:7px;
  color:#8ca4c2;
  font-size:11px;
}

.page-size select{
  min-height:30px;
  padding:3px 26px 3px 9px;
  border-color:#294665;
  background:#101e38;
}

.pager{
  display:flex;
  align-items:center;
  overflow:hidden;
  min-height:31px;
  border:1px solid #294562;
  border-radius:6px;
  background:#0c1930;
}

.pager button{
  width:32px;
  min-height:30px;
  padding:0;
  border:0;
  border-radius:0;
  background:transparent;
  color:#91b5d1;
}

.pager button:hover:not(:disabled){
  color:#e8f9ff;
  background:#173451;
}

.pager>span{
  display:flex;
  align-items:center;
  justify-content:center;
  gap:5px;
  min-width:58px;
  min-height:23px;
  padding:0 8px;
  border-right:1px solid #223a55;
  border-left:1px solid #223a55;
  color:#819ab8;
  font:11px Consolas,monospace;
}

.pager>span b{
  color:#58d4ea;
  font-size:12px;
}

.pager>span i{
  color:#526b89;
  font-style:normal;
}

@media(max-width:520px){
  .task-result-footer{
    align-items:flex-start;
    flex-direction:column;
    gap:8px;
  }

  .result-controls{
    justify-content:space-between;
    width:100%;
  }
}
</style>
