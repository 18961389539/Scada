import { test, expect, type APIRequestContext, type Page } from '@playwright/test'
import type { ScreenDocument, ScreenSummary } from '../src/screens/model'
import { visibleNavigationIds } from './nav-catalog'

const screens: ScreenSummary[] = [
  { id: 101, name: 'Pump 2', revision: 1, publishedRevision: null, updatedTs: 1000, updatedBy: 'Engineer', hasDraftChanges: true },
  { id: 102, name: 'Pump 10', revision: 2, publishedRevision: 2, updatedTs: 2000, updatedBy: 'operator', hasDraftChanges: false },
  { id: 103, name: 'Boiler 1', revision: 3, publishedRevision: 2, updatedTs: 3000, updatedBy: 'ENGINEER', hasDraftChanges: true }
]

async function openScreens(page: Page, request: APIRequestContext): Promise<void> {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: screens }))
  await page.goto('/screens')
  await expect(page.locator('[data-test="screens-count"]')).toHaveText('显示 3 / 3 个画面')
}

async function select(page: Page, testId: string, label: string): Promise<void> {
  await page.locator(`[data-test="${testId}"]`).click()
  await page.getByRole('option', { name: label, exact: true }).click()
}

test('screen catalog searches names and editors, filters publication state, and sorts names naturally', async ({ page, request }) => {
  await openScreens(page, request)
  const cards = page.locator('.screen-card h3')
  await expect(cards).toHaveText(['Boiler 1', 'Pump 10', 'Pump 2'])

  await page.getByRole('textbox', { name: '搜索画面名称或修改人' }).fill('  pump ENGINEER  ')
  await expect(cards).toHaveText(['Pump 2'])
  await expect(page.locator('[data-test="screens-count"]')).toHaveText('显示 1 / 3 个画面')
  await page.locator('[data-test="screens-reset"]').click()

  await select(page, 'screens-status', '有未发布修改')
  await expect(cards).toHaveText(['Boiler 1'])
  await expect(page.locator('[data-test="screen-card-103"] .el-tag')).toHaveText('有未发布修改')
  await select(page, 'screens-status', '已发布')
  await expect(cards).toHaveText(['Boiler 1', 'Pump 10'])
  await select(page, 'screens-status', '尚未发布')
  await expect(cards).toHaveText(['Pump 2'])
  await page.locator('[data-test="screens-reset"]').click()

  await select(page, 'screens-sort', '按名称排序')
  await expect(cards).toHaveText(['Boiler 1', 'Pump 2', 'Pump 10'])
  await page.getByRole('textbox', { name: '搜索画面名称或修改人' }).fill('missing-screen')
  await expect(page.locator('[data-test="screens-no-matches"]')).toContainText('没有符合筛选条件的画面')
  await page.locator('[data-test="screens-no-matches"]').getByRole('button', { name: '清除筛选' }).click()
  await expect(cards).toHaveCount(3)
  await expect(page.locator('[data-test="screen-card-103"] .el-tag')).toBeVisible()
  await expect.poll(() => page.locator('[data-test="screen-card-103"] .el-tag').evaluate(el => getComputedStyle(el).opacity)).toBe('1')
  await page.screenshot({ path: test.info().outputPath('screen-catalog-desktop.png'), animations: 'disabled' })
})

test('screen catalog reload recovers from a failure and keeps active filters during refresh', async ({ page, request }) => {
  await openScreens(page, request)
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ status: 500, json: { message: '画面服务暂时不可用' } }))
  await page.reload()
  await expect(page.locator('[data-test="screens-load-error"]')).toContainText('画面服务暂时不可用')
  await expect(page.locator('[data-test="screens-no-matches"]')).toHaveCount(0)
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: screens }))
  await page.getByRole('button', { name: '重新加载', exact: true }).click()
  await expect(page.locator('.screen-card')).toHaveCount(3)
  await expect(page.locator('[data-test="screens-load-error"]')).toHaveCount(0)
  await page.getByRole('textbox', { name: '搜索画面名称或修改人' }).fill('operator')
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('.screen-card h3')).toHaveText(['Pump 10'])
  await expect(page.getByRole('textbox', { name: '搜索画面名称或修改人' })).toHaveValue('operator')
})

