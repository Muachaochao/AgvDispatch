<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from 'vue';
import { annotationLabel } from '../area-annotations';
import { arcInfo, type Layer } from '../map-editing';
import { edgePath, type AreaAnnotation, type Definition, type Edge, type Node, type Resource, type SceneItem } from '../workbench';

const props = defineProps<{
  definition: Definition;
  scene: SceneItem[];
  nodes: Node[];
  edges: Edge[];
  resources: Resource[];
  layers: Layer[];
  areas: AreaAnnotation[];
  labels: ReadonlySet<string>;
  viewBox: string;
  scale: number;
  showArrowheads: boolean;
  navigating: boolean;
  simplified: boolean;
  revision: number;
}>();

const canvas = ref<HTMLCanvasElement>();
let frame = 0;
let observer: ResizeObserver | undefined;
let themeObserver: MutationObserver | undefined;
const OVERSCAN = 0.25;
const OVERSCAN_EDGE = OVERSCAN / 2;
interface NavigationBase {
  view: { x: number; y: number; width: number; height: number };
  expanded: { x: number; y: number; width: number; height: number };
  scale: number;
  offsetX: number;
  offsetY: number;
}
let navigationBase: NavigationBase | undefined;
interface EdgeCache {
  edges: Edge[];
  layers: Layer[];
  revision: number;
  scale: number;
  showArrowheads: boolean;
  batches: { path: Path2D; color: string; opacity: number; lineWidth: number }[];
  arrows: { tip: { x: number; y: number }; direction: { x: number; y: number }; color: string }[];
}
interface NodeCache {
  nodes: Node[];
  layers: Layer[];
  revision: number;
  scale: number;
  batches: { path: Path2D; fill: string; stroke: string; lineWidth: number; opacity: number }[];
}
let edgeCache: EdgeCache | undefined;
let nodeCache: NodeCache | undefined;

function layerFor(item: { layerId?: string }): Layer | undefined {
  const id = item.layerId || 'navigation';
  return props.layers.find(layer => layer.id === id);
}

function styleColors() {
  const style = getComputedStyle(canvas.value!);
  return {
    page: style.getPropertyValue('--ui-page-deep').trim() || '#050b14',
    text: style.getPropertyValue('--ui-text').trim() || '#e7f0fa',
    light: document.documentElement.dataset.theme === 'light'
  };
}

function drawArrow(
  context: CanvasRenderingContext2D,
  tip: { x: number; y: number },
  direction: { x: number; y: number },
  color: string
) {
  const size = props.scale;
  const length = Math.hypot(direction.x, direction.y);
  if (length < 1e-8) return;
  const dx = direction.x / length;
  const dy = direction.y / length;
  const base = { x: tip.x - dx * size, y: tip.y - dy * size };
  const normal = { x: -dy * size * 0.45, y: dx * size * 0.45 };
  context.beginPath();
  context.moveTo(tip.x, tip.y);
  context.lineTo(base.x + normal.x, base.y + normal.y);
  context.lineTo(base.x - normal.x, base.y - normal.y);
  context.closePath();
  context.fillStyle = color;
  context.fill();
}

function edgeDirection(edge: Edge, nodes: Map<string, Node>): { x: number; y: number } {
  const from = nodes.get(edge.from);
  const to = nodes.get(edge.to);
  if (!from || !to) return { x: 0, y: 0 };
  if (edge.geometryKind === 'CubicBezier' && edge.controlPoints?.length === 2) {
    const control = edge.controlPoints[1]!;
    return { x: to.x - control.x, y: -(to.y - control.y) };
  }
  if (edge.geometryKind === 'CircularArc' && edge.controlPoints?.length === 1) {
    const arc = arcInfo(from, edge.controlPoints[0]!, to);
    if (arc) {
      const angle = arc.start + arc.sweep;
      const tangent = arc.sweep >= 0
        ? { x: -Math.sin(angle), y: Math.cos(angle) }
        : { x: Math.sin(angle), y: -Math.cos(angle) };
      return { x: tangent.x, y: -tangent.y };
    }
  }
  return { x: to.x - from.x, y: -(to.y - from.y) };
}

