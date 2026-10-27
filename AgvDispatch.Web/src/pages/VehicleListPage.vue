<script setup lang="ts">
import ActionIcon from '../components/ActionIcon.vue';
import { computed, ref, onMounted, onUnmounted } from 'vue';
import { useRouter } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { vehicleCategory, categoryLabels, shortTime, type VehicleCategory } from '../dashboard';
import { label } from '../state';
import { IconRobot, IconArrowLeft } from '@tabler/icons-vue';

const store = useDispatchStore();
const router = useRouter();
const vehicleFilter = ref('all');
const vehicleSearch = ref('');
type ConnectionSetting = { vehicleId: string; enabled: boolean; ipAddress: string; port: number; navigationPort: number };
type ConnectionSettings = { revision: string; vehicles: ConnectionSetting[]; activeVehicles: ConnectionSetting[];
  restartRequired: boolean; canManage: boolean };
const connections = ref<ConnectionSettings>();
const pending = ref('');
const connectionMessage = ref('');
const connectionError = ref('');
const activeConnection = (id: string) => connections.value?.activeVehicles?.find(v => v.vehicleId === id);
async function loadConnections() {
  try {
    const response = await fetch('/api/vehicles/configuration');
    if (!response.ok) throw new Error('无法读取车辆连接配置，请刷新重试。');
    connections.value = await response.json();
  } catch (e) { connectionError.value = String(e instanceof Error ? e.message : e); }
}
async function toggleConnection(id: string) {
  if (pending.value || !connections.value) return;
  const enabled = !activeConnection(id)?.enabled;
  pending.value = id; connectionError.value = ''; connectionMessage.value = '';
  try {
    const response = await fetch('/api/vehicles/connection', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ vehicleId: id, enabled, expectedRevision: connections.value.revision }) });
    const result = await response.json();
    if (!response.ok || result.error) throw new Error(result.error ?? result.message ?? '连接操作失败，请查询原状态。');
    connections.value = result;
    connectionMessage.value = enabled ? `${id} 已启用连接，等待实时采集；在线与可派车状态请查看车辆列表。`
      : `${id} 已断开通信，连接参数保留。`;
  } catch (e) {
    connectionError.value = String(e instanceof Error ? e.message : e);
    await loadConnections(); // Read back after an uncertain response; never replay the mutation.
  } finally { pending.value = ''; }
}
let connectionTimer: ReturnType<typeof setInterval> | undefined;
onMounted(() => { void loadConnections(); connectionTimer = setInterval(() => { if (!pending.value) void loadConnections(); }, 10000); });
onUnmounted(() => clearInterval(connectionTimer));

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
    <p class="page-hint">连接／断开逐车即时生效并保存，保留 IP 和地图配置。断开通信不是急停；有未结束任务或车辆仍在动作时须先处置原任务。</p>
    <p v-if="connections?.restartRequired" role="status">有待重启的车辆或通信配置，受控重启后才能使用即时连接按钮。</p>
    <p v-if="connectionMessage" role="status">{{ connectionMessage }}</p>
    <p v-if="connectionError" role="alert">{{ connectionError }}</p>
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
            <td>
              <button class="table-button" @click="router.push({ name: 'overview' })"><ActionIcon name="locate"/>地图定位</button>
              <button class="table-button" :disabled="!connections?.canManage || connections.restartRequired || !!pending || !activeConnection(v.vehicleId)"
                @click="toggleConnection(v.vehicleId)">
                {{ pending === v.vehicleId ? '处理中…' : activeConnection(v.vehicleId)?.enabled ? '断开通信' : '连接通信' }}
              </button>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-if="!filteredVehicles.length" class="empty-panel">没有匹配车辆</p>
    </div>
  </section>
</template>
