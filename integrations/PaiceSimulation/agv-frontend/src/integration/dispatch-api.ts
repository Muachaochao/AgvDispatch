import {
  publishedDispatchMap,
  sourceDefinitionHash,
  type SourceMap,
  type DispatchCar,
  type DispatchTaskDetail,
  type DispatchWaitSnapshot,
} from "./dispatch-adapter";
import http from "@/api/http";

export interface DispatchStatus {
  enabled: boolean;
  running?: boolean;
  phase?: string;
  fresh?: boolean;
  rootName?: string;
  simulationRate?: number;
  simulationOriginUnixMs?: number;
  preflight?: { passed: boolean; checks: { name: string; status: string }[] };
  startupChecks?: { name: string; status: string; detail: string }[];
  startupPlan?: { schemeId?: string; status?: string; message?: string };
  runtimeRelease?: { releaseId: string; algorithmMode: string; serviceSha256: string; webHostSha256: string; optionsSha256: string } | null;
  businessPlan?: {
    seed: number;
    count: number;
    intervalSeconds: number;
    stationIds: string[];
    calls: { stationId: string; atSeconds: number; vehicleId: null }[];
  } | null;
  error?: string;
  message?: string;
  map?: SourceMap | null;
  device?: { time: number; cars?: Record<string, DispatchCar> } | null;
  configuration?: {
    vehicles?: { id: string; initialSite: string; initialLoaded?: boolean; speedScale?: number; lengthMeters?: number; widthMeters?: number }[];
    stations?: { stationId: string; pickupSite?: string }[];
    dumper?: { dumpingSite: string; waitingSite: string; waitingSites?: string[]; resourceId: string };
    inspectionSite?: string;
    downstreamAdmissionCapacity?: number;
    plcSignalTable?: { fileName?: string; sha256?: string; sourceLabel?: string };
    mapIdentity?: {
      mapId?: string;
      mapVersionId?: string;
      sourceName?: string;
      definitionHash?: string;
    };
  };
  schemes?: {
    id: string;
    name: string;
    mapName?: string;
    vehicleCount?: number;
    stationCount?: number;
  }[];
  scenario?: { id?: string; name?: string; status?: string; message?: string; startedAt?: number | null; callRows?: CallRow[] };
  history?: DispatchReport[];
  report?: DispatchReport | null;
}
export interface DispatchReport {
    id?: string;
    name?: string;
    startedAt?: number;
    finishedAt?: number;
    status?: string;
    message?: string;
    firstFailure?: string;
    callRows?: CallRow[];
    waitingChain?: {
      taskId?: string;
      stationId?: string;
      vehicleId?: string;
      stepNumber?: number;
      stepName?: string;
      waitReason?: string;
    }[];
    metrics?: Record<string, unknown>;
    checks?: { name: string; status: string }[];
}
export interface CallRow {
  planIndex: number;
  stationId: string;
  atSeconds: number;
  actualAtSeconds?: number;
  taskId?: string;
  vehicleId?: string;
  stepNumber?: number;
  stepId?: string;
  stepType?: string;
  stepName?: string;
  stepStartedAt?: string;
  destinationSite?: string;
  sourceSite?: string;
  createdAt?: string;
  taskStage?: string;
  status: string;
  waitingExplanation?: string;
}
export interface TaskRow {
  taskId: string;
  vehicleId?: string | null;
  stationId?: string;
  stage: string;
  createdAt?: string;
}
export interface ResourceRow {
  resourceId: string;
  capacity: number;
  occupiedCount: number;
  runtimeBoundary?: { x: number; y: number }[] | null;
  leases?: { vehicleId: string; taskId: string; acquiredAt?: string }[];
}
export interface DispatchMonitor extends DispatchWaitSnapshot {
  resources?: ResourceRow[];
}
export interface DispatchTrafficEvent {
  eventId: string;
  kind: string;
  resourceIds: string[];
  vehicleId?: string;
  taskId?: string;
  reason?: string;
  occurredAt: string;
}

export async function dispatchRead<T>(
  path: string,
  signal?: AbortSignal,
  body?: Record<string, unknown>,
): Promise<T> {
  const response = await http.request<T>({
    url: proxyPath(path),
    method: body ? "POST" : "GET",
    timeout: ['/api/simulation/scheme-select', '/api/simulation/scheme-start'].includes(path) ? 180000 : 15000,
    data: body,
    signal: signal
      ? AbortSignal.any([signal, AbortSignal.timeout(15000)])
      : AbortSignal.timeout(['/api/simulation/scheme-select', '/api/simulation/scheme-start'].includes(path) ? 180000 : 15000),
  });
  return response as T;
}

