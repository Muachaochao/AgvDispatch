export interface MapNodeBatchInput {
  id: string;
  x: number;
  y: number;
  layerId: string;
  fill: string;
  stroke: string;
}

export interface MapNodeBatch {
  key: string;
  path: string;
  fill: string;
  stroke: string;
  nodeCount: number;
}

function circlePath(x: number, y: number, radius: number): string {
  return `M ${x - radius} ${-y} a ${radius} ${radius} 0 1 0 ${radius * 2} 0 a ${radius} ${radius} 0 1 0 ${-radius * 2} 0`;
}

export function buildMapNodeBatches(
  nodes: readonly MapNodeBatchInput[],
  radius: number
): MapNodeBatch[] {
  const safeRadius = Math.max(1e-6, radius);
  const batches = new Map<string, MapNodeBatch>();
  for (const node of nodes) {
    const key = `${node.layerId}|${node.fill}|${node.stroke}`;
    const segment = circlePath(node.x, node.y, safeRadius);
    const current = batches.get(key);
    if (current) {
      current.path += ` ${segment}`;
      current.nodeCount += 1;
      continue;
    }
    batches.set(key, {
      key,
      path: segment,
      fill: node.fill,
      stroke: node.stroke,
      nodeCount: 1
    });
  }
  return [...batches.values()];
}
