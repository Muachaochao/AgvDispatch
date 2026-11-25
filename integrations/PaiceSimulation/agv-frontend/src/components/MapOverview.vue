<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { RoadNetPayload } from '@/api/roadnet'
import { mapBounds, type MapBounds, type MapPoint } from '@/integration/map-navigation'

const props = defineProps<{ map: RoadNetPayload | null; displayedNodes?: ReadonlyMap<string, MapPoint>; view?: MapBounds | null; target?: MapPoint | null }>()
const emit = defineEmits<{ center: [point: MapPoint]; settled: [] }>()
const canvas = ref<HTMLCanvasElement>()
const collapsed = ref(false)
const width = 200, height = 108
const dragging = ref(false)
let drag: { id: number; rect: DOMRect } | null = null
let pending: MapPoint | null = null
let frame = 0
const projection = computed(() => {
  const bounds = mapBounds(props.displayedNodes?.values() ?? props.map?.nodes ?? [])
  if (!bounds) return null
  const scale = Math.min((width - 16) / Math.max(1, bounds.right - bounds.left), (height - 16) / Math.max(1, bounds.bottom - bounds.top))
  const tx = width / 2 - (bounds.left + bounds.right) / 2 * scale
  const ty = height / 2 - (bounds.top + bounds.bottom) / 2 * scale
  return { scale, tx, ty }
})
function renderMap() {
  const ctx = canvas.value?.getContext('2d')
  const p = projection.value
  if (!ctx || !p || !props.map) return
  ctx.clearRect(0, 0, width, height)
  const byId = new Map(props.map.nodes.map(node => [node.id, node]))
  const project = ({ x, y }: MapPoint) => [x * p.scale + p.tx, y * p.scale + p.ty] as const
  ctx.strokeStyle = '#849ab5'
  ctx.lineWidth = 0.65
  ctx.beginPath()
  for (const edge of props.map.edges) {
    const from = byId.get(edge.from), to = byId.get(edge.to)
    if (!from || !to) continue
    const points = [
      props.displayedNodes?.get(from.id) ?? from,
      ...(edge.points?.slice(1, -1) ?? []),
      props.displayedNodes?.get(to.id) ?? to
    ]
    ctx.moveTo(...project(points[0]!))
    for (let i = 1; i < points.length; i++) ctx.lineTo(...project(points[i]!))
  }
  ctx.stroke()
  ctx.fillStyle = '#3980b7'
  for (const node of props.map.nodes) {
    if (node.type !== 'WORKSTATION') continue
    const [x, y] = project(props.displayedNodes?.get(node.id) ?? node)
    ctx.fillRect(x - 0.7, y - 0.7, 1.4, 1.4)
  }
}
const viewport = computed(() => {
  const p = projection.value, view = props.view
  if (!p || !view) return null
  const clamp = (value: number, limit: number) => Math.max(0, Math.min(limit, value))
  const x = clamp(view.left * p.scale + p.tx, width), y = clamp(view.top * p.scale + p.ty, height)
  const right = clamp(view.right * p.scale + p.tx, width), bottom = clamp(view.bottom * p.scale + p.ty, height)
  return { left: x / width * 100 + '%', top: y / height * 100 + '%', width: (right - x) / width * 100 + '%', height: (bottom - y) / height * 100 + '%' }
})
const dot = computed(() => {
  const p = projection.value, target = props.target
  if (!p || !target) return null
  const x = target.x * p.scale + p.tx, y = target.y * p.scale + p.ty
  return x >= 0 && x <= width && y >= 0 && y <= height ? { left: x / width * 100 + '%', top: y / height * 100 + '%' } : null
})
function flushCenter() {
  cancelAnimationFrame(frame)
  frame = 0
  if (!pending) return
  emit('center', pending)
  pending = null
}

function moveDrag(event: PointerEvent) {
  const p = projection.value
  if (!p || drag?.id !== event.pointerId) return
  const { rect } = drag
  const x = Math.max(0, Math.min(width, (event.clientX - rect.left) * width / rect.width))
  const y = Math.max(0, Math.min(height, (event.clientY - rect.top) * height / rect.height))
  pending = { x: (x - p.tx) / p.scale, y: (y - p.ty) / p.scale }
  if (!frame) frame = requestAnimationFrame(flushCenter)
}

function startDrag(event: PointerEvent) {
  const el = canvas.value
  if (!el || drag || event.button !== 0 || !event.isPrimary) return
  const rect = el.getBoundingClientRect()
  if (!rect.width || !rect.height) return
  drag = { id: event.pointerId, rect }
  dragging.value = true
  el.setPointerCapture(event.pointerId)
  moveDrag(event)
}

function endDrag(event: PointerEvent) {
  if (drag?.id !== event.pointerId) return
  if (event.type === 'pointerup') moveDrag(event)
  flushCenter()
  drag = null
  dragging.value = false
  emit('settled')
}

watch([() => props.map, () => props.displayedNodes, collapsed], renderMap, { flush: 'post' })
onMounted(renderMap)
onBeforeUnmount(() => cancelAnimationFrame(frame))
</script>

<template>
  <aside v-if="projection" class="map-overview" aria-label="地图缩略图">
    <button v-if="collapsed" type="button" class="overview-toggle" @click="collapsed = false">展开缩略图</button>
    <template v-else>
      <canvas ref="canvas" :width="width" :height="height" :class="{ 'is-dragging': dragging }"
        aria-label="缩略图，点击或拖动移动视图"
        @pointerdown="startDrag" @pointermove="moveDrag" @pointerup="endDrag"
        @pointercancel="endDrag" @lostpointercapture="endDrag" />
      <span v-if="viewport" class="overview-viewport" :style="viewport" />
      <span v-if="dot" class="overview-target" :style="dot" />
      <button type="button" class="overview-toggle collapse" aria-label="收起缩略图" @click="collapsed = true">−</button>
    </template>
  </aside>
</template>

<style scoped>
.map-overview { position: absolute; z-index: 2; top: 10px; right: 10px; overflow: hidden; border: 1px solid #c9d7e5; border-radius: 6px; background: #f7faff; }
.map-overview canvas { display: block; width: clamp(116px, 16vw, 200px); height: auto; cursor: var(--map-cursor-grab); touch-action: none; }
.map-overview canvas.is-dragging { cursor: var(--map-cursor-grabbing); }
.overview-toggle { border: 0; padding: 3px 7px; border-radius: 4px; color: #354863; background: #fff; cursor: pointer; }
.overview-toggle.collapse { position: absolute; top: 3px; right: 3px; }
.overview-viewport, .overview-target { position: absolute; pointer-events: none; }
.overview-viewport { border: 1px solid #264b91; background: #3564b517; }
.overview-target { width: 7px; height: 7px; border-radius: 50%; background: #9352b2; border: 1px solid white; transform: translate(-50%, -50%); }
</style>
