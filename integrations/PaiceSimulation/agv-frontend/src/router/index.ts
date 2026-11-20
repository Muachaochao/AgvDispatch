import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/login',
      name: 'login',
      component: () => import('@/views/LoginView.vue'),
      meta: { public: true }
    },
    {
      path: '/',
      component: () => import('@/views/LayoutView.vue'),
      redirect: '/dashboard',
      children: [
        {
          path: 'dashboard',
          name: 'dashboard',
          component: () => import('@/views/SimulationHubView.vue'),
          meta: { title: '运行总览' }
        },
        {
          path: 'roadnet',
          name: 'roadnet',
          component: () => import('@/views/RoadNetView.vue'),
          meta: { title: '车间地图', subtitle: '路网编辑' }
        },
        {
          path: 'tasks',
          name: 'tasks',
          component: () => import('@/views/TaskSetView.vue'),
          meta: { title: '任务集' }
        },
        {
          path: 'simulation',
          name: 'simulation',
          component: () => import('@/views/SimulationHubView.vue'),
          meta: { title: '仿真中心' }
        },
        {
          path: 'experiments',
          redirect: { path: '/simulation', query: { mode: 'compare' } }
        },
        {
          path: 'dispatch',
          redirect: { path: '/simulation', query: { mode: 'main' } }
        },
        {
          path: 'admin/users',
          name: 'admin-users',
          component: () => import('@/views/admin/UserManageView.vue'),
          meta: { title: '用户管理', roles: ['admin'] }
        },
        {
          path: 'admin/dict',
          name: 'admin-dict',
          component: () => import('@/views/admin/DictManageView.vue'),
          meta: { title: '数字字典', roles: ['admin'] }
        }
      ]
    },
    { path: '/:pathMatch(.*)*', redirect: '/' }
  ]
})

router.beforeEach((to) => {
  const auth = useAuthStore()
  if (!to.meta.public && !auth.token) {
    return { name: 'login' }
  }
  if (to.meta.public && auth.token && to.name === 'login') {
    return { name: 'dashboard' }
  }
  const roles = to.meta.roles as string[] | undefined
  if (roles && !roles.includes(auth.role)) {
    return { name: 'dashboard' }
  }
})

export default router
