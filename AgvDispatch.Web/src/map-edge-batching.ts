export interface MapEdgeBatchInput {
  id: string;
  path: string;
  layerId: string;
  color: string;
  opacity: number;
  strokeWidth: number;
  bidirectional: boolean;
}

export interface MapEdgeBatch {
  key: string;
  path: string;
  color: string;
  opacity: number;
  strokeWidth: number;
  markerStart: boolean;
  markerEnd: boolean;
  edgeCount: number;
}

export function buildMapEdgeBatches(
  edges: readonly MapEdgeBatchInput[],
  showArrowheads: boolean
): MapEdgeBatch[] {
  const batches = new Map<string, MapEdgeBatch>();
  for (const edge of edges) {
    if (!edge.path) continue;
    const markerStart = showArrowheads && edge.bidirectional;
    const markerEnd = showArrowheads;
    const key = [
      edge.layerId,
      edge.color,
      edge.opacity,
      edge.strokeWidth,
      markerStart ? 1 : 0,
      markerEnd ? 1 : 0
    ].join('|');
    const current = batches.get(key);
    if (current) {
      current.path += ` ${edge.path}`;
      current.edgeCount += 1;
      continue;
    }
    batches.set(key, {
      key,
      path: edge.path,
      color: edge.color,
      opacity: edge.opacity,
      strokeWidth: edge.strokeWidth,
      markerStart,
      markerEnd,
      edgeCount: 1
    });
  }
  return [...batches.values()];
}