function proxyPath(path: string) {
  const [pathname, query] = path.split("?", 2);
  if (pathname === "/api/simulation") return withQuery("/dispatch/main/simulation", query);
  if (pathname === "/api/simulation/live") return "/dispatch/main/simulation/live";
  if (pathname === "/api/simulation/snapshot") return "/dispatch/main/simulation/snapshot";
  if (pathname === "/api/simulation/monitor") return "/dispatch/main/simulation/monitor";
  if (pathname === "/api/simulation/resources") return "/dispatch/main/simulation/resources";
  if (pathname === "/api/simulation/tasks") {
    return withQuery("/dispatch/main/simulation/tasks", query);
  }
  if (pathname.startsWith("/api/simulation/tasks/")) {
    return "/dispatch/main/simulation/tasks/" + pathname.slice("/api/simulation/tasks/".length);
  }
  if (pathname === "/api/simulation/report" || pathname === "/api/simulation/replay") {
    return withQuery(pathname.replace("/api/", "/dispatch/main/"), query);
  }
  if (pathname.startsWith("/api/simulation/")) {
    return "/dispatch/main/simulation/" + pathname.slice("/api/simulation/".length);
  }
  if (pathname === "/api/snapshot") return "/dispatch/main/snapshot";
  if (pathname === "/api/maps") return "/dispatch/main/maps";
  if (/^\/api\/maps\/[^/]+\/workspace$/.test(pathname)) {
    const id = pathname.split("/")[3];
    return `/dispatch/main/maps/${encodeURIComponent(id)}/workspace`;
  }
  throw new Error(`不支持的主工程读取路径：${path}`);
}

function withQuery(path: string, query?: string) {
  return query ? `${path}?${query}` : path;
}

export async function loadPublishedDispatchMap(signal?: AbortSignal) {
  type Snapshot = Parameters<typeof publishedDispatchMap>[0];
  type Workspace = Parameters<typeof publishedDispatchMap>[1];
  const before = await dispatchRead<Snapshot>("/api/snapshot", signal);
  if (!before.map) throw new Error("主工程没有活动地图");
  const workspace = await dispatchRead<Workspace>(
    `/api/maps/${encodeURIComponent(before.map.id)}/workspace`,
    signal,
  );
  const definition = publishedDispatchMap(before, workspace);
  const after = await dispatchRead<Snapshot>("/api/snapshot", signal);
  publishedDispatchMap(after, workspace);
  if (after.map?.definitionHash !== before.map.definitionHash)
    throw new Error("拉取期间主工程地图摘要变化，请刷新重试");
  return { definition, identity: before.map };
}

export async function loadWebSimulationMap(signal?: AbortSignal) {
  const live = await dispatchRead<DispatchStatus>("/api/simulation", signal);
  if (!live.enabled || !live.map)
    throw new Error("Web 仿真中心没有可读取的当前场景地图");
  const definition = live.map;
  if (definition.unit !== "m") throw new Error("只接受米制完整 Web 仿真地图");
  const config = live.configuration;
  return {
    definition,
    protectedSites: [
      ...(config?.vehicles ?? []).map(vehicle => vehicle.initialSite),
      ...(config?.stations ?? []).map(station => station.pickupSite),
      ...(config?.dumper?.waitingSites ?? []),
      config?.dumper?.waitingSite,
      config?.dumper?.dumpingSite,
      config?.inspectionSite,
    ].filter((site): site is string => !!site),
    identity: {
      sourceType: "Web仿真实例地图",
      instanceName: live.rootName,
      sourceMapId:
        live.configuration?.mapIdentity?.mapId ||
        `simulation:${live.rootName ?? "current"}:${definition.name ?? "map"}`,
      mapName: definition.name,
      mapVersionId: live.configuration?.mapIdentity?.mapVersionId ?? "current",
      sourceName: live.configuration?.mapIdentity?.sourceName ?? definition.name,
      definitionHash:
        live.configuration?.mapIdentity?.definitionHash ||
        (await sourceDefinitionHash(definition)),
      updatedAt: new Date().toISOString(),
    },
    suggestedInitialSite:
      definition.nodes.find(
        (node) => node.enabled !== false && node.type === "ChargingPoint",
      )?.id ??
      definition.nodes.find(
        (node) => node.enabled !== false && node.type === "Station",
      )?.id ??
      "",
  };
}

export function downloadIntegrationRecord(value: unknown, name: string) {
  const url = URL.createObjectURL(
    new Blob([JSON.stringify(value, null, 2)], { type: "application/json" }),
  );
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
export type { DispatchTaskDetail };
