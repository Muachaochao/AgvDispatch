export type DispatchAssignmentMode = 'Disabled' | 'Shadow' | 'Global';

export interface DispatchAssignmentOptions {
  mode: DispatchAssignmentMode;
  intervalMs: number;
  windowSize: number;
  candidatesPerVehicle: number;
  minimumCandidatesPerTask: number;
  waitPriorityPerMinute: number;
  maxWaitPriority: number;
  deadlineHorizonMinutes: number;
  maxDeadlinePriority: number;
  minimumBatteryPercent: number;
  nominalSpeedMetersPerSecond: number;
  standbyBonusSeconds: number;
  batteryRiskSeconds: number;
  crossZoneSeconds: number;
  returnCostWeight: number;
  siteZones: Record<string, string>;
  stationDeadlineMinutes: Record<string, number>;
}

export interface DispatchZoneRow { siteId: string; zone: string }
export interface DispatchDeadlineRow { stationId: string; minutes: number }

export interface DispatchOptimizationCandidate {
  vehicleId: string;
  travelSeconds: number;
  totalCost: number;
  reason: string;
  eligible: boolean;
  trafficWaitSeconds?: number;
  returnSeconds?: number;
  batteryRiskSeconds?: number;
  crossZoneSeconds?: number;
  standbyBonusSeconds?: number;
}

export interface DispatchOptimizationTask {
  taskId: string;
  stationId: string;
  calledAt: string;
  basePriority: number;
  effectivePriority: number;
  waitMinutes: number;
  recommendedVehicleId: string | null;
  greedyVehicleId: string | null;
  dispatchedVehicleId: string | null;
  reason: string;
  candidates: DispatchOptimizationCandidate[];
  blockedLayer?: string | null;
}

export type DispatchPipelineLayerKey = 'queue' | 'candidates' | 'matching' | 'traffic' | 'safety' | 'execution';
export interface DispatchPipelineLayerSnapshot {
  key: string;
  number: number;
  name: string;
  state: string;
  summary: string;
  itemCount: number | null;
  updatedAt: string;
}

const dispatchPipelineDefinitions = [
  { key: 'queue', number: 1, name: 'PLC 呼叫队列', purpose: '持久化、去重，保留优先级和原始呼叫时间', link: '/tasks', linkLabel: '查看业务任务' },
  { key: 'candidates', number: 2, name: '候选筛选', purpose: '核对车辆健康、能力、电量、车辆池与路线可达', link: '/vehicles', linkLabel: '查看车辆状态' },
  { key: 'matching', number: 3, name: '滚动全局匹配', purpose: '对当前窗口任务与可用车辆生成推荐组合', link: null, linkLabel: '' },
  { key: 'traffic', number: 4, name: '交通路权', purpose: '按优先级申请容量，交接后释放原占用', link: '/resource-review', linkLabel: '查看公共资源' },
  { key: 'safety', number: 5, name: '安全复核', purpose: '命令登记前复核地图、车辆、路权及 PLC 许可', link: '/commissioning', linkLabel: '查看执行条件' },
  { key: 'execution', number: 6, name: '执行与恢复', purpose: '沿原任务和命令队列执行，保留恢复依据', link: '/recovery', linkLabel: '查看执行恢复' },
] as const;

export function dispatchPipelineLayerName(key?: string | null): string {
  const layer = dispatchPipelineDefinitions.find(layer => layer.key === key);
  return layer ? `第 ${layer.number} 层 · ${layer.name}` : '';
}

export function dispatchPipelineLayers(layers: DispatchPipelineLayerSnapshot[] | undefined, fresh: boolean, now = Date.now()) {
  const states: Record<string, { label: string; tone: string }> = {
    Ready: { label: '已就绪', tone: 'ready' }, Running: { label: '运行中', tone: 'running' },
    Waiting: { label: '等待中', tone: 'waiting' }, Blocked: { label: '已阻止', tone: 'blocked' },
    Disabled: { label: '未启用', tone: 'disabled' }, Observing: { label: '影子观察', tone: 'observing' },
    Idle: { label: '当前空闲', tone: 'idle' }
  };
  return dispatchPipelineDefinitions.map(definition => {
    const rows = layers?.filter(layer => layer.key === definition.key) ?? [];
    const observed = rows.length === 1 ? rows[0] : undefined;
    const stamp = observed ? Date.parse(observed.updatedAt) : Number.NaN;
    const valid = !!observed && !!states[observed.state] && Number.isFinite(stamp) && stamp > 0 && stamp <= now + 1000;
    const current = valid && fresh && now - stamp < 15000;
    return {
      ...definition,
      label: !observed ? '未取得状态' : !valid ? '尚未观察' : current ? states[observed.state]!.label : '观察已过期',
      tone: !observed || !valid ? 'unknown' : current ? states[observed.state]!.tone : 'stale',
      state: current ? observed.state : 'Unknown',
      summary: observed?.summary || '等待后台提供该层的运行观察。',
      itemCount: current && typeof observed.itemCount === 'number' && Number.isInteger(observed.itemCount) && observed.itemCount >= 0
        ? observed.itemCount : null,
      updatedAt: valid ? observed.updatedAt : null,
    };
  });
}

