import type { RoadNetPayload } from "../api/roadnet.ts";

export interface MapPoint { x: number; y: number }
export interface MapBounds { left: number; top: number; right: number; bottom: number }
export interface MapLocation extends MapPoint {
  id: string;
  kind: "node" | "edge" | "vehicle";
  aliases?: string[];
}

export function mapNavigationItems(map: RoadNetPayload | null, displayedNodes?: ReadonlyMap<string, MapPoint>): MapLocation[] {
  if (!map) return [];
  const nodes = new Map(map.nodes.map(node => [node.id, node]));
  const source = map.integration?.sourceDefinition as { nodes?: { id: string; agvSiteId?: string }[] } | undefined;
  const aliases = new Map(source?.nodes?.map(node => [node.id, node.agvSiteId]));
  const originalRoads = new Map<string, string[]>();
  const edgeMapping = map.integration?.edgeMapping as Record<string, string[]> | undefined;
  for (const [sourceId, segments] of Object.entries(edgeMapping ?? {})) {
    if (!Array.isArray(segments)) continue;
    for (const segment of segments) {
      const values = originalRoads.get(segment) ?? [];
      values.push(sourceId);
      originalRoads.set(segment, values);
    }
  }
  const result: MapLocation[] = map.nodes.map(node => {
    const location = displayedNodes?.get(node.id) ?? node;
    const alias = node.agvSiteId ?? aliases.get(node.id);
    return { id: node.id, kind: "node", x: location.x, y: location.y, aliases: alias ? [alias] : [] };
  });
  for (const edge of map.edges) {
    const from = displayedNodes?.get(edge.from) ?? nodes.get(edge.from);
    const to = displayedNodes?.get(edge.to) ?? nodes.get(edge.to);
    if (!from || !to) continue;
    const middle = edge.points?.[Math.floor(edge.points.length / 2)];
    const point = edge.points && edge.points.length > 2 && middle ? middle : { x: (from.x + to.x) / 2, y: (from.y + to.y) / 2 };
    result.push({ ...point, id: edge.id, kind: "edge", aliases: originalRoads.get(edge.id) });
  }
  return result;
}

export function findMapMatches(items: MapLocation[], query: string) {
  const term = query.trim().toLowerCase();
  if (!term) return [];
  const exact: MapLocation[] = [], partial: MapLocation[] = [];
  for (const item of items) {
    const names = [item.id, ...(item.aliases ?? [])].map(id => id.toLowerCase());
    if (names.includes(term)) exact.push(item);
    else if (names.some(id => id.includes(term))) partial.push(item);
  }
  return [...exact, ...partial];
}

export function mapBounds(points: Iterable<MapPoint>): MapBounds | null {
  let left = Infinity, right = -Infinity, top = Infinity, bottom = -Infinity;
  for (const { x, y } of points) {
    if (!Number.isFinite(x) || !Number.isFinite(y)) continue;
    left = Math.min(left, x); right = Math.max(right, x);
    top = Math.min(top, y); bottom = Math.max(bottom, y);
  }
  return Number.isFinite(left) ? { left, right, top, bottom } : null;
}

export async function toggleMapFullscreen(target: HTMLElement | null | undefined) {
  if (!target) return;
  if (document.fullscreenElement === target) await document.exitFullscreen();
  else await target.requestFullscreen();
}