test('screen catalog retains its filters and sort across reload and a running-screen return', async ({ page, request }) => {
  await openScreens(page, request)
  await page.getByRole('textbox', { name: '搜索画面名称或修改人' }).fill('Pump')
  await select(page, 'screens-status', '已发布')
  await select(page, 'screens-sort', '按名称排序')
  await expect(page).toHaveURL(/sort=name/)
  await page.reload()
  await expect(page.getByRole('textbox', { name: '搜索画面名称或修改人' })).toHaveValue('Pump')
  await expect(page.locator('.screen-card h3')).toHaveText(['Pump 10'])
  await expect(page.locator('[data-test="screens-status"]')).toContainText('已发布')
  await expect(page.locator('[data-test="screens-sort"]')).toContainText('按名称排序')
  await page.route(url => url.pathname === '/api/screens/102', route => route.fulfill({ json: { ...screens[1], document: { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [] } } }))
  await page.locator('[data-test="screen-run-102"]').click()
  await expect(page).toHaveURL(/\/screens\/102\?/)
  await page.locator('[data-test="screen-back"]').click()
  await expect(page.locator('.screen-card h3')).toHaveText(['Pump 10'])
  await expect(page.locator('[data-test="screens-sort"]')).toContainText('按名称排序')
  await page.locator('[data-test="screens-reset"]').click()
  await expect(page).toHaveURL(/\/screens$/)
  await expect(page.locator('.screen-card')).toHaveCount(3)
})

test('screen catalog separates draft and running versions and hides maintenance in a menu', async ({ page, request }) => {
  await openScreens(page, request)
  const row = page.locator('[data-test="screen-card-103"]')
  await expect(row.locator('[data-test="screen-published-version"]')).toHaveText('运行版本 V2')
  await expect(row.locator('[data-test="screen-draft-version"]')).toHaveText('草稿版本 V3')
  await expect(row).toContainText('草稿名称')
  await expect(row.getByRole('button', { name: '运行已发布版本 V2', exact: true })).toBeVisible()
  await expect(row.getByRole('button', { name: '删除', exact: true })).toHaveCount(0)
  await page.locator('[data-test="screen-more-103"]').click()
  await expect(page.getByRole('menuitem', { name: '删除', exact: true })).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.locator('[data-test="screen-card-101"] [data-test="screen-published-version"]')).toHaveText('尚未发布')
  await expect(page.locator('[data-test="screen-run-101"]')).toHaveCount(0)
})

test('screen catalog compact rows fit desktop and narrow mobile widths', async ({ page, request }) => {
  await openScreens(page, request)
  for (const width of [1600, 390, 320]) {
    await page.setViewportSize({ width, height: 900 })
    await page.reload()
    await expect(page.locator('.screen-card')).toHaveCount(3)
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width)
    expect(await page.locator('.screen-card').evaluateAll(rows => Math.max(...rows.map(row => row.getBoundingClientRect().height)))).toBeLessThan(170)
    await page.screenshot({ path: test.info().outputPath(`screen-list-${width}.png`), animations: 'disabled' })
  }
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: [{ ...screens[2], name: 'LongScreenName'.repeat(12) }] }))
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('.screen-card')).toHaveCount(1)
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(320)
})

test('screen catalog and command palette fit a narrow viewport with long screen names', async ({ page, request }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await openScreens(page, request)
  const expectedIds = await visibleNavigationIds(page)
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({ json: [{ ...screens[0], name: 'LongScreenName'.repeat(12) }] }))
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('.screen-card')).toHaveCount(1)
  await expect.poll(() => page.locator('.screen-card .el-tag').evaluate(el => getComputedStyle(el).opacity)).toBe('1')
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
  await page.screenshot({ path: test.info().outputPath('screen-catalog-mobile.png'), animations: 'disabled' })

  await page.locator('[data-test="nav-search"]').click()
  const palette = page.locator('[data-test="command-palette"]')
  await expect(palette).toBeVisible()
  await expect.poll(() => palette.locator('[data-test^="command-palette-item-"]').evaluateAll(rows => rows.map(row => row.getAttribute('data-test')).sort())).toEqual(expectedIds)
  const bounds = await palette.boundingBox()
  expect(bounds!.x).toBeGreaterThanOrEqual(0)
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(390)
  await page.screenshot({ path: test.info().outputPath('command-palette-mobile.png'), animations: 'disabled' })
})

