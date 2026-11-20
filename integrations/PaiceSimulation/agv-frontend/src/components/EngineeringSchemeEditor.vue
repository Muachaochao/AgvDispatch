<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, ref, shallowRef, watch } from 'vue'
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
const editing = ref(false)
const pickingStop = ref(false)
const pointMap = ref<InstanceType<typeof EngineeringPointMap>>()
const selectedSite = ref('')
const showSignals = ref(false)
const mapTab = ref<'preview' | 'points'>('preview')
let originalBindings: Pick<Configuration, 'stations' | 'plcSignalTable'> | undefined
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
const comparePointLabels = new Intl.Collator(undefined, { numeric: true }).compare
const sites = computed(() => nodeOptions.value
  .filter(site => {
    const node = nodes.value.get(site.value)
    return node?.enabled !== false && node?.type === 'Station'
  })
  .sort((a, b) => comparePointLabels(a.label, b.label)))
const vehicles = computed(() => configuration.value?.vehicles ?? [])
const markers = computed<EngineeringPoint[]>(() => {
  const config = configuration.value
  if (!config) return []
  return [
    ...vehicles.value.map(vehicle => ({ label: vehicle.id, nodeId: vehicle.initialSite, group: 'vehicle' })),
    { label: '倒料点', nodeId: config.dumper.dumpingSite, group: 'facility' },
    ...(config.dumper.waitingSites ?? [config.dumper.waitingSite]).map((nodeId, i) => ({ label: '等待位 ' + (i + 1), nodeId, group: 'facility' })),
    { label: '检修／观察点', nodeId: config.inspectionSite, group: 'inspection' },
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
watch([stationId, locked], () => { pickingStop.value = false })
watch(mapTab, tab => { if (tab === 'preview') pickingStop.value = false })
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
  showSignals.value = false; mapTab.value = 'preview'; selectedSite.value = ''
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
    selectedSite.value = config.stations[0]?.pickupSite ?? ''
  } catch (e) { error.value = String(e) } finally { busy.value = false }
}

function inspectNode(id: string) {
  const owner = configuration.value?.stations.find(row => row.pickupSite === id)
  if (owner) stationId.value = owner.stationId
  selectedSite.value = id
}

async function focusPoint(id: string) {
  pickingStop.value = false
  selectedSite.value = id
  mapTab.value = 'points'
  await nextTick()
  if (nodes.value.get(id)?.enabled !== false && nodes.value.has(id)) pointMap.value?.focus()
}

function locatePoint(id: string) {
  inspectNode(id)
  void focusPoint(id)
}

function inspectStation(id: string) {
  stationId.value = id
  if (station.value) void focusPoint(station.value.pickupSite)
}

function inspectIssue(issue: EngineeringPoint) {
  if (issue.group === 'station') stationId.value = issue.label
  void focusPoint(issue.nodeId)
}

function beginEditing() {
  const config = configuration.value
  if (!config || locked.value) return
  originalBindings = { stations: config.stations.map(row => ({ ...row })), plcSignalTable: config.plcSignalTable }
  name.value = (props.status?.schemes?.find(row => row.id === scheme.value)?.name ?? '').slice(0, 73).trimEnd() + ' · 配置副本'
  editing.value = true
  error.value = ''; notice.value = ''
}

function finishEditing() {
  editing.value = false
  pickingStop.value = false
  originalBindings = undefined
}

function cancelEditing() {
  if (busy.value) return
  if (configuration.value && originalBindings) Object.assign(configuration.value, originalBindings)
  finishEditing()
  error.value = ''; notice.value = ''
}

async function chooseStopOnMap() {
  if (!editing.value || locked.value || !station.value) return
  await focusPoint(station.value.pickupSite)
  if (editing.value && !locked.value) pickingStop.value = true
}

function selectNode(id: string) {
  if (busy.value) return
  if (!editing.value || !pickingStop.value || locked.value) {
    inspectNode(id)
    return
  }
  if (!station.value) return
  if (!sites.value.some(site => site.value === id)) {
    error.value = '请在地图上点一个已启用的工位。'
    return
  }
  station.value.pickupSite = id
  selectedSite.value = id
  pickingStop.value = false
  error.value = ''
}

async function upload(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || !editing.value || locked.value || !configuration.value) return
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
    notice.value = 'PLC 点表校验通过。点击「另存为新方案」保存。'
  } catch (e) { error.value = String(e) } finally { busy.value = false }
}

async function save() {
  if (!editing.value || !configuration.value || !name.value.trim() || locked.value || issues.value.length) return
  busy.value = true; error.value = ''; notice.value = ''
  try {
    const result = await dispatchRead<DispatchStatus & { selectedSchemeId?: string }>('/api/simulation/scheme-save', undefined, { name: name.value.trim(), configuration: configuration.value })
    if (result.selectedSchemeId) {
      scheme.value = result.selectedSchemeId
      finishEditing()
    } else {
      if (originalBindings) Object.assign(configuration.value, originalBindings)
      finishEditing()
    }
    emit('saved', result)
    notice.value = `已保存「${name.value.trim()}」。可在「场景与呼叫」选择。`
  } catch (e) { error.value = String(e) } finally { busy.value = false }
}
</script>

