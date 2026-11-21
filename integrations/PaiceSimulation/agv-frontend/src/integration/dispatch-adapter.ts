import type { RoadNetPayload, NetNode, NetEdge } from "../api/roadnet.ts";
import type { AgvView } from "../types.ts";

export interface Point {
  x: number;
  y: number;
}
export interface SourceNode extends Point {
  id: string;
  agvSiteId?: string | null;
  type?: string;
  enabled?: boolean;
}
export interface SourceEdge {
  id?: string;
  from: string;
  to: string;
  distance?: number;
  speedLimit?: number;
  bidirectional?: boolean;
  enabled?: boolean;
  geometryKind?: string;
  controlPoints?: Point[] | null;
  resourceIds?: string[] | null;
}
export interface SourceMap {
  name?: string;
  unit?: string;
  coordinateSystem?: string;
  nodes: SourceNode[];
  edges: SourceEdge[];
  resources?: unknown[];
  [key: string]: unknown;
}
export interface DispatchCar extends Point {
  mechanism?: { source: string; operation?: string | null; taskId?: string | null; state: string; upperLimit: boolean | null; lowerLimit: boolean | null };
  angle?: number;
  moving?: boolean;
  offline?: boolean;
  paused?: boolean;
  blocked?: boolean;
  blockedByVehicle?: string | null;
  loaded?: boolean;
  current_task?: string | null;
  waitReason?: string;
  station?: string;
  batteryPercent?: number;
  speedMps?: number;
  charging?: boolean;
  motionEdge?: string | null;
  recovery?: { state?: string; message?: string; nextAction?: string; safetyPointTarget?: string | null; durationSeconds?: number };
}
export interface DispatchTaskDetail {
  task: {
    taskId: string;
    vehicleId?: string | null;
    stationId?: string;
    stage?: string;
    createdAt?: string;
    updatedAt?: string;
  };
  waitingExplanation?: string;
  evidenceSource?: string;
  evidenceMessage?: string;
  executionTimeline?: { id: string; stepId?: string; occurredAt: string; kind: string; title: string; summary?: string }[];
  handover?: { exitPhysicallyVerified?: boolean; recordedReleaseReasons?: string[]; reason?: string };
  trafficWaiting?: { reason: string; blockingKey?: string; waitingSinceUtc?: string }[] | null;
  handshakeContractVersion?: string;
  plcConditions?: { stationId: string; variableName: string; address: string; expectedValue: boolean; currentValue?: boolean | null; quality: string; satisfied: boolean; reason?: string; observedAt?: string }[];
  handshakes?: { stepId: string; phase: string; reason: string; cycleId: string; updatedAt?: string; pendingSignal?: string; pendingValue?: boolean | null }[];
  steps?: {
    stepId?: string;
    sequence: number;
    stepType: string;
    status: string;
    sourceSite?: string | null;
    destinationSite?: string | null;
    elapsedText?: string;
    waitingExplanation?: string;
    isCurrent?: boolean;
    completionRequirement?: string;
    observedPhase?: string;
    startedAt?: string | null;
    completedAt?: string | null;
    commandStateExplanation?: string;
    evidenceBoundary?: string;
  }[];
  commands?: {
    commandId: string;
    stepId?: string;
    vendorCommandId?: string | null;
    status?: string;
    sentAt?: string | null;
    lastError?: string | null;
    route?: {
      mapVersionId?: string;
      nodeIds?: string[];
      edgeIds?: string[];
      vendorSites?: string[];
      action?: string;
    } | null;
  }[];
}
export interface DispatchWaitRequest {
  taskId: string;
  stepId: string;
  vehicleId?: string | null;
  source?: string;
  destination?: string;
  reason?: string;
  waitingCategory?: string;
  waitingSeconds?: number | null;
  waitingStartKnown?: boolean;
  waitingClockValid?: boolean;
  blockingSince?: string | null;
  blockingObservationCurrent?: boolean;
  blockingStartKnown?: boolean;
  blockingClockValid?: boolean;
  state?: string;
  resourceIds?: string[];
  blockers?: { resourceId: string; vehicleId?: string | null }[];
}
export interface DispatchWaitSnapshot {
  connected: boolean;
  traffic?: {
    capturedAt?: string | null;
    requestsTruncated?: boolean;
    requests: DispatchWaitRequest[];
  } | null;
}
interface DispatchWaitTask {
  status?: string;
  taskStage?: string;
  taskId?: string;
  vehicleId?: string | null;
  stepNumber?: number | null;
  stepId?: string;
}
export interface DispatchWaitState {
  label: string;
  type: "info" | "warning" | "primary" | "success" | "danger";
  request?: DispatchWaitRequest;
}

