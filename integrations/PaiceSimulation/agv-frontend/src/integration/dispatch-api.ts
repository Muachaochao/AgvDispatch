import {
  publishedDispatchMap,
  selectWebSimulationMap,
  sourceDefinitionHash,
  type SourceMap,
  type DispatchCar,
  type DispatchTaskDetail,
  type WebMapSummary,
} from "./dispatch-adapter";

export interface DispatchStatus {
  enabled: boolean;
  running?: boolean;
  fresh?: boolean;
  rootName?: string;
  error?: string;
  message?: string;
  map?: SourceMap | null;
  device?: { time: number; cars?: Record<string, DispatchCar> } | null;
  configuration?: {
    mapIdentity?: {
      mapId?: string;
      mapVersionId?: string;
      definitionHash?: string;
    };
  };
  schemes?: {
    id: string;
    name: string;
    vehicleCount?: number;
    stationCount?: number;
  }[];
  scenario?: { name?: string; status?: string; message?: string };
  report?: {
    id?: string;
    status?: string;
    metrics?: Record<string, unknown>;
    checks?: { name: string; status: string }[];
  };
}
export interface TaskRow {
  taskId: string;
  vehicleId?: string | null;
  stationId?: string;
  stage: string;
}
export interface ResourceRow {
  resourceId: string;
  capacity: number;
  occupiedCount: number;
  leases?: { vehicleId: string; taskId: string }[];
}

export async function dispatchRead<T>(
  path: string,
  signal?: AbortSignal,
  body?: Record<string, unknown>,
): Promise<T> {
  const response = await fetch("/dispatch-api" + path, {
    method: body ? "POST" : "GET",
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
    signal: signal
      ? AbortSignal.any([signal, AbortSignal.timeout(15000)])
      : AbortSignal.timeout(15000),
  });
  const text = await response.text();
  let result: unknown;
  try {
    result = JSON.parse(text);
  } catch {
    throw new Error(
      `主工程接口不可达或返回无效内容（HTTP ${response.status}）`,
    );
  }
  if (!response.ok)
    throw new Error(
      (result as { error?: string; message?: string }).error ??
        (result as { message?: string }).message ??
        `主工程接口 HTTP ${response.status}`,
    );
  return result as T;
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
  const maps = await dispatchRead<WebMapSummary[]>("/api/maps", signal);
  const selected = selectWebSimulationMap(maps);
  if (!selected) throw new Error("Web 中没有可用的已发布米制仿真地图");
  type Workspace = Parameters<typeof publishedDispatchMap>[1];
  const workspace = await dispatchRead<Workspace>(
    `/api/maps/${encodeURIComponent(selected.mapId)}/workspace`,
    signal,
  );
  const published = workspace.published;
  if (
    !published ||
    published.status !== "Published" ||
    published.mapId !== selected.mapId ||
    published.versionId !== selected.publishedVersionId
  )
    throw new Error("Web 仿真地图发布版本已变化，请刷新后重试");
  const definition = published.definition;
  if (definition.unit !== "m") throw new Error("只接受米制完整 Web 仿真地图");
  return {
    definition,
    identity: {
      sourceType: "Web仿真地图",
      sourceMapId: selected.mapId,
      mapName: selected.name,
      mapVersionId: selected.publishedVersionId,
      definitionHash: await sourceDefinitionHash(definition),
      updatedAt: selected.updatedAt,
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
