import { test, expect, type Page } from './fixtures'

async function signIn(page: Page): Promise<void> {
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
}

test('login drops an off-site redirect', async ({ page }) => {
  test.setTimeout(60000)
  await page.goto('/login?redirect=https://evil.example/phish')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/\/overview/)
  await expect(page).not.toHaveURL(/evil\.example/)
})

test('alarm bell count comes from the summary, not the newest alarm page', async ({ page }) => {
  test.setTimeout(60000)
  await page.route('**/api/alarms/summary', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ unack: 42, acked: 3, rtnUnack: 1, shelved: 0, suppressed: 0, chattering: 0, shelvedRules: 0, outOfServiceRules: 0 })
    })
  )
  await page.route(
    (url) => url.pathname === '/api/alarms',
    (route) => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' })
  )
  await signIn(page)
  const badge = page.locator('.el-badge', { has: page.locator('[data-test="alarm-bell"]') }).locator('.el-badge__content')
  await expect(badge).toHaveText('42')
})

test('console banner ranks Warning above a newer Info and uses the real level names', async ({ page }) => {
  test.setTimeout(60000)
  await signIn(page)
  let priorityFirst = false
  await page.route('**/api/hmi/layouts', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 1, name: '主控', revision: 1, updatedTs: 1, updatedBy: 't',
        document: { monitors: [{ name: '屏', columns: 1, rows: 1, regions: [{ id: 'b', kind: 'alarmBanner', row: 1, col: 1, rowSpan: 1, colSpan: 1 }] }] }
      }])
    })
  })
  await page.route('**/api/alarms/summary', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ unack: 7, acked: 2, rtnUnack: 0, shelved: 0, suppressed: 0, chattering: 0, shelvedRules: 0, outOfServiceRules: 0 })
    })
  )
  await page.route(
    (url) => url.pathname === '/api/alarms',
    (route) => {
      priorityFirst = new URL(route.request().url()).searchParams.get('priorityFirst') === 'true'
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          { id: 1, level: 'Info', message: '提示报警-新', startTs: Date.now(), ackTs: null, endTs: null },
          { id: 2, level: 'Warning', message: '警告报警-旧', startTs: Date.now() - 120000, ackTs: null, endTs: null }
        ])
      })
    }
  )
  await page.goto('/console')
  await expect(page.locator('[data-test="alarm-banner-level"]')).toHaveText('警告')
  await expect(page.locator('[data-test="alarm-banner"]')).toContainText('警告报警-旧')
  await expect(page.locator('[data-test="alarm-banner-count"]')).toContainText('未确认 7 / 活动 9')
  expect(priorityFirst).toBe(true)
})

test('console does not claim there are no alarms when the refresh fails', async ({ page }) => {
  test.setTimeout(60000)
  await signIn(page)
  await page.route('**/api/hmi/layouts', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 1, name: '主控', revision: 1, updatedTs: 1, updatedBy: 't',
        document: { monitors: [{ name: '屏', columns: 1, rows: 1, regions: [{ id: 'b', kind: 'alarmBanner', row: 1, col: 1, rowSpan: 1, colSpan: 1 }] }] }
      }])
    })
  })
  await page.route(
    (url) => url.pathname === '/api/alarms',
    (route) => route.fulfill({ status: 500, contentType: 'application/json', body: '{}' })
  )
  await page.goto('/console')
  await expect(page.locator('[data-test="console-alarm-error"]')).toBeVisible()
  await expect(page.locator('[data-test="alarm-banner"]')).not.toContainText('无活动报警')
})

test('console shows a layout load failure instead of an empty workstation', async ({ page }) => {
  test.setTimeout(60000)
  await signIn(page)
  await page.route(
    (url) => url.pathname === '/api/hmi/layouts',
    (route) => route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '运行布局不可用' }) })
  )
  await page.goto('/console')
  await expect(page.locator('[data-test="console-layout-error"]')).toContainText('运行布局不可用')
  await expect(page.getByText('尚无运行布局')).toHaveCount(0)
})

test('recipe abort asks before stopping a running download', async ({ page }) => {
  test.setTimeout(60000)
  await signIn(page)
  await page.route('**/api/recipes/downloads**', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        id: 99, recipeId: 1, recipeName: 'PE-审核', version: 2, templateId: 1, areaId: 1, status: 'Running', statusLabel: '运行中',
        requestedBy: 'admin', approvedBy: null, approvalId: null, createdTs: Date.now(), startedTs: Date.now(), endedTs: null,
        written: 0, message: '', results: []
      }])
    })
  })
  let aborts = 0
  page.on('request', (req) => { if (req.url().includes('/downloads/99/abort')) aborts += 1 })
  await page.goto('/recipes')
  await page.locator('[data-test="recipe-abort-99"]').click()
  await expect(page.getByRole('dialog')).toContainText('不会自动回滚')
  await page.getByRole('dialog').getByRole('button', { name: '取消' }).click()
  expect(aborts).toBe(0)
})

test('reports page explains an empty table before the first query', async ({ page }) => {
  test.setTimeout(60000)
  await signIn(page)
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [{ id: 901, deviceId: null, name: '报表测点' }] }))
  await page.goto('/reports')
  await expect(page.locator('[data-test="reports-empty"]')).toContainText('选择时间范围和测点')
})

test('failed report query shows the error instead of an empty archive', async ({ page, request }) => {
  test.setTimeout(60000)
  const login = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(login.ok()).toBeTruthy()
  const headers = { Authorization: `Bearer ${(await login.json()).token}` }
  const suffix = Date.now()
  const device = await request.post('/api/devices', { headers, data: { name: `RPT-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false } })
  expect(device.ok(), await device.text()).toBeTruthy()
  const created = await device.json()
  const tag = await request.post('/api/tags', { headers, data: { deviceId: created.id, name: `RPT-T-${suffix}`, area: 'HoldingRegister', address: 1, dataType: 'Float', scale: 1, offset: 0, writable: false, archiveMode: 'None', deadband: 0, archivePeriodSec: 60 } })
  expect(tag.ok(), await tag.text()).toBeTruthy()
  await page.route('**/api/reports*', async route => {
    const url = new URL(route.request().url())
    if (url.pathname === '/api/reports') {
      await route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '历史报表不可用' }) })
      return
    }
    await route.continue()
  })
  await signIn(page)
  await page.goto('/reports')
  await page.getByRole('button', { name: '生成报表' }).click()
  const empty = page.locator('[data-test="reports-empty"]')
  await expect(empty).toContainText('历史报表不可用')
  await expect(empty).not.toContainText('所选范围内暂无归档数据')
  await expect(empty).not.toContainText('选择时间范围和测点')
})
