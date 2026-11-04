<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { IconPlayerPlay, IconPlayerStop, IconRefresh, IconDownload, IconFlask, IconRobot } from '@tabler/icons-vue';
import { simulationApi, simulationFresh, simulationGeometry, simulationLabel, simulationTone, scenarioActive,
  type SimulationStatus, type SimulationRun, type SimulationControls, type SimulationReport,
  type SimulationConfigureRequest, type SimulationMapDefinition, type SimulationCall,
  type SimulationRecoveryItem } from '../simulation';
import { importProfControlRpj } from '../profcontrol-rpj';

const status = ref<SimulationStatus | null>(null), error = ref(''), notice = ref('');
const reading = ref(false), busy = ref(false), connected = ref(false), now = ref(Date.now());
const selectedVehicle = ref('');
const newVehicleCount = ref(2);
const blockedEdgeIds = ref<string[]>([]);
const speedScale = ref(1), readyDelay = ref(0), doneDelay = ref(5);
const run = ref<SimulationRun>({ name: 'normal-cycle', rounds: 1, intervalSeconds: 1, timeoutSeconds: 600 });
const plcFault = ref<'ready' | 'done'>('ready'), plcFaultHold = ref(10);
const restartStage = ref<'pickup' | 'delivery'>('pickup');
const mapDefinition = ref<SimulationMapDefinition | null>(null), mapFileName = ref('');
const stationPickupA = ref(''), stationPickupB = ref(''), waitingSite = ref(''), dumpingSite = ref('');
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
const plcFeedback = computed(() => status.value?.device?.simulationControl);
const appliedBlockedEdges = computed(() => vehicle.value?.simulationControl?.blockedEdges ?? []);
const blockableEdges = computed(() => (status.value?.map?.edges ?? []).filter(edge => !!edge.id));
const geometry = computed(() => simulationGeometry(status.value?.map?.nodes ?? [], status.value?.map?.edges ?? [], cars.value));
const progress = computed(() => Math.min(100, Math.max(0, (status.value?.scenario?.completedRounds ?? 0) / Math.max(1, status.value?.scenario?.totalRounds ?? 1) * 100)));
const report = computed(() => status.value?.report);
const historicalReport = computed(() => !!report.value?.id && !!status.value?.scenario?.id && report.value.id !== status.value.scenario.id);
const scenarioNames: Record<string, string> = { 'normal-cycle': '完整业务循环', 'simultaneous-calls': '多工位同时呼叫',
  'custom-calls': '自定义呼叫计划', 'disconnect-reconnect': '车辆断连与重连', 'plc-delay': 'PLC 反馈延迟',
  'plc-recovery': 'PLC卡位与恢复', 'service-restart': '后台重启与恢复屏障', 'resume-recovery': '恢复后继续原任务' };
const metrics = computed(() => Object.entries(report.value?.metrics ?? {}).filter(([, value]) => value !== null && typeof value !== 'object'));
const runValid = computed(() => Number.isInteger(run.value.rounds) && run.value.rounds >= 1 && run.value.rounds <= 100 &&
  Number.isFinite(run.value.intervalSeconds) && run.value.intervalSeconds >= 0 && run.value.intervalSeconds <= 60 &&
  Number.isFinite(run.value.timeoutSeconds) && run.value.timeoutSeconds >= 30 && run.value.timeoutSeconds <= 3600);
const currentTask = computed(() => vehicle.value?.current_task || '当前无运输命令');
const snapshotTime = computed(() => stamp(status.value?.device?.time));
const mapNodes = computed(() => (mapDefinition.value?.nodes ?? []).filter(node => node.enabled !== false));
const mapResources = computed(() => (mapDefinition.value?.resources ?? []).filter(resource => (resource.boundary?.length ?? 0) >= 3));
const recovery = computed(() => status.value?.recovery);
const protocol = computed(() => status.value?.protocol);
const equipment = computed(() => status.value?.equipment);
const callPlanValid = computed(() => Number.isInteger(callCount.value) && callCount.value >= 1 && callCount.value <= 500 &&
  Number.isFinite(callInterval.value) && callInterval.value >= 0 && callInterval.value <= 60 && [1, 2].includes(callBatchSize.value) &&
  callPattern.value.split(',').map(value => value.trim()).filter(value => value === 'CNC-001' || value === 'CNC-002').length > 0);
function voltageCurveValid(curve: VehicleDraft['voltageCurve']): boolean {
  if (!Array.isArray(curve) || curve.length < 2 || curve.length > 20) return false;
  if (!curve.every(point => Number.isFinite(point.percent) && point.percent >= 0 && point.percent <= 100 &&
    Number.isFinite(point.voltage) && point.voltage >= 0.1 && point.voltage <= 1000)) return false;
  return new Set(curve.map(point => point.percent)).size === curve.length;
}
const configurationValid = computed(() => !!mapDefinition.value && !!stationPickupA.value && !!waitingSite.value && !!dumpingSite.value &&
  !!dumperResourceId.value && !!inspectionSite.value && customVehicles.value.length >= 1 && customVehicles.value.length <= 8 &&
  (!stationPickupB.value || stationPickupB.value !== stationPickupA.value) && waitingSite.value !== dumpingSite.value &&
  customVehicles.value.every(vehicle => /^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$/.test(vehicle.id) && !!vehicle.initialSite &&
    Number.isFinite(vehicle.radiusMeters) && vehicle.radiusMeters >= 0.1 && vehicle.radiusMeters <= 5 &&
    Number.isFinite(vehicle.batteryPercent) && vehicle.batteryPercent! >= 0 && vehicle.batteryPercent! <= 100 &&
    Number.isFinite(vehicle.speedScale) && vehicle.speedScale! >= 0.1 && vehicle.speedScale! <= 10 &&
    Number.isFinite(vehicle.accelerationMetersPerSecondSquared) && vehicle.accelerationMetersPerSecondSquared! >= 0.05 &&
    vehicle.accelerationMetersPerSecondSquared! <= 10 && Number.isFinite(vehicle.operationSeconds) &&
    vehicle.operationSeconds! >= 0.1 && vehicle.operationSeconds! <= 120 &&
    Number.isFinite(vehicle.travelCostPercentPerMeter) && vehicle.travelCostPercentPerMeter! >= 0 &&
    vehicle.travelCostPercentPerMeter! <= 10 && Number.isFinite(vehicle.idleCostPercentPerHour) &&
    vehicle.idleCostPercentPerHour! >= 0 && vehicle.idleCostPercentPerHour! <= 100 &&
    voltageCurveValid(vehicle.voltageCurve) && Number.isFinite(vehicle.chargingGainPercentPerHour) &&
    vehicle.chargingGainPercentPerHour! >= 0 && vehicle.chargingGainPercentPerHour! <= 100) &&
  new Set(customVehicles.value.map(vehicle => vehicle.id)).size === customVehicles.value.length &&
  new Set(customVehicles.value.map(vehicle => vehicle.initialSite)).size === customVehicles.value.length);
