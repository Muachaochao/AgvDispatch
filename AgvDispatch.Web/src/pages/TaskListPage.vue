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
import { IconListDetails, IconArrowLeft, IconPlus, IconSearch, IconChevronLeft, IconChevronRight } from '@tabler/icons-vue';
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
const pages = computed(() => Math.max(1, Math.ceil((queriedTasks.value?.totalCount ?? 0) / (queriedTasks.value?.pageSize ?? appliedQuery.value.pageSize))));
const tableCount = computed(() => queriedTasks.value?.totalCount ?? 0);
const canSubmit = computed(() => store.fresh && store.submissionEnabled && !actionBusy.value && !optionsBusy.value && (requestUncertain.value || (!!options.value?.available && !!selectedStation.value?.available && !!selectedVehicle.value?.available && pickupReachable.value)));
const taskStatusTone=(stage:string,waiting=false)=>stage==='Faulted'?'fault':['Cancelled','CancellationPending','CancellationReview'].includes(stage)?'cancelled':stage==='Completed'?'completed':waiting||isWaitingStage(stage)?'waiting':'running';

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
    if (mine === queryEpoch) { queriedTasks.value = result; appliedQuery.value = query; taskPage.value = result.page; }
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
      <div class="task-page-title"><IconListDetails :size="22" /><h2>业务任务</h2></div>
      <div class="task-page-actions"><button class="accent-button" @click="loadOptions()"><IconPlus :size="15" />创建任务</button><button @click="router.push('/engineering-tests')">工程测试</button><button class="quiet-button" @click="router.push('/')"><IconArrowLeft :size="15" />大屏</button></div>
    </header>
    <form class="task-query-toolbar" @submit.prevent="queryTasks(1, true)">
      <label class="query-search"><IconSearch :size="14" /><input v-model="search" maxlength="128" placeholder="任务、工位或车辆" aria-label="搜索任务" :disabled="queryBusy" /></label>
      <select v-model="taskFilter" aria-label="筛选任务状态" :disabled="queryBusy"><option value="all">全部状态</option><option value="waiting">待派车</option><option value="running">执行中</option><option value="completed">已完成</option><option value="failed">异常</option><option value="cancelled">已取消</option></select>
      <div class="query-date-range"><input v-model="createdFrom" type="datetime-local" aria-label="创建时间从" :disabled="queryBusy" /><span>至</span><input v-model="createdTo" type="datetime-local" aria-label="创建时间至" :disabled="queryBusy" /></div>
      <div class="history-presets" aria-label="快捷时间范围"><button v-for="item in ([['today', '今天'], ['yesterday', '昨天'], ['week', '近7天'], ['all', '全部时间']] as const)" :key="item[0]" type="button" :disabled="queryBusy || !store.fresh" @click="chooseTimeRange(item[0])">{{ item[1] }}</button></div>
      <button type="submit" class="accent-button query-submit" :class="{changed:queryChanged}" :disabled="queryBusy || !store.fresh">{{ queryBusy ? '查询中' : queryChanged ? '应用筛选' : '查询' }}</button>
      <button type="button" class="quiet-button reset-submit" :disabled="queryBusy" @click="resetTaskQuery">重置</button>
    </form>
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
            <td><span class="state-label" :class="taskStatusTone(task.stage,!!taskWait(store.snapshot,task,store.now))">{{ taskWait(store.snapshot,task,store.now)?.label ?? label(task.stage) }}</span><small v-if="taskWait(store.snapshot,task,store.now)" style="display:block;max-width:300px;white-space:normal">业务阶段：{{label(task.stage)}} · {{taskWait(store.snapshot,task,store.now)?.reason}}</small></td>
            <td>{{ task.priority }}</td>
            <td>{{ shortTime(task.createdAt) }}</td>
            <td>{{ shortTime(task.updatedAt) }}</td>
            <td class="message-cell" :title="task.errorMessage ?? ''">{{ task.errorMessage ? industrialText(task.errorMessage) : '—' }}</td>
          </tr>
        </tbody>
      </table>
      <p v-if="!visibleTasks.length" class="empty-panel">{{ queryBusy ? '正在查询任务记录…' : queryError && !queriedTasks ? '读取失败，请重新查询。' : '当前条件下没有任务，可调整创建时间或状态。' }}</p>
    </div>
    <footer class="task-result-footer">
      <div class="result-total"><span>任务总数</span><b>{{ tableCount }}</b></div>
      <div class="result-controls">
        <label class="page-size">每页<select v-model.number="pageSize" aria-label="每页任务条数" :disabled="queryBusy" @change="queryTasks(1, true)"><option :value="20">20 条</option><option :value="50">50 条</option><option :value="100">100 条</option><option :value="200">200 条</option></select></label>
        <nav class="pager" aria-label="任务分页">
          <button :disabled="taskPage <= 1 || queryBusy" aria-label="上一页" title="上一页" @click="queryTasks(taskPage - 1)"><IconChevronLeft :size="15" /></button>
          <span><b>{{ taskPage }}</b><i>/</i>{{ pages }}</span>
          <button :disabled="taskPage >= pages || queryBusy" aria-label="下一页" title="下一页" @click="queryTasks(taskPage + 1)"><IconChevronRight :size="15" /></button>
        </nav>
      </div>
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
      <label>前置任务（可选）<input v-model="dependencyTaskIdsInput" maxlength="520" placeholder="多个任务编号用逗号分隔；完成前不派车" :disabled="requestUncertain || actionBusy" /></label>
      <label>地图版本<input :value="options?.mapVersionId ?? '—'" readonly /></label>
    </div>
    <section v-if="options?.template" class="template-instantiation">
      <header><div><h4>模板输入 · {{ options.template.id }} v{{ options.template.version }}</h4><p>只填写模板输入，节点内部通过类型化连接自动引用。模板不会直接执行任意脚本。</p></div><details><summary>执行引擎信息</summary><code>{{ options.template.engine }}</code></details></header>
      <dl><div v-for="input in templateInputs" :key="input.key"><dt>{{ input.label }} <small>{{ input.key }}</small></dt><dd>{{ input.value }}</dd></div></dl>
      <details><summary>查看输出变量与节点失败策略</summary><p v-for="output in templateOutputs" :key="output.key">{{ output.label }} · {{ output.key }} · {{ output.type }}</p><p>节点 {{ options.template.nodes.length }} 个；机械/握手节点默认禁止自动重试，异常分支 {{ options.template.guards.length }} 条。</p></details>
    </section>
    <details class="form-explanation"><summary>查看工位条件与未满足项</summary><div role="status">
      <p v-for="s in options?.stations ?? []" :key="s.stationId">{{ s.stationId }}：{{ s.explanation }}</p>
      <p v-if="selectedVehicle">车辆：{{ selectedVehicle.explanation }}</p>
      <p v-if="selectedStation && selectedVehicle && !pickupReachable">所选车辆当前站点到取料点没有可执行路径，请检查地图道路和方向。</p>
    </div></details>
    <p class="form-explanation"><RouterLink class="cyan" to="/engineering-settings" @click="formOpen = false">配置工位、等待点、倒料点及车辆动作</RouterLink>；道路与站点位置在地图编辑中定义。当前业务步骤固定为九步。</p>
    <div class="inline-warning">{{ store.submissionEnabled ? '提交后仍须经过后台核验，成功接收不等于执行完成。' : 'Web提交未启用，可核对条件及路线。' }}</div>
    <div v-if="routePreview" class="route-preview"><b>{{ routePreview.message }}</b><p v-for="leg in routePreview.legs" :key="leg.sequence">{{ leg.sequence }}. {{ leg.name }}：{{ leg.vendorPath || leg.logicalPath }}</p></div>
    <p v-if="actionMessage" class="form-feedback" role="status">{{ industrialText(actionMessage) }}</p>
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
