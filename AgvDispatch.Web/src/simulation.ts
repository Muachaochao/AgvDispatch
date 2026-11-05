import type { Snapshot, WebMap } from './types.ts';
import type { VendorPath } from './vendor-path.ts';
import { isFresh } from './state.ts';

export interface SimulationVehicle {
  x: number; y: number; angle?: number; station?: string; loaded?: boolean;
  current_task?: string | null; tasks?: Record<string, number>; offline?: boolean;
  paused?: boolean; speedScale?: number; completed_segments?: number; waitReason?: string;
  blockedByVehicle?: string | null;
  batteryPercent?: number; batteryVoltage?: number; charging?: boolean; speedMps?: number; distanceMeters?: number;
  simulationControl?: { sequence?: string; error?: string | null; responseDropPending?: boolean; blockedEdges?: string[]; charging?: boolean; plc?: SimulationControls['plc'] };
}
export interface SimulationNode { id: string; agvSiteId?: string | null; x: number; y: number; type?: string; enabled?: boolean; resourceIds?: string[] }
export interface SimulationEdge { from: string; to: string; id?: string; enabled?: boolean; bidirectional?: boolean; distance?: number; speedLimit?: number; geometryKind?: string; controlPoints?: { x: number; y: number }[] | null; resourceIds?: string[]; vendorPath?: VendorPath | null }
export interface SimulationResource { id: string; type?: string; capacity?: number; boundary?: { x: number; y: number }[] | null }
export interface SimulationMapDefinition { name?: string; source?: string; coordinateSystem?: string; unit?: string; nodes: SimulationNode[]; edges: SimulationEdge[]; resources: SimulationResource[]; [key: string]: unknown }
export interface SimulationMapWorkspace {
  project?: { mapId: string; isActive: boolean; activeVersionId?: string | null };
  published?: { mapId: string; versionId: string; status: string; definition: SimulationMapDefinition } | null;
}

// A live WebMap is a display projection: its sampled curve points are not control
// points and it omits resource bindings, speed limits and vendor path evidence.
export function simulationMapFromPublishedVersion(map: WebMap, workspace: SimulationMapWorkspace | null): SimulationMapDefinition {
  const project = workspace?.project, published = workspace?.published;
  if (!project?.isActive || project.mapId !== map.id || project.activeVersionId !== map.version ||
      published?.mapId !== map.id || published.versionId !== map.version || published.status !== 'Published')
    throw new Error('当前运行版本与完整发布地图不一致，未替换仿真地图。请刷新后重试，或导入该运行版本的完整地图文件。');
  const definition = published.definition;
  if (!definition || !Array.isArray(definition.nodes) || !Array.isArray(definition.edges) || !Array.isArray(definition.resources) ||
      definition.nodes.length < 3 || definition.edges.length === 0 || definition.unit !== 'm' ||
      definition.coordinateSystem !== map.coordinateSystem || definition.unit !== map.unit)
    throw new Error('后台未返回完整的米制发布地图，不能用显示快照代替。');
  if (definition.nodes.some(node => !Array.isArray(node.resourceIds)) || definition.edges.some(edge =>
      !Array.isArray(edge.resourceIds) || !Number.isFinite(edge.distance) || !Number.isFinite(edge.speedLimit) ||
      !['Line', 'CubicBezier', 'CircularArc'].includes(edge.geometryKind ?? '') ||
      (edge.geometryKind === 'CubicBezier' && edge.controlPoints?.length !== 2) ||
      (edge.geometryKind === 'CircularArc' && edge.controlPoints?.length !== 1) ||
      edge.controlPoints?.some(point => !Number.isFinite(point.x) || !Number.isFinite(point.y))))
    throw new Error('完整发布地图缺少道路、曲线控制点或资源绑定，未自动推测或补写。请先核对地图版本。');
  // Preserve every original field, including future metadata, and never mutate the source.
  return JSON.parse(JSON.stringify(definition)) as SimulationMapDefinition;
}

