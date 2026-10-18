<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { useDispatchStore } from './stores/dispatch';
import { shortTime } from './dashboard';
import {
  IconRobot, IconRoute, IconBell, IconLayoutDashboard, IconListDetails, IconMap,
  IconPlus, IconPlugConnected, IconCircleCheck, IconCpu, IconNetwork
} from '@tabler/icons-vue';
import { isFresh } from './state';
import type { AdapterIdentity } from './release-state';
import AppToast from './components/AppToast.vue';
import AppLoading from './components/AppLoading.vue';

const store = useDispatchStore();
const router = useRouter();
const route = useRoute();

const navigation = [
  { path: '/', label: '调度大屏', icon: IconLayoutDashboard },
  { path: '/vehicles', label: '车辆列表', icon: IconRobot },
  { path: '/tasks', label: '任务列表', icon: IconListDetails },
  { path: '/alarms', label: '异常告警', icon: IconBell },
  { path: '/maps', label: '地图查看', icon: IconMap },
  { path: '/planning', label: '工程编辑', icon: IconRoute },
  { path: '/engineering-settings', label: '工程配置', icon: IconCpu },
  { path: '/commissioning', label: '联调检查', icon: IconCircleCheck }
] as const;

function isActive(path: string) {
  if (path === '/') return route.path === '/';
  return route.path.startsWith(path);
}

onMounted(() => {
  store.startNowTimer();
  void store.connectBackend();
});
onUnmounted(() => {
  store.dispose();
});
</script>

<template>
  <div class="console-shell">
    <AppLoading :active="!store.snapshot && !store.error" message="正在连接C#调度主服务…" />

    <header class="console-header">
      <button class="console-brand" @click="router.push('/')">
        <IconRobot :size="31" stroke="1.5" />
        <div><h1>AGV 机器人调度系统</h1><span>DISPATCH CONTROL CENTER</span></div>
      </button>
      <div class="header-totals">
        <div><span>执行任务</span><b>{{ store.ringSegments[0].value }}</b></div>
        <div><span>等待任务</span><b class="cyan">{{ store.ringSegments[1].value }}</b></div>
        <div><span>未确认告警</span><b :class="store.openAlarms.length ? 'amber-text' : ''">{{ store.openAlarms.length }}</b></div>
        <small>当前已加载数据</small>
      </div>
      <nav aria-label="页面导航">
        <button v-for="item in navigation" :key="item.path" :class="{ active: isActive(item.path) }" @click="router.push(item.path)">
          <component :is="item.icon" :size="15" />{{ item.label }}
        </button>
      </nav>
      <div class="header-actions">
        <button class="accent-button" @click="router.push({ name: 'tasks', query: { create: '1' } })"><IconPlus :size="16" />创建任务</button>
        <button v-if="!store.demoMode" @click="store.connectDemo()" title="启动演示模式（合成数据，无需后台）">演示模式</button>
        <button v-else class="accent-button" @click="store.connectBackend()" title="退出演示，连接真实后台">退出演示</button>
        <button class="icon-button" aria-label="重新连接后台" title="重新连接后台" @click="store.connectBackend()"><IconPlugConnected :size="19" /></button>
      </div>
    </header>

    <div class="source-bar" :class="{ disconnected: !store.fresh && !store.demoMode }">
      <div class="source-indicator">
        <i class="status-dot" :class="store.demoMode ? 'amber' : store.fresh ? 'green' : 'red'"></i>
        <b>{{ store.demoMode ? '演示模式 · 合成数据' : store.fresh ? '真实后台已连接' : '真实后台未连接 / 数据过期' }}</b>
        <span>{{ store.demoMode ? (store.rehearsalPhase===null?'九步任务流程模拟，每3秒推进一步。数据为浏览器合成，不代表真实设备。':'交通与取消恢复演练：手动推进。全部为合成教学，不代表真实设备。') : store.error || (store.snapshot ? `最近观察 ${shortTime(store.snapshot.receivedAt)}` : '正在连接C#调度主服务…') }}</span>
      </div>
      <button class="text-button" @click="router.push('/commissioning')">
        {{ store.fresh ? '主服务' : '最后观察主服务' }}
        {{ store.snapshot?.status?.service?.version ?? '版本未返回' }} ·
        {{ store.snapshot?.status?.execution ? store.snapshot.status.execution.handshakeEnabled ? '持久化新握手' : '原执行器' : '模式未返回' }}
        {{ store.snapshot?.status?.execution?.buildId.slice(0, 12) ?? '' }}
      </button>
      <button class="text-button" @click="store.connectBackend()">重新连接后台</button>
    </div>

    <router-view v-slot="{ Component }">
      <component :is="Component" :snapshot="store.snapshot" :adapter="store.adapterIdentity" />
    </router-view>

    <footer class="equipment-strip">
      <b>设备状态<small>DEVICE STATUS</small></b>
      <button v-for="device in store.equipment.slice(0, 4)" :key="device.id">
        <IconCpu :size="19" /><span>{{ device.name }}</span>
        <i class="status-dot" :class="store.fresh && device.quality === 'Good' ? 'green' : 'amber'"></i>
        <small>{{ store.fresh ? device.status : '数据非当前' }}</small>
      </button>
      <div><IconNetwork :size="20" /><span>后台通信</span><i class="status-dot" :class="store.fresh ? 'green' : 'red'"></i><small>{{ store.fresh ? '已连接' : '未连接' }}</small></div>
      <div><IconRoute :size="20" /><span>执行开关</span><small>{{ !store.fresh || !store.snapshot?.status ? '未取得有效状态' : store.snapshot.status.safety.dispatchingEnabled ? '调度已启用' : '调度未启用' }}</small></div>
    </footer>

    <AppToast />
  </div>
</template>
