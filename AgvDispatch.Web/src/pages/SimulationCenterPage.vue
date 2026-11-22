<script setup lang="ts">
import { defineAsyncComponent, computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { IconPlayerPlay, IconPlayerStop, IconRefresh, IconDownload, IconFlask, IconRobot } from '@tabler/icons-vue';
import { ElSelectV2 } from 'element-plus';
import 'element-plus/es/components/select-v2/style/css';
import { simulationApi as defaultSimulationApi, simulationFresh, simulationGeometry, simulationLabel, loadCurrentSimulationMap, simulationTone, scenarioActive, simulationVehicleOriginsFromLive,
  simulationMetricLabel, simulationMetricValue, simulationMetricComparison, simulationReplayCars,
  simulationStationBindingsFromCatalog, simulationVehicleVisualState, simulationVehicleVisualColor,
  simulationVehicleVisualLabel, simulationObstacleRecoveryLabel,
  type SimulationStatus, type SimulationRun, type SimulationControls, type SimulationReport,
  type SimulationConfigureRequest, type SimulationMapDefinition, type SimulationCall,
  type SimulationRecoveryItem, type SimulationReplay, type SimulationStationConfiguration,
  type SimulationPlcSignalTable, type SimulationStationCatalogEntry,
  type ServicePlcPointTableSnapshot, type SimulationNode, type SimulationVehicle,
  type SimulationGuidance, type SimulationTimeoutRecovery, type SimulationTimeoutRecoveryResult } from '../simulation';
import { importProfControlRpj } from '../profcontrol-rpj';
import { actionStateLabel, equipmentDirectionLabel, industrialText } from '../operator-labels';
import { useDispatchStore } from '../stores/dispatch';
import type { Snapshot, TaskDetail, TaskPage, TaskRow, TrafficRequest } from '../types';
import { resourceHandovers } from '../traffic-display';

type SimulationStage = 'overview' | 'tasks' | 'environment' | 'scenario' | 'faults' | 'manual' | 'results';
const props = defineProps<{
  embedded?: {
    stage: SimulationStage;
    taskId?: string;
    request: typeof defaultSimulationApi;
    archive: (id: string) => Promise<Blob>;
    field: { snapshot: Snapshot | null; fresh: boolean; demoMode: boolean; api: (path: string) => Promise<unknown> };
  };
}>();
const emit = defineEmits<{ navigate: [stage: SimulationStage] }>();
const simulationApi = props.embedded?.request ?? defaultSimulationApi;
const SimulationThreeView = defineAsyncComponent(() => import('../components/SimulationThreeView.vue'));
const sceneDimension = ref<'2d' | '3d'>('2d');
const threeEdgeColors = computed(() => Object.fromEntries(geometry.value.edges.map(edge => {
  const classes = taskRouteEdgeClass(edge.id);
  const color = appliedBlockedEdges.value.includes(edge.id ?? '') ? '#ff8a6c' :
    classes['task-route-waiting'] ? '#ff8a6c' : classes['task-route-focus'] ? '#ffffff' :
    classes['task-route-current'] ? '#5ff5da' : classes['task-route-return'] ? '#ffc76b' :
    classes['task-route-future'] ? '#54cce3' : classes['task-route-completed'] ? '#4c938e' : '#3e8da8';
  return [edge.key, color];
})));
const overviewRoadHighlights = computed(() => {
  const highlights: Record<string, string> = {};
  const blocked = new Set(appliedBlockedEdges.value);
  const routeEdges = taskRouteLegs.value.flatMap(leg => leg.edgeIds);
  for (const id of new Set([...blocked, ...routeEdges])) {
    const classes = taskRouteEdgeClass(id);
    if (blocked.has(id) || classes['task-route-waiting']) highlights[id] = '#b64c2f';
    else if (classes['task-route-current'] || classes['task-route-focus']) highlights[id] = '#087968';
    else if (classes['task-route-return']) highlights[id] = '#a96c10';
    else if (classes['task-route-future']) highlights[id] = '#3456c8';
    else if (classes['task-route-completed']) highlights[id] = '#62918a';
  }
  return highlights;
});
const dispatchStore = props.embedded?.field ?? useDispatchStore();
const route = useRoute(), router = useRouter();
const status = ref<SimulationStatus | null>(null), error = ref(''), actionError = ref(''), notice = ref('');
let metadataLoaded = false, lastMetadataRefresh = 0;
const MAX_SIMULATION_STATIONS = 305;
const reading = ref(false), busy = ref(false), connected = ref(false), now = ref(Date.now());
const selectedVehicle = ref('');
const newVehicleCount = ref(2);
const blockedEdgeIds = ref<string[]>([]);
const speedScale = ref(0.75), readyDelay = ref(0), doneDelay = ref(5);
const run = ref<SimulationRun>({ name: 'normal-cycle', rounds: 1, intervalSeconds: 1, timeoutSeconds: 600 });
const plcFault = ref<'ready' | 'done'>('ready'), plcFaultHold = ref(10);
const roadBlockHold = ref(5);
const restartStage = ref<'pickup' | 'delivery'>('pickup');
const mapDefinition = ref<SimulationMapDefinition | null>(null), mapFileName = ref('');
const liveMapIdentity = ref('');
const showCadReference = ref(false);
const mapViewMode = ref<'all' | 'vehicles'>('all');
const useFieldVehiclePositions = ref(true);
const mapIdentityDraft = ref<SimulationConfigureRequest['mapIdentity']>(undefined);
const plcTableText = ref(''), plcTableFileName = ref(''), plcTableSha256 = ref('');
const plcTableMetadata = ref<SimulationPlcSignalTable | null>(null);
const plcStationCatalog = ref<SimulationStationCatalogEntry[]>([]);
const plcTableReading = ref(false);
const stationDrafts = ref<SimulationStationConfiguration[]>([
  { stationId: 'CNC-001', pickupSite: '', signalSource: 'verified' },
  { stationId: 'CNC-002', pickupSite: '', signalSource: 'verified' }
]);
const stationFilter = ref('');
const stationPage = ref(1);
const stationsPerPage = 12;
const filteredStations = computed(() => {
  const search = stationFilter.value.trim().toLowerCase();
  return stationDrafts.value.map((station, index) => ({ station, index })).filter(({ station }) =>
    station.stationId.toLowerCase().includes(search) || station.pickupSite.toLowerCase().includes(search));
});
const stationPageCount = computed(() => Math.max(1, Math.ceil(filteredStations.value.length / stationsPerPage)));
const visibleStations = computed(() => {
  const offset = (stationPage.value - 1) * stationsPerPage;
  return filteredStations.value.slice(offset, offset + stationsPerPage);
});
watch(stationFilter, () => { stationPage.value = 1; }, { flush: 'sync' });
watch(stationPageCount, count => { stationPage.value = Math.min(stationPage.value, count); });
const waitingSite = ref(''), dumpingSite = ref('');
const schemeName = ref(''), selectedScheme = ref(''), selectedHistory = ref(''), comparisonId = ref('');
const selectedReport = ref<SimulationReport | null>(null), comparisonReport = ref<SimulationReport | null>(null);
const replay = ref<SimulationReplay | null>(null), replayIndex = ref(0), replayPlaying = ref(false), historyReading = ref(false);
const manualGuide = ref<SimulationGuidance | null>(null), manualRecovery = ref<SimulationTimeoutRecovery | null>(null);
const manualRecoveryReading = ref(false), manualRecoveryError = ref('');
const manualRecoveryReason = ref('仿真中已核对车辆、载荷、PLC和原任务，条件一致后继续');
const manualVehicleConfirmed = ref(false), manualLoadConfirmed = ref(false), manualPlcConfirmed = ref(false);
const simulationTasks = ref<TaskRow[]>([]), simulationTaskPage = ref<TaskPage | null>(null);
const simulationTaskFilter = ref<'All' | 'Active' | 'Completed' | 'Failed'>('All');
const simulationTaskSearch = ref('');
const selectedTaskId = ref(''), selectedTaskDetail = ref<TaskDetail | null>(null);
const taskReading = ref(false), taskDetailReading = ref(false), taskError = ref('');
const selectedTaskRouteStepId = ref('');
const selectedTaskRouteCommandId = ref('');
let taskDetailRequestId = 0;
const taskRuntimeSnapshot = ref<Snapshot | null>(null), taskSnapshotError = ref('');
const taskSnapshotFetchedAt = ref(0);
const reportTaskDetailId = ref(''), reportTaskDetail = ref<TaskDetail | null>(null);
const reportTaskReading = ref(false), reportTaskError = ref('');
const recordingVideo = ref(false), videoProgress = ref(0);
const additionalWaitingSites = ref<string[]>([]);
const downstreamAdmissionCapacity = ref(0);
const inspectionSite = ref(''), dumperResourceId = ref('');
const customVehicles = ref([
  { id: 'AGV-SIM-01', initialSite: '', radiusMeters: 0.49, lengthMeters: 0.8, widthMeters: 0.52, initialLoaded: false, batteryPercent: 86, speedScale: 0.75,
    accelerationMetersPerSecondSquared: 0.6, operationSeconds: 2, travelCostPercentPerMeter: 0.01, idleCostPercentPerHour: 0.1,
    voltageCurve: [{ percent: 0, voltage: 21 }, { percent: 100, voltage: 26 }], chargingGainPercentPerHour: 20 },
  { id: 'AGV-SIM-02', initialSite: '', radiusMeters: 0.49, lengthMeters: 0.8, widthMeters: 0.52, initialLoaded: false, batteryPercent: 86, speedScale: 0.75,
    accelerationMetersPerSecondSquared: 0.6, operationSeconds: 2, travelCostPercentPerMeter: 0.01, idleCostPercentPerHour: 0.1,
    voltageCurve: [{ percent: 0, voltage: 21 }, { percent: 100, voltage: 26 }], chargingGainPercentPerHour: 20 }
]);
type VehicleDraft = (typeof customVehicles.value)[number];
const callCount = ref(2), callInterval = ref(2), callBatchSize = ref(2), callPattern = ref('CNC-001,CNC-002'), callVehicleId = ref('');
const recoveryMaterial = ref('SIM-ONLY：原车辆、箱体及任务归属已按当前设备快照核对');
const recoveryReason = ref('隔离仿真中核对原运输机械结果和PLC周期后继续原任务');
const recoveryDecision = ref(10);
const vehicleConfirmed = ref(false), loadConfirmed = ref(false), plcConfirmed = ref(false);
const protocolText = ref(JSON.stringify({
  name: 'loopback-protocol', transport: 'tcp', port: 0, framing: 'line-json', state: { ready: true },
  rules: [{ id: 'ping', match: { command: 'ping' }, response: { ok: true, command: '{{request.command}}', count: '{{counters.ping}}' } }]
}, null, 2));
const protocolFile = ref('');
const equipmentTravelSeconds = ref(2), equipmentDoorSeconds = ref(1);
const signalSetText = ref('{"call":true}'), signalScheduleText = ref('[{"id":"delayed-stop","afterMs":2000,"set":{"call":false}}]');
let disposed = false, poll: ReturnType<typeof setInterval> | undefined, tick: ReturnType<typeof setInterval> | undefined;
let controller: AbortController | undefined;
const cars = computed(() => status.value?.device?.cars ?? {});
const vehicleIds = computed(() => Object.keys(cars.value));
const vehicle = computed(() => cars.value[selectedVehicle.value]);
const fresh = computed(() => connected.value && simulationFresh(status.value, now.value));
const active = computed(() => scenarioActive(status.value?.scenario));
const available = computed(() => connected.value && !!status.value?.enabled && !busy.value && !reading.value);
const canControl = computed(() => available.value && fresh.value);
const starting = computed(() => status.value?.phase === 'starting');
const cleanupRequired = computed(() => connected.value && status.value?.phase === 'failed');
const plcFeedback = computed(() => status.value?.device?.simulationControl);
const appliedBlockedEdges = computed(() => vehicle.value?.simulationControl?.blockedEdges ?? []);
const blockableEdges = computed(() => (status.value?.map?.edges ?? []).filter(edge => !!edge.id));
const geometry = computed(() => simulationGeometry(status.value?.map?.nodes ?? [], status.value?.map?.edges ?? [], cars.value));
const cadBackground = computed(() => simulationCadBackground(status.value?.map, geometry.value));
const mapAreas = computed(() => simulationAreaOverlays(status.value?.map, geometry.value));
const mapViewBox = computed(() => {
  if (mapViewMode.value !== 'vehicles' || !geometry.value.cars.length) return '0 0 800 430';
  const xs = geometry.value.cars.map(car => car.x);
  const ys = geometry.value.cars.map(car => car.y);
  const minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys);
  const width = Math.min(760, Math.max(300, (maxX - minX) * 1.8));
  const height = width / (800 / 430);
  const centerX = (minX + maxX) / 2, centerY = (minY + maxY) / 2;
  return `${Math.max(0, Math.min(800 - width, centerX - width / 2))} ${Math.max(0, Math.min(430 - height, centerY - height / 2))} ${width} ${height}`;
});
const denseMap = computed(() => geometry.value.nodes.length > 120);
const progress = computed(() => Math.min(100, Math.max(0, (status.value?.scenario?.completedRounds ?? 0) / Math.max(1, status.value?.scenario?.totalRounds ?? 1) * 100)));
const report = computed(() => selectedReport.value ?? status.value?.report);
const historicalReport = computed(() => !!report.value?.id && !!status.value?.scenario?.id && report.value.id !== status.value.scenario.id);
const manualReadOnly = computed(() => selectedReport.value !== null);
const manualGuidanceRows = computed<SimulationGuidance[]>(() => {
  if (!selectedReport.value) return status.value?.recoveryGuidance ?? [];
  const tasks = new Map((selectedReport.value.taskSummary ?? []).map(task => [task.taskId, task]));
  return (selectedReport.value.waitingChain ?? []).filter(row => !!row.taskId).map(row => {
    const task = tasks.get(row.taskId!);
    return {
      taskId: row.taskId!, vehicleId: row.vehicleId, stationId: row.stationId,
      stepNumber: task?.currentStepNumber, stepName: task?.currentStepName || row.stepName,
      blockedBy: row.blockedBy || row.waitReason || task?.waitingExplanation,
      restoreAction: row.restoreAction || '按当轮任务、车辆、载荷、PLC和路权证据确定现场处置；历史记录不能直接恢复。',
      expectedFeedback: row.expectedFeedback || '恢复后原任务编号不变，现场反馈与当轮任务一致。',
      site: row.site, x: row.x, y: row.y, taskStage: task?.stage, currentStepStatus: task?.currentStepStatus,
      historical: true,
    };
  });
});
const scenarioNames: Record<string, string> = { 'normal-cycle': '完整业务循环', 'simultaneous-calls': '多工位同时呼叫',
  'custom-calls': '自定义呼叫计划', 'dispatch-backlog': '正式调度持续排队测试', 'disconnect-reconnect': '车辆断连与重连', 'plc-delay': 'PLC 反馈延迟',
  'plc-recovery': 'PLC卡位与恢复', 'service-restart': '后台重启与恢复屏障', 'resume-recovery': '恢复后继续原任务',
  'vendor-cancel-return': '车端返程中途取消', 'response-loss': '运输命令回包丢失', 'road-block-recovery': '道路临时阻塞后恢复',
  'obstacle-auto-detour': '前方障碍自动改道',
  'obstacle-auto-safety-return': '道路中段自动退回安全站',
  'obstacle-timeout-pickup': '取料途中受阻并超时',
  'obstacle-timeout-fullbox': '满箱运输受阻并超时',
  'obstacle-timeout-return': '空箱返程受阻并超时',
  'obstacle-timeout-midroad': '道路中段受阻并超时',
  'obstacle-timeout-safe-site': '安全点转移后继续',
  'obstacle-timeout-manual': '无法恢复转人工处置' };
const metrics = computed(() => Object.entries(report.value?.metrics ?? {}).filter(([, value]) => value !== null && typeof value !== 'object'));
const metricComparison = computed(() => simulationMetricComparison(report.value?.metrics, comparisonReport.value?.metrics));
const firstFailure = computed(() => typeof report.value?.firstFailure === 'string' ? report.value.firstFailure :
  report.value?.firstFailure?.message || report.value?.checks?.find(check => ['fail', 'failed', 'error'].includes(check.status))?.detail);
const versionComparison = computed(() => status.value?.versionComparison);
const configuredStationIds = computed(() => (status.value?.running ? status.value.configuration?.stations ?? stationDrafts.value : stationDrafts.value).map(station => station.stationId));
const replayFrame = computed(() => replay.value?.timeline[replayIndex.value]);
const replayGeometry = computed(() => simulationGeometry((replay.value?.map ?? status.value?.map)?.nodes ?? [],
  (replay.value?.map ?? status.value?.map)?.edges ?? [], simulationReplayCars(replayFrame.value)));
const replayCadBackground = computed(() => simulationCadBackground(replay.value?.map ?? status.value?.map, replayGeometry.value));
const replayMapAreas = computed(() => simulationAreaOverlays(replay.value?.map ?? status.value?.map, replayGeometry.value));
const denseReplay = computed(() => replayGeometry.value.nodes.length > 120);
const singleRoundScenarios = new Set<SimulationRun['name']>(['service-restart', 'resume-recovery', 'vendor-cancel-return',
  'response-loss', 'plc-recovery', 'road-block-recovery', 'obstacle-auto-detour',
  'obstacle-auto-safety-return', 'obstacle-timeout-pickup',
  'obstacle-timeout-fullbox', 'obstacle-timeout-return', 'obstacle-timeout-midroad',
  'obstacle-timeout-safe-site', 'obstacle-timeout-manual']);
const obstacleTimeoutScenarios = new Set<SimulationRun['name']>(['obstacle-timeout-pickup',
  'obstacle-timeout-fullbox', 'obstacle-timeout-return', 'obstacle-timeout-midroad',
  'obstacle-timeout-safe-site', 'obstacle-timeout-manual']);
const runValid = computed(() => Number.isInteger(run.value.rounds) && run.value.rounds >= 1 && run.value.rounds <= 100 &&
  (!singleRoundScenarios.has(run.value.name) || run.value.rounds === 1) &&
  Number.isFinite(run.value.intervalSeconds) && run.value.intervalSeconds >= 0 && run.value.intervalSeconds <= 60 &&
  Number.isFinite(run.value.timeoutSeconds) && run.value.timeoutSeconds >= 30 && run.value.timeoutSeconds <= 86400);
const currentTask = computed(() => vehicle.value?.current_task || '当前无运输命令');
const snapshotTime = computed(() => stamp(status.value?.device?.time));
const mapNodes = computed(() => (mapDefinition.value?.nodes ?? []).filter(node => node.enabled !== false));
const mapResources = computed(() => (mapDefinition.value?.resources ?? []).filter(resource => (resource.boundary?.length ?? 0) >= 3));
const configurationLocked = computed(() => busy.value || !!status.value?.running || starting.value || active.value);
const compareMapNumber = new Intl.Collator(undefined, { numeric: true }).compare;
const siteTypes = {
  WaitingPoint: '等待位',
  Station: '工位／设备停车点',
  ChargingPoint: '充电位',
  TrafficStop: '道路安全停点',
  Intersection: '路口',
  Waypoint: '普通航点',
  Other: '未标注类型的点位',
};
const resourceTypes = {
  Bound: '倒料停车点已关联',
  Station: '工位／设备区',
  NarrowAisle: '窄通道',
  SingleLane: '单行区',
  Intersection: '路口',
  ChargingBay: '充电区',
  Other: '未标注类型的区域',
};
type MapOption = { value: string; label: string };
function groupMapOptions<T>(
  items: readonly T[],
  labels: Record<string, string>,
  groupKey: (item: T) => string,
  toOption: (item: T) => MapOption,
) {
  const groups = new Map(Object.entries(labels).map(([key, label]) => [key, { label, options: [] as MapOption[] }]));
  for (const item of items) (groups.get(groupKey(item)) ?? groups.get('Other'))?.options.push(toOption(item));
  return [...groups.values()].filter(group => group.options.length).map(group => ({
    label: group.label, options: group.options.sort((a, b) => compareMapNumber(a.label, b.label)),
  }));
}
const siteOptions = computed(() => groupMapOptions(mapNodes.value, siteTypes,
  node => node.type === 'WaitingPoint' && node.resourceIds?.includes(`SIM-STOP-${node.id}`) ? 'TrafficStop' : node.type ?? 'Other',
  node => ({ value: node.id, label: node.agvSiteId && node.agvSiteId !== node.id ? `${node.agvSiteId} · ${node.id}` : node.id })));
const extraWaitingOptions = computed(() => siteOptions.value.map(group => ({
  ...group, options: group.options.filter(option => option.value !== waitingSite.value && option.value !== dumpingSite.value),
})).filter(group => group.options.length));
const dumperResourceOptions = computed(() => {
  const bound = new Set(mapNodes.value.find(node => node.id === dumpingSite.value)?.resourceIds ?? []);
  return groupMapOptions(mapResources.value, resourceTypes,
    resource => bound.has(resource.id) ? 'Bound' : resource.type ?? 'Other',
    resource => ({ value: resource.id, label: `${resource.id} · 容量 ${resource.capacity ?? 1}` }));
});
const recovery = computed(() => status.value?.recovery);
const manualCar = computed(() => manualGuide.value?.vehicleId ? cars.value[manualGuide.value.vehicleId] ?? null : null);
const manualRecoveryReady = computed(() => manualRecovery.value?.outcome === 'ReadyForControlledDetour' &&
  !manualRecovery.value.sendAttempted && (manualRecovery.value.candidatePath?.length ?? 0) >= 2);
const manualRecoveryCanExecute = computed(() => manualRecoveryReady.value && !!manualRecovery.value?.taskId &&
  Number.isInteger(manualRecovery.value.revision) && manualVehicleConfirmed.value &&
  manualLoadConfirmed.value && manualPlcConfirmed.value && manualRecoveryReason.value.trim().length >= 10 &&
  fresh.value && !busy.value);
const selectedSchemeRecord = computed(() => (status.value?.schemes ?? []).find(scheme => scheme.id === selectedScheme.value));
const selectedSchemeReady = computed(() => !!selectedSchemeRecord.value?.plcTableName && !!selectedSchemeRecord.value?.plcTableSha256);
const protocol = computed(() => status.value?.protocol);
const equipment = computed(() => status.value?.equipment);
const taskCounts = computed(() => {
  const summary = simulationTaskPage.value?.summary;
  if (summary) return summary;
  const rows = simulationTasks.value;
  return {
    total: rows.length,
    running: rows.filter(task => ['Running', 'Dispatching', 'InProgress'].includes(task.stage)).length,
    waiting: rows.filter(task => ['Created', 'WaitingForVehicle', 'WaitingForDumper', 'WaitingForPlcConfirmation'].includes(task.stage)).length,
    completed: rows.filter(task => task.stage === 'Completed').length,
    cancelled: rows.filter(task => task.stage === 'Cancelled').length,
    failed: rows.filter(task => ['Faulted', 'Failed', 'CancellationReview'].includes(task.stage)).length,
    other: 0
  };
});
function taskWaitingRequest(taskId: string) {
  return (taskRuntimeSnapshot.value?.traffic?.requests ?? [])
    .filter(request => request.taskId === taskId && request.state === 'Waiting')
    .sort((left, right) => Date.parse(right.updatedAt ?? '') - Date.parse(left.updatedAt ?? ''))[0];
}
function taskWaitingSeconds(taskId: string) {
  const value = taskWaitingRequest(taskId)?.waitingSeconds;
  return value !== null && value !== undefined && Number.isFinite(value) ? Math.max(0, Math.floor(value)) : null;
}
type TaskRouteLegKind = 'completed' | 'current' | 'future';
type TaskRouteLeg = {
  stepId: string; commandId: string; sequence: number; kind: TaskRouteLegKind;
  isCurrent: boolean; isReturn: boolean; source: string; destination: string;
  nodeIds: string[]; edgeIds: string[];
};
const taskRouteLegs = computed<TaskRouteLeg[]>(() => {
  const detail = selectedTaskDetail.value;
  if (!detail) return [];
  const steps = new Map(detail.steps.filter(step => !!step.stepId).map(step => [step.stepId!, step]));
  return (detail.commands ?? []).flatMap(command => {
    const step = steps.get(command.stepId);
    if (!step?.stepId || !command.route?.edgeIds?.length) return [];
    const active = step.isCurrent || ['Ready', 'Executing', 'WaitingForTraffic'].includes(step.status);
    const kind: TaskRouteLegKind = step.status === 'Completed' ? 'completed' : active ? 'current' : 'future';
    const nodeIds = command.route.nodeIds ?? [];
    const sites = command.route.vendorSites ?? [];
    return [{
      stepId: step.stepId, commandId: command.commandId, sequence: step.sequence, kind,
      isCurrent: active, isReturn: step.stepType === 'ReturnEmptyBox',
      source: sites[0] || nodeIds[0] || step.sourceSite || '—',
      destination: sites.at(-1) || nodeIds.at(-1) || step.destinationSite || '—',
      nodeIds, edgeIds: command.route.edgeIds
    }];
  }).sort((left, right) => left.sequence - right.sequence);
});
const selectedTaskCurrentStep = computed(() => {
  const steps = selectedTaskDetail.value?.steps ?? [];
  return steps.find(step => step.isCurrent) ??
    steps.find(step => ['Ready', 'Executing', 'WaitingForTraffic'].includes(step.status)) ?? null;
});
const selectedEvidenceStep = computed(() => {
  const steps = selectedTaskDetail.value?.steps ?? [];
  return steps.find(step => evidenceStepKey(step) === selectedTaskRouteStepId.value) ??
    selectedTaskCurrentStep.value ??
    steps.find(step => step.stepId === taskRouteLegs.value[0]?.stepId) ?? steps[0];
});
const selectedStepRoutes = computed(() =>
  taskRouteLegs.value.filter(leg => leg.stepId === selectedEvidenceStep.value?.stepId));
const selectedTaskRouteLeg = computed(() =>
  selectedStepRoutes.value.find(leg => leg.commandId === selectedTaskRouteCommandId.value) ??
  selectedStepRoutes.value.at(-1));
const selectedRouteExistsOnMap = computed(() => {
  const leg = selectedTaskRouteLeg.value;
  const map = status.value?.map;
  if (!leg || !map) return false;
  const nodes = new Set(map.nodes.filter(node => node.enabled !== false).map(node => node.id));
  const edges = new Map(map.edges.filter(edge => edge.enabled !== false).map(edge => [edge.id, edge]));
  return leg.nodeIds.length === leg.edgeIds.length + 1 && leg.nodeIds.every(id => nodes.has(id)) &&
    leg.edgeIds.every((id, index) => {
      const edge = edges.get(id);
      const from = leg.nodeIds[index], to = leg.nodeIds[index + 1];
      return edge && ((edge.from === from && edge.to === to) || (edge.to === from && edge.from === to));
    });
});
const evidenceRouteColor = computed(() => selectedTaskRouteLeg.value?.isReturn ? '#a96c10' : '#087968');
const evidenceRoadHighlights = computed(() => Object.fromEntries(
  (selectedRouteExistsOnMap.value ? selectedTaskRouteLeg.value?.edgeIds ?? [] : [])
    .map(id => [id, evidenceRouteColor.value])));
const evidenceRouteEndpoints = computed(() => {
  const leg = selectedRouteExistsOnMap.value ? selectedTaskRouteLeg.value : undefined;
  if (!leg) return [];
  const start = leg.nodeIds[0]!, end = leg.nodeIds.at(-1)!;
  return start === end ? [{ nodeId: start, label: '起终点', site: leg.source }] : [
    { nodeId: start, label: '起点', site: leg.source },
    { nodeId: end, label: '终点', site: leg.destination }
  ];
});
const archivedTaskEvidence = computed(() => !status.value?.running ||
  selectedTaskDetail.value?.evidenceSource?.startsWith('saved-report') ||
  ['Completed', 'Cancelled'].includes(selectedTaskDetail.value?.task.stage ?? ''));
const evidenceStepHandshakes = computed(() => selectedTaskDetail.value?.handshakes?.filter(
  handshake => handshake.stepId === selectedEvidenceStep.value?.stepId) ?? []);
const evidenceStepCommands = computed(() => selectedTaskDetail.value?.commands?.filter(
  command => command.stepId === selectedEvidenceStep.value?.stepId) ?? []);