export function dispatchWaitSnapshotFresh(snapshot: DispatchWaitSnapshot | null, now: number) {
  const age = now - Date.parse(snapshot?.traffic?.capturedAt ?? "");
  return snapshot?.connected === true && age >= -1000 && age < 10000;
}

export function dispatchWaitState(task: DispatchWaitTask, requests: readonly DispatchWaitRequest[]): DispatchWaitState {
  if (task.status === "completed" || task.taskStage === "Completed") return { label: "已完成", type: "success" };
  if (task.taskStage === "Cancelled" || task.status === "Cancelled") return { label: "任务取消", type: "info" };
  if (task.taskStage === "Faulted" || task.status === "Faulted") return { label: "任务故障", type: "danger" };
  if (["CancellationPending", "CancellationReview"].includes(task.taskStage ?? "")) return { label: "取消处理中", type: "warning" };
  if (task.status === "pending") return { label: "待触发", type: "info" };
  if (!task.taskId) return { label: "已触发 · 待建单", type: "info" };
  const taskRequests = requests.filter(item => item.taskId === task.taskId);
  if (!task.vehicleId) return { label: "已建单待派", type: "info", request: taskRequests.find(item => item.waitingCategory !== "Equipment") };

  const request = taskRequests.find(item => item.vehicleId === task.vehicleId
    && (!task.stepId || task.stepId === item.stepId)
    && (task.stepNumber == null || (task.stepNumber === 4
      ? item.waitingCategory === "Equipment"
      : [1, 3, 5, 7].includes(task.stepNumber) && item.waitingCategory !== "Equipment")));
  if (request && request.waitingCategory !== "Equipment") return { label: "已派车等路权", type: "warning", request };
  if (request || [2, 4, 6, 8, 9].includes(task.stepNumber ?? 0)
    || (task.stepNumber == null && ["PickingUp", "WaitingForDumper", "WaitingForPlcConfirmation"].includes(task.taskStage ?? ""))) {
    return { label: "等 PLC／机构反馈", type: "warning", request };
  }
  return { label: "执行中", type: "primary" };
}

export function dispatchWaitDuration(request: Pick<DispatchWaitRequest, "waitingSeconds" | "waitingStartKnown" | "waitingClockValid">) {
  const seconds = request.waitingSeconds;
  if (!request.waitingStartKnown || request.waitingClockValid === false || seconds == null
    || !Number.isFinite(seconds) || seconds < 0) return "等待起点未知";
  const total = Math.floor(seconds);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor(total / 60) % 60;
  return `已等 ${[hours && `${hours}小时`, (minutes || hours) && `${minutes}分`, `${total % 60}秒`].filter(Boolean).join(" ")}`;
}

export function dispatchWaitBlockers(blockers: DispatchWaitRequest["blockers"] = []) {
  const grouped = new Map<string, Set<string>>();
  for (const { resourceId, vehicleId } of blockers) {
    const vehicles = grouped.get(resourceId) ?? new Set<string>();
    if (vehicleId) vehicles.add(vehicleId);
    grouped.set(resourceId, vehicles);
  }
  return [...grouped].map(([resourceId, vehicles]) => ({ resourceId, vehicles: [...vehicles].sort() }));
}

