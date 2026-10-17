import type { Alarm, TaskRow, Vehicle } from './types.ts';
import { isRunningStage } from './state.ts';
export type VehicleCategory = 'running' | 'waiting' | 'idle' | 'charging' | 'fault' | 'offline' | 'unknown';
export const categoryLabels: Record<VehicleCategory, string> = { running: '执行中', waiting: '等待中', idle: '空闲', charging: '充电中', fault: '故障', offline: '离线 / 过期', unknown: '待核对' };
export function vehicleCategory(vehicle: Vehicle, tasks: TaskRow[], current: boolean): VehicleCategory {
  if (!current || vehicle.connection !== 'Online' || vehicle.quality !== 'Good') return 'offline';
  if (vehicle.emergencyStopped === true || vehicle.blocked === true) return 'fault';
  if (vehicle.charging === true) return 'charging';
  if (tasks.some(t => t.vehicleId === vehicle.vehicleId && isRunningStage(t.stage))) return 'running';
  if (tasks.some(t => t.vehicleId === vehicle.vehicleId && ['Created', 'WaitingForVehicle', 'CancellationPending', 'CancellationReview'].includes(t.stage))) return 'waiting';
  if (['Idle', 'None', '空闲', '待命'].includes(vehicle.navigationState ?? '')) return 'idle';
  return 'unknown';
}
export const alarmStatus = (status: string) => ({ Open: '未确认', Acknowledged: '已确认', Recovered: '已恢复' }[status] ?? status);
export const alarmSeverity = (severity: string) => ({ Info: '信息', Warning: '警告', Error: '错误', Critical: '严重' }[severity] ?? severity);
export function filterAlarms(alarms: Alarm[], search: string, status: string, severity: string): Alarm[] {
  return alarms.filter(a => (status === 'all' || a.status === status) && (severity === 'all' || a.severity === severity) &&
    `${a.alarmId} ${a.title} ${a.sourceId} ${a.message}`.toLowerCase().includes(search.toLowerCase()));
}
export const shortTime = (value: string | null | undefined) => value && Number.isFinite(Date.parse(value))
  ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—';
