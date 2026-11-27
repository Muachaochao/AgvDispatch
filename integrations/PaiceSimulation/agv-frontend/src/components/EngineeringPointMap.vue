<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import type { RoadNetPayload } from '@/api/roadnet'
import type { EngineeringPoint } from '@/integration/engineering-points'
import MapNavigationToolbar from './MapNavigationToolbar.vue'
import MapOverview from './MapOverview.vue'
import { mapBounds, mapNavigationItems, toggleMapFullscreen, type MapBounds, type MapLocation, type MapPoint } from '@/integration/map-navigation'

const props = defineProps<{
  map: RoadNetPayload
  markers: EngineeringPoint[]
  selectedId: string
  highlightEdges?: ReadonlySet<string>
  diagnostic?: boolean
  stationOnly?: boolean
}>()
const emit = defineEmits<{ select: [id: string]; inspect: [id: string] }>()
const surface = ref<HTMLDivElement>()
const canvas = ref<HTMLCanvasElement>()
const zoom = ref(1)
const labels = ref(false)
const marked = ref(false)
const center = shallowRef<MapPoint | null>(null)
const located = shallowRef<MapLocation | null>(null)
const viewport = shallowRef<MapBounds | null>(null)
const items = computed(() => mapNavigationItems(props.map))
const bounds = computed(() => mapBounds(props.map.nodes))
const selectedNode = computed(() => props.map.nodes.find(node => node.id === props.selectedId))
const target = computed(() => located.value ?? selectedNode.value ?? null)
const dragging = ref(false)
const markerById = computed(() => {
  const markers = new Map<string, EngineeringPoint>()
  for (const marker of props.markers) {
    if (!markers.has(marker.nodeId) || marker.group === 'vehicle') markers.set(marker.nodeId, marker)
  }
  return markers
})
const roadPaths = computed(() => {
  const normal = new Path2D()
  const highlighted = new Path2D()
  const byId = new Map(props.map.nodes.map(node => [node.id, node]))
  const locatedEdge = located.value?.kind === 'edge' ? located.value.id : null
  for (const edge of props.map.edges) {
    const from = byId.get(edge.from), to = byId.get(edge.to)
    if (!from || !to) continue
    const path = marked.value || locatedEdge === edge.id || props.highlightEdges?.has(edge.id) ? highlighted : normal
    const points = edge.points?.length ? edge.points : [from, to]
    path.moveTo(points[0]!.x, points[0]!.y)
    for (const point of points.slice(1)) path.lineTo(point.x, point.y)
  }
  return { normal, highlighted }
})
let observer: ResizeObserver | undefined
let frame = 0
let drag: { pointerId: number; x: number; y: number; center: MapPoint; scale: number; moved: boolean } | null = null
let renderedCenter: MapPoint = { x: 0, y: 0 }
let renderedScale = 1
let renderedZoom = 1

function fit() { zoom.value = 1; center.value = null }
function zoomBy(factor: number) {
  center.value = renderedCenter
  zoom.value = Math.max(0.5, Math.min(12, zoom.value * factor))
}
function pan(x: number, y: number) {
  const step = 60 / renderedScale
  center.value = { x: renderedCenter.x + x * step, y: renderedCenter.y + y * step }
}
function locate(item: MapLocation) {
  marked.value = false
  located.value = item
  center.value = item
  zoom.value = Math.max(zoom.value, 2.5)
  if (item.kind === 'node') emit('inspect', item.id)
}
function clear() { marked.value = false; located.value = null }

function requestPaint() {
  if (!frame) frame = requestAnimationFrame(paint)
}

