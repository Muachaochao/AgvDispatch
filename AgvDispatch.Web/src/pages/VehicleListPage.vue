<script setup lang="ts">
import ActionIcon from '../components/ActionIcon.vue';
import { computed, ref, onMounted, onUnmounted } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { useDispatchStore } from '../stores/dispatch';
import { vehicleCategory, categoryLabels, shortTime } from '../dashboard';
import { vehicleObstacle } from '../obstacles';
import ConsoleDialog from '../ConsoleDialog.vue';
import VehicleObstacleDetails from '../components/VehicleObstacleDetails.vue';
import { label } from '../state';
import { IconRobot, IconArrowLeft } from '@tabler/icons-vue';

const store = useDispatchStore();
const router = useRouter();
const route = useRoute();
const vehicleFilter = ref(typeof route.query.filter === 'string' && route.query.filter in categoryLabels ? route.query.filter : 'all');
const vehicleSearch = ref('');
const obstacleVehicleId = ref('');
const obstacleVehicle = computed(() => store.vehicles.find(v => v.vehicleId === obstacleVehicleId.value));
type ConnectionSetting = { vehicleId: string; enabled: boolean; ipAddress: string; port: number; navigationPort: number };
type ConnectionSettings = { revision: string; vehicles: ConnectionSetting[]; activeVehicles: ConnectionSetting[];
  restartRequired: boolean; canManage: boolean };
const connections = ref<ConnectionSettings>();
const pending = ref('');
const connectionMessage = ref('');
const connectionError = ref('');
const connectionTargetId = ref('');
const disconnectConfirmed = ref(false);
const activeConnection = (id: string) => connections.value?.activeVehicles?.find(v => v.vehicleId === id);
const connectionTarget = computed(() => activeConnection(connectionTargetId.value));
async function loadConnections() {
  try {
    const response = await fetch('/api/vehicles/configuration');
    if (!response.ok) throw new Error('无法读取车辆连接配置，请刷新重试。');
    connections.value = await response.json();
  } catch (e) { connectionError.value = String(e instanceof Error ? e.message : e); }
}
function requestConnectionToggle(id: string) {
  if (pending.value || !connections.value) return;
  if (!activeConnection(id)?.enabled) {
    void toggleConnection(id);
    return;
  }
  connectionTargetId.value = id;
  disconnectConfirmed.value = false;
  connectionMessage.value = '';
  connectionError.value = '';
}
function closeConnectionConfirmation() {
  if (pending.value) return;
  connectionTargetId.value = '';
  disconnectConfirmed.value = false;
}
async function confirmDisconnect() {
  const target = connectionTarget.value;
  if (!target?.enabled || !disconnectConfirmed.value || pending.value) return;
  await toggleConnection(target.vehicleId, true);
}
async function toggleConnection(id: string, disconnectConfirmedOnServer = false) {
  if (pending.value || !connections.value) return;
  const enabled = !activeConnection(id)?.enabled;
  if (!enabled && !disconnectConfirmedOnServer) return;
  pending.value = id; connectionError.value = ''; connectionMessage.value = '';
  try {
    const response = await fetch('/api/vehicles/connection', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ vehicleId: id, enabled, expectedRevision: connections.value.revision }) });
    const result = await response.json();
    if (!response.ok || result.error) throw new Error(result.error ?? result.message ?? '连接操作失败，请查询原状态。');
    connections.value = result;
    connectionMessage.value = enabled ? `${id} 已启用连接，等待实时采集；在线与可派车状态请查看车辆列表。`
      : `${id} 已断开通信，连接参数保留。`;
    connectionTargetId.value = '';
    disconnectConfirmed.value = false;
  } catch (e) {
    connectionError.value = String(e instanceof Error ? e.message : e);
    await loadConnections(); // Read back after an uncertain response; never replay the mutation.
    connectionTargetId.value = '';
    disconnectConfirmed.value = false;
  } finally { pending.value = ''; }
}
let connectionTimer: ReturnType<typeof setInterval> | undefined;
onMounted(() => { void loadConnections(); connectionTimer = setInterval(() => { if (!pending.value) void loadConnections(); }, 10000); });
onUnmounted(() => clearInterval(connectionTimer));

const filteredVehicles = computed(() => store.vehicles.filter(v =>
  (vehicleFilter.value === 'all' || vehicleCategory(v, store.allTasks, store.fresh, store.now) === vehicleFilter.value) &&
  `${v.vehicleId} ${v.displayName} ${v.currentStation ?? ''}`.toLowerCase().includes(vehicleSearch.value.toLowerCase())
));
</script>

