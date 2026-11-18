<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { findMapMatches, type MapLocation } from '@/integration/map-navigation'
const props = defineProps<{ items: MapLocation[]; zoom: number; labels: boolean; marked: boolean }>()
const emit = defineEmits<{
  locate: [item: MapLocation]; zoom: [factor: number]; pan: [x: number, y: number]
  fit: []; fullscreen: []; labels: [value: boolean]; selectAll: []; clear: []
}>()
const query = ref('')
const cursor = ref(-1)
const matches = computed(() => findMapMatches(props.items, query.value))
watch(query, () => { cursor.value = -1 })
function find() {
  if (!matches.value.length) return
  cursor.value = (cursor.value + 1) % matches.value.length
  emit('locate', matches.value[cursor.value]!)
}
function clear() {
  query.value = ''
  emit('clear')
}
</script>

<template>
  <div class="map-navigation" aria-label="地图定位工具">
    <el-input v-model="query" size="small" placeholder="搜索编号" clearable aria-label="搜索地图编号" @keyup.enter="find">
      <template #append><el-button :disabled="!matches.length" @click="find">{{ cursor < 0 ? '查找' : '下一个' }}</el-button></template>
    </el-input>
    <el-button size="small" :type="labels ? 'primary' : 'default'" :aria-pressed="labels" @click="emit('labels', !labels)">显示编号</el-button>
    <el-button size="small" @click="emit('selectAll')">全选</el-button>
    <el-button size="small" :disabled="!marked && !query" @click="clear">清空</el-button>
    <el-button-group>
      <el-button size="small" aria-label="缩小" @click="emit('zoom', 0.8)">−</el-button>
      <el-button size="small" title="适应窗口" @click="emit('fit')">{{ zoom }}%</el-button>
      <el-button size="small" aria-label="放大" @click="emit('zoom', 1.25)">＋</el-button>
    </el-button-group>
    <el-button-group>
      <el-button size="small" aria-label="向左平移" @click="emit('pan', -1, 0)">←</el-button>
      <el-button size="small" aria-label="向右平移" @click="emit('pan', 1, 0)">→</el-button>
      <el-button size="small" aria-label="向上平移" @click="emit('pan', 0, -1)">↑</el-button>
      <el-button size="small" aria-label="向下平移" @click="emit('pan', 0, 1)">↓</el-button>
    </el-button-group>
    <el-button size="small" @click="emit('fit')">适应窗口</el-button>
    <el-button size="small" @click="emit('fullscreen')">全屏</el-button>
    <span v-if="query.trim()" class="map-search-result" role="status">{{ !matches.length ? '无匹配编号' : cursor < 0 ? `${matches.length} 个匹配` : `${matches[cursor]?.id ?? ''} · ${cursor + 1}/${matches.length}` }}</span>
  </div>
</template>

<style scoped>
.map-navigation { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; padding: 6px 0; min-width: 0; }
.map-navigation > .el-input { width: 190px; }
.map-navigation > .el-button { margin: 0; }
.map-search-result { color: #506078; font-size: 12px; overflow-wrap: anywhere; }
</style>
