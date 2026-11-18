<script setup lang="ts">
import { computed } from 'vue'
import type { RoadNetPayload } from '@/api/roadnet'
import EngineeringPointMap from './EngineeringPointMap.vue'

const props = defineProps<{
  roadnet: RoadNetPayload | null
  modelValue: string | null
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
  close: []
}>()

const map = computed(() => props.roadnet && ({
  ...props.roadnet,
  nodes: props.roadnet.nodes.map(node => ({ ...node, y: -node.y })),
  edges: props.roadnet.edges.map(edge => ({
    ...edge,
    points: edge.points?.map(point => ({ ...point, y: -point.y })),
  })),
}))

function choose(id: string) {
  emit('update:modelValue', id)
  emit('close')
}
</script>

<template>
  <EngineeringPointMap v-if="map?.nodes.length" :map="map" :markers="[]" :selected-id="modelValue ?? ''" station-only @select="choose" />
  <el-empty v-else description="该路网还没有节点" />
  <p v-if="map?.nodes.length" class="muted">点击蓝色工位选为任务端点。</p>
</template>
