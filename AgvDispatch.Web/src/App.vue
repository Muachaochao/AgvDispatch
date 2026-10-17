<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { DispatchScene } from './DispatchScene';
import { newTaskRequestId } from './task-request';
import { isFresh, isRunningStage, label, mayPlot } from './state';
import type { Alarm, CreationOptions, Device, MapSummary, RoutePreview, Snapshot, TaskDetail, TaskRow } from './types';
import { IconRobot, IconRoute, IconBell, IconLayoutDashboard, IconListDetails, IconMap, IconArrowsMaximize, IconRefresh, IconPlus,
  IconPlayerPlay, IconPlayerPause, IconPlayerSkipForward, IconX, IconSearch, IconChevronLeft, IconChevronRight, IconPlugConnected,
  IconCpu, IconNetwork, IconArrowLeft, IconZoomIn, IconZoomOut, IconEye, IconCircleCheck, IconAlertTriangle } from '@tabler/icons-vue';
import RingChart from './RingChart.vue';
import ConsoleDialog from './ConsoleDialog.vue';
import PlanningWorkbench from './PlanningWorkbench.vue';
import TaskExecutionDetails from './TaskExecutionDetails.vue';
import CommissioningPanel from './CommissioningPanel.vue';
import type { AdapterIdentity } from './release-state';
import { alarmSeverity, alarmStatus, categoryLabels, filterAlarms, shortTime, vehicleCategory, type VehicleCategory } from './dashboard';

const source = ref<'demo' | 'backend'>('backend');
const snapshot = ref<Snapshot | null>(null);
const selectedVehicle = ref('');
const selectedTask = ref('');
const selectedEdge = ref('');
const detail = ref<TaskDetail | null>(null);
const mapElement = ref<HTMLElement>();
const error = ref('');
const sceneError = ref('');
const now = ref(Date.now());
const search = ref('');
const taskFilter = ref('all');
const options = ref<CreationOptions | null>(null);
const route = ref<RoutePreview | null>(null);
const submissionEnabled = ref(false);
const adapterIdentity=ref<AdapterIdentity|null>(null);
const formOpen = ref(false);
const station = ref('');
const vehicle = ref('');
const priority = ref(50);
const actionBusy = ref(false);
const actionMessage = ref('');
const requestUncertain = ref(false);
const view = ref<'overview' | 'vehicles' | 'tasks' | 'alarms' | 'maps' | 'planning' | 'commissioning'>('overview');
const planningTab=ref('map');
const mapView = ref<'top' | 'isometric'>('top');
const vehicleFilter = ref('all');
const vehicleSearch = ref('');
const alarmSearch = ref('');
const alarmStateFilter = ref('all');
const alarmLevelFilter = ref('all');
const selectedAlarm = ref<Alarm | null>(null);
const selectedDevice = ref<Device | null>(null);
const showTaskDialog = ref(false);
const catalog = ref<MapSummary[]>([]);
const selectedMap = ref<MapSummary | null>(null);
const catalogError = ref('');
const queryBusy = ref(false);
const queryError = ref('');
const taskPage = ref(1);
const queriedTasks = ref<{ items: TaskRow[]; totalCount: number; page: number; pageSize: number } | null>(null);
let scene: DispatchScene | undefined;
let socket: WebSocket | undefined;
let epoch = 0;
let detailEpoch = 0;
let timer: ReturnType<typeof setInterval>;
const controllers = new Set<AbortController>();
const fresh = computed(() => isFresh(snapshot.value, now.value));
const vehicles = computed(() => snapshot.value?.status?.agvs ?? []);
const chosenVehicle = computed(() => vehicles.value.find(v => v.vehicleId === selectedVehicle.value));
const allTasks = computed(() => snapshot.value?.tasks?.items ?? []);
const visibleTasks = computed(() => queriedTasks.value?.items ?? allTasks.value.filter(task =>
  (taskFilter.value === 'all' || (taskFilter.value === 'waiting' ? task.stage === 'WaitingForVehicle' || task.stage === 'Created' : taskFilter.value === 'failed' ? ['Faulted', 'CancellationReview'].includes(task.stage) : taskFilter.value === 'completed' ? task.stage === 'Completed' : taskFilter.value === 'cancelled' ? task.stage === 'Cancelled' : isRunningStage(task.stage))) &&
  `${task.taskId} ${task.stationId} ${task.vehicleId ?? ''}`.toLowerCase().includes(search.value.toLowerCase())));
const equipment = computed(() => snapshot.value?.devices?.equipment ?? []);
const resources = computed(() => snapshot.value?.resources ?? []);
const selectedRoad = computed(() => snapshot.value?.map?.edges.find(edge => edge.id === selectedEdge.value));
const mapVehicleCount = computed(() => snapshot.value ? vehicles.value.filter(v => mayPlot(snapshot.value!, v, now.value)).length : 0);
const activeCount = computed(() => allTasks.value.filter(task => !['Completed', 'Cancelled'].includes(task.stage)).length);
const canSubmit = computed(() => source.value === 'backend' && fresh.value && submissionEnabled.value && !actionBusy.value &&
  (requestUncertain.value || (!!options.value?.available && !!station.value && !!vehicle.value)));
