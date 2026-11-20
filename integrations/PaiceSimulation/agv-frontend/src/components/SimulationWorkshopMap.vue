<script setup lang="ts">
import { computed, defineAsyncComponent, ref, shallowRef, watch } from 'vue'
import type { RoadNetPayload } from '@/api/roadnet'
import { dispatchSceneMap, dispatchViews, workshopMapName, type SourceMap } from '@/integration/dispatch-adapter'
import { mapBounds } from '@/integration/map-navigation'
import { simulationVehicleVisualColor, simulationVehicleVisualLabel, type SimulationVehicle } from '@main-web/simulation'
import RoadNetEditor from './RoadNetEditor.vue'

const RoadNetScene = defineAsyncComponent(() => import('./RoadNetScene.vue'))
const props = defineProps<{
  definition?: SourceMap | null
  fleetCount?: number
  stationCount?: number
  cars?: Record<string, SimulationVehicle>
  fresh?: boolean
  selectedVehicle?: string
  roadHighlights?: Record<string, string>
}>()
const emit = defineEmits<{ selectVehicle: [id: string] }>()
const preview = shallowRef<{ map: RoadNetPayload; offset: { x: number; y: number } } | null>(null)
const revision = ref(0)
const error = ref('')
const show3d = ref(false)
let lastDefinition: string | undefined

watch(() => props.definition, (definition) => {
  const signature = definition ? JSON.stringify(definition) : undefined
  if (lastDefinition === signature) return
  lastDefinition = signature
  revision.value++
  error.value = ''
  preview.value = null
  if (!definition) return
  try {
    const graph = dispatchSceneMap({
      ...definition,
      nodes: definition.nodes.filter(node => node.enabled !== false),
      edges: definition.edges.filter(edge => edge.enabled !== false)
    })
    const bounds = mapBounds([...graph.nodes, ...graph.edges.flatMap(edge => edge.points ?? [])])
    if (!bounds) return
    const left = bounds.left - 4
    const top = bounds.top - 4
    const offset = { x: left, y: -top }
    const project = <T extends { x: number; y: number }>(point: T): T => ({
      ...point,
      x: point.x - left,
      y: point.y - top
    })
    preview.value = {
      offset,
      map: {
        nodes: graph.nodes.map(project),
        edges: graph.edges.map(edge => ({ ...edge, points: edge.points?.map(project) })),
        width: Math.max(50, Math.ceil(bounds.right - left + 4)),
        height: Math.max(34, Math.ceil(bounds.bottom - top + 4)),
        integration: { sourceDefinition: definition, coordinateOffset: offset }
      }
    }
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  }
}, { immediate: true })

const title = computed(() => props.definition ? workshopMapName(props.definition) : '地图预览')
const agvs = computed(() => {
  const offset = preview.value?.offset
  const cars = props.cars
  if (!offset || !cars) return []
  const fresh = props.fresh === true
  return dispatchViews(cars, fresh).map(car => {
    const feedback = cars[car.id]!
    return {
      ...car,
      x: car.x - offset.x,
      y: car.y + offset.y,
      color: simulationVehicleVisualColor(feedback, fresh),
      label: simulationVehicleVisualLabel(feedback, fresh),
      loaded: feedback.loaded
    }
  })
})
const routes = computed(() => preview.value?.map.edges.flatMap(edge => {
  const color = props.roadHighlights?.[edge.id]
  return color && edge.points?.length ? [{ id: edge.id, color, points: edge.points }] : []
}) ?? [])
const legend = [
  { text: '执行中', color: '#41d9bf' },
  { text: '作业等待／载箱待命', color: '#e8bf64' },
  { text: '空载待命', color: '#6fb9ff' },
  { text: '道路受阻', color: '#ee9c63' },
  { text: '暂停／故障', color: '#ef7692' },
  { text: '离线／反馈过期', color: '#65748a' }
]
</script>

