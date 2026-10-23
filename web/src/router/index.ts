import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { canOperate, isAdmin } from '../utils'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', name: 'login', component: () => import('../views/LoginView.vue'), meta: { public: true } },
    {
      path: '/',
      component: () => import('../layout/AppLayout.vue'),
      children: [
        { path: '', redirect: '/overview' },
        { path: 'overview', name: 'overview', component: () => import('../views/OverviewView.vue'), meta: { title: '总览' } },
        {
          path: 'realtime',
          name: 'realtime',
          component: () => import('../views/RealtimeView.vue'),
          meta: { title: '实时监视' }
        },
        { path: 'trends', name: 'trends', component: () => import('../views/TrendsView.vue'), meta: { title: '趋势曲线' } },
        { path: 'alarms', name: 'alarms', component: () => import('../views/AlarmsView.vue'), meta: { title: '报警' } },
        { path: 'reports', name: 'reports', component: () => import('../views/ReportsView.vue'), meta: { title: '报表' } },
        {
          path: 'config/devices',
          name: 'devices',
          component: () => import('../views/DevicesView.vue'),
          meta: { title: '设备配置', admin: true }
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

  if (to.meta.admin && !isAdmin(auth.user?.role)) {
    return { name: 'overview' }
  }

  if (to.name === 'login') {
    return { name: 'overview' }
  }

  return true
})

export { canOperate }
export default router
