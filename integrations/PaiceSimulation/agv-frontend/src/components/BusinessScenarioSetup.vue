<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { dispatchRead, downloadIntegrationRecord, type DispatchStatus } from '@/integration/dispatch-api'
const props = defineProps<{ status: DispatchStatus | null }>()
const emit = defineEmits<{ changed: [value: DispatchStatus] }>()
const scheme = ref(''), loadedScheme = ref(''), loadedRoot = ref('')
const vehicleId = ref(''), initialSite = ref(''), stationId = ref('')
const mode = ref('single'), seed = ref(20261117), count = ref(10), interval = ref(3), stationIds = ref<string[]>([])
const busy = ref(false), error = ref('')
const vehicles = computed(() => props.status?.configuration?.vehicles ?? [])
const stations = computed(() => props.status?.configuration?.stations ?? [])
const sites = computed(() => (props.status?.map?.nodes ?? []).filter(n => n.enabled !== false))
const locked = computed(() => busy.value || !!props.status?.running || ['starting','stopping'].includes(props.status?.phase ?? ''))
const ready = computed(() => loadedScheme.value === scheme.value && loadedRoot.value === props.status?.rootName && !!loadedScheme.value)
watch(vehicleId, id => { initialSite.value = vehicles.value.find(v => v.id === id)?.initialSite ?? '' })
async function load() {
 busy.value = true; error.value = ''; loadedScheme.value = ''
 try {
  const value = await dispatchRead<DispatchStatus>('/api/simulation/scheme-select', undefined, { id: scheme.value })
  emit('changed', value)
  loadedScheme.value = scheme.value; loadedRoot.value = value.rootName ?? ''
  vehicleId.value = value.configuration?.vehicles?.[0]?.id ?? ''
  initialSite.value = value.configuration?.vehicles?.[0]?.initialSite ?? ''
  stationId.value = value.configuration?.stations?.[0]?.stationId ?? ''
  stationIds.value = value.configuration?.stations?.map(s => s.stationId) ?? []
 } catch (e) { error.value = String(e) } finally { busy.value = false }
}
async function start() {
 if (!ready.value || locked.value) return
 busy.value = true; error.value = ''
 try {
  const value = await dispatchRead<DispatchStatus>('/api/simulation/scheme-start', undefined, {
   id: scheme.value, ...(mode.value === 'single' ? { business: { vehicleId: vehicleId.value, initialSite: initialSite.value, stationId: stationId.value } } : { plan: { seed: seed.value, count: count.value, intervalSeconds: interval.value, stationIds: stationIds.value } })
  })
  loadedScheme.value = ''; emit('changed', value)
 } catch (e) { error.value = String(e) } finally { busy.value = false }
}
</script>
<template>
 <section class="panel business-setup">
  <h3>业务场景配置</h3>
  <p class="muted">选择工程方案，配置单次或随机满箱呼叫。主工程负责派车、取箱、倒料、返箱及 PLC 收尾。</p>
  <el-alert v-if="error" :title="error" type="error" :closable="false" />
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
   <el-form-item label="业务模式"><el-radio-group v-model="mode" :disabled="locked"><el-radio-button value="single">单次呼叫</el-radio-button><el-radio-button value="random">随机持续呼叫</el-radio-button></el-radio-group></el-form-item>
   <template v-if="ready && mode === 'single'">
    <el-form-item label="参与车辆"><el-select v-model="vehicleId" :disabled="locked"><el-option v-for="v in vehicles" :key="v.id" :label="v.id" :value="v.id" /></el-select><span class="muted">仅启用这一辆车，空载起步，速度倍率 1</span></el-form-item>
    <el-form-item label="初始位置"><el-select v-model="initialSite" filterable :disabled="locked"><el-option v-for="n in sites" :key="n.id" :label="n.agvSiteId || n.id" :value="n.id" /></el-select></el-form-item>
    <el-form-item label="呼叫工位"><el-select v-model="stationId" filterable :disabled="locked"><el-option v-for="s in stations" :key="s.stationId" :label="s.stationId" :value="s.stationId" /></el-select></el-form-item>
   </template>
   <template v-if="ready && mode === 'random'">
    <p>使用方案全部 {{ vehicles.length }} 辆车及各自初始位置，空载起步，速度倍率 1。</p>
    <el-form-item label="呼叫范围"><el-select v-model="stationIds" multiple filterable collapse-tags :disabled="locked" style="width: 100%"><el-option v-for="s in stations" :key="s.stationId" :label="s.stationId" :value="s.stationId" /></el-select></el-form-item>
    <el-form-item label="随机种子"><el-input-number v-model="seed" :min="0" :max="2147483647" :precision="0" :disabled="locked" /></el-form-item>
    <el-form-item label="呼叫总量"><el-input-number v-model="count" :min="1" :max="500" :precision="0" :disabled="locked" /></el-form-item>
    <el-form-item label="呼叫间隔秒"><el-input-number v-model="interval" :min="0" :max="60" :disabled="locked" /></el-form-item>
   </template>
   <el-form-item><el-button type="primary" :loading="busy" :disabled="locked || !ready || (mode === 'single' ? !vehicleId || !initialSite || !stationId : !stationIds.length)" @click="start">{{ mode === 'single' ? '开始单次业务' : '开始业务计划' }}</el-button></el-form-item>
  </el-form>
  <p v-if="status?.running" class="muted">环境运行中。九步进度和 PLC 条件见下方任务详情，重新配置前请先停止隔离环境。</p>
 </section>
</template>
<style scoped>
h3 { margin-top: 0; } .el-button, span.muted { margin-left: 12px; } .business-setup { order: 1; }
</style>
