import type { WebMap } from './types.ts';

export interface RoadClosure {
  closureId: string;
  mapId: string;
  versionId: string;
  definitionHash: string;
  edgeId: string;
  fromNodeId: string;
  toNodeId: string;
  traversals: { edgeId: string; fromNodeId: string; toNodeId: string }[];
  bidirectional: boolean;
  reason: string;
  source: string;
  createdAt: string;
  active: boolean;
  releasedAt: string | null;
  releaseReason: string | null;
}

export interface RoadClosurePage {
  ready: boolean;
  receivedAt: string;
  mapId: string | null;
  versionId: string | null;
  definitionHash: string | null;
  revision: number;
  closures: RoadClosure[];
}

export interface RoadRoutePreview {
  receivedAt: string;
  mapId: string | null;
  versionId: string | null;
  definitionHash: string | null;
  revision: number;
  sourceSite: string;
  targetSite: string;
  available: boolean;
  route: { nodes: string[]; edgeIds: string[]; resourceIds: string[]; cost: number } | null;
  message: string;
}

export function closureMapMatches(
  record: Pick<RoadClosurePage, 'mapId' | 'versionId' | 'definitionHash'> | null,
  map: WebMap | null | undefined
): boolean {
  return !!record && !!map && !!map.definitionHash && record.mapId === map.id &&
    record.versionId === map.version && record.definitionHash?.toLowerCase() === map.definitionHash.toLowerCase();
}

export function closurePageCurrent(page: RoadClosurePage | null, map: WebMap | null | undefined,
  fresh: boolean, now: number): boolean {
  const age = now - Date.parse(page?.receivedAt ?? '');
  return fresh && page?.ready === true && closureMapMatches(page, map) && Number.isFinite(age) && age >= -1000 && age <= 15000;
}

export function closureRouteCurrent(preview: RoadRoutePreview | null, page: RoadClosurePage | null,
  map: WebMap | null | undefined, source: string, destination: string, fresh: boolean, now: number): boolean {
  const age = now - Date.parse(preview?.receivedAt ?? '');
  return !!preview && closurePageCurrent(page, map, fresh, now) && closureMapMatches(preview, map) &&
    preview.revision === page?.revision && preview.sourceSite === source && preview.targetSite === destination &&
    Number.isFinite(age) && age >= -1000 && age <= 15000;
}

export function closureSites(map: WebMap | null | undefined): { id: string; label: string }[] {
  return (map?.nodes ?? []).filter(node => node.enabled).map(node => ({
    id: node.id,
    label: node.agvSiteId && node.agvSiteId !== node.id ? `${node.agvSiteId}（${node.id}）` : node.id
  }));
}
