<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { useDispatchStore } from './stores/dispatch';
import { useChargingStore } from './stores/charging';
import { shortTime } from './dashboard';
import { chargerMonitoringState, equipmentTone, stationSignalSummary, executionModeState } from './equipment-status';
import {
  IconRobot, IconRoute, IconBell, IconLayoutDashboard, IconListDetails, IconMap,
  IconPlus, IconPlugConnected, IconCircleCheck, IconCpu, IconNetwork, IconChevronDown, IconFlask
} from '@tabler/icons-vue';
import ConsoleDialog from './ConsoleDialog.vue';
import AppToast from './components/AppToast.vue';
import AppLoading from './components/AppLoading.vue';
import { industrialText, qualityLabel, signalLabel } from './operator-labels';

const store = useDispatchStore();
const charging = useChargingStore();
const router = useRouter();
const route = useRoute();
const connectionOpen = ref(false);
const stationSignalsOpen = ref(false);
const executionModeOpen = ref(false);
const selectedStationId = ref('');
const plcDevices = computed(() => store.equipment.filter(device => device.category === 'PLC' || device.quality === '连接诊断'));
const stationDevices = computed(() => store.equipment.filter(device => !plcDevices.value.some(plc => plc.id === device.id)));
const stationSignals = computed(() => stationSignalSummary(stationDevices.value, store.fresh));
const dumperCount = computed(() => stationDevices.value.filter(device => device.category === '倒料机').length);
const stationCount = computed(() => stationDevices.value.length - dumperCount.value);
const executionMode = computed(() => executionModeState(store.snapshot?.status?.safety, store.fresh));
const selectedStation = computed(() => stationDevices.value.find(device => device.id === selectedStationId.value));
const engineeringMenu = ref<HTMLDetailsElement>();
const dateFormatter = new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Shanghai', year: 'numeric', month: '2-digit', day: '2-digit', weekday: 'short' });
const timeFormatter = new Intl.DateTimeFormat('zh-CN', { timeZone: 'Asia/Shanghai', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' });
const clockDate = computed(() => dateFormatter.format(store.now));
const clockTime = computed(() => timeFormatter.format(store.now));
const connectionState = computed(() => store.fresh ? '已连接' : store.snapshot ? '断连 / 数据过期' : store.error ? '未连接' : '连接中');
const lastObservation = computed(() => store.snapshot ? `最近采样 ${shortTime(store.snapshot.receivedAt)}` : '尚无后台观察');
const chargerMonitor = computed(() => chargerMonitoringState(charging.status, charging.loadError, store.now));

const navigation = [
  { path: '/', label: '调度大屏', icon: IconLayoutDashboard },
  { path: '/vehicles', label: '车辆列表', icon: IconRobot },
  { path: '/tasks', label: '业务任务', icon: IconListDetails },
  { path: '/recovery', label: '异常恢复', icon: IconCircleCheck },
  { path: '/alarms', label: '异常告警', icon: IconBell },
  { path: '/maps', label: '地图管理', icon: IconMap }
] as const;
const engineering = [
  { path: '/dispatch-pipeline', label: '六层调度监控', group: '运行诊断', icon: IconRoute },
  { path: '/dispatch-optimization', label: '滚动派车优化', group: '运行诊断', icon: IconRoute },
  { path: '/engineering', label: '工程工作台', group: '工程配置', icon: IconCpu },
  { path: '/planning', label: '地图编辑', group: '工程配置', icon: IconRoute },
  { path: '/simulation-center', label: '仿真测试中心', group: '联调工具', icon: IconFlask },
  { path: '/navigation-test', label: '单车导航调试', group: '联调工具', icon: IconRoute },
  { path: '/charging', label: '充电管理', group: '联调工具', icon: IconPlugConnected },
] as const;
function closeMenu(event: Event) {
  if (!engineeringMenu.value?.contains(event.target as Node)) engineeringMenu.value?.removeAttribute('open');
}
watch(() => route.fullPath, () => engineeringMenu.value?.removeAttribute('open'));

function isActive(path: string) {
  if (path === '/') return route.path === '/';
  return route.path.startsWith(path);
}
function signalDisplay(name: string) { return signalLabel(name, name); }
function fieldValue(value: unknown) {
  if (value === true) return '1';
  if (value === false) return '0';
  if (value === null || value === undefined || value === '') return '未知';
  return String(value);
}

onMounted(() => {
  store.startNowTimer();
  void store.connectBackend().then(() => { if (!store.engineeringSimulationOnly) charging.start(); });
  document.addEventListener('pointerdown', closeMenu);
});
onUnmounted(() => {
  store.dispose();
  charging.stop();
  document.removeEventListener('pointerdown', closeMenu);
});
</script>

<template>
  <div class="console-shell">
    <AppLoading :active="!store.snapshot && !store.error" message="正在连接C#调度主服务…" />

    <header class="console-header">
      <div class="brand-and-clock"><button class="console-brand" @click="router.push('/')">
        <IconRobot :size="31" stroke="1.5" />
        <div><h1>AGV 机器人调度系统</h1><span>铝屑回收 · 实时调度</span></div>
      </button>
      <time class="header-clock" :datetime="new Date(store.now).toISOString()" aria-label="北京时间"><b>{{clockTime}}</b><span>{{clockDate}}</span></time></div>
      <nav aria-label="页面导航">
        <button v-for="item in navigation" :key="item.path" :class="{ active: isActive(item.path) }" :aria-current="isActive(item.path) ? 'page' : undefined" @click="router.push(item.path)">
          <component :is="item.icon" :size="15" />{{ item.label }}
        </button>
        <details ref="engineeringMenu" class="engineering-menu" @keydown.esc="engineeringMenu?.removeAttribute('open'); engineeringMenu?.querySelector('summary')?.focus()">
          <summary :class="{ active: engineering.some(item => isActive(item.path)) }"><IconCpu :size="15" />工程工具<IconChevronDown :size="13" /></summary>
          <div class="engineering-dropdown" aria-label="工程工具">
            <template v-for="(item,index) in engineering" :key="item.path"><p v-if="index===0||engineering[index-1]?.group!==item.group">{{item.group}}</p><button :class="{ active: isActive(item.path) }" :aria-current="isActive(item.path) ? 'page' : undefined" @click="router.push(item.path); engineeringMenu?.removeAttribute('open')"><component :is="item.icon" :size="16" />{{item.label}}</button></template>
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
      <b>设备状态<small>连接与执行许可</small></b>
      <button v-for="device in plcDevices" :key="device.id" title="查看设备通信与执行门禁" @click="router.push('/commissioning')">
        <IconCpu :size="19" /><span>{{ device.name }}</span>
        <i class="status-dot" :class="equipmentTone(device, store.fresh)"></i>
        <small>{{ store.fresh ? industrialText(device.status) : '数据非当前' }}</small>
      </button>
      <button class="station-signal-summary" title="查看各工位的通信、业务状态和PLC点位；信号正常不代表已允许派车" @click="stationSignalsOpen = true">
        <IconCpu :size="19" /><span>{{stationCount}} 工位 · {{dumperCount}} 倒料</span>
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
      <button class="execution-mode" title="查看任务派发、PLC写入和车辆指令的启用状态；此处不会切换开关" @click="executionModeOpen = true"><IconRoute :size="20" /><span>运行模式</span><i class="status-dot" :class="executionMode.tone"></i><small>{{executionMode.label}}</small></button>
    </footer>

    <ConsoleDialog v-if="stationSignalsOpen" title="工位信号" wide @close="stationSignalsOpen = false; selectedStationId = ''">
      <p class="form-explanation">当前接入 {{stationCount}} 处工位、{{dumperCount}} 台倒料机。仅统计当前运行配置，不累计历史方案中的工位。</p>
      <p class="form-explanation">此处查看信号采集与握手状态。未呼叫、等待握手属于业务状态，不代表通信故障。</p>
      <p v-if="!store.fresh" class="inline-warning">后台数据非当前，以下信息仅供回看。</p>
      <p v-if="!stationDevices.length" class="empty-panel">尚未配置工位。</p>
      <div class="station-signal-list">
        <button v-for="device in stationDevices" :key="device.id" :aria-pressed="selectedStationId === device.id" @click="selectedStationId = selectedStationId === device.id ? '' : device.id">
          <i class="status-dot" :class="equipmentTone(device, store.fresh)"></i><b>{{device.name}}</b><span>采集：{{qualityLabel(device.quality)}}</span><small>{{industrialText(device.status)}}</small><span>查看点位</span>
        </button>
      </div>
      <template v-if="selectedStation">
        <h3 class="signal-detail-heading">{{selectedStation.name}} · 点位详情</h3>
        <p class="form-explanation">{{selectedStation.summary}}</p>
        <div class="signal-detail-table"><table class="dialog-table"><thead><tr><th>信号</th><th>值</th><th>质量</th><th>地址</th><th>更新时间</th></tr></thead>
          <tbody><tr v-for="field in selectedStation.fields" :key="field.name"><td>{{signalDisplay(field.name)}}<details><summary>原始字段</summary><code>{{field.name}}</code></details></td><td>{{fieldValue(field.value)}}</td><td>{{qualityLabel(field.quality)}}</td><td>{{field.address}}</td><td>{{field.updatedAt}}</td></tr></tbody>
        </table></div>
      </template>
    </ConsoleDialog>
    <ConsoleDialog v-if="executionModeOpen" title="调度运行状态" @close="executionModeOpen = false">
      <p class="connection-summary">{{executionMode.label}}</p>
      <p class="form-explanation">这里显示后台执行权限，不是启停按钮。信号采集正常不代表可以派车；任务执行还需要通过现场恢复、车辆、地图和路线核验。</p>
      <p v-if="!store.fresh" class="inline-warning">后台数据非当前，不能据此判断执行权限。</p>
      <dl class="property-list">
        <dt>任务派发</dt><dd>{{!store.fresh || !store.snapshot?.status ? '未知' : store.snapshot.status.safety.dispatchingEnabled ? '已开启' : '未开启'}}</dd>
        <dt>PLC 写入</dt><dd>{{!store.fresh || !store.snapshot?.status ? '未知' : store.snapshot.status.safety.plcWritesAllowed ? '已开启' : '未开启'}}</dd>
        <dt>车辆指令</dt><dd>{{!store.fresh || !store.snapshot?.status ? '未知' : store.snapshot.status.safety.agvCommandsAllowed ? '已开启' : '未开启'}}</dd>
      </dl>
      <p class="form-explanation">查看模式用于看地图、设备与历史记录。需要测试业务时，通过统一业务启动入口选择车辆；开启执行权限不会替代现场核对。</p>
      <template #footer><button @click="executionModeOpen = false">关闭</button><button @click="executionModeOpen = false; router.push('/commissioning')">查看联调条件</button></template>
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
/* Grow into the available width on desktop; preserve readable horizontal
   scrolling on narrow windows instead of squeezing labels. */
.equipment-strip>button{flex:1 0 155px;min-width:0}
.equipment-strip>.charger-monitor{flex:1.2 0 220px;min-width:0}
.equipment-strip>.station-signal-summary{flex:1.2 0 215px;min-width:0}
.equipment-strip>.backend-status{flex:1.7 0 300px;min-width:0}
.equipment-strip>.execution-mode{flex:1 0 185px}
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