function drawScene(context: CanvasRenderingContext2D) {
  for (const item of props.scene) {
    const layer = layerFor(item);
    context.globalAlpha = layer?.alpha ?? 1;
    if (item.kind !== 'text') {
      context.fillStyle = item.color;
      context.globalAlpha = (layer?.alpha ?? 1) * 0.18;
      context.fillRect(item.x, -item.y - item.height, item.width, item.height);
      context.globalAlpha = layer?.alpha ?? 1;
      context.strokeStyle = item.color;
      context.lineWidth = props.scale * 0.12;
      context.strokeRect(item.x, -item.y - item.height, item.width, item.height);
    }
    context.fillStyle = item.color;
    context.font = `${props.scale * 1.1}px "Cascadia Mono", Consolas, "Microsoft YaHei UI", sans-serif`;
    context.fillText(item.name, item.x + props.scale * 0.2, -item.y - item.height + props.scale * 1.2);
  }
  context.globalAlpha = 1;
}

function drawResources(context: CanvasRenderingContext2D) {
  for (const resource of props.resources) {
    if (!resource.boundary?.length) continue;
    const layer = layerFor(resource);
    context.globalAlpha = (layer?.alpha ?? 1) * 0.09;
    context.fillStyle = '#e0b25b';
    context.beginPath();
    resource.boundary.forEach((point, index) => {
      if (index === 0) context.moveTo(point.x, -point.y);
      else context.lineTo(point.x, -point.y);
    });
    context.closePath();
    context.fill();
    context.globalAlpha = layer?.alpha ?? 1;
    context.strokeStyle = layer?.color || '#b79a68';
    context.lineWidth = props.scale * (layer?.strokeWidth ?? 2) / 10;
    context.stroke();
  }
  context.globalAlpha = 1;
}

function drawAnnotations(context: CanvasRenderingContext2D, colors: ReturnType<typeof styleColors>) {
  for (const annotation of props.areas) {
    if (annotation.boundary.length < 2) continue;
    context.beginPath();
    annotation.boundary.forEach((point, index) => {
      if (index === 0) context.moveTo(point.x, -point.y);
      else context.lineTo(point.x, -point.y);
    });
    context.closePath();
    context.globalAlpha = annotation.fillOpacity;
    context.fillStyle = annotation.fillColor;
    context.fill();
    context.globalAlpha = 1;
    context.strokeStyle = annotation.strokeColor;
    context.lineWidth = 1.5 * props.scale / 10;
    context.stroke();
    const label = annotationLabel(annotation);
    context.font = `${props.scale * 1.2}px "Microsoft YaHei UI", "Microsoft YaHei", sans-serif`;
    context.textAlign = 'center';
    context.lineJoin = 'round';
    context.lineWidth = props.scale * 0.22;
    context.strokeStyle = colors.light ? '#ffffff' : '#0b1229';
    context.strokeText(annotation.text, label.x, -label.y - props.scale * 0.5);
    context.fillStyle = colors.light ? '#6c3f7c' : '#f2c5ff';
    context.fillText(annotation.text, label.x, -label.y - props.scale * 0.5);
    context.textAlign = 'start';
  }
}

function drawEdges(context: CanvasRenderingContext2D, nodeMap: Map<string, Node>): { batches: number; arrows: number } {
  const cacheValid = edgeCache?.edges === props.edges &&
    edgeCache.layers === props.layers &&
    edgeCache.revision === props.revision &&
    edgeCache.scale === props.scale &&
    edgeCache.showArrowheads === props.showArrowheads;
  if (!cacheValid) {
    const batches = new Map<string, { path: Path2D; color: string; opacity: number; lineWidth: number }>();
    const arrows: EdgeCache['arrows'] = [];
    for (const edge of props.edges) {
      const path = edgePath(edge, props.definition);
      if (!path) continue;
      const layer = layerFor(edge);
      const color = layer?.color || (edge.enabled ? '#40a6c4' : '#566175');
      const opacity = layer?.alpha ?? 1;
      const lineWidth = props.scale * (layer?.strokeWidth ?? 2) / 10;
      const key = `${edge.layerId || 'navigation'}|${color}|${opacity}|${lineWidth}`;
      const current = batches.get(key);
      if (current) {
        current.path.addPath(new Path2D(path));
      } else {
        batches.set(key, { path: new Path2D(path), color, opacity, lineWidth });
      }
      if (!props.showArrowheads) continue;
      const from = nodeMap.get(edge.from);
      const to = nodeMap.get(edge.to);
      if (!from || !to) continue;
      const direction = edgeDirection(edge, nodeMap);
      arrows.push({ tip: { x: to.x, y: -to.y }, direction, color });
      if (edge.bidirectional) {
        arrows.push({ tip: { x: from.x, y: -from.y }, direction: { x: -direction.x, y: -direction.y }, color });
      }
    }
    edgeCache = {
      edges: props.edges,
      layers: props.layers,
      revision: props.revision,
      scale: props.scale,
      showArrowheads: props.showArrowheads,
      batches: [...batches.values()],
      arrows
    };
  }
  const cache = edgeCache!;
  for (const batch of cache.batches) {
    context.globalAlpha = batch.opacity;
    context.strokeStyle = batch.color;
    context.lineWidth = batch.lineWidth;
    context.stroke(batch.path);
  }
  context.globalAlpha = 1;
  if (!props.simplified && props.showArrowheads) {
    for (const arrow of cache.arrows) drawArrow(context, arrow.tip, arrow.direction, arrow.color);
  }
  return {
    batches: cache.batches.length,
    arrows: props.simplified ? 0 : cache.arrows.length
  };
}

