import { test, expect, type APIRequestContext, type Page } from './fixtures'
import { openNavigationGroup, visibleNavigationIds } from './nav-catalog'
import { findNavByPath } from '../src/nav/catalog'
import { displayOrder, moveRelative, reorderVisible } from '../src/nav/order'

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

async function seedNavigation(page: Page, userName: string, favorites: string[]): Promise<void> {
  await page.addInitScript(({ userName, favorites }) => {
    localStorage.setItem(`scada_nav_fav_${userName}`, JSON.stringify(favorites))
  }, { userName, favorites })
}

test('navigation catalog resolves child pages, query strings, and path boundaries', () => {
  for (const [path, id] of [
    ['/alarms?state=unack#list', 'alarms'],
    ['/screens/42/', 'screens'],
    ['/config/screens/42?tab=bindings', 'screens'],
    ['/config/object-templates/42#parameters', 'object-templates'],
    ['/console/3', 'console'],
    ['/production/events/', 'production-events'],
    ['/config/devices#scope', 'devices']
  ]) expect(findNavByPath(path)?.id, path).toBe(id)
  expect(findNavByPath('/screens-extra/42')).toBeUndefined()
  expect(findNavByPath('/config/screens-extra/42')).toBeUndefined()
  expect(findNavByPath('/config/devices-extra')).toBeUndefined()
})

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

test('page loading: cold-cache login stays usable on a throttled connection', async ({ page }, testInfo) => {
  test.setTimeout(45000)
  const cdp = await page.context().newCDPSession(page)
  await cdp.send('Network.enable')
  await cdp.send('Network.setCacheDisabled', { cacheDisabled: true })
  await cdp.send('Network.emulateNetworkConditions', {
    offline: false, latency: 150, downloadThroughput: 200000, uploadThroughput: 62500
  })
  const scripts: string[] = []
  page.on('request', request => {
    if (request.resourceType() === 'script') scripts.push(new URL(request.url()).pathname)
  })
  const started = Date.now()
  await page.goto('/login', { waitUntil: 'domcontentloaded' })
  await expect(page.getByPlaceholder(/用户名/)).toBeEditable({ timeout: 20000 })
  const elapsedMs = Date.now() - started
  await testInfo.attach('cold-login.json', {
    body: JSON.stringify({ elapsedMs, latencyMs: 150, downloadBytesPerSecond: 200000, scripts }),
    contentType: 'application/json'
  })
  expect(elapsedMs).toBeLessThan(20000)
  expect(scripts.filter(path => /vendor-(echarts|monaco)/.test(path))).toEqual([])
  await cdp.detach()
})

