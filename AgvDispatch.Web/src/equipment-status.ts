import type { Device } from './types';

export type EquipmentTone = 'green' | 'amber' | 'red' | 'offline';

export function executionModeState(safety: { dispatchingEnabled: boolean; plcWritesAllowed: boolean; agvCommandsAllowed: boolean } | null | undefined, current: boolean) {
  if (!current || !safety) return { label: '状态未取得', tone: 'offline' as EquipmentTone };
  const flags = [safety.dispatchingEnabled, safety.plcWritesAllowed, safety.agvCommandsAllowed];
  if (flags.every(value => value === false)) return { label: '查看模式', tone: 'offline' as EquipmentTone };
  if (flags.every(value => value === true)) return { label: '执行已启用', tone: 'green' as EquipmentTone };
  return { label: '部分执行开启', tone: 'amber' as EquipmentTone };
}

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
  chargerId?: string; chargerAddress?: string; commissioned: boolean; allowedVehicleIds: string[]; controlMode?: string;
}
export interface ChargingBatteryReading {
  batteryPercent?: number | null; current?: number | null; temperature?: number | null;
  charging?: boolean | null; observedAt?: string;
}
export interface ChargingChargerReading {
  chargerIp: string; isAuto?: boolean | null; charging?: boolean | null;
  current?: number | null; voltage?: number | null; alarms: string[]; observedAt: string;
}
export interface ChargingWorkflowStatus {
  operationId: string; kind: string; state: string; updatedAt: string; message: string;
  battery?: ChargingBatteryReading; station?: ChargingChargerReading;
  continuousPositiveSeconds: number; shutdownVerified: boolean; evidenceDirectory?: string;
}
export interface ChargerTelemetrySnapshot {
  chargerId: string; chargerName: string; address: string; ackPort?: number; statusPort?: number;
  ackPortReachable: boolean; statusPortReachable: boolean;
  observedAt: string; outcome: 'connected' | 'degraded' | 'offline'; message: string;
  lastFullSuccessAt?: string | null; transport?: string;
}
export interface ChargingSession {
  sessionId: string; vehicleId: string; phase: string; message: string; updatedAt: string; site?: ChargingSite;
  waitingRoute?: { path: string[] };
  siteEstimates?: { siteId: string; bayWaitSeconds?: number | null; trafficWaitSeconds?: number | null;
    travelSeconds?: number | null; startInSeconds?: number | null }[];
  battery?: ChargingBatteryReading; requestId?: string; originSiteId?: string | null; returnSiteId?: string | null;
  requestedReturnSiteId?: string | null; targetBatteryPercent?: number | null; requiredPositiveSeconds?: number | null;
  scriptOperationId?: string | null; workflowStatus?: ChargingWorkflowStatus; shutdownVerified?: boolean;
}
export interface ChargingStatus {
  enabled: boolean; blockReason?: string; capturedAt: string;
  options: { startBatteryPercent: number; criticalBatteryPercent: number; targetBatteryPercent: number; sites: ChargingSite[];
    stateMaxAgeSeconds?: number; allowTimedTests?: boolean; returnToStandbyAfterCharging?: boolean; defaultStandbySiteId?: string };
  sessions: ChargingSession[];
  chargers?: ChargerTelemetrySnapshot[];
}

export const chargingPhaseLabels: Record<string, string> = {
  Queued: '排队', WaitingForBusiness: '等待业务安全结束', Reserved: '已预留充电位',
  SendingToWaitingSite: '准备前往安全等桩位', GoingToWaitingSite: '前往安全等桩位',
  SendingToCharger: '前往命令待确认', GoingToCharger: '前往接近点',
  SendingDock: '对接命令待确认', Docking: '识别并低速对接',
  StartingSession: '建立充电会话', SessionRunning: '充电会话运行中',
  StartingCharge: '启动充电待确认', AwaitingCurrent: '等待充电电流', Charging: '充电中',
  StoppingCharge: '停止充电待确认', AwaitingStop: '等待停止确认',
  SendingUndock: '退桩命令待确认', Undocking: '低速退桩',
  SendingExit: '退出命令待确认', Exiting: '退出充电位',
  SendingReturn: '返程命令待确认', Returning: '返回待机点', Hold: '暂停待核对', Completed: '已结束'
};

export function chargingObservationFresh(stamp: string | undefined, now: number, maxAgeSeconds = 5) {
  const time = stamp ? Date.parse(stamp) : NaN;
  return Number.isFinite(time) && Number.isFinite(now) && time <= now + 1000 && now - time <= maxAgeSeconds * 1000;
}

export function matchingChargingWorkflow(row: ChargingSession) {
  return row.scriptOperationId && row.workflowStatus?.operationId === row.scriptOperationId ? row.workflowStatus : undefined;
}

