export interface TaskSwitchIntent {
  requestId: string;
  taskId: string;
  expectedStage: string;
  targetStationId: string;
  priority: number;
  reason: string;
  siteConfirmed: boolean;
  mode?: 'Auto' | 'CancelEmptyNavigation' | 'CompleteBusinessReturn';
}

export interface TaskSwitchRecord {
  switchId: string;
  sourceTaskId: string;
  vehicleId: string;
  targetStationId: string;
  targetTaskId?: string | null;
  stage: string;
  message: string;
  blockers: string[];
  revision: number;
  updatedAt: string;
  reason?: string;
  priority?: number;
  mapId: string;
  mapVersionId: string;
  parameterRevision: string;
  replacementRequestId: string;
  cancelRequestId: string;
  request?: TaskSwitchIntent;
  resumeStage?: string | null;
  resolvedMode?: 'Auto' | 'CancelEmptyNavigation' | 'CompleteBusinessReturn';
}

export interface TaskSwitchOptions {
  enabled: boolean;
  eligible: boolean;
  message: string;
  vehicleId: string | null;
  stations: { stationId: string; pickupSite: string; available: boolean; explanation: string }[];
  blockers: string[];
  current: TaskSwitchRecord | null;
  modeHint?: 'Auto' | 'CancelEmptyNavigation' | 'CompleteBusinessReturn';
  modeDescription?: string;
  handoffReadySites?: string[];
  handoffMessage?: string;
}

export interface TaskSwitchResult {
  accepted: boolean;
  message: string;
  current: TaskSwitchRecord | null;
}

const names: Record<string, string> = {
  Requested: '已记录换单申请', AwaitingStop: '等待原任务结束与停稳', ClosingOriginal: '核对原业务收尾',
  AwaitingBusinessCleanup: '完成原业务返箱收尾',
  ReadyToSwitch: '换单条件已满足', SubmittingReplacement: '核对新任务受理', Completed: '换单完成',
  ReviewHold: '暂停待核对', Suspended: '换单已挂起', Abandoned: '已停止换单',
};

export const taskSwitchStageName = (stage: string): string => names[stage] ?? `未知阶段（${stage}）`;
export const taskSwitchTerminal = (stage: string): boolean => stage === 'Completed' || stage === 'Abandoned';
export const taskSwitchStorageKey = (taskId: string): string => `agv-task-switch-intent-v1:${taskId}`;

export function taskSwitchIntentError(value: TaskSwitchIntent): string | null {
  if (!/^[a-f0-9]{32}$/i.test(value.requestId) || !value.taskId || value.taskId.length > 64 || !value.expectedStage || value.expectedStage.length > 64)
    return '换单申请编号或原任务信息无效。';
  if (!value.targetStationId?.trim() || value.targetStationId.length > 32)
    return '请选择后台配置中的目标工位。';
  if (!Number.isInteger(value.priority) || value.priority < 0 || value.priority > 100)
    return '优先级应为 0～100 的整数。';
  if (typeof value.reason !== 'string' || value.reason.trim().length < 10 || value.reason.trim().length > 1000)
    return '请填写至少10字、最多1000字的换单依据。';
  if (value.siteConfirmed !== true) return '请核对并勾选本次车辆箱体责任及现场条件。';
  if (value.mode !== undefined && !['Auto', 'CancelEmptyNavigation', 'CompleteBusinessReturn'].includes(value.mode))
    return '换单执行方式无效。';
  return null;
}

export function parseTaskSwitchIntent(raw: string | null, taskId: string): TaskSwitchIntent | null {
  if (raw === null) return null;
  const candidate: unknown = JSON.parse(raw);
  if (!candidate || typeof candidate !== 'object') throw new Error('原换单申请记录损坏，请先核对后台记录。');
  const value = candidate as TaskSwitchIntent;
  if (typeof value.taskId !== 'string' || typeof value.requestId !== 'string' || typeof value.expectedStage !== 'string' ||
      typeof value.targetStationId !== 'string' || value.taskId !== taskId || taskSwitchIntentError(value))
    throw new Error('原换单申请记录不完整或归属不符，请先核对后台记录。');
  return value;
}

// A timeout never authorizes changing the payload or creating another request ID.
export function retainTaskSwitchIntent(previous: TaskSwitchIntent | null, draft: TaskSwitchIntent): TaskSwitchIntent {
  const error = taskSwitchIntentError(draft);
  if (error) throw new Error(error);
  const normalized = { ...draft, reason: draft.reason.trim() };
  if (previous) {
    if (previous.requestId !== normalized.requestId || previous.taskId !== normalized.taskId ||
        previous.expectedStage !== normalized.expectedStage || previous.targetStationId !== normalized.targetStationId ||
        previous.priority !== normalized.priority || previous.reason !== normalized.reason || previous.siteConfirmed !== normalized.siteConfirmed ||
        (previous.mode ?? 'Auto') !== (normalized.mode ?? 'Auto'))
      throw new Error('原换单申请结果未确认，不能修改参数或更换申请编号。请查询原申请。');
    return previous;
  }
  return normalized;
}

export function observeTaskSwitchIntent(intent: TaskSwitchIntent, record: TaskSwitchRecord | null): 'Accepted' | 'Missing' | 'Mismatch' {
  if (!record) return 'Missing';
  const original = record.request;
  return record.switchId === intent.requestId && record.sourceTaskId === intent.taskId &&
    record.targetStationId === intent.targetStationId && (original?.priority ?? record.priority) === intent.priority &&
    (original?.reason ?? record.reason) === intent.reason && (!original || original.requestId === intent.requestId &&
      original.taskId === intent.taskId && original.targetStationId === intent.targetStationId &&
      original.expectedStage === intent.expectedStage && original.siteConfirmed === intent.siteConfirmed &&
      (original.mode ?? 'Auto') === (intent.mode ?? 'Auto'))
    ? 'Accepted' : 'Mismatch';
}

export const taskSwitchSteps = [
  { key: 'Requested', name: '记录申请' },
  { key: 'AwaitingStop', name: '原任务收尾' },
  { key: 'ClosingOriginal', name: '原单收尾' },
  { key: 'ReadyToSwitch', name: '条件核验' },
  { key: 'SubmittingReplacement', name: '创建新单' },
  { key: 'Completed', name: '切换完成' },
] as const;

export function taskSwitchProgress(stage: string): number {
  if (stage === 'AwaitingBusinessCleanup') return 1;
  return taskSwitchSteps.findIndex(item => item.key === stage);
}
