import { createRouter, createWebHashHistory } from 'vue-router';

const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: '/dispatch-pipeline', name: 'dispatch-pipeline', component: () => import('./pages/DispatchPipelinePage.vue') },
    { path: '/dispatch-optimization', name: 'dispatch-optimization', component: () => import('./pages/DispatchOptimizationPage.vue') },
    { path: '/charging', name: 'charging', component: () => import('./pages/ChargingPage.vue') },
    { path: '/resource-review', name: 'resource-review', component: () => import('./pages/ResourceReviewPage.vue') },
    { path: '/engineering-runtime', name: 'engineering-runtime', component: () => import('./pages/EngineeringRuntimePage.vue') },
    { path: '/engineering', name: 'engineering-center', component: () => import('./pages/EngineeringCenterPage.vue') },
    { path: '/engineering-tests', name: 'engineering-tests', component: () => import('./pages/TestCenterPage.vue') },
    { path: '/communication', name: 'communication', component: () => import('./pages/CommunicationPage.vue') },
    { path: '/recovery', name: 'recovery', component: () => import('./pages/RecoveryPage.vue') },
    { path: '/plc-cycle-test', name: 'plc-cycle-test', component: () => import('./pages/PlcCycleTestPage.vue') },
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
