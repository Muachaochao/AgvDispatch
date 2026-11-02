<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { useDispatchStore } from './stores/dispatch';
import { shortTime } from './dashboard';
import { chargerMonitoringState, equipmentTone, stationSignalSummary } from './equipment-status';
import {
  IconRobot, IconRoute, IconBell, IconLayoutDashboard, IconListDetails, IconMap,
  IconPlus, IconPlugConnected, IconCircleCheck, IconCpu, IconNetwork, IconChevronDown, IconFlask
} from '@tabler/icons-vue';
import ConsoleDialog from './ConsoleDialog.vue';
import AppToast from './components/AppToast.vue';
import AppLoading from './components/AppLoading.vue';

const store = useDispatchStore();
const router = useRouter();
const route = useRoute();
const connectionOpen = ref(false);
const stationSignalsOpen = ref(false);
const selectedStationId = ref('');
const plcDevices = computed(() => store.equipment.filter(device => device.category === 'PLC' || device.quality === '连接诊断'));
const stationDevices = computed(() => store.equipment.filter(device => !plcDevices.value.some(plc => plc.id === device.id)));
const stationSignals = computed(() => stationSignalSummary(stationDevices.value, store.fresh));
const selectedStation = computed(() => stationDevices.value.find(device => device.id === selectedStationId.value));
const engineeringMenu = ref<HTMLDetailsElement>();
const dateFormatter = new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Shanghai', year: 'numeric', month: '2-digit', day: '2-digit', weekday: 'short' });
const timeFormatter = new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Shanghai', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' });
const clockDate = computed(() => dateFormatter.format(store.now));
const clockTime = computed(() => timeFormatter.format(store.now));
const connectionState = computed(() => store.fresh ? '已连接' : store.snapshot ? '断连 / 数据过期' : store.error ? '未连接' : '连接中');
const lastObservation = computed(() => store.snapshot ? `最近采样 ${shortTime(store.snapshot.receivedAt)}` : '尚无后台观察');
const chargerMonitor = chargerMonitoringState();

const navigation = [
  { path: '/', label: '调度大屏', icon: IconLayoutDashboard },
  { path: '/vehicles', label: '车辆列表', icon: IconRobot },
  { path: '/tasks', label: '业务任务', icon: IconListDetails },
  { path: '/recovery', label: '异常恢复', icon: IconCircleCheck },
  { path: '/alarms', label: '异常告警', icon: IconBell },
  { path: '/maps', label: '地图管理', icon: IconMap }
] as const;
const engineering = [
  { path: '/charging', label: '充电管理', icon: IconPlugConnected },
  { path: '/engineering', label: '工程配置中心', icon: IconCpu },
  { path: '/engineering-runtime', label: '工程执行接入', icon: IconCpu },
  { path: '/engineering-tests', label: '工程测试中心', icon: IconFlask },
  { path: '/communication', label: 'PLC / AGV通信配置', icon: IconNetwork },
  { path: '/plc-cycle-test', label: 'PLC空载联调', icon: IconRoute },
  { path: '/navigation-test', label: '单车导航调试', icon: IconRoute },
  { path: '/planning', label: '工程编辑', icon: IconRoute },
  { path: '/engineering-settings', label: '现场任务参数与地图绑定', icon: IconCpu },
  { path: '/commissioning', label: '联调检查', icon: IconCircleCheck }
] as const;
function closeMenu(event: Event) {
  if (!engineeringMenu.value?.contains(event.target as Node)) engineeringMenu.value?.removeAttribute('open');
}
watch(() => route.fullPath, () => engineeringMenu.value?.removeAttribute('open'));

function isActive(path: string) {
  if (path === '/') return route.path === '/';
  return route.path.startsWith(path);
}

onMounted(() => {
  store.startNowTimer();
  void store.connectBackend();
  document.addEventListener('pointerdown', closeMenu);
});
onUnmounted(() => {
  store.dispose();
  document.removeEventListener('pointerdown', closeMenu);
});
</script>

