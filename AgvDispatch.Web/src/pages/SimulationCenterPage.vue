<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { IconPlayerPlay, IconPlayerStop, IconRefresh, IconDownload, IconFlask, IconRobot } from '@tabler/icons-vue';
import { simulationApi, simulationFresh, simulationGeometry, simulationLabel, loadCurrentSimulationMap, simulationTone, scenarioActive,
  simulationMetricLabel, simulationMetricValue, simulationMetricComparison, simulationReplayCars,
  type SimulationStatus, type SimulationRun, type SimulationControls, type SimulationReport,
  type SimulationConfigureRequest, type SimulationMapDefinition, type SimulationCall,
  type SimulationRecoveryItem, type SimulationReplay, type SimulationStationConfiguration,
  type SimulationPlcSignalTable, type SimulationStationCatalogEntry } from '../simulation';
import { importProfControlRpj } from '../profcontrol-rpj';
import { actionStateLabel, equipmentDirectionLabel, industrialText } from '../operator-labels';
import { useDispatchStore } from '../stores/dispatch';

const dispatchStore = useDispatchStore();
const status = ref<SimulationStatus | null>(null), error = ref(''), actionError = ref(''), notice = ref('');
const reading = ref(false), busy = ref(false), connected = ref(false), now = ref(Date.now());
const selectedVehicle = ref('');
const newVehicleCount = ref(2);
const blockedEdgeIds = ref<string[]>([]);
const speedScale = ref(1), readyDelay = ref(0), doneDelay = ref(5);
const run = ref<SimulationRun>({ name: 'normal-cycle', rounds: 1, intervalSeconds: 1, timeoutSeconds: 600 });
const plcFault = ref<'ready' | 'done'>('ready'), plcFaultHold = ref(10);
const restartStage = ref<'pickup' | 'delivery'>('pickup');
const mapDefinition = ref<SimulationMapDefinition | null>(null), mapFileName = ref('');
const liveMapIdentity = ref('');
const mapIdentityDraft = ref<SimulationConfigureRequest['mapIdentity']>(undefined);
const plcTableText = ref(''), plcTableFileName = ref(''), plcTableSha256 = ref('');
const plcTableMetadata = ref<SimulationPlcSignalTable | null>(null);
const plcStationCatalog = ref<SimulationStationCatalogEntry[]>([]);
const plcTableReading = ref(false);
const stationDrafts = ref<SimulationStationConfiguration[]>([
  { stationId: 'CNC-001', pickupSite: '', signalSource: 'verified' },
  { stationId: 'CNC-002', pickupSite: '', signalSource: 'verified' }
]);
const waitingSite = ref(''), dumpingSite = ref('');
const schemeName = ref(''), selectedScheme = ref(''), selectedHistory = ref(''), comparisonId = ref('');
const selectedReport = ref<SimulationReport | null>(null), comparisonReport = ref<SimulationReport | null>(null);
const replay = ref<SimulationReplay | null>(null), replayIndex = ref(0), replayPlaying = ref(false), historyReading = ref(false);
const additionalWaitingSites = ref<string[]>([]);
const inspectionSite = ref(''), dumperResourceId = ref('');
const customVehicles = ref([
  { id: 'AGV-SIM-01', initialSite: '', radiusMeters: 0.6, initialLoaded: false, batteryPercent: 86, speedScale: 1,
    accelerationMetersPerSecondSquared: 0.6, operationSeconds: 2, travelCostPercentPerMeter: 0.01, idleCostPercentPerHour: 0.1,
    voltageCurve: [{ percent: 0, voltage: 21 }, { percent: 100, voltage: 26 }], chargingGainPercentPerHour: 20 },
  { id: 'AGV-SIM-02', initialSite: '', radiusMeters: 0.6, initialLoaded: false, batteryPercent: 86, speedScale: 1,
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
const progress = computed(() => Math.min(100, Math.max(0, (status.value?.scenario?.completedRounds ?? 0) / Math.max(1, status.value?.scenario?.totalRounds ?? 1) * 100)));
const report = computed(() => selectedReport.value ?? status.value?.report);
const historicalReport = computed(() => !!report.value?.id && !!status.value?.scenario?.id && report.value.id !== status.value.scenario.id);
const scenarioNames: Record<string, string> = { 'normal-cycle': '完整业务循环', 'simultaneous-calls': '多工位同时呼叫',
  'custom-calls': '自定义呼叫计划', 'dispatch-backlog': '正式调度持续排队测试', 'disconnect-reconnect': '车辆断连与重连', 'plc-delay': 'PLC 反馈延迟',
  'plc-recovery': 'PLC卡位与恢复', 'service-restart': '后台重启与恢复屏障', 'resume-recovery': '恢复后继续原任务' };
const metrics = computed(() => Object.entries(report.value?.metrics ?? {}).filter(([, value]) => value !== null && typeof value !== 'object'));
const metricComparison = computed(() => simulationMetricComparison(report.value?.metrics, comparisonReport.value?.metrics));
const firstFailure = computed(() => typeof report.value?.firstFailure === 'string' ? report.value.firstFailure :
  report.value?.firstFailure?.message || report.value?.checks?.find(check => ['fail', 'failed', 'error'].includes(check.status))?.detail);
const versionComparison = computed(() => status.value?.versionComparison);
const configuredStationIds = computed(() => (status.value?.running ? status.value.configuration?.stations ?? stationDrafts.value : stationDrafts.value).map(station => station.stationId));
const replayFrame = computed(() => replay.value?.timeline[replayIndex.value]);
const replayGeometry = computed(() => simulationGeometry((replay.value?.map ?? status.value?.map)?.nodes ?? [],
  (replay.value?.map ?? status.value?.map)?.edges ?? [], simulationReplayCars(replayFrame.value)));
const runValid = computed(() => Number.isInteger(run.value.rounds) && run.value.rounds >= 1 && run.value.rounds <= 100 &&
  Number.isFinite(run.value.intervalSeconds) && run.value.intervalSeconds >= 0 && run.value.intervalSeconds <= 60 &&
  Number.isFinite(run.value.timeoutSeconds) && run.value.timeoutSeconds >= 30 && run.value.timeoutSeconds <= 3600);
const currentTask = computed(() => vehicle.value?.current_task || '当前无运输命令');
const snapshotTime = computed(() => stamp(status.value?.device?.time));
const mapNodes = computed(() => (mapDefinition.value?.nodes ?? []).filter(node => node.enabled !== false));
const mapResources = computed(() => (mapDefinition.value?.resources ?? []).filter(resource => (resource.boundary?.length ?? 0) >= 3));
const recovery = computed(() => status.value?.recovery);
const selectedSchemeRecord = computed(() => (status.value?.schemes ?? []).find(scheme => scheme.id === selectedScheme.value));
const selectedSchemeReady = computed(() => !!selectedSchemeRecord.value?.plcTableName && !!selectedSchemeRecord.value?.plcTableSha256);
const protocol = computed(() => status.value?.protocol);
const equipment = computed(() => status.value?.equipment);
type SimulationStage = 'overview' | 'environment' | 'scenario' | 'faults' | 'results';
const activeStage = ref<SimulationStage>('overview');
const stageTabs: { id: SimulationStage; number: string; title: string; description: string }[] = [
  { id: 'overview', number: '00', title: '运行总览', description: '当前状态、下一步和实时地图' },
  { id: 'environment', number: '01', title: '环境与地图', description: '导入地图、车辆和扩展设备' },
  { id: 'scenario', number: '02', title: '场景编排', description: '选择场景、轮次和呼叫计划' },
  { id: 'faults', number: '03', title: '故障注入', description: '车辆、PLC、道路和通信异常' },
  { id: 'results', number: '04', title: '恢复与报告', description: '现场式恢复和检查证据' }
];
const scenarioDescriptions: Record<SimulationRun['name'], string> = {
  'normal-cycle': '从工位呼叫开始，经过取箱、等待、倒料、返空和28阶段握手，验证一次完整业务闭环。',
  'simultaneous-calls': '两个呼叫工位在同一设备扫描周期置位，验证并发建单、车辆分配、交通资源等待和最终释放。',
  'custom-calls': '按指定次数、批次间隔、工位顺序和车辆运行呼叫计划，适合反复包场或定点压测。',
  'dispatch-backlog': '按时间表持续产生工位呼叫，不指定车辆。由正式后台建单、选择空闲车辆和维护队列，检查忙车排除、重复占车与等待链。',
  'disconnect-reconnect': '在正式运输命令执行中切断车端通信，再恢复连接，验证原任务和路权能否续接。',
  'plc-delay': '延迟或阻止倒料机“就绪许可”和“倒料完成”反馈，验证握手阶段不会越级，恢复后仍能按原链路闭合。',
  'plc-recovery': '把指定 PLC 信号卡住一段时间后恢复，验证卡位期间任务保持等待且没有跳过步骤。',
  'service-restart': '在活动运输持有路权时重启私有仿真后台，验证原任务、命令和路权保留，恢复屏障未自动放行。',
  'resume-recovery': '在逐项核对车辆、载荷和 PLC 周期后放行，只续接原任务编号，不创建替代任务。'
};
const currentScenarioDescription = computed(() => scenarioDescriptions[run.value.name]);
const modeledTaskCount = computed(() => Object.values(cars.value).filter(car => !!car.current_task).length);
const offlineVehicleCount = computed(() => Object.values(cars.value).filter(car => !!car.offline).length);
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
    title: '当前实例保留了上次设备历史', detail: '为保证故障、载荷和路权不会串到下一轮，已经运行或停止过的实例不能原地重启。请拉取当前地图后创建新的隔离实例；车辆数量可继续设为 2 辆。', button: '去新建干净实例' };
  if (!status.value.running) return { stage: 'overview' as const, kind: 'start' as const, eyebrow: '准备完成',
    title: '启动当前隔离仿真环境', detail: `将启动实例 ${status.value.rootName || '—'} 的正式仿真后台、虚拟车辆和共享 PLC；不影响日常 5199 后台。`, button: '启动仿真环境' };
  if (active.value) return { stage: 'overview' as const, kind: 'observe' as const, eyebrow: '场景运行中',
    title: '观察任务、车辆和 PLC 的实际反馈', detail: status.value.scenario?.message || '场景正在执行；可从下方地图、车辆状态和场景事件核对现场反馈。', button: '查看运行总览' };
  if (report.value) return { stage: 'results' as const, kind: 'results' as const, eyebrow: '已有检查结果',
    title: '先核对上一次报告，再开始下一场景', detail: `最近报告为“${scenarioNames[report.value.name ?? ''] ?? report.value.name ?? '未知场景'}”。失败、中止和证据不足都不解释为通过。`, button: '查看报告与证据' };
  return { stage: 'scenario' as const, kind: 'scenario' as const, eyebrow: '设备已就绪',
    title: '选择场景并运行', detail: '第一次建议选择“完整业务循环”、1轮和600秒超时。运行后页面会自动显示阶段、任务、车辆、PLC 与路权检查。', button: '开始编排场景' };
});
function openStage(stage: SimulationStage) { activeStage.value = stage; }
function followNextAction() {
  activeStage.value = nextAction.value.stage;
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
  if (stationDrafts.value.length < 1 || stationDrafts.value.length > 32) problems.push('工位数量须为1至32个。');
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
  if (!waitingSite.value) problems.push('未选择主等待位。');
  if (!dumpingSite.value) problems.push('未选择倒料位。');
  if (!inspectionSite.value) problems.push('未选择检修或设备观察点。');
  if (!dumperResourceId.value) problems.push('未选择倒料资源边界。');
  if (waitingSite.value && waitingSite.value === dumpingSite.value) problems.push('主等待位与倒料位不能相同。');
  if (additionalWaitingSites.value.some(site => site === waitingSite.value || site === dumpingSite.value || !mapNodes.value.some(node => node.id === site)))
    problems.push('其他等待位包含重复、无效或与倒料位相同的节点。');
  if (customVehicles.value.length < 1 || customVehicles.value.length > 8) problems.push('车辆数量须为1至8辆。');
  customVehicles.value.forEach((vehicle, index) => {
    const label = `车辆${index + 1}`;
    if (!/^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$/.test(vehicle.id)) problems.push(`${label} ID格式无效。`);
    if (!vehicle.initialSite || !mapNodes.value.some(node => node.id === vehicle.initialSite)) problems.push(`${label}未选择有效初始节点。`);
    if (!Number.isFinite(vehicle.radiusMeters) || vehicle.radiusMeters < 0.1 || vehicle.radiusMeters > 5) problems.push(`${label}包络半径无效。`);
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
      customVehicles.value = imported.sourceVehicles.map((vehicle, index) => ({
        id: `RPJ-AGV-${String(index + 1).padStart(2, '0')}`,
        initialSite: '', radiusMeters: Math.max(.1, Math.max(vehicle.widthMeters, vehicle.heightMeters) / 2),
        initialLoaded: false, batteryPercent: 86, speedScale: 1, accelerationMetersPerSecondSquared: .6,
        operationSeconds: 2, travelCostPercentPerMeter: .01, idleCostPercentPerHour: .1,
        voltageCurve: [{ percent: 0, voltage: 21 }, { percent: 100, voltage: 26 }], chargingGainPercentPerHour: 20
      }));
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
    const text = (await file.text()).replace(/^\uFEFF/, '');
    const validated = await simulationApi<SimulationPlcSignalTable>('/point-table-validate', { fileName: file.name, csvText: text });
    adoptPlcTable(validated, text);
    notice.value = `PLC点表已验证：${validated.stationCount ?? 0}个工位、${validated.signalCount ?? 0}条点位，SHA256 ${validated.sha256.slice(0, 12)}…`;
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
  busy.value = true;
  try {
    const imported = await loadCurrentSimulationMap(path => dispatchStore.api(path));
    if (disposed) return;
    const live = imported.identity;
    mapDefinition.value = imported.definition;
    clearPlcTableDraft();
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
    resetVehicleOrigins();
    notice.value = `已复制当前发布地图的完整道路、曲线和资源绑定。${imported.parameterWarning || '已套用同一地图版本的工位参数。'}请选择检修点，并核对倒料资源和车辆起点后创建隔离实例。`;
  } catch (reason) {
    if (!disposed) actionError.value = reason instanceof Error ? reason.message : String(reason);
  } finally { busy.value = false; }
}
function vehicleCountChanged() {
  const count = Math.max(1, Math.min(8, Number(newVehicleCount.value) || 1));
  const current = [...customVehicles.value];
  customVehicles.value = Array.from({ length: count }, (_, index) => current[index] ?? {
    id: `AGV-SIM-${String(index + 1).padStart(2, '0')}`, initialSite: '', radiusMeters: 0.6,
    initialLoaded: false, batteryPercent: 86, speedScale: 1, accelerationMetersPerSecondSquared: 0.6,
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
async function selectHistory() {
  replayPlaying.value = false; replay.value = null; replayIndex.value = 0;
  if (!selectedHistory.value) { selectedReport.value = null; return; }
  historyReading.value = true;
  try { selectedReport.value = await simulationApi<SimulationReport>('/report?id=' + encodeURIComponent(selectedHistory.value)); }
  catch (reason) { actionError.value = reason instanceof Error ? reason.message : String(reason); }
  finally { historyReading.value = false; }
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
    waitingSite.value = next.configuration.dumper.waitingSite;
    additionalWaitingSites.value = (next.configuration.dumper.waitingSites ?? []).filter(site => site !== waitingSite.value);
    dumpingSite.value = next.configuration.dumper.dumpingSite; dumperResourceId.value = next.configuration.dumper.resourceId;
    inspectionSite.value = next.configuration.inspectionSite;
    const boundTable = next.configuration.plcSignalTable ?? next.plcSignalTable ?? null;
    if (boundTable) {
      plcTableFileName.value = boundTable.fileName;
      plcTableSha256.value = boundTable.sha256;
      plcTableMetadata.value = { ...boundTable, ...(plcTableText.value ? { csvText: plcTableText.value } : {}) };
      plcStationCatalog.value = boundTable.stations ?? next.stationCatalog ?? [];
    }
    const defaults = customVehicles.value[0]!;
    customVehicles.value = next.configuration.vehicles.map(vehicle => ({ ...defaults, ...vehicle,
      voltageCurve: vehicle.voltageCurve?.map(point => ({ ...point })) ?? [{ percent: 0, voltage: 21 }, { percent: 100, voltage: 26 }] }));
    newVehicleCount.value = customVehicles.value.length;
    callPattern.value = stationDrafts.value.map(station => station.stationId).join(',');
    callBatchSize.value = Math.min(callBatchSize.value, stationDrafts.value.length);
    selectedHistory.value = ''; selectedReport.value = null; replay.value = null; replayPlaying.value = false;
  }
  if (!selectedScheme.value) selectedScheme.value = next.selectedSchemeId ?? next.schemes?.[0]?.id ?? '';
  status.value = next; connected.value = true;
  if (!Object.hasOwn(next.device?.cars ?? {}, selectedVehicle.value)) selectedVehicle.value = Object.keys(next.device?.cars ?? {})[0] ?? '';
}
async function refresh(force = false) {
  if (disposed || reading.value || (busy.value && !force)) return;
  reading.value = true; const request = new AbortController(); controller = request;
  const timeout = setTimeout(() => request.abort(), 45000);
  try { const next = await simulationApi<SimulationStatus>('', undefined, request.signal); if (!disposed) { accept(next); error.value = ''; } }
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
  const timeout = setTimeout(() => request.abort(), 60000);
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
watch(selectedVehicle, () => { blockedEdgeIds.value = [...appliedBlockedEdges.value]; });
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
  <main class="simulation-page">
    <header class="simulation-title">
      <div>
        <p class="page-kicker">隔离环境 · 正式调度仿真</p>
        <h2><IconFlask :size="24" />仿真测试中心</h2>
        <p>按“环境 → 场景 → 故障 → 证据”顺序操作。虚拟设备只通过正式调度接口回读，不使用改库或改动任务终态制造通过。</p>
      </div>
      <div class="simulation-header-actions">
        <div class="mode-card"><small>当前仿真方式</small><b>设备回环 + 正式调度</b><span>虚拟车辆与设备通过正式通信链路参与测试</span></div>
        <button :disabled="busy || reading" @click="refresh()"><IconRefresh :size="16" />刷新状态</button>
      </div>
    </header>

    <p v-if="error || actionError" class="simulation-error" role="alert">{{ industrialText(actionError || error) }}</p>
    <p v-if="notice" class="simulation-notice" role="status">{{ industrialText(notice) }}</p>

    <section class="mission-control" aria-labelledby="simulation-environment">
      <div class="mission-identity">
        <span class="mission-label">当前实例</span>
        <h3 id="simulation-environment">{{ status?.rootName || '尚未选择仿真实例' }}</h3>
        <p>{{ industrialText(status?.error || status?.message || '正在读取仿真环境配置…') }}</p>
        <p v-if="status?.startupPlan?.message">{{ industrialText(status.startupPlan.message) }}</p>
        <p v-if="requiresFreshInstance" class="inline-warning">该实例已有历史状态，不能原地重启。请到“环境与地图”创建新的干净实例。</p>
      </div>
      <div class="mission-runtime">
        <span :class="['state-pill', cleanupRequired ? 'fail' : fresh ? 'pass' : 'unknown']">{{ !connected ? '控制器未连接' : !status?.enabled ? '未配置' : starting ? '启动中 · 正在连接' : cleanupRequired ? '启动失败 · 待清理' : !status?.running ? '环境已停止' : fresh ? '运行中 · 反馈有效' : '运行中 · 反馈过期' }}</span>
        <small>最近采样 {{ snapshotTime }}</small>
      </div>
      <div class="mission-metrics">
        <div><small>车辆</small><b>{{ vehicleIds.length }}</b><span>{{ offlineVehicleCount }} 辆离线</span></div>
        <div><small>执行任务</small><b>{{ modeledTaskCount }}</b><span>来自虚拟车端</span></div>
        <div><small>场景</small><b>{{ simulationLabel(status?.scenario?.status) }}</b><span>{{ status?.scenario?.completedRounds ?? 0 }}/{{ status?.scenario?.totalRounds || run.rounds }} 轮</span></div>
      </div>
      <div class="mission-actions">
        <button class="primary" :disabled="!available || !!status?.running || starting || requiresFreshInstance || cleanupRequired" @click="action('start')"><IconPlayerPlay :size="16" />{{ starting ? '正在启动…' : cleanupRequired ? '启动失败待清理' : requiresFreshInstance ? '请先新建实例' : '启动仿真环境' }}</button>
        <button :disabled="!available || (!status?.running && !starting && !cleanupRequired)" @click="action('stop')"><IconPlayerStop :size="16" />{{ cleanupRequired ? '停止并清理' : '停止仿真环境' }}</button>
      </div>
    </section>

    <section class="next-step-card" :data-action="nextAction.kind" aria-live="polite">
      <div class="next-step-index">建议</div>
      <div class="next-step-copy"><small>{{ nextAction.eyebrow }}</small><h3>{{ nextAction.title }}</h3><p>{{ industrialText(nextAction.detail) }}</p></div>
      <button class="primary" @click="followNextAction">{{ nextAction.button }}</button>
    </section>

    <nav class="stage-tabs" aria-label="仿真工作步骤">
      <button v-for="tab in stageTabs" :key="tab.id" :class="{ active: activeStage === tab.id }" :aria-selected="activeStage === tab.id" @click="openStage(tab.id)">
        <span>{{ tab.number }}</span><strong>{{ tab.title }}</strong><small>{{ tab.description }}</small>
      </button>
    </nav>

    <section v-show="activeStage === 'overview'" class="stage-surface overview-stage">
      <div class="first-run-guide">
        <header><div><p class="section-kicker">首次使用</p><h3>第一次仿真按这四步走</h3></div><span>先建干净实例，再启动和运行场景</span></header>
        <div class="guide-grid">
          <article :class="{ complete: !!status?.enabled, current: !status?.enabled || requiresFreshInstance }"><b>01</b><div><strong>拉取地图并新建实例</strong><p>进入“环境与地图”，拉取当前运行地图，核对工位、等待位、倒料资源与车辆起点，然后创建隔离实例。</p><button @click="openStage('environment')">配置地图与车辆</button></div></article>
          <article :class="{ complete: !!status?.running, current: !!status?.enabled && !status?.running && !requiresFreshInstance && !cleanupRequired }"><b>02</b><div><strong>启动隔离环境</strong><p>新建实例后点击启动。每个实例只从干净状态启动一次；如果启动失败，先停止并清理，再新建实例。</p><button :disabled="!available || !!status?.running || starting || requiresFreshInstance || cleanupRequired" @click="action('start')">启动仿真环境</button></div></article>
          <article :class="{ complete: !!report, current: !!status?.running && !active && !report }"><b>03</b><div><strong>选择并运行场景</strong><p>先从完整业务循环 1 轮开始；双车排队测试可选择多工位同时呼叫，再按需设置轮次和呼叫计划。</p><button @click="openStage('scenario')">编排场景</button></div></article>
          <article :class="{ current: !!report }"><b>04</b><div><strong>查看结果与现场恢复</strong><p>报告显示任务、命令、PLC、交通资源、残留占用和恢复屏障。异常场景结束后，再到恢复页逐项核对。</p><button @click="openStage('results')">查看恢复与报告</button></div></article>
        </div>
      </div>

      <div class="overview-grid">
        <section class="simulation-panel map-panel" aria-labelledby="simulation-map">
          <header><div><p class="section-kicker">实时位置</p><h3 id="simulation-map">车辆与交通实况</h3></div><span :class="['state-pill', fresh ? 'pass' : 'unknown']">{{ fresh ? '设备实际反馈' : '历史位置 / 尚无反馈' }}</span></header>
          <svg v-if="geometry.nodes.length" class="simulation-map" viewBox="0 0 800 430" role="img" aria-label="仿真地图及虚拟车辆位置">
            <g class="roads"><path v-for="edge in geometry.edges" :key="edge.key" :d="edge.path" :class="{ blocked: !!edge.id && appliedBlockedEdges.includes(edge.id) }" /></g>
            <g v-for="node in geometry.nodes" :key="node.id" class="map-node"><circle :cx="node.x" :cy="node.y" r="4"/><text :x="node.x + 8" :y="node.y - 8">{{ node.id }}</text></g>
            <g v-for="(car, index) in geometry.cars" :key="car.id" class="map-car" :class="{ stale: !fresh, offline: car.offline, selected: selectedVehicle === car.id }" :style="{ '--car-color': ['#41d9bf', '#6fb9ff', '#e8bf64', '#cb9aff'][index % 4] }" :transform="`translate(${car.x} ${car.y})`">
              <circle r="17"/><rect x="-10" y="-7" width="20" height="14" rx="3" :transform="`rotate(${-(car.angle ?? 0) * 180 / Math.PI})`"/><text x="0" y="-26" text-anchor="middle">{{ car.id }}</text><title>{{ car.id }} · {{ car.station }} · {{ car.loaded ? '载箱' : '空载' }}</title>
            </g>
          </svg>
          <div v-else class="map-empty"><strong>当前没有可显示的位置反馈</strong><span>启动已配置实例后，地图位置来自虚拟设备快照。</span></div>
          <div class="map-caption"><span>道路、车辆和阻塞状态均来自设备模型反馈。</span><span>{{ vehicleIds.length }} 辆 · {{ offlineVehicleCount }} 辆离线</span></div>
        </section>

        <section class="simulation-panel live-operations">
          <header><div><p class="section-kicker">当前状态</p><h3>当前运行</h3></div><span :class="['state-pill', simulationTone(status?.scenario?.status)]">{{ simulationLabel(status?.scenario?.status) }}</span></header>
          <div class="live-facts">
            <div><small>场景</small><b>{{ scenarioNames[status?.scenario?.name ?? run.name] ?? '待选择' }}</b></div>
            <div><small>进度</small><b>{{ status?.scenario?.completedRounds ?? 0 }} / {{ status?.scenario?.totalRounds || run.rounds }} 轮</b></div>
            <div><small>设备</small><b>{{ fresh ? '反馈有效' : '需要刷新' }}</b></div>
            <div><small>扩展设备</small><b>{{ protocol?.running || equipment?.running ? '部分运行' : '未启用' }}</b></div>
          </div>
          <div class="scenario-progress compact"><div><b>{{ Math.round(progress) }}%</b><span>{{ active ? '执行中' : '当前未执行' }}</span></div><progress :value="progress" max="100" aria-label="场景完成进度"/><p>{{ industrialText(status?.scenario?.message || '进入场景编排选择测试目标；运行后此处显示实际阶段和等待原因。') }}</p></div>
          <div class="toolbar quick-actions"><button class="primary" @click="openStage('scenario')">选择场景</button><button :disabled="!available || !active" @click="action('abort')">中止后续编排</button><button @click="openStage('faults')">注入故障</button></div>
        </section>
      </div>
    </section>

    <section v-show="activeStage === 'environment'" class="stage-surface environment-stage">
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
          <button :disabled="busy || !dispatchStore.fresh || dispatchStore.demoMode || !dispatchStore.snapshot?.map || !!status?.running || starting || active" @click="loadCurrentMap"><IconRefresh :size="15" />拉取当前运行地图</button>
          <label class="file-picker">选择地图文件<input type="file" accept=".json,.agvmap.json,.rpj,application/json" :disabled="busy || !!status?.running || starting || active" @change="loadMapFile" /></label>
          <div><b v-if="mapFileName">{{ mapFileName }}</b><span v-if="liveMapIdentity">{{ liveMapIdentity }}</span><span v-if="mapDefinition">{{ mapDefinition.nodes.length }} 节点 · {{ mapDefinition.edges.length }} 道路 · {{ mapDefinition.resources.length }} 资源</span><span v-if="mapIdentityDraft?.definitionHash">地图摘要 {{ mapIdentityDraft.definitionHash.slice(0, 12) }}…</span><span v-else-if="!mapDefinition">尚未载入自定义地图</span></div>
        </div>
        <div class="plc-table-binding">
          <div class="designer-section-title"><span>PLC</span><div><b>绑定当前地图对应的点表</b><small>点表随方案保存；创建实例时复制为实例专属 simulation-signals.csv。</small></div></div>
          <div class="form-row">
            <label class="file-picker">选择 PLC 点表 CSV<input type="file" accept=".csv,text/csv" :disabled="busy || !!status?.running || starting || active" @change="loadPlcTableFile" /></label>
            <button :disabled="busy || plcTableReading || !!status?.running || starting || active || !status?.plcSignalTable" @click="loadCurrentPlcTable">载入当前实例点表</button>
          </div>
          <div v-if="plcTableMetadata" class="plc-table-summary">
            <b>{{ plcTableFileName }}</b>
            <span>{{ plcTableMetadata.stationCount ?? plcStationCatalog.length }} 工位 · {{ plcTableMetadata.signalCount ?? 0 }} 条点位 · {{ plcTableMetadata.bytes ?? 0 }} 字节</span>
            <code>SHA256 {{ plcTableSha256 }}</code>
          </div>
          <p v-else class="inline-warning">未选择点表。仿真不会使用仓库全局 PLC 点表，也不会悄悄沿用现场后台实验室文件。</p>
        </div>
        <template v-if="mapDefinition">
          <div class="designer-section-title"><span>01</span><div><b>业务拓扑映射</b><small>把地图节点绑定到当前 28 阶段业务模型。</small></div></div>
          <fieldset :disabled="busy || !!status?.running || starting || active" class="topology-grid">
            <div class="station-designer">
              <div v-for="(station, index) in stationDrafts" :key="index" class="station-config-row">
                <label>工位编号<input v-model.trim="station.stationId" placeholder="CNC-001" maxlength="7" /></label>
                <label>工位取货节点<select v-model="station.pickupSite" @change="resetVehicleOrigins"><option value="">请选择节点</option><option v-for="node in mapNodes" :key="node.id" :value="node.id">{{ node.id }} · {{ node.agvSiteId }}</option></select></label>
                <label>信号依据<select v-model="station.signalSource"><option value="verified">当前有效点表</option><option value="isolated-synthetic">隔离合成点位（现场未确认）</option></select></label>
                <label>派车优先级<input :value="station.priority" type="number" min="0" max="100" step="1" placeholder="沿用默认" @input="station.priority = ($event.target as HTMLInputElement).value === '' ? undefined : Number(($event.target as HTMLInputElement).value)" /></label>
                <button :disabled="stationDrafts.length <= 1" @click="stationDrafts.splice(index, 1); resetVehicleOrigins()">删除工位</button>
                <small :class="{ 'inline-warning': station.signalSource === 'isolated-synthetic' }">{{ station.signalSource === 'isolated-synthetic' ? '仅用于隔离仿真，不代表现场已有此工位和地址。' : (status?.stationCatalog?.find(item => item.stationId === station.stationId && item.signalSource === 'verified')?.sourceLabel || '必须与当前已确认且启用的点表工位一致。') }}</small>
              </div>
              <button :disabled="stationDrafts.length >= 32" @click="addStation">增加隔离仿真工位</button><small>本页最多配置32个工位；仅已确认且启用的点表工位可选择“当前有效点表”。优先级0–100，数值越大越优先。</small>
            </div>
            <label>主等待位节点<select v-model="waitingSite" @change="additionalWaitingSites = additionalWaitingSites.filter(site => site !== waitingSite); resetVehicleOrigins()"><option v-for="node in mapNodes" :key="node.id" :value="node.id">{{ node.id }}</option></select></label>
            <label>其他等待位（可多选）<select v-model="additionalWaitingSites" multiple @change="resetVehicleOrigins"><option v-for="node in mapNodes.filter(node => node.id !== waitingSite && node.id !== dumpingSite)" :key="node.id" :value="node.id">{{ node.id }}</option></select><small>多车排队使用全部等待位；拉取当前地图时同步带入现场候车位配置。</small></label>
            <label>倒料位节点<select v-model="dumpingSite" @change="resetVehicleOrigins"><option v-for="node in mapNodes" :key="node.id" :value="node.id">{{ node.id }}</option></select></label>
            <label>检修 / 设备观察点<select v-model="inspectionSite" @change="resetVehicleOrigins"><option value="">请选择检修或观察点</option><option v-for="node in mapNodes" :key="node.id" :value="node.id">{{ node.id }}</option></select></label>
            <label>倒料资源边界<select v-model="dumperResourceId"><option value="">请选择倒料资源边界</option><option v-for="resource in mapResources" :key="resource.id" :value="resource.id">{{ resource.id }} · 容量 {{ resource.capacity ?? 1 }}</option></select></label>
          </fieldset>
          <div class="vehicle-designer">
            <div class="designer-section-title"><span>02</span><div><b>车辆画像 · {{ customVehicles.length }} 辆</b><small>配置车辆、电机、电池和动作模型参数。</small></div></div>
            <div v-for="(vehicle, index) in customVehicles" :key="index" class="vehicle-config-row">
              <label>车辆 ID<input v-model.trim="vehicle.id" maxlength="32" /></label>
              <label>初始节点<select v-model="vehicle.initialSite"><option v-for="node in mapNodes" :key="node.id" :value="node.id">{{ node.id }}</option></select></label>
              <label>包络半径 / m<input v-model.number="vehicle.radiusMeters" type="number" min="0.1" max="5" step="0.05" /></label>
              <label>电量 / %<input v-model.number="vehicle.batteryPercent" type="number" min="0" max="100" step="1" /></label>
              <label>速度倍率<input v-model.number="vehicle.speedScale" type="number" min="0.1" max="10" step="0.1" /></label>
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
            <button :disabled="customVehicles.length >= 8" @click="customVehicles.push({ id: `AGV-SIM-${String(customVehicles.length + 1).padStart(2, '0')}`, initialSite: '', radiusMeters: 0.6, initialLoaded: false, batteryPercent: 86, speedScale: 1, accelerationMetersPerSecondSquared: 0.6, operationSeconds: 2, travelCostPercentPerMeter: 0.01, idleCostPercentPerHour: 0.1, voltageCurve: [{ percent: 0, voltage: 21 }, { percent: 100, voltage: 26 }], chargingGainPercentPerHour: 20 }); resetVehicleOrigins()">增加车辆</button>
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
              <div class="toolbar"><button class="primary" :disabled="busy || protocol?.running" @click="startProtocol">启动协议服务器</button><button :disabled="busy || !protocol?.running" @click="action('protocol-stop')">停止协议服务器</button></div>
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
              <div class="toolbar"><label>楼层运行 / 秒<input v-model.number="equipmentTravelSeconds" type="number" min="0.1" max="300" step="0.1" /></label><label>开关门 / 秒<input v-model.number="equipmentDoorSeconds" type="number" min="0.1" max="60" step="0.1" /></label><button class="primary" :disabled="busy || equipment?.running" @click="startEquipment">启动设备模型</button><button :disabled="busy || !equipment?.running" @click="action('equipment-stop')">停止设备模型</button></div>
              <dl class="equipment-facts"><dt>当前 / 目标楼层</dt><dd>{{ equipment?.currentFloor || '—' }} → {{ equipment?.targetFloor || '—' }}</dd><dt>运行状态</dt><dd>{{ equipment?.fault ? '故障' : equipment?.moving ? `${equipmentDirectionLabel(equipment?.direction)} · ${Math.round((equipment?.progress ?? 0) * 100)}%` : equipmentDirectionLabel(equipment?.direction) }}</dd><dt>门状态</dt><dd>{{ equipmentDirectionLabel(equipment?.door) }}</dd></dl>
              <div class="toolbar"><button :disabled="busy || !equipment?.running || equipment?.fault || equipment?.moving || equipment?.door !== 'Closed'" @click="equipmentControl({ elevator: { targetFloor: '2' } })">前往2层</button><button :disabled="busy || !equipment?.running || equipment?.fault || equipment?.moving || equipment?.door !== 'Closed'" @click="equipmentControl({ elevator: { targetFloor: '1' } })">前往1层</button><button :disabled="busy || !equipment?.running || equipment?.moving" @click="equipmentControl({ elevator: { doorCommand: 'open' } })">开门</button><button :disabled="busy || !equipment?.running || equipment?.moving" @click="equipmentControl({ elevator: { doorCommand: 'close' } })">关门</button><button :disabled="busy || !equipment?.running" @click="equipmentControl({ elevator: { fault: true } })">注入故障</button><button :disabled="busy || !equipment?.running" @click="equipmentControl({ elevator: { reset: true } })">复位设备</button></div>
            </div>
            <div>
              <label>立即设置信号<textarea v-model="signalSetText" rows="3" spellcheck="false"></textarea></label>
              <label>信号时间轴 JSON<textarea v-model="signalScheduleText" rows="4" spellcheck="false"></textarea></label>
              <div class="toolbar"><button :disabled="busy || !equipment?.running" @click="setEquipmentSignals">应用信号及时间轴</button><button :disabled="busy || !equipment?.running" @click="equipmentControl({ signals: { clearSchedule: true } })">清除时间轴</button></div>
              <pre>{{ JSON.stringify({ signals: equipment?.signals || {}, scheduled: equipment?.schedules?.length || 0, recent: equipment?.signalEvents?.slice(-5) || [] }, null, 2) }}</pre>
            </div>
          </div>
        </section>
      </div>
    </section>

    <section v-show="activeStage === 'scenario'" class="stage-surface scenario-stage">
      <div class="scenario-layout">
        <section class="simulation-panel scenario-panel" aria-labelledby="simulation-scenario">
          <header><div><p class="section-kicker">场景设置</p><h3 id="simulation-scenario">场景编排</h3></div><span :class="['state-pill', simulationTone(status?.scenario?.status)]">{{ simulationLabel(status?.scenario?.status) }}</span></header>
          <fieldset :disabled="busy || active">
            <label>测试场景<select v-model="run.name" aria-label="测试场景"><option value="normal-cycle">完整业务循环</option><option value="simultaneous-calls">多工位同时呼叫</option><option value="dispatch-backlog">正式调度持续排队测试</option><option value="custom-calls">自定义呼叫计划</option><option value="disconnect-reconnect">车辆断连与重连</option><option value="plc-delay">PLC 反馈延迟</option><option value="plc-recovery">PLC卡位与恢复</option><option value="service-restart">后台重启与恢复屏障</option><option value="resume-recovery">恢复后继续原任务</option></select></label>
            <div class="form-row"><label>循环轮数<input v-model.number="run.rounds" type="number" min="1" max="100" step="1" /></label><label>轮间隔 / 秒<input v-model.number="run.intervalSeconds" type="number" min="0" max="60" /></label></div>
            <label>单轮超时 / 秒<input v-model.number="run.timeoutSeconds" type="number" min="30" max="3600" /></label>
            <div v-if="['custom-calls', 'dispatch-backlog'].includes(run.name)" class="call-plan">
              <div class="form-row"><label>呼叫总数<input v-model.number="callCount" type="number" min="1" max="500" step="1" /></label><label>批次间隔 / 秒<input v-model.number="callInterval" type="number" min="0" max="60" step="1" /></label></div>
              <div class="form-row"><label>每批同时呼叫<select v-model.number="callBatchSize"><option v-for="count in configuredStationIds.length" :key="count" :value="count">{{ count }}个工位</option></select></label><label v-if="run.name === 'custom-calls'">指定车辆<select v-model="callVehicleId"><option value="">场景选择可达车辆</option><option v-for="id in vehicleIds" :key="id" :value="id">{{ id }}</option></select></label><p v-else class="inline-warning">由正式后台自主派车；呼叫计划中不指定车辆。</p></div>
              <label>工位轮转顺序<input v-model.trim="callPattern" placeholder="CNC-001,CNC-002" /></label>
              <small>{{ run.name === 'dispatch-backlog' ? '按时间持续追加，不等上一批完成；同一工位仍有原任务时，后续呼叫保留在计划队列。验证忙车排除建议1车2工位或2车3工位，第3工位须明确使用隔离合成点位。只有真正观察到全部车辆忙碌期间仍有正式待派任务，才证明覆盖了积压；2车2工位可能无法形成积压。工位优先级在新建环境前配置，并写入报告。' : '同一批次工位会在同一设备扫描中置位；指定车辆不可达时停止并报告。此场景按批次完成后继续。' }}</small>
            </div>
            <div v-if="run.name === 'plc-recovery'" class="form-row"><label>卡位信号<select v-model="plcFault"><option value="ready">倒料机就绪许可</option><option value="done">倒料完成，允许离开</option></select></label><label>卡位保持 / 秒<input v-model.number="plcFaultHold" type="number" min="1" max="120" /></label></div>
            <label v-if="run.name === 'service-restart'">重启注入阶段<select v-model="restartStage"><option value="pickup">原取料运输执行中（可验证到站补记后续接）</option><option value="delivery">满箱运输执行中（保留更复杂恢复现场）</option></select></label>
          </fieldset>
          <div class="toolbar"><button class="primary" :disabled="!canControl || active || !runValid || (['custom-calls', 'dispatch-backlog'].includes(run.name) && !callPlanValid)" @click="startScenario"><IconPlayerPlay :size="16" />运行场景</button><button :disabled="!available || !active" @click="action('abort')">中止后续场景</button></div>
          <p v-if="run.name === 'service-restart'" class="inline-warning">该场景结束时保留原任务和恢复屏障，不表示完整业务循环已完成。</p>
          <p v-if="run.name === 'resume-recovery'" class="inline-warning">只有在恢复页逐项核对并放行后才能运行；它只续接原任务编号。</p>
          <p v-if="!runValid" class="inline-warning">轮数 1–100、间隔 0–60 秒、超时 30–3600 秒。</p>
        </section>

        <aside class="scenario-brief">
          <div><p class="section-kicker">验证目标</p><h3>{{ scenarioNames[run.name] }}</h3><p>{{ currentScenarioDescription }}</p></div>
          <dl><dt>建议起点</dt><dd>{{ status?.running && fresh ? '环境与设备反馈已就绪，可以直接运行' : '先启动仿真环境，再运行场景' }}</dd><dt>观察重点</dt><dd>原任务步骤、运输命令、虚拟设备接收、PLC 电平、交通资源和残留占用</dd><dt>结束方式</dt><dd>通过、失败、中止、未知分别保留，不以旧报告代替本轮结果</dd></dl>
          <div class="scenario-progress"><div><b>{{ status?.scenario?.completedRounds ?? 0 }} / {{ status?.scenario?.totalRounds ?? run.rounds }} 轮</b><span>{{ Math.round(progress) }}%</span></div><progress :value="progress" max="100" aria-label="场景完成进度"/><p>{{ industrialText(status?.scenario?.message || '运行后显示当前阶段、等待原因和设备实际反馈。') }}</p></div>
          <button @click="openStage('faults')">运行中需要注入故障</button>
        </aside>
      </div>
    </section>

    <section v-show="activeStage === 'faults'" class="stage-surface fault-stage">
      <div class="fault-intro"><div><p class="section-kicker">故障控制</p><h3>先选择目标，再注入单点故障</h3></div><p>每次控制都会校验反馈编号。画面提交成功不等于故障已生效，须查看设备实际反馈和场景事件。</p></div>
      <div class="simulation-grid fault-grid">
        <section class="simulation-panel vehicle-panel" aria-labelledby="simulation-vehicle">
          <header><div><p class="section-kicker">虚拟车辆</p><h3 id="simulation-vehicle"><IconRobot :size="18" />虚拟车辆与通信</h3></div><span v-if="!fresh" class="inline-warning">反馈无效，控制已禁用</span></header>
          <label class="vehicle-select">选择车辆<select v-model="selectedVehicle" aria-label="选择虚拟车辆"><option v-if="!vehicleIds.length" value="">尚无车辆</option><option v-for="id in vehicleIds" :key="id" :value="id">{{ id }}</option></select></label>
          <dl class="vehicle-facts"><dt>状态</dt><dd>{{ !fresh ? '未知 / 历史快照' : vehicle?.offline ? '通信断开' : vehicle?.paused ? '运动暂停' : vehicle?.blockedByVehicle ? '近障停车等待' : vehicle?.current_task ? '执行中' : '空闲' }}</dd><dt>位置</dt><dd>{{ vehicle?.station || '尚无反馈' }}</dd><dt>载箱</dt><dd>{{ !fresh ? '未知' : vehicle?.loaded ? '有箱' : '无箱' }}</dd><dt>电量 / 电压</dt><dd>{{ vehicle?.batteryPercent !== undefined ? `${vehicle.batteryPercent.toFixed(1)}%` : '未知' }} · {{ vehicle?.batteryVoltage !== undefined ? `${vehicle.batteryVoltage.toFixed(2)} V` : '未知' }}{{ vehicle?.charging ? ' · 充电中' : '' }}</dd><dt>速度 / 里程</dt><dd>{{ vehicle?.speedMps !== undefined ? `${vehicle.speedMps.toFixed(2)} m/s` : '未知' }} · {{ vehicle?.distanceMeters !== undefined ? `${vehicle.distanceMeters.toFixed(2)} m` : '未知' }}</dd><dt>原运输编号</dt><dd>{{ currentTask }}</dd><dt>当前等待原因</dt><dd>{{ industrialText(vehicle?.waitReason || status?.scenario?.message || '尚无等待原因反馈') }}</dd></dl>
          <fieldset :disabled="!canControl || !selectedVehicle"><div class="toolbar"><button @click="carControl({ paused: true })">暂停运动</button><button @click="carControl({ paused: false })">恢复运动</button><button @click="carControl({ offline: true })">断开通信</button><button @click="carControl({ offline: false })">恢复通信</button><button @click="carControl({ charging: true })">开始充电</button><button @click="carControl({ charging: false })">停止充电</button><button @click="carControl({ dropNextResponse: true })">丢弃下一响应</button></div><div class="speed-control"><label>运动倍率<input v-model.number="speedScale" type="number" min="0.1" max="10" step="0.1" /></label><button :disabled="!Number.isFinite(speedScale) || speedScale < 0.1 || speedScale > 10" @click="carControl({ speedScale })">应用倍率</button></div></fieldset>
          <small v-if="vehicle?.simulationControl">设备反馈控制编号：{{ vehicle.simulationControl.sequence || '尚无反馈' }} · 当前运动倍率 {{ vehicle.speedScale ?? '未知' }}<span v-if="vehicle.simulationControl.responseDropPending"> · 等待丢弃下一响应</span></small>
          <p v-if="vehicle?.simulationControl?.error" class="inline-warning">{{ vehicle.simulationControl.error }}</p>
          <details class="edge-controls"><summary>道路阻塞 · 已反馈 {{ appliedBlockedEdges.length }} 条</summary><fieldset :disabled="!canControl || !selectedVehicle"><p>选择道路，在所选虚拟车经过时阻止运动，观察调度等待与超时处理。</p><div class="edge-options"><label v-for="edge in blockableEdges" :key="edge.id"><input v-model="blockedEdgeIds" type="checkbox" :value="edge.id" />{{ edge.id }} · {{ edge.from }} → {{ edge.to }}</label><span v-if="!blockableEdges.length">当前地图没有可用道路。</span></div><div class="toolbar"><button :disabled="!blockableEdges.length" @click="carControl({ blockedEdges: blockedEdgeIds })">应用道路阻塞</button><button @click="blockedEdgeIds = []; carControl({ blockedEdges: [] })">解除所选车全部道路阻塞</button></div></fieldset></details>
          <small>断开通信只改变网络连接；车辆是否继续运动以设备模型反馈为准。</small>
        </section>

        <section class="simulation-panel plc-panel" aria-labelledby="simulation-plc">
          <header><div><p class="section-kicker">PLC与倒料设备</p><h3 id="simulation-plc">虚拟 PLC 与倒料设备</h3></div><span class="scope-label">P 侧设备模型输入</span></header>
          <fieldset :disabled="!canControl"><div class="form-row"><label>就绪许可延迟 / 秒<input v-model.number="readyDelay" type="number" min="0" max="300" /></label><label>倒料完成许可延迟 / 秒<input v-model.number="doneDelay" type="number" min="0" max="300" /></label></div><button :disabled="![readyDelay, doneDelay].every(value => Number.isFinite(value) && value >= 0 && value <= 300)" @click="plcControl({ readyDelaySeconds: readyDelay, doneDelaySeconds: doneDelay })">应用反馈延迟</button>
            <div class="toolbar plc-faults"><button @click="plcControl({ suppressReady: true })">阻止就绪许可</button><button @click="plcControl({ suppressReady: false })">恢复就绪许可</button><button @click="plcControl({ suppressDone: true })">阻止完成许可</button><button @click="plcControl({ suppressDone: false })">恢复完成许可</button><button @click="plcControl({ fault: true })">注入设备故障</button><button @click="plcControl({ fault: false })">撤销设备故障</button></div>
          </fieldset><small v-if="plcFeedback">设备反馈控制编号：{{ plcFeedback.sequence || '尚无反馈' }} · 就绪许可延迟 {{ plcFeedback.plc?.readyDelaySeconds ?? 0 }} 秒 · 完成许可延迟 {{ plcFeedback.plc?.doneDelaySeconds ?? 5 }} 秒<span v-if="plcFeedback.plc?.suppressReady"> · 就绪许可已阻止</span><span v-if="plcFeedback.plc?.suppressDone"> · 完成许可已阻止</span><span v-if="plcFeedback.plc?.fault"> · 设备故障</span></small><p v-if="plcFeedback?.error" class="inline-warning">{{ plcFeedback.error }}</p><small>反馈由虚拟设备按实际到位、许可与动作产生，调度侧输出由正式后台负责。</small>
        </section>
      </div>
    </section>

    <section v-show="activeStage === 'results'" class="stage-surface results-stage">
      <section v-if="status?.recoveryGuidance?.length" class="simulation-panel recovery-guidance" aria-labelledby="recovery-guidance-title">
        <header><div><p class="section-kicker">当前阻塞</p><h3 id="recovery-guidance-title">卡在哪里，怎样恢复</h3></div><span :class="['state-pill', fresh ? 'waiting' : 'unknown']">{{ fresh ? '当前任务证据' : '历史证据，先刷新' }}</span></header>
        <article v-for="(guide, index) in status.recoveryGuidance" :key="guide.taskId + index">
          <b>{{ guide.vehicleId || '尚未派车' }} · {{ guide.stationId || '工位待核对' }} · {{ guide.taskId }}</b>
          <dl><dt>当前步骤</dt><dd>{{ guide.stepNumber ? `第${guide.stepNumber}步 · ` : '' }}{{ industrialText(guide.stepName || '等待派车') }}</dd><dt>等待什么</dt><dd>{{ industrialText(guide.blockedBy || '尚无明确阻塞证据') }}</dd><dt>需要恢复</dt><dd>{{ industrialText(guide.restoreAction || '先核对当前反馈，勿跳过原任务') }}</dd><dt>恢复后确认</dt><dd>{{ industrialText(guide.expectedFeedback || '同一原任务继续推进且占用正常释放') }}</dd></dl>
        </article>
      </section>
      <section v-if="recovery" class="simulation-panel recovery-panel" aria-labelledby="simulation-recovery">
        <header><div><p class="section-kicker">现场恢复</p><h3 id="simulation-recovery">现场式恢复核对</h3></div><span :class="['state-pill', recovery.released ? 'pass' : 'waiting']">{{ recovery.released ? '已放行' : '恢复屏障保持' }}</span></header>
        <p>恢复操作直接走私有 Service 的恢复接口。每次决策必须绑定当前会话、条目修订号和显式人工确认；未全部核对时不能放行，放行后也只能续接原任务编号。</p>
        <p v-if="recovery.error" class="inline-warning">{{ recovery.error }}</p>
        <div class="recovery-items">
          <article v-for="item in recovery.items" :key="item.ticketId">
            <div class="recovery-title"><b>{{ item.taskId === '__SITE__' ? '全场核对' : item.taskId }}</b><span :class="['state-pill', item.verified ? 'pass' : 'waiting']">{{ item.verified ? '已核对' : '待核对' }}</span></div>
            <p>{{ item.stepText }} · {{ actionStateLabel(item.state) }}</p><small>{{ industrialText(item.reason) }}</small>
            <p v-if="item.vehicleSummary">{{ item.vehicleSummary }}</p><p v-if="item.plcSummary">{{ item.plcSummary }}</p>
            <p v-if="item.loadSummary">{{ item.loadSummary }}</p>
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

      <section class="simulation-panel report-panel" aria-labelledby="simulation-report">
        <header><div><p class="section-kicker">检查结果</p><h3 id="simulation-report">自动检查与报告</h3></div><div class="toolbar"><span :class="['state-pill', simulationTone(report?.status)]">{{ simulationLabel(report?.status) }}</span><button :disabled="!report || busy" @click="downloadReport"><IconDownload :size="16" />导出报告 JSON</button></div></header>
        <div v-if="report" class="report-identity"><span v-if="historicalReport" class="state-pill waiting">上一场景报告 · 与当前场景不同</span><b>{{ scenarioNames[report.name ?? ''] ?? report.name ?? '未提供场景名称' }}</b><span>报告编号：{{ report.id || '未提供' }}</span><span>完成时间：{{ stamp(report.finishedAt ?? report.generatedAt) }}</span></div>
        <p v-if="historicalReport && active" class="inline-warning">当前场景仍在运行，以下结果属于上一场景；本次结果将在场景结束后生成。</p>
        <p v-if="report?.recoveryHold" class="inline-warning">恢复屏障检查已结束，原活动任务保留待核对；该报告不表示完整业务循环已完成。</p>
        <p v-if="!report" class="report-empty">尚无报告。运行场景后显示任务、命令、PLC、交通和残留占用检查；未取得证据的项目保留为未核定。</p>
        <template v-else><p v-if="report.message">{{ industrialText(report.message) }}</p><p v-if="firstFailure" class="first-failure"><b>第一失败原因</b>{{ industrialText(firstFailure) }}</p><details v-if="report.versionComparison" class="report-version"><summary>本轮绑定的后台版本与配置</summary><p>{{ industrialText(report.versionComparison.message) }}</p><p>现场：{{ report.versionComparison.field?.informationalVersion || report.versionComparison.field?.buildId || '身份未核定' }} · 仿真：{{ report.versionComparison.simulation?.informationalVersion || report.versionComparison.simulation?.buildId || '身份未核定' }}</p><ul><li v-for="(difference, index) in report.versionComparison.differences ?? []" :key="index">{{ difference.label || '待核定项目' }}：现场 {{ industrialText(difference.field ?? '未取得') }}；仿真 {{ industrialText(difference.simulation ?? '未取得') }}</li></ul></details><dl v-if="metrics.length" class="report-metrics"><div v-for="[key, value] in metrics" :key="key"><dt>{{ simulationMetricLabel(key) }}</dt><dd>{{ simulationMetricValue(value) }}</dd></div></dl><div class="report-table"><table><thead><tr><th>检查项</th><th>结果</th><th>证据 / 说明</th></tr></thead><tbody><tr v-for="(check, index) in report.checks ?? []" :key="index"><td>{{ industrialText(check.name) }}</td><td><span :class="['state-pill', simulationTone(check.status)]">{{ simulationLabel(check.status) }}</span></td><td>{{ industrialText(check.detail || '未提供说明') }}</td></tr></tbody></table></div>
          <div v-if="report.waitingChain?.length" class="waiting-chain"><h4>任务与车辆等待链</h4><ol><li v-for="(wait, index) in report.waitingChain" :key="index"><b>{{ wait.vehicleId || '尚未派车' }} · {{ wait.taskId || '待建单' }}</b><span>{{ wait.stationId }} · {{ wait.site || '位置未反馈' }} · {{ industrialText(wait.stepName || '等待分配') }}</span><p>{{ industrialText(wait.waitReason || wait.blockedBy || '等待原因未反馈') }}</p></li></ol></div>
        </template>
      </section>
      <section class="simulation-panel history-panel" aria-labelledby="simulation-history">
        <header><div><p class="section-kicker">历史证据</p><h3 id="simulation-history">历史对比与问题回放</h3></div><span>只读历史，不操作车辆</span></header>
        <div class="form-row"><label>查看报告<select v-model="selectedHistory" :disabled="historyReading" @change="selectHistory"><option value="">当前实例最近报告</option><option v-for="item in status?.history ?? []" :key="item.id" :value="item.id">{{ stamp(item.finishedAt ?? item.generatedAt) }} · {{ scenarioNames[item.name ?? ''] || '仿真场景' }} · {{ simulationLabel(item.status) }}</option></select></label><label>对比基准<select v-model="comparisonId" @change="compareHistory"><option value="">不对比</option><option v-for="item in status?.history ?? []" :key="item.id" :value="item.id">{{ stamp(item.finishedAt ?? item.generatedAt) }} · {{ scenarioNames[item.name ?? ''] || '仿真场景' }} · {{ simulationLabel(item.status) }}</option></select></label></div>
        <p v-if="!(status?.history?.length)" class="report-empty">还没有历史场景。运行后会保留通过、失败和中止结果。</p>
        <p v-if="comparisonReport" class="inline-warning">数值差异仅作比较；请同时核对当轮地图、后台版本和场景参数，增减值不直接代表变好或变坏。</p>
        <div v-if="metricComparison.length" class="report-table"><table><thead><tr><th>指标</th><th>当前报告</th><th>基准报告</th><th>差值</th></tr></thead><tbody><tr v-for="item in metricComparison" :key="item.key"><td>{{ item.label }}</td><td>{{ simulationMetricValue(item.current) }}</td><td>{{ simulationMetricValue(item.previous) }}</td><td>{{ item.delta > 0 ? '+' : '' }}{{ simulationMetricValue(item.delta) }}</td></tr></tbody></table></div>
        <div class="toolbar"><button :disabled="historyReading || !report?.id" @click="loadReplay">载入本报告时间轴</button><button :disabled="!replay?.timeline.length" @click="replayPlaying = !replayPlaying">{{ replayPlaying ? '暂停回放' : '播放回放' }}</button></div>
        <p v-if="replay?.sampled" class="inline-warning">时间轴已按原始时间顺序抽样，保留最后一帧；完整观察记录仍保存在该轮证据文件中。</p>
        <template v-if="replay?.timeline.length"><label class="replay-slider">观察时间 · {{ stamp(replayFrame?.time) }} · {{ replayIndex + 1 }}/{{ replay.timeline.length }}<input v-model.number="replayIndex" aria-label="回放时间点" type="range" min="0" :max="replay.timeline.length - 1" step="1" @input="replayPlaying = false" /></label>
          <svg v-if="replay.map && replayGeometry.nodes.length" class="simulation-map replay-map" viewBox="0 0 800 430" role="img" aria-label="历史任务位置回放"><g class="roads"><path v-for="edge in replayGeometry.edges" :key="edge.key" :d="edge.path" /></g><g v-for="node in replayGeometry.nodes" :key="node.id" class="map-node"><circle :cx="node.x" :cy="node.y" r="3"/><text :x="node.x + 7" :y="node.y - 7">{{ node.id }}</text></g><g v-for="(car, index) in replayGeometry.cars" :key="car.id" class="map-car" :style="{ '--car-color': ['#41d9bf', '#6fb9ff', '#e8bf64', '#cb9aff'][index % 4] }" :transform="`translate(${car.x} ${car.y})`"><circle r="16"/><text x="0" y="-23" text-anchor="middle">{{ car.id }}</text></g></svg>
          <p v-else class="inline-warning">{{ replay.mapMessage || '该轮没有保存完整地图，只显示任务时间轴，不叠加到当前地图。' }}</p>
          <div class="waiting-chain"><ol><li v-for="car in replayFrame?.vehicles ?? []" :key="car.vehicleId"><b>{{ car.vehicleId }} · {{ car.site || '道路中' }}</b><span>{{ car.taskId || '空闲' }}</span><p>{{ industrialText(car.waitReason || '无阻塞反馈') }}</p></li><li v-for="task in replayFrame?.tasks ?? []" :key="task.taskId"><b>{{ task.taskId }} · {{ task.vehicleId || '等待派车' }}</b><span>{{ task.stationId }} · {{ industrialText(task.step || task.stage || '等待推进') }}</span><p>{{ industrialText(task.waitReason || '无阻塞反馈') }}</p></li></ol></div>
        </template><p v-else-if="replay" class="report-empty">本轮没有可回放的观察采样；不补造位置和任务轨迹。</p>
      </section>
      <details v-if="status?.scenario?.events?.length" class="simulation-panel event-panel"><summary>场景事件 · {{ status.scenario.events.length }} 条</summary><ol><li v-for="(event, index) in [...status.scenario.events].reverse().slice(0, 100)" :key="index"><time>{{ stamp(event.time) }}</time>{{ industrialText(event.message) }}</li></ol></details>
    </section>

    <p class="simulation-boundary">仿真是否就绪，以本页实例、虚拟车辆和设备反馈为准；页面底部全局设备栏对应日常现场后台。本仿真采用统一启停、车辆物理模型、设备时间轴和独立故障注入。合成模型不代表真实固件、机械动作、定位误差与安全联锁已经验收；这些项目仍需现场测试。</p>
  </main>
</template>

<style scoped>
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
</style>
