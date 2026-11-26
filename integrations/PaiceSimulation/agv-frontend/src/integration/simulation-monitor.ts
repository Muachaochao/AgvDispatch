import { dispatchWaitBlockers, dispatchWaitDuration, isReferenceRoute, type DispatchCar, type DispatchTaskDetail, type DispatchWaitRequest, type SourceMap } from './dispatch-adapter.ts'
import type { CallRow, DispatchChargingSession, DispatchStatus, ResourceRow } from './dispatch-api.ts'

export const businessStepNames: Record<string, string> = {
  GoToPickup: '前往工位取满箱', PickupFullBox: '取满箱确认',
  DeliverFullBoxToDumper: '运送满箱到等待位', WaitForDumperReady: '等待倒料许可',
  EnterDumpingPosition: '进入倒料位', DumpFullBox: '倒料确认',
  ReturnEmptyBox: '返回工位', PlaceEmptyBox: '放回空箱确认', CompletePlcHandshake: 'PLC 握手收尾',
}
const stepsByNumber = Object.values(businessStepNames)

export function businessStepLabel(row: { stepNumber?: number; stepType?: string; stepName?: string }) {
  return row.stepNumber ? row.stepName || businessStepNames[row.stepType ?? ''] || stepsByNumber[row.stepNumber - 1] || '' : ''
}

export function elapsedLabel(seconds?: number | null) {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return '—'
  const whole = Math.floor(seconds)
  const hours = Math.floor(whole / 3600), minutes = Math.floor(whole / 60) % 60
  return `${hours ? `${hours}时` : ''}${hours || minutes ? `${minutes}分` : ''}${whole % 60}秒`
}

export function secondsSince(time?: string | null, now = Date.now()) {
  const start = Date.parse(time ?? '')
  return Number.isFinite(start) && start <= now ? (now - start) / 1000 : undefined
}

export function runTiming(status?: Pick<DispatchStatus, 'simulationRate' | 'device' | 'scenario' | 'report'> | null) {
  const { device, scenario, report, simulationRate } = status ?? {}
  const rate = typeof simulationRate === 'number' && Number.isFinite(simulationRate) && simulationRate >= 1
    ? simulationRate : undefined
  const matchingReport = scenario?.id ? report?.id === scenario.id
    : scenario?.startedAt != null ? scenario.startedAt === report?.startedAt : true
  const start = scenario?.startedAt ?? (matchingReport ? report?.startedAt : undefined)
  const end = (matchingReport ? report?.finishedAt : undefined) ?? device?.time
  const businessSeconds = typeof start === 'number' && typeof end === 'number' &&
    Number.isFinite(start) && Number.isFinite(end) && end >= start ? end - start : undefined
  return { rate, businessSeconds, wallSeconds: rate && businessSeconds !== undefined ? businessSeconds / rate : undefined }
}

export function briefReason(text?: string) {
  if (!text) return ''
  if (/必要公共资源尚未可用|滚动窗口.*(资源|等待)/.test(text)) return '首段公共路权暂不可用'
  if (/准入/.test(text)) return '等待业务准入名额'
  if (/TransportBound|持久化阶段/.test(text)) return ''
  return text.split(/[；\r\n]/, 1)[0].replace(/\s*TASK-[\w-]+/g, '').slice(0, 90).trim()
}

export function callPresentation(row: CallRow, rows: readonly CallRow[], elapsed: number) {
  const previous = row.status === 'pending' && elapsed >= row.atSeconds
    ? rows.slice(0, row.planIndex).reverse().find(item => item.stationId === row.stationId && ['active', 'created', 'emitted'].includes(item.status))
    : undefined
  if (row.status === 'pending') return {
    previous, step: '—', reason: previous ? `等待同工位第 ${previous.planIndex + 1} 单闭合`
      : elapsed >= row.atSeconds ? '等待工位 PLC 就绪' : '等待计划触发',
  }
  if (!row.taskId) return { previous, step: '—', reason: '等待 PLC 呼叫建单' }
  if (!row.vehicleId) {
    const candidate = /([\w-]+)\s*已作为候选/.exec(row.waitingExplanation ?? '')?.[1]
    return { previous, step: '待派车', reason: [candidate && `候选 ${candidate}`,
      briefReason(row.waitingExplanation) || '等待调度匹配'].filter(Boolean).join(' · ') }
  }
  if (row.status === 'completed' || row.taskStage === 'Completed') return { previous, step: '已完成九步', reason: '' }
  const step = businessStepLabel(row)
  return { previous, step: step ? `${row.stepNumber}/9 ${step}` : '—', reason: briefReason(row.waitingExplanation) }
}

