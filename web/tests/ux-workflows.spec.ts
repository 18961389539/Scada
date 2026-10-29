import { test, expect, type APIRequestContext, type Page } from './fixtures'
import type { LoginResult } from '../src/types'
import { defaultLandingPath } from '../src/access/policies'
import { visibleNavigationIds } from './nav-catalog'

async function login(request: APIRequestContext, userName = 'admin'): Promise<LoginResult> {
  const response = await request.post('/api/auth/login', { data: { userName, password: userName === 'admin' ? 'admin123' : 'secret123' } })
  expect(response.ok(), await response.text()).toBeTruthy()
  return response.json()
}

async function signIn(page: Page, session: LoginResult): Promise<void> {
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, session)
}

async function post<T>(request: APIRequestContext, session: LoginResult, url: string, data: unknown): Promise<T> {
  const response = await request.post(url, { headers: { Authorization: `Bearer ${session.token}` }, data })
  expect(response.ok(), await response.text()).toBeTruthy()
  return response.json()
}

test('role defaults distinguish engineering and operations without requiring legacy roles', () => {
  expect(defaultLandingPath({ role: 'Admin' })).toBe('/overview')
  expect(defaultLandingPath({ role: 'Operator' })).toBe('/screens')
  expect(defaultLandingPath({ role: 'Viewer' })).toBe('/screens')
  expect(defaultLandingPath({ role: 'None', access: { global: [], areas: [{ areaId: 7, permissions: ['View', 'Configure'] }] } })).toBe('/overview')
  expect(defaultLandingPath({ role: 'None', access: { global: ['System'], areas: [] } })).toBe('/overview')
  expect(defaultLandingPath({ role: 'None', access: { global: ['ManageUsers'], areas: [] } })).toBe('/overview')
  expect(defaultLandingPath({ role: 'None', access: { global: [], areas: [{ areaId: 7, permissions: ['View', 'Control'] }] } })).toBe('/screens')
})

for (const demoMode of [false, true]) {
  test(`login has explicit ${demoMode ? 'demo' : 'production'} presentation and shared site identity`, async ({ page }) => {
    await page.route(url => url.pathname === '/api/auth/site-info', route => route.fulfill({ json: { siteName: 'Assembly Hall', demoMode } }))
    await page.goto('/login')
    await expect(page.locator('[data-test="login-site-name"]')).toHaveText('Assembly Hall')
    await expect(page.locator('[data-test="login-user-name"]')).toHaveValue(demoMode ? 'admin' : '')
    await expect(page.locator('[data-test="login-password"]')).toHaveValue(demoMode ? 'admin123' : '')
    await expect(page.locator('[data-test="login-demo-mode"]')).toHaveCount(demoMode ? 1 : 0)
    await expect(page.locator('[data-test="login-demo-credentials"]')).toHaveCount(demoMode ? 1 : 0)
    await page.locator('[data-test="login-user-name"]').fill('admin')
    await page.locator('[data-test="login-password"]').fill('admin123')
    await page.locator('[data-test="login-password"]').press('Enter')
    await expect(page).toHaveURL('/overview')
    await expect(page.locator('[data-test="header-site-name"]')).toHaveText('Assembly Hall')
    await expect(page.locator('[data-test="header-demo-mode"]')).toHaveCount(demoMode ? 1 : 0)
  })
}

test('site information errors remain explicit and retry never discards entered credentials', async ({ page }) => {
  let unavailable = true
  await page.route(url => url.pathname === '/api/auth/site-info', route => route.fulfill(unavailable
    ? { status: 503, json: { message: 'Site identity unavailable' } }
    : { json: { siteName: 'Recovered site', demoMode: false } }))
  await page.goto('/login')
  await expect(page.locator('[data-test="login-site-error"]')).toContainText('Site identity unavailable')
  await page.locator('[data-test="login-user-name"]').fill('admin')
  await page.locator('[data-test="login-password"]').fill('admin123')
  unavailable = false
  await page.locator('[data-test="login-site-retry"]').click()
  await expect(page.locator('[data-test="login-site-error"]')).toHaveCount(0)
  await expect(page.locator('[data-test="login-password"]')).toHaveValue('admin123')
  await page.locator('[data-test="login-password"]').press('Enter')
  await expect(page).toHaveURL('/overview')
})

