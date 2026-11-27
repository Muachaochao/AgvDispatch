<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import {
  IconArrowsShuffle, IconBooks, IconCalendarEvent, IconChartArrows, IconChevronDown,
  IconFlask, IconLayoutDashboard, IconListDetails, IconLogout, IconMap2, IconMenu2,
  IconReport, IconRoute, IconSettings, IconTool, IconUsers, IconX
} from '@tabler/icons-vue'
import { useAuthStore } from '@/stores/auth'
import { dispatchLocation, dispatchSection, dispatchSections, type DispatchSection } from '@/integration/dispatch-workspace'
import '@/styles/workspace.css'

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()

const groups = [
  { title: '仿真准备', sections: ['overview', 'engineering', 'plan'] },
  { title: '运行与诊断', sections: ['monitor', 'diagnosis', 'faults', 'recovery'] },
  { title: '验收', sections: ['report'] }
] as const
const parentSection: Partial<Record<DispatchSection, DispatchSection>> = {
  environment: 'engineering', scenarios: 'plan',
  'task-evidence': 'monitor', 'report-evidence': 'report'
}
const extras = [
  { path: '/roadnet', title: '车间地图', icon: IconMap2 },
  { path: '/tasks', title: '点到点任务集', icon: IconListDetails },
  { path: '/simulation?mode=native', title: '原生算法', icon: IconRoute },
  { path: '/simulation?mode=compare', title: '算法对比', icon: IconArrowsShuffle }
]
const settings = [
  { path: '/admin/users', title: '用户管理', icon: IconUsers },
  { path: '/admin/dict', title: '数字字典', icon: IconBooks }
]
const sectionIcons = {
  overview: IconLayoutDashboard,
  engineering: IconMap2,
  plan: IconCalendarEvent,
  monitor: IconListDetails,
  diagnosis: IconChartArrows,
  faults: IconFlask,
  recovery: IconTool,
  report: IconReport
} satisfies Partial<Record<DispatchSection, typeof IconMap2>>
const section = computed(() => dispatchSection(route.query.section))
const mainMode = computed(() => route.path === '/dashboard' || (route.path === '/simulation' && !['native', 'compare'].includes(String(route.query.mode))))
const activeSection = computed(() => mainMode.value ? parentSection[section.value] ?? section.value : null)
const activeExtra = computed(() => extras.find(item => item.path.includes('?')
  ? route.path === '/simulation' && item.path.endsWith(`=${route.query.mode}`)
  : item.path === route.path))
const currentTitle = computed(() => mainMode.value ? dispatchSections[section.value]
  : activeExtra.value?.title ?? String(route.meta.title ?? ''))
const menuOpen = ref(false)
const menuToggle = ref<HTMLButtonElement>()
const menuClose = ref<HTMLButtonElement>()

async function openMenu() {
  menuOpen.value = true
  await nextTick()
  menuClose.value?.focus()
}

function closeMenu() {
  if (!menuOpen.value) return
  menuOpen.value = false
  menuToggle.value?.focus()
}

watch(() => route.fullPath, closeMenu)

function logout() {
  auth.logout()
  ElMessage.success('已退出登录')
  router.push('/login')
}
</script>

<template>
  <el-container class="layout paice-workspace" @keydown.esc="closeMenu">
    <div v-if="menuOpen" class="menu-backdrop" aria-hidden="true" @click="closeMenu"></div>
    <el-aside id="workspace-sidebar" width="208px" class="aside" :class="{ 'menu-open': menuOpen }">
      <div class="brand-bar">
        <RouterLink class="brand" :to="dispatchLocation('overview')" @click="closeMenu">
          <span class="brand-mark"><IconRoute :size="22" aria-hidden="true" /></span>
          <span>排策智演</span>
        </RouterLink>
        <button ref="menuClose" class="icon-button menu-close" type="button" aria-label="关闭菜单" @click="closeMenu">
          <IconX :size="20" aria-hidden="true" />
        </button>
      </div>
      <nav class="menu" aria-label="仿真工作区">
        <section v-for="group in groups" :key="group.title">
          <h2>{{ group.title }}</h2>
          <RouterLink v-for="item in group.sections" :key="item" :to="dispatchLocation(item)"
            :class="{ selected: item === activeSection }" :aria-current="item === activeSection ? 'page' : undefined" @click="closeMenu">
            <component :is="sectionIcons[item]" :size="18" aria-hidden="true" />
            <span>{{ dispatchSections[item] }}</span>
          </RouterLink>
        </section>
        <details :open="!!activeExtra">
          <summary><IconFlask :size="18" aria-hidden="true" /><span>算法实验</span><IconChevronDown class="chevron" :size="14" aria-hidden="true" /></summary>
          <RouterLink v-for="item in extras" :key="item.path" :to="item.path"
            :class="{ selected: item === activeExtra }" :aria-current="item === activeExtra ? 'page' : undefined" @click="closeMenu">
            <component :is="item.icon" :size="17" aria-hidden="true" /><span>{{ item.title }}</span>
          </RouterLink>
        </details>
        <details v-if="auth.isAdmin" :open="route.path.startsWith('/admin/')">
          <summary><IconSettings :size="18" aria-hidden="true" /><span>系统设置</span><IconChevronDown class="chevron" :size="14" aria-hidden="true" /></summary>
          <RouterLink v-for="item in settings" :key="item.path" :to="item.path"
            :class="{ selected: route.path === item.path }" :aria-current="route.path === item.path ? 'page' : undefined" @click="closeMenu">
            <component :is="item.icon" :size="17" aria-hidden="true" /><span>{{ item.title }}</span>
          </RouterLink>
        </details>
      </nav>
    </el-aside>

    <el-container class="workspace-content">
      <el-header class="header">
        <div class="header-left">
          <button ref="menuToggle" class="icon-button menu-toggle" type="button" aria-label="打开菜单"
            aria-controls="workspace-sidebar" :aria-expanded="menuOpen" @click="openMenu">
            <IconMenu2 :size="21" aria-hidden="true" />
          </button>
          <h1>{{ currentTitle }}</h1>
        </div>
        <div class="header-right">
          <div class="account">
            <span class="username">{{ auth.displayName }}</span>
            <span class="role">{{ auth.isAdmin ? '管理员' : '调度人员' }}</span>
          </div>
          <el-button class="logout" text @click="logout"><IconLogout :size="17" aria-hidden="true" />退出</el-button>
        </div>
      </el-header>

      <el-main class="main">
        <router-view />
      </el-main>
    </el-container>
  </el-container>