function stamp(value?: string | number): string {
  if (value === undefined) return '尚无记录';
  const date = new Date(typeof value === 'number' ? value * 1000 : value);
  return Number.isFinite(date.getTime()) ? date.toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false }) : '尚无记录';
}
function defaultSite(names: string[], fallbackIndex: number): string {
  return mapNodes.value.find(node => names.some(name => node.id.includes(name)))?.id ?? mapNodes.value[fallbackIndex]?.id ?? '';
}
function resetVehicleOrigins() {
  const excluded = new Set([stationPickupA.value, stationPickupB.value, waitingSite.value, dumpingSite.value, inspectionSite.value].filter(Boolean));
  const stationOrigins = [stationPickupA.value, stationPickupB.value, inspectionSite.value,
    ...mapNodes.value.filter(node => node.type === 'Station' && !excluded.has(node.id)).map(node => node.id)];
  const origins = [...new Set(stationOrigins.filter(Boolean).concat(mapNodes.value.map(node => node.id).filter(id => !excluded.has(id))))];
  customVehicles.value.forEach((vehicle, index) => { vehicle.initialSite = origins[index] ?? mapNodes.value[index]?.id ?? ''; });
}
async function loadMapFile(event: Event) {
  error.value = ''; notice.value = '';
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
    stationPickupA.value = defaultSite(['取货A', 'LM101', 'CNC-001'], 0);
    stationPickupB.value = defaultSite(['取货B', 'LM102', 'CNC-002'], 1);
    dumpingSite.value = defaultSite(['倒料', 'LM108'], 2);
    waitingSite.value = defaultSite(['等待', 'LM107'], 3);
    inspectionSite.value = defaultSite(['检修', '停车'], 4);
    dumperResourceId.value = mapResources.value.find(resource => resource.id.includes('倒料'))?.id ?? mapResources.value[0]?.id ?? '';
    resetVehicleOrigins();
    notice.value = imported
      ? `已转换 ProfControl ${file.name}：${value.nodes.length}个节点、${value.edges.length}条道路、${value.resources.length}个资源、${imported.sourceVehicles.length}辆车。${imported.warnings.join('；')}`
      : `已载入地图 ${file.name}：${value.nodes.length}个节点、${value.edges.length}条道路、${value.resources.length}个资源。请核对语义节点后再新建实例。`;
  } catch (reason) { mapDefinition.value = null; error.value = reason instanceof Error ? reason.message : String(reason); }
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
async function configureSimulation() {
  if (!configurationValid.value || !mapDefinition.value || status.value?.running || active.value) return;
  const stations: SimulationConfigureRequest['stations'] = [{ stationId: 'CNC-001', pickupSite: stationPickupA.value }];
  if (stationPickupB.value) stations.push({ stationId: 'CNC-002' as const, pickupSite: stationPickupB.value });
  const payload: SimulationConfigureRequest = {
    name: mapDefinition.value.name || mapFileName.value.replace(/\.json$/i, '') || 'custom-simulation',
    mapDefinition: mapDefinition.value, stations,
    vehicles: customVehicles.value.map(vehicle => ({ ...vehicle,
      voltageCurve: [...vehicle.voltageCurve].sort((left, right) => left.percent - right.percent) })),
    dumper: { dumpingSite: dumpingSite.value, waitingSite: waitingSite.value, resourceId: dumperResourceId.value },
    inspectionSite: inspectionSite.value
  };
  await action('configure', payload);
}
function buildCustomCalls(): SimulationCall[] {
  const pattern = callPattern.value.split(',').map(value => value.trim()).filter((value): value is 'CNC-001' | 'CNC-002' => value === 'CNC-001' || value === 'CNC-002');
  return Array.from({ length: callCount.value }, (_, index) => ({
    stationId: pattern[index % pattern.length], atSeconds: Math.floor(index / callBatchSize.value) * callInterval.value,
    vehicleId: callVehicleId.value || null
  }));
}
async function startScenario() {
  if (!runValid.value) return;
  const payload: SimulationRun = { ...run.value };
  if (payload.name === 'custom-calls') {
    if (!callPlanValid.value) { error.value = '核对呼叫数量、间隔、批量和工位序列。'; return; }
    payload.calls = buildCustomCalls();
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
  busy.value = true; error.value = ''; notice.value = '';
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
  busy.value = true; error.value = ''; notice.value = '';
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
  'protocol-start' | 'protocol-stop' | 'equipment-start' | 'equipment-control' | 'equipment-stop', payload: unknown = {}) {
  if (!available.value || (name === 'run' && (!fresh.value || !runValid.value || active.value)) || (name === 'control' && !fresh.value)) return;
  if (name === 'new-instance' && (status.value?.running || starting.value || active.value)) return;
  if (name === 'new-instance' && (!Number.isInteger(newVehicleCount.value) || newVehicleCount.value < 1 || newVehicleCount.value > 8)) return;
  busy.value = true; error.value = ''; notice.value = '';
  const request = new AbortController(); controller = request;
  const timeout = setTimeout(() => request.abort(), 60000);
  try {
    const next = await simulationApi<SimulationStatus>('/' + name, payload, request.signal);
    if (!disposed) { accept(next); notice.value = name === 'control' ? '控制请求已提交，请核对设备状态与事件中的实际反馈。' : next.message ?? '请求已提交，正在读取仿真运行状态。'; }
  } catch (reason) { if (!disposed) { connected.value = false; error.value = (reason instanceof Error ? reason.message : String(reason)) + '；请刷新原记录后核对执行结果。'; } }
  finally { clearTimeout(timeout); busy.value = false; if (controller === request) controller = undefined; }
}
function carControl(values: NonNullable<SimulationControls['vehicles']>[string]) {
  if (!selectedVehicle.value || !canControl.value) return;
  void action('control', { sequence: crypto.randomUUID(), vehicles: { [selectedVehicle.value]: values } } satisfies SimulationControls);
}
function plcControl(values: SimulationControls['plc']) {
  if (canControl.value) void action('control', { sequence: crypto.randomUUID(), plc: values } satisfies SimulationControls);
}
watch(selectedVehicle, () => { blockedEdgeIds.value = [...appliedBlockedEdges.value]; });
async function downloadReport() {
  if (busy.value || !report.value) return;
  try {
    const data = await simulationApi<SimulationReport>('/report');
    const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }));
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'simulation-report-' + (data.id?.replace(/[^a-zA-Z0-9_-]/g, '') || 'latest') + '.json'; anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  } catch (reason) { error.value = reason instanceof Error ? reason.message : String(reason); }
}
onMounted(() => { void refresh(); poll = setInterval(() => void refresh(), 1500); tick = setInterval(() => { now.value = Date.now(); }, 500); });
onUnmounted(() => { disposed = true; controller?.abort(); clearInterval(poll); clearInterval(tick); });
</script>

