<script setup lang="ts">
import ActionIcon from '../components/ActionIcon.vue';
import { computed, ref } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { vehicleCategory, categoryLabels, shortTime, type VehicleCategory } from '../dashboard';
import { label } from '../state';
import { IconRobot, IconArrowLeft } from '@tabler/icons-vue';

const store = useDispatchStore();
const router = useRouter();
const vehicleFilter = ref('all');
const vehicleSearch = ref('');

const filteredVehicles = computed(() => store.vehicles.filter(v =>
  (vehicleFilter.value === 'all' || vehicleCategory(v, store.allTasks, store.fresh) === vehicleFilter.value) &&
  `${v.vehicleId} ${v.displayName} ${v.currentStation ?? ''}`.toLowerCase().includes(vehicleSearch.value.toLowerCase())
));
</script>

<template>
  <section class="management-page">
    <header class="management-title">
      <h2><IconRobot :size="23" />车辆列表</h2>
      <span>共 {{ store.vehicles.length }} 辆</span>
      <button class="accent-button" @click="router.push({ name: 'planning', query: { tab: 'vehicles' } })"><ActionIcon name="vehicle"/>新增 / 配置车辆</button>
      <button @click="router.push('/')"><IconArrowLeft :size="15" />返回大屏</button>
    </header>
    <div class="query-bar">
      <label>车辆 / 站点<input v-model="vehicleSearch" placeholder="编号、名称、当前位置" aria-label="搜索车辆" /></label>
      <label>车辆状态<select v-model="vehicleFilter" aria-label="筛选车辆状态">
        <option value="all">全部</option>
        <option v-for="stat in store.vehicleStats" :key="stat.key" :value="stat.key">{{ stat.label }}</option>
      </select></label>
      <button @click="vehicleSearch = ''; vehicleFilter = 'all'"><ActionIcon name="refresh"/>重置</button>
    </div>
    <div class="management-table">
      <table>
        <thead><tr><th>车辆编号</th><th>名称</th><th>状态</th><th>当前站点</th><th>原始坐标 X / Y</th><th>电量</th><th>定位置信度</th><th>数据质量</th><th>最近采集</th><th>操作</th></tr></thead>
        <tbody>
          <tr v-for="v in filteredVehicles" :key="v.vehicleId">
            <td class="mono cyan">{{ v.vehicleId }}</td>
            <td>{{ v.displayName }}</td>
            <td><span class="state-label" :class="vehicleCategory(v, store.allTasks, store.fresh)">{{ categoryLabels[vehicleCategory(v, store.allTasks, store.fresh)] }}</span></td>
            <td>{{ v.currentStation ?? '—' }}</td>
            <td class="mono">{{ v.x?.toFixed(2) ?? '—' }} / {{ v.y?.toFixed(2) ?? '—' }}</td>
            <td class="green-text">{{ v.batteryPercent ?? '—' }}%</td>
            <td>{{ v.localizationConfidence ?? '—' }}</td>
            <td>{{ label(v.quality) }}</td>
            <td>{{ shortTime(v.lastSuccessfulPollAt) }}</td>
            <td><button class="table-button" @click="router.push({ name: 'overview' })"><ActionIcon name="locate"/>地图定位</button></td>
          </tr>
        </tbody>
      </table>
      <p v-if="!filteredVehicles.length" class="empty-panel">没有匹配车辆</p>
    </div>
  </section>
</template>
