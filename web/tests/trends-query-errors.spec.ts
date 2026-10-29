import { test, expect, type APIRequestContext, type Page } from './fixtures'

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const body = await response.json()
  return { token: body.token as string, user: body.user, headers: { Authorization: `Bearer ${body.token}` } }
}

async function sign(page: Page, session: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript((data) => {
    localStorage.setItem('scada_token', data.token)
    localStorage.setItem('scada_user', JSON.stringify(data.user))
  }, session)
}

test('a later trend query failure keeps the curve and the reason', async ({ page, request }) => {
  const session = await login(request)
  const suffix = Date.now()
  const device = await request.post('/api/devices', {
    headers: session.headers,
    data: {
      name: `TR-D-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 35000 + (suffix % 20000), stationNumber: 1,
      byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1
    }
  })
  expect(device.ok(), await device.text()).toBeTruthy()
  const tag = await request.post('/api/tags', {
    headers: session.headers,
    data: {
      deviceId: (await device.json()).id, name: `TR-T-${suffix}`, area: 'HoldingRegister', address: 0, dataType: 'Float',
      scale: 1, offset: 0, writable: false, archiveMode: 'None', archivePeriodSec: 60, deadband: 0
    }
  })
  expect(tag.ok(), await tag.text()).toBeTruthy()
  const tagId = (await tag.json()).id as number
  await sign(page, session)
  let historyFail = false
  await page.route((url) => url.pathname === '/api/history', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    if (historyFail) return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '历史不可用' }) })
    return route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ resolution: 'Raw', series: [{ tagId, points: [{ ts: Date.now(), value: 12.5, quality: 'Good' }] }] })
    })
  })
  await page.route((url) => url.pathname === '/api/history/analysis', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '统计分析不可用' }) }))
  await page.goto('/trends')
  await page.locator('[data-test="clear-tags"]').click()
  await page.locator('[data-test="tag-picker"]').click()
  await page.getByRole('option', { name: new RegExp(`TR-T-${suffix}`) }).click()
  await page.keyboard.press('Escape')
  await expect(page.locator('[data-test="selected-count"]')).toContainText('已选 1')
  await page.getByRole('button', { name: '查询', exact: true }).click()
  await expect(page.locator('[data-test="point-count"]')).toBeVisible()
  await expect(page.locator('[data-test="trend-analysis-error"]')).toContainText('统计分析不可用')
  historyFail = true
  await page.getByRole('button', { name: '查询', exact: true }).click()
  await expect(page.locator('[data-test="trend-query-error"]')).toContainText('历史不可用')
  await expect(page.locator('[data-test="point-count"]')).toBeVisible()
  await expect(page.locator('[data-test="playback-bar"]')).toBeVisible()
  await expect(page.locator('[data-test="chart-empty"]')).toHaveCount(0)
  await page.locator('[data-test="auto-refresh"]').click()
  await expect(page.locator('[data-test="trend-query-error"]')).toContainText('历史不可用')
  await expect(page.locator('[data-test="point-count"]')).toBeVisible()
})
