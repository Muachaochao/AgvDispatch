import type { WebMap } from './types.ts';

export interface NavigationDetourPlan {
  available: boolean;
  message: string;
  closureRevision: number;
  mapId: string;
  versionId: string;
  definitionHash: string;
  robotMap: string;
  robotMapMd5: string;
  sourceSite: string;
  targetSite: string;
  route: string[];
  blockedEdgeIds: string[];
}

export interface NavigationDetourPreview {
  previewId: string;
  expiresAt: string;
  taskId: string;
  vehicleId: string;
  blockedSince: string | null;
  plan: NavigationDetourPlan;
}

export interface NavigationDetourRecord {
  previewId: string;
  state: string;
  message: string;
  plan: NavigationDetourPlan;
  originalVendorTaskIds: string[];
  originalRoute: string[];
  stoppedObservedAt: string | null;
  sendAttemptedAt: string | null;
  newVendorTaskIds: string[];
}

export interface NavigationAutoDetourRecord {
  state: string;
  message: string;
  updatedAt: string;
  operator: string;
  blockedSince: string | null;
  sourceSite: string | null;
  blockedToSite: string | null;
  previewId: string | null;
  planMessage: string | null;
  candidateRoute: string[];
  blockedEdgeIds: string[];
}

export interface NavigationDetourCurrent {
  taskId: string;
  vehicleId: string;
  targetSite: string;
  robotMap: string;
  robotMapMd5: string;
  state: string;
  detour?: NavigationDetourRecord | null;
  autoDetour?: NavigationAutoDetourRecord | null;
}

export function detourMapMatches(plan: NavigationDetourPlan, map: WebMap | null | undefined): boolean {
  return !!map?.definitionHash && plan.mapId === map.id && plan.versionId === map.version &&
    plan.definitionHash.toLowerCase() === map.definitionHash.toLowerCase();
}

export function detourPreviewCurrent(preview: NavigationDetourPreview | null, current: NavigationDetourCurrent | null,
  map: WebMap | null | undefined, now: number): boolean {
  const mayReview = !current?.detour || (!current.detour.sendAttemptedAt && ['ReviewHold', 'Ready'].includes(current.detour.state));
  return !!preview && !!current && mayReview && preview.taskId === current.taskId && preview.vehicleId === current.vehicleId &&
    preview.plan.robotMap === current.robotMap && preview.plan.robotMapMd5 === current.robotMapMd5 &&
    preview.plan.targetSite === current.targetSite && detourMapMatches(preview.plan, map) &&
    Number.isFinite(Date.parse(preview.expiresAt)) && Date.parse(preview.expiresAt) > now;
}

export function detourCanContinue(current: NavigationDetourCurrent | null, map: WebMap | null | undefined): boolean {
  return !!current && current.state === 'DetourReady' && current.detour?.state === 'Ready' &&
    current.detour.plan.available && detourMapMatches(current.detour.plan, map) && !current.detour.sendAttemptedAt;
}

export const detourStateLabels: Record<string, string> = {
  CancelIntent: '已记录结束原导航的意图', CancelRequested: '等待原导航终结',
  AwaitingOriginalTerminal: '等待原导航终结与停稳核验', Ready: '停稳核验通过，等待确认执行',
  ReviewHold: '等待人工复核', SendIntent: '已记录改道发送意图', Executing: '查询改道导航执行状态',
  Uncertain: '改道状态不确定，等待查询原任务',
  Completed: '改道导航已结束并核对到点', Failed: '改道导航失败', Cancelled: '改道导航已取消'
};

export const autoDetourStateLabels: Record<string, string> = {
  WaitingForBlock: '等待连续阻挡证据', Planning: '正在规划备用路线', Unavailable: '没有执行自动改道',
  CancelIntent: '已生成候选，准备结束原导航', CancellationRequested: '等待原导航终结',
  CancelRequested: '取消待核实，继续查询原任务', CancelBlocked: '结束原导航前核验未通过',
  AwaitingOriginalTerminal: '等待原导航终结与停稳核验', ReadyToSend: '停稳核验通过，准备发送备选路线',
  Executing: '查询备选导航执行状态', SendBlocked: '发送前核验未通过', ReviewHold: '等待人工复核',
  Uncertain: '状态不确定，继续查询原任务', Completed: '已完成并核对到点', Failed: '导航失败，等待现场核对',
  Cancelled: '已取消并核对停稳'
};
