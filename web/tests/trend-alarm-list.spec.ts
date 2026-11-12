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

test('trend alarm list offers a keyboard reachable alternative to chart markers', async ({ page, request }) => {
  const session = await login(request)
  const suffix = Date.now()
  const device = await request.post('/api/devices', {
    headers: session.headers,
    data: {
      name: `TA-D-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 35000 + (suffix % 20000), stationNumber: 1,
      byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1
    }
  })
  expect(device.ok(), await device.text()).toBeTruthy()
  const deviceId = (await device.json()).id as number
  const tag = await request.post('/api/tags', {
    headers: session.headers,
    data: {
      deviceId, name: `TA-T-${suffix}`, area: 'HoldingRegister', address: 0, dataType: 'Float',
      scale: 1, offset: 0, writable: false, archiveMode: 'None', archivePeriodSec: 60, deadband: 0
    }
  })
  expect(tag.ok(), await tag.text()).toBeTruthy()
  const tagId = (await tag.json()).id as number

  await sign(page, session)
  const now = Date.now()
  const alarm = {
    id: 987654, tagId, deviceId, ruleId: null, level: 'Critical', value: 11.2,
    startTs: now - 120_000, endTs: null, ackUser: null, ackTs: null,
    message: '出口压力超限', shelved: false, suppressed: false
  }
  await page.route(url => url.pathname === '/api/history', route => route.request().method() === 'GET'
    ? route.fulfill({ json: { resolution: 'Raw', series: [{ tagId, points: [{ ts: now - 60_000, value: 12.5, quality: 'Good' }] }] } })
    : route.continue())
  await page.route(url => url.pathname === '/api/alarms', route => route.request().method() === 'GET'
    ? route.fulfill({ json: [alarm] })
    : route.continue())

  await page.goto('/trends')
  await page.locator('[data-test="clear-tags"]').click()
  await page.locator('[data-test="tag-picker"]').click()
  await page.getByRole('option', { name: new RegExp(`TA-T-${suffix}`) }).click()
  await page.keyboard.press('Escape')
  await page.getByRole('button', { name: '查询', exact: true }).click()

  const list = page.locator('[data-test="trend-alarm-list"]')
  await expect(list).toBeVisible()
  await expect(list.locator('summary')).toContainText('区间内报警 1 条')

  // 默认折叠，展开后每行是原生按钮：Tab 可聚焦，Enter 打开对应报警
  await list.locator('summary').click()
  const row = page.locator('[data-test="trend-alarm-row"]')
  await expect(row).toContainText('出口压力超限')
  await expect(row).toContainText('严重')
  await row.focus()
  await expect(row).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/\/alarms\?.*focus=987654/)
})
