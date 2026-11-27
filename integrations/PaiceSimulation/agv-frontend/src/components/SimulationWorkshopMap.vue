<script setup lang="ts">
import { computed, defineAsyncComponent, ref, shallowRef, watch } from 'vue'
import type { RoadNetPayload } from '@/api/roadnet'
import { dispatchSceneMap, dispatchViews, workshopMapName, type SourceMap } from '@/integration/dispatch-adapter'
import { mapBounds, mapNavigationItems, previewNodePositions, type MapLocation, type MapPoint } from '@/integration/map-navigation'
import { simulationVehicleVisualColor, simulationVehicleVisualLabel } from '@main-web/simulation'
import type { DispatchCar } from '@/integration/dispatch-adapter'
import RoadNetEditor from './RoadNetEditor.vue'

const RoadNetScene = defineAsyncComponent(() => import('./RoadNetScene.vue'))
const props = defineProps<{
  definition?: SourceMap | null
  fleetCount?: number
  stationCount?: number
  cars?: Record<string, DispatchCar>
  fresh?: boolean
  selectedVehicle?: string
  roadHighlights?: Record<string, string>
  routeEndpoints?: { nodeId: string; label: string; site: string }[]
  compactHeader?: boolean
  resourceBoundary?: MapPoint[]
  route?: MapPoint[]
  routeColor?: string
  instantVehicles?: boolean
  location?: { nodeId?: string; edgeIds?: string[]; vehicleId?: string; point?: MapPoint; label?: string } | null
  vehicleProfiles?: { id: string; lengthMeters?: number; widthMeters?: number }[]
  selectNodes?: boolean
  selectedNodeIds?: string[]
}>()
const emit = defineEmits<{ selectVehicle: [id: string]; selectNode: [node: { id: string; type: string }] }>()
const preview = shallowRef<{ map: RoadNetPayload; offset: { x: number; y: number } } | null>(null)
const revision = ref(0)
const error = ref('')
const show3d = ref(false)
const editor = ref<InstanceType<typeof RoadNetEditor> | null>(null)
const scene = ref<InstanceType<typeof RoadNetScene> | null>(null)
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
const profiles = computed(() => new Map(props.vehicleProfiles?.map(profile => [profile.id, profile])))
const agvs = computed(() => {
  const offset = preview.value?.offset
  const cars = props.cars
  if (!offset || !cars) return []
  const fresh = props.fresh === true
  return dispatchViews(cars, fresh).map(car => {
    const feedback = cars[car.id]!
    const { recovery: _recovery, ...visualFeedback } = feedback
    return {
      ...car,
      lengthMeters: profiles.value.get(car.id)?.lengthMeters,
      widthMeters: profiles.value.get(car.id)?.widthMeters,
      x: car.x - offset.x,
      y: car.y + offset.y,
      color: simulationVehicleVisualColor(visualFeedback, fresh),
      label: simulationVehicleVisualLabel(visualFeedback, fresh),
      loaded: feedback.loaded
    }
  })
})
const routes = computed(() => preview.value?.map.edges.flatMap(edge => {
  const color = props.roadHighlights?.[edge.id]
  return color && edge.points?.length ? [{ id: edge.id, color, points: edge.points }] : []
}) ?? [])
const endpointMarkers = computed(() => {
  if (!props.routeEndpoints?.length) return []
  const map = preview.value?.map
  if (!map) return []
  const nodes = previewNodePositions(map.nodes, map.edges)
  return props.routeEndpoints.flatMap(endpoint => {
    const node = nodes.get(endpoint.nodeId)
    return node ? [{ ...endpoint, x: node.x, y: node.y }] : []
  })
})
const navigation = computed(() => {
  const offset = preview.value?.offset
  return offset ? (props.route ?? []).map(point => ({ x: point.x - offset.x, y: point.y + offset.y })) : []
})
const overlays = computed(() => {
  const paths = [...routes.value, { id: 'active-navigation', points: navigation.value, color: props.routeColor ?? '#14796f' }]
  const offset = preview.value?.offset, boundary = props.resourceBoundary
  if (offset && boundary?.length) {
    paths.push({ id: 'resource-boundary', color: '#be5b20', points: [...boundary, boundary[0]!]
      .map(point => ({ x: point.x - offset.x, y: -point.y + offset.y })) })
  }
  return paths
})
const staticLocations = computed(() => mapNavigationItems(preview.value?.map ?? null))
const vehicleLocations = computed<MapLocation[]>(() => agvs.value.map(car => ({ ...car, kind: 'vehicle' })))
function focusLocation() {
  const location = props.location, offset = preview.value?.offset
  if (!location || !offset) return
  const point = location.point && { x: location.point.x - offset.x, y: -location.point.y + offset.y }
  const vehicle = vehicleLocations.value.find(item => item.id === location.vehicleId)
  const edgeIds = location.edgeIds ?? []
  editor.value?.focusMap(location.nodeId ? [location.nodeId] : [], edgeIds, vehicle ?? (edgeIds.length ? undefined : point))
  if (!scene.value) return
  const target = vehicle ?? staticLocations.value.find(item => item.id === (location.nodeId ?? edgeIds[0]))
    ?? (point && { ...point, id: location.label ?? '资源', kind: 'edge' as const })
  if (target) scene.value.locate(target)
}
function selectNode(node: { id: string; type: string }) {
  if (props.selectNodes) emit('selectNode', node)
}
watch([() => props.location, editor, scene], focusLocation, { flush: 'post' })
watch([overlays, scene], ([paths, mapScene]) => mapScene?.drawPaths(paths), { flush: 'post' })
// Route colours match the overview road highlights (AgvDispatch.Web SimulationCenterPage overviewRoadHighlights).
const roadLegend = [
  { text: '当前段 · 路权已占用', color: '#087968', dashed: false },
  { text: '后续路段 · 待申请', color: '#3456c8', dashed: true },
  { text: '等待路权 / 阻塞', color: '#b64c2f', dashed: false },
  { text: '返程路段', color: '#a96c10', dashed: true }
]
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
  <section class="workshop-preview" :class="{ 'instant-vehicles': instantVehicles }">
    <header v-if="!compactHeader" class="preview-header">
      <h3 :title="definition?.name">{{ title }}</h3>
      <p v-if="definition">
        {{ definition.nodes.length }} 节点 · {{ definition.edges.length }} 道路
        <template v-if="stationCount != null"> · {{ stationCount }} 工位</template>
        <template v-if="fleetCount != null"> · {{ fleetCount }} 台车</template>
      </p>
    </header>
    <el-alert v-if="error" type="error" :title="`地图预览加载失败 · ${error}`" :closable="false" show-icon />
    <div v-else-if="preview" class="preview-canvas">
      <RoadNetEditor :key="revision" ref="editor" :initial="preview.map" :name="title" preview-only
        :vehicle-locations="vehicleLocations" @vehicle-select="emit('selectVehicle', $event)" @node-select="selectNode">
        <template #preview-actions>
          <el-button size="small" @click="show3d = true">3D 预览</el-button>
        </template>
        <template #map-overlay="{ pixelsPerMeter, zoom }">
          <g v-if="overlays.length || agvs.length" data-export="ignore">
            <polyline v-for="segment in overlays" :key="segment.id" class="preview-route"
              :points="segment.points.map(point => `${point.x * pixelsPerMeter},${point.y * pixelsPerMeter}`).join(' ')"
              :stroke="segment.color" />
            <g v-for="(marker, index) in endpointMarkers" :key="marker.nodeId" class="preview-route-endpoint"
              :transform="`translate(${marker.x * pixelsPerMeter} ${marker.y * pixelsPerMeter})`">
              <title>{{ marker.label }} · {{ marker.site }}</title>
              <circle r="8" />
              <text :y="index === 0 ? -14 : 23">{{ marker.label }}</text>
            </g>
            <g v-for="(car, index) in agvs" :key="car.id" class="preview-vehicle"
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
              <text :x="(index % 2) * 18 - 9" :y="-12 - (index % 3) * 15" :class="{ 'visible-label': zoom >= 1.6 }">{{ car.id }}</text>
            </g>
          </g>
        </template>
      </RoadNetEditor>
    </div>
    <div v-else class="preview-empty"><slot name="empty">当前没有可预览的地图。</slot></div>
    <div v-if="agvs.length" class="map-legend-groups">
      <ul class="vehicle-legend" aria-label="车辆状态图例">
        <li class="legend-title">车辆</li>
        <li v-for="item in legend" :key="item.text"><i :style="{ background: item.color }" />{{ item.text }}</li>
      </ul>
      <ul class="vehicle-legend road-legend" aria-label="道路与路权图例">
        <li class="legend-title">道路与路权</li>
        <li v-for="item in roadLegend" :key="item.text"><i class="road-swatch" :class="{ dashed: item.dashed }" :style="{ borderTopColor: item.color }" />{{ item.text }}</li>
      </ul>
    </div>

    <el-dialog v-model="show3d" :title="`${title} · 3D 预览`" width="min(92vw, 1500px)" top="5vh" destroy-on-close>
      <div v-if="preview" class="preview-3d">
        <RoadNetScene ref="scene" :roadnet="preview.map" :agvs="agvs" :observed="fresh" exact-vehicle-positions
          :selectable="selectNodes" :selected-node-ids="selectedNodeIds" vehicle-selectable
          @vehicle-select="emit('selectVehicle', $event)" @node-select="selectNode" />
      </div>
    </el-dialog>
  </section>