<template>
  <section class="management-page">
    <header class="management-title">
      <h2><IconRobot :size="23" />车辆列表</h2>
      <span>共 {{ store.vehicles.length }} 辆</span>
      <button @click="router.push('/charging')">充电管理</button>
      <button @click="router.push('/navigation-test')"><ActionIcon name="vehicle"/>单车导航调试</button>
      <button class="accent-button" @click="router.push({ name: 'planning', query: { tab: 'vehicles' } })"><ActionIcon name="vehicle"/>新增 / 配置车辆</button>
      <button @click="router.push('/')"><IconArrowLeft :size="15" />返回大屏</button>
    </header>
    <p class="page-hint">连接会立即启用并保存；断开通信会先要求二次确认。两者都保留 IP 和地图配置。断开通信不是急停，有未结束任务或车辆仍在动作时须先处置原任务。本页不直接下发通用停止或恢复运动，任务冻结与继续在任务详情处理，单车原导航取消与继续须进入受控导航调试。</p>
    <p v-if="connections?.restartRequired" role="status">有待重启的车辆或通信配置，受控重启后才能使用即时连接按钮。</p>
    <p v-if="connectionMessage" role="status">{{ connectionMessage }}</p>
    <p v-if="connectionError" role="alert">{{ connectionError }}</p>
    <div class="query-bar">
      <label><span>车辆 / 站点</span><el-input v-model="vehicleSearch" clearable placeholder="编号、名称、当前位置" aria-label="搜索车辆" /></label>
      <label><span>车辆状态</span><el-select v-model="vehicleFilter" aria-label="筛选车辆状态"><el-option label="全部" value="all" /><el-option v-for="stat in store.vehicleStats" :key="stat.key" :label="stat.label" :value="stat.key" /></el-select></label>
      <el-button @click="vehicleSearch = ''; vehicleFilter = 'all'"><ActionIcon name="refresh"/>重置</el-button>
    </div>
    <div class="management-table">
      <el-table :data="filteredVehicles" row-key="vehicleId" stripe height="100%" table-layout="fixed" class="business-table vehicle-table">
        <el-table-column label="车辆编号" width="110" fixed="left"><template #default="{ row }"><span class="table-mono">{{ row.vehicleId }}</span></template></el-table-column>
        <el-table-column label="名称" prop="displayName" min-width="160" show-overflow-tooltip />
        <el-table-column label="状态" width="125"><template #default="{ row }"><span class="state-label" :class="vehicleCategory(row, store.allTasks, store.fresh, store.now)">{{ categoryLabels[vehicleCategory(row, store.allTasks, store.fresh, store.now)] }}</span></template></el-table-column>
        <el-table-column label="障碍状态 / 已观察时长" min-width="190"><template #default="{ row }"><button class="obstacle-summary" @click="obstacleVehicleId = row.vehicleId" :aria-label="row.vehicleId + ' 障碍状态及处置依据'"><span>{{ vehicleObstacle(row, store.fresh, store.now).label }}</span><small>{{ vehicleObstacle(row, store.fresh, store.now).duration }} · 查看依据</small></button></template></el-table-column>
        <el-table-column label="当前站点" width="115"><template #default="{ row }">{{ row.currentStation ?? '—' }}</template></el-table-column>
        <el-table-column label="原始坐标 X / Y" width="150"><template #default="{ row }"><span class="table-mono">{{ row.x?.toFixed(2) ?? '—' }} / {{ row.y?.toFixed(2) ?? '—' }}</span></template></el-table-column>
        <el-table-column label="电量" width="90"><template #default="{ row }"><span class="battery-value">{{ row.batteryPercent ?? '—' }}%</span></template></el-table-column>
        <el-table-column label="定位置信度" width="110"><template #default="{ row }">{{ row.localizationConfidence ?? '—' }}</template></el-table-column>
        <el-table-column label="数据质量" width="105"><template #default="{ row }">{{ label(row.quality) }}</template></el-table-column>
        <el-table-column label="最近采集" width="155"><template #default="{ row }">{{ shortTime(row.lastSuccessfulPollAt) }}</template></el-table-column>
        <el-table-column label="操作" width="210" fixed="right"><template #default="{ row }"><div class="table-actions"><el-button link type="primary" @click="router.push({ name: 'overview' })"><ActionIcon name="locate"/>地图定位</el-button><el-button link :type="activeConnection(row.vehicleId)?.enabled ? 'danger' : 'primary'" :disabled="!connections?.canManage || connections.restartRequired || !!pending || !activeConnection(row.vehicleId)" @click="requestConnectionToggle(row.vehicleId)">{{ pending === row.vehicleId ? '处理中…' : activeConnection(row.vehicleId)?.enabled ? '断开通信' : '连接通信' }}</el-button></div></template></el-table-column>
        <template #empty><div class="table-empty">没有匹配车辆</div></template>
      </el-table>
    </div>
    <ConsoleDialog v-if="obstacleVehicle" :title="obstacleVehicle.vehicleId + ' · 障碍状态与处置'" @close="obstacleVehicleId = ''">
      <VehicleObstacleDetails :vehicle="obstacleVehicle" :current="store.fresh" :now="store.now" />
    </ConsoleDialog>
    <ConsoleDialog v-if="connectionTarget" :title="connectionTarget.vehicleId + ' · 确认断开通信'" @close="closeConnectionConfirmation">
      <div class="disconnect-target">
        <div><span>车辆</span><b>{{ connectionTarget.vehicleId }}</b></div>
        <div><span>控制地址</span><b>{{ connectionTarget.ipAddress }}:{{ connectionTarget.port }}</b></div>
        <div><span>导航端口</span><b>{{ connectionTarget.navigationPort }}</b></div>
      </div>
      <div class="disconnect-boundary">
        <strong>断开通信不是急停，也不会自动取消原任务。</strong>
        <ul>
          <li>连接参数会保留，但实时状态和派车通信会停止。</li>
          <li>车辆可能继续执行已经接受的原任务动作，必须查看车端和现场反馈。</li>
          <li>有未结束任务、载荷、PLC 或路权责任时，应先按原任务处置。</li>
          <li>需要取消或继续单车原导航时，请进入受控导航调试，不在本页下发。</li>
        </ul>
      </div>
      <label class="disconnect-check">
        <input v-model="disconnectConfirmed" type="checkbox" :disabled="!!pending" />
        <span>我已核对车辆、现场状态和原任务责任，理解断开通信不等于停车或急停</span>
      </label>
      <template #footer>
        <div class="disconnect-actions">
          <button :disabled="!!pending" @click="closeConnectionConfirmation">取消</button>
          <button class="danger-action" :disabled="!disconnectConfirmed || !!pending" @click="confirmDisconnect">
            {{ pending ? '正在断开…' : '确认断开通信' }}
          </button>
        </div>
      </template>
    </ConsoleDialog>
  </section>
