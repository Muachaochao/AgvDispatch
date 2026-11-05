import type { Alarm, TaskRow, Vehicle } from './types.ts';
import { isRunningStage } from './state.ts';
import { vehicleObstacle, vehicleTelemetryCurrent } from './obstacles.ts';
export type VehicleCategory = 'running' | 'waiting' | 'blocked' | 'idle' | 'charging' | 'fault' | 'offline' | 'unknown';
export const categoryLabels: Record<VehicleCategory, string> = { running: '执行中', waiting: '等待中', blocked: '受阻等待', idle: '空闲', charging: '充电中', fault: '故障', offline: '离线 / 过期', unknown: '待核对' };
export function vehicleCategory(vehicle: Vehicle, tasks: TaskRow[], current: boolean, now = Date.now()): VehicleCategory {
  if (!vehicleTelemetryCurrent(vehicle, current, now)) return 'offline';
  const obstacle = vehicleObstacle(vehicle, current, now);
  if (obstacle.state === 'SafetyStop') return 'fault';
  if (obstacle.state === 'Blocked' || obstacle.state === 'Prolonged') return 'blocked';
  if (obstacle.state === 'Unknown') return 'unknown';
  if (vehicle.charging === true) return 'charging';
  if (tasks.some(t => t.vehicleId === vehicle.vehicleId && isRunningStage(t.stage))) return 'running';
  if (tasks.some(t => t.vehicleId === vehicle.vehicleId && ['Created', 'WaitingForVehicle', 'CancellationPending', 'CancellationReview'].includes(t.stage))) return 'waiting';
  if (['Idle', 'None', '空闲', '待命'].includes(vehicle.navigationState ?? '')) return 'idle';
  return 'unknown';
}
export const alarmStatus = (status: string) => ({ Open: '未确认', Acknowledged: '已确认', Recovered: '已恢复' }[status] ?? '状态待核对');
export const alarmSeverity = (severity: string) => ({ Info: '信息', Warning: '警告', Error: '错误', Critical: '严重' }[severity] ?? '级别待核对');
export const navigationStateLabel = (state: string | null | undefined) => {
  if (!state) return '状态未知';
  return ({
    '0': '待命',
    '1': '准备导航',
    '2': '导航执行中',
    '3': '导航暂停',
    '4': '导航完成',
    Idle: '待命',
    None: '待命',
    Running: '导航执行中'
  } as Record<string, string>)[state] ?? state;
};
export function filterAlarms(alarms: Alarm[], search: string, status: string, severity: string): Alarm[] {
  return alarms.filter(a => (status === 'all' || a.status === status) && (severity === 'all' || a.severity === severity) &&
    `${a.alarmId} ${a.title} ${a.sourceId} ${a.message}`.toLowerCase().includes(search.toLowerCase()));
}
export const shortTime = (value: string | null | undefined) => value && Number.isFinite(Date.parse(value))
  ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—';