</template>

<style scoped>
.workshop-preview { min-width: 0; }
.preview-header { display: flex; align-items: baseline; flex-wrap: wrap; gap: 6px 14px; margin-bottom: 12px; }
.preview-header h3 { margin: 0; font-size: 17px; }
.preview-header p { margin: 0; color: var(--ui-text-3); font-size: 12px; }
.preview-canvas { height: clamp(460px, 68dvh, 780px); }
.preview-3d { height: min(72dvh, 760px); }
.preview-empty { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding: 18px 0; color: var(--ui-text-3); }
.preview-route { fill: none; stroke-width: 3px; stroke-linecap: round; stroke-linejoin: round; vector-effect: non-scaling-stroke; pointer-events: none; }
.preview-route-endpoint circle { fill: #25429e; stroke: #fff; stroke-width: 2px; }
.preview-route-endpoint text { fill: #203653; stroke: #fff; stroke-width: 4px; paint-order: stroke; text-anchor: middle; font-size: 18px; font-weight: 700; }
.preview-vehicle { cursor: var(--map-cursor-pointer, pointer); transition: transform .45s linear; }
.workshop-preview.instant-vehicles .preview-vehicle { transition: none; }
@media (prefers-reduced-motion: reduce) {
  .preview-vehicle { transition: none; }
}
.preview-vehicle rect { stroke: #243a56; stroke-width: 1.4; vector-effect: non-scaling-stroke; }
.preview-vehicle path { fill: #1b334d; }
.preview-vehicle text {
  visibility: visible;
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
.map-legend-groups { display: flex; flex-wrap: wrap; gap: 8px 18px; margin-top: 12px; padding: 8px 12px; border: 1px solid var(--ui-line); border-radius: 10px; background: var(--ui-surface-soft); }
.vehicle-legend { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 14px; padding: 0; margin: 0; list-style: none; color: var(--ui-text-2, var(--ui-text-3)); font-size: 12px; }
.road-legend { padding-left: 18px; border-left: 1px solid var(--ui-line); }
.vehicle-legend .legend-title { color: var(--ui-text-3); font-size: 11px; font-weight: 600; letter-spacing: 1px; }
.vehicle-legend i.road-swatch { width: 22px; height: 0; border-radius: 2px; border-top: 4px solid; }
.vehicle-legend i.road-swatch.dashed { border-top-style: dashed; border-top-width: 3px; }
.vehicle-legend li { display: flex; align-items: center; gap: 5px; }
.vehicle-legend i { display: block; width: 8px; height: 8px; border-radius: 50%; }
</style>