<template>
  <div class="console-shell">
    <AppLoading :active="!store.snapshot && !store.error" message="正在连接C#调度主服务…" />

    <header class="console-header">
      <div class="brand-and-clock"><button class="console-brand" @click="router.push('/')">
        <IconRobot :size="31" stroke="1.5" />
        <div><h1>AGV 机器人调度系统</h1><span>DISPATCH CONTROL CENTER</span></div>
      </button>
      <time class="header-clock" :datetime="new Date(store.now).toISOString()" aria-label="北京时间"><b>{{clockTime}}</b><span>{{clockDate}}</span></time></div>
      <nav aria-label="页面导航">
        <button v-for="item in navigation" :key="item.path" :class="{ active: isActive(item.path) }" :aria-current="isActive(item.path) ? 'page' : undefined" @click="router.push(item.path)">
          <component :is="item.icon" :size="15" />{{ item.label }}
        </button>
        <details ref="engineeringMenu" class="engineering-menu" @keydown.esc="engineeringMenu?.removeAttribute('open'); engineeringMenu?.querySelector('summary')?.focus()">
          <summary :class="{ active: engineering.some(item => isActive(item.path)) }"><IconCpu :size="15" />工程工具<IconChevronDown :size="13" /></summary>
          <div class="engineering-dropdown" aria-label="工程工具">
            <button v-for="item in engineering" :key="item.path" :class="{ active: isActive(item.path) }" :aria-current="isActive(item.path) ? 'page' : undefined" @click="router.push(item.path); engineeringMenu?.removeAttribute('open')"><component :is="item.icon" :size="16" />{{item.label}}</button>
          </div>
        </details>
      </nav>
      <div class="header-actions">
        <button class="accent-button" @click="router.push({ name: 'tasks', query: { create: '1' } })"><IconPlus :size="16" />创建任务</button>
      </div>
    </header>


    <router-view v-slot="{ Component }">
      <component :is="Component" :snapshot="store.snapshot" :adapter="store.adapterIdentity" />
    </router-view>

    <footer v-if="store.engineeringSimulationOnly" class="engineering-isolated-footer">工程隔离模拟 · 设备、任务与现场恢复接口已禁用 · 不连接真车或PLC · 现场5199未改动</footer>
    <footer v-else class="equipment-strip">
      <b>设备状态<small>DEVICE STATUS</small></b>
      <button v-for="device in plcDevices" :key="device.id" title="查看设备通信与执行门禁" @click="router.push('/commissioning')">
        <IconCpu :size="19" /><span>{{ device.name }}</span>
        <i class="status-dot" :class="equipmentTone(device, store.fresh)"></i>
        <small>{{ store.fresh ? device.status : '数据非当前' }}</small>
      </button>
      <button class="station-signal-summary" title="查看各工位的通信、业务状态和PLC点位；信号正常不代表已允许派车" @click="stationSignalsOpen = true">
        <IconCpu :size="19" /><span>工位信号</span>
        <i class="status-dot" :class="stationSignals.tone"></i><small>{{stationSignals.label}}</small>
      </button>
      <button class="charger-monitor" :title="chargerMonitor.summary" @click="router.push('/charging')">
        <IconPlugConnected :size="19" /><span>充电桩</span>
        <i class="status-dot" :class="chargerMonitor.tone"></i><small>{{chargerMonitor.label}}</small>
      </button>
      <div class="backend-status" :class="{disconnected:!store.fresh}">
        <button class="backend-details-button" aria-label="后台连接详情" @click="connectionOpen = true"><IconNetwork :size="19" /><span>后台通信</span></button>
        <i class="status-dot" :class="store.fresh ? 'green' : 'red'"></i>
        <div class="backend-observation"><b role="status">{{connectionState}}</b><small :title="store.error || lastObservation">{{store.error || lastObservation}}</small></div>
        <button class="icon-button" aria-label="重新连接后台" title="重新连接后台" @click="store.connectBackend()"><IconPlugConnected :size="17" /></button>
      </div>
      <div><IconRoute :size="20" /><span>执行开关</span><small>{{ !store.fresh || !store.snapshot?.status ? '未取得有效状态' : store.snapshot.status.safety.dispatchingEnabled ? '调度已启用' : '调度未启用' }}</small></div>
    </footer>

    <ConsoleDialog v-if="stationSignalsOpen" title="工位信号" wide @close="stationSignalsOpen = false; selectedStationId = ''">
      <p class="form-explanation">此处查看信号采集与握手状态。未呼叫、等待握手属于业务状态，不代表通信故障。</p>
      <p v-if="!store.fresh" class="inline-warning">后台数据非当前，以下信息仅供回看。</p>
      <p v-if="!stationDevices.length" class="empty-panel">尚未配置工位。</p>
      <div class="station-signal-list">
        <button v-for="device in stationDevices" :key="device.id" :aria-pressed="selectedStationId === device.id" @click="selectedStationId = selectedStationId === device.id ? '' : device.id">
          <i class="status-dot" :class="equipmentTone(device, store.fresh)"></i><b>{{device.name}}</b><span>采集：{{device.quality}}</span><small>{{device.status}}</small><span>查看点位</span>
        </button>
      </div>
      <template v-if="selectedStation">
        <h3 class="signal-detail-heading">{{selectedStation.name}} · 点位详情</h3>
        <p class="form-explanation">{{selectedStation.summary}}</p>
        <div class="signal-detail-table"><table class="dialog-table"><thead><tr><th>信号</th><th>值</th><th>质量</th><th>地址</th><th>更新时间</th></tr></thead>
          <tbody><tr v-for="field in selectedStation.fields" :key="field.name"><td>{{field.name}}</td><td>{{field.value}}</td><td>{{field.quality}}</td><td>{{field.address}}</td><td>{{field.updatedAt}}</td></tr></tbody>
        </table></div>
      </template>
    </ConsoleDialog>
    <ConsoleDialog v-if="connectionOpen" title="后台连接详情" @close="connectionOpen = false">
      <p class="connection-summary" :class="store.fresh ? 'green-text' : 'amber-text'">{{connectionState}} · {{lastObservation}}</p>
      <p v-if="store.error" class="inline-warning">{{store.error}}</p>
      <dl class="property-list">
        <dt>主服务版本</dt><dd>{{store.snapshot?.status?.service?.version ?? '未取得'}}</dd>
        <dt>发布批次</dt><dd>{{store.snapshot?.status?.execution?.informationalVersion ?? '未取得'}}</dd>
        <dt>执行模式</dt><dd>{{store.snapshot?.status?.execution ? store.snapshot.status.execution.handshakeEnabled ? '持久化新握手' : '原执行器' : '未取得'}}</dd>
        <dt>构建身份</dt><dd>{{store.snapshot?.status?.execution?.buildId ?? '未取得'}}</dd>
        <dt>活动地图</dt><dd>{{store.snapshot?.map ? `${store.snapshot.map.name} / ${store.snapshot.map.version}` : '未取得活动地图'}}</dd>
      </dl>
      <template #footer><button @click="connectionOpen = false">关闭</button><button @click="connectionOpen = false; router.push('/commissioning')">联调检查</button><button class="accent-button" @click="store.connectBackend()">重新连接后台</button></template>
    </ConsoleDialog>
    <AppToast />
  </div>
</template>

<style scoped>
.equipment-strip>button{background:transparent;border-color:transparent;min-width:150px}
.equipment-strip>button:hover{background:#172a44;border-color:#315273}
.equipment-strip>.charger-monitor{min-width:215px}
.equipment-strip>.station-signal-summary{min-width:185px}
.station-signal-list{display:grid;gap:8px;margin-top:16px}
.station-signal-list>button{justify-content:flex-start;padding:12px;white-space:normal;flex-wrap:wrap;text-align:left}
.station-signal-list>button[aria-pressed=true]{border-color:var(--cyan);background:#15314c}
.station-signal-list b{min-width:105px}
.station-signal-list small{color:var(--muted)}
.station-signal-list>button>span:last-child{margin-left:auto;color:var(--cyan)}
.signal-detail-heading{font-size:13px;margin:20px 0 10px}
.signal-detail-table{overflow:auto;margin-top:10px}
.signal-detail-table td{white-space:nowrap}
</style>
