<script setup lang="ts">
import { computed, ref } from 'vue'
import RoadNetScene from './RoadNetScene.vue'
import { dispatchSceneMap, type SourceMap } from '@/integration/dispatch-adapter'
import { dispatchRead, type DispatchStatus } from '@/integration/dispatch-api'

const props = defineProps<{ status: DispatchStatus | null }>()
const emit = defineEmits<{ saved: [status: DispatchStatus] }>()
type Station = { stationId: string; pickupSite: string; offset: number; signalSource: string; sourceLabel?: string; priority?: number }
type PointTable = { fileName: string; csvText: string; sha256: string; stations: Station[] }
type Configuration = { mapDefinition: SourceMap; stations: Station[]; plcSignalTable: PointTable }
const scheme = ref(''), name = ref(''), stationId = ref(''), error = ref(''), notice = ref('')
const configuration = ref<Configuration | null>(null)
const sceneMap = ref<ReturnType<typeof dispatchSceneMap> | null>(null)
const busy = ref(false)
const bindOnMap = ref(false)
const locked = computed(() => busy.value || props.status?.running || ['starting', 'stopping'].includes(props.status?.phase ?? ''))
const station = computed(() => configuration.value?.stations.find(row => row.stationId === stationId.value))
const binding = computed(() => configuration.value?.plcSignalTable.stations.find(row => row.stationId === stationId.value))
const sites = computed(() => configuration.value?.mapDefinition.nodes.filter(node => node.enabled !== false && node.type === 'Station') ?? [])

async function load() {
  busy.value = true; error.value = ''; notice.value = ''; configuration.value = null; sceneMap.value = null
  bindOnMap.value = false
  try {
    const value = await dispatchRead<{ configuration: Configuration }>('/api/simulation/scheme-select', undefined, { id: scheme.value, checkOnly: true })
    const config = value.configuration
    configuration.value = config
    sceneMap.value = dispatchSceneMap(config.mapDefinition)
    stationId.value = config.stations[0]?.stationId ?? ''
    name.value = (props.status?.schemes?.find(row => row.id === scheme.value)?.name ?? '') + ' · 配置副本'
  } catch (e) { error.value = String(e) } finally { busy.value = false }
}

function selectNode(node: { id: string }) {
  if (busy.value || !station.value) return
  const owner = configuration.value?.stations.find(row => row.pickupSite === node.id)
  if (!bindOnMap.value) {
    if (owner) {
      stationId.value = owner.stationId
      error.value = ''
    } else {
      error.value = '该节点当前未绑定 CNC 工位。'
    }
    return
  }
  if (locked.value) return
  if (!sites.value.some(site => site.id === node.id)) {
    error.value = '请选择已启用的工位停靠节点。'
    return
  }
  station.value.pickupSite = node.id
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
  if (!configuration.value || !name.value.trim() || locked.value) return
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
      <p>{{ configuration.mapDefinition.name }} · {{ configuration.stations.length }} 个工位 · {{ configuration.plcSignalTable.fileName }}</p>
      <el-form label-width="120px">
        <el-form-item label="CNC 工位">
          <el-select v-model="stationId" filterable :disabled="busy" style="width: 240px">
            <el-option v-for="row in configuration.stations" :key="row.stationId" :label="row.stationId" :value="row.stationId" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="station" label="取返箱停靠点">
          <el-select v-model="station.pickupSite" filterable :disabled="locked" style="width: 240px">
            <el-option v-for="site in sites" :key="site.id" :label="site.agvSiteId || site.id" :value="site.id" />
          </el-select>
          <span>节点 {{ station.pickupSite }}</span>
        </el-form-item>
        <el-form-item v-if="binding" label="PLC 工位绑定">
          <span>DB1.DBX{{ binding.offset }}.0–7 · {{ station?.signalSource === 'verified' ? '当前点表已核定' : '隔离仿真点位' }}</span>
        </el-form-item>
        <el-form-item label="导入 PLC 点表">
          <input type="file" accept=".csv,text/csv" :disabled="locked" @change="upload" />
        </el-form-item>
        <el-form-item label="新方案名称">
          <el-input v-model="name" maxlength="100" :disabled="locked" style="width: 320px" />
          <el-button type="primary" :disabled="locked || !name.trim()" @click="save">另存工程方案</el-button>
        </el-form-item>
      </el-form>
      <el-radio-group v-model="bindOnMap" :disabled="busy" aria-label="地图选点用途">
        <el-radio-button :value="false">点图核对工位</el-radio-button>
        <el-radio-button :value="true" :disabled="locked">点图修改停靠点</el-radio-button>
      </el-radio-group>
      <RoadNetScene :roadnet="sceneMap" :selectable="!busy" :selected-node-ids="station ? [station.pickupSite] : []" @node-select="selectNode" />
    </template>
  </section>
</template>

<style scoped>
.engineering-panel { order: 2; }
.el-form { margin-top: 16px; }
.el-form-item span, .el-form-item .el-button { margin-left: 12px; }
</style>
