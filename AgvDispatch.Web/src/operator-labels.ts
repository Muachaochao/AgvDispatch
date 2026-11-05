const signalLabels: Record<string, string> = {
  FullBoxRequested: '满箱呼叫',
  BoxPresent: '箱体检测',
  Faulted: '设备故障',
  CommunicationOnline: '下游设备在线',
  FullBoxRequestAcknowledged: '呼叫应答',
  FullBoxRemoved: '满箱已取走',
  EmptyBoxTaskAcknowledged: '返空箱任务应答',
  EmptyBoxPlaced: '空箱已放到位',
  AgvAtWaitingPosition: '车辆已到等待位',
  AgvAtDumpingPosition: '车辆已到倒料位',
  DumperReady: '倒料机就绪许可',
  DumpingCompletedMayLeave: '倒料完成，允许离开'
};

export const operatorSignalOptions = Object.freeze(Object.entries(signalLabels).map(([value, label]) => ({ value, label })));

const phaseLabels: Record<string, string> = {
  BaselineCaptured: '已记录初始状态',
  TransportBound: '运输任务已绑定',
  ReadyApplied: '已写入就绪许可',
  ReadyConsumed: '就绪许可已接收',
  DumpingApplied: '已写入车辆到位通知',
  DumpingCompleted: '倒料完成条件已满足',
  ReadyToComplete: '等待本步骤收尾',
  CompletionSaved: '收尾状态已保存',
  ReturnBound: '返程任务已绑定',
  ExitVerified: '车辆已确认离开作业区',
  ExitHandoverComplete: '车辆已出区，设备和区域已交接',
  HandoverComplete: '设备和区域已交接'
};

const actionStateLabels: Record<string, string> = {
  Accepted: '已受理，等待执行',
  Assigned: '已分配，等待执行',
  Waiting: '等待执行',
  Running: '正在执行',
  Sending: '正在发送',
  Sent: '厂家已确认',
  Pending: '等待处理',
  Returning: '正在返回',
  Suspended: '已暂停',
  Completed: '已完成并核对到点',
  Failed: '执行失败',
  Faulted: '设备故障',
  Cancelled: '已取消',
  CancelRequested: '取消请求待核对',
  Uncertain: '结果不确定，禁止重发',
  Rejected: '请求已拒绝',
  ObservedTarget: '已观察目标限位',
  Stopped: '输出已关闭，请现场确认停稳'
};

const qualityLabels: Record<string, string> = {
  Good: '正常',
  Bad: '异常',
  Stale: '已过期',
  Unknown: '未知',
  Disabled: '未启用',
  Context: '配置记录'
};

const runtimeKindLabels: Record<string, string> = {
  Signals: 'PLC 信号观察',
  Signal: 'PLC 信号观察',
  HandshakeTransition: '握手阶段变化',
  HandshakeReviewed: '握手人工核对',
  ExitVerified: '车辆实际出区核对',
  EquipmentClaimed: '设备归属申请',
  EquipmentReleased: '设备归属释放',
  TaskCreated: '任务已创建',
  PlcWriteIntent: 'PLC 写入意图',
  PlcWriteAcknowledged: 'PLC 写入调用返回',
  PlcWriteReadback: 'PLC 写入回读',
  CommandSent: '车辆命令已发送',
  StepStarted: '步骤开始',
  StepCompleted: '步骤完成',
  StepFaulted: '步骤异常'
};

const vehicleActionLabels: Record<string, string> = {
  Up: '上升',
  Down: '下降',
  JackLoad: '顶升取箱',
  JackUnload: '下降放箱',
  ForkLoad: '货叉取箱',
  ForkUnload: '货叉放箱',
  Wait: '仅导航，不执行机械动作',
  Dock: '低速对接',
  Undock: '低速退出',
  Charge: '启动充电',
  StopCharge: '停止充电'
};

const engineeringOptionLabels: Record<string, string> = {
  Read: '读取',
  Write: '写入',
  Bool: '布尔量',
  Jack: '顶升式车辆',
  Fork: '货叉式车辆',
  Intersection: '交叉口',
  NarrowAisle: '窄道',
  Station: '工位',
  Waiting: '等待位',
  Dumper: '倒料区',
  Standby: '待机位'
};

const equipmentDirectionLabels: Record<string, string> = {
  Up: '上升',
  Down: '下降',
  Stop: '停止',
  Stopped: '已停止',
  Opening: '正在打开',
  Closing: '正在关闭',
  Open: '已打开',
  Closed: '已关闭'
};