const selectedTaskTrafficRequest = computed(() => {
  const detail = selectedTaskDetail.value;
  if (!detail) return null;
  const requests = (taskRuntimeSnapshot.value?.traffic?.requests ?? [])
    .filter(request => request.taskId === detail.task.taskId && request.stepId === selectedTaskCurrentStep.value?.stepId)
    .sort((left, right) => Date.parse(right.updatedAt ?? '') - Date.parse(left.updatedAt ?? ''));
  return requests.find(request => request.state === 'Waiting') ?? requests[0] ?? null;
});
const selectedTaskWaitBlockers = computed(() => selectedTaskTrafficRequest.value?.blockers ?? []);
const selectedTaskWaitSeconds = computed(() => {
  const seconds = selectedTaskTrafficRequest.value?.waitingSeconds;
  return typeof seconds === 'number' && Number.isFinite(seconds) ? Math.max(0, Math.floor(seconds)) : null;
});
const selectedTaskHandovers = computed(() => resourceHandovers(taskRuntimeSnapshot.value, selectedTaskTrafficRequest.value));
const selectedTaskWaitGroups = computed(() => {
  const groups = new Map<string, {
    vehicleId: string; taskId: string; resources: string[]; heartbeatExpired: boolean;
  }>();
  for (const blocker of selectedTaskWaitBlockers.value) {
    const key = `${blocker.vehicleId}|${blocker.taskId}`;
    const group = groups.get(key) ?? {
      vehicleId: blocker.vehicleId, taskId: blocker.taskId,
      resources: [], heartbeatExpired: false
    };
    if (!group.resources.includes(blocker.resourceId)) group.resources.push(blocker.resourceId);
    group.heartbeatExpired ||= blocker.heartbeatExpired;
    groups.set(key, group);
  }
  return [...groups.values()];
});
function routeActionLabel(action?: string | null) {
  return ({ JackLoad: '顶升取箱', JackUnload: '放下空箱', Wait: '导航/等待' } as Record<string, string>)[action ?? ''] ??
    (action ? industrialText(action) : '运输移动');
}
function routeResourceLabel(resourceId?: string | null) {
  if (!resourceId) return '未知资源';
  if (resourceId.startsWith('AUTO-CORRIDOR-')) {
    const map = status.value?.map;
    const edges = (map?.edges ?? []).filter(edge => edge.resourceIds?.includes(resourceId));
    const nodes = new Map((map?.nodes ?? []).map(node => [node.id, node.agvSiteId || node.id]));
    const endpoints = [...new Set(edges.flatMap(edge => [nodes.get(edge.from) || edge.from, nodes.get(edge.to) || edge.to]))];
    if (endpoints.length === 2) return `${endpoints[0]} ↔ ${endpoints[1]} 路段互斥路权`;
    if (endpoints.length > 2) return `${endpoints.slice(0, 3).join('/')} 等路段组合互斥路权`;
    return `路段互斥路权 ${resourceId.slice(-4).toUpperCase()}`;
  }
  if (resourceId.startsWith('TRAFFIC-')) return `${resourceId.replace('TRAFFIC-', '')} 交叉口路权`;
  if (resourceId.startsWith('STANDBY-')) return `${resourceId.replace('STANDBY-', '')} 候车位`;
  if (resourceId.startsWith('WAIT-')) return `${resourceId.replace('WAIT-', '')} 等待区`;
  if (resourceId.startsWith('CNC-')) return `${resourceId.split('-').slice(0, 2).join('-')} 取箱位`;
  if (resourceId.startsWith('DUMP-')) return '终端倒料区';
  return resourceId;
}
function taskRouteEdgeClass(edgeId?: string | null) {
  if (!edgeId) return {};
  const legs = taskRouteLegs.value.filter(leg => leg.edgeIds.includes(edgeId));
  if (!legs.length) return {};
  const focused = selectedTaskRouteLeg.value?.edgeIds.includes(edgeId) ?? false;
  const current = legs.some(leg => leg.isCurrent);
  const future = legs.some(leg => leg.kind === 'future');
  const returning = focused ? !!selectedTaskRouteLeg.value?.isReturn :
    legs.some(leg => leg.isReturn && leg.kind !== 'completed');
  const waiting = !!selectedTaskTrafficRequest.value &&
    selectedTaskTrafficRequest.value.state === 'Waiting' &&
    legs.some(leg => leg.isCurrent);
  return {
    'task-route': true,
    'task-route-focus': focused,
    'task-route-waiting': !focused && waiting,
    'task-route-current': !focused && current,
    'task-route-return': focused ? !!selectedTaskRouteLeg.value?.isReturn : !current && returning,
    'task-route-future': !focused && !current && !returning && future,
    'task-route-completed': !focused && !current && !returning && !future,
    'task-route-dimmed': !!selectedTaskRouteStepId.value && !focused
  };
}
function taskRouteNodeClass(nodeId?: string | null) {
  if (!nodeId) return {};
  const legs = taskRouteLegs.value.filter(leg => leg.nodeIds.includes(nodeId));
  if (!legs.length) return {};
  const focused = selectedTaskRouteLeg.value?.nodeIds.includes(nodeId) ?? false;
  const waiting = !!selectedTaskTrafficRequest.value &&
    selectedTaskTrafficRequest.value.state === 'Waiting' &&
    legs.some(leg => leg.isCurrent);
  return {
    'task-node': true,
    'task-node-focus': focused,
    'task-node-waiting': !focused && waiting,
    'task-node-current': !focused && legs.some(leg => leg.isCurrent),
    'task-node-return': !focused && legs.some(leg => leg.isReturn && leg.kind !== 'completed'),
    'task-node-completed': !focused && legs.every(leg => leg.kind === 'completed'),
    'task-node-dimmed': !!selectedTaskRouteStepId.value && !focused
  };
}
function evidenceStepKey(step: TaskDetail['steps'][number]) {
  return step.stepId || `sequence-${step.sequence}`;
}
function selectEvidenceStep(step: TaskDetail['steps'][number]) {
  selectedTaskRouteStepId.value = evidenceStepKey(step);
  selectedTaskRouteCommandId.value = '';
}
function trafficWaitingText(request?: TrafficRequest | null) {
  if (!request) return '当前没有从后台读取到该步骤的路权申请。';
  if (request.state === 'Waiting') {
    if (request.waitingSeconds !== null && request.waitingSeconds !== undefined && Number.isFinite(request.waitingSeconds))
      return `已等待 ${Math.floor(request.waitingSeconds)} 秒：${industrialText(request.reason)}`;
    return industrialText(request.reason);
  }
  if (request.state === 'Leased') return `路权已取得，可以继续行驶。${industrialText(request.reason)}`;
  if (request.state === 'Released') return `该步骤路权已释放：${industrialText(request.reason)}`;
  return industrialText(request.reason || `路权状态：${request.state}`);
}
type TaskTimelineEntry = { id: string; occurredAt: string; title: string; summary: string; source: string };
const taskTimeline = computed(() => {
  const detail = selectedTaskDetail.value;
  if (!detail) return [];
  const entries: TaskTimelineEntry[] = [];
  const add = (id: string | undefined, occurredAt: string | undefined, title: string, summary: string, source: string) => {
    if (!occurredAt || !Number.isFinite(Date.parse(occurredAt))) return;
    entries.push({ id: id || `${occurredAt}-${title}`, occurredAt, title, summary, source });
  };
  add(`${detail.task.taskId}-created`, detail.task.createdAt, '任务创建', `${detail.task.stationId} 工位呼叫进入正式调度队列`, '任务');
  for (const event of detail.executionTimeline ?? [])
    add(event.id, event.occurredAt, industrialText(event.title), industrialText(event.summary), event.source);
  for (const step of detail.steps) {
    add(step.stepId ? `${step.stepId}-start` : undefined, step.startedAt, `第${step.sequence}步开始`, taskStepLabel(step.stepType), '步骤');
    add(step.stepId ? `${step.stepId}-done` : undefined, step.completedAt, `第${step.sequence}步完成`, taskStepLabel(step.stepType), '步骤');
  }
  for (const handshake of detail.handshakes ?? []) {
    add(`${handshake.cycleId}-start`, handshake.startedAt, 'PLC握手开始', handshakePhaseLabel(handshake.phase), 'PLC');
    add(`${handshake.cycleId}-update`, handshake.updatedAt,
      handshake.pendingSignal ? `PLC等待${plcSignalLabel(handshake.pendingSignal)}` : 'PLC握手状态更新',
      handshake.reason, 'PLC');
  }
  if (detail.waitingExplanation)
    add(`${detail.task.taskId}-waiting`, detail.task.updatedAt, '当前等待原因', detail.waitingExplanation, '等待');
  const unsatisfied = (detail.plcConditions ?? []).filter(signal => !signal.satisfied);
  if (unsatisfied.length)
    add(`${detail.task.taskId}-plc-waiting`, detail.task.updatedAt, 'PLC条件尚未满足',
      unsatisfied.slice(0, 6).map(signal => `${plcSignalLabel(signal.variableName)}：${plcConditionLabel(signal.currentValue, signal.expectedValue)}`).join('；'),
      'PLC');
  if (detail.resources?.length)
    add(`${detail.task.taskId}-resources`, detail.task.updatedAt, '道路与设备占用',
      detail.resources.map(resource => `${resource.resourceId} 由 ${resource.vehicleId} 持有`).join('；'), '路权');
  const unique = new Map<string, TaskTimelineEntry>();
  for (const entry of entries) {
    const signature = `${entry.occurredAt}|${entry.title}|${entry.summary}`;
    if (!unique.has(signature)) unique.set(signature, entry);
  }
  return [...unique.values()].sort((left, right) => Date.parse(left.occurredAt) - Date.parse(right.occurredAt));
});
const denseLabelStations = new Set([1, 25, 50, 75, 100, 125, 150, 175, 200, 225, 250, 275, 300, 305]);
type MapGeometry = ReturnType<typeof simulationGeometry>;
function simulationCadBackground(map: SimulationMapDefinition | null | undefined, scene: MapGeometry) {
  const annotation = map?.areaAnnotations?.find(item => item.sourceType === 'CadRasterBackground' && item.sourceJson);
  if (!annotation?.sourceJson) return null;
  try {
    const metadata = JSON.parse(annotation.sourceJson) as {
      asset?: string; opacity?: number;
      viewBox?: { minX?: number; minY?: number; width?: number; height?: number };
    };
    const box = metadata.viewBox;
    if (!metadata.asset || !box || ![box.minX, box.minY, box.width, box.height].every(Number.isFinite) || Number(box.width) <= 0 || Number(box.height) <= 0) return null;
    const left = Number(box.minX) / 1000, right = (Number(box.minX) + Number(box.width)) / 1000;
    const top = (Number(box.minY) + Number(box.height)) / 1000, bottom = Number(box.minY) / 1000;
    const topLeft = scene.position({ x: left, y: top }), bottomRight = scene.position({ x: right, y: bottom });
    return { href: '/' + metadata.asset.replace(/^\/+/, ''), x: topLeft.x, y: topLeft.y,
      width: bottomRight.x - topLeft.x, height: bottomRight.y - topLeft.y,
      opacity: Number.isFinite(metadata.opacity) ? Math.min(.4, Math.max(.05, Number(metadata.opacity))) : .2 };
  } catch { return null; }
}
function simulationAreaOverlays(map: SimulationMapDefinition | null | undefined, scene: MapGeometry) {
  return (map?.areaAnnotations ?? []).flatMap(item => {
    if (item.sourceType !== 'CadSimulationEvidence' || !Array.isArray(item.boundary)) return [];
    let boundary: { x: number; y: number }[] = [];
    for (const value of item.boundary) {
      if (!value || typeof value !== 'object' || !('x' in value) || !('y' in value)) continue;
      const x = Number(value.x), y = Number(value.y);
      if (Number.isFinite(x) && Number.isFinite(y)) boundary.push({ x, y });
    }
    if (boundary.length < 3) return [];
    const referenceBoundary = item.id === 'SIM-AREA-WAIT'
      ? { left: 983.7, right: 988, bottom: -430.55, top: -428 }
      : item.id === 'CAD-TERMINAL-PLATFORM'
        ? { left: 984, right: 989.2, bottom: -432.126, top: -430.726 }
        : item.id === 'SIM-AREA-CHARGE'
          ? { left: 980.5, right: 983.95, bottom: -433.83, top: -432.35 }
          : null;
    if (referenceBoundary)
      boundary = [
        { x: referenceBoundary.left, y: referenceBoundary.bottom },
        { x: referenceBoundary.right, y: referenceBoundary.bottom },
        { x: referenceBoundary.right, y: referenceBoundary.top },
        { x: referenceBoundary.left, y: referenceBoundary.top }
      ];
    const points = boundary.map(scene.position);
    const labelX = points.reduce((sum, point) => sum + point.x, 0) / points.length;
    const labelY = points.reduce((sum, point) => sum + point.y, 0) / points.length;
    const override = item.id === 'SIM-AREA-WAIT'
      ? { text: '等待区', strokeColor: '#22c55e', fillColor: '#16a34a', fillOpacity: .08, radius: 9 }
      : item.id === 'CAD-TERMINAL-PLATFORM'
        ? { text: '倒料区', strokeColor: '#22d3ee', fillColor: '#0891b2', fillOpacity: .09, radius: 12 }
        : item.id === 'SIM-AREA-CHARGE'
          ? { text: '充电区', strokeColor: '#8b5cf6', fillColor: '#7c3aed', fillOpacity: .09, radius: 11 }
          : null;
    const strokeColor = override?.strokeColor ?? (typeof item.strokeColor === 'string' ? item.strokeColor : '#38bdf8');
    const fillColor = override?.fillColor ?? (typeof item.fillColor === 'string' ? item.fillColor : strokeColor);
    const fillOpacity = override?.fillOpacity ?? (Number.isFinite(Number(item.fillOpacity))
      ? Math.min(.25, Math.max(.03, Number(item.fillOpacity))) : .06);
    const minX = Math.min(...points.map(point => point.x)), maxX = Math.max(...points.map(point => point.x));
    const minY = Math.min(...points.map(point => point.y)), maxY = Math.max(...points.map(point => point.y));
    const radius = Math.min(override?.radius ?? 0, (maxX - minX) / 4, (maxY - minY) / 4);
    return [{
      id: item.id ?? item.text ?? 'map-area',
      text: override?.text ?? item.text ?? '仿真分区',
      points: points.map(point => `${point.x},${point.y}`).join(' '),
      path: override && radius > 0
        ? `M ${minX + radius} ${minY} H ${maxX - radius} Q ${maxX} ${minY} ${maxX} ${minY + radius} V ${maxY - radius} Q ${maxX} ${maxY} ${maxX - radius} ${maxY} H ${minX + radius} Q ${minX} ${maxY} ${minX} ${maxY - radius} V ${minY + radius} Q ${minX} ${minY} ${minX + radius} ${minY} Z`
        : '',
      labelX, labelY, strokeColor, fillColor, fillOpacity,
      showBoundary: item.id !== 'SIM-AREA-TERMINAL'
    }];
  });
}
function mapNodeLabel(node: SimulationNode): string {
  return node.agvSiteId || node.id;
}
const visibleMapAreaLabels = new Set(['SIM-AREA-WAIT', 'SIM-AREA-CHARGE', 'CAD-TERMINAL-PLATFORM']);
function showMapAreaLabel(areaId: string): boolean {
  return areaId.startsWith('SIM-AREA-G') || visibleMapAreaLabels.has(areaId);
}
function showDenseNodeLabel(node: SimulationNode): boolean {
  const label = mapNodeLabel(node);
  if (label === 'DUMPER-001') return true;
  const currentStation = vehicle.value?.station;
  if (currentStation && (node.id === currentStation || node.agvSiteId === currentStation)) return true;
  const station = /^BUS-CNC-(\d{3})$/.exec(node.id) ?? /^CNC-(\d{3})$/.exec(label);
  return !!station && denseLabelStations.has(Number(station[1]));
}
function mapNodeRadius(node: SimulationNode): number {
  if (!denseMap.value) return 4;
  return showDenseNodeLabel(node) ? 2.8 : (node.id.startsWith('APPROACH-') ? 1.2 : 1.5);
}
function vehicleMapColor(car: SimulationVehicle): string {
  return simulationVehicleVisualColor(car, fresh.value);
}
function vehicleVisualState(car: SimulationVehicle) {
  return simulationVehicleVisualState(car, fresh.value);
}
function vehicleVisualLabel(car: SimulationVehicle) {
  return simulationVehicleVisualLabel(car, fresh.value);
}
const taskStageLabels: Record<string, string> = {
  Created: '已创建', WaitingForVehicle: '等待派车', Dispatching: '正在派车', GoingToPickup: '前往取货',
  PickingUp: '执行取货', GoingToDumper: '送往倒料', WaitingForDumper: '等待倒料', Dumping: '倒料中',
  ReturningEmptyBox: '返送空箱', WaitingForPlcConfirmation: '等待PLC确认', Completed: '已完成',
  Faulted: '故障', Failed: '失败', Cancelled: '已取消', CancellationPending: '等待取消确认',
  CancellationReview: '取消核对', Running: '执行中', InProgress: '执行中'
};
const taskStepLabels: Record<string, string> = {
  GoToPickup: '前往工位取满箱', PickupFullBox: '确认取走满箱', DeliverFullBoxToDumper: '运送满箱到等待位',
  WaitForDumperReady: '等待倒料机就绪', EnterDumpingPosition: '进入倒料位', DumpFullBox: '倒料并等待完成',
  ReturnEmptyBox: '返送空箱', PlaceEmptyBox: '空箱放回工位', CompletePlcHandshake: 'PLC 握手收尾'
};
const taskStatusLabels: Record<string, string> = {
  Pending: '待开始', Ready: '已就绪', Executing: '正在执行', Waiting: '等待中',
  Completed: '已完成', Failed: '失败', Faulted: '故障', Cancelled: '已取消',
  ManualIntervention: '需要人工处理'
};
const plcSignalLabels: Record<string, string> = {
  FullBoxRequested: '满箱呼叫', FullBoxRequestAcknowledged: '满箱呼叫已应答',
  FullBoxRemoved: '满箱已取走', EmptyBoxTaskAcknowledged: '空箱任务已应答',
  EmptyBoxPlaced: '空箱已放回', BoxPresent: '箱体在位', DumperReady: '倒料机就绪',
  DumperDone: '倒料完成', Faulted: '设备故障', CommunicationOnline: '设备通信正常'
};
const handshakePhaseLabels: Record<string, string> = {
  Idle: '准备', BaselineCaptured: '已记录原始状态', Requesting: '正在请求',
  WaitingRequest: '等待设备确认请求', RequestAcknowledged: '请求已确认',
  WaitingCompletion: '等待设备完成', CompletionAcknowledged: '完成已确认',
  Completed: '握手完成', Failed: '握手失败', Waiting: '等待条件'
};
function taskStageLabel(stage?: string | null) { return taskStageLabels[stage ?? ''] ?? stage ?? '状态待核对'; }
function taskStepLabel(step?: string | null) { return taskStepLabels[step ?? ''] ?? step ?? '等待推进'; }
function taskStatusLabel(status?: string | null) { return taskStatusLabels[status ?? ''] ?? (status ? industrialText(status) : '状态待核对'); }
function plcSignalLabel(signal?: string | null) { return plcSignalLabels[signal ?? ''] ?? (signal ? industrialText(signal) : 'PLC条件'); }
function handshakePhaseLabel(phase?: string | null) { return handshakePhaseLabels[phase ?? ''] ?? (phase ? industrialText(phase) : '等待握手状态'); }
function plcConditionLabel(current: boolean | null, expected: boolean) {
  const actual = current === null ? '尚未取得反馈' : current ? '当前已置位' : '当前未置位';
  return `${actual}，要求${expected ? '置位' : '复位'}`;
}
function taskTone(stage?: string | null) {
  if (stage === 'Completed') return 'pass';
  if (['Faulted', 'Failed', 'CancellationReview'].includes(stage ?? '')) return 'fail';
  if (['Created', 'WaitingForVehicle', 'WaitingForDumper', 'WaitingForPlcConfirmation', 'CancellationPending'].includes(stage ?? '')) return 'waiting';
  return 'unknown';
}
function selectVehicleTask(vehicleId: string) {
  selectedVehicle.value = vehicleId;
  const task = simulationTasks.value.find(item => item.vehicleId === vehicleId && item.stage !== 'Completed') ??
    simulationTasks.value.find(item => item.vehicleId === vehicleId);
  if (task) void selectSimulationTask(task.taskId);
}
async function selectSimulationTask(taskId: string) {
  if (!taskId || (selectedTaskId.value === taskId && taskDetailReading.value)) return;
  const requestId = ++taskDetailRequestId;
  if (selectedTaskId.value !== taskId) {
    selectedTaskDetail.value = null;
    selectedTaskRouteStepId.value = '';
    selectedTaskRouteCommandId.value = '';
  }
  selectedTaskId.value = taskId;
  taskDetailReading.value = true;
  taskError.value = '';
  try {
    const detail = await simulationApi<TaskDetail>('/tasks/' + encodeURIComponent(taskId));
    if (!disposed && requestId === taskDetailRequestId) {
      selectedTaskDetail.value = detail;
      if (detail.task.vehicleId) selectedVehicle.value = detail.task.vehicleId;
      await refreshTaskRuntimeSnapshot();
    }
  } catch (reason) {
    if (!disposed && requestId === taskDetailRequestId)
      taskError.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    if (requestId === taskDetailRequestId) taskDetailReading.value = false;
  }
}
async function refreshTaskRuntimeSnapshot(force = false) {
  if (!status.value?.running) {
    taskRuntimeSnapshot.value = null;
    taskSnapshotError.value = '';
    taskSnapshotFetchedAt.value = 0;
    return;
  }
  if (!force && Date.now() - taskSnapshotFetchedAt.value < 1000) return;
  try {
    const snapshot = await simulationApi<Snapshot>('/snapshot');
    if (!disposed) {
      taskRuntimeSnapshot.value = snapshot;
      taskSnapshotError.value = '';
      taskSnapshotFetchedAt.value = Date.now();
    }
  } catch (reason) {
    if (!disposed) {
      taskRuntimeSnapshot.value = null;
      taskSnapshotError.value = reason instanceof Error ? reason.message : String(reason);
    }
  }
}
async function loadSimulationTaskWindow(filter: typeof simulationTaskFilter.value, search: string): Promise<TaskPage> {
  const query = new URLSearchParams({ filter, page: '1', pageSize: '200' });
  if (search.trim()) query.set('search', search.trim());
  const first = await simulationApi<TaskPage>('/tasks?' + query.toString());
  const items = [...(first.items ?? [])];
  const target = Math.min(first.totalCount, 500);
  const pages = Math.ceil(target / 200);
  for (let page = 2; page <= pages; page++) {
    query.set('page', String(page));
    const next = await simulationApi<TaskPage>('/tasks?' + query.toString());
    items.push(...(next.items ?? []));
  }
  return { ...first, items: items.slice(0, target), page: 1, pageSize: 200 };
}
async function refreshSimulationTasks() {
  if (taskReading.value || (!status.value?.running && !report.value)) return;
  const root = status.value?.rootName;
  taskReading.value = true;
  taskError.value = '';
  try {
    const page = await loadSimulationTaskWindow(simulationTaskFilter.value, simulationTaskSearch.value);
    if (disposed || root !== status.value?.rootName) return;
    simulationTaskPage.value = page;
    simulationTasks.value = page.items ?? [];
    if (status.value?.running && simulationTasks.value.length) await refreshTaskRuntimeSnapshot();
    const preferredTask = simulationTasks.value.find(task => !!taskWaitingRequest(task.taskId)) ?? simulationTasks.value[0];
    if (!selectedTaskId.value && preferredTask)
      await selectSimulationTask(preferredTask.taskId);
    else if (selectedTaskId.value && !taskDetailReading.value)
      await selectSimulationTask(selectedTaskId.value);
  } catch (reason) {
    if (!disposed) taskError.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    taskReading.value = false;
  }
}
async function toggleReportTask(taskId: string) {
  if (reportTaskReading.value) return;
  if (reportTaskDetailId.value === taskId) {
    reportTaskDetailId.value = '';
    reportTaskDetail.value = null;
    reportTaskError.value = '';
    return;
  }
  reportTaskDetailId.value = taskId;
  reportTaskDetail.value = null;
  reportTaskError.value = '';
  reportTaskReading.value = true;
  try {
    const detail = await simulationApi<TaskDetail>('/tasks/' + encodeURIComponent(taskId));
    if (!disposed && reportTaskDetailId.value === taskId) reportTaskDetail.value = detail;
  } catch (reason) {
    if (!disposed && reportTaskDetailId.value === taskId)
      reportTaskError.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    reportTaskReading.value = false;
  }
}
function vehicleBodySize(id: string) {
  const profile = status.value?.configuration?.vehicles?.find(item => item.id === id);
  const lengthMeters = profile?.lengthMeters ?? .8;
  const widthMeters = profile?.widthMeters ?? .52;
  const length = Math.min(28, Math.max(10, lengthMeters * geometry.value.scale));
  const width = length * widthMeters / Math.max(.01, lengthMeters);
  return { length, width };
}
const stageTabs: { id: SimulationStage; number: string; title: string; description: string; path: string }[] = [
  { id: 'overview', number: '01', title: '运行总览', description: '看清车辆在哪、正在做什么和下一步', path: '' },
  { id: 'tasks', number: '02', title: '任务运行', description: '查看真实任务、九步进度、命令、PLC和路权', path: 'tasks' },
  { id: 'environment', number: '03', title: '环境与地图', description: '复制工程配置并准备干净仿真实例', path: 'environment' },
  { id: 'scenario', number: '04', title: '场景编排', description: '安排测试目标、呼叫批次和运行轮次', path: 'scenario' },
  { id: 'faults', number: '05', title: '故障处理', description: '模拟现场常见的断开、卡位和道路阻塞', path: 'faults' },
  { id: 'manual', number: '06', title: '人工处置', description: '集中核对车辆、载荷、PLC和恢复条件', path: 'manual-recovery' },
  { id: 'results', number: '07', title: '监测与报告', description: '查看检查结果、历史对比和问题回放', path: 'results' }
];
const activeStage = computed<SimulationStage>(() => {
  if (props.embedded) return props.embedded.stage;
  const value = String(route.params.section ?? '');
  return stageTabs.find(tab => tab.id === value || tab.path === value)?.id ?? 'overview';
});
const activeStageInfo = computed(() => stageTabs.find(tab => tab.id === activeStage.value) ?? stageTabs[0]!);
const scenarioDescriptions: Record<SimulationRun['name'], string> = {
  'normal-cycle': '模拟一台 CNC 发出满箱呼叫，车辆前去取箱、送终端倒料、返送空箱，最后回到工位完成收尾。',
  'simultaneous-calls': '两个呼叫工位在同一设备扫描周期置位，验证并发建单、车辆分配、交通资源等待和最终释放。',
  'custom-calls': '按指定次数、批次间隔、工位顺序和车辆运行呼叫计划，适合反复包场或定点压测。',
  'dispatch-backlog': '按时间表持续产生工位呼叫，不指定车辆。由正式后台建单、选择空闲车辆和维护队列，检查忙车排除、重复占车与等待链。',
  'disconnect-reconnect': '在正式运输命令执行中切断车端通信，再恢复连接，验证原任务和路权能否续接。',
  'plc-delay': '让倒料机晚一点回复“可以进入”或“倒料完成”，观察车辆是否原地等待，恢复后能否继续原任务。',
  'plc-recovery': '把指定 PLC 信号卡住一段时间后恢复，验证卡位期间任务保持等待且没有跳过步骤。',
  'service-restart': '车辆正在运输时重启仿真后台，观察原任务、原路线和占用是否会丢失，以及恢复页能否解释清楚。',
  'resume-recovery': '在逐项核对车辆、载荷和 PLC 周期后放行，只续接原任务编号，不创建替代任务。',
  'vendor-cancel-return': '在返箱 3066 任务链最后两段由车端返回取消，验证后台不会伪造完成、不会清掉原路权，并进入取消核对。',
  'response-loss': '丢弃一次 3066 传输响应，验证后台按原任务和原命令身份核对重试，生成重复任务或更换命令即失败。',
  'road-block-recovery': '在取货运输途中临时封锁实际行驶道路，解除后验证原任务自动续接且路权不会提前释放。',
  'obstacle-auto-detour': '虚拟车先上报前方障碍。后台连续观察后规划替代路线，安全条件全部通过时自动取消原导航、确认终态并发送一条替代路线；原业务任务、载荷、PLC责任和路权保持不变。',
  'obstacle-auto-safety-return': '满箱车辆在道路中段真实上报障碍。后台精确取消原3066，在停稳和全部安全门通过后，短直边使用一次3055低速后退，长距离使用受控3051退至后方安全站，到站后再由原车续接3066。',
  'obstacle-timeout-pickup': '空载前往取料时持续受阻，车辆随后返回状态7。验证原任务冻结、空载状态可查、没有任何第二条导航被自动发送。',
  'obstacle-timeout-fullbox': '满箱运往倒料机时持续受阻并返回状态7。验证满箱责任、原倒料机和原车辆不被替换，任务与路权保持现场。',
  'obstacle-timeout-return': '倒料完成后的空箱返程持续受阻并返回状态7。验证返箱、放箱和PLC收尾不会提前完成。',
  'obstacle-timeout-midroad': '车辆在道路中间受阻并超时。验证后台不会把最近站点冒充成可信起点，清除障碍后仍要求车辆先到安全站点。',
  'obstacle-timeout-safe-site': '车辆在道路中段超时后，仿真台模拟现场受控转移到安全站点；后台重新评估障碍、位置和原路线，再由原车续接原业务任务。',
  'obstacle-timeout-manual': '车辆通信或位置无法安全恢复时，验证原任务进入明确的人工处置状态，不自动发车、不假完成、不释放占用。'
};
const currentScenarioDescription = computed(() => scenarioDescriptions[run.value.name]);
const modeledTaskCount = computed(() => Object.values(cars.value).filter(car => !!car.current_task).length);
const offlineVehicleCount = computed(() => Object.values(cars.value).filter(car => !!car.offline).length);
const onlineVehicleCount = computed(() => Object.values(cars.value).filter(car => !car.offline).length);
const loadedVehicleCount = computed(() => Object.values(cars.value).filter(car => !!car.loaded).length);
const requiresFreshInstance = computed(() => connected.value && !!status.value?.enabled && !status.value?.running &&
  !starting.value && Object.keys(cars.value).length > 0);
const nextAction = computed(() => {
  if (!connected.value) return { stage: 'environment' as const, kind: 'refresh' as const, eyebrow: '连接检查',
    title: '先恢复仿真控制器连接', detail: '页面目前没有取得仿真状态。先刷新；若仍失败，查看 WebHost 与控制器日志，不要据此启动场景。', button: '刷新连接' };
  if (!status.value?.enabled) return { stage: 'environment' as const, kind: 'configure' as const, eyebrow: '首次准备',
    title: '配置独立仿真环境', detail: '为当前 5199 指定一个隔离仿真实例和回环控制器端口。完成一次后，日常只在本页启停和运行场景。', button: '查看配置说明' };
  if (starting.value) return { stage: 'overview' as const, kind: 'observe' as const, eyebrow: '正在启动',
    title: '等待私有后台和虚拟设备就绪', detail: '启动过程中不要重复提交。页面会自动读取车辆、PLC、端口和恢复屏障状态。', button: '查看启动进度' };
  if (cleanupRequired.value) return { stage: 'overview' as const, kind: 'cleanup' as const, eyebrow: '启动失败',
    title: '先停止并清理失败环境', detail: status.value.error || '上次启动没有完成。保留日志和状态，再停止本实例的残留进程，然后新建干净实例。', button: '停止并清理' };
  if (requiresFreshInstance.value) return { stage: 'environment' as const, kind: 'configure' as const, eyebrow: '需要新建实例',
    title: '当前实例保留了上次设备历史', detail: '为保证故障、载荷和路权不会串到下一轮，已经运行或停止过的实例不能原地重启。请拉取当前地图后创建新的隔离实例；当前全厂方案可继续使用 5 辆车。', button: '去新建干净实例' };
  if (!status.value.running) return { stage: 'overview' as const, kind: 'start' as const, eyebrow: '准备完成',
    title: '启动当前隔离仿真环境', detail: `将启动实例 ${status.value.rootName || '—'} 的正式仿真后台、虚拟车辆和共享 PLC；不影响日常 5199 后台。`, button: '启动仿真环境' };
  if (active.value) return { stage: 'overview' as const, kind: 'observe' as const, eyebrow: '场景运行中',
    title: '观察任务、车辆和 PLC 的实际反馈', detail: status.value.scenario?.message || '场景正在执行；可从下方地图、车辆状态和场景事件核对现场反馈。', button: '查看运行总览' };
  if (status.value.recoveryGuidance?.length) return { stage: 'manual' as const, kind: 'manual' as const, eyebrow: '需要人工处置',
    title: `${status.value.recoveryGuidance.length} 个任务仍在等待恢复`, detail: '先核对车辆位置、载荷、PLC和原任务，再决定受控续接、取消或保留现场后重测。', button: '进入人工处置' };
  if (report.value) return { stage: 'results' as const, kind: 'results' as const, eyebrow: '已有检查结果',
    title: '先核对上一次报告，再开始下一场景', detail: `最近报告为“${scenarioNames[report.value.name ?? ''] ?? report.value.name ?? '未知场景'}”。失败、中止和证据不足都不解释为通过。`, button: '查看报告与证据' };
  return { stage: 'scenario' as const, kind: 'scenario' as const, eyebrow: '设备已就绪',
    title: '选择场景并运行', detail: '第一次建议选择“完整业务循环”跑 1 轮。运行后页面会用业务语言显示车辆去哪、在做什么、为什么等待。', button: '开始编排场景' };
});
function humanLocation(site?: string | null) {
  if (!site) return '位置尚未反馈';
  const normalized = site.replace(/^BUS-/, '');
  if (/^CNC-\d{3}$/.test(normalized)) return `${normalized} 工位`;
  if (/^WAIT-\d{2}$/.test(normalized)) return `待命位 ${Number(normalized.slice(-2))}`;
  if (/^CHARGE-\d{2}$/.test(normalized)) return `充电位 ${Number(normalized.slice(-2))}`;
  if (normalized === 'DUMPER-001') return '终端倒料位';
  if (normalized === 'INSPECT-01') return '检修观察点';
  if (site === '道路中') return '正在道路上行驶';
  return normalized;
}
function recoveryBusinessSummary(item: SimulationRecoveryItem) {
  const details: string[] = [];
  if (item.vehicleSummary) details.push(/未取得|缺失|离线/.test(item.vehicleSummary) ? '车辆与位置信息尚未关联' : '车辆与位置信息已读取');
  if (item.loadSummary) details.push(/未取得|缺失|未知/.test(item.loadSummary) ? '载荷信息仍需现场核对' : '载荷信息已读取');
  if (item.plcSummary) {
    const matches = [...item.plcSummary.matchAll(/([A-Za-z0-9-]+)\/([A-Za-z0-9]+)=(True|False)/g)];
    const stations = new Set(matches.map(match => match[1])).size;
    const faulted = matches.filter(match => match[2] === 'Faulted' && match[3] === 'True').length;
    const offline = matches.filter(match => match[2] === 'CommunicationOnline' && match[3] === 'False').length;
    details.push(`PLC反馈共 ${matches.length} 项，涉及 ${stations} 个工位；故障 ${faulted} 项，离线 ${offline} 项`);
  }
  return details.join('；') || '当前没有需要展开的技术字段';
}
const vehicleStories = computed(() => Object.entries(cars.value).map(([id, car]) => {
  const tone = vehicleVisualState(car);
  const state = vehicleVisualLabel(car);
  const action = tone === 'offline' ? '继续保留任务和路权责任；恢复连接并刷新后再核对位置与载荷。' :
    tone === 'fault' ? '检查暂停或设备故障注入；恢复后继续原任务。' :
    tone === 'blocked' ? industrialText(car.waitReason || vehicleVisualLabel(car)) :
    tone === 'running' ? `当前位置：${humanLocation(car.station)}，速度 ${car.speedMps?.toFixed(2) ?? '—'} m/s。` :
    tone === 'waiting' ? industrialText(car.waitReason || (car.loaded ? '车上有箱，等待下一步作业条件。' : '车辆有任务，但当前没有移动。')) :
    `已停在${humanLocation(car.station)}，${car.loaded ? '车上有箱' : '空载'}。`;
  return { id, tone, state, action, location: humanLocation(car.station) };
}));
const overviewHeadline = computed(() => {
  if (!connected.value) return '暂时看不到仿真环境';
  if (!status.value?.enabled) return '还没有准备仿真环境';
  if (requiresFreshInstance.value) return '这个实例已经跑过，需要新建一个干净环境';
  if (!status.value?.running) return '仿真环境已停止';
  if (active.value) return `正在运行：${scenarioNames[status.value.scenario?.name ?? run.value.name] ?? '仿真场景'}`;
  if (report.value) return '上一轮测试已经生成结果';
  return '环境和车辆已经准备好';
});
const overviewNarrative = computed(() => {
  if (!connected.value) return '先刷新连接。页面恢复后，再判断车辆和任务状态。';
  if (!status.value?.enabled) return '先到“环境与地图”选择方案，或者把当前工程带入一个隔离实例。';
  if (requiresFreshInstance.value) return '为保证上一轮的任务、载荷和路权不会串到下一轮，请另建干净实例。';
  if (!status.value?.running) return '当前没有运行中的仿真车辆。确认实例后，可以启动环境或按保存方案一键开测。';
  if (active.value) return industrialText(status.value.scenario?.message || '地图会持续更新车辆位置，右侧显示每辆车正在做什么。');
  if (report.value) return '先看结果和恢复建议，确认无残留任务、路权或载荷后，再开始下一轮。';
  return `${onlineVehicleCount.value} 辆车已就绪，${loadedVehicleCount.value} 辆载箱，${modeledTaskCount.value} 辆正在执行任务。下一步进入“场景编排”选择这次要测什么。`;
});
function openStage(stage: SimulationStage) {
  if (props.embedded) {
    emit('navigate', stage);
    return;
  }
  const target = stageTabs.find(tab => tab.id === stage) ?? stageTabs[0]!;
  const location = target.path
    ? { name: 'simulation-center', params: { section: target.path } }
    : { name: 'simulation-center' };
  if (route.name !== 'simulation-center' || activeStage.value !== stage) void router.push(location);
}
function followNextAction() {
  openStage(nextAction.value.stage);
  if (nextAction.value.kind === 'refresh') void refresh();
  if (nextAction.value.kind === 'start') void action('start');
  if (nextAction.value.kind === 'cleanup') void action('stop');
}
const callPlanValid = computed(() => Number.isInteger(callCount.value) && callCount.value >= 1 && callCount.value <= 500 &&
  Number.isFinite(callInterval.value) && callInterval.value >= 0 && callInterval.value <= 60 && Number.isInteger(callBatchSize.value) &&
  callBatchSize.value >= 1 && callBatchSize.value <= configuredStationIds.value.length &&
  callPattern.value.split(',').map(value => value.trim()).every(value => configuredStationIds.value.includes(value)));