const currentStep = computed(() => detail.value?.steps.find(step => step.isCurrent));
const vehicleCategories: VehicleCategory[] = ['running', 'waiting', 'idle', 'charging', 'fault', 'offline', 'unknown'];
const vehicleStats = computed(() => vehicleCategories.map(key => ({ key, label: categoryLabels[key], count: vehicles.value.filter(v => vehicleCategory(v, allTasks.value, fresh.value) === key).length })));
const filteredVehicles = computed(() => vehicles.value.filter(v => (vehicleFilter.value === 'all' || vehicleCategory(v, allTasks.value, fresh.value) === vehicleFilter.value) &&
  `${v.vehicleId} ${v.displayName} ${v.currentStation ?? ''}`.toLowerCase().includes(vehicleSearch.value.toLowerCase())));
const alarms = computed(() => snapshot.value?.alarms ?? []);
const openAlarms = computed(() => alarms.value.filter(a => a.status === 'Open'));
const visibleAlarms = computed(() => filterAlarms(alarms.value, alarmSearch.value, alarmStateFilter.value, alarmLevelFilter.value));
const ringSegments = computed(() => [
  { label: '执行', value: allTasks.value.filter(t => isRunningStage(t.stage)).length, color: '#31c6e6' },
  { label: '等待', value: allTasks.value.filter(t => ['Created', 'WaitingForVehicle'].includes(t.stage)).length, color: '#e9b34f' },
  { label: '完成', value: allTasks.value.filter(t => t.stage === 'Completed').length, color: '#25d3a4' },
  { label: '异常/其他', value: allTasks.value.filter(t => !isRunningStage(t.stage) && !['Created', 'WaitingForVehicle', 'Completed'].includes(t.stage)).length, color: '#ee668b' }
]);
const pages = computed(() => Math.max(1, Math.ceil((queriedTasks.value?.totalCount ?? snapshot.value?.tasks?.totalCount ?? 0) / 50)));
const tableCount = computed(() => queriedTasks.value?.totalCount ?? snapshot.value?.tasks?.totalCount ?? 0);
const navigation = [ { key: 'overview', label: '调度大屏', icon: IconLayoutDashboard }, { key: 'vehicles', label: '车辆列表', icon: IconRobot },
  { key: 'tasks', label: '任务列表', icon: IconListDetails }, { key: 'alarms', label: '异常告警', icon: IconBell }, { key: 'maps', label: '地图查看', icon: IconMap }, { key:'planning',label:'工程编辑',icon:IconRoute }, {key:'commissioning',label:'联调检查',icon:IconCircleCheck} ] as const;

async function api<T>(path: string, payload?: unknown): Promise<T> {
  const controller = new AbortController(); controllers.add(controller);
  const timeout = setTimeout(() => controller.abort(), 35000);
  try {
    const response = await fetch(path, { method: payload === undefined ? 'GET' : 'POST',
      headers: payload === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: payload === undefined ? undefined : JSON.stringify(payload), signal: controller.signal, cache: 'no-store' });
    const data = await response.json();
    if (!response.ok) throw new Error(data.detail ?? data.error ?? `HTTP ${response.status}`);
    return data;
  } finally { clearTimeout(timeout); controllers.delete(controller); }
}

