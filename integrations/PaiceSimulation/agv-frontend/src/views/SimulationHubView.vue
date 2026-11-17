<script setup lang="ts">
import { computed, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import SimulationView from './SimulationView.vue'
import DispatchIntegrationView from './DispatchIntegrationView.vue'
import ExperimentView from './ExperimentView.vue'

type SimulationMode = 'native' | 'main' | 'compare'

const route = useRoute()
const router = useRouter()
const modes: { name: SimulationMode; label: string }[] = [
  { name: 'native', label: '原生算法' },
  { name: 'main', label: '主工程调度' },
  { name: 'compare', label: '对比实验' }
]

const mode = computed<SimulationMode>(() => {
  const value = String(route.query.mode || 'main')
  return modes.some((item) => item.name === value) ? (value as SimulationMode) : 'main'
})

function changeMode(value: string | number | boolean) {
  const next = modes.some((item) => item.name === value) ? String(value) : 'main'
  void router.replace({ path: '/simulation', query: { ...route.query, mode: next } })
}

watch(mode, (value) => {
  if (route.query.mode !== value) {
    router.replace({ path: '/simulation', query: { ...route.query, mode: value } })
  }
})
</script>

<template>
  <div class="simulation-hub">
    <div class="hub-heading panel">
      <div>
        <h2>仿真中心</h2>
        <p class="muted">统一运行原生算法、主工程调度和同输入对比实验</p>
      </div>
      <el-radio-group
        :model-value="mode"
        size="large"
        @change="changeMode"
      >
        <el-radio-button v-for="item in modes" :key="item.name" :value="item.name">
          {{ item.label }}
        </el-radio-button>
      </el-radio-group>
    </div>

    <SimulationView v-if="mode === 'native'" />
    <DispatchIntegrationView v-else-if="mode === 'main'" />
    <ExperimentView v-else />
  </div>
</template>

<style scoped>
.simulation-hub {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.hub-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 18px;
}

.hub-heading h2 {
  margin: 0 0 4px;
}

.hub-heading p {
  margin: 0;
}

@media (max-width: 900px) {
  .hub-heading {
    align-items: flex-start;
    flex-direction: column;
  }
}
</style>