export interface MapConversion {
  roadnet: RoadNetPayload;
  sourceDefinition: SourceMap;
  warnings: string[];
  edgeMapping: Record<string, string[]>;
  coordinateOffset: Point;
  excludedNodes: string[];
  excludedEdges: string[];
}
export class RoundTripBlockedError extends Error {
  readonly nodeIds: string[];
  constructor(nodeIds: string[]) {
    super(`以下点位无法确认可安全排除：${nodeIds.join("、")} 无法与初始点往返。`);
    this.nodeIds = nodeIds;
  }
}
export function applyImportRoadDirections(definition: SourceMap, directions: Readonly<Record<string, boolean>>): SourceMap {
  return {
    ...definition,
    edges: definition.edges.map((edge, i) => {
      const id = edge.id ?? `source-edge-${i}`;
      return Object.prototype.hasOwnProperty.call(directions, id) ? { ...edge, bidirectional: directions[id] } : edge;
    }),
  };
}

export function sourcePointTraffic(definition: SourceMap, nodeId: string) {
  const enabled = new Set(definition.nodes.filter(node => node.enabled !== false).map(node => node.id));
  let incoming = 0, outgoing = 0;
  for (const edge of definition.edges) {
    if (edge.enabled === false || !enabled.has(edge.from) || !enabled.has(edge.to)) continue;
    if (edge.to === nodeId || (edge.bidirectional && edge.from === nodeId)) incoming++;
    if (edge.from === nodeId || (edge.bidirectional && edge.to === nodeId)) outgoing++;
  }
  const reason = !enabled.has(nodeId) ? "点位停用或不存在"
    : !incoming && !outgoing ? "没有启用的关联道路"
    : !outgoing ? "只进不出"
    : !incoming ? "只出不进"
    : "无法与初始点往返";
  return { incoming, outgoing, reason };
}
function validPoint(point: Point) {
  return Number.isFinite(point.x) && Number.isFinite(point.y);
}

