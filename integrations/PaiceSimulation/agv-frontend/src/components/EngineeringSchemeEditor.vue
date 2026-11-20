<script setup lang="ts">
import { computed, defineAsyncComponent, ref, shallowRef, watch } from 'vue'
import EngineeringPointMap from './EngineeringPointMap.vue'
import { dispatchSceneMap, type SourceMap } from '@/integration/dispatch-adapter'
import { dispatchRead, type DispatchStatus } from '@/integration/dispatch-api'
import { inspectEngineeringPoints, readPlcSignals, type EngineeringPoint } from '@/integration/engineering-points'

const props = defineProps<{ status: DispatchStatus | null }>()
const emit = defineEmits<{ saved: [status: DispatchStatus] }>()
const SimulationWorkshopMap = defineAsyncComponent(() => import('./SimulationWorkshopMap.vue'))
type Station = { stationId: string; pickupSite: string; offset: number; signalSource: string; sourceLabel?: string; priority?: number }
type PointTable = { fileName: string; csvText: string; sha256: string; stations: Station[] }
type Configuration = {
  mapDefinition: SourceMap
  stations: Station[]
  plcSignalTable: PointTable
  vehicles: { id: string; initialSite: string; initialLoaded: boolean; speedScale: number }[]
  dumper: { dumpingSite: string; waitingSite: string; waitingSites?: string[]; resourceId: string }
  inspectionSite: string
  downstreamAdmissionCapacity?: number
}
const scheme = ref(''), name = ref(''), stationId = ref(''), error = ref(''), notice = ref('')
const configuration = ref<Configuration | null>(null)
const sceneMap = shallowRef<ReturnType<typeof dispatchSceneMap> | null>(null)
const busy = ref(false)
const bindOnMap = ref(false)
const selectedSite = ref('')
const showSignals = ref(false)
const mapTab = ref<'preview' | 'points'>('preview')
const locked = computed(() => busy.value || props.status?.running || ['starting', 'stopping'].includes(props.status?.phase ?? ''))
const station = computed(() => configuration.value?.stations.find(row => row.stationId === stationId.value))
const bindings = computed(() => new Map(configuration.value?.plcSignalTable.stations.map(row => [row.stationId, row])))
const binding = computed(() => bindings.value.get(stationId.value))
const nodes = computed(() => new Map(configuration.value?.mapDefinition.nodes.map(node => [node.id, node])))
const point = computed(() => nodes.value.get(selectedSite.value))
const stationOptions = computed(() => configuration.value?.stations.map(row => ({ value: row.stationId, label: row.stationId })) ?? [])
const nodeOptions = computed(() => [...nodes.value.values()].map(node => ({
  value: node.id, label: node.agvSiteId && node.agvSiteId !== node.id ? node.agvSiteId + ' · ' + node.id : node.id,
})))
const sites = computed(() => nodeOptions.value.filter(site => {
  const node = nodes.value.get(site.value)
  return node?.enabled !== false && node?.type === 'Station'
}))
const vehicles = computed(() => configuration.value?.vehicles ?? [])
const markers = computed<EngineeringPoint[]>(() => {
  const config = configuration.value
  if (!config) return []
  return [
    ...vehicles.value.map(vehicle => ({ label: vehicle.id, nodeId: vehicle.initialSite, group: 'vehicle' })),
    { label: '倒料点', nodeId: config.dumper.dumpingSite, group: 'facility' },
    ...(config.dumper.waitingSites ?? [config.dumper.waitingSite]).map((nodeId, i) => ({ label: '等待位 ' + (i + 1), nodeId, group: 'facility' })),
    { label: '检修点', nodeId: config.inspectionSite, group: 'inspection' },
  ]
})
const issues = computed(() => {
  const stations = configuration.value?.stations ?? []
  const results = inspectEngineeringPoints(nodes.value, [
    ...markers.value,
    ...stations.map(row => ({ label: row.stationId, nodeId: row.pickupSite, group: 'station' })),
  ])
  for (const row of stations) {
    const entry = bindings.value.get(row.stationId)
    if (!entry || entry.offset !== row.offset)
      results.push({ label: row.stationId, nodeId: row.pickupSite, group: 'station', reason: entry ? 'PLC 字节与工程配置不一致' : 'PLC 点表缺少工位' })
  }
  return results
})
const signalCatalog = computed(() => readPlcSignals(configuration.value?.plcSignalTable.csvText ?? ''))
const signalOwner = computed(() => selectedSite.value === configuration.value?.dumper.dumpingSite ? 'DUMPER-001' : stationId.value)
const signals = computed(() => signalCatalog.value.get(signalOwner.value) ?? [])
const syntheticCount = computed(() => configuration.value?.stations.filter(row => row.signalSource !== 'verified').length ?? 0)
watch(() => station.value?.pickupSite, site => { if (site) selectedSite.value = site })
watch(() => props.status?.schemes, schemes => {
  if (scheme.value || !schemes?.length) return
  const fullFactory = schemes.find(item => {
    const name = item.mapName || item.name
    return item.vehicleCount === 5 && item.stationCount === 305
      && (name.includes('B07') || name.includes('全厂五车仿真'))
  })
  if (fullFactory) {
    scheme.value = fullFactory.id
    void load()
  }
}, { immediate: true })

