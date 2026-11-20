<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useAuthStore } from '@/stores/auth'
import { dispatchLocation, dispatchSection, dispatchSections, type DispatchSection } from '@/integration/dispatch-workspace'

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()

const groups: { title: string; sections: DispatchSection[] }[] = [
  { title: '仿真准备', sections: ['overview', 'engineering', 'plan'] },
  { title: '运行与诊断', sections: ['monitor', 'diagnosis', 'faults', 'recovery'] },
  { title: '验收', sections: ['report'] }
]
const parentSection: Partial<Record<DispatchSection, DispatchSection>> = {
  environment: 'engineering', scenarios: 'plan',
  'task-evidence': 'monitor', 'report-evidence': 'report'
}
const extras = [
  { path: '/roadnet', title: '车间地图' },
  { path: '/tasks', title: '点到点任务集' },
  { path: '/simulation?mode=native', title: '原生算法' },
  { path: '/simulation?mode=compare', title: '算法对比' }
]
const settings = [
  { path: '/admin/users', title: '用户管理' },
  { path: '/admin/dict', title: '数字字典' }
]
const section = computed(() => dispatchSection(route.query.section))
const mainMode = computed(() => route.path === '/dashboard' || (route.path === '/simulation' && !['native', 'compare'].includes(String(route.query.mode))))
const activeSection = computed(() => mainMode.value ? parentSection[section.value] ?? section.value : null)
const activeExtra = computed(() => extras.find(item => item.path.includes('?')
  ? route.path === '/simulation' && item.path.endsWith(`=${route.query.mode}`)
  : item.path === route.path))
const currentTitle = computed(() => mainMode.value ? dispatchSections[section.value]
  : activeExtra.value?.title ?? String(route.meta.title ?? ''))

function logout() {
  auth.logout()
  ElMessage.success('已退出登录')
  router.push('/login')
}
</script>

<template>
  <el-container class="layout">
    <el-aside width="204px" class="aside">
      <RouterLink class="logo" :to="dispatchLocation('overview')">排策智演</RouterLink>
      <nav class="menu" aria-label="仿真工作区">
        <section v-for="group in groups" :key="group.title">
          <h2>{{ group.title }}</h2>
          <RouterLink v-for="item in group.sections" :key="item" :to="dispatchLocation(item)"
            :class="{ selected: item === activeSection }" :aria-current="item === activeSection ? 'page' : undefined">
            {{ dispatchSections[item] }}
          </RouterLink>
        </section>
        <details :open="!!activeExtra">
          <summary>算法实验</summary>
          <RouterLink v-for="item in extras" :key="item.path" :to="item.path"
            :class="{ selected: item === activeExtra }">{{ item.title }}</RouterLink>
        </details>
        <details v-if="auth.isAdmin" :open="route.path.startsWith('/admin/')">
          <summary>系统设置</summary>
          <RouterLink v-for="item in settings" :key="item.path" :to="item.path"
            :class="{ selected: route.path === item.path }">{{ item.title }}</RouterLink>
        </details>
      </nav>
    </el-aside>

    <el-container>
      <el-header class="header">
        <div class="header-title">
          {{ currentTitle }}
        </div>
        <div class="header-right">
          <el-tag size="small" :type="auth.isAdmin ? 'danger' : 'success'">
            {{ auth.isAdmin ? '管理员' : '调度人员' }}
          </el-tag>
          <span class="username">{{ auth.displayName }}</span>
          <el-button link type="primary" @click="logout">退出</el-button>
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
}

.aside {
  background: #101c33;
  display: flex;
  flex-direction: column;
}

.logo {
  display: block;
  text-decoration: none;
  flex-shrink: 0;
  height: 60px;
  line-height: 60px;
  text-align: center;
  color: #fff;
  font-size: 18px;
  letter-spacing: 3px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.menu {
  flex: 1;
  overflow-y: auto;
  padding: 8px 12px 16px;
}

.menu section { margin: 12px 0 16px; }
.menu h2 { margin: 0 0 4px 10px; font-size: 12px; font-weight: 500; color: #a1b3cf; }
.menu a, .menu summary {
  display: block;
  padding: 8px 10px;
  border-radius: 6px;
  color: #c7d4e9;
  text-decoration: none;
  line-height: 1.35;
}

.menu summary { display: list-item; cursor: pointer; }
.menu details a { margin-left: 12px; }
.menu a:hover, .menu summary:hover {
  background: rgba(255, 255, 255, 0.06);
  color: #fff;
}

.menu a.selected {
  background: #1f3b73;
  color: #fff;
}

.header {
  background: #fff;
  display: flex;
  align-items: center;
  justify-content: space-between;
  border-bottom: 1px solid #ebeef5;
}

.header-title {
  font-size: 16px;
  font-weight: 600;
  display: flex;
  align-items: baseline;
  gap: 8px;
}

.header-right {
  display: flex;
  align-items: center;
  gap: 12px;
}

.username {
  font-size: 14px;
  color: #606266;
}

.main {
  padding: 20px;
  scrollbar-gutter: stable;
}
</style>
