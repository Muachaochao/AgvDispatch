<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { RoadNetPayload } from '@/api/roadnet'
import type { EngineeringPoint } from '@/integration/engineering-points'

const props = defineProps<{
  map: RoadNetPayload
  markers: EngineeringPoint[]
  selectedId: string
}>()
const emit = defineEmits<{ select: [id: string] }>()
const canvas = ref<HTMLCanvasElement>()
const zoom = ref(1)
let observer: ResizeObserver | undefined
let projected: { id: string; x: number; y: number }[] = []

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
  const xs = props.map.nodes.map(node => node.x), ys = props.map.nodes.map(node => node.y)
  const minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys)
  const scale = Math.min((width - 40) / Math.max(1, maxX - minX), (height - 40) / Math.max(1, maxY - minY)) * zoom.value
  const focused = zoom.value > 1 ? props.map.nodes.find(node => node.id === props.selectedId) : undefined
  const midX = focused?.x ?? (minX + maxX) / 2, midY = focused?.y ?? (minY + maxY) / 2
  const x = (value: number) => (value - midX) * scale + width / 2
  const y = (value: number) => (value - midY) * scale + height / 2
  ctx.strokeStyle = '#a0b4c9'
  ctx.beginPath()
  for (const edge of props.map.edges) {
    const points = edge.points ?? []
    if (!points.length) continue
    ctx.moveTo(x(points[0]!.x), y(points[0]!.y))
    for (const point of points.slice(1)) ctx.lineTo(x(point.x), y(point.y))
  }
  ctx.stroke()
  const marked = new Map<string, EngineeringPoint>()
  for (const marker of props.markers) {
    if (!marked.has(marker.nodeId) || marker.group === 'vehicle') marked.set(marker.nodeId, marker)
  }
  projected = []
  const overlays = []
  for (const node of props.map.nodes) {
    const marker = marked.get(node.id)
    const cx = x(node.x), cy = y(node.y)
    const selected = node.id === props.selectedId
    projected.push({ id: node.id, x: cx, y: cy })
    if (selected || marker) {
      overlays.push({ node, marker, cx, cy, selected })
      continue
    }
    ctx.fillStyle = node.type === 'WORKSTATION' ? '#197c9d' : '#acbac9'
    ctx.beginPath()
    ctx.arc(cx, cy, node.type === 'WORKSTATION' ? 2.3 : 1, 0, Math.PI * 2)
    ctx.fill()
  }
  overlays.sort((a, b) => Number(a.selected) - Number(b.selected))
  for (const { node, marker, cx, cy, selected } of overlays) {
    ctx.fillStyle = selected ? '#8d35bb' : marker?.group === 'vehicle' ? '#98510d' : '#16754c'
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
  if (selected) emit('select', selected)
}

watch([() => props.map, () => props.markers, () => props.selectedId, zoom], paint, { flush: 'post' })
onMounted(() => {
  observer = new ResizeObserver(paint)
  observer.observe(canvas.value!)
})
onBeforeUnmount(() => observer?.disconnect())
</script>

<template>
  <div class="engineering-map">
    <el-space wrap>
      <el-button size="small" :disabled="zoom === 1" @click="zoom = 1">全图</el-button>
      <el-button size="small" :disabled="!selectedId || zoom === 2.5" @click="zoom = 2.5">放大所选点位</el-button>
      <span>蓝色工位 · 棕色车辆初始点 · 绿色业务点 · 紫色选中点</span>
    </el-space>
    <canvas ref="canvas" role="img" aria-label="工程二维地图" @click="select" />
  </div>
</template>

<style scoped>
.engineering-map { min-width: 0; }
canvas { display: block; width: 100%; height: clamp(300px, 40vh, 480px); margin-top: 8px; border: 1px solid #dbe4ee; border-radius: 8px; cursor: crosshair; }
.engineering-map span { font-size: 12px; color: #485970; }
</style>