<template>
  <main class="simulation-page">
    <header class="simulation-title">
      <div><h2><IconFlask :size="24" />仿真测试中心</h2><p>虚拟车辆与 PLC 连接正式调度流程，运行场景、注入故障并保存检查结果。</p></div>
      <div class="toolbar"><span class="scope-label">独立仿真环境</span><button :disabled="busy || reading" @click="refresh()"><IconRefresh :size="16" />刷新原记录</button></div>
    </header>
    <p v-if="error" class="simulation-error" role="alert">{{ error }}</p>
    <p v-if="notice" class="simulation-notice" role="status">{{ notice }}</p>
    <section class="environment-panel" aria-labelledby="simulation-environment">
      <div><h3 id="simulation-environment">{{ status?.rootName || '尚未选择仿真实例' }}</h3><p>{{ status?.error || status?.message || '正在读取仿真环境配置…' }}</p></div>
      <div class="environment-state"><span :class="['state-pill', fresh ? 'pass' : 'unknown']">{{ !connected ? '未取得当前状态' : !status?.enabled ? '未启用' : !status?.running ? '已停止' : fresh ? '运行中 · 设备反馈有效' : '运行中 · 设备反馈过期' }}</span><small>最近设备采样：{{ snapshotTime }}</small></div>
      <div class="toolbar"><button class="primary" :disabled="!available || !!status?.running || starting" @click="action('start')"><IconPlayerPlay :size="16" />{{ starting ? '正在准备环境…' : '启动仿真环境' }}</button><button :disabled="!available || (!status?.running && !starting)" @click="action('stop')"><IconPlayerStop :size="16" />停止仿真环境</button><label class="new-vehicle-count">快速新实例车辆数<input v-model.number="newVehicleCount" aria-label="新实例车辆数" type="number" min="1" max="8" step="1" :disabled="!available || !!status?.running || starting || active" @change="vehicleCountChanged" /></label><button :disabled="!available || !!status?.running || starting || active || !Number.isInteger(newVehicleCount) || newVehicleCount < 1 || newVehicleCount > 8" @click="action('new-instance', { vehicles: newVehicleCount })">沿用当前地图新建</button></div>
    </section>
    <section class="simulation-panel instance-designer" aria-labelledby="simulation-instance-designer">
      <header><h3 id="simulation-instance-designer">自定义仿真环境</h3><span class="scope-label">停止环境后创建新的隔离实例</span></header>
      <p class="designer-intro">导入本项目 <code>.agvmap.json</code>，或只读转换 ProfControl <code>.rpj</code> 中的站点、路径、曲线、控制区和车辆元数据，再指定两个呼叫工位取货点、等待点、倒料点和资源边界。地图只进入新实例，不改现场地图。</p>
      <div class="map-import">
        <label class="file-picker">选择地图文件<input type="file" accept=".json,.agvmap.json,.rpj,application/json" :disabled="!!status?.running || starting || active" @change="loadMapFile" /></label>
        <span v-if="mapFileName">已载入：{{ mapFileName }} · {{ mapDefinition?.nodes.length }} 节点 / {{ mapDefinition?.edges.length }} 道路 / {{ mapDefinition?.resources.length }} 资源</span>
        <span v-else>尚未载入自定义地图</span>
      </div>
      <template v-if="mapDefinition">
        <fieldset :disabled="!!status?.running || starting || active" class="topology-grid">
          <label>呼叫工位 1 取货点<select v-model="stationPickupA" @change="resetVehicleOrigins"><option value="">不启用</option><option v-for="node in mapNodes" :key="node.id" :value="node.id">{{ node.id }} · {{ node.agvSiteId }}</option></select></label>
          <label>呼叫工位 2 取货点<select v-model="stationPickupB" @change="resetVehicleOrigins"><option value="">不启用第二工位</option><option v-for="node in mapNodes" :key="node.id" :value="node.id">{{ node.id }} · {{ node.agvSiteId }}</option></select></label>
          <label>等待位节点<select v-model="waitingSite" @change="resetVehicleOrigins"><option v-for="node in mapNodes" :key="node.id" :value="node.id">{{ node.id }}</option></select></label>
          <label>倒料位节点<select v-model="dumpingSite" @change="resetVehicleOrigins"><option v-for="node in mapNodes" :key="node.id" :value="node.id">{{ node.id }}</option></select></label>
          <label>检修 / 设备观察点<select v-model="inspectionSite" @change="resetVehicleOrigins"><option v-for="node in mapNodes" :key="node.id" :value="node.id">{{ node.id }}</option></select></label>
          <label>倒料资源边界<select v-model="dumperResourceId"><option v-for="resource in mapResources" :key="resource.id" :value="resource.id">{{ resource.id }} · 容量 {{ resource.capacity ?? 1 }}</option></select></label>
        </fieldset>
        <div class="vehicle-designer">
          <div class="designer-subhead"><b>虚拟车辆 · {{ customVehicles.length }} 辆</b><small>ID须唯一；起点是地图启用节点；半径用于合成碰撞检查。</small></div>
          <div v-for="(vehicle, index) in customVehicles" :key="index" class="vehicle-config-row">
            <label>车辆 ID<input v-model.trim="vehicle.id" maxlength="32" /></label>
            <label>初始节点<select v-model="vehicle.initialSite"><option v-for="node in mapNodes" :key="node.id" :value="node.id">{{ node.id }}</option></select></label>
            <label>包络半径 / m<input v-model.number="vehicle.radiusMeters" type="number" min="0.1" max="5" step="0.05" /></label>
            <label>电量 / %<input v-model.number="vehicle.batteryPercent" type="number" min="0" max="100" step="1" /></label>
            <label>速度倍率<input v-model.number="vehicle.speedScale" type="number" min="0.1" max="10" step="0.1" /></label>
            <label class="loaded-config"><input v-model="vehicle.initialLoaded" type="checkbox" />初始载箱</label>
            <button :disabled="customVehicles.length <= 1" @click="customVehicles.splice(index, 1); resetVehicleOrigins()">删除</button>
            <details class="vehicle-advanced"><summary>物理参数 · 加速度 {{ vehicle.accelerationMetersPerSecondSquared ?? 0.6 }} m/s² · 动作 {{ vehicle.operationSeconds ?? 2 }} 秒</summary>
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
        <p v-if="!configurationValid" class="inline-warning">请核对地图、工位取货点、等待/倒料/检修节点、倒料资源，以及每辆车唯一ID和起点。</p>
        <div class="toolbar designer-actions"><button class="primary" :disabled="!available || !!status?.running || starting || active || !configurationValid" @click="configureSimulation">创建自定义隔离实例</button><span>创建后不会自动启动；核对实例名和拓扑后再启动。</span></div>
      </template>
    </section>
    <section class="simulation-panel protocol-designer" aria-labelledby="simulation-protocol">
      <header><h3 id="simulation-protocol">通用协议虚拟服务器</h3><span :class="['state-pill', protocol?.running ? 'pass' : 'unknown']">{{ protocol?.running ? '监听中' : '未运行' }}</span></header>
      <p>用声明式JSON模拟 TCP、UDP 或 HTTP 设备：按请求字段、文本或正则匹配，维护状态变量，返回模板响应，并可设置延迟、丢响应和断连。仅监听回环地址，不执行任意程序代码。</p>
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
          <small>该服务器用于设备协议和脚本测试；通用HTTP请求不会自动冒充当前正式AGV连接器。</small>
        </aside>
      </div>
    </section>
    <section class="simulation-panel equipment-designer" aria-labelledby="simulation-equipment-model">
      <header><h3 id="simulation-equipment-model">电梯 / 门 / 信号发生器</h3><span :class="['state-pill', equipment?.running ? 'pass' : 'unknown']">{{ equipment?.running ? '模型运行中' : '未运行' }}</span></header>
      <p>这是可选的通用设备模型，用于演练楼层运输、开关门和外部信号时间轴。当前尚未把电梯状态接入正式派车或安全联锁。</p>
      <div class="equipment-grid">
        <div>
          <div class="toolbar"><label>楼层运行 / 秒<input v-model.number="equipmentTravelSeconds" type="number" min="0.1" max="300" step="0.1" /></label><label>开关门 / 秒<input v-model.number="equipmentDoorSeconds" type="number" min="0.1" max="60" step="0.1" /></label><button class="primary" :disabled="busy || equipment?.running" @click="startEquipment">启动设备模型</button><button :disabled="busy || !equipment?.running" @click="action('equipment-stop')">停止设备模型</button></div>
          <dl class="equipment-facts"><dt>当前 / 目标楼层</dt><dd>{{ equipment?.currentFloor || '—' }} → {{ equipment?.targetFloor || '—' }}</dd><dt>运行状态</dt><dd>{{ equipment?.fault ? '故障' : equipment?.moving ? `${equipment?.direction} · ${Math.round((equipment?.progress ?? 0) * 100)}%` : equipment?.direction || '停止' }}</dd><dt>门状态</dt><dd>{{ equipment?.door || '—' }}</dd></dl>
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
    <section v-if="connected && !status?.enabled" class="configuration-guide">
      <h3>配置一次，之后在此页面操作</h3><p>请先准备独立仿真实例，并为当前 Web 入口配置仿真控制器、Python 路径和实例目录。启用后即可在本页启动、运行场景和导出报告。</p>
      <p>实例使用独立数据库、通信端口与虚拟设备。配置状态及实际连接条件由后台核对。</p>
    </section>
    <div class="simulation-grid">
      <section class="simulation-panel map-panel" aria-labelledby="simulation-map">
        <header><h3 id="simulation-map">地图与车辆</h3><span :class="['state-pill', fresh ? 'pass' : 'unknown']">{{ fresh ? '设备实际反馈' : '历史位置 / 尚无反馈' }}</span></header>
        <svg v-if="geometry.nodes.length" class="simulation-map" viewBox="0 0 800 430" role="img" aria-label="仿真地图及虚拟车辆位置">
          <g class="roads"><path v-for="edge in geometry.edges" :key="edge.key" :d="edge.path" :class="{ blocked: !!edge.id && appliedBlockedEdges.includes(edge.id) }" /></g>
          <g v-for="node in geometry.nodes" :key="node.id" class="map-node"><circle :cx="node.x" :cy="node.y" r="4"/><text :x="node.x + 8" :y="node.y - 8">{{ node.id }}</text></g>
          <g v-for="(car, index) in geometry.cars" :key="car.id" class="map-car" :class="{ stale: !fresh, offline: car.offline, selected: selectedVehicle === car.id }" :style="{ '--car-color': ['#41d9bf', '#6fb9ff', '#e8bf64', '#cb9aff'][index % 4] }" :transform="`translate(${car.x} ${car.y})`">
            <circle r="17"/><rect x="-10" y="-7" width="20" height="14" rx="3" :transform="`rotate(${-(car.angle ?? 0) * 180 / Math.PI})`"/><text x="0" y="-26" text-anchor="middle">{{ car.id }}</text><title>{{ car.id }} · {{ car.station }} · {{ car.loaded ? '载箱' : '空载' }}</title>
          </g>
        </svg>
        <div v-else class="map-empty">启动已配置的仿真实例后显示地图和车辆位置。</div>
        <div class="map-caption"><span>车辆位置来自设备模型，坐标按地图比例显示。</span><span>{{ vehicleIds.length }} 辆虚拟车</span></div>
      </section>
      <section class="simulation-panel scenario-panel" aria-labelledby="simulation-scenario">
        <header><h3 id="simulation-scenario">场景编排</h3><span :class="['state-pill', simulationTone(status?.scenario?.status)]">{{ simulationLabel(status?.scenario?.status) }}</span></header>
        <fieldset :disabled="busy || active"><label>测试场景<select v-model="run.name" aria-label="测试场景"><option value="normal-cycle">完整业务循环</option><option value="simultaneous-calls">多工位同时呼叫</option><option value="custom-calls">自定义呼叫计划</option><option value="disconnect-reconnect">车辆断连与重连</option><option value="plc-delay">PLC 反馈延迟</option><option value="plc-recovery">PLC卡位与恢复</option><option value="service-restart">后台重启与恢复屏障</option><option value="resume-recovery">恢复后继续原任务</option></select></label>
          <div class="form-row"><label>循环轮数<input v-model.number="run.rounds" type="number" min="1" max="100" step="1" /></label><label>轮间隔 / 秒<input v-model.number="run.intervalSeconds" type="number" min="0" max="60" /></label></div>
          <label>单轮超时 / 秒<input v-model.number="run.timeoutSeconds" type="number" min="30" max="3600" /></label>
          <div v-if="run.name === 'custom-calls'" class="call-plan">
            <div class="form-row"><label>呼叫总数<input v-model.number="callCount" type="number" min="1" max="500" step="1" /></label><label>批次间隔 / 秒<input v-model.number="callInterval" type="number" min="0" max="60" step="1" /></label></div>
            <div class="form-row"><label>每批同时呼叫<select v-model.number="callBatchSize"><option :value="1">1个工位</option><option :value="2">2个工位同时</option></select></label><label>指定车辆<select v-model="callVehicleId"><option value="">自动选择可达车辆</option><option v-for="id in vehicleIds" :key="id" :value="id">{{ id }}</option></select></label></div>
            <label>工位轮转顺序<input v-model.trim="callPattern" placeholder="CNC-001,CNC-002" /></label>
            <small>同一批次2个工位会在同一设备扫描中置位；正式后台按车辆可达、空闲、并发上限和路权自动编排。指定车辆不可达时停止并报告。</small>
          </div>
          <div v-if="run.name === 'plc-recovery'" class="form-row"><label>卡位信号<select v-model="plcFault"><option value="ready">倒料机 Ready</option><option value="done">倒料完成 Done</option></select></label><label>卡位保持 / 秒<input v-model.number="plcFaultHold" type="number" min="1" max="120" /></label></div>
          <label v-if="run.name === 'service-restart'">重启注入阶段<select v-model="restartStage"><option value="pickup">原取料运输执行中（可验证到站补记后续接）</option><option value="delivery">满箱运输执行中（保留更复杂恢复现场）</option></select></label>
        </fieldset>
        <div class="toolbar"><button class="primary" :disabled="!canControl || active || !runValid || (run.name === 'custom-calls' && !callPlanValid)" @click="startScenario"><IconPlayerPlay :size="16" />运行场景</button><button :disabled="!available || !active" @click="action('abort')">中止后续场景</button></div>
        <p v-if="run.name === 'service-restart'" class="inline-warning">该场景验证活动任务重启后保留恢复屏障，结束时保留原任务等待核对。</p>
        <p v-if="run.name === 'resume-recovery'" class="inline-warning">仅在恢复条目全部核对并由下方按钮放行后运行；它只续接原任务，不新建任务。</p>
        <p v-if="!runValid" class="inline-warning">轮数 1–100、间隔 0–60 秒、超时 30–3600 秒。</p>
        <div class="scenario-progress"><div><b>{{ status?.scenario?.completedRounds ?? 0 }} / {{ status?.scenario?.totalRounds ?? run.rounds }} 轮</b><span>{{ Math.round(progress) }}%</span></div><progress :value="progress" max="100" aria-label="场景完成进度"/><p>{{ status?.scenario?.message || '选择场景并启动。检查结果保存在原场景记录中。' }}</p></div>
        <small>中止停止提交后续呼叫，已下发任务及故障现场保留供核对。停止环境后可沿用当前地图或导入自定义地图创建新实例，旧记录保留。新实例支持1–8辆虚拟车，但每辆车必须使用不同Station起点；现有PLC模型提供两个呼叫工位。</small>
      </section>
      <section class="simulation-panel vehicle-panel" aria-labelledby="simulation-vehicle">
        <header><h3 id="simulation-vehicle"><IconRobot :size="18" />虚拟车辆与故障</h3><span v-if="!fresh" class="inline-warning">反馈无效，控制已禁用</span></header>
        <label class="vehicle-select">选择车辆<select v-model="selectedVehicle" aria-label="选择虚拟车辆"><option v-if="!vehicleIds.length" value="">尚无车辆</option><option v-for="id in vehicleIds" :key="id" :value="id">{{ id }}</option></select></label>
        <dl class="vehicle-facts"><dt>状态</dt><dd>{{ !fresh ? '未知 / 历史快照' : vehicle?.offline ? '通信断开' : vehicle?.paused ? '运动暂停' : vehicle?.current_task ? '执行中' : '空闲' }}</dd><dt>位置</dt><dd>{{ vehicle?.station || '尚无反馈' }}</dd><dt>载箱</dt><dd>{{ !fresh ? '未知' : vehicle?.loaded ? '有箱' : '无箱' }}</dd><dt>电量 / 电压</dt><dd>{{ vehicle?.batteryPercent !== undefined ? `${vehicle.batteryPercent.toFixed(1)}%` : '未知' }} · {{ vehicle?.batteryVoltage !== undefined ? `${vehicle.batteryVoltage.toFixed(2)} V` : '未知' }}{{ vehicle?.charging ? ' · 充电中' : '' }}</dd><dt>速度 / 里程</dt><dd>{{ vehicle?.speedMps !== undefined ? `${vehicle.speedMps.toFixed(2)} m/s` : '未知' }} · {{ vehicle?.distanceMeters !== undefined ? `${vehicle.distanceMeters.toFixed(2)} m` : '未知' }}</dd><dt>原运输编号</dt><dd>{{ currentTask }}</dd><dt>当前等待原因</dt><dd>{{ vehicle?.waitReason || status?.scenario?.message || '尚无等待原因反馈' }}</dd></dl>
        <fieldset :disabled="!canControl || !selectedVehicle"><div class="toolbar"><button @click="carControl({ paused: true })">暂停运动</button><button @click="carControl({ paused: false })">恢复运动</button><button @click="carControl({ offline: true })">断开通信</button><button @click="carControl({ offline: false })">恢复通信</button><button @click="carControl({ charging: true })">开始充电</button><button @click="carControl({ charging: false })">停止充电</button><button @click="carControl({ dropNextResponse: true })">丢弃下一响应</button></div><div class="speed-control"><label>运动倍率<input v-model.number="speedScale" type="number" min="0.1" max="10" step="0.1" /></label><button :disabled="!Number.isFinite(speedScale) || speedScale < 0.1 || speedScale > 10" @click="carControl({ speedScale })">应用倍率</button></div></fieldset>
        <small v-if="vehicle?.simulationControl">设备反馈控制编号：{{ vehicle.simulationControl.sequence || '尚无反馈' }} · 当前运动倍率 {{ vehicle.speedScale ?? '未知' }}<span v-if="vehicle.simulationControl.responseDropPending"> · 等待丢弃下一响应</span></small>
        <p v-if="vehicle?.simulationControl?.error" class="inline-warning">{{ vehicle.simulationControl.error }}</p>
        <details class="edge-controls"><summary>道路阻塞 · 已反馈 {{ appliedBlockedEdges.length }} 条</summary><fieldset :disabled="!canControl || !selectedVehicle"><p>选择道路，在所选虚拟车经过时阻止运动，观察调度等待与超时处理。</p><div class="edge-options"><label v-for="edge in blockableEdges" :key="edge.id"><input v-model="blockedEdgeIds" type="checkbox" :value="edge.id" />{{ edge.id }} · {{ edge.from }} → {{ edge.to }}</label><span v-if="!blockableEdges.length">当前地图没有可用道路。</span></div><div class="toolbar"><button :disabled="!blockableEdges.length" @click="carControl({ blockedEdges: blockedEdgeIds })">应用道路阻塞</button><button @click="blockedEdgeIds = []; carControl({ blockedEdges: [] })">解除所选车全部道路阻塞</button></div></fieldset></details>
        <small>断开通信仅改变网络连接；车辆是否继续运动以设备模型反馈为准。</small>
      </section>
      <section class="simulation-panel plc-panel" aria-labelledby="simulation-plc">
        <header><h3 id="simulation-plc">虚拟 PLC 与倒料设备</h3><span class="scope-label">P 侧设备模型输入</span></header>
        <fieldset :disabled="!canControl"><div class="form-row"><label>Ready 延迟 / 秒<input v-model.number="readyDelay" type="number" min="0" max="300" /></label><label>Done 延迟 / 秒<input v-model.number="doneDelay" type="number" min="0" max="300" /></label></div><button :disabled="![readyDelay, doneDelay].every(value => Number.isFinite(value) && value >= 0 && value <= 300)" @click="plcControl({ readyDelaySeconds: readyDelay, doneDelaySeconds: doneDelay })">应用反馈延迟</button>
          <div class="toolbar plc-faults"><button @click="plcControl({ suppressReady: true })">阻止 Ready</button><button @click="plcControl({ suppressReady: false })">恢复 Ready</button><button @click="plcControl({ suppressDone: true })">阻止 Done</button><button @click="plcControl({ suppressDone: false })">恢复 Done</button><button @click="plcControl({ fault: true })">注入设备故障</button><button @click="plcControl({ fault: false })">撤销设备故障</button></div>
        </fieldset><small v-if="plcFeedback">设备反馈控制编号：{{ plcFeedback.sequence || '尚无反馈' }} · Ready 延迟 {{ plcFeedback.plc?.readyDelaySeconds ?? 0 }} 秒 · Done 延迟 {{ plcFeedback.plc?.doneDelaySeconds ?? 5 }} 秒<span v-if="plcFeedback.plc?.suppressReady"> · Ready 已阻止</span><span v-if="plcFeedback.plc?.suppressDone"> · Done 已阻止</span><span v-if="plcFeedback.plc?.fault"> · 设备故障</span></small><p v-if="plcFeedback?.error" class="inline-warning">{{ plcFeedback.error }}</p><small>反馈由虚拟设备按实际到位、许可与动作产生，调度侧输出由正式后台负责。</small>
      </section>
    </div>
    <section v-if="recovery" class="simulation-panel recovery-panel" aria-labelledby="simulation-recovery">
      <header><h3 id="simulation-recovery">现场式恢复核对</h3><span :class="['state-pill', recovery.released ? 'pass' : 'waiting']">{{ recovery.released ? '已放行' : '恢复屏障保持' }}</span></header>
      <p>恢复操作直接走私有 Service 的恢复接口。每次决策必须绑定当前会话、条目修订号和显式人工确认；未全部核对时不能放行，放行后也只能续接原任务编号。</p>
      <p v-if="recovery.error" class="inline-warning">{{ recovery.error }}</p>
      <div class="recovery-items">
        <article v-for="item in recovery.items" :key="item.ticketId">
          <div class="recovery-title"><b>{{ item.taskId === '__SITE__' ? '全场核对' : item.taskId }}</b><span :class="['state-pill', item.verified ? 'pass' : 'waiting']">{{ item.verified ? '已核对' : '待核对' }}</span></div>
          <p>{{ item.stepText }} · {{ item.state }}</p><small>{{ item.reason }}</small>
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
      <header><h3 id="simulation-report">自动检查与报告</h3><div class="toolbar"><span :class="['state-pill', simulationTone(report?.status)]">{{ simulationLabel(report?.status) }}</span><button :disabled="!report || busy" @click="downloadReport"><IconDownload :size="16" />导出报告 JSON</button></div></header>
      <div v-if="report" class="report-identity"><span v-if="historicalReport" class="state-pill waiting">上一场景报告 · 与当前场景不同</span><b>{{ scenarioNames[report.name ?? ''] ?? report.name ?? '未提供场景名称' }}</b><span>报告编号：{{ report.id || '未提供' }}</span><span>完成时间：{{ stamp(report.finishedAt ?? report.generatedAt) }}</span></div>
      <p v-if="historicalReport && active" class="inline-warning">当前场景仍在运行，以下结果属于上一场景；本次结果将在场景结束后生成。</p>
      <p v-if="report?.recoveryHold" class="inline-warning">恢复屏障检查已结束，原活动任务保留待核对；该报告不表示完整业务循环已完成。</p>
      <p v-if="!report" class="report-empty">尚无报告。运行场景后显示任务、命令、PLC、交通和残留占用检查；未取得证据的项目保留为未核定。</p>
      <template v-else><p v-if="report.message">{{ report.message }}</p><dl v-if="metrics.length" class="report-metrics"><div v-for="[key, value] in metrics" :key="key"><dt>{{ key }}</dt><dd>{{ value }}</dd></div></dl><div class="report-table"><table><thead><tr><th>检查项</th><th>结果</th><th>证据 / 说明</th></tr></thead><tbody><tr v-for="(check, index) in report.checks ?? []" :key="index"><td>{{ check.name }}</td><td><span :class="['state-pill', simulationTone(check.status)]">{{ simulationLabel(check.status) }}</span></td><td>{{ check.detail || '未提供说明' }}</td></tr></tbody></table></div></template>
    </section>
    <details v-if="status?.scenario?.events?.length" class="simulation-panel event-panel"><summary>场景事件 · {{ status.scenario.events.length }} 条</summary><ol><li v-for="(event, index) in [...status.scenario.events].reverse().slice(0, 100)" :key="index"><time>{{ stamp(event.time) }}</time>{{ event.message }}</li></ol></details>
    <p class="simulation-boundary">本页验证虚拟模型中的正式调度行为。真实固件、机械动作、定位误差与安全联锁仍需现场验证。</p>
  </main>