interface Point { x: number; y: number }
interface ResourceLocation { label: string; nodeId?: string; edgeIds: string[]; point?: Point; boundary?: Point[] }
type MapResource = { id?: string; name?: string; type?: string; boundary?: Point[] | null }
function resourceCenter(boundary?: Point[] | null) {
  if (!boundary?.length || !boundary.every(point => Number.isFinite(point.x) && Number.isFinite(point.y))) return undefined
  return {
    x: boundary.reduce((sum, point) => sum + point.x, 0) / boundary.length,
    y: boundary.reduce((sum, point) => sum + point.y, 0) / boundary.length,
  }
}
export function resourceLocations(map?: SourceMap | null, runtime: readonly ResourceRow[] = []) {
  const locations = new Map<string, ResourceLocation>()
  if (!map) return locations
  const nodes = new Map(map.nodes.filter(node => node.enabled !== false).map(node => [node.id, node]))
  const mapResources = (map.resources ?? []) as MapResource[]
  const edgeIdsByResource = new Map<string, string[]>()
  const pointsByResource = new Map<string, { x: number; y: number }>()
  const stationNodes = [...nodes.values()].filter(node => /^(?:BUS-)?CNC-\d+$/.test(node.id))
  for (const edge of map.edges) {
    if (!edge.id || edge.enabled === false) continue
    const from = nodes.get(edge.from), to = nodes.get(edge.to)
    if (!from || !to) continue
    for (const id of edge.resourceIds ?? []) {
      const edges = edgeIdsByResource.get(id) ?? []
      edges.push(edge.id)
      edgeIdsByResource.set(id, edges)
      if (!pointsByResource.has(id)) pointsByResource.set(id, { x: (from.x + to.x) / 2, y: (from.y + to.y) / 2 })
    }
  }
  const definitions = new Map(mapResources.filter(item => item?.id).map(item => [item.id!, item]))
  for (const row of runtime) {
    if (row.runtimeBoundary?.length && !definitions.has(row.resourceId)) {
      definitions.set(row.resourceId, { boundary: row.runtimeBoundary })
    }
  }
  const ids = new Set([...definitions.keys(), ...edgeIdsByResource.keys()])
  for (const id of ids) {
    const definition = definitions.get(id)
    const edgeIds = edgeIdsByResource.get(id) ?? []
    const short = id.length > 18 ? id.slice(-6) : id
    const station = nodes.get(id) ?? nodes.get(`BUS-${id}`) ?? nodes.get(id.replace(/^X-/, ''))
    const boundary = definition?.boundary
    const center = resourceCenter(boundary)
    const point = pointsByResource.get(id) ?? center
    let nearest: (typeof stationNodes)[number] | undefined
    let nearestDistance = Infinity
    if (point) for (const node of stationNodes) {
      const distance = (node.x - point.x) ** 2 + (node.y - point.y) ** 2
      if (distance < nearestDistance) { nearest = node; nearestDistance = distance }
    }
    const prefix = /^DUMPER/.test(id) ? '倒料位' : /^CHARG/.test(id) ? '充电位'
      : id.startsWith('X-') ? '交叉口' : '通道'
    const location = nearest?.id.replace(/^BUS-/, '')
    const label = definition?.name || (/^CNC-\d+$/.test(id) ? id
      : `${location && nearestDistance <= 100 && prefix === '通道' ? `${location} 附近` : ''}${prefix} ${short}`)
    locations.set(id, { label, nodeId: station?.id, edgeIds, point, boundary: center ? boundary ?? undefined : undefined })
  }
  return locations
}