function paint() {
  frame = 0
  const el = canvas.value
  const ctx = el?.getContext('2d')
  if (!el || !ctx || !props.map.nodes.length) return
  const width = el.clientWidth, height = el.clientHeight
  if (!width || !height) return
  const ratio = Math.min(window.devicePixelRatio, 2)
  const pixelWidth = Math.round(width * ratio), pixelHeight = Math.round(height * ratio)
  if (el.width !== pixelWidth) el.width = pixelWidth
  if (el.height !== pixelHeight) el.height = pixelHeight
  ctx.setTransform(ratio, 0, 0, ratio, 0, 0)
  ctx.fillStyle = '#f3f7fc'
  ctx.fillRect(0, 0, width, height)
  const extent = bounds.value
  if (!extent) return
  const { left, right, top, bottom } = extent
  const scale = Math.min(Math.max(1, width - 40) / Math.max(1, right - left), Math.max(1, height - 40) / Math.max(1, bottom - top)) * zoom.value
  const focused = center.value ?? (zoom.value > 1 ? target.value : null)
  const midX = focused?.x ?? (left + right) / 2, midY = focused?.y ?? (top + bottom) / 2
  renderedCenter = { x: midX, y: midY }
  renderedScale = scale
  renderedZoom = zoom.value
  viewport.value = { left: midX - width / 2 / scale, right: midX + width / 2 / scale, top: midY - height / 2 / scale, bottom: midY + height / 2 / scale }
  const x = (value: number) => (value - midX) * scale + width / 2
  const y = (value: number) => (value - midY) * scale + height / 2
  ctx.save()
  ctx.translate(x(0), y(0))
  ctx.scale(scale, scale)
  ctx.strokeStyle = '#a0b4c9'
  ctx.lineWidth = 1 / scale
  ctx.stroke(roadPaths.value.normal)
  ctx.strokeStyle = '#7540a8'
  ctx.lineWidth = 2.5 / scale
  ctx.stroke(roadPaths.value.highlighted)
  ctx.restore()

  const stations = new Path2D()
  const junctions = new Path2D()
  const overlays = []
  const selectedId = located.value?.kind === 'node' ? located.value.id : props.selectedId
  for (const node of props.map.nodes) {
    const cx = x(node.x), cy = y(node.y)
    if (cx < -8 || cy < -8 || cx > width + 8 || cy > height + 8) continue
    const marker = markerById.value.get(node.id)
    const selected = node.id === selectedId
    if (selected || marker) {
      overlays.push({ node, marker, cx, cy, selected })
      continue
    }
    const station = node.type === 'WORKSTATION'
    const path = station ? stations : junctions
    const radius = station ? 2.3 : 1
    path.moveTo(cx + radius, cy)
    path.arc(cx, cy, radius, 0, Math.PI * 2)
  }
  ctx.fillStyle = marked.value ? '#7950a3' : '#acbac9'
  ctx.fill(junctions)
  ctx.fillStyle = marked.value ? '#7950a3' : '#197c9d'
  ctx.fill(stations)
  if (labels.value) {
    const occupied = new Set<string>()
    ctx.font = '10px system-ui'
    ctx.fillStyle = '#3b4a60'
    for (const item of items.value) {
      const cx = x(item.x), cy = y(item.y)
      if (cx < 0 || cy < 12 || cx > width || cy > height) continue
      const cell = `${Math.floor(cx / 105)}:${Math.floor(cy / 20)}`
      if (occupied.has(cell)) continue
      occupied.add(cell)
      ctx.fillText(item.id, cx + 4, cy - 4)
    }
  }
  overlays.sort((a, b) => Number(a.selected) - Number(b.selected))
  for (const { node, marker, cx, cy, selected } of overlays) {
    ctx.fillStyle = marker?.group === 'error' ? '#b42318' : marker?.group === 'excluded' ? '#b35a12'
      : selected ? '#8d35bb' : marker?.group === 'vehicle' ? '#98510d' : '#16754c'
    ctx.beginPath()
    ctx.arc(cx, cy, selected ? 6 : 4, 0, Math.PI * 2)
    ctx.fill()
    if (selected) {
      ctx.font = 'bold 12px system-ui'
      ctx.fillText(marker?.label ?? node.id, cx + 8, cy - 7)
    }
  }
}

function select(event: PointerEvent) {
  const el = canvas.value!
  const rect = el.getBoundingClientRect()
  const x = renderedCenter.x + (event.clientX - rect.left - el.clientLeft - el.clientWidth / 2) / renderedScale
  const y = renderedCenter.y + (event.clientY - rect.top - el.clientTop - el.clientHeight / 2) / renderedScale
  let nearest = 10 / renderedScale
  let selected = ''
  for (const node of props.map.nodes) {
    if (props.stationOnly && node.type !== 'WORKSTATION') continue
    const distance = Math.hypot(x - node.x, y - node.y)
    if (distance < nearest) { nearest = distance; selected = node.id }
  }
  if (selected) {
    located.value = null
    emit('select', selected)
  }
}

