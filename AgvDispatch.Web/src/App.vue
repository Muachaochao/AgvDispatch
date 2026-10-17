<script setup lang="ts">
import { onMounted, onUnmounted, computed } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { useDispatchStore } from './stores/dispatch';
import { shortTime } from './dashboard';
import { ElContainer, ElHeader, ElMain, ElFooter, ElMenu, ElMenuItem, ElBadge, ElButton, ElTag, ElSpace, ElIcon, ElTooltip } from 'element-plus';
import { Monitor, Connection, Plus, Refresh, Van, List, Bell, MapLocation, EditPen, CircleCheck, Cpu, Setting } from '@element-plus/icons-vue';
import AppToast from './components/AppToast.vue';
import AppLoading from './components/AppLoading.vue';

const store = useDispatchStore();
const router = useRouter();
const route = useRoute();

const navigation = [
  { path: '/', label: '调度大屏', icon: Monitor },
  { path: '/vehicles', label: '车辆列表', icon: Van },
  { path: '/tasks', label: '任务列表', icon: List },
  { path: '/alarms', label: '异常告警', icon: Bell },
  { path: '/maps', label: '地图查看', icon: MapLocation },
  { path: '/planning', label: '工程编辑', icon: EditPen },
  { path: '/commissioning', label: '联调检查', icon: CircleCheck }
] as const;

const activeMenu = computed(() => {
  if (route.path === '/') return '/';
  const matched = navigation.find(n => n.path !== '/' && route.path.startsWith(n.path));
  return matched?.path ?? '/';
});

onMounted(() => {
  store.startNowTimer();
  void store.connectBackend();
});
onUnmounted(() => {
  store.dispose();
});
</script>

<template>
  <div class="dark console-shell">
    <AppLoading :active="!store.snapshot && !store.error" message="正在连接C#调度主服务…" />

    <ElContainer direction="vertical" style="height:100dvh;min-height:680px">
      <ElHeader class="app-header" height="64px">
        <div class="brand" @click="router.push('/')">
          <ElIcon :size="28" color="#67e8f9"><Monitor /></ElIcon>
          <div>
            <h1>AGV 机器人调度系统</h1>
            <span class="brand-sub">DISPATCH CONTROL CENTER</span>
          </div>
        </div>

        <div class="header-stats">
          <div class="stat-item"><span>执行任务</span><b>{{ store.ringSegments[0].value }}</b></div>
          <div class="stat-item"><span>等待任务</span><b class="text-cyan">{{ store.ringSegments[1].value }}</b></div>
          <div class="stat-item">
            <span>未确认告警</span>
            <ElBadge :value="store.openAlarms.length" :hidden="!store.openAlarms.length" type="danger">
              <b>{{ store.openAlarms.length }}</b>
            </ElBadge>
          </div>
        </div>

        <ElMenu mode="horizontal" :default-active="activeMenu" :ellipsis="false" class="app-nav" @select="(path: string) => router.push(path)">
          <ElMenuItem v-for="item in navigation" :key="item.path" :index="item.path">
            <ElIcon><component :is="item.icon" /></ElIcon>
            <span>{{ item.label }}</span>
          </ElMenuItem>
        </ElMenu>

        <ElSpace class="header-actions">
          <ElButton type="primary" @click="router.push({ name: 'tasks', query: { create: '1' } })">
            <ElIcon><Plus /></ElIcon>创建任务
          </ElButton>
          <ElTooltip content="重新连接后台" placement="bottom">
            <ElButton circle @click="store.connectBackend()">
              <ElIcon><Refresh /></ElIcon>
            </ElButton>
          </ElTooltip>
        </ElSpace>
      </ElHeader>

      <div class="source-bar" :class="{ disconnected: !store.fresh }">
        <div class="source-indicator">
          <ElTag :type="store.fresh ? 'success' : 'danger'" size="small" effect="dark" round>
            {{ store.fresh ? '已连接' : '未连接' }}
          </ElTag>
          <span class="source-text">{{ store.error || (store.snapshot ? `最近观察 ${shortTime(store.snapshot.receivedAt)}` : '正在连接C#调度主服务…') }}</span>
        </div>
        <ElButton text size="small" @click="router.push('/commissioning')">
          {{ store.fresh ? '主服务' : '最后观察' }}
          {{ store.snapshot?.status?.service?.version ?? '版本未返回' }} ·
          {{ store.snapshot?.status?.execution?.handshakeEnabled ? '持久化握手' : '原执行器' }}
        </ElButton>
      </div>

      <ElMain class="app-main">
        <router-view v-slot="{ Component }">
          <component :is="Component" :snapshot="store.snapshot" :adapter="store.adapterIdentity" />
        </router-view>
      </ElMain>

      <ElFooter class="app-footer" height="52px">
        <div class="footer-devices">
          <ElTag v-for="device in store.equipment.slice(0, 4)" :key="device.id" :type="store.fresh && device.quality === 'Good' ? 'success' : 'warning'" effect="plain" size="small">
            <ElIcon :size="14"><Cpu /></ElIcon>
            {{ device.name }} · {{ store.fresh ? device.status : '非当前' }}
          </ElTag>
        </div>
        <ElSpace :size="16">
          <ElTag :type="store.fresh ? 'success' : 'danger'" effect="plain" size="small">
            <ElIcon :size="14"><Connection /></ElIcon>
            后台通信 · {{ store.fresh ? '已连接' : '未连接' }}
          </ElTag>
          <ElTag :type="store.snapshot?.status?.safety.dispatchingEnabled ? 'success' : 'info'" effect="plain" size="small">
            <ElIcon :size="14"><Setting /></ElIcon>
            {{ store.snapshot?.status?.safety.dispatchingEnabled ? '调度已启用' : '调度未启用' }}
          </ElTag>
        </ElSpace>
      </ElFooter>
    </ElContainer>

    <AppToast />
  </div>
