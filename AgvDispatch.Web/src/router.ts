import { createRouter, createWebHashHistory } from 'vue-router';

const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: '/navigation-test', name: 'navigation-test', component: () => import('./pages/NavigationTestPage.vue') },
    { path: '/', name: 'overview', component: () => import('./pages/OverviewPage.vue') },
    { path: '/vehicles', name: 'vehicles', component: () => import('./pages/VehicleListPage.vue') },
    { path: '/tasks', name: 'tasks', component: () => import('./pages/TaskListPage.vue') },
    { path: '/tasks/:id', name: 'task-detail', component: () => import('./pages/TaskListPage.vue') },
    { path: '/alarms', name: 'alarms', component: () => import('./pages/AlarmPage.vue') },
    { path: '/maps', name: 'maps', component: () => import('./pages/MapCatalogPage.vue') },
    { path: '/planning', name: 'planning', component: () => import('./PlanningWorkbench.vue'), props: true },
    { path: '/engineering-settings', name: 'engineering-settings', component: () => import('./pages/EngineeringSettingsPage.vue') },
    { path: '/commissioning', name: 'commissioning', component: () => import('./CommissioningPanel.vue'), props: true },
  ]
});

export default router;
