import type { Vehicle, VehicleObstacle } from './types.ts';

export const obstacleNavigationPolicy = '车端先报Blocked/52200；10秒开始核验原任务终态，30秒预警、60秒报警、120秒建议封路；仅原任务全链终态且安全条件通过后自动改道';
export const localDetourEvidence = '自动改道保留原任务、载荷、PLC责任和路权；位置、地图、车身包络或原任务终态未知时冻结';
export interface ObstacleView extends VehicleObstacle { label: string; tone: 'blocked' | 'fault' | 'unknown' | 'idle'; duration: string }

export function vehicleTelemetryCurrent(vehicle: Vehicle, current: boolean, now = Date.now()): boolean {
  const age = now - Date.parse(vehicle.lastSuccessfulPollAt ?? '');
  return current && vehicle.connection === 'Online' && vehicle.quality === 'Good' &&
    Number.isFinite(age) && age >= -1000 && age <= 5000;
}

export function obstacleDuration(seconds: number | null): string {
  if (seconds === null || !Number.isFinite(seconds) || seconds < 0) return '时长未知';
  const value = Math.floor(seconds);
  return value < 60 ? `${value} 秒` : `${Math.floor(value / 60)} 分 ${value % 60} 秒`;
}

export function vehicleObstacle(vehicle: Vehicle, current: boolean, now = Date.now()): ObstacleView {
  const observation = vehicle.obstacle;
  const base: VehicleObstacle = { state: 'Unknown', since: null, durationSeconds: null, lastClearedAt: observation?.lastClearedAt ?? null,
    source: observation?.source ?? 'Blocked 字段（旧后台未提供障碍诊断）', message: '',
    navigationPolicy: observation?.navigationPolicy || obstacleNavigationPolicy,
    localDetourEvidence: observation?.localDetourEvidence || localDetourEvidence };
  // Neither a stale clear sample nor a cached blocked sample proves the current state.
  if (!vehicleTelemetryCurrent(vehicle, current, now)) return { ...base, label: '状态未知', tone: 'unknown', duration: '—',
    message: '车辆状态已失效，不能确认障碍解除或原任务恢复。' };
  if (vehicle.emergencyStopped === true || observation?.state === 'SafetyStop') return { ...base, ...observation,
    state: 'SafetyStop', label: '安全暂停', tone: 'fault', duration: '—',
    message: observation?.state === 'SafetyStop' ? observation.message : '车辆急停触发，按安全流程处置。' };
  // Unknown safety telemetry is authoritative; a blocked flag cannot establish a safe local wait.
  if (observation?.state === 'Unknown' || vehicle.emergencyStopped !== false) return { ...base,
    label: '状态未知', tone: 'unknown', duration: '—',
    message: observation?.state === 'Unknown' ? observation.message : '急停状态尚未明确，不能确认车辆仅在局部受阻等待。' };
  if (observation?.state === 'Blocked' || observation?.state === 'Prolonged' || vehicle.blocked === true) {
    const seconds = observation?.durationSeconds;
    const durationSeconds = typeof seconds === 'number' && Number.isFinite(seconds) && seconds >= 0 ? seconds : null;
    const prolonged = observation?.state === 'Prolonged' || (durationSeconds !== null && durationSeconds >= 30);
    return { ...base, ...observation, state: prolonged ? 'Prolonged' : 'Blocked', durationSeconds,
      label: prolonged ? '持续受阻（≥30秒）' : '受阻等待', tone: 'blocked', duration: obstacleDuration(durationSeconds),
      message: observation?.message || '该车保留原任务等待车端处理；障碍移开后的继续执行须由车端状态确认。' };
  }
  if (!observation && vehicle.blocked !== false) return { ...base,
    label: '状态未知', tone: 'unknown', duration: '—', message: '尚未取得明确的障碍状态。' };
  return { ...base, ...observation, state: 'Clear', label: observation?.lastClearedAt ? '已观察到解除' : '未受阻',
    tone: 'idle', duration: '—', message: observation?.message || '未观察到受阻；原任务是否继续及完成仍以车端任务反馈为准。' };
}
