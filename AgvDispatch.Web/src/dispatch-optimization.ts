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