export interface SimulationCurrentMap {
  identity: WebMap;
  definition: SimulationMapDefinition;
  parameters: { mapId?: string; mapVersionId?: string; configuration?: {
    pickups?: { stationId?: string; pickupSite: string }[]; waitingSites?: string[]; waitingSite?: string; dumpingSite?: string;
  } } | null;
  parameterWarning: string;
}
export async function loadCurrentSimulationMap(read: (path: string) => Promise<unknown>, now = () => Date.now()): Promise<SimulationCurrentMap> {
  const before = await read('/api/snapshot') as Snapshot;
  if (before?.source !== 'backend' || !isFresh(before, now()) || !before.map?.id || !before.map.version || !before.map.definitionHash)
    throw new Error('当前运行地图没有有效观察。请等待后台连接和地图刷新后重试。');
  const identity = before.map;
  const workspace = await read(`/api/maps/${encodeURIComponent(identity.id)}/workspace`) as SimulationMapWorkspace;
  const definition = simulationMapFromPublishedVersion(identity, workspace);
  let parameters: SimulationCurrentMap['parameters'] = null, parameterWarning = '';
  try {
    const result = await read('/api/task-parameters') as SimulationCurrentMap['parameters'];
    if (result?.mapId !== identity.id || result.mapVersionId !== identity.version || !result.configuration)
      throw new Error('工位参数与当前运行地图版本不一致');
    parameters = result;
  } catch {
    parameterWarning = '当前工位参数不可用或不属于该地图版本，请手动选择取货、等待、倒料和检修节点。';
  }
  const after = await read('/api/snapshot') as Snapshot;
  if (after?.source !== 'backend' || !isFresh(after, now()) || after.map?.id !== identity.id ||
      after.map.version !== identity.version || after.map.definitionHash !== identity.definitionHash)
    throw new Error('拉取期间运行地图发生变化或连接已失效，未替换仿真地图。请刷新后重新拉取。');
  return { identity, definition, parameters, parameterWarning };
}
export interface SimulationStationConfiguration { stationId: string; pickupSite: string; offset?: number; signalSource?: 'verified' | 'isolated-synthetic'; priority?: number }
export interface SimulationPlcSignalTable {
  fileName: string;
  csvText?: string;
  sha256: string;
  sourceSha256?: string;
  sourceFile?: string;
  sourceDeviceId?: string;
  sourceDeviceName?: string;
  bytes?: number;
  rowCount?: number;
  signalCount?: number;
  stationCount?: number;
  dumperSignalCount?: number;
  privateSha256?: string;
  mapDefinitionHash?: string;
  headers?: string[];
  stations?: SimulationStationCatalogEntry[];
}
export interface ServicePlcPointTableSnapshot {
  deviceId: string; deviceName: string; enabled: boolean; relativePath: string; fileName: string;
  sha256: string; bytes: number; csvText: string; readAt: string;
  availableTables: { deviceId: string; deviceName: string; enabled: boolean; relativePath: string }[];
}
export interface SimulationStationCatalogEntry {
  stationId: string; offset?: number; signalSource: 'verified' | 'isolated-synthetic';
  sourceLabel?: string; fieldConfirmed?: boolean;
}
export interface SimulationVehicleConfiguration {
  id: string; initialSite: string; radiusMeters: number; lengthMeters: number; widthMeters: number; initialLoaded?: boolean;
  batteryPercent?: number; speedScale?: number; voltageCurve?: { percent: number; voltage: number }[];
  chargingGainPercentPerHour?: number;
}
export interface SimulationConfigureRequest {
  name: string; mapDefinition: SimulationMapDefinition; stations: SimulationStationConfiguration[];
  vehicles: SimulationVehicleConfiguration[];
  dumper: { dumpingSite: string; waitingSite: string; waitingSites?: string[]; resourceId: string };
  inspectionSite: string; plcSignalTable: SimulationPlcSignalTable;
  mapIdentity?: { mapId?: string; mapVersionId?: string; sourceName?: string; definitionHash?: string };
}
export interface SimulationCall { stationId: string; atSeconds: number; vehicleId?: string | null }
export interface SimulationScheme { id: string; name: string; updatedAt?: number; mapName?: string; mapId?: string; mapVersionId?: string; mapDefinitionHash?: string; vehicleCount?: number; stationCount?: number; scenarioName?: string; plcTableName?: string; plcTableSha256?: string; plcSourceSha256?: string }
export interface SimulationGuidance { taskId: string; vehicleId?: string; stationId?: string; stepNumber?: number; stepName?: string; blockedBy?: string; restoreAction?: string; expectedFeedback?: string }
export interface SimulationReplayFrame {
  time: number; vehicles?: { vehicleId: string; x: number; y: number; angle?: number; site?: string; waitReason?: string; taskId?: string }[];
  tasks?: { taskId: string; stationId?: string; vehicleId?: string; stage?: string; step?: string | number; waitReason?: string }[];
  leases?: unknown[];
}
export interface SimulationReplay { id: string; timeline: SimulationReplayFrame[]; map?: SimulationMapDefinition; mapMessage?: string | null; sampled?: boolean }
export interface SimulationRecoveryItem {
  ticketId: string; taskId: string; stepId?: string; stepText: string; state: string; reason: string; verified: boolean;
  revision: number; vehicleId?: string | null; vehicleSummary?: string; plcSummary?: string; resourceSummary?: string; loadSummary?: string;
}
export interface SimulationRecovery {
  sessionId?: string; released: boolean; message?: string; items: SimulationRecoveryItem[]; error?: string;
}
export interface SimulationProtocolStatus {
  running: boolean; name?: string; transport?: string; framing?: string; port?: number; rules?: number;
  requests?: number; state?: Record<string, unknown>; counters?: Record<string, number>;
  startedAt?: number; lastEvent?: Record<string, unknown> | null; error?: string;
}
export interface SimulationEquipmentStatus {
  running: boolean; floors?: string[]; currentFloor?: string; targetFloor?: string | null;
  direction?: string; door?: string; moving?: boolean; progress?: number; fault?: boolean;
  signals?: Record<string, unknown>; schedules?: unknown[]; signalEvents?: Record<string, unknown>[]; error?: string;
}
export interface SimulationCheck { name: string; status: string; detail?: unknown }
export interface SimulationTaskRecord {
  taskId: string; stationId?: string; vehicleId?: string; stage?: string; currentStepNumber?: number;
  currentStepType?: string; currentStepName?: string; currentStepStatus?: string; waitingExplanation?: string;
  completed?: boolean; steps?: { number: number; stepType?: string; stepName?: string; status?: string; waitingExplanation?: string }[];
}
export interface SimulationVersionComparison {
  status?: string; message?: string;
  simulation?: { informationalVersion?: string | null; buildId?: string; [key: string]: unknown } | null;
  field?: { informationalVersion?: string | null; buildId?: string; [key: string]: unknown } | null;
  observedAt?: number | null; scope?: string;
  differences?: { key?: string; label?: string; simulation?: unknown; field?: unknown; [key: string]: unknown }[];
  [key: string]: unknown;
}
export interface SimulationReport {
  status: string; checks?: SimulationCheck[]; metrics?: Record<string, unknown>;
  id?: string; name?: string; message?: string; generatedAt?: string | number; finishedAt?: string | number;
  recoveryHold?: boolean; completedBusinessCycle?: boolean; [key: string]: unknown;
  firstFailure?: string | { message?: string; taskId?: string; vehicleId?: string; stepName?: string };
  waitingChain?: { taskId?: string; vehicleId?: string; stationId?: string; stepName?: string; waitReason?: string; blockedBy?: string; site?: string }[];
  taskSummary?: SimulationTaskRecord[];
  versionComparison?: SimulationVersionComparison;
}