</template>

<style scoped>
.simulation-page{flex:1;min-height:0;overflow:auto;padding:8px 12px 30px;color:#d3e2f3}.simulation-title{display:flex;justify-content:space-between;align-items:center;gap:16px;padding:8px 0 18px}.simulation-title h2{display:flex;align-items:center;gap:9px;font-size:23px}.simulation-title p{margin-top:9px;color:#94abc8;line-height:1.6}.toolbar{display:flex;align-items:center;flex-wrap:wrap;gap:8px}.new-vehicle-count{display:flex;align-items:center;gap:7px;color:#9cb1cb;font-size:11px}.new-vehicle-count input{width:60px}.scope-label{font-size:11px;color:#7ad4e7;border:1px solid #2b6676;background:#102e3e;padding:4px 8px;border-radius:4px;white-space:nowrap}.environment-panel,.simulation-panel,.configuration-guide{border:1px solid #2b4162;border-radius:6px;background:#101b32;padding:18px}.environment-panel{display:flex;align-items:center;flex-wrap:wrap;gap:20px;margin-bottom:14px}.environment-panel>div:first-child{flex:1;min-width:240px}.environment-panel h3{font-size:16px}.environment-panel p{color:#9cb1cb;line-height:1.6;margin-top:7px;overflow-wrap:anywhere}.environment-state{display:flex;flex-direction:column;gap:8px}.environment-state small{color:#89a0be}.simulation-grid{display:grid;grid-template-columns:minmax(0,1.4fr) minmax(320px,1fr);gap:14px}.simulation-panel{min-width:0}.simulation-panel>header{display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:16px}.simulation-panel h3{display:flex;align-items:center;gap:8px;font-size:15px}.simulation-panel small{display:block;color:#91a8c3;line-height:1.65;margin-top:12px}.simulation-map{width:100%;height:340px;background:#0c1529;border:1px solid #223654;border-radius:4px}.roads path{fill:none;stroke:#3b5475;stroke-width:4;stroke-linecap:round}.roads path.blocked{stroke:#ef947b;stroke-dasharray:8 6}.edge-controls{border-top:1px solid #2b4162;padding-top:12px;margin-top:15px}.edge-controls summary{cursor:pointer;color:#a9c7dc}.edge-controls p{line-height:1.6;color:#91a8c3;margin:12px 0}.edge-options{max-height:150px;overflow:auto;margin:12px 0}.edge-options label{display:flex;flex-direction:row;align-items:center;gap:8px;padding:5px}.edge-options input{width:15px;min-height:15px}.map-node circle{fill:#90abc8;stroke:#0c1529;stroke-width:2}.map-node text{font-size:12px;fill:#a5b7cc;paint-order:stroke;stroke:#0c1529;stroke-width:4;stroke-linejoin:round}.map-car{color:var(--car-color);transition:transform .5s linear}.map-car>circle{fill:var(--car-color);fill-opacity:.12;stroke:var(--car-color);stroke-width:1.5}.map-car rect{fill:var(--car-color);stroke:#091525;stroke-width:2}.map-car text{fill:var(--car-color);font-size:13px;font-weight:600;paint-order:stroke;stroke:#0c1529;stroke-width:4;stroke-linejoin:round}.map-car.selected>circle{stroke-width:3}.map-car.stale{opacity:.4}.map-car.offline>circle{stroke-dasharray:4 3}.map-caption{display:flex;justify-content:space-between;gap:12px;color:#86a0bf;padding-top:10px;line-height:1.6}.map-empty{min-height:340px;display:grid;place-content:center;color:#839bbb;background:#0c1529;padding:28px;text-align:center}.simulation-panel label{display:flex;flex-direction:column;gap:7px;color:#a5bad2;font-size:12px}.simulation-panel input,.simulation-panel select{width:100%;min-width:0}.simulation-panel fieldset{border:0;padding:0;margin:0 0 16px;min-width:0}.form-row{display:grid;grid-template-columns:1fr 1fr;gap:12px;margin:14px 0}.scenario-panel fieldset>label{margin-bottom:12px}.simulation-page .primary{color:#a6f0ff;border-color:#289db6;background:#134050}.state-pill{display:inline-flex;padding:4px 8px;border:1px solid #3f526d;border-radius:4px;font-size:11px;white-space:nowrap}.state-pill.pass{color:#67e1bb;background:#112f2e;border-color:#286a59}.state-pill.fail{color:#ffacb6;background:#3c2432;border-color:#865064}.state-pill.waiting{color:#f1d08f;background:#342e21;border-color:#75603a}.state-pill.unknown{color:#a2b1c6;background:#1a273c;border-color:#3c4f6a}.scenario-progress{border-top:1px solid #293c59;margin-top:19px;padding-top:16px}.scenario-progress>div{display:flex;justify-content:space-between;color:#a5d5e4}.scenario-progress progress{width:100%;height:6px;margin:12px 0;accent-color:#3ac5cf}.scenario-progress p{line-height:1.7;color:#b1c5dd;overflow-wrap:anywhere}.vehicle-select{max-width:320px}.vehicle-facts{display:grid;grid-template-columns:105px minmax(0,1fr);gap:9px 12px;line-height:1.5;margin:16px 0 19px}.vehicle-facts dt{color:#8fa6c3}.vehicle-facts dd{margin:0;overflow-wrap:anywhere}.speed-control{display:flex;align-items:flex-end;gap:10px;margin-top:15px}.speed-control label{max-width:130px}.plc-faults{margin-top:18px}.report-panel{margin-top:14px}.report-metrics{display:flex;flex-wrap:wrap;gap:12px 28px;padding-bottom:8px}.report-metrics dt{font-size:11px;color:#8da6c3}.report-metrics dd{margin:5px 0;font-size:19px;color:#b7e7ef;font-variant-numeric:tabular-nums}.report-identity{display:flex;flex-wrap:wrap;align-items:center;gap:10px 18px;padding:0 0 15px;color:#99b1ca;line-height:1.6}.report-identity b{color:#c2dbe8}.report-panel>.inline-warning{line-height:1.7;margin-bottom:12px}.report-table{overflow:auto}.report-panel table{width:100%;border-collapse:collapse;text-align:left;line-height:1.6}.report-panel th{font-weight:400;color:#8fa7c6;background:#12223a}.report-panel td,.report-panel th{padding:12px;border-bottom:1px solid #293b56}.report-panel td:first-child{min-width:150px}.report-panel td:last-child{overflow-wrap:anywhere}.report-empty{color:#91a8c3;line-height:1.8;padding:8px 0}.simulation-error,.simulation-notice{padding:12px 15px;line-height:1.6;margin-bottom:14px;border-radius:4px}.simulation-error{background:#402633;border:1px solid #865269;color:#ffbdc8}.simulation-notice{background:#143037;border:1px solid #2c626a;color:#9edfe0}.inline-warning{color:#eac17d;font-size:11px}.scenario-panel>.inline-warning{margin-top:10px}.configuration-guide{margin-bottom:14px}.configuration-guide h3{margin-bottom:10px}.configuration-guide p{line-height:1.8;color:#a0b8d0}.event-panel{margin-top:14px}.event-panel summary{cursor:pointer;color:#a8cedf}.event-panel ol{list-style:none;padding:0;max-height:250px;overflow:auto}.event-panel li{display:flex;gap:16px;line-height:1.7;border-top:1px solid #263a55;padding:9px 0;overflow-wrap:anywhere}.event-panel time{flex:none;color:#8ca5c1}.simulation-boundary{color:#8098b5;line-height:1.8;margin-top:16px;font-size:11px}@media(max-width:1050px){.simulation-grid{grid-template-columns:1fr}.simulation-title{align-items:flex-start}.map-panel{order:0}}@media(max-width:650px){.simulation-page{padding:5px}.simulation-title{flex-direction:column}.environment-panel{padding:14px}.simulation-panel{padding:14px}.simulation-panel>header{align-items:flex-start;flex-wrap:wrap}.simulation-map{height:280px}.form-row{grid-template-columns:1fr}.map-caption{flex-direction:column}.event-panel li{display:block}.event-panel time{display:block}.vehicle-facts{grid-template-columns:85px minmax(0,1fr)}}@media(prefers-reduced-motion:reduce){.map-car{transition:none}}
.instance-designer{margin-bottom:14px}.designer-intro{line-height:1.75;color:#9db4ce;margin-bottom:14px}.designer-intro code{color:#8ce0e4}.map-import{display:flex;align-items:center;flex-wrap:wrap;gap:12px;padding:12px;background:#0c172b;border:1px solid #263d5b;margin-bottom:14px}.map-import>span{color:#8da7c4;line-height:1.55}.file-picker{max-width:330px}.file-picker input{padding:7px}.topology-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}.vehicle-designer{border-top:1px solid #293e5d;margin-top:16px;padding-top:14px}.designer-subhead{display:flex;justify-content:space-between;gap:12px;align-items:baseline;margin-bottom:10px}.designer-subhead small,.designer-actions span{color:#8da4c0}.vehicle-config-row{display:grid;grid-template-columns:1fr 1.3fr .7fr auto;gap:9px;align-items:end;padding:9px 0;border-top:1px solid #223651}.agentel-config-row button{height:34px}.designer-actions{margin-top:15px}.call-plan{border-top:1px solid #2a405f;padding-top:13px;margin-top:14px}.recovery-panel{margin-top:14px}.recovery-panel>p{line-height:1.75;color:#a1b8d1}.recovery-items{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:10px;margin:14px 0}.recovery-items article{border:1px solid #304965;background:#0d192d;padding:12px}.recovery-title{display:flex;justify-content:space-between;gap:10px;margin-bottom:9px}.recovery-items p,.recovery-items small{display:block;color:#9fb6cf;line-height:1.6;overflow-wrap:anywhere;margin:5px 0}.recovery-items button{margin-top:8px}.recovery-confirmations{display:flex;flex-wrap:wrap;gap:18px;margin-top:12px}.recovery-confirmations label{display:flex;flex-direction:row;align-items:center;gap:8px}.recovery-confirmations input{width:16px;min-height:16px}.simulation-panel textarea{width:100%;min-width:0;resize:vertical}@media(max-width:1050px){.topology-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:650px){.topology-grid,.vehicle-config-row{grid-template-columns:1fr}}
.vehicle-config-row{grid-template-columns:minmax(120px,1fr) minmax(150px,1.3fr) repeat(3,minmax(90px,.65fr)) auto auto}.vehicle-config-row .loaded-config{flex-direction:row;align-items:center;gap:7px}.vehicle-config-row .loaded-config input{width:16px;min-height:16px}.vehicle-config-row button{height:34px}.vehicle-advanced{grid-column:1/-1;border:1px solid #223651;padding:9px}.vehicle-advanced summary{cursor:pointer;color:#8db5ce}.vehicle-advanced .form-row{margin:10px 0 0}
.protocol-designer{margin-bottom:14px}.protocol-designer>p{line-height:1.75;color:#9fb6cf;margin-bottom:12px}.protocol-grid{display:grid;grid-template-columns:minmax(0,1.5fr) minmax(260px,.8fr);gap:14px}.protocol-grid textarea{font-family:Consolas,monospace;font-size:12px;line-height:1.5;margin-top:10px;background:#091426;color:#bfe6ef}.protocol-grid aside{border:1px solid #2b4162;background:#0b172a;padding:14px}.protocol-grid aside dl{display:grid;grid-template-columns:90px minmax(0,1fr);gap:8px;line-height:1.5}.protocol-grid aside dt{color:#829bb8}.protocol-grid aside dd{margin:0;overflow-wrap:anywhere}.protocol-grid pre{max-height:220px;overflow:auto;background:#07101f;padding:10px;color:#a9d7e2;white-space:pre-wrap;overflow-wrap:anywhere}@media(max-width:850px){.protocol-grid{grid-template-columns:1fr}}
.voltage-curve-editor{margin-top:12px;border-top:1px solid #223651;padding-top:10px}.voltage-point{display:grid;grid-template-columns:minmax(110px,1fr) minmax(110px,1fr) auto;gap:9px;align-items:end;margin:8px 0}.voltage-point button{height:34px}.voltage-curve-editor .toolbar{margin-top:9px}.equipment-designer{margin-bottom:14px}.equipment-designer>p{line-height:1.75;color:#9fb6cf;margin-bottom:12px}.equipment-grid{display:grid;grid-template-columns:1fr 1fr;gap:16px}.equipment-grid>div{border:1px solid #2b4162;background:#0b172a;padding:14px;min-width:0}.equipment-grid label{min-width:150px}.equipment-grid textarea{font-family:Consolas,monospace;font-size:12px;line-height:1.5}.equipment-facts{display:grid;grid-template-columns:130px minmax(0,1fr);gap:8px;margin:16px 0;line-height:1.5}.equipment-facts dt{color:#829bb8}.equipment-facts dd{margin:0}.equipment-grid pre{max-height:180px;overflow:auto;background:#07101f;padding:10px;color:#a9d7e2;white-space:pre-wrap}@media(max-width:850px){.equipment-grid{grid-template-columns:1fr}}
</style>