export function resourceRows(resources: readonly ResourceRow[], requests: readonly DispatchWaitRequest[],
  locations: ReadonlyMap<string, ResourceLocation>, search: string, showAll: boolean) {
  const byResource = new Map<string, DispatchWaitRequest[]>()
  const liveRequests = requests.filter(request => !isReferenceRoute(request))
  for (const request of liveRequests) for (const id of new Set([
    ...(request.blockers ?? []).map(blocker => blocker.resourceId), ...(request.resourceIds ?? []),
  ])) {
    const waits = byResource.get(id) ?? []
    waits.push(request)
    byResource.set(id, waits)
  }
  const blockers = new Set(liveRequests.flatMap(request => (request.blockers ?? []).map(blocker => blocker.resourceId)))
  const query = search.trim().toLowerCase()
  return resources.flatMap(resource => {
    const waits = byResource.get(resource.resourceId) ?? []
    const location = locations.get(resource.resourceId)
    const label = location?.label ?? (resource.resourceId.length > 18 ? `资源 ${resource.resourceId.slice(-6)}` : resource.resourceId)
    if (query ? !`${label} ${resource.resourceId}`.toLowerCase().includes(query)
      : !showAll && !resource.occupiedCount && !waits.length) return []
    return [{ ...resource, label, location, waits, blocking: blockers.has(resource.resourceId) }]
  }).sort((a, b) => Number(b.blocking) - Number(a.blocking) || Number(!!b.waits.length) - Number(!!a.waits.length)
    || Number(!!b.occupiedCount) - Number(!!a.occupiedCount) || a.label.localeCompare(b.label, 'zh-CN', { numeric: true }))
}

export function summarizeWait(request: DispatchWaitRequest, locations: ReadonlyMap<string, ResourceLocation>, candidate = false, omitDuration = false) {
  const blockers = dispatchWaitBlockers(request.blockers)
  if (isReferenceRoute(request)) return [
    candidate && request.vehicleId && `候选 ${request.vehicleId}`,
    !omitDuration && dispatchWaitDuration(request),
    candidate ? '暂无可执行首窗' : '暂无可走候车路线',
    blockers.length > 0 && `参考路线沿线 ${blockers.length} 项占满`,
  ].filter(Boolean).join(' · ')
  const first = blockers[0]
  const cause = first ? `${first.vehicles.length ? `${first.vehicles.join('、')} 占用` : '持有车待确认'}${locations.get(first.resourceId)?.label ?? first.resourceId}`
    : briefReason(request.reason) || '等待资源放行'
  return [candidate && request.vehicleId && `候选 ${request.vehicleId}`, !omitDuration && dispatchWaitDuration(request), cause,
    blockers.length > 1 && `另有 ${blockers.length - 1} 项`].filter(Boolean).join(' · ')
}

export function trafficWaitDetails(request: DispatchWaitRequest, now: number) {
  const blockers = dispatchWaitBlockers(request.blockers)
  const reference = isReferenceRoute(request)
  const blockingSeconds = !reference && blockers.length && request.blockingObservationCurrent &&
    request.blockingStartKnown && request.blockingClockValid !== false
    ? secondsSince(request.blockingSince, now) ?? null
    : null
  return {
    key: `${request.taskId}/${request.stepId}`, request, blockers, blockingSeconds, reference,
    holders: [...new Set(blockers.flatMap(blocker => blocker.vehicles))].sort(),
    resourceCount: request.resourceIds && new Set(request.resourceIds.map(id => id.toUpperCase())).size,
    heldCount: request.heldResourceIds && new Set(request.heldResourceIds.map(id => id.toUpperCase())).size,
    segmentCount: request.edgeIds?.length,
  }
}

export function trafficWaitRows(requests: readonly DispatchWaitRequest[], now: number) {
  return requests.filter(request => request.state === 'Waiting' && request.waitingCategory !== 'Equipment')
    .map(request => trafficWaitDetails(request, now))
    .sort((a, b) => Number(a.reference) - Number(b.reference) || Number(!!b.blockers.length) - Number(!!a.blockers.length)
      || (b.request.waitingSeconds ?? -1) - (a.request.waitingSeconds ?? -1))
}