</template>

<style scoped>
.obstacle-summary{display:flex;flex-direction:column;align-items:flex-start;gap:5px;line-height:1.5;white-space:normal;text-align:left;border:0;background:transparent;padding:0;min-height:0}
.obstacle-summary small{color:var(--ui-text-3);font-size:10px}
.battery-value{color:var(--ui-success);font-family:Consolas,monospace}
.table-actions{display:flex;align-items:center;gap:2px;flex-wrap:wrap}
.disconnect-target{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:8px}
.disconnect-target>div{min-width:0;padding:10px 12px;border:1px solid var(--ui-line);border-radius:7px;background:var(--ui-surface-raised)}
.disconnect-target span{display:block;color:var(--ui-text-3);font-size:10px}
.disconnect-target b{display:block;margin-top:5px;color:var(--ui-text);font:12px Consolas,monospace;overflow-wrap:anywhere}
.disconnect-boundary{margin-top:12px;padding:12px 14px;border:1px solid color-mix(in srgb,var(--ui-danger) 36%,var(--ui-line));border-radius:7px;background:color-mix(in srgb,var(--ui-danger) 7%,var(--ui-surface-raised))}
.disconnect-boundary strong{color:var(--ui-text)}
.disconnect-boundary ul{margin:7px 0 0;padding-left:18px;color:var(--ui-text-2);font-size:11px;line-height:1.8}
.disconnect-check{display:flex;align-items:flex-start;gap:8px;margin-top:12px;color:var(--ui-text-2);font-size:11px;line-height:1.7}
.disconnect-check input{margin-top:2px}
.disconnect-actions{display:flex;align-items:center;justify-content:flex-end;gap:8px;width:100%}
.danger-action{border-color:color-mix(in srgb,var(--ui-danger) 55%,var(--ui-line))!important;background:color-mix(in srgb,var(--ui-danger) 12%,var(--ui-surface-raised))!important;color:var(--ui-text)!important}
@media(max-width:600px){.disconnect-target{grid-template-columns:1fr}.disconnect-actions{align-items:stretch;flex-direction:column}.disconnect-actions button{width:100%}}
</style>