<template>
  <section class="panel engineering-panel">
    <h3>仿真工程方案</h3>
    <el-space wrap>
      <el-select v-model="scheme" placeholder="选择工程方案" aria-label="仿真工程方案" style="width: min(320px, 75vw)" :disabled="busy || editing" @change="load">
        <el-option v-for="item in status?.schemes ?? []" :key="item.id" :label="item.name" :value="item.id" />
      </el-select>
      <el-button :disabled="!scheme || busy || editing" :loading="busy && !configuration" @click="load">重新加载</el-button>
    </el-space>
    <el-alert v-if="error && !pickingStop && !editing" :title="error" type="error" :closable="false" show-icon />
    <template v-if="configuration">
      <div class="engineering-map-header">
        <el-radio-group v-model="mapTab" size="small" aria-label="工程地图视图">
          <el-radio-button value="preview">全厂地图</el-radio-button>
          <el-radio-button value="points">查工位与车辆起点</el-radio-button>
        </el-radio-group>
        <el-tag effect="light" :type="issues.length ? 'warning' : 'success'">
          {{ issues.length ? `点位检查：${issues.length} 项异常` : '点位检查：未发现配置异常' }}
        </el-tag>
      </div>
      <SimulationWorkshopMap v-if="mapTab === 'preview'"
        :definition="configuration.mapDefinition" :fleet-count="vehicles.length" :station-count="configuration.stations.length" />
      <template v-else>
        <div v-if="pickingStop" class="map-pick-banner" role="status">
          <span>{{ error || `请在图上点选 ${stationId} 的新停车点。` }}</span>
          <el-button size="small" @click="pickingStop = false; error = ''">取消选点</el-button>
        </div>
        <div class="engineering-layout">
          <EngineeringPointMap v-if="sceneMap" ref="pointMap" :map="sceneMap" :markers="markers" :selected-id="selectedSite" @select="selectNode" @inspect="inspectNode" />
          <aside class="engineering-overview">
            <label class="inspection-field">
              <span>地图定位</span>
              <el-select-v2 :model-value="selectedSite" :options="nodeOptions" filterable placeholder="输入地图点位编号" @change="locatePoint" />
            </label>
            <p v-if="point" class="point-detail">X {{ point.x.toFixed(3) }} · Y {{ point.y.toFixed(3) }} {{ configuration.mapDefinition.unit }} · {{ point.enabled === false ? '停用' : '启用' }}</p>
            <p v-else-if="selectedSite" class="point-detail">{{ selectedSite }} · 地图点位不存在</p>
            <label class="inspection-field">
              <span>CNC 工位</span>
              <el-select-v2 :model-value="stationId" :options="stationOptions" filterable :disabled="busy" @change="inspectStation" />
            </label>
            <dl v-if="station" class="engineering-facts">
              <div><dt>取箱／还箱停车点</dt><dd>{{ station.pickupSite }}</dd></div>
            </dl>
            <el-button v-if="binding || signals.length" size="small" :aria-expanded="showSignals" @click="showSignals = !showSignals">
              {{ showSignals ? '收起 PLC 信号' : '查看 PLC 信号' }}
            </el-button>
            <details class="engineering-locations">
              <summary>车辆起点与业务点位</summary>
              <ul>
                <li v-for="vehicle in vehicles" :key="vehicle.id">
                  <el-button link type="primary" @click="locatePoint(vehicle.initialSite)">{{ vehicle.id }} · {{ vehicle.initialSite }}</el-button>
                  <small>{{ vehicle.initialLoaded ? '有箱' : '空载' }} · {{ vehicle.speedScale }}×</small>
                </li>
                <li v-for="marker in markers.filter(item => item.group !== 'vehicle')" :key="marker.label">
                  <el-button link type="primary" @click="locatePoint(marker.nodeId)">{{ marker.label }} · {{ marker.nodeId }}</el-button>
                </li>
              </ul>
            </details>
          </aside>
        </div>
      </template>
      <section class="engineering-check">
        <header class="engineering-check-header">
          <h4>点位检查详情</h4>
          <el-button v-if="!editing" :disabled="locked" @click="beginEditing">编辑配置</el-button>
        </header>
        <ul v-if="issues.length" class="point-issues">
          <li v-for="issue in issues" :key="issue.label + issue.reason">
            <el-button type="danger" link @click="inspectIssue(issue)">{{ issue.label }} · {{ issue.nodeId }} · {{ issue.reason }}</el-button>
          </li>
        </ul>
        <p v-else>已核对车辆起点、工位停车点、业务点位和 PLC 绑定。</p>
        <dl class="engineering-facts engineering-summary">
          <div>
            <dt>倒料资源</dt>
            <dd><el-button link type="primary" @click="locatePoint(configuration.dumper.dumpingSite)">{{ configuration.dumper.resourceId }}</el-button></dd>
          </div>
          <div>
            <dt>同时执行上限</dt>
            <dd>{{ configuration.downstreamAdmissionCapacity ? `${configuration.downstreamAdmissionCapacity} 单` : '不限' }}</dd>
            <p v-if="configuration.downstreamAdmissionCapacity">派车去取箱时占用名额，返箱收尾后释放。</p>
          </div>
        </dl>
      </section>
      <el-alert v-if="notice && !editing" :title="notice" type="success" :closable="false" show-icon />
      <section v-if="editing" class="engineering-edit">
        <h4>编辑配置</h4>
        <el-alert v-if="error && !pickingStop" :title="error" type="error" :closable="false" show-icon />
        <el-alert v-if="notice" :title="notice" type="success" :closable="false" show-icon />
        <p v-if="locked && !busy">仿真进行中，停止后可继续修改或保存。</p>
        <el-form label-position="top" @submit.prevent>
          <div class="engineering-edit-grid">
            <el-form-item label="要修改的 CNC 工位">
              <el-select-v2 :model-value="stationId" :options="stationOptions" filterable :disabled="busy" @change="inspectStation" />
            </el-form-item>
            <el-form-item v-if="station" label="取箱／还箱停车点">
              <el-select-v2 v-model="station.pickupSite" :options="sites" filterable :disabled="locked" @change="pickingStop = false" />
              <el-button :disabled="locked" size="small" @click="chooseStopOnMap">在地图上选新停车点</el-button>
            </el-form-item>
            <el-form-item label="导入 PLC 点表">
              <p class="point-detail">{{ configuration.plcSignalTable.fileName }}</p>
              <input aria-label="导入 PLC 点表" type="file" accept=".csv,text/csv" :disabled="locked" @change="upload" />
            </el-form-item>
            <el-form-item label="新方案名称">
              <el-input v-model="name" maxlength="80" :disabled="locked" />
            </el-form-item>
          </div>
          <p v-if="issues.length">请先处理点位检查中的异常，再保存方案。</p>
          <el-space wrap>
            <el-button :disabled="busy" @click="cancelEditing">取消编辑</el-button>
            <el-button type="primary" :disabled="locked || !name.trim() || !!issues.length" @click="save">另存为新方案</el-button>
          </el-space>
        </el-form>
      </section>
      <div v-if="mapTab === 'points' && showSignals">
        <h4>{{ signalOwner }} 的 PLC 信号</h4>
        <p class="point-detail">
          {{ configuration.plcSignalTable.fileName }} · {{ syntheticCount }} 个工位使用隔离仿真信号
          <template v-if="binding && signalOwner === stationId"> · DB1.DBX{{ binding.offset }}.0–7 · {{ station?.signalSource === 'verified' ? '当前点表已核定' : '隔离仿真点位' }}</template>
        </p>
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
.engineering-overview, .engineering-check, .engineering-edit { background: var(--el-fill-color-lighter); border: 1px solid var(--el-border-color-lighter); border-radius: 10px; padding: 14px 16px; }
.engineering-overview { align-self: start; }
.engineering-overview ul, .point-issues { list-style: none; padding: 0; margin: 6px 0; }
.engineering-overview small { margin-left: 8px; }
.inspection-field { display: grid; gap: 6px; margin-bottom: 8px; font-weight: 600; }
.inspection-field + .inspection-field, .point-detail + .inspection-field { margin-top: 14px; }
.engineering-locations { margin-top: 12px; }
.engineering-locations summary { cursor: pointer; color: var(--el-text-color-secondary); }
.point-detail, .engineering-check p, .engineering-edit p { margin: 6px 0; color: var(--el-text-color-secondary); font-size: 13px; }
.engineering-facts { display: flex; flex-wrap: wrap; gap: 10px; margin: 10px 0; }
.engineering-facts dt { color: var(--el-text-color-secondary); font-size: 13px; }
.engineering-facts dd { margin: 2px 0 0; font-weight: 600; overflow-wrap: anywhere; }
.engineering-summary { gap: 12px 32px; margin-bottom: 0; }
.engineering-summary dd { min-height: 26px; display: flex; align-items: center; }
.engineering-map-header, .engineering-check-header, .map-pick-banner { display: flex; align-items: center; flex-wrap: wrap; justify-content: space-between; gap: 8px; }
.engineering-check-header h4, .engineering-edit h4 { margin: 0; }
.map-pick-banner { padding: 8px 12px; border-radius: 8px; background: var(--el-color-primary-light-9); }
.point-issues { max-height: 200px; overflow: auto; }
.engineering-edit h4 { margin-bottom: 12px; }
.engineering-edit-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(260px, 100%), 1fr)); gap: 0 16px; max-width: 850px; }
.engineering-edit-grid .el-form-item .el-button { margin-top: 6px; }
.engineering-edit-grid .el-form-item .point-detail { width: 100%; overflow-wrap: anywhere; }
.engineering-edit-grid input[type="file"] { max-width: 100%; }
@media (max-width: 1040px) { .engineering-layout { grid-template-columns: minmax(0, 1fr); } }
</style>