export function sampleDispatchEdge(
  a: Point,
  b: Point,
  edge: SourceEdge,
): Point[] {
  const cp = edge.controlPoints ?? [];
  if (![a, b, ...cp].every(validPoint)) throw new Error("道路坐标无效");
  const kind = edge.geometryKind ?? "Line";
  if (kind === "Line") return [{ ...a }, { ...b }];
  if (kind === "CubicBezier" && cp.length === 2) {
    return Array.from({ length: 25 }, (_, i) => {
      const t = i / 24,
        u = 1 - t;
      return {
        x:
          u * u * u * a.x +
          3 * u * u * t * cp[0].x +
          3 * u * t * t * cp[1].x +
          t * t * t * b.x,
        y:
          u * u * u * a.y +
          3 * u * u * t * cp[0].y +
          3 * u * t * t * cp[1].y +
          t * t * t * b.y,
      };
    });
  }
  if (kind === "CircularArc" && cp.length === 1) {
    const c = cp[0],
      den = 2 * (a.x * (c.y - b.y) + c.x * (b.y - a.y) + b.x * (a.y - c.y));
    if (Math.abs(den) < 1e-10) throw new Error("圆弧控制点与端点共线");
    const q = (p: Point) => p.x * p.x + p.y * p.y;
    const x =
      (q(a) * (c.y - b.y) + q(c) * (b.y - a.y) + q(b) * (a.y - c.y)) / den;
    const y =
      (q(a) * (b.x - c.x) + q(c) * (a.x - b.x) + q(b) * (c.x - a.x)) / den;
    const angle = (p: Point) => Math.atan2(p.y - y, p.x - x);
    const mod = (n: number) => (n + Math.PI * 2) % (Math.PI * 2);
    const ab = mod(angle(b) - angle(a)),
      ac = mod(angle(c) - angle(a));
    const sweep = ac <= ab ? ab : ab - Math.PI * 2,
      r = Math.hypot(a.x - x, a.y - y);
    return Array.from({ length: 25 }, (_, i) =>
      i === 0
        ? { ...a }
        : i === 24
          ? { ...b }
          : {
              x: x + r * Math.cos(angle(a) + (sweep * i) / 24),
              y: y + r * Math.sin(angle(a) + (sweep * i) / 24),
            },
    );
  }
  throw new Error(`不支持的道路几何或控制点数量 ${kind}`);
}
function nodeType(type?: string): NetNode["type"] {
  return (
    (
      {
        Station: "WORKSTATION",
        Charging: "CHARGER",
        Charger: "CHARGER",
        ChargingStation: "CHARGER",
        Waiting: "BUFFER",
        WaitingPoint: "JUNCTION",
        ChargingPoint: "CHARGER",
      } as Record<string, NetNode["type"]>
    )[type ?? ""] ?? "JUNCTION"
  );
}
export function convertDispatchMap(
  definition: SourceMap,
  initialSite?: string,
  options: {
    roundTripOnly?: boolean;
    simplifyRoads?: boolean;
    mergeOppositeParallel?: boolean;
    protectedSiteIds?: readonly string[];
  } = {},
): MapConversion {
  if (definition.unit !== "m") throw new Error("只接受米制完整地图");
  const warnings = [
    "实验道路容量设为 1，车道使用 SINGLE。",
    "区域资源、厂家道路属性、PLC条件及车体包络保存在来源记录中。",
  ];
  const enabledNodes = definition.nodes.filter((n) => n.enabled !== false);
  const enabledNodeIds = new Set(enabledNodes.map(n => n.id));
  const transitWaiting = enabledNodes.filter(
    (n) => n.type === "WaitingPoint",
  ).length;
  if (transitWaiting)
    warnings.push(
      `${transitWaiting} 个 WaitingPoint 转为实验 JUNCTION，候车业务语义保存在来源记录。`,
    );
  let sourceNodes = enabledNodes;
  const excludedNodes: string[] = [],
    excludedEdges: string[] = [];
  if (options.roundTripOnly) {
    const anchor =
      initialSite ||
      enabledNodes.find((n) => nodeType(n.type) === "CHARGER")?.id;
    if (!anchor || !enabledNodes.some((n) => n.id === anchor))
      throw new Error("请先选择有效实验初始停放点");
    const allowed = new Set(enabledNodes.map((n) => n.id));
    const outgoing = new Map<string, string[]>(),
      incoming = new Map<string, string[]>();
    const add = (a: string, b: string) => {
      outgoing.set(a, [...(outgoing.get(a) ?? []), b]);
      incoming.set(b, [...(incoming.get(b) ?? []), a]);
    };
    for (const edge of definition.edges) {
      if (
        edge.enabled === false ||
        !allowed.has(edge.from) ||
        !allowed.has(edge.to)
      )
        continue;
      add(edge.from, edge.to);
      if (edge.bidirectional) add(edge.to, edge.from);
    }
    const visit = (adjacency: Map<string, string[]>) => {
      const seen = new Set<string>(),
        pending = [anchor];
      while (pending.length) {
        const id = pending.pop()!;
        if (seen.has(id)) continue;
        seen.add(id);
        pending.push(
          ...(adjacency.get(id) ?? []).filter((next) => !seen.has(next)),
        );
      }
      return seen;
    };
    const forward = visit(outgoing),
      backward = visit(incoming);
    const outside = enabledNodes.filter((n) => !forward.has(n.id) || !backward.has(n.id));
    if (options.protectedSiteIds) {
      const protectedIds = new Set(options.protectedSiteIds);
      const blocked = outside.filter(node =>
        protectedIds.has(node.id) || (node.type !== "Waypoint" && node.type !== "Junction"),
      );
      if (blocked.length) throw new RoundTripBlockedError(blocked.map(node => node.id));
    }
    sourceNodes = enabledNodes.filter((n) => forward.has(n.id) && backward.has(n.id));
    excludedNodes.push(...outside.map((n) => n.id));
    const included = new Set(sourceNodes.map((n) => n.id));
    excludedEdges.push(
      ...definition.edges.flatMap((e, i) =>
        e.enabled !== false && enabledNodeIds.has(e.from) && enabledNodeIds.has(e.to) &&
        (!included.has(e.from) || !included.has(e.to)) ? [e.id ?? `source-edge-${i}`] : [],
      ),
    );
    warnings.push(
      `按初始点 ${anchor} 保留可往返区域，排除 ${excludedNodes.length} 个节点和 ${excludedEdges.length} 条道路。`,
    );
  }
  const nodes: NetNode[] = sourceNodes.map((n) => {
    if (!n.id || !validPoint(n)) throw new Error("地图节点 ID 或坐标无效");
    return { id: n.id, x: n.x, y: n.y, type: nodeType(n.type), capacity: 1 };
  });
  const byId = new Map(sourceNodes.map((n) => [n.id, n]));
  const used = new Set(nodes.map((n) => n.id));
  if (used.size !== nodes.length) throw new Error("地图节点 ID 重复");
  if (initialSite) {
    const node = nodes.find((n) => n.id === initialSite);
    if (!node) throw new Error("实验初始停放点无效");
    node.type = "CHARGER";
    warnings.push(`实验初始停放点 ${initialSite} 在副本中标为 CHARGER。`);
  }
  const unique = (prefix: string) => {
    let id = prefix;
    while (used.has(id)) id += "_";
    used.add(id);
    return id;
  };
  const edges: NetEdge[] = [],
    edgeMapping: Record<string, string[]> = {};
  let simplifiedRoads = 0;
  const edgeIds = new Set<string>();
  const pairKey = (edge: SourceEdge) =>
    JSON.stringify([edge.from, edge.to].sort());
  const pairCounts = new Map<string, number>();
  const pairGroups = new Map<string, SourceEdge[]>();
  for (const edge of definition.edges) {
    if (edge.enabled !== false && byId.has(edge.from) && byId.has(edge.to)) {
      pairCounts.set(pairKey(edge), (pairCounts.get(pairKey(edge)) ?? 0) + 1);
      const group = pairGroups.get(pairKey(edge)) ?? [];
      group.push(edge);
      pairGroups.set(pairKey(edge), group);
    }
  }
  const consumedEdges = new Set<string>();
  for (const [index, edge] of definition.edges.entries()) {
    if (edge.enabled === false) continue;
    const id = edge.id ?? `source-edge-${index}`;
    if (consumedEdges.has(id)) continue;
    if (edgeIds.has(id)) throw new Error("地图道路 ID 重复");
    edgeIds.add(id);
    const from = byId.get(edge.from),
      to = byId.get(edge.to);
    if (!from || !to) {
      if (!options.roundTripOnly || !enabledNodeIds.has(edge.from) || !enabledNodeIds.has(edge.to))
        warnings.push(`跳过端点禁用或缺失的道路 ${id}`);
      continue;
    }
    if (!(Number.isFinite(edge.distance) && edge.distance! > 0))
      throw new Error(`道路 ${id} 缺少有效长度`);
    if (!(Number.isFinite(edge.speedLimit) && edge.speedLimit! > 0))
      throw new Error(`道路 ${id} 缺少有效速度`);
    const reciprocal =
      options.mergeOppositeParallel === false || edge.bidirectional
        ? undefined
        : (pairGroups.get(pairKey(edge)) ?? []).find((candidate) => {
            const candidateId =
              candidate.id ??
              `source-edge-${definition.edges.indexOf(candidate)}`;
            return (
              candidateId !== id &&
              candidate.enabled !== false &&
              !candidate.bidirectional &&
              candidate.from === edge.to &&
              candidate.to === edge.from &&
              Math.abs(candidate.distance! - edge.distance!) < 1e-6 &&
              Math.abs(candidate.speedLimit! - edge.speedLimit!) < 1e-9 &&
              JSON.stringify(candidate.resourceIds ?? []) ===
                JSON.stringify(edge.resourceIds ?? [])
            );
          });
    if (reciprocal)
      consumedEdges.add(
        reciprocal.id ?? `source-edge-${definition.edges.indexOf(reciprocal)}`,
      );
    const points = options.simplifyRoads
      ? [{ ...from }, { ...to }]
      : sampleDispatchEdge(from, to, edge);
    if (
      options.simplifyRoads &&
      edge.geometryKind &&
      edge.geometryKind !== "Line"
    )
      simplifiedRoads++;
    if (
      points.length === 2 &&
      (pairCounts.get(pairKey(edge)) ?? 0) > 1 &&
      !reciprocal
    )
      points.splice(1, 0, { x: (from.x + to.x) / 2, y: (from.y + to.y) / 2 });
    const lengths = points
      .slice(1)
      .map((p, i) => Math.hypot(p.x - points[i].x, p.y - points[i].y));
    const total = lengths.reduce((a, b) => a + b, 0);
    if (lengths.some((n) => n <= 1e-9) || !Number.isFinite(total))
      throw new Error(`道路 ${id} 存在零长分段`);
    const ids = [
      edge.from,
      ...points.slice(1, -1).map((p, i) => {
        const nodeId = unique(`__dispatch_${id}_p${i + 1}`);
        nodes.push({ id: nodeId, ...p, type: "JUNCTION", capacity: 1 });
        return nodeId;
      }),
      edge.to,
    ];
    const segmentIds = lengths.map((length, i) => {
      const segmentId =
        lengths.length === 1 ? id : unique(`__dispatch_${id}_s${i + 1}`);
      edges.push({
        id: segmentId,
        from: ids[i],
        to: ids[i + 1],
        length: (edge.distance! * length) / total,
        speed: edge.speedLimit!,
        capacity: 1,
        direction: edge.bidirectional || reciprocal ? "TWO_WAY" : "ONE_WAY",
        laneMode: "SINGLE",
      });
      return segmentId;
    });
    edgeMapping[id] = segmentIds;
    if (reciprocal) {
      const reciprocalId =
        reciprocal.id ?? `source-edge-${definition.edges.indexOf(reciprocal)}`;
      edgeMapping[reciprocalId] = segmentIds;
    }
    if (points.length > 2)
      warnings.push(`道路 ${id} 转换为 ${lengths.length} 段折线。`);
  }
  if (simplifiedRoads)
    warnings.push(
      `简化道路开启，${simplifiedRoads} 条曲线按起终点直线显示，算法继续使用原道路长度。`,
    );
  const xs = nodes.map((n) => n.x),
    ys = nodes.map((n) => n.y);
  const minX = nodes.length ? Math.min(...xs) : 0,
    minY = nodes.length ? Math.min(...ys) : 0;
  const maxX = nodes.length ? Math.max(...xs) : 1,
    maxY = nodes.length ? Math.max(...ys) : 1;
  const coordinateOffset = { x: minX - 4, y: maxY + 4 };
  for (const node of nodes) {
    node.x -= coordinateOffset.x;
    // Web 仿真按 -Y 朝屏幕上方显示；实验画布沿用同一方向，避免全厂图上下翻转。
    node.y = coordinateOffset.y - node.y;
  }
  warnings.push(
    `实验坐标统一转换，原始 X=实验 X+(${coordinateOffset.x.toFixed(6)})，原始 Y=(${coordinateOffset.y.toFixed(6)})-实验 Y。`,
  );
  return {
    roadnet: {
      nodes,
      edges,
      width: Math.max(50, Math.ceil(maxX - minX) + 8),
      height: Math.max(34, Math.ceil(maxY - minY) + 8),
    },
    coordinateOffset,
    sourceDefinition: JSON.parse(JSON.stringify(definition)),
    warnings,
    edgeMapping,
    excludedNodes,
    excludedEdges,
  };
}
export function dispatchViews(
  cars: Record<string, DispatchCar>,
  fresh: boolean,
): AgvView[] {
  return Object.entries(cars)
    .filter(([, car]) => validPoint(car))
    .map(([id, car]) => ({
      id,
      x: car.x,
      y: -car.y,
      heading: ((car.angle ?? 0) * 180) / Math.PI,
      taskId: 0,
      station: car.station,
      status:
        !fresh || car.offline
          ? "OFFLINE"
          : car.paused
            ? "FAULT"
            : car.blocked || car.blockedByVehicle
              ? "BLOCKED"
              : car.charging
                ? "CHARGING"
                : car.moving || (car.speedMps ?? 0) > 0.01
                  ? "MOVING"
                  : car.current_task || car.loaded
                    ? "WAITING"
                    : "IDLE",
    }));
}
export function dispatchSceneMap(definition: SourceMap): RoadNetPayload {
  const nodes = definition.nodes
    .filter(validPoint)
    .map((n) => ({
      id: n.id,
      agvSiteId: n.agvSiteId ?? undefined,
      x: n.x,
      y: -n.y,
      type: nodeType(n.type),
    }));
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const edges = definition.edges.flatMap((e, i) => {
    const a = byId.get(e.from),
      b = byId.get(e.to);
    if (!a || !b) return [];
    return [
      {
        id: e.id ?? String(i),
        from: e.from,
        to: e.to,
        length: e.distance ?? 0,
        speed: e.speedLimit ?? 0,
        capacity: 1,
        direction: e.bidirectional
          ? ("TWO_WAY" as const)
          : ("ONE_WAY" as const),
        points: sampleDispatchEdge(a, b, e),
      },
    ];
  });
  return { nodes, edges };
}
export function transportTasks(
  details: DispatchTaskDetail[],
  roadnet: RoadNetPayload,
) {
  const stations = new Set(
    roadnet.nodes.filter((n) => n.type === "WORKSTATION").map((n) => n.id),
  );
  const tasks: { id: number; from: string; to: string }[] = [],
    mapping: { id: number; taskId: string; commandId: string }[] = [],
    skipped: string[] = [];
  for (const detail of details)
    for (const command of detail.commands ?? []) {
      const route = command.route?.nodeIds ?? [],
        from = route[0],
        to = route[route.length - 1];
      if (
        !from ||
        !to ||
        from === to ||
        !stations.has(from) ||
        !stations.has(to)
      ) {
        skipped.push(
          `${detail.task.taskId} / ${command.commandId} 的端点不满足实验工位条件`,
        );
        continue;
      }
      const id = tasks.length + 1;
      tasks.push({ id, from, to });
      mapping.push({
        id,
        taskId: detail.task.taskId,
        commandId: command.commandId,
      });
    }
  return { tasks, mapping, skipped };
}
interface SourceSnapshot {
  connected: boolean;
  source: string;
  receivedAt: string;
  map?: {
    id: string;
    version: string;
    definitionHash?: string;
    unit?: string;
  } | null;
}
interface SourceWorkspace {
  project?: { mapId: string; isActive: boolean; activeVersionId?: string };
  published?: {
    mapId: string;
    versionId: string;
    status: string;
    definition: SourceMap;
  };
}
export function publishedDispatchMap(
  snapshot: SourceSnapshot,
  workspace: SourceWorkspace,
): SourceMap {
  const age = Date.now() - Date.parse(snapshot.receivedAt),
    map = snapshot.map,
    pub = workspace.published,
    project = workspace.project;
  if (
    !snapshot.connected ||
    snapshot.source !== "backend" ||
    !Number.isFinite(age) ||
    age > 15000 ||
    age < -1000 ||
    !map?.definitionHash
  )
    throw new Error("主工程没有有效地图观察");
  if (
    !pub ||
    !project?.isActive ||
    project.mapId !== map.id ||
    project.activeVersionId !== map.version ||
    pub.mapId !== map.id ||
    pub.versionId !== map.version ||
    pub.status !== "Published"
  )
    throw new Error("主工程发布地图版本不一致");
  if (pub.definition.unit !== "m") throw new Error("只接受米制完整地图");
  return pub.definition;
}

