<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { dispatchRead, downloadIntegrationRecord, type DispatchStatus } from '@/integration/dispatch-api'
import { startupAccepted } from '@/integration/startup-result'
const props = defineProps<{ status: DispatchStatus | null }>()
const emit = defineEmits<{ changed: [value: DispatchStatus] }>()
const scheme = ref(''), loadedScheme = ref(''), loadedRoot = ref('')
const vehicleId = ref(''), initialSite = ref(''), stationId = ref('')
const mode = ref('single'), seed = ref(20261117), count = ref(10), interval = ref(3), stationIds = ref<string[]>([])
const busy = ref(false), error = ref('')
const startupNotice = ref('')
const vehicleStarts = ref<{ id: string; initialSite: string }[]>([])
const draft = ref<DispatchStatus | null>(null)
const preview = ref<DispatchStatus | null>(null), checkedInput = ref('')
const vehicles = computed(() => draft.value?.configuration?.vehicles ?? props.status?.configuration?.vehicles ?? [])
const stations = computed(() => draft.value?.configuration?.stations ?? props.status?.configuration?.stations ?? [])
const sites = computed(() => (draft.value?.map?.nodes ?? props.status?.map?.nodes ?? []).filter(n => n.enabled !== false))
const locked = computed(() => busy.value || !!props.status?.running || ['starting','stopping'].includes(props.status?.phase ?? ''))
const ready = computed(() => loadedScheme.value === scheme.value && loadedRoot.value === props.status?.rootName && !!loadedScheme.value)
const input = computed(() => ({ id: scheme.value, ...(mode.value === 'single'
 ? { business: { vehicleId: vehicleId.value, initialSite: initialSite.value, stationId: stationId.value } }
 : { vehicleStarts: vehicleStarts.value, plan: { seed: seed.value, count: count.value, intervalSeconds: interval.value, stationIds: stationIds.value } }) }))
