export type PipelineLayerKey = 'queue' | 'candidates' | 'matching' | 'traffic' | 'safety' | 'execution';
export interface PipelineGate { key: string; label: string; enabled: boolean | null; detail: string }
export interface PipelineMetric { label: string; value: string; unit?: string | null }
export interface PipelineCheck {
  key: string; label: string; state: string; actual: string; expected: string; reason: string; observedAt?: string | null;
}
export interface PipelineRecord {
  id: string; taskId?: string | null; stationId?: string | null; vehicleId?: string | null; resourceId?: string | null;
  state: string; title: string; reason: string; updatedAt?: string | null; link?: string | null;
  facts: { label: string; value: string }[];
}
export interface PipelineInspectionLayer {
  key: string; number: number; name: string; state: string; summary: string; observedAt?: string | null;
  metrics: PipelineMetric[]; checks: PipelineCheck[]; records: PipelineRecord[];
  totalCount: number; truncated: boolean; note: string;
}
export interface PipelineInspectionSnapshot {
  capturedAt: string; cycleNumber: number; optimizationCapturedAt?: string | null; mode: string; summary: string;
  gates: PipelineGate[]; layers: PipelineInspectionLayer[];
}
export interface PipelineObservation { id: string; snapshot: PipelineInspectionSnapshot }
export interface PipelineClock {
  originUnixMs?: number | null;
  rate?: number | null;
}

export const pipelineDefinitions = [
  { key: 'queue', number: 1, name: 'PLC 呼叫队列', question: '有哪些呼叫正在排队？', input: '工位呼叫与 PLC 应答', output: '保留原编号、原时间的等待任务', link: '/tasks', linkLabel: '业务任务' },
  { key: 'candidates', number: 2, name: '候选筛选', question: '哪些车辆能接，哪些不能？', input: '等待任务与车辆实时状态', output: '通过硬约束的任务—车辆候选', link: '/vehicles', linkLabel: '车辆列表' },
  { key: 'matching', number: 3, name: '滚动全局匹配', question: '本轮为什么这样分配？', input: '当前窗口与可行候选图', output: '全局推荐、传统选择与实际登记', link: '/dispatch-optimization', linkLabel: '优化策略与成本' },
  { key: 'traffic', number: 4, name: '交通路权', question: '资源被谁占着，谁在等？', input: '候选路线与公共资源容量', output: '原步骤实际路权或等待原因', link: '/resource-review', linkLabel: '公共资源与交接' },
  { key: 'safety', number: 5, name: '安全复核', question: '发送前哪些条件还没满足？', input: '地图、车辆、路权及 PLC 条件', output: '最近实际复核结果与当前门禁', link: '/commissioning', linkLabel: '现场联调检查' },
  { key: 'execution', number: 6, name: '执行与恢复', question: '命令到哪一步，是否在重试？', input: '原任务步骤与持久化命令', output: '原命令发送、反馈及恢复状态', link: '/recovery', linkLabel: '执行与恢复记录' },
] as const;

export function pipelineLayerKey(value: unknown): PipelineLayerKey {
  return pipelineDefinitions.find(row => row.key === value)?.key ?? 'queue';
}

export function pipelineTime(value?: string | null): string {
  const stamp = value ? Date.parse(value) : Number.NaN;
  return Number.isFinite(stamp) && stamp > 0
    ? new Date(stamp).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false }) : '尚无记录';
}

export function pipelineTimestampFresh(stamp: string | null | undefined, reference: number, maxAge = 15000): boolean {
  const time = stamp ? Date.parse(stamp) : Number.NaN;
  return Number.isFinite(reference) && Number.isFinite(time) && time > 0 && time <= reference + 1000 && reference - time < maxAge;
}

export function pipelineBusinessReference(wallNow: number, clock?: PipelineClock | null) {
  const origin = clock?.originUnixMs, rate = clock?.rate;
  if (!Number.isFinite(wallNow) || !Number.isFinite(origin) || !Number.isFinite(rate) ||
      origin! <= 0 || rate! < 1 || rate! > 8 || wallNow < origin!)
    return wallNow;
  return origin! + (wallNow - origin!) * rate!;
}

export function pipelineSimulationCaptureValid(
  capturedAt: string | null | undefined,
  wallNow: number,
  clock?: PipelineClock | null,
  maxAge = 30_000,
) {
  if (!clock || !Number.isFinite(clock.originUnixMs) || !Number.isFinite(clock.rate))
    return pipelineTimestampFresh(capturedAt, wallNow, maxAge);
  return pipelineTimestampFresh(capturedAt, pipelineBusinessReference(wallNow, clock), maxAge);
}

const knownStates: Record<string, { label: string; tone: string }> = {
  Ready: { label: '已就绪', tone: 'ready' }, Running: { label: '运行中', tone: 'running' },
  Waiting: { label: '等待中', tone: 'waiting' }, Blocked: { label: '已阻止', tone: 'blocked' },
  Disabled: { label: '未启用', tone: 'disabled' }, Observing: { label: '影子观察', tone: 'observing' },
  Idle: { label: '空闲', tone: 'idle' }, Unknown: { label: '未取得状态', tone: 'unknown' },
  Stale: { label: '观察已过期', tone: 'stale' }, Pass: { label: '通过', tone: 'ready' },
  Fail: { label: '不满足', tone: 'blocked' }, NotChecked: { label: '尚未复核', tone: 'unknown' }
};