// Only matched, fresh worker station observations establish communication.
// A configured site, vehicle charging flag or historical completed session does not.
export function chargerMonitoringState(status?: ChargingStatus, readError = '', now = Date.now()) {
  const policy = readError ? '读取失败' : !status ? '未取得' : status.enabled ? '已启用' : '未启用';
  const scripted = status?.options.sites.some(site => site.controlMode === 'RobokitScript') ?? false;
  const unknown = {
    label: '未接入实时监测',
    tone: 'offline' as EquipmentTone,
    communication: '未接入',
    operation: '未知',
    fault: '未知',
    lastFeedback: '尚未取得',
    output: '未知', vehicleCharge: '未知', policy,
    summary: '尚未取得充电桩实时反馈，不能判断当前通信、充电或故障状态。'
  };
  if (readError) return { ...unknown, label: '状态读取失败', communication: '后台读取失败', summary: '后台充电状态读取失败，已有采样仅供回看。' };
  const rows = status?.sessions.filter(row => row.phase !== 'Completed' && row.site?.controlMode === 'RobokitScript') ?? [];
  const observations = rows.map(row => ({ row, workflow: matchingChargingWorkflow(row) }))
    .filter(item => item.workflow?.station)
    .sort((a, b) => Date.parse(b.workflow!.station!.observedAt) - Date.parse(a.workflow!.station!.observedAt));
  if (!observations.length) {
    const chargers = status?.chargers ?? [];
    const currentChargers = chargers.filter(row => chargingObservationFresh(row.observedAt, now, 5));
    if (currentChargers.length) {
      const selected = currentChargers.find(row => row.outcome !== 'connected') ?? currentChargers[0];
      if (!chargingObservationFresh(status?.capturedAt, now, 5)) return { ...unknown, label: '充电桩状态过期', communication: '后台快照非当前' };
      if (selected.outcome === 'connected') return {
        ...unknown, label: '通信已连接 · 状态待读取', tone: 'amber' as EquipmentTone,
        communication: '20207 / 20204 均可连接', operation: '待厂家只读状态协议',
        lastFeedback: new Date(selected.observedAt).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false }),
        summary: `${selected.chargerId} · ${selected.chargerName} · ${selected.address} 的通信端口在线；为避免1001控制报文改变充电模式，未把端口在线冒充工作状态、故障或充电电流。`
      };
      return {
        ...unknown, label: selected.outcome === 'offline' ? '充电桩通信中断' : '充电桩通信部分异常',
        tone: (selected.outcome === 'offline' ? 'red' : 'amber') as EquipmentTone,
        communication: selected.message, lastFeedback: new Date(selected.observedAt).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false }),
        summary: '当前只取得TCP连通检查结果，充电状态和故障仍未知。'
      };
    }
    return chargers.length
      ? { ...unknown, label: '充电桩通信数据过期', communication: '后台快照非当前' }
      : scripted ? { ...unknown, label: '等待桩端反馈', communication: '尚无本轮回包',
          summary: '已配置反光板插接流程，桩端状态等待本轮充电会话反馈；配置不代表在线或已充电。' } : unknown;
  }
  const maxAge = Math.min(5, status?.options.stateMaxAgeSeconds && status.options.stateMaxAgeSeconds > 0 ? status.options.stateMaxAgeSeconds : 5);
  const current = observations.filter(item => chargingObservationFresh(item.workflow!.updatedAt, now, maxAge)
    && chargingObservationFresh(item.workflow!.station!.observedAt, now, maxAge));
  if (!chargingObservationFresh(status?.capturedAt, now, maxAge) || !current.length) return {
    ...unknown, label: '桩端反馈过期', communication: '数据非当前',
    lastFeedback: `${new Date(observations[0].workflow!.station!.observedAt).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false })}（历史）`,
    summary: '本轮桩端反馈已过期，当前工作状态和故障均未知；保留原会话等待核对。'
  };
  // When more than one station is active, a newer normal packet must not hide an alarm.
  const selected = current.find(item => item.workflow!.station!.alarms?.length) ?? current[0];
  const workflow = selected.workflow!, station = workflow.station!;
  const alarms = Array.isArray(station.alarms) ? station.alarms : undefined;
  const review = selected.row.phase === 'Hold' || workflow.state === 'Unknown' || workflow.state === 'Failed';
  const battery = workflow.battery;
  const vehicleCharge = battery && chargingObservationFresh(battery.observedAt, now, maxAge)
    ? battery.charging === true && typeof battery.current === 'number' && Number.isFinite(battery.current) && battery.current > .1
      ? '车端状态及正电流已确认' : '尚未共同确认充电状态与正电流'
    : '车端采样非当前';
  const number = (value: number | null | undefined, unit: string) => typeof value === 'number' && Number.isFinite(value) ? `${value.toFixed(2)} ${unit}` : `— ${unit}`;
  return {
    label: alarms?.length ? '桩端报警' : review ? '反馈在线 · 待核对' : '反馈在线',
    tone: (alarms?.length ? 'red' : review || !alarms ? 'amber' : 'green') as EquipmentTone,
    communication: '已收到桩端回包',
    operation: station.charging === true ? '桩端报告充电' : station.charging === false ? '桩端未报告充电' : '桩端充电状态待核定',
    fault: alarms ? alarms.length ? alarms.join('、') : '本次反馈无报警' : '未知',
    lastFeedback: new Date(station.observedAt).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false }),
    output: `${number(station.voltage, 'V')} / ${number(station.current, 'A')}`, vehicleCharge, policy,
    summary: `${station.chargerIp} · ${selected.row.vehicleId} 的本轮桩端反馈。车端实际充电证据单独显示，停充及退桩仍由后台核验。`
  };
}
