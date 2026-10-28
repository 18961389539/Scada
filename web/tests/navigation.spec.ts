import { test, expect, type APIRequestContext, type Page } from '@playwright/test'
import { openNavigationGroup, visibleNavigationIds } from './nav-catalog'

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

test('page loading: ordinary routes defer chart and editor dependencies', async ({ page, request }) => {
  const auth = await login(request)
  await signIn(page, auth)
  const scripts: string[] = []
  const errors: string[] = []
  page.on('request', request => {
    if (request.resourceType() === 'script') scripts.push(new URL(request.url()).pathname)
  })
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('/overview')
  await expect(page.locator('[data-test="overview-quick"]')).toBeVisible()
  const scriptBytes = await page.evaluate(() => performance.getEntriesByType('resource')
    .filter(entry => entry.name.endsWith('.js'))
    .reduce((total, entry) => total + (entry as PerformanceResourceTiming).decodedBodySize, 0))
  expect(scriptBytes).toBeGreaterThan(0)
  expect(scriptBytes).toBeLessThan(1650000)
  await page.locator('[data-test="nav-item-realtime"]').click()
  await expect(page.locator('[data-test="realtime-search"]')).toBeVisible()
  expect(scripts.filter(path => /vendor-(echarts|monaco)|ScreenDesignerView-|ControlView-/.test(path))).toEqual([])
  await page.locator('[data-test="nav-item-trends"]').click()
  await expect(page).toHaveURL('/trends')
  await expect(page.locator('[data-test="tag-picker"]')).toBeVisible()
  expect(scripts.some(path => path.includes('vendor-echarts'))).toBe(true)
  expect(scripts.some(path => /vendor-monaco|ScreenDesignerView-|ControlView-/.test(path))).toBe(false)
  expect(errors).toEqual([])
})

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
  await openNavigationGroup(page, 'monitoring')
  await page.locator('[data-test="nav-item-overview"]').click()
  await expect(page).toHaveURL(/\/overview/)
  await expect(page.locator('[data-test="overview-quick"]')).toBeVisible()
  await page.locator('[data-test="overview-quick"] > summary').click()
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

test('command palette keeps all pages discoverable, scrolls with keyboard selection, and ignores IME confirmation', async ({ page, request }) => {
  const auth = await login(request)
  await signIn(page, auth)
  await page.goto('/screens')
  const expectedIds = await visibleNavigationIds(page)
  await page.locator('[data-test="nav-search"]').click()
  const palette = page.locator('[data-test="command-palette"]')
  const input = palette.locator('[data-test="command-palette-input"]')
  const items = palette.locator('[data-test^="command-palette-item-"]')
  await expect(palette).toBeVisible()
  await expect(input).toBeFocused()
  // A recent visit must not hide the remaining catalog, and each page appears once.
  await expect(items).toHaveCount(expectedIds.length)
  await expect.poll(() => items.evaluateAll(rows => rows.map(row => row.getAttribute('data-test')).sort())).toEqual(expectedIds)
  await expect(palette.getByText('其他页面', { exact: true })).toBeVisible()
  await expect(palette.locator('[data-test="command-palette-count"]')).toHaveText(`${expectedIds.length} 个页面`)

  await input.press('ArrowUp')
  const last = items.last()
  await expect(last).toHaveClass(/active/)
  await expect.poll(() => palette.locator('[data-test="command-palette-results"]').evaluate(el => el.scrollTop)).toBeGreaterThan(0)
  const bounds = await palette.locator('[data-test="command-palette-results"]').boundingBox()
  const row = await last.boundingBox()
  expect(row!.y + row!.height).toBeLessThanOrEqual(bounds!.y + bounds!.height + 1)
  await input.press('ArrowDown')
  await expect(items.first()).toHaveClass(/active/)
  await expect.poll(() => palette.locator('[data-test="command-palette-results"]').evaluate(el => el.scrollTop)).toBe(0)

  await input.fill('测点配置')
  await input.dispatchEvent('compositionstart')
  await input.dispatchEvent('keydown', { key: 'Enter', isComposing: true })
  await expect(palette).toBeVisible()
  await expect(page).toHaveURL('/screens')
  await input.dispatchEvent('compositionend')
  await input.press('Enter')
  await expect(page).toHaveURL('/config/tags')
})