function draw() { scene?.update(snapshot.value, selectedVehicle.value, selectedEdge.value); }
function stopConnection() {
  socket?.close(); socket = undefined;
  controllers.forEach(controller => controller.abort()); controllers.clear();
}
async function connectBackend() {
  stopConnection(); const mine = ++epoch; ++detailEpoch;
  source.value = 'backend'; snapshot.value = null; detail.value = null; route.value = null; queriedTasks.value = null; catalog.value = []; showTaskDialog.value = false; selectedAlarm.value = null; selectedDevice.value = null;
  options.value = null; selectedTask.value = ''; selectedVehicle.value = ''; selectedEdge.value = ''; error.value = ''; actionMessage.value = ''; submissionEnabled.value = false; actionBusy.value = false;
  draw();
  try {
    const config = await api<{ taskSubmissionEnabled: boolean;adapter?:AdapterIdentity }>('/api/config');
    if (mine !== epoch) return;
    submissionEnabled.value = config.taskSubmissionEnabled;
    adapterIdentity.value=config.adapter??null;
    const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/api/ws`);
    socket = ws;
    ws.onmessage = event => {
      if (mine !== epoch) return;
      try {
        const incoming = JSON.parse(event.data) as Snapshot;
        if (incoming.source !== 'backend' || typeof incoming.connected !== 'boolean') throw new Error('后台快照格式无效');
        if (!incoming.connected) {
          snapshot.value = snapshot.value ? { ...snapshot.value, connected: false, error: incoming.error } : incoming;
          detail.value = null; options.value = null; route.value = null;
          error.value = incoming.error ?? '后台不可用';
        } else {
          const mapChanged = snapshot.value?.map?.id !== incoming.map?.id || snapshot.value?.map?.version !== incoming.map?.version;
          snapshot.value = incoming; error.value = '';
          if (mapChanged) { route.value = null; options.value = null; }
          if (!selectedVehicle.value) selectedVehicle.value = incoming.status?.agvs[0]?.vehicleId ?? '';
          if (!selectedTask.value && incoming.tasks?.items.length) selectTask(incoming.tasks.items[0].taskId);
          else if (selectedTask.value) void loadDetail(selectedTask.value);
        }
        draw();
      } catch (ex) { error.value = String(ex); }
    };
    ws.onerror = () => { if (mine === epoch) error.value = 'WebSocket 连接失败，请确认本机 Web 接入服务正在运行。'; };
    ws.onclose = () => {
      if (mine !== epoch) return;
      if (snapshot.value) snapshot.value = { ...snapshot.value, connected: false };
      detail.value = null; options.value = null; route.value = null;
      error.value ||= '后台推送已断开，点击“重新连接”重试。'; draw();
    };
  } catch (ex) { if (mine === epoch) error.value = String(ex); }
}
async function loadDetail(id: string) {
  const mine = epoch, selection = ++detailEpoch;
  try { const result = await api<TaskDetail | null>(`/api/tasks/${encodeURIComponent(id)}`);
    if (mine === epoch && selection === detailEpoch && selectedTask.value === id && fresh.value) detail.value = result;
  } catch (ex) { if (mine === epoch && selection === detailEpoch) { detail.value = null; error.value = String(ex); } }
}
function selectTask(id: string) { selectedTask.value = id; detail.value = null;
  void loadDetail(id); }
async function loadOptions() {
  formOpen.value = true; route.value = null; actionMessage.value = '';
  const mine = epoch;
  try { const result = await api<CreationOptions>('/api/task-options'); if (mine !== epoch) return;
    options.value = result; station.value = result.stations.find(s => s.available)?.stationId ?? ''; vehicle.value = result.vehicles.find(v => v.available)?.vehicleId ?? '';
  } catch (ex) { if (mine === epoch) actionMessage.value = String(ex); }
}
async function previewRoute() {
  if (!options.value || !station.value || !vehicle.value) return;
  actionBusy.value = true; route.value = null; const mine = epoch;
  try { const result = await api<RoutePreview>('/api/route-preview', { stationId: station.value, vehicleId: vehicle.value, mapId: options.value.mapId, mapVersionId: options.value.mapVersionId, parameterRevision: options.value.parameterRevision });
    if (mine === epoch) route.value = result;
  } catch (ex) { if (mine === epoch) actionMessage.value = String(ex); }
  finally { if (mine === epoch) actionBusy.value = false; }
}
async function submitTask() {
  if (!canSubmit.value) return;
  const mine = epoch;
  const storageKey = 'agv-web-pending-request';
  let request: Record<string, unknown>;
  try {
    const stored = sessionStorage.getItem(storageKey);
    request = requestUncertain.value && stored ? JSON.parse(stored) : { requestId: newTaskRequestId(), stationId: station.value, requestedVehicleId: vehicle.value,
      priority: priority.value, mapId: options.value!.mapId, mapVersionId: options.value!.mapVersionId, parameterRevision: options.value!.parameterRevision };
    sessionStorage.setItem(storageKey, JSON.stringify(request));
  } catch { actionMessage.value = '无法持久保存申请编号，本次未提交。'; return; }
  actionBusy.value = true; actionMessage.value = '正在提交，保持原申请编号。'; requestUncertain.value = true;
  try {
    const result = await api<{ accepted: boolean; created: boolean; taskId: string | null; message: string }>('/api/tasks', request);
    sessionStorage.removeItem(storageKey); requestUncertain.value = false;
    if (mine !== epoch) return;
    actionMessage.value = result.message;
    if (result.accepted && result.taskId) selectTask(result.taskId);
  } catch (ex) { if (mine === epoch) actionMessage.value = `结果未确认：${String(ex)}。保留原申请，重试使用同一个编号。`; }
  finally { if (mine === epoch) actionBusy.value = false; }
}
watch([selectedVehicle, selectedEdge], draw);
watch([station, vehicle, priority], () => { route.value = null; });
watch([search, taskFilter], () => { queriedTasks.value = null; taskPage.value = 1; });
function openTask(id: string) { showTaskDialog.value = true; selectTask(id); }
function setView(key: typeof view.value) { view.value = key; if (key === 'maps') void loadCatalog(); }
async function loadCatalog() {
  catalogError.value = ''; const mine = epoch;
  try { const result = await api<MapSummary[]>('/api/maps'); if (mine === epoch) catalog.value = result; }
  catch (ex) { if (mine === epoch) catalogError.value = String(ex); }
}
async function queryTasks(page = 1) {
  queryError.value = ''; taskPage.value = page;
  const mine = epoch; queryBusy.value = true;
  try { const filter = ({ all: 'All', waiting: 'Waiting', running: 'Running', failed: 'Failed', completed: 'Completed', cancelled: 'Cancelled' } as Record<string, string>)[taskFilter.value];
    const result = await api<{ items: TaskRow[]; totalCount: number; page: number; pageSize: number }>(`/api/tasks?filter=${filter}&search=${encodeURIComponent(search.value)}&page=${page}`);
    if (mine === epoch) queriedTasks.value = result;
  } catch (ex) { if (mine === epoch) queryError.value = String(ex); }
  finally { if (mine === epoch) queryBusy.value = false; }
}
function resetTaskQuery() { search.value = ''; taskFilter.value = 'all'; queriedTasks.value = null; taskPage.value = 1; queryError.value = ''; }
function openPlanning(tab='map') { planningTab.value=tab;view.value='planning'; }
function navigateCommissioning(target:string){if(target==='map')openPlanning('map');else if(target==='vehicles')openPlanning('vehicles');else if(target==='vehicle-status')setView('vehicles');else if(target==='tasks')setView('tasks');void nextTick(()=>window.scrollTo({top:0,behavior:'auto'}));}
function setMapView(mode: 'top' | 'isometric') { mapView.value = mode; scene?.view(mode); }
function closeDialogs(event?: KeyboardEvent) { if (event && event.key !== 'Escape') return; formOpen.value = false; showTaskDialog.value = false; selectedAlarm.value = null; selectedMap.value = null; selectedDevice.value = null; }
function fullscreen() { void mapElement.value?.parentElement?.requestFullscreen().catch(() => { sceneError.value = '当前浏览器未允许全屏，仍可用缩放按钮查看。'; }); }

onMounted(() => {
  window.addEventListener('keydown', closeDialogs);
  try { requestUncertain.value = !!sessionStorage.getItem('agv-web-pending-request'); } catch { /* submission checks storage before sending */ }
  try { scene = new DispatchScene(mapElement.value!, id => { selectedVehicle.value = id; }); draw(); }
  catch (ex) { sceneError.value = `三维视图初始化失败：${String(ex)}。任务和设备数据仍可查看。`; }
  void connectBackend();
  timer = setInterval(() => { now.value = Date.now(); if (!fresh.value) draw(); }, 1000);
});
onUnmounted(() => { ++epoch; stopConnection(); clearInterval(timer); scene?.dispose(); window.removeEventListener('keydown', closeDialogs); });
</script>

<template>
  <div class="console-shell">
    <header class="console-header">
      <button class="console-brand" @click="setView('overview')"><IconRobot :size="31" stroke="1.5" /><div><h1>AGV 机器人调度系统</h1><span>DISPATCH CONTROL CENTER</span></div></button>
      <div class="header-totals"><div><span>执行任务</span><b>{{ ringSegments[0].value }}</b></div><div><span>等待任务</span><b class="cyan">{{ ringSegments[1].value }}</b></div><div><span>未确认告警</span><b :class="openAlarms.length ? 'amber-text' : ''">{{ openAlarms.length }}</b></div><small>当前已加载数据</small></div>
      <nav aria-label="页面导航"><button v-for="item in navigation" :key="item.key" :class="{ active: view === item.key }" @click="setView(item.key)"><component :is="item.icon" :size="15" />{{ item.label }}</button></nav>
      <div class="header-actions"><button class="accent-button" @click="loadOptions"><IconPlus :size="16" />创建任务</button><button class="icon-button" :aria-label="'重新连接后台'" :title="'重新连接后台'" @click="connectBackend"><IconPlugConnected :size="19" /></button></div>
    </header>
    <div class="source-bar" :class="{disconnected:!fresh}"><div class="source-indicator"><i class="status-dot" :class="fresh?'green':'red'"></i><b>{{fresh?'真实后台已连接':'真实后台未连接 / 数据过期'}}</b><span>{{error || (snapshot?`最近观察 ${shortTime(snapshot.receivedAt)} · 非原子设备快照`:'正在连接C#调度主服务…')}}</span></div><button class="text-button" @click="setView('commissioning')">{{fresh?'主服务':'最后观察主服务'}} {{snapshot?.status?.service?.version??'版本未返回'}} · {{snapshot?.status?.execution?snapshot.status.execution.handshakeEnabled?'持久化新握手':'原执行器':'模式未返回'}} {{snapshot?.status?.execution?.buildId.slice(0,12)??''}}</button><button class="text-button" @click="connectBackend">重新连接后台</button></div>

    <div v-show="view === 'overview'" class="control-grid">
      <aside class="left-column">
        <section class="control-panel statistics-panel"><header class="section-heading"><h2>车辆状态统计 <small>FLEET STATUS</small></h2><span class="outline-badge">{{ snapshot?.status ? `已观察 ${vehicles.length} 辆` : '待采集' }}</span></header><div class="fleet-counts"><button v-for="stat in vehicleStats.filter(s => s.key !== 'unknown' || s.count > 0)" :key="stat.key" :class="stat.key" @click="vehicleFilter = stat.key; setView('vehicles')"><i class="status-dot" :class="stat.key"></i><b>{{ stat.count }}</b><span>{{ stat.label }}</span></button></div></section>
        <section class="control-panel vehicle-focus"><header class="section-heading"><h2>车辆详情 <small>VEHICLE</small></h2><button class="text-button" @click="setView('vehicles')">列表</button></header><template v-if="chosenVehicle"><div class="focus-vehicle"><IconRobot :size="35" stroke="1.2" /><div><b>{{ chosenVehicle.vehicleId }}</b><small>{{ chosenVehicle.displayName }}</small></div><span class="state-label" :class="vehicleCategory(chosenVehicle,allTasks,fresh)">{{ categoryLabels[vehicleCategory(chosenVehicle,allTasks,fresh)] }}</span></div><div class="battery-line"><span>电量</span><progress max="100" :value="chosenVehicle.batteryPercent ?? 0"></progress><b>{{ chosenVehicle.batteryPercent ?? '—' }}%</b></div><dl class="property-list"><dt>当前站点</dt><dd>{{ chosenVehicle.currentStation ?? '未知' }}</dd><dt>导航状态</dt><dd>{{ chosenVehicle.navigationState ?? '未知' }}</dd><dt>原始坐标</dt><dd>{{ chosenVehicle.x?.toFixed(2) ?? '—' }} / {{ chosenVehicle.y?.toFixed(2) ?? '—' }}</dd><dt>定位质量</dt><dd>{{ label(chosenVehicle.quality) }} / {{ chosenVehicle.localizationConfidence ?? '—' }}</dd><dt>地图叠加</dt><dd>{{ snapshot && mayPlot(snapshot,chosenVehicle,now) ? '已核定且有效' : '待核对 / 不显示' }}</dd></dl></template><p v-else class="empty-panel">连接后台后选择车辆</p><div class="vehicle-picker"><button v-for="v in vehicles" :key="v.vehicleId" :class="{ selected: v.vehicleId === selectedVehicle }" @click="selectedVehicle = v.vehicleId">{{ v.vehicleId }}</button></div></section>
        <section class="control-panel alert-preview"><header class="section-heading"><h2>异常告警 <small>ALARM CENTER</small></h2><button class="text-button" @click="setView('alarms')">更多 {{ openAlarms.length }}</button></header><div class="mini-table-head"><span>级别 / 对象</span><span>告警信息</span></div><button v-for="alarm in openAlarms.slice(0,5)" :key="alarm.alarmId" class="alarm-preview-row" @click="selectedAlarm = alarm"><div><span class="severity" :class="alarm.severity.toLowerCase()">{{ alarmSeverity(alarm.severity) }}</span><small>{{ alarm.sourceId }}</small></div><span>{{ alarm.title }}<small>{{ shortTime(alarm.lastObservedAt) }}</small></span></button><p v-if="!openAlarms.length" class="empty-panel"><IconCircleCheck :size="25" />{{ fresh ? '暂无未确认告警' : '等待有效告警数据' }}</p><div class="panel-footnote">确认与恢复是不同状态，不在此自动清除告警</div></section>
      </aside>

      <section class="control-panel map-control"><header class="section-heading map-heading"><div><h2>{{ snapshot?.map?.name ?? '当前运行地图' }}</h2><small>{{ snapshot?.map?.id ?? '未连接' }} · {{ snapshot?.map?.version ?? '—' }}</small></div><div class="map-toolbar"><button :class="{ active: mapView === 'top' }" @click="setMapView('top')">2D</button><button :class="{ active: mapView === 'isometric' }" @click="setMapView('isometric')">3D</button><button class="icon-button" aria-label="全屏地图" title="全屏地图" @click="fullscreen"><IconArrowsMaximize :size="15" /></button></div></header><div class="map-viewport"><div ref="mapElement" class="scene-container" aria-label="可交互的运行地图"></div><div v-if="sceneError || !snapshot?.map" class="scene-empty">{{ sceneError || '尚未取得后台运行地图。请确认C#主服务已启动、管道名称一致且已激活运行地图。' }}</div><div class="map-status-legend"><span><i class="status-dot running"></i>执行</span><span><i class="status-dot waiting"></i>等待</span><span><i class="status-dot fault"></i>故障</span><span><i class="status-dot offline"></i>离线</span></div><div class="map-navigation"><IconRoute :size="15" /><span>厂家站点与导航道路</span><small>显示不作为执行放行依据</small></div><div class="map-zoom"><button aria-label="地图缩小" @click="scene?.zoom(1.2)"><IconZoomOut :size="16" /></button><button @click="scene?.view(mapView)">复位</button><button aria-label="地图放大" @click="scene?.zoom(0.8)"><IconZoomIn :size="16" /></button></div><div class="map-caption-bottom"><span>{{ snapshot?.map?.nodes.length ?? 0 }} 节点 / {{ snapshot?.map?.edges.length ?? 0 }} 道路</span><span>{{ mapVehicleCount }} 辆可定位 · {{ mapView === 'top' ? '右键平移 · 滚轮缩放' : '左键旋转 · 右键平移' }}</span></div></div><div class="map-bottom-tools"><label>路段定位<select v-model="selectedEdge" aria-label="选择地图路段"><option value="">选择道路</option><option v-for="edge in snapshot?.map?.edges ?? []" :key="edge.id" :value="edge.id">{{ edge.id }} · {{ edge.from }} → {{ edge.to }}</option></select></label><span v-if="selectedRoad">{{ selectedRoad.from }} {{ selectedRoad.bidirectional ? '↔' : '→' }} {{ selectedRoad.to }}</span><span v-else>米制导航图 / {{ snapshot?.map?.coordinateSystem ?? '待核定' }}</span></div><div v-for="warning in snapshot?.map?.warnings ?? []" :key="warning" class="inline-warning">{{ warning }}</div></section>

      <aside class="right-column"><section class="control-panel distribution-panel"><header class="section-heading"><h2>任务状态分布 <small>TASK STATUS</small></h2></header><RingChart :segments="ringSegments" :total="allTasks.length" /><div class="ring-legend"><span v-for="segment in ringSegments" :key="segment.label"><i class="status-dot" :style="{background:segment.color}"></i>{{ segment.label }}<b>{{ segment.value }}</b></span></div><p class="panel-footnote">当前快照最多50条，不代表全历史统计</p></section>
        <section class="control-panel live-tasks"><header class="section-heading"><h2>当前任务 <small>LIVE TASKS</small></h2><button class="text-button" @click="setView('tasks')">全部</button></header><div class="current-task-counts"><span>执行 <b>{{ ringSegments[0].value }}</b></span><span>等待 <b>{{ ringSegments[1].value }}</b></span></div><button v-for="task in allTasks.slice(0,6)" :key="task.taskId" class="live-task" @click="openTask(task.taskId)"><div><b>{{ task.taskId }}</b><span class="state-label" :class="task.stage === 'Faulted' ? 'fault' : isRunningStage(task.stage) ? 'running' : 'waiting'">{{ label(task.stage) }}</span></div><p>{{ task.stationId }}<span>{{ task.vehicleId ?? '待分配' }}</span></p><small>{{ task.errorMessage || (detail?.task.taskId === task.taskId ? detail.waitingExplanation : '点击查看步骤及PLC条件') }}</small></button><p v-if="!allTasks.length" class="empty-panel">暂无任务数据</p></section>
        <section class="control-panel shared-panel"><header class="section-heading"><h2>共享区域 <small>TRAFFIC</small></h2></header><div v-for="resource in resources" :key="resource.resourceId" class="shared-resource"><span><i class="status-dot" :class="resource.occupiedCount ? 'amber' : 'green'"></i>{{ resource.resourceId }}</span><b>{{ resource.occupiedCount }}/{{ resource.capacity }}</b><small>{{ resource.leases.map(l=>l.vehicleId).join('、') || (resource.occupiedCount ? '已占用' : '空闲') }}</small></div><p v-if="!resources.length" class="empty-panel">暂无资源数据</p></section>
      </aside>
    </div>

    <CommissioningPanel v-if="view==='commissioning'" :snapshot="snapshot" :adapter="adapterIdentity" @navigate="navigateCommissioning" />
    <PlanningWorkbench v-show="view==='planning'" :source="source" :snapshot="snapshot" :initial-tab="planningTab" />

    <section v-if="view === 'vehicles'" class="management-page"><header class="management-title"><h2><IconRobot :size="23" />车辆列表</h2><span>共 {{ vehicles.length }} 辆</span><button class="accent-button" @click="openPlanning('vehicles')">新增 / 配置车辆</button><button @click="setView('overview')"><IconArrowLeft :size="15" />返回大屏</button></header><div class="query-bar"><label>车辆 / 站点<input v-model="vehicleSearch" placeholder="编号、名称、当前位置" aria-label="搜索车辆" /></label><label>车辆状态<select v-model="vehicleFilter" aria-label="筛选车辆状态"><option value="all">全部</option><option v-for="stat in vehicleStats" :key="stat.key" :value="stat.key">{{ stat.label }}</option></select></label><button @click="vehicleSearch='';vehicleFilter='all'">重置</button></div><div class="management-table"><table><thead><tr><th>车辆编号</th><th>名称</th><th>状态</th><th>当前站点</th><th>原始坐标 X / Y</th><th>电量</th><th>定位置信度</th><th>数据质量</th><th>最近采集</th><th>操作</th></tr></thead><tbody><tr v-for="v in filteredVehicles" :key="v.vehicleId"><td class="mono cyan">{{ v.vehicleId }}</td><td>{{ v.displayName }}</td><td><span class="state-label" :class="vehicleCategory(v,allTasks,fresh)">{{ categoryLabels[vehicleCategory(v,allTasks,fresh)] }}</span></td><td>{{ v.currentStation ?? '—' }}</td><td class="mono">{{ v.x?.toFixed(2) ?? '—' }} / {{ v.y?.toFixed(2) ?? '—' }}</td><td class="green-text">{{ v.batteryPercent ?? '—' }}%</td><td>{{ v.localizationConfidence ?? '—' }}</td><td>{{ label(v.quality) }}</td><td>{{ shortTime(v.lastSuccessfulPollAt) }}</td><td><button class="table-button" @click="selectedVehicle=v.vehicleId;setView('overview')">地图定位</button></td></tr></tbody></table><p v-if="!filteredVehicles.length" class="empty-panel">没有匹配车辆</p></div></section>

    <section v-if="view === 'tasks'" class="management-page"><header class="management-title"><h2><IconListDetails :size="23" />任务列表</h2><span>{{ queriedTasks ? '查询总数' : '快照总数' }} {{ tableCount }} 条</span><button class="accent-button" @click="loadOptions"><IconPlus :size="15" />创建任务</button><button @click="setView('overview')"><IconArrowLeft :size="15" />返回大屏</button></header><div class="query-bar"><label>任务 / 工位 / 车辆<input v-model="search" placeholder="输入关键词" aria-label="搜索任务" @keydown.enter="queryTasks(1)" /></label><label>任务状态<select v-model="taskFilter" aria-label="筛选任务状态"><option value="all">全部</option><option value="waiting">等待</option><option value="running">执行中</option><option value="completed">已完成</option><option value="failed">异常</option><option value="cancelled">已取消</option></select></label><button class="accent-button" :disabled="queryBusy || (source==='backend'&&!fresh)" @click="queryTasks(1)"><IconSearch :size="15" />{{ queryBusy ? '查询中' : '查询' }}</button><button @click="resetTaskQuery">重置</button><small>{{ queriedTasks ? '后台分页查询结果' : '当前快照筛选；后台可点击查询读取历史' }}</small></div><p v-if="queryError" class="inline-warning">{{ queryError }}</p><div class="management-table"><table><thead><tr><th>操作</th><th>任务编号</th><th>来源工位</th><th>车辆</th><th>状态</th><th>优先级</th><th>创建时间</th><th>更新时间</th><th>异常说明</th></tr></thead><tbody><tr v-for="task in visibleTasks" :key="task.taskId"><td><button class="table-button" @click="openTask(task.taskId)">详情</button></td><td class="cyan mono">{{ task.taskId }}</td><td>{{ task.stationId }}</td><td>{{ task.vehicleId ?? '待分配' }}</td><td><span class="state-label" :class="task.stage==='Faulted'?'fault':task.stage==='Completed'?'idle':'running'">{{ label(task.stage) }}</span></td><td>{{ task.priority }}</td><td>{{ shortTime(task.createdAt) }}</td><td>{{ shortTime(task.updatedAt) }}</td><td class="message-cell">{{ task.errorMessage ?? '—' }}</td></tr></tbody></table><p v-if="!visibleTasks.length" class="empty-panel">暂无匹配任务</p></div><footer class="pagination"><span>当前页 {{ visibleTasks.length }} 条 · 每页最多50条</span><button :disabled="taskPage<=1||queryBusy" @click="queryTasks(taskPage-1)"><IconChevronLeft :size="14" />上一页</button><b>{{ taskPage }} / {{ pages }}</b><button :disabled="taskPage>=pages||queryBusy" @click="queryTasks(taskPage+1)">下一页<IconChevronRight :size="14" /></button></footer></section>

    <section v-if="view === 'alarms'" class="management-page"><header class="management-title"><h2><IconBell :size="23" />异常告警</h2><span>未确认 {{ openAlarms.length }} 条 / 已加载 {{ alarms.length }} 条</span><button @click="setView('overview')"><IconArrowLeft :size="15" />返回大屏</button></header><div class="query-bar"><label>告警 / 对象<input v-model="alarmSearch" aria-label="搜索告警" placeholder="告警信息、编号、车辆或任务" /></label><label>状态<select v-model="alarmStateFilter" aria-label="筛选告警状态"><option value="all">全部</option><option value="Open">未确认</option><option value="Acknowledged">已确认</option><option value="Recovered">已恢复</option></select></label><label>级别<select v-model="alarmLevelFilter" aria-label="筛选告警级别"><option value="all">全部</option><option value="Info">信息</option><option value="Warning">警告</option><option value="Error">错误</option><option value="Critical">严重</option></select></label><button @click="alarmSearch='';alarmStateFilter='all';alarmLevelFilter='all'">重置</button></div><div class="management-table"><table><thead><tr><th>操作</th><th>告警编号</th><th>关联对象</th><th>级别</th><th>告警信息</th><th>状态</th><th>首次发生</th><th>最近观察</th><th>恢复时间</th></tr></thead><tbody><tr v-for="alarm in visibleAlarms" :key="alarm.alarmId"><td><button class="table-button" @click="selectedAlarm=alarm">详情</button></td><td class="mono cyan">{{ alarm.alarmId }}</td><td>{{ alarm.sourceId }}</td><td><span class="severity" :class="alarm.severity.toLowerCase()">{{ alarmSeverity(alarm.severity) }}</span></td><td>{{ alarm.title }}</td><td>{{ alarmStatus(alarm.status) }}</td><td>{{ shortTime(alarm.firstOccurredAt) }}</td><td>{{ shortTime(alarm.lastObservedAt) }}</td><td>{{ shortTime(alarm.recoveredAt) }}</td></tr></tbody></table><p v-if="!visibleAlarms.length" class="empty-panel">暂无匹配告警</p></div><footer class="pagination"><span>显示后台最近100条；告警确认、现场恢复仍沿用WPF操作。</span></footer></section>

    <section v-if="view === 'maps'" class="management-page"><header class="management-title"><h2><IconMap :size="23" />地图查看与版本</h2><span>共 {{ catalog.length }} 张</span><button class="accent-button" @click="openPlanning('map')">地图 / 场景编辑</button><button @click="openPlanning('plans')">方案管理</button><button @click="loadCatalog"><IconRefresh :size="15" />刷新</button><button @click="setView('overview')"><IconArrowLeft :size="15" />返回大屏</button></header><div class="map-catalog-note">复用厂家建好的地图。本页查看运行版本；工程编辑中可加载完整草稿、编辑和发布，正式运行切换沿用WPF安全流程。</div><p v-if="catalogError" class="inline-warning">{{ catalogError }}</p><div class="map-catalog"><article v-for="map in catalog" :key="map.mapId" class="map-card"><div class="map-card-art"><IconMap :size="64" stroke="1" /><span class="outline-badge" :class="{ cyan:map.isActive }">{{ map.isActive?'当前运行':'未运行' }}</span><b>{{ map.mapId }}</b><small>{{ map.unit }} / {{ map.coordinateSystem }}</small></div><h3>{{ map.name }}</h3><p>{{ map.description || '未填写说明' }}</p><dl><dt>运行版本</dt><dd>{{ map.activeVersionId ?? '—' }}</dd><dt>发布版本</dt><dd>{{ map.publishedVersionId ?? '—' }}</dd></dl><footer><button @click="selectedMap=map"><IconEye :size="14" />查看信息</button><button v-if="map.isActive && map.mapId===snapshot?.map?.id" class="accent-button" @click="setView('overview')">查看运行图</button></footer></article><p v-if="!catalog.length&&!catalogError" class="empty-panel">没有可查看的地图</p></div></section>

    <footer class="equipment-strip"><b>设备状态<small>DEVICE STATUS</small></b><button v-for="device in equipment.slice(0,4)" :key="device.id" @click="selectedDevice=device"><IconCpu :size="19" /><span>{{ device.name }}</span><i class="status-dot" :class="fresh&&device.quality==='Good'?'green':'amber'"></i><small>{{ fresh ? device.status : '数据非当前' }}</small></button><div><IconNetwork :size="20" /><span>后台通信</span><i class="status-dot" :class="fresh?'green':'red'"></i><small>{{ fresh?'已连接':'未连接' }}</small></div><div><IconRoute :size="20" /><span>执行开关</span><small>{{ !fresh || !snapshot?.status ? '未取得有效状态' : snapshot.status.safety.dispatchingEnabled ? '调度已启用' : '调度未启用' }}</small></div></footer>

    <ConsoleDialog v-if="showTaskDialog" title="任务运行详情 · 九步、条件与执行证据" wide @close="showTaskDialog=false"><TaskExecutionDetails v-if="detail" :detail="detail" :fresh="fresh" :now="now" /><p v-else class="empty-panel">{{ error || '正在读取任务详情…' }}</p></ConsoleDialog>

    <ConsoleDialog v-if="formOpen" title="创建调度任务" @close="formOpen=false"><p class="form-explanation">{{ (options?.explanation ?? actionMessage) || '正在查询后台创建条件…' }}</p><div class="task-type"><IconRoute :size="20" /><div><b>取满箱 → 倒料 → 返空箱</b><small>使用本项目现有九步流程</small></div></div><div class="creation-grid"><label>取料工位<select v-model="station" :disabled="requestUncertain||actionBusy"><option value="">请选择</option><option v-for="s in options?.stations??[]" :key="s.stationId" :value="s.stationId" :disabled="!s.available">{{ s.stationId }} · {{ s.pickupSite }}</option></select></label><label>车辆<select v-model="vehicle" :disabled="requestUncertain||actionBusy"><option value="">请选择</option><option v-for="v in options?.vehicles??[]" :key="v.vehicleId" :value="v.vehicleId" :disabled="!v.available">{{ v.label }}</option></select></label><label>优先级（0–100）<input v-model.number="priority" type="number" min="0" max="100" :disabled="requestUncertain||actionBusy" /></label><label>地图版本<input :value="options?.mapVersionId??'—'" readonly /></label></div><div class="inline-warning">{{ submissionEnabled?'提交后仍须经过后台核验，成功接收不等于执行完成。':'Web提交未启用，可核对条件及路线。' }}</div><div v-if="route" class="route-preview"><b>{{ route.message }}</b><p v-for="leg in route.legs" :key="leg.sequence">{{ leg.sequence }}. {{ leg.name }}：{{ leg.vendorPath||leg.logicalPath }}</p></div><p v-if="actionMessage" class="form-feedback" role="status">{{ actionMessage }}</p><template #footer><button @click="formOpen=false">取消</button><button :disabled="!options||!station||!vehicle||actionBusy||(source==='backend'&&!fresh)" @click="previewRoute">核对四段路线</button><button class="accent-button" :disabled="!canSubmit" @click="submitTask">{{ actionBusy?'处理中…':requestUncertain?'重试原申请':'提交后台' }}</button></template></ConsoleDialog>

    <ConsoleDialog v-if="selectedAlarm" title="告警详情" @close="selectedAlarm=null"><div class="detail-summary"><div><span>告警编号</span><b>{{ selectedAlarm.alarmId }}</b></div><div><span>关联对象</span><b>{{ selectedAlarm.sourceId }}</b></div><div><span>级别 / 状态</span><b>{{ alarmSeverity(selectedAlarm.severity) }} / {{ alarmStatus(selectedAlarm.status) }}</b></div><div><span>代码</span><b>{{ selectedAlarm.code }}</b></div></div><h3 class="subheading">{{ selectedAlarm.title }}</h3><p class="long-message">{{ selectedAlarm.message }}</p><p class="muted-note">首次发生：{{ shortTime(selectedAlarm.firstOccurredAt) }}<br>最近观察：{{ shortTime(selectedAlarm.lastObservedAt) }}<br>恢复时间：{{ shortTime(selectedAlarm.recoveredAt) }}</p><div class="inline-warning">此处只查看，确认及恢复按现有WPF流程处理。</div></ConsoleDialog>
    <ConsoleDialog v-if="selectedDevice" :title="selectedDevice.name+' · PLC设备状态'" wide @close="selectedDevice=null"><p class="form-explanation">{{ selectedDevice.summary }}</p><table class="dialog-table"><thead><tr><th>信号</th><th>值</th><th>质量</th><th>地址</th><th>更新时间</th></tr></thead><tbody><tr v-for="field in selectedDevice.fields" :key="field.name"><td>{{ field.name }}</td><td>{{ field.value }}</td><td>{{ field.quality }}</td><td>{{ field.address }}</td><td>{{ field.updatedAt }}</td></tr></tbody></table></ConsoleDialog>
    <ConsoleDialog v-if="selectedMap" title="地图版本信息" @close="selectedMap=null"><dl class="property-list map-properties"><dt>地图编号</dt><dd>{{ selectedMap.mapId }}</dd><dt>地图名称</dt><dd>{{ selectedMap.name }}</dd><dt>坐标系</dt><dd>{{ selectedMap.coordinateSystem }}</dd><dt>单位</dt><dd>{{ selectedMap.unit }}</dd><dt>运行版本</dt><dd>{{ selectedMap.activeVersionId??'—' }}</dd><dt>发布版本</dt><dd>{{ selectedMap.publishedVersionId??'—' }}</dd><dt>更新时间</dt><dd>{{ shortTime(selectedMap.updatedAt) }}</dd></dl><p class="muted-note">不在Web中修改或激活地图。</p></ConsoleDialog>
  </div>
</template>
