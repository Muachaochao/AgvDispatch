import { createRouter, createWebHashHistory } from 'vue-router';

const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: '/simulation-center/:section?', name: 'simulation-center', component: () => import('./pages/SimulationCenterPage.vue') },
    { path: '/dispatch-pipeline', name: 'dispatch-pipeline', component: () => import('./pages/DispatchPipelinePage.vue') },
    { path: '/dispatch-optimization', name: 'dispatch-optimization', component: () => import('./pages/DispatchOptimizationPage.vue') },
    { path: '/charging', name: 'charging', component: () => import('./pages/ChargingPage.vue') },
    { path: '/resource-review', name: 'resource-review', component: () => import('./pages/ResourceReviewPage.vue') },
    { path: '/engineering-runtime', name: 'engineering-runtime', redirect: to => ({ path: '/engineering', query: { ...to.query, tab: 'runtime' } }) },
    { path: '/engineering', name: 'engineering-center', component: () => import('./pages/EngineeringCenterPage.vue') },
    { path: '/engineering-tests', name: 'engineering-tests', redirect: to => ({ path: '/engineering', query: { ...to.query, tab: 'tests' } }) },
    { path: '/communication', name: 'communication', redirect: to => ({ path: '/engineering', query: { ...to.query, tab: 'communication' } }) },
    { path: '/recovery', name: 'recovery', component: () => import('./pages/RecoveryPage.vue') },
    { path: '/navigation-test', name: 'navigation-test', component: () => import('./pages/NavigationTestPage.vue') },
    { path: '/', name: 'overview', component: () => import('./pages/OverviewPage.vue') },
    { path: '/vehicles', name: 'vehicles', component: () => import('./pages/VehicleListPage.vue') },
    { path: '/tasks', name: 'tasks', component: () => import('./pages/TaskListPage.vue') },
    { path: '/tasks/:id', name: 'task-detail', component: () => import('./pages/TaskListPage.vue') },
    { path: '/alarms', name: 'alarms', component: () => import('./pages/AlarmPage.vue') },
    { path: '/maps', name: 'maps', component: () => import('./pages/MapCatalogPage.vue') },
    {
      path: '/planning',
      name: 'planning',
      component: () => import('./PlanningWorkbench.vue'),
      props: route => ({
        source: route.query.source === 'demo' ? 'demo' : undefined,
        initialTab: typeof route.query.tab === 'string' ? route.query.tab : undefined
      })
    },
    { path: '/engineering-settings', name: 'engineering-settings', redirect: to => ({ path: '/engineering', query: { ...to.query, tab: 'settings' } }) },
    { path: '/commissioning', name: 'commissioning', redirect: to => ({ path: '/engineering', query: { ...to.query, tab: 'commissioning' } }) },
  ]
});

export default router;