export interface WebMapSummary {
  mapId: string;
  name: string;
  description?: string;
  unit?: string;
  isActive: boolean;
  publishedVersionId?: string | null;
  updatedAt?: string;
}

export function selectWebSimulationMap(maps: WebMapSummary[]) {
  const candidates = maps.filter(
    (map) => !!map.publishedVersionId && map.unit === "m",
  );
  const score = (map: WebMapSummary) => {
    const text = `${map.name} ${map.description ?? ""}`;
    return (
      (text.includes("五车") ? 4 : 0) +
      (text.includes("305CNC") ? 4 : 0) +
      (text.includes("仿真图") ? 2 : 0)
    );
  };
  const marked = candidates
    .filter((map) => score(map) > 0)
    .sort(
      (a, b) =>
        score(b) - score(a) ||
        String(b.updatedAt ?? "").localeCompare(String(a.updatedAt ?? "")),
    );
  if (marked[0]) return marked[0];
  return (
    candidates.find((map) => map.isActive) ??
    candidates.sort((a, b) =>
      String(b.updatedAt ?? "").localeCompare(String(a.updatedAt ?? "")),
    )[0]
  );
}

export function simulationRoadnetName(definition: SourceMap) {
  const name = definition.name ?? "Web仿真图";
  if (name.includes("28阶段") || name.includes("闭环联调"))
    return "真实场景仿真";
  if (name.includes("305") && name.includes("五车")) return "全厂五车仿真";
  return `${name}（仿真）`;
}