function hasChinese(value: string): boolean {
  return /[\u3400-\u9fff]/.test(value);
}

export function signalLabel(variableName: string | null | undefined, fallback = ''): string {
  if (!variableName) return hasChinese(fallback) ? fallback : '未知信号';
  return signalLabels[variableName] ?? (hasChinese(fallback) ? fallback : '未识别信号');
}

export function handshakePhaseLabel(phase: string | null | undefined): string {
  if (!phase) return '尚未进入握手阶段';
  return phaseLabels[phase] ?? (hasChinese(phase) ? phase : '握手阶段待核对');
}

export function actionStateLabel(state: string | null | undefined): string {
  if (!state) return '状态未知';
  return actionStateLabels[state] ?? (hasChinese(state) ? state : '状态待核对');
}

export function qualityLabel(quality: string | null | undefined): string {
  if (!quality) return '未知';
  return qualityLabels[quality] ?? '状态待核对';
}

export function failurePolicyLabel(value: string | null | undefined): string {
  if (!value) return '未配置';
  return ({ HoldUntilCondition: '保持并等待条件', Retry: '允许受控重试', Fail: '直接判定失败' } as Record<string, string>)[value] ?? '策略待核对';
}

export function timeoutActionLabel(value: string | null | undefined): string {
  if (!value) return '未配置';
  return ({ NotifyAndHold: '通知并暂停等待核对', Cancel: '尝试取消', Ignore: '仅记录不自动处理' } as Record<string, string>)[value] ?? '动作待核对';
}

export function alarmSourceLabel(sourceType: string | null | undefined): string {
  if (!sourceType) return '未知对象';
  return ({ AGV: '车辆', PLC: 'PLC', TRAFFIC: '交通资源', Traffic: '交通资源', TrafficResource: '交通资源',
    TrafficLease: '交通路权', TASK: '业务任务', Task: '业务任务' } as Record<string, string>)[sourceType]
    ?? (hasChinese(sourceType) ? sourceType : '其他来源');
}

export function alarmTitleText(value: string | null | undefined): string {
  if (!value) return '未提供告警名称';
  const normalized = value.replace(/PLC存在(\d+)个Stale质量点位/g, 'PLC 有 $1 个数据质量过期点位');
  return industrialText(normalized)
    .replace(/\bInterrupted\b/g, '通信中断')
    .replace(/\bConnecting\b/g, '正在连接')
    .replace(/\bConnected\b/g, '已连接')
    .replace(/\bBlocked\b/g, '车辆受阻')
    .replace(/\bCritical\b/g, '严重')
    .replace(/\bError\b/g, '错误');
}

export function alarmMessageText(value: string | null | undefined): string {
  if (!value) return '未提供告警说明';
  if (/^Caution:\s*robot is blocked\.?$/i.test(value.trim()))
    return '车端提示：车辆当前受阻。障碍移除后继续核对原任务，不能仅凭提示解除恢复。';
  if (/^The operation was canceled\.?$/i.test(value.trim()))
    return '厂家接口提示：本次操作已取消。';
  if (/^The requested operation requires an element of type 'Number', but the target element has type 'Null'\.?$/i.test(value.trim()))
    return '厂家接口返回了空数值，无法完成本次操作。请核对车辆采样、原任务和连接状态。';
  if (/robot is slipping/i.test(value))
    return '车端提示：车辆出现打滑。厂家已记录本次速度与偏差参数，请结合当前位置、载荷和路线核对。';
  const normalized = value.replace(/PLC存在(\d+)个Stale质量点位/g, 'PLC 有 $1 个数据质量过期点位');
  return industrialText(normalized)
    .replace(/\bInterrupted\b/g, '通信中断')
    .replace(/\bConnecting\b/g, '正在连接')
    .replace(/\bConnected\b/g, '已连接')
    .replace(/\bBlocked\b/g, '受阻')
    .replace(/\bStale\b/g, '数据过期')
    .replace(/Socket Exception\s*->\s*/gi, '网络连接异常：')
    .replace(/\bException\b/g, '异常');
}

export function contractLabel(value: string | null | undefined): string {
  if (!value) return '未配置握手契约';
  return ({
    'plc-28-stage-v2': 'PLC 28 阶段统一执行契约（第 2 版）',
    'sender-owned-return-confirmed-v1': '旧版返程确认契约'
  } as Record<string, string>)[value] ?? (hasChinese(value) ? value : '自定义握手契约');
}