</template>

<style scoped>
.layout {
  height: 100%;
  background: var(--workspace-bg);
  color: var(--ui-text);
}

.aside {
  background: #131d30;
  display: flex;
  flex-direction: column;
}

.brand-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  min-height: 68px;
  padding: 0 16px;
  flex-shrink: 0;
  border-bottom: 1px solid #ffffff14;
}

.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  color: #f6f8fc;
  font-size: 18px;
  font-weight: 650;
  text-decoration: none;
}

.brand-mark {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 8px;
  background: #3858d0;
  color: #fff;
}

.menu {
  flex: 1;
  overflow-y: auto;
  padding: 6px 10px 18px;
  scrollbar-color: #43516a transparent;
}

.menu section { margin: 16px 0; }
.menu h2 { margin: 0 0 6px 12px; font-size: 12px; font-weight: 550; color: #a1afc5; }
.menu a, .menu summary {
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 40px;
  padding: 7px 11px;
  margin: 2px 0;
  border-radius: 7px;
  color: #c6d1e3;
  text-decoration: none;
  font-size: 14px;
}

.menu a > svg, .menu summary > svg { flex: none; color: #a7b8d5; }
.menu summary { list-style: none; cursor: pointer; }
.menu summary::-webkit-details-marker { display: none; }
.menu .chevron { margin-left: auto; }
.menu details[open] .chevron { transform: rotate(180deg); }
.menu details a { margin-left: 12px; font-size: 13px; }
.menu a:hover, .menu summary:hover {
  background: #ffffff12;
  color: #fff;
}

.menu a.selected {
  background: #29438d;
  color: #fff;
  box-shadow: inset 3px 0 #8ba6ff;
}

.menu a.selected > svg { color: #dce5ff; }
.workspace-content { min-width: 0; min-height: 0; }

.header, .header-left, .header-right {
  display: flex;
  align-items: center;
}

.header {
  --el-header-height: 68px;
  justify-content: space-between;
  gap: 12px;
  padding: 0 clamp(14px, 2vw, 28px);
  background: var(--ui-surface);
  border-bottom: 1px solid var(--ui-line);
}

.header-left, .header-right { gap: 12px; min-width: 0; }
.header h1 {
  margin: 0;
  font-size: clamp(18px, 1.55vw, 23px);
  font-weight: 680;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.account { display: grid; min-width: 0; gap: 2px; text-align: right; }
.username { font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.role { color: var(--ui-text-3); font-size: 11px; }
.logout { flex: none; }
.logout :deep(span) { gap: 5px; }

.icon-button {
  display: none;
  align-items: center;
  justify-content: center;
  flex: none;
  width: 38px;
  height: 38px;
  border: 1px solid var(--ui-line);
  border-radius: 7px;
  color: var(--ui-text);
  background: var(--ui-surface);
  cursor: pointer;
}

.menu-backdrop { display: none; }
.main {
  min-height: 0;
  padding: clamp(12px, 1.65vw, 24px);
  scrollbar-gutter: stable;
}

@media (max-width: 860px) {
  .aside {
    display: none;
    position: fixed;
    z-index: 903;
    inset: 0 auto 0 0;
    width: min(260px, 86vw);
  }
  .aside.menu-open { display: flex; }
  .menu-backdrop { display: block; position: fixed; z-index: 902; inset: 0; background: #0f172a80; }
  .menu-toggle, .menu-close { display: inline-flex; }
  .menu-close { border-color: #ffffff24; background: transparent; color: #f6f8fc; }
}

@media (max-width: 420px) {
  .header-right { gap: 4px; }
  .role { display: none; }
  .username { max-width: 82px; }
}
</style>
