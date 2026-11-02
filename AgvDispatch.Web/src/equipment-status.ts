import type { Device } from './types';

export type EquipmentTone = 'green' | 'amber' | 'red' | 'offline';

// This is a signal/communication summary, not permission to dispatch a job.
export function stationSignalSummary(devices: Device[], current: boolean) {
  const tones = devices.map(device => equipmentTone(device, current));
  const good = tones.filter(tone => tone === 'green').length;
  const tone: EquipmentTone = !current || !devices.length ? 'offline' :
    tones.includes('red') ? 'red' : good === devices.length ? 'green' : 'amber';
  return { tone, label: !current ? '数据非当前' : !devices.length ? '尚未配置' :
    `${good}/${devices.length} 正常`, total: devices.length };
}

export function equipmentTone(device: Pick<Device, 'quality' | 'status'>, current: boolean): EquipmentTone {
  if (!current) return 'offline';
  if (['未启用', 'Disabled'].includes(device.quality) || device.status === '通信未启用') return 'offline';
  if (['数据过期', 'Stale'].includes(device.quality) || device.status === '数据过期') return 'amber';
  if (/故障|报警|急停|信号冲突|中断|未连接|离线/.test(device.status) ||
      ['Faulted', 'Interrupted', 'Offline', 'Disconnected'].includes(device.status)) return 'red';
  if (['正常', 'Good'].includes(device.quality)) return 'green';
  // PLC connection diagnostics use a display label rather than SignalQuality.
  if (device.quality === '连接诊断' && device.status === '正常') return 'green';
  return 'amber';
}

export interface ChargingSite {
  id: string; chargingSiteId: string; exitSiteId: string; resourceId: string;
  commissioned: boolean; allowedVehicleIds: string[];
}
export interface ChargingSession {
  sessionId: string; vehicleId: string; phase: string; message: string; updatedAt: string; site?: ChargingSite;
  battery?: { batteryPercent?: number; current?: number; temperature?: number; charging?: boolean };
}
export interface ChargingStatus {
  enabled: boolean; blockReason?: string; capturedAt: string;
  options: { startBatteryPercent: number; criticalBatteryPercent: number; targetBatteryPercent: number; sites: ChargingSite[] };
  sessions: ChargingSession[];
}

// /api/charging reports the C# workflow and vehicle samples. It currently has no
// charger TCP observations. Configuration and workflow phases cannot prove the
// charger is connected, charging or free of alarms.
export function chargerMonitoringState(status?: ChargingStatus, readError = '') {
  return {
    label: '未接入实时监测',
    tone: 'offline' as EquipmentTone,
    communication: '未接入',
    operation: '未知',
    fault: '未知',
    lastFeedback: '尚未取得',
    policy: readError ? '读取失败' : !status ? '未取得' : status.enabled ? '已启用' : '未启用',
    summary: readError ? '后台充电策略读取失败，充电桩实时反馈尚未接入。' :
      status?.options.sites.length ? '充电位配置仅代表后台策略，尚未取得充电桩实时反馈。' :
      '尚未接入充电桩实时反馈，不能判断当前通信、充电或故障状态。'
  };
}