export function vehicleTaskKind(car: Pick<DispatchCar, 'charging' | 'current_task' | 'recovery'> | undefined, business: boolean) {
  if (car?.charging || car?.current_task?.startsWith('CHARGE-')) return '充电'
  if (car?.recovery && car.recovery.state !== 'Recovered') return business ? '业务 · 障碍恢复' : '障碍恢复'
  if (car?.current_task?.startsWith('STANDBY-')) return '回待机'
  if (business) return car?.current_task ? '业务 · 导航' : '业务'
  return car?.current_task ? '导航' : car ? '待命' : '未确认'
}

const chargingLabels: Record<string, string> = {
  Reserved: '准备前往充电位', SendingToCharger: '准备前往充电位', GoingToCharger: '前往充电位',
  SendingDock: '准备对接充电位', Docking: '对接充电位', StartingCharge: '启动充电',
  StartingSession: '启动充电', AwaitingCurrent: '等待充电电流', Charging: '充电中',
  SessionRunning: '充电中', StoppingCharge: '正在停充', AwaitingStop: '确认已停充',
  SendingExit: '准备离桩', Exiting: '正在离桩', SendingUndock: '准备离桩',
  Undocking: '正在离桩', SendingReturn: '准备返回待机点', Returning: '返回待机点',
  SendingToWaitingSite: '准备前往安全等桩位', GoingToWaitingSite: '前往安全等桩位',
  Hold: '充电暂停',
}

export function chargingPresentation(sessions: readonly DispatchChargingSession[] | null | undefined, vehicleId: string, business: boolean) {
  const session = sessions?.find(row => row.vehicleId === vehicleId && row.phase !== 'Completed')
  if (!session) return undefined
  const { phase, site, battery } = session
  const pending = phase === 'Queued' || phase === 'WaitingForBusiness'
  const confirmed = phase === 'Charging' || phase === 'SessionRunning' && battery?.charging === true
  const label = pending ? business ? '业务完成后充电' : session.waitingSiteId ? '安全位等桩' : '排队充电'
    : phase === 'AwaitingStop' && session.shutdownVerified ? '等待离桩'
    : phase === 'SessionRunning' && !confirmed ? '等待充电确认'
    : chargingLabels[phase]
  if (!label) return undefined
  const staging = ['SendingToWaitingSite', 'GoingToWaitingSite'].includes(phase)
  const target = staging ? session.waitingSiteId : ['SendingReturn', 'Returning'].includes(phase) ? session.returnSiteId
    : ['SendingExit', 'Exiting', 'SendingUndock', 'Undocking'].includes(phase) ? site?.exitSiteId
    : pending || phase === 'Hold' ? undefined : site?.chargingSiteId
  const current = confirmed && battery?.current != null && battery.current > 0 ? battery.current : null
  const earliest = session.siteEstimates?.filter(estimate => estimate.startInSeconds != null)
    .sort((a, b) => a.startInSeconds! - b.startInSeconds!)[0]
  const details = [
    site?.id ?? session.waitingSiteId,
    pending && earliest?.startInSeconds != null ? `${earliest.siteId} 约 ${Math.ceil(earliest.startInSeconds)} 秒` : undefined,
    session.targetBatteryPercent == null ? undefined : `目标 ${session.targetBatteryPercent}%`,
    current == null ? undefined : `${current.toFixed(1)} A`,
  ].filter(Boolean).join(' · ')
  return {
    label, pending, target, details, reason: session.message, phaseSince: session.phaseSince, current,
    type: confirmed ? 'success' as const : phase === 'Hold' ? 'danger' as const : 'warning' as const,
  }
}

export function routeTarget(route?: NonNullable<DispatchTaskDetail['commands']>[number]['route']) {
  return route?.vendorSites?.at(-1) ?? route?.nodeIds?.at(-1)
}