async function monitoringScreen(page: Page, request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token }) => {
    localStorage.setItem('scada_token', token)
    localStorage.removeItem('scada_user')
    localStorage.setItem('scada_sound', 'off')
  }, auth)
  const state = { failAlarms: true, failHistory: true, failOee: true, quality: 'Good', sampleAge: 0, alarmCount: 0 }
  const base = { x: 20, y: 20, width: 280, height: 100, rotation: 0, text: '', color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null, action: { type: 'none' as const, screenId: null, tagId: null, value: 1 } }
  const document: ScreenDocument = { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [
    { ...base, id: 'alarm', type: 'alarm-banner' },
    { ...base, id: 'history', type: 'chart-line', y: 150, height: 180, tags: [900001] },
    { ...base, id: 'oee', type: 'oee-kpi', x: 340, height: 180, lineId: 900001 },
    { ...base, id: 'bar', type: 'chart-bar', x: 660, height: 180, tags: [900001] },
    { ...base, id: 'table', type: 'table', x: 340, y: 230, tags: [900001] },
    { ...base, id: 'value', type: 'value', x: 660, y: 230, tagId: 900001 },
    { ...base, id: 'command', type: 'button', x: 20, y: 380, text: '验证命令', action: { type: 'command', screenId: null, tagId: 900001, value: 42 } }
  ] }
  await page.route(url => url.pathname === '/api/screens/900001', route => route.fulfill({ json: { ...screens[1], id: 900001, name: '监视验收', document } }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ id: 900001, name: '验收设备', pollIntervalMs: 1000, enabled: true }] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [{ id: 900001, deviceId: 900001, name: '验收压力', unit: 'MPa', dataType: 'Float', writable: false }] }))
  await page.route(url => url.pathname === '/api/realtime', async route => route.fulfill({ json: [{ tagId: 900001, value: 42, quality: state.quality, ts: await page.evaluate(() => Date.now()) - state.sampleAge }] }))
  await page.route(url => url.pathname === '/api/alarms', route => state.failAlarms
    ? route.fulfill({ status: 500, json: { message: '报警接口不可用' } })
    : route.fulfill({ json: Array.from({ length: state.alarmCount }, (_, index) => ({ id: index + 1, tagId: 900001, level: 'Warning', startTs: Date.now(), endTs: null, message: '压力报警' })) }))
  await page.route(url => url.pathname === '/api/history', route => state.failHistory
    ? route.fulfill({ status: 500, json: { message: '历史接口不可用' } })
    : route.fulfill({ json: { series: [{ tagId: 900001, points: [{ ts: Date.now(), value: 42 }] }] } }))
  await page.route(url => url.pathname === '/api/oee/analytics', route => state.failOee
    ? route.fulfill({ status: 500, json: { message: 'OEE 接口不可用' } })
    : route.fulfill({ json: { kpi: { availability: .9, performance: .9, quality: .9, oee: .729 } } }))
  await page.goto('/screens/900001')
  await expect(page.locator('[data-node-id="alarm"]')).toBeVisible()
  return state
}

test('screen monitoring reports failed sources instead of no alarms and recovers on refresh', async ({ page, request }) => {
  const state = await monitoringScreen(page, request)
  await expect(page.locator('[data-test="runtime-alarm-error"]')).toContainText('报警接口不可用')
  await expect(page.locator('[data-node-id="alarm"]')).toContainText('报警数据不可用')
  await expect(page.locator('[data-test="runtime-history-error"]')).toContainText('历史接口不可用')
  await expect(page.locator('[data-test="runtime-oee-error"]')).toContainText('OEE 接口不可用')
  state.failAlarms = state.failHistory = state.failOee = false
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('[data-node-id="alarm"]')).toContainText('无报警')
  await expect(page.locator('[data-test="runtime-alarm-error"]')).toHaveCount(0)
  await expect(page.locator('[data-test="runtime-history-error"]')).toHaveCount(0)
  await expect(page.locator('[data-test="runtime-oee-error"]')).toHaveCount(0)
})