function drawNodes(context: CanvasRenderingContext2D, colors: ReturnType<typeof styleColors>) {
  const radius = Math.max(1e-6, props.scale * 0.55);
  const labelFont = Math.max(1e-6, props.scale * 1.05);
  const labelOffsetX = props.scale * 0.85;
  const labelOffsetY = props.scale * 0.65;
  const cacheValid = nodeCache?.nodes === props.nodes &&
    nodeCache.layers === props.layers &&
    nodeCache.revision === props.revision &&
    nodeCache.scale === props.scale;
  if (!cacheValid) {
    const batches = new Map<string, { path: Path2D; fill: string; stroke: string; lineWidth: number; opacity: number }>();
    for (const node of props.nodes) {
      const layer = layerFor(node);
      const fill = layer?.color || (node.enabled ? '#0d344a' : '#344254');
      const stroke = layer?.color || (node.enabled ? '#59bfd9' : '#71829a');
      const lineWidth = props.scale * 0.15;
      const opacity = layer?.alpha ?? 1;
      const key = `${node.layerId || 'navigation'}|${fill}|${stroke}|${lineWidth}|${opacity}`;
      let batch = batches.get(key);
      if (!batch) {
        batch = { path: new Path2D(), fill, stroke, lineWidth, opacity };
        batches.set(key, batch);
      }
      batch.path.moveTo(node.x + radius, -node.y);
      batch.path.arc(node.x, -node.y, radius, 0, Math.PI * 2);
    }
    nodeCache = {
      nodes: props.nodes,
      layers: props.layers,
      revision: props.revision,
      scale: props.scale,
      batches: [...batches.values()]
    };
  }
  const cache = nodeCache!;
  for (const batch of cache.batches) {
    context.globalAlpha = batch.opacity;
    context.fillStyle = batch.fill;
    context.fill(batch.path);
    context.strokeStyle = batch.stroke;
    context.lineWidth = batch.lineWidth;
    context.stroke(batch.path);
  }
  context.globalAlpha = 1;
  if (props.simplified) return;
  context.font = `${labelFont}px "Cascadia Mono", Consolas, "Microsoft YaHei UI", sans-serif`;
  context.textAlign = 'start';
  context.textBaseline = 'alphabetic';
  for (const node of props.nodes) {
    if (!props.labels.has(node.id)) continue;
    const layer = layerFor(node);
    context.globalAlpha = layer?.alpha ?? 1;
    const x = node.x + labelOffsetX;
    const y = -node.y - labelOffsetY;
    context.lineJoin = 'round';
    context.lineWidth = props.scale * 0.22;
    context.strokeStyle = colors.page;
    context.strokeText(node.id, x, y);
    context.fillStyle = colors.text;
    context.fillText(node.id, x, y);
  }
  context.globalAlpha = 1;
}