test('login remains usable when public site identity cannot be loaded', async ({ page }) => {
  await page.route(url => url.pathname === '/api/auth/site-info', route => route.fulfill({ status: 503, json: { message: 'Identity service unavailable' } }))
  await page.goto('/login')
  await expect(page.locator('[data-test="login-site-error"]')).toBeVisible()
  await page.locator('[data-test="login-user-name"]').fill('admin')
  await page.locator('[data-test="login-password"]').fill('admin123')
  await page.locator('[data-test="login-password"]').press('Enter')
  await expect(page).toHaveURL('/overview')
  await expect(page.locator('[data-test="header-site-error"]')).toBeVisible()
})

test('operators land on process screens, retain deep links and use daily events without engineering access', async ({ page, request }) => {
  const admin = await login(request), suffix = Date.now()
  const userName = `ux-operator-${suffix}`
  await post(request, admin, '/api/users', { userName, password: 'secret123', displayName: 'Daily operator', role: 'Operator', enabled: true })
  const line = await post<{ id: number }>(request, admin, '/api/oee/lines', { name: `Daily-${suffix}`, areaId: 1, idealCycleSec: 60, enabled: true, stateMapJson: '{}' })
  const reason = await post<{ id: number }>(request, admin, '/api/oee/reasons', { code: `UX${suffix}`.slice(0, 20), name: `Reason-${suffix}`, category: 'Availability', isPlanned: false, enabled: true })
  const end = Date.now() - 60000, start = end - 600000
  const event = await post<{ id: number }>(request, admin, '/api/oee/downtimes', { lineId: line.id, startTs: start, endTs: end, state: 'Fault', confirmed: false })
  await page.goto('/login')
  await page.locator('[data-test="login-user-name"]').fill(userName)
  await page.locator('[data-test="login-password"]').fill('secret123')
  await page.locator('[data-test="login-password"]').press('Enter')
  await expect(page).toHaveURL('/screens')
  await page.goto('/')
  await expect(page).toHaveURL('/screens')
  await page.goto('/config/oee')
  await expect(page).toHaveURL('/screens')
  await page.goto(`/production/events?lineId=${line.id}&from=${start - 60000}&to=${end + 60000}`)
  await expect(page.locator('[data-test="oee-downtimes"]')).toContainText(`Daily-${suffix}`)
  await expect(page.getByRole('tab', { name: '产线', exact: true })).toHaveCount(0)
  await expect(page.locator('[data-test="oee-thresholds"]')).toHaveCount(0)
  const row = page.locator('[data-test="oee-downtimes"] .el-table__row').filter({ hasText: `Daily-${suffix}` })
  await expect(row).toContainText('10.0')
  await row.locator('[data-test="oee-dt-edit"]').click()
  const dialog = page.getByRole('dialog', { name: '停机', exact: true })
  await expect(dialog.locator('[data-test="oee-dt-line"]').getByRole('combobox')).toBeDisabled()
  await dialog.locator('[data-test="oee-dt-reason"]').click()
  await page.getByRole('option', { name: new RegExp(`Reason-${suffix}`) }).click()
  await dialog.getByRole('textbox', { name: '备注', exact: true }).fill('Classified on shift')
  await dialog.locator('[data-test="oee-dt-save"]').click()
  await expect(dialog).toBeHidden()
  await expect(row).toContainText(`Reason-${suffix}`)
  const operator = await login(request, userName)
  const headers = { Authorization: `Bearer ${operator.token}` }
  const stored = await (await request.get(`/api/oee/downtimes?lineId=${line.id}&from=${start - 60000}&to=${end + 60000}`, { headers })).json()
  expect(stored.find((item: { id: number; reasonId: number }) => item.id === event.id)?.reasonId).toBe(reason.id)
  await page.locator('[data-test="production-event-confirmation"]').click()
  await page.getByRole('option', { name: '待确认', exact: true }).click()
  const query = page.waitForRequest(request => new URL(request.url()).pathname === '/api/oee/downtimes' && new URL(request.url()).searchParams.get('confirmed') === 'false')
  await page.locator('[data-test="production-event-query"]').click()
  await query
  await row.locator('[data-test="oee-dt-confirm"]').click()
  await expect(row).toHaveCount(0)
  const ids = await visibleNavigationIds(page)
  expect(ids).toContain('command-palette-item-production-events')
  expect(ids).not.toContain('command-palette-item-oee-config')
  await page.goto('/login?redirect=/trends')
  await page.locator('[data-test="login-user-name"]').fill(userName)
  await page.locator('[data-test="login-password"]').fill('secret123')
  await page.locator('[data-test="login-password"]').press('Enter')
  await expect(page).toHaveURL('/trends')
})

