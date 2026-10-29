import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { canOperate } from '../utils'
import { allows, defaultLandingPath } from '../access/policies'
import { tr } from '../i18n'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', name: 'login', component: () => import('../views/LoginView.vue'), meta: { public: true } },
    { path: '/console/:layoutId?', component: () => import('../views/ConsoleView.vue'), meta: { title: tr('index.k0dd591') } },
    { path: '/config/layout-preview', component: () => import('../views/LayoutPreviewView.vue'), meta: { title: tr('layoutPreview.runtimePreview'), policy: 'global-configure' } },
    { path: '/kiosk/screens/:id', component: () => import('../views/KioskScreenView.vue'), meta: { title: tr('common.kiosk') } },
    {
      path: '/',
      component: () => import('../layout/AppLayout.vue'),
      children: [
        { path: 'config/layouts', component: () => import('../views/LayoutsView.vue'), meta: { title: tr('index.kbdee6f'), policy: 'global-configure' } },
        { path: 'config/hmi-library', component: () => import('../views/HmiLibraryView.vue'), meta: { title: tr('index.k5e9dd6'), policy: 'global-configure' } },
        { path: 'config/object-templates', component: () => import('../views/ObjectTemplatesView.vue'), meta: { title: tr('index.k5c3e7d'), policy: 'global-configure' } },
        { path: 'config/gateways', component: () => import('../views/GatewaysView.vue'), meta: { title: tr('index.k626545'), policy: 'global-configure' } },
        { path: 'calc-jobs', component: () => import('../views/CalcJobsView.vue'), meta: { title: tr('index.k8efdcf') } },
        { path: 'report-center', component: () => import('../views/ReportCenterView.vue'), meta: { title: tr('ux.reports.business') } },
        { path: 'config/calculations', component: () => import('../views/CalculationsView.vue'), meta: { title: tr('index.k4e0387'), policy: 'any-configure' } },
        { path: 'config/oee', component: () => import('../views/OeeConfigView.vue'), meta: { title: tr('navMenu.oeeConfig'), policy: 'any-configure' } },
        { path: 'config/object-templates/:id', component: () => import('../views/ObjectTemplateEditorView.vue'), meta: { title: tr('index.ka1ab09'), policy: 'global-configure' } },
        { path: 'screens', component: () => import('../views/ScreensView.vue'), meta: { title: tr('index.k8a1840') } },
        { path: 'screens/:id', component: () => import('../views/ScreenRunView.vue'), meta: { title: tr('common.screenRun') } },
        { path: 'config/screens/:id', component: () => import('../views/ScreenDesignerView.vue'), meta: { title: tr('index.k684331'), policy: 'global-configure' } },
        { path: 'license', name: 'license', component: () => import('../views/LicenseView.vue'), meta: { title: tr('license.title') } },
        { path: 'overview', name: 'overview', component: () => import('../views/OverviewView.vue'), meta: { title: tr('index.kc122e1') } },
        {
          path: 'realtime',
          name: 'realtime',
          component: () => import('../views/RealtimeView.vue'),
          meta: { title: tr('index.k445294') }
        },
        { path: 'recipes', name: 'recipes', component: () => import('../views/RecipesView.vue'), meta: { title: tr('index.k4b435b'), policy: 'any-view' } },
        { path: 'traceability', name: 'traceability', component: () => import('../views/TraceabilityView.vue'), meta: { title: tr('traceability.title'), policy: 'any-view' } },
        { path: 'control', name: 'control', component: () => import('../views/ControlView.vue'), meta: { title: tr('index.k4eb87f'), policy: 'any-control' } },
        { path: 'trends', name: 'trends', component: () => import('../views/TrendsView.vue'), meta: { title: tr('index.k402be4') } },
        { path: 'alarms', name: 'alarms', component: () => import('../views/AlarmsView.vue'), meta: { title: tr('index.k9f45b3') } },
        { path: 'alarm-analytics', name: 'alarm-analytics', component: () => import('../views/AlarmAnalyticsView.vue'), meta: { title: tr('index.kfce701') } },
        { path: 'oee', name: 'oee', component: () => import('../views/OeeView.vue'), meta: { title: tr('navMenu.oee') } },
        { path: 'production/events', name: 'production-events', component: () => import('../views/OeeConfigView.vue'), props: { operationsOnly: true }, meta: { title: tr('ux.events.title'), policy: 'any-control' } },
        { path: 'config/notifications', name: 'notifications', component: () => import('../views/NotificationsView.vue'), meta: { title: tr('index.k0ca38c'), policy: 'any-configure' } },
        { path: 'config/annunciators', name: 'annunciators', component: () => import('../views/AnnunciatorView.vue'), meta: { title: tr('navMenu.annunciator'), policy: 'any-configure' } },
        { path: 'config/external-api', name: 'external-api', component: () => import('../views/ExternalApiView.vue'), meta: { title: tr('navMenu.externalApi'), policy: 'global-system' } },
        { path: 'reports', name: 'reports', component: () => import('../views/ReportsView.vue'), meta: { title: tr('ux.reports.statistics') } },
        {
          path: 'config/devices',
          name: 'devices',
          component: () => import('../views/DevicesView.vue'),
          meta: { title: tr('index.k845e2b'), policy: 'any-configure' }
        },
        {
          path: 'config/project',
          name: 'project',
          component: () => import('../views/ProjectView.vue'),
          meta: { title: tr('index.k4d7ee6'), policy: 'any-configure' }
        },
        {
          path: 'config/tags',
          name: 'tags',
          component: () => import('../views/TagsView.vue'),
          meta: { title: tr('index.kb5c4a3'), policy: 'any-configure' }
        },
        {
          path: 'config/rules',
          name: 'rules',
          component: () => import('../views/AlarmRulesView.vue'),
          meta: { title: tr('index.k1c7490'), policy: 'any-configure' }
        },
        { path: 'ops', name: 'ops', component: () => import('../views/OpsView.vue'), meta: { title: tr('index.k74fa83'), policy: 'global-system' } },
        {
          path: 'system',
          name: 'system',
          component: () => import('../views/SystemView.vue'),
          meta: { title: tr('index.k04ca1c'), policy: 'system-admin' }
        }
      ]
    },
    { path: '/:pathMatch(.*)*', redirect: '/' }
  ]
})

// 浏览器标签标题跟随路由，便于多标签页识别与屏幕阅读器播报当前页面。
router.afterEach((to) => {
  const title = typeof to.meta.title === 'string' ? to.meta.title : ''
  document.title = title ? `${title} · SCADA` : 'SCADA'
})

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  if (to.meta.public) {
    return true
  }

  if (!auth.token) {
    return { name: 'login', query: { redirect: to.fullPath } }
  }

  try {
    await auth.ensureUser()
  } catch {
    return { name: 'login' }
  }

  if (to.path === '/') return defaultLandingPath(auth.user)

  // 路由与导航共用同一份策略表；路径前缀、页面特判不再参与判权。
  if (!allows(auth.user, to.meta.policy)) {
    return defaultLandingPath(auth.user)
  }

  return true
})

export { canOperate }
export default router