async function load() {
  busy.value = true; error.value = ''; notice.value = ''; configuration.value = null; sceneMap.value = null
  bindOnMap.value = false; showSignals.value = false; mapTab.value = 'preview'
  try {
    const value = await dispatchRead<{ configuration: Configuration }>('/api/simulation/scheme-select', undefined, { id: scheme.value, checkOnly: true })
    const config = value.configuration
    configuration.value = config
    sceneMap.value = dispatchSceneMap({
      ...config.mapDefinition,
      nodes: config.mapDefinition.nodes.filter(node => node.enabled !== false),
      edges: config.mapDefinition.edges.filter(edge => edge.enabled !== false),
    })
    stationId.value = config.stations[0]?.stationId ?? ''
    name.value = (props.status?.schemes?.find(row => row.id === scheme.value)?.name ?? '') + ' · 配置副本'
  } catch (e) { error.value = String(e) } finally { busy.value = false }
}

function inspectNode(id: string) {
  const owner = configuration.value?.stations.find(row => row.pickupSite === id)
  if (owner) stationId.value = owner.stationId
  selectedSite.value = id
}

function inspectIssue(issue: EngineeringPoint) {
  if (issue.group === 'station') stationId.value = issue.label
  selectedSite.value = issue.nodeId
  mapTab.value = 'points'
}

function selectNode(id: string) {
  if (busy.value) return
  if (!bindOnMap.value) {
    inspectNode(id)
    return
  }
  if (locked.value || !station.value) return
  if (!sites.value.some(site => site.value === id)) {
    error.value = '请选择已启用的工位停靠节点。'
    return
  }
  station.value.pickupSite = id
  error.value = ''; notice.value = ''
}

async function upload(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || locked.value || !configuration.value) return
  busy.value = true; error.value = ''; notice.value = ''
  try {
    const csvText = await file.text()
    const table = await dispatchRead<PointTable>('/api/simulation/point-table-validate', undefined, { fileName: file.name, csvText })
    const byId = new Map(table.stations.map(row => [row.stationId, row]))
    const config = configuration.value
    const missing = config.stations.find(row => !byId.has(row.stationId))
    if (missing) throw new Error(`点表缺少工位 ${missing.stationId}`)
    config.stations = config.stations.map(row => {
      const point = byId.get(row.stationId)!
      return { stationId: row.stationId, pickupSite: row.pickupSite, priority: row.priority,
        offset: point.offset, signalSource: point.signalSource === 'verified' ? row.signalSource : 'isolated-synthetic' }
    })
    config.plcSignalTable = { ...table, csvText }
    notice.value = 'PLC 点表校验通过，可另存方案。'
  } catch (e) { error.value = String(e) } finally { busy.value = false }
}

async function save() {
  if (!configuration.value || !name.value.trim() || locked.value || issues.value.length) return
  busy.value = true; error.value = ''; notice.value = ''
  try {
    const result = await dispatchRead<DispatchStatus>('/api/simulation/scheme-save', undefined, { name: name.value.trim(), configuration: configuration.value })
    emit('saved', result)
    notice.value = '已另存工程方案，可在呼叫计划中选择。'
  } catch (e) { error.value = String(e) } finally { busy.value = false }
}
</script>