for (const touch of [false, true]) {
  test.describe(touch ? 'industrial touch screen' : 'industrial mouse screen', () => {
    test.use({ viewport: { width: 1366, height: 768 }, hasTouch: touch })
    test('essential configuration tables fit without horizontal scrolling and retain confirmed secondary actions', async ({ page, request }) => {
      const session = await login(request), name = `Table-${Date.now()}`
      const device = await post<{ id: number }>(request, session, '/api/devices', { name, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1 })
      await post(request, session, '/api/tags', { deviceId: device.id, name: `${name}-tag`, area: 'HoldingRegister', address: 0, dataType: 'UInt16', scale: 1, offset: 0, writable: false, archiveMode: 'None', archivePeriodSec: 600, deadband: 0 })
      await signIn(page, session)
      for (const kind of ['device', 'tag'] as const) {
        await page.goto(`/config/${kind === 'device' ? 'devices' : 'tags'}`)
        const table = page.locator(`[data-test="${kind === 'device' ? 'devices' : 'tags'}-table"]`)
        const row = table.locator('.el-table__body-wrapper .el-table__row').filter({ hasText: kind === 'device' ? name : `${name}-tag` })
        await expect(row).toBeVisible()
        await expect.poll(() => table.evaluate(element => {
          const body = element.querySelector<HTMLElement>('.el-table__body')!
          const viewport = element.querySelector<HTMLElement>('.el-table__body-wrapper')!
          return body.getBoundingClientRect().width - viewport.clientWidth
        })).toBeLessThanOrEqual(1)
        const actions = row.locator('.engineering-actions'), buttons = actions.getByRole('button')
        for (const button of await buttons.all()) {
          const bounds = await button.boundingBox()
          expect(bounds!.height).toBeGreaterThanOrEqual(touch ? 44 : 36)
          expect(bounds!.width).toBeGreaterThanOrEqual(touch ? 44 : 40)
        }
        expect((await actions.boundingBox())!.width).toBeLessThanOrEqual(kind === 'device' ? 220 : 160)
        const idHeader = table.getByRole('columnheader', { name: /^ID(?: |$)/ })
        await expect(idHeader).toHaveCount(0)
        await page.locator(`[data-test="${kind}-engineering-columns"]`).click()
        await expect(idHeader).toBeVisible()
        await page.locator(`[data-test="${kind}-engineering-columns"]`).click()
        await row.locator(`[data-test="${kind}-more"]`).click()
        await expect(page.locator(`[data-test="${kind}-copy"]:visible`)).toBeVisible()
        await page.locator(`[data-test="${kind}-delete"]:visible`).click()
        const confirmation = page.locator('.el-message-box')
        await expect(confirmation).toContainText(kind === 'device' ? name : `${name}-tag`)
        await confirmation.getByRole('button', { name: '取消', exact: true }).click()
        await expect(confirmation).toBeHidden()
        await expect(row).toBeVisible()
      }
    })
  })
}

test('setup guide is permission aware and never treats saved objects as verified live data', async ({ page, request }) => {
  await signIn(page, await login(request))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [] }))
  await page.goto('/overview')
  const guide = page.locator('[data-test="setup-guide"]')
  await expect(guide).toHaveAttribute('open', '')
  await expect(guide).toContainText('人工核对')
  await expect(guide).toContainText('保存配置不等于连接成功')
  await expect(guide.getByRole('link', { name: '建立备份并验证恢复', exact: true })).toBeVisible()
  await guide.locator('summary').click()
  await expect(guide).not.toHaveAttribute('open')
})

test('daily production filters and entry dialogs remain usable at narrow mobile widths', async ({ page, request }) => {
  await signIn(page, await login(request))
  for (const width of [390, 320]) {
    await page.setViewportSize({ width, height: 900 })
    await page.goto('/production/events')
    const query = page.locator('[data-test="production-event-query"]')
    await expect(query).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)
    await page.locator('[data-test="oee-add-downtime"]').click()
    const dialog = page.getByRole('dialog', { name: '停机', exact: true })
    await expect(dialog.locator('[data-test="oee-dt-reason"]')).toBeVisible()
    const bounds = await dialog.boundingBox()
    expect(bounds!.x).toBeGreaterThanOrEqual(0)
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width + 1)
    await dialog.getByRole('button', { name: '取消', exact: true }).click()
    await expect(dialog).toBeHidden()
  }
})