test('command palette favorites work by keyboard without navigating and empty search can be cleared', async ({ page, request }) => {
  const auth = await login(request)
  await signIn(page, auth)
  await page.goto('/screens')
  const expectedIds = await visibleNavigationIds(page)
  await page.locator('[data-test="nav-search"]').click()
  const palette = page.locator('[data-test="command-palette"]')
  const input = palette.locator('[data-test="command-palette-input"]')
  await expect(input).toBeFocused()
  await input.fill('测点')
  const star = palette.locator('[data-test="command-palette-item-tags"] [data-test="command-palette-star"]')
  await star.focus()
  await star.press('Enter')
  await expect(star).toHaveAttribute('aria-pressed', 'true')
  await expect(page).toHaveURL('/screens')
  await expect(palette).toBeVisible()
  await star.press('Enter')
  await expect(star).toHaveAttribute('aria-pressed', 'false')

  await input.fill('no-such-page-123456')
  const empty = palette.locator('[data-test="command-palette-empty"]')
  await expect(empty).toBeVisible()
  await empty.getByRole('button', { name: '清空搜索', exact: true }).click()
  await expect(input).toHaveValue('')
  await expect(input).toBeFocused()
  await expect.poll(() => palette.locator('[data-test^="command-palette-item-"]').evaluateAll(rows => rows.map(row => row.getAttribute('data-test')).sort())).toEqual(expectedIds)

  // Moving a page into the Favorites section must retain keyboard focus.
  await star.focus()
  await star.press('Enter')
  await expect(star).toHaveAttribute('aria-pressed', 'true')
  await expect(star).toBeFocused()
  await star.press('Enter')
  await expect(star).toHaveAttribute('aria-pressed', 'false')
  await expect(star).toBeFocused()
})
test('sidebar: favorites and recents are pinned, groups keep open state across reloads, filter supports keyboard', async ({ page, request }) => {
  const auth = await login(request)
  await signIn(page, auth)
  await page.goto('/overview')
  await page.evaluate(() => {
    localStorage.removeItem('scada_nav_recent_admin')
    localStorage.removeItem('scada_nav_fav_admin')
    localStorage.removeItem('scada_nav_open_admin')
  })
  await page.reload()

  // 命令面板里收藏后，侧栏固定区立即出现（共用同一份 localStorage 数据）
  await page.locator('[data-test="nav-search"]').click()
  await page.locator('[data-test="command-palette-input"]').fill('alarms')
  await page.locator('[data-test="command-palette-item-alarms"] [data-test="command-palette-star"]').click()
  await page.keyboard.press('Escape')
  const fav = page.locator('[data-test="nav-fav-alarms"]')
  await expect(fav).toBeVisible()
  await fav.click()
  await expect(page).toHaveURL(/\/alarms$/)
  await expect(page.locator('[data-test="nav-fav-alarms"]')).toHaveClass(/active/)

  // 手动展开的分组不能被路由切换或刷新覆盖
  await page.locator('[data-test="nav-config-group"] .el-sub-menu__title').click()
  await expect(page.locator('[data-test="nav-config-group"]')).toHaveClass(/is-opened/)
  await page.reload()
  await expect(page.locator('[data-test="nav-config-group"]')).toHaveClass(/is-opened/)
  await expect(page.locator('[data-test="nav-item-gateways"]')).toBeVisible()

  // 过滤结果可用 ↑↓ 选择、Enter 打开
  await page.locator('[data-test="nav-item-overview"]').click()
  await expect(page).toHaveURL(/\/overview$/)
  const filter = page.locator('[data-test="nav-filter"]')
  await filter.fill('报警')
  await filter.press('ArrowDown')
  await expect(page.locator('[data-test="nav-item-alarms"]')).toHaveClass(/is-kbd/)
  await filter.press('Enter')
  await expect(page).toHaveURL(/\/alarms$/)
})

test('sidebar collapses to an icon rail on medium screens and expands again', async ({ page, request }) => {
  const auth = await login(request)
  await signIn(page, auth)
  await page.setViewportSize({ width: 1000, height: 800 })
  await page.goto('/overview')
  const aside = page.locator('.aside').first()
  await expect(aside).toHaveCSS('width', '64px')
  await expect(page.locator('[data-test="nav-filter"]')).toHaveCount(0)
  await expect(page.locator('[data-test="nav-monitoring-group"]')).toBeVisible()

  await page.setViewportSize({ width: 1280, height: 800 })
  await expect(aside).toHaveCSS('width', '220px')
  await expect(page.locator('[data-test="nav-filter"]')).toBeVisible()
})
