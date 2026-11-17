<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { dispatchRead, type DispatchStatus } from '@/integration/dispatch-api'
const props = defineProps<{ status: DispatchStatus | null }>()
const emit = defineEmits<{ changed: [value: DispatchStatus] }>()
const scheme = ref(''), loadedScheme = ref(''), loadedRoot = ref('')
const vehicleId = ref(''), initialSite = ref(''), stationId = ref('')
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
 } catch (e) { error.value = String(e) } finally { busy.value = false }
}
async function start() {
 if (!ready.value || locked.value) return
 busy.value = true; error.value = ''
 try {
  const value = await dispatchRead<DispatchStatus>('/api/simulation/scheme-start', undefined, {
   id: scheme.value, business: { vehicleId: vehicleId.value, initialSite: initialSite.value, stationId: stationId.value }
  })
  loadedScheme.value = ''; emit('changed', value)
 } catch (e) { error.value = String(e) } finally { busy.value = false }
}
</script>
<template>
 <section class="panel business-setup">
  <h3>单车单工位业务场景</h3>
  <p class="muted">选择工程方案与初始位置，发出一次满箱呼叫。主工程负责派车、取箱、倒料、返箱及 PLC 收尾。</p>
  <el-alert v-if="error" :title="error" type="error" :closable="false" />
  <el-form label-width="100px">
   <el-form-item label="工程方案">
    <el-select v-model="scheme" :disabled="locked" placeholder="选择已保存的工程方案" style="width: min(600px, 100%)">
     <el-option v-for="s in status?.schemes ?? []" :key="s.id" :label="s.name" :value="s.id" />
    </el-select>
    <el-button :disabled="locked || !scheme" @click="load">加载方案</el-button>
   </el-form-item>
   <template v-if="ready">
    <el-form-item label="参与车辆"><el-select v-model="vehicleId" :disabled="locked"><el-option v-for="v in vehicles" :key="v.id" :label="v.id" :value="v.id" /></el-select><span class="muted">仅启用这一辆车，空载起步，速度倍率 1</span></el-form-item>
    <el-form-item label="初始位置"><el-select v-model="initialSite" filterable :disabled="locked"><el-option v-for="n in sites" :key="n.id" :label="n.agvSiteId || n.id" :value="n.id" /></el-select></el-form-item>
    <el-form-item label="呼叫工位"><el-select v-model="stationId" filterable :disabled="locked"><el-option v-for="s in stations" :key="s.stationId" :label="s.stationId" :value="s.stationId" /></el-select></el-form-item>
   </template>
   <el-form-item><el-button type="primary" :loading="busy" :disabled="locked || !ready || !vehicleId || !initialSite || !stationId" @click="start">开始单次业务</el-button></el-form-item>
  </el-form>
  <p v-if="status?.running" class="muted">环境运行中。九步进度和 PLC 条件见下方任务详情，重新配置前请先停止隔离环境。</p>
 </section>
</template>
<style scoped>
h3 { margin-top: 0; } .el-button, span.muted { margin-left: 12px; } .business-setup { order: 1; }
</style>