function startDrag(event: PointerEvent) {
  if (drag || !event.isPrimary || event.button > 2) return
  event.preventDefault()
  drag = {
    pointerId: event.pointerId, x: event.clientX, y: event.clientY,
    center: center.value ?? renderedCenter, scale: renderedScale, moved: false,
  }
  dragging.value = true
  canvas.value?.setPointerCapture(event.pointerId)
}

function moveDrag(event: PointerEvent) {
  if (drag?.pointerId !== event.pointerId) return
  const dx = event.clientX - drag.x, dy = event.clientY - drag.y
  if (!drag.moved && Math.hypot(dx, dy) < 5) return
  drag.moved = true
  center.value = { x: drag.center.x - dx / drag.scale, y: drag.center.y - dy / drag.scale }
}

function endDrag(event: PointerEvent) {
  if (drag?.pointerId !== event.pointerId) return
  if (event.type === 'pointerup') {
    moveDrag(event)
    if (!drag.moved && event.button === 0) select(event)
  }
  drag = null
  dragging.value = false
}

function wheelZoom(event: WheelEvent) {
  const el = canvas.value!
  const nextZoom = Math.max(0.5, Math.min(12, zoom.value * Math.exp(-event.deltaY * (event.deltaMode === 0 ? 0.0015 : 0.03))))
  if (nextZoom === zoom.value || drag) return
  const rect = el.getBoundingClientRect()
  const dx = event.clientX - rect.left - el.clientLeft - el.clientWidth / 2
  const dy = event.clientY - rect.top - el.clientTop - el.clientHeight / 2
  const shift = renderedZoom / renderedScale * (1 / zoom.value - 1 / nextZoom)
  const focused = center.value ?? renderedCenter
  center.value = { x: focused.x + dx * shift, y: focused.y + dy * shift }
  zoom.value = nextZoom
}

watch([bounds, markerById, roadPaths, () => props.selectedId, zoom, center, located, labels, marked], requestPaint, { flush: 'post' })
onMounted(() => {
  observer = new ResizeObserver(requestPaint)
  observer.observe(canvas.value!)
})
onBeforeUnmount(() => {
  observer?.disconnect()
  cancelAnimationFrame(frame)
})
defineExpose({ focus: () => {
  located.value = null
  center.value = null
  zoom.value = 2.5
  canvas.value?.parentElement?.scrollIntoView({ block: 'nearest' })
} })
</script>

<template>
  <div ref="surface" class="engineering-map">
    <MapNavigationToolbar :items="items" :zoom="Math.round(zoom * 100)" :labels="labels" :marked="marked || !!located"
      @locate="locate" @zoom="zoomBy" @pan="pan" @fit="fit" @fullscreen="toggleMapFullscreen(surface)"
      @labels="labels = $event" @select-all="marked = true" @clear="clear" />
    <div class="engineering-legend">
      <span v-if="diagnostic">红色错误 · 橙色排除项 · 紫色关联道路</span>
      <span v-else-if="!stationOnly">蓝色工位 · 棕色车辆初始点 · 绿色业务点 · 紫色选中点</span>
    </div>
    <div class="engineering-viewport">
      <canvas ref="canvas" role="img" :class="{ 'is-dragging': dragging }"
        :aria-label="diagnostic ? '导入路网校验地图' : stationOnly ? '任务工位地图' : '工程二维地图'"
        @pointerdown="startDrag" @pointermove="moveDrag" @pointerup="endDrag"
        @pointercancel="endDrag" @lostpointercapture="endDrag" @wheel.prevent="wheelZoom" @contextmenu.prevent />
      <MapOverview :map="map" :view="viewport" :target="target" @center="center = $event" />
    </div>
  </div>
</template>

<style scoped>
.engineering-map { min-width: 0; }
.engineering-viewport { position: relative; margin-top: 6px; }
.engineering-viewport > canvas { display: block; width: 100%; height: clamp(300px, 40vh, 480px); border: 1px solid #dbe4ee; border-radius: 8px; cursor: var(--map-cursor-grab); touch-action: none; }
.engineering-viewport > canvas.is-dragging { cursor: var(--map-cursor-grabbing); }
.engineering-map:fullscreen { background: #fff; padding: 12px; display: flex; flex-direction: column; }
.engineering-map:fullscreen .engineering-viewport { flex: 1; min-height: 0; }
.engineering-map:fullscreen .engineering-viewport > canvas { height: 100%; }
.engineering-map span { font-size: 12px; color: #485970; }
</style>
