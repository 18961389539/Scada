import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const body = await response.json()
  return { token: body.token as string, user: body.user, headers: { Authorization: `Bearer ${body.token}` } }
}

async function signIn(page: Page, session: { token: string; user: unknown }): Promise<void> {
  await page.goto('/login')
  await page.evaluate((s) => {
    localStorage.setItem('scada_token', s.token)
    localStorage.setItem('scada_user', JSON.stringify(s.user))
  }, session)
}

test('ops status polling keeps a backup-list failure on the page', async ({ page, request }) => {
  const session = await login(request)
  await signIn(page, session)
  await page.route((url) => url.pathname === '/api/maintenance/backups', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '备份记录不可用' }) })
  })
  await page.goto('/ops')
  await expect(page.locator('[data-test="ops-load-error"]')).toContainText('备份记录不可用')
  await expect(page.locator('[data-test="ha-standalone"]')).toBeVisible({ timeout: 12000 })
  await expect(page.locator('[data-test="ops-load-error"]')).toContainText('备份记录不可用')
  await expect(page.getByText('暂无备份记录')).toHaveCount(0)
  await expect(page.getByText('暂无备份包')).toHaveCount(0)
})

test('control polling keeps approval and run refresh failures', async ({ page, request }) => {
  const session = await login(request)
  const suffix = Date.now()
  const device = await request.post('/api/devices', {
    headers: session.headers,
    data: {
      name: `POLL-D-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 32000 + (suffix % 20000), stationNumber: 1,
      byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1
    }
  })
  expect(device.ok(), await device.text()).toBeTruthy()
  const tag = await request.post('/api/tags', {
    headers: session.headers,
    data: {
      deviceId: (await device.json()).id, name: `POLL-T-${suffix}`, area: 'HoldingRegister', address: 0, dataType: 'UInt16',
      scale: 1, offset: 0, writable: true, archiveMode: 'None', archivePeriodSec: 60, deadband: 0
    }
  })
  expect(tag.ok(), await tag.text()).toBeTruthy()
  const tagId = (await tag.json()).id as number
  const sequence = await request.post('/api/control/sequences', {
    headers: session.headers,
    data: {
      name: `POLL-S-${suffix}`, areaId: 1, description: null, dualApproval: false, revision: 0,
      steps: [{ id: 'w', type: 'wait', name: 'hold', expression: `#${tagId} == 1`, timeoutMs: 600000 }]
    }
  })
  expect(sequence.ok(), await sequence.text()).toBeTruthy()
  const started = await request.post(`/api/control/sequences/${(await sequence.json()).id}/start`, { headers: session.headers, data: { confirmed: true } })
  expect(started.ok(), await started.text()).toBeTruthy()
  const runId = (await started.json()).runId as number
  expect(runId).toBeTruthy()

  await signIn(page, session)
  await page.goto('/control')
  await page.getByRole('tab', { name: '顺控' }).click()
  await expect(page.locator(`[data-test="run-status-${runId}"]`)).toContainText('运行中')
  await page.getByRole('tab', { name: /待复核/ }).click()
  await page.route((url) => url.pathname === '/api/control/requests' || url.pathname === '/api/control/runs', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    const message = new URL(route.request().url()).pathname.endsWith('/runs') ? '运行记录不可用' : '待复核不可用'
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message }) })
  })
  await expect(page.locator('[data-test="control-runs-error"]')).toContainText('运行记录不可用', { timeout: 8000 })
  await expect(page.locator('[data-test="control-requests-error"]')).toContainText('待复核不可用', { timeout: 8000 })
  await expect(page.locator('[data-test="control-load-error"]')).toHaveCount(0)
  await page.getByRole('tab', { name: '顺控' }).click()
  await expect(page.locator(`[data-test="run-status-${runId}"]`)).toContainText('运行中')
})