test('command palette: open via shortcut and header, search, navigate; sidebar filter; overview shortcuts', async ({ page, request }) => {
  const auth = await login(request)
  await signIn(page, auth)
  await page.goto('/overview')
  await page.evaluate(() => {
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

  // 总览快捷入口（客户端路由，保留 localStorage）
  await openNavigationGroup(page, 'monitoring')
  await page.locator('[data-test="nav-item-overview"]').click()
  await expect(page).toHaveURL(/\/overview/)
  await expect(page.locator('[data-test="overview-quick"]')).toBeVisible()
  await page.locator('[data-test="overview-quick"] > summary').click()
  await expect(page.locator('[data-test="overview-quick-alarms"]')).toBeVisible()

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
  // Empty favorites must not hide the catalog, and each page appears once.
  await expect(items).toHaveCount(expectedIds.length)
  await expect.poll(() => items.evaluateAll(rows => rows.map(row => row.getAttribute('data-test')).sort())).toEqual(expectedIds)
  // 刚打开的工艺画面会记入最近访问，其余页面归到「其他页面」
  await expect(palette.getByRole('region', { name: '最近访问' })).toContainText('工艺画面')
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
test('sidebar: favorites stay accessible, groups keep open state, filter supports keyboard and IME', async ({ page, request }) => {
  const auth = await login(request)
  await signIn(page, auth)
  await page.goto('/overview')
  await page.evaluate(() => {
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
  await filter.dispatchEvent('compositionstart')
  await filter.dispatchEvent('keydown', { key: 'Enter', isComposing: true })
  await expect(page).toHaveURL(/\/overview$/)
  await filter.dispatchEvent('compositionend')
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

test('sidebar keeps the menu visible without a recent pages section', async ({ page, request }) => {
  await page.setViewportSize({ width: 1366, height: 768 })
  await signIn(page, await login(request))
  await seedNavigation(page, 'admin', [])
  await page.goto('/overview')
  await expect(page.getByRole('navigation', { name: '主导航' })).toBeVisible()
  await expect(page.locator('[data-test="nav-pins"]')).toHaveCount(0)
  await expect(page.locator('[data-test="nav-recent-toggle"]')).toHaveCount(0)
  await expect(page.locator('[data-test="nav-system-group"] > .el-sub-menu__title')).toBeInViewport({ ratio: 1 })
})

test('sidebar favorites use a compact two-column layout without hiding the menu', async ({ page, request }) => {
  await page.setViewportSize({ width: 1366, height: 768 })
  await signIn(page, await login(request))
  await seedNavigation(page, 'admin',
    ['/realtime', '/trends', '/alarms', '/config/devices', '/config/tags', '/report-center'])
  await page.goto('/overview')
  const favorites = page.locator('[data-test^="nav-fav-"]')
  await expect(favorites).toHaveCount(6)
  const boxes = await favorites.evaluateAll(items => items.map(item => {
    const { x, y } = item.getBoundingClientRect()
    return { x, y }
  }))
  expect(boxes[1].x).toBeGreaterThan(boxes[0].x)
  expect(boxes[1].y).toBe(boxes[0].y)
  expect(boxes[2].y).toBeGreaterThan(boxes[0].y)
  expect((await page.locator('[data-test="nav-pins"]').boundingBox())!.height).toBeLessThanOrEqual(180)
  expect((await page.locator('.nav-menu').boundingBox())!.height).toBeGreaterThan(400)
  await page.locator('[data-test="nav-fav-alarms"]').click()
  await expect(page).toHaveURL('/alarms')
  await expect(page.locator('[data-test="nav-fav-alarms"]')).toHaveAttribute('aria-current', 'page')
})

test('manual sidebar folding clears hidden filters, retains groups, and keeps flyouts readable', async ({ page, request }) => {
  await page.setViewportSize({ width: 1366, height: 768 })
  await signIn(page, await login(request))
  await page.goto('/overview')
  await openNavigationGroup(page, 'config')
  const filter = page.locator('[data-test="nav-filter"]')
  await filter.fill('报警')
  const toggle = page.locator('[data-test="sidebar-toggle"]')
  await toggle.click()
  await expect(toggle).toHaveAttribute('aria-expanded', 'false')
  await expect(toggle).toHaveAccessibleName('展开导航栏')
  const aside = page.locator('.aside').first()
  await expect(aside).toHaveCSS('width', '64px')
  await expect(filter).toHaveCount(0)
  await expect(page.locator('.nav-menu > .el-sub-menu')).toHaveCount(4)
  await page.locator('[data-test="nav-monitoring-group"] > .el-sub-menu__title').hover()
  const popup = page.locator('.nav-group-popup.el-popper:visible')
  await expect(popup).toBeVisible()
  await expect(popup).toHaveCSS('background-color', await aside.evaluate(el => getComputedStyle(el).backgroundColor))
  await popup.locator('[data-test="nav-item-trends"]').click()
  await expect(page).toHaveURL('/trends')
  await expect(aside).toHaveCSS('width', '64px')
  await expect(page.locator('[data-test="nav-monitoring-group"]')).toHaveClass(/is-current-group/)

  await toggle.click()
  await expect(aside).toHaveCSS('width', '220px')
  await expect(toggle).toHaveAccessibleName('收起导航栏')
  await expect(filter).toHaveValue('')
  await expect(page.locator('[data-test="nav-config-group"]')).toHaveClass(/is-opened/)
  await expect(page.locator('[data-test="nav-item-trends"]')).toHaveAttribute('aria-current', 'page')
})

test('mobile navigation closes for the current page, explicit close, and keyboard page search', async ({ page, request }) => {
  await page.setViewportSize({ width: 320, height: 800 })
  await signIn(page, await login(request))
  await page.goto('/screens')
  const menu = page.locator('[data-test="mobile-menu"]')
  const drawer = page.locator('[data-test="mobile-drawer"]')
  await menu.click()
  await expect(menu).toHaveAttribute('aria-expanded', 'true')
  await expect(page.getByRole('navigation', { name: '主导航' })).toBeVisible()
  await page.locator('[data-test="nav-item-screens"]').click()
  await expect(page).toHaveURL('/screens')
  await expect(drawer).toBeHidden()
  await expect(menu).toBeFocused()

  await menu.click()
  await page.locator('[data-test="mobile-menu-close"]').click()
  await expect(drawer).toBeHidden()
  await expect(menu).toBeFocused()

  await menu.click()
  await page.keyboard.press('Control+KeyK')
  await expect(drawer).toBeHidden()
  await expect(page.locator('[data-test="command-palette"]')).toBeVisible()
  await expect(page.locator('[data-test="command-palette-input"]')).toBeFocused()
  await page.keyboard.press('Escape')
  await menu.click()
  await page.locator('[data-test="nav-item-trends"]').click()
  await expect(page).toHaveURL('/trends')
  await expect(drawer).toBeHidden()
})

test('navigation shortcuts, groups, and search retain permission filtering', async ({ page, request }) => {
  const admin = await login(request)
  const userName = `nav-viewer-${Date.now()}`
  const roleResponse = await request.post('/api/roles', { headers: admin.headers, data: { name: userName, permissions: ['View'] } })
  expect(roleResponse.ok(), await roleResponse.text()).toBeTruthy()
  const role = await roleResponse.json()
  const userResponse = await request.post('/api/users', { headers: admin.headers, data: { userName, password: 'secret123', displayName: '导航查看员', role: 'None', enabled: true } })
  expect(userResponse.ok(), await userResponse.text()).toBeTruthy()
  const user = await userResponse.json()
  const assignment = await request.put(`/api/users/${user.id}/assignments`, { headers: admin.headers, data: { assignments: [{ roleId: role.id, areaId: null }] } })
  expect(assignment.ok(), await assignment.text()).toBeTruthy()
  const sessionResponse = await request.post('/api/auth/login', { data: { userName, password: 'secret123' } })
  expect(sessionResponse.ok()).toBeTruthy()
  await signIn(page, await sessionResponse.json())
  await seedNavigation(page, userName, ['/screens', '/system', '/config/tags'])
  await page.goto('/screens')
  await expect(page.locator('[data-test="nav-fav-screens"]')).toBeVisible()
  await expect(page.locator('[data-test="nav-fav-system"], [data-test="nav-fav-tags"]')).toHaveCount(0)
  await expect(page.locator('[data-test="nav-config-group"], [data-test="nav-system-group"]')).toHaveCount(0)
  await page.locator('[data-test="nav-search"]').click()
  await expect(page.locator('[data-test="command-palette-item-system"], [data-test="command-palette-item-devices"]')).toHaveCount(0)
  await page.goto('/system')
  await expect(page).toHaveURL('/screens')
})

async function dragToPoint(page: Page, from: string, to: string, at: (box: { x: number; y: number; width: number; height: number }) => { x: number; y: number }) {
  const source = page.locator(from)
  const target = page.locator(to)
  await source.scrollIntoViewIfNeeded()
  await target.scrollIntoViewIfNeeded()
  const start = await source.boundingBox()
  const end = await target.boundingBox()
  expect(start, from).toBeTruthy()
  expect(end, to).toBeTruthy()
  await page.mouse.move(start!.x + start!.width / 2, start!.y + start!.height / 2)
  await page.mouse.down()
  const point = at(end!)
  await page.mouse.move(point.x, point.y, { steps: 18 })
  await page.mouse.up()
}

test('sidebar drag reorders items, groups, and favorites without taking over clicks', async ({ page, request }) => {
  expect(moveRelative(['a', 'b', 'c'], 'c', 'a', false)).toEqual(['c', 'a', 'b'])
  expect(moveRelative(['a', 'h', 'b'], 'a', 'b', true)).toEqual(['h', 'b', 'a'])
  expect(displayOrder(['a', 'b', 'c'], ['c', 'missing', 'a'])).toEqual(['c', 'a', 'b'])
  expect(reorderVisible(['a', 'h', 'b', 'c'], ['a', 'b', 'c'], 'a', 'c', true)).toEqual(['b', 'h', 'c', 'a'])

  const auth = await login(request)
  await signIn(page, auth)
  await page.goto('/overview')
  await page.evaluate(() => {
    localStorage.setItem('scada_nav_fav_admin', JSON.stringify(['/realtime', '/alarms', '/trends']))
    localStorage.removeItem('scada_nav_order_admin')
  })
  await page.reload()
  await openNavigationGroup(page, 'monitoring')
  await page.locator('[data-test="nav-adjust"]').click()

  const itemIds = () => page.locator('[data-test="nav-monitoring-group"] [data-test^="nav-item-"]').evaluateAll(rows => rows.map(row => row.getAttribute('data-test')))
  const beforeItems = await itemIds()
  expect(beforeItems[0]).toBe('nav-item-overview')
  const expectedItems = beforeItems.filter(id => id !== 'nav-item-overview')
  expectedItems.splice(expectedItems.indexOf('nav-item-realtime') + 1, 0, 'nav-item-overview')
  await dragToPoint(page, '[data-test="nav-item-overview"] [data-test="nav-drag"]', '[data-test="nav-item-realtime"]', box => ({ x: box.x + box.width / 2, y: box.y + box.height - 4 }))
  await expect.poll(itemIds).toEqual(expectedItems)

  await page.locator('[data-test="nav-monitoring-group"] .nav-group-label').click()
  await expect(page.locator('[data-test="nav-monitoring-group"]')).not.toHaveClass(/is-opened/)
  await page.locator('.nav-menu').evaluate(el => { el.scrollTop = 0 })
  const groupIds = () => page.locator('.nav-menu > .el-sub-menu').evaluateAll(rows => rows.map(row => row.getAttribute('data-test')))
  expect(await groupIds()).toEqual(['nav-monitoring-group', 'nav-production-group', 'nav-config-group', 'nav-system-group'])
  await dragToPoint(
    page,
    '[data-test="nav-production-group"] > .el-sub-menu__title [data-test="nav-drag"]',
    '[data-test="nav-monitoring-group"] > .el-sub-menu__title',
    box => ({ x: box.x + box.width / 2, y: box.y + 12 })
  )
  await expect.poll(groupIds).toEqual(['nav-production-group', 'nav-monitoring-group', 'nav-config-group', 'nav-system-group'])

  const favIds = () => page.locator('[data-test^="nav-fav-"]').evaluateAll(rows => rows.map(row => row.getAttribute('data-test')))
  expect(await favIds()).toEqual(['nav-fav-realtime', 'nav-fav-alarms', 'nav-fav-trends'])
  await dragToPoint(page, '[data-test="nav-fav-trends"]', '[data-test="nav-fav-realtime"]', box => ({ x: box.x + 4, y: box.y + box.height / 2 }))
  await expect.poll(favIds).toEqual(['nav-fav-trends', 'nav-fav-realtime', 'nav-fav-alarms'])

  await page.reload()
  await expect.poll(groupIds).toEqual(['nav-production-group', 'nav-monitoring-group', 'nav-config-group', 'nav-system-group'])
  await openNavigationGroup(page, 'monitoring')
  await page.locator('[data-test="nav-adjust"]').click()
  await expect.poll(itemIds).toEqual(expectedItems)
  await expect.poll(favIds).toEqual(['nav-fav-trends', 'nav-fav-realtime', 'nav-fav-alarms'])

  await page.locator('[data-test="nav-item-realtime"] [data-test="nav-drag"]').click()
  await expect(page).toHaveURL(/\/overview$/)
  await page.locator('[data-test="nav-item-realtime"]').click()
  await expect(page).toHaveURL(/\/realtime$/)
  await page.locator('[data-test="nav-fav-alarms"]').click()
  await expect(page).toHaveURL(/\/alarms$/)

  const filter = page.locator('[data-test="nav-filter"]')
  await filter.fill('报警')
  await expect(page.locator('[data-test="nav-drag"]')).toHaveCount(0)
  await filter.fill('')
  await expect.poll(itemIds).toEqual(expectedItems)
})