export interface DispatchOptimizationSnapshot {
  options: DispatchAssignmentOptions;
  revision: string;
  capturedAt: string;
  cycleNumber: number;
  durationMs: number;
  waitingTaskCount: number;
  windowTaskCount: number;
  eligibleVehicleCount: number;
  candidateCount: number;
  retainedCandidateCount?: number;
  shadowCycleCount?: number;
  improvedCycleCount?: number;
  routeCacheHits: number;
  routeCacheMisses: number;
  message: string;
  tasks: DispatchOptimizationTask[];
  layers?: DispatchPipelineLayerSnapshot[];
}

export const assignmentModeLabels: Record<DispatchAssignmentMode, string> = {
  Disabled: '传统派车', Shadow: '影子比对', Global: '全局匹配'
};

export function assignmentOptionsError(options: DispatchAssignmentOptions): string {
  if (!['Disabled', 'Shadow', 'Global'].includes(options.mode)) return '请选择有效的派车模式。';
  const ranges: [keyof DispatchAssignmentOptions, string, number, number, boolean][] = [
    ['intervalMs', '滚动周期', 2000, 5000, true],
    ['windowSize', '窗口任务数', 1, 1000, true],
    ['candidatesPerVehicle', '每车候选数', 1, 100, true],
    ['minimumCandidatesPerTask', '每任务保留车辆数', 1, 5, true],
    ['waitPriorityPerMinute', '每分钟等待补偿', 0, 100, false],
    ['maxWaitPriority', '等待补偿上限', 0, 10000, false],
    ['deadlineHorizonMinutes', '截止时间预警窗口', 1, 1440, false],
    ['maxDeadlinePriority', '截止时间补偿上限', 0, 10000, false],
    ['minimumBatteryPercent', '业务最低电量', 0, 100, false],
    ['nominalSpeedMetersPerSecond', '路线估算速度', 0.05, 10, false],
    ['standbyBonusSeconds', '待机车加分', 0, 1, false],
    ['batteryRiskSeconds', '低电量风险成本', 0, 3600, false],
    ['crossZoneSeconds', '跨区成本', 0, 3600, false],
    ['returnCostWeight', '返程成本权重', 0, 10, false],
  ];
  for (const [key, label, min, max, integer] of ranges) {
    const value = options[key];
    if (typeof value !== 'number' || !Number.isFinite(value) || value < min || value > max || (integer && !Number.isInteger(value)))
      return `${label}须为 ${min}～${max}${integer ? ' 的整数' : ' 之间的数值'}。`;
  }
  const zones = Object.entries(options.siteZones ?? {});
  const deadlines = Object.entries(options.stationDeadlineMinutes ?? {});
  if (zones.length > 1000 || deadlines.length > 1000) return '区域和工位时限配置各不能超过 1000 项。';
  if (zones.some(([site, zone]) => !site.trim() || site.length > 128 || typeof zone !== 'string' || !zone.trim() || zone.length > 128))
    return '区域配置须填写有效站点与区域名称，长度均不能超过 128 字符。';
  if (deadlines.some(([station, minutes]) => !station.trim() || station.length > 128 || typeof minutes !== 'number' || !Number.isFinite(minutes) || minutes < 1 || minutes > 10080))
    return '工位时限须为 1～10080 分钟，工位编号不能为空且不能超过 128 字符。';
  return '';
}

export function assignmentMappingRowsError(zones: DispatchZoneRow[], deadlines: DispatchDeadlineRow[]): string {
  if (zones.some(row => !row.siteId.trim() || !row.zone.trim())) return '请填写每一条区域配置的站点与区域，或删除空行。';
  if (deadlines.some(row => !row.stationId.trim())) return '请填写工位时限的工位编号，或删除空行。';
  const duplicate = (values: string[]) => new Set(values.map(value => value.trim().toLocaleLowerCase())).size !== values.length;
  if (duplicate(zones.map(row => row.siteId))) return '同一站点只能配置一个业务区域。';
  if (duplicate(deadlines.map(row => row.stationId))) return '同一工位只能配置一个呼叫时限。';
  return '';
}

export function changedAssignmentCount(tasks: DispatchOptimizationTask[]): number {
  return tasks.filter(task => (task.recommendedVehicleId ?? '') !== (task.greedyVehicleId ?? '')).length;
}

export function assignmentNumber(value: number | null | undefined, digits = 1): string {
  return typeof value === 'number' && Number.isFinite(value) ? value.toFixed(digits) : '—';
}