const checked = computed(() => !!preview.value?.preflight?.passed && checkedInput.value === JSON.stringify(input.value))
async function check() {
 busy.value = true; error.value = ''; preview.value = null
 try {
  const request = JSON.parse(JSON.stringify(input.value))
  preview.value = await dispatchRead<DispatchStatus>('/api/simulation/scheme-select', undefined, { ...request, checkOnly: true })
  checkedInput.value = JSON.stringify(request)
 } catch (e) { error.value = String(e) } finally { busy.value = false }
}
watch(vehicleId, id => { initialSite.value = vehicles.value.find(v => v.id === id)?.initialSite ?? '' })
async function load() {
 busy.value = true; error.value = ''; loadedScheme.value = ''
 try {
  const value = await dispatchRead<DispatchStatus>('/api/simulation/scheme-select', undefined, { id: scheme.value, checkOnly: true })
  draft.value = value
  loadedScheme.value = scheme.value; loadedRoot.value = props.status?.rootName ?? ''
  vehicleId.value = value.configuration?.vehicles?.[0]?.id ?? ''
  initialSite.value = value.configuration?.vehicles?.[0]?.initialSite ?? ''
  stationId.value = value.configuration?.stations?.[0]?.stationId ?? ''
  stationIds.value = value.configuration?.stations?.map(s => s.stationId) ?? []
  vehicleStarts.value = (value.configuration?.vehicles ?? []).map(v => ({ id: v.id, initialSite: v.initialSite }))
  preview.value = null; checkedInput.value = ''
 } catch (e) { error.value = String(e) } finally { busy.value = false }
}
async function start() {
 if (!ready.value || locked.value || !checked.value) return
 const previousRoot = props.status?.rootName
 busy.value = true; error.value = ''; startupNotice.value = ''
 try {
  const value = await dispatchRead<DispatchStatus>('/api/simulation/scheme-start', undefined, input.value)
  loadedScheme.value = ''; emit('changed', value)
 } catch (e) {
  error.value = String(e)
  try {
   const value = await dispatchRead<DispatchStatus>('/api/simulation')
   emit('changed', value)
   if (startupAccepted(value, previousRoot, scheme.value)) {
    error.value = ''; loadedScheme.value = ''
    startupNotice.value = '后台已接收本次开测，正在同步启动与业务结果。'
   } else if (value.error) error.value = value.error
  } catch { /* Keep the original startup error visible. */ }
 } finally { busy.value = false }
}
</script>
<template>
 <section class="panel business-setup">
  <h3>业务场景配置</h3>
  <p class="muted">选择工程方案，配置单次或随机满箱呼叫。主工程负责派车、取箱、倒料、返箱及 PLC 收尾。</p>
  <p v-if="status?.runtimeRelease" class="muted">运行包 {{ status.runtimeRelease.releaseId }} · 调度模式 {{ status.runtimeRelease.algorithmMode }}</p>
  <el-alert v-if="error" :title="error" type="error" :closable="false" />
  <el-alert v-if="startupNotice" :title="startupNotice" type="info" :closable="false" />
  <div v-if="status?.businessPlan" class="plan-summary">
   <p>当前计划：{{ status.businessPlan.count }} 次呼叫 · 间隔 {{ status.businessPlan.intervalSeconds }} 秒 · 种子 {{ status.businessPlan.seed }} · {{ status.businessPlan.stationIds.length }} 个工位</p>
   <el-button @click="downloadIntegrationRecord(status.businessPlan, 'business-call-plan.json')">下载业务计划</el-button>
   <details><summary>呼叫工位范围</summary>{{ status.businessPlan.stationIds.join('、') }}</details>
  </div>
  <el-form label-width="100px">
   <el-form-item label="工程方案">
    <el-select v-model="scheme" :disabled="locked" placeholder="选择已保存的工程方案" style="width: min(600px, 100%)">
     <el-option v-for="s in status?.schemes ?? []" :key="s.id" :label="s.name" :value="s.id" />
    </el-select>
    <el-button :disabled="locked || !scheme" @click="load">加载方案</el-button>
   </el-form-item>
   <el-form-item label="业务模式"><el-radio-group v-model="mode" :disabled="locked"><el-radio-button value="single">单次呼叫</el-radio-button><el-radio-button value="random">随机呼叫计划</el-radio-button></el-radio-group></el-form-item>
   <details v-if="ready">
    <summary>地图与业务绑定 · {{ stations.length }} 个工位</summary>
    <p>{{ draft?.configuration?.mapIdentity?.sourceName }} · {{ draft?.configuration?.mapIdentity?.definitionHash }}</p>
    <p>倒料点 {{ draft?.configuration?.dumper?.dumpingSite }} · 等待位 {{ draft?.configuration?.dumper?.waitingSites?.join('、') || draft?.configuration?.dumper?.waitingSite }} · 准入容量 {{ draft?.configuration?.downstreamAdmissionCapacity ?? '沿用配置' }}</p>
    <p>PLC 点表 {{ draft?.configuration?.plcSignalTable?.fileName }} · {{ draft?.configuration?.plcSignalTable?.sha256 }}</p>
    <el-table :data="stations" max-height="240"><el-table-column prop="stationId" label="工位" /><el-table-column prop="pickupSite" label="取返箱点" /></el-table>
   </details>
   <template v-if="ready && mode === 'single'">
    <el-form-item label="参与车辆"><el-select v-model="vehicleId" :disabled="locked"><el-option v-for="v in vehicles" :key="v.id" :label="v.id" :value="v.id" /></el-select><span class="muted">仅启用这一辆车，空载起步，速度倍率 1</span></el-form-item>
    <el-form-item label="初始位置"><el-select v-model="initialSite" filterable :disabled="locked"><el-option v-for="n in sites" :key="n.id" :label="n.agvSiteId || n.id" :value="n.id" /></el-select></el-form-item>
    <el-form-item label="呼叫工位"><el-select v-model="stationId" filterable :disabled="locked"><el-option v-for="s in stations" :key="s.stationId" :label="s.stationId" :value="s.stationId" /></el-select></el-form-item>
   </template>
   <template v-if="ready && mode === 'random'">
    <p>{{ vehicles.length }} 辆车空载起步，速度倍率 1。</p>
    <el-form-item v-for="vehicle in vehicleStarts" :key="vehicle.id" :label="vehicle.id">
     <el-select v-model="vehicle.initialSite" filterable :disabled="locked"><el-option v-for="n in sites" :key="n.id" :label="n.agvSiteId || n.id" :value="n.id" /></el-select>
    </el-form-item>
    <el-form-item label="呼叫范围"><el-select v-model="stationIds" multiple filterable collapse-tags :disabled="locked" style="width: 100%"><el-option v-for="s in stations" :key="s.stationId" :label="s.stationId" :value="s.stationId" /></el-select></el-form-item>
    <el-form-item label="随机种子"><el-input-number v-model="seed" :min="0" :max="2147483647" :precision="0" :disabled="locked" /></el-form-item>
    <el-form-item label="呼叫总量"><el-input-number v-model="count" :min="1" :max="500" :precision="0" :disabled="locked" /></el-form-item>
    <el-form-item label="呼叫间隔秒"><el-input-number v-model="interval" :min="0" :max="60" :disabled="locked" /></el-form-item>
   </template>
   <el-form-item>
    <el-button :loading="busy" :disabled="locked || !ready" @click="check">检查并预览</el-button>
    <el-button type="primary" :loading="busy" :disabled="locked || !ready || !checked" @click="start">{{ mode === 'single' ? '开始单次业务' : '开始业务计划' }}</el-button>
   </el-form-item>
  </el-form>
  <section v-if="preview">
   <p v-if="!checked">配置已变化，请重新检查。</p>
   <p v-for="item in preview.preflight?.checks" :key="item.name">{{ item.name }} · {{ item.status === 'passed' ? '通过' : '独立开发环境，未绑定日常发布包' }}</p>
   <p v-if="preview.runtimeRelease">{{ preview.runtimeRelease.releaseId }} · {{ preview.runtimeRelease.algorithmMode }}</p>
   <el-table v-if="preview.businessPlan" :data="preview.businessPlan.calls" max-height="240"><el-table-column prop="stationId" label="呼叫工位" /><el-table-column prop="atSeconds" label="计划时间（秒）" /></el-table>
  </section>
  <p v-if="status?.phase === 'starting'">正在准备隔离设备、连接正式后台并恢复地图，请等待就绪。</p>
  <el-table v-if="status?.startupChecks?.length" :data="status.startupChecks">
   <el-table-column prop="name" label="环境启动检查" />
   <el-table-column label="结果"><template #default="{ row }">{{ ({ pending: '待检查', running: '检查中', passed: '通过', failed: '失败' } as Record<string, string>)[row.status] || row.status }}</template></el-table-column>
   <el-table-column prop="detail" label="详细信息" />
  </el-table>
  <el-alert v-if="status?.error" :title="status.error" type="error" :closable="false" />
  <p v-if="status?.running" class="muted">环境运行中。九步进度和 PLC 条件见下方任务详情，重新配置前请先停止隔离环境。</p>
 </section>
</template>
<style scoped>
h3 { margin-top: 0; } .el-button, span.muted { margin-left: 12px; } .business-setup { order: 1; }
</style>