function draw() {
  const element = canvas.value;
  const container = element?.parentElement;
  if (!element || !container) return;
  const containerBounds = container.getBoundingClientRect();
  if (!containerBounds.width || !containerBounds.height) return;
  const [x, y, viewWidth, viewHeight] = props.viewBox.split(/\s+/).map(Number);
  if (![x, y, viewWidth, viewHeight].every(Number.isFinite) || viewWidth <= 0 || viewHeight <= 0) return;
  if (props.navigating && navigationBase) {
    const currentScale = Math.min(containerBounds.width / viewWidth, containerBounds.height / viewHeight);
    if (currentScale + 1e-9 < navigationBase.scale) {
      navigationBase = undefined;
    } else {
    const currentOffsetX = (containerBounds.width - viewWidth * currentScale) / 2;
    const currentOffsetY = (containerBounds.height - viewHeight * currentScale) / 2;
    const scale = currentScale / navigationBase.scale;
    const canvasLeft = -OVERSCAN_EDGE * containerBounds.width;
    const canvasTop = -OVERSCAN_EDGE * containerBounds.height;
    const translateX = currentOffsetX - canvasLeft - scale * navigationBase.offsetX +
      (scale * navigationBase.expanded.x - x) * currentScale;
    const translateY = currentOffsetY - canvasTop - scale * navigationBase.offsetY +
      (scale * navigationBase.expanded.y - y) * currentScale;
    element.style.transform = Math.abs(scale - 1) < 1e-6
      ? `translate3d(${translateX}px, ${translateY}px, 0)`
      : `matrix3d(${scale}, 0, 0, 0, 0, ${scale}, 0, 0, 0, 0, 1, 0, ${translateX}, ${translateY}, 0, 1)`;
    element.dataset.renderMode = 'navigation';
    element.dataset.renderNavigation = 'transform';
    element.dataset.renderLabels = '0';
    element.dataset.renderArrows = '0';
    return;
    }
  }
  element.style.transform = 'none';
  const ratio = window.devicePixelRatio || 1;
  const canvasWidth = containerBounds.width * (1 + OVERSCAN);
  const canvasHeight = containerBounds.height * (1 + OVERSCAN);
  const width = Math.max(1, Math.round(canvasWidth * ratio));
  const height = Math.max(1, Math.round(canvasHeight * ratio));
  if (element.width !== width || element.height !== height) {
    element.width = width;
    element.height = height;
  }
  const context = element.getContext('2d');
  if (!context) return;
  context.setTransform(1, 0, 0, 1, 0, 0);
  context.clearRect(0, 0, width, height);
  const expanded = {
    x: x - viewWidth * OVERSCAN_EDGE,
    y: y - viewHeight * OVERSCAN_EDGE,
    width: viewWidth * (1 + OVERSCAN),
    height: viewHeight * (1 + OVERSCAN)
  };
  const cssScale = Math.min(canvasWidth / expanded.width, canvasHeight / expanded.height);
  const offsetCssX = (canvasWidth - expanded.width * cssScale) / 2;
  const offsetCssY = (canvasHeight - expanded.height * cssScale) / 2;
  context.setTransform(
    cssScale * ratio,
    0,
    0,
    cssScale * ratio,
    (offsetCssX - expanded.x * cssScale) * ratio,
    (offsetCssY - expanded.y * cssScale) * ratio
  );
  context.lineCap = 'round';
  context.lineJoin = 'round';

  const colors = styleColors();
  const nodeMap = new Map(props.nodes.map(node => [node.id, node]));
  if (!props.simplified) {
    drawAnnotations(context, colors);
    drawScene(context);
    drawResources(context);
  }
  const edgeStats = drawEdges(context, nodeMap);
  drawNodes(context, colors);

  element.dataset.renderNodes = String(props.nodes.length);
  element.dataset.renderEdges = String(props.edges.length);
  element.dataset.renderEdgeBatches = String(edgeStats.batches);
  element.dataset.renderArrows = String(edgeStats.arrows);
  element.dataset.renderLabels = String(props.labels.size);
  element.dataset.renderMode = props.simplified ? 'simplified' : 'detail';
  element.dataset.renderNavigation = 'redraw';
  navigationBase = {
    view: { x, y, width: viewWidth, height: viewHeight },
    expanded,
    scale: cssScale,
    offsetX: offsetCssX,
    offsetY: offsetCssY
  };
}

function schedule() {
  if (frame) return;
  frame = requestAnimationFrame(() => {
    frame = 0;
    draw();
  });
}

watch(
  () => [props.viewBox, props.revision, props.navigating, props.simplified, props.showArrowheads, props.scale, props.nodes, props.edges, props.resources, props.scene, props.areas, props.labels],
  schedule,
  { flush: 'post' }
);

onMounted(() => {
  observer = new ResizeObserver(() => {
    navigationBase = undefined;
    schedule();
  });
  observer.observe(canvas.value!);
  themeObserver = new MutationObserver(schedule);
  themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme', 'class'] });
  schedule();
});

onUnmounted(() => {
  if (frame) cancelAnimationFrame(frame);
  observer?.disconnect();
  themeObserver?.disconnect();
});
</script>

<template>
  <canvas ref="canvas" class="map-static-canvas" aria-hidden="true"/>
</template>

<style scoped>
.map-static-canvas {
  position: absolute;
  top: -12.5%;
  left: -12.5%;
  display: block;
  width: 125%;
  height: 125%;
  pointer-events: none;
  transform-origin: 0 0;
  will-change: transform;
  contain: strict;
  backface-visibility: hidden;
}
</style>