test('screen monitoring rejects invalid and stale chart and table samples', async ({ page, request }) => {
  const state = await monitoringScreen(page, request)
  state.failAlarms = state.failHistory = state.failOee = false
  const refresh = () => page.getByRole('button', { name: '刷新', exact: true }).click()
  await refresh()
  await expect(page.locator('[data-node-id="bar"] [data-chart]')).not.toHaveAttribute('data-unavailable')
  await expect(page.locator('[data-node-id="table"]')).toContainText('42.0')
  state.quality = 'Bad'
  await refresh()
  await expect(page.locator('[data-node-id="bar"] [data-chart]')).toHaveAttribute('data-unavailable', 'true')
  await expect(page.locator('[data-node-id="table"]')).toContainText('--')
  state.quality = 'Good'; state.sampleAge = 60000
  await refresh()
  await expect(page.locator('[data-node-id="bar"] [data-chart]')).toHaveAttribute('data-unavailable', 'true')
  await expect(page.locator('[data-node-id="table"]')).toContainText('--')
  await expect(page.locator('[data-node-id="value"]')).toContainText('--')
  state.sampleAge = 0
  await refresh()
  await expect(page.locator('[data-node-id="bar"] [data-chart]')).not.toHaveAttribute('data-unavailable')
})

test('screen monitoring labels system alarms and discloses truncated counts', async ({ page, request }) => {
  const state = await monitoringScreen(page, request)
  state.failAlarms = state.failHistory = state.failOee = false
  state.alarmCount = 2000
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('[data-test="runtime-alarm-scope"]')).toHaveCount(1)
  await expect(page.locator('[data-test="runtime-alarm-scope"]')).toHaveText('系统活动报警')
  await expect(page.locator('[data-test="runtime-alarm-limited"]')).toBeVisible()
  await expect(page.locator('[data-node-id="alarm"]')).toContainText('至少 2000 条')
  state.alarmCount = 1
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('[data-test="runtime-alarm-limited"]')).toHaveCount(0)
  await expect(page.locator('[data-node-id="alarm"]')).toContainText('系统活动报警：1 条')
})

test('screen monitoring verifies unchanged samples and detects a stopped sampling clock', async ({ page, request }) => {
  await page.clock.install()
  const state = await monitoringScreen(page, request)
  state.failAlarms = state.failHistory = state.failOee = false
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await page.clock.runFor(21000)
  await expect(page.locator('[data-node-id="table"]')).toContainText('42.0')
  await expect(page.locator('[data-node-id="bar"] [data-chart]')).not.toHaveAttribute('data-unavailable')
  state.sampleAge = 60000
  await page.clock.runFor(21000)
  await expect(page.locator('[data-node-id="table"]')).toContainText('--')
  await expect(page.locator('[data-node-id="bar"] [data-chart]')).toHaveAttribute('data-unavailable', 'true')
  state.sampleAge = 0
  await page.clock.runFor(6000)
  await expect(page.locator('[data-node-id="table"]')).toContainText('42.0')
})

test('screen monitoring retains command confirmation and mobile read-only protection', async ({ page, request }) => {
  const state = await monitoringScreen(page, request)
  state.failAlarms = state.failHistory = state.failOee = false
  await page.route(url => url.pathname === '/api/control/check', route => route.fulfill({ json: { tagId: 900001, tagName: '验收压力', current: 42, mode: 'Remote', lockouts: [], refusals: [], dualApproval: false } }))
  let submissions = 0
  await page.route(url => url.pathname === '/api/screens/900001/actions/command', route => { submissions++; return route.fulfill({ json: { success: true, readBack: 42 } }) })
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await page.getByRole('button', { name: '验证命令', exact: true }).click()
  await expect(page.locator('[data-test="control-confirm"]')).toBeVisible()
  expect(submissions).toBe(0)
  await page.locator('[data-test="control-confirm"]').getByRole('button', { name: '取消', exact: true }).click()
  expect(submissions).toBe(0)
  await page.getByRole('button', { name: '验证命令', exact: true }).click()
  await page.locator('[data-test="control-confirm-ok"]').click()
  await expect.poll(() => submissions).toBe(1)
  await page.route(url => url.pathname === '/api/client-policy', route => route.fulfill({ json: { mobileControl: false } }))
  await page.setViewportSize({ width: 390, height: 844 })
  await page.reload()
  await expect(page.locator('[data-test="mobile-readonly"]')).toBeVisible()
  await page.getByRole('button', { name: '验证命令', exact: true }).click()
  await expect(page.locator('[data-test="runtime-command-error"]')).toBeVisible()
  expect(submissions).toBe(1)
})