function voltageCurveValid(curve: VehicleDraft['voltageCurve']): boolean {
  if (!Array.isArray(curve) || curve.length < 2 || curve.length > 20) return false;
  if (!curve.every(point => Number.isFinite(point.percent) && point.percent >= 0 && point.percent <= 100 &&
    Number.isFinite(point.voltage) && point.voltage >= 0.1 && point.voltage <= 1000)) return false;
  return new Set(curve.map(point => point.percent)).size === curve.length;
}
const configurationProblems = computed(() => {
  const problems: string[] = [];
  if (!mapDefinition.value) problems.push('尚未载入地图。');
  if (!plcTableText.value || !plcTableMetadata.value) problems.push('尚未绑定并验证与当前地图对应的PLC点表CSV。');
  if (stationDrafts.value.length < 1 || stationDrafts.value.length > MAX_SIMULATION_STATIONS)
    problems.push(`工位数量须为1至${MAX_SIMULATION_STATIONS}个。`);
  const boundStations = new Set(plcStationCatalog.value.map(item => item.stationId));
  const verified = new Set(plcStationCatalog.value.filter(item => item.signalSource === 'verified').map(item => item.stationId));
  stationDrafts.value.forEach((station, index) => {
    const label = `工位${index + 1}`;
    if (!/^CNC-\d{3}$/.test(station.stationId)) problems.push(`${label}编号格式无效。`);
    if (!station.pickupSite || !mapNodes.value.some(node => node.id === station.pickupSite)) problems.push(`${label}未选择有效取货节点。`);
    if (station.priority !== undefined && (!Number.isInteger(station.priority) || station.priority < 0 || station.priority > 100))
      problems.push(`${label}优先级须为0至100的整数。`);
    if (!boundStations.has(station.stationId)) problems.push(`${label}不存在于当前绑定的PLC点表。`);
    if (station.signalSource !== 'isolated-synthetic' && !verified.has(station.stationId))
      problems.push(`${label}的点位未在当前有效点表中确认，请改选“隔离合成点位”。`);
  });
  if (new Set(stationDrafts.value.map(station => station.stationId)).size !== stationDrafts.value.length)
    problems.push('工位编号不能重复。');
  if (new Set(stationDrafts.value.map(station => station.pickupSite)).size !== stationDrafts.value.length)
    problems.push('各工位不能共用同一取货节点。');
  if (!waitingSite.value) problems.push('请选择主要倒料候车位。');
  if (!dumpingSite.value) problems.push('请选择倒料停车点。');
  if (!inspectionSite.value) problems.push('请选择检修／观察点。');
  if (!dumperResourceId.value) problems.push('请选择倒料区占用范围。');
  if (!Number.isInteger(downstreamAdmissionCapacity.value) || downstreamAdmissionCapacity.value < 0 || downstreamAdmissionCapacity.value > 8)
    problems.push('取箱到收尾的并行上限须为 0 至 8 的整数。');
  if (waitingSite.value && waitingSite.value === dumpingSite.value) problems.push('主等待位与倒料位不能相同。');
  if (additionalWaitingSites.value.some(site => site === waitingSite.value || site === dumpingSite.value || !mapNodes.value.some(node => node.id === site)))
    problems.push('其他倒料候车位须有效，且与主要候车位、倒料停车点分开。');
  if (customVehicles.value.length < 1 || customVehicles.value.length > 8) problems.push('车辆数量须为1至8辆。');
  customVehicles.value.forEach((vehicle, index) => {
    const label = `车辆${index + 1}`;
    if (!/^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$/.test(vehicle.id)) problems.push(`${label} ID格式无效。`);
    if (!vehicle.initialSite || !mapNodes.value.some(node => node.id === vehicle.initialSite)) problems.push(`${label}未选择有效初始节点。`);
    if (!Number.isFinite(vehicle.lengthMeters) || !Number.isFinite(vehicle.widthMeters) ||
      vehicle.widthMeters < 0.1 || vehicle.lengthMeters < vehicle.widthMeters || vehicle.lengthMeters > 20)
      problems.push(`${label}车长和车宽无效，须满足0.1≤宽≤长≤20米。`);
    if (!Number.isFinite(vehicle.batteryPercent) || vehicle.batteryPercent < 0 || vehicle.batteryPercent > 100) problems.push(`${label}电量无效。`);
    if (!Number.isFinite(vehicle.speedScale) || vehicle.speedScale < 0.1 || vehicle.speedScale > 10) problems.push(`${label}速度倍率无效。`);
    if (!Number.isFinite(vehicle.accelerationMetersPerSecondSquared) || vehicle.accelerationMetersPerSecondSquared < 0.05 ||
      vehicle.accelerationMetersPerSecondSquared > 10) problems.push(`${label}加速度无效。`);
    if (!Number.isFinite(vehicle.operationSeconds) || vehicle.operationSeconds < 0.1 || vehicle.operationSeconds > 120)
      problems.push(`${label}取放动作时长无效。`);
    if (!Number.isFinite(vehicle.travelCostPercentPerMeter) || vehicle.travelCostPercentPerMeter < 0 ||
      vehicle.travelCostPercentPerMeter > 10) problems.push(`${label}行驶耗电参数无效。`);
    if (!Number.isFinite(vehicle.idleCostPercentPerHour) || vehicle.idleCostPercentPerHour < 0 || vehicle.idleCostPercentPerHour > 100)
      problems.push(`${label}待机耗电参数无效。`);
    if (!voltageCurveValid(vehicle.voltageCurve)) problems.push(`${label}电池电压曲线无效。`);
    if (!Number.isFinite(vehicle.chargingGainPercentPerHour) || vehicle.chargingGainPercentPerHour < 0 ||
      vehicle.chargingGainPercentPerHour > 100) problems.push(`${label}充电参数无效。`);
  });
  if (new Set(customVehicles.value.map(vehicle => vehicle.id)).size !== customVehicles.value.length)
    problems.push('车辆 ID 不能重复。');
  if (new Set(customVehicles.value.map(vehicle => vehicle.initialSite)).size !== customVehicles.value.length)
    problems.push('每辆虚拟车必须使用不同初始节点。');
  return [...new Set(problems)];
});
const configurationValid = computed(() => configurationProblems.value.length === 0);
function stamp(value?: string | number): string {
  if (value === undefined) return '尚无记录';
  const date = new Date(typeof value === 'number' ? value * 1000 : value);
  return Number.isFinite(date.getTime()) ? date.toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false }) : '尚无记录';
}
function defaultSite(names: string[], fallbackIndex: number): string {
  return mapNodes.value.find(node => names.some(name => node.id.includes(name)))?.id ?? mapNodes.value[fallbackIndex]?.id ?? '';
}
function resetVehicleOrigins() {
  const pickups = stationDrafts.value.map(station => station.pickupSite);
  const excluded = new Set([...pickups, waitingSite.value, ...additionalWaitingSites.value, dumpingSite.value, inspectionSite.value].filter(Boolean));
  const stationOrigins = [...pickups, inspectionSite.value,
    ...mapNodes.value.filter(node => node.type === 'Station' && !excluded.has(node.id)).map(node => node.id)];
  const origins = [...new Set(stationOrigins.filter(Boolean).concat(mapNodes.value.map(node => node.id).filter(id => !excluded.has(id))))];
  customVehicles.value.forEach((vehicle, index) => { vehicle.initialSite = origins[index] ?? mapNodes.value[index]?.id ?? ''; });
}
async function loadMapFile(event: Event) {
  error.value = ''; actionError.value = ''; notice.value = '';
  const file = (event.target as HTMLInputElement).files?.[0]; if (!file) return;
  if (file.size > 5 * 1024 * 1024) { error.value = '地图文件不能超过5 MiB。'; return; }
  try {
    const raw = await file.text();
    const parsed = JSON.parse(raw.replace(/^\uFEFF/, ''));
    const profControl = /\.rpj$/i.test(file.name) || Array.isArray(parsed.ItemsInNet);
    const imported = profControl ? importProfControlRpj(raw, file.name) : null;
    const value = imported?.mapDefinition ?? parsed as SimulationMapDefinition;
    if (!value || !Array.isArray(value.nodes) || !Array.isArray(value.edges) || !Array.isArray(value.resources) || value.nodes.length < 3 || !value.edges.length) throw new Error('地图必须包含至少3个节点、道路和资源数组。');
    mapDefinition.value = value; mapFileName.value = file.name;
    mapIdentityDraft.value = { sourceName: file.name };
    clearPlcTableDraft();
    if (imported?.sourceVehicles.length) {
      customVehicles.value = imported.sourceVehicles.map((vehicle, index) => {
        const lengthMeters = Math.max(.1, vehicle.widthMeters, vehicle.heightMeters);
        const widthMeters = Math.min(vehicle.widthMeters, vehicle.heightMeters);
        return {
          id: `RPJ-AGV-${String(index + 1).padStart(2, '0')}`, initialSite: '',
          radiusMeters: Math.hypot(lengthMeters, widthMeters) / 2, lengthMeters, widthMeters,
          initialLoaded: false, batteryPercent: 86, speedScale: .75, accelerationMetersPerSecondSquared: .6,
          operationSeconds: 2, travelCostPercentPerMeter: .01, idleCostPercentPerHour: .1,
          voltageCurve: [{ percent: 0, voltage: 21 }, { percent: 100, voltage: 26 }], chargingGainPercentPerHour: 20
        };
      });
      newVehicleCount.value = customVehicles.value.length;
    }
    stationDrafts.value = [
      { stationId: 'CNC-001', pickupSite: defaultSite(['取货A', 'LM101', 'CNC-001'], 0), signalSource: 'verified' },
      { stationId: 'CNC-002', pickupSite: defaultSite(['取货B', 'LM102', 'CNC-002'], 1), signalSource: 'verified' }
    ];
    dumpingSite.value = defaultSite(['倒料', 'LM108'], 2);
    waitingSite.value = defaultSite(['等待', 'LM107'], 3);
    additionalWaitingSites.value = [];
    inspectionSite.value = defaultSite(['检修', '停车'], 4);
    dumperResourceId.value = mapResources.value.find(resource => resource.id.includes('倒料'))?.id ?? mapResources.value[0]?.id ?? '';
    resetVehicleOrigins();
    notice.value = imported
      ? `已转换工程包 ${file.name}：${value.nodes.length}个节点、${value.edges.length}条道路、${value.resources.length}个资源、${imported.sourceVehicles.length}辆车。${imported.warnings.join('；')}`
      : `已载入地图 ${file.name}：${value.nodes.length}个节点、${value.edges.length}条道路、${value.resources.length}个资源。请核对语义节点后再新建实例。`;
  } catch (reason) { mapDefinition.value = null; error.value = reason instanceof Error ? reason.message : String(reason); }
}
function adoptPlcTable(table: SimulationPlcSignalTable, csvText: string) {
  plcTableText.value = csvText;
  plcTableFileName.value = table.fileName;
  plcTableSha256.value = table.sha256;
  plcStationCatalog.value = table.stations ?? [];
  plcTableMetadata.value = { ...table, csvText };
  const confirmed = new Set(plcStationCatalog.value.filter(item => item.signalSource === 'verified').map(item => item.stationId));
  stationDrafts.value.forEach(station => {
    if (confirmed.has(station.stationId)) station.signalSource = 'verified';
    else if (plcStationCatalog.value.some(item => item.stationId === station.stationId)) station.signalSource = 'isolated-synthetic';
  });
  if (mapDefinition.value && stationDrafts.value.length <= 2 && stationDrafts.value.every(station => !station.pickupSite))
    autoMapStations(false);
}
function clearPlcTableDraft() {
  plcTableText.value = ''; plcTableFileName.value = ''; plcTableSha256.value = '';
  plcStationCatalog.value = []; plcTableMetadata.value = null;
}
async function loadPlcTableFile(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0];
  if (!file) return;
  if (!/\.csv$/i.test(file.name) || file.size > 8 * 1024 * 1024) {
    actionError.value = 'PLC 点表必须是最大 8 MiB 的 CSV 文件。';
    return;
  }
  busy.value = true; actionError.value = ''; notice.value = '';
  try {
    const bytes = await file.arrayBuffer();
    const digest = await crypto.subtle.digest('SHA-256', bytes);
    const sourceSha256 = [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('');
    const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes).replace(/^\uFEFF/, '');
    const validated = await simulationApi<SimulationPlcSignalTable>('/point-table-validate', {
      fileName: file.name, csvText: text, sourceSha256, sourceFile: file.name
    });
    adoptPlcTable(validated, text);
    notice.value = `PLC点表已验证：${validated.stationCount ?? 0}个工位、${validated.signalCount ?? 0}条点位；原文件摘要 ${sourceSha256.slice(0, 12)}…，仿真副本摘要 ${validated.sha256.slice(0, 12)}…`;
  } catch (reason) {
    plcTableText.value = ''; plcTableFileName.value = ''; plcTableSha256.value = ''; plcStationCatalog.value = [];
    plcTableMetadata.value = null;
    actionError.value = reason instanceof Error ? reason.message : String(reason);
  } finally { busy.value = false; }
}
async function loadCurrentPlcTable() {
  if (plcTableReading.value) return;
  plcTableReading.value = true; actionError.value = '';
  try {
    const table = await simulationApi<SimulationPlcSignalTable>('/point-table');
    if (!table.csvText) throw new Error('当前实例没有返回完整PLC点表内容。');
    adoptPlcTable(table, table.csvText);
    notice.value = '已载入当前隔离实例实际使用的PLC点表副本。';
  } catch (reason) {
    actionError.value = reason instanceof Error ? reason.message : String(reason);
  } finally { plcTableReading.value = false; }
}
async function loadCurrentMap() {
  if (busy.value || status.value?.running || starting.value || active.value) return;
  error.value = ''; actionError.value = ''; notice.value = '';
  clearPlcTableDraft();
  busy.value = true;
  try {
    const imported = await loadCurrentSimulationMap(path => dispatchStore.api(path));
    const serviceTable = await dispatchStore.api('/api/communication/point-table') as ServicePlcPointTableSnapshot;
    const validatedTable = await simulationApi<SimulationPlcSignalTable>('/point-table-validate', {
      fileName: serviceTable.fileName, csvText: serviceTable.csvText, sourceSha256: serviceTable.sha256,
      sourceFile: serviceTable.relativePath, sourceDeviceId: serviceTable.deviceId, sourceDeviceName: serviceTable.deviceName
    });
    const communication = await dispatchStore.api('/api/communication') as {
      saved?: { agvs?: { vehicleId?: string; displayName?: string; enabled?: boolean }[] };
    };
    if (disposed) return;
    const live = imported.identity;
    mapDefinition.value = imported.definition;
    adoptPlcTable(validatedTable, serviceTable.csvText);
    liveMapIdentity.value = `${live.name || '当前运行地图'} · ${live.id} / ${live.version}`;
    mapIdentityDraft.value = { mapId: live.id, mapVersionId: live.version, sourceName: live.name || '当前运行地图',
      definitionHash: live.definitionHash };
    mapFileName.value = '当前运行地图完整副本';
    const configuration = imported.parameters?.configuration;
    const validSite = (site?: string) => mapNodes.value.some(node => node.id === site) ? site! : '';
    const verifiedStations = new Set(plcStationCatalog.value
      .filter(station => station.signalSource === 'verified' && station.fieldConfirmed !== false)
      .map(station => station.stationId));
    stationDrafts.value = (configuration?.pickups ?? []).map((pickup, index) => {
      const stationId = pickup.stationId || `CNC-${String(index + 1).padStart(3, '0')}`;
      return {
        stationId,
        pickupSite: validSite(pickup.pickupSite),
        signalSource: verifiedStations.has(stationId) ? 'verified' : 'isolated-synthetic'
      };
    });
    if (!stationDrafts.value.length) stationDrafts.value = [{ stationId: 'CNC-001', pickupSite: '', signalSource: 'verified' }];
    waitingSite.value = validSite(configuration?.waitingSites?.[0] ?? configuration?.waitingSite);
    additionalWaitingSites.value = (configuration?.waitingSites ?? []).slice(1).map(site => validSite(site)).filter(Boolean);
    dumpingSite.value = validSite(configuration?.dumpingSite);
    const dumpBindings = mapNodes.value.find(node => node.id === dumpingSite.value)?.resourceIds ?? [];
    const dumpResources = mapResources.value.filter(resource => dumpBindings.includes(resource.id));
    dumperResourceId.value = dumpResources.length === 1 ? dumpResources[0]!.id : '';
    const reservedSites = new Set([
      ...stationDrafts.value.map(station => station.pickupSite),
      waitingSite.value,
      ...additionalWaitingSites.value,
      dumpingSite.value
    ].filter(Boolean));
    inspectionSite.value = validSite(status.value?.configuration?.inspectionSite) ||
      mapNodes.value.find(node => !reservedSites.has(node.id))?.id || '';
    const configuredVehicles = (communication.saved?.agvs ?? [])
      .filter(vehicle => !!vehicle.vehicleId && vehicle.enabled === true).slice(0, 8);
    if (configuredVehicles.length > 0) {
      const template = customVehicles.value[0]!;
      customVehicles.value = configuredVehicles.map((vehicle, index) => ({
        ...template, id: vehicle.vehicleId!, initialSite: '',
        voltageCurve: template.voltageCurve.map(point => ({ ...point }))
      }));
      newVehicleCount.value = customVehicles.value.length;
    }
    resetVehicleOrigins();
    let positionNotice = '';
    if (useFieldVehiclePositions.value) {
      const origins = simulationVehicleOriginsFromLive(dispatchStore.snapshot?.status?.agvs ?? [], mapNodes.value);
      const used = new Set<string>();
      for (const vehicle of customVehicles.value) {
        const liveSite = origins.positions[vehicle.id];
        if (liveSite && !used.has(liveSite)) {
          vehicle.initialSite = liveSite;
          used.add(liveSite);
        }
      }
      for (const vehicle of customVehicles.value) {
        if (origins.positions[vehicle.id]) continue;
        const fallback = mapNodes.value.find(node => !used.has(node.id));
        if (fallback) {
          vehicle.initialSite = fallback.id;
          used.add(fallback.id);
        }
      }
      positionNotice = ` 已按现场快照映射 ${origins.matched.length}/${configuredVehicles.length || customVehicles.value.length} 辆车的位置` +
        (origins.conflicts.length ? `；位置冲突：${origins.conflicts.join('、')}，冲突车辆改用空闲起点。` : '') +
        (origins.unmatched.length ? `；未取得有效位置：${origins.unmatched.join('、')}，改用空闲起点。` : '') +
        (!dispatchStore.fresh || dispatchStore.demoMode ? ' 当前现场快照并非全部新鲜，创建前请核对。' : '');
    }
    notice.value = `已拉取当前工程：地图、任务参数、${configuredVehicles.length || customVehicles.value.length}辆车辆和PLC点表 ${serviceTable.fileName}（${serviceTable.deviceName}）。${imported.parameterWarning || '已自动映射同一地图版本的工位与业务拓扑。'}${positionNotice}请核对后创建隔离实例。`;
  } catch (reason) {
    if (!disposed) actionError.value = reason instanceof Error ? reason.message : String(reason);
  } finally { busy.value = false; }
}
function vehicleCountChanged() {
  const count = Math.max(1, Math.min(8, Number(newVehicleCount.value) || 1));
  const current = [...customVehicles.value];
  customVehicles.value = Array.from({ length: count }, (_, index) => current[index] ?? {
    id: `AGV-SIM-${String(index + 1).padStart(2, '0')}`, initialSite: '',
    radiusMeters: 0.49, lengthMeters: 0.8, widthMeters: 0.52,
    initialLoaded: false, batteryPercent: 86, speedScale: .75, accelerationMetersPerSecondSquared: 0.6,
    operationSeconds: 2, travelCostPercentPerMeter: 0.01, idleCostPercentPerHour: 0.1,
    voltageCurve: [{ percent: 0, voltage: 21 }, { percent: 100, voltage: 26 }], chargingGainPercentPerHour: 20
  });
  resetVehicleOrigins();
}
function addVoltagePoint(vehicle: VehicleDraft) {
  if (!vehicle.voltageCurve || vehicle.voltageCurve.length >= 20) return;
  const sorted = [...vehicle.voltageCurve].sort((left, right) => left.percent - right.percent);
  let percent = 50;
  let voltage = 24;
  for (let index = 1; index < sorted.length; index++) {
    if (sorted[index].percent - sorted[index - 1].percent > 1) {
      percent = Math.round((sorted[index].percent + sorted[index - 1].percent) / 2);
      voltage = Math.round((sorted[index].voltage + sorted[index - 1].voltage) / 2 * 10) / 10;
      break;
    }
  }
  vehicle.voltageCurve.push({ percent, voltage });
}
function updateVehicleFootprint(vehicle: VehicleDraft) {
  if (Number.isFinite(vehicle.lengthMeters) && Number.isFinite(vehicle.widthMeters)) {
    vehicle.radiusMeters = Math.max(0.49, Math.hypot(vehicle.lengthMeters, vehicle.widthMeters) / 2);
  }
}
function removeVoltagePoint(vehicle: VehicleDraft, index: number) {
  if ((vehicle.voltageCurve?.length ?? 0) > 2) vehicle.voltageCurve!.splice(index, 1);
}
function sortVoltageCurve(vehicle: VehicleDraft) {
  vehicle.voltageCurve?.sort((left, right) => left.percent - right.percent);
}
function configurationDraft(): SimulationConfigureRequest | null {
  if (!configurationValid.value || !mapDefinition.value || !plcTableMetadata.value || !plcTableText.value) return null;
  return {
    name: mapDefinition.value.name || mapFileName.value.replace(/\.json$/i, '') || 'custom-simulation',
    mapDefinition: mapDefinition.value, stations: stationDrafts.value.map(station => ({ ...station })),
    vehicles: customVehicles.value.map(vehicle => ({ ...vehicle,
      voltageCurve: [...vehicle.voltageCurve].sort((left, right) => left.percent - right.percent) })),
    dumper: { dumpingSite: dumpingSite.value, waitingSite: waitingSite.value, waitingSites: [waitingSite.value, ...additionalWaitingSites.value], resourceId: dumperResourceId.value },
    inspectionSite: inspectionSite.value,
    downstreamAdmissionCapacity: downstreamAdmissionCapacity.value,
    plcSignalTable: { ...plcTableMetadata.value, csvText: plcTableText.value },
    mapIdentity: mapIdentityDraft.value
  };
}
async function configureSimulation() {
  if (status.value?.running || active.value) return;
  const payload = configurationDraft(); if (payload) await action('configure', payload);
}
function buildCustomCalls(): SimulationCall[] {
  const pattern = callPattern.value.split(',').map(value => value.trim()).filter(value => configuredStationIds.value.includes(value));
  return Array.from({ length: callCount.value }, (_, index) => ({
    stationId: pattern[index % pattern.length], atSeconds: Math.floor(index / callBatchSize.value) * callInterval.value,
    ...(run.value.name === 'dispatch-backlog' ? {} : { vehicleId: callVehicleId.value || null })
  }));
}
function scenarioDraft(): SimulationRun {
  const payload: SimulationRun = { ...run.value };
  if (['custom-calls', 'dispatch-backlog'].includes(payload.name)) payload.calls = buildCustomCalls();
  if (payload.name === 'plc-recovery') { payload.plcFault = plcFault.value; payload.holdSeconds = plcFaultHold.value; }
  if (payload.name === 'road-block-recovery' || payload.name === 'obstacle-auto-detour' ||
      payload.name === 'obstacle-auto-safety-return' ||
      obstacleTimeoutScenarios.has(payload.name)) payload.holdSeconds = roadBlockHold.value;
  if (payload.name === 'service-restart') payload.restartStage = restartStage.value;
  return payload;
}
function addStation() {
  const used = new Set(stationDrafts.value.map(station => station.stationId));
  const verified = plcStationCatalog.value.find(station => station.signalSource === 'verified' && !used.has(station.stationId));
  const bound = plcStationCatalog.value.find(station => !used.has(station.stationId));
  const next = Array.from({ length: 999 }, (_, index) => `CNC-${String(index + 1).padStart(3, '0')}`).find(id => !used.has(id))!;
  stationDrafts.value.push({ stationId: verified?.stationId ?? bound?.stationId ?? next, pickupSite: '',
    signalSource: verified ? 'verified' : 'isolated-synthetic' });
  stationFilter.value = '';
  stationPage.value = stationPageCount.value;
}
function autoMapStations(notify = true) {
  if (!mapDefinition.value) {
    actionError.value = '请先载入仿真地图，再按 PLC 点表自动映射工位。';
    return;
  }
  if (!plcStationCatalog.value.length) {
    actionError.value = '请先载入并验证当前地图对应的 PLC 点表。';
    return;
  }
  const mapped = simulationStationBindingsFromCatalog(mapDefinition.value.nodes, plcStationCatalog.value);
  if (!mapped.stations.length) {
    actionError.value = '地图与 PLC 点表没有可唯一对应的工位，请核对节点号和 agvSiteId。';
    return;
  }
  stationDrafts.value = mapped.stations;
  callPattern.value = mapped.stations.map(station => station.stationId).join(',');
  callCount.value = Math.max(1, Math.min(500, callCount.value || mapped.stations.length));
  callBatchSize.value = Math.min(Math.max(1, callBatchSize.value), mapped.stations.length);
  actionError.value = '';
  if (notify) {
    notice.value = `已按地图与 PLC 点表自动映射 ${mapped.stations.length} 个工位；` +
      (mapped.unresolved.length
        ? `另有 ${mapped.unresolved.length} 个点未映射：${mapped.unresolved.slice(0, 8).map(item => `${item.stationId}（${item.reason}）`).join('、')}${mapped.unresolved.length > 8 ? '…' : ''}。`
        : '全部点表工位均已唯一映射，可继续逐项修改。');
  }
}
async function saveScheme() {
  if (!schemeName.value.trim()) return;
  if (!runValid.value) { actionError.value = '请核对场景轮数、间隔和超时后再保存方案。'; return; }
  const configuration = configurationDraft();
  if (mapDefinition.value && !configuration) { actionError.value = '请完善当前地图和车辆草稿后再保存方案。'; return; }
  if (['custom-calls', 'dispatch-backlog'].includes(run.value.name) && !callPlanValid.value) { actionError.value = '请核对呼叫计划后再保存方案。'; return; }
  const saved = await action('scheme-save', { name: schemeName.value.trim(), ...(configuration ? { configuration } : {}), scenario: scenarioDraft() });
  if (saved?.selectedSchemeId) selectedScheme.value = saved.selectedSchemeId;
}
async function deleteScheme() {
  if (!selectedScheme.value || !window.confirm('删除这个保存方案？不会停止或删除历史仿真实例与报告。')) return;
  await action('scheme-delete', { id: selectedScheme.value });
  selectedScheme.value = '';
}
function manualRecoveryLabel(state?: string | null) {
  return ({
    AwaitingObstacleClear: '等待清除障碍',
    ManualRelocationRequired: '需要移动到可信安全站点',
    ReadyForControlledDetour: '安全条件已满足，可核对后继续',
    SafetyHold: '安全条件不足，保持人工处置',
    Executing: '已发送替代导航，正在执行',
    Recovered: '原任务已恢复'
  } as Record<string, string>)[state ?? ''] ?? (state ? industrialText(state) : '尚未读取恢复候选');
}
function manualVehicleSummary(guide: SimulationGuidance) {
  if (guide.historical) {
    const site = guide.site || guide.stationId || '位置未保存';
    const state = guide.currentStepStatus ? taskStatusLabel(guide.currentStepStatus) : '历史现场';
    const coordinates = Number.isFinite(guide.x) && Number.isFinite(guide.y) ? ` · X=${guide.x}, Y=${guide.y}` : '';
    return `${site} · ${state}${coordinates}`;
  }
  const car = guide.vehicleId ? cars.value[guide.vehicleId] : null;
  if (!car) return '未取得该车辆的实时状态';
  return `${humanLocation(car.station)} · ${car.loaded ? '车上有箱' : '空载'} · ${car.offline ? '通信中断' :
    car.paused ? '车辆暂停' : car.blocked || car.blockedByVehicle ? '仍受阻' : '车辆在线'}`;
}
function manualNextStep() {
  if (!manualGuide.value) return '先从上方列表选择一个待处置任务。';
  if (manualGuide.value.historical)
    return `${manualGuide.value.restoreAction || '按当轮证据核对现场。'} 当前显示历史报告，只能查看，不能向旧任务发送恢复命令。`;
  const car = manualCar.value;
  if (!car) return '先恢复车辆通信并刷新实时状态，禁止直接发送新导航。';
  if (car.offline) return '进入故障处理，恢复该车通信；确认定位新鲜、车辆停稳后再读取恢复候选。';
  if (car.simulationControl?.obstacle) return '进入故障处理清除前方障碍；如果已到状态7，清障后仍需重新评估，不能直接续跑。';
  if (manualRecovery.value?.outcome === 'ManualRelocationRequired')
    return '先把车辆受控移动到原路线中的安全站点，再重新读取候选；不要使用最近站点冒充当前位置。';
  if (manualRecovery.value?.outcome === 'SafetyHold')
    return '通信、定位、载荷或PLC条件仍不安全。保留任务和路权，完成现场核对后再评估。';
  if (manualRecoveryReady.value)
    return '车辆、载荷和PLC核对后执行受控续接；系统只允许原车继续原业务任务。';
  return '核对原任务、车辆位置、载荷、PLC和受影响道路，再决定续接、取消或停止后新建实例。';
}
async function selectManualGuide(guide: SimulationGuidance) {
  manualGuide.value = guide;
  if (guide.vehicleId) selectedVehicle.value = guide.vehicleId;
  manualRecovery.value = null; manualRecoveryError.value = '';
  manualVehicleConfirmed.value = false; manualLoadConfirmed.value = false; manualPlcConfirmed.value = false;
  if (!guide.taskId || guide.historical) return;
  manualRecoveryReading.value = true;
  try {
    manualRecovery.value = await simulationApi<SimulationTimeoutRecovery>('/timeout-recovery/' + encodeURIComponent(guide.taskId));
  } catch (reason) {
    manualRecoveryError.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    manualRecoveryReading.value = false;
  }
}
async function executeManualRecovery() {
  const recovery = manualRecovery.value;
  if (!recovery || !manualRecoveryCanExecute.value || recovery.revision === undefined) return;
  busy.value = true; actionError.value = ''; notice.value = '';
  try {
    const result = await simulationApi<SimulationTimeoutRecoveryResult>(
      '/timeout-recovery/' + encodeURIComponent(recovery.taskId) + '/execute',
      { taskId: recovery.taskId, expectedRevision: recovery.revision, executeConfirmed: true,
        vehicleAndLocationConfirmed: true, loadAndOwnershipConfirmed: true,
        plcConditionsConfirmed: true, reason: manualRecoveryReason.value.trim() });
    if (!result.accepted) throw new Error(result.message || '人工恢复未被后台接受。');
    notice.value = '人工恢复已接受。只由原车续接原业务任务，请到任务运行和监测报告核对后续反馈。';
    await refresh(true);
    if (manualGuide.value) await selectManualGuide(manualGuide.value);
  } catch (reason) {
    actionError.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    busy.value = false;
  }
}
function openManualGuideFaults() {
  if (manualGuide.value?.vehicleId) selectedVehicle.value = manualGuide.value.vehicleId;
  openStage('faults');
}
async function stopForCleanRetest() {
  if (status.value?.running) await action('stop');
  notice.value = '当前故障现场和报告已保留。请在“环境与地图”按方案新建干净实例，再从场景编排选择要重测的阻挡场景。';
  openStage('environment');
}
async function selectHistory() {
  replayPlaying.value = false; replay.value = null; replayIndex.value = 0;
  if (!selectedHistory.value) { selectedReport.value = null; return; }
  historyReading.value = true;
  try {
    selectedReport.value = await simulationApi<SimulationReport>('/report?id=' + encodeURIComponent(selectedHistory.value));
    replay.value = await simulationApi<SimulationReplay>('/replay?id=' + encodeURIComponent(selectedHistory.value));
    replayIndex.value = 0;
  }
  catch (reason) { actionError.value = reason instanceof Error ? reason.message : String(reason); }
  finally { historyReading.value = false; }
}
function clearHistorySelection() {
  selectedHistory.value = ''; selectedReport.value = null; replay.value = null; replayPlaying.value = false;
  manualGuide.value = null; manualRecovery.value = null; manualRecoveryError.value = '';
}
async function compareHistory() {
  comparisonReport.value = null;
  if (!comparisonId.value) return;
  try { comparisonReport.value = await simulationApi<SimulationReport>('/report?id=' + encodeURIComponent(comparisonId.value)); }
  catch (reason) { actionError.value = reason instanceof Error ? reason.message : String(reason); }
}
async function loadReplay() {
  if (!report.value?.id || historyReading.value) return;
  historyReading.value = true; replayPlaying.value = false;
  try { replay.value = await simulationApi<SimulationReplay>('/replay?id=' + encodeURIComponent(report.value.id)); replayIndex.value = 0; }
  catch (reason) { actionError.value = reason instanceof Error ? reason.message : String(reason); }
  finally { historyReading.value = false; }
}
async function startScenario() {
  if (!runValid.value) return;
  const payload = scenarioDraft();
  if (['custom-calls', 'dispatch-backlog'].includes(payload.name)) {
    if (!callPlanValid.value) { error.value = '核对呼叫数量、间隔、批量和工位序列。'; return; }
  }
  if (payload.name === 'plc-recovery') {
    if (!Number.isFinite(plcFaultHold.value) || plcFaultHold.value < 1 || plcFaultHold.value > 120) {
      error.value = 'PLC卡位保持时间须为1至120秒。'; return;
    }
    payload.plcFault = plcFault.value; payload.holdSeconds = plcFaultHold.value;
  }
  if (payload.name === 'service-restart') payload.restartStage = restartStage.value;
  await action('run', payload);
}
async function submitRecovery(item: SimulationRecoveryItem) {
  if (!recovery.value?.sessionId || !vehicleConfirmed.value || !loadConfirmed.value || !plcConfirmed.value) return;
  busy.value = true; error.value = ''; actionError.value = ''; notice.value = '';
  try {
    await simulationApi('/recover', {
      sessionId: recovery.value.sessionId, ticketId: item.ticketId, revision: item.revision, decision: recoveryDecision.value,
      materialReference: recoveryMaterial.value, reason: recoveryReason.value,
      vehicleAndLocationConfirmed: vehicleConfirmed.value, loadAndOwnershipConfirmed: loadConfirmed.value, plcCycleConfirmed: plcConfirmed.value
    });
    notice.value = '恢复条目已由私有后台应用；请刷新并核对是否全部通过。';
    await refresh(true);
  } catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason); }
  finally { busy.value = false; }
}
async function releaseRecovery() {
  if (!recovery.value?.sessionId || !recovery.value.items.every(item => item.verified)) return;
  busy.value = true; error.value = ''; actionError.value = ''; notice.value = '';
  try {
    await simulationApi('/recover-release', { sessionId: recovery.value.sessionId, reason: recoveryReason.value });
    notice.value = '恢复屏障已放行。原任务只能按原编号继续；请运行“恢复后继续原任务”场景核对最终闭合。';
    await refresh(true);
  } catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason); }
  finally { busy.value = false; }
}
async function loadProtocolFile(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]; if (!file) return;
  if (file.size > 256 * 1024) { error.value = '协议脚本不能超过256 KiB。'; return; }
  try { protocolText.value = (await file.text()).replace(/^\uFEFF/, ''); JSON.parse(protocolText.value); protocolFile.value = file.name; notice.value = '已载入协议脚本，核对端口和规则后启动。'; }
  catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason); }
}
async function startProtocol() {
  try {
    const config = JSON.parse(protocolText.value);
    if (!config || typeof config !== 'object' || Array.isArray(config)) throw new Error('协议脚本必须是JSON对象。');
    await action('protocol-start', config);
  } catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason); }
}
function protocolAddress() {
  if (!protocol.value?.port) return '尚未监听';
  return `${protocol.value.transport}://127.0.0.1:${protocol.value.port}`;
}
function equipmentControl(payload: Record<string, unknown>) {
  void action('equipment-control', { sequence: crypto.randomUUID(), ...payload });
}
async function startEquipment() {
  await action('equipment-start', { sequence: crypto.randomUUID(), travelSeconds: equipmentTravelSeconds.value, doorSeconds: equipmentDoorSeconds.value });
}
async function setEquipmentSignals() {
  try {
    const set = JSON.parse(signalSetText.value), schedule = JSON.parse(signalScheduleText.value);
    if (!set || typeof set !== 'object' || Array.isArray(set) || !Array.isArray(schedule)) throw new Error('信号集合须为对象，时间轴须为数组。');
    equipmentControl({ signals: { set, schedule } });
  } catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason); }
}
function accept(next: SimulationStatus) {
  if (next.rootName !== status.value?.rootName && next.configuration && next.map) {
    mapDefinition.value = next.map; mapFileName.value = next.configuration.mapName || '已保存的实例地图';
    mapIdentityDraft.value = next.configuration.mapIdentity ?? undefined;
    stationDrafts.value = next.configuration.stations.map(station => ({ ...station, signalSource: station.signalSource ?? 'verified' }));
    stationFilter.value = '';
    stationPage.value = 1;
    waitingSite.value = next.configuration.dumper.waitingSite;
    additionalWaitingSites.value = (next.configuration.dumper.waitingSites ?? []).filter(site => site !== waitingSite.value);
    dumpingSite.value = next.configuration.dumper.dumpingSite; dumperResourceId.value = next.configuration.dumper.resourceId;
    inspectionSite.value = next.configuration.inspectionSite;
    downstreamAdmissionCapacity.value = next.configuration.downstreamAdmissionCapacity ?? 0;
    const boundTable = next.configuration.plcSignalTable ?? next.plcSignalTable ?? null;
    if (boundTable) {
      plcTableFileName.value = boundTable.fileName;
      plcTableSha256.value = boundTable.sha256;
      plcTableMetadata.value = { ...boundTable, ...(plcTableText.value ? { csvText: plcTableText.value } : {}) };
      plcStationCatalog.value = boundTable.stations ?? next.stationCatalog ?? [];
    }
    const defaults = customVehicles.value[0]!;
    customVehicles.value = next.configuration.vehicles.map(vehicle => ({ ...defaults, ...vehicle,
      lengthMeters: vehicle.lengthMeters ?? Math.max(.1, (vehicle.radiusMeters ?? .49) * 2),
      widthMeters: vehicle.widthMeters ?? Math.max(.1, (vehicle.radiusMeters ?? .49) * 2),
      voltageCurve: vehicle.voltageCurve?.map(point => ({ ...point })) ?? [{ percent: 0, voltage: 21 }, { percent: 100, voltage: 26 }] }));
    newVehicleCount.value = customVehicles.value.length;
    callPattern.value = stationDrafts.value.map(station => station.stationId).join(',');
    callBatchSize.value = Math.min(callBatchSize.value, stationDrafts.value.length);
    selectedHistory.value = ''; selectedReport.value = null; replay.value = null; replayPlaying.value = false;
    simulationTasks.value = []; simulationTaskPage.value = null;
    selectedTaskId.value = ''; selectedTaskDetail.value = null;
    taskDetailRequestId++; taskDetailReading.value = false;
    selectedTaskRouteStepId.value = ''; selectedTaskRouteCommandId.value = '';
    taskRuntimeSnapshot.value = null; taskSnapshotError.value = '';
    taskSnapshotFetchedAt.value = 0;
    reportTaskDetailId.value = ''; reportTaskDetail.value = null; reportTaskError.value = '';
    manualGuide.value = null; manualRecovery.value = null; manualRecoveryError.value = '';
    manualVehicleConfirmed.value = false; manualLoadConfirmed.value = false; manualPlcConfirmed.value = false;
  }
  if (!selectedScheme.value) selectedScheme.value = next.selectedSchemeId ?? next.schemes?.[0]?.id ?? '';
  status.value = next; connected.value = true;
  if (!Object.hasOwn(next.device?.cars ?? {}, selectedVehicle.value)) selectedVehicle.value = Object.keys(next.device?.cars ?? {})[0] ?? '';
}
async function refresh(force = false) {
  if (disposed || reading.value || (busy.value && !force)) return;
  reading.value = true; const request = new AbortController(); controller = request;
  const timeout = setTimeout(() => request.abort(), 45000);
  try {
    let readMetadata = force || !metadataLoaded || !status.value ||
      Date.now() - lastMetadataRefresh > 60_000;
    let received = await simulationApi<SimulationStatus>(
      readMetadata ? '' : '/live', undefined, request.signal);
    if (!readMetadata && (received.rootName !== status.value?.rootName ||
        received.scenario?.id !== status.value?.scenario?.id ||
        received.scenario?.status !== status.value?.scenario?.status)) {
      received = await simulationApi<SimulationStatus>('', undefined, request.signal);
      readMetadata = true;
    }
    if (!disposed) {
      const next = readMetadata ? received : { ...status.value!, ...received };
      if (readMetadata) {
        metadataLoaded = true;
        lastMetadataRefresh = Date.now();
      }
      accept(next);
      error.value = '';
      if (next.running || (readMetadata && activeStage.value === 'tasks' && next.report))
        void refreshSimulationTasks();
    }
  }
  catch (reason) { if (!disposed) { connected.value = false; error.value = reason instanceof Error ? reason.message : String(reason); } }
  finally { clearTimeout(timeout); reading.value = false; if (controller === request) controller = undefined; }
}
async function action(name: 'start' | 'stop' | 'run' | 'abort' | 'control' | 'new-instance' | 'configure' |
  'scheme-save' | 'scheme-select' | 'scheme-start' | 'scheme-delete' |
  'protocol-start' | 'protocol-stop' | 'equipment-start' | 'equipment-control' | 'equipment-stop', payload: unknown = {}): Promise<SimulationStatus | undefined> {
  if (!available.value || (name === 'run' && (!fresh.value || !runValid.value || active.value)) || (name === 'control' && !fresh.value)) return;
  if (name === 'new-instance' && (status.value?.running || starting.value || active.value)) return;
  if (name === 'new-instance' && (!Number.isInteger(newVehicleCount.value) || newVehicleCount.value < 1 || newVehicleCount.value > 8)) return;
  busy.value = true; error.value = ''; actionError.value = ''; notice.value = '';
  const request = new AbortController(); controller = request;
  const preparesInstance = ['new-instance', 'configure', 'scheme-select', 'scheme-start'].includes(name);
  const timeout = setTimeout(() => request.abort(), preparesInstance ? 145000 : 60000);
  let result: SimulationStatus | undefined;
  try {
    const next = await simulationApi<SimulationStatus>('/' + name, payload, request.signal);
    result = next;
    if (!disposed) { accept(next); notice.value = name === 'control' ? '控制请求已提交，请核对设备状态与事件中的实际反馈。' : next.message ?? '请求已提交，正在读取仿真运行状态。'; }
  } catch (reason) {
    if (!disposed) {
      actionError.value = (reason instanceof Error ? reason.message : String(reason)) +
        (name === 'start' ? '；请按上方提示处理后重新启动。' : '；请刷新原记录后核对执行结果。');
    }
  }
  finally { clearTimeout(timeout); busy.value = false; if (controller === request) controller = undefined; }
  return result;
}
function carControl(values: NonNullable<SimulationControls['vehicles']>[string]) {
  if (!selectedVehicle.value || !canControl.value) return;
  void action('control', { sequence: crypto.randomUUID(), vehicles: { [selectedVehicle.value]: values } } satisfies SimulationControls);
}
function plcControl(values: SimulationControls['plc']) {
  if (canControl.value) void action('control', { sequence: crypto.randomUUID(), plc: values } satisfies SimulationControls);
}
watch(selectedVehicle, () => {
  blockedEdgeIds.value = [...appliedBlockedEdges.value];
  if (Number.isFinite(vehicle.value?.speedScale)) speedScale.value = vehicle.value.speedScale!;
});
watch(activeStage, stage => { if (stage === 'tasks') void refreshSimulationTasks(); });
watch(() => [props.embedded?.taskId, activeStage.value, status.value?.rootName] as const, ([taskId, stage, root]) => {
  if (taskId && root && stage === 'tasks') void selectSimulationTask(taskId);
});
watch(() => run.value.name, name => { if (name === 'dispatch-backlog') { callCount.value = Math.max(6, callCount.value); callInterval.value = 1; } });
async function downloadReport() {
  if (busy.value || !report.value) return;
  try {
    const data = await simulationApi<SimulationReport>('/report' + (report.value.id ? '?id=' + encodeURIComponent(report.value.id) : ''));
    const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }));
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'simulation-report-' + (data.id?.replace(/[^a-zA-Z0-9_-]/g, '') || 'latest') + '.json'; anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  } catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason); }
}
function downloadBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a'); anchor.href = url; anchor.download = fileName; anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
async function downloadEvidenceArchive() {
  if (busy.value || !report.value?.id) return;
  busy.value = true; actionError.value = ''; notice.value = '';
  try {
    const id = report.value.id;
    if (props.embedded) {
      downloadBlob(await props.embedded.archive(id), `simulation-evidence-${id}.zip`);
      notice.value = '完整证据包已导出。';
      return;
    }
    const response = await fetch('/api/simulation/archive?id=' + encodeURIComponent(id), { cache: 'no-store' });
    if (!response.ok) {
      const detail = await response.json().catch(() => ({}));
      throw new Error(detail.error || detail.detail || `证据包导出失败（${response.status}）`);
    }
    downloadBlob(await response.blob(), `simulation-evidence-${report.value.id}.zip`);
    notice.value = '完整证据包已导出：包含任务清单、逐次观察、地图、PLC点表、场景报告、设备日志和服务日志。';
  } catch (reason) {
    actionError.value = reason instanceof Error ? reason.message : String(reason);
  } finally { busy.value = false; }
}
function fitCanvasText(ctx: CanvasRenderingContext2D, value: string, maximum: number): string {
  if (ctx.measureText(value).width <= maximum) return value;
  let text = value;
  while (text.length > 1 && ctx.measureText(text + '…').width > maximum) text = text.slice(0, -1);
  return text + '…';
}
function drawReplayFrame(canvas: HTMLCanvasElement, frame: SimulationReplay['timeline'][number], index: number, total: number) {
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('浏览器无法创建视频画布。');
  const width = canvas.width, height = canvas.height;
  ctx.clearRect(0, 0, width, height);
  ctx.fillStyle = '#081226'; ctx.fillRect(0, 0, width, height);
  const gradient = ctx.createLinearGradient(0, 0, width, 0);
  gradient.addColorStop(0, '#123654'); gradient.addColorStop(1, '#0d1b33');
  ctx.fillStyle = gradient; ctx.fillRect(0, 0, width, 92);
  ctx.fillStyle = '#bceef4'; ctx.font = '700 30px "Microsoft YaHei", sans-serif';
  const vehicleCount = status.value?.configuration?.vehicles?.length ?? frame.vehicles?.length ?? 0;
  const stationCount = status.value?.configuration?.stations?.length ?? report.value?.taskSummary?.length ?? 0;
  ctx.fillText(`${vehicleCount}车${stationCount}工位 · 隔离仿真回放`, 30, 40);
  ctx.fillStyle = '#8fb5c9'; ctx.font = '18px "Microsoft YaHei", sans-serif';
  ctx.fillText(`场景：${scenarioNames[report.value?.name ?? ''] ?? report.value?.name ?? '仿真任务'}  ·  报告：${report.value?.id ?? '未提供'}`, 30, 70);
  ctx.fillStyle = '#61d6c2'; ctx.font = '700 17px "Microsoft YaHei", sans-serif';
  ctx.fillText(`${index + 1} / ${total}`, width - 130, 40);
  ctx.fillStyle = '#92a9c4'; ctx.font = '15px "Microsoft YaHei", sans-serif';
  const stampText = Number.isFinite(frame.time) ? new Date(frame.time * 1000).toLocaleString('zh-CN', {
    timeZone: 'Asia/Shanghai', hour12: false
  }) : '时间未提供';
  ctx.fillText(stampText, width - 300, 70);

  ctx.fillStyle = '#0c172b'; ctx.strokeStyle = '#294666'; ctx.lineWidth = 1;
  ctx.fillRect(24, 106, 1020, 666); ctx.strokeRect(24, 106, 1020, 666);
  const geometry = simulationGeometry(replay.value?.map?.nodes ?? [], replay.value?.map?.edges ?? [], simulationReplayCars(frame));
  ctx.save();
  ctx.beginPath(); ctx.rect(32, 114, 1004, 650); ctx.clip();
  ctx.translate(94, 219); ctx.scale(1.17, 1.17);
  ctx.lineCap = 'round'; ctx.strokeStyle = '#385474'; ctx.lineWidth = 4;
  for (const edge of geometry.edges) ctx.stroke(new Path2D(edge.path));
  for (const node of geometry.nodes) {
    ctx.fillStyle = '#7696b6'; ctx.beginPath(); ctx.arc(node.x, node.y, 3, 0, Math.PI * 2); ctx.fill();
    if (node.type === 'Station' || node.id.includes('BUS-LM')) {
      ctx.fillStyle = '#89a6c1'; ctx.font = '11px "Microsoft YaHei", sans-serif';
      ctx.fillText(node.agvSiteId || node.id, node.x + 6, node.y - 6);
    }
  }
  const colors = ['#41d9bf', '#6fb9ff', '#e8bf64', '#cb9aff'];
  geometry.cars.forEach((car, carIndex) => {
    const color = colors[carIndex % colors.length];
    const profile = status.value?.configuration?.vehicles?.find(vehicle => vehicle.id === car.id);
    const length = (profile?.lengthMeters ?? .8) * geometry.scale;
    const width = (profile?.widthMeters ?? .52) * geometry.scale;
    ctx.save(); ctx.translate(car.x, car.y); ctx.rotate(-(car.angle ?? 0));
    ctx.fillStyle = color; ctx.globalAlpha = .24; ctx.fillRect(-length / 2, -width / 2, length, width);
    ctx.globalAlpha = 1; ctx.strokeStyle = color; ctx.lineWidth = 2; ctx.strokeRect(-length / 2, -width / 2, length, width);
    ctx.restore();
    ctx.fillStyle = color; ctx.font = '700 13px "Microsoft YaHei", sans-serif'; ctx.textAlign = 'center';
    ctx.fillText(car.id, car.x, car.y - 23); ctx.textAlign = 'left';
    ctx.font = '11px "Microsoft YaHei", sans-serif'; ctx.fillText(car.station || '道路中', car.x + 20, car.y + 4);
  });
  ctx.restore();

  ctx.fillStyle = '#0c172b'; ctx.strokeStyle = '#294666';
  ctx.fillRect(1060, 106, 316, 666); ctx.strokeRect(1060, 106, 316, 666);
  ctx.fillStyle = '#a8e2e8'; ctx.font = '700 20px "Microsoft YaHei", sans-serif';
  ctx.fillText('车辆与任务', 1080, 142);
  let y = 178;
  ctx.font = '15px "Microsoft YaHei", sans-serif';
  for (const car of frame.vehicles ?? []) {
    ctx.fillStyle = '#69d5c3';
    ctx.fillText(fitCanvasText(ctx, `${car.vehicleId} · ${car.site || '道路中'}`, 270), 1080, y);
    y += 24;
    ctx.fillStyle = '#93a9c1'; ctx.font = '12px "Microsoft YaHei", sans-serif';
    ctx.fillText(fitCanvasText(ctx, car.taskId ? `任务 ${car.taskId}` : '空闲', 270), 1092, y);
    if (car.waitReason) { y += 19; ctx.fillStyle = '#e3bd78'; ctx.fillText(fitCanvasText(ctx, car.waitReason, 270), 1092, y); }
    y += 28; ctx.font = '15px "Microsoft YaHei", sans-serif';
  }
  ctx.strokeStyle = '#294666'; ctx.beginPath(); ctx.moveTo(1080, y); ctx.lineTo(1356, y); ctx.stroke(); y += 28;
  for (const task of (frame.tasks ?? []).slice(0, 5)) {
    ctx.fillStyle = '#a8cfe4'; ctx.font = '14px "Microsoft YaHei", sans-serif';
    ctx.fillText(fitCanvasText(ctx, `${task.vehicleId || '待派车'} · ${task.stationId || '工位未知'}`, 270), 1080, y);
    y += 22;
    ctx.fillStyle = '#849db9'; ctx.font = '12px "Microsoft YaHei", sans-serif';
    ctx.fillText(fitCanvasText(ctx, String(task.step || task.stage || '等待推进'), 270), 1092, y);
    if (task.waitReason) { y += 18; ctx.fillStyle = '#e3bd78'; ctx.fillText(fitCanvasText(ctx, task.waitReason, 270), 1092, y); }
    y += 28;
    if (y > 720) break;
  }
  const progress = total > 0 ? index / total : 0;
  ctx.fillStyle = '#17304d'; ctx.fillRect(24, 786, 1352, 8);
  ctx.fillStyle = '#42d6d5'; ctx.fillRect(24, 786, 1352 * progress, 8);
  ctx.fillStyle = '#7e98b5'; ctx.font = '13px "Microsoft YaHei", sans-serif';
  ctx.fillText('回环 Robokit/S7 合成模型 · 任务、车辆、PLC与路权观察记录 · 不替代实车和安全验收', 24, 812);
}
async function exportReplayVideo() {
  if (recordingVideo.value || !report.value?.id) return;
  if (!replay.value?.timeline.length) await loadReplay();
  if (!replay.value?.timeline.length) {
    actionError.value = '当前报告没有可导出的回放采样。';
    return;
  }
  if (typeof MediaRecorder === 'undefined') {
    actionError.value = '当前浏览器不支持WebM视频录制，请使用最新版Edge或Chrome。';
    return;
  }
  const mimeType = ['video/webm;codecs=vp9', 'video/webm;codecs=vp8', 'video/webm']
    .find(type => MediaRecorder.isTypeSupported(type));
  if (!mimeType) {
    actionError.value = '当前浏览器没有可用的WebM编码器。';
    return;
  }
  const source = replay.value.timeline;
  const step = Math.max(1, Math.ceil(source.length / 600));
  const frames = source.filter((_, index) => index % step === 0 || index === source.length - 1);
  const canvas = document.createElement('canvas'); canvas.width = 1400; canvas.height = 820;
  const stream = canvas.captureStream(10);
  const recorder = new MediaRecorder(stream, { mimeType, videoBitsPerSecond: 2_500_000 });
  const chunks: BlobPart[] = [];
  recordingVideo.value = true; videoProgress.value = 0; actionError.value = '';
  try {
    const finished = new Promise<Blob>((resolve, reject) => {
      recorder.ondataavailable = event => { if (event.data.size) chunks.push(event.data); };
      recorder.onerror = () => reject(new Error('浏览器视频编码失败。'));
      recorder.onstop = () => resolve(new Blob(chunks, { type: mimeType }));
    });
    recorder.start(1000);
    for (let index = 0; index < frames.length; index++) {
      drawReplayFrame(canvas, frames[index]!, index, frames.length);
      videoProgress.value = Math.round((index + 1) / frames.length * 100);
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    await new Promise(resolve => setTimeout(resolve, 250));
    recorder.stop();
    const blob = await finished;
    if (!blob.size) throw new Error('视频没有生成有效内容。');
    downloadBlob(blob, `simulation-replay-${report.value.id}.webm`);
    notice.value = `回放视频已导出，共 ${frames.length} 帧；原始 ${source.length} 个观察采样仍保存在完整证据包中。`;
  } catch (reason) {
    actionError.value = reason instanceof Error ? reason.message : String(reason);
  } finally {
    for (const track of stream.getTracks()) track.stop();
    recordingVideo.value = false;
  }
}
onMounted(() => { void refresh(); poll = setInterval(() => void refresh(), 1500); tick = setInterval(() => {
  now.value = Date.now();
  if (replayPlaying.value && replay.value?.timeline.length) {
    if (replayIndex.value < replay.value.timeline.length - 1) replayIndex.value++;
    else replayPlaying.value = false;
  }
}, 500); });
onUnmounted(() => { disposed = true; controller?.abort(); clearInterval(poll); clearInterval(tick); });
</script>

<template>
  <main class="simulation-page" :class="{ embedded: !!embedded }">
    <header v-if="!embedded" class="simulation-title">
      <div>
        <p class="page-kicker">隔离环境 · 正式调度仿真</p>
        <h2><IconFlask :size="24" />仿真测试中心</h2>
        <p>七个模块分别是独立页面。先在总览看车辆，再到任务运行核对九步、命令、PLC和路权。</p>
      </div>
      <div class="simulation-header-actions">
        <div class="mode-card"><small>当前仿真方式</small><b>设备回环 + 正式调度</b><span>虚拟车辆与设备通过正式通信链路参与测试</span></div>
        <button :disabled="busy || reading" @click="refresh()"><IconRefresh :size="16" />刷新状态</button>
      </div>
    </header>

    <p v-if="error || actionError" class="simulation-error" role="alert">{{ industrialText(actionError || error) }}</p>
    <p v-if="notice" class="simulation-notice" role="status">{{ industrialText(notice) }}</p>

    <div class="simulation-workspace">
      <div v-if="!embedded || activeStage === 'overview'" class="simulation-sidebar">
        <nav v-if="!embedded" class="stage-tabs module-nav" aria-label="仿真测试功能页面">
          <button v-for="tab in stageTabs" :key="tab.id" :class="{ active: activeStage === tab.id }" :aria-current="activeStage === tab.id ? 'page' : undefined" @click="openStage(tab.id)">
            <span>{{ tab.number }}</span><strong>{{ tab.title }}</strong>
          </button>
        </nav>
        <aside v-if="activeStage === 'overview'" class="simulation-panel business-story sidebar-business-story">
        <header><div><p v-if="!embedded" class="section-kicker">当前运行</p><h3>{{ embedded ? '车队状态' : '车辆正在做什么' }}</h3></div><span :class="['state-pill', active ? 'waiting' : report ? 'pass' : 'unknown']">{{ active ? '测试中' : report ? '有结果' : '待测试' }}</span></header>
        <div class="overview-hero" :data-action="nextAction.kind" aria-live="polite">
          <div class="overview-hero-copy">
            <div class="overview-status-title"><span :class="['state-pill', cleanupRequired ? 'fail' : fresh ? 'pass' : 'unknown']">{{ !status?.enabled ? '未准备' : status?.running ? '运行正常' : '已停止' }}</span><b>{{ overviewHeadline }}</b></div>
            <dl v-if="embedded" class="fleet-counts">
              <div><dt>在线</dt><dd>{{ onlineVehicleCount }}</dd></div>
              <div><dt>载箱</dt><dd>{{ loadedVehicleCount }}</dd></div>
              <div><dt>执行任务</dt><dd>{{ modeledTaskCount }}</dd></div>
            </dl>
            <p v-else>{{ onlineVehicleCount }} 辆车在线 · {{ loadedVehicleCount }} 辆载箱 · {{ modeledTaskCount }} 辆执行任务</p>
          </div>
          <button class="primary overview-next-action" :disabled="busy || reading" @click="followNextAction">{{ nextAction.button }}</button>
        </div>
        <div class="vehicle-story-list">
          <article v-for="item in vehicleStories" :key="item.id" :class="item.tone">
            <div><b>{{ item.id }}</b><span>{{ item.state }}</span></div>
            <small>{{ item.location }}</small>
            <p>{{ item.action }}</p>
          </article>
        </div>
        <div class="toolbar quick-actions"><button class="primary" @click="openStage('scenario')">选择场景</button><button @click="openStage('faults')">制造异常</button><button @click="openStage('results')">查看结果</button></div>
        <p class="sidebar-simulation-note">隔离仿真 · 不连接真车或现场 PLC</p>
        </aside>
      </div>
      <div class="simulation-workspace-content">

    <section v-if="activeStage !== 'overview' && !(embedded && activeStage === 'tasks')" class="module-heading">
      <div>
        <p v-if="!embedded" class="section-kicker">独立页面 {{ activeStageInfo.number }} / 07</p>
        <h3 v-if="!embedded" id="simulation-environment">{{ activeStageInfo.title }}</h3>
        <p>{{ activeStageInfo.description }}</p>
      </div>
      <div class="module-heading-status">
        <span :class="['state-pill', cleanupRequired ? 'fail' : fresh ? 'pass' : 'unknown']">{{ !connected ? '连接中' : !status?.enabled ? '未准备' : !status?.running ? '环境已停止' : fresh ? '运行正常' : '数据待刷新' }}</span>
        <small>{{ vehicleIds.length }} 辆车 · {{ onlineVehicleCount }} 辆在线 · {{ configuredStationIds.length }} 个工位</small>
      </div>
      <div class="module-heading-actions">
        <button class="primary" :disabled="!available || !!status?.running || starting || requiresFreshInstance || cleanupRequired" @click="action('start')"><IconPlayerPlay :size="16" />{{ starting ? '正在启动…' : cleanupRequired ? '启动失败待清理' : requiresFreshInstance ? '请先新建实例' : '启动环境' }}</button>
        <button :disabled="!available || (!status?.running && !starting && !cleanupRequired)" @click="action('stop')"><IconPlayerStop :size="16" />{{ cleanupRequired ? '停止并清理' : '停止环境' }}</button>
      </div>
    </section>

    <section v-if="activeStage === 'overview'" class="stage-surface overview-stage">
      <details class="instance-details">
        <summary>技术实例信息</summary>
        <p>{{ status?.rootName || '尚未选择仿真实例' }} · 最近采样 {{ snapshotTime }}</p>
        <p v-if="status?.startupPlan?.message">{{ industrialText(status.startupPlan.message) }}</p>
        <p v-if="requiresFreshInstance" class="inline-warning">该实例已有历史状态，不能原地重启。请到“环境与地图”创建新的干净实例。</p>
      </details>

      <div class="overview-grid business-overview-grid">
        <slot name="overview-map" :status="status" :fresh="fresh" :selected-vehicle="selectedVehicle"
          :select-vehicle="selectVehicleTask" :road-highlights="overviewRoadHighlights">
        <section class="simulation-panel map-panel" aria-labelledby="simulation-map">
          <header><div><p class="section-kicker">车辆现在在哪</p><h3 id="simulation-map">全厂运行画面</h3></div><div class="toolbar map-header-actions"><span class="map-view-switch"><button :aria-pressed="sceneDimension === '2d'" :class="{ active: sceneDimension === '2d' }" @click="sceneDimension = '2d'">二维</button><button :aria-pressed="sceneDimension === '3d'" :class="{ active: sceneDimension === '3d' }" @click="sceneDimension = '3d'">三维</button></span><span class="map-view-switch"><button :class="{ active: mapViewMode === 'all' }" :aria-pressed="mapViewMode === 'all'" @click="mapViewMode = 'all'">全厂</button><button :class="{ active: mapViewMode === 'vehicles' }" :aria-pressed="mapViewMode === 'vehicles'" :disabled="!geometry.cars.length" @click="mapViewMode = 'vehicles'">车辆附近</button></span><button v-if="cadBackground" class="map-layer-toggle" :aria-pressed="showCadReference" @click="showCadReference = !showCadReference">{{ showCadReference ? '关闭CAD参考' : '查看CAD参考' }}</button></div></header>
          <SimulationThreeView v-if="geometry.nodes.length && sceneDimension === '3d'" :geometry="geometry" :fresh="fresh" :selected="selectedVehicle" :focus-vehicles="mapViewMode === 'vehicles'" :edge-colors="threeEdgeColors" @select="selectVehicleTask" />
          <svg v-else-if="geometry.nodes.length" class="simulation-map" :class="{ 'vehicle-focus': mapViewMode === 'vehicles' }" :viewBox="mapViewBox" role="img" aria-label="仿真地图及虚拟车辆位置">
            <defs><pattern id="simulation-grid" width="40" height="40" patternUnits="userSpaceOnUse"><path d="M 40 0 L 0 0 0 40" fill="none" stroke="#26364b" stroke-width="1"/></pattern><marker id="simulation-route-arrow" markerUnits="userSpaceOnUse" markerWidth="3.2" markerHeight="3.2" refX="2.6" refY="1.6" orient="auto"><path d="M0,0 L3.2,1.6 L0,3.2 Z" fill="#5fa8b8"/></marker></defs>
            <rect width="800" height="430" fill="url(#simulation-grid)"/>
            <image v-if="showCadReference && cadBackground" class="cad-reference" :href="cadBackground.href" :x="cadBackground.x" :y="cadBackground.y" :width="cadBackground.width" :height="cadBackground.height" :opacity="cadBackground.opacity" preserveAspectRatio="none" />
            <g v-if="mapAreas.length" class="map-zones"><g v-for="area in mapAreas" :key="area.id" :data-area-id="area.id"><path v-if="area.showBoundary && area.path" :d="area.path" :fill="area.fillColor" :fill-opacity="area.fillOpacity" :stroke="area.strokeColor" stroke-opacity=".78" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" /><polygon v-else-if="area.showBoundary" :points="area.points" :fill="area.fillColor" :fill-opacity="area.fillOpacity" :stroke="area.strokeColor" :stroke-opacity=".55" /><text v-if="showMapAreaLabel(area.id)" :x="area.labelX" :y="area.labelY" :fill="area.strokeColor">{{ area.text }}</text></g></g>
            <g class="roads"><path v-for="edge in geometry.edges" :key="edge.key" :d="edge.path" :class="[taskRouteEdgeClass(edge.id), { blocked: !!edge.id && appliedBlockedEdges.includes(edge.id) }]" marker-end="url(#simulation-route-arrow)" /></g>
            <g v-for="node in geometry.nodes" :key="node.id" class="map-node" :class="[taskRouteNodeClass(node.id), { dense: denseMap }]"><title>{{ mapNodeLabel(node) }} · {{ node.type || '地图节点' }}</title><circle :cx="node.x" :cy="node.y" :r="mapNodeRadius(node)"/><text v-if="!denseMap || showDenseNodeLabel(node)" :x="node.x + 7" :y="node.y - 7">{{ mapNodeLabel(node) }}</text></g>
            <g v-for="car in geometry.cars" :key="car.id" class="map-car" :class="{ stale: !fresh, offline: car.offline, selected: selectedVehicle === car.id }" :style="{ '--car-color': vehicleMapColor(car) }" :transform="`translate(${car.x} ${car.y})`" role="button" tabindex="0" @click="selectVehicleTask(car.id)" @keydown.enter="selectVehicleTask(car.id)">
              <circle :r="vehicleBodySize(car.id).length * .72"/><g :transform="`rotate(${-(car.angle ?? 0) * 180 / Math.PI})`"><rect :x="-vehicleBodySize(car.id).length / 2" :y="-vehicleBodySize(car.id).width / 2" :width="vehicleBodySize(car.id).length" :height="vehicleBodySize(car.id).width" rx="2"/><path :d="`M ${vehicleBodySize(car.id).length / 2 - 1} 0 l -4 -2.4 v 4.8 z`"/></g><g v-if="car.loaded" class="map-load-badge"><rect x="5" y="3" width="9" height="7" rx="1"/><text x="9.5" y="8.1" text-anchor="middle">箱</text></g><text x="0" :y="-vehicleBodySize(car.id).length / 2 - 6" text-anchor="middle">{{ car.id }}</text><title>{{ car.id }} · {{ humanLocation(car.station) }} · {{ vehicleVisualLabel(car) }} · {{ car.loaded ? '载箱' : '空载' }}</title>
            </g>
          </svg>
          <div v-else class="map-empty"><strong>当前没有可显示的位置反馈</strong><span>启动环境后，这里会显示车辆在全厂里的实际位置。</span></div>
          <div class="map-run-legend"><span><i class="map-legend-dot running"></i>执行中</span><span><i class="map-legend-dot waiting"></i>作业等待/载箱待命</span><span><i class="map-legend-dot idle"></i>空载待命</span><span><i class="map-legend-dot blocked"></i>道路受阻</span><span><i class="map-legend-dot fault"></i>暂停/故障</span><span><i class="map-legend-dot offline"></i>离线/状态过期</span><span><i class="map-load-legend"></i>箱 = 当前载箱</span></div>
          <div class="map-caption"><span>彩色分区依次为一组、二组、三组、四组和末组；站点详细名称可悬停查看。{{ showCadReference && cadBackground ? ' CAD参考层只用于核对位置。' : '' }}</span><span>{{ vehicleIds.length }} 辆 · {{ offlineVehicleCount }} 辆离线</span></div>
        </section>
        </slot>
      </div>
    </section>

    <section v-if="activeStage === 'tasks'" class="stage-surface tasks-stage">
      <section class="simulation-panel task-command-panel">
        <header>
          <h3>任务运行</h3>
          <span :class="['state-pill', status?.running ? 'pass' : simulationTasks.length ? 'waiting' : 'unknown']">{{ status?.running ? '隔离后台在线' : simulationTasks.length ? '最近报告证据' : '仿真未运行' }}</span>
        </header>
        <div class="task-counts">
          <article><small>任务总数</small><b>{{ taskCounts.total }}</b></article>
          <article><small>执行中</small><b>{{ taskCounts.running }}</b></article>
          <article><small>等待中</small><b>{{ taskCounts.waiting }}</b></article>
          <article><small>已完成</small><b>{{ taskCounts.completed }}</b></article>
          <article :class="{ fault: taskCounts.failed > 0 }"><small>异常</small><b>{{ taskCounts.failed }}</b></article>
        </div>
        <div class="task-filter-row">
          <label>状态<select v-model="simulationTaskFilter" @change="refreshSimulationTasks"><option value="All">全部</option><option value="Active">执行中</option><option value="Completed">已完成</option><option value="Failed">失败/异常</option></select></label>
          <label class="task-search">搜索<input v-model.trim="simulationTaskSearch" placeholder="任务号、工位或车辆" @keyup.enter="refreshSimulationTasks" /></label>
          <button :disabled="taskReading || (!status?.running && !report)" @click="refreshSimulationTasks"><IconRefresh :size="15" />{{ taskReading ? '读取中' : '刷新任务' }}</button>
        </div>
        <p v-if="taskError" class="simulation-error" role="alert">{{ industrialText(taskError) }}</p>
      </section>

      <div class="task-workbench">
        <section class="simulation-panel task-table-panel">
          <header><div><h3>任务队列</h3><small>{{ simulationTasks.length }} 条 · 实际派车来自正式调度</small></div></header>
          <div class="task-table-scroll">
            <table class="task-table">
              <thead><tr><th>工位</th><th>车辆</th><th>状态</th><th>更新时间</th></tr></thead>
              <tbody>
                <tr v-for="task in simulationTasks" :key="task.taskId" :class="{ selected: selectedTaskId === task.taskId, 'traffic-waiting': !!taskWaitingRequest(task.taskId) }" role="button" tabindex="0" @click="selectSimulationTask(task.taskId)" @keydown.enter="selectSimulationTask(task.taskId)" @keydown.space.prevent="selectSimulationTask(task.taskId)">
                  <td><b>{{ task.stationId || '未提供' }}</b></td>
                  <td>{{ task.vehicleId || '尚未派车' }}</td>
                  <td><span :class="['state-pill', taskTone(task.stage)]">{{ taskStageLabel(task.stage) }}</span><small v-if="taskWaitingRequest(task.taskId)" class="task-waiting-signal">等待路权 · {{ taskWaitingSeconds(task.taskId) ?? '—' }} 秒</small><small v-if="task.errorMessage" class="task-error-text">{{ industrialText(task.errorMessage) }}</small></td>
                  <td>{{ stamp(task.updatedAt) }}</td>
                </tr>
                <tr v-if="!simulationTasks.length"><td colspan="4" class="task-empty">{{ taskReading ? '正在读取任务…' : status?.running || report ? '当前筛选没有任务。' : '启动隔离仿真后显示任务数据。' }}</td></tr>
              </tbody>
            </table>
          </div>
        </section>

        <section class="simulation-panel task-detail-panel">
          <template v-if="selectedTaskDetail">
            <header>
              <h3>{{ selectedTaskDetail.task.stationId }} · {{ selectedTaskDetail.task.vehicleId || '等待派车' }}</h3>
              <span :class="['state-pill', taskTone(selectedTaskDetail.task.stage)]">{{ taskStageLabel(selectedTaskDetail.task.stage) }}</span>
            </header>
            <ol class="task-step-list" aria-label="任务九步">
              <li v-for="step in selectedTaskDetail.steps" :key="evidenceStepKey(step)"
                :class="{ current: step.isCurrent, completed: step.status === 'Completed', failed: ['Failed','ManualIntervention'].includes(step.status) }">
                <button type="button" :aria-pressed="selectedEvidenceStep === step" aria-controls="task-evidence-route"
                  @click="selectEvidenceStep(step)">
                  <span class="task-step-number">{{ step.sequence }}</span>
                  <span><strong>{{ taskStepLabel(step.stepType) }}</strong><small>{{ taskStatusLabel(step.status) }}<template v-if="step.elapsedText && /^\d/.test(step.elapsedText)"> · {{ step.elapsedText }}</template></small></span>
                </button>
              </li>
            </ol>
          </template>
          <p v-else class="task-empty">{{ taskError || (selectedTaskId ? '正在读取任务详情…' : '选择左侧任务查看详情。') }}</p>
        </section>
      </div>

      <section v-if="selectedTaskDetail" id="task-evidence-route" class="simulation-panel task-route-panel">
        <header>
          <h3 v-if="selectedEvidenceStep">第 {{ selectedEvidenceStep.sequence }} 步 · {{ taskStepLabel(selectedEvidenceStep.stepType) }}</h3>
          <h3 v-else>任务路线</h3>
          <span class="scope-label">{{ archivedTaskEvidence ? '历史记录' : '当前实例' }}</span>
        </header>
        <div v-if="selectedTaskRouteLeg" class="task-route-controls">
          <p class="task-route-endpoints"><i :style="{ background: evidenceRouteColor }" /><span>{{ selectedTaskRouteLeg.source }}</span><span aria-hidden="true">→</span><span>{{ selectedTaskRouteLeg.destination }}</span></p>
          <label v-if="selectedStepRoutes.length > 1">
            路线记录
            <select :value="selectedTaskRouteLeg.commandId" @change="selectedTaskRouteCommandId = ($event.target as HTMLSelectElement).value">
              <option v-for="(leg, index) in selectedStepRoutes" :key="leg.commandId" :value="leg.commandId">{{ index + 1 }} / {{ selectedStepRoutes.length }} · {{ leg.source }} → {{ leg.destination }}</option>
            </select>
          </label>
        </div>
        <p v-if="!selectedTaskRouteLeg" class="task-route-message">本步没有路线记录<template v-if="evidenceStepCommands.length"> · {{ routeActionLabel(evidenceStepCommands.at(-1)?.route?.action) }} · {{ actionStateLabel(evidenceStepCommands.at(-1)?.status) }}</template></p>
        <p v-else-if="!selectedRouteExistsOnMap" class="task-route-message">所选路线无法在当前实例地图中定位，可展开原始命令查看节点。</p>
        <div v-if="selectedEvidenceStep && (selectedEvidenceStep.waitingExplanation || (!selectedTaskRouteLeg && (selectedEvidenceStep.observedPhase || selectedEvidenceStep.completionRequirement)))" class="task-step-observation">
          {{ industrialText(selectedEvidenceStep.waitingExplanation || selectedEvidenceStep.observedPhase || selectedEvidenceStep.completionRequirement) }}
        </div>
        <div v-if="selectedEvidenceStep === selectedTaskCurrentStep && selectedTaskTrafficRequest?.state === 'Waiting'" class="task-route-now">
          <strong>等待路权<template v-if="selectedTaskWaitSeconds !== null"> · {{ selectedTaskWaitSeconds }} 秒</template></strong>
          <span v-if="selectedTaskWaitGroups.length">等待 {{ selectedTaskWaitGroups.map(group => group.vehicleId).join('、') }} 释放</span>
          <span v-else>{{ industrialText(selectedTaskTrafficRequest.reason) }}</span>
        </div>
        <div class="task-route-map-wrap">
          <slot name="task-route-map" :definition="status?.map" :cars="archivedTaskEvidence ? undefined : cars"
            :fresh="fresh" :selected-vehicle="selectedTaskDetail.task.vehicleId || undefined"
            :road-highlights="evidenceRoadHighlights" :route-endpoints="evidenceRouteEndpoints">
            <svg v-if="geometry.nodes.length" class="task-route-map" viewBox="0 0 800 430" role="img" aria-label="所选步骤路线">
              <path v-for="edge in geometry.edges" :key="edge.key" :d="edge.path" fill="none"
                :stroke="evidenceRoadHighlights[edge.id ?? ''] || '#9baec8'" :stroke-width="evidenceRoadHighlights[edge.id ?? ''] ? 4 : 1" />
              <g v-for="node in geometry.nodes.filter(node => evidenceRouteEndpoints.some(endpoint => endpoint.nodeId === node.id))" :key="node.id">
                <circle :cx="node.x" :cy="node.y" r="5" :fill="evidenceRouteColor" stroke="white" stroke-width="2" />
                <text :x="node.x + 9" :y="node.y - 8">{{ evidenceRouteEndpoints.find(endpoint => endpoint.nodeId === node.id)?.label }}</text>
              </g>
              <template v-if="!archivedTaskEvidence">
                <g v-for="car in geometry.cars" :key="car.id" :transform="`translate(${car.x} ${car.y})`">
                  <circle r="5" :fill="vehicleMapColor(car)" stroke="#243a56" stroke-width="1.5" />
                  <text v-if="car.id === selectedTaskDetail.task.vehicleId" x="8" y="12">{{ car.id }}</text>
                  <title>{{ car.id }} · {{ vehicleVisualLabel(car) }}</title>
                </g>
              </template>
            </svg>
            <p v-else class="task-route-message">当前没有可预览的地图。</p>
          </slot>
        </div>
        <details v-if="selectedEvidenceStep" :key="`${selectedTaskId}-${evidenceStepKey(selectedEvidenceStep)}`" class="task-evidence-details">
          <summary>本步证据</summary>
          <p v-if="selectedEvidenceStep.startedAt">开始 {{ stamp(selectedEvidenceStep.startedAt) }}<template v-if="selectedEvidenceStep.completedAt"> · 完成 {{ stamp(selectedEvidenceStep.completedAt) }}</template></p>
          <p v-if="selectedEvidenceStep.observedPhase">{{ industrialText(selectedEvidenceStep.observedPhase) }}</p>
          <p v-if="selectedEvidenceStep.completionRequirement">{{ industrialText(selectedEvidenceStep.completionRequirement) }}</p>
          <p v-if="selectedEvidenceStep.evidenceBoundary">{{ industrialText(selectedEvidenceStep.evidenceBoundary) }}</p>
          <p v-for="handshake in evidenceStepHandshakes" :key="handshake.cycleId">{{ handshakePhaseLabel(handshake.phase) }} · {{ industrialText(handshake.reason) }}</p>
          <p v-if="selectedEvidenceStep.errorMessage">{{ industrialText(selectedEvidenceStep.errorMessage) }}</p>
          <p v-if="!selectedEvidenceStep.startedAt && !selectedEvidenceStep.observedPhase && !selectedEvidenceStep.completionRequirement && !selectedEvidenceStep.evidenceBoundary && !selectedEvidenceStep.errorMessage && !evidenceStepHandshakes.length">本步暂无额外证据。</p>
        </details>
      </section>

      <section v-if="selectedTaskDetail" :key="selectedTaskId" class="simulation-panel task-evidence-archive">
        <details class="task-evidence-details">
          <summary>原始命令与任务编号</summary>
          <p class="evidence-raw">{{ selectedTaskDetail.task.taskId }}</p>
          <p v-if="selectedTaskDetail.evidenceMessage">{{ industrialText(selectedTaskDetail.evidenceMessage) }}</p>
          <details v-for="(command, index) in selectedTaskDetail.commands ?? []" :key="command.commandId" class="task-evidence-details">
            <summary>命令 {{ index + 1 }} · {{ actionStateLabel(command.status) }}<template v-if="command.route?.vendorSites?.length"> · {{ command.route.vendorSites[0] }} → {{ command.route.vendorSites.at(-1) }}</template></summary>
            <p class="evidence-raw">{{ command.commandId }}<template v-if="command.vendorCommandId"> · {{ command.vendorCommandId }}</template> · 尝试 {{ command.attemptCount }} 次</p>
            <p v-if="command.lastError">{{ industrialText(command.lastError) }}</p>
            <p v-if="command.route?.nodeIds?.length" class="evidence-raw">{{ command.route.nodeIds.join(' → ') }}</p>
            <p v-if="command.route?.vendorSites?.length" class="evidence-raw">{{ command.route.vendorSites.join(' → ') }}</p>
          </details>
        </details>
        <details class="task-evidence-details">
          <summary>PLC 与路权</summary>
          <p v-if="selectedTaskDetail.waitingExplanation">{{ industrialText(selectedTaskDetail.waitingExplanation) }}</p>
          <p v-if="taskSnapshotError">路权读取失败 · {{ industrialText(taskSnapshotError) }}</p>
          <p v-if="selectedTaskTrafficRequest">{{ trafficWaitingText(selectedTaskTrafficRequest) }}</p>
          <p v-for="group in selectedTaskWaitGroups" :key="`${group.vehicleId}-${group.taskId}`"><b>{{ group.vehicleId }}</b> · {{ group.resources.map(routeResourceLabel).join('、') }}<template v-if="group.heartbeatExpired"> · 心跳已过期</template></p>
          <p v-if="selectedTaskTrafficRequest?.resourceIds?.length">申请 {{ selectedTaskTrafficRequest.resourceIds.length }} 项路权 · {{ selectedTaskTrafficRequest.resourceIds.map(routeResourceLabel).join('、') }}</p>
          <p v-for="resource in selectedTaskDetail.resources ?? []" :key="resource.resourceId"><b>{{ routeResourceLabel(resource.resourceId) }}</b> · {{ resource.vehicleId }} · {{ resource.heartbeatExpired ? '心跳过期' : '占用中' }} · {{ stamp(resource.expiresAt) }}</p>
          <p v-for="event in selectedTaskHandovers.slice(0, 12)" :key="event.eventId">{{ event.vehicleId }} · {{ event.kind === 'ResourceAcquired' ? '取得' : '核验后释放' }} · {{ stamp(event.occurredAt) }} · {{ event.resourceIds.map(routeResourceLabel).join('、') }}</p>
          <p v-for="signal in selectedTaskDetail.plcConditions ?? []" :key="`${signal.stationId}-${signal.variableName}`"><b>{{ plcSignalLabel(signal.variableName) }}</b> · {{ plcConditionLabel(signal.currentValue, signal.expectedValue) }}</p>
          <p v-for="handshake in selectedTaskDetail.handshakes ?? []" :key="handshake.cycleId"><b>{{ handshakePhaseLabel(handshake.phase) }}</b> · {{ industrialText(handshake.reason) }}<template v-if="handshake.pendingSignal"> · 等待{{ plcSignalLabel(handshake.pendingSignal) }}{{ handshake.pendingValue ? '置位' : '复位' }}</template></p>
        </details>
        <details class="task-evidence-details">
          <summary>时间轴 · {{ taskTimeline.length }} 条</summary>
          <ol class="task-timeline"><li v-for="event in taskTimeline.slice(-160)" :key="event.id"><time>{{ stamp(event.occurredAt) }}</time><b :title="industrialText(event.title)">{{ industrialText(event.title) }}</b><span class="task-timeline-copy" :title="industrialText(event.summary)"><em>{{ event.source }}</em><span>{{ industrialText(event.summary) }}</span></span></li></ol>
        </details>
      </section>
    </section>

    <section v-if="activeStage === 'environment'" class="stage-surface environment-stage">
      <section class="simulation-panel scheme-panel" aria-labelledby="simulation-schemes">
        <header><div><p class="section-kicker">日常开测</p><h3 id="simulation-schemes">保存方案，下次直接开测</h3></div><span class="scope-label">记住地图、车辆、工位和场景</span></header>
        <p>保存后可重复创建干净实例。控制器重开会记住最近选择；已有任务、故障和恢复决策仍保留在原实例中。</p>
        <div class="form-row"><label>保存方案名称<input v-model.trim="schemeName" maxlength="80" placeholder="例如：当前地图双车持续排队" /></label><label>已保存方案<select v-model="selectedScheme" aria-label="选择仿真方案"><option value="">请选择方案</option><option v-for="scheme in status?.schemes ?? []" :key="scheme.id" :value="scheme.id">{{ scheme.name }} · {{ scheme.vehicleCount }}辆 · {{ scheme.stationCount }}工位 · {{ scheme.plcTableName ? `点表 ${scheme.plcTableName}` : '旧方案未绑定点表' }}</option></select></label></div>
        <div class="toolbar"><button :disabled="!available || !schemeName || active || starting" @click="saveScheme">保存当前方案</button><button :disabled="!available || !selectedScheme || !selectedSchemeReady || !!status?.running || starting || active" @click="action('scheme-select', { id: selectedScheme })">按方案新建实例</button><button class="primary" :disabled="!available || !selectedScheme || !selectedSchemeReady || !!status?.running || starting || active" @click="action('scheme-start', { id: selectedScheme })">一键开测</button><button :disabled="!available || !selectedScheme || !!status?.running || starting || active" @click="deleteScheme">删除所选方案</button></div>
        <p v-if="selectedScheme && !selectedSchemeReady" class="inline-warning">该旧方案没有绑定PLC点表，不能创建实例。请重新选择地图并上传对应CSV，然后另存为新方案。</p>
        <small>一键开测会新建实例、启动虚拟设备，再运行方案中保存的场景。运行中请先停止当前环境。</small>
        <details v-if="status?.recentInstances?.length" class="recent-instances"><summary>最近实例 · {{ status.recentInstances.length }} 个</summary><ol><li v-for="instance in status.recentInstances" :key="instance.rootName"><b>{{ instance.mapName || '未命名地图' }}</b><span>{{ instance.vehicleCount }}辆车 · {{ instance.stationCount }}工位 · {{ stamp(instance.updatedAt) }}</span><small>{{ instance.rootName }}</small></li></ol></details>
      </section>
      <section v-if="versionComparison" class="simulation-panel version-panel" aria-labelledby="simulation-versions">
        <header><div><p class="section-kicker">结果适用范围</p><h3 id="simulation-versions">仿真与现场后台版本对照</h3></div><span :class="['state-pill', versionComparison.status === 'matched' ? 'pass' : versionComparison.status === 'different' ? 'waiting' : 'unknown']">{{ versionComparison.status === 'matched' ? '所列项目一致' : versionComparison.status === 'different' ? '存在差异' : '尚未核定' }}</span></header>
        <p>{{ industrialText(versionComparison.message || '读取构建身份和关键配置后才能比较。') }}</p>
        <div class="version-builds"><div><small>现场后台</small><b>{{ versionComparison.field?.informationalVersion || versionComparison.field?.buildId || '未取得构建身份' }}</b></div><div><small>仿真后台</small><b>{{ versionComparison.simulation?.informationalVersion || versionComparison.simulation?.buildId || '未取得构建身份' }}</b></div></div>
        <div v-if="versionComparison.differences?.length" class="report-table"><table><thead><tr><th>对照项目</th><th>现场</th><th>仿真</th></tr></thead><tbody><tr v-for="(difference, index) in versionComparison.differences" :key="index"><td>{{ difference.label || '待核定配置' }}</td><td>{{ industrialText(difference.field ?? '未取得') }}</td><td>{{ industrialText(difference.simulation ?? '未取得') }}</td></tr></tbody></table></div>
        <small>{{ versionComparison.scope || '构建和配置比较不替代现场机械动作、定位与安全联锁验收。' }} · 对照时间 {{ stamp(versionComparison.observedAt ?? undefined) }}</small>
      </section>
      <section class="new-instance-control">
        <div><p class="section-kicker">沿用地图新建</p><h3>沿用当前地图新建隔离实例</h3><p>不复制数据库、设备历史或已经注入的故障；适用于修改车辆数量后重新开一轮。</p></div>
        <label>车辆数量<input v-model.number="newVehicleCount" aria-label="新实例车辆数" type="number" min="1" max="8" step="1" :disabled="!available || !!status?.running || starting || active" @change="vehicleCountChanged" /></label>
        <button :disabled="!available || !!status?.running || starting || active || !Number.isInteger(newVehicleCount) || newVehicleCount < 1 || newVehicleCount > 8" @click="action('new-instance', { vehicles: newVehicleCount })">沿用当前地图新建</button>
      </section>
      <section class="simulation-panel instance-designer" aria-labelledby="simulation-instance-designer">
        <header><div><p class="section-kicker">环境配置</p><h3 id="simulation-instance-designer">地图、工位与虚拟车辆</h3></div><span class="scope-label">停止环境后创建新的隔离实例</span></header>
        <p class="designer-intro">支持本项目 <code>.agvmap.json</code>，并兼容只读 <code>.rpj</code> 工程包。导入后必须核对语义工位、资源边界和每辆车的唯一起点；这些数据只进入新实例。</p>
        <div class="map-import">
          <div class="toolbar"><button :disabled="busy || !dispatchStore.fresh || dispatchStore.demoMode || !dispatchStore.snapshot?.map || !!status?.running || starting || active" @click="loadCurrentMap"><IconRefresh :size="15" />拉取当前工程（地图+参数+点表）</button><label class="loaded-config"><input v-model="useFieldVehiclePositions" type="checkbox" />同时按当前真车位置设置初始点</label></div>
          <label class="file-picker">选择地图文件<input type="file" accept=".json,.agvmap.json,.rpj,application/json" :disabled="busy || !!status?.running || starting || active" @change="loadMapFile" /></label>
          <div class="map-import-summary">
            <b v-if="mapFileName">{{ mapFileName }}</b>
            <span v-if="liveMapIdentity">{{ liveMapIdentity }}</span>
            <span v-if="mapDefinition">{{ mapDefinition.nodes.length }} 节点 · {{ mapDefinition.edges.length }} 道路 · {{ mapDefinition.resources.length }} 资源</span>
            <span v-if="mapIdentityDraft?.definitionHash">地图摘要 {{ mapIdentityDraft.definitionHash.slice(0, 12) }}…</span>
            <span v-else-if="!mapDefinition">尚未载入自定义地图</span>
          </div>
        </div>
        <div class="plc-table-binding">
          <div class="designer-section-title"><span>PLC</span><div><b>绑定当前地图对应的点表</b><small>点表随方案保存；创建实例时复制为实例专属 simulation-signals.csv。载入后可按节点自动映射全部工位，再逐项修改。</small></div></div>
          <div class="form-row">
            <label class="file-picker">选择 PLC 点表 CSV<input type="file" accept=".csv,text/csv" :disabled="busy || !!status?.running || starting || active" @change="loadPlcTableFile" /></label>
            <button :disabled="busy || plcTableReading || !!status?.running || starting || active || !status?.plcSignalTable" @click="loadCurrentPlcTable">载入当前实例点表</button>
          </div>
          <div v-if="plcTableMetadata" class="plc-table-summary">
            <b>{{ plcTableFileName }}</b>
            <span>{{ plcTableMetadata.stationCount ?? plcStationCatalog.length }} 工位 · {{ plcTableMetadata.signalCount ?? 0 }} 条点位 · {{ plcTableMetadata.bytes ?? 0 }} 字节</span>
            <code v-if="plcTableMetadata.sourceSha256">原工程 SHA256 {{ plcTableMetadata.sourceSha256 }}</code>
            <code>仿真副本 SHA256 {{ plcTableSha256 }}</code>
            <small v-if="plcTableMetadata.sourceFile">来源：{{ plcTableMetadata.sourceDeviceName || plcTableMetadata.sourceDeviceId || '当前工程' }} · {{ plcTableMetadata.sourceFile }}</small>
            <button :disabled="!mapDefinition || !plcStationCatalog.length" @click="autoMapStations()">按地图和点表自动映射全部工位</button>
          </div>
          <p v-else class="inline-warning">未选择点表。仿真不会使用仓库全局 PLC 点表，也不会悄悄沿用现场后台实验室文件。</p>
        </div>
        <template v-if="mapDefinition">
          <div class="designer-section-title"><span>01</span><div><b>业务拓扑映射</b><small>把地图节点绑定到当前 28 阶段业务模型。</small></div></div>
          <div class="toolbar station-page-tools">
            <input v-model="stationFilter" type="search" aria-label="搜索工位或取货节点" placeholder="搜索工位或取货节点" />
            <button type="button" :disabled="stationPage <= 1" @click="stationPage--">上一页</button>
            <span>{{ filteredStations.length }} 个工位 · 第 {{ stationPage }} / {{ stationPageCount }} 页</span>
            <button type="button" :disabled="stationPage >= stationPageCount" @click="stationPage++">下一页</button>
          </div>
          <fieldset :disabled="busy || !!status?.running || starting || active" class="topology-grid">
            <div class="station-designer">
              <datalist id="simulation-pickup-nodes">
                <template v-for="group in siteOptions" :key="group.label">
                  <option v-for="site in group.options" :key="site.value" :value="site.value" :label="`${group.label} · ${site.label}`" />
                </template>
              </datalist>
              <p v-if="!visibleStations.length">没有匹配的工位。</p>
              <div v-for="{ station, index } in visibleStations" :key="index" class="station-config-row">
                <label>工位编号<input v-model.trim="station.stationId" placeholder="CNC-001" maxlength="7" /></label>
                <label>工位取货节点<input v-model.trim.lazy="station.pickupSite" list="simulation-pickup-nodes" placeholder="输入或选择节点" @change="resetVehicleOrigins" /></label>
                <label>信号依据<select v-model="station.signalSource"><option value="verified">当前有效点表</option><option value="isolated-synthetic">隔离合成点位（现场未确认）</option></select></label>
                <label>派车优先级<input :value="station.priority" type="number" min="0" max="100" step="1" placeholder="沿用默认" @input="station.priority = ($event.target as HTMLInputElement).value === '' ? undefined : Number(($event.target as HTMLInputElement).value)" /></label>
                <button :disabled="stationDrafts.length <= 1" @click="stationDrafts.splice(index, 1); resetVehicleOrigins()">删除工位</button>
                <small :class="{ 'inline-warning': station.signalSource === 'isolated-synthetic' }">{{ station.signalSource === 'isolated-synthetic' ? '仅用于隔离仿真，不代表现场已有此工位和地址。' : (status?.stationCatalog?.find(item => item.stationId === station.stationId && item.signalSource === 'verified')?.sourceLabel || '必须与当前已确认且启用的点表工位一致。') }}</small>
              </div>
              <button :disabled="stationDrafts.length >= MAX_SIMULATION_STATIONS" @click="addStation">增加隔离仿真工位</button><small>本页最多配置 {{ MAX_SIMULATION_STATIONS }} 个工位；可按当前 CAD 仿真图和点表一次自动映射，再逐项修改。现场未确认的点位会保留为“隔离合成点位”。</small>
            </div>
            <label>主要倒料候车位
              <ElSelectV2 v-model="waitingSite" :options="siteOptions" :disabled="configurationLocked" :teleported="false"
                class="topology-select" filterable aria-label="主要倒料候车位" placeholder="搜索候车位编号"
                @change="additionalWaitingSites = additionalWaitingSites.filter(site => site !== waitingSite); resetVehicleOrigins()" />
              <small>倒料机忙时，载着满箱的车可以先停在这里等。</small>
            </label>
            <label>其他倒料候车位（可多选）
              <ElSelectV2 v-model="additionalWaitingSites" :options="extraWaitingOptions" :disabled="configurationLocked" :teleported="false"
                class="topology-select" multiple collapse-tags :max-collapse-tags="2" filterable
                aria-label="其他倒料候车位" placeholder="搜索并选择候车位" @change="resetVehicleOrigins" />
              <small>多车排队时，调度会从已配置的候车位中选当前可用的位置。</small>
            </label>
            <label>倒料停车点
              <ElSelectV2 v-model="dumpingSite" :options="siteOptions" :disabled="configurationLocked" :teleported="false"
                class="topology-select" filterable aria-label="倒料停车点" placeholder="搜索倒料点编号" @change="resetVehicleOrigins" />
              <small>车进倒料区后停下来，配合设备完成倒料的位置。</small>
            </label>
            <label>检修／观察点
              <ElSelectV2 v-model="inspectionSite" :options="siteOptions" :disabled="configurationLocked" :teleported="false"
                class="topology-select" filterable aria-label="检修／观察点" placeholder="搜索检修或观察点" @change="resetVehicleOrigins" />
              <small>给仿真保留的检修停车或设备观察位置。</small>
            </label>
            <label>倒料区占用范围
              <ElSelectV2 v-model="dumperResourceId" :options="dumperResourceOptions" :disabled="configurationLocked" :teleported="false"
                class="topology-select" filterable aria-label="倒料区占用范围" placeholder="搜索区域编号" />
              <small>地图上要保护的倒料区域。容量为 1 时，同一时间只允许 1 台车占用。</small>
            </label>
            <label>取箱到收尾的并行上限
              <input v-model.number="downstreamAdmissionCapacity" type="number" min="0" max="8" step="1" />
              <small>0 表示不额外限流，车辆、路权和设备许可照常检查。填正数时，一单从派车去取箱到返箱并核验收尾占一个名额。</small>
            </label>
          </fieldset>
          <div class="vehicle-designer">
            <div class="designer-section-title"><span>02</span><div><b>车辆画像 · {{ customVehicles.length }} 辆</b><small>配置车辆、电机、电池和动作模型参数。</small></div></div>
            <div v-for="(vehicle, index) in customVehicles" :key="index" class="vehicle-config-row">
              <label>车辆 ID<input v-model.trim="vehicle.id" maxlength="32" /></label>
              <label>车辆起点
                <ElSelectV2 v-model="vehicle.initialSite" :options="siteOptions" :disabled="configurationLocked" :teleported="false"
                  class="topology-select" filterable :aria-label="`${vehicle.id} 的车辆起点`" placeholder="搜索车辆起点" />
              </label>
              <label>车长 / m<input v-model.number="vehicle.lengthMeters" type="number" min="0.1" max="20" step="0.01" @input="updateVehicleFootprint(vehicle)" /></label>
              <label>车宽 / m<input v-model.number="vehicle.widthMeters" type="number" min="0.1" max="20" step="0.01" @input="updateVehicleFootprint(vehicle)" /></label>
              <label>电量 / %<input v-model.number="vehicle.batteryPercent" type="number" min="0" max="100" step="1" /></label>
              <label>限速倍率<input v-model.number="vehicle.speedScale" type="number" min="0.1" max="10" step="0.05" /><small>默认 0.75；道路限速 1.0 m/s 时对应实车 0.75 m/s。调高仅用于加速仿真。</small></label>
              <label class="loaded-config"><input v-model="vehicle.initialLoaded" type="checkbox" />初始载箱</label>
              <button :disabled="customVehicles.length <= 1" @click="customVehicles.splice(index, 1); resetVehicleOrigins()">删除</button>
              <details class="vehicle-advanced"><summary>高级物理参数 · 加速度 {{ vehicle.accelerationMetersPerSecondSquared ?? 0.6 }} m/s² · 动作 {{ vehicle.operationSeconds ?? 2 }} 秒</summary>
                <div class="form-row"><label>加速度 / m/s²<input v-model.number="vehicle.accelerationMetersPerSecondSquared" type="number" min="0.05" max="10" step="0.05" /></label><label>取放动作时长 / 秒<input v-model.number="vehicle.operationSeconds" type="number" min="0.1" max="120" step="0.1" /></label></div>
                <div class="form-row"><label>行驶耗电 / %每米<input v-model.number="vehicle.travelCostPercentPerMeter" type="number" min="0" max="10" step="0.001" /></label><label>待机耗电 / %每小时<input v-model.number="vehicle.idleCostPercentPerHour" type="number" min="0" max="100" step="0.01" /></label></div>
                <div class="voltage-curve-editor">
                  <div class="designer-subhead"><b>电池电压曲线 · {{ vehicle.voltageCurve?.length ?? 0 }} 点</b><small>支持2–20个唯一电量点；创建时按电量升序保存。</small></div>
                  <div class="voltage-point" v-for="(point, pointIndex) in vehicle.voltageCurve" :key="pointIndex">
                    <label>电量 / %<input v-model.number="point.percent" type="number" min="0" max="100" step="0.1" /></label>
                    <label>电压 / V<input v-model.number="point.voltage" type="number" min="0.1" max="1000" step="0.1" /></label>
                    <button :disabled="(vehicle.voltageCurve?.length ?? 0) <= 2" @click="removeVoltagePoint(vehicle, pointIndex)">删除点</button>
                  </div>
                  <div class="toolbar"><button :disabled="(vehicle.voltageCurve?.length ?? 0) >= 20" @click="addVoltagePoint(vehicle)">增加曲线点</button><button @click="sortVoltageCurve(vehicle)">按电量排序</button></div>
                </div>
                <div class="form-row"><label>充电 / %每小时<input v-model.number="vehicle.chargingGainPercentPerHour" type="number" min="0" max="100" step="0.1" /></label></div>
              </details>
            </div>
            <button :disabled="customVehicles.length >= 8" @click="customVehicles.push({ id: `AGV-SIM-${String(customVehicles.length + 1).padStart(2, '0')}`, initialSite: '', radiusMeters: 0.49, lengthMeters: 0.8, widthMeters: 0.52, initialLoaded: false, batteryPercent: 86, speedScale: 0.75, accelerationMetersPerSecondSquared: 0.6, operationSeconds: 2, travelCostPercentPerMeter: 0.01, idleCostPercentPerHour: 0.1, voltageCurve: [{ percent: 0, voltage: 21 }, { percent: 100, voltage: 26 }], chargingGainPercentPerHour: 20 }); resetVehicleOrigins()">增加车辆</button>
          </div>
          <div v-if="!configurationValid" class="inline-warning"><b>创建前还需处理：</b><ul><li v-for="problem in configurationProblems" :key="problem">{{ problem }}</li></ul></div>
          <div class="toolbar designer-actions"><button class="primary" :disabled="!available || !!status?.running || starting || active || !configurationValid" @click="configureSimulation">创建自定义隔离实例</button><span>创建后不会自动启动；核对实例名和拓扑后再启动。</span></div>
        </template>
      </section>

      <section v-if="connected && !status?.enabled" class="configuration-guide">
        <h3>首次配置入口</h3><p>请先准备独立仿真实例，并为当前 Web 入口配置仿真控制器、Python 路径和实例目录。启用后即可在本页启动、运行场景和导出报告。</p>
        <p>实例使用独立数据库、通信端口与虚拟设备。不要手工启动同一实例的第二套控制台。</p>
      </section>

      <div class="environment-tools">
        <section class="simulation-panel protocol-designer" aria-labelledby="simulation-protocol">
          <header><div><p class="section-kicker">协议模拟</p><h3 id="simulation-protocol">通用协议虚拟服务器</h3></div><span :class="['state-pill', protocol?.running ? 'pass' : 'unknown']">{{ protocol?.running ? '监听中' : '未运行' }}</span></header>
          <p>用声明式 JSON 模拟 TCP、UDP 或 HTTP 设备。规则只做匹配、状态和模板响应；可注入延迟、丢响应和断连，不执行任意程序代码。</p>
          <div class="protocol-grid">
            <div>
              <label class="file-picker">载入协议 JSON<input type="file" accept=".json,application/json" @change="loadProtocolFile" /></label>
              <textarea v-model="protocolText" rows="13" spellcheck="false" aria-label="协议模拟JSON"></textarea>
              <div class="toolbar"><button class="primary" :disabled="!available || protocol?.running" @click="startProtocol">启动协议服务器</button><button :disabled="!available || !protocol?.running" @click="action('protocol-stop')">停止协议服务器</button></div>
            </div>
            <aside>
              <dl><dt>端点</dt><dd>{{ protocolAddress() }}</dd><dt>协议 / 分帧</dt><dd>{{ protocol?.transport || '—' }} / {{ protocol?.framing || '—' }}</dd><dt>规则数</dt><dd>{{ protocol?.rules ?? 0 }}</dd><dt>请求数</dt><dd>{{ protocol?.requests ?? 0 }}</dd></dl>
              <p v-if="protocolFile">脚本文件：{{ protocolFile }}</p>
              <p v-if="protocol?.error" class="inline-warning">{{ protocol.error }}</p>
              <details v-if="protocol?.lastEvent"><summary>最近匹配事件</summary><pre>{{ JSON.stringify(protocol.lastEvent, null, 2) }}</pre></details>
              <small>该服务器用于设备协议脚本测试，不冒充当前正式 AGV 连接器。</small>
            </aside>
          </div>
        </section>

        <section class="simulation-panel equipment-designer" aria-labelledby="simulation-equipment-model">
          <header><div><p class="section-kicker">扩展设备</p><h3 id="simulation-equipment-model">电梯、门与信号发生器</h3></div><span :class="['state-pill', equipment?.running ? 'pass' : 'unknown']">{{ equipment?.running ? '模型运行中' : '未运行' }}</span></header>
          <p>用于独立演练楼层运输、开关门和外部信号时间轴。该模型当前不进入正式派车契约或安全联锁。</p>
          <div class="equipment-grid">
            <div>
              <div class="toolbar"><label>楼层运行 / 秒<input v-model.number="equipmentTravelSeconds" type="number" min="0.1" max="300" step="0.1" /></label><label>开关门 / 秒<input v-model.number="equipmentDoorSeconds" type="number" min="0.1" max="60" step="0.1" /></label><button class="primary" :disabled="!available || equipment?.running" @click="startEquipment">启动设备模型</button><button :disabled="!available || !equipment?.running" @click="action('equipment-stop')">停止设备模型</button></div>
              <dl class="equipment-facts"><dt>当前 / 目标楼层</dt><dd>{{ equipment?.currentFloor || '—' }} → {{ equipment?.targetFloor || '—' }}</dd><dt>运行状态</dt><dd>{{ equipment?.fault ? '故障' : equipment?.moving ? `${equipmentDirectionLabel(equipment?.direction)} · ${Math.round((equipment?.progress ?? 0) * 100)}%` : equipmentDirectionLabel(equipment?.direction) }}</dd><dt>门状态</dt><dd>{{ equipmentDirectionLabel(equipment?.door) }}</dd></dl>
              <div class="toolbar"><button :disabled="!available || !equipment?.running || equipment?.fault || equipment?.moving || equipment?.door !== 'Closed'" @click="equipmentControl({ elevator: { targetFloor: '2' } })">前往2层</button><button :disabled="!available || !equipment?.running || equipment?.fault || equipment?.moving || equipment?.door !== 'Closed'" @click="equipmentControl({ elevator: { targetFloor: '1' } })">前往1层</button><button :disabled="!available || !equipment?.running || equipment?.moving" @click="equipmentControl({ elevator: { doorCommand: 'open' } })">开门</button><button :disabled="!available || !equipment?.running || equipment?.moving" @click="equipmentControl({ elevator: { doorCommand: 'close' } })">关门</button><button :disabled="!available || !equipment?.running" @click="equipmentControl({ elevator: { fault: true } })">注入故障</button><button :disabled="!available || !equipment?.running" @click="equipmentControl({ elevator: { reset: true } })">复位设备</button></div>
            </div>
            <div>
              <label>立即设置信号<textarea v-model="signalSetText" rows="3" spellcheck="false"></textarea></label>
              <label>信号时间轴 JSON<textarea v-model="signalScheduleText" rows="4" spellcheck="false"></textarea></label>
              <div class="toolbar"><button :disabled="!available || !equipment?.running" @click="setEquipmentSignals">应用信号及时间轴</button><button :disabled="!available || !equipment?.running" @click="equipmentControl({ signals: { clearSchedule: true } })">清除时间轴</button></div>
              <pre>{{ JSON.stringify({ signals: equipment?.signals || {}, scheduled: equipment?.schedules?.length || 0, recent: equipment?.signalEvents?.slice(-5) || [] }, null, 2) }}</pre>
            </div>
          </div>
        </section>
      </div>
    </section>

    <section v-if="activeStage === 'scenario'" class="stage-surface scenario-stage">
      <div class="scenario-layout">
        <section class="simulation-panel scenario-panel" aria-labelledby="simulation-scenario">
          <header><div><p class="section-kicker">场景设置</p><h3 id="simulation-scenario">场景编排</h3></div><span :class="['state-pill', simulationTone(status?.scenario?.status)]">{{ simulationLabel(status?.scenario?.status) }}</span></header>
          <fieldset :disabled="busy || active">
            <label>测试场景<select v-model="run.name" aria-label="测试场景">
              <option value="normal-cycle">完整业务循环</option>
              <option value="simultaneous-calls">多工位同时呼叫</option>
              <option value="dispatch-backlog">正式调度持续排队测试</option>
              <option value="custom-calls">自定义呼叫计划</option>
              <option value="disconnect-reconnect">车辆断连与重连</option>
              <option value="plc-delay">PLC 反馈延迟</option>
              <option value="plc-recovery">PLC卡位与恢复</option>
              <option value="service-restart">后台重启与恢复屏障</option>
              <option value="resume-recovery">恢复后继续原任务</option>
              <option value="vendor-cancel-return">车端返程中途取消</option>
              <option value="response-loss">运输命令回包丢失</option>
              <option value="road-block-recovery">道路临时阻塞后恢复</option>
              <option value="obstacle-auto-detour">前方障碍自动改道</option>
              <option value="obstacle-auto-safety-return">道路中段自动退回安全站</option>
              <option value="obstacle-timeout-pickup">取料途中受阻并超时</option>
              <option value="obstacle-timeout-fullbox">满箱运输受阻并超时</option>
              <option value="obstacle-timeout-return">空箱返程受阻并超时</option>
              <option value="obstacle-timeout-midroad">道路中段受阻并超时</option>
              <option value="obstacle-timeout-safe-site">安全点转移后继续</option>
              <option value="obstacle-timeout-manual">无法恢复转人工处置</option>
            </select></label>
            <div class="form-row"><label>循环轮数<input v-model.number="run.rounds" type="number" min="1" max="100" step="1" /></label><label>轮间隔 / 秒<input v-model.number="run.intervalSeconds" type="number" min="0" max="60" /></label></div>
            <label>单轮超时 / 秒<input v-model.number="run.timeoutSeconds" type="number" min="30" max="86400" /></label>
            <div v-if="['custom-calls', 'dispatch-backlog'].includes(run.name)" class="call-plan">
              <div class="form-row"><label>呼叫总数<input v-model.number="callCount" type="number" min="1" max="500" step="1" /></label><label>批次间隔 / 秒<input v-model.number="callInterval" type="number" min="0" max="60" step="1" /></label></div>
              <div class="form-row"><label>每批同时呼叫<select v-model.number="callBatchSize"><option v-for="count in configuredStationIds.length" :key="count" :value="count">{{ count }}个工位</option></select></label><label v-if="run.name === 'custom-calls'">指定车辆<select v-model="callVehicleId"><option value="">场景选择可达车辆</option><option v-for="id in vehicleIds" :key="id" :value="id">{{ id }}</option></select></label><p v-else class="inline-warning">由正式后台自主派车；呼叫计划中不指定车辆。</p></div>
              <label>工位轮转顺序<input v-model.trim="callPattern" placeholder="CNC-001,CNC-002" /></label>
              <small>{{ run.name === 'dispatch-backlog' ? '按时间持续追加，不等上一批完成；同一工位仍有原任务时，后续呼叫保留在计划队列。验证忙车排除建议1车2工位或2车3工位，第3工位须明确使用隔离合成点位。只有真正观察到全部车辆忙碌期间仍有正式待派任务，才证明覆盖了积压；2车2工位可能无法形成积压。工位优先级在新建环境前配置，并写入报告。' : '同一批次工位会在同一设备扫描中置位；指定车辆不可达时停止并报告。此场景按批次完成后继续。' }}</small>
            </div>
            <div v-if="run.name === 'plc-recovery'" class="form-row"><label>卡位信号<select v-model="plcFault"><option value="ready">倒料机就绪许可</option><option value="done">倒料完成，允许离开</option></select></label><label>卡位保持 / 秒<input v-model.number="plcFaultHold" type="number" min="1" max="120" /></label></div>
            <label v-if="run.name === 'road-block-recovery' || run.name === 'obstacle-auto-detour' || obstacleTimeoutScenarios.has(run.name)">{{ obstacleTimeoutScenarios.has(run.name) ? '障碍保持时间' : (run.name === 'road-block-recovery' ? '道路阻塞保持' : '车端障碍保持') }} / 秒<input v-model.number="roadBlockHold" type="number" min="1" max="120" /></label>
            <label v-if="run.name === 'service-restart'">重启注入阶段<select v-model="restartStage"><option value="pickup">原取料运输执行中（可验证到站补记后续接）</option><option value="delivery">满箱运输执行中（保留更复杂恢复现场）</option></select></label>
          </fieldset>
          <div class="toolbar"><button class="primary" :disabled="!canControl || active || !runValid || (['custom-calls', 'dispatch-backlog'].includes(run.name) && !callPlanValid)" @click="startScenario"><IconPlayerPlay :size="16" />运行场景</button><button :disabled="!available || !active" @click="action('abort')">中止后续场景</button></div>
          <p v-if="run.name === 'service-restart'" class="inline-warning">该场景结束时保留原任务和恢复屏障，不表示完整业务循环已完成。</p>
          <p v-if="run.name === 'vendor-cancel-return'" class="inline-warning">通过表示“预期取消已被正确识别并保留现场”，不表示业务完成；运行后会留有原任务路权，需按恢复页核对。</p>
          <p v-if="obstacleTimeoutScenarios.has(run.name) && run.name !== 'obstacle-timeout-safe-site'" class="inline-warning">通过表示“车辆持续受阻并返回状态7后，系统正确冻结或转入人工处置”，不表示业务已完成；运行后应导出证据并建立干净实例再继续。</p>
          <p v-if="run.name === 'obstacle-timeout-safe-site'" class="inline-warning">安全点移动由仿真台模拟现场受控转移；通过条件是后台重新核验安全位置、载荷、PLC和原路线后，由原车续接且最终完成原业务任务。</p>
          <p v-if="run.name === 'resume-recovery'" class="inline-warning">只有在恢复页逐项核对并放行后才能运行；它只续接原任务编号。</p>
          <p v-if="!runValid" class="inline-warning">轮数 1–100、间隔 0–60 秒、超时 30–86400 秒。</p>
        </section>

        <aside class="scenario-brief">
          <div><p class="section-kicker">验证目标</p><h3>{{ scenarioNames[run.name] }}</h3><p>{{ currentScenarioDescription }}</p></div>
          <dl><dt>建议起点</dt><dd>{{ status?.running && fresh ? '环境与车辆已准备好，可以直接运行' : '先启动仿真环境，再运行场景' }}</dd><dt>观察重点</dt><dd>车辆是否按顺序取箱、倒料、返空，是否发生抢路、长时间等待或漏做步骤</dd><dt>结束方式</dt><dd>通过、失败、中止和证据不足会分别保留，不会用上一轮结果代替本轮</dd></dl>
          <div class="scenario-progress"><div><b>{{ status?.scenario?.completedRounds ?? 0 }} / {{ status?.scenario?.totalRounds ?? run.rounds }} 轮</b><span>{{ Math.round(progress) }}%</span></div><progress :value="progress" max="100" aria-label="场景完成进度"/><p>{{ industrialText(status?.scenario?.message || '运行后显示当前阶段、等待原因和设备实际反馈。') }}</p></div>
          <button @click="openStage('faults')">运行中需要注入故障</button>
        </aside>
      </div>
    </section>

    <section v-if="activeStage === 'faults'" class="stage-surface fault-stage">
      <div class="fault-intro"><div><p class="section-kicker">故障控制</p><h3>先选择目标，再注入单点故障</h3></div><p>每次控制都会校验反馈编号。画面提交成功不等于故障已生效，须查看设备实际反馈和场景事件。</p></div>
      <div class="simulation-grid fault-grid">
        <section class="simulation-panel vehicle-panel" aria-labelledby="simulation-vehicle">
          <header><div><p class="section-kicker">先选车</p><h3 id="simulation-vehicle"><IconRobot :size="18" />车辆现在怎么样</h3></div><span v-if="!fresh" class="inline-warning">反馈无效，控制已禁用</span></header>
          <label class="vehicle-select">选择车辆<select v-model="selectedVehicle" aria-label="选择虚拟车辆"><option v-if="!vehicleIds.length" value="">尚无车辆</option><option v-for="id in vehicleIds" :key="id" :value="id">{{ id }}</option></select></label>
          <dl class="vehicle-facts">
            <dt>现在</dt><dd>{{ vehicle ? vehicleVisualLabel(vehicle) : (!fresh ? '暂时没有新反馈' : '尚未选择车辆') }}</dd>
            <dt>位置</dt><dd>{{ humanLocation(vehicle?.station) }}</dd>
            <dt>载荷</dt><dd>{{ !fresh ? '待核对' : vehicle?.loaded ? '车上有箱' : '空载' }}</dd>
            <dt>自动处置</dt><dd>{{ vehicle?.recovery ? simulationObstacleRecoveryLabel(vehicle.recovery.state) : '未进入障碍恢复流程' }}</dd>
            <dt v-if="vehicle?.recovery?.safetyPointTarget">退站目标</dt>
            <dd v-if="vehicle?.recovery?.safetyPointTarget">{{ humanLocation(vehicle.recovery.safetyPointTarget) }} · 低速倒走，到站后再续接业务</dd>
            <dt>下一步</dt><dd>{{ industrialText(vehicle?.recovery?.nextAction || vehicle?.waitReason || status?.scenario?.message || '目前没有等待原因') }}</dd>
          </dl>
          <fieldset :disabled="!canControl || !selectedVehicle"><div class="toolbar"><button @click="carControl({ paused: true })">暂停运动</button><button @click="carControl({ paused: false })">恢复运动</button><button @click="carControl({ obstacle: true })">注入前方障碍</button><button @click="carControl({ obstacle: false })">清除前方障碍</button><button @click="carControl({ offline: true })">断开通信</button><button @click="carControl({ offline: false })">恢复通信</button><button @click="carControl({ charging: true })">开始充电</button><button @click="carControl({ charging: false })">停止充电</button><button @click="carControl({ dropNextResponse: true })">丢弃下一响应</button><button @click="carControl({ cancelFinalReturnSegments: 2 })">模拟返程末两段取消</button></div><div class="speed-control"><label>运动倍率<input v-model.number="speedScale" type="number" min="0.1" max="10" step="0.05" /><small>1 为方案配置速度；0.75 且道路限速 1 m/s 时为 0.75 m/s。</small></label><button :disabled="!Number.isFinite(speedScale) || speedScale < 0.1 || speedScale > 10" @click="carControl({ speedScale })">应用倍率</button></div></fieldset>
          <details v-if="vehicle" class="technical-details"><summary>更多车辆数据</summary><dl class="vehicle-facts"><dt>电量</dt><dd>{{ vehicle.batteryPercent !== undefined ? `${vehicle.batteryPercent.toFixed(1)}%` : '未知' }}{{ vehicle.charging ? ' · 充电中' : '' }}</dd><dt>电压</dt><dd>{{ vehicle.batteryVoltage !== undefined ? `${vehicle.batteryVoltage.toFixed(2)} V` : '未知' }}</dd><dt>速度</dt><dd>{{ vehicle.speedMps !== undefined ? `${vehicle.speedMps.toFixed(2)} m/s` : '未知' }}</dd><dt>当前运输</dt><dd>{{ currentTask }}</dd></dl><small v-if="vehicle.simulationControl">控制反馈编号：{{ vehicle.simulationControl.sequence || '尚无反馈' }} · 当前运动倍率 {{ vehicle.speedScale ?? '未知' }}<span v-if="vehicle.simulationControl.responseDropPending"> · 等待丢弃下一响应</span></small></details>
          <p v-if="vehicle?.simulationControl?.error" class="inline-warning">{{ vehicle.simulationControl.error }}</p>
          <details class="edge-controls"><summary>道路阻塞 · 已反馈 {{ appliedBlockedEdges.length }} 条</summary><fieldset :disabled="!canControl || !selectedVehicle"><p>选择道路，在所选虚拟车经过时阻止运动，观察调度等待与超时处理。</p><div class="edge-options"><label v-for="edge in blockableEdges" :key="edge.id"><input v-model="blockedEdgeIds" type="checkbox" :value="edge.id" />{{ edge.id }} · {{ edge.from }} → {{ edge.to }}</label><span v-if="!blockableEdges.length">当前地图没有可用道路。</span></div><div class="toolbar"><button :disabled="!blockableEdges.length" @click="carControl({ blockedEdges: blockedEdgeIds })">应用道路阻塞</button><button @click="blockedEdgeIds = []; carControl({ blockedEdges: [] })">解除所选车全部道路阻塞</button></div></fieldset></details>
          <small>断开通信只改变网络连接；车辆是否继续运动以设备模型反馈为准。</small>
        </section>

        <section class="simulation-panel plc-panel" aria-labelledby="simulation-plc">
          <header><div><p class="section-kicker">倒料设备反应</p><h3 id="simulation-plc">让设备晚一点回应</h3></div><span class="scope-label">模拟设备反应</span></header>
          <fieldset :disabled="!canControl"><div class="form-row"><label>车辆到位后，多久允许进入？ / 秒<input v-model.number="readyDelay" type="number" min="0" max="300" /></label><label>倒料完成后，多久允许离开？ / 秒<input v-model.number="doneDelay" type="number" min="0" max="300" /></label></div><button :disabled="![readyDelay, doneDelay].every(value => Number.isFinite(value) && value >= 0 && value <= 300)" @click="plcControl({ readyDelaySeconds: readyDelay, doneDelaySeconds: doneDelay })">让设备按这个时间回应</button>
            <div class="toolbar plc-faults"><button @click="plcControl({ suppressReady: true })">阻止就绪许可</button><button @click="plcControl({ suppressReady: false })">恢复就绪许可</button><button @click="plcControl({ suppressDone: true })">阻止完成许可</button><button @click="plcControl({ suppressDone: false })">恢复完成许可</button><button @click="plcControl({ fault: true })">注入设备故障</button><button @click="plcControl({ fault: false })">撤销设备故障</button></div>
          </fieldset><small>车辆会一直等待，直到设备给出允许进入或允许离开的反馈。</small><details v-if="plcFeedback" class="technical-details"><summary>更多设备数据</summary><small>控制反馈编号：{{ plcFeedback.sequence || '尚无反馈' }} · 就绪许可延迟 {{ plcFeedback.plc?.readyDelaySeconds ?? 0 }} 秒 · 完成许可延迟 {{ plcFeedback.plc?.doneDelaySeconds ?? 5 }} 秒<span v-if="plcFeedback.plc?.suppressReady"> · 就绪许可已阻止</span><span v-if="plcFeedback.plc?.suppressDone"> · 完成许可已阻止</span><span v-if="plcFeedback.plc?.fault"> · 设备故障</span></small></details><p v-if="plcFeedback?.error" class="inline-warning">{{ plcFeedback.error }}</p>
        </section>
      </div>
    </section>

    <section v-if="activeStage === 'manual'" class="stage-surface manual-stage">
      <section class="simulation-panel manual-hero">
        <div><p class="section-kicker">先保安全，再恢复责任</p><h3>现场式人工处置</h3><p>把“哪辆车、哪一步、什么载荷、为什么停、需要谁动作、恢复后看什么”放在一起。系统不会用一个通用按钮掩盖现场条件。</p></div>
        <div class="manual-hero-actions"><button v-if="manualReadOnly" class="primary" @click="clearHistorySelection">返回当前人工处置</button><button v-else @click="openStage('faults')">清除障碍或恢复通信</button><button @click="openStage('results')">{{ manualReadOnly ? '返回历史报告' : '查看本轮报告' }}</button><button v-if="!manualReadOnly" :disabled="!status?.running || busy" @click="stopForCleanRetest">保留现场并准备重测</button></div>
      </section>
      <p v-if="manualReadOnly" class="simulation-panel manual-history-banner"><b>历史报告只读</b>正在查看 {{ scenarioNames[selectedReport?.name ?? ''] || selectedReport?.name || '历史场景' }} · {{ selectedReport?.id }}。这里展示当时保存的故障上下文，不能向已经结束的实例发送清障、通信或恢复命令。</p>
      <section v-if="manualGuidanceRows.length" class="simulation-panel recovery-guidance manual-guidance" aria-labelledby="recovery-guidance-title">
        <header><div><p class="section-kicker">{{ manualReadOnly ? '历史故障上下文' : '待处置任务' }}</p><h3 id="recovery-guidance-title">卡在哪里，怎样恢复</h3></div><span :class="['state-pill', manualReadOnly ? 'unknown' : fresh ? 'waiting' : 'unknown']">{{ manualReadOnly ? '历史只读' : fresh ? `${manualGuidanceRows.length} 个任务待核对` : '证据过期，先刷新' }}</span></header>
        <article v-for="(guide, index) in manualGuidanceRows" :key="guide.taskId + index" :class="{selected: manualGuide?.taskId === guide.taskId}">
          <div class="manual-guide-title"><b>{{ guide.vehicleId || '尚未派车' }} · {{ guide.stationId || '工位待核对' }}</b><code>{{ guide.taskId }}</code><span class="state-pill waiting">需要处置</span></div>
          <dl><dt>车辆与载荷</dt><dd>{{ manualVehicleSummary(guide) }}</dd><dt>当前步骤</dt><dd>{{ guide.stepNumber ? `第${guide.stepNumber}步 · ` : '' }}{{ industrialText(guide.stepName || '等待派车') }}</dd><dt>为什么停</dt><dd>{{ industrialText(guide.blockedBy || '尚无明确阻塞证据') }}</dd><dt>现在要做什么</dt><dd>{{ industrialText(guide.restoreAction || '先核对当前反馈，勿跳过原任务') }}</dd><dt>恢复后确认</dt><dd>{{ industrialText(guide.expectedFeedback || '同一原任务继续推进且占用正常释放') }}</dd></dl>
          <div class="toolbar"><button class="primary" @click="selectManualGuide(guide)">{{ manualReadOnly ? '展开历史处置说明' : '核对这个任务' }}</button><button v-if="!manualReadOnly" @click="selectedVehicle = guide.vehicleId || selectedVehicle; openStage('faults')">去清除现场条件</button><button @click="openStage('results')">查看报告证据</button></div>
        </article>
      </section>
      <p v-else class="simulation-panel report-empty">当前没有待人工处置的任务。完整业务任务会正常结束；受阻超时场景则应在这里出现原任务、载荷责任和恢复建议。</p>

      <section v-if="manualGuide" class="simulation-panel manual-decision" aria-labelledby="manual-recovery-decision">
        <header><div><p class="section-kicker">{{ manualGuide.historical ? '历史处置对象' : '当前处置对象' }}</p><h3 id="manual-recovery-decision">{{ manualGuide.vehicleId || '车辆待核对' }} · {{ manualGuide.stationId || '工位待核对' }}</h3></div><span :class="['state-pill', manualGuide.historical ? 'unknown' : manualRecoveryReady ? 'pass' : 'waiting']">{{ manualGuide.historical ? '历史只读' : manualRecoveryLabel(manualRecovery?.outcome) }}</span></header>
        <p class="manual-next-step"><b>下一步</b>{{ manualNextStep() }}</p>
        <div class="manual-facts">
          <article><small>车辆与位置</small><b>{{ manualVehicleSummary(manualGuide) }}</b></article>
          <article><small>原运输任务</small><b>{{ manualRecovery?.originalVendorTaskId || manualGuide.taskId }}</b></article>
          <article><small>{{ manualGuide.historical ? '历史位置' : '原目的站' }}</small><b>{{ manualGuide.historical ? (manualGuide.site || '未保存') : (manualRecovery?.destinationSite || '待恢复候选读取') }}</b></article>
          <article><small>{{ manualGuide.historical ? '判定依据' : '候选路线' }}</small><b>{{ manualGuide.historical ? (manualGuide.expectedFeedback || '按历史证据核对') : (manualRecovery?.candidatePath?.length ? manualRecovery.candidatePath.join(' → ') : '尚无可执行路线') }}</b></article>
        </div>
        <p v-if="manualRecovery?.message" class="manual-recovery-message">{{ industrialText(manualRecovery.message) }}</p>
        <p v-if="manualRecoveryError" class="inline-warning">{{ industrialText(manualRecoveryError) }}</p>
        <div class="toolbar"><button v-if="!manualGuide.historical" :disabled="manualRecoveryReading || !fresh" @click="selectManualGuide(manualGuide)">{{ manualRecoveryReading ? '读取中…' : '刷新恢复候选' }}</button><button v-if="!manualGuide.historical" @click="openManualGuideFaults">打开故障控制</button><button v-if="manualGuide.historical" @click="clearHistorySelection">返回当前人工处置</button></div>
        <fieldset v-if="manualRecoveryReady" :disabled="busy || !fresh">
          <legend>受控续接核对</legend>
          <label>处置依据<textarea v-model.trim="manualRecoveryReason" rows="2" minlength="10"></textarea></label>
          <div class="recovery-confirmations"><label><input v-model="manualVehicleConfirmed" type="checkbox" />车辆已停稳且在可信站点</label><label><input v-model="manualLoadConfirmed" type="checkbox" />载荷、箱体和原任务归属一致</label><label><input v-model="manualPlcConfirmed" type="checkbox" />PLC周期和路权条件一致</label></div>
          <button class="primary" :disabled="!manualRecoveryCanExecute" @click="executeManualRecovery">由原车受控续接原任务</button>
        </fieldset>
        <p v-else-if="manualGuide.historical" class="inline-warning">历史报告不能执行恢复。要重新验证这一故障，请停止当前实例、按方案新建后重新运行相同场景。</p>
        <p v-else class="inline-warning">当前没有可发送的替代路线。先按“下一步”完成现场条件，再刷新候选；系统不会从道路中间直接冒充站点发车。</p>
      </section>

      <section v-if="recovery" class="simulation-panel recovery-panel" aria-labelledby="simulation-recovery">
        <header><div><p class="section-kicker">现场恢复</p><h3 id="simulation-recovery">现场式恢复核对</h3></div><span :class="['state-pill', recovery.released ? 'pass' : 'waiting']">{{ recovery.released ? '已放行' : '恢复屏障保持' }}</span></header>
        <p>恢复操作直接走私有 Service 的恢复接口。每次决策必须绑定当前会话、条目修订号和显式人工确认；未全部核对时不能放行，放行后也只能续接原任务编号。</p>
        <p v-if="recovery.error" class="inline-warning">{{ recovery.error }}</p>
        <div class="recovery-items">
          <article v-for="item in recovery.items" :key="item.ticketId">
            <div class="recovery-title"><b>{{ item.taskId === '__SITE__' ? '全场核对' : item.taskId }}</b><span :class="['state-pill', item.verified ? 'pass' : 'waiting']">{{ item.verified ? '已核对' : '待核对' }}</span></div>
            <p class="recovery-step">{{ item.stepText }} · {{ actionStateLabel(item.state) }}</p>
            <small class="recovery-reason">{{ industrialText(item.reason) }}</small>
            <p class="recovery-business-summary">{{ recoveryBusinessSummary(item) }}</p>
            <details v-if="item.vehicleSummary || item.plcSummary || item.loadSummary" class="technical-details"><summary>查看原始核对明细</summary><p v-if="item.vehicleSummary">{{ item.vehicleSummary }}</p><p v-if="item.plcSummary">{{ item.plcSummary }}</p><p v-if="item.loadSummary">{{ item.loadSummary }}</p></details>
            <button v-if="!recovery.released && !item.verified" :disabled="busy || !fresh || !vehicleConfirmed || !loadConfirmed || !plcConfirmed || recoveryMaterial.trim().length < 10 || recoveryReason.trim().length < 10" @click="submitRecovery(item)">提交此项恢复决策</button>
          </article>
        </div>
        <fieldset :disabled="recovery.released || busy">
          <div class="form-row"><label>恢复决策<select v-model.number="recoveryDecision"><option :value="10">继续原任务原节点</option><option :value="20">核对后补记物理完成</option><option :value="30">核对后关闭取消</option></select></label><label>放行原因<input v-model.trim="recoveryReason" minlength="10" /></label></div>
          <label>物料 / 任务归属核对引用<textarea v-model.trim="recoveryMaterial" rows="2" minlength="10"></textarea></label>
          <div class="recovery-confirmations"><label><input v-model="vehicleConfirmed" type="checkbox" />车辆与位置已核对</label><label><input v-model="loadConfirmed" type="checkbox" />载荷与归属已核对</label><label><input v-model="plcConfirmed" type="checkbox" />PLC周期已核对</label></div>
        </fieldset>
        <div class="toolbar"><button class="primary" :disabled="busy || !fresh || recovery.released || !recovery.items.length || !recovery.items.every(item => item.verified) || recoveryReason.trim().length < 10" @click="releaseRecovery">全部核对后放行恢复</button><small>放行后可运行“恢复后继续原任务”场景，核对九步、运输命令和资源最终闭合。</small></div>
      </section>
    </section>

    <section v-if="activeStage === 'results'" class="stage-surface results-stage">
      <section class="simulation-panel report-panel" aria-labelledby="simulation-report">
        <header><div><p class="section-kicker">检查结果</p><h3 id="simulation-report">自动检查与报告</h3></div><div class="toolbar"><span :class="['state-pill', simulationTone(report?.status)]">{{ simulationLabel(report?.status) }}</span><button :disabled="!report || busy" @click="downloadReport"><IconDownload :size="16" />导出报告 JSON</button><button :disabled="!report?.id || busy" @click="downloadEvidenceArchive"><IconDownload :size="16" />导出完整证据包</button></div></header>
        <div v-if="report" class="report-identity"><span v-if="historicalReport" class="state-pill waiting">上一场景报告 · 与当前场景不同</span><b>{{ scenarioNames[report.name ?? ''] ?? report.name ?? '未提供场景名称' }}</b><span>报告编号：{{ report.id || '未提供' }}</span><span>完成时间：{{ stamp(report.finishedAt ?? report.generatedAt) }}</span></div>
        <p v-if="historicalReport && active" class="inline-warning">当前场景仍在运行，以下结果属于上一场景；本次结果将在场景结束后生成。</p>
        <p v-if="report?.recoveryHold" class="inline-warning">恢复屏障检查已结束，原活动任务保留待核对；该报告不表示完整业务循环已完成。</p>
        <p v-if="!report" class="report-empty">尚无报告。运行场景后显示任务、命令、PLC、交通和残留占用检查；未取得证据的项目保留为未核定。</p>
        <template v-else><p v-if="report.message">{{ industrialText(report.message) }}</p><p v-if="firstFailure" class="first-failure"><b>第一失败原因</b>{{ industrialText(firstFailure) }}</p><details v-if="report.versionComparison" class="report-version"><summary>本轮绑定的后台版本与配置</summary><p>{{ industrialText(report.versionComparison.message) }}</p><p>现场：{{ report.versionComparison.field?.informationalVersion || report.versionComparison.field?.buildId || '身份未核定' }} · 仿真：{{ report.versionComparison.simulation?.informationalVersion || report.versionComparison.simulation?.buildId || '身份未核定' }}</p><ul><li v-for="(difference, index) in report.versionComparison.differences ?? []" :key="index">{{ difference.label || '待核定项目' }}：现场 {{ industrialText(difference.field ?? '未取得') }}；仿真 {{ industrialText(difference.simulation ?? '未取得') }}</li></ul></details><dl v-if="metrics.length" class="report-metrics"><div v-for="[key, value] in metrics" :key="key"><dt>{{ simulationMetricLabel(key) }}</dt><dd>{{ simulationMetricValue(value) }}</dd></div></dl>
          <div v-if="report.taskSummary?.length" class="report-table task-records">
            <h4>本轮任务清单 · {{ report.taskSummary.length }} 单 <small>点击任一任务展开九步与交接证据</small></h4>
            <table>
              <thead><tr><th>任务编号</th><th>工位</th><th>车辆</th><th>状态</th><th>当前步骤 / 等待原因</th></tr></thead>
              <tbody>
                <template v-for="task in report.taskSummary" :key="task.taskId">
                  <tr class="task-record-row" :class="{ expanded: reportTaskDetailId === task.taskId }" role="button" tabindex="0" @click="toggleReportTask(task.taskId)" @keydown.enter="toggleReportTask(task.taskId)">
                    <td><code>{{ task.taskId }}</code><small>{{ reportTaskDetailId === task.taskId ? '收起证据' : '展开证据' }}</small></td>
                    <td>{{ task.stationId || '未提供' }}</td>
                    <td>{{ task.vehicleId || '尚未派车' }}</td>
                    <td><span :class="['state-pill', task.completed ? 'pass' : simulationTone(task.currentStepStatus)]">{{ task.completed ? '已完成' : simulationLabel(task.currentStepStatus) }}</span></td>
                    <td><b>{{ task.currentStepNumber ? `第${task.currentStepNumber}步 · ` : '' }}{{ task.currentStepType ? taskStepLabel(task.currentStepType) : industrialText(task.currentStepName || '等待派车') }}</b><small v-if="task.waitingExplanation">{{ industrialText(task.waitingExplanation) }}</small></td>
                  </tr>
                  <tr v-if="reportTaskDetailId === task.taskId" class="task-record-evidence-row">
                    <td colspan="5">
                      <div class="report-task-evidence">
                        <header>
                          <div><b>{{ task.stationId || '工位待核对' }} · {{ task.vehicleId || '尚未派车' }}</b><small>{{ taskStageLabel(reportTaskDetail?.task.stage ?? task.stage) }} · {{ (reportTaskDetail?.steps ?? task.steps ?? []).filter(step => step.status === 'Completed').length }}/{{ (reportTaskDetail?.steps ?? task.steps ?? []).length }} 步完成</small></div>
                          <span :class="['state-pill', taskTone(reportTaskDetail?.task.stage ?? task.stage)]">{{ taskStageLabel(reportTaskDetail?.task.stage ?? task.stage) }}</span>
                        </header>
                        <p v-if="reportTaskDetail?.waitingExplanation || task.waitingExplanation" class="task-waiting-copy">{{ industrialText(reportTaskDetail?.waitingExplanation || task.waitingExplanation || '') }}</p>
                        <ol v-if="reportTaskDetail" class="report-task-nine-steps">
                          <li v-for="step in reportTaskDetail.steps" :key="step.stepId || step.sequence" :class="{ completed: step.status === 'Completed', failed: ['Failed','ManualIntervention'].includes(step.status) }">
                            <span>{{ step.sequence }}</span>
                            <div><b>{{ taskStepLabel(step.stepType) }}</b><small>{{ taskStatusLabel(step.status) }} · {{ step.elapsedText || '未完成' }}</small><p v-if="step.waitingExplanation">{{ industrialText(step.waitingExplanation) }}</p><details v-if="step.observedPhase || step.completionRequirement"><summary>完成条件与证据边界</summary><p>{{ industrialText(step.observedPhase || step.completionRequirement) }}</p><p>{{ industrialText(step.evidenceBoundary || '') }}</p></details></div>
                          </li>
                        </ol>
                        <ol v-else-if="task.steps?.length" class="report-task-nine-steps report-task-nine-steps-summary">
                          <li v-for="step in task.steps" :key="step.number" :class="{ completed: step.status === 'Completed', failed: ['Failed','ManualIntervention'].includes(step.status ?? '') }">
                            <span>{{ step.number }}</span>
                            <div><b>{{ taskStepLabel(step.stepType) }}</b><small>{{ taskStatusLabel(step.status) }}</small><p v-if="step.waitingExplanation">{{ industrialText(step.waitingExplanation) }}</p></div>
                          </li>
                        </ol>
                        <div v-if="reportTaskReading" class="report-task-loading">正在补充实时命令、PLC握手与路权证据…</div>
                        <p v-else-if="reportTaskError" class="inline-warning">报告内九步证据已显示；实时命令、PLC握手和路权明细目前不可读取：{{ industrialText(reportTaskError) }}</p>
                        <div v-if="reportTaskDetail" class="report-task-evidence-grid">
                          <article><h5>车辆与路权</h5><p><b>车辆</b>{{ reportTaskDetail.task.vehicleId || '尚未派车' }}</p><p v-for="resource in reportTaskDetail.resources ?? []" :key="resource.resourceId"><b>{{ routeResourceLabel(resource.resourceId) }}</b>{{ resource.vehicleId }} · {{ resource.heartbeatExpired ? '心跳已过期' : '占用正常' }}</p><p v-if="!reportTaskDetail.resources?.length">当前没有道路或设备占用。</p></article>
                          <article><h5>PLC 与握手</h5><p v-for="signal in reportTaskDetail.plcConditions ?? []" :key="`${signal.stationId}-${signal.variableName}`"><b>{{ plcSignalLabel(signal.variableName) }}</b>{{ plcConditionLabel(signal.currentValue, signal.expectedValue) }}</p><p v-for="handshake in reportTaskDetail.handshakes ?? []" :key="handshake.cycleId"><b>{{ handshakePhaseLabel(handshake.phase) }}</b>{{ industrialText(handshake.reason) }}</p><p v-if="!reportTaskDetail.plcConditions?.length && !reportTaskDetail.handshakes?.length">该任务没有额外 PLC 证据。</p></article>
                          <article><h5>运输命令与出区</h5><p v-for="command in reportTaskDetail.commands ?? []" :key="command.commandId"><b>{{ actionStateLabel(command.status) }}</b>{{ command.route?.vendorSites?.join(' → ') || command.stepId }} · 尝试 {{ command.attemptCount }} 次</p><p v-if="reportTaskDetail.handover"><b>出区核对</b>返程命令{{ reportTaskDetail.handover.returnCommandCompleted ? '已完成' : '未完成' }}；离开工位{{ reportTaskDetail.handover.exitPhysicallyVerified ? '已有物理证据' : '尚无物理证据' }}</p><p v-if="!reportTaskDetail.commands?.length">尚未生成运输命令。</p></article>
                        </div>
                      </div>
                    </td>
                  </tr>
                </template>
              </tbody>
            </table>
          </div>
          <div class="report-table check-records"><table><thead><tr><th>检查项</th><th>结果</th><th>证据 / 说明</th></tr></thead><tbody><tr v-for="(check, index) in report.checks ?? []" :key="index"><td>{{ industrialText(check.name) }}</td><td><span :class="['state-pill', simulationTone(check.status)]">{{ simulationLabel(check.status) }}</span></td><td>{{ industrialText(check.detail || '未提供说明') }}</td></tr></tbody></table></div>
          <div v-if="report.waitingChain?.length" class="waiting-chain"><h4>任务与车辆等待链</h4><ol><li v-for="(wait, index) in report.waitingChain" :key="index"><b>{{ wait.vehicleId || '尚未派车' }} · {{ wait.taskId || '待建单' }}</b><span>{{ wait.stationId }} · {{ wait.site || '位置未反馈' }} · {{ industrialText(wait.stepName || '等待分配') }}</span><p>{{ industrialText(wait.waitReason || wait.blockedBy || '等待原因未反馈') }}</p></li></ol></div>
        </template>
      </section>
      <section class="simulation-panel history-panel" aria-labelledby="simulation-history">
        <header><div><p class="section-kicker">历史证据</p><h3 id="simulation-history">历史对比与问题回放</h3></div><span>只读历史，不操作车辆</span></header>
        <div class="form-row"><label>查看报告<select v-model="selectedHistory" :disabled="historyReading" @change="selectHistory"><option value="">当前实例最近报告</option><option v-for="item in status?.history ?? []" :key="item.id" :value="item.id">{{ stamp(item.finishedAt ?? item.generatedAt) }} · {{ scenarioNames[item.name ?? ''] || '仿真场景' }} · {{ simulationLabel(item.status) }}</option></select></label><label>对比基准<select v-model="comparisonId" @change="compareHistory"><option value="">不对比</option><option v-for="item in status?.history ?? []" :key="item.id" :value="item.id">{{ stamp(item.finishedAt ?? item.generatedAt) }} · {{ scenarioNames[item.name ?? ''] || '仿真场景' }} · {{ simulationLabel(item.status) }}</option></select></label></div>
        <p v-if="!(status?.history?.length)" class="report-empty">还没有历史场景。运行后会保留通过、失败和中止结果。</p>
        <p v-if="comparisonReport" class="inline-warning">数值差异仅作比较；请同时核对当轮地图、后台版本和场景参数，增减值不直接代表变好或变坏。</p>
        <div v-if="metricComparison.length" class="report-table"><table><thead><tr><th>指标</th><th>当前报告</th><th>基准报告</th><th>差值</th></tr></thead><tbody><tr v-for="item in metricComparison" :key="item.key"><td>{{ item.label }}</td><td>{{ simulationMetricValue(item.current) }}</td><td>{{ simulationMetricValue(item.previous) }}</td><td>{{ item.delta > 0 ? '+' : '' }}{{ simulationMetricValue(item.delta) }}</td></tr></tbody></table></div>
        <div class="toolbar"><button :disabled="historyReading || !report?.id" @click="loadReplay">载入本报告时间轴</button><button :disabled="!replay?.timeline.length" @click="replayPlaying = !replayPlaying">{{ replayPlaying ? '暂停回放' : '播放回放' }}</button><button :disabled="historyReading || recordingVideo || !report?.id" @click="exportReplayVideo"><IconDownload :size="16" />{{ recordingVideo ? `正在录制 ${videoProgress}%` : '导出回放视频' }}</button></div>
        <p v-if="replay?.sampled" class="inline-warning">时间轴已按原始时间顺序抽样，保留最后一帧；完整观察记录仍保存在该轮证据文件中。</p>
        <template v-if="replay?.timeline.length"><label class="replay-slider">观察时间 · {{ stamp(replayFrame?.time) }} · {{ replayIndex + 1 }}/{{ replay.timeline.length }}<input v-model.number="replayIndex" aria-label="回放时间点" type="range" min="0" :max="replay.timeline.length - 1" step="1" @input="replayPlaying = false" /></label>
          <svg v-if="replay.map && replayGeometry.nodes.length" class="simulation-map replay-map" viewBox="0 0 800 430" role="img" aria-label="历史任务位置回放"><image v-if="showCadReference && replayCadBackground" class="cad-reference" :href="replayCadBackground.href" :x="replayCadBackground.x" :y="replayCadBackground.y" :width="replayCadBackground.width" :height="replayCadBackground.height" :opacity="replayCadBackground.opacity" preserveAspectRatio="none" /><g v-if="replayMapAreas.length" class="map-zones"><g v-for="area in replayMapAreas" :key="area.id"><polygon :points="area.points" :fill="area.fillColor" :fill-opacity="area.fillOpacity" :stroke="area.strokeColor" :stroke-opacity=".55" /><text :x="area.labelX" :y="area.labelY" :fill="area.strokeColor">{{ area.text }}</text></g></g><g class="roads"><path v-for="edge in replayGeometry.edges" :key="edge.key" :d="edge.path" /></g><g v-for="node in replayGeometry.nodes" :key="node.id" class="map-node" :class="{ dense: denseReplay }"><title>{{ mapNodeLabel(node) }} · {{ node.type || '地图节点' }}</title><circle :cx="node.x" :cy="node.y" :r="denseReplay ? (showDenseNodeLabel(node) ? 3 : 1.4) : 3"/><text v-if="!denseReplay || showDenseNodeLabel(node)" :x="node.x + 7" :y="node.y - 7">{{ mapNodeLabel(node) }}</text></g><g v-for="(car, index) in replayGeometry.cars" :key="car.id" class="map-car" :style="{ '--car-color': ['#41d9bf', '#6fb9ff', '#e8bf64', '#cb9aff'][index % 4] }" :transform="`translate(${car.x} ${car.y})`"><circle r="16"/><text x="0" y="-23" text-anchor="middle">{{ car.id }}</text></g></svg>
          <p v-else class="inline-warning">{{ replay.mapMessage || '该轮没有保存完整地图，只显示任务时间轴，不叠加到当前地图。' }}</p>
          <div class="waiting-chain"><ol><li v-for="car in replayFrame?.vehicles ?? []" :key="car.vehicleId"><b>{{ car.vehicleId }} · {{ car.site || '道路中' }}</b><span>{{ car.taskId || '空闲' }}</span><p>{{ industrialText(car.waitReason || '无阻塞反馈') }}</p></li><li v-for="task in replayFrame?.tasks ?? []" :key="task.taskId"><b>{{ task.taskId }} · {{ task.vehicleId || '等待派车' }}</b><span>{{ task.stationId }} · {{ industrialText(task.step || task.stage || '等待推进') }}</span><p>{{ industrialText(task.waitReason || '无阻塞反馈') }}</p></li></ol></div>
        </template><p v-else-if="replay" class="report-empty">本轮没有可回放的观察采样；不补造位置和任务轨迹。</p>
      </section>
      <details v-if="status?.scenario?.events?.length" class="simulation-panel event-panel"><summary>场景事件 · {{ status.scenario.events.length }} 条</summary><ol><li v-for="(event, index) in [...status.scenario.events].reverse().slice(0, 100)" :key="index"><time>{{ stamp(event.time) }}</time>{{ industrialText(event.message) }}</li></ol></details>
    </section>

    <p class="simulation-boundary">仿真是否就绪，以本页实例、虚拟车辆和设备反馈为准；页面底部全局设备栏对应日常现场后台。本仿真采用统一启停、车辆物理模型、设备时间轴和独立故障注入。合成模型不代表真实固件、机械动作、定位误差与安全联锁已经验收；这些项目仍需现场测试。</p>
      </div>
    </div>
  </main>
</template>

<style scoped>
.map-import { border-radius: 8px; }
.map-import .loaded-config { flex-direction: row; align-items: center; }
.map-import .loaded-config input { width: auto; }
.map-import-summary { display: flex; flex-wrap: wrap; gap: 4px 12px; min-width: 0; overflow-wrap: anywhere; }
.topology-grid > label, .vehicle-config-row > label { min-width: 0; }
.topology-grid > label > small { margin: 0; }
.topology-select { width: 100%; }
.map-node.dense circle{fill:#55779a;stroke-width:1}.map-node.dense text{font-size:10px}.cad-reference{pointer-events:none;filter:grayscale(.35) brightness(1.08)}
.simulation-page{flex:1;min-height:0;overflow:auto;padding:8px 12px 30px;color:#d3e2f3}.simulation-title{display:flex;justify-content:space-between;align-items:center;gap:16px;padding:8px 0 18px}.simulation-title h2{display:flex;align-items:center;gap:9px;font-size:23px}.simulation-title p{margin-top:9px;color:#94abc8;line-height:1.6}.toolbar{display:flex;align-items:center;flex-wrap:wrap;gap:8px}.new-vehicle-count{display:flex;align-items:center;gap:7px;color:#9cb1cb;font-size:11px}.new-vehicle-count input{width:60px}.scope-label{font-size:11px;color:#7ad4e7;border:1px solid #2b6676;background:#102e3e;padding:4px 8px;border-radius:4px;white-space:nowrap}.environment-panel,.simulation-panel,.configuration-guide{border:1px solid #2b4162;border-radius:6px;background:#101b32;padding:18px}.environment-panel{display:flex;align-items:center;flex-wrap:wrap;gap:20px;margin-bottom:14px}.environment-panel>div:first-child{flex:1;min-width:240px}.environment-panel h3{font-size:16px}.environment-panel p{color:#9cb1cb;line-height:1.6;margin-top:7px;overflow-wrap:anywhere}.environment-state{display:flex;flex-direction:column;gap:8px}.environment-state small{color:#89a0be}.simulation-grid{display:grid;grid-template-columns:minmax(0,1.4fr) minmax(320px,1fr);gap:14px}.simulation-panel{min-width:0}.simulation-panel>header{display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:16px}.simulation-panel h3{display:flex;align-items:center;gap:8px;font-size:15px}.simulation-panel small{display:block;color:#91a8c3;line-height:1.65;margin-top:12px}.simulation-map{width:100%;height:340px;background:#0c1529;border:1px solid #223654;border-radius:4px}.roads path{fill:none;stroke:#3b5475;stroke-width:4;stroke-linecap:round}.roads path.blocked{stroke:#ef947b;stroke-dasharray:8 6}.edge-controls{border-top:1px solid #2b4162;padding-top:12px;margin-top:15px}.edge-controls summary{cursor:pointer;color:#a9c7dc}.edge-controls p{line-height:1.6;color:#91a8c3;margin:12px 0}.edge-options{max-height:150px;overflow:auto;margin:12px 0}.edge-options label{display:flex;flex-direction:row;align-items:center;gap:8px;padding:5px}.edge-options input{width:15px;min-height:15px}.map-node circle{fill:#90abc8;stroke:#0c1529;stroke-width:2}.map-node text{font-size:12px;fill:#a5b7cc;paint-order:stroke;stroke:#0c1529;stroke-width:4;stroke-linejoin:round}.map-car{color:var(--car-color);transition:transform .5s linear}.map-car>circle{fill:var(--car-color);fill-opacity:.12;stroke:var(--car-color);stroke-width:1.5}.map-car rect{fill:var(--car-color);stroke:#091525;stroke-width:2}.map-car text{fill:var(--car-color);font-size:13px;font-weight:600;paint-order:stroke;stroke:#0c1529;stroke-width:4;stroke-linejoin:round}.map-car.selected>circle{stroke-width:3}.map-car.stale{opacity:.4}.map-car.offline>circle{stroke-dasharray:4 3}.map-caption{display:flex;justify-content:space-between;gap:12px;color:#86a0bf;padding-top:10px;line-height:1.6}.map-empty{min-height:340px;display:grid;place-content:center;color:#839bbb;background:#0c1529;padding:28px;text-align:center}.simulation-panel label{display:flex;flex-direction:column;gap:7px;color:#a5bad2;font-size:12px}.simulation-panel input,.simulation-panel select{width:100%;min-width:0}.simulation-panel fieldset{border:0;padding:0;margin:0 0 16px;min-width:0}.form-row{display:grid;grid-template-columns:1fr 1fr;gap:12px;margin:14px 0}.scenario-panel fieldset>label{margin-bottom:12px}.simulation-page .primary{color:#a6f0ff;border-color:#289db6;background:#134050}.state-pill{display:inline-flex;padding:4px 8px;border:1px solid #3f526d;border-radius:4px;font-size:11px;white-space:nowrap}.state-pill.pass{color:#67e1bb;background:#112f2e;border-color:#286a59}.state-pill.fail{color:#ffacb6;background:#3c2432;border-color:#865064}.state-pill.waiting{color:#f1d08f;background:#342e21;border-color:#75603a}.state-pill.unknown{color:#a2b1c6;background:#1a273c;border-color:#3c4f6a}.scenario-progress{border-top:1px solid #293c59;margin-top:19px;padding-top:16px}.scenario-progress>div{display:flex;justify-content:space-between;color:#a5d5e4}.scenario-progress progress{width:100%;height:6px;margin:12px 0;accent-color:#3ac5cf}.scenario-progress p{line-height:1.7;color:#b1c5dd;overflow-wrap:anywhere}.vehicle-select{max-width:320px}.vehicle-facts{display:grid;grid-template-columns:105px minmax(0,1fr);gap:9px 12px;line-height:1.5;margin:16px 0 19px}.vehicle-facts dt{color:#8fa6c3}.vehicle-facts dd{margin:0;overflow-wrap:anywhere}.speed-control{display:flex;align-items:flex-end;gap:10px;margin-top:15px}.speed-control label{max-width:130px}.plc-faults{margin-top:18px}.report-panel{margin-top:14px}.report-metrics{display:flex;flex-wrap:wrap;gap:12px 28px;padding-bottom:8px}.report-metrics dt{font-size:11px;color:#8da6c3}.report-metrics dd{margin:5px 0;font-size:19px;color:#b7e7ef;font-variant-numeric:tabular-nums}.report-identity{display:flex;flex-wrap:wrap;align-items:center;gap:10px 18px;padding:0 0 15px;color:#99b1ca;line-height:1.6}.report-identity b{color:#c2dbe8}.report-panel>.inline-warning{line-height:1.7;margin-bottom:12px}.report-table{overflow:auto}.report-panel table{width:100%;border-collapse:collapse;text-align:left;line-height:1.6}.report-panel th{font-weight:400;color:#8fa7c6;background:#12223a}.report-panel td,.report-panel th{padding:12px;border-bottom:1px solid #293b56}.report-panel td:first-child{min-width:150px}.report-panel td:last-child{overflow-wrap:anywhere}.report-empty{color:#91a8c3;line-height:1.8;padding:8px 0}.simulation-error,.simulation-notice{padding:12px 15px;line-height:1.6;margin-bottom:14px;border-radius:4px}.simulation-error{background:#402633;border:1px solid #865269;color:#ffbdc8}.simulation-notice{background:#143037;border:1px solid #2c626a;color:#9edfe0}.inline-warning{color:#eac17d;font-size:11px}.scenario-panel>.inline-warning{margin-top:10px}.configuration-guide{margin-bottom:14px}.configuration-guide h3{margin-bottom:10px}.configuration-guide p{line-height:1.8;color:#a0b8d0}.event-panel{margin-top:14px}.event-panel summary{cursor:pointer;color:#a8cedf}.event-panel ol{list-style:none;padding:0;max-height:250px;overflow:auto}.event-panel li{display:flex;gap:16px;line-height:1.7;border-top:1px solid #263a55;padding:9px 0;overflow-wrap:anywhere}.event-panel time{flex:none;color:#8ca5c1}.simulation-boundary{color:#8098b5;line-height:1.8;margin-top:16px;font-size:11px}@media(max-width:1050px){.simulation-grid{grid-template-columns:1fr}.simulation-title{align-items:flex-start}.map-panel{order:0}}@media(max-width:650px){.simulation-page{padding:5px}.simulation-title{flex-direction:column}.environment-panel{padding:14px}.simulation-panel{padding:14px}.simulation-panel>header{align-items:flex-start;flex-wrap:wrap}.simulation-map{height:280px}.form-row{grid-template-columns:1fr}.map-caption{flex-direction:column}.event-panel li{display:block}.event-panel time{display:block}.vehicle-facts{grid-template-columns:85px minmax(0,1fr)}}@media(prefers-reduced-motion:reduce){.map-car{transition:none}}
.instance-designer{margin-bottom:14px}.designer-intro{line-height:1.75;color:#9db4ce;margin-bottom:14px}.designer-intro code{color:#8ce0e4}.map-import{display:flex;align-items:center;flex-wrap:wrap;gap:12px;padding:12px;background:#0c172b;border:1px solid #263d5b;margin-bottom:14px}.map-import>span{color:#8da7c4;line-height:1.55}.file-picker{max-width:330px}.file-picker input{padding:7px}.plc-table-binding{border:1px solid #294666;background:#0a1628;padding:13px;margin-bottom:14px}.plc-table-summary{display:grid;gap:5px;padding:10px;border:1px solid #23605d;background:#0a2427}.plc-table-summary span{color:#9ed6d0}.plc-table-summary code{display:block;overflow-wrap:anywhere;color:#77aee4;font-size:11px}.topology-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}.vehicle-designer{border-top:1px solid #293e5d;margin-top:16px;padding-top:14px}.designer-subhead{display:flex;justify-content:space-between;gap:12px;align-items:baseline;margin-bottom:10px}.designer-subhead small,.designer-actions span{color:#8da4c0}.vehicle-config-row{display:grid;grid-template-columns:1fr 1.3fr .7fr auto;gap:9px;align-items:end;padding:9px 0;border-top:1px solid #223651}.agentel-config-row button{height:34px}.designer-actions{margin-top:15px}.call-plan{border-top:1px solid #2a405f;padding-top:13px;margin-top:14px}.recovery-panel{margin-top:14px}.recovery-panel>p{line-height:1.75;color:#a1b8d1}.recovery-items{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:10px;margin:14px 0}.recovery-items article{border:1px solid #304965;background:#0d192d;padding:12px}.recovery-title{display:flex;justify-content:space-between;gap:10px;margin-bottom:9px}.recovery-items p,.recovery-items small{display:block;color:#9fb6cf;line-height:1.6;overflow-wrap:anywhere;margin:5px 0}.recovery-items button{margin-top:8px}.recovery-confirmations{display:flex;flex-wrap:wrap;gap:18px;margin-top:12px}.recovery-confirmations label{display:flex;flex-direction:row;align-items:center;gap:8px}.recovery-confirmations input{width:16px;min-height:16px}.simulation-panel textarea{width:100%;min-width:0;resize:vertical}@media(max-width:1050px){.topology-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:650px){.topology-grid,.vehicle-config-row{grid-template-columns:1fr}}
.vehicle-config-row{grid-template-columns:minmax(120px,1fr) minmax(150px,1.3fr) repeat(3,minmax(90px,.65fr)) auto auto}.vehicle-config-row .loaded-config{flex-direction:row;align-items:center;gap:7px}.vehicle-config-row .loaded-config input{width:16px;min-height:16px}.vehicle-config-row button{height:34px}.vehicle-advanced{grid-column:1/-1;border:1px solid #223651;padding:9px}.vehicle-advanced summary{cursor:pointer;color:#8db5ce}.vehicle-advanced .form-row{margin:10px 0 0}
.protocol-designer{margin-bottom:14px}.protocol-designer>p{line-height:1.75;color:#9fb6cf;margin-bottom:12px}.protocol-grid{display:grid;grid-template-columns:minmax(0,1.5fr) minmax(260px,.8fr);gap:14px}.protocol-grid textarea{font-family:Consolas,monospace;font-size:12px;line-height:1.5;margin-top:10px;background:#091426;color:#bfe6ef}.protocol-grid aside{border:1px solid #2b4162;background:#0b172a;padding:14px}.protocol-grid aside dl{display:grid;grid-template-columns:90px minmax(0,1fr);gap:8px;line-height:1.5}.protocol-grid aside dt{color:#829bb8}.protocol-grid aside dd{margin:0;overflow-wrap:anywhere}.protocol-grid pre{max-height:220px;overflow:auto;background:#07101f;padding:10px;color:#a9d7e2;white-space:pre-wrap;overflow-wrap:anywhere}@media(max-width:850px){.protocol-grid{grid-template-columns:1fr}}
.voltage-curve-editor{margin-top:12px;border-top:1px solid #223651;padding-top:10px}.voltage-point{display:grid;grid-template-columns:minmax(110px,1fr) minmax(110px,1fr) auto;gap:9px;align-items:end;margin:8px 0}.voltage-point button{height:34px}.voltage-curve-editor .toolbar{margin-top:9px}.equipment-designer{margin-bottom:14px}.equipment-designer>p{line-height:1.75;color:#9fb6cf;margin-bottom:12px}.equipment-grid{display:grid;grid-template-columns:1fr 1fr;gap:16px}.equipment-grid>div{border:1px solid #2b4162;background:#0b172a;padding:14px;min-width:0}.equipment-grid label{min-width:150px}.equipment-grid textarea{font-family:Consolas,monospace;font-size:12px;line-height:1.5}.equipment-facts{display:grid;grid-template-columns:130px minmax(0,1fr);gap:8px;margin:16px 0;line-height:1.5}.equipment-facts dt{color:#829bb8}.equipment-facts dd{margin:0}.equipment-grid pre{max-height:180px;overflow:auto;background:#07101f;padding:10px;color:#a9d7e2;white-space:pre-wrap}@media(max-width:850px){.equipment-grid{grid-template-columns:1fr}}
.simulation-page{padding:12px 18px 34px;background:linear-gradient(180deg,#08101f 0,#0a1324 210px,#091221 100%)}
.simulation-title{align-items:flex-start;padding:10px 2px 18px}.simulation-title>div:first-child{max-width:780px}.simulation-title h2{margin:2px 0 0;font-size:27px;letter-spacing:.01em}.simulation-title p{max-width:820px;margin-top:7px}.page-kicker,.section-kicker{margin:0;color:#4ad7e6!important;font:600 10px/1.3 Consolas,monospace;letter-spacing:.15em;text-transform:uppercase}.simulation-header-actions{display:flex;align-items:stretch;gap:10px}.mode-card{display:flex;flex-direction:column;min-width:240px;padding:10px 12px;border:1px solid #28506a;border-radius:7px;background:#0c2034}.mode-card small,.mode-card span{color:#88a5c2;font-size:10px}.mode-card b{margin:3px 0;color:#d9f7f7;font-size:13px}.simulation-header-actions>button{align-self:center}
.mission-control{display:grid;grid-template-columns:minmax(260px,1.2fr) minmax(170px,.55fr) minmax(330px,1fr) auto;gap:16px;align-items:stretch;padding:14px 16px;border:1px solid #28506d;border-radius:8px;background:#0d1c31;box-shadow:0 12px 35px #0003}.mission-identity{min-width:0}.mission-label{color:#62d5e3;font-size:10px;letter-spacing:.12em}.mission-identity h3{margin:3px 0 5px;font-size:17px;color:#e0eef8;overflow-wrap:anywhere}.mission-identity p{margin:0;color:#87a2be;font-size:11px;line-height:1.55}.mission-runtime{display:flex;flex-direction:column;justify-content:center;gap:7px}.mission-runtime small{color:#829bb6}.mission-metrics{display:grid;grid-template-columns:repeat(3,minmax(80px,1fr));gap:1px;background:#24415b}.mission-metrics>div{display:flex;flex-direction:column;justify-content:center;gap:2px;padding:8px 11px;background:#0a182b}.mission-metrics small,.mission-metrics span{color:#8099b5;font-size:9px}.mission-metrics b{color:#d6f1f4;font-size:15px;font-variant-numeric:tabular-nums}.mission-actions{display:flex;flex-direction:column;justify-content:center;gap:7px}.mission-actions button{min-width:112px}
.next-step-card{display:grid;grid-template-columns:64px minmax(0,1fr) auto;align-items:center;gap:16px;margin:14px 0;padding:14px 16px;border:1px solid #2b6a75;border-left:4px solid #36cfe0;border-radius:8px;background:linear-gradient(90deg,#102c3b,#0e2032 55%,#101d30)}.next-step-index{display:grid;place-items:center;width:54px;height:36px;border:1px solid #3f7f8d;border-radius:5px;color:#71e1ea;font:700 10px Consolas,monospace;letter-spacing:.12em}.next-step-copy small{color:#68cfdb;font-size:10px}.next-step-copy h3{margin:2px 0 3px;color:#e4f4f8;font-size:16px}.next-step-copy p{margin:0;color:#9fb9cf;font-size:11px;line-height:1.55}
.stage-tabs{display:grid;grid-template-columns:repeat(5,minmax(110px,1fr));gap:8px;margin:14px 0}.stage-tabs button{display:grid;grid-template-columns:34px minmax(0,1fr);grid-template-rows:auto auto;gap:1px 9px;align-items:center;min-height:62px;padding:9px 10px;text-align:left;border:1px solid #263d59;border-radius:6px;background:#0b172a;color:#9bb2ca}.stage-tabs button>span{grid-row:1/3;display:grid;place-items:center;width:32px;height:32px;border-radius:50%;background:#132840;color:#6acbd7;font:600 10px Consolas,monospace}.stage-tabs button strong{color:#c8d9e8;font-size:12px}.stage-tabs button small{font-size:9px;line-height:1.35;color:#768fac}.stage-tabs button:hover{border-color:#39728c}.stage-tabs button.active{border-color:#33bfd1;background:linear-gradient(135deg,#103a4c,#11273d);box-shadow:inset 0 0 0 1px #1c6b7c}.stage-tabs button.active>span{background:#1b6272;color:#d7fbff}
.stage-surface{margin-top:14px}.overview-stage,.environment-stage,.scenario-stage,.fault-stage,.results-stage{padding-bottom:8px}.first-run-guide{border:1px solid #273e5b;border-radius:8px;background:#0b172a;padding:15px 16px;margin-bottom:14px}.first-run-guide>header,.fault-intro{display:flex;justify-content:space-between;align-items:center;gap:18px}.first-run-guide>header h3,.fault-intro h3{margin:2px 0 0;font-size:17px;color:#d7e7f3}.first-run-guide>header>span{color:#7f9ab7;font-size:10px}.guide-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:10px;margin-top:13px}.guide-grid article{display:grid;grid-template-columns:36px minmax(0,1fr);gap:10px;padding:12px;border:1px solid #263d59;background:#0a1426;min-height:148px}.guide-grid article>b{display:grid;place-items:center;width:32px;height:32px;border:1px solid #35526f;border-radius:50%;color:#7290ae;font:600 10px Consolas,monospace}.guide-grid article strong{color:#c9dae8;font-size:12px}.guide-grid article p{margin:5px 0 10px;color:#829cb7;font-size:10px;line-height:1.55}.guide-grid article button{width:100%;margin-top:auto}.guide-grid article.complete{border-color:#285b4f;background:#0b211f}.guide-grid article.complete>b{border-color:#2a7964;color:#70dfba}.guide-grid article.current{border-color:#2e8d9d;box-shadow:inset 3px 0 #38c8d8}
.overview-grid{display:grid;grid-template-columns:minmax(0,1.55fr) minmax(300px,.75fr);gap:14px}.live-operations{display:flex;flex-direction:column}.live-facts{display:grid;grid-template-columns:1fr 1fr;gap:8px;margin:8px 0 14px}.live-facts>div{padding:10px;border:1px solid #263d58;background:#0a1729}.live-facts small{display:block;color:#7d96b2;margin:0 0 4px}.live-facts b{color:#cce7ec;font-size:12px}.scenario-progress.compact{margin-top:auto}.quick-actions{margin-top:14px}.quick-actions button{flex:1;min-width:98px}
.designer-section-title{display:flex;align-items:center;gap:10px;margin:13px 0 10px;padding-top:12px;border-top:1px solid #263d58}.designer-section-title>span{display:grid;place-items:center;width:28px;height:28px;border-radius:50%;background:#15374a;color:#64d7e1;font:600 10px Consolas,monospace}.designer-section-title div{display:flex;flex-direction:column}.designer-section-title b{color:#c7dde9;font-size:12px}.designer-section-title small{color:#7f99b4;font-size:10px}.environment-tools{display:grid;gap:14px}.configuration-guide{margin-top:14px}
.new-instance-control{display:grid;grid-template-columns:minmax(0,1fr) 130px auto;gap:14px;align-items:end;margin-bottom:14px;padding:14px 16px;border:1px solid #2b4964;border-radius:7px;background:#0c192c}.new-instance-control h3{margin:2px 0 4px;font-size:15px;color:#d3e5ef}.new-instance-control p:last-child{margin:0;color:#819bb6;font-size:10px;line-height:1.5}.new-instance-control label{display:flex;flex-direction:column;gap:6px;color:#93a9bf;font-size:10px}.new-instance-control input{width:100%}
.scenario-layout{display:grid;grid-template-columns:minmax(0,1.25fr) minmax(300px,.75fr);gap:14px;align-items:start}.scenario-brief{position:sticky;top:10px;padding:17px;border:1px solid #2d506d;border-radius:7px;background:linear-gradient(160deg,#10243a,#0b172a)}.scenario-brief h3{margin:3px 0 8px;color:#e0edf5;font-size:18px}.scenario-brief>div:first-child>p:last-child{color:#9db5cb;line-height:1.7;font-size:12px}.scenario-brief dl{display:grid;grid-template-columns:82px minmax(0,1fr);gap:9px 12px;margin:16px 0;padding:13px 0;border-top:1px solid #29425e;border-bottom:1px solid #29425e;line-height:1.55}.scenario-brief dt{color:#7d99b7}.scenario-brief dd{margin:0;color:#b3c9dc;font-size:11px}.scenario-brief>button{width:100%;margin-top:8px}
.fault-intro{margin-bottom:13px;padding:14px 16px;border:1px solid #4e3c3a;border-left:3px solid #e18f65;border-radius:6px;background:#201822}.fault-intro>p{max-width:600px;margin:0;color:#c1a9a9;font-size:11px;line-height:1.65}.fault-grid{grid-template-columns:minmax(0,1.15fr) minmax(320px,.85fr)}
.results-stage>.simulation-panel{margin-bottom:14px}.recovery-panel{margin-top:0}
.scheme-panel,.version-panel{margin-bottom:14px}.scheme-panel>p,.version-panel>p{color:#9fb6cf;line-height:1.7}.recent-instances{margin-top:14px;border-top:1px solid #29425e;padding-top:12px}.recent-instances summary,.report-version summary{cursor:pointer;color:#a8cedf}.recent-instances ol,.waiting-chain ol{list-style:none;margin:10px 0;padding:0;display:grid;gap:8px}.recent-instances li,.waiting-chain li{display:flex;flex-direction:column;gap:5px;border:1px solid #2a405c;padding:10px;background:#0b1729;overflow-wrap:anywhere}.recent-instances span,.waiting-chain span,.waiting-chain p{color:#91abc5;font-size:11px;line-height:1.6;margin:0}.version-builds{display:grid;grid-template-columns:1fr 1fr;gap:12px;margin:12px 0}.version-builds>div{border:1px solid #29415e;padding:12px;min-width:0}.version-builds small{margin:0 0 6px}.version-builds b{color:#c9e4ef;overflow-wrap:anywhere}.version-panel table,.history-panel table{width:100%;border-collapse:collapse;text-align:left;line-height:1.65}.version-panel td,.version-panel th,.history-panel td,.history-panel th{padding:10px;border-bottom:1px solid #2a3e59;overflow-wrap:anywhere}.version-panel th,.history-panel th{color:#91abc5;font-weight:400;background:#12223a}.station-designer{grid-column:1/-1;border:1px solid #29415e;padding:12px}.station-config-row{display:grid;grid-template-columns:110px minmax(140px,1fr) minmax(170px,1fr) 90px auto;gap:10px;align-items:end;padding:8px 0 12px;border-bottom:1px solid #263d58;margin-bottom:10px}.station-config-row small{grid-column:1/-1;margin:0}.station-config-row button{height:34px}.recovery-guidance article{border-top:1px solid #2a405c;padding:12px 0}.recovery-guidance article>b{color:#d0e4ef;overflow-wrap:anywhere}.recovery-guidance dl{display:grid;grid-template-columns:100px minmax(0,1fr);gap:9px;margin:12px 0;line-height:1.7}.recovery-guidance dt{color:#7f9cb9}.recovery-guidance dd{margin:0;color:#b7cede;overflow-wrap:anywhere}.first-failure{display:flex;flex-direction:column;gap:6px;padding:12px;border:1px solid #80535a;background:#302131;color:#f0b2b8;line-height:1.65;margin:12px 0}.report-version{margin:12px 0;padding:12px;border:1px solid #29415e;color:#9fb6cf;line-height:1.7}.report-version p,.report-version li{overflow-wrap:anywhere}.history-panel>header>span{font-size:11px;color:#91abc5}.history-panel .toolbar{margin-top:14px}.replay-slider{margin:15px 0;line-height:1.7}.replay-slider input{accent-color:#37c9d8}.replay-map .map-car{transition:none}.waiting-chain{margin-top:16px}.waiting-chain h4{color:#bdd9e8;margin-bottom:10px}@media(max-width:850px){.station-config-row{grid-template-columns:1fr 1fr}.station-config-row small{grid-column:1/-1}}@media(max-width:620px){.version-builds,.station-config-row{grid-template-columns:1fr}.recovery-guidance dl{grid-template-columns:1fr;gap:4px}.recovery-guidance dd{margin-bottom:8px}}
@media(max-width:1180px){.mission-control{grid-template-columns:minmax(240px,1fr) minmax(240px,1fr) auto}.mission-runtime{grid-column:1}.mission-metrics{grid-column:2}.mission-actions{grid-column:3}.guide-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}
@media(max-width:900px){.simulation-header-actions{width:100%;justify-content:space-between}.simulation-title{flex-direction:column}.mission-control{grid-template-columns:1fr 1fr}.mission-actions{grid-column:1/-1;flex-direction:row}.stage-tabs{grid-template-columns:repeat(3,minmax(120px,1fr))}.overview-grid,.scenario-layout,.fault-grid{grid-template-columns:1fr}.scenario-brief{position:static}}
@media(max-width:620px){.simulation-page{padding:6px 7px 20px}.mission-control,.next-step-card,.stage-tabs,.guide-grid,.new-instance-control{grid-template-columns:1fr}.mission-runtime,.mission-metrics,.mission-actions,.next-step-index{grid-column:auto}.mission-actions{flex-direction:column}.next-step-index{width:100%;height:26px}.stage-tabs{max-height:240px;overflow:auto}.stage-tabs button{min-height:54px}.first-run-guide>header,.fault-intro{align-items:flex-start;flex-direction:column}.guide-grid article{min-height:auto}.simulation-header-actions{align-items:stretch;flex-direction:column}.mode-card{min-width:0}}
.simulation-page{max-width:1840px;margin:0 auto;padding-top:4px!important}
.simulation-title{padding:2px 2px 8px!important;align-items:center!important}
.simulation-title .page-kicker,.simulation-title>div:first-child>p:last-child,.simulation-title .mode-card{display:none!important}
.simulation-title h2{margin:0!important;font-size:23px!important}
.simulation-title .simulation-header-actions{gap:6px!important}
.simulation-workspace{display:grid;grid-template-columns:270px minmax(0,1fr);gap:10px;align-items:stretch;min-height:calc(100vh - 140px)}
.simulation-sidebar{display:flex;flex-direction:column;gap:9px;min-height:0;height:calc(100vh - 140px)}
.simulation-workspace-content{min-width:0}
.embedded:has(.simulation-workspace > :only-child) .simulation-workspace{grid-template-columns:minmax(0,1fr);min-height:0}
.embedded .simulation-workspace{min-height:0}
.module-nav{grid-template-columns:repeat(5,minmax(0,1fr))!important;gap:6px!important;margin:0 0 12px!important;padding:6px;border:1px solid #263e59;border-radius:8px;background:#081425}
.simulation-sidebar>.module-nav{grid-template-columns:1fr!important;margin:0!important;padding:7px;flex:none}
.simulation-sidebar>.module-nav button{min-height:48px!important;padding:8px 10px!important}
.module-nav button{display:flex!important;grid-template-columns:none!important;grid-template-rows:none!important;align-items:center!important;justify-content:flex-start!important;min-height:44px!important;padding:8px 12px!important;border-color:transparent!important;background:transparent!important;box-shadow:none!important}
.module-nav button>span{display:grid;place-items:center;width:26px;height:26px;border-radius:5px;background:#10253b;color:#6fcbd7;font:600 9px Consolas,monospace;flex:none}
.module-nav button strong{font-size:12px;color:#9fb6ca;white-space:nowrap}
.module-nav button.active{border-color:#2d7185!important;background:#102b3c!important;box-shadow:inset 0 -2px #3cd1df!important}
.module-nav button.active strong{color:#e4f8fb}
.module-heading{display:grid;grid-template-columns:minmax(360px,1fr) auto auto;align-items:center;gap:20px;margin-bottom:14px;padding:16px 18px;border:1px solid #28506d;border-radius:8px;background:linear-gradient(105deg,#0d2037,#0b172b)}
.module-heading h3{margin:3px 0 4px;font-size:24px;color:#e4f1f7}
.module-heading p:last-child{margin:0;color:#92aac2;font-size:12px}
.module-heading-status{display:flex;flex-direction:column;align-items:flex-start;gap:6px}
.module-heading-status small{color:#7f9ab4;font-size:10px}
.module-heading-actions{display:flex;gap:8px}
.stage-surface{margin-top:0!important}
.overview-stage{padding-bottom:0!important}
.overview-hero{display:grid;grid-template-columns:1fr;gap:7px;padding:8px;border:1px solid #2b5a72;border-left:3px solid #36cfe0;border-radius:6px;background:#0b1d30}
.overview-status-title{display:flex;align-items:center;gap:9px;min-width:0}
.overview-status-title b{font-size:16px;color:#e8f6f8;white-space:nowrap}
.overview-hero-copy>p:last-child{margin:4px 0 0;color:#8faebf;font-size:9px;line-height:1.35;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden}
.overview-kpis{display:grid;grid-template-columns:repeat(3,minmax(120px,1fr));border:1px solid #2b475f;background:#081522}
.overview-kpis>div{display:flex;flex-direction:column;gap:2px;padding:11px 13px;border-right:1px solid #2b475f}
.overview-kpis>div:last-child{border-right:0}
.overview-kpis small,.overview-kpis span{color:#7f9bb4;font-size:9px}
.overview-kpis b{color:#dff2f5;font-size:15px}
.overview-next-action{width:100%;min-width:0;min-height:30px;padding:4px 8px}
.instance-details{display:none}
.instance-details summary{cursor:pointer;width:max-content}
.instance-details p{margin:7px 0;overflow-wrap:anywhere}
.business-overview-grid{display:block!important;position:relative;min-height:clamp(640px,calc(100vh - 175px),980px)}
.business-overview-grid .map-panel{width:100%;min-height:inherit}
.business-overview-grid .simulation-map{height:clamp(600px,calc(100vh - 205px),940px)}
.business-overview-grid .roads path{stroke:#477f96;stroke-width:3.2}
.business-overview-grid .roads path.blocked{stroke:#ff9b7d;stroke-width:5}
.business-overview-grid .map-node.dense text{font-size:9px;font-weight:600}
.business-overview-grid .map-node.dense circle{stroke-width:1.2}
.simulation-map.vehicle-focus .roads path{stroke-width:4.2}
.map-header-actions{gap:6px!important}
.map-view-switch{display:inline-flex;border:1px solid #2b4b66;border-radius:4px;overflow:hidden}
.map-view-switch button{border:0;border-radius:0;min-height:26px;padding:4px 9px;font-size:10px;background:#0a1729}
.map-view-switch button+button{border-left:1px solid #2b4b66}
.map-view-switch button.active{background:#18536a;color:#e7fbff}
.business-story{display:flex;flex-direction:column;padding:9px!important;background:#091626!important;border-color:#31536f!important;overflow:hidden}
.sidebar-business-story{position:static!important;width:auto!important;max-height:none!important;flex:1;min-height:0}
.map-panel{position:relative}
.business-story>header{margin-bottom:7px!important}
.business-story>header h3{font-size:14px}
.story-lead{padding:12px 13px;margin-bottom:10px;border:1px solid #2b4c62;background:#0a1a2b}
.story-lead small{display:block;color:#57cfda;font-size:10px;margin-bottom:5px}
.story-lead p{margin:0;color:#bed6e2;font-size:12px;line-height:1.7}
.vehicle-story-list{display:grid;gap:5px;max-height:none;min-height:0;flex:1;overflow:auto;padding-right:2px}
.vehicle-story-list article{padding:7px 8px;border:1px solid #293f59;border-left:3px solid #7e91a8;background:#0a1728}
.vehicle-story-list article.running{border-left-color:#41d9bf}.vehicle-story-list article.waiting{border-left-color:#e8bf64}.vehicle-story-list article.idle{border-left-color:#6fb9ff}.vehicle-story-list article.blocked{border-left-color:#ee9c63}.vehicle-story-list article.fault{border-left-color:#ef7692}.vehicle-story-list article.offline{border-left-color:#65748a}
.vehicle-story-list article>div{display:flex;justify-content:space-between;gap:10px}
.vehicle-story-list article b{color:#d9edf4;font-size:12px}
.vehicle-story-list article span{color:#6fd7df;font-size:10px}
.vehicle-story-list article small{display:block;color:#7e9ab5;font-size:9px;margin-top:3px}
.vehicle-story-list article p{margin:3px 0 0;color:#a5bdd0;font-size:10px;line-height:1.4;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden}
.business-story .quick-actions{margin-top:auto;padding-top:12px}
.business-story .quick-actions button{min-width:0;padding-left:6px;padding-right:6px;font-size:10px}
.sidebar-simulation-note{margin:8px 0 0;padding-top:7px;border-top:1px solid #29425c;color:#6f8ca6;font-size:9px;line-height:1.4;text-align:center}
.technical-details{margin-top:12px;border-top:1px solid #263b55;padding-top:10px;color:#7894ad}
.technical-details summary{cursor:pointer;color:#8fb9cf;font-size:11px}
.technical-details p{max-height:180px;overflow:auto;white-space:pre-wrap;overflow-wrap:anywhere;font-family:Consolas,"Microsoft YaHei",monospace}
.recovery-step{margin:3px 0!important}
.recovery-reason{display:block;color:#d8b978!important;line-height:1.55;margin:5px 0!important}
.recovery-business-summary{margin:7px 0!important;padding:8px 10px;border-left:2px solid #3fc9d6;background:#0b2331;color:#a9dce4!important;line-height:1.55!important}
.map-panel>header{margin-bottom:8px!important}
.map-layer-toggle{font-size:10px;padding:4px 8px;min-height:26px}
.map-zones polygon{stroke-width:1.2;stroke-dasharray:5 4}
.map-zones text{display:block;font-size:9px;font-weight:600;text-anchor:middle;paint-order:stroke;stroke:#07101f;stroke-width:4;stroke-linejoin:round;pointer-events:none}
.map-zones polygon{opacity:.85}
.map-node circle{fill:#092534!important;stroke:#48d7cf!important;stroke-width:1.5!important}
.map-node.dense circle{fill:#092534!important;stroke:#42bfc8!important}
.vehicle-focus .map-zones text,.vehicle-focus .map-node text{display:none}
.map-run-legend{position:absolute;left:20px;top:56px;z-index:5;display:flex;flex-wrap:wrap;gap:7px 13px;max-width:calc(100% - 40px);padding:6px 9px;border:1px solid #2b4058;border-radius:5px;background:#091526e8;color:#8ca8bd;font-size:9px;pointer-events:none}
.map-run-legend span{display:flex;align-items:center;gap:5px}
.map-legend-dot{width:6px;height:6px;border-radius:50%;background:#73879b}
.map-legend-dot.running{background:#41d9bf}.map-legend-dot.waiting{background:#e8bf64}.map-legend-dot.idle{background:#6fb9ff}.map-legend-dot.blocked{background:#ee9c63}.map-legend-dot.fault{background:#ef7692}.map-legend-dot.offline{background:#65748a}
.map-load-badge rect{fill:#e8bf64;stroke:#fff0b1;stroke-width:.8}.map-load-badge text{fill:#32230a!important;stroke:none!important;font-size:5.5px!important;font-weight:800}.map-load-legend{display:inline-block;width:8px;height:7px;border:1px solid #fff0b1;border-radius:1px;background:#e8bf64}
.simulation-map.vehicle-focus .map-node text{display:none}
.map-car path{fill:#ffffff;fill-opacity:.9}
.map-car text{font-size:10px!important}
@media(max-width:1180px){.module-heading{grid-template-columns:1fr auto}.module-heading-status{grid-column:1}.module-heading-actions{grid-column:2;grid-row:1/3}.overview-hero{grid-template-columns:1fr auto}.overview-next-action{grid-column:2;grid-row:1/3}.business-overview-grid{grid-template-columns:minmax(0,2.4fr) minmax(230px,.75fr)!important}.business-overview-grid .simulation-map{height:min(680px,calc(100vh - 245px))}}
@media(max-width:900px){.simulation-workspace{grid-template-columns:1fr;min-height:0}.simulation-sidebar{height:auto}.simulation-sidebar>.module-nav{grid-template-columns:repeat(2,minmax(0,1fr))!important;margin-bottom:0!important}.sidebar-business-story{display:block}.business-overview-grid{min-height:0}.business-overview-grid .simulation-map{height:520px}.vehicle-story-list{max-height:360px;min-height:0}}
@media(max-width:760px){.module-nav{grid-template-columns:repeat(2,minmax(0,1fr))!important}.module-heading,.overview-hero{grid-template-columns:1fr}.module-heading-actions,.overview-next-action{grid-column:auto;grid-row:auto}.module-heading-actions{flex-wrap:wrap}.overview-kpis{grid-template-columns:1fr}.overview-kpis>div{border-right:0;border-bottom:1px solid #2b475f}.overview-kpis>div:last-child{border-bottom:0}.business-overview-grid .simulation-map{height:360px}}
@media(max-width:650px){.simulation-map{height:310px}}
.tasks-stage{display:grid;gap:10px}
.task-command-panel{padding:14px 16px}
.task-command-panel>header{margin-bottom:12px}
.task-command-panel>header p:last-child{max-width:880px;margin:4px 0 0;color:#8fa9bf;font-size:11px;line-height:1.55}
.task-counts{display:grid;grid-template-columns:repeat(5,minmax(100px,1fr));gap:8px}
.task-counts article{display:flex;align-items:center;justify-content:space-between;gap:10px;padding:9px 11px;border:1px solid #29455f;border-radius:5px;background:#091727}
.task-counts small{margin:0!important;color:#88a1b7!important;font-size:10px}
.task-counts b{color:#cce9ef;font-size:18px;font-variant-numeric:tabular-nums}
.task-counts article.fault{border-color:#754354;background:#231726}.task-counts article.fault b{color:#ff9eb3}
.task-filter-row{display:grid;grid-template-columns:150px minmax(240px,1fr) auto;gap:9px;align-items:end;margin-top:10px}
.task-filter-row label{margin:0}
.task-filter-row input,.task-filter-row select{height:34px}
.task-search input{min-width:0}
.task-workbench{display:grid;grid-template-columns:minmax(340px,.82fr) minmax(0,1.18fr);gap:10px;align-items:stretch}
.task-table-panel,.task-detail-panel{padding:13px!important}
.task-table-panel>header,.task-detail-panel>header{margin-bottom:10px!important}
.task-table-scroll{max-height:340px;overflow:auto;border:1px solid #253c57}
.task-table{width:100%;border-collapse:collapse;text-align:left}
.task-table th{position:sticky;top:0;z-index:1;padding:9px 8px;border-bottom:1px solid #2c4663;background:#10233a;color:#7f9ab4;font-size:10px;font-weight:500;white-space:nowrap}
.task-table td{padding:9px 8px;border-bottom:1px solid #223750;color:#b5cad8;font-size:10px;vertical-align:top;line-height:1.45}
.task-table tbody tr{cursor:pointer;transition:background .15s}.task-table tbody tr:hover{background:#10253a}.task-table tbody tr.selected{background:#113044;box-shadow:inset 3px 0 #37cbd9}.task-table tbody tr.traffic-waiting:not(.selected){background:#211a18;box-shadow:inset 3px 0 #e48a63}.task-waiting-signal{display:inline-block!important;margin:4px 0 0!important;padding:2px 5px;border:1px solid #8b5a43;border-radius:3px;color:#ffc196!important;background:#2b1d18;white-space:nowrap}
.task-table td:first-child{min-width:140px}.task-table td:nth-child(2){white-space:nowrap}.task-table td:nth-child(4){max-width:180px;overflow-wrap:anywhere}
.task-table td b{display:block;color:#dcecf2;font-size:11px}.task-table td small{margin:2px 0 0;color:#748fa9;font-size:9px;overflow-wrap:anywhere}
.task-table .task-error-text{color:#f0a6b0}.task-empty{padding:32px 14px!important;text-align:center;color:#8099b1!important}
.task-detail-panel{display:flex;flex-direction:column;min-width:0}
.task-waiting-copy{margin:0 0 10px;padding:9px 11px;border-left:3px solid #e1b967;background:#211f18;color:#dfc985;line-height:1.6;font-size:11px}
.task-step-list{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:7px;margin:0;padding:0;list-style:none}
.task-step-list>li{min-width:0;border:1px solid var(--ui-line);border-radius:6px;background:var(--ui-surface-raised)}
.task-step-list>li>button{width:100%;min-height:62px;height:auto;display:grid;grid-template-columns:26px minmax(0,1fr);align-items:center;gap:8px;padding:8px;text-align:left;white-space:normal;border:0;border-radius:5px;background:transparent;color:var(--ui-text)}
.task-step-list>li>button[aria-pressed="true"]{outline:2px solid var(--ui-primary);outline-offset:-1px;background:var(--ui-surface-soft)}
.task-step-list .task-step-number{display:grid;place-items:center;width:24px;height:24px;border-radius:50%;background:var(--ui-surface-soft);font-weight:700;color:var(--ui-text-2)}
.task-step-list strong,.task-step-list small{display:block;line-height:1.45}.task-step-list strong{font-size:13px}.task-step-list small{margin-top:3px;color:var(--ui-text-3)}
.task-step-list>li.current{border-color:var(--ui-warning)}.task-step-list>li.completed .task-step-number{background:#d8eee6;color:#17634f}.task-step-list>li.failed{border-color:var(--ui-danger)}
.task-route-panel{scroll-margin-top:16px}.task-route-panel>header{align-items:center;margin-bottom:8px!important}
.task-route-map-wrap{min-width:0}.task-route-map{display:block;width:100%;height:auto;max-height:640px;background:#f8fafc;border:1px solid #dbe3ef;border-radius:8px}.task-route-map text{font-size:12px;fill:#172338;paint-order:stroke;stroke:#fff;stroke-width:2px}
.task-route-controls,.task-route-now{display:flex;align-items:center;flex-wrap:wrap;gap:8px 16px}.task-route-controls{justify-content:space-between;margin-bottom:12px}
.task-route-controls label,.task-route-endpoints{display:flex;flex-direction:row;align-items:center;flex-wrap:wrap;gap:7px;color:var(--ui-text-2);font-size:13px}.task-route-endpoints{margin:0;overflow-wrap:anywhere}.task-route-endpoints i{width:18px;height:3px;border-radius:2px}.task-route-controls select{width:auto;max-width:min(360px,100%)}
.task-route-message,.task-step-observation,.task-route-now{padding:8px 12px;border:1px solid var(--ui-line);border-radius:6px;background:var(--ui-surface-soft);color:var(--ui-text-2);margin:0 0 12px;font-size:13px;line-height:1.5;overflow-wrap:anywhere}
.task-route-now strong{color:var(--ui-warning)}
.task-evidence-details{margin-top:8px;border-top:1px solid var(--ui-line);padding-top:8px;min-width:0}.task-evidence-details summary{cursor:pointer;font-size:13px;color:var(--ui-text-2)}.task-evidence-details p{font-size:12px;color:var(--ui-text-3);overflow-wrap:anywhere}.task-evidence-details .evidence-raw{font-family:Consolas,monospace;line-height:1.65}.task-evidence-details .task-evidence-details{margin-inline-start:16px}
.task-evidence-archive>.task-evidence-details:first-child{margin-top:0;border-top:0;padding-top:0}
.roads path.task-route{fill:none;stroke-linecap:round;stroke-linejoin:round;transition:stroke .2s,stroke-width .2s,opacity .2s}
.roads path.task-route-completed{stroke:#4c938e;stroke-width:4.2;opacity:.72;filter:drop-shadow(0 0 2px #285f5b)}
.roads path.task-route-future{stroke:#54cce3;stroke-width:4.2;stroke-dasharray:9 6;opacity:.78}
.roads path.task-route-current{stroke:#5ff5da;stroke-width:7;opacity:1;filter:drop-shadow(0 0 5px #33cdb6);animation:task-route-flow 1.2s linear infinite}
.roads path.task-route-waiting{stroke:#ff8a6c;stroke-width:7;stroke-dasharray:10 5;opacity:1;filter:drop-shadow(0 0 6px #e85f45);animation:task-route-wait .8s ease-in-out infinite alternate}
.roads path.task-route-return{stroke:#ffc76b;stroke-width:5;stroke-dasharray:11 5;opacity:.9;filter:drop-shadow(0 0 4px #a9762e)}
.roads path.task-route-focus{stroke:#f3ffff;stroke-width:8.5;stroke-dasharray:none;opacity:1;filter:drop-shadow(0 0 7px #68ffe6)}
.roads path.task-route-focus.task-route-return{stroke:#ffd27d;filter:drop-shadow(0 0 7px #d59235)}
.roads path.task-route-dimmed{stroke-width:3.2;opacity:.18;filter:none;stroke-dasharray:none;animation:none}
.map-node.task-node circle{fill:#153c3b!important;stroke:#62c8c0!important;stroke-width:2!important}
.map-node.task-node text{fill:#c9f4ee;font-weight:700;font-size:10px!important;opacity:1!important}
.map-node.task-node-current circle{fill:#187763!important;stroke:#8effe5!important;stroke-width:3!important;filter:drop-shadow(0 0 4px #45dbc1)}
.map-node.task-node-waiting circle{fill:#713a2d!important;stroke:#ff9b79!important;stroke-width:3.2!important;filter:drop-shadow(0 0 5px #ed6d50);animation:task-node-wait .8s ease-in-out infinite alternate}
.map-node.task-node-return circle{fill:#61451f!important;stroke:#ffd27d!important;stroke-width:2.5!important}
.map-node.task-node-focus circle{fill:#f2ffff!important;stroke:#68ffe6!important;stroke-width:3.5!important;filter:drop-shadow(0 0 6px #5ff5da)}
.map-node.task-node-dimmed{opacity:.24}
@keyframes task-route-flow{to{stroke-dashoffset:-24}}@keyframes task-route-wait{from{stroke-opacity:.58}to{stroke-opacity:1}}@keyframes task-node-wait{from{opacity:.68}to{opacity:1}}
.task-timeline{max-height:330px;margin:0;padding:0;overflow-y:auto;overflow-x:hidden;list-style:none}
.task-timeline li{display:grid;grid-template-columns:120px 180px minmax(0,1fr);gap:12px;align-items:center;min-width:0;min-height:40px;padding:7px 4px;border-top:1px solid #20364f;font-size:10px;line-height:1.45}
.task-timeline time,.task-timeline b,.task-timeline-copy{min-width:0}.task-timeline time{color:#648198;font-variant-numeric:tabular-nums;white-space:nowrap}.task-timeline b{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#cae1e9;font-weight:600}.task-timeline-copy{display:grid;grid-template-columns:48px minmax(0,1fr);gap:8px;align-items:center;color:#8fa9bf}.task-timeline-copy em{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#58cbd4;font-style:normal}.task-timeline-copy>span{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.task-records>h4 small{display:inline;margin-left:8px;color:#758ea8;font-size:9px;font-weight:400}
.task-record-row{cursor:pointer}.task-record-row:hover td{background:#10253a}.task-record-row.expanded td{border-bottom-color:#31546f;background:#0d2638}
.task-record-row td:first-child small{display:block;margin-top:3px;color:#54c8d3}
.task-record-evidence-row>td{padding:0!important;background:#071522!important;border-bottom:1px solid #31526d!important}
.report-task-loading{padding:18px;color:#8fa9bf}.report-task-evidence{padding:13px;border-left:3px solid #38cbd7}
.report-task-evidence>header{display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:9px}.report-task-evidence>header div{display:flex;flex-direction:column;gap:2px}.report-task-evidence>header b{color:#d7ebf2}.report-task-evidence>header small{display:block;margin:0;color:#7d99b2}
.report-task-nine-steps{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:7px;margin:9px 0;padding:0;list-style:none}
.report-task-nine-steps>li{display:grid;grid-template-columns:24px minmax(0,1fr);gap:7px;padding:8px;border:1px solid #263f59;background:#091626}
.report-task-nine-steps>li>span{display:grid;place-items:center;width:22px;height:22px;border-radius:50%;background:#193149;color:#78a7b8;font:600 9px Consolas,monospace}
.report-task-nine-steps b{display:block;color:#c9dfe8;font-size:10px}.report-task-nine-steps small{display:block;margin:2px 0 0;color:#809bb3;font-size:9px}.report-task-nine-steps p{margin:4px 0 0;color:#bda66e;font-size:9px;line-height:1.45}.report-task-nine-steps details{margin-top:4px}.report-task-nine-steps summary{font-size:9px}.report-task-nine-steps>li.completed{border-color:#2b6558}.report-task-nine-steps>li.failed{border-color:#824d5d}
.report-task-evidence-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:8px}.report-task-evidence-grid article{padding:9px;border:1px solid #253d58;background:#0a1727}.report-task-evidence-grid h5{margin:0 0 6px;color:#c3dae5;font-size:10px}.report-task-evidence-grid p{display:flex;flex-direction:column;gap:2px;margin:0;padding:5px 0;border-top:1px solid #1c3248;color:#88a3ba;font-size:9px;line-height:1.45}.report-task-evidence-grid p:first-of-type{border-top:0}.report-task-evidence-grid b{color:#c7dce5}
@media(max-width:1180px){.task-counts{grid-template-columns:repeat(3,minmax(100px,1fr))}.task-workbench{grid-template-columns:minmax(310px,.9fr) minmax(0,1.1fr)}.task-step-list{grid-template-columns:repeat(2,minmax(0,1fr))}.task-timeline li{grid-template-columns:108px 150px minmax(0,1fr)}}
@media(max-width:900px){.task-workbench{grid-template-columns:1fr}.task-table-panel,.task-detail-panel{min-height:0}.task-table-scroll{max-height:420px}.task-counts{grid-template-columns:repeat(2,minmax(100px,1fr))}.report-task-evidence-grid{grid-template-columns:1fr}.report-task-nine-steps{grid-template-columns:repeat(2,minmax(0,1fr))}}
@media(max-width:650px){.task-filter-row{grid-template-columns:1fr}.task-counts{grid-template-columns:1fr 1fr}.task-step-list,.report-task-nine-steps{grid-template-columns:1fr}.task-timeline li{grid-template-columns:1fr;gap:3px}.task-timeline b,.task-timeline-copy>span{white-space:normal}.report-task-evidence>header{align-items:flex-start;flex-direction:column}}
.manual-stage{display:grid;gap:14px}
.manual-hero{display:flex;align-items:center;justify-content:space-between;gap:20px;padding:18px 20px;border-left:3px solid #5dd8df;background:linear-gradient(120deg,#102839,#0c1729)}
.manual-hero h3{font-size:21px;margin:3px 0 7px;color:#e2f4f7}.manual-hero p{max-width:820px;line-height:1.75;color:#b2c9d6}.manual-hero-actions{display:flex;gap:8px;flex-wrap:wrap;justify-content:flex-end}
.manual-guidance>article.selected{border-color:#65d9df;background:linear-gradient(110deg,#102a37,#0a1928);box-shadow:inset 3px 0 #65d9df}
.manual-guide-title{display:grid;grid-template-columns:minmax(210px,auto) minmax(0,1fr) auto;align-items:center;gap:12px}.manual-guide-title code{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#729eb0;font-size:10px}
.manual-decision{padding:18px;border-color:#3f6c78}.manual-next-step{display:grid;grid-template-columns:110px minmax(0,1fr);gap:14px;padding:14px 16px;border:1px solid #8a6b3c;background:#2a241b;color:#f0d6a8;line-height:1.75}.manual-next-step b{color:#ffd88f}
.manual-facts{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:9px;margin:14px 0}.manual-facts article{min-width:0;padding:12px;border:1px solid #29445e;background:#0a1828}.manual-facts small{display:block;color:#7896ad;font-size:9px;margin-bottom:5px}.manual-facts b{display:block;color:#c7e0e8;font-size:11px;line-height:1.55;overflow-wrap:anywhere}
.manual-recovery-message{margin:12px 0;padding:12px 14px;border-left:2px solid #54cbd4;background:#0b2030;color:#a9d1dc!important;line-height:1.7}
@media(max-width:980px){.manual-hero{align-items:flex-start;flex-direction:column}.manual-hero-actions{justify-content:flex-start}.manual-facts{grid-template-columns:repeat(2,minmax(0,1fr))}.manual-guide-title{grid-template-columns:1fr}.manual-guide-title code{white-space:normal;overflow-wrap:anywhere}}
@media(max-width:650px){.manual-facts{grid-template-columns:1fr}.manual-next-step{grid-template-columns:1fr}}
.station-page-tools{margin:0 0 12px}
.station-page-tools input{width:min(100%,260px)}
.station-page-tools span{font-variant-numeric:tabular-nums}
</style>