export function pipelineEvidenceState(state: string, observedAt: string | null | undefined, fresh: boolean, reference: number) {
  const known = knownStates[state];
  if (!known) return { label: /[\u3400-\u9fff]/.test(state) ? state : '状态待核对', tone: 'unknown' };
  if (!fresh) return { label: '当前连接或采集已失效', tone: 'stale' };
  if (['Unknown', 'NotChecked', 'Stale'].includes(state)) return known;
  if (!pipelineTimestampFresh(observedAt, reference)) return { label: observedAt ? '观察已过期或时间无效' : '尚无有效观察', tone: 'stale' };
  return known;
}

export function pipelineLayerViews(snapshot: PipelineInspectionSnapshot | undefined, fresh: boolean, reference: number) {
  return pipelineDefinitions.map(definition => {
    const matches = snapshot?.layers?.filter(row => row.key === definition.key) ?? [];
    const raw = matches.length === 1 ? matches[0] : undefined;
    const observed = raw ? { ...raw, metrics: Array.isArray(raw.metrics) ? raw.metrics : [], checks: Array.isArray(raw.checks) ? raw.checks : [],
      records: Array.isArray(raw.records) ? raw.records.map(record => ({ ...record, facts: Array.isArray(record.facts) ? record.facts : [] })) : [] } : undefined;
    const status = observed ? pipelineEvidenceState(observed.state, observed.observedAt, fresh, reference)
      : { label: matches.length > 1 ? '观察记录重复' : '未取得状态', tone: 'unknown' };
    return { ...definition, observed, status };
  });
}

export function retainPipelineObservation(history: PipelineObservation[], snapshot: PipelineInspectionSnapshot): PipelineObservation[] {
  // Each poll has a distinct capture time even if optimization is disabled or its cycle number did not change.
  const copy = JSON.parse(JSON.stringify(snapshot)) as PipelineInspectionSnapshot;
  return [{ id: snapshot.capturedAt, snapshot: copy }, ...history.filter(row => row.id !== snapshot.capturedAt)].slice(0, 60);
}

const recordLabels: Record<string, string> = {
  Created: '等待 PLC 应答', PendingAcknowledgement: '等待 PLC 应答', WaitingForVehicle: '等待派车', CommandPending: '命令待发送',
  Pending: '待处理', RetryScheduled: '等待原命令重试', Sending: '已领取发送', Sent: '厂家已确认',
  SubmittingToAgv: '车辆命令提交中', GoingToPickup: '前往取货点', PickingUp: '取箱中',
  GoingToDumper: '前往倒料区', WaitingForDumper: '等待倒料位', Dumping: '倒料中', ReturningEmptyBox: '返还空箱中',
  WaitingForPlcConfirmation: '等待 PLC 确认', Completed: '已完成', Cancelled: '已取消', Faulted: '故障',
  Failed: '失败', Rejected: '已拒绝', Acquired: '已取得路权', Held: '保留占用', Retained: '异常保留占用',
  NotRequired: '无需公共资源', Released: '已释放', PassedPendingRelease: '通过待原子释放',
  Eligible: '可参与候选', Excluded: '已排除',
  Online: '在线', Offline: '离线', NotChecked: '尚未复核', Unknown: '未取得状态',
  Observed: '当前状态采集', Recommended: '仅推荐 · 尚未登记', Prepared: '原命令已登记',
  Full: '资源容量已满', Available: '当前容量可用', FullBoxWaitingForPickup: '满箱等待取走',
  PickupInProgress: '取箱中', WaitingForEmptyBox: '等待空箱', EmptyBoxPlacementInProgress: '空箱放置中',
  SignalConflict: '信号冲突', ManualIntervention: '等待人工处理', Occupied: '已有任务或路权责任',
  CancellationPending: '取消处理中', CancellationReview: '取消待复核', Executing: '执行中'
};
export function pipelineRecordState(state: string) {
  const base = knownStates[state];
  const issue = pipelineRecordIsIssue({ state } as PipelineRecord);
  return { label: recordLabels[state] ?? base?.label ?? (/[\u3400-\u9fff]/.test(state) ? state : '状态待核对'), tone: issue ? 'waiting' : 'neutral' };
}
export function pipelineRecordIsIssue(record: PipelineRecord): boolean {
  return /waiting|blocked|fail|fault|retry|pending|retained|stale|unknown|offline|excluded|rejected|notchecked|signalconflict|manualintervention|cancellationreview/i.test(record.state) || record.state === 'Full';
}
export function filterPipelineRecords(records: PipelineRecord[], search: string, onlyIssues: boolean): PipelineRecord[] {
  const terms = search.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
  return records.filter(record => {
    if (onlyIssues && !pipelineRecordIsIssue(record)) return false;
    const text = [record.id, record.taskId, record.stationId, record.vehicleId, record.resourceId, record.title, record.reason, record.state,
      ...record.facts.flatMap(fact => [fact.label, fact.value])].join(' ').toLocaleLowerCase();
    return terms.every(term => text.includes(term));
  });
}
export function pipelineInternalLink(value?: string | null): string | undefined {
  return value?.startsWith('/') && !value.startsWith('//') && !/[\\\r\n\u0000]/.test(value) ? value : undefined;
}

export function pipelineExecutionSummary(gates: PipelineGate[], fresh: boolean): string {
  if (!fresh) return '未取得当前执行开关';
  const dispatch = gates.find(gate => gate.key === 'dispatch');
  const outbox = gates.find(gate => gate.key === 'outbox');
  if (dispatch?.enabled === false || outbox?.enabled === false) return '查看模式 · 自动派车链未全部启用';
  if (dispatch?.enabled === true && outbox?.enabled === true) return '派车与发送开关已开启 · 逐单仍须复核';
  return '执行开关状态不完整';
}