</template>

<style scoped>
.app-header{display:flex;align-items:center;gap:16px;background:#0d1b2a;border-bottom:1px solid #1e3a5f;padding:0 20px;z-index:10}
.brand{display:flex;align-items:center;gap:12px;cursor:pointer;flex-shrink:0}
.brand h1{font-size:20px;letter-spacing:2px;color:#a9e7f4;font-weight:650;margin:0;line-height:1.3}
.brand-sub{font-size:8px;letter-spacing:3px;color:#5a8aa8;display:block}
.header-stats{display:flex;gap:20px;margin-left:auto;margin-right:12px}
.stat-item{text-align:center}.stat-item span{display:block;font-size:10px;color:#8da3c6;white-space:nowrap}
.stat-item b{font-family:Consolas,monospace;font-size:20px;letter-spacing:1px}
.text-cyan{color:#67e8f9}
.app-nav{background:transparent;border:none;flex-shrink:1;overflow:hidden}
.app-nav .el-menu-item{color:#b9cde9;height:62px;font-size:12px}
.app-nav .el-menu-item.is-active{color:#67e8f9;border-bottom-color:#67e8f9}
.header-actions{flex-shrink:0}
.source-bar{display:flex;justify-content:space-between;align-items:center;gap:12px;padding:0 20px;min-height:32px;background:#0a1525;font-size:11px}
.source-bar.disconnected{background:#1a0d0d}
.source-indicator{display:flex;gap:8px;align-items:center;min-width:0}
.source-text{color:#8ca1c1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.app-main{padding:16px 20px;background:#070d20;overflow:auto}
.app-footer{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:0 20px;background:#0d1b2a;border-top:1px solid #1e3a5f;overflow-x:auto}
.footer-devices{display:flex;gap:8px;overflow:hidden}
@media(max-width:1400px){.header-stats{display:none}}
@media(max-width:1100px){.app-nav .el-menu-item span{display:none}.app-nav .el-menu-item{padding:0 12px}}
</style>
