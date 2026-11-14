import { selectNodeLabels, type MapLabelOptions, type MapLabelNode } from './map-render-lod';

interface LabelIndexRequest {
  id: number;
  nodes: MapLabelNode[];
  options: MapLabelOptions;
}

interface LabelIndexResponse {
  id: number;
  ids: string[];
  durationMs: number;
}

self.onmessage = (event: MessageEvent<LabelIndexRequest>) => {
  const { id, nodes, options } = event.data;
  const startedAt = performance.now();
  const ids = selectNodeLabels(nodes, options).map(node => node.id);
  const response: LabelIndexResponse = {
    id,
    ids,
    durationMs: performance.now() - startedAt
  };
  self.postMessage(response);
};