const metricNames: Record<string, string> = {
  taskCount: '任务数', completedTaskCount: '已完成任务数', completedRounds: '完成轮数', durationSeconds: '运行时长（秒）',
  samples: '观察采样数', minimumSeparationMeters: '车辆中心最小间距（米）', longestUnchangedStepSeconds: '最长步骤等待（秒）',
  commandCount: '原始运输命令数', vehicleCount: '车辆数', callCount: '计划呼叫数', issuedCalls: '已触发呼叫数',
  maximumQueuedTasks: '最大排队任务数', maximumActiveTasks: '最大执行任务数', longestWaitSeconds: '最长派车等待（秒）',
  maxWaitingSeconds: '最长派车等待（秒）', duplicateVehicleAssignments: '同车重复分配次数', stationCount: '工位数',
  acceptedCalls: '已接收呼叫数', peakQueueLength: '最大积压任务数', maxQueueDepth: '最大积压任务数',
  maximumWaitingTasks: '最多待派任务', maximumDuePlanCalls: '最多到期待发呼叫', maximumBusyWaitingTasks: '全部车辆忙碌时最多待派任务'
};
export function simulationMetricLabel(key: string): string {
  return metricNames[key] ?? (/[\u3400-\u9fff]/.test(key) ? key : '扩展检查指标');
}
export function simulationMetricValue(value: unknown): string {
  if (typeof value === 'boolean') return value ? '是' : '否';
  if (typeof value === 'number') return Number.isFinite(value) ? new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 3 }).format(value) : '证据无效';
  return typeof value === 'string' ? value : '未提供';
}
export function simulationMetricComparison(current: Record<string, unknown> = {}, previous: Record<string, unknown> = {}) {
  return Object.entries(current).flatMap(([key, value]) => typeof value === 'number' && Number.isFinite(value) &&
    typeof previous[key] === 'number' && Number.isFinite(previous[key])
    ? [{ key, label: simulationMetricLabel(key), current: value, previous: previous[key] as number, delta: value - (previous[key] as number) }] : []);
}
export function simulationReplayCars(frame?: SimulationReplayFrame): Record<string, SimulationVehicle> {
  return Object.fromEntries((frame?.vehicles ?? []).filter(vehicle => !!vehicle.vehicleId && Number.isFinite(vehicle.x) && Number.isFinite(vehicle.y))
    .map(vehicle => [vehicle.vehicleId, { x: vehicle.x, y: vehicle.y, angle: vehicle.angle, station: vehicle.site,
      current_task: vehicle.taskId, waitReason: vehicle.waitReason }]));
}
export interface SimulationScenario {
  id?: string; name?: string; status: string; completedRounds?: number; totalRounds?: number;
  message?: string; startedAt?: string | number; events?: { time?: string | number; message: string }[];
}
export interface SimulationStatus {
  enabled: boolean; rootName?: string; running?: boolean; fresh?: boolean; message?: string; error?: string;
  device?: { time: number; cars?: Record<string, SimulationVehicle>; memory_hex?: string; controls?: unknown;
    simulationControl?: { sequence?: string; error?: string | null; plc?: SimulationControls['plc'] } } | null;
  map?: SimulationMapDefinition | null;
  scenario?: SimulationScenario | null; report?: SimulationReport | null; phase?: string; controls?: Partial<SimulationControls>;
  configuration?: {
    stations: SimulationStationConfiguration[]; vehicles: SimulationVehicleConfiguration[]; inspectionSite: string;
    dumper: { dumpingSite: string; waitingSite: string; waitingSites?: string[]; resourceId: string }; mapName: string;
    plcSignalTable?: SimulationPlcSignalTable | null;
    mapIdentity?: { mapId?: string; mapVersionId?: string; sourceName?: string; definitionHash?: string } | null;
  } | null;
  recovery?: SimulationRecovery | null;
  protocol?: SimulationProtocolStatus | null;
  equipment?: SimulationEquipmentStatus | null;
  schemes?: SimulationScheme[]; selectedSchemeId?: string | null;
  recentInstances?: { rootName: string; mapName?: string; vehicleCount?: number; stationCount?: number; updatedAt?: number }[];
  stationCatalog?: SimulationStationCatalogEntry[];
  plcSignalTable?: SimulationPlcSignalTable | null;
  history?: SimulationReport[]; recoveryGuidance?: SimulationGuidance[];
  startupPlan?: { status: string; message?: string };
  versionComparison?: SimulationVersionComparison;
}
export interface SimulationControls {
  sequence: string;
  vehicles?: Record<string, { paused?: boolean; offline?: boolean; charging?: boolean; speedScale?: number; dropNextResponse?: boolean; blockedEdges?: string[] }>;
  plc?: { readyDelaySeconds?: number; doneDelaySeconds?: number; suppressReady?: boolean; suppressDone?: boolean; fault?: boolean };
}
export interface SimulationRun {
  name: 'normal-cycle' | 'simultaneous-calls' | 'custom-calls' | 'disconnect-reconnect' | 'plc-delay' |
    'service-restart' | 'plc-recovery' | 'resume-recovery' | 'dispatch-backlog';
  rounds: number; intervalSeconds: number; timeoutSeconds: number; calls?: SimulationCall[];
  plcFault?: 'ready' | 'done'; holdSeconds?: number; restartStage?: 'pickup' | 'delivery';
}

