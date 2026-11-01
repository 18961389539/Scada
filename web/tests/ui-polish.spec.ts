import { test, expect, type APIRequestContext, type Page } from './fixtures'

async function signIn(page: Page, request: APIRequestContext, theme: 'light' | 'dark' = 'light') {
  const login = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(login.ok()).toBeTruthy()
  const auth = await login.json()
  await page.addInitScript(({ token, user, theme }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_theme', theme)
  }, { token: auth.token, user: auth.user, theme })
  return { Authorization: `Bearer ${auth.token}` }
}

async function seedTag(request: APIRequestContext, headers: Record<string, string>) {
  const device = await request.post('/api/devices', {
    headers, data: { name: `UI-D-${Date.now()}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1 }
  })
  expect(device.ok(), await device.text()).toBeTruthy()
  const tag = await request.post('/api/tags', {
    headers, data: { deviceId: (await device.json()).id, name: `UI-T-${Date.now()}`, area: 'HoldingRegister', address: 1, dataType: 'Float', scale: 1, offset: 0, writable: false, archiveMode: 'None', deadband: 0, archivePeriodSec: 60 }
  })
  expect(tag.ok(), await tag.text()).toBeTruthy()
}

// Element 的 .el-input/.el-select 自带 width:100%，按需注入后排在全局样式之后；
// 曾经把设备、报警规则、配方三页的筛选控件各拉成一整行。
test('catalog filters stay on one row with their designed widths', async ({ page, request }) => {
  await signIn(page, request)
  await page.setViewportSize({ width: 1600, height: 1000 })
  await page.goto('/config/devices')
  const filters = page.locator('form.catalog-filters')
  const search = await filters.locator('.catalog-search').boundingBox()
  const selects = await filters.locator('.catalog-select').evaluateAll(list => list.map(e => { const b = e.getBoundingClientRect(); return { y: b.y, width: b.width } }))
  expect(Math.round(search!.width)).toBe(300)
  expect(selects).toHaveLength(2)
  for (const select of selects) {
    expect(Math.round(select.width)).toBe(170)
    expect(Math.abs(select.y - search!.y)).toBeLessThan(2)
  }
})

for (const theme of ['light', 'dark'] as const) {
  test(`table router-links use the theme link color, not the browser default (${theme})`, async ({ page, request }) => {
    const headers = await signIn(page, request, theme)
    await seedTag(request, headers)
    await page.setViewportSize({ width: 1600, height: 1000 })
    await page.goto('/realtime')
    const link = page.locator('[data-test="realtime-trend"]').first()
    await expect(link).toBeVisible()
    const style = await link.evaluate(a => {
      const probe = document.createElement('span')
      probe.style.color = 'var(--scada-primary)'
      document.body.append(probe)
      const primary = getComputedStyle(probe).color
      probe.remove()
      const next = a.nextElementSibling!.getBoundingClientRect()
      return { color: getComputedStyle(a).color, primary, decoration: getComputedStyle(a).textDecorationLine, gap: next.left - a.getBoundingClientRect().right }
    })
    expect(style.color).toBe(style.primary)
    expect(style.decoration).toBe('none')
    expect(style.gap).toBeGreaterThanOrEqual(8)
  })
}
