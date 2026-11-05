import type { Snapshot, Vehicle } from './types.ts';

export function isFresh(snapshot: Snapshot | null, now = Date.now()): boolean {
  if (!snapshot?.connected) return false;
  const age = now - Date.parse(snapshot.receivedAt);
  return Number.isFinite(age) && age >= -1000 && age <= 7000;
}

export function mayPlot(snapshot: Snapshot, vehicle: Vehicle, now = Date.now()): boolean {
  const age = now - Date.parse(vehicle.lastSuccessfulPollAt ?? '');
  return isFresh(snapshot, now) && snapshot.map?.unit === 'm' &&
    snapshot.map.coordinateSystem !== 'SmapWorldTranslated' &&
    snapshot.positionedVehicleIds.includes(vehicle.vehicleId) && vehicle.connection === 'Online' && vehicle.quality === 'Good' &&
    vehicle.mapPositionState === 'Normal' && vehicle.localizationConfidence !== null && vehicle.localizationConfidence >= 0.5 &&
    vehicle.x !== null && Number.isFinite(vehicle.x) && vehicle.y !== null && Number.isFinite(vehicle.y) &&
    Number.isFinite(age) && age >= -1000 && age <= 5000;
}

export const stages: Record<string, string> = {
  Created: '已创建', WaitingForVehicle: '等待派车', SubmittingToAgv: '正在下发', Assigned: '已派车',
  Executing: '执行中', Running: '执行中', Completed: '已完成', Faulted: '异常待核对', Cancelled: '已取消',
  GoToPickup: '前往取货', PickupFullBox: '确认取走满箱', DeliverFullBoxToDumper: '前往等待位',
  WaitForDumperReady: '等待倒料机就绪', EnterDumpingPosition: '进入倒料位', DumpFullBox: '等待倒料完成',
  ReturnEmptyBox: '返回空箱', PlaceEmptyBox: '确认放箱', CompletePlcHandshake: '收尾握手',
  Pending: '待执行', Waiting: '等待', Succeeded: '已完成', Failed: '失败', Online: '在线', Offline: '离线',
  Good: '有效', Bad: '异常', Stale: '已过期', Disabled: '未启用', Unknown: '未知', Connected: '已连接',
  GoingToPickup: '前往取货', PickingUp: '取箱确认', GoingToDumper: '前往倒料机', WaitingForDumper: '等待倒料机',
  Dumping: '倒料中', ReturningEmptyBox: '返空箱', WaitingForPlcConfirmation: '等待PLC确认',
  CancellationPending: '取消确认中', CancellationReview: '取消待核对', Ready: '条件就绪', CommandPending: '命令待发',
  Sending: '发送中', RetryScheduled: '等待核对重试', ManualIntervention: '需人工处置'
};
export const label = (value: string | null | undefined) => value
  ? (stages[value] ?? (/[\u3400-\u9fff]/.test(value) ? value : '状态待核对'))
  : '—';

export const isRunningStage = (stage: string) => ['Executing', 'Running', 'GoingToPickup', 'PickingUp', 'GoingToDumper',
  'WaitingForDumper', 'Dumping', 'ReturningEmptyBox', 'WaitingForPlcConfirmation', 'SubmittingToAgv'].includes(stage);

export const isWaitingStage = (stage: string) => ['Created', 'WaitingForVehicle', 'WaitingForDumper',
  'WaitingForPlcConfirmation', 'RetryScheduled'].includes(stage);
