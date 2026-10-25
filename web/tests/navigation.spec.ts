import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(r.ok()).toBeTruthy()
  const result = await r.json()
  return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

async function signIn(page: Page, auth: { token: string; user: unknown }) {
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
}

test('command palette: open via shortcut and header, search, navigate; sidebar filter; overview shortcuts', async ({ page, request }) => {
  const auth = await login(request)
  await signIn(page, auth)
  await page.goto('/overview')
  await page.evaluate(() => {
    localStorage.removeItem('scada_nav_recent_admin')
    localStorage.removeItem('scada_nav_fav_admin')
  })
  await page.reload()
  await expect(page.getByRole('menuitem', { name: '总览' })).toBeVisible()

  // 顶栏搜索按钮打开
  await page.locator('[data-test="nav-search"]').click()
  await expect(page.locator('[data-test="command-palette"]')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.locator('[data-test="command-palette"]')).toBeHidden()

  // Ctrl+K 打开并搜索跳转
  await page.keyboard.press('Control+KeyK')
  const palette = page.locator('[data-test="command-palette"]')
  await expect(palette).toBeVisible()
  const input = page.locator('[data-test="command-palette-input"]')
  await expect(input).toBeFocused()
  await input.fill('测点')
  await expect(page.locator('[data-test="command-palette-item-tags"]')).toBeVisible()
  await page.locator('[data-test="command-palette-item-tags"]').click()
  await expect(page).toHaveURL(/\/config\/tags/)
  await expect(palette).toBeHidden()

  // 侧栏筛选
  await page.locator('[data-test="nav-filter"]').fill('报警')
  await expect(page.locator('[data-test="nav-item-alarms"]')).toBeVisible()
  await expect(page.locator('[data-test="nav-item-alarm-analytics"]')).toBeVisible()
  await expect(page.locator('[data-test="nav-item-realtime"]')).toHaveCount(0)
  await page.locator('[data-test="nav-filter"]').fill('zzz-no-match')
  await expect(page.locator('[data-test="nav-filter-empty"]')).toBeVisible()
  await page.locator('[data-test="nav-filter"]').fill('')

  // 总览快捷入口 + 最近访问（客户端路由，保留 localStorage）
  await page.locator('[data-test="nav-item-overview"]').click()
  await expect(page).toHaveURL(/\/overview/)
  await expect(page.locator('[data-test="overview-quick"]')).toBeVisible()
  await expect(page.locator('[data-test="overview-quick-alarms"]')).toBeVisible()
  await expect(page.locator('[data-test="overview-recent-tags"]')).toBeVisible()

  // 收藏：在面板里星标，总览出现收藏
  await page.locator('[data-test="nav-search"]').click()
  await page.locator('[data-test="command-palette-input"]').fill('alarms')
  const alarmRow = page.locator('[data-test="command-palette-item-alarms"]')
  await expect(alarmRow).toBeVisible()
  await alarmRow.locator('[data-test="command-palette-star"]').click()
  await page.keyboard.press('Escape')
  await expect(page.locator('[data-test="overview-fav-alarms"]')).toBeVisible()

  // 总览打开面板
  await page.locator('[data-test="overview-open-palette"]').click()
  await expect(page.locator('[data-test="command-palette"]')).toBeVisible()
})
