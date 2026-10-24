import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { canOperate, hasAny, hasGlobal } from '../utils'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', name: 'login', component: () => import('../views/LoginView.vue'), meta: { public: true } },
    {
      path: '/',
      component: () => import('../layout/AppLayout.vue'),
      children: [
        { path: 'config/object-templates', component: () => import('../views/ObjectTemplatesView.vue'), meta: { title: '设备对象与模板', admin: true } },
        { path: 'config/gateways', component: () => import('../views/GatewaysView.vue'), meta: { title: '采集网关', admin: true } },
        { path: 'calc-jobs', component: () => import('../views/CalcJobsView.vue'), meta: { title: '计算任务' } },
        { path: 'report-center', component: () => import('../views/ReportCenterView.vue'), meta: { title: '报表中心' } },
        { path: 'config/calculations', component: () => import('../views/CalculationsView.vue'), meta: { title: '计算测点', admin: true } },
        { path: 'config/object-templates/:id', component: () => import('../views/ObjectTemplateEditorView.vue'), meta: { title: '设备模板编辑', admin: true } },
        { path: 'screens', component: () => import('../views/ScreensView.vue'), meta: { title: '工艺画面' } },
        { path: 'screens/:id', component: () => import('../views/ScreenRunView.vue'), meta: { title: '工艺画面' } },
        { path: 'config/screens/:id', component: () => import('../views/ScreenDesignerView.vue'), meta: { title: '画面组态', admin: true } },
        { path: '', redirect: '/overview' },
        { path: 'overview', name: 'overview', component: () => import('../views/OverviewView.vue'), meta: { title: '总览' } },
        {
          path: 'realtime',
          name: 'realtime',
          component: () => import('../views/RealtimeView.vue'),
          meta: { title: '实时监视' }
        },
        { path: 'recipes', name: 'recipes', component: () => import('../views/RecipesView.vue'), meta: { title: '配方管理' } },
        { path: 'control', name: 'control', component: () => import('../views/ControlView.vue'), meta: { title: '现场操作' } },
        { path: 'trends', name: 'trends', component: () => import('../views/TrendsView.vue'), meta: { title: '趋势曲线' } },
        { path: 'alarms', name: 'alarms', component: () => import('../views/AlarmsView.vue'), meta: { title: '报警' } },
        { path: 'alarm-analytics', name: 'alarm-analytics', component: () => import('../views/AlarmAnalyticsView.vue'), meta: { title: '报警分析' } },
        { path: 'config/notifications', name: 'notifications', component: () => import('../views/NotificationsView.vue'), meta: { title: '报警通知', admin: true } },
        { path: 'reports', name: 'reports', component: () => import('../views/ReportsView.vue'), meta: { title: '报表' } },
        {
          path: 'config/devices',
          name: 'devices',
          component: () => import('../views/DevicesView.vue'),
          meta: { title: '设备配置', admin: true }
        },
        {
          path: 'config/project',
          name: 'project',
          component: () => import('../views/ProjectView.vue'),
          meta: { title: '工程管理', admin: true }
        },
        {
          path: 'config/tags',
          name: 'tags',
          component: () => import('../views/TagsView.vue'),
          meta: { title: '测点配置', admin: true }
        },
        {
          path: 'config/rules',
          name: 'rules',
          component: () => import('../views/AlarmRulesView.vue'),
          meta: { title: '报警规则', admin: true }
        },
        { path: 'ops', name: 'ops', component: () => import('../views/OpsView.vue'), meta: { title: '运维与高可用', admin: true } },
        {
          path: 'system',
          name: 'system',
          component: () => import('../views/SystemView.vue'),
          meta: { title: '系统管理', admin: true }
        }
      ]
    },
    { path: '/:pathMatch(.*)*', redirect: '/overview' }
  ]
})

// 不区分区域的工程对象（网关、模板、画面组态）需要全局配置权限。
const globalOnly = ['/config/gateways', '/config/object-templates', '/config/screens']

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
