/** 判断一条已解析路由要不要记为「上次访问」。Kiosk 和大屏预览不记，避免把操作员再带进专用窗。 */
export function shouldRememberRoute(to: {
  path: string
  name?: string | symbol | null
  meta?: { public?: boolean }
  matched: { path: string }[]
}): boolean {
  if (to.meta?.public || to.name === 'change-password') return false
  if (to.matched.some(record => record.path === '/:pathMatch(.*)*')) return false
  if (to.path === '/' || to.path === '/login') return false
  if (to.path.startsWith('/kiosk')) return false
  if (to.path.startsWith('/config/layout-preview')) return false
  const inAppLayout = to.matched.some(record => record.path === '/')
  const isConsole = to.path === '/console' || to.path.startsWith('/console/')
  return inAppLayout || isConsole
}