export async function simulationApi<T>(path = '', body?: unknown, signal?: AbortSignal): Promise<T> {
  const response = await fetch('/api/simulation' + path, {
    method: body === undefined ? 'GET' : 'POST', cache: 'no-store',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: signal ?? AbortSignal.timeout(45000)
  });
  const result = await response.json();
  if (!response.ok) throw new Error(result.detail ?? result.error ?? `仿真请求失败 (${response.status})`);
  return result as T;
}

export function simulationFresh(status: SimulationStatus | null, now: number): boolean {
  const stamp = status?.device?.time;
  return !!status?.enabled && !!status.running && status.fresh === true &&
    typeof stamp === 'number' && Number.isFinite(stamp) && stamp * 1000 <= now + 1000 && now - stamp * 1000 < 5000;
}
export function scenarioActive(scenario?: SimulationScenario | null): boolean {
  return ['running', 'starting', 'pending', 'stopping'].includes(scenario?.status?.toLowerCase() ?? '');
}
export function simulationTone(status?: string): 'pass' | 'fail' | 'waiting' | 'unknown' {
  switch (status?.toLowerCase()) {
    case 'pass': case 'passed': case 'completed': return 'pass';
    case 'fail': case 'failed': case 'error': return 'fail';
    case 'running': case 'starting': case 'pending': case 'waiting': return 'waiting';
    default: return 'unknown';
  }
}
export function simulationLabel(status?: string): string {
  const known=({ pass: '通过', passed: '通过', fail: '失败', failed: '失败', error: '出错', completed: '已完成',
    running: '运行中', starting: '启动中', pending: '等待中', waiting: '等待中', stopping: '停止中',
    aborted: '已中止', idle: '未运行', unknown: '未核定', inconclusive: '证据不足', skipped: '未执行' } as Record<string, string>)[status?.toLowerCase() ?? ''];
  return known ?? (status && /[\u3400-\u9fff]/.test(status) ? status : status ? '状态待核对' : '尚无记录');
}
export function simulationGeometry(nodes: SimulationNode[], edges: SimulationEdge[], cars: Record<string, SimulationVehicle>) {
  const validNodes = nodes.filter(node => Number.isFinite(node.x) && Number.isFinite(node.y));
  const byId = new Map(validNodes.flatMap(node => [[node.id, node], ...(node.agvSiteId ? [[node.agvSiteId, node] as const] : [])] as const));
  const points = [...validNodes, ...edges.flatMap(edge => edge.controlPoints ?? []).filter(point => Number.isFinite(point.x) && Number.isFinite(point.y)), ...Object.values(cars).filter(car => Number.isFinite(car.x) && Number.isFinite(car.y))];
  const minX = Math.min(0, ...points.map(point => point.x)), maxX = Math.max(1, ...points.map(point => point.x));
  const minY = Math.min(0, ...points.map(point => point.y)), maxY = Math.max(1, ...points.map(point => point.y));
  const scale = Math.min(700 / Math.max(1, maxX - minX), 330 / Math.max(1, maxY - minY));
  const offsetX = (800 - (maxX - minX) * scale) / 2;
  const offsetY = (430 - (maxY - minY) * scale) / 2;
  const position = (point: { x: number; y: number }) => ({ x: offsetX + (point.x - minX) * scale, y: 430 - offsetY - (point.y - minY) * scale });
  return { scale, nodes: validNodes.map(node => ({ ...node, ...position(node) })),
    edges: edges.flatMap((edge, index) => {
      const from = byId.get(edge.from), to = byId.get(edge.to); if (!from || !to) return [];
      const a = position(from), b = position(to);
      const controls = (edge.controlPoints ?? []).filter(point => Number.isFinite(point.x) && Number.isFinite(point.y)).map(position);
      const path = `M ${a.x} ${a.y} ` + (edge.geometryKind === 'CubicBezier' && controls.length === 2
        ? `C ${controls[0].x} ${controls[0].y} ${controls[1].x} ${controls[1].y} ${b.x} ${b.y}` : `L ${b.x} ${b.y}`);
      return [{ ...edge, key: edge.id ?? String(index), a, b, path }];
    }),
    cars: Object.entries(cars).filter(([, car]) => Number.isFinite(car.x) && Number.isFinite(car.y)).map(([id, car]) => ({ ...car, id, ...position(car) })) };
}
