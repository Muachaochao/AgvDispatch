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
let observer: ResizeObserver | undefined
let projected: { id: string; x: number; y: number }[] = []
let renderedCenter: MapPoint = { x: 0, y: 0 }
let renderedScale = 1

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

function paint() {
  const el = canvas.value
  const ctx = el?.getContext('2d')
  if (!el || !ctx || !props.map.nodes.length) return
  const width = el.clientWidth, height = el.clientHeight
  if (!width || !height) return
  const ratio = Math.min(window.devicePixelRatio, 2)
  el.width = Math.round(width * ratio)
  el.height = Math.round(height * ratio)
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
  viewport.value = { left: midX - width / 2 / scale, right: midX + width / 2 / scale, top: midY - height / 2 / scale, bottom: midY + height / 2 / scale }
  const x = (value: number) => (value - midX) * scale + width / 2
  const y = (value: number) => (value - midY) * scale + height / 2
  const byId = new Map(props.map.nodes.map(node => [node.id, node]))
  const highlighted = located.value?.kind === 'edge' ? located.value.id : null
  function roads(highlight: boolean) {
    if (!ctx) return
    ctx.strokeStyle = highlight ? '#7540a8' : '#a0b4c9'
    ctx.lineWidth = highlight ? 2.5 : 1
    ctx.beginPath()
    for (const edge of props.map.edges) {
      if ((marked.value || highlighted === edge.id || !!props.highlightEdges?.has(edge.id)) !== highlight) continue
      const from = byId.get(edge.from), to = byId.get(edge.to)
      if (!from || !to) continue
      const points = edge.points?.length ? edge.points : [from, to]
      ctx.moveTo(x(points[0]!.x), y(points[0]!.y))
      for (let i = 1; i < points.length; i++) ctx.lineTo(x(points[i]!.x), y(points[i]!.y))
    }
    ctx.stroke()
  }
  roads(false)
  if (marked.value || highlighted || props.highlightEdges?.size) roads(true)
  const markerById = new Map<string, EngineeringPoint>()
  for (const marker of props.markers) {
    if (!markerById.has(marker.nodeId) || marker.group === 'vehicle') markerById.set(marker.nodeId, marker)
  }
  projected = []
  const overlays = []
  for (const node of props.map.nodes) {
    const marker = markerById.get(node.id)
    const cx = x(node.x), cy = y(node.y)
    const selected = node.id === (located.value?.kind === 'node' ? located.value.id : props.selectedId)
    if (!props.stationOnly || node.type === 'WORKSTATION') projected.push({ id: node.id, x: cx, y: cy })
    if (selected || marker) {
      overlays.push({ node, marker, cx, cy, selected })
      continue
    }
    ctx.fillStyle = marked.value ? '#7950a3' : node.type === 'WORKSTATION' ? '#197c9d' : '#acbac9'
    ctx.beginPath()
    ctx.arc(cx, cy, node.type === 'WORKSTATION' ? 2.3 : 1, 0, Math.PI * 2)
    ctx.fill()
  }
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

function select(event: MouseEvent) {
  const rect = canvas.value!.getBoundingClientRect()
  let nearest = 10
  let selected = ''
  for (const point of projected) {
    const distance = Math.hypot(event.clientX - rect.left - point.x, event.clientY - rect.top - point.y)
    if (distance < nearest) { nearest = distance; selected = point.id }
  }
  if (selected) {
    located.value = null
    emit('select', selected)
  }
}

watch([() => props.map, () => props.markers, () => props.selectedId, () => props.highlightEdges, zoom, center, located, labels, marked], paint, { flush: 'post' })
onMounted(() => {
  observer = new ResizeObserver(paint)
  observer.observe(canvas.value!)
})
onBeforeUnmount(() => observer?.disconnect())
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
      <canvas ref="canvas" role="img" :aria-label="diagnostic ? '导入路网校验地图' : stationOnly ? '任务工位地图' : '工程二维地图'" @click="select" />
      <MapOverview :map="map" :view="viewport" :target="target" @center="center = $event" />
    </div>
  </div>
</template>

<style scoped>
.engineering-map { min-width: 0; }
.engineering-viewport { position: relative; margin-top: 6px; }
.engineering-viewport > canvas { display: block; width: 100%; height: clamp(300px, 40vh, 480px); border: 1px solid #dbe4ee; border-radius: 8px; cursor: crosshair; }
.engineering-map:fullscreen { background: #fff; padding: 12px; display: flex; flex-direction: column; }
.engineering-map:fullscreen .engineering-viewport { flex: 1; min-height: 0; }
.engineering-map:fullscreen .engineering-viewport > canvas { height: 100%; }
.engineering-map span { font-size: 12px; color: #485970; }
</style>
