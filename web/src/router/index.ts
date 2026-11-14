import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { canOperate } from '../utils'
import { allows, defaultLandingPath, type AccessHolder } from '../access/policies'
import { tr } from '../i18n'
import { ensureProtocolCatalog } from '../protocols'
import { readLastText, rememberText } from '../composables/lastChoice'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', name: 'login', component: () => import('../views/LoginView.vue'), meta: { public: true } },
    // 强制改密是登录后的第一站：不挂在 AppLayout 下，避免未授权的导航与轮询先跑起来。
    { path: '/change-password', name: 'change-password', component: () => import('../views/ChangePasswordView.vue'), meta: { title: tr('passwordChange.title') } },
    { path: '/console/:layoutId?', component: () => import('../views/ConsoleView.vue'), meta: { title: tr('workbench.runtimeTitle') } },
    { path: '/config/layout-preview', component: () => import('../views/LayoutPreviewView.vue'), meta: { title: tr('layoutPreview.runtimePreview'), policy: 'global-configure' } },
    { path: '/kiosk/screens/:id', component: () => import('../views/KioskScreenView.vue'), meta: { title: tr('common.kiosk') } },
    {
      path: '/',
      component: () => import('../layout/AppLayout.vue'),
      children: [
        { path: 'config/layouts', component: () => import('../views/LayoutsView.vue'), meta: { title: tr('workbench.configurationTitle'), policy: 'global-configure' } },
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
        { path: 'assets', name: 'assets', component: () => import('../views/AssetsView.vue'), meta: { title: tr('assets.title'), policy: 'any-view' } },
        { path: 'spc', name: 'spc', component: () => import('../views/SpcView.vue'), meta: { title: tr('spc.title'), policy: 'any-view' } },
        { path: 'quality', name: 'quality', component: () => import('../views/QualityView.vue'), meta: { title: tr('quality.title'), policy: 'any-view' } },
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
          path: 'config/channels',
          name: 'channels',
          component: () => import('../views/ChannelsView.vue'),
          meta: { title: tr('navMenu.channels'), policy: 'global-configure' }
        },
        {
          path: 'config/serial-profiles',
          name: 'serial-profiles',
          component: () => import('../views/SerialProfilesView.vue'),
          meta: { title: tr('serialProfiles.title'), policy: 'global-configure' }
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
        },
        // 未知地址给出可解释的落点，而不是静默把人送回总览
        { path: ':pathMatch(.*)*', component: () => import('../views/NotFoundView.vue'), meta: { title: tr('uxShell.notFoundTitle') } }
      ]
    }
  ]
})

// 每位登录用户在本机记住最后一次停留的应用内页面，下次从根地址或重新登录时直接回到这里。
// 只记路径不记查询串：筛选条件等临时参数的还原交给各页面自己的记忆逻辑。
const LAST_ROUTE_KEY = 'route'

function lastVisitedPath(user: AccessHolder): string | null {
  const saved = readLastText(LAST_ROUTE_KEY)
  if (!saved) return null
  const target = router.resolve(saved)
  // 地址已不存在（落到兜底路由）、是公开页/改密页或当前用户无权限时，按默认首页处理。
  if (!target.matched.length) return null
  if (target.matched.some(record => record.path === '/:pathMatch(.*)*')) return null
  if (target.meta.public || target.name === 'change-password') return null
  return allows(user, target.meta.policy) ? target.fullPath : null
}

/** 登录后或访问根地址时的落点：优先上次停留页面，否则回到角色默认首页。 */
export function startPath(user: AccessHolder): string {
  return lastVisitedPath(user) ?? defaultLandingPath(user)
}

// 浏览器标签标题跟随路由，便于多标签页识别与屏幕阅读器播报当前页面。
router.afterEach((to) => {
  const title = typeof to.meta.title === 'string' ? to.meta.title : ''
  document.title = title ? `${title} · SCADA` : 'SCADA'
  // 仅记忆主框架内的有效页面；登录页、改密页、全屏运行页与未知地址不参与。
  const inAppLayout = to.matched.some(record => record.path === '/')
  const notFound = to.matched.some(record => record.path === '/:pathMatch(.*)*')
  if (inAppLayout && !notFound && !to.meta.public) rememberText(LAST_ROUTE_KEY, to.path)
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
    // 令牌失效/网络失败同样要记住用户原本要去哪：登录后回到同一个地址，而不是默认首页。
    return { name: 'login', query: { redirect: to.fullPath } }
  }

  // 未改密前服务端只放行「查自己」与「改密」，前端先把人送到改密页，
  // 否则每进一个页面都会撞一次 428 提示。改完再回原来要去的地方。
  if (auth.mustChangePassword && to.name !== 'change-password') {
    return { name: 'change-password', query: { redirect: to.fullPath } }
  }
  if (!auth.mustChangePassword && to.name === 'change-password') {
    try { await auth.confirmSession() } catch { return true }
    if (!auth.mustChangePassword) return defaultLandingPath(auth.user)
  }

  if (!auth.mustChangePassword) {
    try { await ensureProtocolCatalog() } catch { /* 下一跳再试；内置协议表单会暂时缺少文本协议项 */ }
  }

  if (to.path === '/') return startPath(auth.user)

  // 路由与导航共用同一份策略表；路径前缀、页面特判不再参与判权。
  if (!allows(auth.user, to.meta.policy)) {
    const landing = defaultLandingPath(auth.user)
    // 落地页本身被拒绝时不再重定向，否则 denied 带上自己会形成无限重定向
    if (to.path === landing) return true
    return { path: landing, query: { denied: to.fullPath } }
  }

  return true
})

export { canOperate }
export default router
