import { test, expect, type APIRequestContext } from './fixtures'

async function adminHeaders(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok(), await response.text()).toBe(true)
  const { token } = await response.json()
  return { Authorization: `Bearer ${token}` }
}

test('core: login, save configuration and reload persisted device', async ({ page, request }) => {
  const headers = await adminHeaders(request)
  const initial = await request.get('/api/devices', { headers })
  expect(await initial.json()).toEqual([])
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  const created = await request.post('/api/devices', { headers, data: {
    name: 'isolated-smoke-device', protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502,
    stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 500, enabled: false
  } })
  expect(created.status(), await created.text()).toBe(201)
  await page.goto('/config/devices')
  await expect(page.getByRole('row').filter({ hasText: 'isolated-smoke-device' })).toBeVisible()
  await page.reload()
  await expect(page.getByRole('row').filter({ hasText: 'isolated-smoke-device' })).toBeVisible()
})

test('core: viewer cannot write configuration or enter administration', async ({ page, request }) => {
  const headers = await adminHeaders(request)
  const created = await request.post('/api/users', { headers, data: {
    userName: 'smoke-viewer', password: 'smoke-viewer-password', displayName: 'Smoke viewer', role: 'Viewer', enabled: true
  } })
  expect(created.ok(), await created.text()).toBe(true)
  const auth = await request.post('/api/auth/login', { data: { userName: 'smoke-viewer', password: 'smoke-viewer-password' } })
  expect(auth.ok(), await auth.text()).toBe(true)
  const session = await auth.json()
  const denied = await request.post('/api/devices', { headers: { Authorization: `Bearer ${session.token}` }, data: {} })
  expect(denied.status()).toBe(403)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, session)
  await page.goto('/system')
  await expect(page).not.toHaveURL(/\/system/)
  await expect(page.locator('[data-test="nav-item-system"]')).toHaveCount(0)
})

test('core: live value, raised alarm and acknowledged state', async ({ page, request }) => {
  const headers = await adminHeaders(request)
  const calculation = await request.post('/api/calculated-tags', { headers, data: {
    name: 'smoke-live-value', expression: '50', intervalMs: 200
  } })
  expect(calculation.ok(), await calculation.text()).toBe(true)
  const tag = await calculation.json()
  const rule = await request.post('/api/alarm-rules', { headers, data: {
    tagId: tag.id, type: 'High', threshold: 10, level: 'Critical', delaySec: 0,
    recoverDeadband: 0, enabled: true, message: 'smoke-high-alarm'
  } })
  expect(rule.ok(), await rule.text()).toBe(true)
  await expect.poll(async () => {
    const response = await request.get('/api/realtime', { headers })
    expect(response.ok(), await response.text()).toBe(true)
    const samples: { tagId: number; value: number }[] = await response.json()
    return samples.find(sample => sample.tagId === tag.id)?.value
  }, { timeout: 15000 }).toBe(50)
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/alarms')
  const row = page.getByRole('row').filter({ hasText: 'smoke-high-alarm' })
  await expect(row.locator('[data-test="alarm-state"]')).toHaveText('未确认', { timeout: 15000 })
  await row.locator('[data-test="ack"]').click()
  await page.locator('[data-test="note-input"]').fill('Smoke test acknowledgement')
  await page.locator('[data-test="note-submit"]').click()
  await expect(row.locator('[data-test="alarm-state"]')).toHaveText('已确认')
})