export function observedPhaseLabel(value: string | null | undefined): string {
  if (!value) return '后台未提供执行阶段';
  const match = value.match(/(?:持久化阶段|phase)\s*([A-Za-z][A-Za-z0-9_]*)/i);
  if (match) return handshakePhaseLabel(match[1]);
  return hasChinese(value) ? value : handshakePhaseLabel(value);
}

export function vehicleActionLabel(value: string | null | undefined): string {
  if (!value) return '未指定车辆动作';
  return vehicleActionLabels[value] ?? (hasChinese(value) ? value : '车辆动作待核对');
}

export function runtimeKindLabel(value: string | null | undefined): string {
  if (!value) return '运行事件';
  return runtimeKindLabels[value] ?? (hasChinese(value) ? value : '其他运行事件');
}

export function engineeringOptionLabel(value: string | null | undefined): string {
  if (!value) return '未设置';
  if (signalLabels[value]) return signalLabels[value];
  if (vehicleActionLabels[value]) return vehicleActionLabels[value];
  return engineeringOptionLabels[value] ?? (hasChinese(value) ? value : '配置值待核对');
}

export function engineeringFieldValueLabel(fieldKey: string | null | undefined, value: unknown): string {
  if (fieldKey === 'name') return signalLabel(String(value ?? ''), String(value ?? ''));
  if (fieldKey === 'direction' || fieldKey === 'dataType' || fieldKey === 'capability' || fieldKey === 'kind')
    return engineeringOptionLabel(String(value ?? ''));
  return value === null || value === undefined || value === '' ? '未设置' : String(value);
}

export function equipmentDirectionLabel(value: string | null | undefined): string {
  if (!value) return '未移动';
  return equipmentDirectionLabels[value] ?? (hasChinese(value) ? value : '方向待核对');
}

export function mapSwitchModeLabel(value: string | null | undefined): string {
  if (!value) return '状态待读取';
  return ({ Held: '已冻结后续执行', Running: '切换进行中', Completed: '切换已完成', Cancelled: '切换已取消' } as Record<string, string>)[value]
    ?? (hasChinese(value) ? value : '切换模式待核对');
}

export function industrialText(value: unknown): string {
  if (value === null || value === undefined || value === '') return '未提供说明';
  if (typeof value === 'boolean') return value ? '1' : '0';
  if (typeof value === 'number') return Number.isFinite(value) ? String(value) : '数值无效，待核对';
  if (Array.isArray(value)) return `共 ${value.length} 项核对记录，详情见下载报告。`;
  if (typeof value !== 'string') return '结构化核对记录已保存，详情见下载报告。';
  const versionMismatch = /工程版本摘要不一致/.test(value);
  let text = value;
  for (const [key, label] of Object.entries(signalLabels).sort((a, b) => b[0].length - a[0].length))
    text = text.replace(new RegExp(`\\b${key}\\b`, 'g'), label);
  const result = text
    .replace(/=(True|False)/g, (_, raw: string) => `=${raw === 'True' ? '1' : '0'}`)
    .replace(/\b(Good|Normal)\b/g, '正常')
    .replace(/\bBad\b/g, '异常')
    .replace(/\bStale\b/g, '已过期')
    .replace(/\bUnknown\b/g, '未知')
    .replace(/\bUnavailable\b/g, '未取得')
    .replace(/\bDisabled\b/g, '未启用')
    .replace(/\bContext\b/g, '配置记录')
    .replace(/\bDumperExitWaiting\b/g, '等待倒料区出区核对')
    .replace(/\bDumperExitVerified\b/g, '倒料区出区核对通过')
    .replace(/\bPLC-REQUEST-ACK\b/g, 'PLC 呼叫应答')
    .replace(/\bCompleted\b/g, '已完成')
    .replace(/\bFailed\b/g, '失败')
    .replace(/\bCancelled\b/g, '已取消')
    .replace(/\bPending\b/g, '待处理')
    .replace(/\bPass\b/g, '满足')
    .replace(/\bWait\b/g, '等待')
    .replace(/\bGlobal\b/g, '全局匹配')
    .replace(/\bShadow\b/g, '影子观察')
    .replace(/\bObserving\b/g, '观察中')
    .replace(/\bRunning\b/g, '运行中')
    .replace(/\bBlocked\b/g, '已阻止')
    .replace(/\bFaulted\b/g, '故障');
  return versionMismatch
    ? `${result}。先刷新工程草稿和版本；仍失败时核对 5199 发布目录、WebHost 与主服务发布批次，不能沿用旧页面继续保存或应用。`
    : result;
}
