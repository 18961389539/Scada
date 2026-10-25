import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { canOperate, hasAny, hasGlobal } from '../utils'
import { tr } from '../i18n'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', name: 'login', component: () => import('../views/LoginView.vue'), meta: { public: true } },
    { path: '/console/:layoutId?', component: () => import('../views/ConsoleView.vue'), meta: { title: tr('index.k0dd591') } },
    { path: '/kiosk/screens/:id', component: () => import('../views/KioskScreenView.vue'), meta: { title: tr('index.k8a1840') } },
    {
      path: '/',
      component: () => import('../layout/AppLayout.vue'),
      children: [
        { path: 'config/layouts', component: () => import('../views/LayoutsView.vue'), meta: { title: tr('index.kbdee6f'), admin: true } },
        { path: 'config/hmi-library', component: () => import('../views/HmiLibraryView.vue'), meta: { title: tr('index.k5e9dd6'), admin: true } },
        { path: 'config/object-templates', component: () => import('../views/ObjectTemplatesView.vue'), meta: { title: tr('index.k5c3e7d'), admin: true } },
        { path: 'config/gateways', component: () => import('../views/GatewaysView.vue'), meta: { title: tr('index.k626545'), admin: true } },
        { path: 'calc-jobs', component: () => import('../views/CalcJobsView.vue'), meta: { title: tr('index.k8efdcf') } },
        { path: 'report-center', component: () => import('../views/ReportCenterView.vue'), meta: { title: tr('index.kb41505') } },
        { path: 'config/calculations', component: () => import('../views/CalculationsView.vue'), meta: { title: tr('index.k4e0387'), admin: true } },
        { path: 'config/object-templates/:id', component: () => import('../views/ObjectTemplateEditorView.vue'), meta: { title: tr('index.ka1ab09'), admin: true } },
        { path: 'screens', component: () => import('../views/ScreensView.vue'), meta: { title: tr('index.k8a1840') } },
        { path: 'screens/:id', component: () => import('../views/ScreenRunView.vue'), meta: { title: tr('index.k8a1840') } },
        { path: 'config/screens/:id', component: () => import('../views/ScreenDesignerView.vue'), meta: { title: tr('index.k684331'), admin: true } },
        { path: '', redirect: '/overview' },
        { path: 'overview', name: 'overview', component: () => import('../views/OverviewView.vue'), meta: { title: tr('index.kc122e1') } },
        {
          path: 'realtime',
          name: 'realtime',
          component: () => import('../views/RealtimeView.vue'),
          meta: { title: tr('index.k445294') }
        },
        { path: 'recipes', name: 'recipes', component: () => import('../views/RecipesView.vue'), meta: { title: tr('index.k4b435b') } },
        { path: 'control', name: 'control', component: () => import('../views/ControlView.vue'), meta: { title: tr('index.k4eb87f') } },
        { path: 'trends', name: 'trends', component: () => import('../views/TrendsView.vue'), meta: { title: tr('index.k402be4') } },
        { path: 'alarms', name: 'alarms', component: () => import('../views/AlarmsView.vue'), meta: { title: tr('index.k9f45b3') } },
        { path: 'alarm-analytics', name: 'alarm-analytics', component: () => import('../views/AlarmAnalyticsView.vue'), meta: { title: tr('index.kfce701') } },
        { path: 'oee', name: 'oee', component: () => import('../views/OeeView.vue'), meta: { title: tr('navMenu.oee') } },
        { path: 'config/notifications', name: 'notifications', component: () => import('../views/NotificationsView.vue'), meta: { title: tr('index.k0ca38c'), admin: true } },
        { path: 'config/annunciators', name: 'annunciators', component: () => import('../views/AnnunciatorView.vue'), meta: { title: tr('navMenu.annunciator') } },
        { path: 'reports', name: 'reports', component: () => import('../views/ReportsView.vue'), meta: { title: tr('index.kca53e6') } },
        {
          path: 'config/devices',
          name: 'devices',
          component: () => import('../views/DevicesView.vue'),
          meta: { title: tr('index.k845e2b'), admin: true }
        },
        {
          path: 'config/project',
          name: 'project',
          component: () => import('../views/ProjectView.vue'),
          meta: { title: tr('index.k4d7ee6'), admin: true }
        },
        {
          path: 'config/tags',
          name: 'tags',
          component: () => import('../views/TagsView.vue'),
          meta: { title: tr('index.kb5c4a3'), admin: true }
        },
        {
          path: 'config/rules',
          name: 'rules',
          component: () => import('../views/AlarmRulesView.vue'),
          meta: { title: tr('index.k1c7490'), admin: true }
        },
        { path: 'ops', name: 'ops', component: () => import('../views/OpsView.vue'), meta: { title: tr('index.k74fa83'), admin: true } },
        {
          path: 'system',
          name: 'system',
          component: () => import('../views/SystemView.vue'),
          meta: { title: tr('index.k04ca1c'), admin: true }
        }
      ]
    },
    { path: '/:pathMatch(.*)*', redirect: '/overview' }
  ]
})

// 不区分区域的工程对象（网关、模板、画面组态）需要全局配置权限。
const globalOnly = ['/config/gateways', '/config/layouts', '/config/hmi-library', '/config/object-templates', '/config/screens']

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

  // 配置页：至少在一个区域有配置权；系统管理：全局用户管理/系统维护/配置任一即可（页内再按权限显示标签）。
  if (to.meta.admin) {
    const allowed = to.path === '/system'
      ? hasGlobal(auth.user, 'ManageUsers') || hasGlobal(auth.user, 'System') || hasGlobal(auth.user, 'Configure')
      : globalOnly.some(p => to.path.startsWith(p)) ? hasGlobal(auth.user, 'Configure') : hasAny(auth.user, 'Configure')
    if (!allowed) return { name: 'overview' }
  }

  if (to.name === 'login') {
    return { name: 'overview' }
  }

  return true
})

export { canOperate }
export default router