<template>
  <section class="workshop-preview">
    <header class="preview-header">
      <h3 :title="definition?.name">{{ title }}</h3>
      <p v-if="definition">
        {{ definition.nodes.length }} 节点 · {{ definition.edges.length }} 道路
        <template v-if="stationCount != null"> · {{ stationCount }} 工位</template>
        <template v-if="fleetCount != null"> · {{ fleetCount }} 台车</template>
      </p>
    </header>
    <el-alert v-if="error" type="error" :title="`地图预览加载失败 · ${error}`" :closable="false" show-icon />
    <div v-else-if="preview" class="preview-canvas">
      <RoadNetEditor :key="revision" :initial="preview.map" :name="title" preview-only>
        <template #preview-actions>
          <el-button size="small" @click="show3d = true">3D 预览</el-button>
        </template>
        <template #map-overlay="{ pixelsPerMeter, zoom }">
          <g v-if="routes.length || agvs.length" data-export="ignore">
            <polyline v-for="segment in routes" :key="segment.id" class="preview-route"
              :points="segment.points.map(point => `${point.x * pixelsPerMeter},${point.y * pixelsPerMeter}`).join(' ')"
              :stroke="segment.color" />
            <g v-for="car in agvs" :key="car.id" class="preview-vehicle"
              :class="{ selected: car.id === selectedVehicle }"
              :transform="`translate(${car.x * pixelsPerMeter} ${car.y * pixelsPerMeter})`"
              role="button" tabindex="0" :aria-label="`查看 ${car.id}`"
              @mousedown.stop @click.stop="emit('selectVehicle', car.id)"
              @keydown.enter.stop.prevent="emit('selectVehicle', car.id)"
              @keydown.space.stop.prevent="emit('selectVehicle', car.id)">
              <title>{{ car.id }} · {{ car.label }}{{ car.loaded ? ' · 载箱' : '' }}</title>
              <g :transform="`rotate(${-car.heading})`">
                <rect x="-12" y="-8" width="24" height="16" rx="3" :fill="car.color" />
                <path d="M2,-4 L8,0 L2,4 Z" />
                <circle v-if="car.loaded" cx="-5" cy="0" r="3" fill="#fff3c5" />
              </g>
              <text x="0" y="-12" :class="{ 'visible-label': zoom >= 1.6 }">{{ car.id }}</text>
            </g>
          </g>
        </template>
      </RoadNetEditor>
    </div>
    <div v-else class="preview-empty"><slot name="empty">当前没有可预览的地图。</slot></div>
    <ul v-if="agvs.length" class="vehicle-legend" aria-label="车辆状态图例">
      <li v-for="item in legend" :key="item.text"><i :style="{ background: item.color }" />{{ item.text }}</li>
    </ul>

    <el-dialog v-model="show3d" :title="`${title} · 3D 预览`" width="min(92vw, 1500px)" top="5vh" destroy-on-close>
      <div v-if="preview" class="preview-3d">
        <RoadNetScene :roadnet="preview.map" :agvs="agvs" :observed="fresh" />
      </div>
    </el-dialog>
  </section>
</template>

<style scoped>
.workshop-preview { min-width: 0; container-type: inline-size; }
.preview-header { display: flex; align-items: baseline; flex-wrap: wrap; gap: 6px 14px; margin-bottom: 12px; }
.preview-header h3 { margin: 0; font-size: 17px; }
.preview-header p { margin: 0; color: var(--ui-text-3); font-size: 12px; }
.preview-canvas { height: clamp(460px, 68dvh, 780px); }
.preview-canvas :deep(.workspace) { grid-template-columns: minmax(0, 1fr) minmax(150px, 21%); }
.preview-3d { height: min(72dvh, 760px); }
.preview-empty { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding: 18px 0; color: var(--ui-text-3); }
.preview-route { fill: none; stroke-width: 3px; stroke-linecap: round; stroke-linejoin: round; vector-effect: non-scaling-stroke; pointer-events: none; }
.preview-vehicle { cursor: var(--map-cursor-pointer, pointer); transition: transform .45s linear; }
@media (prefers-reduced-motion: reduce) {
  .preview-vehicle { transition: none; }
}
.preview-vehicle rect { stroke: #243a56; stroke-width: 1.4; vector-effect: non-scaling-stroke; }
.preview-vehicle path { fill: #1b334d; }
.preview-vehicle text {
  visibility: hidden;
  fill: #203653;
  stroke: #fff;
  stroke-width: 3px;
  paint-order: stroke;
  text-anchor: middle;
  font-size: 12px;
  font-weight: 700;
  pointer-events: none;
}
.preview-vehicle:is(.selected, :hover, :focus-visible) text,
.preview-vehicle .visible-label { visibility: visible; }
.preview-vehicle:is(.selected, :focus-visible) rect { stroke: #25429e; stroke-width: 2.8; }
.vehicle-legend { display: flex; flex-wrap: wrap; gap: 6px 14px; padding: 0; margin: 12px 0 0; list-style: none; color: var(--ui-text-3); font-size: 12px; }
.vehicle-legend li { display: flex; align-items: center; gap: 5px; }
.vehicle-legend i { display: block; width: 8px; height: 8px; border-radius: 50%; }
@container (max-width: 690px) {
  .preview-canvas :deep(.workspace) { grid-template-columns: minmax(0, 1fr); grid-template-rows: minmax(0, 1fr) 120px; }
}
</style>