export function workshopMapName(definition: SourceMap) {
  const name = definition.name?.trim();
  if (name && (name === "全厂五车仿真" || (name.includes("B07-1F") && name.includes("305"))))
    return "全厂五车仿真";
  return name || "隔离实例地图";
}

export function resolveMapSiteId(
  definition: SourceMap,
  station?: string | null,
) {
  if (!station) return "";
  return (
    definition.nodes.find(
      (node) =>
        node.enabled !== false &&
        (node.id === station || node.agvSiteId === station),
    )?.id ?? ""
  );
}

export async function sourceDefinitionHash(definition: SourceMap) {
  const digest = await crypto.subtle.digest(
    "SHA-256",
    new TextEncoder().encode(JSON.stringify(definition)),
  );
  return [...new Uint8Array(digest)]
    .map((value) => value.toString(16).padStart(2, "0"))
    .join("")
    .toUpperCase();
}

export function roadnetFingerprint(map: RoadNetPayload) {
  return JSON.stringify({
    nodes: map.nodes
      .map((n) => [n.id, n.x, n.y, n.type, n.capacity ?? 1])
      .sort((a, b) => String(a[0]).localeCompare(String(b[0]))),
    edges: map.edges
      .map((e) => [
        e.id,
        e.from,
        e.to,
        e.length,
        e.direction,
        e.speed,
        e.capacity,
        e.laneMode ?? "SINGLE",
      ])
      .sort((a, b) => String(a[0]).localeCompare(String(b[0]))),
  });
}

