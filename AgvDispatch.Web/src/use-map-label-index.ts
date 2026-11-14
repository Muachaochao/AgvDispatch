import { onMounted, onUnmounted, ref, shallowRef, watch, type Ref } from 'vue';
import {
  selectNodeLabels,
  type MapLabelMode,
  type MapLabelNode,
  type MapViewRect
} from './map-render-lod';

export interface MapLabelIndexOptions {
  mode: MapLabelMode;
  zoom: number;
  scale: number;
  view: MapViewRect;
  selectedIds: readonly string[];
}

interface WorkerResponse {
  id: number;
  ids: string[];
  durationMs: number;
}

export function useMapLabelIndex(
  nodes: Ref<readonly MapLabelNode[]>,
  options: Ref<MapLabelIndexOptions>
) {
  const ids = shallowRef<string[]>([]);
  const mode = ref<'main' | 'worker'>('main');
  const durationMs = ref(0);
  let worker: Worker | undefined;
  let requestId = 0;
  let disposed = false;

  function plainRequest() {
    const value = options.value;
    return {
      nodes: nodes.value.map(node => ({
        id: node.id,
        x: node.x,
        y: node.y,
        type: node.type,
        enabled: node.enabled
      })),
      options: {
        mode: value.mode,
        zoom: value.zoom,
        scale: value.scale,
        view: { ...value.view },
        selectedIds: [...value.selectedIds]
      }
    };
  }

  function calculateOnMain(id: number) {
    const request = plainRequest();
    const startedAt = performance.now();
    if (id !== requestId || disposed) return;
    ids.value = selectNodeLabels(request.nodes, request.options).map(node => node.id);
    durationMs.value = performance.now() - startedAt;
  }

  function schedule() {
    if (disposed) return;
    const id = ++requestId;
    if (!worker) {
      calculateOnMain(id);
      return;
    }
    const request = plainRequest();
    worker.postMessage({ id, ...request });
  }

  watch([nodes, options], schedule, { immediate: true });

  onMounted(() => {
    try {
      worker = new Worker(new URL('./map-label-worker.ts', import.meta.url), { type: 'module' });
      worker.onmessage = (event: MessageEvent<WorkerResponse>) => {
        if (disposed || event.data.id !== requestId) return;
        ids.value = event.data.ids;
        durationMs.value = event.data.durationMs;
        mode.value = 'worker';
      };
      worker.onerror = () => {
        worker?.terminate();
        worker = undefined;
        mode.value = 'main';
        schedule();
      };
      schedule();
    } catch {
      worker = undefined;
      mode.value = 'main';
      schedule();
    }
  });

  onUnmounted(() => {
    disposed = true;
    worker?.terminate();
    worker = undefined;
  });

  return { ids, mode, durationMs };
}
