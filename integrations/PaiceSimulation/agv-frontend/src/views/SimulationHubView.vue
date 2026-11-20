<script setup lang="ts">
import { computed, defineAsyncComponent } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { dispatchLocation, dispatchSection, type DispatchSection } from '@/integration/dispatch-workspace'

const SimulationView = defineAsyncComponent(() => import('./SimulationView.vue'))
const DispatchIntegration = defineAsyncComponent(() => import('./DispatchIntegrationView.vue'))
const SimulationOperations = defineAsyncComponent(() => import('./SimulationOperationsView.vue'))
const ExperimentView = defineAsyncComponent(() => import('./ExperimentView.vue'))
const route = useRoute()
const router = useRouter()
const mode = computed(() => route.query.mode === 'native' || route.query.mode === 'compare' ? route.query.mode : 'main')
const taskId = computed(() => typeof route.query.taskId === 'string' ? route.query.taskId : undefined)
const section = computed<DispatchSection>({
  get: () => dispatchSection(route.query.section),
  set: value => { void router.push(dispatchLocation(value)) }
})
const businessForms = new Set<DispatchSection>(['engineering', 'plan', 'monitor', 'report'])
</script>

<template>
  <SimulationView v-if="mode === 'native'" />
  <ExperimentView v-else-if="mode === 'compare'" />
  <DispatchIntegration v-else-if="businessForms.has(section)" v-model="section" :task-id="taskId" />
  <SimulationOperations v-else :section="section" />
</template>
