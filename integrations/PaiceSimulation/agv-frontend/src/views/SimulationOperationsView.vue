<script setup lang="ts">
import { computed, defineAsyncComponent, reactive, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { IconArrowRight, IconChartArrows, IconListDetails, IconMap2, IconRefresh } from '@tabler/icons-vue'
import http from '@/api/http'
import { dispatchLocation, type DispatchSection } from '@/integration/dispatch-workspace'
import { useAuthStore } from '@/stores/auth'
import { isFresh } from '@main-web/state'
import type { Snapshot } from '@main-web/types'

const SimulationCenter = defineAsyncComponent(() => import('@main-web/pages/SimulationCenterPage.vue'))
const SimulationWorkshopMap = defineAsyncComponent(() => import('@/components/SimulationWorkshopMap.vue'))
const Pipeline = defineAsyncComponent(() => import('@main-web/pages/DispatchPipelinePage.vue'))
const Optimization = defineAsyncComponent(() => import('@main-web/pages/DispatchOptimizationPage.vue'))

const props = defineProps<{ section: DispatchSection }>()
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const sections = {
  overview: 'overview', tasks: 'task-evidence', environment: 'environment',
  scenario: 'scenarios', faults: 'faults', manual: 'recovery', results: 'report-evidence'
} as const
type Stage = keyof typeof sections
const stage = computed(() => (Object.keys(sections) as Stage[]).find(key => sections[key] === props.section))

const prefix = '/dispatch/main'
const simulationBase = prefix + '/simulation'
const instancePreparation = new Set(['/new-instance', '/configure', '/scheme-select', '/scheme-start'])
async function request<T>(path = '', body?: unknown, signal?: AbortSignal): Promise<T> {
  const response = await http.request<T>({
    url: simulationBase + path, method: body === undefined ? 'GET' : 'POST',
    data: body, signal, timeout: body !== undefined && instancePreparation.has(path) ? 140000 : 50000
  })
  return response as T
}
function archive(id: string) {
  return http.get<any, Blob>(`${simulationBase}/archive`, {
    params: { id }, responseType: 'blob', timeout: 100000
  })
}

const fieldPaths = new Map([
  ['/api/snapshot', '/snapshot'],
  ['/api/task-parameters', '/field-task-parameters'],
  ['/api/communication', '/field-vehicles'],
  ['/api/communication/point-table', '/field-point-table'],
])
const field = reactive({
  snapshot: null as Snapshot | null,
  fresh: false,
  demoMode: false,
  api(path: string) {
    const target = fieldPaths.get(path) ?? (/^\/api\/maps\/[A-Za-z0-9_-]+\/workspace$/.test(path) ? path.slice(4) : undefined)
    if (!target) return Promise.reject(new Error('不支持的现场只读接口'))
    return http.get<unknown, unknown>(prefix + target)
  }
})

async function readField() {
  field.fresh = false
  try {
    const snapshot = await field.api('/api/snapshot') as Snapshot
    field.snapshot = snapshot
    field.fresh = snapshot.source === 'backend' && isFresh(snapshot)
  } catch {
    field.snapshot = null
  }
}
watch(() => props.section, section => {
  if (section === 'environment') void readField()
}, { immediate: true })

const embedded = computed(() => stage.value ? {
  stage: stage.value,
  taskId: typeof route.query.taskId === 'string' ? route.query.taskId : undefined,
  request, archive, field
} : undefined)
function navigate(next: Stage) {
  void router.push(dispatchLocation(sections[next]))
}
const taskLink = (taskId: string) => dispatchLocation('task-evidence', taskId)
const diagnostics = computed(() => route.query.view === 'optimization' ? 'optimization' : 'pipeline')
</script>

<template>
  <div class="simulation-operations">
    <nav class="workspace-actions" aria-label="相关仿真操作">
      <RouterLink v-if="section === 'overview'" :to="dispatchLocation('engineering')"><IconMap2 :size="16" aria-hidden="true" />检查工程与地图</RouterLink>
      <RouterLink v-if="section === 'overview'" :to="dispatchLocation('monitor')"><IconListDetails :size="16" aria-hidden="true" />打开运行监控<IconArrowRight :size="14" aria-hidden="true" /></RouterLink>
      <RouterLink v-if="section === 'environment'" :to="dispatchLocation('engineering')">工程点位与启动检查</RouterLink>
      <el-button v-if="section === 'environment'" @click="readField"><IconRefresh :size="16" aria-hidden="true" />刷新现场只读配置</el-button>
      <RouterLink v-if="section === 'scenarios'" :to="dispatchLocation('plan')">单次与随机业务计划</RouterLink>
      <RouterLink v-if="section === 'task-evidence'" :to="dispatchLocation('monitor', embedded?.taskId)">多单呼叫与车辆路线</RouterLink>
      <RouterLink v-if="section === 'report-evidence'" :to="dispatchLocation('report')">本轮报告与视频</RouterLink>
      <template v-if="section === 'diagnosis'">
        <RouterLink :to="dispatchLocation('diagnosis')" :aria-current="diagnostics === 'pipeline' ? 'page' : undefined"><IconChartArrows :size="16" aria-hidden="true" />六层调度</RouterLink>
        <RouterLink :to="{ query: { mode: 'main', section: 'diagnosis', view: 'optimization' } }" :aria-current="diagnostics === 'optimization' ? 'page' : undefined">滚动匹配与成本</RouterLink>
        <RouterLink :to="dispatchLocation('monitor')">交通资源与车辆</RouterLink>
      </template>
    </nav>
    <SimulationCenter v-if="embedded" :embedded="embedded" @navigate="navigate">
      <template #overview-map="{ status, fresh, selectedVehicle, selectVehicle, roadHighlights }">
        <SimulationWorkshopMap class="panel" :definition="status?.map" :cars="status?.device?.cars"
          :station-count="status?.configuration?.stations?.length"
          :fleet-count="status?.configuration?.vehicles?.length"
          :fresh="fresh" :selected-vehicle="selectedVehicle" :road-highlights="roadHighlights"
          @select-vehicle="selectVehicle">
          <template #empty>
            <span>当前隔离实例尚未提供地图。</span>
            <RouterLink :to="dispatchLocation('engineering')">查看工程配置</RouterLink>
          </template>
        </SimulationWorkshopMap>
      </template>
    </SimulationCenter>
    <Optimization v-else-if="diagnostics === 'optimization'" :simulation-read="signal => request('/dispatch-optimization', undefined, signal)"
      :simulation-save="auth.isAdmin ? (update, signal) => request('/dispatch-optimization/options', update, signal) : undefined"
      :simulation-task-link="taskLink" />
    <Pipeline v-else :simulation-read="signal => request('/dispatch-pipeline', undefined, signal)" :simulation-task-link="taskLink" />
  </div>
</template>