<template>
  <section class="panel engineering-panel">
    <h3>工程配置</h3>
    <el-alert v-if="error" :title="error" type="error" :closable="false" />
    <el-alert v-if="notice" :title="notice" type="success" :closable="false" />
    <el-space wrap>
      <el-select v-model="scheme" placeholder="选择工程方案" style="width: 320px" :disabled="busy" @change="configuration = null; sceneMap = null">
        <el-option v-for="item in status?.schemes ?? []" :key="item.id" :label="item.name" :value="item.id" />
      </el-select>
      <el-button :disabled="!scheme || busy" @click="load">加载工程配置</el-button>
    </el-space>
    <template v-if="configuration">
      <el-radio-group v-model="mapTab" size="small" aria-label="工程地图视图">
        <el-radio-button value="preview">地图预览</el-radio-button>
        <el-radio-button value="points">工程点位与绑定</el-radio-button>
      </el-radio-group>
      <SimulationWorkshopMap v-if="mapTab === 'preview'"
        :definition="configuration.mapDefinition" :fleet-count="vehicles.length" :station-count="configuration.stations.length" />
      <div v-else class="engineering-layout">
        <EngineeringPointMap v-if="sceneMap" :map="sceneMap" :markers="markers" :selected-id="selectedSite" @select="selectNode" @inspect="inspectNode" />
        <aside class="engineering-overview">
          <h4>车辆初始点 · {{ vehicles.length }} 台</h4>
          <ul class="engineering-vehicles">
            <li v-for="vehicle in vehicles" :key="vehicle.id">
              <el-button link type="primary" @click="inspectNode(vehicle.initialSite)">{{ vehicle.id }} · {{ vehicle.initialSite }}</el-button>
              <small>{{ vehicle.initialLoaded ? '有箱' : '空载' }} · {{ vehicle.speedScale }}×</small>
            </li>
          </ul>
          <h4>业务点位</h4>
          <ul>
            <li v-for="marker in markers.filter(item => item.group !== 'vehicle')" :key="marker.label">
              <el-button link type="primary" @click="inspectNode(marker.nodeId)">{{ marker.label }} · {{ marker.nodeId }}</el-button>
            </li>
          </ul>
          <p>倒料资源 {{ configuration.dumper.resourceId }} · 准入 {{ configuration.downstreamAdmissionCapacity || '不限流' }}</p>
          <el-select-v2 :model-value="selectedSite" :options="nodeOptions" filterable placeholder="查找地图点位" @change="inspectNode" />
          <p v-if="point" class="point-detail">{{ point.id }} · X {{ point.x.toFixed(3) }} · Y {{ point.y.toFixed(3) }} {{ configuration.mapDefinition.unit }} · {{ point.enabled === false ? '停用' : '启用' }}</p>
          <p v-else-if="selectedSite" class="point-detail">{{ selectedSite }} · 地图点位不存在</p>
        </aside>
      </div>
      <h4>点位检查 · {{ issues.length }} 项异常</h4>
      <ul v-if="issues.length" class="point-issues">
        <li v-for="issue in issues" :key="issue.label + issue.reason">
          <el-button type="danger" link @click="inspectIssue(issue)">{{ issue.label }} · {{ issue.nodeId }} · {{ issue.reason }}</el-button>
        </li>
      </ul>
      <p v-else>已核对工位、车辆初始点、业务点位及 PLC 字节绑定。</p>
      <el-radio-group v-model="bindOnMap" :disabled="busy" aria-label="地图选点用途" @change="mapTab = 'points'">
        <el-radio-button :value="false">点图核对工位</el-radio-button>
        <el-radio-button :value="true" :disabled="locked">点图修改停靠点</el-radio-button>
      </el-radio-group>
      <el-form label-width="120px">
        <el-form-item label="CNC 工位">
          <el-select-v2 v-model="stationId" :options="stationOptions" filterable :disabled="busy" style="width: 240px" />
        </el-form-item>
        <el-form-item v-if="station" label="取返箱停靠点">
          <el-select-v2 v-model="station.pickupSite" :options="sites" filterable :disabled="locked" style="width: 240px" />
          <span>节点 {{ station.pickupSite }}</span>
        </el-form-item>
        <el-form-item v-if="binding" label="PLC 工位绑定">
          <span>DB1.DBX{{ binding.offset }}.0–7 · {{ station?.signalSource === 'verified' ? '当前点表已核定' : '隔离仿真点位' }}</span>
          <el-button @click="showSignals = !showSignals">{{ showSignals ? '收起 PLC 点表' : '查看 PLC 点表' }}</el-button>
        </el-form-item>
        <el-form-item label="当前 PLC 点表">
          <span>{{ configuration.plcSignalTable.fileName }} · {{ syntheticCount }} 个工位使用隔离仿真信号</span>
        </el-form-item>
        <el-form-item label="导入 PLC 点表">
          <input type="file" accept=".csv,text/csv" :disabled="locked" @change="upload" />
        </el-form-item>
        <el-form-item label="新方案名称">
          <el-input v-model="name" maxlength="100" :disabled="locked" style="width: 320px" />
          <el-button type="primary" :disabled="locked || !name.trim() || !!issues.length" @click="save">另存工程方案</el-button>
        </el-form-item>
      </el-form>
      <div v-if="showSignals">
        <h4>{{ signalOwner }} 的 PLC 信号</h4>
        <el-table :data="signals" class="engineering-signals" empty-text="点表没有该工位的信号记录">
          <el-table-column prop="name" label="信号" min-width="215" />
          <el-table-column prop="address" label="地址" min-width="130" />
          <el-table-column label="方向" min-width="140"><template #default="{ row }">{{ row.direction === 'PlcToHost' ? 'PLC → 上位机' : row.direction === 'HostToPlc' ? '上位机 → PLC' : row.direction }}</template></el-table-column>
          <el-table-column prop="access" label="读写" />
          <el-table-column label="点表启用"><template #default="{ row }">{{ row.enabled ? '是' : '否' }}</template></el-table-column>
        </el-table>
      </div>
    </template>
  </section>
</template>

<style scoped>
.engineering-panel { order: 2; }
.engineering-layout { display: grid; grid-template-columns: minmax(0, 1fr) minmax(260px, 320px); gap: 16px; }
.engineering-overview { background: #f5f7fc; border-radius: 8px; padding: 12px; }
.engineering-overview h4 { margin: 4px 0; }
.engineering-overview ul, .point-issues { list-style: none; padding: 0; margin: 6px 0; }
.engineering-overview small { margin-left: 8px; }
.engineering-overview p { margin: 6px 0; }
.point-issues { max-height: 200px; overflow: auto; }
.el-form { margin-top: 16px; }
.el-form-item span, .el-form-item .el-button { margin-left: 12px; }
@media (max-width: 1040px) { .engineering-layout { grid-template-columns: minmax(0, 1fr); } }
</style>
