export type MapLabelMode = 'auto' | 'key' | 'all' | 'none';

export interface MapLabelNode {
  id: string;
  x: number;
  y: number;
  type: string;
  enabled: boolean;
}

export interface MapViewRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface MapLabelOptions {
  mode: MapLabelMode;
  zoom: number;
  scale: number;
  view: MapViewRect;
  selectedIds: readonly string[];
}

interface Rect {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

const KEY_NODE_TYPES = new Set(['Station', 'Intersection', 'ChargingPoint', 'WaitingPoint']);
const ALL_LABELS_ZOOM = 2.5;
const SELECTED_PRIORITY = 0;
const PRIORITY_BY_TYPE: Record<string, number> = {
  Station: 10,
  Intersection: 12,
  ChargingPoint: 20,
  WaitingPoint: 24,
  Waypoint: 40
};

function overlaps(left: Rect, right: Rect): boolean {
  return left.minX < right.maxX &&
    left.maxX > right.minX &&
    left.minY < right.maxY &&
    left.maxY > right.minY;
}

function cellRange(rect: Rect, cellSize: number): [number, number, number, number] {
  return [
    Math.floor(rect.minX / cellSize),
    Math.floor(rect.minY / cellSize),
    Math.floor(rect.maxX / cellSize),
    Math.floor(rect.maxY / cellSize)
  ];
}

function insertRect(grid: Map<string, Rect[]>, rect: Rect, cellSize: number): void {
  const [minCellX, minCellY, maxCellX, maxCellY] = cellRange(rect, cellSize);
  for (let cellX = minCellX; cellX <= maxCellX; cellX += 1) {
    for (let cellY = minCellY; cellY <= maxCellY; cellY += 1) {
      const key = `${cellX}:${cellY}`;
      const bucket = grid.get(key);
      if (bucket) bucket.push(rect);
      else grid.set(key, [rect]);
    }
  }
}

function collides(grid: Map<string, Rect[]>, rect: Rect, cellSize: number): boolean {
  const [minCellX, minCellY, maxCellX, maxCellY] = cellRange(rect, cellSize);
  for (let cellX = minCellX; cellX <= maxCellX; cellX += 1) {
    for (let cellY = minCellY; cellY <= maxCellY; cellY += 1) {
      for (const occupied of grid.get(`${cellX}:${cellY}`) ?? []) {
        if (overlaps(rect, occupied)) return true;
      }
    }
  }
  return false;
}

function labelRect(node: MapLabelNode, scale: number): Rect {
  const fontSize = scale * 1.05;
  const anchorX = node.x + scale * 0.85;
  const baseline = -node.y - scale * 0.65;
  return {
    minX: anchorX - scale * 0.12,
    minY: baseline - fontSize,
    maxX: anchorX + Math.max(scale * 2, node.id.length * fontSize * 0.62),
    maxY: baseline + scale * 0.22
  };
}

function nodeMarkerRect(node: MapLabelNode, scale: number): Rect {
  const radius = scale * 0.7;
  return {
    minX: node.x - radius,
    minY: -node.y - radius,
    maxX: node.x + radius,
    maxY: -node.y + radius
  };
}

function insideView(node: MapLabelNode, view: MapViewRect, scale: number): boolean {
  const margin = scale * 12;
  return node.x >= view.x - margin &&
    node.x <= view.x + view.width + margin &&
    -node.y >= view.y - margin &&
    -node.y <= view.y + view.height + margin;
}

export function isKeyNodeType(type: string): boolean {
  return KEY_NODE_TYPES.has(type);
}

export function nodeLabelPriority(node: MapLabelNode, selected: boolean): number {
  if (selected) return SELECTED_PRIORITY;
  return (PRIORITY_BY_TYPE[node.type] ?? 50) + (node.enabled ? 0 : 5);
}

export function shouldShowAllNodeLabels(mode: MapLabelMode, zoom: number): boolean {
  if (mode === 'all') return true;
  return mode === 'auto' && zoom >= ALL_LABELS_ZOOM;
}

export function selectNodeLabels(nodes: readonly MapLabelNode[], options: MapLabelOptions): MapLabelNode[] {
  if (options.mode === 'none') return [];
  const scale = Math.max(1e-6, options.scale);
  const selected = new Set(options.selectedIds);
  const showAll = shouldShowAllNodeLabels(options.mode, options.zoom);
  const candidates = nodes
    .filter(node => insideView(node, options.view, scale))
    .filter(node => selected.has(node.id) || showAll || isKeyNodeType(node.type))
    .map(node => ({ node, rect: labelRect(node, scale), priority: nodeLabelPriority(node, selected.has(node.id)) }))
    .sort((left, right) => left.priority - right.priority || left.node.id.localeCompare(right.node.id));

  const markerGrid = new Map<string, Rect[]>();
  const cellSize = scale * 8;
  for (const node of nodes) {
    if (!selected.has(node.id) || !insideView(node, options.view, scale)) continue;
    insertRect(markerGrid, nodeMarkerRect(node, scale), cellSize);
  }

  const accepted: MapLabelNode[] = [];
  for (const candidate of candidates) {
    if (collides(markerGrid, candidate.rect, cellSize)) continue;
    accepted.push(candidate.node);
    insertRect(markerGrid, candidate.rect, cellSize);
  }
  return accepted;
}