export function dispatchIsFresh(
  status: {
    enabled: boolean;
    running?: boolean;
    fresh?: boolean;
    device?: { time: number } | null;
  } | null,
  now: number,
) {
  const stamp = status?.device?.time;
  return (
    !!status?.enabled &&
    !!status.running &&
    status.fresh === true &&
    typeof stamp === "number" &&
    Number.isFinite(stamp) &&
    stamp * 1000 <= now + 1000 &&
    now - stamp * 1000 < 5000
  );
}
export function dispatchRoutePoints(
  map: RoadNetPayload,
  nodeIds: string[],
  edgeIds: string[],
): Point[] {
  const points: Point[] = [];
  const nodes = new Map(map.nodes.map((node) => [node.id, node]));
  const edges = new Map(map.edges.map((edge) => [edge.id, edge]));
  for (const [index, id] of edgeIds.entries()) {
    const edge = edges.get(id);
    if (!edge) return [];
    const geometry =
      edge.points ??
      [nodes.get(edge.from), nodes.get(edge.to)].filter(
        (p): p is NetNode => !!p,
      );
    if (geometry.length < 2) return [];
    const oriented =
      edge.from === nodeIds[index] ? geometry : [...geometry].reverse();
    points.push(...(index ? oriented.slice(1) : oriented));
  }
  return points;
}
